using PromptCraft.Interfaces;
using Ke.Bee.Localization.Extensions;
using Ke.Bee.Localization.Options;
using Ke.Bee.Localization.Providers;
using Microsoft.Extensions.DependencyInjection;
using SukiUI.Dialogs;
using SukiUI.Toasts;
using System.Globalization;

namespace PromptCraft.Service
{
    public static class ConfigService
    {
        public static ServiceCollection InitPromptCraftServices(this ServiceCollection service, string? defaultLan = null)
        {
            service.AddSingleton<IBasePageService, PageService>();
            service.AddSingleton<IBaseClipboardService, ClipboardService>();
            service.AddSingleton<ISukiToastManager, SukiToastManager>();
            service.AddSingleton<ISukiDialogManager, SukiDialogManager>();
            service.AddSingleton<IBaseViewService, ViewService>();
            service.AddSingleton<IBaseNotice, NoticeService>();
            // 日志服务：必须注册与启动引导相同的静态实例（LogService.Instance），
            // 否则 DI 会另建一个默认 Info 级别的实例，App.Initialize 里设置的 MinLevel 对注入方不生效
            service.AddSingleton<IBaseLogService>(LogService.Instance);
            //本地化配置
            service.AddLocalization<AvaloniaJsonLocalizationProvider>(() =>
             {
                 var options = new AvaloniaLocalizationOptions(
                     // 支持的本地化语言文化
                     new List<CultureInfo>
                     {
                        new("en-US"),
                        new("zh-CN")
                     },
                     // defaultCulture, 用于设置当前文化（currentCulture）不在 cultures 列表中时的情况以及作为缺失的本地化条目的备用文化（fallback culture）
                     new CultureInfo("zh-CN"),
                     // currentCulture 在基础设施加载时设置，可以从应用程序设置或其他地方获取
                     //Thread.CurrentThread.CurrentCulture,
                     new CultureInfo(defaultLan ?? "zh-CN"),
                     $"PromptCraft/Assets/i18n");
                 return options;
             });
            return service;
        }
    }
}
