using Avalonia.Interactivity;
using PromptCraft.BaseModel;
using PromptCraft.Consts.Event;
using System;

namespace PromptCraft;

public partial class CustomThemColor : PageBase
{
    public CustomThemColor(IServiceProvider service) : base(service)
    {
        InitializeComponent();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        _notice.Publish(EventNameConst.CustomAddThemColorUnloadEvent);
        base.OnUnloaded(e);
        Dispose();
    }
}