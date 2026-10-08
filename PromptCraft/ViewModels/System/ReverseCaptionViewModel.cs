using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
using PromptCraft.Service.Inference.Reverse;
using Ke.Bee.Localization.Localizer;
using Ke.Bee.Localization.Localizer.Abstractions;
using LibVLCSharp.Shared;
using Material.Icons;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SukiUI.Controls;
using SukiUI.MessageBox;
using SukiUI.Toasts;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.System;

/// <summary>
/// 反推页（1:1 对齐 PromptMaster PmReverse）：
/// 打标对象 / 输出格式 / 提示词工程（PE 注册表）/ 输出语言 / 打标模型 / 质量词 / 长度 / 附加要求 / torii 更多设置；
/// 素材多选批量反推、进度、停止、复制、单条/批量保存到词库；无日志面板（写日志系统）。
/// </summary>
public partial class ReverseCaptionViewModel : ViewModelBase, ISukiStackPageTitleProvider
{
    private readonly IServiceProvider _services;
    private readonly PmPromptEngineeringService _peService;
    private readonly IExpandSettingsStore _settings;
    private CancellationTokenSource? _cts;
    private bool _settingsLoaded;
    private List<PromptEngineeringProfile>? _sourceProfiles;

    private const string KeyCaptionModel = "pm_reverse_caption_model";
    private const string KeyCaptionProvider = "pm_reverse_caption_provider"; // PromptCraft 扩展：跨 provider 同名模型消歧
    private const string KeyViewMode = "pm_reverse_view_mode";               // grid / list 记忆（对齐 PM 素材视图记忆）

    // ==================== 素材区 ====================

    public ObservableCollection<ReverseMediaItem> Assets { get; } = new();

    /// <summary>网格 / 卡片（列表）视图切换（对齐 PromptMaster media-grid / media-card；选择记忆到 comfyui.db）。</summary>
    [ObservableProperty]
    private bool _isGridView;

    partial void OnIsGridViewChanged(bool value)
    {
        // 对齐扩写页 _settingsLoaded 语义：仅恢复完成后写回，避免启动时覆盖记忆
        if (_settingsLoaded) _settings?.Set(KeyViewMode, value ? "grid" : "list");
    }

    [ObservableProperty] private ReverseMediaItem? _selectedAsset;

    // ==================== 打标对象 ====================

    public sealed record MediaTargetOption(string Value, string Label);

    private static List<MediaTargetOption> BuildMediaTargetItems() => new()
    {
        new("mixed", Localizer.Instance?["ReverseFormatMixed"] ?? "Mixed"),
        new("image", Localizer.Instance?["ReverseFormatImage"] ?? "Image"),
        new("video", Localizer.Instance?["ReverseFormatVideo"] ?? "Video"),
    };

    public IReadOnlyList<MediaTargetOption> MediaTargetItems { get; private set; } = BuildMediaTargetItems();

    [ObservableProperty] private string _mediaTarget = "mixed";

