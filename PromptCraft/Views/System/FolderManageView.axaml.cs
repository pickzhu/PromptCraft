using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PromptCraft.ViewModels.Folders;
using System.Linq;

namespace PromptCraft.Views.Folders;

/// <summary>
/// 文件夹管理页：左栏文件夹列表（三类混排，单击=打开内容，右键=菜单，空白右键=新建），右栏内容区（资产右键=各自操作菜单）。
/// </summary>
public partial class FolderManageView : UserControl
{
    private FolderRow? _contextFolder;
    private FolderAssetItem? _contextAsset;

    public FolderManageView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>页面每次加载（含从其他页面切换回来）主动刷新数据：切页回来内容区不依赖事件驱动也能恢复显示。</summary>
    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FolderManageViewModel vm && !vm.IsLoading)
            await vm.LoadCommand.ExecuteAsync(null);
    }

    private FolderManageViewModel? Vm => DataContext as FolderManageViewModel;

    /// <summary>左栏空白处右键：弹出"新建文件夹"菜单（不再直接弹创建对话框，避免误触）。</summary>
    private void OnFoldersAreaPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        // 仅空白区域触发（命中卡片时由卡片处理器接管并 Handled）
        if (sender is Border { DataContext: FolderManageViewModel } && Vm != null)
        {
            FolderAreaPopup.IsOpen = true;
            e.Handled = true;
        }
    }

    /// <summary>空白区右键菜单：新建文件夹。</summary>
    private void OnAreaNewFolderMenuItemClick(object? sender, RoutedEventArgs e)
    {
        FolderAreaPopup.IsOpen = false;
        Vm?.NewFolderCommand.Execute(null);
    }

    /// <summary>内部空白区右键菜单：把剪贴板资产粘贴到当前打开的文件夹。</summary>
    private void OnAreaPasteMenuItemClick(object? sender, RoutedEventArgs e)
    {
        FolderAreaPopup.IsOpen = false;
        Vm?.PasteToFolderCommand.Execute(null);
    }

    /// <summary>文件夹卡片：右键=菜单（打开/粘贴/重命名/删除）；双击=进入文件夹内部。</summary>
    private void OnFolderCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm == null) return;
        if (sender is not Border { DataContext: FolderRow row }) return;

        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            _contextFolder = row;
            FolderCardTitle.Text = $"{row.Name}（{row.AssetCount}）";
            FolderCardPopup.IsOpen = true;
            e.Handled = true;
            return;
        }

        // 双击进入内部（默认首页只显示文件夹列表，单击不做任何事）
        if (e.ClickCount >= 2)
        {
            Vm.OpenFolderCommand.Execute(row);
            e.Handled = true;
        }
    }

    /// <summary>内容资产卡片：右键=菜单（编辑或详情/移除/加入其他文件夹/复制/删除）；双击=打开详情。</summary>
    private async void OnAssetPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm == null) return;
        if (sender is not Border { DataContext: FolderAssetItem asset }) return;

        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            _contextAsset = asset;
            AssetPopupTitle.Text = asset.Name;
            AssetPopup.IsOpen = true;
            e.Handled = true;
            return;
        }

        // 左键双击打开资产详情（提示词=编辑弹窗；工作流/图库=详情弹窗）
        if (e.ClickCount >= 2)
        {
            await Vm.OpenAssetCommand.ExecuteAsync(asset);
            e.Handled = true;
        }
    }

    // ---- 文件夹卡片菜单 ----

    private void OnFolderOpenMenuItemClick(object? sender, RoutedEventArgs e)
    {
        FolderCardPopup.IsOpen = false;
        if (_contextFolder != null) Vm?.OpenFolderCommand.Execute(_contextFolder);
    }

    private void OnFolderPasteMenuItemClick(object? sender, RoutedEventArgs e)
    {
        FolderCardPopup.IsOpen = false;
        if (_contextFolder != null) Vm?.PasteToFolderCommand.Execute(_contextFolder);
    }

    private void OnFolderRenameMenuItemClick(object? sender, RoutedEventArgs e)
    {
        FolderCardPopup.IsOpen = false;
        if (_contextFolder != null) Vm?.RenameFolderCommand.Execute(_contextFolder);
    }

    private void OnFolderDeleteMenuItemClick(object? sender, RoutedEventArgs e)
    {
        FolderCardPopup.IsOpen = false;
        if (_contextFolder != null) Vm?.DeleteFolderCommand.Execute(_contextFolder);
    }

    // ---- 资产菜单 ----

    private async void OnAssetOpenMenuItemClick(object? sender, RoutedEventArgs e)
    {
        AssetPopup.IsOpen = false;
        if (_contextAsset != null && Vm != null)
            await Vm.OpenAssetCommand.ExecuteAsync(_contextAsset);
    }

    private async void OnAssetRemoveMenuItemClick(object? sender, RoutedEventArgs e)
    {
        AssetPopup.IsOpen = false;
        if (_contextAsset != null && Vm != null)
            await Vm.RemoveFromFolderCommand.ExecuteAsync(_contextAsset);
    }

    private async void OnAssetAddToFolderMenuItemClick(object? sender, RoutedEventArgs e)
    {
        AssetPopup.IsOpen = false;
        if (_contextAsset != null && Vm != null)
            await Vm.AddAssetToFolderCommand.ExecuteAsync(_contextAsset);
    }

    private void OnAssetCopyMenuItemClick(object? sender, RoutedEventArgs e)
    {
        AssetPopup.IsOpen = false;
        if (_contextAsset != null) Vm?.CopyAssetCommand.Execute(_contextAsset);
    }

    private async void OnAssetDeleteMenuItemClick(object? sender, RoutedEventArgs e)
    {
        AssetPopup.IsOpen = false;
        if (_contextAsset != null && Vm != null)
            await Vm.DeleteAssetCommand.ExecuteAsync(_contextAsset);
    }
}
