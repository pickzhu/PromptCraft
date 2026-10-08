using Avalonia.Controls;
using Avalonia.Input;
using PromptCraft.ViewModels.PromptLibrary;

namespace PromptCraft.Views.PromptLibrary;

/// <summary>提示词编辑对话框：封面选择 / 标签勾选（保存/关闭按钮在宿主 foot）。</summary>
public partial class PromptEditDialogView : UserControl
{
    public PromptEditDialogView()
    {
        InitializeComponent();
    }

    private PromptEditModel? Vm => DataContext as PromptEditModel;

    private void OnCoverClick(object? sender, PointerPressedEventArgs e)
    {
        Vm?.PickCoverCommand.Execute(null);
        e.Handled = true;
    }
}
