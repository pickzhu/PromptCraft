using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.BaseModel;
using PromptCraft.Data;
using PromptCraft.Interfaces;
using PromptCraft.Models.ComfyUI;
using PromptCraft.Service;
using PromptCraft.Utils;
using Material.Icons;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SukiUI.Controls;
using SukiUI.MessageBox;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.ComfyUI;

/// <summary>筛选模式</summary>
public enum FilterMode
{
    All,
    Favorites,
    NSFW,
}

/// <summary>可选标签项（用于多选下拉）</summary>
public partial class TagItem : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isSelected;
    public int Id { get; set; }

    /// <summary>标签颜色（保存的 Tags.Color，缺失时回退名称哈希色）</summary>
    public IBrush Color { get; set; } = Brushes.Transparent;

    public TagItem() { }
    public TagItem(Tag tag) { Id = tag.Id; Name = tag.Name; Color = TagPalette.GetBrush(tag.Color, tag.Name); }
}

/// <summary>标签显示（名称 + 固定颜色）</summary>
public partial class TagDisplay : ObservableObject
{
    public string Name { get; set; } = "";
    public IBrush Color { get; set; } = Brushes.Transparent;
}

/// <summary>筛选模式下拉项</summary>
public partial class FilterModeItem : ObservableObject
{
    public string Display { get; set; } = "";
    public FilterMode Mode { get; set; }
}

public partial class ComfyGalleryModel : ViewModelBase
{
    private readonly IServiceProvider _service;
    private readonly IBaseLogService _log;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isSyncing;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private ObservableCollection<ImageItem> _images = new();
    [ObservableProperty] private FilterMode _currentFilterMode = FilterMode.All;
    [ObservableProperty] private int _filterModeIndex;
    [ObservableProperty] private ObservableCollection<TagItem> _availableTags = new();

    /// <summary>标签管理抽屉组件（封装新建/重命名/删除，变更后自动刷新本页下拉）</summary>
    public TagManagerDrawerModel TagDrawer { get; }

    /// <summary>筛选模式下拉选项</summary>
    public FilterModeItem[] FilterModes { get; private set; } = [];

    /// <summary>当前筛选模式显示文本</summary>
    public string FilterModeDisplay => FilterModes.FirstOrDefault(m => m.Mode == CurrentFilterMode)?.Display ?? _local["FilterAll"];

    /// <summary>已选标签摘要文本</summary>
    public string SelectedTagsSummary
    {
        get
        {
            var selected = AvailableTags.Where(t => t?.IsSelected ?? false)?.Select(t => (t?.Name ?? ""))?.ToList();
            return selected?.Count == 0 ? _local["AllTags"]
                 : selected?.Count == 1 ? selected[0]
                 : $"{selected?[0]} +{selected?.Count - 1}";
        }
    }

    // 瀑布流内部状态
    private int _pageIndex;
    private int _pageSize = 90;
    private int _totalCount;
    private string _currentFilterText = "";

    public bool HasMoreItems => (_pageIndex + 1) * _pageSize < _totalCount;

    public ComfyGalleryModel(IServiceProvider service, IBaseLogService log) : base(
        service.GetRequiredService<Ke.Bee.Localization.Localizer.Abstractions.ILocalizer>(),
        service.GetRequiredService<PromptCraft.Interfaces.IBaseNotice>())
    {
        _service = service;
        _log = log;
        _displayName = "COMFIGALLERY";
        _icon = MaterialIconKind.Image;
        _index = 10;
        _sideMenu = true;
        TagDrawer = new TagManagerDrawerModel(
            _service.GetRequiredService<IDbContextFactory<ComfyDbContext>>(),
            _service.GetRequiredService<ITagRepository>(),
            _local, _noticeService, _log);
        // 抽屉内标签变更后刷新本页标签下拉（选中状态重置，与旧抽屉行为一致）
        TagDrawer.TagsChanged += () => _ = LoadTagsAsync();
        FilterModes =
        [
            new() { Display = _local["FilterAll"], Mode = FilterMode.All },
            new() { Display = _local["FilterFavorites"], Mode = FilterMode.Favorites },
            new() { Display = _local["FilterNsfw"], Mode = FilterMode.NSFW },
        ];
        _ = LoadTagsAsync();
        _noticeService.Subscribe(PromptCraft.Consts.Event.EventNameConst.PromptLibraryChangedEvent, OnLibraryChanged);
    }

    /// <summary>资产/文件夹变更后整体重载（文件夹浏览统一走文件夹管理页，本页不再有文件夹筛选）。</summary>
    private async void OnLibraryChanged(object? _)
    {
        _pageIndex = 0;
        Images.Clear();
        await LoadImagesAsync(isReset: true);
    }

