namespace BaseClassLib.Extends
{
    public static class ServiceProviderExtend
    {
        public static T? GetService<T>(this IServiceProvider serviceProvider, Type type)
        {
            return (T?)serviceProvider.GetService(type);
        }
    }
}