    partial void OnMediaTargetChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedMediaTarget));
        FilterAssetsByMediaTarget(value);
    }

    /// <summary>打标对象下拉选中项（对象绑定）。</summary>
    public MediaTargetOption? SelectedMediaTarget
    {
        get => MediaTargetItems.FirstOrDefault(o => o.Value == MediaTarget);
        set => MediaTarget = value?.Value ?? "mixed";
    }

    // ==================== 输出格式 / 提示词工程 ====================

    public sealed record OutputFormatOption(string Id, string Label);
    private static List<OutputFormatOption> BuildOutputFormatItems() => new()
    {
        new("prose", Localizer.Instance?["PeFormatProse"] ?? "Prose"),
        new("sd_tags", Localizer.Instance?["PeFormatSdTags"] ?? "SD Tags"),
        new("danbooru_tags", Localizer.Instance?["PeFormatDanbooru"] ?? "Danbooru Tags"),
        new("structured_md", Localizer.Instance?["PeFormatStructuredMd"] ?? "Structured MD"),
        new("structured_json", Localizer.Instance?["PeFormatStructuredJson"] ?? "Structured JSON"),
    };
    public IReadOnlyList<OutputFormatOption> OutputFormatItems { get; private set; } = BuildOutputFormatItems();

    [ObservableProperty] private string _peOutputFormat = "prose";

    /// <summary>输出格式下拉选中项（Avalonia ComboBox 无 SelectedValuePath，用对象绑定）。</summary>
    public OutputFormatOption? SelectedOutputFormat
    {
        get => OutputFormatItems.FirstOrDefault(o => o.Id == PeOutputFormat);
        set => PeOutputFormat = value?.Id ?? "";
    }

    public ObservableCollection<PeProfileOption> PeProfiles { get; } = new();

    /// <summary>按当前输出格式过滤后的 PE 下拉（对齐前端 filteredProfiles 计算属性：非破坏式，源集合 PeProfiles 不被移除）。</summary>
    public ObservableCollection<PeProfileOption> VisiblePeProfiles { get; } = new();

    [ObservableProperty] private PeProfileOption? _selectedPe;

    partial void OnSelectedPeChanged(PeProfileOption? value)
    {
        // 对齐前端：torii 工程选中时应用其 useNames 默认值；Descriptive 工程关闭质量词
        if (value != null && value.IsTorii)
        {
            ToriiUseNames = value.UseNamesDefault;
        }
        if (value != null && value.BuiltinKey == "Descriptive")
        {
            QualityPromptEnabled = false;
        }
        OnPropertyChanged(nameof(IsTagLinePe));
        OnPropertyChanged(nameof(IsToriiPe));
        OnPropertyChanged(nameof(LengthLabel));
        OnPropertyChanged(nameof(LengthHint));
        OnPropertyChanged(nameof(ToriiPanelTitle));
        OnPropertyChanged(nameof(ExtraPromptPlaceholder));
    }

    // ==================== 输出语言（显示 中文/English，对齐 PromptMaster） ====================

    public IReadOnlyList<TrainChoice> LangItems { get; } = new List<TrainChoice>
    {
        new("zh", "中文"),
        new("en", "English"),
    };

    [ObservableProperty] private string _captionLang = "zh";

    public TrainChoice? SelectedLang
    {
        get => LangItems.FirstOrDefault(x => x.Key == CaptionLang);
        set { if (value != null && value.Key != CaptionLang) CaptionLang = value.Key; }
    }

    partial void OnCaptionLangChanged(string value) => OnPropertyChanged(nameof(SelectedLang));

    // ==================== 打标模型（服务商两级，对齐扩写页：记忆恢复 + IsDefaultReverse 兜底） ====================

    public AvaloniaList<ProviderConfig> Providers { get; } = new();

    [ObservableProperty] private ProviderConfig? _selectedProvider;

    partial void OnSelectedProviderChanged(ProviderConfig? value)
    {
        if (_settingsLoaded) _settings?.Set(KeyCaptionProvider, value?.Id ?? "");
        // 对齐现有反推页：切服务商自动选其默认反推模型
        SelectedModel = value?.Models.FirstOrDefault(m => m.IsDefaultReverse)
                        ?? value?.Models.FirstOrDefault(m => m.UseForReverse)
                        ?? value?.Models.FirstOrDefault();
        OnPropertyChanged(nameof(HasProviderModels));
        OnPropertyChanged(nameof(ProviderModelPlaceholder));
        OnPropertyChanged(nameof(ProviderModelEmptyText));
    }

    /// <summary>当前服务商是否有可选的打标模型（无模型时展示"去配置"提示，对齐 PromptMaster pm-online-model-hint）。</summary>
    public bool HasProviderModels => SelectedProvider?.Models.Count > 0;

    /// <summary>服务商模型下拉占位文案（有模型/无模型两种）。</summary>
    public string ProviderModelPlaceholder => HasProviderModels
        ? (_local["TrainProviderModelPlaceholder"] ?? "选择该服务下的模型")
        : (_local["TrainProviderModelPlaceholderEmpty"] ?? "请先到设置配置并勾选打标模型");

    /// <summary>服务商模型空态提示（无模型时显示在第二个下拉下方）。</summary>
    public string ProviderModelEmptyText => string.Format(
        _local["TrainProviderModelEmptyFormat"] ?? "{0} 未配置打标模型，请到设置页勾选后使用",
        SelectedProvider?.Name ?? _local["TrainProviderModelUnknown"] ?? "当前服务商");

    /// <summary>"去配置"跳转设置页（对齐 PromptMaster 去配置按钮）。</summary>
    public IRelayCommand GoSettingsCommand => new RelayCommand(() =>
    {
        LogService.Instance.Info("反推：跳转设置页配置打标模型", "ReverseCaption");
        _noticeService.Publish(PromptCraft.Consts.Event.EventNameConst.SystemNavigatePageEvent, typeof(SettingModel));
    });

    [ObservableProperty] private ProviderModel? _selectedModel;

    partial void OnSelectedModelChanged(ProviderModel? value)
    {
        if (_settingsLoaded) _settings?.Set(KeyCaptionModel, value?.ModelName ?? "");
        // 对齐前端 ot()：torii 模型输出语言锁定英文
        if (IsModelTorii && CaptionLang != "en")
        {
            CaptionLang = "en";
        }
        OnPropertyChanged(nameof(IsModelTorii));
        OnPropertyChanged(nameof(IsLangLocked));
        ApplyModelToMediaTarget();
    }

    /// <summary>当前打标模型是否为 ToriiGate 类（只支持图片 + 锁英文输出，对齐前端 pe()/st()）。</summary>
    public bool IsModelTorii
        => SelectedModel != null && CaptionModels.IsToriiGateCaptionModel(SelectedModel.ModelName);

    /// <summary>torii 模型时输出语言锁定英文（对齐前端 ot()）。</summary>
    public bool IsLangLocked => IsModelTorii;

    /// <summary>对齐前端 a(Ka)：torii 模型强制打标对象=图片。</summary>
    private void ApplyModelToMediaTarget()
    {
        if (IsModelTorii && MediaTarget != "image")
        {
            MediaTarget = "image";
            OnPropertyChanged(nameof(IsMediaTargetLocked));
        }
    }

    /// <summary>torii 模型时打标对象仅图片可选（对齐前端 st()）。</summary>
    public bool IsMediaTargetLocked => IsModelTorii;

    // ==================== 质量提示词（tag 类工程） ====================

    [ObservableProperty] private bool _qualityPromptEnabled;

    [ObservableProperty] private string _qualityPromptPrefix =
        "masterpiece, best quality, score_9, score_8, highres, absurdres, anime screenshot, official art";

    // ==================== 提示词长度 / 标签量 ====================

    public sealed record LengthOption(string Key, string Label, string? TagHint = null);
    public IReadOnlyList<LengthOption> LengthItems { get; }

    [ObservableProperty] private LengthOption? _selectedLength;

    [ObservableProperty] private decimal _customLenChars = 300;

    public bool IsCustomLen => SelectedLength?.Key == "custom";

    partial void OnSelectedLengthChanged(LengthOption? value) => OnPropertyChanged(nameof(IsCustomLen));

    // ==================== 附加要求 ====================

    [ObservableProperty] private string _extraPrompt = "";

    // ==================== Torii 更多设置 ====================

    [ObservableProperty] private bool _toriiUseNames = true;

    [ObservableProperty] private bool _toriiAddTags;

    [ObservableProperty] private string _toriiGroundingTags = "";

    [ObservableProperty] private string _toriiGroundingCharacters = "";

    // ==================== 运行状态 ====================

    [ObservableProperty] private bool _isBusy;

    // ==================== 计算属性 ====================

    /// <summary>一行式标签工程（对齐前端 dt()：非 torii 且 builtinKey 为 SD/Danbooru，自定义默认 SD）：显示质量词与「标签量」。</summary>
    public bool IsTagLinePe
        => !IsToriiPe && (SelectedPe?.BuiltinKey is null or "Stable_Diffusion_Prompt" or "Danbooru_tag_list");

    /// <summary>Torii 结构化工程：显示「更多设置」。</summary>
    public bool IsToriiPe => SelectedPe?.IsTorii == true;

    /// <summary>自定义提示词 placeholder（对齐前端：torii 英文 / 其它中文）。</summary>
    public string ExtraPromptPlaceholder => IsToriiPe
        ? _local["ReverseExtraPlaceholderEn"]
        : _local["ReverseExtraPlaceholder"];

    public string LengthLabel => IsTagLinePe ? _local["ReverseLengthLabelTag"] : _local["ReverseLengthLabel"];

    public string LengthHint
    {
        get
        {
            if (IsCustomLen)
            {
                var n = (int)CustomLenChars;
                return n > 0
                    ? string.Format(_local["ReverseCustomLenSetFormat"], n)
                    : _local["ReverseCustomLenInputPrompt"];
            }
            if (IsTagLinePe)
            {
                // tag 密度提示（对齐 Reverse-0f57c870.js gt 映射）
                var density = SelectedLength?.TagHint ?? "";
                return string.Format(_local["ReverseTagDensityHint"], density.Length > 0 ? density + "，" : "", ResolveLenMaxTokens(SelectedLength?.Key));
            }
            return string.Format(_local["ReverseLenConsistentHint"], ResolveLenMaxTokens(SelectedLength?.Key));
        }
    }

    /// <summary>tag 类长度档位密度提示（gt 映射：very_short 约 8～15 个 tag / short 15～25 / medium 25～40 / long 35～55 / very_long 45～70）。</summary>
    private static string TagDensityHint(string key) => key switch
    {
        "very_short" => Localizer.Instance?["ReverseTagDensityVeryShort"] ?? "≈8-15 tags",
        "short" => Localizer.Instance?["ReverseTagDensityShort"] ?? "≈15-25 tags",
        "medium" => Localizer.Instance?["ReverseTagDensityMedium"] ?? "≈25-40 tags",
        "long" => Localizer.Instance?["ReverseTagDensityLong"] ?? "≈35-55 tags",
        "very_long" => Localizer.Instance?["ReverseTagDensityVeryLong"] ?? "≈45-70 tags",
        _ => "",
    };

    // ==================== 4 段控件提示文案（对齐 Reverse-0f57c870.js vl / cl / pl / ml） ====================

    /// <summary>打标对象提示（vl）。</summary>
    public string MediaTargetHint =>
        _local["ReverseMediaTargetHint"];

    /// <summary>角色名提示（cl）。</summary>
    public string ToriiUseNamesHint =>
        _local["ReverseUseNamesHint"];

    /// <summary>注入提示（pl）。</summary>
    public string ToriiInjectHint =>
        _local["ReverseInjectHint"];

    /// <summary>角色名列表提示（ml）。</summary>
    public string ToriiRoleListHint =>
        _local["ReverseRoleListHint"];

    public string ToriiPanelTitle => _local["ReverseToriiPanelTitle"];

    public int DoneCount => Assets.Count(a => !string.IsNullOrWhiteSpace(a.Caption));

    public int AssetsCount => Assets.Count;

    public string AssetsTitle => string.Format(_local["ReverseAssetsTitleFormat"] ?? "素材 ({0})", AssetsCount);

    public string BatchSaveLabel => string.Format(_local["ReverseBatchSaveLabel"], DoneCount);

    public string Title => _local["ReverseCaptionTitle"];

    public string? SelectedCaption => SelectedAsset?.Caption;

    /// <summary>反推结果文字数量（右下角显示；随选中素材与编辑实时刷新）。</summary>
    public int SelectedCaptionLength => SelectedAsset?.Caption?.Length ?? 0;

    public string SelectedCaptionLengthText => string.Format(_local["ReverseCaptionLengthFormat"] ?? "共 {0} 字", SelectedCaptionLength);

    /// <summary>上次订阅 PropertyChanged 的素材（退订防泄漏）。</summary>
    private ReverseMediaItem? _captionSubscribed;

    partial void OnSelectedAssetChanged(ReverseMediaItem? value)
    {
        if (_captionSubscribed != null)
            _captionSubscribed.PropertyChanged -= OnAssetPropertyChanged;
        _captionSubscribed = value;
        if (_captionSubscribed != null)
            _captionSubscribed.PropertyChanged += OnAssetPropertyChanged;
        OnPropertyChanged(nameof(SelectedCaptionLength));
        OnPropertyChanged(nameof(SelectedCaptionLengthText));
        OnPropertyChanged(nameof(SelectedCaption));
    }

    private void OnAssetPropertyChanged(object? sender, global::System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReverseMediaItem.Caption))
        {
            OnPropertyChanged(nameof(SelectedCaptionLength));
        OnPropertyChanged(nameof(SelectedCaptionLengthText));
            OnPropertyChanged(nameof(SelectedCaptionLengthText));
            OnPropertyChanged(nameof(SelectedCaption));
        }
    }

    // ==================== 构造 ====================

    public ReverseCaptionViewModel(ILocalizer localizer, IBaseNotice baseNotice, IServiceProvider services)
        : base(localizer, baseNotice)
    {
        _displayName = "REVERSECAPTION";
        _icon = MaterialIconKind.ImageSearch;
        _index = 50;
        _sideMenu = true;
        _services = services;
        _settings = services.GetRequiredService<IExpandSettingsStore>();
        _peService = new PmPromptEngineeringService(
            workspaceRoot: ResolveWorkspaceRoot(),
            dbFactory: services.GetRequiredService<IDbContextFactory<ComfyDbContext>>());

        var presets = PromptCraft.Service.Inference.ExpandRules.Lengths
            .Select(l => new LengthOption(l.Key, _local[$"LengthLabel_{l.Key}"], TagDensityHint(l.Key)))
            .ToList();
        presets.Add(new LengthOption("custom", _local["LengthLabel_custom"], _local["ReverseCustomLenHint"]));
        LengthItems = presets;

        _selectedLength = LengthItems.FirstOrDefault(x => x.Key == "long") ?? LengthItems[3];

        // 设置页保存/删除提供商后刷新（VM 单例只构造一次，否则新加的服务商永远加载不到）
        _noticeService.Subscribe(EventNameConst.ProviderChangedEvent, _ => _ = ReloadProvidersAsync());
        // PE 页增删改/启用开关变化 → 重载反推工程下拉（enabledOnly 联动：被禁用工程不出现在选择框）
        _noticeService.Subscribe(EventNameConst.PromptEngineeringChangedEvent, _ => _ = LoadPeProfilesAsync());
        // PE 页增删改/启用开关变化 → 重载 PE 下拉（enabledOnly 联动，被禁用工程不出现在选项框）
        _noticeService.Subscribe(EventNameConst.PromptEngineeringChangedEvent, _ => _ = LoadPeProfilesAsync());

        _ = InitializeAsync();
    }

    private static string ResolveWorkspaceRoot()
    {
        try
        {
            var svc = new WorkspaceService();
            return string.IsNullOrEmpty(svc.Root) ? WorkspaceService.DefaultRoot : svc.Root;
        }
        catch
        {
            return WorkspaceService.DefaultRoot;
        }
    }

    private async Task InitializeAsync()
    {
        await _settings!.LoadAsync();
        // 恢复视图选择（对齐 PM：media-grid / media-card 记忆；缺省 grid）
        var viewMode = _settings.Get(KeyViewMode);
        if (viewMode == "list") IsGridView = false;
        else if (viewMode == "grid") IsGridView = true;
        await LoadProvidersAsync();
        await LoadPeProfilesAsync();
    }

    private async Task LoadProvidersAsync()
    {
        try
        {
            await _settings!.LoadAsync();
            _settingsLoaded = true;
            await ReloadProvidersAsync();
        }
        catch (Exception ex)
        {
            PromptCraft.Service.LogService.Instance.Warn(string.Format(_local["LoadProvidersFailedLog"], ex.Message), "ReverseCaption");
        }
    }

    /// <summary>
    /// 重载提供商列表并恢复选中模型（ProviderChangedEvent 触发时复用，只动提供商/模型，不重置页面其他状态）。
    /// </summary>
    private async Task ReloadProvidersAsync()
    {
        try
        {
            var svc = _services.GetRequiredService<IProviderService>();
            var all = await svc.GetAllAsync(default);
            Providers.Clear();
            foreach (var p in all)
                Providers.Add(p);

            // 对齐扩写页：先恢复记忆（providerId + modelName），失败兜底 IsDefaultReverse
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
                    var m = p.Models.FirstOrDefault(x => x.IsDefaultReverse);
                    if (m != null) { selProv = p; selModel = m; break; }
                }
            }
            SelectedProvider = selProv ?? Providers.FirstOrDefault();
            SelectedModel = selModel ?? SelectedProvider?.Models.FirstOrDefault();
        }
        catch (Exception ex)
        {
            PromptCraft.Service.LogService.Instance.Warn(string.Format(_local["LoadProvidersFailedLog"], ex.Message), "ReverseCaption");
        }
    }

    // ==================== PE 列表（对齐 listPromptEngineering kind=reverse enabledOnly + 输出格式过滤） ====================

    private async Task LoadPeProfilesAsync()
    {
        try
        {
            var profiles = await Task.Run(() => _peService.ListProfiles(kind: "reverse", enabledOnly: true));
            _sourceProfiles = profiles;
            PeProfiles.Clear();
            foreach (var p in profiles)
            {
                PeProfiles.Add(PeProfileOption.From(p));
            }
            RefreshPeByOutputFormat();

            // 保持当前选择；失效则取第一个（对齐前端 ensureValidSelection）
            if (SelectedPe != null && PeProfiles.Any(x => x.Id == SelectedPe.Id))
                return;
            SelectedPe = PeProfiles.FirstOrDefault(x => x.Id == "pe_reverse_descriptive")
                         ?? PeProfiles.FirstOrDefault();
        }
        catch (Exception ex)
        {
            PromptCraft.Service.LogService.Instance.Warn(string.Format(_local["PeLoadFailedLog"], ex.Message), "ReverseCaption");
        }
    }

    partial void OnPeOutputFormatChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedOutputFormat));
        RefreshPeByOutputFormat();
    }

    private void RefreshPeByOutputFormat()
    {
        // 对齐 usePromptEngineeringPicker 的 filteredProfiles：输出格式变化 → 重过滤 PE 下拉（不破坏 PeProfiles 源集合），
        // 并默认选中过滤后第一项（对齐 ensureValidSelection 的无匹配重置 + 用户验收：切格式后选第一项）。
        var filtered = PeProfiles.Where(p => p.OutputFormat == PeOutputFormat).ToList();
        VisiblePeProfiles.Clear();
        foreach (var item in filtered)
            VisiblePeProfiles.Add(item);
        SelectedPe = VisiblePeProfiles.FirstOrDefault();
    }

    // ==================== 素材 ====================

    public void AddPaths(IEnumerable<string> paths)
    {
        var list = (paths ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (list.Count == 0) return;
        var added = 0;
        var skipped = 0;
        var known = new HashSet<string>(Assets.Select(a => NormalizePath(a.Path)), StringComparer.OrdinalIgnoreCase);
        foreach (var p in list)
        {
            var isImage = IsImagePath(p);
            var isVideo = IsVideoPath(p);
            if (!isImage && !isVideo) { skipped++; continue; }
            if (MediaTarget == "image" && !isImage) { skipped++; continue; }
            if (MediaTarget == "video" && !isVideo) { skipped++; continue; }
            var key = NormalizePath(p);
            if (known.Contains(key)) continue;
            Assets.Add(new ReverseMediaItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Path = p,
                IsVideo = isVideo,
                Status = _local["ReverseStatusWaiting"],
            });
            known.Add(key);
            added++;
            // 图片异步加载缩略图；视频播放/封面由卡片 MediaVideoView 组件自管理（VideoPath 绑定 Path 自动加载）
            var item = Assets[^1];
            if (!isVideo)
            {
                _ = LoadThumbnailAsync(item);
            }
        }
        if (added == 0)
        {
            ShowToast(_local["ReverseAssetsEmpty"] ?? "", "");
            return;
        }
        if (SelectedAsset == null)
            SelectedAsset = Assets.FirstOrDefault();
        OnPropertyChanged(nameof(DoneCount));
        OnPropertyChanged(nameof(AssetsCount));
        OnPropertyChanged(nameof(AssetsTitle));
        OnPropertyChanged(nameof(BatchSaveLabel));
        var kind = MediaTarget == "video" ? _local["ReverseMediaKindVideo"] : MediaTarget == "image" ? _local["ReverseMediaKindImage"] : _local["ReverseMediaKindAsset"];
        ShowToast(string.Format(_local["ReverseMediaAddedFormat"], added, kind), skipped > 0 ? string.Format(_local["ReverseMediaSkippedFormat"], skipped) : "");
    }

    [RelayCommand]
    private async Task RemoveAssetAsync(ReverseMediaItem item)
    {
        if (IsBusy || item == null) return;
        if (!await ConfirmDeleteAsync(string.Format(_local["ReverseDeleteConfirm"] ?? "", item.Name))) return;
        Assets.Remove(item);
        if (SelectedAsset == item)
            SelectedAsset = Assets.FirstOrDefault();
        OnPropertyChanged(nameof(DoneCount));
        OnPropertyChanged(nameof(AssetsCount));
        OnPropertyChanged(nameof(AssetsTitle));
        OnPropertyChanged(nameof(BatchSaveLabel));
    }

    // ---- LibVLC 视频播放：由卡片 MediaVideoView 组件自管理（复用 Test 已验证范式） ----

    /// <summary>删除确认框（对齐 PromptLibrary ConfirmDeleteAsync：SukiMessageBox OKCancel）。</summary>
    private async Task<bool> ConfirmDeleteAsync(string message)
    {
        var confirm = await SukiUI.MessageBox.SukiMessageBox.ShowDialog(
            new SukiUI.Controls.SukiMessageBoxHost
            {
                Content = new TextBlock
                {
                    Text = message,
                    Margin = new Thickness(4),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                ActionButtonsPreset = SukiUI.MessageBox.SukiMessageBoxButtons.OKCancel,
            },
            new SukiUI.MessageBox.SukiMessageBoxOptions { Title = _local["ReverseDeleteConfirmTitle"], MinWidth = 340 });
        return confirm is SukiUI.MessageBox.SukiMessageBoxResult r && r.Equals(SukiUI.MessageBox.SukiMessageBoxResult.OK);
    }

    /// <summary>图片缩略图异步加载（DecodeToWidth 320 省内存；失败回退图标）。</summary>
    private static async Task LoadThumbnailAsync(ReverseMediaItem item)
    {
        try
        {
            var bmp = await Task.Run(() =>
            {
                try
                {
                    using var fs = File.OpenRead(item.Path);
                    return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(fs, 320);
                }
                catch
                {
                    return null;
                }
            });
            if (bmp != null)
                item.Thumbnail = bmp;
        }
        catch
        {
            // 缩略图失败不影响素材使用
        }
    }

    private void FilterAssetsByMediaTarget(string target)
    {
        var toRemove = Assets.Where(a =>
            target == "image" ? a.IsVideo
            : target == "video" ? !a.IsVideo
            : false).ToList();
        foreach (var item in toRemove)
            Assets.Remove(item);
        if (SelectedAsset != null && !Assets.Contains(SelectedAsset))
            SelectedAsset = Assets.FirstOrDefault();
    }

    [RelayCommand]
    private void ClearAssets()
    {
        if (IsBusy) return;
        Assets.Clear();
        SelectedAsset = null;
        OnPropertyChanged(nameof(DoneCount));
        OnPropertyChanged(nameof(AssetsCount));
        OnPropertyChanged(nameof(AssetsTitle));
        OnPropertyChanged(nameof(BatchSaveLabel));
    }

    [RelayCommand]
    private async Task PickMediaAsync()
    {
        var window = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window == null) return;
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _local["SelectImage"] ?? "",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType(_local["ReverseMediaFileType"])
            {
                Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.bmp", "*.mp4", "*.webm", "*.mov", "*.mkv", "*.avi" },
            } },
        });
        if (files.Count == 0) return;
        AddPaths(files.Select(f => f.TryGetLocalPath() ?? f.Path.AbsolutePath));
    }

    // ==================== 运行 ====================

    [RelayCommand]
    private async Task RunBatchAsync()
    {
        if (IsBusy) return;
        var paths = Assets.Select(a => a.Path).ToList();
        if (paths.Count == 0)
        {
            ShowToast(_local["ReverseNoAssetError"] ?? "", "");
            return;
        }
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var providerName = SelectedProvider?.Name ?? _local["ReverseNoProvider"];
        var modelName = SelectedModel?.ModelName ?? _local["ReverseNoModel"];
        PromptCraft.Service.LogService.Instance.Info(
            string.Format(_local["ReverseBatchStartLog"], providerName, modelName, paths.Count, MediaTarget, SelectedPe?.Label ?? "-"),
            "ReverseCaption");
        try
        {
            var svc = _services.GetRequiredService<IReverseCaptionService>();
            var reqs = paths.Select(p => BuildRequest(p)).ToList();
            var progress = new Progress<ReverseProgressEvent>(e =>
            {
                var item = Assets.FirstOrDefault(a => string.Equals(NormalizePath(a.Path), NormalizePath(e.MediaPath ?? ""), StringComparison.OrdinalIgnoreCase));
                if (item == null) return;
                Dispatcher.UIThread.Post(() =>
                {
                    switch (e.Status)
                    {
                        case "processing":
                            item.Status = _local["ReverseStatusProcessing"];
                            item.Progress = 5;
                            break;
                        case "done":
                            item.Status = _local["ReverseStatusDone"];
                            item.Caption = e.Caption ?? "";
                            item.Error = "";
                            item.Progress = 100;
                            // 结果进右栏：当前无选中、或选中项还没有结果时，自动跟随第一个完成的素材
                            if (SelectedAsset == null || string.IsNullOrWhiteSpace(SelectedAsset.Caption))
                            {
                                SelectedAsset = item;
                                OnPropertyChanged(nameof(SelectedAsset));
                            }
                            break;
                        case "error":
                            item.Status = _local["ReverseStatusFailed"];
                            item.Error = e.Error ?? _local["ReverseFailToast"];
                            break;
                    }
                    if (item == SelectedAsset)
                        OnPropertyChanged(nameof(SelectedCaption));
                    OnPropertyChanged(nameof(DoneCount));
                    OnPropertyChanged(nameof(BatchSaveLabel));
                });
            });

            var results = await svc.CaptionBatchAsync(reqs, SelectedProvider, SelectedModel, _cts.Token, progress);
            var ok = results.Count(r => r.Success);
            var fail = results.Count - ok;
            PromptCraft.Service.LogService.Instance.Info(
                fail == 0
                    ? string.Format(_local["ReverseBatchDoneLog"], ok)
                    : string.Format(_local["ReverseBatchDonePartialLog"], ok, fail),
                "ReverseCaption");
            ShowToast(ok > 0 ? string.Format(_local["ReverseBatchDoneToast"], ok) : _local["ReverseFailToast"], fail > 0 ? string.Format(_local["ReverseFailCountToast"], fail) : "");
        }
        catch (OperationCanceledException)
        {
            PromptCraft.Service.LogService.Instance.Info(_local["ReverseBatchStoppedLog"], "ReverseCaption");
            ShowToast(_local["ReverseStoppedToast"], "");
        }
        catch (Exception ex)
        {
            PromptCraft.Service.LogService.Instance.Warn(string.Format(_local["ReverseBatchFailedLog"], ex.Message), "ReverseCaption", ex);
            ShowToast(_local["ReverseFailToast"], ex.Message);
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    /// <summary>反推当前素材（对齐 PM sn()：反推前先选中该项，完成后结果面板展示点击项的结果）。</summary>
    [RelayCommand]
    private async Task ReverseSingleAsync(ReverseMediaItem? item)
    {
        if (IsBusy) return;
        item ??= SelectedAsset;
        if (item == null) return;
        // 对齐 PM sn(e)：qt(e.id) 先选中该项，结果面板跟随显示
        SelectedAsset = item;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var modelName = SelectedModel?.ModelName ?? _local["ReverseNoModel"];
        PromptCraft.Service.LogService.Instance.Info(
            string.Format(_local["ReverseSingleLog"], modelName, MediaTarget, SelectedPe?.Label ?? "-", item.Path),
            "ReverseCaption");
        try
        {
            item.Status = _local["ReverseStatusProcessing"];
            item.Progress = 5;
            var svc = _services.GetRequiredService<IReverseCaptionService>();
            var text = await svc.CaptionAsync(BuildRequest(item.Path), SelectedProvider, SelectedModel, _cts.Token);
            item.Status = _local["ReverseStatusDone"];
            item.Caption = text;
            item.Progress = 100;
            // 结果进右栏「反推结果」面板：自动选中当前素材（对齐 PM：反推当前素材后结果区展示）
            SelectedAsset = item;
            OnPropertyChanged(nameof(SelectedAsset));
            OnPropertyChanged(nameof(SelectedCaption));
            OnPropertyChanged(nameof(DoneCount));
            OnPropertyChanged(nameof(BatchSaveLabel));
            PromptCraft.Service.LogService.Instance.Info(string.Format(_local["ReverseDoneLog"], item.Path), "ReverseCaption");
            ShowToast(_local["ReverseDoneToast"], "");
        }
        catch (OperationCanceledException)
        {
            item.Status = _local["ReverseStatusWaiting"];
            PromptCraft.Service.LogService.Instance.Info(string.Format(_local["ReverseStoppedLog"], item.Path), "ReverseCaption");
        }
        catch (Exception ex)
        {
            item.Status = _local["ReverseStatusFailed"];
            item.Error = ex.Message;
            PromptCraft.Service.LogService.Instance.Warn(string.Format(_local["ReverseFailedLog"], item.Path, ex.Message), "ReverseCaption", ex);
            ShowToast(_local["ReverseFailToast"], ex.Message);
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>组装单条请求（对齐前端 tn()：全部参数映射到 ReverseCaptionRequest，PE 由服务端 applyReverseToCaption 注入）。</summary>
    private ReverseCaptionRequest BuildRequest(string path)
    {
        var req = new ReverseCaptionRequest
        {
            MediaPath = path,
            MediaTarget = MediaTarget,
            CaptionModel = SelectedModel?.ModelName,
            CaptionLang = CaptionLang,
            PeId = SelectedPe?.Id,
            Type = SelectedPe?.BuiltinKey ?? "Stable_Diffusion_Prompt",
            Len = SelectedLength?.Key ?? "medium",
            CaptionLenChars = IsCustomLen ? (int)CustomLenChars : null,
            ExtraPrompt = ExtraPrompt,
            QualityPromptEnabled = IsTagLinePe && QualityPromptEnabled,
            QualityPromptPrefix = QualityPromptPrefix?.Trim() ?? "",
            ToriiUseNames = ToriiUseNames,
            ToriiAddTags = ToriiAddTags,
            ToriiGroundingTags = ToriiGroundingTags,
            ToriiGroundingCharacters = ToriiGroundingCharacters,
        };
        return req;
    }

    // ==================== 复制 / 保存到词库 ====================

    [RelayCommand]
    private void CopySelected()
    {
        var text = SelectedAsset?.Caption;
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowToast(_local["ReverseCopyEmpty"], "");
            return;
        }
        try
        {
            var clip = _services.GetRequiredService<IBaseClipboardService>();
            clip.CopyToClipboard(text);
            ShowToast(_local["ReverseCopied"], "");
        }
        catch (Exception ex)
        {
            ShowToast(_local["ReverseCopyFailed"], ex.Message);
        }
    }

    /// <summary>单条保存到词库（对齐 PromptMaster SavePromptDialog：标题=文件名、note=反推自）。</summary>
    [RelayCommand]
    private async Task SaveSelectedToLibraryAsync()
    {
        var item = SelectedAsset;
        if (item == null || string.IsNullOrWhiteSpace(item.Caption))
        {
            ShowToast(_local["ReverseNothingToSave"], "");
            return;
        }
        try
        {
            var local = _services.GetRequiredService<Ke.Bee.Localization.Localizer.Abstractions.ILocalizer>();
            var notice = _services.GetRequiredService<IBaseNotice>();
            var log = _services.GetRequiredService<IBaseLogService>();
            var name = Path.GetFileNameWithoutExtension(item.Path);
            if (string.IsNullOrWhiteSpace(name)) name = _local["ReverseDefaultPromptName"];
            await PromptCraft.ViewModels.PromptLibrary.PromptEditDialogOpener.OpenEditAsync(
                _services,
                existing: null,
                dialogTitle: _local["ReverseSaveToLibraryTitle"],
                local,
                notice,
                log,
                beforeOpen: vm =>
                {
                    vm.Title = name;
                    vm.Positive = item.Caption ?? "";
                    vm.Negative = "";
                    vm.Note = string.Format(_local["ReverseFromFormat"], Path.GetFileName(item.Path));
                });
        }
        catch (Exception ex)
        {
            ShowToast(_local["PeSaveFailed"], ex.Message);
        }
    }

    /// <summary>批量保存到词库（对齐 PromptMaster：弹窗选文件夹/标签/封面 → 标题=文件名、note=反推自）。</summary>
    [RelayCommand]
    private async Task SaveBatchToLibraryAsync()
    {
        var items = Assets.Where(a => !string.IsNullOrWhiteSpace(a.Caption)).ToList();
        if (items.Count == 0)
        {
            ShowToast(_local["ReverseNoResults"], "");
            return;
        }
        try
        {
            // 宿主弹窗（同 PromptEditDialogOpener 范式）
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
                || desktop.MainWindow is not { } owner
                || !_services.GetRequiredService<IBaseViewService>().TryCreateView(
                    new ReverseBatchSaveModel(_services, items.Count), out var view, "ReverseBatchSave"))
            {
                // 无宿主/视图未注册：兜底按未分类保存
                await SaveBatchCoreAsync(items, new List<int>(), new List<string>(), "");
                return;
            }

            var batchVm = view.DataContext as ReverseBatchSaveModel;
            var host = new SukiMessageBoxHost
            {
                Content = view,
                IconPreset = null,
                Width = 580,
                ActionButtonsPreset = SukiMessageBoxButtons.Close,
                // 宿主 foot：[取消] [保存]（body 内取消/保存已移除）
                ActionButtonsSource = new Avalonia.Collections.AvaloniaList<Avalonia.Controls.Button>
                {
                    CreateBatchSaveCancelButton(),
                    CreateBatchSaveConfirmButton(batchVm),
                },
            };
            var options = new SukiMessageBoxOptions
            {
                Title = _local["ReverseBatchSaveTitle"],
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            if (owner is SukiWindow sukiOwner)
            {
                options = options with
                {
                    BackgroundAnimationEnabled = sukiOwner.BackgroundAnimationEnabled,
                    BackgroundForceSoftwareRendering = sukiOwner.BackgroundForceSoftwareRendering,
                    BackgroundShaderCode = sukiOwner.BackgroundShaderCode,
                    BackgroundShaderFile = sukiOwner.BackgroundShaderFile,
                    BackgroundStyle = sukiOwner.BackgroundStyle,
                    BackgroundTransitionTime = sukiOwner.BackgroundTransitionTime,
                    BackgroundTransitionsEnabled = sukiOwner.BackgroundTransitionsEnabled,
                };
            }
            var window = SukiMessageBox.CreateMessageBoxWindow(options);
            if (batchVm != null)
            {
                batchVm.Confirmed += async (folderIds, tagIds, cover) =>
                {
                    await SaveBatchCoreAsync(items, folderIds, tagIds, cover);
                    window.Close();
                };
                batchVm.Cancelled += () => window.Close();
            }
            window.Content = host;
            // 宿主 foot：[取消]（Esc） [保存]（执行 ConfirmCommand → Confirmed → 保存并关窗）
            if (host.ActionButtonsSource is { } buttons)
            {
                buttons[0].Click += (_, _) => batchVm?.CancelCommand.Execute(null);
                buttons[1].Click += (_, _) => batchVm?.ConfirmCommand.Execute(null);
                buttons[0].IsCancel = true;
            }
            window.KeyUp += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Escape) window.Close();
            };
            await window.ShowDialog(owner);
        }
        catch (Exception ex)
        {
            ShowToast(_local["ReverseBatchSaveFailed"], ex.Message);
        }
    }

    /// <summary>宿主 foot"取消"按钮（Esc/取消语义；点击触发 CancelCommand → Cancelled → 关窗）。</summary>
    private static Avalonia.Controls.Button CreateBatchSaveCancelButton()
    {
        var button = BaseClassLib.Extends.SukiMessageBoxButtonsFactoryExtend.CreateButton(
            SukiUI.MessageBox.SukiMessageBoxResult.Cancel, Localizer.Instance?["PeCancelButton"] ?? "");
        button.IsCancel = true;
        return button;
    }

    /// <summary>宿主 foot"保存"按钮（点击触发 ConfirmCommand → Confirmed → 保存并关窗）。</summary>
    private static Avalonia.Controls.Button CreateBatchSaveConfirmButton(ReverseBatchSaveModel? vm)
    {
        var button = BaseClassLib.Extends.SukiMessageBoxButtonsFactoryExtend.CreateButton(
            SukiUI.MessageBox.SukiMessageBoxResult.OK, Localizer.Instance?["PeSaveButton"] ?? "");
        if (vm != null) button.Click += (_, _) => vm.ConfirmCommand.Execute(null);
        return button;
    }

    /// <summary>按所选文件夹/标签/封面逐条保存并反馈（对齐 PromptMaster：成功 o 条 / 成功 o 失败 s）。</summary>
    private async Task SaveBatchCoreAsync(List<ReverseMediaItem> items, List<int> folderIds, List<string> tagIds, string cover)
    {
        var library = _services.GetRequiredService<IPromptLibraryService>();
        var ok = 0;
        var fail = 0;
        foreach (var item in items)
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(item.Path);
                if (string.IsNullOrWhiteSpace(name)) name = _local["ReverseDefaultPromptName"];
                var promptId = Guid.NewGuid().ToString("N");
                await library.SavePromptAsync(new Prompt
                {
                    Id = promptId,
                    Title = name,
                    Positive = item.Caption ?? "",
                    Negative = "",
                    FolderIds = folderIds,
                    Cover = cover,
                    Note = string.Format(_local["ReverseFromFormat"], Path.GetFileName(item.Path)),
                    PromptTags = tagIds.Select(tagId => new PromptTagMap { PromptId = promptId, TagId = tagId }).ToList(),
                });
                ok++;
            }
            catch
            {
                fail++;
            }
        }
        ShowToast(
            fail == 0 ? string.Format(_local["ReverseBatchSavedOk"], ok) : string.Format(_local["ReverseBatchSavedPartial"], ok, fail),
            "");
    }

    // ==================== 工具 ====================

    private static string NormalizePath(string p) => (p ?? "").Replace('\\', '/').ToLowerInvariant();

    private static bool IsImagePath(string p)
    {
        var ext = Path.GetExtension(p)?.TrimStart('.').ToLowerInvariant() ?? "";
        return ext is "png" or "jpg" or "jpeg" or "webp" or "gif" or "bmp";
    }

    private static bool IsVideoPath(string p)
    {
        var ext = Path.GetExtension(p)?.TrimStart('.').ToLowerInvariant() ?? "";
        return ext is "mp4" or "webm" or "mov" or "mkv" or "avi";
    }

    private static int ResolveLenMaxTokens(string? key)
    {
        var preset = PromptCraft.Service.Inference.ExpandRules.Lengths.FirstOrDefault(l => l.Key == key);
        return preset?.MaxTokens ?? 512;
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

    public override void OnSystemLangueChanged(object? data)
    {
        // 语言切换：重建本地化选项列表 + 重映射素材状态显示串 + 重建 PE 下拉（保持当前选择）
        MediaTargetItems = BuildMediaTargetItems();
        OutputFormatItems = BuildOutputFormatItems();
        foreach (var item in Assets)
            item.Status = RelocalizeStatus(item.Status);
        var currentPeId = SelectedPe?.Id;
        if (_sourceProfiles != null)
        {
            PeProfiles.Clear();
            foreach (var p in _sourceProfiles)
                PeProfiles.Add(PeProfileOption.From(p));
        }
        var filtered = PeProfiles.Where(p => p.OutputFormat == PeOutputFormat).ToList();
        VisiblePeProfiles.Clear();
        foreach (var item in filtered)
            VisiblePeProfiles.Add(item);
        SelectedPe = currentPeId != null
            ? filtered.FirstOrDefault(x => x.Id == currentPeId) ?? filtered.FirstOrDefault()
            : filtered.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedMediaTarget));
        OnPropertyChanged(nameof(SelectedOutputFormat));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(AssetsTitle));
        OnPropertyChanged(nameof(SelectedCaptionLengthText));
        OnPropertyChanged(nameof(BatchSaveLabel));
        OnPropertyChanged(nameof(LengthLabel));
        base.OnSystemLangueChanged(data);
    }

    /// <summary>素材状态显示串重映射（zh/en 旧语言值 → 当前语言值；ReverseConv 守卫依赖该值匹配）。</summary>
    private static string RelocalizeStatus(string old) => old switch
    {
        "处理中" or "Processing" => Localizer.Instance?["ReverseStatusProcessing"] ?? old,
        "完成" or "Done" => Localizer.Instance?["ReverseStatusDone"] ?? old,
        "失败" or "Failed" => Localizer.Instance?["ReverseStatusFailed"] ?? old,
        "等待" or "Waiting" => Localizer.Instance?["ReverseStatusWaiting"] ?? old,
        _ => old,
    };
}

