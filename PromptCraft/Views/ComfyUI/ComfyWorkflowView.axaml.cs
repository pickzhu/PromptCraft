using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PromptCraft.BaseModel;
using PromptCraft.ViewModels.ComfyUI;

namespace PromptCraft.Views.ComfyUI;

public partial class ComfyWorkflowView : PageBase
{
    public ComfyWorkflowView(System.IServiceProvider service) : base(service)
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ComfyWorkflowModel vm)
            await vm.OnLoadedAsync();
    }

    /// <summary>卡片按下：右键打开"加入文件夹"菜单；双击打开工作流详情。</summary>
    private async void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not ComfyWorkflowModel vm) return;
        if (sender is not Border { DataContext: WorkflowItem item }) return;

        // 右键：记录上下文工作流并打开菜单
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            vm.SetContextWorkflow(item);
            WorkflowContextTitle.Text = item.Name;
            WorkflowContextPopup.IsOpen = true;
            e.Handled = true;
            return;
        }

        if (e.ClickCount >= 2)
        {
            await vm.OpenDetailCommand.ExecuteAsync(item);
        }
    }

    /// <summary>右键菜单：加入文件夹（弹窗多选文件夹后保存）</summary>
    private async void OnAddToFolderMenuItemClick(object? sender, RoutedEventArgs e)
    {
        WorkflowContextPopup.IsOpen = false;
        if (DataContext is ComfyWorkflowModel vm)
            await vm.AddToFolderCommand.ExecuteAsync(null);
    }
}
