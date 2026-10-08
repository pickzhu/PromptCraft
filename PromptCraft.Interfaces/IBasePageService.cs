using BaseClassLib;

namespace PromptCraft.Interfaces
{
    public interface IBasePageService
    {
        public void RequestNavigation<T>() where T : ModelBase;
    }
}
