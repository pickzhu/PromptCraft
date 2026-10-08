using Avalonia.Interactivity;
using PromptCraft.BaseModel;

namespace PromptCraft.Views.ComfyUI;

public partial class ImageDetailView : PageBase
{
    public ImageDetailView(System.IServiceProvider service) : base(service)
    {
        InitializeComponent();
    }

    // ---- 视频预览控制（中央播放按钮 / 控制条暂停、停止；复用工作流详情 MediaVideoView 范式） ----
    private void OnDetailVideoPlay(object? sender, RoutedEventArgs e) => DetailVideoView.PlayPause();
    private void OnDetailVideoPause(object? sender, RoutedEventArgs e) => DetailVideoView.PlayPause();
    private void OnDetailVideoStop(object? sender, RoutedEventArgs e) => DetailVideoView.Stop();
}
