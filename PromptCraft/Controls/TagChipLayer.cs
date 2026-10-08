using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using PromptCraft.ViewModels.System;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PromptCraft.Controls;

/// <summary>
/// 素材 chip 自绘层：作为 TagChipTextBox 模板内与 TextPresenter 同位同尺寸的覆盖层，
/// 在 Render 里直接绘制素材 chip（缩略图/类型图标 + @图片N + 边框）。
/// 样式严格对齐 PromptMaster 前端 ul()：
///   display:inline-flex;height:22px;margin:0 3px;padding:1px 7px 1px 2px;
///   border-radius:4px;border:1px solid {kind 色};background:transparent(用输入框背景填充遮原文);
///   color:inherit(继承输入框文字色);font-size:13px;gap:4px;图片 18px 圆角 3px。
/// 与 presenter 同位同尺寸 → 两者坐标系完全一致，chip 位置直接取
/// presenter.TextLayout.HitTestTextPosition 的字符 Rect（presenter 本地值，零坐标转换）。
/// </summary>
public sealed class TagChipLayer : Control
{
    private static readonly Regex TagRegex = new(@"<(Picture|Video|Audio|File)\s+\d+>", RegexOptions.Compiled);
    private const double ChipHeight = 22;      // 对齐 ul() height 22px
    private const double ChipFontSize = 13;    // 对齐 ul() font-size 13px
    private const double ChipRadius = 4;       // 对齐 ul() border-radius 4px
    private const double MarginX = 3;          // 对齐 ul() margin 0 3px
    private const double PadLeft = 2;          // 对齐 ul() padding 1px 7px 1px 2px
    private const double PadRight = 7;
    private const double Gap = 4;              // 对齐 ul() gap 4px（图标与文字间距）
    private const double IconSize = 18;        // 对齐 ul() 图片 18px
    private readonly Dictionary<string, TextLayout> _labelCache = new(StringComparer.Ordinal);

    /// <summary>被覆盖的文本渲染控件（坐标基准，模板内同位设置）。
    /// 订阅其 Background/TextLayout 属性变化：SukiUI 切主题时 presenter.Background
    /// （模板内 DynamicResource SukiDarkColor）必然更新，本层随之强制重绘 chip。</summary>
    private TextPresenter? _presenter;

    public TextPresenter? Presenter
    {
        get => _presenter;
        set
        {
            if (ReferenceEquals(_presenter, value)) return;
            if (_presenter != null) _presenter.PropertyChanged -= OnPresenterPropertyChanged;
            _presenter = value;
            if (_presenter != null) _presenter.PropertyChanged += OnPresenterPropertyChanged;
        }
    }

