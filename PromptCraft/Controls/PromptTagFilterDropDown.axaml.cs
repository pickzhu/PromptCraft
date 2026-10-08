using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PromptCraft.ViewModels.PromptLibrary;
using Ke.Bee.Localization;
using Ke.Bee.Localization.Localizer;
using System;
using System.Linq;

namespace PromptCraft.Controls;

/// <summary>
/// 词库标签多选筛选下拉：点击行切换选中，显示"全部标签 / 单选名 / 名 +N"摘要，
/// 选择变化时触发 <see cref="SelectionChanged"/>（宿主订阅后刷新过滤）。
/// </summary>
public partial class PromptTagFilterDropDown : UserControl
{
    public static readonly StyledProperty<System.Collections.IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<PromptTagFilterDropDown, System.Collections.IEnumerable?>(nameof(ItemsSource));

    public System.Collections.IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>标签选择变化（宿主绑定刷新过滤）。</summary>
    public event Action? SelectionChanged;

    public PromptTagFilterDropDown()
    {
        InitializeComponent();
        ItemsList.ItemsSource = ItemsSource;
        ItemsSourceProperty.Changed.AddClassHandler<PromptTagFilterDropDown>((s, e) =>
        {
            s.ItemsList.ItemsSource = e.NewValue as System.Collections.IEnumerable;
        });
    }

    private void OnItemClick(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: PromptTagItem item })
        {
            item.IsSelected = !item.IsSelected;
            RefreshDisplay();
            SelectionChanged?.Invoke();
            e.Handled = true;
        }
    }

    /// <summary>外部改变选中态后刷新按钮摘要（如点击卡片标签筛选）。</summary>
    public void RefreshDisplay() => UpdateDisplayText();

    private void UpdateDisplayText()
    {
        if (ItemsSource is not System.Collections.Generic.IEnumerable<PromptTagItem> tags) return;
        var selected = tags.Where(t => t.IsSelected).Select(t => t.Name).ToList();
        DisplayText.Text = selected.Count == 0
            ? Localizer.Instance?["AllTags"] ?? ""
            : selected.Count == 1
                ? selected[0]
                : $"{selected[0]} +{selected.Count - 1}";
    }
}
