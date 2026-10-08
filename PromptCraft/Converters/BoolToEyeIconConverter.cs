using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Material.Icons;

namespace PromptCraft.Converters;

/// <summary>真 = 睁眼 Eye，假 = 闭眼 EyeOff。用于 API Key 眼睛开关。</summary>
public sealed class BoolToEyeIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool b && b) ? MaterialIconKind.Eye : MaterialIconKind.EyeOff;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
