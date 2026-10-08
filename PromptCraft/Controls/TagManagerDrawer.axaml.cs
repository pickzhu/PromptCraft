using Avalonia.Controls;
using Avalonia.Input;
using PromptCraft.ViewModels.ComfyUI;

namespace PromptCraft.Controls;

/// <summary>标签管理抽屉组件（无业务逻辑，全部走 TagManagerDrawerModel 命令绑定）。</summary>
public partial class TagManagerDrawer : UserControl
{
    public TagManagerDrawer()
    {
        InitializeComponent();
    }

    /// <summary>点击遮罩关闭抽屉。</summary>
    private void OnOverlayClick(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is TagManagerDrawerModel vm)
            vm.CloseCommand.Execute(null);
    }

    /// <summary>新建标签输入框内回车 → 直接执行添加（与点击「添加标签」等价）。</summary>
    private void OnNewTagKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is TagManagerDrawerModel vm
            && vm.AddTagCommand.CanExecute(null))
        {
            e.Handled = true;
            vm.AddTagCommand.Execute(null);
        }
    }
}