/// <summary>XAML 转换器（反推页专用）。</summary>
public static class ReverseConv
{
    /// <summary>状态是否处理中（显示进度条）。</summary>
    public static readonly IValueConverter IsProcessing =
        new FuncValueConverter<string?, bool>(s => s == Localizer.Instance?["ReverseStatusProcessing"]);

    /// <summary>字符串非空（显示错误 / 启用按钮）。</summary>
    public static readonly IValueConverter IsNotEmpty =
        new FuncValueConverter<string?, bool>(s => !string.IsNullOrWhiteSpace(s));

    /// <summary>状态 tag 背景色：等待=灰、处理中=蓝、完成=绿、失败=红。</summary>
    public static readonly IValueConverter StatusBrush =
        new FuncValueConverter<string?, IBrush>(s => s switch
        {
            var v when v == Localizer.Instance?["ReverseStatusProcessing"] => new SolidColorBrush(Color.Parse("#1A0078D4")),
            var v when v == Localizer.Instance?["ReverseStatusDone"] => new SolidColorBrush(Color.Parse("#1A4CAF50")),
            var v when v == Localizer.Instance?["ReverseStatusFailed"] => new SolidColorBrush(Color.Parse("#1AE81123")),
            _ => new SolidColorBrush(Color.Parse("#1A808080")),
        });

