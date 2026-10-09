using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using BaseClassLib;
using PromptCraft.Data;
using PromptCraft.Interfaces;
using PromptCraft.Service;
using PromptCraft.Service.Diagnostics;
using PromptCraft.StaticData;
using PromptCraft.Utils;
using PromptCraft.ViewModels;
using PromptCraft.Views;
using Ke.Bee.Localization.Localizer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using SukiUI;
using SukiUI.Controls;
using SukiUI.Toasts;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PromptCraft;

public partial class App : Application
{

    /// <summary>桌面生命周期（托盘与窗口联动）。</summary>
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    /// <summary>启动时构建的服务容器（托盘菜单动作取用服务）。</summary>
    private IServiceProvider? _services;

    /// <summary>托盘图标上一次点击时间（双击检测）。</summary>
    private DateTime _lastTrayClick = DateTime.MinValue;

    /// <summary>托盘双击判定时间窗口（毫秒）。</summary>
    private const double TrayDoubleClickWindowMs = 350;
    public override void Initialize()
    {
        // 日志系统：最早阶段引导静态实例 + 安装全局异常处理器 + 记录启动
        var logger = PromptCraft.Service.LogService.Instance;
        logger.InstallGlobalExceptionHandlers();
        // 全局崩溃防护：托管异常统一接收（日志+dump）；原生/硬崩溃兜底转储（桌面端已在 Program.Main 安装，此处幂等覆盖其他宿主）
        CrashGuard.Install();
        logger.Info(Localizer.Instance?["AppInitStartLog"] ?? "应用初始化开始...", "App");

        // 先确保配置库存在（建表 / 旧库补列），再加载配置：
        // 否则全新库 / 旧库首次启动时 LoadFromDb 会失败回退 JSON，读不到上次保存的日志级别
        ConfigRepository.EnsureDatabase();
        AppInitData.InitAppConfigData();
        // 应用配置中的日志输出级别（低于该级别的日志被过滤）。
        // 注意：此处不再记录"日志输出级别"日志——仅设置页修改级别时才记录。
        if (AppInitData.Config != null)
        {
            logger.MinLevel = (PromptCraft.Models.LogLevel)AppInitData.Config.LogLevel;
        }
        // 迁移 JSON 配置到 SQLite（幂等）
        ConfigRepository.MigrateFromJson(AppInitData.Config);
        AvaloniaXamlLoader.Load(this);
        // 托盘：TrayIcon/NativeMenuItem 的 CLR 事件无法在 XAML 中绑定，统一在代码侧接线（此时托盘图标树已加载）
        WireTrayEvents();
#if DEBUG
        this.AttachDeveloperTools();
#endif
        logger.Info(Localizer.Instance?["XamlResourcesLoadedLog"] ?? "XAML 资源加载完成", "App");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var logger = PromptCraft.Service.LogService.Instance;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var collection = new ServiceCollection();
            collection.AddSingleton(desktop);
            collection.InitPromptCraftServices(AppInitData.Config?.Language.ToString().Replace('_', '-'));
            collection.AddComfyUIServices();
            collection.InitViewServices();
            var service = collection.BuildServiceProvider();

            // 供托盘菜单等应用级动作取用（桌面生命周期 + 服务容器）
            _desktop = desktop;
            _services = service;

            // T0.1：初始化工作空间（读 AppConfig.WorkspaceDir，建 8 个子目录；缩略图目录依赖它）
            EnsureWorkspace(service, logger);

            // 启动时统一确保 ComfyUI 数据库 schema 就绪（新库建表 / 老库补列；提示词库已并入同一库）。
            EnsureComfyDatabaseSchema(service, logger);

            var views = service.GetService<IBaseViewService>()!;
            DataTemplates.Add(new ViewLocator(views));
            DisableAvaloniaDataAnnotationValidation();
            desktop.MainWindow = views.CreateView<ModelBase>(service, "Main") as Window;
            //App需要手动绑定DataContext，否则无法使用，可以通过这种方式来实现本地化
            this.DataContext = service.GetService<AppViewModel>();

            // 启动 ComfyUI WebSocket 长连接：从配置地址尝试连接（失败重试 3 次后弹自动消失的提示）
            StartComfyWebSocket(service, logger);

            // 预热 LibVLC（首次构造会加载插件库，可能数百毫秒）：后台线程执行，
            // 避免首次添加视频素材时在 UI 线程初始化造成卡顿；视频组件共用同一全局实例
            _ = Task.Run(() =>
            {
                try
                {
                    LibVlcProvider.WarmUp();
                }
                catch (Exception ex)
                {
                    logger.Warn(string.Format(Localizer.Instance?["LibVlcWarmUpFailed"] ?? "", ex.Message), "App", ex);
                }
            });
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            var collection = new ServiceCollection();
            collection.AddSingleton(singleViewPlatform);
            collection.InitPromptCraftServices(AppInitData.Config?.Language.ToString().Replace('_', '-'));
            collection.AddComfyUIServices();
            collection.InitViewServices();
            var service = collection.BuildServiceProvider();

            // 单视图平台（Browser 等）同样在启动时升级 Comfy 库 schema
            EnsureComfyDatabaseSchema(service, logger);

            var views = service.GetService<IBaseViewService>()!;
            DataTemplates.Add(new ViewLocator(views));
            singleViewPlatform.MainView = new SukiMainHost
            {
                Hosts = [],
                //Content = views.CreateView<>()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>启动时初始化工作空间：读 AppConfig.WorkspaceDir，校验并创建 8 个子目录（T0.1）。</summary>
    private static void EnsureWorkspace(IServiceProvider service, PromptCraft.Service.LogService logger)
    {
        try
        {
            var ws = service.GetRequiredService<IWorkspaceService>();
            var root = ws.InitializeAsync().GetAwaiter().GetResult();
            logger.Info(string.Format(Localizer.Instance?["WorkspaceReady"] ?? "", root), "App");
        }
        catch (Exception ex)
        {
            logger.Error(string.Format(Localizer.Instance?["WorkspaceInitFailed"] ?? "", ex.Message), "App", ex);
        }
    }

    /// <summary>
    /// 启动 ComfyUI WebSocket 长连接会话（后台线程）：连接失败自动重试 3 次，
    /// 仍失败在主线程弹一个自动消失的 Toast 提示；连接成功后由 Hub 保持，
    /// 意外断线（如 ComfyUI 关闭）时同样弹自动消失的 Toast 并后台自动重连。
    /// </summary>
    private static void StartComfyWebSocket(IServiceProvider service, PromptCraft.Service.LogService logger)
    {
        try
        {
            var hub = service.GetRequiredService<IComfyUIWebSocketHub>();
            hub.ConnectionFailed += () => Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (service.GetService(typeof(ISukiToastManager)) is not ISukiToastManager toastManager) return;
                    var toast = FluentSukiToastBuilder.CreateSimpleInfoToast(toastManager);
                    toast.SetTitle(Localizer.Instance?["ComfyConnectFailedTitle"] ?? "");
                    toast.SetContent(Localizer.Instance?["ComfyConnectFailedContent"] ?? "");
                    toast.SetCanDismissByClicking(true);
                    toast.Toast.DismissTimeout = TimeSpan.FromSeconds(4); // 自动消失
                    toast.Queue();
                }
                catch (Exception ex)
                {
                    logger.Warn(string.Format(Localizer.Instance?["ComfyConnectToastFailed"] ?? "", ex.Message), "App", ex);
                }
            });
            // 已建立连接后意外断开（如 ComfyUI 被关闭）：弹自动消失的提示，后台自动重连
            hub.ConnectionLost += _ => Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (service.GetService(typeof(ISukiToastManager)) is not ISukiToastManager toastManager) return;
                    var toast = FluentSukiToastBuilder.CreateSimpleInfoToast(toastManager);
                    toast.SetTitle(Localizer.Instance?["ComfyDisconnectedTitle"] ?? "");
                    toast.SetContent(Localizer.Instance?["ComfyDisconnectedContent"] ?? "");
                    toast.SetCanDismissByClicking(true);
                    toast.Toast.DismissTimeout = TimeSpan.FromSeconds(4); // 自动消失
                    toast.Queue();
                }
                catch (Exception ex)
                {
                    logger.Warn(string.Format(Localizer.Instance?["ComfyDisconnectToastFailed"] ?? "", ex.Message), "App", ex);
                }
            });
            hub.Start();
            logger.Info(Localizer.Instance?["ComfyWsStarted"] ?? "", "App");
        }
        catch (Exception ex)
        {
            logger.Warn(string.Format(Localizer.Instance?["ComfyWsStartFailed"] ?? "", ex.Message), "App", ex);
        }
    }

    /// <summary>启动时升级 ComfyUI 数据库 schema（幂等）：新库 EnsureCreated 建表，老库手动补列/建索引。</summary>
    private static void EnsureComfyDatabaseSchema(IServiceProvider service, PromptCraft.Service.LogService logger)
    {
        try
        {
            using var comfyDb = service.GetRequiredService<IDbContextFactory<ComfyDbContext>>().CreateDbContext();
            ComfyDbMigrator.EnsureSchemaAsync(comfyDb).GetAwaiter().GetResult();
            logger.Info(Localizer.Instance?["ComfyDbSchemaReady"] ?? "", "App");
        }
        catch (Exception ex)
        {
            logger.Error(string.Format(Localizer.Instance?["ComfyDbInitFailed"] ?? "", ex.Message), "App", ex);
        }
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        var _theme = SukiTheme.GetInstance();
        if (AppInitData.Config != null)
        {
            _theme.ChangeBaseTheme(AppInitData.Config.IsLight ? ThemeVariant.Light : ThemeVariant.Dark);
            if (AppInitData.Config.AddedThemeColors?.Any() == true)
            {
                _theme.AddColorThemes(AppInitData.Config.AddedThemeColors.Select(x =>
                new SukiUI.Models.SukiColorTheme(x.Name, new Avalonia.Media.Color(x.PrimaryColorA, x.PrimaryColorR, x.PrimaryColorG, x.PrimaryColorB), new Avalonia.Media.Color(x.AccentColorA, x.AccentColorR, x.AccentColorG, x.AccentColorB))));
            }
            if (!string.IsNullOrEmpty(AppInitData.Config.SukiColorThemeName) && _theme.ColorThemes.Any(x => x.DisplayName == AppInitData.Config.SukiColorThemeName))
            {
                _theme.ChangeColorTheme(_theme.ColorThemes.First(x => x.DisplayName == AppInitData.Config.SukiColorThemeName));
            }
            _theme.Locale = AppInitData.Config.Language.ToString().Replace('_', '-');
        }
    }

    // ---------- 托盘：单击/双击/菜单动作 ----------

    /// <summary>托盘 CLR 事件接线：顺序与 App.axaml 的 &lt;NativeMenu&gt; 一致——[0]打开主窗口 [1]打开工作空间 [2]同步 [3]分隔线 [4]退出。</summary>
    private void WireTrayEvents()
    {
        try
        {
            if (GetValue(TrayIcon.IconsProperty) is not TrayIcons icons || icons.Count == 0) return;
            var tray = icons[0];
            tray.Clicked += OnTrayIconClicked;
            if (tray.Menu is not { } menu) return;
            EventHandler?[] actions = [OnTrayShowClicked, OnTrayOpenWorkspaceClicked, OnTraySyncComfyClicked, null, OnTrayExitClicked];
            var index = 0;
            foreach (var item in menu.Items)
            {
                if (item is NativeMenuItem menuItem && index < actions.Length && actions[index] is { } handler)
                {
                    menuItem.Click += handler;
                }
                index++;
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn("托盘菜单接线失败：" + ex.Message, "App", ex);
        }
    }

    /// <summary>托盘图标单击：作为双击检测的第一击（Windows 左键触发 Clicked，右键弹菜单）。</summary>
    private void OnTrayIconClicked(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastTrayClick).TotalMilliseconds <= TrayDoubleClickWindowMs)
        {
            _lastTrayClick = DateTime.MinValue;
            ShowMainWindow();
        }
        else
        {
            _lastTrayClick = now;
        }
    }

    /// <summary>显示并激活主窗口（隐藏/最小化后恢复）。</summary>
    private void ShowMainWindow()
    {
        try
        {
            if (_desktop?.MainWindow is { } window)
            {
                window.WindowState = WindowState.Normal;
                window.Show();
                window.Activate();
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn("托盘显示主窗口失败：" + ex.Message, "Tray", ex);
        }
    }

    private void OnTrayShowClicked(object? sender, EventArgs e) => ShowMainWindow();

    /// <summary>打开工作空间目录（在资源管理器中定位）。</summary>
    private void OnTrayOpenWorkspaceClicked(object? sender, EventArgs e)
    {
        try
        {
            var root = _services?.GetService<IWorkspaceService>()?.Root;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                LogService.Instance.Warn("托盘打开工作空间失败：工作空间目录不存在或未初始化", "Tray");
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = root, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn("托盘打开工作空间失败：" + ex.Message, "Tray", ex);
        }
    }

    /// <summary>托盘触发一次 ComfyUI 图库同步（与图库页同一服务）。</summary>
    private async void OnTraySyncComfyClicked(object? sender, EventArgs e)
    {
        try
        {
            var sync = _services?.GetRequiredService<IImageSyncService>();
            if (sync is null) return;
            LogService.Instance.Info(Localizer.Instance?["SyncStarted"] ?? "开始同步图片", "Tray");
            var result = await sync.SyncAsync();
            var msg = string.Format(Localizer.Instance?["SyncCompleted"] ?? "同步完成: 新增 {0}, 更新 {1}, 删除 {2}",
                result.NewFiles, result.UpdatedFiles, result.DeletedFiles);
            LogService.Instance.Info(msg, "Tray");
            ShowTrayToast(msg);
        }
        catch (Exception ex)
        {
            var msg = string.Format(Localizer.Instance?["SyncFailed"] ?? "同步失败: {0}", ex.Message);
            LogService.Instance.Warn(msg, "Tray", ex);
            ShowTrayToast(msg, success: false);
        }
    }

    /// <summary>托盘"退出"：先放行主窗口真实关闭，再由桌面生命周期随窗口关闭退出。</summary>
    private void OnTrayExitClicked(object? sender, EventArgs e)
    {
        try
        {
            if (_desktop?.MainWindow is MainWindow mainWindow)
            {
                mainWindow.AllowClose = true;
                mainWindow.Close();
            }
            else
            {
                _desktop?.Shutdown();
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn("托盘退出失败：" + ex.Message, "Tray", ex);
        }
    }

    /// <summary>托盘动作完成后的自动消失提示（窗口隐藏时也会排队，下次显示时可见）。</summary>
    private void ShowTrayToast(string content, bool success = true)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_services?.GetService(typeof(ISukiToastManager)) is not ISukiToastManager toastManager) return;
                var toast = FluentSukiToastBuilder.CreateSimpleInfoToast(toastManager);
                toast.SetTitle("PromptCraft");
                toast.SetContent(content);
                toast.SetCanDismissByClicking(true);
                toast.Toast.DismissTimeout = TimeSpan.FromSeconds(4);
                toast.Queue();
            }
            catch (Exception ex)
            {
                LogService.Instance.Warn("托盘提示弹窗失败：" + ex.Message, "Tray", ex);
            }
        });
    }
}
