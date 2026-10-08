using Avalonia;
using BaseClassLib.Extends;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.BaseModel;
using PromptCraft.Common;
using PromptCraft.Data;
using PromptCraft.Interfaces;
using PromptCraft.Models.ComfyUI;
using PromptCraft.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SukiUI.Controls;
using SukiUI.MessageBox;
using SukiUI.Toasts;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.ComfyUI;

/// <summary>执行历史列表项。</summary>
public partial class WorkflowJobItem : ObservableObject
{
    public int Id { get; init; }

    // 运行中/终态可实时更新（OnJobProgress 按 JobId 就地同步，不依赖列表重建也能反映最新状态）
    [ObservableProperty] private int _progressValue;
    [ObservableProperty] private int _progressMax = 1;
    [ObservableProperty] private bool _isRunning;

    /// <summary>已弹过结果提示的 JobId（终态事件可能触发多次：execution_success + FinalizeJobAsync 收尾），每个 Job 只提示一次。</summary>
    private readonly HashSet<int> _notifiedJobIds = new();
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string? _errorMessage;

    public string TimeText { get; init; } = "";
    public string DurationText { get; init; } = "";
    public string PromptId { get; init; } = "";

    /// <summary>输出记录（打开图片详情/对比用：含文件名/子目录/ImageInfoId）。</summary>
    public List<JobOutput> Outputs { get; set; } = new();

    /// <summary>对比多选状态（选中两条后可对比）。</summary>
    [ObservableProperty] private bool _isSelected;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(StatusBrush));
    public bool HasOutputs => OutputThumbs.Count > 0;

    public IBrush StatusBrush => Status switch
    {
        "completed" => new SolidColorBrush(Color.FromRgb(76, 175, 80)),
        "failed" => new SolidColorBrush(Color.FromRgb(244, 67, 54)),
        "running" => new SolidColorBrush(Color.FromRgb(33, 150, 243)),
        _ => new SolidColorBrush(Color.FromRgb(158, 158, 158)),
    };

    public ObservableCollection<Bitmap> OutputThumbs { get; } = new();
}

/// <summary>
/// 工作流详情弹窗 ViewModel：封面图（回退链）+ JSON 编辑 + 触发执行（内嵌进度）+ 执行历史 + 删除。
/// </summary>
public partial class WorkflowDetailModel : ViewModelBase
{
    private readonly WorkflowItem _source;
    private readonly ComfySettings _settings;
    private readonly IDbContextFactory<ComfyDbContext> _dbFactory;
    private readonly IServiceProvider _service;
    private readonly IBaseLogService _log;

    /// <summary>图片提取工作流时为其 ImageWorkflows.Id，否则 null（WorkflowId 语义为 Workflows.Id）。</summary>
    private readonly int? _imageWorkflowId;

    /// <summary>图片提取工作流执行时物化出的 Workflows.Id（执行历史归属）。</summary>
    private int _materializedWorkflowId;

    /// <summary>是否为图片/视频提取工作流（决定"配置参数"按钮切换为"选用图片参数"）。</summary>
    public bool IsImageExtractedWorkflow => _imageWorkflowId != null;

