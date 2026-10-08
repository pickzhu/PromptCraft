using Avalonia.Data;
using Avalonia.Data.Converters;
using PromptCraft.BaseModel;
using System;
using System.Globalization;

namespace PromptCraft;

public partial class PromptEngineeringView : PageBase
{
    public PromptEngineeringView(IServiceProvider service) : base(service)
    {
        InitializeComponent();
    }
}

/// <summary>string → bool 相等转换（RadioButton 按 Tag 选中显示），OneWay。</summary>
public class KindToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Equals(value?.ToString(), parameter?.ToString());

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
