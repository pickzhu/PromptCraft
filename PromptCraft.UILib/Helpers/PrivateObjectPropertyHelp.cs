using System.Reflection;

namespace PromptCraft.UILib.Helpers
{
    /// <summary>
    /// 反射读取/写入对象的私有字段或私有属性（用于 SukiUI 等第三方控件内部私有成员访问）。
    /// 由原 HelpLibs 项目并入 UILib。
    /// </summary>
    public class PrivateObjectPropertyHelp
    {
        public static TReturn? GetObjectProperty<T, TReturn>(T obj, string propertyName)
        {
            Type targetType = obj!.GetType();
            FieldInfo? privateFieldInfo = targetType.GetField(propertyName, BindingFlags.NonPublic | BindingFlags.Instance);

            if (privateFieldInfo != null)
            {
                return (TReturn?)privateFieldInfo.GetValue(obj);
            }

            PropertyInfo? prop = targetType.GetProperty(propertyName, BindingFlags.NonPublic);

            if (prop == null)
            {
                return default;
            }

            return (TReturn?)prop.GetValue(obj);
        }

        public static void SetObjectPropertyValue<T, TValue>(T obj, TValue value, string propertyName)
        {
            Type targetType = obj!.GetType();
            FieldInfo? privateFieldInfo = targetType.GetField(propertyName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (privateFieldInfo != null)
            {
                privateFieldInfo.SetValue(obj, value);
                return;
            }

            PropertyInfo? prop = targetType.GetProperty(propertyName, BindingFlags.NonPublic);
            if (prop == null)
            {
                return;
            }

            prop.SetValue(obj, value);
        }
    }
}
