using Avalonia.Media;

namespace PromptCraft.Models.ComfyUI;

/// <summary>
/// 标签颜色调色板（全局统一入口）。
/// 规则：标签创建时从调色板随机取一个颜色保存到 Tags.Color（hex 字符串），
/// 之后所有展示标签的地方一律使用保存的颜色；老数据/异常数据回退到名称哈希固定色。
/// 调色板全部为深色系，保证白色标签文字可读。
/// </summary>
public static class TagPalette
{
    /// <summary>标签调色板（60 色，深色系，来自 Material Design 600-900 级色板）。</summary>
    public static readonly IReadOnlyList<Color> Colors =
    [
        // 红
        Color.Parse("#D32F2F"), Color.Parse("#C62828"), Color.Parse("#B71C1C"),
        // 粉
        Color.Parse("#C2185B"), Color.Parse("#AD1457"), Color.Parse("#880E4F"),
        // 紫
        Color.Parse("#8E24AA"), Color.Parse("#7B1FA2"), Color.Parse("#6A1B9A"), Color.Parse("#4A148C"),
        // 深紫
        Color.Parse("#512DA8"), Color.Parse("#4527A0"), Color.Parse("#311B92"),
        // 靛
        Color.Parse("#303F9F"), Color.Parse("#283593"), Color.Parse("#1A237E"),
        // 蓝
        Color.Parse("#1976D2"), Color.Parse("#1565C0"), Color.Parse("#0D47A1"),
        // 浅蓝
        Color.Parse("#0288D1"), Color.Parse("#0277BD"), Color.Parse("#01579B"),
        // 青
        Color.Parse("#0097A7"), Color.Parse("#00838F"), Color.Parse("#006064"),
        // 蓝绿
        Color.Parse("#00796B"), Color.Parse("#00695C"), Color.Parse("#004D40"),
        // 绿
        Color.Parse("#388E3C"), Color.Parse("#2E7D32"), Color.Parse("#1B5E20"),
        // 浅绿
        Color.Parse("#689F38"), Color.Parse("#558B2F"), Color.Parse("#33691E"),
        // 黄绿
        Color.Parse("#AFB42B"), Color.Parse("#9E9D24"), Color.Parse("#827717"),
        // 琥珀/橙
        Color.Parse("#F57F17"), Color.Parse("#F9A825"), Color.Parse("#E65100"),
        Color.Parse("#EF6C00"), Color.Parse("#F57C00"), Color.Parse("#FB8C00"),
        // 棕
        Color.Parse("#5D4037"), Color.Parse("#4E342E"), Color.Parse("#3E2723"),
        // 灰蓝
        Color.Parse("#455A64"), Color.Parse("#37474F"), Color.Parse("#263238"),
        // 补充亮色（仍保证白字可读）
        Color.Parse("#E64A19"), Color.Parse("#D84315"), Color.Parse("#BF360C"),
        Color.Parse("#6D4C41"), Color.Parse("#546E7A"), Color.Parse("#00695C"),
        Color.Parse("#8E24AA"), Color.Parse("#3949AB"), Color.Parse("#00838F"),
        Color.Parse("#7CB342"), Color.Parse("#5E35B1"), Color.Parse("#AD1457"),
    ];

    /// <summary>随机取一个调色板颜色（hex 字符串 "#RRGGBB"）。</summary>
    public static string GetRandomColorHex() => ToHex(Colors[Random.Shared.Next(Colors.Count)]);

    /// <summary>
    /// 随机取一个不与已有标签颜色重复的颜色（hex 字符串）。
    /// 最多尝试 20 次，仍全部冲突时退化为纯随机（避免死循环）。
    /// </summary>
    public static string PickDistinctColorHex(IReadOnlyCollection<string> usedColors)
    {
        if (usedColors.Count == 0) return GetRandomColorHex();
        for (var i = 0; i < 20; i++)
        {
            var color = Colors[Random.Shared.Next(Colors.Count)];
            var hex = ToHex(color);
            if (!usedColors.Contains(hex)) return hex;
        }
        return GetRandomColorHex();
    }

    /// <summary>
    /// 根据标签取显示画刷：优先使用保存的颜色（Tags.Color），
    /// 缺失/非法时回退到名称哈希固定色，保证任何标签都有稳定颜色。
    /// </summary>
    public static SolidColorBrush GetBrush(string? colorHex, string? name = null)
    {
        if (!string.IsNullOrWhiteSpace(colorHex)
            && Color.TryParse(colorHex, out var color))
        {
            return new SolidColorBrush(color);
        }
        return GetFallbackBrush(name ?? "");
    }

    /// <summary>名称哈希固定色（老数据/异常数据兜底，与旧实现保持一致）。</summary>
    public static SolidColorBrush GetFallbackBrush(string tagName)
    {
        // 用无符号取模避免 string.GetHashCode() 返回 int.MinValue 时 Math.Abs 溢出
        var hash = (uint)tagName.GetHashCode();
        var index = (int)(hash % (uint)Colors.Count);
        return new SolidColorBrush(Colors[index]);
    }

    private static string ToHex(Color color)
    {
        // Avalonia 的 ToString 会输出 #AARRGGBB（含 alpha），标签色统一存 6 位 RGB
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    /// <summary>工具：已用颜色集合（供创建标签时避重）。</summary>
    public static IReadOnlyCollection<string> NormalizeUsedColors(IEnumerable<string?>? colors)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (colors == null) return set;
        foreach (var c in colors)
        {
            if (!string.IsNullOrWhiteSpace(c) && Color.TryParse(c, out var color))
                set.Add(ToHex(color));
        }
        return set;
    }
}
