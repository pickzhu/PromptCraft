using Avalonia.Controls;
using Avalonia.Input;
using PromptCraft.ViewModels.Folders;
using PromptCraft.ViewModels.PromptLibrary;

namespace PromptCraft.Views.Folders;

public partial class FolderPickerView : UserControl
{
    public FolderPickerView()
    {
        InitializeComponent();
    }

    /// <summary>自定义勾选行点击：切换 IsSelected 并同步到 ViewModel（替代 SukiUI CheckBox，修复复选框渲染不全）。</summary>
    private void OnFolderItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: PromptFolderItem item }) return;
        item.IsSelected = !item.IsSelected;
        if (DataContext is FolderPickerModel vm)
            vm.ToggleFolderCommand.Execute(item);
        e.Handled = true;
    }
}
