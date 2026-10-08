using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.BaseModel;
using PromptCraft.Consts.Event;
using PromptCraft.Data;
using PromptCraft.Interfaces;
using PromptCraft.Models;
using PromptCraft.Models.ComfyUI;
using PromptCraft.Service;
using PromptCraft.Utils;
using PromptCraft.ViewModels.Folders;
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
using System.Threading.Tasks;

namespace PromptCraft.ViewModels.PromptLibrary;

/// <summary>
/// 提示词库页面 ViewModel（PromptMaster 迁移 T1.2/T1.3）。
/// 交互对齐 PmLibrary + 图库 ComfyGallery：瀑布流卡片（WrapPanel 同款卡片）、无限滚动加载、
/// 客户端实时过滤（标题/正负面/标签/备注）+ 标签多选筛选、单击选中/Ctrl 多选/双击编辑详情、
/// 右键菜单（编辑/复制正/复制负/复制封面/大图/删除）、本页全选 + 批量删除 + 标签管理抽屉（全局标签池）。
/// 双击卡片由 View 触发 <see cref="EditPromptCommand"/> 弹出编辑对话框。
/// </summary>
public partial class PromptLibraryModel : ViewModelBase
{
    private readonly IServiceProvider _service;
    private readonly IPromptLibraryService _library;
    private readonly IFolderService _folderService;
    private readonly IWorkspaceService _workspace;
    private readonly IBaseLogService _log;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasMoreItems;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private ObservableCollection<PromptCardItem> _prompts = new();
    [ObservableProperty] private ObservableCollection<PromptTagItem> _availableTags = new();
    [ObservableProperty] private int _totalCount;

    /// <summary>标签管理抽屉（全局标签池，与图库/工作流共用）。</summary>
    public PromptCraft.ViewModels.ComfyUI.TagManagerDrawerModel TagDrawer { get; }

    private const int PageSize = 60;

    private List<PromptCardItem> _allPrompts = new();
    private List<PromptTagItem> _allTags = new();
    private int _pageIndex;
    private readonly HashSet<string> _selectedIds = new();

    public PromptLibraryModel(
        IServiceProvider service,
        IPromptLibraryService library,
        IFolderService folderService,
        IWorkspaceService workspace,
        IBaseLogService log)
        : base(
            service.GetRequiredService<Ke.Bee.Localization.Localizer.Abstractions.ILocalizer>(),
            service.GetRequiredService<PromptCraft.Interfaces.IBaseNotice>())
    {
        _service = service;
        _library = library;
        _folderService = folderService;
        _workspace = workspace;
        _log = log;
        _displayName = "PROMPTLIBRARY";
        _icon = MaterialIconKind.ContentPaste;
        _index = 20;
        _sideMenu = true;

        // 标签管理统一用图库/工作流同款 TagManagerDrawerModel（共用组件，行为一致）
        TagDrawer = new PromptCraft.ViewModels.ComfyUI.TagManagerDrawerModel(
            service.GetRequiredService<IDbContextFactory<PromptCraft.Data.ComfyDbContext>>(),
            service.GetRequiredService<ITagRepository>(),
            _local, _noticeService, _log);
        TagDrawer.TagsChanged += () => _ = LoadAsync();

        _noticeService.Subscribe(EventNameConst.PromptLibraryChangedEvent, OnLibraryChanged);
    }

    public override void Disposed()
    {
        _noticeService.Unsubscribe(EventNameConst.PromptLibraryChangedEvent, OnLibraryChanged);
        base.Disposed();
    }

    private async void OnLibraryChanged(object? _) => await LoadAsync();

    // ---- 数据加载（对齐图库 ComfyGallery：全量索引 + 瀑布流无限滚动） ----

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var prompts = await _library.QueryAsync(new PromptQuery(Page: 1, PageSize: int.MaxValue));
            var tags = await _library.GetTagsAsync();

            var tagNameMap = tags.ToDictionary(t => t.Id.ToString(), t => t.Name);
            var tagBrushMap = tags.ToDictionary(
                t => t.Id.ToString(),
                t => (Avalonia.Media.IBrush)TagPalette.GetBrush(t.Color, t.Name));

