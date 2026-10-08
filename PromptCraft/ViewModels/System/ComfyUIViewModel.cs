using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.BaseModel;
using PromptCraft.Interfaces;
using PromptCraft.StaticData;
using Ke.Bee.Localization.Localizer.Abstractions;
using Material.Icons;
using SukiUI.Controls;

namespace PromptCraft.ViewModels.System;

/// <summary>
/// ComfyUI 内嵌浏览页：用 NativeWebView 直接显示配置的 ComfyUI 地址
/// （AppConfig.ComfyApiUrl，默认 http://127.0.0.1:8188）。
/// 仅承载地址导航与刷新，页面本体由 View 内的 WebView 控件渲染。
/// </summary>
public partial class ComfyUIViewModel : ViewModelBase, ISukiStackPageTitleProvider
{
    private const string DefaultComfyUrl = "http://127.0.0.1:8188";

    /// <summary>页面标题（Suki 堆栈页头）。</summary>
    public string Title => _local?["ComfyUIView"] ?? "ComfyUI";

    /// <summary>配置的 ComfyUI 基地址（兜底到本地默认端口）。</summary>
    public Uri HomeUri { get; }

    [ObservableProperty] private string _currentUrl = "";

    public ComfyUIViewModel(ILocalizer localizer, IBaseNotice baseNotice)
        : base(localizer, baseNotice)
    {
        _displayName = "ComfyUIView";
        _icon = MaterialIconKind.Web;
        _index = 25;
        _sideMenu = true;

        var raw = AppInitData.Config?.ComfyApiUrl;
        if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw, UriKind.Absolute, out _))
        {
            raw = DefaultComfyUrl;
        }
        HomeUri = new Uri(raw, UriKind.Absolute);
        CurrentUrl = raw;
    }

    /// <summary>用户点击「前往」：解析地址并请求导航（非法地址忽略）。</summary>
    [RelayCommand]
    private void Go()
    {
        var text = (CurrentUrl ?? string.Empty).Trim();
        if (text.Length == 0 || !Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return;
        }
        CurrentUrl = uri.ToString();
        NavigateRequested?.Invoke(uri);
    }

    /// <summary>回到配置的首页。</summary>
    [RelayCommand]
    private void Home()
    {
        CurrentUrl = HomeUri.ToString();
        NavigateRequested?.Invoke(HomeUri);
    }

    /// <summary>刷新当前页面。</summary>
    [RelayCommand]
    private void Refresh() => RefreshRequested?.Invoke();

    /// <summary>View 订阅后驱动 NativeWebView 导航。</summary>
    public event Action<Uri>? NavigateRequested;

    /// <summary>View 订阅后驱动 NativeWebView 刷新。</summary>
    public event Action? RefreshRequested;

    public override void OnSystemLangueChanged(object? data)
    {
        OnPropertyChanged(nameof(Title));
        base.OnSystemLangueChanged(data);
    }
}