    public override void Disposed()
    {
        _noticeService.Unsubscribe(PromptCraft.Consts.Event.EventNameConst.PromptLibraryChangedEvent, OnLibraryChanged);
        base.Disposed();
    }

    /// <summary>语言切换时刷新显示文字</summary>
    public override void OnSystemLangueChanged(object? data)
    {
        FilterModes =
        [
            new() { Display = _local["FilterAll"], Mode = FilterMode.All },
            new() { Display = _local["FilterFavorites"], Mode = FilterMode.Favorites },
            new() { Display = _local["FilterNsfw"], Mode = FilterMode.NSFW },
        ];
        OnPropertyChanged(nameof(FilterModeDisplay));
        OnPropertyChanged(nameof(FilterModes));
        OnPropertyChanged(nameof(SelectedTagsSummary));
        _ = LoadTagsAsync();
    }

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
            _log.Error(_local["LoadTagsFailed"], "Gallery", ex);
        }
    }

    private void OnTagSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TagItem.IsSelected))
        {
            OnPropertyChanged(nameof(SelectedTagsSummary));
            _pageIndex = 0;
            Images.Clear();
            _ = LoadImagesAsync(isReset: true);
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        _currentFilterText = value ?? "";
        _pageIndex = 0;
        _images.Clear();
        _ = LoadImagesAsync(isReset: true);
    }

    partial void OnCurrentFilterModeChanged(FilterMode value)
    {
        OnPropertyChanged(nameof(FilterModeDisplay));
        FilterModeIndex = (int)value;
        _pageIndex = 0;
        _images.Clear();
        _ = LoadImagesAsync(isReset: true);
    }

    partial void OnFilterModeIndexChanged(int value)
    {
        if (value >= 0 && value <= 2 && (FilterMode)value != CurrentFilterMode)
            CurrentFilterMode = (FilterMode)value;
    }

    [RelayCommand]
    private async Task SyncImagesAsync()
    {
        IsSyncing = true;
        StatusMessage = _local["Syncing"];
        _log.Info(_local["SyncStarted"], "Gallery");
        try
        {
            var syncService = _service.GetRequiredService<IImageSyncService>();
            var result = await syncService.SyncAsync();
            StatusMessage = string.Format(_local["SyncCompleted"], result.NewFiles, result.UpdatedFiles, result.DeletedFiles);
            if (result.SkippedFiles > 0)
                StatusMessage += string.Format(_local["SyncSkipped"], result.SkippedFiles);
            if (result.Errors > 0)
                StatusMessage += string.Format(_local["SyncErrors"], result.Errors);
            _log.Info(string.Format(_local["ImageSyncDoneLog"], StatusMessage), "Gallery");

            _pageIndex = 0;
            Images.Clear();
            _totalCount = 0;
            await LoadImagesAsync(isReset: true);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["SyncFailed"], ex.Message);
            _log.Error(_local["ImageSyncFailed"], "Gallery", ex);
        }
        finally
        {
            IsSyncing = false;
        }
    }

    /// <summary>加载第一页（页面打开时调用）</summary>
    [RelayCommand]
    private async Task LoadImagesAsync()
    {
        if (IsLoading) return;
        _pageIndex = 0;
        Images.Clear();
        await LoadImagesAsync(isReset: true);
    }

    /// <summary>加载更多（滚动到底部时调用）</summary>
    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (IsLoading || !HasMoreItems) return;
        _pageIndex++;
        await LoadImagesAsync(isReset: false);
    }

    private async Task LoadImagesAsync(bool isReset)
    {
        IsLoading = true;
        try
        {
            var factory = _service.GetRequiredService<IDbContextFactory<ComfyDbContext>>();
            using var db = await factory.CreateDbContextAsync();
            // 确保表存在且 schema 已升级（老库补列/补建 ImageWorkflows、ImagePrompts 表）
            await ComfyDbMigrator.EnsureSchemaAsync(db);

            IQueryable<ImageInfo> query = db.ImageMetadata
                .Include(x => x.Status)
                .Include(x => x.ImageTags)
                    .ThenInclude(x => x.Tag);

            // 搜索文本：匹配文件名或标签名
            if (!string.IsNullOrWhiteSpace(_currentFilterText))
            {
                var search = _currentFilterText.Trim();
                query = query.Where(x =>
                    x.FileName.Contains(search) ||
                    x.ImageTags.Any(t => t.Tag.Name.Contains(search)) ||
                    (x.Hash != null && x.Hash.Contains(search)));
            }

            // NSFW 默认隐藏：只有切换到 NSFW 筛选才显示被标记的图片（All/Favorites 一律排除）
            if (CurrentFilterMode != FilterMode.NSFW)
                query = query.Where(x => x.Status == null || !x.Status.IsNsfw);

            // 筛选模式
            switch (CurrentFilterMode)
            {
                case FilterMode.Favorites:
                    query = query.Where(x => x.Status != null && x.Status.IsFavorite);
                    break;
                case FilterMode.NSFW:
                    query = query.Where(x => x.Status != null && x.Status.IsNsfw);
                    break;
            }

            // 多标签筛选
            var selectedTagNames = AvailableTags.Where(t => t.IsSelected).Select(t => t.Name).ToList();
            if (selectedTagNames.Count > 0)
            {
                query = query.Where(x => x.ImageTags.Any(it => selectedTagNames.Contains(it.Tag.Name)));
            }

            // 总数（仅第一页需要）
            if (isReset)
                _totalCount = await query.CountAsync();

            // 分页
            var images = await query
                .OrderByDescending(x => x.CreatedAt)
                .Skip(_pageIndex * _pageSize)
                .Take(_pageSize)
                .ToListAsync();

            var settings = _service.GetRequiredService<ComfySettings>();
            foreach (var img in images)
            {
                var thumbPath = GetThumbnailPath(settings, img);
                var item = new ImageItem
                {
                    ImageInfoId = img.Id,
                    FileName = img.FileName,
                    FileSize = img.FileSize,
                    Width = img.Width,
                    Height = img.Height,
                    IsFavorite = img.Status?.IsFavorite ?? false,
                    IsNsfw = img.Status?.IsNsfw ?? false,
                    Rating = img.Status?.Rating ?? 0,
                    RelativePath = img.RelativePath,
                    OutputDir = settings.ComfyOutputDir,
                    Hash = img.Hash,
                    CreatedAt = img.CreatedAt,
                    Tags = new ObservableCollection<TagDisplay>(
                        img.ImageTags?.Select(it => new TagDisplay
                        {
                            Name = it.Tag.Name,
                            Color = TagPalette.GetBrush(it.Tag.Color, it.Tag.Name)
                        }) ?? Enumerable.Empty<TagDisplay>()),
                };

                if (!string.IsNullOrEmpty(thumbPath) && File.Exists(thumbPath))
                {
                    try { item.Thumbnail = new Bitmap(thumbPath); }
                    catch (Exception ex) { _log.Debug(string.Format(_local["LoadThumbnailFailed"], thumbPath), "Gallery", ex); }
                }

                Images.Add(item);
            }

            OnPropertyChanged(nameof(HasMoreItems));
            var loaded = isReset ? images.Count : Images.Count;
            StatusMessage = string.Format(_local["ImagesLoaded"], loaded, _totalCount);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["LoadFailed"], ex.Message);
            _log.Error(_local["LoadImageListFailed"], "Gallery", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string GetThumbnailPath(ComfySettings settings, ImageInfo img)
    {
        var thumbDir = settings.ThumbDir;
        // 优先新命名（相对路径哈希）：不同目录同名文件的缩略图互不覆盖
        var newPath = Path.Combine(thumbDir, ImageSyncService.GetThumbFileName(img.RelativePath, settings.ThumbMaxDimension));
        if (File.Exists(newPath)) return newPath;

        // 兼容旧命名（尚未重新同步时立即恢复显示；同步时迁移会生成新命名并逐步替换）
        var legacyName = $"{Path.GetFileNameWithoutExtension(img.FileName)}_{settings.ThumbMaxDimension}.jpg";
        var legacyPath = Path.Combine(thumbDir, legacyName);
        return File.Exists(legacyPath) ? legacyPath : newPath;
    }

    [RelayCommand]
    private async Task SearchAsync() { _currentFilterText = SearchText ?? ""; _pageIndex = 0; Images.Clear(); await LoadImagesAsync(isReset: true); }

    /// <summary>打开图片详情预览</summary>
    [RelayCommand]
    private async Task OpenDetailAsync(ImageItem? item)
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
            var detailVm = new ImageDetailModel(item, settings, dbFactory, _service, _local, _noticeService, _log);
            var viewService = _service.GetRequiredService<IBaseViewService>();
            if (!viewService.TryCreateView(detailVm, out var detailView, "ImageDetail"))
            {
                return;
            }

            var host = new SukiMessageBoxHost
            {
                Content = detailView,
                IconPreset = null,
                Width = 900,
                ActionButtonsPreset = SukiMessageBoxButtons.Close,
            };

            // 继承主窗口的背景设置，保持弹窗风格一致
            var options = new SukiMessageBoxOptions
            {
                Title = item.FileName,
                // 手动定位（不用 CenterOwner 居中），由下方 Opened 事件控制位置
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

            // 详情页删除成功后：关闭弹窗并刷新图库列表
            detailVm.Deleted += () =>
            {
                window.Close();
                _pageIndex = 0;
                Images.Clear();
                _ = LoadImagesAsync(isReset: true);
            };

            // 详情页数据变更（标签修改）后：局部刷新该列表项，无需重开详情即可看到新标签
            detailVm.DataChanged += () => { _ = RefreshItemTagsAsync(item.ImageInfoId); };

            // 关闭按钮 / Esc 关闭
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
                if (e.Key == Key.Escape)
                {
                    window.Close();
                }
            };

            // 定位：水平居中，弹窗顶部放在屏幕约 12% 高度处（搜索框下方一点）
            window.Opened += (_, _) =>
            {
                // 用主窗口的屏幕信息定位（弹窗刚显示时自身 Screens 可能尚未就绪）
                var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
                if (screen is null) return;
                var wa = screen.WorkingArea;
                // window.Width/Height 是逻辑像素，需按 DPI 缩放换算成物理像素，才能与 WorkingArea 直接运算
                var scale = screen.Scaling;
                var w = double.IsNaN(window.Width) ? 900 : window.Width;
                var h = double.IsNaN(window.Height) ? 800 : window.Height;
                window.Position = new PixelPoint(
                    wa.X + (int)((wa.Width - w * scale) / 2),
                    wa.Y + (int)(wa.Height * 0.12));
            };

            window.Closed += (_, _) => window.Content = null;

            await window.ShowDialog<object?>(owner);
            _log.Debug(string.Format(_local["OpenImageDetailDone"], item.FileName), "Gallery");
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["PreviewFailed"], ex.Message);
            _log.Error(string.Format(_local["OpenImageDetailFailed"], item.FileName), "Gallery", ex);
        }
    }

    #region ComfyUI API 调用

    /// <summary>
    /// 获取队列状态
    /// </summary>
    [RelayCommand]
    private async Task RefreshQueueStatusAsync()
    {
        try
        {
            var client = _service.GetRequiredService<IComfyUIService>();
            var status = await client.GetQueueStatusAsync();
            if (status != null)
            {
                StatusMessage = string.Format(_local["QueueStatus"], status.Pending.Count, status.Running.Count);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["QueueRefreshFailed"], ex.Message);
            _log.Warn(_local["RefreshQueueFailed"], "Gallery", ex);
        }
    }

    /// <summary>
    /// 获取节点信息
    /// </summary>
    [RelayCommand]
    private async Task RefreshNodeInfoAsync()
    {
        try
        {
            var client = _service.GetRequiredService<IComfyUIService>();
            var nodeInfo = await client.GetObjectInfoAsync();
            if (nodeInfo != null)
            {
                StatusMessage = string.Format(_local["NodesLoaded"], nodeInfo.Nodes.Count);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["NodeInfoRefreshFailed"], ex.Message);
            _log.Warn(_local["RefreshNodeInfoFailed"], "Gallery", ex);
        }
    }

    /// <summary>已选中图片 ID 列表（用于批量操作）</summary>
    public int[] SelectedImageIds => Images.Where(i => i.IsSelected).Select(i => i.ImageInfoId).ToArray();
    public bool HasSelection => Images.Any(i => i.IsSelected);

    /// <summary>恰好选中两张图片（对比功能的可用条件）</summary>
    public bool HasTwoSelection => Images.Count(i => i.IsSelected) == 2;

    /// <summary>选中数量摘要（工具栏实时显示）</summary>
    public string SelectionSummary => HasSelection ? string.Format(_local["SelectedCount"], Images.Count(i => i.IsSelected)) : "";

    /// <summary>选中状态变化后刷新对比按钮/菜单/数量提示（由图片点击处理调用）</summary>
    public void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasTwoSelection));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    /// <summary>批量删除选中的图片（删除逻辑与单张删除一致，弹窗确认后执行）</summary>
    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var selected = Images.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;

        // 弹窗确认：删除不可恢复
        var confirm = await SukiMessageBox.ShowDialog(
            new SukiMessageBoxHost
            {
                Content = new TextBlock
                {
                    Text = string.Format(_local["ConfirmDeleteSelected"], selected.Count),
                    Margin = new Thickness(4),
                    TextWrapping = TextWrapping.Wrap,
                },
                ActionButtonsPreset = SukiMessageBoxButtons.OKCancel,
            },
            new SukiMessageBoxOptions { Title = _local["ConfirmDelete"], MinWidth = 360 });
        if (!(confirm is SukiMessageBoxResult r && r.Equals(SukiMessageBoxResult.OK))) return;

        try
        {
            var syncService = _service.GetRequiredService<IImageSyncService>();
            var result = await syncService.DeleteImagesAsync(selected.Select(i => i.ImageInfoId).ToArray());
            StatusMessage = result.Failed > 0
                ? string.Format(_local["DeleteResultWithFailed"], result.Deleted, result.Failed)
                : string.Format(_local["DeleteResult"], result.Deleted);
            if (result.Failed > 0)
                _log.Warn(string.Format(_local["BatchDeleteImagePartialFailed"], string.Join("; ", result.Errors)), "Gallery");

            // 刷新列表
            _pageIndex = 0;
            Images.Clear();
            _totalCount = 0;
            await LoadImagesAsync(isReset: true);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["DeleteImagesFailed"], ex.Message);
            _log.Error(_local["BatchDeleteImagesFailed"], "Gallery", ex);
        }
    }

    /// <summary>右键菜单：把选中的图片加入所选文件夹（多对多，多选时可批量）。</summary>
    [RelayCommand]
    private async Task AddToFolderAsync()
    {
        var selected = Images.Where(i => i.IsSelected).Select(i => i.ImageInfoId).ToArray();
        if (selected.Length == 0) return;
        var picked = await FolderPickerDialog.PickAsync(FolderScopes.Gallery, null, _service);
        if (picked == null) return;
        if (picked.Count == 0)
        {
            ShowToast(_local["AddToFolderNone"] ?? "", "");
            return;
        }
        try
        {
            var folderService = _service.GetRequiredService<IFolderService>();
            await folderService.AddToFoldersAsync(FolderScopes.Gallery,
                selected.Select(id => id.ToString()).ToList(), picked);
            ShowToast(string.Format(_local["AddToFolderDone"] ?? "", selected.Length, picked.Count), "");
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["FolderSaveFailed"] ?? "", ex.Message);
            _log.Error(string.Format(_local["FolderSaveFailed"] ?? "", ex.Message), "Gallery", ex);
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
            _log.Error("Toast failed", "Gallery", ex);
        }
    }

    #region 右键菜单（收藏/NSFW/打标签/打开文件夹/复制路径）

    /// <summary>右键菜单：收藏项文案（按选中图片状态切换 收藏/取消收藏）</summary>
    public string ContextFavoriteText { get; private set; } = "";

    /// <summary>右键菜单：NSFW 项文案（按选中图片状态切换 标记/取消）</summary>
    public string ContextNsfwText { get; private set; } = "";

    /// <summary>右键菜单打开前刷新动态文案（由 View 在右键按下时调用）</summary>
    public void RefreshContextMenuState()
    {
        var selected = Images.Where(i => i.IsSelected).ToList();
        ContextFavoriteText = selected.Count > 0 && selected.All(i => i.IsFavorite) ? _local["Unfavorite"] : _local["Favorite"];
        ContextNsfwText = selected.Count > 0 && selected.All(i => i.IsNsfw) ? _local["UnmarkNsfw"] : _local["MarkAsNsfw"];
        OnPropertyChanged(nameof(ContextFavoriteText));
        OnPropertyChanged(nameof(ContextNsfwText));
        NotifySelectionChanged();
    }

    /// <summary>切换收藏：全部已收藏则取消收藏，否则全部设为收藏（右键菜单入口）</summary>
    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        var selected = Images.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;
        var target = !selected.All(i => i.IsFavorite);
        if (!await SetStatusAsync(selected.Select(i => i.ImageInfoId).ToArray(), fav: target, nsfw: null)) return;
        ApplyStatusToItems(selected, fav: target, nsfw: null);
        StatusMessage = target ? string.Format(_local["FavoriteCount"], selected.Count) : string.Format(_local["UnfavoriteCount"], selected.Count);
        RefreshContextMenuState();
        _log.Info(string.Format(_local["FavoriteToggleLog"], target ? _local["Favorite"] : _local["Unfavorite"], selected.Count), "Gallery");
    }

    /// <summary>切换 NSFW：全部已标记则取消，否则全部标记（右键菜单入口）</summary>
    [RelayCommand]
    private async Task ToggleNsfwAsync()
    {
        var selected = Images.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;
        var target = !selected.All(i => i.IsNsfw);
        if (!await SetStatusAsync(selected.Select(i => i.ImageInfoId).ToArray(), fav: null, nsfw: target)) return;
        ApplyStatusToItems(selected, fav: null, nsfw: target);
        StatusMessage = target ? string.Format(_local["NsfwMarked"], selected.Count) : string.Format(_local["NsfwUnmarked"], selected.Count);
        RefreshContextMenuState();
        _log.Info(string.Format(_local["NsfwToggleLog"], target ? _local["Mark"] : _local["Unmark"], selected.Count), "Gallery");
    }

    /// <summary>批量写 ImageStatus（缺记录自动补建，老库图片也能切换；返回是否成功）</summary>
    private async Task<bool> SetStatusAsync(int[] imageIds, bool? fav, bool? nsfw)
    {
        try
        {
            using var db = await DbFactory.CreateDbContextAsync();
            var statuses = await db.ImageStatuses.Where(s => imageIds.Contains(s.ImageInfoId)).ToListAsync();
            // 正常同步入库必有 Status；老库可能缺失，补建避免切换无效
            var existingIds = statuses.Select(s => s.ImageInfoId).ToHashSet();
            foreach (var id in imageIds.Where(id => !existingIds.Contains(id)))
            {
                db.ImageStatuses.Add(new ImageStatus
                {
                    ImageInfoId = id,
                    IsFavorite = fav ?? false,
                    IsNsfw = nsfw ?? false,
                    UpdatedAt = DateTime.UtcNow,
                });
            }
            foreach (var s in statuses)
            {
                if (fav.HasValue) s.IsFavorite = fav.Value;
                if (nsfw.HasValue) s.IsNsfw = nsfw.Value;
                s.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["StatusSaveFailed"], ex.Message);
            _log.Error(_local["BatchUpdateStatusFailed"], "Gallery", ex);
            return false;
        }
    }

    /// <summary>同步列表项的收藏/NSFW 显示，并把当前筛选模式下不再可见的项移出列表（如 All 下标记 NSFW）</summary>
    private void ApplyStatusToItems(List<ImageItem> selected, bool? fav, bool? nsfw)
    {
        foreach (var item in selected)
        {
            if (fav.HasValue) item.IsFavorite = fav.Value;
            if (nsfw.HasValue) item.IsNsfw = nsfw.Value;
            // 与 LoadImagesAsync 的查询过滤保持一致：All/Favorites 隐藏 NSFW，Favorites 只看收藏，NSFW 只看标记
            var stillVisible = CurrentFilterMode switch
            {
                FilterMode.Favorites => item.IsFavorite && !item.IsNsfw,
                FilterMode.NSFW => item.IsNsfw,
                _ => !item.IsNsfw,
            };
            if (!stillVisible)
            {
                Images.Remove(item);
                _totalCount = Math.Max(0, _totalCount - 1);
            }
        }
        OnPropertyChanged(nameof(HasMoreItems));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    /// <summary>在资源管理器中定位选中图片（取第一张，Explorer /select）</summary>
    [RelayCommand]
    private void OpenSelectedFolder()
    {
        var first = Images.FirstOrDefault(i => i.IsSelected);
        if (first == null || string.IsNullOrEmpty(first.FullPath)) return;
        try
        {
            var dir = Path.GetDirectoryName(first.FullPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{first.FullPath}\"") { UseShellExecute = true });
            _log.Info(string.Format(_local["OpenFolderLog"], dir), "Gallery");
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["OpenFolderFailed"], ex.Message);
            _log.Warn(_local["OpenFolderFailedLog"], "Gallery", ex);
        }
    }

    /// <summary>复制选中图片的完整路径（多张以换行分隔）</summary>
    [RelayCommand]
    private void CopySelectedPaths()
    {
        var paths = Images.Where(i => i.IsSelected).Select(i => i.FullPath).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (paths.Count == 0) return;
        try
        {
            // 复用项目统一剪贴板服务（Avalonia 12 IClipboard 无 SetTextAsync，由服务封装 SetValueAsync）
            _service.GetRequiredService<IBaseClipboardService>().CopyToClipboard(string.Join('\n', paths));
            StatusMessage = string.Format(_local["PathsCopied"], paths.Count);
            _log.Info(string.Format(_local["CopyPathsLog"], paths.Count), "Gallery");
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["CopyPathsFailed"], ex.Message);
            _log.Warn(_local["CopyPathsFailedLog"], "Gallery", ex);
        }
    }

    #endregion

    /// <summary>打开两张图片的对比弹窗（顶部按钮与右键菜单共用入口）</summary>
    [RelayCommand]
    private async Task CompareSelectedAsync()
    {
        var selected = Images.Where(i => i.IsSelected).ToList();
        if (selected.Count != 2)
        {
            StatusMessage = selected.Count == 0 ? _local["SelectTwoFirst"] : string.Format(_local["SelectExactlyTwo"], selected.Count);
            return;
        }

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } owner)
        {
            return;
        }

        try
        {
            var settings = _service.GetRequiredService<ComfySettings>();
            var dbFactory = _service.GetRequiredService<IDbContextFactory<ComfyDbContext>>();
            var compareVm = new ImageCompareModel(selected[0], selected[1], settings, dbFactory, _local, _noticeService, _log);
            var viewService = _service.GetRequiredService<IBaseViewService>();
            if (!viewService.TryCreateView(compareVm, out var compareView, "ImageCompare"))
            {
                return;
            }

            var host = new SukiMessageBoxHost
            {
                Content = compareView,
                IconPreset = null,
                Width = 1000,
                Height = 820,
                ActionButtonsPreset = SukiMessageBoxButtons.Close,
            };

            // 继承主窗口的背景设置，保持弹窗风格一致
            var options = new SukiMessageBoxOptions
            {
                Title = string.Format(_local["CompareTitle"], selected[0].FileName, selected[1].FileName),
                // 手动定位（不用 CenterOwner 居中），由下方 Opened 事件控制位置
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

            // 关闭按钮 / Esc 关闭
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
                if (e.Key == Key.Escape)
                {
                    window.Close();
                }
            };

            // 定位：水平居中，弹窗顶部放在屏幕约 8% 高度处
            window.Opened += (_, _) =>
            {
                var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
                if (screen is null) return;
                var wa = screen.WorkingArea;
                var scale = screen.Scaling;
                var w = double.IsNaN(window.Width) ? 1000 : window.Width;
                var h = double.IsNaN(window.Height) ? 820 : window.Height;
                window.Position = new PixelPoint(
                    wa.X + (int)((wa.Width - w * scale) / 2),
                    wa.Y + (int)(wa.Height * 0.08));
            };

            window.Closed += (_, _) => window.Content = null;

            await window.ShowDialog<object?>(owner);
            _log.Debug(string.Format(_local["CompareDone"], selected[0].FileName, selected[1].FileName), "Gallery");
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["CompareFailed"], ex.Message);
            _log.Error(_local["OpenImageCompareFailed"], "Gallery", ex);
        }
    }

    /// <summary>局部刷新单个列表项的标签（详情页修改标签后调用，避免整体重载丢失滚动位置）</summary>
    public async Task RefreshItemTagsAsync(int imageInfoId)
    {
        try
        {
            using var db = await DbFactory.CreateDbContextAsync();
            // EF 不能翻译画刷构造，先投影名称+颜色 hex，客户端再构造 TagDisplay
            var tagData = await db.ImageTags
                .Where(it => it.ImageInfoId == imageInfoId)
                .Include(it => it.Tag)
                .Select(it => new { it.Tag.Name, it.Tag.Color })
                .ToListAsync();
            var item = Images.FirstOrDefault(i => i.ImageInfoId == imageInfoId);
            if (item == null) return;
            item.Tags.Clear();
            foreach (var t in tagData)
                item.Tags.Add(new TagDisplay { Name = t.Name, Color = TagPalette.GetBrush(t.Color, t.Name) });
        }
        catch (Exception ex)
        {
            _log.Error(string.Format(_local["RefreshItemTagsFailed"], imageInfoId), "Gallery", ex);
        }
    }

    /// <summary>打开标签管理对话框</summary>
    [RelayCommand]
    private async Task ManageTagsAsync()
    {
        try
        {
            var dbFactory = _service.GetRequiredService<IDbContextFactory<ComfyDbContext>>();
            var tagVm = new TagManagerModel(dbFactory, _local, _noticeService, _log);
            await SukiMessageBox.ShowDialog(
                new SukiMessageBoxHost
                {
                    Content = tagVm,
                    Width = 480,
                    Height = 500,
                    ActionButtonsPreset = SukiMessageBoxButtons.Close,
                },
                new SukiMessageBoxOptions { Title = _local["TagManager"] });
            // 关闭后刷新标签下拉
            _ = LoadTagsAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["TagManagerFailed"], ex.Message);
            _log.Error(_local["OpenTagManagerFailed"], "Gallery", ex);
        }
    }

    /// <summary>批量打标签</summary>
    [RelayCommand]
    private async Task BatchTagAsync()
    {
        if (!HasSelection) return;
        try
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
                || desktop.MainWindow is not { } owner)
            {
                return;
            }

            var dbFactory = _service.GetRequiredService<IDbContextFactory<ComfyDbContext>>();
            var batchVm = new BatchTagModel(SelectedImageIds, dbFactory, _local, _noticeService, _log);

            var host = new SukiMessageBoxHost
            {
                Content = batchVm,
                Width = 400,
                Height = 450,
                ActionButtonsPreset = SukiMessageBoxButtons.Close,
                // 宿主 foot：[保存（关闭按钮左侧，点击执行 ApplyCommand）] [关闭]
                ActionButtonsSource = new Avalonia.Collections.AvaloniaList<Avalonia.Controls.Button>
                {
                    CreateBatchTagSaveButton(batchVm),
                    CreateBatchTagCloseButton(),
                },
            };
            var options = new SukiMessageBoxOptions { Title = _local["BatchTagTitle"] };
            var window = SukiMessageBox.CreateMessageBoxWindow(options);
            window.Content = host;
            if (host.ActionButtonsSource is { } buttons)
            {
                for (var i = 1; i < buttons.Count; i++)
                    buttons[i].Click += (_, _) => window.Close();
                buttons[^1].IsCancel = true;
            }
            window.KeyUp += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Escape) window.Close();
            };
            await window.ShowDialog(owner);
            // 刷新
            _pageIndex = 0;
            Images.Clear();
            _ = LoadImagesAsync(isReset: true);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["BatchTagFailed"], ex.Message);
            _log.Error(_local["BatchTagFailedLog"], "Gallery", ex);
        }
    }

    /// <summary>宿主 foot"保存"按钮（原 body"应用标签"，改名并移入宿主；点击执行 ApplyCommand，状态反馈留在 body）。</summary>
    private Avalonia.Controls.Button CreateBatchTagSaveButton(BatchTagModel vm)
    {
        var button = BaseClassLib.Extends.SukiMessageBoxButtonsFactoryExtend.CreateButton(SukiMessageBoxResult.OK, _local["Save"] ?? "");
        button.Click += (_, _) => vm.ApplyCommand.Execute(null);
        return button;
    }

    /// <summary>宿主 foot"关闭"按钮（Esc/取消语义）。</summary>
    private Avalonia.Controls.Button CreateBatchTagCloseButton()
    {
        var button = BaseClassLib.Extends.SukiMessageBoxButtonsFactoryExtend.CreateButton(SukiMessageBoxResult.Close, _local["Close"] ?? "");
        button.IsCancel = true;
        return button;
    }

    /// <summary>打开标签管理抽屉（封装组件：新建/重命名/删除统一实现）</summary>
    [RelayCommand]
    private async Task OpenTagDrawerAsync() => await TagDrawer.OpenAsync();

    private IDbContextFactory<ComfyDbContext> DbFactory =>
        _service.GetRequiredService<IDbContextFactory<ComfyDbContext>>();

    #endregion
}

