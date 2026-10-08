using Avalonia;
using Avalonia.Collections;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.BaseModel;
using PromptCraft.Consts.Event;
using PromptCraft.Data;
using PromptCraft.Interfaces;
using PromptCraft.Models;
using PromptCraft.Models.Inference;
using PromptCraft.Service;
using PromptCraft.Service.Inference;
using PromptCraft.Service.Inference.Minimax;
using PromptCraft.Service.Inference.Reverse;
using Ke.Bee.Localization.Localizer;
using Ke.Bee.Localization.Localizer.Abstractions;
using LibVLCSharp.Shared;
using Material.Icons;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SukiUI.Controls;
using SukiUI.Toasts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.System;

public partial class ExpandViewModel : ViewModelBase, ISukiStackPageTitleProvider
{
    private readonly IServiceProvider _services;
    private IExpandSettingsStore? _settings;
    /// <summary>提示词工程服务（kind=expand；启用状态与 PE 页联动，ListProfiles 读 comfyui.db pe_profiles）。</summary>
    private readonly PmPromptEngineeringService _peService;
    /// <summary>恢复完成前不写库（对齐 PromptMaster On 标志位：confirm 初始化后才保存）。</summary>
    private bool _settingsLoaded;

    // 参数记忆 key（对齐 PromptMaster xt 映射，反混淆前端 556 行）
    private const string KeyRuleId = "pm_expand_rule_id";
    private const string KeyOutputLang = "pm_expand_output_lang";
    private const string KeyLength = "pm_expand_length";
    private const string KeyLengthChars = "pm_expand_length_chars";
    private const string KeyCustomPrompt = "pm_expand_user_extra_prompt";
    private const string KeyEngine = "pm_expand_engine";
    private const string KeyCaptionModel = "pm_expand_caption_model";
    private const string KeyCaptionProvider = "pm_expand_caption_provider"; // PromptCraft 扩展：跨 provider 同名模型消歧

    [ObservableProperty] private string _userInput = "";
    [ObservableProperty] private string _result = "";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _selectedOutputLang = "zh";
    [ObservableProperty] private ExpandRuleOption? _selectedRule;
    [ObservableProperty] private LengthOption? _selectedLength;
    [ObservableProperty] private EngineOption? _selectedEngine;
    [ObservableProperty] private string _customPrompt = "";
    [ObservableProperty] private ProviderConfig? _selectedProvider;
    [ObservableProperty] private ProviderModel? _selectedModel;
    [ObservableProperty] private bool _qualityPromptEnabled;

    // ============================================================
    // 模型选择器（对齐反混淆前端 1072-1142：popover 触发按钮 + 搜索 + 服务商族 + 在线模型列表 + 去配置）
    // ============================================================

    [ObservableProperty] private bool _isModelPickerOpen;
    [ObservableProperty] private string _modelSearchQuery = "";
    public AvaloniaList<ProviderModel> FilteredModels { get; } = new();

    /// <summary>触发按钮文案（对齐 Jn 显示：模型名，空则「请选择模型」）。</summary>
    public string ModelPickerLabel => SelectedModel?.ModelName
        ?? (_local["NoModelSelected"] ?? "");

    /// <summary>当前服务商无可用模型时的提示（对齐 1128-1132「未配置{族}服务商的扩写模型」+ 去配置）。</summary>
    public string NoModelsText => string.Format(_local["NoModelsText"], SelectedProvider?.Name ?? "");

    public bool HasExpandModels => FilteredModels.Count > 0;

    partial void OnModelSearchQueryChanged(string value)
    {
        RefreshFilteredModels();
        OnPropertyChanged(nameof(HasExpandModels));
    }

    partial void OnSelectedProviderChanged(ProviderConfig? value)
    {
        if (_settingsLoaded) _settings?.Set(KeyCaptionProvider, value?.Id ?? "");
        RefreshFilteredModels();
        OnPropertyChanged(nameof(HasExpandModels));
        OnPropertyChanged(nameof(NoModelsText));
        OnPropertyChanged(nameof(ModelPickerLabel));
    }

    partial void OnSelectedModelChanged(ProviderModel? value)
    {
        if (_settingsLoaded) _settings?.Set(KeyCaptionModel, value?.ModelName ?? "");
        OnPropertyChanged(nameof(ModelPickerLabel));
    }

    /// <summary>按搜索词过滤当前服务商模型（对齐 Hn：在线模型列表 + 搜索过滤）。</summary>
    private void RefreshFilteredModels()
    {
        FilteredModels.Clear();
        if (SelectedProvider == null) return;
        foreach (var m in SelectedProvider.Models)
        {
            if (string.IsNullOrEmpty(ModelSearchQuery)
                || m.ModelName.Contains(ModelSearchQuery, StringComparison.OrdinalIgnoreCase))
            {
                FilteredModels.Add(m);
            }
        }
    }

    /// <summary>选中服务商族（对齐 Bn onMousedown：Un=e.value → 刷模型列表）。</summary>
    public void SelectProviderFamily(ProviderConfig p)
    {
        SelectedProvider = p;
        ModelSearchQuery = "";
        RefreshFilteredModels();
    }

    /// <summary>选中模型并关闭面板（对齐 nt：e.value → 记忆）。</summary>
    public void SelectModelFromPicker(ProviderModel m)
    {
        SelectedModel = m;
        IsModelPickerOpen = false;
    }

    /// <summary>去配置（对齐 Gn：跳服务商设置）。PromptCraft 直接跳转设置页。</summary>
    public void GoConfigureProvider()
    {
        IsModelPickerOpen = false;
        LogService.Instance.Info("扩写：跳转设置页配置模型", "Expand");
        _noticeService.Publish(PromptCraft.Consts.Event.EventNameConst.SystemNavigatePageEvent, typeof(SettingModel));
    }

    /// <summary>"去配置"命令（XAML 绑定，同 GoConfigureProvider）。</summary>
    public IRelayCommand GoSettingsCommand => new RelayCommand(GoConfigureProvider);
    [ObservableProperty] private string _qualityPromptPrefix = "masterpiece, best quality, score_9, score_8, highres, absurdres, anime screenshot, official art";
    [ObservableProperty] private string _customChars = "";
    [ObservableProperty] private string _referenceMediaPath = "";

