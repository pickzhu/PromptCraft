using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PromptCraft.BaseModel;
using PromptCraft.ViewModels.ComfyUI;

namespace PromptCraft.Views.ComfyUI;

public partial class ThumbnailPickerView : PageBase
{
    public ThumbnailPickerView(System.IServiceProvider service) : base(service)
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ThumbnailPickerModel vm)
            await vm.LoadAsync();
    }

    /// <summary>滚动接近底部时自动加载更多（无需点击）。</summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not ThumbnailPickerModel vm) return;
        // ScrollChangedEventArgs 只携带 Delta，绝对位置取事件源 ScrollViewer 自身
        if (sender is not ScrollViewer sv || sv.Extent.Height <= 0 || sv.Viewport.Height <= 0) return;
        var ratio = (sv.Offset.Y + sv.Viewport.Height) / sv.Extent.Height;
        if (ratio >= 0.85) vm.LoadMoreCommand.Execute(null);
    }

    /// <summary>单击选中；双击直接触发确定（关闭弹窗并返回选中图片）。</summary>
    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not ThumbnailPickerModel vm) return;
        if (sender is not Border { DataContext: ThumbnailPickerItem item }) return;

        vm.SelectItemCommand.Execute(item);
        if (e.ClickCount >= 2)
        {
            vm.ConfirmCommand.Execute(null);
            e.Handled = true;
        }
    }
}