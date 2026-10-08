using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PromptCraft.BaseModel;
using PromptCraft.ViewModels.ComfyUI;

namespace PromptCraft.Views.ComfyUI;

public partial class WorkflowDetailView : PageBase
{
    public WorkflowDetailView(System.IServiceProvider service) : base(service)
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkflowDetailModel vm)
            await vm.OnLoadedAsync();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkflowDetailModel vm)
            vm.Detach();
    }

    /// <summary>双击执行历史条目：打开该记录第一张输出图片的详情弹窗（与图库一致）。</summary>
    private void OnJobDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { DataContext: WorkflowJobItem item }
            && DataContext is WorkflowDetailModel vm)
        {
            vm.OpenImageDetailCommand.Execute(item);
        }
    }

    // ---- 视频预览控制（中央播放按钮 / 左下角暂停、停止；对齐扩写页 MediaVideoView 组件） ----
    private void OnDetailVideoPlay(object? sender, RoutedEventArgs e) => DetailVideoView.PlayPause();
    private void OnDetailVideoPause(object? sender, RoutedEventArgs e) => DetailVideoView.PlayPause();
    private void OnDetailVideoStop(object? sender, RoutedEventArgs e) => DetailVideoView.Stop();
}
