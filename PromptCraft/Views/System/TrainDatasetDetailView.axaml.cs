using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PromptCraft;

public partial class TrainDatasetDetailView : UserControl
{
    public TrainDatasetDetailView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
