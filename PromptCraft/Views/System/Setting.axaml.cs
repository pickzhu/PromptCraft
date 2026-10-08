using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PromptCraft.BaseModel;
using PromptCraft.Consts.Event;
using PromptCraft.UILib.Helpers;
using PromptCraft.ViewModels.System;
using SukiUI.Controls;
using System;
using System.Linq;

namespace PromptCraft;

public partial class Setting : PageBase
{
    public Setting(IServiceProvider service) : base(service)
    {
        InitializeComponent();
        _notice.Subscribe(EventNameConst.SystemLangueChageEvent, OnSystemLangueChanged);
        // 页头标题是 SukiStackPage 在 Content 设置时的一次性快照，不会随绑定/属性变化自动刷新。
        // ViewService 在 view 创建后（尚未 attach）就设置了 DataContext，因此 Loaded 时 DataContext 必已就绪，
        // 只需在首次加载/页面重挂（Loaded）与语言切换（OnSystemLangueChanged）时刷新页头。
        // 注意：不要用 DataContextChanged 触发——该事件在 view 未 attach、模板未应用时也会触发，
        // 此阶段做模板遍历有风险（可能导致页面渲染中断/消失），且此时页头 TextBlock 尚未创建，刷新也无效。
        Loaded += (_, _) =>
        {
            RefreshStackHeaderTitle(0, "Loaded");
            // Loaded 冒泡时父（本页）先于子（SukiStackPage）处理，页头可能尚未就绪；
            // 延迟到 UI 队列末尾再补一次，确保模板应用后页头 TextBlock 已存在。
            Dispatcher.UIThread.Post(() => RefreshStackHeaderTitle(0, "Post"), DispatcherPriority.Background);
        };
    }

