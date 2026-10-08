using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PromptCraft;

public partial class TrainDatasetsView : UserControl
{
    public TrainDatasetsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
