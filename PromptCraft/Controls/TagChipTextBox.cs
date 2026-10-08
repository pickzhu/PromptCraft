using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using PromptCraft.ViewModels.System;
using System;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace PromptCraft.Controls;

/// <summary>
/// 素材 chip 输入框：继承 TextBox（光标/IME/选区/剪贴板/滚动等编辑逻辑 100% 保留），
/// 模板内 TextPresenter 与 TagChipLayer 同位放置，chip 与文本同源同坐标渲染。
/// 文本层仍是 <Picture N> 原样；Backspace/Delete 整段删除由页面 HandleTagDeletion 处理。
/// </summary>
public sealed class TagChipTextBox : TextBox
{
    private TagChipLayer? _layer;
    private TextPresenter? _presenter;
    private ExpandViewModel? _vm;
    private bool _subscribed;

    /// <summary>触发 chip 自绘层重绘（外部滚动容器/素材变化时调用）。</summary>
    public void InvalidateChips() => _layer?.InvalidateChips();

    /// <summary>
    /// 获取光标在 relativeTo 控件坐标系下的 Rect（用于定位 @mention 弹出层等）。
    /// 返回 null 当 presenter/layout 尚未就绪。
    /// </summary>
    public Rect? GetCaretRectRelativeTo(Control relativeTo)
    {
        var presenter = _presenter;
        var layout = presenter?.TextLayout;
        if (presenter == null || layout == null) return null;
        var caret = Math.Clamp(CaretIndex, 0, (Text ?? "").Length);
        var rect = layout.HitTestTextPosition(caret);
        var topLeft = presenter.TranslatePoint(new Point(rect.X, rect.Y), relativeTo);
        if (topLeft == null) return null;
        return new Rect(topLeft.Value, new Size(rect.Width, rect.Height));
    }

    private static readonly Regex TagRegex = new(@"<(Picture|Video|Audio|File)\s+\d+>");