    [ObservableProperty] private int _workflowId;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _workflowJson = "";
    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] private bool _isPreviewLoading;
    [ObservableProperty] private bool _isArchived;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isRunning;

    // ---- 视频工作流预览（详情页预览区：视频产物 → MediaVideoView 播放，封面用工作流缩略图）----
    /// <summary>是否为视频提取/视频产物工作流（决定预览区显示播放器而非图片）。</summary>
    [ObservableProperty] private bool _isVideoWorkflow;
    /// <summary>视频产物完整路径（MediaVideoView.VideoPath）。</summary>
    [ObservableProperty] private string? _videoPath;
    /// <summary>视频封面图路径（工作流 ThumbnailPath / 产物缩略图；MediaVideoView.CoverPath，避免重复抓帧）。</summary>
    [ObservableProperty] private string? _coverImagePath;
    /// <summary>播放状态（TwoWay 绑定 MediaVideoView.IsPlaying，驱动中央播放按钮/左下角控制条显隐）。</summary>
    [ObservableProperty] private bool _isPlaying;

    /// <summary>中央播放按钮可见：视频工作流且未播放（含暂停）。</summary>
    public bool ShowCenterPlay => IsVideoWorkflow && !IsPlaying;

    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(ShowCenterPlay));
    partial void OnIsVideoWorkflowChanged(bool value) => OnPropertyChanged(nameof(ShowCenterPlay));

    /// <summary>已弹过结果提示的 JobId（终态事件可能触发多次：execution_success + FinalizeJobAsync 收尾），每个 Job 只提示一次。</summary>
    private readonly HashSet<int> _notifiedJobIds = new();
    /// <summary>刷新序号：并发刷新时只允许最后一次（最新序号）生效，防止旧的查询结果（如提交时读到的 queued）后完成并覆盖终态结果。</summary>
    private int _refreshSeq;
    [ObservableProperty] private bool _canRun = true;
    [ObservableProperty] private int _progressValue;
    [ObservableProperty] private int _progressMax = 100;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _nodeLog = "";
    [ObservableProperty] private bool _hasJobs;
    [ObservableProperty] private ObservableCollection<WorkflowJobItem> _jobs = new();

    // ---- 工作流标签（与图库共用 Tag 表，详情页勾选即增删 WorkflowTag 关联）----
    [ObservableProperty] private ObservableCollection<TagItem> _availableTags = new();

    /// <summary>工作流内容为空时底部"删除"按钮才可用（判断依据：详情内加载的工作流 JSON 为空）。</summary>
    public bool CanDelete => string.IsNullOrWhiteSpace(WorkflowJson);

    partial void OnWorkflowJsonChanged(string value) => OnPropertyChanged(nameof(CanDelete));

    /// <summary>已选标签摘要（无选中"全部标签"）</summary>
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

    /// <summary>加载工作流已分配标签（WorkflowId 即 Workflows 表 Id；图片提取时也是提取记录 Id）。</summary>
    public async Task LoadWorkflowTagsAsync()
    {
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            var assignedTagIds = await db.WorkflowTags
                .Where(wt => wt.WorkflowId == WorkflowId)
                .Select(wt => wt.TagId)
                .ToListAsync();
            var tags = await db.Tags.OrderBy(t => t.Name).ToListAsync();
            var items = tags.Select(t => new TagItem(t) { IsSelected = assignedTagIds.Contains(t.Id) }).ToList();
            foreach (var item in items)
                item.PropertyChanged += OnTagToggled;
            AvailableTags = new ObservableCollection<TagItem>(items);
            OnPropertyChanged(nameof(SelectedTagsSummary));
        }
        catch (Exception ex)
        {
            _log.Error(string.Format(_local["LoadWorkflowTagFailed"], Name), "Workflow", ex);
        }
    }

    /// <summary>勾选/取消标签 → 增删 WorkflowTag 关联（实时生效，与图库详情页一致）。</summary>
    private async void OnTagToggled(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TagItem.IsSelected)) return;
        if (sender is not TagItem tag) return;
        OnPropertyChanged(nameof(SelectedTagsSummary));
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            var existing = await db.WorkflowTags
                .FirstOrDefaultAsync(wt => wt.WorkflowId == WorkflowId && wt.TagId == tag.Id);
            if (tag.IsSelected && existing == null)
            {
                db.WorkflowTags.Add(new WorkflowTag { WorkflowId = WorkflowId, TagId = tag.Id });
                await db.SaveChangesAsync();
                _log.Info(string.Format(_local["AddWorkflowTag"], Name, WorkflowId, tag.Name), "Workflow");
                WorkflowChanged?.Invoke();
            }
            else if (!tag.IsSelected && existing != null)
            {
                db.WorkflowTags.Remove(existing);
                await db.SaveChangesAsync();
                _log.Info(string.Format(_local["RemoveWorkflowTag"], Name, WorkflowId, tag.Name), "Workflow");
                WorkflowChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            _log.Error(string.Format(_local["UpdateWorkflowTagFailed"], Name), "Workflow", ex);
        }
    }

    /// <summary>删除成功后请求关闭弹窗（由打开方订阅并关闭窗口）。</summary>
    public event Action? RequestClose;

    /// <summary>详情内工作流数据发生变化（打标签/改封面/删除），由列表页订阅后就地刷新对应卡片。</summary>
    public event Action? WorkflowChanged;

    public WorkflowDetailModel(
        WorkflowItem source,
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

        WorkflowId = source.Id;
        Name = source.Name;
        _imageWorkflowId = source.ImageWorkflowId;
    }

    /// <summary>页面加载：读取工作流（Workflows 表 / 图片提取 ImageWorkflows 表）、加载预览图与执行历史，并订阅执行进度事件。</summary>
    public async Task OnLoadedAsync()
    {
        try
        {
            var service = _service.GetRequiredService<IComfyUIService>();
            service.JobProgressChanged += OnJobProgress;
            _log.Debug(string.Format(_local["OpenWorkflowDetail"], Name, WorkflowId, _imageWorkflowId?.ToString() ?? "-"), "Workflow");

            // 工作流标签（与图库共用 Tag 表，勾选即增删 WorkflowTag 关联）
            await LoadWorkflowTagsAsync();

            using var db = await _dbFactory.CreateDbContextAsync();

            // ---- 图片提取工作流（Workflows 表 Source="image"）----
            if (_imageWorkflowId is int iwfId)
            {
                var iwf = await db.Workflows.AsNoTracking().FirstOrDefaultAsync(x => x.Id == iwfId && (x.Source == "image" || x.Source == "video"));
                if (iwf == null)
                {
                    StatusText = _local["WorkflowNotFound"];
                    IsArchived = true;
                    return;
                }
                WorkflowJson = FormatJson(ComfyMetadataCodec.Decompress(iwf.WorkflowJsonBlob) ?? "");
                IsArchived = iwf.IsDeleted;
                StatusText = IsArchived ? _local["WorkflowArchived"] : "";
                // 名称只读：取自提取标题，不可编辑
                if (!string.IsNullOrEmpty(iwf.Name)) Name = iwf.Name;

                await Task.WhenAll(LoadPreviewAsync(iwf), RefreshHistoryAsync());
                return;
            }

            // ---- Workflows 表工作流（ComfyUI 同步 / 本地新建）----
            var workflow = await db.Workflows.AsNoTracking().FirstOrDefaultAsync(x => x.Id == WorkflowId);
            if (workflow == null)
            {
                StatusText = _local["WorkflowNotFound"];
                IsArchived = true;
                return;
            }
            WorkflowJson = FormatJson(workflow.GetWorkflowJson() ?? "");
            IsArchived = workflow.IsDeleted;
            StatusText = IsArchived ? _local["WorkflowArchived"] : "";

            await Task.WhenAll(LoadPreviewAsync(workflow), RefreshHistoryAsync());
        }
        catch (Exception ex)
        {
            StatusText = string.Format(_local["LoadDetailFailed"], ex.Message);
            _log.Error(string.Format(_local["LoadWorkflowDetailFailed"], Name), "Workflow", ex);
        }
    }

    /// <summary>弹窗关闭时解绑进度事件，避免泄漏。</summary>
    public void Detach()
    {
        var service = _service.GetRequiredService<IComfyUIService>();
        service.JobProgressChanged -= OnJobProgress;
    }

    #region 封面图（回退链）

    /// <summary>
    /// 预览图回退链：
    /// - 图片提取（Source="image"）：手动封面(ThumbnailPath) → 自身关联的第一张图 → 内置默认图。
    /// - 用户工作流：手动设置(ThumbnailPath) → 同款工作流(GUID)生成的第一张图 → 该工作流任务输出的第一张图 → 内置默认图。
    /// </summary>
    private async Task LoadPreviewAsync(Workflow workflow)
    {
        IsPreviewLoading = true;
        try
        {
            // 0) 视频工作流（Source=="video" 或自身关联产物为视频）：预览区切播放器，封面用工作流缩略图
            if (await DetectVideoWorkflowAsync(workflow))
            {
                if (string.IsNullOrEmpty(CoverImagePath))
                {
                    var cover = await ResolveProductThumbPathAsync(workflow);
                    CoverImagePath = cover;
                }
                return;
            }

            // 1) 手动设置
            if (!string.IsNullOrEmpty(workflow.ThumbnailPath) && File.Exists(workflow.ThumbnailPath))
            {
                PreviewImage = await LoadBitmapAsync(workflow.ThumbnailPath);
                return;
            }

            // ---- 图片提取工作流：自身关联的第一张图 → 默认图 ----
            if ((workflow.Source == "image" || workflow.Source == "video"))
            {
                using var imgDb = await _dbFactory.CreateDbContextAsync();
                var rel = await imgDb.Workflows
                    .Where(x => x.Id == workflow.Id && (x.Source == "image" || x.Source == "video"))
                    .SelectMany(x => x.Images)
                    .OrderBy(x => x.CreatedAt)
                    .Select(x => x.RelativePath)
                    .FirstOrDefaultAsync();
                if (rel != null)
                {
                    var bmp = await TryLoadProductBitmapAsync(rel);
                    if (bmp != null) { PreviewImage = bmp; return; }
                }
                PreviewImage = LoadPlaceholderBitmap();
                return;
            }

            // 2) GUID 匹配：用户工作流 JSON 顶级 "id" == 图片提取工作流的 WorkflowGuid
            try
            {
                var wfGuid = ExtractWorkflowGuid(workflow.GetWorkflowJson() ?? "");
                if (!string.IsNullOrEmpty(wfGuid))
                {
                    using var db = await _dbFactory.CreateDbContextAsync();
                    var rel = await db.Workflows
                        .Where(x => x.WorkflowGuid == wfGuid && (x.Source == "image" || x.Source == "video") && !x.IsDeleted)
                        .SelectMany(x => x.Images)
                        .OrderBy(x => x.CreatedAt)
                        .Select(x => x.RelativePath)
                        .FirstOrDefaultAsync();
                    if (rel != null)
                    {
                        var bmp = await TryLoadProductBitmapAsync(rel);
                        if (bmp != null) { PreviewImage = bmp; return; }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Debug(_local["GuidMatchThumbFailed"], "Workflow", ex);
            }

            // 3) 任务输出第一张图
            try
            {
                using var db = await _dbFactory.CreateDbContextAsync();
                var rel = await db.JobOutputs
                    .Where(x => x.Job.WorkflowId == WorkflowId && !x.IsDeleted && x.ImageInfoId != null)
                    .OrderBy(x => x.Job.CreatedAt)
                    .Select(x => x.Image!.RelativePath)
                    .FirstOrDefaultAsync();
                if (rel != null)
                {
                    var bmp = await TryLoadProductBitmapAsync(rel);
                    if (bmp != null) { PreviewImage = bmp; return; }
                }
            }
            catch (Exception ex)
            {
                _log.Debug(_local["JobOutputMatchThumbFailed"], "Workflow", ex);
            }

            // 4) 默认占位图
            PreviewImage = LoadPlaceholderBitmap();
        }
        catch (Exception ex)
        {
            _log.Warn(string.Format(_local["LoadPreviewFailed"], Name), "Workflow", ex);
        }
        finally
        {
            IsPreviewLoading = false;
        }
    }

    /// <summary>判定视频工作流：自身关联产物第一张为视频文件（或 Source=="video"）→ 设置 VideoPath / IsVideoWorkflow。</summary>
    private async Task<bool> DetectVideoWorkflowAsync(Workflow workflow)
    {
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            var rel = await db.Workflows.AsNoTracking()
                .Where(x => x.Id == workflow.Id)
                .SelectMany(x => x.Images)
                .OrderBy(x => x.CreatedAt)
                .Select(x => x.RelativePath)
                .FirstOrDefaultAsync();
            if (string.IsNullOrEmpty(rel)) return false;

            var full = Path.Combine(_settings.ComfyOutputDir, rel);
            if (!File.Exists(full) || !PromptCraft.Service.ComfyMediaKinds.IsVideo(full)) return false;

            VideoPath = full;
            IsVideoWorkflow = true;
            return true;
        }
        catch (Exception ex)
        {
            _log.Debug(_local["VideoWorkflowDetectFailed"] ?? "视频工作流检测失败: {0}", "Workflow", ex);
            return false;
        }
    }

    /// <summary>解析视频工作流的封面路径：工作流 ThumbnailPath（同步时已存首帧封面）优先，否则产物缩略图。</summary>
    private async Task<string?> ResolveProductThumbPathAsync(Workflow workflow)
    {
        if (!string.IsNullOrEmpty(workflow.ThumbnailPath) && File.Exists(workflow.ThumbnailPath))
            return workflow.ThumbnailPath;

        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            var rel = await db.Workflows.AsNoTracking()
                .Where(x => x.Id == workflow.Id)
                .SelectMany(x => x.Images)
                .OrderBy(x => x.CreatedAt)
                .Select(x => x.RelativePath)
                .FirstOrDefaultAsync();
            if (string.IsNullOrEmpty(rel)) return null;

            var thumb = Path.Combine(_settings.ThumbDir,
                PromptCraft.Service.ImageSyncService.GetThumbFileName(rel, _settings.ThumbMaxDimension));
            return File.Exists(thumb) ? thumb : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从工作流 JSON 提取顶级 "id"（ComfyUI 工作流 UUID）；无则返回 null</summary>
    private static string? ExtractWorkflowGuid(string workflowJson)
    {
        if (string.IsNullOrWhiteSpace(workflowJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(workflowJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) return null;
            var s = id.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>选择封面：优先弹图库选择器（可点选图库图片，或切到从电脑选择）。</summary>
    [RelayCommand]
    private async Task ChooseImageAsync()
    {
        try
        {
            // 仅显示当前工作流生成的图片（Workflow.Images 关联 + 执行历史输出）；无产物则回退全库
            var ids = await LoadWorkflowProductImageIdsAsync(_imageWorkflowId ?? WorkflowId, _materializedWorkflowId);
            var pickerResult = await OpenGalleryPickerAsync(ids.Count > 0 ? ids : null);
            string? picked = null;
            if (pickerResult == ThumbnailPickerModel.PickerResult.PickedGallery)
                picked = SelectedPickerPath;
            else if (pickerResult == ThumbnailPickerModel.PickerResult.PickFromComputer)
                picked = await PickFromComputerAsync();

            if (picked == null) return;
            await ApplyThumbnailAsync(picked);
        }
        catch (Exception ex)
        {
            StatusText = string.Format(_local["SetCoverFailed"], ex.Message);
            _log.Error(string.Format(_local["SetCoverLogFailed"], Name), "Workflow", ex);
        }
    }

    /// <summary>从图库选择器返回时暂存选中的图片路径（弹窗内 VM 生命周期结束后仍可用）。</summary>
    private string? SelectedPickerPath { get; set; }

    /// <summary>从图库选择器返回时暂存选中的 ImageInfo Id（选用图片参数场景：按 Id 查该图节点参数）。</summary>
    private int? SelectedPickerImageInfoId { get; set; }

    /// <summary>当前工作流生成的图片 Id 集合（Workflow.Images 关联 + 执行历史 JobOutputs 输出；封面选择/选用图片参数过滤用）。</summary>
    private async Task<List<int>> LoadWorkflowProductImageIdsAsync(params int[] workflowIds)
    {
        if (workflowIds.Length == 0) return new List<int>();
        using var db = await _dbFactory.CreateDbContextAsync();
        var ids = await db.Workflows.AsNoTracking()
            .Where(w => workflowIds.Contains(w.Id))
            .SelectMany(w => w.Images)
            .Select(x => x.Id)
            .ToListAsync();
        var jobIds = await db.JobOutputs.AsNoTracking()
            .Where(x => workflowIds.Contains(x.Job.WorkflowId) && !x.IsDeleted && x.ImageInfoId != null)
            .Select(x => x.ImageInfoId!.Value)
            .ToListAsync();
        return ids.Concat(jobIds).Distinct().ToList();
    }

    /// <summary>反序列化提示词表的节点快照（Rehydrate 前准备；对齐图库详情 ImageDetailModel）。</summary>
    private static List<NodeSnapshot>? ParseNodeSnapshots(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<List<NodeSnapshot>>(json); }
        catch { return null; }
    }

    /// <summary>弹出图库封面选择器（SukiMessageBox + ThumbnailPickerView），返回用户操作结果。
    /// <paramref name="allowedImageIds"/> 非空时仅显示这些图片（当前工作流生成的图片）；autoSelectFirst 加载后默认选中第一张。</summary>
    private async Task<ThumbnailPickerModel.PickerResult> OpenGalleryPickerAsync(
        IReadOnlyCollection<int>? allowedImageIds = null,
        bool autoSelectFirst = false)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } owner)
        {
            return ThumbnailPickerModel.PickerResult.Cancelled;
        }

        var viewService = _service.GetRequiredService<IBaseViewService>();
        var pickerVm = new ThumbnailPickerModel(_settings, _dbFactory, _local, _noticeService, _log, allowedImageIds, autoSelectFirst);
        if (!viewService.TryCreateView(pickerVm, out var pickerView, "ThumbnailPicker"))
        {
            _log.Warn(_local["ThumbnailPickerViewNotRegistered"], "Workflow");
            return ThumbnailPickerModel.PickerResult.Cancelled;
        }

        var host = new SukiMessageBoxHost
        {
            Content = pickerView,
            IconPreset = null,
            Width = 760,
            ActionButtonsPreset = SukiMessageBoxButtons.Close,
        };

        // 底部操作区：[从电脑选择] [确定]（Source 覆盖 Preset → 不显示关闭按钮；Esc = 取消）
        var pickBtn = CreatePickerButton(SukiMessageBoxResult.Continue, _local["PickFromComputer"], pickerVm.PickFromComputerCommand);
        var confirmBtn = CreatePickerButton(SukiMessageBoxResult.OK, _local["Ok"], pickerVm.ConfirmCommand);
        confirmBtn.IsEnabled = false;
        pickerVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ThumbnailPickerModel.SelectedItem))
                confirmBtn.IsEnabled = pickerVm.SelectedItem != null;
        };
        host.ActionButtonsSource = new Avalonia.Collections.AvaloniaList<Avalonia.Controls.Button>
        {
            pickBtn,
            confirmBtn,
        };

        var options = new SukiMessageBoxOptions
        {
            Title = _local["ChooseCoverImageTitle"],
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
        window.Content = host;
        pickerVm.RequestClose += () => window.Close();

        // Esc 关闭 = 取消
        window.KeyUp += (_, e) =>
        {
            if (e.Key == Key.Escape) window.Close();
        };

        window.Opened += (_, _) =>
        {
            var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
            if (screen is null) return;
            var wa = screen.WorkingArea;
            var scale = screen.Scaling;
            var w = double.IsNaN(window.Width) ? 760 : window.Width;
            var h = double.IsNaN(window.Height) ? 560 : window.Height;
            window.Position = new PixelPoint(
                wa.X + (int)((wa.Width - w * scale) / 2),
                wa.Y + (int)(wa.Height * 0.1));
        };

        window.Closed += (_, _) => window.Content = null;
        await window.ShowDialog<object?>(owner);

        SelectedPickerPath = pickerVm.SelectedPath;
        SelectedPickerImageInfoId = pickerVm.SelectedImageInfoId;
        return pickerVm.Result;
    }

    /// <summary>底部操作按钮（点击执行命令；RequestClose 由 VM 内触发关闭）。</summary>
    private static Avalonia.Controls.Button CreatePickerButton(
        SukiMessageBoxResult result, string text, CommunityToolkit.Mvvm.Input.IRelayCommand command)
    {
        var button = SukiMessageBoxButtonsFactoryExtend.CreateButton(result, text);
        button.Click += (_, _) => command.Execute(null);
        return button;
    }

    /// <summary>从电脑选择图片（系统文件选择器），返回绝对路径；取消返回 null。</summary>
    private async Task<string?> PickFromComputerAsync()
    {
        var provider = StorageService.GetStorageProvider();
        if (provider == null) return null;
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _local["ChooseWorkflowCoverTitle"],
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(_local["ImageFileType"])
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"],
                    MimeTypes = ["image/png", "image/jpeg", "image/webp", "image/bmp"],
                },
                StorageService.All,
            ],
        });
        if (files.Count == 0) return null;
        var picked = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(picked) || !File.Exists(picked)) return null;
        return picked;
    }

    /// <summary>把选中的图片复制到缩略图目录，并更新对应来源的 ThumbnailPath（Workflows 表 / ImageWorkflows 表）。</summary>
    private async Task ApplyThumbnailAsync(string picked)
    {
        Directory.CreateDirectory(_settings.ThumbDir);
        var ext = Path.GetExtension(picked);
        var dest = Path.Combine(_settings.ThumbDir, $"workflow_{WorkflowId}_thumb{ext}");
        File.Copy(picked, dest, overwrite: true);

        if (_imageWorkflowId is int iwfId)
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            var iwf = await db.Workflows.FirstOrDefaultAsync(x => x.Id == iwfId && (x.Source == "image" || x.Source == "video"));
            if (iwf == null) return;
            iwf.ThumbnailPath = dest;
            await db.SaveChangesAsync();
            _log.Info(string.Format(_local["SetImageWorkflowCover"], Name, dest), "Workflow");
        }
        else
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            var workflow = await db.Workflows.FirstOrDefaultAsync(x => x.Id == WorkflowId);
            if (workflow == null) return;
            workflow.ThumbnailPath = dest;
            workflow.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            _log.Info(string.Format(_local["SetWorkflowCover"], Name, dest), "Workflow");
        }
        // 封面变化实时通知列表页刷新对应卡片
        WorkflowChanged?.Invoke();

        PreviewImage = await LoadBitmapAsync(dest);
        StatusText = _local["CoverUpdated"];
    }

    private static async Task<Bitmap?> LoadBitmapAsync(string path)
    {
        try
        {
            return await Task.Run(() => new Bitmap(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>加载产物预览图：图片直接解码原文件；视频解码其首帧封面缩略图（jpg），
    /// 避免 Skia 直接解码视频抛 "Unable to load bitmap"。加载失败返回 null（调用方回退）。</summary>
    private async Task<Bitmap?> TryLoadProductBitmapAsync(string relativePath)
    {
        var path = Path.Combine(_settings.ComfyOutputDir, relativePath);
        if (!File.Exists(path)) return null;
        if (PromptCraft.Service.ComfyMediaKinds.IsVideo(path))
        {
            var thumb = Path.Combine(_settings.ThumbDir,
                PromptCraft.Service.ImageSyncService.GetThumbFileName(relativePath, _settings.ThumbMaxDimension));
            if (File.Exists(thumb)) return await LoadBitmapAsync(thumb);
            return null;
        }
        return await LoadBitmapAsync(path);
    }

    private static Bitmap? LoadPlaceholderBitmap()
    {
        try
        {
            var uri = new Uri("avares://PromptCraft/Assets/workflow_placeholder.png");
            using var stream = Avalonia.Platform.AssetLoader.Open(uri);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>规范化并缩进展开 JSON（仅影响显示/编辑缓冲区；保存时才落库）。解析失败返回原文。</summary>
    private static string FormatJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        try
        {
            return JsonNode.Parse(json)?.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }) ?? json;
        }
        catch
        {
            return json;
        }
    }

    #endregion

    #region JSON 编辑与保存

    /// <summary>保存工作流 JSON（校验 JSON 合法性后写库）。</summary>
    [RelayCommand]
    private async Task SaveJsonAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(WorkflowJson))
            {
                StatusText = _local["WorkflowContentEmpty"];
                return;
            }
            // 校验并规范化 JSON（缩进展开、中文不转义）；保存/哈希统一用规范化文本
            string normalized;
            try
            {
                normalized = JsonNode.Parse(WorkflowJson)?.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }) ?? WorkflowJson;
            }
            catch (Exception ex)
            {
                StatusText = string.Format(_local["JsonFormatError"], ex.Message);
                _log.Warn(string.Format(_local["JsonSaveRejected"], Name), "Workflow");
                return;
            }

            // ---- 图片提取工作流：更新压缩 JSON（名称只读；WorkflowGuid 为去重键，编辑不变更）----
            if (_imageWorkflowId is int iwfId)
            {
                using var db2 = await _dbFactory.CreateDbContextAsync();
                var iwf = await db2.Workflows.FirstOrDefaultAsync(x => x.Id == iwfId && (x.Source == "image" || x.Source == "video"));
                if (iwf == null)
                {
                    StatusText = _local["WorkflowNotFound"];
                    return;
                }
                var oldJson = ComfyMetadataCodec.Decompress(iwf.WorkflowJsonBlob) ?? "";
                var semanticChanged = FormatJson(oldJson) != normalized;
                iwf.WorkflowJsonBlob = ComfyMetadataCodec.Compress(normalized);
                await db2.SaveChangesAsync();
                WorkflowJson = normalized;
                _log.Info(string.Format(_local["SaveImageWorkflowJson"], Name, iwfId, normalized.Length, semanticChanged), "Workflow");
                StatusText = _local["Saved"];
                return;
            }

            using var db = await _dbFactory.CreateDbContextAsync();
            var workflow = await db.Workflows.FirstOrDefaultAsync(x => x.Id == WorkflowId);
            if (workflow == null)
            {
                StatusText = _local["WorkflowNotFound"];
                return;
            }
            workflow.WorkflowJsonBlob = ComfyMetadataCodec.Compress(normalized);
            workflow.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            WorkflowJson = normalized;
            _log.Info(string.Format(_local["SaveWorkflowJson"], Name, WorkflowId, normalized.Length), "Workflow");
            StatusText = _local["Saved"];
        }
        catch (Exception ex)
        {
            StatusText = string.Format(_local["SaveFailed"], ex.Message);
            _log.Error(string.Format(_local["SaveWorkflowJsonFailed"], Name), "Workflow", ex);
        }
    }

    #endregion

    #region 触发执行 + 进度

    /// <summary>触发执行：UI→API 转换 → POST /prompt → 后台跟踪进度（WS + 轮询兜底）。</summary>
    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsRunning) return;
        try
        {
            IsRunning = true;
            CanRun = false;
            ProgressValue = 0;
            ProgressMax = 100;
            ProgressText = _local["Submitting"];
            NodeLog = "";
            StatusText = "";

            var service = _service.GetRequiredService<IComfyUIService>();
            SubmitWorkflowResponse result;

            // 应用"配置参数"弹窗保存的参数（覆盖 widgets_values 后提交；无配置返回 null 用原始 JSON）
            var overrideUiJson = await BuildOverrideWorkflowJsonAsync();

            if (_imageWorkflowId != null)
            {
                // 图片提取工作流：先物化 Workflows 记录（执行历史归属），再提交
                var (submit, workflowId) = await service.SubmitAndTrackImageWorkflowAsync(overrideUiJson ?? WorkflowJson, Name);
                result = submit;
                _materializedWorkflowId = workflowId;
            }
            else
            {
                result = await service.SubmitAndTrackWorkflowAsync(WorkflowId, overrideUiJson);
            }
            if (!result.Success)
            {
                StatusText = string.Format(_local["RunFailedWithError"], result.ErrorMessage);
                ProgressText = _local["SubmitFailed"];
                // 完整响应已由 ComfyUIService.SubmitCoreAsync 记 Error，此处仅记 Debug 避免重复
                _log.Debug(string.Format(_local["WorkflowSubmitRejected"], Name, WorkflowId), "Workflow");
                IsRunning = false; // 提交阶段即失败，直接收尾（避免进度区悬挂）
                CanRun = true;      // 提交失败：恢复执行按钮
                ShowResultToast(_local["WorkflowRunFailed"], result.ErrorMessage ?? _local["UnknownError"]);
                return;
            }

            StatusText = _local["SubmittedWaiting"];
            _log.Info(string.Format(_local["UserTriggerRun"], Name, WorkflowId), "Workflow");
            _ = RefreshHistoryAsync();
            // 注意：提交成功后 IsRunning 保持 true、CanRun 保持 false——执行期间禁止重复触发，
            // 直到 OnJobProgress 收到终态（completed/failed）才恢复按钮。
        }
        catch (Exception ex)
        {
            StatusText = string.Format(_local["RunException"], ex.Message);
            IsRunning = false;
            CanRun = true;
            _log.Error(string.Format(_local["WorkflowRunException"], Name), "Workflow", ex);
        }
        finally
        {
            // 提交成功后 IsRunning 保持 true（等待 OnJobProgress 终态），CanRun 保持 false（防重复触发）
        }
    }

    /// <summary>进度事件回调（后台线程），Dispatcher 封送更新 UI。</summary>
    private void OnJobProgress(WorkflowJobProgress progress)
    {
        if (progress.JobId == 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            switch (progress.Status)
            {
                case "running":
                    if (progress.ProgressMax > 0)
                    {
                        ProgressValue = progress.ProgressValue;
                        ProgressMax = progress.ProgressMax;
                        ProgressText = $"{progress.ProgressValue} / {progress.ProgressMax}";
                    }
                    StatusText = progress.Status;
                    break;
                case "completed":
                    ProgressText = _local["Completed"];
                    StatusText = _local["RunCompleted"];
                    IsRunning = false; // 执行结束，隐藏进度区
                    break;
                case "failed":
                    ProgressText = _local["RunFailed"];
                    StatusText = string.Format(_local["RunFailedWithError"], progress.ErrorMessage);
                    IsRunning = false; // 执行结束，隐藏进度区
                    break;
            }
            if (!string.IsNullOrEmpty(progress.NodeLogLine))
                NodeLog = NodeLog.Length > 8000
                    ? NodeLog[^6000..] + "\n" + progress.NodeLogLine
                    : NodeLog + "\n" + progress.NodeLogLine;

            // 同步执行历史对应条目：进度条实时更新，状态就地更新（排队中→执行中→成功/失败），
            // 即使后台列表重建失败，用户也能看到最新结果
            var jobItem = Jobs.FirstOrDefault(j => j.Id == progress.JobId);
            if (jobItem != null)
            {
                if (progress.Status == "running")
                {
                    jobItem.Status = "running";
                    jobItem.StatusText = _local["Running"];
                    jobItem.IsRunning = true;
                    if (progress.ProgressMax > 0)
                    {
                        jobItem.ProgressValue = progress.ProgressValue;
                        jobItem.ProgressMax = progress.ProgressMax;
                    }
                }
                else
                {
                    jobItem.IsRunning = false;
                }
                if (progress.Status is "completed" or "failed")
                {
                    jobItem.Status = progress.Status;
                    jobItem.StatusText = progress.Status == "completed" ? _local["Success"] : _local["Failed"];
                    jobItem.ErrorMessage = progress.ErrorMessage;
                }
            }

            // 终态：同一 Job 只弹一次结果提示（execution_success 与 FinalizeJobAsync 收尾都会发终态事件，避免双弹窗）。
            // 执行按钮在终态恢复（执行期间保持禁用，防止重复触发）。
            // 列表刷新只在"结果已落库"终态触发（此时 DB 已更新）：execution_success 到达时 DB 可能还是 queued，
            // 立即刷新会把就地更新的"成功"覆盖回"排队中"。
            if (progress.Status is "completed" or "failed")
            {
                CanRun = true;
                if (_notifiedJobIds.Add(progress.JobId))
                {
                    if (progress.Status == "completed")
                        ShowResultToast(_local["RunSucceeded"], string.Format(_local["WorkflowRunCompleted"], Name));
                    else
                        ShowResultToast(_local["RunFailed"], progress.ErrorMessage ?? _local["UnknownError"]);
                }
                if (string.Equals(progress.NodeLogLine, "结果已落库", StringComparison.Ordinal))
                    _ = RefreshHistoryAsync();
            }
        });
    }

    #endregion

    /// <summary>轻量 Toast 结果提示（数秒后自动消失，点击可提前关闭），成功/失败通用。</summary>
    private void ShowResultToast(string title, string content)
    {
        try
        {
            var toastManager = _service.GetService(typeof(ISukiToastManager)) as ISukiToastManager;
            if (toastManager == null) return;
            var toast = FluentSukiToastBuilder.CreateSimpleInfoToast(toastManager);
            toast.SetTitle(title);
            toast.SetContent(content);
            toast.SetCanDismissByClicking(true);
            toast.Toast.DismissTimeout = TimeSpan.FromSeconds(4);
            toast.Queue();
        }
        catch (Exception ex)
        {
            _log.Debug(_local["ToastErrorFailed"], "Workflow", ex);
        }
    }

    #region 执行历史

    /// <summary>刷新执行历史（时间倒序，数据尽量全：状态/耗时/PromptId/错误/输出图）。
    /// 并发安全：每次刷新取最新序号，只有最后一次刷新的结果被采用——避免旧查询（如提交时读到的 queued）
    /// 在终态刷新之后完成、把"成功"覆盖回"排队中"。</summary>
    [RelayCommand]
    public async Task RefreshHistoryAsync()
    {
        var seq = ++_refreshSeq;
        try
        {
            // 图片提取工作流：执行历史挂在物化出的 Workflows 记录上；未执行过则按顶级 WorkflowGuid 查找归属
            var effectiveWorkflowId = _imageWorkflowId == null ? WorkflowId : _materializedWorkflowId;
            if (_imageWorkflowId != null && effectiveWorkflowId == 0)
            {
                using var probe = await _dbFactory.CreateDbContextAsync();
                var wfGuid = ExtractWorkflowGuid(WorkflowJson);
                var materialized = wfGuid == null ? null : await probe.Workflows
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.WorkflowGuid == wfGuid && (x.Source == "image" || x.Source == "video") && !x.IsDeleted);
                if (materialized != null) _materializedWorkflowId = materialized.Id;
                effectiveWorkflowId = _materializedWorkflowId;
            }
            if (effectiveWorkflowId == 0)
            {
                if (seq != _refreshSeq) return; // 已有更新的刷新，丢弃本次
                Jobs = new ObservableCollection<WorkflowJobItem>();
                HasJobs = false;
                return;
            }

            var service = _service.GetRequiredService<IComfyUIService>();
            var jobs = await service.GetWorkflowJobsAsync(effectiveWorkflowId);
            var items = new List<WorkflowJobItem>();
            foreach (var job in jobs)
            {
                // 收尾空窗保护：execution_success 已就地更新为终态（成功/失败），但 FinalizeJobAsync
                // 落库 completed/failed 前 DB 仍是 queued（有约几百 ms 空窗：查 /history + 构建输出 + 保存）。
                // 此时刷新若按 DB 重建会把"成功"覆盖回"排队中"。DB 读到 queued 而本地已是终态 → 保留本地终态。
                var localItem = Jobs.FirstOrDefault(j => j.Id == job.Id);
                if (localItem is { Status: "completed" or "failed" } && job.Status == "queued")
                {
                    var kept = new WorkflowJobItem
                    {
                        Id = localItem.Id,
                        Status = localItem.Status,
                        StatusText = localItem.StatusText,
                        TimeText = localItem.TimeText,
                        DurationText = localItem.DurationText,
                        PromptId = localItem.PromptId,
                        ErrorMessage = localItem.ErrorMessage,
                        Outputs = localItem.Outputs,
                    };
                    foreach (var thumb in localItem.OutputThumbs)
                        kept.OutputThumbs.Add(thumb);
                    SubscribeJobSelection(kept);
                    items.Add(kept);
                    continue;                }

                var item = new WorkflowJobItem
                {
                    Id = job.Id,
                    Status = job.Status,
                    StatusText = job.Status switch
                    {
                        "queued" => _local["Queued"],
                        "running" => _local["Running"],
                        "completed" => _local["Success"],
                        "failed" => _local["Failed"],
                        _ => job.Status,
                    },
                    TimeText = job.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    DurationText = FormatDuration(job),
                    PromptId = job.PromptId ?? "",
                    ErrorMessage = job.ErrorMessage,
                };
                item.Outputs = job.Outputs.Where(x => !x.IsDeleted).ToList();
                SubscribeJobSelection(item);
                foreach (var output in job.Outputs.Where(x => !x.IsDeleted))
                {
                    var path = Path.Combine(_settings.ComfyOutputDir, output.SubFolder ?? "", output.FileName);
                    if (File.Exists(path))
                    {
                        var bmp = await LoadBitmapAsync(path);
                        if (bmp != null) item.OutputThumbs.Add(bmp);
                    }
                }
                items.Add(item);
            }
            // 只采用最新一次刷新的结果：期间有更新的刷新发起（如终态刷新）则丢弃本次（旧数据）
            if (seq != _refreshSeq) return;
            Jobs = new ObservableCollection<WorkflowJobItem>(items);
            HasJobs = items.Count > 0;
        }
        catch (Exception ex)
        {
            _log.Warn(string.Format(_local["LoadRunHistoryFailed"], Name), "Workflow", ex);
        }
    }

    private static string FormatDuration(WorkflowJob job)
    {
        var end = job.CompletedAt ?? DateTime.UtcNow;
        var start = job.StartedAt ?? job.CreatedAt;
        var span = end - start;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalSeconds < 1 ? "<1s"
             : span.TotalMinutes < 1 ? $"{span.TotalSeconds:F0}s"
             : $"{span.Minutes}m{span.Seconds}s";
    }

    #endregion

    #region 删除

    /// <summary>删除工作流：逻辑删除（DB 依赖数据 + ComfyUI 远端文件）。</summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        try
        {
            var deleteHint = _imageWorkflowId != null
                ? _local["DeleteHintImageExtract"]
                : _local["DeleteHintNormal"];
            var confirm = await SukiMessageBox.ShowDialog(
                new SukiMessageBoxHost
                {
                    Content = new Avalonia.Controls.TextBlock
                    {
                        Text = string.Format(_local["ConfirmDeleteWorkflow"], Name, deleteHint),
                        Margin = new Thickness(4),
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        MaxWidth = 320,
                    },
                    ActionButtonsPreset = SukiMessageBoxButtons.OKCancel,
                },
                new SukiMessageBoxOptions
                {
                    Title = _local["ConfirmDelete"],
                    MinWidth = 340,
                });
            if (confirm is not SukiMessageBoxResult r || !r.Equals(SukiMessageBoxResult.OK)) return;

            var service = _service.GetRequiredService<IComfyUIService>();

            // ---- 图片提取工作流：逻辑删除（ImageWorkflow + 关联 ImagePrompt，保留图片）----
            if (_imageWorkflowId is int iwfId)
            {
                var ok = await service.DeleteImageWorkflowAsync(iwfId);
                if (!ok)
                {
                    StatusText = _local["WorkflowNotFound"];
                    return;
                }
                StatusText = _local["Deleted"];
                WorkflowChanged?.Invoke();
                RequestClose?.Invoke();
                return;
            }

            using var db = await _dbFactory.CreateDbContextAsync();
            var workflow = await db.Workflows.AsNoTracking().FirstOrDefaultAsync(x => x.Id == WorkflowId);
            if (workflow == null)
            {
                StatusText = _local["WorkflowNotFound"];
                return;
            }
            await service.DeleteWorkflowAsync(workflow);
            StatusText = _local["Deleted"];
            WorkflowChanged?.Invoke();
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            StatusText = string.Format(_local["DeleteFailed"], ex.Message);
            _log.Error(string.Format(_local["DeleteWorkflowFailed"], Name), "Workflow", ex);
        }
    }

    #endregion

    #region 参数配置

    /// <summary>加载该工作流的参数配置并应用到 UI 工作流 JSON；无配置/解析失败返回 null（用原始 JSON 执行）。</summary>
    private async Task<string?> BuildOverrideWorkflowJsonAsync()
    {
        var effectiveWorkflowId = _imageWorkflowId == null ? WorkflowId : _materializedWorkflowId;
        if (effectiveWorkflowId == 0) return null;
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            var row = await db.WorkflowParamsSet.AsNoTracking()
                .FirstOrDefaultAsync(x => x.WorkflowId == effectiveWorkflowId);
            if (row == null || string.IsNullOrEmpty(row.ParamsJson)) return null;
            var entries = JsonSerializer.Deserialize<List<WorkflowParamEntry>>(row.ParamsJson);
            if (entries == null || entries.Count == 0) return null;
            var overridden = WorkflowJsonConverter.ApplyParams(WorkflowJson, entries);
            _log.Debug(string.Format(_local["AppliedParamsConfig"], entries.Count, effectiveWorkflowId), "Workflow");
            return overridden;
        }
        catch (Exception ex)
        {
            _log.Warn(string.Format(_local["LoadParamsConfigFailed"], effectiveWorkflowId), "Workflow", ex);
            return null;
        }
    }

    /// <summary>打开"配置参数"弹窗（参数保存到 WorkflowParams 表，执行时应用）。</summary>
    [RelayCommand]
    private async Task OpenParamsAsync()
    {
        try
        {
            // 图片提取工作流：先确保物化（参数配置按 WorkflowId 存储）
            var effectiveWorkflowId = _imageWorkflowId == null ? WorkflowId : _materializedWorkflowId;
            if (_imageWorkflowId != null && effectiveWorkflowId == 0)
            {
                using var probe = await _dbFactory.CreateDbContextAsync();
                // 图片提取工作流必有顶级 UUID；按 WorkflowGuid 查重（原"明文 JSON 内容相等"无法直接对压缩 Blob 查询）
                var wfGuid = ExtractWorkflowGuid(WorkflowJson);
                var existing = wfGuid == null ? null : await probe.Workflows.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.WorkflowGuid == wfGuid && (x.Source == "image" || x.Source == "video") && !x.IsDeleted);
                if (existing != null)
                {
                    _materializedWorkflowId = existing.Id;
                }
                else
                {
                    var wf = new Workflow
                    {
                        Name = Name,
                        WorkflowJsonBlob = ComfyMetadataCodec.Compress(WorkflowJson),
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    };
                    probe.Workflows.Add(wf);
                    await probe.SaveChangesAsync();
                    _materializedWorkflowId = wf.Id;
                    _log.Info(string.Format(_local["ImageWorkflowMaterialized"], wf.Id), "Workflow");
                }
                effectiveWorkflowId = _materializedWorkflowId;
            }
            if (effectiveWorkflowId == 0) return;

            // 图片提取工作流：参数来自生成它的图片 → 先选用图片（该工作流产物，默认最新一张），
            // 按该图节点参数 Rehydrate 恢复真实值后，再打开参数配置弹窗
            string paramsJson = WorkflowJson;
            if (_imageWorkflowId != null)
            {
                var ids = await LoadWorkflowProductImageIdsAsync(_imageWorkflowId.Value, effectiveWorkflowId);
                if (ids.Count > 0)
                {
                    var pickerResult = await OpenGalleryPickerAsync(ids, autoSelectFirst: true);
                    if (pickerResult != ThumbnailPickerModel.PickerResult.PickedGallery || SelectedPickerImageInfoId == null)
                        return;

                    using var pickDb = await _dbFactory.CreateDbContextAsync();
                    var pickedImg = await pickDb.ImageMetadata.AsNoTracking()
                        .Include(x => x.Prompt)
                        .FirstOrDefaultAsync(x => x.Id == SelectedPickerImageInfoId);
                    var rehydrated = ComfyWorkflowNormalizer.Rehydrate(WorkflowJson,
                        ParseNodeSnapshots(pickedImg?.Prompt?.GetNodesJson()));
                    if (!string.IsNullOrWhiteSpace(rehydrated)) paramsJson = rehydrated;
                    LogService.Instance.Info($"选用图片参数：WorkflowId={effectiveWorkflowId} 图片Id={SelectedPickerImageInfoId}", "Workflow");
                }
                else
                {
                    // 该工作流暂无生成图片可参考 → 直接按结构 JSON 打开（参数弹窗显示空态）
                    LogService.Instance.Debug($"工作流 {effectiveWorkflowId} 无产物图片可参考，按结构 JSON 打开参数配置", "Workflow");
                }
            }

            await ShowParamsDialogAsync(effectiveWorkflowId, paramsJson);
        }
        catch (Exception ex)
        {
            _log.Error(string.Format(_local["OpenParamsConfigFailed"], Name), "Workflow", ex);
        }
    }

    /// <summary>参数配置弹窗（SukiMessageBox + WorkflowParams 视图；workflowJson 可为 Rehydrate 后的完整参数版）。</summary>
    private async Task ShowParamsDialogAsync(int effectiveWorkflowId, string paramsJson)
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
                || desktop.MainWindow is not { } owner)
            {
                return;
            }

            var viewService = _service.GetRequiredService<IBaseViewService>();
            var vm = new WorkflowParamsModel(effectiveWorkflowId, paramsJson, _dbFactory, _local, _noticeService, _log);
            if (!viewService.TryCreateView(vm, out var paramsView, "WorkflowParams"))
            {
                _log.Warn(_local["ParamsViewNotRegistered"], "Workflow");
                return;
            }

            var host = new SukiMessageBoxHost
            {
                Content = paramsView,
                IconPreset = null,
                Width = 720,
                ActionButtonsPreset = SukiMessageBoxButtons.Close,
            };

            // 底部操作区：[保存]（VM 保存成功后 RequestClose） [关闭]
            var saveBtn = SukiMessageBoxButtonsFactoryExtend.CreateButton(SukiMessageBoxResult.OK, _local["Save"]);
            saveBtn.Click += (_, _) => vm.SaveCommand.Execute(null);
            var closeBtn = SukiMessageBoxButtonsFactoryExtend.CreateButton(SukiMessageBoxResult.Cancel, _local["Close"]);
            closeBtn.Click += (_, _) => vm.RequestCloseNow();
            host.ActionButtonsSource = new Avalonia.Collections.AvaloniaList<Avalonia.Controls.Button> { saveBtn, closeBtn };

            var options = new SukiMessageBoxOptions
            {
                Title = string.Format(_local["ParamsConfigTitle"], Name),
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
            window.Content = host;
            vm.RequestClose += () => window.Close();

            window.KeyUp += (_, e) =>
            {
                if (e.Key == Key.Escape) window.Close();
            };

            window.Opened += (_, _) =>
            {
                var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
                if (screen is null) return;
                var wa = screen.WorkingArea;
                var scale = screen.Scaling;
                var w = double.IsNaN(window.Width) ? 720 : window.Width;
                var h = double.IsNaN(window.Height) ? 560 : window.Height;
                window.Position = new PixelPoint(
                    wa.X + (int)((wa.Width - w * scale) / 2),
                    wa.Y + (int)(wa.Height * 0.1));
            };

            window.Closed += (_, _) => window.Content = null;
            await window.ShowDialog<object?>(owner);
        }
        catch (Exception ex)
        {
            _log.Error(string.Format(_local["OpenParamsConfigFailed"], Name), "Workflow", ex);
        }
    }

    #endregion

    #region 执行记录管理（删除 / 清空）

    /// <summary>删除单条执行记录（逻辑删除 Job + 输出）。</summary>
    [RelayCommand]
    private async Task DeleteJobAsync(WorkflowJobItem? item)
    {
        if (item == null) return;
        try
        {
            var ok = await new JobRepository(_dbFactory).SoftDeleteJobAsync(item.Id);
            _log.Info(string.Format(_local["DeleteJobLog"], item.Id, WorkflowId, ok), "Workflow");
            await RefreshHistoryAsync();
        }
        catch (Exception ex)
        {
            _log.Error(string.Format(_local["DeleteJobFailed"], item?.Id), "Workflow", ex);
        }
    }

    /// <summary>清空该工作流的全部执行记录（逻辑删除，需确认）。</summary>
    [RelayCommand]
    private async Task ClearJobsAsync()
    {
        var effectiveWorkflowId = _imageWorkflowId == null ? WorkflowId : _materializedWorkflowId;
        if (effectiveWorkflowId == 0) return;
        try
        {
            var confirm = await SukiMessageBox.ShowDialog(
                new SukiMessageBoxHost
                {
                    Content = new Avalonia.Controls.TextBlock
                    {
                        Text = string.Format(_local["ConfirmClearJobs"], Name),
                        Margin = new Thickness(4),
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        MaxWidth = 320,
                    },
                    ActionButtonsPreset = SukiMessageBoxButtons.OKCancel,
                },
                new SukiMessageBoxOptions
                {
                    Title = _local["ConfirmClearHistory"],
                    MinWidth = 340,
                });
            if (confirm is not SukiMessageBoxResult r || !r.Equals(SukiMessageBoxResult.OK)) return;

            var count = await new JobRepository(_dbFactory).SoftDeleteByWorkflowAsync(effectiveWorkflowId);
            _log.Info(string.Format(_local["ClearHistoryLog"], effectiveWorkflowId, count), "Workflow");
            await RefreshHistoryAsync();
        }
        catch (Exception ex)
        {
            _log.Error(string.Format(_local["ClearHistoryFailed"], effectiveWorkflowId), "Workflow", ex);
        }
    }

    /// <summary>对比可用状态：恰好选中两条执行记录。</summary>
    public bool CanCompare => Jobs.Count(x => x.IsSelected) == 2;

    /// <summary>订阅历史条目的选中变化，联动对比按钮可用状态。</summary>
    private void SubscribeJobSelection(WorkflowJobItem item)
    {
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkflowJobItem.IsSelected))
                OnPropertyChanged(nameof(CanCompare));
        };
    }

    #endregion

    #region 图片详情 / 对比（与图库一致）

    /// <summary>双击执行历史条目：打开该记录第一张输出图片的详情弹窗（与图库详情一致）。</summary>
    [RelayCommand]
    private async Task OpenImageDetailAsync(WorkflowJobItem? item)
    {
        if (item == null) return;
        var first = item.Outputs.FirstOrDefault(x => !x.IsDeleted);
        if (first == null)
        {
            ShowResultToast(_local["NoImageToView"], _local["NoOutputImages"]);
            return;
        }
        var imageItem = await BuildImageItemAsync(first);
        if (imageItem == null)
        {
            ShowResultToast(_local["ImageFileNotFound"], first.FileName);
            return;
        }
        OpenImageDetailWindow(imageItem);
    }

    /// <summary>对比两条选中的执行记录（与图库对比一致）。</summary>
    [RelayCommand]
    private async Task CompareJobsAsync()
    {
        var selected = Jobs.Where(x => x.IsSelected).ToList();
        if (selected.Count != 2)
        {
            ShowResultToast(_local["CompareNeedsTwo"], string.Format(_local["CompareNeedExactlyTwo"], selected.Count));
            return;
        }
        var leftOut = selected[0].Outputs.FirstOrDefault(x => !x.IsDeleted);
        var rightOut = selected[1].Outputs.FirstOrDefault(x => !x.IsDeleted);
        var left = await BuildImageItemAsync(leftOut);
        var right = await BuildImageItemAsync(rightOut);
        if (left == null || right == null)
        {
            ShowResultToast(_local["NoImagesToCompare"], _local["BothNeedOutputImages"]);
            return;
        }
        OpenImageCompareWindow(left, right);
    }

    /// <summary>从 JobOutput 构造图库 ImageItem（优先用已入库的 ImageInfo，否则按文件信息）。</summary>
    private async Task<ImageItem?> BuildImageItemAsync(JobOutput? output)
    {
        if (output == null) return null;
        var rel = (string.IsNullOrEmpty(output.SubFolder) ? "" : output.SubFolder.Replace('\\', '/') + "/") + output.FileName;
        var full = string.IsNullOrEmpty(output.SubFolder)
            ? Path.Combine(_settings.ComfyOutputDir, output.FileName)
            : Path.Combine(_settings.ComfyOutputDir, output.SubFolder, output.FileName);
        if (!File.Exists(full)) return null;

        ImageInfo? info = null;
        try
        {
            using var db = await _dbFactory.CreateDbContextAsync();
            if (output.ImageInfoId is int iid)
                info = await db.ImageMetadata.AsNoTracking().FirstOrDefaultAsync(x => x.Id == iid);
            else
                info = await db.ImageMetadata.AsNoTracking().FirstOrDefaultAsync(x => x.RelativePath == rel);
        }
        catch (Exception ex)
        {
            _log.Debug(_local["QueryOutputMetaFailed"], "Workflow", ex);
        }

        var fi = new FileInfo(full);
        return new ImageItem
        {
            ImageInfoId = info?.Id ?? 0,
            RelativePath = rel,
            OutputDir = _settings.ComfyOutputDir,
            FileName = output.FileName,
            FileSize = info?.FileSize ?? fi.Length,
            Width = info?.Width ?? 0,
            Height = info?.Height ?? 0,
            Hash = info?.Hash,
            CreatedAt = info?.CreatedAt ?? fi.LastWriteTimeUtc,
        };
    }

    /// <summary>打开图片详情弹窗（SukiMessageBox + ImageDetailView，与图库一致）。</summary>
    private void OpenImageDetailWindow(ImageItem item)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } owner)
        {
            return;
        }
        try
        {
            var detailVm = new ImageDetailModel(item, _settings, _dbFactory, _service, _local, _noticeService, _log);
            var viewService = _service.GetRequiredService<IBaseViewService>();
            if (!viewService.TryCreateView(detailVm, out var detailView, "ImageDetail")) return;

            var host = new SukiMessageBoxHost
            {
                Content = detailView,
                IconPreset = null,
                Width = 900,
                ActionButtonsPreset = SukiMessageBoxButtons.Close,
            };
            var options = new SukiMessageBoxOptions
            {
                Title = item.FileName,
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
            window.Content = host;
            if (host.ActionButtonsSource is { } buttons)
            {
                foreach (var button in buttons)
                {
                    button.Click += (_, _) => window.Close();
                }
                if (buttons.Count == 1)
                {
                    buttons[0].IsCancel = true;
                }
            }
            window.KeyUp += (_, e) =>
            {
                if (e.Key == Key.Escape) window.Close();
            };

            window.Opened += (_, _) =>
            {
                var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
                if (screen is null) return;
                var wa = screen.WorkingArea;
                var scale = screen.Scaling;
                var w = double.IsNaN(window.Width) ? 900 : window.Width;
                var h = double.IsNaN(window.Height) ? 640 : window.Height;
                window.Position = new PixelPoint(
                    wa.X + (int)((wa.Width - w * scale) / 2),
                    wa.Y + (int)(wa.Height * 0.08));
            };

            window.Closed += (_, _) => window.Content = null;
            window.ShowDialog(owner);
        }
        catch (Exception ex)
        {
            _log.Error(string.Format(_local["OpenImageDetailFailed"], item.FileName), "Workflow", ex);
        }
    }

    /// <summary>打开图片对比弹窗（SukiMessageBox + ImageCompareView，与图库一致）。</summary>
    private void OpenImageCompareWindow(ImageItem left, ImageItem right)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } owner)
        {
            return;
        }
        try
        {
            var compareVm = new ImageCompareModel(left, right, _settings, _dbFactory, _local, _noticeService, _log);
            var viewService = _service.GetRequiredService<IBaseViewService>();
            if (!viewService.TryCreateView(compareVm, out var compareView, "ImageCompare")) return;

            var host = new SukiMessageBoxHost
            {
                Content = compareView,
                IconPreset = null,
                Width = 1100,
                ActionButtonsPreset = SukiMessageBoxButtons.Close,
            };
            var options = new SukiMessageBoxOptions
            {
                Title = _local["ImageCompare"],
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
            window.Content = host;
            if (host.ActionButtonsSource is { } buttons)
            {
                foreach (var button in buttons)
                {
                    button.Click += (_, _) => window.Close();
                }
                if (buttons.Count == 1)
                {
                    buttons[0].IsCancel = true;
                }
            }
            window.KeyUp += (_, e) =>
            {
                if (e.Key == Key.Escape) window.Close();
            };

            window.Opened += (_, _) =>
            {
                var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
                if (screen is null) return;
                var wa = screen.WorkingArea;
                var scale = screen.Scaling;
                var w = double.IsNaN(window.Width) ? 1100 : window.Width;
                var h = double.IsNaN(window.Height) ? 700 : window.Height;
                window.Position = new PixelPoint(
                    wa.X + (int)((wa.Width - w * scale) / 2),
                    wa.Y + (int)(wa.Height * 0.06));
            };

            window.Closed += (_, _) => window.Content = null;
            window.ShowDialog(owner);
        }
        catch (Exception ex)
        {
            _log.Error(string.Format(_local["OpenImageCompareFailedVs"], left.FileName, right.FileName), "Workflow", ex);
        }
    }

    #endregion
}
