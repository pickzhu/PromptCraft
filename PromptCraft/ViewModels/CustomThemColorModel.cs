using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PromptCraft.BaseModel;
using PromptCraft.Consts.Event;
using PromptCraft.Interfaces;
using Ke.Bee.Localization.Localizer.Abstractions;
using Material.Icons;
using SukiUI.Models;

namespace PromptCraft.ViewModels;

public partial class CustomThemColorModel : ViewModelBase
{

    [ObservableProperty] private string _colorName = "Pink";
    [ObservableProperty] private Color _primaryColor = Colors.DeepPink;
    [ObservableProperty] private Color _accentColor = Colors.Pink;
    [ObservableProperty] private string _accentColorStr = "";
    [ObservableProperty] private string _primaryColorStr = "";

    public CustomThemColorModel(ILocalizer localizer, IBaseNotice baseNotice) : base(localizer, baseNotice)
    {
        _accentColorStr = _accentColor.ToString();
        _primaryColorStr = _primaryColor.ToString();
        _displayName = "CUSTOMTHEMCOLOR";
        _icon = MaterialIconKind.Cab;
        _index = 0;
        _sideMenu = false;
        _noticeService.Subscribe(EventNameConst.CustomAddThemColorUnloadEvent, OnThemUnload);
    }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="obj"></param>
    private void OnThemUnload(object? obj)
    {
        _noticeService.Publish(EventNameConst.CustomAddThemColorDataEvent, new SukiColorTheme(ColorName, PrimaryColor, AccentColor));
    }

    partial void OnAccentColorChanged(Color value)
    {
        AccentColorStr = value.ToString();
    }

    partial void OnPrimaryColorChanged(Color value)
    {
        PrimaryColorStr = value.ToString();
    }

    public override void Disposed()
    {
        _noticeService.Unsubscribe(EventNameConst.CustomAddThemColorUnloadEvent, OnThemUnload);
        base.Disposed();
    }
}
