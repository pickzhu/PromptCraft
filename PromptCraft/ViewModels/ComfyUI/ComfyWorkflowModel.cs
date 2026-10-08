using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using BaseClassLib.Extends;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.BaseModel;
using PromptCraft.Data;
using PromptCraft.Interfaces;
using PromptCraft.Models.ComfyUI;
using PromptCraft.Utils;
using Ke.Bee.Localization.Localizer;
using Material.Icons;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SukiUI.Controls;
using SukiUI.MessageBox;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.ComfyUI;

/// <summary>工作流来源：ComfyUI 同步 / 本地新建 / 图片提取（同步图片时从 PNG 元数据提取）。</summary>
public enum WorkflowSource
{
    Comfy,
    Local,
    ImageExtracted,
    VideoExtracted,
}

/// <summary>工作流列表卡片项。</summary>
public partial class WorkflowItem : ObservableObject
{
    /// <summary>实体 Id：Workflows.Id 或 ImageWorkflows.Id（见 <see cref="Source"/>）。</summary>
    public int Id { get; init; }

    /// <summary>图片提取时为 ImageWorkflows.Id，其余为 null。</summary>
    public int? ImageWorkflowId { get; init; }

    public WorkflowSource Source { get; init; }

    public string Name { get; init; } = "";
    [ObservableProperty] private string _updatedText = "";
    public int NodeCount { get; init; }
    public string? SourcePath { get; init; }

    /// <summary>工作流内容为空（WorkflowJsonBlob 为空），此时卡片删除按钮才可用。</summary>
    [ObservableProperty] private bool _isEmptyContent;

    /// <summary>合并列表排序时间（Workflows.UpdatedAt / ImageWorkflows.CreatedAt 统一）。</summary>
    public DateTime SortTime { get; init; }

    /// <summary>是否来自 ComfyUI（有 SourcePath），列表显示云端徽标。</summary>
    public bool FromComfy => Source == WorkflowSource.Comfy;

    /// <summary>是否显示来源徽标（本地新建的不显示）。</summary>
    public bool ShowBadge => Source != WorkflowSource.Local;

    /// <summary>来源徽标文案。</summary>
    public string SourceBadge => Source switch
    {
        WorkflowSource.Comfy => "ComfyUI",
        WorkflowSource.ImageExtracted => Localizer.Instance?["ImageExtractedBadge"] ?? "",
        WorkflowSource.VideoExtracted => Localizer.Instance?["VideoExtracted"] ?? "",
        _ => Localizer.Instance?["LocalBadge"] ?? "",
    };

    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _isSelected;

    /// <summary>卡片标签色块（与图库卡片一致，Tag 表共用）。</summary>
    public ObservableCollection<TagDisplay> Tags { get; } = new();
}

/// <summary>
/// 工作流管理页 ViewModel：数据库列表 + ComfyUI 同步 + 双击详情。
/// </summary>
public partial class ComfyWorkflowModel : ViewModelBase
{
    private readonly IServiceProvider _service;
    private readonly IBaseLogService _log;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isSyncing;
    [ObservableProperty] private string _syncButtonText = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private ObservableCollection<WorkflowItem> _workflows = new();
    [ObservableProperty] private WorkflowItem? _selectedWorkflow;

    // ---- 搜索 / 标签筛选 / 标签管理抽屉（与图库共用 Tag 表与抽屉组件）----
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private ObservableCollection<TagItem> _availableTags = new();

    /// <summary>标签管理抽屉组件（封装新建/重命名/删除，变更后自动刷新本页下拉与列表）</summary>
    public TagManagerDrawerModel TagDrawer { get; }

    /// <summary>已选标签摘要文本（无选中"全部标签"，单个显示名称，多个显示"第一个 +N"）</summary>
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

    /// <summary>右键菜单指向的工作流 Id（由视图在右键时设置）。</summary>
    private int _contextWorkflowId;

    public void SetContextWorkflow(WorkflowItem item) => _contextWorkflowId = item.Id;

