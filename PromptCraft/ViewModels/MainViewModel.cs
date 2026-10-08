using Avalonia.Collections;
using Avalonia.Styling;
using BaseClassLib;
using BaseClassLib.Extends;
using BaseClassLib.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptCraft.BaseModel;
using PromptCraft.Consts.Event;
using PromptCraft.Interfaces;
using PromptCraft.StaticData;
using PromptCraft.ViewModels.System;
using Ke.Bee.Localization.Localizer.Abstractions;
using SukiUI;
using SukiUI.Dialogs;
using SukiUI.Enums;
using SukiUI.Toasts;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PromptCraft.ViewModels;

public partial class MainWindowModel : ViewModelBase
{
    [ObservableProperty]
    private IAvaloniaReadOnlyList<ModelBase> _demoPages;
    [ObservableProperty] private ModelBase? _activePage;
    [ObservableProperty] private bool _windowLocked = false;
    [ObservableProperty] private bool _titleBarVisible = true;
    [ObservableProperty] private SukiBackgroundStyle _backgroundStyle = SukiBackgroundStyle.GradientSoft;
    [ObservableProperty] private bool _animationsEnabled = true;
    [ObservableProperty] private bool _transitionsEnabled;
    [ObservableProperty] private double _transitionTime;
    [ObservableProperty] private bool _showTitleBar = true;
    [ObservableProperty] private bool _showBottomBar = true;

    private readonly SukiTheme _theme;
    private readonly SettingModel _theming;

    public ISukiToastManager ToastManager { get; }
    public ISukiDialogManager DialogManager { get; }

    IEnumerable<ModelBase> _pages;
    public MainWindowModel(ILocalizer localizer, IBaseNotice notice, IEnumerable<ModelBase> pages, ISukiToastManager toastManager, ISukiDialogManager dialogManager) : base(localizer, notice)
    {
        //AnimationsEnabled = false;
        ToastManager = toastManager;
        DialogManager = dialogManager;

        _pages = pages;
        DemoPages = new AvaloniaList<ModelBase>(pages.Where(x => x.SideMenu).OrderBy(x => x.Index).ThenBy(x => x.DisplayName));
        _theming = (SettingModel)DemoPages.First(x => x is SettingModel);

        _theming.BackgroundStyleChanged += style => BackgroundStyle = style;
        _theming.BackgroundAnimationsChanged += enabled =>
        {
            AnimationsEnabled = enabled;
        };
        _theming.BackgroundTransitionsChanged += enabled => TransitionsEnabled = enabled;

        _theme = SukiTheme.GetInstance();
        _noticeService.Subscribe(EventNameConst.SystemMainWindowUnloadedEvent, OnMainWindowUnload);
        _noticeService.Subscribe(EventNameConst.SystemNavigatePageEvent, OnNavigatePage);
        if (AppInitData.Config == null)
        {
            return;
        }
        BackgroundStyle = AppInitData.Config.BackgroundStyle;
        ShowBottomBar = AppInitData.Config.ShowBottomBar;
        AnimationsEnabled = AppInitData.Config.BackgroundAnimations;
        TransitionsEnabled = AppInitData.Config.BackgroundTransitions;
        ShowTitleBar = AppInitData.Config.ShowTitleBar;
    }

    /// <summary>子页请求切换到指定页面（如"去配置"跳设置页）。payload 为页面 ViewModel 的 Type。</summary>
    private void OnNavigatePage(object? data)
    {
        if (data is not Type type) return;
        var target = _pages.FirstOrDefault(p => p.GetType() == type);
        if (target == null) return;
        ActivePage = target;
    }

    private void OnMainWindowUnload(object? obj)    {
        bool isLight = _theme.ActiveBaseTheme == ThemeVariant.Light;
        Config config = new Config()
        {
            SukiColorThemeName = _theme.ActiveColorTheme?.DisplayName,
            BackgroundAnimations = AnimationsEnabled,
            BackgroundStyle = BackgroundStyle,
            BackgroundTransitions = TransitionsEnabled,
            ShowTitleBar = ShowTitleBar,
            IsLight = isLight,
            Language = LanguageExtend.ParseEnumIgnoreCase<LanguageEnum>(_local.CurrentCulture.Name.Replace('-', '_')),
            ShowBottomBar = ShowBottomBar,
            // 保留 ComfyUI 配置（关闭时不修改）
            ComfyApiUrl = AppInitData.Config?.ComfyApiUrl ?? "http://127.0.0.1:8188",
            ComfyOutputDir = AppInitData.Config?.ComfyOutputDir ?? string.Empty,
            ThumbMaxDimension = AppInitData.Config?.ThumbMaxDimension ?? 300,
            ThumbQuality = AppInitData.Config?.ThumbQuality ?? 80,
            LogLevel = AppInitData.Config?.LogLevel ?? 2,
        };
        if (AppInitData.Config?.AddedThemeColors?.Any() == true)
        {
            config.AddedThemeColors = AppInitData.Config.AddedThemeColors;
        }
        AppInitData.Config = config;
        AppInitData.SaveConfig();

        this.Dispose();
    }

    public override void OnSystemLangueChanged(object? data)
    {
        var oldActive = ActivePage;
        DemoPages = new AvaloniaList<ModelBase>(_pages.Where(x => x.SideMenu).OrderBy(x => x.Index).ThenBy(x => x.DisplayName));
        ActivePage = oldActive;
    }

    bool _ch = false;
    [RelayCommand]
    private void ChangeLangure()
    {
        _ch = !_ch;
        if (_local != null)
            _local.CurrentCulture = new CultureInfo(_ch ? "zh-CN" : "en-US");
        _noticeService?.Publish(EventNameConst.SystemLangueChageEvent, _local?.CurrentCulture);
    }

    public override void Disposed()
    {
        _noticeService.Unsubscribe(EventNameConst.SystemMainWindowUnloadedEvent, OnMainWindowUnload);
        _noticeService.Unsubscribe(EventNameConst.SystemNavigatePageEvent, OnNavigatePage);
        base.Disposed();
    }
}