    /// <summary>状态 tag 前景色：处理中=蓝、完成=绿、失败=红、等待=灰。</summary>
    public static readonly IValueConverter StatusForeground =
        new FuncValueConverter<string?, IBrush>(s => s switch
        {
            var v when v == Localizer.Instance?["ReverseStatusProcessing"] => new SolidColorBrush(Color.Parse("#0078D4")),
            var v when v == Localizer.Instance?["ReverseStatusDone"] => new SolidColorBrush(Color.Parse("#4CAF50")),
            var v when v == Localizer.Instance?["ReverseStatusFailed"] => new SolidColorBrush(Color.Parse("#E81123")),
            _ => new SolidColorBrush(Color.Parse("#808080")),
        });

    /// <summary>视频播放中 → Pause，否则 PlayArrow（播放/暂停图标）。</summary>
    public static readonly IValueConverter PlayingToIcon =
        new FuncValueConverter<bool, MaterialIconKind>(isPlaying => isPlaying ? MaterialIconKind.Pause : MaterialIconKind.PlayArrow);

    /// <summary>素材类型 → 图标（视频 = Videocam，图片 = Image）。</summary>
    public static readonly IValueConverter VideoToIcon =
        new FuncValueConverter<bool, MaterialIconKind>(isVideo => isVideo ? MaterialIconKind.Videocam : MaterialIconKind.Image);
}