    private void OnPresenterPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // 主题切换（背景/前景色变）或布局/文本变化 → chip 重绘（InvalidateVisual 同帧合并，开销可忽略）
        InvalidateChips();
    }

    /// <summary>chip 背景画笔：模板内绑定 {DynamicResource SukiDarkColor}。
    /// DynamicResource 订阅在本控件自身属性上，SukiUI 切换主题（资源字典替换）时属性自动更新，
    /// AffectsRender 注册后属性变化自动触发重绘，解决"主题切换后 chip 保持旧背景色"问题。</summary>
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<TagChipLayer, IBrush?>(nameof(Background));

    public static readonly StyledProperty<IBrush?> TextColorProperty =
    AvaloniaProperty.Register<TagChipLayer, IBrush?>(nameof(TextColor));

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? TextColor
    {
        get => GetValue(TextColorProperty);
        set => SetValue(TextColorProperty, value);
    }

    static TagChipLayer()
    {
        AffectsRender<TagChipLayer>(BackgroundProperty, TextColorProperty);
    }

    /// <summary>文本/布局/素材变化后调用，触发重绘。</summary>
    public void InvalidateChips() => InvalidateVisual();

    public TagChipLayer()
    {
        // 主题切换（SukiUI 亮/暗）时强制重绘 chip（双保险：若 SukiUI 同时改窗口 ThemeVariant）
        AttachedToVisualTree += (_, _) =>
        {
            var app = Avalonia.Application.Current;
            if (app != null) app.ActualThemeVariantChanged += OnThemeChanged;
            var top = TopLevel.GetTopLevel(this);
            if (top != null) top.ActualThemeVariantChanged += OnThemeChanged;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            var app = Avalonia.Application.Current;
            if (app != null) app.ActualThemeVariantChanged -= OnThemeChanged;
            var top = TopLevel.GetTopLevel(this);
            if (top != null) top.ActualThemeVariantChanged -= OnThemeChanged;
        };
    }

    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateChips();

    public override void Render(DrawingContext context)
    {
        var presenter = Presenter;
        var layout = presenter?.TextLayout;
        var text = presenter?.Text;
        var vm = DataContext as ExpandViewModel;
        if (layout == null || string.IsNullOrEmpty(text) || vm == null) return;

        // 文字颜色 = 输入框文字色（ul() color:inherit）；chip 背景 = presenter 背景优先
        // （模板内 DynamicResource SukiDarkColor，主题切换必然更新且本层已监听重绘），
        // 兜底自身 Background（模板同源绑定），最后固定深色
        var textBrush = presenter?.Foreground ?? TextColor;
        var bgBrush = presenter?.Background ?? Background;

        var byTag = new Dictionary<string, ExpandViewModel.MediaItem>(StringComparer.Ordinal);
        foreach (var m in vm.ReferenceMedia) byTag[m.Tag] = m;

        try
        {
            foreach (Match match in TagRegex.Matches(text))
            {
                if (!byTag.TryGetValue(match.Value, out var item)) continue;
                var start = layout.HitTestTextPosition(match.Index);      // tag 首字符框 Rect（presenter 本地）
                var end = layout.HitTestTextPosition(match.Index + match.Length); // tag 右缘（下一个字符的左缘）
                // chip 覆盖：前导空格 + tag + 后随空格1（空格是真实文本，光标落在后随空格2 → chip 外可见）

                if (Math.Abs(start.Y - end.Y) > 1) continue;              // tag 被拆行则放弃（罕见）
                double lineH = Math.Max(end.Bottom - start.Top, layout.LineHeight > 0 ? layout.LineHeight : (presenter?.FontSize ?? 14) * 1.4);
                double h = ChipHeight; // ul() height:22px 固定（max-height:22px）
                // 左缘：tag 左缘再左移一点（用户要求左边框向左移，视觉与缩略图贴合）；
                // 右缘：收在 tag 右缘（不覆盖后随空格），光标落在空格处（chip 外，可见）。
                double x = start.X - MarginX;
                double w = Math.Max(end.X - start.X, 28) + MarginX + 2;
                double y = Math.Max(0, start.Y + (lineH - h) / 2); // 上边缘防裁剪（不超出 presenter 顶部）
                var rect = new Rect(x, y, w, h);

                var kindBrush = new SolidColorBrush(Color.Parse(item.KindColor));

                // 1) 圆角背景（输入框背景色，遮住 tag 原文）+ 1px kind 色边框
                context.DrawRectangle(bgBrush, new Pen(kindBrush, 1), rect, ChipRadius, ChipRadius);

                // 2) 图标：图片=缩略图；无缩略图时 kind 色字形（图片 🖼 / 视频 ▶ / 音频 ♪），对齐 ul() glyph
                double iconH = Math.Min(h - 4, IconSize);
                var iconRect = new Rect(rect.X + PadLeft, rect.Y + (h - iconH) / 2, iconH, iconH);
                if (item.HasThumbnail && item.Thumbnail != null)
                {
                    // Avalonia DrawImage(source, sourceRect, destRect) —— 源区在前、目标区在后；参数写反会导致缩略图按原图尺寸绘制溢出 chip
                    var src = new Rect(0, 0, item.Thumbnail.PixelSize.Width, item.Thumbnail.PixelSize.Height);
                    context.DrawImage(item.Thumbnail, src, iconRect);
                }
                else
                {
                    var glyph = item.Kind == "图片" ? "🖼" : item.Kind == "视频" ? "▶" : "♪";
                    var gl = GetLabel(glyph, kindBrush, 10);
                    var gp = new Point(iconRect.X + (iconRect.Width - gl.Width) / 2,
                                        iconRect.Y + (iconRect.Height - gl.Height) / 2);
                    gl.Draw(context, gp);
                }

                // 3) 文案 @图片N：color:inherit（输入框文字色），gap 4px，右侧 padding 7px
                var label = GetLabel(item.ChipText, textBrush, ChipFontSize);
                var lp = new Point(iconRect.Right + Gap, rect.Y + (rect.Height - label.Height) / 2);
                label.Draw(context, lp);

                // 4) 光标镂空：光标在 tag 内时，chip 背景在光标字符位置覆盖回背景色留一条 2px 缝，
                //    让 presenter 层绘制的光标透出（对齐 PromptMaster chip 元素的视觉语义：光标不落在 chip 内部显示）
                if (presenter?.CaretIndex >= match.Index && presenter.CaretIndex <= match.Index + match.Length)
                {
                    var caretRect = layout.HitTestTextPosition(presenter.CaretIndex);
                    if (Math.Abs(caretRect.Y - start.Y) <= 1)
                        context.DrawRectangle(bgBrush, null, new Rect(caretRect.X, rect.Y, 2, h));
                }
            }
        }
        catch
        {
            // 布局异常时静默降级为纯文本显示
        }
    }

    private TextLayout GetLabel(string text, IBrush? brush, double fontSize)
    {
        // 缓存 key 必须含画笔颜色：TextLayout 构造时冻结画笔，主题切换后 presenter.Foreground
        // 变化而 text 不变，若 key 不含颜色会命中旧缓存 → chip 字体颜色被固定（本次修复点）
        var brushKey = brush is ISolidColorBrush sc ? sc.Color.ToString() : (brush?.ToString() ?? "none");
        var key = fontSize + "|" + text + "|" + brushKey;
        if (_labelCache.TryGetValue(key, out var cached)) return cached;
        var layout = new TextLayout(text,
            new Typeface(Presenter?.FontFamily ?? FontFamily.Default,
                Presenter?.FontStyle ?? FontStyle.Normal,
                Presenter?.FontWeight ?? FontWeight.Normal),
            fontSize, brush);
        _labelCache[key] = layout;
        return layout;
    }
}
