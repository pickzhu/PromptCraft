using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.BaseModel;
using PromptCraft.Data;
using PromptCraft.Interfaces;
using PromptCraft.Models.ComfyUI;
using PromptCraft.Service;
using Ke.Bee.Localization.Localizer;
using Microsoft.EntityFrameworkCore;
using SukiUI.Controls;
using SukiUI.MessageBox;
using SukiUI.Toasts;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.ComfyUI;

/// <summary>
/// 图片详情预览 ViewModel，用于在弹窗中显示大图和元数据
/// </summary>
public partial class ImageDetailModel : ViewModelBase
{
    private readonly ImageItem _source;
    private readonly ComfySettings _settings;
    private readonly IDbContextFactory<ComfyDbContext> _dbFactory;
    private readonly IBaseLogService _log;
    private readonly IServiceProvider _service;
    private ImagePrompt? _prompt;

    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private Bitmap? _fullImage;
    [ObservableProperty] private bool _isImageLoading;
    [ObservableProperty] private int _width;
    [ObservableProperty] private int _height;
    [ObservableProperty] private long _fileSize;
    [ObservableProperty] private string _hash = "";
    [ObservableProperty] private string _createdAt = "";
    [ObservableProperty] private string _fullPath = "";
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private bool _isNsfw;

    // ---- 视频条目预览（详情大图区：视频 → MediaVideoView 播放，封面用同步生成的缩略图）----
    /// <summary>是否为视频条目（决定大图区显示播放器而非图片）。</summary>
    [ObservableProperty] private bool _isVideoItem;
    /// <summary>视频封面图路径（同步生成的缩略图 jpg；MediaVideoView.CoverPath，避免重复抓帧）。</summary>
    [ObservableProperty] private string? _coverImagePath;
    /// <summary>播放状态（TwoWay 绑定 MediaVideoView.IsPlaying，驱动中央播放按钮/控制条显隐）。</summary>
    [ObservableProperty] private bool _isPlaying;