            _allTags = tags.Select(t => new PromptTagItem(t)).ToList();
            _allPrompts = prompts.Items.Select(p => ToCardItem(p, tagNameMap, tagBrushMap)).ToList();

            // 刷新标签下拉（保留选中项）
            var kept = AvailableTags.Where(t => t.IsSelected).Select(t => t.Id).ToHashSet();
            foreach (var t in _allTags) t.IsSelected = kept.Contains(t.Id);
            AvailableTags = new ObservableCollection<PromptTagItem>(_allTags);

            // 全量重载后卡片为新建实例，选中状态清空（对齐图库重载语义）
            foreach (var p in _allPrompts) p.IsSelected = false;
            _selectedIds.Clear();
            StatusMessage = string.Format(_local["PromptTotalCount"], _allPrompts.Count);
            ResetInfiniteList();
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["PromptLoadFailed"], ex.Message);
            _log.Error(_local["PromptLoadFailedLog"], "PromptLibrary", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static PromptCardItem ToCardItem(Prompt p, Dictionary<string, string> tagNameMap, Dictionary<string, Avalonia.Media.IBrush> tagBrushMap)
    {
        var item = new PromptCardItem
        {
            Id = p.Id,
            Title = p.Title,
            Positive = p.Positive,
            Negative = p.Negative,
            Note = p.Note,
            Cover = p.Cover,
            FolderIds = p.FolderMaps?.Select(m => m.FolderId).ToList() ?? new List<int>(),
            UpdatedAt = p.UpdatedAt,
            UpdatedAtDisplay = p.UpdatedAt.ToString("yyyy-MM-dd HH:mm"),
            TagIds = p.PromptTags?.Select(t => t.TagId).ToList() ?? new List<string>(),
        };
        foreach (var tid in item.TagIds)
        {
            if (tagNameMap.TryGetValue(tid, out var name))
            {
                item.Tags.Add(new PromptTagChip { Id = tid, Name = name, Color = tagBrushMap[tid] });
            }
        }
        return item;
    }

    /// <summary>当前已加载卡片异步加载封面（对齐 PmLibrary 封面显示）。</summary>
    private async Task LoadCoverForPageAsync()
    {
        var coversDir = _workspace.CoversDir;
        foreach (var card in Prompts)
        {
            if (card.HasCover && card.CoverFullPath == null)
            {
                var path = Path.Combine(coversDir, card.Cover);
                card.CoverFullPath = File.Exists(path) ? path : null;
                if (card.CoverFullPath != null)
                {
                    _ = card.LoadCoverAsync();
                }
            }
        }
        await Task.CompletedTask;
    }

    // ---- 过滤 + 无限滚动（对齐图库 ScrollChanged 加载更多） ----

    private List<PromptCardItem> Filtered()
    {
        var keyword = SearchText?.Trim().ToLowerInvariant() ?? "";
        var tagFilter = AvailableTags.Where(t => t.IsSelected).Select(t => t.Id).ToList();

        return _allPrompts.Where(p =>
        {
            if (tagFilter.Count > 0)
            {
                var set = new HashSet<string>(p.TagIds);
                if (!tagFilter.All(set.Contains)) return false;
            }
            if (keyword.Length == 0) return true;
            var tagNames = string.Join(" ", p.Tags.Select(t => t.Name));
            return $"{p.Title}\n{p.Positive}\n{p.Negative}\n{p.Note}\n{tagNames}".ToLowerInvariant().Contains(keyword);
        }).ToList();
    }

    partial void OnSearchTextChanged(string value)
    {
        ResetInfiniteList();
    }

    partial void OnTotalCountChanged(int value)
    {
        OnPropertyChanged(nameof(TotalCountText));
    }

    /// <summary>重置无限滚动列表：过滤 → 取第一批（对齐图库 LoadImagesAsync(isReset:true)）。</summary>
    private void ResetInfiniteList()
    {
        var filtered = Filtered();
        TotalCount = filtered.Count;
        _pageIndex = 0;

        Prompts = new ObservableCollection<PromptCardItem>(filtered.Take(PageSize));
        HasMoreItems = filtered.Count > Prompts.Count;

        NotifySelectionChanged();
        _ = LoadCoverForPageAsync();
    }

    /// <summary>加载更多（滚动到底部时调用，对齐图库 LoadMoreCommand）。</summary>
    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (IsLoading || !HasMoreItems) return;
        var filtered = Filtered();
        if (filtered.Count <= Prompts.Count) { HasMoreItems = false; return; }

        IsLoading = true;
        try
        {
            _pageIndex++;
            var next = filtered.Skip(Prompts.Count).Take(PageSize).ToList();
            foreach (var item in next) Prompts.Add(item);
            HasMoreItems = Prompts.Count < filtered.Count;
            _ = LoadCoverForPageAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ---- 选中状态（对齐图库 ComfyGallery：单击单选 / Ctrl 多选 / 双击编辑） ----

    public int SelectedCount => _selectedIds.Count;
    public bool HasSelection => _selectedIds.Count > 0;

    public bool IsAllPageSelected =>
        Prompts.Count > 0 && Prompts.All(p => _selectedIds.Contains(p.Id));

    public bool IsPageIndeterminate
    {
        get
        {
            var n = Prompts.Count(p => _selectedIds.Contains(p.Id));
            return n > 0 && n < Prompts.Count;
        }
    }

    public string SelectedCountText => string.Format(_local["PromptSelectedCount"], SelectedCount);
    public string TotalCountText => string.Format(_local["PromptTotalFooter"], TotalCount);

    /// <summary>选中变化后由 View 调用（对齐图库 NotifySelectionChanged）。</summary>
    public void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsAllPageSelected));
        OnPropertyChanged(nameof(IsPageIndeterminate));
        OnPropertyChanged(nameof(SelectedCountText));
    }

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

    /// <summary>标签下拉选择变化 → 重置列表并过滤。</summary>
    public void OnTagSelectionChanged()
    {
        ResetInfiniteList();
        OnPropertyChanged(nameof(SelectedTagsSummary));
    }

    /// <summary>当前已加载列表全选 / 取消全选。</summary>
    [RelayCommand]
    private void ToggleSelectPage()
    {
        if (IsAllPageSelected)
        {
            foreach (var p in Prompts)
            {
                p.IsSelected = false;
                _selectedIds.Remove(p.Id);
            }
        }
        else
        {
            foreach (var p in Prompts)
            {
                p.IsSelected = true;
                _selectedIds.Add(p.Id);
            }
        }
        NotifySelectionChanged();
    }

    /// <summary>清空选中。</summary>
    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var p in _allPrompts) p.IsSelected = false;
        _selectedIds.Clear();
        NotifySelectionChanged();
    }

    /// <summary>右键菜单：把选中的提示词加入所选文件夹（多对多，多选时批量）。</summary>
    [RelayCommand]
    private async Task AddToFolderAsync()
    {
        var ids = _selectedIds.ToList();
        if (ids.Count == 0) return;
        var picked = await FolderPickerDialog.PickAsync(FolderScopes.Prompt, null, _service);
        if (picked == null) return;
        if (picked.Count == 0)
        {
            ShowToast(_local["AddToFolderNone"] ?? "", "");
            return;
        }
        try
        {
            var folderService = _service.GetRequiredService<IFolderService>();
            await folderService.AddToFoldersAsync(FolderScopes.Prompt, ids, picked);
            _log.Info(string.Format(_local["PromptAddToFolderLog"] ?? "", ids.Count, picked.Count), "PromptLibrary");
            ShowToast(string.Format(_local["AddToFolderDone"] ?? "", ids.Count, picked.Count), "");
        }
        catch (Exception ex)
        {
            _log.Error(string.Format(_local["FolderSaveFailed"] ?? "", ex.Message), "PromptLibrary", ex);
        }
    }

    /// <summary>
    /// 卡片单击选择（对齐图库 ComfyGallery）：Ctrl = 切换当前项；非 Ctrl = 先清其他再切换。
    /// </summary>
    public void ToggleSelect(PromptCardItem item, bool ctrl)
    {
        if (ctrl)
        {
            item.IsSelected = !item.IsSelected;
        }
        else
        {
            foreach (var other in _allPrompts.Where(x => x.IsSelected && x != item))
                other.IsSelected = false;
            item.IsSelected = !item.IsSelected;
        }
        SyncSelectedIds();
        NotifySelectionChanged();
    }

    /// <summary>以卡片选中态为准重建 _selectedIds（单一事实源 = 卡片 IsSelected）。</summary>
    private void SyncSelectedIds()
    {
        _selectedIds.Clear();
        foreach (var p in _allPrompts.Where(p => p.IsSelected)) _selectedIds.Add(p.Id);
    }

    // ---- 新建 / 编辑（双击卡片） ----

    [RelayCommand]
    private async Task NewPromptAsync()
    {
        await OpenEditDialogAsync(null);
    }

    /// <summary>双击卡片：弹出编辑详情对话框（对齐 PmLibrary 点击卡片进编辑，用户要求双击触发）。</summary>
    [RelayCommand]
    private async Task EditPromptAsync(PromptCardItem? item)
    {
        if (item == null) return;
        Prompt? existing = null;
        if (item != null)
        {
            existing = await _library.GetPromptAsync(item.Id);
        }
        await OpenEditDialogAsync(existing);
    }

    /// <summary>打开编辑对话框（SukiMessageBox 宿主，与 ImageDetail 弹窗一致）。</summary>
    private async Task OpenEditDialogAsync(Prompt? existing)
    {
        await PromptEditDialogOpener.OpenEditAsync(
            _service, existing,
            existing == null ? _local["PromptEditNewTitle"] : _local["PromptEditTitle"],
            _local, _noticeService, _log,
            onSaved: () =>
            {
                _ = LoadAsync();
                ShowToast(_local["PromptSaved"], existing?.Title ?? "");
            });
    }

    // ---- 删除 ----

    [RelayCommand]
    private async Task DeletePromptAsync(PromptCardItem? item)
    {
        if (item == null) return;
        if (!await ConfirmDeleteAsync(string.Format(_local["PromptDeleteConfirm"], item.Title))) return;

        try
        {
            await _library.DeletePromptsAsync(new[] { item.Id });
            _selectedIds.Remove(item.Id);
            ShowToast(_local["PromptDeleted"], item.Title);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["PromptDeleteFailed"], ex.Message);
            _log.Error(string.Format(_local["PromptDeleteFailedLog"], item.Title), "PromptLibrary", ex);
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (!HasSelection) return;
        if (!await ConfirmDeleteAsync(string.Format(_local["PromptDeleteSelectedConfirm"], SelectedCount))) return;

        try
        {
            var ids = _selectedIds.ToList();
            await _library.DeletePromptsAsync(ids);
            _selectedIds.Clear();
            ShowToast(_local["PromptDeleted"], string.Format(_local["PromptDeletedCount"], ids.Count));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["PromptDeleteFailed"], ex.Message);
            _log.Error(_local["PromptBatchDeleteFailedLog"], "PromptLibrary", ex);
        }
    }

    private async Task<bool> ConfirmDeleteAsync(string message)
    {
        var confirm = await SukiMessageBox.ShowDialog(
            new SukiMessageBoxHost
            {
                Content = new TextBlock
                {
                    Text = message,
                    Margin = new Thickness(4),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                ActionButtonsPreset = SukiMessageBoxButtons.OKCancel,
            },
            new SukiMessageBoxOptions { Title = _local["ConfirmDelete"], MinWidth = 340 });
        return confirm is SukiMessageBoxResult r && r.Equals(SukiMessageBoxResult.OK);
    }

    // ---- 复制 ----

    /// <summary>把选中提示词复制到跨页共享剪贴板（配合菜单管理页"粘贴"加入目标文件夹）。</summary>
    [RelayCommand]
    private void CopyToClipboard()
    {
        var items = _allPrompts.Where(p => p.IsSelected).Select(p => new FolderAssetItem
        {
            Scope = FolderScopes.Prompt,
            AssetId = p.Id,
            Name = p.Title,
            TypeDisplay = _local["FolderScopePrompt"] ?? "提示词",
            SubText = p.UpdatedAtDisplay,
            SortTime = p.UpdatedAt,
            ThumbPath = p.CoverFullPath ?? "",
        }).ToList();
        if (items.Count == 0)
        {
            ShowToast(_local["AddToFolderNone"] ?? "", "");
            return;
        }
        var clipboard = _service.GetRequiredService<IFolderClipboardService>();
        clipboard.Copy(items);
        _log.Info(string.Format(_local["FolderCopiedLog"] ?? "", string.Join("、", items.Select(i => i.Name))), "PromptLibrary");
        ShowToast(string.Format(_local["FolderCopiedHint"] ?? "", items.Count), "");
    }

    [RelayCommand]
    private async Task CopyPositiveAsync(PromptCardItem? item)
    {
        if (item == null) return;
        await CopyTextAsync(item.Positive, _local["CopyPositive"]);
    }

    [RelayCommand]
    private async Task CopyNegativeAsync(PromptCardItem? item)
    {
        if (item == null) return;
        await CopyTextAsync(item.Negative, _local["CopyNegative"]);
    }

    /// <summary>复制封面图片到剪贴板（对齐 PromptMaster copyCoverToClipboard，可在画图等应用粘贴）。</summary>
    [RelayCommand]
    private async Task CopyCoverAsync(PromptCardItem? item)
    {
        if (item == null) return;
        if (string.IsNullOrEmpty(item.CoverFullPath) || !File.Exists(item.CoverFullPath))
        {
            ShowToast(_local["PromptNoCover"], item.Title);
            return;
        }
        try
        {
            var path = item.CoverFullPath;
            var bitmap = await Task.Run(() =>
            {
                try { using var fs = File.OpenRead(path); return new Bitmap(fs); }
                catch { return null; }
            });
            if (bitmap == null)
            {
                ShowToast(_local["CopyFailed"], item.Title);
                return;
            }
            var top = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d
                ? d.MainWindow
                : null;
            if (top?.Clipboard == null) return;
            await Avalonia.Input.Platform.ClipboardExtensions.SetBitmapAsync(top.Clipboard, bitmap);
            ShowToast(_local["Copied"], _local["PromptCopyCover"]);
        }
        catch (Exception ex)
        {
            _log.Warn(string.Format(_local["PromptCopyCoverFailedLog"], item.Title), "PromptLibrary", ex);
            ShowToast(_local["CopyFailed"], item.Title);
        }
    }

    /// <summary>大图预览（对齐 PromptMaster 看大图：封面大图 + 复制封面 + 可拖拽导出）。</summary>
    [RelayCommand]
    private async Task OpenBigImageAsync(PromptCardItem? item)
    {
        if (item == null) return;
        if (string.IsNullOrEmpty(item.CoverFullPath) || !File.Exists(item.CoverFullPath))
        {
            ShowToast(_local["PromptNoCover"], item.Title);
            return;
        }

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } owner)
        {
            return;
        }

        try
        {
            var host = new SukiMessageBoxHost
            {
                Content = new PromptCraft.Views.PromptLibrary.PromptBigImageView { DataContext = item },
                IconPreset = null,
                Width = 720,
                ActionButtonsPreset = SukiMessageBoxButtons.Close,
            };
            var options = new SukiMessageBoxOptions
            {
                Title = item.Title,
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
            // 宿主 foot 关闭按钮接线（修复左下角关闭不起作用）+ Esc 关闭
            if (host.ActionButtonsSource is { } buttons)
            {
                for (var i = 0; i < buttons.Count; i++)
                    buttons[i].Click += (_, _) => window.Close();
                buttons[^1].IsCancel = true;
            }
            window.KeyUp += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Escape) window.Close();
            };
            await window.ShowDialog(owner);
        }
        catch (Exception ex)
        {
            _log.Warn(string.Format(_local["PromptOpenBigImageFailedLog"], ex.Message), "PromptLibrary", ex);
        }
    }

    private async Task CopyTextAsync(string text, string what)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowToast(_local["CopyEmpty"], what);
            return;
        }
        // 项目统一剪贴板服务（Avalonia 12 IClipboard 无 SetTextAsync，由服务封装）
        _service.GetRequiredService<IBaseClipboardService>().CopyToClipboard(text);
        ShowToast(_local["Copied"], what);
        await Task.CompletedTask;
    }

    // ---- 批量打标签（右键菜单/工具栏：对当前全部选中提示词，支持多选） ----

    /// <summary>打开批量打标签对话框：全局标签池复选框，勾选集 = 这批提示词的最终标签集（对齐图库 BatchTag）。</summary>
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
            var library = _service.GetRequiredService<IPromptLibraryService>();
            var batchVm = new PromptBatchTagModel(
                _selectedIds.ToArray(), dbFactory, library, _local, _noticeService, _log);

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
            var options = new SukiMessageBoxOptions { Title = _local["PromptBatchTagTitle"] };
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
            // AssignTagsAsync 已发布 PromptLibraryChangedEvent（自动重载）；此处再兜底刷新一次
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(_local["PromptBatchTagFailed"], ex.Message);
            _log.Error(_local["PromptBatchTagFailedLog"], "PromptLibrary", ex);
        }
    }

    /// <summary>宿主 foot"保存"按钮：点击执行 ApplyCommand，状态反馈留在 body。</summary>
    private Avalonia.Controls.Button CreateBatchTagSaveButton(PromptBatchTagModel vm)
    {
        var button = BaseClassLib.Extends.SukiMessageBoxButtonsFactoryExtend.CreateButton(
            SukiMessageBoxResult.OK, _local["Save"] ?? "");
        button.Click += (_, _) => vm.ApplyCommand.Execute(null);
        return button;
    }

    /// <summary>宿主 foot"关闭"按钮（Esc/取消语义）。</summary>
    private Avalonia.Controls.Button CreateBatchTagCloseButton()
    {
        var button = BaseClassLib.Extends.SukiMessageBoxButtonsFactoryExtend.CreateButton(
            SukiMessageBoxResult.Close, _local["Close"] ?? "");
        button.IsCancel = true;
        return button;
    }

    // ---- 标签抽屉 ----

    [RelayCommand]
    private void OpenTagDrawer()
    {
        _ = TagDrawer.OpenAsync();
    }

    /// <summary>点击卡片标签 → 单选该标签筛选（对齐 PmLibrary 点击标签 chip 筛选）。</summary>
    public void OnCardTagClick(string tagId)
    {
        foreach (var t in AvailableTags) t.IsSelected = t.Id == tagId;
        ResetInfiniteList();
        OnPropertyChanged(nameof(SelectedTagsSummary));
    }

    // ---- Toast（轻量提示，对齐 PmLibrary ElMessage 反馈） ----

    private void ShowToast(string title, string content)
    {
        try
        {
            var toastManager = _service.GetService(typeof(ISukiToastManager)) as ISukiToastManager;
            if (toastManager == null) return;
            var toast = FluentSukiToastBuilder.CreateSimpleInfoToast(toastManager);
            toast.SetTitle(title);
            toast.SetContent(content);
            toast.SetCanDismissByClicking(true);
            toast.Toast.DismissTimeout = TimeSpan.FromSeconds(3);
            toast.Queue();
        }
        catch (Exception ex)
        {
            _log.Debug(string.Format(_local["PromptToastFailed"], ex.Message), "PromptLibrary");
        }
    }

    public override void OnSystemLangueChanged(object? data)
    {
        OnPropertyChanged(nameof(SelectedTagsSummary));
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(TotalCountText));
    }
}