    /// <summary>右键菜单：把工作流加入所选文件夹（多对多）。</summary>
    [RelayCommand]
    private async Task AddToFolderAsync()
    {
        if (_contextWorkflowId <= 0) return;
        var picked = await FolderPickerDialog.PickAsync(FolderScopes.Workflow, null, _service);
        if (picked == null) return;
        if (picked.Count == 0)
        {
            ShowToast(_local["AddToFolderNone"] ?? "", "");
            return;
        }
        try
        {
            var folderService = _service.GetRequiredService<IFolderService>();
            await folderService.AddToFoldersAsync(FolderScopes.Workflow,
                new List<string> { _contextWorkflowId.ToString() }, picked);
            ShowToast(string.Format(_local["AddToFolderDone"] ?? "", 1, picked.Count), "");
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["FolderSaveFailed"] ?? "", ex.Message);
            _log.Error(string.Format(_local["FolderSaveFailed"] ?? "", ex.Message), "Workflow", ex);
        }
    }

    private void ShowToast(string title, string content)
    {
        try
        {
            var toastManager = _service.GetService(typeof(SukiUI.Toasts.ISukiToastManager)) as SukiUI.Toasts.ISukiToastManager;
            if (toastManager == null) return;
            var toast = SukiUI.Toasts.FluentSukiToastBuilder.CreateSimpleInfoToast(toastManager);
            toast.SetTitle(title);
            toast.SetContent(content);
            toast.SetCanDismissByClicking(true);
            toast.Toast.DismissTimeout = TimeSpan.FromSeconds(3);
            toast.Queue();
        }
        catch (Exception ex)
        {
            _log.Error("Toast failed", "Workflow", ex);
        }
    }

    public ComfyWorkflowModel(IServiceProvider service, IBaseLogService log) : base(
        service.GetRequiredService<Ke.Bee.Localization.Localizer.Abstractions.ILocalizer>(),
        service.GetRequiredService<PromptCraft.Interfaces.IBaseNotice>())
    {
        _service = service;
        _log = log;
        SyncButtonText = _local["SyncShort"];
        _displayName = "COMFYWORKFLOW";
        _icon = MaterialIconKind.Code;
        _index = 11;
        _sideMenu = true;
        TagDrawer = new TagManagerDrawerModel(
            _service.GetRequiredService<IDbContextFactory<ComfyDbContext>>(),
            _service.GetRequiredService<ITagRepository>(),
            _local, _noticeService, _log);
        // 抽屉内标签变更后刷新本页标签下拉与工作流列表
        TagDrawer.TagsChanged += () =>
        {
            _ = LoadTagsAsync();
            _ = LoadWorkflowsAsync();
        };
        _ = LoadTagsAsync();
        _noticeService.Subscribe(PromptCraft.Consts.Event.EventNameConst.PromptLibraryChangedEvent, OnLibraryChanged);
    }

    /// <summary>资产/文件夹变更后整体重载（文件夹浏览统一走文件夹管理页，本页不再有文件夹筛选）。</summary>
    private async void OnLibraryChanged(object? _)
    {
        await LoadWorkflowsAsync();
    }

    public override void Disposed()
    {
        _noticeService.Unsubscribe(PromptCraft.Consts.Event.EventNameConst.PromptLibraryChangedEvent, OnLibraryChanged);
        base.Disposed();
    }

    [RelayCommand]
    private async Task LoadWorkflowsAsync()
    {
        IsLoading = true;
        try
        {
            using var db = _service.GetRequiredService<IDbContextFactory<ComfyDbContext>>().CreateDbContext();

            // 搜索：名称 / 来源名 / 标签名匹配（与图库搜索语义一致）
            var search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
            // 标签筛选：多选标签 OR 语义（同图库 Any 匹配）
            var selectedTagNames = AvailableTags.Where(t => t.IsSelected).Select(t => t.Name).ToList();

            IQueryable<Workflow> Filter(IQueryable<Workflow> q)
            {
                q = q.Where(x => !x.IsDeleted);
                if (search != null)
                    q = q.Where(x => x.Name.Contains(search)
                        || (x.SourcePath != null && x.SourcePath.Contains(search))
                        || x.WorkflowTags.Any(wt => wt.Tag.Name.Contains(search)));
                if (selectedTagNames.Count > 0)
                    q = q.Where(x => x.WorkflowTags.Any(wt => selectedTagNames.Contains(wt.Tag.Name)));
                return q;
            }

            // 1) Workflows 表（ComfyUI 同步 + 本地新建）—— 投影轻量字段，不加载 WorkflowJsonBlob 大列
            var workflows = await Filter(db.Workflows.AsNoTracking())
                .OrderByDescending(x => x.UpdatedAt)
                .Select(x => new { x.Id, x.Name, x.UpdatedAt, x.SourcePath, NodeCount = x.Inputs.Count(), Tags = x.WorkflowTags.Select(wt => new { wt.Tag.Name, wt.Tag.Color }).ToList(), IsEmptyContent = x.WorkflowJsonBlob == null || x.WorkflowJsonBlob.Length == 0 })
                .ToListAsync();
            var items = workflows.Select(x => new WorkflowItem
            {
                Id = x.Id,
                Source = string.IsNullOrEmpty(x.SourcePath) ? WorkflowSource.Local : WorkflowSource.Comfy,
                Name = x.Name,
                UpdatedText = x.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                NodeCount = x.NodeCount,
                SourcePath = x.SourcePath,
                SortTime = x.UpdatedAt,
                IsEmptyContent = x.IsEmptyContent,
            }).ToList();
            foreach (var item in items)
                FillTags(item, workflows.First(w => w.Id == item.Id).Tags.Select(t => (t.Name, t.Color)));

            // 2) 图片/视频提取工作流（同步时从输出元数据提取，Workflows 表 Source="image"/"video"）—— 投影轻量字段，不加载 WorkflowJsonBlob 大列
            var imageWorkflows = await Filter(db.Workflows.AsNoTracking())
                .Where(x => x.Source == "image" || x.Source == "video")
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => new { x.Id, x.Name, x.Source, x.CreatedAt, x.NodeCount, x.WorkflowGuid, Tags = x.WorkflowTags.Select(wt => new { wt.Tag.Name, wt.Tag.Color }).ToList(), IsEmptyContent = x.WorkflowJsonBlob == null || x.WorkflowJsonBlob.Length == 0 })
                .ToListAsync();
            var imageItems = imageWorkflows.Select(x => new WorkflowItem
            {
                Id = x.Id,
                ImageWorkflowId = x.Id,
                Source = x.Source == "image" ? WorkflowSource.ImageExtracted : WorkflowSource.VideoExtracted,
                Name = x.Name ?? string.Format(_local["ImageExtractedWorkflowName"], x.WorkflowGuid?[..8]),
                UpdatedText = x.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                NodeCount = x.NodeCount,
                SortTime = x.CreatedAt,
                IsEmptyContent = x.IsEmptyContent,
            }).ToList();
            foreach (var item in imageItems)
                FillTags(item, imageWorkflows.First(w => w.Id == item.Id).Tags.Select(t => (t.Name, t.Color)));
            items.AddRange(imageItems);

            // 3) 统一按时间倒序
            items = items.OrderByDescending(x => x.SortTime).ToList();

            Workflows = new ObservableCollection<WorkflowItem>(items);
            StatusMessage = string.Format(_local["WorkflowCountSummary"], items.Count, imageWorkflows.Count);
            _log.Info(string.Format(_local["WorkflowListLoaded"], items.Count, imageWorkflows.Count), "Workflow");

            // 逐项异步加载缩略图（不阻塞列表渲染）
            foreach (var item in items)
            {
                try
                {
                    item.Thumbnail = await LoadThumbnailAsync(item, db);
                }
                catch (Exception ex)
                {
                    _log.Debug(string.Format(_local["LoadThumbnailFailedLog"], item.Name), "Workflow", ex);
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["LoadFailed"], ex.Message);
            _log.Error(_local["LoadWorkflowListFailed"], "Workflow", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RefreshWorkflowsAsync()
    {
        await LoadWorkflowsAsync();
    }

    /// <summary>填充卡片标签色块（颜色优先取保存的 Tags.Color，缺失回退名称哈希色，与图库一致）。</summary>
    private static void FillTags(WorkflowItem item, IEnumerable<(string Name, string? Color)> tags)
    {
        foreach (var (name, color) in tags)
            item.Tags.Add(new TagDisplay { Name = name, Color = TagPalette.GetBrush(color, name) });
    }

    /// <summary>加载全部标签到多选下拉（Tag 表与图库共用，标签池统一）。</summary>
    public async Task LoadTagsAsync()
    {
        try
        {
            var factory = _service.GetRequiredService<IDbContextFactory<ComfyDbContext>>();
            using var db = await factory.CreateDbContextAsync();
            var tags = await db.Tags.OrderBy(t => t.Name).ToListAsync();
            var items = tags.Select(t => new TagItem(t)).ToList();
            foreach (var item in items)
                item.PropertyChanged += OnTagSelectionChanged;
            AvailableTags = new ObservableCollection<TagItem>(items);
        }
        catch (Exception ex)
        {
            _log.Error(_local["LoadTagsFailed"], "Workflow", ex);
        }
    }

    private void OnTagSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TagItem.IsSelected))
        {
            OnPropertyChanged(nameof(SelectedTagsSummary));
            _ = LoadWorkflowsAsync();
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        _ = LoadWorkflowsAsync();
    }

    // ===== 标签管理抽屉（封装组件 TagManagerDrawer，新建/改名/删除统一实现）=====

    /// <summary>打开标签管理抽屉（组件）。</summary>
    [RelayCommand]
    private async Task OpenTagDrawerAsync() => await TagDrawer.OpenAsync();

    /// <summary>从配置的 ComfyUI 拉取工作流并保存到数据库。</summary>
    [RelayCommand]
    private async Task SyncFromComfyAsync()
    {
        if (IsSyncing) return;
        IsSyncing = true;
        SyncButtonText = _local["Syncing"];
        StatusMessage = _local["SyncingWorkflows"];
        _log.Info(_local["UserTriggeredSync"], "Workflow");
        try
        {
            var service = _service.GetRequiredService<IComfyUIService>();
            var result = await service.SyncWorkflowsFromComfyAsync();
            await LoadWorkflowsAsync();
            StatusMessage = string.Format(_local["SyncWorkflowsDone"], result.New, result.Updated) + (result.Errors > 0 ? string.Format(_local["SyncErrorsPart"], result.Errors) : "");
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["SyncFailedDetail"], ex.Message);
            _log.Error(_local["SyncWorkflowsFailed"], "Workflow", ex);
        }
        finally
        {
            IsSyncing = false;
            SyncButtonText = _local["SyncShort"];
        }
    }

    /// <summary>新建一个空白工作流并打开详情。</summary>
    [RelayCommand]
    private async Task AddWorkflowAsync()
    {
        try
        {
            var workflowRepo = _service.GetRequiredService<IWorkflowRepository>();
            var workflow = new Workflow
            {
                Name = string.Format(_local["NewWorkflowName"], DateTime.Now.ToString("MMddHHmmss")),
                WorkflowJsonBlob = ComfyMetadataCodec.Compress("{}"),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            await workflowRepo.AddAsync(workflow);
            _log.Info(string.Format(_local["WorkflowCreated"], workflow.Name, workflow.Id), "Workflow");
            await LoadWorkflowsAsync();
            var item = Workflows.FirstOrDefault(x => x.Id == workflow.Id);
            if (item != null) await OpenDetailAsync(item);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["CreateFailed"], ex.Message);
            _log.Error(_local["CreateWorkflowFailed"], "Workflow", ex);
        }
    }

    /// <summary>双击卡片打开详情弹窗（图片 + JSON 编辑 + 执行 + 历史 + 删除）。</summary>
    [RelayCommand]
    private async Task OpenDetailAsync(WorkflowItem? item)
    {
        if (item == null) return;
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } owner)
        {
            return;
        }

        try
        {
            var settings = _service.GetRequiredService<ComfySettings>();
            var dbFactory = _service.GetRequiredService<IDbContextFactory<ComfyDbContext>>();
            var detailVm = new WorkflowDetailModel(item, settings, dbFactory, _service, _local, _noticeService, _log);
            var viewService = _service.GetRequiredService<IBaseViewService>();
            if (!viewService.TryCreateView(detailVm, out var detailView, "WorkflowDetail"))
            {
                _log.Warn(_local["OpenDetailViewNotRegistered"], "Workflow");
                return;
            }

            var host = new SukiMessageBoxHost
            {
                Content = detailView,
                IconPreset = null,
                Width = 980,
                ActionButtonsPreset = SukiMessageBoxButtons.Close,
                // 底部操作区：删除（左，仅工作流内容为空时可用）+ 关闭（右）
                ActionButtonsSource = new AvaloniaList<Avalonia.Controls.Button>
                {
                    CreateDeleteButton(detailVm),
                    CreateCloseButton(),
                },
            };

            var options = new SukiMessageBoxOptions
            {
                Title = item.Name,
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
            // 详情页删除成功后关闭弹窗
            detailVm.RequestClose += () => window.Close();
            // 详情内修改（打标签/改封面/删除）→ 实时就地刷新列表中对应卡片，无需等关闭、不整表重载
            detailVm.WorkflowChanged += async () => await RefreshWorkflowItemAsync(item);
            // 底部"关闭"按钮点击关窗（删除按钮的 Click 已在 CreateDeleteButton 内绑定执行删除命令）
            if (host.ActionButtonsSource is { } buttons)
            {
                for (var i = 1; i < buttons.Count; i++)
                    buttons[i].Click += (_, _) => window.Close();
                buttons[^1].IsCancel = true;
            }
            window.KeyUp += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    window.Close();
                }
            };

            window.Opened += (_, _) =>
            {
                var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
                if (screen is null) return;
                var wa = screen.WorkingArea;
                var scale = screen.Scaling;
                var w = double.IsNaN(window.Width) ? 980 : window.Width;
                var h = double.IsNaN(window.Height) ? 800 : window.Height;
                window.Position = new PixelPoint(
                    wa.X + (int)((wa.Width - w * scale) / 2),
                    wa.Y + (int)(wa.Height * 0.08));
            };

            window.Closed += (_, _) => window.Content = null;

            await window.ShowDialog<object?>(owner);
            _log.Debug(string.Format(_local["CloseDetailLog"], item.Name), "Workflow");

            // 详情内修改已通过 WorkflowChanged 实时刷新卡片，关闭时无需再刷新
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["OpenDetailFailed"], ex.Message);
            _log.Error(string.Format(_local["OpenWorkflowDetailFailed"], item.Name), "Workflow", ex);
        }
    }

    /// <summary>底部"关闭"按钮（Esc/取消语义）。</summary>
    private static Avalonia.Controls.Button CreateCloseButton()
    {
        var button = SukiMessageBoxButtonsFactoryExtend.CreateButton(SukiMessageBoxResult.Close, Localizer.Instance?["Close"] ?? "");
        button.IsCancel = true;
        return button;
    }

    /// <summary>
    /// 底部"删除"按钮：位于关闭按钮左侧，仅当详情内工作流 JSON 为空（CanDelete）时显示；
    /// 工作流有内容时不显示该按钮。点击执行详情页 DeleteCommand（含确认框、依赖数据逻辑删除、ComfyUI 远端清理）。
    /// </summary>
    private static Avalonia.Controls.Button CreateDeleteButton(WorkflowDetailModel vm)
    {
        var button = SukiMessageBoxButtonsFactoryExtend.CreateButton(SukiMessageBoxResult.Close, Localizer.Instance?["Delete"] ?? "");
        button.Click += async (_, _) => await vm.DeleteCommand.ExecuteAsync(null);
        // 初始隐藏（详情 JSON 未加载完前不显示），CanDelete 变为 true（内容为空）时才显示
        button.IsVisible = false;
        // 危险操作红色样式
        button.Background = new Avalonia.Media.SolidColorBrush(Color.Parse("#E81123"));
        button.Foreground = Avalonia.Media.Brushes.White;
        button.BorderBrush = new Avalonia.Media.SolidColorBrush(Color.Parse("#E81123"));
        button.Padding = new Avalonia.Thickness(16, 4);
        // 详情加载出 JSON 后 CanDelete 变化，实时更新显示/隐藏
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkflowDetailModel.CanDelete))
                button.IsVisible = vm.CanDelete;
        };
        return button;
    }

    /// <summary>
    /// 详情页关闭后：只就地刷新被修改的工作流卡片（标签/更新时间/封面），不整表重载。
    /// 若详情页删除了工作流，则从列表移除该卡片。
    /// </summary>
    private async Task RefreshWorkflowItemAsync(WorkflowItem item)
    {
        try
        {
            using var db = _service.GetRequiredService<IDbContextFactory<ComfyDbContext>>().CreateDbContext();
            var wf = await db.Workflows.AsNoTracking()
                .Where(x => x.Id == item.Id)
                .Select(x => new { x.IsDeleted, x.UpdatedAt, Tags = x.WorkflowTags.Select(wt => new { wt.Tag.Name, wt.Tag.Color }).ToList() })
                .FirstOrDefaultAsync();
            if (wf == null || wf.IsDeleted)
            {
                Workflows.Remove(item);
                StatusMessage = _local["WorkflowDeleted"];
                _log.Info(string.Format(_local["DetailDeletedRemoveCard"], item.Name), "Workflow");
                return;
            }

            item.Tags.Clear();
            foreach (var t in wf.Tags)
                item.Tags.Add(new TagDisplay { Name = t.Name, Color = TagPalette.GetBrush(t.Color, t.Name) });
            item.UpdatedText = wf.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            item.Thumbnail = await LoadThumbnailAsync(item, db);
            _log.Debug(string.Format(_local["RefreshCardAfterClose"], item.Name), "Workflow");
        }
        catch (Exception ex)
        {
            _log.Error(string.Format(_local["RefreshCardFailed"], item.Name), "Workflow", ex);
        }
    }

    /// <summary>
    /// 缩略图回退链：
    /// - Workflows 表（ComfyUI/本地）：手动设置 → 同款工作流(哈希)生成的第一张图 → 任务输出第一张图 → 默认图。
    /// - 图片提取：手动封面 → 自身关联的第一张图 → 默认图。
    /// </summary>
        private async Task<Bitmap?> LoadThumbnailAsync(WorkflowItem item, ComfyDbContext db)
    {
        var settings = _service.GetRequiredService<ComfySettings>();

        // ---- 图片提取工作流：手动封面 → 自身第一张图 → 占位图（只投影轻量列，不拉 WorkflowJsonBlob）----
        if (item.Source == WorkflowSource.ImageExtracted || item.Source == WorkflowSource.VideoExtracted)
        {
            var thumbPath = await db.Workflows.AsNoTracking()
                .Where(x => x.Id == item.Id && (x.Source == "image" || x.Source == "video"))
                .Select(x => x.ThumbnailPath)
                .FirstOrDefaultAsync();
            if (!string.IsNullOrEmpty(thumbPath) && File.Exists(thumbPath))
                return await LoadBitmapAsync(thumbPath);

            var rel = await db.Workflows.AsNoTracking()
                .Where(x => x.Id == item.Id && (x.Source == "image" || x.Source == "video"))
                .SelectMany(x => x.Images)
                .OrderBy(x => x.CreatedAt)
                .Select(x => x.RelativePath)
                .FirstOrDefaultAsync();
            if (rel != null)
            {
                var bmp = await TryLoadProductBitmapAsync(settings, rel);
                if (bmp != null) return bmp;
            }
            return LoadPlaceholderBitmap();
        }

        // ---- Workflows 表（ComfyUI / 本地）----
        // 1) 用户手动设置（只投影 ThumbnailPath，不拉 WorkflowJsonBlob）
        var thumbPath2 = await db.Workflows.AsNoTracking()
            .Where(x => x.Id == item.Id)
            .Select(x => x.ThumbnailPath)
            .FirstOrDefaultAsync();
        if (!string.IsNullOrEmpty(thumbPath2) && File.Exists(thumbPath2))
            return await LoadBitmapAsync(thumbPath2);

        // 2) 任务输出第一张图（不需要 JSON，先查；多数情况在此命中）
        try
        {
            var rel = await db.JobOutputs.AsNoTracking()
                .Where(x => x.Job.WorkflowId == item.Id && !x.IsDeleted && x.ImageInfoId != null)
                .OrderBy(x => x.Job.CreatedAt)
                .Select(x => x.Image!.RelativePath)
                .FirstOrDefaultAsync();
            if (rel != null)
            {
                var bmp = await TryLoadProductBitmapAsync(settings, rel);
                if (bmp != null) return bmp;
            }
        }
        catch (Exception ex)
        {
            _log.Debug(_local["ThumbMatchJobFailed"], "Workflow", ex);
        }

        // 3) GUID 匹配：用户工作流 JSON 顶级 "id"（ComfyUI 工作流 UUID）== 图片提取工作流的 WorkflowGuid
        //    （仅走到这一步才拉取 Blob 单列，解压后提取 GUID）
        try
        {
            var blob = await db.Workflows.AsNoTracking()
                .Where(x => x.Id == item.Id)
                .Select(x => x.WorkflowJsonBlob)
                .FirstOrDefaultAsync();
            var json = ComfyMetadataCodec.Decompress(blob);
            if (string.IsNullOrEmpty(json)) return LoadPlaceholderBitmap();

            var wfGuid = ExtractWorkflowGuid(json);
            if (string.IsNullOrEmpty(wfGuid)) return LoadPlaceholderBitmap();

            var rel = await db.Workflows.AsNoTracking()
                .Where(x => x.WorkflowGuid == wfGuid && (x.Source == "image" || x.Source == "video") && !x.IsDeleted)
                .SelectMany(x => x.Images)
                .OrderBy(x => x.CreatedAt)
                .Select(x => x.RelativePath)
                .FirstOrDefaultAsync();
            if (rel != null)
            {
                var bmp = await TryLoadProductBitmapAsync(settings, rel);
                if (bmp != null) return bmp;
            }
        }
        catch (Exception ex)
        {
            _log.Debug(_local["ThumbMatchGuidFailed"], "Workflow", ex);
        }

        // 4) 默认占位图
        return LoadPlaceholderBitmap();
    }

    /// <summary>加载产物预览图：产物是图片直接加载原文件；是视频（缩略图为 jpg）加载其首帧封面，
    /// 避免 Skia 直接解码视频抛 "Unable to load bitmap"。加载失败返回 null（由调用方回退）。</summary>
    private async Task<Bitmap?> TryLoadProductBitmapAsync(ComfySettings settings, string relativePath)
    {
        var path = Path.Combine(settings.ComfyOutputDir, relativePath);
        if (!File.Exists(path)) return null;
        if (PromptCraft.Service.ComfyMediaKinds.IsVideo(path))
        {
            var thumb = Path.Combine(settings.ThumbDir,
                PromptCraft.Service.ImageSyncService.GetThumbFileName(relativePath, settings.ThumbMaxDimension));
            if (File.Exists(thumb)) return await LoadBitmapAsync(thumb);
            return null;
        }
        return await LoadBitmapAsync(path);
    }

    /// <summary>从工作流 JSON 提取顶级 "id"（ComfyUI 工作流 UUID）；无则返回 null</summary>
    private static string? ExtractWorkflowGuid(string workflowJson)
    {
        try
        {
            using var doc = global::System.Text.Json.JsonDocument.Parse(workflowJson);
            if (doc.RootElement.ValueKind != global::System.Text.Json.JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("id", out var id) || id.ValueKind != global::System.Text.Json.JsonValueKind.String) return null;
            var s = id.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch
        {
            return null;
        }
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

    public override void OnSystemLangueChanged(object? data)
    {
        OnPropertyChanged(nameof(SelectedTagsSummary));
        if (!IsSyncing) SyncButtonText = _local["SyncShort"];
    }

    public async Task OnLoadedAsync()
    {
        await LoadWorkflowsAsync();
    }
}
