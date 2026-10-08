using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PromptCraft.ViewModels.PromptLibrary;
using System.Linq;

namespace PromptCraft.Views.PromptLibrary;

/// <summary>
/// 提示词库页面（对齐图库 ComfyGallery 交互）：瀑布流卡片、无限滚动加载、
/// 单击选中 / Ctrl 多选 / 双击编辑详情 / 右键菜单（编辑/复制正/复制负/复制封面/大图/删除）、
/// 封面按住拖动 = 导出封面文件（对齐 PmLibrary pm-native-drag-file）。
/// </summary>
public partial class PromptLibraryView : UserControl
{
    /// <summary>Ctrl 物理键按下状态（由 KeyDown/KeyUp 跟踪，避免事件参数里修饰键快照不可靠）。</summary>
    private bool _ctrlDown;

    /// <summary>当前右键菜单指向的卡片（菜单项针对单卡操作）。</summary>
    private PromptCardItem? _contextCard;

    public PromptLibraryView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        AddHandler(KeyDownEvent, OnPageKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPageKeyUp, RoutingStrategies.Tunnel);
    }

    private PromptLibraryModel? Vm => DataContext as PromptLibraryModel;

    private void OnPageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl) _ctrlDown = true;
    }

    private void OnPageKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl) _ctrlDown = false;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (Vm != null)
            await Vm.LoadCommand.ExecuteAsync(null);
        PromptScrollViewer.ScrollChanged += OnScrollChanged;
    }

    /// <summary>滚动到底部 → 加载更多（对齐图库无限滚动）。</summary>
    private async void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (Vm == null) return;
        var scroll = PromptScrollViewer;
        if (scroll.Extent.Height - scroll.Offset.Y - scroll.Viewport.Height < 200
            && Vm.HasMoreItems && !Vm.IsLoading)
            await Vm.LoadMoreCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// 卡片按下：右键 = 未选先单选并打开菜单；双击 = 编辑详情；单击 = 选中（Ctrl 多选）。
    /// 封面区已处理拖拽导出，未拖出时事件冒泡到此处统一走选中/编辑。
    /// </summary>
    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm == null) return;
        if (sender is not Border { DataContext: PromptCardItem item }) return;

        // 右键：未选中的卡片先单选（对齐图库），已选中的保持现状
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            if (!item.IsSelected)
            {
                foreach (var other in Vm.Prompts.Where(x => x.IsSelected && x != item))
                    other.IsSelected = false;
                item.IsSelected = !item.IsSelected;
                Vm.NotifySelectionChanged();
            }
            _contextCard = item;
            ContextTitle.Text = Vm.SelectedCountText;
            RefreshContextMenuEnabled();
            PromptContextPopup.IsOpen = true;
            e.Handled = true;
            return;
        }

        if (e.ClickCount >= 2)
        {
            Vm.EditPromptCommand.Execute(item);
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 1)
        {
            bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || _ctrlDown;
            Vm.ToggleSelect(item, ctrl);
            e.Handled = true;
        }
    }

    /// <summary>封面：单击/双击冒泡到卡片（选中/编辑）；封面拖拽导出已移除。</summary>
    private void OnTagChipClick(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { Tag: PromptTagChip chip } && Vm != null)
        {
            Vm.OnCardTagClick(chip.Id);
            TagFilterDropDown?.RefreshDisplay();
            e.Handled = true;
        }
    }

    private void OnTagSelectionChanged()
    {
        Vm?.OnTagSelectionChanged();
    }

    // ---- 右键菜单项（先关菜单再执行，避免菜单残留遮挡，对齐图库） ----

    /// <summary>菜单打开前刷新可用状态：不支持多选的功能（编辑/复制正/复制负/复制封面/大图/删除）在选中多个时禁用；打标签/加入文件夹/复制到剪贴板支持多选，始终可用。</summary>
    private void RefreshContextMenuEnabled()
    {
        var single = Vm != null && Vm.SelectedCount <= 1;
        ContextEditButton.IsEnabled = single;
        ContextCopyPosButton.IsEnabled = single;
        ContextCopyNegButton.IsEnabled = single;
        ContextCopyCoverButton.IsEnabled = single;
        ContextBigImageButton.IsEnabled = single;
        ContextDeleteButton.IsEnabled = single;
        ContextTagButton.IsEnabled = Vm?.HasSelection ?? false;
        ContextAddToFolderButton.IsEnabled = true;
        ContextCopyClipboardButton.IsEnabled = true;
    }

    private void OnEditMenuItemClick(object? sender, RoutedEventArgs e)
    {
        PromptContextPopup.IsOpen = false;
        if (_contextCard != null) Vm?.EditPromptCommand.Execute(_contextCard);
    }

    private void OnCopyPositiveMenuItemClick(object? sender, RoutedEventArgs e)
    {
        PromptContextPopup.IsOpen = false;
        if (_contextCard != null) Vm?.CopyPositiveCommand.Execute(_contextCard);
    }

    private void OnCopyNegativeMenuItemClick(object? sender, RoutedEventArgs e)
    {
        PromptContextPopup.IsOpen = false;
        if (_contextCard != null) Vm?.CopyNegativeCommand.Execute(_contextCard);
    }

    private void OnCopyCoverMenuItemClick(object? sender, RoutedEventArgs e)
    {
        PromptContextPopup.IsOpen = false;
        if (_contextCard != null) Vm?.CopyCoverCommand.Execute(_contextCard);
    }

    private void OnBigImageMenuItemClick(object? sender, RoutedEventArgs e)
    {
        PromptContextPopup.IsOpen = false;
        if (_contextCard != null) Vm?.OpenBigImageCommand.Execute(_contextCard);
    }

    /// <summary>右键菜单：批量打标签（对全部选中提示词，支持多选）</summary>
    private void OnBatchTagMenuItemClick(object? sender, RoutedEventArgs e)
    {
        PromptContextPopup.IsOpen = false;
        Vm?.BatchTagCommand.Execute(null);
    }

    private void OnDeleteMenuItemClick(object? sender, RoutedEventArgs e)
    {
        PromptContextPopup.IsOpen = false;
        if (_contextCard != null) Vm?.DeletePromptCommand.Execute(_contextCard);
    }

    /// <summary>右键菜单：加入文件夹（弹窗多选文件夹后保存，Ctrl 多选时批量）</summary>
    private async void OnAddToFolderMenuItemClick(object? sender, RoutedEventArgs e)
    {
        PromptContextPopup.IsOpen = false;
        if (Vm != null)
            await Vm.AddToFolderCommand.ExecuteAsync(null);
    }

    /// <summary>右键菜单：复制选中提示词到共享剪贴板（配合菜单管理页"粘贴"加入目标文件夹）</summary>
    private void OnCopyToClipboardMenuItemClick(object? sender, RoutedEventArgs e)
    {
        PromptContextPopup.IsOpen = false;
        Vm?.CopyToClipboardCommand.Execute(null);
    }
}
