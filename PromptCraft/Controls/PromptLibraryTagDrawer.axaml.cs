using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace PromptCraft.Controls;

/// <summary>词库标签管理抽屉（逻辑在 PromptLibraryTagDrawerModel，宿主仅放置并绑定 DataContext）。</summary>
public partial class PromptLibraryTagDrawer : UserControl
{
    public PromptLibraryTagDrawer()
    {
        InitializeComponent();
    }

    private void OnOverlayClick(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ViewModels.PromptLibrary.PromptLibraryTagDrawerModel vm)
        {
            vm.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }
}
