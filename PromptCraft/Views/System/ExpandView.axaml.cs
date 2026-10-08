using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PromptCraft.BaseModel;
using PromptCraft.Models;
using PromptCraft.ViewModels.System;
using Ke.Bee.Localization.Localizer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PromptCraft;

public partial class ExpandView : PageBase
{
    /// <summary>素材卡播放按钮：视频 → 卡片内 MediaVideoView 组件 PlayPause；音频 → ViewModel PlayMedia。</summary>
    private void OnMediaPlayClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Control c || c.DataContext is not ExpandViewModel.MediaItem item) return;
            if (item.IsVideo)
            {
                // 从按钮向上逐级找含 MediaVideoView 的 Border（卡片根），组件与按钮是兄弟节点
                var vv = c.GetVisualAncestors()
                    .OfType<Border>()
                    .Select(b => b.GetVisualDescendants().OfType<PromptCraft.Controls.MediaVideoView>().FirstOrDefault())
                    .FirstOrDefault(v => v != null);
                vv?.PlayPause();
            }
            else if (item.IsAudio)
            {
                (c.FindAncestorOfType<ItemsControl>()?.DataContext as ExpandViewModel)?.PlayMediaCommand.Execute(item);
            }
        }
        catch (Exception ex)
        {
            PromptCraft.Service.LogService.Instance.Warn(string.Format(Localizer.Instance?["MediaPlayFailedLog"] ?? "播放失败: {0}", ex.Message), "Expand", ex);
        }
    }

    /// <summary>提示词工程下拉：鼠标悬停滚轮不得切换选中值（下拉未展开时拦截滚轮）。</summary>
    private void OnEngineComboWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is ComboBox combo && !combo.IsDropDownOpen) e.Handled = true;
    }

    public ExpandView(IServiceProvider service) : base(service)
    {
        InitializeComponent();
        // 提示词工程下拉：滚轮悬停时不应切换选中项（对齐 PromptMaster：滚轮只滚动页面）
        EngineCombo.PointerWheelChanged += OnEngineComboWheel;
        // SukiUI nightly 的 ComboBox 模板 MaxDropDownHeight 绑定失效：下拉按内容高度完整展开，
        // MiniMaxH3 等长列表（11 项）超出窗口底部 → 底部选项不可见/点不到（反推页列表短所以正常）。
        // 不碰模板：展开后直接把下拉 Popup 的内容容器限高 300 → 内部 ScrollViewer 滚动，全部可点。
        foreach (var combo in new[] { ProviderCombo, RuleCombo, EngineCombo, LengthCombo })
        {
            if (combo == null) continue;
            combo.DropDownOpened += (_, _) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        foreach (var popup in combo.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>())
                            if (popup.Child is Avalonia.Controls.Control c)
                                c.MaxHeight = 300;
                    }
                    catch (Exception ex)
                    {
                        PromptCraft.Service.LogService.Instance.Warn(string.Format(Localizer.Instance?["ExpandDropDownHeightFailedLog"] ?? "", ex.Message), "Expand", ex);
                    }
                }, DispatcherPriority.Loaded);
            };
        }
        // 下拉点击兜底：SukiUI nightly 的 ComboBoxItem 样式可能把 PointerReleased 标记 Handled，
        // 导致项自身的选中逻辑被跳过（SelectionChanged 不触发），但事件仍冒泡到 ComboBox。
        // 从 e.Source 向上找 ComboBoxItem → 手动 SelectedIndex（正常路径为幂等，缺陷路径兜底选中）。
        foreach (var combo in new[] { ProviderCombo, RuleCombo, EngineCombo, LengthCombo })
        {
            if (combo == null) continue;
            combo.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent,
                (_, e) => TrySelectFromPointer(combo, e),
                Avalonia.Interactivity.RoutingStrategies.Bubble);
        }
        // 素材区拖拽（对齐 PromptMaster onDragenter/onDragover/onDrop）
        var zone = this.FindControl<StackPanel>("MediaDropZone");
        if (zone != null)
        {
            zone.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            zone.AddHandler(DragDrop.DropEvent, OnDrop);
        }
        // 创作需求输入框的外层滚动容器：chip 自绘层随滚动重绘（内层滚动由 TagChipTextBox 模板内部处理）
        if (ComposerScroller != null)
            ComposerScroller.ScrollChanged += (_, _) => (DemandBox as PromptCraft.Controls.TagChipTextBox)?.InvalidateChips();
    }

    /// <summary>下拉点击兜底：从 PointerReleased 的点击源向上找 ComboBoxItem，手动选中（SukiUI 缺陷路径补救）。</summary>
    private static void TrySelectFromPointer(ComboBox combo, PointerReleasedEventArgs e)
    {
        try
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            if (e.Source is not Visual src) return;
            var item = FindVisualParent<ComboBoxItem>(src);
            if (item?.DataContext == null) return;
            var target = item.DataContext;
            var items = combo.Items;
            if (items == null) return;
            for (var i = 0; i < items.Count; i++)
            {
                if (Equals(items[i], target))
                {
                    combo.SelectedIndex = i; // 正常路径同值幂等；缺陷路径兜底选中
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            PromptCraft.Service.LogService.Instance.Warn(string.Format(Localizer.Instance?["ExpandDropDownClickFailedLog"] ?? "", ex.Message), "Expand", ex);
        }
    }

    private static T? FindVisualParent<T>(Visual? v) where T : Visual
    {
        while (v != null)
        {
            if (v is T t) return t;
            v = v.GetVisualParent();
        }
        return null;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is not ExpandViewModel vm || vm.IsBusy)
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ExpandViewModel vm || vm.IsBusy)
        {
            e.Handled = true;
            return;
        }
        var paths = new List<string>();
        foreach (var item in e.DataTransfer.Items)
        {
            if (!item.Formats.Contains(Avalonia.Input.DataFormat.File))
                continue;
            var raw = item.TryGetRaw(Avalonia.Input.DataFormat.File);
            switch (raw)
            {
                case IStorageItem storage:
                    {
                        var p = storage.TryGetLocalPath();
                        if (!string.IsNullOrWhiteSpace(p))
                            paths.Add(p);
                        break;
                    }
                case string s when !string.IsNullOrWhiteSpace(s):
                    paths.Add(s);
                    break;
            }
        }
        vm.ProcessMediaDrop(paths);
        e.Handled = true;
    }

    // ============================================================
    // @mention 素材引用（对齐反混淆前端 fl/El/Dl/Pl/Nl；仅媒体型创作需求输入）
    // ============================================================

    /// <summary>onInput 等价：文本变化时检测 @ 前缀（对齐 Ol→Ml）；chip 重绘/水印由 TagChipTextBox 模板内部处理。</summary>
    private void OnDemandTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (DataContext is not ExpandViewModel vm) return;
        var tb = DemandBox;
        if (tb == null) return;
        var text = tb.Text ?? "";
        var caret = Math.Clamp(tb.CaretIndex, 0, text.Length);
        vm.UpdateMention(text.Substring(0, caret));
        // 延迟到布局完成后再定位（TextLayout 需反映最新文本）
        Dispatcher.UIThread.Post(PositionMentionPopup, DispatcherPriority.Loaded);
    }

    /// <summary>把 mention Popup 定位到光标所在行正下方。</summary>
    private void PositionMentionPopup()
    {
        if (MentionPopup == null || DemandBox is not PromptCraft.Controls.TagChipTextBox tb) return;
        var rect = tb.GetCaretRectRelativeTo(tb);
        if (rect == null) return;
        double popupHalf = 160; // 面板宽 320
        double caretX = rect.Value.X;
        // Placement="Bottom" 居中对齐 → HorizontalOffset 从中心偏移，让面板中心落在光标 X
        double centerX = caretX;
        // clamp：面板不超出输入框左右边缘
        if (centerX - popupHalf < 4) centerX = popupHalf + 4;
        if (centerX + popupHalf > tb.Bounds.Width - 4) centerX = tb.Bounds.Width - popupHalf - 4;
        MentionPopup.HorizontalOffset = centerX - tb.Bounds.Width / 2;
        MentionPopup.VerticalOffset = rect.Value.Bottom + 4 - tb.Bounds.Height;
    }

    // ============================================================
    // @mention 交互（文本层存 <Picture N> tag，chip 由模板内 TagChipTextPresenter 在文本层绘制；对齐 Nl/Ml/Ol）
    // ============================================================

    private static readonly Regex TagRegex = new(@"<(Picture|Video|Audio|File)\s+\d+>");

    /// <summary>Backspace/Delete 整段删除素材 tag（对齐浏览器删除 contenteditable chip 元素：tag 作为一个整体）。
    /// 返回是否命中了 tag（命中则已改文本并返回 true，由调用方置 Handled 阻止默认逐字符删除）。</summary>
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
                // Backspace：光标在 tag 内，或光标紧贴 tag 后（含 tag 后随空格）
                if (start <= caret && caret <= end) { hit = m; break; }
                if (end == caret) { hit = m; break; }
                if (end < caret && text[end] == ' ' && end + 1 == caret) { hit = m; break; }
            }
            if (start > caret) break;
        }
        if (hit == null) return false;
        // 删除 tag + 后随单个空格（对齐浏览器元素删除）
        var endIndex = hit.Index + hit.Length;
        if (endIndex < text.Length && text[endIndex] == ' ') endIndex++;
        tb.Text = text.Remove(hit.Index, endIndex - hit.Index);
        tb.CaretIndex = Math.Max(0, hit.Index);
        return true;
    }

    // ============================================================
    // @mention 交互（文本层存 <Picture N> tag，显示层 chip 覆盖；对齐 Nl/Ml/Ol）
    // ============================================================

    /// <summary>onKeydown 等价（对齐 Dl）：mention 打开时拦截方向键/Enter/Tab/Escape。
    /// Backspace/Delete 的整段删除已由 TagChipTextBox 类内 OnKeyDown 处理（XAML bubble 太晚）。</summary>
    private void OnDemandKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ExpandViewModel vm) return;
        var tb = DemandBox;
        if (tb == null) return;
        // mention 面板交互（方向键/Enter/Tab/Escape）
        if (!vm.IsMentionOpen) return;
        switch (e.Key)
        {
            case Key.Down:
                vm.MoveMention(1);
                e.Handled = true;
                break;
            case Key.Up:
                vm.MoveMention(-1);
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Tab:
                if (vm.MentionFiltered.Count > 0)
                {
                    InsertMention(vm);
                    e.Handled = true;
                }
                break;
            case Key.Escape:
                vm.CloseMention();
                e.Handled = true;
                break;
        }
    }

    /// <summary>onBlur 等价（对齐 Pl）：180ms 后关闭（点击面板时不误关）。</summary>
    private void OnDemandLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ExpandViewModel vm) return;
        _ = Task.Delay(180).ContinueWith(_ =>
            Dispatcher.UIThread.Post(() => vm.CloseMention()),
            TaskScheduler.Default);
    }

    /// <summary>面板项点击（onMousedown → Nl(e)，prevent 默认保持焦点）。</summary>
    private void OnMentionItemClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not ExpandViewModel.MediaItem item) return;
        if (DataContext is not ExpandViewModel vm) return;
        vm.SelectMentionItem(item);
        InsertMention(vm);
        e.Handled = true;
    }

    /// <summary>对齐 Nl：插入 `{tag} ` 替换 @ 段，光标定位在 tag 后。</summary>
    private void InsertMention(ExpandViewModel vm)
    {
        var tb = DemandBox;
        if (tb == null) return;
        var text = tb.Text ?? "";
        var caret = Math.Clamp(tb.CaretIndex, 0, text.Length);
        var (newText, newCaret) = vm.ApplyMention(caret);
        if (newCaret < 0) return;
        tb.Text = newText; // 触发 TextChanged → UpdateMention（无 @ 段 → 自动关闭面板）
        tb.CaretIndex = newCaret;
        tb.Focus();
    }

    // ============================================================
    // 模型选择器（对齐反混淆 1072-1142）
    // ============================================================

    /// <summary>触发按钮：打开/关闭 popover（对齐 lt stop）。</summary>
    private void OnModelPickerToggle(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ExpandViewModel vm) return;
        vm.IsModelPickerOpen = !vm.IsModelPickerOpen;
        e.Handled = true;
    }

    /// <summary>点击服务商族（对齐 Bn onMousedown：切换 provider 并刷新模型列表）。</summary>
    private void OnProviderFamilyClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not ProviderConfig p) return;
        if (DataContext is not ExpandViewModel vm) return;
        vm.SelectProviderFamily(p);
        e.Handled = true;
    }

    /// <summary>点击在线模型（对齐 nt：选中并关闭）。</summary>
    private void OnModelClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not ProviderModel m) return;
        if (DataContext is not ExpandViewModel vm) return;
        vm.SelectModelFromPicker(m);
        e.Handled = true;
    }

    /// <summary>去配置（对齐 Gn）。</summary>
    private void OnGoConfigure(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ExpandViewModel vm) return;
        vm.GoConfigureProvider();
        e.Handled = true;
    }
}
