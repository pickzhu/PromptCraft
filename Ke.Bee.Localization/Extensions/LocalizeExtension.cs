using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;
using System.Globalization;

namespace Ke.Bee.Localization.Extensions;

/// <summary>
/// Avalonia 本地化扩展
/// </summary>
public class LocalizeExtension : MarkupExtension
{
    public LocalizeExtension(object key)
    {
        Key = key;
    }

    /// <summary>
    /// 本地化 key。支持两种形式：
    /// 1. 静态字符串：{i18n:Localize Greeting}
    /// 2. 绑定（动态 key）：{i18n:Localize {Binding DisplayName}}，运行时以绑定值作为 key 查询
    /// </summary>
    public object Key { get; }

    public string? Context { get; }

    /// <summary>
    /// 为 xaml 文件提供值
    /// </summary>
    /// <param name="serviceProvider"></param>
    /// <returns></returns>
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (Key is BindingBase keyBinding)
        {
            // 动态 key：key 来自绑定值，语言切换（CurrentCulture 变化）时也需重新求值，
            // 因此额外绑定 Localizer.CurrentCulture 触发 MultiBinding 刷新。
            return new MultiBinding
            {
                Converter = DynamicLocalizeConverter.Instance,
                Bindings =
                {
                    keyBinding,
                    new Binding
                    {
                        Source = Localizer.Localizer.Instance,
                        Path = nameof(Localizer.Localizer.CurrentCulture),
                        Mode = BindingMode.OneWay
                    }
                }
            };
        }

        var keyToUse = (string)Key;
        if (!string.IsNullOrWhiteSpace(Context))
            keyToUse = $"{Context}/{Key}";

        var binding = new ReflectionBindingExtension($"[{keyToUse}]")
        {
            Mode = BindingMode.OneWay,
            Source = Localizer.Localizer.Instance
        };

        return binding.ProvideValue(serviceProvider);
    }

    /// <summary>
    /// 动态 key 转换器：以绑定到的 key 值查询 Localizer。
    /// 第二个绑定值（CurrentCulture）仅用于在语言切换时触发重新求值。
    /// </summary>
    private sealed class DynamicLocalizeConverter : IMultiValueConverter
    {
        public static readonly DynamicLocalizeConverter Instance = new();

        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count > 0 && values[0] is string key && !string.IsNullOrWhiteSpace(key))
                return Localizer.Localizer.Instance?[key];

            return null;
        }

        public IList<object?>? ConvertBack(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
            => null;
    }
}
