using SukiUI.Models;

namespace BaseClassLib.Models;

public class AddThemeModel
{
#pragma warning disable CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑添加 "required" 修饰符或声明为可为 null。
    public AddThemeModel() { }
#pragma warning restore CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑添加 "required" 修饰符或声明为可为 null。
    public AddThemeModel(SukiColorTheme suki)
    {
        this.Name = suki.DisplayName;
        this.AccentColorG = suki.Accent.G;
        this.AccentColorB = suki.Accent.B;
        this.AccentColorR = suki.Accent.R;
        this.AccentColorA = suki.Accent.A;

        this.PrimaryColorG = suki.Primary.G;
        this.PrimaryColorA = suki.Primary.A;
        this.PrimaryColorB = suki.Primary.B;
        this.PrimaryColorR = suki.Primary.R;
    }
    public string Name { get; set; }
    public byte PrimaryColorA { get; set; }
    public byte PrimaryColorR { get; set; }
    public byte PrimaryColorG { get; set; }
    public byte PrimaryColorB { get; set; }
    public byte AccentColorA { get; set; }
    public byte AccentColorR { get; set; }
    public byte AccentColorG { get; set; }
    public byte AccentColorB { get; set; }
}