    /// <summary>中央播放按钮可见：视频条目且未播放（含暂停）。</summary>
    public bool ShowCenterPlay => IsVideoItem && !IsPlaying;

    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(ShowCenterPlay));
    partial void OnIsVideoItemChanged(bool value) => OnPropertyChanged(nameof(ShowCenterPlay));

    // 图标切换走计算属性（Style Setter 无法覆盖 TextBlock 的本地 Text 值，必须由数据驱动）
    partial void OnIsFavoriteChanged(bool value)
    {
        OnPropertyChanged(nameof(FavoriteIcon));
        OnPropertyChanged(nameof(FavoriteIconBrush));
    }

    partial void OnIsNsfwChanged(bool value)
    {
        OnPropertyChanged(nameof(NsfwIcon));
        OnPropertyChanged(nameof(NsfwIconBrush));
    }

    /// <summary>收藏图标：未收藏 ♡ 空心 → 已收藏 ♥ 实心</summary>
    public string FavoriteIcon => IsFavorite ? "♥" : "♡";

    /// <summary>收藏颜色：未收藏灰 → 已收藏红</summary>
    public IBrush FavoriteIconBrush => new SolidColorBrush(Color.Parse(IsFavorite ? "#E81123" : "#909399"));

    /// <summary>NSFW 图标：未开启 ⭕ 红圈无禁止线 → 开启 🔞</summary>
    public string NsfwIcon => IsNsfw ? "🔞" : "⭕";

    /// <summary>NSFW 颜色：红色（未开启/开启均为红色系，图标本身区分状态）</summary>
    public IBrush NsfwIconBrush => new SolidColorBrush(Color.Parse("#E81123"));

    // ---- ComfyUI 元数据（拆表存储，仅详情页需要时从工作流/提示词表按需解压）----
    [ObservableProperty] private bool _hasMetadata;
    [ObservableProperty] private bool _isWorkflowArchived;
    [ObservableProperty] private string _workflowTitle = "";
    [ObservableProperty] private string _positivePrompt = "";
    [ObservableProperty] private string _negativePrompt = "";
    [ObservableProperty] private string _extractedSummary = "";
    [ObservableProperty] private string _promptJson = "";
    [ObservableProperty] private string _workflowJson = "";

    [ObservableProperty] private ObservableCollection<TagItem> _availableTags = new();
    [ObservableProperty] private bool _isTagDropdownOpen;

    /// <summary>已选标签摘要文本：无选中显示 All Tags，多个显示"第一个 +数量"</summary>
    public string SelectedTagsSummary
    {
        get
        {
            var selected = AvailableTags.Where(t => t.IsSelected).Select(t => t.Name).ToList();
            return selected.Count == 0 ? _local["AllTags"]
                 : selected.Count == 1 ? selected[0]
                 : $"{selected[0]} +{selected.Count - 1}";
        }
    }

    public string FileSizeDisplay => FileSize switch
    {
        < 1024 => $"{FileSize} B",
        < 1024 * 1024 => $"{FileSize / 1024} KB",
        _ => $"{FileSize / (1024.0 * 1024):F1} MB"
    };

    public string DimensionsDisplay => $"{Width} × {Height}";

    public ImageDetailModel(
        ImageItem source,
        ComfySettings settings,
        IDbContextFactory<ComfyDbContext> dbFactory,
        IServiceProvider service,
        Ke.Bee.Localization.Localizer.Abstractions.ILocalizer localizer,
        PromptCraft.Interfaces.IBaseNotice notice,
        PromptCraft.Interfaces.IBaseLogService log)
        : base(localizer, notice)
    {
        _source = source;
        _settings = settings;
        _dbFactory = dbFactory;
        _service = service;
        _log = log;

        FileName = source.FileName;
        Width = source.Width;
        Height = source.Height;
        FileSize = source.FileSize;
        Hash = source.Hash ?? "";
        CreatedAt = source.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
        FullPath = source.FullPath;
        IsFavorite = source.IsFavorite;
        IsNsfw = source.IsNsfw;

        // 视频条目：大图区显示播放器（封面用同步缩略图，避免 Skia 直接解码视频）
        IsVideoItem = PromptCraft.Service.ComfyMediaKinds.IsVideo(FullPath);
        if (IsVideoItem && !string.IsNullOrEmpty(source.RelativePath))
        {
            var thumb = Path.Combine(_settings.ThumbDir,
                PromptCraft.Service.ImageSyncService.GetThumbFileName(source.RelativePath, _settings.ThumbMaxDimension));
            CoverImagePath = File.Exists(thumb) ? thumb : null;
        }

        _ = LoadFullImageAsync();
        _ = LoadTagsAsync();
        _ = LoadMetadataAsync();
    }

    /// <summary>
    /// 从工作流/提示词表读取图片的 ComfyUI 元数据并解压。
    /// 这是"压缩存储、按需解压"的统一读取入口（<c>GetWorkflowJson / GetPromptJson</c>），后续其他场景复用同一方法。
    /// </summary>
    private async Task LoadMetadataAsync()
    {
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            var img = await db.ImageMetadata
                .Include(x => x.Workflow)
                .Include(x => x.Prompt)
                .FirstOrDefaultAsync(x => x.Id == _source.ImageInfoId);
            if (img?.Workflow == null && img?.Prompt == null)
            {
                HasMetadata = false;
                return;
            }

            HasMetadata = true;
            IsWorkflowArchived = img.Workflow?.IsDeleted == true;
            WorkflowTitle = img.Workflow?.Name ?? "";
            _prompt = img.Prompt;
            PositivePrompt = img.Prompt?.PositivePrompt ?? "";
            NegativePrompt = img.Prompt?.NegativePrompt ?? "";
            PromptJson = FormatJsonText(img.Prompt?.GetPromptJson());
            // 工作流入库时只存结构（参数已剔除），这里用该图的节点参数组装回完整工作流展示
            WorkflowJson = FormatJsonText(ComfyWorkflowNormalizer.Rehydrate(
                img.Workflow?.GetWorkflowJson(),
                ParseNodeSnapshots(img.Prompt?.GetNodesJson())));
            ExtractedSummary = BuildSummary(img.Prompt);
        }
        catch (Exception ex)
        {
            _log.Debug(string.Format(_local["LoadImageMetadataFailed"], FileName), "ImageDetail", ex);
        }
    }

    /// <summary>格式化 JSON 便于查看；null/空串返回空串，非法 JSON 原样返回</summary>
    private static string FormatJsonText(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }

    /// <summary>把提示词表的节点参数 JSON 反序列化为 NodeSnapshot 列表（组装工作流用）</summary>
    private static List<NodeSnapshot>? ParseNodeSnapshots(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<List<NodeSnapshot>>(json); }
        catch { return null; }
    }

    /// <summary>把提示词表里的常用参数拼成一行摘要（模型 / seed / steps / cfg / 采样器 / LoRA）</summary>
    private static string BuildSummary(ImagePrompt? p)
    {
        if (p == null) return "";
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(p.Model)) parts.Add(string.Format(Localizer.Instance?["ModelSummary"] ?? "", p.Model));
        if (!string.IsNullOrEmpty(p.Seed)) parts.Add(string.Format(Localizer.Instance?["SeedSummary"] ?? "", p.Seed));
        if (!string.IsNullOrEmpty(p.Steps)) parts.Add(string.Format(Localizer.Instance?["StepsSummary"] ?? "", p.Steps));
        if (!string.IsNullOrEmpty(p.Cfg)) parts.Add(string.Format(Localizer.Instance?["CfgSummary"] ?? "", p.Cfg));
        if (!string.IsNullOrEmpty(p.Sampler)) parts.Add(string.Format(Localizer.Instance?["SamplerSummary"] ?? "", p.Sampler));
        if (!string.IsNullOrEmpty(p.Scheduler)) parts.Add(string.Format(Localizer.Instance?["SchedulerSummary"] ?? "", p.Scheduler));
        if (p.Width is > 0) parts.Add(string.Format(Localizer.Instance?["DimensionsSummary"] ?? "", p.Width, p.Height));
        if (!string.IsNullOrEmpty(p.LoraNames)) parts.Add(string.Format(Localizer.Instance?["LoraSummary"] ?? "", p.LoraNames));
        return string.Join(" | ", parts);
    }

    private async Task LoadFullImageAsync()
    {
        if (string.IsNullOrEmpty(FullPath) || !File.Exists(FullPath))
            return;

        // 视频条目：不解码为位图（大图区由 MediaVideoView 播放器接管，封面走 CoverImagePath）
        if (IsVideoItem)
            return;

        IsImageLoading = true;
        try
        {
            var bitmap = await Task.Run(() =>
            {
                try { return new Bitmap(FullPath); }
                catch (Exception ex)
                {
                    _log.Debug(string.Format(_local["ImageDecodeFailed"], FullPath), "ImageDetail", ex);
                    return null;
                }
            });

            if (bitmap != null)
            {
                FullImage = bitmap;
                Width = bitmap.PixelSize.Width;
                Height = bitmap.PixelSize.Height;
            }
        }
        finally
        {
            IsImageLoading = false;
        }
    }

    private async Task LoadTagsAsync()
    {
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();

            // 获取该图片已分配的标签 ID
            var assignedTagIds = await db.ImageTags
                .Where(it => it.ImageInfoId == _source.ImageInfoId)
                .Select(it => it.TagId)
                .ToListAsync();

            // 获取所有标签，标记已分配的
            var tags = await db.Tags.OrderBy(t => t.Name).ToListAsync();
            var items = tags.Select(t => new TagItem(t)
            {
                IsSelected = assignedTagIds.Contains(t.Id)
            }).ToList();

            foreach (var item in items)
                item.PropertyChanged += OnTagToggled;

            AvailableTags = new ObservableCollection<TagItem>(items);
            OnPropertyChanged(nameof(SelectedTagsSummary));
        }
        catch (Exception ex)
        {
            _log.Error(string.Format(_local["LoadImageTagsFailed"], FileName), "ImageDetail", ex);
        }
    }

    private async void OnTagToggled(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TagItem.IsSelected)) return;
        if (sender is not TagItem tag) return;
        OnPropertyChanged(nameof(SelectedTagsSummary));

        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();

            if (tag.IsSelected)
            {
                // 添加标签关联
                var existing = await db.ImageTags
                    .FirstOrDefaultAsync(it => it.ImageInfoId == _source.ImageInfoId && it.TagId == tag.Id);
                if (existing == null)
                {
                    db.ImageTags.Add(new ImageTag
                    {
                        ImageInfoId = _source.ImageInfoId,
                        TagId = tag.Id
                    });
                }
            }
            else
            {
                // 移除标签关联
                var link = await db.ImageTags
                    .FirstOrDefaultAsync(it => it.ImageInfoId == _source.ImageInfoId && it.TagId == tag.Id);
                if (link != null)
                    db.ImageTags.Remove(link);
            }

            await db.SaveChangesAsync();
            // 通知图库列表局部刷新该图片的标签（保存已成功，实时生效）
            DataChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error(string.Format(_local["UpdateImageTagsFailed"], FileName), "ImageDetail", ex);
        }
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        IsFavorite = !IsFavorite;
        _source.IsFavorite = IsFavorite;
        await SaveStatusAsync();
    }

    [RelayCommand]
    private async Task ToggleNsfwAsync()
    {
        IsNsfw = !IsNsfw;
        _source.IsNsfw = IsNsfw;
        await SaveStatusAsync();
    }

    private async Task SaveStatusAsync()
    {
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            var status = await db.ImageStatuses.FirstOrDefaultAsync(x => x.ImageInfoId == _source.ImageInfoId);
            if (status != null)
            {
                status.IsFavorite = IsFavorite;
                status.IsNsfw = IsNsfw;
                status.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _log.Error(string.Format(_local["SaveImageStatusFailed"], FileName), "ImageDetail", ex);
        }
    }

    /// <summary>删除成功后触发（由宿主窗口订阅：关闭弹窗并刷新图库列表）</summary>
    public event Action? Deleted;

    /// <summary>数据变更后触发（如标签修改；宿主订阅后局部刷新图库列表对应项，无需重开详情）</summary>
    public event Action? DataChanged;

    // ---- 存为提示词（图库 → 词库的显式保存入口；不自动入库） ----

    /// <summary>是否可保存到词库：有元数据且正提示词非空。</summary>
    public bool CanSaveToLibrary => HasMetadata && !string.IsNullOrWhiteSpace(PositivePrompt);

    partial void OnHasMetadataChanged(bool value) => OnPropertyChanged(nameof(CanSaveToLibrary));

    partial void OnPositivePromptChanged(string value) => OnPropertyChanged(nameof(CanSaveToLibrary));

    /// <summary>
    /// 「存为提示词」：打开词库编辑对话框，预填本图提取的正/负提示词与参数，
    /// 用户确认后写入词库（显式路径，图库提取永不自动入库）。
    /// </summary>
    [RelayCommand]
    private async Task SaveToLibraryAsync()
    {
        if (!CanSaveToLibrary) return;

        var defaultTitle = !string.IsNullOrWhiteSpace(WorkflowTitle)
            ? WorkflowTitle
            : Path.GetFileNameWithoutExtension(FileName);
        var seed = _prompt?.Seed ?? "";
        var models = _prompt?.Model ?? "";

        await PromptCraft.ViewModels.PromptLibrary.PromptEditDialogOpener.OpenEditAsync(
            _service,
            existing: null,
            dialogTitle: _local["PromptSaveFromGallery"],
            _local, _noticeService, _log,
            beforeOpen: vm =>
            {
                vm.Title = defaultTitle;
                vm.Positive = PositivePrompt;
                vm.Negative = NegativePrompt;
                vm.Seed = seed;
                vm.Models = models;
            },
            onSaved: () =>
            {
                try
                {
                    var toastManager = _service.GetService(typeof(ISukiToastManager)) as ISukiToastManager;
                    if (toastManager != null)
                    {
                        var toast = SukiUI.Toasts.FluentSukiToastBuilder.CreateSimpleInfoToast(toastManager);
                        toast.SetTitle(_local["PromptSaved"]);
                        toast.SetContent(defaultTitle);
                        toast.SetCanDismissByClicking(true);
                        toast.Toast.DismissTimeout = TimeSpan.FromSeconds(3);
                        toast.Queue();
                    }
                }
                catch (Exception ex)
                {
                    _log.Debug(string.Format(_local["ToastFailed"], ex.Message), "ImageDetail");
                }
            });
    }

    /// <summary>删除当前图片：原文件 + 缩略图 + 入库记录 + 无其他图片引用的提示词，弹窗确认后执行</summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        var confirm = await SukiMessageBox.ShowDialog(
            new SukiMessageBoxHost
            {
                Content = new TextBlock
                {
                    Text = string.Format(_local["ConfirmDeleteImage"], FileName),
                    Margin = new Thickness(4),
                    TextWrapping = TextWrapping.Wrap,
                },
                ActionButtonsPreset = SukiMessageBoxButtons.OKCancel,
            },
            new SukiMessageBoxOptions { Title = _local["ConfirmDelete"], MinWidth = 360 });
        if (!(confirm is SukiMessageBoxResult r && r.Equals(SukiMessageBoxResult.OK))) return;

        try
        {
            var sync = new ImageSyncService(_settings, _dbFactory, _log);
            var result = await sync.DeleteImagesAsync(new[] { _source.ImageInfoId });
            if (result.Deleted > 0)
            {
                Deleted?.Invoke();
            }
            else if (result.Failed > 0)
            {
                _log.Warn(string.Format(_local["DeleteImageFailedDetail"], string.Join("; ", result.Errors)), "ImageDetail");
                await SukiMessageBox.ShowDialog(
                    new SukiMessageBoxHost
                    {
                        Content = new TextBlock
                        {
                            Text = string.Format(_local["DeleteFailedHint"], string.Join("\n", result.Errors)),
                            Margin = new Thickness(4),
                            TextWrapping = TextWrapping.Wrap,
                        },
                        ActionButtonsPreset = SukiMessageBoxButtons.OK,
                    },
                    new SukiMessageBoxOptions { Title = _local["DeleteFailedTitle"], MinWidth = 400 });
            }
        }
        catch (Exception ex)
        {
            _log.Error(string.Format(_local["DeleteImageFailed"], FileName), "ImageDetail", ex);
        }
    }
}