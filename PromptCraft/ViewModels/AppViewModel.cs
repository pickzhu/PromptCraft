using CommunityToolkit.Mvvm.ComponentModel;
using PromptCraft.BaseModel;
using PromptCraft.Interfaces;
using PromptCraft.StaticData;
using Ke.Bee.Localization.Localizer.Abstractions;

namespace PromptCraft.ViewModels;

public partial class AppViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _menuExit = string.Empty;

    [ObservableProperty]
    private string _trayShowMain = string.Empty;

    [ObservableProperty]
    private string _trayOpenWorkspace = string.Empty;

    [ObservableProperty]
    private string _traySyncComfy = string.Empty;

    public string Language { get; set; }

    public AppViewModel(ILocalizer localizer, IBaseNotice baseNotice) : base(localizer, baseNotice)
    {
        if (AppInitData.Config == null)
        {
            Language = "zh-CN";
        }
        else
        {
            Language = AppInitData.Config.Language.ToString().Replace('_', '-');
        }
        PageDataChaged();
    }


    private void PageDataChaged()
    {
        this.MenuExit = _local?["EXIT"].ToString() ?? string.Empty;
        this.TrayShowMain = _local?["TrayShowMain"].ToString() ?? string.Empty;
        this.TrayOpenWorkspace = _local?["TrayOpenWorkspace"].ToString() ?? string.Empty;
        this.TraySyncComfy = _local?["TraySyncComfy"].ToString() ?? string.Empty;
    }

    public override void OnSystemLangueChanged(object? data)
    {
        PageDataChaged();
        base.OnSystemLangueChanged(data);
    }
}