public partial class ImageItem : ObservableObject
{
    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private long _fileSize;
    [ObservableProperty] private int _width;
    [ObservableProperty] private int _height;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private bool _isNsfw;
    [ObservableProperty] private int _rating;

    /// <summary>画廊多选状态</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>缩略图位图</summary>
    public Bitmap? Thumbnail { get; set; }

    /// <summary>EF Core Id（用于详情查询）</summary>
    public int ImageInfoId { get; set; }

    /// <summary>相对路径（用于加载原图）</summary>
    public string RelativePath { get; set; } = "";

    /// <summary>输出目录绝对路径（从 ComfySettings 注入）</summary>
    public string OutputDir { get; set; } = "";

    /// <summary>原图绝对路径</summary>
    public string FullPath => string.IsNullOrEmpty(RelativePath) ? "" : Path.Combine(OutputDir, RelativePath);

    /// <summary>SHA256 Hash</summary>
    public string? Hash { get; set; }

    /// <summary>文件创建时间</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>标签显示列表（名称 + 颜色）</summary>
    public ObservableCollection<TagDisplay> Tags { get; set; } = new();

    public string FileSizeDisplay => FileSize switch
    {
        < 1024 => $"{FileSize} B",
        < 1024 * 1024 => $"{FileSize / 1024} KB",
        _ => $"{FileSize / (1024.0 * 1024):F1} MB"
    };

    public string DimensionsDisplay => $"{Width} × {Height}";
}