    private void ModelCombo_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox cb || DataContext is not SettingModel vm) return;
        var tb = cb.GetTemplateDescendants().OfType<TextBox>().FirstOrDefault();
        var name = tb?.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        vm.EnsureModelInPool(name);
        if (cb.DataContext is ProviderModelRow row && row.ModelName != name)
            row.ModelName = name;
    }

    /// <summary>提供商编辑表单的输入框失焦：自动保存（不弹模态、不打断用户）。</summary>
    private async void ProviderEditor_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingModel vm) return;
        if (string.IsNullOrWhiteSpace(vm.ProviderEditName) || string.IsNullOrWhiteSpace(vm.ProviderEditBaseUrl)) return;
        try { await vm.SaveProviderCommand.ExecuteAsync(null); } catch { }
    }

    protected override void OnSystemLangueChanged(object? obj)
    {
        RefreshStackHeaderTitle(0, "Lang");
    }

    /// <summary>
    /// SukiStackPage 的页头标题是 Content 设置时的一次性字符串快照（只有 Content 变化才会重算），
    /// 且 Avalonia 12 编译 XAML 下 Name="{Binding}" 绑定失效（Name 恒 null，页头 fallback 为类型名）。
    /// 因此首次加载、页面重挂、语言切换时都需要手动把页头文本刷新为 VM 的最新值。
    /// 直接通过 this.Content（XAML 根就是 SukiStackPage）定位，避免模板遍历的时序/异常风险；
    /// 失败时延迟重试（最多 5 次），整体 try/catch，页头刷新失败绝不影响页面渲染。
    /// 每步诊断写入 LogService（category=Setting），便于确认卡点。
    /// </summary>
    private void RefreshStackHeaderTitle(int attempt, string why)
    {
        try
        {
            if (DataContext is not SettingModel vm)
            {
                //Log(string.Format(_local["SettingHeaderSkipDataContext"], why, attempt, DataContext?.GetType().Name ?? "null"));
                Retry(attempt, why);
                return;
            }
            if (this.Content is not SukiStackPage sp)
            {
                //Log(string.Format(_local["SettingHeaderSkipContent"], why, attempt, this.Content?.GetType().Name ?? "null"));
                Retry(attempt, why);
                return;
            }
            var header = PrivateObjectPropertyHelp.GetObjectProperty<SukiStackPage, StackPanel>(sp, "_stackHeaders");
            if (header == null)
            {
                //Log(string.Format(_local["SettingHeaderSkipStack"], why, attempt));
                Retry(attempt, why);
                return;
            }
            AvaloniaList<Control> root = ((AvaloniaList<Control>)(((System.Collections.ICollection)header.Children).SyncRoot));
            if (root.Count == 0)
            {
                //Log(string.Format(_local["SettingHeaderSkipChildren"], why, attempt));
                Retry(attempt, why);
                return;
            }
            if (root[0] is not TextBlock text)
            {
                //Log(string.Format(_local["SettingHeaderSkipRoot"], why, attempt, root[0].GetType().Name));
                Retry(attempt, why);
                return;
            }
            text.Text = vm.Themeing;
            //Log(string.Format(_local["SettingHeaderSuccess"], why, attempt, text.Text));
        }
        catch (Exception ex)
        {
            Log(string.Format(_local["SettingHeaderError"], why, attempt, ex.Message));
            Retry(attempt, why);
        }
    }

    private void Retry(int attempt, string why)
    {
        if (attempt >= 5) return;
        Dispatcher.UIThread.Post(() => RefreshStackHeaderTitle(attempt + 1, why), DispatcherPriority.Background);
    }

    private static void Log(string message) => PromptCraft.Service.LogService.Instance.Info(message, "Setting");

    /// <summary>
    /// 可编辑模型下拉框行为（点击文本框打开下拉）。
    /// 原实现：在 PointerReleased 上无条件 post 打开下拉，导致——
    ///   1) 点选下拉项后 popup 已因选中而关闭，但释放事件仍冒泡到此处，读到的 IsDropDownOpen=false，
    ///      于是又把下拉重新打开（"选中模型后下拉仍展开"）；
    ///   2) 下拉"关了就重开"，用户紧接着点第二个模型时 popup 尚未就绪，点击落到 ComboBox 本体，
    ///      表现为"切换不成功、仍显示上次的模型"。
    /// 新实现：仅在"按下瞬间下拉是关闭的 + 释放源在可编辑文本框内"时才打开下拉；
    /// 下拉项选择、箭头/边框的开关均由原生逻辑处理，绝不干预。
    /// </summary>
    private void ComboBox_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox comboBox) return;
        // Loaded 可能因行虚拟化/重新挂载对同一实例重复触发，避免重复挂处理器
        if (comboBox.Tag is ModelComboBehaviorMarker) return;
        comboBox.Tag = new ModelComboBehaviorMarker();
        AttachModelComboBehavior(comboBox);
    }

    private sealed class ModelComboBehaviorMarker { }

    private static void AttachModelComboBehavior(ComboBox comboBox)
    {
        bool wasOpenAtPress = false;

        comboBox.AddHandler(InputElement.PointerPressedEvent, (_, _) =>
        {
            // 记录按下瞬间下拉是否已打开：下拉项点击、点文本框关闭等按下都算"已打开"
            wasOpenAtPress = comboBox.IsDropDownOpen;
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        comboBox.AddHandler(InputElement.PointerReleasedEvent, (_, args) =>
        {
            if (comboBox.IsDropDownOpen || wasOpenAtPress) return;
            if (args.Source is not Visual source || !IsInsideTextBox(comboBox, source)) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!comboBox.IsDropDownOpen) comboBox.IsDropDownOpen = true;
            }, DispatcherPriority.Background);
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        // 下拉项点击兜底（对齐扩写页提示词工程 TrySelectFromPointer）：SukiUI nightly 的 ComboBoxItem
        // 样式可能把 PointerReleased 标记 Handled，导致项自身选中逻辑被跳过（SelectionChanged 不触发）。
        // 模型池很大时点击选中不稳定，这里从 e.Source 向上找 ComboBoxItem → 手动 SelectedIndex + 回写文本。
        comboBox.AddHandler(InputElement.PointerReleasedEvent, (_, args) =>
        {
            if (args.InitialPressMouseButton != MouseButton.Left) return;
            if (args.Source is not Visual src) return;
            var item = FindVisualParent<ComboBoxItem>(src);
            if (item?.DataContext is not ProviderModelRow row) return;
            var items = comboBox.Items;
            if (items == null) return;
            for (var i = 0; i < items.Count; i++)
            {
                if (Equals(items[i], row))
                {
                    comboBox.SelectedIndex = i;          // 正常路径同值幂等；缺陷路径兜底选中
                    comboBox.Text = row.ModelName;       // 回写可编辑文本（Text 双向绑定同值幂等）
                    return;
                }
            }
        }, RoutingStrategies.Bubble);

        // Avalonia 12 ComboBox.OnGotFocus 会 _inputTextBox.SelectAll()（原生行为：聚焦即全选文本）。
        // 实例处理器在类处理器之后执行，这里把光标移到末尾，取消"选中模型后输入框整段高亮"。
        comboBox.GotFocus += (_, _) =>
        {
            var tb = comboBox.GetTemplateDescendants().OfType<TextBox>().FirstOrDefault();
            if (tb == null) return;
            var len = tb.Text?.Length ?? 0;
            if (tb.SelectionStart != len || tb.SelectionEnd != len)
            {
                tb.SelectionStart = len;
                tb.SelectionEnd = len;
            }
        };
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

    private static bool IsInsideTextBox(ComboBox comboBox, Visual source)
    {
        var textBox = comboBox.GetTemplateDescendants().OfType<TextBox>().FirstOrDefault();
        if (textBox == null) return false;
        for (var v = source; v != null; v = v.GetVisualParent())
        {
            if (v == textBox) return true;
            if (v == comboBox) return false;
        }
        return false;
    }
}