/// <summary>反推素材条目（对齐前端 media tile/card 状态机：等待/处理中/完成/失败）。</summary>
public partial class ReverseMediaItem : ObservableObject
{
    public string Id { get; init; } = "";
    public string Path { get; init; } = "";
    public bool IsVideo { get; init; }
    public string Name => global::System.IO.Path.GetFileName(Path);

    /// <summary>缩略图（图片 DecodeToWidth 320；视频=null，封面由卡片 MediaVideoView 组件抓帧显示）。</summary>
    [ObservableProperty]
    private Avalonia.Media.Imaging.Bitmap? _thumbnail;

    partial void OnThumbnailChanged(Avalonia.Media.Imaging.Bitmap? value)
    {
        OnPropertyChanged(nameof(HasThumbnail));
    }

    public bool HasThumbnail => Thumbnail != null;

    /// <summary>视频是否播放中（控制 overlay 图标；由卡片 MediaVideoView 组件 TwoWay 同步）。</summary>
    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _caption = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private double _progress;
}

/// <summary>PE 下拉选项（label=内置原名 / 「自定义 / 名称」）。</summary>
public sealed record PeProfileOption(
    string Id,
    string Label,
    string Description,
    string OutputFormat,
    string? BuiltinKey,
    bool IsTorii,
    bool UseNamesDefault)
{
    public static PeProfileOption From(PromptEngineeringProfile p)
    {
        var label = p.Builtin ? (Localizer.Instance?[$"PeName_{p.Id}"] ?? p.Name) : string.Format(Localizer.Instance?["CustomPeLabelFormat"] ?? "", p.Name);
        var isTorii = ToriiGateFormats.IsStructuredTemplateProfile(p);
        return new PeProfileOption(
            p.Id,
            label,
            p.Builtin ? (Localizer.Instance?[$"PeDescription_{p.Id}"] ?? p.Description) : p.Description ?? "",
            string.IsNullOrWhiteSpace(p.OutputFormat) ? "prose" : p.OutputFormat,
            string.IsNullOrWhiteSpace(p.BuiltinKey) ? null : p.BuiltinKey,
            isTorii,
            p.ToriiUseNamesDefault ?? true);
    }
}
