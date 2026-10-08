using Avalonia.Interactivity;
using PromptCraft.BaseModel;
using PromptCraft.ViewModels.Test; // 引入 ViewModel 命名空间
using System;

namespace PromptCraft
{
    public partial class Test : PageBase
    {
        // 假设 service 容器中已经注册了 TestViewModel
        public Test(IServiceProvider service) : base(service)
        {
            InitializeComponent();

            // 从 IServiceProvider 中获取已注册的 ViewModel 并绑定到 DataContext
            // 如果你的依赖注入框架没有注册它，也可以暂时用：DataContext = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<TestViewModel>(service);
            var viewModel = (TestViewModel)service.GetService(typeof(TestViewModel));
            if (viewModel != null)
            {
                this.DataContext = viewModel;
            }
        }
    }
}
