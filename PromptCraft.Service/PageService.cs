using BaseClassLib;
using PromptCraft.Interfaces;

namespace PromptCraft.Service
{
    public class PageService : IBasePageService
    {
        public Action<Type>? NavigationRequested { get; set; }
        public void RequestNavigation<T>() where T : ModelBase
        {
            NavigationRequested?.Invoke(typeof(T));
        }
    }
}
