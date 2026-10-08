using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using PromptCraft.ViewModels.System;

namespace PromptCraft;

public partial class PromptPlazaView : UserControl
{
    public PromptPlazaView()
    {
        InitializeComponent();
        // 运行时 XAML 加载下 x:Name 字段不赋值，按项目惯例用 FindControl 获取（对齐 ExpandView MediaDropZone）
        var sv = this.FindControl<ScrollViewer>("PlazaScroll");
        if (sv != null) sv.PropertyChanged += OnPlazaScrollPropertyChanged;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>滚动到底部（距底 &lt; 一屏）触发加载下一页（订阅 Offset/Extent/Viewport 变化，跨 Avalonia 版本稳定）。
    /// 列数/列宽由 WaterfallPanel 自行处理，此处只负责分页。</summary>
    private void OnPlazaScrollPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ScrollViewer.OffsetProperty
            && e.Property != ScrollViewer.ExtentProperty
            && e.Property != ScrollViewer.ViewportProperty)
        {
            return;
        }
        if (sender is not ScrollViewer sv) return;
        if (sv.Extent.Height - (sv.Offset.Y + sv.Viewport.Height) < 120
            && DataContext is PromptPlazaViewModel vm && vm.HasMore && !vm.IsLoadingMore)
        {
            vm.LoadMoreCommand.Execute(null);
        }
    }

    /// <summary>点击卡片打开详情抽屉（卡片为 Border 直挂交互，无 Button 动画/呼吸）。</summary>
    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && sender is Control { DataContext: PlazaItem item }
            && DataContext is PromptPlazaViewModel vm)
        {
            vm.OpenDetailCommand.Execute(item);
        }
    }

    /// <summary>点击遮罩层关闭详情抽屉（对齐 PromptMaster 抽屉遮罩点击关闭）。</summary>
    private void OnMaskPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is PromptPlazaViewModel vm && vm.IsDetailOpen)
        {
            vm.CloseDetailCommand.Execute(null);
        }
    }
}
