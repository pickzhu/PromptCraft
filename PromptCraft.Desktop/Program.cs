using Avalonia;
using PromptCraft.Service;
using PromptCraft.Service.Diagnostics;
using LibVLCSharp.Shared;
using ShowMeTheXaml;
using System;
using LibVLCSharp.Avalonia;
using System.IO;

namespace PromptCraft.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // 日志系统：在应用最早期引导并记录启动（全局异常处理器在 App.Initialize 中统一安装）
        var logger = LogService.Instance;
        // 全局崩溃防护：最早阶段安装（托管异常 → 日志+dump；原生崩溃 → 进程内转储；硬崩溃 → WER 兜底）
        CrashGuard.Install();
        // 启动时清理过期日志：只保留最近 7 天
        logger.CleanupOldLogs();

        try
        {
#if DEBUG
            string currentDir = AppDomain.CurrentDomain.BaseDirectory;
            string baseProjectVlcPath = Path.Combine(currentDir, @"..\..\..\..\BaseClassLib\bin\Debug\net9.0\libvlc\win-x64\");
            Core.Initialize(baseProjectVlcPath);
#else
            Core.Initialize();
#endif
            BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // 记录未捕获的顶层异常（便于错误信息收集），再保留原始崩溃行为
            logger.Fatal("应用运行阶段出现未捕获异常", "Program", ex);
            throw;
        }
        finally
        {
            logger.Info("PromptCraft 桌面端退出", "Program");
            // 退出前清理过期日志（与应用退出同步执行）
            logger.CleanupOldLogs();
            logger.Flush();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseXamlDisplay();
}
