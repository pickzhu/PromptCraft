using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace PromptCraft.Converters;

/// <summary>
/// 真 = 显示明文（\0 不遮罩），假 = 用 * 遮罩。用于 API Key 眼睛开关。
/// </summary>
public sealed class BoolToPasswordCharConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool b && b) ? '\0' : '*';

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
