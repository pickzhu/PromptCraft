using Avalonia.Interactivity;
using Avalonia.Controls;
using PromptCraft.Consts.Event;
using PromptCraft.Interfaces;
using SukiUI.Controls;
using System;

namespace PromptCraft.Views;

public partial class MainWindow : SukiWindow
{
    private readonly IBaseNotice _notice;

    /// <summary>是否允许真实关闭窗口（托盘"退出"置位；普通关闭 = 最小化到托盘）。</summary>
    private bool _allowClose;

    /// <summary>托盘"退出"放行后允许真实关闭。</summary>
    public bool AllowClose
    {
        get => _allowClose;
        set => _allowClose = value;
    }
    public MainWindow(IBaseNotice notice)
    {
        _notice = notice;
        InitializeComponent();
        _notice.Subscribe(EventNameConst.SystemOpenUrlEvent, OnOpenUrl);
    }

    private async void OnOpenUrl(object? obj)
    {
        if (obj != null && obj is Uri uri && GetTopLevel(this)?.Launcher is { } launcher)
        {
            await launcher.LaunchUriAsync(uri);
        }
    }

    /// <summary>关闭窗口时最小化到托盘；托盘"退出"或系统关机/应用退出时才真正关闭。</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // 系统关机或应用整体退出时放行，避免阻止退出流程
        if (e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown)
        {
            base.OnClosing(e);
            return;
        }
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        _notice.Publish(EventNameConst.SystemMainWindowUnloadedEvent);
        _notice.Unsubscribe(EventNameConst.SystemOpenUrlEvent, OnOpenUrl);
        base.OnUnloaded(e);
        Dispose();
    }
}