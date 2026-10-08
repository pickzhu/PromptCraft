using Ke.Bee.Localization.Localizer;

namespace BaseClassLib.Extends;

public static class LanguageExtend
{
    // 方法2：不区分大小写解析
    public static T ParseEnumIgnoreCase<T>(string value, bool ignoreCase = true) where T : struct, Enum
    {
        if (Enum.TryParse<T>(value, ignoreCase, out T result))
        {
            return result;
        }
        throw new ArgumentException(string.Format(Localizer.Instance?["ConvertFailed"] ?? "", value, typeof(T).Name));
    }
}
