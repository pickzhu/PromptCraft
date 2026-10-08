using Avalonia.Interactivity;
using PromptCraft.Consts.Event;
using PromptCraft.Interfaces;
using SukiUI.Controls;
using System;

namespace PromptCraft.Views;

public partial class MainWindow : SukiWindow
{
    private readonly IBaseNotice _notice;
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

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        _notice.Publish(EventNameConst.SystemMainWindowUnloadedEvent);
        _notice.Unsubscribe(EventNameConst.SystemOpenUrlEvent, OnOpenUrl);
        base.OnUnloaded(e);
        Dispose();
    }
}