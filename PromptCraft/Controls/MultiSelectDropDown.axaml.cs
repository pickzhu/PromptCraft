using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using PromptCraft.ViewModels.ComfyUI;
using Ke.Bee.Localization;
using Ke.Bee.Localization.Localizer;
using System.Collections;

namespace PromptCraft.Controls;

public partial class MultiSelectDropDown : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<MultiSelectDropDown, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<string> SelectedDisplayTextProperty =
        AvaloniaProperty.Register<MultiSelectDropDown, string>(nameof(SelectedDisplayText), "");

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public string SelectedDisplayText
    {
        get => GetValue(SelectedDisplayTextProperty);
        set => SetValue(SelectedDisplayTextProperty, value);
    }

    public MultiSelectDropDown()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsSourceProperty && change.NewValue is IEnumerable items)
            ItemsList.ItemsSource = items;
        else if (change.Property == SelectedDisplayTextProperty)
            DisplayText.Text = change.NewValue as string ?? (Localizer.Instance?["AllTags"] ?? "");
    }

    private void OnItemClick(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: TagItem item })
            item.IsSelected = !item.IsSelected;
    }
}