    public sealed class MediaItem : ObservableObject
    {
        public required string FilePath { get; init; }
        public required string FileName { get; init; }
        public required string Kind { get; init; } // 图片/视频/音频
        public required int Index { get; init; }
        /// <summary>卡片标签（对齐 al 构造：`图片 1` / `视频 1` / `音频 1`）。</summary>
        public required string Label { get; init; }
        /// <summary>素材标签（对齐 enumerateTaggedMedia：&lt;Picture N&gt; / &lt;Video N&gt; / &lt;Audio N&gt;）。</summary>
        public required string Tag { get; init; }
        /// <summary>chip 文本（对齐 ul()：`@图片1`）。</summary>
        public string ChipText => Tag switch
        {
            var t when t.StartsWith("<Picture") => "@图片" + Index,
            var t when t.StartsWith("<Video") => "@视频" + Index,
            var t when t.StartsWith("<Audio") => "@音频" + Index,
            _ => "@文件" + Index,
        };
        /// <summary>类型配色（对齐 il：image #e6a23c / video #409eff / audio #67c23a / file #909399）。</summary>
        public string KindColor => Kind switch
        {
            "图片" => "#e6a23c",
            "视频" => "#409eff",
            _ => "#67c23a",
        };
        /// <summary>缩略图（图片=原图缩略；视频=null，封面由 LibVLC 抓帧填入 CoverFrame；音频=null 显示类型徽标）。</summary>
        public Avalonia.Media.Imaging.Bitmap? Thumbnail
        {
            get => _thumbnail;
            set { _thumbnail = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasThumbnail)); }
        }
        private Avalonia.Media.Imaging.Bitmap? _thumbnail;
        public bool HasThumbnail => Thumbnail != null;
        /// <summary>视频/音频可播放（卡片显示 ▶/♪ 播放按钮，点击用 LibVLC 内嵌播放，对齐 PromptMaster <video>/<audio>）。</summary>
        public bool IsPlayable => Kind is "视频" or "音频";
        public bool IsVideo => Kind == "视频";
        public bool IsAudio => Kind == "音频";
        /// <summary>LibVLC 播放器（惰性创建；视频用 RGBA 软渲染输出到 Frame，音频仅出声）。</summary>
        public MediaPlayer? Player
        {
            get => _player;
            set { _player = value; OnPropertyChanged(); }
        }
        private MediaPlayer? _player;
        /// <summary>播放状态（▶/⏸ 图标切换；由卡片 MediaVideoView 组件 TwoWay 同步）。</summary>
        public bool IsPlaying
        {
            get => _isPlaying;
            set { _isPlaying = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowPlaceholder)); }
        }
        private bool _isPlaying;
        /// <summary>无缩略图占位（音频卡）：未播放时显示 ♪ 徽标。</summary>
        public bool ShowPlaceholder => !HasThumbnail && !IsPlaying;
    }
    public AvaloniaList<MediaItem> ReferenceMedia { get; } = new();

    // 素材合法扩展名（对齐 PromptMaster Expand 前端 Qa 集合）
    private static readonly HashSet<string> SupportedExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "gif", "webp", "bmp",
        "mp4", "mov", "avi", "mkv", "webm",
        "mp3", "wav", "flac", "aac", "m4a", "ogg",
    };
    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
        { "jpg", "jpeg", "png", "gif", "webp", "bmp" };
    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
        { "mp4", "mov", "avi", "mkv", "webm" };

    private static bool IsSupportedPath(string p)
        => SupportedExts.Contains(Path.GetExtension(p).TrimStart('.').ToLowerInvariant());

    private static string ClassifyExt(string ext) =>
        ImageExts.Contains(ext) ? "图片" : VideoExts.Contains(ext) ? "视频" : "音频";

    /// <summary>对齐 enumerateTaggedMedia 的尖括号标签（&lt;Picture N&gt; / &lt;Video N&gt; / &lt;Audio N&gt;，序号按 kind 分组计数）。</summary>
    private static string BuildTag(string kind, int index) => kind switch
    {
        "图片" => $"<Picture {index}>",
        "视频" => $"<Video {index}>",
        _ => $"<Audio {index}>",
    };

    /// <summary>卡片标签（对齐 al 构造：`图片 N` / `视频 N` / `音频 N`，序号按 kind 分组计数）。</summary>
    private static string BuildLabel(string kind, int index) => kind switch
    {
        "图片" => string.Format(Localizer.Instance?["MediaKindImageLabel"] ?? "", index),
        "视频" => string.Format(Localizer.Instance?["MediaKindVideoLabel"] ?? "", index),
        _ => string.Format(Localizer.Instance?["MediaKindAudioLabel"] ?? "", index),
    };

    /// <summary>chip 文本（对齐 ul()：`@图片N` 无空格）。</summary>
    private static string BuildChipText(string kind, int index) => kind switch
    {
        "图片" => $"@图片{index}",
        "视频" => $"@视频{index}",
        _ => $"@音频{index}",
    };

    /// <summary>该 kind 下已有素材数（对齐 al 的 e[kind] 计数器，同类递增）。</summary>
    private int KindCount(string kind) => ReferenceMedia.Count(m => m.Kind == kind);

    /// <summary>图片缩略图（DecodeToWidth 320 省内存；失败返回 null）。</summary>
    private static Avalonia.Media.Imaging.Bitmap? LoadThumbnail(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(fs, 320);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>添加合法素材（去重），返回新增数。对齐 PromptMaster xl()。视频异步抽首帧做缩略图。</summary>
    public int AddMediaFiles(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var p in paths ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var ext = Path.GetExtension(p).TrimStart('.').ToLowerInvariant();
            if (!SupportedExts.Contains(ext)) continue;
            if (ReferenceMedia.Any(m => string.Equals(m.FilePath, p, StringComparison.OrdinalIgnoreCase))) continue;
            var kind = ClassifyExt(ext);
            var n = KindCount(kind) + 1; // 序号按 kind 分组（对齐 al 计数器）
            var item = new MediaItem
            {
                FilePath = p,
                FileName = Path.GetFileName(p),
                Kind = kind,
                Index = n,
                Label = BuildLabel(kind, n),
                Tag = BuildTag(kind, n),
                Thumbnail = ImageExts.Contains(ext) ? LoadThumbnail(p) : null,
            };
            ReferenceMedia.Add(item);
            // 视频播放/封面全部由卡片内 MediaVideoView 组件自管理（VideoPath 绑定 FilePath 自动加载），
            // ViewModel 不再创建视频 LibVLC 播放器 —— 对齐 Test 页已验证组件范式
            added++;
        }
        OnPropertyChanged(nameof(HasMedia));
        return added;
    }

    /// <summary>拖入素材处理（对齐 PromptMaster onDrop wl()：过滤/去重/提示）。</summary>
    public void ProcessMediaDrop(IReadOnlyList<string> paths)
    {
        if (paths == null || paths.Count == 0)
        {
            ShowToast(_local["ExpandDropMediaHint"], "");
            return;
        }
        var valid = paths.Where(IsSupportedPath).ToList();
        if (valid.Count == 0)
        {
            ShowToast(_local["ExpandDropInvalidFormat"], "");
            return;
        }
        var added = AddMediaFiles(valid);
        if (added > 0 && valid.Count < paths.Count)
            ShowToast(string.Format(_local["MediaAddedPartial"], added), "");
        else if (added == 0)
            ShowToast(_local["MediaAlreadyExists"], "");
    }

    [RelayCommand]
    private async Task PickReferenceAsync()
    {
        var window = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window == null) return;
        var files = await window.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = _local["SelectReferenceMedia"],
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new Avalonia.Platform.Storage.FilePickerFileType(_local["MediaFileTypeFilter"])
                {
                    Patterns = SupportedExts.Select(e => $"*.{e}").ToArray(),
                }
            },
        });
        if (files == null || files.Count == 0) return;
        var paths = files.Select(f => f.Path.LocalPath).ToList();
        var added = AddMediaFiles(paths);
        if (added == 0)
            ShowToast(_local["MediaAddNone"], "");
    }

    [RelayCommand]
    private void RemoveReference(MediaItem item)
    {
        DisposePlayer(item);
        ReferenceMedia.Remove(item);
        OnPropertyChanged(nameof(HasMedia));
        // 素材变化 → mention 过滤/空态刷新 + 已插入文本里的 <Picture N> chip 需同步（由 TextChanged 触发重建）
        RefreshMentionFiltered();
        if (ReferenceMedia.Count == 0) CloseMention();
    }

    // ---- LibVLC 播放（视频由卡片 MediaVideoView 组件自管理；音频播放兜底） ----

    /// <summary>预创建音频播放器（视频由卡片 MediaVideoView 组件自管理；此方法仅兜底，视频素材不再走这里）。</summary>
    private void EnsurePlaybackPlayer(MediaItem item)
    {
        try
        {
            if (item.Player != null) return;
            if (!item.IsVideo) return;
            var vlc = new LibVLC(enableDebugLogs: false, "--avcodec-hw=any", "--file-caching=300");
            var player = new MediaPlayer(vlc);
            player.Volume = 100;
            player.Playing += (_, _) =>
            {
                try { player.Time = 20; } catch { }
            };
            player.Playing += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => item.IsPlaying = true);
            player.Paused += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => item.IsPlaying = false);
            player.Stopped += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => item.IsPlaying = false);
            player.EndReached += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => item.IsPlaying = false);
            // 播放出错：释放播放器，保证不留下坏状态的 player（应用内播放，不弹系统播放器）
            player.EncounteredError += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => DisposePlayer(item));
            item.Player = player;
        }
        catch (Exception ex)
        {
            LogService.Instance.Error(string.Format(Localizer.Instance?["MediaPlayFailedLog"] ?? "播放器预创建失败: {0}", ex.Message), "Expand", ex);
        }
    }

    /// <summary>播放/暂停素材（音频走 LibVLC 出声；视频由卡片组件 PlayPause 处理，不经过这里）。</summary>
    [RelayCommand]
    private async Task PlayMedia(MediaItem item)
    {
        if (item == null || string.IsNullOrEmpty(item.FilePath) || !File.Exists(item.FilePath))
        {
            ShowToast(_local["MediaFileMissing"], "");
            return;
        }
        try
        {
            if (item.Player != null && item.Player.State is LibVLCSharp.Shared.VLCState.Error or LibVLCSharp.Shared.VLCState.Ended)
            {
                DisposePlayer(item);
            }
            if (item.Player == null)
            {
                EnsurePlaybackPlayer(item);
                if (item.Player == null)
                {
                    // LibVLC 不可用：仅提示，应用内播放（不弹系统播放器）
                    ShowToast(_local["MediaPlayFailed"], "");
                    return;
                }
            }
            if (item.Player.IsPlaying)
            {
                item.Player.Pause();
            }
            else
            {
                if (!item.Player.Play())
                {
                    DisposePlayer(item);
                    return;
                }
                await Task.Delay(250);
            }
        }
        catch (Exception ex)
        {
            ShowToast(_local["MediaPlayFailed"], "");
            LogService.Instance.Error(string.Format(_local["MediaPlayFailedLog"], ex.Message), "Expand", ex);
        }
    }

    // ---- LibVLC 播放（视频由卡片 MediaVideoView 组件自管理，音频播放兜底） ----

    /// <summary>释放素材的 LibVLC 播放器（停止+释放，防泄漏；Stop 可能在后台阻塞 → 后台执行）。</summary>
    private static void DisposePlayer(MediaItem item)
    {
        var p = item.Player;
        if (p != null)
        {
            item.Player = null;
            _ = Task.Run(() =>
            {
                try { p.Stop(); } catch { }
                try { p.Dispose(); } catch { }
            });
            item.IsPlaying = false;
        }
        item.IsPlaying = false;
    }

    [RelayCommand]
    private void ClearReferences()
    {
        foreach (var item in ReferenceMedia) DisposePlayer(item);
        ReferenceMedia.Clear();
        OnPropertyChanged(nameof(HasMedia));
        RefreshMentionFiltered();
        CloseMention();
    }

    public bool IsTagFormat =>
        SelectedRule?.Id is "sd_tags" or "danbooru_tags";

    public bool IsMinimax => SelectedRule?.Id == "minimax";

    /// <summary>质量词区显示条件（对齐 PromptMaster：!Ql && un，媒体型隐藏）。</summary>
    public bool ShowQualityPrompt => IsTagFormat && !IsMinimax;

    /// <summary>质量词内容框显示条件（对齐 PromptMaster：!Ql && un && zl，勾选后才显示）。</summary>
    public bool ShowQualityPromptBox => ShowQualityPrompt && QualityPromptEnabled;

    partial void OnQualityPromptEnabledChanged(bool value) => OnPropertyChanged(nameof(ShowQualityPromptBox));

    /// <summary>自定义字数（NumericUpDown 适配，对齐 PromptMaster input-number 1~20000）。</summary>
    public decimal? CustomCharsNumber
    {
        get => decimal.TryParse(CustomChars, out var d) ? d : null;
        set => CustomChars = value?.ToString() ?? "";
    }

    partial void OnCustomCharsChanged(string value)
    {
        OnPropertyChanged(nameof(CustomCharsNumber));
        if (_settingsLoaded) _settings?.Set(KeyLengthChars, value ?? "");
    }

    partial void OnCustomPromptChanged(string value)
    {
        if (_settingsLoaded) _settings?.Set(KeyCustomPrompt, value ?? "");
    }

    /// <summary>素材区是否有素材（清空按钮/继续添加文案切换）。</summary>
    public bool HasMedia => ReferenceMedia.Count > 0;

    /// <summary>创作需求输入框 placeholder（对齐 PromptMaster an()）。</summary>
    public string DemandPlaceholder => IsMinimax
        ? _local["DemandPlaceholderMinimax"]
        : _local["DemandPlaceholder"];

    /// <summary>结果区 placeholder（对齐 PromptMaster：媒体型含问询提示）。</summary>
    public string ResultPlaceholder => IsMinimax
        ? _local["ResultPlaceholderMinimax"]
        : _local["ResultPlaceholder"];

    /// <summary>非媒体型「篇幅：{label}」提示（对齐 PromptMaster ct() 展示）。</summary>
    public string LengthHint => IsMinimax || SelectedLength == null
        ? ""
        : string.Format(_local["LengthHintFormat"], SelectedLength.LabelUi);

    /// <summary>输出语言 radio（zh/en，对齐 PromptMaster radio-button 组）。</summary>
    public bool IsLangZh
    {
        get => SelectedOutputLang == "zh";
        set { if (value) SelectedOutputLang = "zh"; }
    }
    public bool IsLangEn
    {
        get => SelectedOutputLang == "en";
        set { if (value) SelectedOutputLang = "en"; }
    }

    partial void OnSelectedOutputLangChanged(string value)
    {
        OnPropertyChanged(nameof(IsLangZh));
        OnPropertyChanged(nameof(IsLangEn));
        if (_settingsLoaded) _settings?.Set(KeyOutputLang, value);
    }

    // ============================================================
    // @mention 素材引用（对齐反混淆前端 Ml/Nl/Dl/Tl/Ol；仅媒体型创作需求输入）
    // ============================================================

    [ObservableProperty] private bool _isMentionOpen;
    [ObservableProperty] private int _mentionIndex;
    public AvaloniaList<MediaItem> MentionFiltered { get; } = new();
    private string _mentionQuery = "";
    private int _mentionStart = -1;

    public bool HasMentionMedia => ReferenceMedia.Count > 0;
    public bool HasMentionMatch => MentionFiltered.Count > 0;
    public bool ShowMentionNoMatch => HasMentionMedia && !HasMentionMatch;
    public string MentionEmptyText => HasMentionMedia ? _local["MentionNoMatch"] : _local["MentionUploadFirst"];

    /// <summary>光标前文本变更时检测 @（对齐 Ol→Ml：/@([^\s@]*)$/；仅媒体型启用——自然语言无素材需求时不弹面板）。</summary>
    public void UpdateMention(string? textBeforeCaret)
    {
        if (!IsMinimax) { CloseMention(); return; }
        textBeforeCaret ??= "";
        var m = Regex.Match(textBeforeCaret, @"@([^\s@]*)$");
        if (!m.Success) { CloseMention(); return; }
        var start = textBeforeCaret.Length - m.Value.Length;
        var query = m.Groups[1].Value;
        var same = IsMentionOpen && _mentionStart == start && _mentionQuery == query;
        _mentionStart = start;
        _mentionQuery = query;
        RefreshMentionFiltered();
        if (!same) MentionIndex = 0;
        else if (MentionIndex >= MentionFiltered.Count) MentionIndex = Math.Max(0, MentionFiltered.Count - 1);
        IsMentionOpen = true;
    }

    /// <summary>按名称/类型/标签过滤（对齐 ll 面板：label/tag/name/`picture N` 英文拼写，al 为空提示上传，ll 为空提示无匹配）。</summary>
    private void RefreshMentionFiltered()
    {
        var q = _mentionQuery.Trim().ToLowerInvariant();
        MentionFiltered.Clear();
        foreach (var item in ReferenceMedia)
        {
            var enSpell = item.Kind switch
            {
                "图片" => $"picture {item.Index}",
                "视频" => $"video {item.Index}",
                _ => $"audio {item.Index}",
            };
            if (string.IsNullOrEmpty(q)
                || item.FileName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || item.Kind.Contains(q, StringComparison.OrdinalIgnoreCase)
                || item.Tag.Contains(q, StringComparison.OrdinalIgnoreCase)
                || item.Label.ToLowerInvariant().Contains(q)
                || enSpell.Contains(q))
            {
                MentionFiltered.Add(item);
            }
        }
        OnPropertyChanged(nameof(HasMentionMedia));
        OnPropertyChanged(nameof(HasMentionMatch));
        OnPropertyChanged(nameof(ShowMentionNoMatch));
        OnPropertyChanged(nameof(MentionEmptyText));
    }

    /// <summary>键盘上下循环移动选中（对齐 Dl ArrowDown/ArrowUp）。</summary>
    public void MoveMention(int delta)
    {
        if (MentionFiltered.Count == 0) return;
        MentionIndex = (MentionIndex + delta + MentionFiltered.Count) % MentionFiltered.Count;
    }

    /// <summary>关闭面板（对齐 Tl）。</summary>
    public void CloseMention()
    {
        IsMentionOpen = false;
        _mentionQuery = "";
        _mentionStart = -1;
        MentionIndex = 0;
    }

    /// <summary>点击面板项：定位选中（onMousedown → Nl(e)）。</summary>
    public void SelectMentionItem(MediaItem item)
    {
        var idx = MentionFiltered.IndexOf(item);
        if (idx >= 0) MentionIndex = idx;
    }

    /// <summary>
    /// 插入选中素材标签（对齐 Nl：`{tag} ` 替换 @ 段——文本层存 &lt;Picture N&gt; tag，显示层 chip 覆盖），返回新文本与新光标位。
    /// </summary>
    public (string Text, int Caret) ApplyMention(int caretIndex)
    {
        if (MentionFiltered.Count == 0 || MentionIndex < 0 || MentionIndex >= MentionFiltered.Count)
            return (UserInput ?? "", -1);
        // 两个空格：chip 边框覆盖「tag + 空格1」，光标落在空格2（chip 外，可见）
        var tag = $"{MentionFiltered[MentionIndex].Tag}  ";
        var text = UserInput ?? "";
        var start = _mentionStart >= 0 ? _mentionStart : Math.Max(0, caretIndex - 1);
        if (start < 0) start = 0;
        if (start > text.Length) start = text.Length;
        var end = Math.Min(Math.Max(caretIndex, start), text.Length);
        // 前导空格：@ 段前不是空格时补一个，让 chip 左侧有缩略图空间
        string lead = (start > 0 && text[start - 1] == ' ') ? "" : " ";
        var newText = text.Remove(start, end - start).Insert(start, lead + tag);
        // var newText = text.Remove(start, end - start).Insert(start, tag);
        CloseMention();
        return (newText, start + lead.Length + tag.Length);
    }

    /// <summary>动态表单字段（按选中的 PE profile 生成，catalog.js formFields 对齐）。</summary>
    public AvaloniaList<FormField> DynamicFields { get; } = new();

    public sealed partial class FormField : ObservableObject
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public bool Required { get; set; }
        public string Type { get; set; } = "text";
        public List<MiniMaxOption> Options { get; set; } = new();
        public bool IsSelect => Options.Count > 0;
        /// <summary>catalog default（normalizeForm 无值时填充）。</summary>
        public string DefaultValue { get; set; } = "";
        public string Placeholder { get; set; } = "";
        /// <summary>showIf 表达式（all/any/equals/notEquals/gte/lte，catalog matchShowIf）。null = 始终显示。</summary>
        public MiniMaxShowIf? ShowIf { get; set; }
        [ObservableProperty] private string _value = "";

        /// <summary>下拉选中项（Avalonia ComboBox 用对象绑定；value 与 Value 双向同步）。</summary>
        public MiniMaxOption? SelectedOption
        {
            get => Options.FirstOrDefault(o => o.Value == Value);
            set => Value = value?.Value ?? "";
        }

        public ExpandViewModel? Parent { get; set; }
        partial void OnValueChanged(string value)
        {
            OnPropertyChanged(nameof(SelectedOption));
            Parent?.OnFieldValueChanged(this);
        }
    }

    public string[] OutputLangItems { get; } = { "zh", "en" };
    public AvaloniaList<ProviderConfig> Providers { get; } = new();

    public sealed record ExpandRuleOption(string Id, string Name);
    public sealed record LengthOption(string Key, string LabelUi, int MaxTokens);
    public sealed record EngineOption(string Key, string Label, string Description);

    /// <summary>输出格式（对齐 PromptMaster promptEngineeringTaxonomy expand kinds 6 类）。</summary>
    public static readonly string[] OutputFormatIds =
        { "prose", "sd_tags", "danbooru_tags", "structured_md", "structured_json", "minimax" };

    // 选项列表缓存（避免每次 getter 返回新 List 造成 ComboBox 绑定/选中不稳定；语言变化时重建）
    private IReadOnlyList<ExpandRuleOption>? _ruleItemsCache;
    private IReadOnlyList<LengthOption>? _lengthItemsCache;

    public IReadOnlyList<ExpandRuleOption> RuleItems =>
        _ruleItemsCache ??= OutputFormatIds
            .Select(id => new ExpandRuleOption(id, _local[$"ExpandRuleName_{id}"]))
            .ToList();

    /// <summary>提示词工程列表：动态加载（kind=expand + enabledOnly，与 PE 页启用开关联动），按输出格式过滤。</summary>
    private IReadOnlyList<EngineOption>? _engineItemsCache;

    /// <summary>已加载的 expand 工程（随 PE 页增删改/启用开关变化重载）。</summary>
    private List<PromptEngineeringProfile> _expandProfiles = new();

    public IReadOnlyList<EngineOption> EngineItems =>
        _engineItemsCache ??= SelectedRule == null
            ? Array.Empty<EngineOption>()
            : _expandProfiles
                .Where(p => p.OutputFormat == SelectedRule.Id)
                .Select(p => new EngineOption(p.Id, p.Name, p.Description))
                .ToList();

    /// <summary>重载 expand 工程列表（PE 页变更事件触发；enabledOnly 过滤被禁用工程）。</summary>
    private async Task ReloadEnginesAsync()
    {
        try
        {
            _expandProfiles = await Task.Run(() => _peService.ListProfiles(kind: "expand", enabledOnly: true));
        }
        catch (Exception ex)
        {
            _expandProfiles = new List<PromptEngineeringProfile>();
            LogService.Instance.Warn(string.Format(_local["PeLoadFailedLog"], ex.Message), "Expand", ex);
        }
        RebuildEngineItems();
    }

    /// <summary>重建 PE 下拉：清缓存重算，保持当前选择；失效则回退第一项。</summary>
    private void RebuildEngineItems()
    {
        _engineItemsCache = null;
        OnPropertyChanged(nameof(EngineItems));
        if (SelectedEngine != null && EngineItems.Any(x => x.Key == SelectedEngine.Key))
            return;
        SelectedEngine = EngineItems.FirstOrDefault();
        OnPropertyChanged(nameof(IsTagFormat));
        OnPropertyChanged(nameof(IsMinimax));
        OnPropertyChanged(nameof(ShowQualityPrompt));
        OnPropertyChanged(nameof(DemandPlaceholder));
        OnPropertyChanged(nameof(ResultPlaceholder));
        OnPropertyChanged(nameof(LengthHint));
    }

    partial void OnSelectedRuleChanged(ExpandRuleOption? value)
    {
        try
        {
            _engineItemsCache = null; // 输出格式变化 → PE 列表重建（缓存实例稳定，防下拉选中不稳）
            OnPropertyChanged(nameof(EngineItems));
            OnPropertyChanged(nameof(IsTagFormat));
            OnPropertyChanged(nameof(IsMinimax));
            OnPropertyChanged(nameof(ShowQualityPrompt));
            OnPropertyChanged(nameof(DemandPlaceholder));
            OnPropertyChanged(nameof(ResultPlaceholder));
            OnPropertyChanged(nameof(LengthHint));
            SelectedEngine = EngineItems.FirstOrDefault();
            RebuildDynamicFields();
            CloseMention(); // 切换输出格式时关闭残留 mention 面板（素材保留，@ 随时可再开）
            if (_settingsLoaded) _settings?.Set(KeyRuleId, value?.Id ?? "");
        }
        catch (Exception ex)
        {
            // 任何意外都不能中断格式切换（ComboBox 选中已生效，仅记录）
            LogService.Instance.Warn(string.Format(_local["OutputFormatSwitchFailedLog"], ex.Message), "Expand", ex);
        }
    }

    partial void OnSelectedEngineChanged(EngineOption? value)
    {
        try
        {
            RebuildDynamicFields();
            CloseMention(); // 切换 PE 时关闭残留 mention 面板（素材保留）
            if (_settingsLoaded) _settings?.Set(KeyEngine, value?.Key ?? "");
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn(string.Format(_local["EngineSwitchFailedLog"], ex.Message), "Expand", ex);
        }
    }

    private readonly Dictionary<string, string> _fieldValues = new();

    /// <summary>当前引擎的 catalog 表单模板（MiniMaxScenarios 全量，含 default/required/showIf 表达式）。</summary>
    private List<MiniMaxFormField> GetTemplatesForEngine()
    {
        var key = SelectedEngine?.Key;
        if (string.IsNullOrEmpty(key) || !IsMinimax) return new();
        return MiniMaxScenarios.GetScenarioByPeId(key)?.FormFields ?? new();
    }

    // 重建动态字段期间抑制字段值回调（新 FormField 初值必然 ≠ ""，若 gate 字段被引用会无限递归 RebuildDynamicFields）
    private bool _suppressFieldEvents;

    private void RebuildDynamicFields()
    {
        if (_suppressFieldEvents) return;
        _suppressFieldEvents = true;
        try
        {
            // 先把当前已输入值缓存（必须在 Clear 之前）
            foreach (var f in DynamicFields)
                _fieldValues[f.Key] = f.Value;
            DynamicFields.Clear();
            if (!IsMinimax) return;

            var templates = GetTemplatesForEngine();

            // 按 showIf 表达式过滤（catalog matchShowIf），default 首次预填，值从缓存恢复
            foreach (var t in templates)
            {
                if (t.ShowIf != null && !t.ShowIf.Match(_fieldValues)) continue;
                if (!_fieldValues.TryGetValue(t.Key, out var cached) && !string.IsNullOrEmpty(t.Default))
                    _fieldValues[t.Key] = t.Default;
                var f = new FormField
                {
                    Key = t.Key,
                    Label = t.Label,
                    Type = t.Type,
                    Options = t.Options.ToList(),
                    Required = t.Required,
                    DefaultValue = t.Default,
                    Placeholder = t.Placeholder,
                    ShowIf = t.ShowIf,
                    Parent = this,
                    Value = _fieldValues.TryGetValue(t.Key, out var v) ? v : "",
                };
                DynamicFields.Add(f);
            }
        }
        finally
        {
            _suppressFieldEvents = false;
        }
    }

    /// <summary>showIf 表达式是否引用某字段（含 all/any 子树）。</summary>
    private static bool ReferencesKey(MiniMaxShowIf? expr, string key)
    {
        if (expr == null) return false;
        if (expr.Key == key) return true;
        if (expr.All != null && expr.All.Any(e => ReferencesKey(e, key))) return true;
        if (expr.Any != null && expr.Any.Any(e => ReferencesKey(e, key))) return true;
        return false;
    }

    private void OnFieldValueChanged(FormField changed)
    {
        if (_suppressFieldEvents) return; // 重建期间初值赋值不回调，避免递归
        // 只有当变更的字段被其他字段 showIf 依赖时才重建，避免打字时销毁输入框
        var templates = GetTemplatesForEngine();
        bool gatesOthers = templates.Any(t => t.ShowIf != null && ReferencesKey(t.ShowIf, changed.Key));
        // 同步缓存
        _fieldValues[changed.Key] = changed.Value;
        if (!gatesOthers) return;
        RebuildDynamicFields();
    }

    /// <summary>组装 MinimaxForm 字典（对齐 catalog normalizeForm：无值填 default，空值字段不传）。</summary>
    private Dictionary<string, string> BuildMinimaxForm()
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in GetTemplatesForEngine())
        {
            if (_fieldValues.TryGetValue(t.Key, out var v) && !string.IsNullOrEmpty(v))
                form[t.Key] = v;
            else if (!string.IsNullOrEmpty(t.Default))
                form[t.Key] = t.Default;
        }
        return form;
    }

    /// <summary>篇幅档：5 档预设 + 「自定义字数」档（对齐 PromptMaster expandLen 第 6 档）。</summary>
    public IReadOnlyList<LengthOption> LengthItems =>
        _lengthItemsCache ??= ExpandRules.Lengths
            .Select(l => new LengthOption(l.Key, _local[$"LengthLabel_{l.Key}"], l.MaxTokens))
            .Append(new LengthOption("custom", _local["LengthLabel_custom"], 0))
            .ToList();

    public string Title => _local["ExpandTitle"];

    /// <summary>是否选中「自定义字数」档（长度下拉第 6 档）。</summary>
    public bool IsCustomLength => SelectedLength?.Key == "custom";

    partial void OnSelectedLengthChanged(LengthOption? value)
    {
        try
        {
            OnPropertyChanged(nameof(IsCustomLength));
            OnPropertyChanged(nameof(LengthHint));
            if (_settingsLoaded) _settings?.Set(KeyLength, value?.Key ?? "");
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn(string.Format(_local["LengthSwitchFailedLog"], ex.Message), "Expand", ex);
        }
    }

    /// <summary>结果区是否有内容（复制/保存按钮可用性）。</summary>
    public bool HasResult => !string.IsNullOrWhiteSpace(Result);

    /// <summary>扩写结果字数（右下角展示，用户要求）。</summary>
    public string ResultCharCountText => string.Format(_local["ResultCharCountText"], Result.Length);

    partial void OnResultChanged(string value)
    {
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ResultCharCountText));
    }

    public override void OnSystemLangueChanged(object? data)
    {
        _ruleItemsCache = null;
        _lengthItemsCache = null;
        OnPropertyChanged(nameof(RuleItems));
        OnPropertyChanged(nameof(LengthItems));
    }

    public ExpandViewModel(ILocalizer localizer, IBaseNotice baseNotice, IServiceProvider services)
        : base(localizer, baseNotice)
    {
        _displayName = "EXPAND";
        _icon = MaterialIconKind.EditBox;
        _index = 40;
        _sideMenu = true;
        _services = services;
        _settings = services.GetRequiredService<IExpandSettingsStore>();
        _peService = new PmPromptEngineeringService(
            workspaceRoot: services.GetService<IWorkspaceService>()?.Root is { } wsRoot && wsRoot.Length > 0 ? wsRoot : null,
            dbFactory: services.GetRequiredService<IDbContextFactory<ComfyDbContext>>());
        _selectedRule = RuleItems[0];
        _selectedLength = LengthItems[2]; // medium 默认
        SelectedEngine = EngineItems.FirstOrDefault();
        // 设置页保存/删除提供商后刷新（VM 单例只构造一次，否则新加的服务商永远加载不到）
        _noticeService.Subscribe(EventNameConst.ProviderChangedEvent, _ => _ = ReloadProvidersAsync());
        // PE 页增删改/启用开关变化 → 重载 expand 工程下拉（enabledOnly 联动）
        _noticeService.Subscribe(EventNameConst.PromptEngineeringChangedEvent, _ => _ = ReloadEnginesAsync());
        _ = LoadProvidersAsync();
        _ = ReloadEnginesAsync();
    }

    private async Task LoadProvidersAsync()
    {
        try
        {
            // 1) 载入参数记忆（comfyui.db AppSettings KV）
            await _settings!.LoadAsync();

            // 2) 恢复规则/输出格式（对齐 pm_expand_rule_id 恢复）
            var ruleId = _settings.Get(KeyRuleId);
            if (!string.IsNullOrEmpty(ruleId))
            {
                var r = RuleItems.FirstOrDefault(x => x.Id == ruleId);
                if (r != null) SelectedRule = r;
            }

            // 3) 恢复输出语言（对齐：仅 zh/en，其它回落 zh）
            var lang = _settings.Get(KeyOutputLang);
            if (lang == "en") SelectedOutputLang = "en";
            else if (lang == "zh") SelectedOutputLang = "zh";

            // 4) 恢复长度档（对齐：expandLen 数字档→custom+chars）
            var lenKey = _settings.Get(KeyLength);
            if (!string.IsNullOrEmpty(lenKey))
            {
                var l = LengthItems.FirstOrDefault(x => x.Key == lenKey);
                if (l != null) SelectedLength = l;
            }
            var chars = _settings.Get(KeyLengthChars);
            if (!string.IsNullOrEmpty(chars) && SelectedLength?.Key == "custom")
                CustomChars = chars;

            // 5) 恢复自定义提示词（对齐 pm_expand_user_extra_prompt）
            var extra = _settings.Get(KeyCustomPrompt);
            if (!string.IsNullOrEmpty(extra)) CustomPrompt = extra;

            // 6) 恢复提示词工程（对齐 pm_expand_engine 兜底；注意在规则恢复后，EngineItems 已重建）
            var engineKey = _settings.Get(KeyEngine);
            if (!string.IsNullOrEmpty(engineKey))
            {
                var e = EngineItems.FirstOrDefault(x => x.Key == engineKey);
                if (e != null) SelectedEngine = e;
            }

            // 7) 恢复完成：此后变更才写库（对齐 PromptMaster confirm On 标志位）
            _settingsLoaded = true;

            // 8) 加载提供商并恢复模型（对齐 captionModel 恢复 + 默认兜底 IsDefaultExpand）
            await ReloadProvidersAsync();
        }
        catch { }
    }

    /// <summary>
    /// 重载提供商列表并恢复选中模型（ProviderChangedEvent 触发时复用，只动提供商/模型，不重置整页状态）。
    /// </summary>
    private async Task ReloadProvidersAsync()
    {
        try
        {
            var svc = _services.GetRequiredService<IProviderService>();
            var all = await svc.GetAllAsync(default);
            Providers.Clear();
            foreach (var p in all) Providers.Add(p);

            var provId = _settings?.Get(KeyCaptionProvider);
            var modelName = _settings?.Get(KeyCaptionModel);
            ProviderConfig? selProv = null;
            ProviderModel? selModel = null;
            if (!string.IsNullOrEmpty(provId) && !string.IsNullOrEmpty(modelName))
            {
                selProv = Providers.FirstOrDefault(p => p.Id == provId);
                selModel = selProv?.Models.FirstOrDefault(m => m.ModelName == modelName);
                if (selModel == null) selProv = null;
            }
            if (selProv == null || selModel == null)
            {
                foreach (var p in Providers)
                {
                    var m = p.Models.FirstOrDefault(x => x.IsDefaultExpand);
                    if (m != null) { selProv = p; selModel = m; break; }
                }
            }
            SelectedProvider = selProv ?? Providers.FirstOrDefault();
            SelectedModel = selModel ?? SelectedProvider?.Models.FirstOrDefault();
        }
        catch (Exception ex)
        {
            PromptCraft.Service.LogService.Instance.Warn(string.Format(_local["LoadProvidersFailedLog"], ex.Message), "Expand");
        }
    }

    [RelayCommand]
    private async Task GenerateAsync()
    {
        if (IsBusy) return;

        // 内容校验（对齐 kt()：创作需求非空 或（媒体型 && 有素材），否则 warning）
        if (string.IsNullOrWhiteSpace(UserInput) && (!IsMinimax || ReferenceMedia.Count == 0))
        {
            ShowToast(IsMinimax ? _local["InputRequiredWithMedia"] : _local["InputRequired"], "");
            return;
        }
        // 媒体型必填校验（对齐 kt()：required 字段逐个提示「请填写：{label}」，一个缺失即返回）
        if (IsMinimax)
        {
            foreach (var f in DynamicFields)
            {
                if (f.Required && string.IsNullOrWhiteSpace(f.Value))
                {
                    ShowToast(string.Format(_local["FieldRequiredFormat"], f.Label), "");
                    return;
                }
            }
        }

        IsBusy = true;
        ErrorMessage = "";
        Result = ""; // 对齐 kt()：提交时清空结果区
        _cts = new CancellationTokenSource();
        var engineName = SelectedEngine?.Label ?? SelectedEngine?.Key ?? _local["PeEngineDefaultName"];
        var modelName = SelectedModel?.ModelName ?? "";
        LogService.Instance.Info(IsMinimax
            ? string.Format(_local["SubmitMediaExpandLog"], engineName, modelName)
            : string.Format(_local["SubmitExpandLog"], modelName), "Expand");
        try
        {
            var svc = _services.GetRequiredService<IExpandService>();
            Result = await svc.ExpandAsync(new ExpandRequest
            {
                // 文本层存 <Picture N> tag（显示层 chip 覆盖），发送直接传
                ShortText = UserInput ?? "",
                PeId = SelectedEngine?.Key,
                RuleId = SelectedRule?.Id,
                // 媒体型不传长度档/自定义字数（对齐 kt()：expandLen/expandLenChars = undefined）
                LengthKey = IsMinimax ? null : SelectedLength?.Key,
                LengthChars = IsMinimax || SelectedLength?.Key != "custom" ? null : CustomChars,
                OutputLang = SelectedOutputLang,
                // 媒体型不传自定义提示词、禁用质量词（对齐 kt()：userExtraPrompt="" / quality_prompt_enabled=false）
                CustomPrompt = IsMinimax ? "" : (CustomPrompt ?? ""),
                QualityPromptEnabled = !IsMinimax && QualityPromptEnabled,
                QualityPromptPrefix = QualityPromptPrefix,
                Type = SelectedRule?.Id == "danbooru_tags" ? "Danbooru_tag_list" : "Stable_Diffusion_Prompt",
                MinimaxForm = IsMinimax ? BuildMinimaxForm() : null,
                MediaPaths = ReferenceMedia.Select(m => m.FilePath).ToList(),
            }, SelectedProvider, SelectedModel, _cts.Token);
            // 完成提示（对齐 gt()：扩写完成）
            if (!string.IsNullOrWhiteSpace(Result))
                ShowToast(_local["ExpandDone"], "");
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = _local["ExpandCancelled"];
            ShowToast(_local["ExpandStopped"], "");
            LogService.Instance.Info(_local["ExpandStoppedLog"], "Expand");
        }
        catch (Exception ex)
        {
            // 对齐 kt() 失败分支：toast + 错误信息；空消息兜底（避免「返回为空」）
            var msg = string.IsNullOrWhiteSpace(ex.Message) ? _local["ExpandFailed"] : ex.Message;
            ErrorMessage = msg;
            ShowToast(msg, "");
            LogService.Instance.Error(string.Format(_local["ExpandFailedLog"], msg), "Expand", ex);
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private CancellationTokenSource? _cts;

    /// <summary>停止扩写（对齐 PromptMaster stopTextExpand：先写日志再取消）。</summary>
    [RelayCommand]
    private void Stop()
    {
        LogService.Instance.Info(_local["ExpandStopRequestedLog"], "Expand");
        _cts?.Cancel();
    }

    [RelayCommand]
    private async Task CopyResult()
    {
        // 对齐 qt()：空内容 info「提示词为空」
        if (string.IsNullOrWhiteSpace(Result))
        {
            ShowToast(_local["PromptEmpty"], "");
            return;
        }
        try
        {
            var clip = _services.GetRequiredService<IBaseClipboardService>();
            clip.CopyToClipboard(Result);
            ShowToast(_local["ExpandCopied"], "");
        }
        catch (Exception ex)
        {
            ShowToast(_local["CopyFailed"], "");
            PromptCraft.Service.LogService.Instance.Warn(string.Format(_local["CopyExpandFailedLog"], ex.Message), "Expand");
        }
    }

    [RelayCommand]
    private async Task SaveToLibraryAsync()
    {
        // 对齐 Lt()：无内容 warning「没有可保存的内容」
        if (string.IsNullOrWhiteSpace(Result))
        {
            ShowToast(_local["NothingToSave"], "");
            return;
        }
        try
        {
            await PromptCraft.ViewModels.PromptLibrary.PromptEditDialogOpener.OpenForExpandAsync(_services, Result, UserInput ?? "");
        }
        catch (Exception ex)
        {
            PromptCraft.Service.LogService.Instance.Error(_local["OpenSaveDialogFailedLog"], "Expand", ex);
        }
    }

    private void ShowToast(string title, string content)
    {
        try
        {
            var toastManager = _services.GetService(typeof(ISukiToastManager)) as ISukiToastManager;
            if (toastManager == null) return;
            var toast = FluentSukiToastBuilder.CreateSimpleInfoToast(toastManager);
            toast.SetTitle(title);
            toast.SetContent(content);
            toast.SetCanDismissByClicking(true);
            toast.Toast.DismissTimeout = TimeSpan.FromSeconds(3);
            toast.Queue();
        }
        catch
        {
            // toast 失败不影响主流程
        }
    }

    /// <summary>页面销毁：释放全部 LibVLC 播放器（防句柄泄漏）。</summary>
    public override void Disposed()
    {
        foreach (var item in ReferenceMedia) DisposePlayer(item);
        base.Disposed();
    }
}
