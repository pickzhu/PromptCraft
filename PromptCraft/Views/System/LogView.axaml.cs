using PromptCraft.Models;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using PromptCraft.BaseModel;
using PromptCraft.Interfaces;
using PromptCraft.ViewModels.System;
using SukiUI.Toasts;
using System;
using System.Collections.Specialized;
using System.Globalization;

namespace PromptCraft;

/// <summary>
/// 日志查看页视图：日志倒序展示（最新在上），新日志到来时若用户停留在顶部则自动保持在顶部。
/// </summary>
public partial class LogView : PageBase
{
    private LogViewModel? _vm;

    public LogView(IServiceProvider service) : base(service)
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // ViewLocator 按 ViewModel 实例缓存视图：LogViewModel 是单例，
        // 视图实例跨页面切换永久复用，DataContext 只在首次创建时设置一次；
        // 之后切页回来靠 OnLoaded 恢复绑定（此时 DataContext 未变，此事件不会再次触发）。
        if (DataContext is LogViewModel vm)
        {
            AttachVm(vm);
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // 视图被缓存复用：每次进入页面都确保绑定与事件订阅（幂等，重复调用无副作用）
        if (DataContext is LogViewModel vm)
        {
            AttachVm(vm);
        }
        ScrollToTop();
    }

    /// <summary>绑定 ViewModel 并订阅集合事件（幂等：同一实例重复调用直接跳过）。</summary>
    private void AttachVm(LogViewModel vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm != null)
        {
            _vm.Entries.CollectionChanged -= OnEntriesChanged;
        }
        _vm = vm;
        _vm.Entries.CollectionChanged += OnEntriesChanged;
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || _vm == null || _vm.Entries.Count == 0)
            return;
        // 新日志可能来自任意线程，统一回 UI 线程处理
        Dispatcher.UIThread.Post(() =>
        {
            // 用户停留在顶部（正在看最新日志）才自动跟随；若在翻阅历史则不打扰
            if (IsAtTop()) ScrollToTop();
        });
    }

    /// <summary>判断列表是否停在顶部（偏移 ≈ 0）。</summary>
    private bool IsAtTop()
    {
        var scroll = LogList.Scroll;
        return scroll == null || scroll.Offset.Y <= 1;
    }

    /// <summary>滚动到第一条（最新，索引 0）。</summary>
    private void ScrollToTop()
    {
        if (_vm == null || _vm.Entries.Count == 0) return;
        LogList.ScrollIntoView(_vm.Entries[0]);
    }

    /// <summary>
    /// 双击日志条目：把该条完整日志（时间戳 + 级别 + 类别 + 正文 + 异常）复制到剪贴板。
    /// </summary>
    private async void OnLogDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm == null || LogList.SelectedItem is not LogEntry entry) return;

        var top = TopLevel.GetTopLevel(LogList);
        if (top?.Clipboard == null) return;

        try
        {
            await top.Clipboard.SetValueAsync(DataFormat.Text, entry.ToLine());
            var preview = entry.Message.Length > 40
                ? entry.Message[..40] + "..."
                : entry.Message;
            _vm.Status = string.Format(_local["CopiedStatus"], preview);
            ShowToast(_local["LogCopiedToast"], preview);
        }
        catch (Exception ex)
        {
            _vm.Status = string.Format(_local["CopyFailedStatus"], ex.Message);
            ShowToast(_local["CopyFailedToast"], ex.Message);
        }
    }

    /// <summary>
    /// 轻量 Toast 提示：显示在应用角落，数秒后自动消失，点击可提前关闭。
    /// 不使用模态框，避免打断操作。
    /// </summary>
    private void ShowToast(string title, string content)
    {
        try
        {
            var toastManager = _service.GetService(typeof(ISukiToastManager)) as ISukiToastManager;
            if (toastManager == null) return;

            var toast = FluentSukiToastBuilder.CreateSimpleInfoToast(toastManager);
            toast.SetTitle(title);
            toast.SetContent(content);
            toast.SetCanDismissByClicking(true);
            toast.Toast.DismissTimeout = TimeSpan.FromSeconds(3); // 3 秒后自动消失
            toast.Queue();
        }
        catch
        {
            // Toast 组件异常不影响复制功能本身
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        // 视图被 SukiMainHost 缓存复用（单例 ViewModel → 同一视图实例反复进入/离开）。
        // 离开时仅解除集合订阅；不置空 _vm、不 Dispose：
        // 置空后 DataContext 未变不会再触发 DataContextChanged，_vm 将永远为 null；
        // Dispose 表示生命周期终结，被复用的控件 Dispose 后无法恢复（语言事件订阅也会丢失）。
        if (_vm != null)
        {
            _vm.Entries.CollectionChanged -= OnEntriesChanged;
        }
        base.OnUnloaded(e);
    }
}

/// <summary>
/// 日志级别 → 前景色转换。Info 返回 UnsetValue 以使用默认前景色（兼容明暗主题）。
/// </summary>
public class LogLevelToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is LogLevel level)
        {
            return level switch
            {
                LogLevel.Trace => Brushes.Gray,
                LogLevel.Debug => Brushes.SlateGray,
                LogLevel.Info => AvaloniaProperty.UnsetValue,
                LogLevel.Warn => Brushes.DarkOrange,
                LogLevel.Error => Brushes.IndianRed,
                LogLevel.Fatal => Brushes.Magenta,
                _ => Brushes.Gray
            };
        }
        return Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
