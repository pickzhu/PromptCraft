using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PromptCraft.ViewModels.ComfyUI;
using System;

namespace PromptCraft.Views.ComfyUI;

public partial class ImageCompareView : UserControl
{
    public ImageCompareView()
    {
        InitializeComponent();
    }

    private ImageCompareModel? CompareVm => DataContext as ImageCompareModel;

    /// <summary>画布尺寸变化时同步给 VM，并刷新分割线/裁剪</summary>
    private void OnCompareSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (CompareVm is { } vm && CompareCanvas.Bounds.Width > 0)
        {
            vm.SetCanvasSize(CompareCanvas.Bounds.Width, CompareCanvas.Bounds.Height);
            ApplySplit();
        }
    }

    /// <summary>鼠标在画布上移动：分割线跟随，鼠标左侧显示图A、右侧显示图B</summary>
    private void OnComparePointerMoved(object? sender, PointerEventArgs e)
    {
        if (CompareVm is not { } vm || CompareCanvas.Bounds.Width <= 0) return;
        var pos = e.GetPosition(CompareCanvas);
        vm.SetSplit(pos.X / CompareCanvas.Bounds.Width);
        ApplySplit();
    }

    /// <summary>左键按下也跟随移动（按下即滑动）</summary>
    private void OnComparePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(CompareCanvas).Properties.IsLeftButtonPressed)
            OnComparePointerMoved(sender, e);
    }

    /// <summary>双击画布重置分割到 50%</summary>
    private void OnCompareDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (CompareVm is { } vm)
        {
            vm.ResetSplit();
            ApplySplit();
        }
    }

    /// <summary>
    /// 以真实画布尺寸统一计算分割线位置与上层图A的裁剪矩形。
    /// 关键：Image.Clip 的坐标系是"图A内容自身坐标"（Uniform 缩放后从内容左上角起），
    /// 而竖线/鼠标在画布坐标——两者相差一个左留白宽度。
    /// 因此 Clip 宽度 = 画布位置 − 左留白（换算回内容坐标），视觉分界才能与竖线重合。
    /// </summary>
    private void ApplySplit()
    {
        var w = CompareCanvas.Bounds.Width;
        var h = CompareCanvas.Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var x = w * (CompareVm?.SplitRatio ?? 0.5);

        // 图A在 Image 控件内的 Uniform 内容矩形（居中、等比缩放），求左留白与内容尺寸
        double contentW = 0, contentH = 0, leftMargin = 0;
        if (LeftImage.Source is Bitmap bmp && bmp.PixelSize.Width > 0 && bmp.PixelSize.Height > 0)
        {
            var iw = bmp.PixelSize.Width;
            var ih = bmp.PixelSize.Height;
            var scale = Math.Min(w / (double)iw, h / (double)ih);
            contentW = iw * scale;
            contentH = ih * scale;
            leftMargin = (w - contentW) / 2;
        }

        // 裁剪宽度换算到内容坐标；竖线仍画在画布坐标 x，两者视觉重合
        var clipX = Math.Clamp(x - leftMargin, 0, contentW > 0 ? contentW : w);
        var clipH = contentH > 0 ? contentH : h;
        LeftImage.Clip = new RectangleGeometry { Rect = new Rect(0, 0, clipX, clipH) };

        SplitLine.Height = h;
        Canvas.SetLeft(SplitLine, x);
    }
}
