using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PromptCraft.BaseModel;
using PromptCraft.ViewModels.ComfyUI;
using System.Linq;

namespace PromptCraft.Views.ComfyUI;

public partial class ComfyGallery : PageBase
{
    /// <summary>Ctrl 物理键按下状态（由 KeyDown/KeyUp 跟踪，避免事件参数里修饰键快照不可靠）</summary>
    private bool _ctrlDown;

    public ComfyGallery(System.IServiceProvider service) : base(service)
    {
        InitializeComponent();
        Loaded += OnLoaded;
        // Tunnel 路由：即使焦点在子控件也能收到键盘事件，实时跟踪 Ctrl 物理键
        AddHandler(KeyDownEvent, OnGalleryKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnGalleryKeyUp, RoutingStrategies.Tunnel);
    }

    private void OnGalleryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl) _ctrlDown = true;
    }

    private void OnGalleryKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl) _ctrlDown = false;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ComfyGalleryModel vm)
            await vm.LoadImagesCommand.ExecuteAsync(null);
        ImageScrollViewer.ScrollChanged += OnScrollChanged;
    }

    private async void OnSyncClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ComfyGalleryModel vm) return;
        SyncBtnText.Text = _local["Syncing"].ToString();
        SyncBtn.IsEnabled = false;
        await vm.SyncImagesCommand.ExecuteAsync(null);
        SyncBtnText.Text = _local["SyncNow"].ToString();
        SyncBtn.IsEnabled = true;
    }

    private async void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not ComfyGalleryModel vm) return;
        var scroll = ImageScrollViewer;
        if (scroll.Extent.Height - scroll.Offset.Y - scroll.Viewport.Height < 200
            && vm.HasMoreItems && !vm.IsLoading)
            await vm.LoadMoreCommand.ExecuteAsync(null);
    }

    private async void OnImagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not ComfyGalleryModel vm) return;
        if (sender is not Border { DataContext: ImageItem item }) return;

        // 右键：不触发左键逻辑；未选中的图先单选它（为对比做准备），已选中的保持现状
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            if (!item.IsSelected)
            {
                foreach (var other in vm.Images.Where(x => x.IsSelected && x != item))
                    other.IsSelected = false;
                item.IsSelected = true;
            }
            vm.NotifySelectionChanged();
            // 按当前选中集刷新菜单动态文案（收藏/NSFW 切换目标、对比可用性、选中数量）
            vm.RefreshContextMenuState();
            ContextTitle.Text = string.Format(_local["GallerySelectedCount"], vm.Images.Count(x => x.IsSelected));
            ContextFavBtn.Content = vm.ContextFavoriteText;
            ContextNsfwBtn.Content = vm.ContextNsfwText;
            CompareContextBtn.IsEnabled = vm.HasTwoSelection;
            // 按下瞬间打开菜单（PlacementMode=Pointer，此时指针还没移动，菜单位置精确不漂移）
            ImageContextPopup.IsOpen = true;
            return;
        }

        if (e.ClickCount >= 2) { await vm.OpenDetailCommand.ExecuteAsync(item); return; }

        // Ctrl 检测双保险：PointerPressed 事件的 KeyModifiers 在某些平台/输入状态下不可靠，
        // 用物理键跟踪状态（_ctrlDown，由 KeyDown/KeyUp 维护）兜底
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || _ctrlDown;

        if (ctrl)
            item.IsSelected = !item.IsSelected;
        else
        {
            foreach (var other in vm.Images.Where(x => x.IsSelected && x != item))
                other.IsSelected = false;
            item.IsSelected = !item.IsSelected;
        }
        vm.NotifySelectionChanged();
    }

    /// <summary>图片右键菜单：先关闭菜单再执行对比（校验与弹窗逻辑在 ViewModel）</summary>
    private async void OnCompareMenuItemClick(object? sender, RoutedEventArgs e)
    {
        ImageContextPopup.IsOpen = false;
        if (DataContext is ComfyGalleryModel vm)
            await vm.CompareSelectedCommand.ExecuteAsync(null);
    }

    /// <summary>右键菜单：切换收藏（先关菜单，避免菜单残留遮挡）</summary>
    private async void OnToggleFavoriteMenuItemClick(object? sender, RoutedEventArgs e)
    {
        ImageContextPopup.IsOpen = false;
        if (DataContext is ComfyGalleryModel vm)
            await vm.ToggleFavoriteCommand.ExecuteAsync(null);
    }

    /// <summary>右键菜单：切换 NSFW 标记</summary>
    private async void OnToggleNsfwMenuItemClick(object? sender, RoutedEventArgs e)
    {
        ImageContextPopup.IsOpen = false;
        if (DataContext is ComfyGalleryModel vm)
            await vm.ToggleNsfwCommand.ExecuteAsync(null);
    }

    /// <summary>右键菜单：批量打标签（打开标签列表 + 复选框弹窗）</summary>
    private async void OnBatchTagMenuItemClick(object? sender, RoutedEventArgs e)
    {
        ImageContextPopup.IsOpen = false;
        if (DataContext is ComfyGalleryModel vm)
            await vm.BatchTagCommand.ExecuteAsync(null);
    }

    /// <summary>右键菜单：加入文件夹（弹窗多选文件夹后保存）</summary>
    private async void OnAddToFolderMenuItemClick(object? sender, RoutedEventArgs e)
    {
        ImageContextPopup.IsOpen = false;
        if (DataContext is ComfyGalleryModel vm)
            await vm.AddToFolderCommand.ExecuteAsync(null);
    }

    /// <summary>右键菜单：资源管理器中定位图片</summary>
    private void OnOpenFolderMenuItemClick(object? sender, RoutedEventArgs e)
    {
        ImageContextPopup.IsOpen = false;
        if (DataContext is ComfyGalleryModel vm)
            vm.OpenSelectedFolderCommand.Execute(null);
    }

    /// <summary>右键菜单：复制选中图片路径</summary>
    private void OnCopyPathMenuItemClick(object? sender, RoutedEventArgs e)
    {
        ImageContextPopup.IsOpen = false;
        if (DataContext is ComfyGalleryModel vm)
            vm.CopySelectedPathsCommand.Execute(null);
    }

    /// <summary>右键菜单：删除所选（弹窗确认，逻辑在 ViewModel）</summary>
    private async void OnDeleteMenuItemClick(object? sender, RoutedEventArgs e)
    {
        ImageContextPopup.IsOpen = false;
        if (DataContext is ComfyGalleryModel vm)
            await vm.DeleteSelectedCommand.ExecuteAsync(null);
    }

    // ===== 标签下拉：行点击切换复选框 =====
    private void OnTagItemClick(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: TagItem item })
            item.IsSelected = !item.IsSelected;
    }
}