    /// <summary>
    /// 在控件内部最先拦截 Backspace/Delete：素材 tag（<Picture N>）作为一个整体删除
    /// （对齐 PromptMaster 删除 contenteditable chip 元素）。必须在类内 override 处理，
    /// 因为 XAML 层 KeyDown（bubble）在 TextBox 内部逐字符删除之后才触发，拦截已晚。
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Back || e.Key == Key.Delete)
        {
            if (HandleTagDeletion(this, e.Key == Key.Delete))
            {
                e.Handled = true;
                return; // 不调 base → 阻止 TextBox 原生逐字符删除
            }
        }
        base.OnKeyDown(e);
    }

    /// <summary>Backspace/Delete 整段删除素材 tag（命中则已改文本并返回 true）。</summary>
    private static bool HandleTagDeletion(TextBox tb, bool forward)
    {
        var text = tb.Text ?? "";
        if (text.Length == 0) return false;
        var caret = Math.Clamp(tb.CaretIndex, 0, text.Length);
        // 找光标所在/紧邻的 tag
        Match? hit = null;
        foreach (Match m in TagRegex.Matches(text))
        {
            var start = m.Index;
            var end = m.Index + m.Length;
            if (forward)
            {
                // Delete：光标在 tag 内，或光标紧贴 tag 前
                if (start <= caret && caret <= end) { hit = m; break; }
                if (start == caret) { hit = m; break; }
            }
            else
            {
                // Backspace：光标在 tag 内，或光标在 tag 后连续空格区域内（含 tag 后随空格）→ 整段删 tag+全部空格
                if (start <= caret && caret <= end) { hit = m; break; }
                if (end < caret && text[end] == ' ')
                {
                    int spEnd = end;
                    while (spEnd < text.Length && text[spEnd] == ' ') spEnd++;
                    if (caret <= spEnd) { hit = m; break; }
                }
            }
            if (start > caret) break;
        }
        if (hit == null) return false;
        // 删除 tag + 后随单个空格（对齐浏览器元素删除）
        //var endIndex = hit.Index + hit.Length;
        //if (endIndex < text.Length && text[endIndex] == ' ') endIndex++;
        //tb.Text = text.Remove(hit.Index, endIndex - hit.Index);
        //tb.CaretIndex = Math.Max(0, hit.Index);

        // 整段范围：前导空格 + tag + 全部后随空格
        int delStart = hit.Index > 0 && text[hit.Index - 1] == ' ' ? hit.Index - 1 : hit.Index;
        int delEnd = hit.Index + hit.Length;
        while (delEnd < text.Length && text[delEnd] == ' ') delEnd++;
        tb.Text = text.Remove(delStart, delEnd - delStart);
        tb.CaretIndex = Math.Max(0, delStart);

        return true;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _placeholder = e.NameScope.Find<TextBlock>("PART_Placeholder");
        var presenter = e.NameScope.Find<TextPresenter>("PART_TextPresenter");
        var layer = e.NameScope.Find<TagChipLayer>("PART_TagChipLayer");
        if (presenter != null && layer != null)
        {
            _presenter = presenter;
            _layer = layer;
            layer.Presenter = presenter;
            layer.IsHitTestVisible = false;
            // 文本变化 → chip 重绘（TextChanged 时 presenter.TextLayout 可能还是旧布局，下一帧再刷一次）+ 水印
            TextChanged += (_, _) =>
            {
                layer.InvalidateChips();
                Dispatcher.UIThread.Post(layer.InvalidateChips, DispatcherPriority.Loaded);
                UpdatePlaceholder();
            };
            // 模板内滚动（文本超高内部滚动）→ chip 跟随重绘
            var scroller = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
            if (scroller != null)
                scroller.ScrollChanged += (_, _) => layer.InvalidateChips();
            // 光标移动 → 重绘（光标在 chip 内时需在 chip 背景镂空，让 presenter 光标透出）
            PropertyChanged += (_, e) =>
            {
                if (e.Property.Name == nameof(CaretIndex)) layer.InvalidateChips();
            };
            layer.InvalidateChips();
        }
        // 素材列表/缩略图变化 → chip 重绘（视频抽帧异步出缩略图后 chip 图标要刷新）
        DataContextChanged += (_, _) => SubscribeVm(DataContext);
        SubscribeVm(DataContext);
        UpdatePlaceholder();
    }

    private TextBlock? _placeholder;

    /// <summary>水印显隐：Text 空且无预编辑文本时显示占位文案。</summary>
    private void UpdatePlaceholder()
    {
        if (_placeholder != null)
            _placeholder.IsVisible = string.IsNullOrEmpty(Text);
    }

    private void SubscribeVm(object? dataContext)
    {
        var vm = dataContext as ExpandViewModel;
        if (vm == _vm && _subscribed) return;
        if (_vm != null && _subscribed)
        {
            _vm.ReferenceMedia.CollectionChanged -= OnMediaCollectionChanged;
            foreach (var m in _vm.ReferenceMedia)
                m.PropertyChanged -= OnMediaItemPropertyChanged;
        }
        _vm = vm;
        _subscribed = vm != null;
        if (vm == null) return;
        vm.ReferenceMedia.CollectionChanged += OnMediaCollectionChanged;
        foreach (var m in vm.ReferenceMedia)
            m.PropertyChanged += OnMediaItemPropertyChanged;
    }

    private void OnMediaCollectionChanged(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (var o in e.OldItems)
                if (o is ExpandViewModel.MediaItem mi) mi.PropertyChanged -= OnMediaItemPropertyChanged;
        if (e.NewItems != null)
            foreach (var o in e.NewItems)
                if (o is ExpandViewModel.MediaItem mi) mi.PropertyChanged += OnMediaItemPropertyChanged;
        _layer?.InvalidateChips();
    }

    private void OnMediaItemPropertyChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ExpandViewModel.MediaItem.Thumbnail) or nameof(ExpandViewModel.MediaItem.HasThumbnail))
            _layer?.InvalidateChips();
    }
}
