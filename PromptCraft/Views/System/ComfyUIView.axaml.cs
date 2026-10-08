using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PromptCraft.BaseModel;
using PromptCraft.ViewModels.System;
using System;

namespace PromptCraft;

/// <summary>
/// ComfyUI 内嵌浏览页：视图被 ViewLocator 按单例 ViewModel 缓存复用。
/// 导航/刷新由 ViewModel 命令触发事件，本视图驱动 NativeWebView 控件执行。
/// </summary>
public partial class ComfyUIView : PageBase
{
    private ComfyUIViewModel? _vm;
    private bool _navigated;

    public ComfyUIView(IServiceProvider service) : base(service)
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is ComfyUIViewModel vm)
        {
            AttachVm(vm);
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ComfyUIViewModel vm)
        {
            AttachVm(vm);
            // 视图被缓存复用：仅首次进入导航到配置首页，之后由命令驱动
            if (!_navigated)
            {
                _navigated = true;
                WebView.Source = vm.HomeUri;
            }
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        // 解除事件订阅；不置空 _vm、不 Dispose（视图被复用，见 LogView 注释）
        if (_vm != null)
        {
            _vm.NavigateRequested -= OnNavigateRequested;
            _vm.RefreshRequested -= OnRefreshRequested;
        }
        base.OnUnloaded(e);
    }

    /// <summary>绑定 ViewModel 事件（幂等：同一实例重复调用直接跳过）。</summary>
    private void AttachVm(ComfyUIViewModel vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm != null)
        {
            _vm.NavigateRequested -= OnNavigateRequested;
            _vm.RefreshRequested -= OnRefreshRequested;
        }
        _vm = vm;
        _vm.NavigateRequested += OnNavigateRequested;
        _vm.RefreshRequested += OnRefreshRequested;
    }

    private void OnNavigateRequested(Uri uri)
    {
        // 设置 Source 等价于 Navigate()；同址再导航仅导致重载，可接受
        WebView.Source = uri;
    }

    private void OnRefreshRequested()
    {
        WebView.Refresh();
    }

    /// <summary>地址栏回车等同点击「前往」。</summary>
    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm != null)
        {
            _vm.GoCommand.Execute(null);
        }
    }
}
