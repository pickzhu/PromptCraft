using Microsoft.Win32;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace PromptCraft.Service.Diagnostics;

/// <summary>
/// 全局崩溃防护：项目内所有未能捕获的异常统一在此接收，写入日志系统并生成可用于分析的转储文件。
/// <para>覆盖范围（分层兜底）：</para>
/// <list type="bullet">
/// <item>托管未处理异常（任意线程 / UI 线程 / 后台线程）→ 记录 Fatal 日志 + 生成 minidump + 附带 <c>*-info.txt</c> 崩溃信息旁路文本（含运行环境 / 异常链 / 最近日志）。</item>
/// <item>未观察任务异常 → 由 <see cref="LogService.InstallGlobalExceptionHandlers"/> 的既有钩子记录并标记已观察（本类不重复订阅，避免重复日志）。</item>
/// <item>Windows 原生崩溃（SEH，如访问违例 / 原生库崩溃）→ 进程内原生异常过滤器直接调用 MiniDumpWriteDump（仅用 P/Invoke，最小化托管面，崩溃现场损坏时静默失败）。</item>
/// <item>进程内代码无法运行的硬崩溃（栈溢出 / FailFast / 被外部强杀）→ 尽力注册 WER LocalDumps（需管理员权限写 HKLM），由操作系统兜底生成 dump。</item>
/// </list>
/// <para>转储目录：<c>%AppData%/PromptCraft/crashes</c>，启动时自动清理只保留最近 <see cref="MaxDumpsToKeep"/> 份。</para>
/// <para>默认生成全内存转储（最利于 dotnet-dump / WinDbg 分析）；设置环境变量 <c>PROMPTCRAFT_CRASH_DUMP_MINI=1</c> 可切换为轻量 minidump（文件更小、更快）。</para>
/// </summary>
public static class CrashGuard
{
    private const int MaxDumpsToKeep = 20;

    private static readonly object Gate = new();
    private static bool _installed;

    private static string? _dumpDirectory;

    // ---- 原生崩溃兜底（Windows only）----
    private static IntPtr _previousFilter;
    private static UnhandledExceptionFilter? _nativeFilterDelegate; // 持有委托引用，防止被 GC 回收导致过滤器失效

    // ---- MiniDump 类型标志（dbghelp.h）----
    private const uint MiniDumpNormal = 0x00000000;
    private const uint MiniDumpWithDataSegs = 0x00000001;
    private const uint MiniDumpWithFullMemory = 0x00000002;
    private const uint MiniDumpWithHandleData = 0x00000004;
    private const uint MiniDumpWithUnloadedModules = 0x00000010;
    private const uint MiniDumpWithThreadInfo = 0x00001000;
    private const uint MiniDumpWithFullMemoryInfo = 0x00000800;

    // ---- Win32 常量 ----
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint CreateAlways = 2;
    private const uint FileAttributeNormal = 0x00000080;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary>崩溃转储目录（<c>%AppData%/PromptCraft/crashes</c>）。</summary>
    public static string DumpDirectory => _dumpDirectory ??= CreateDumpDirectory();

    /// <summary>
    /// 安装全局崩溃防护。幂等：桌面端（Program.Main 最早阶段）与共享宿主（App.Initialize）都会调用，重复调用自动忽略。
    /// 安装失败不阻塞启动（仅记 Warn）。
    /// </summary>
    public static void Install()
    {
        if (_installed) return;
        lock (Gate)
        {
            if (_installed) return;
            _installed = true;
            try
            {
                _dumpDirectory = CreateDumpDirectory();
                CleanupOldDumps();

                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                AppDomain.CurrentDomain.ProcessExit += (_, _) => TryFlushLogs();

                // Windows 原生崩溃兜底：进程内异常过滤器 + WER LocalDumps 系统级兜底
                if (OperatingSystem.IsWindows())
                {
                    InstallNativeExceptionFilter();
                    RegisterWerLocalDumps();
                }

                LogService.Instance.Info($"全局崩溃防护已启用，转储目录：{DumpDirectory}", "CrashGuard");
            }
            catch (Exception ex)
            {
                LogService.Instance.Warn($"全局崩溃防护安装失败：{ex.Message}", "CrashGuard", ex);
            }
        }
    }

    // ---------------------------------------------------------------------
    // 托管未处理异常（任意线程）
    // ---------------------------------------------------------------------

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var ex = e.ExceptionObject as Exception;
            var summary = BuildCrashSummary("托管未处理异常", ex, e.IsTerminating);
            LogService.Instance.Fatal(summary, "CrashGuard", ex);
            TryFlushLogs(); // 先排空日志队列，保证信息文件中的「最近日志」包含本次崩溃记录

            var dumpPath = WriteDump("unhandled");
            if (dumpPath != null)
                LogService.Instance.Fatal($"已生成崩溃转储：{dumpPath}", "CrashGuard");

            // 崩溃信息旁路文本：不依赖内存日志缓冲，便于直接打开分析
            WriteInfoFile(dumpPath, summary, ex);
        }
        catch (Exception inner)
        {
            // 崩溃处理自身异常不能递归日志，尽力直接追加到崩溃目录
            TryDirectAppend(Path.Combine(DumpDirectory, "crashguard-errors.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] 崩溃处理自身异常: {inner}");
        }
        finally
        {
            TryFlushLogs(); // 进程即将终止，同步排空日志队列避免丢失
        }
    }

    // ---------------------------------------------------------------------
    // Windows 原生崩溃（SEH）
    // ---------------------------------------------------------------------

    private delegate uint UnhandledExceptionFilter(IntPtr exceptionInfo);

    private static void InstallNativeExceptionFilter()
    {
        try
        {
            _nativeFilterDelegate = NativeExceptionFilter;
            _previousFilter = SetUnhandledExceptionFilter(Marshal.GetFunctionPointerForDelegate(_nativeFilterDelegate));
            LogService.Instance.Debug("已安装原生异常过滤器（Windows SEH 崩溃 → 进程内转储）", "CrashGuard");
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn($"安装原生异常过滤器失败：{ex.Message}", "CrashGuard", ex);
        }
    }

    /// <summary>
    /// 原生未处理异常（访问违例等）回调：尽力写 minidump 后返回 <c>EXCEPTION_CONTINUE_SEARCH</c>，
    /// 交由系统默认处理（WER / 终止进程），不干预进程的默认崩溃行为。
    /// 崩溃现场可能已损坏，全程最小化托管面，任何失败都静默。
    /// </summary>
    private static uint NativeExceptionFilter(IntPtr exceptionInfo)
    {
        try
        {
            WriteMinidumpNativeOnly();
        }
        catch
        {
            // 崩溃现场损坏时静默
        }
        return 0; // EXCEPTION_CONTINUE_SEARCH
    }

    /// <summary>原生过滤器专用：仅使用 P/Invoke（CreateFileW / MiniDumpWriteDump / CloseHandle）写转储，避免托管分配。</summary>
    private static void WriteMinidumpNativeOnly()
    {
        string path;
        try { path = BuildDumpPath("native"); }
        catch { return; }

        var hFile = CreateFileW(path, GenericWrite, FileShareRead, IntPtr.Zero, CreateAlways, FileAttributeNormal, IntPtr.Zero);
        if (hFile == InvalidHandleValue) return;
        try
        {
            MiniDumpWriteDump(GetCurrentProcess(), GetCurrentProcessId(), hFile, NativeDumpType(),
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(hFile);
        }
    }

    /// <summary>尽力注册 WER LocalDumps（HKLM，需管理员权限）：进程内代码无法运行的硬崩溃由系统兜底生成 dump。失败不影响运行。</summary>
    private static void RegisterWerLocalDumps()
    {
        try
        {
            var appName = Process.GetCurrentProcess().ProcessName + ".exe";
            using var key = Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\" + appName);
            key.SetValue("DumpFolder", DumpDirectory, RegistryValueKind.String);
            key.SetValue("DumpType", UseMinidump ? 1 : 2, RegistryValueKind.DWord); // 1=minidump 2=full
            key.SetValue("DumpCount", MaxDumpsToKeep, RegistryValueKind.DWord);
            LogService.Instance.Info("已注册 WER LocalDumps 系统级兜底转储（写入 HKLM）", "CrashGuard");
        }
        catch (UnauthorizedAccessException)
        {
            LogService.Instance.Info("未注册 WER LocalDumps（无管理员权限写 HKLM；进程内崩溃过滤器仍会生成转储）", "CrashGuard");
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn($"注册 WER LocalDumps 失败：{ex.Message}", "CrashGuard", ex);
        }
    }

    // ---------------------------------------------------------------------
    // 转储与信息文件
    // ---------------------------------------------------------------------

    private static string CreateDumpDirectory()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PromptCraft", "crashes");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanupOldDumps()
    {
        try
        {
            var dir = DumpDirectory;
            if (!Directory.Exists(dir)) return;
            foreach (var pattern in new[] { "crash-*.dmp", "crash-*.txt" })
            {
                var stale = Directory.GetFiles(dir, pattern)
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                    .Skip(MaxDumpsToKeep)
                    .ToList();
                foreach (var f in stale)
                {
                    try { File.Delete(f); }
                    catch { /* 被占用/权限不足时跳过 */ }
                }
            }
        }
        catch
        {
            // 清理失败不影响运行
        }
    }

    /// <summary>为托管未处理异常生成转储文件（全内存，便于 dotnet-dump / WinDbg 分析）。返回文件路径；失败返回 null。</summary>
    private static string? WriteDump(string kind)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return null; // 非 Windows 平台无 MiniDumpWriteDump
            var path = BuildDumpPath(kind);
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                var ok = MiniDumpWriteDump(GetCurrentProcess(), GetCurrentProcessId(),
                    fs.SafeFileHandle.DangerousGetHandle(), ManagedDumpType(),
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (!ok)
                {
                    var err = Marshal.GetLastWin32Error();
                    LogService.Instance.Warn($"生成崩溃转储失败（Win32 错误 {err}）", "CrashGuard");
                    return null;
                }
            }
            return path;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn($"生成崩溃转储异常：{ex.Message}", "CrashGuard", ex);
            return null;
        }
    }

    private static string BuildDumpPath(string kind)
    {
        var dir = DumpDirectory;
        var baseName = $"crash-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{kind}";
        var path = Path.Combine(dir, baseName + ".dmp");
        var seq = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(dir, $"{baseName}-{seq++}.dmp");
        }
        return path;
    }

    /// <summary>崩溃信息旁路文本（仅托管崩溃路径）：运行环境 + 异常链 + 最近日志，供直接打开分析。</summary>
    private static void WriteInfoFile(string? dumpPath, string summary, Exception? ex)
    {
        try
        {
            var dir = DumpDirectory;
            var infoPath = dumpPath == null
                ? Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}-info.txt")
                : Path.ChangeExtension(dumpPath, ".txt");
            var sb = new StringBuilder();
            sb.AppendLine(summary);
            sb.AppendLine(new string('-', 80));
            sb.AppendLine("【异常详情】");
            if (ex != null) AppendException(sb, ex);
            else sb.AppendLine("（异常对象不是 Exception 类型，详见日志）");
            sb.AppendLine();
            sb.AppendLine(new string('-', 80));
            sb.AppendLine("【最近日志】");
            try
            {
                foreach (var entry in LogService.Instance.RecentEntries.Take(40))
                    sb.AppendLine(entry.ToLine());
            }
            catch
            {
                sb.AppendLine("（读取最近日志失败）");
            }
            File.WriteAllText(infoPath, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // 信息文件写入失败不影响转储本身
        }
    }

    private static string BuildCrashSummary(string kind, Exception? ex, bool isTerminating)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"应用发生崩溃：{kind}（进程将{(isTerminating ? "终止" : "可能继续")}）");
        sb.AppendLine($"发生时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"进程：{Process.GetCurrentProcess().ProcessName}（PID {Environment.ProcessId}）");
        sb.AppendLine($"应用版本：{GetAppVersion()}");
        sb.AppendLine($"运行时：{RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"操作系统：{RuntimeInformation.OSDescription}（{RuntimeInformation.OSArchitecture}）");
        sb.AppendLine($"进程架构：{RuntimeInformation.ProcessArchitecture}");
        if (ex != null)
        {
            sb.AppendLine($"异常类型：{ex.GetType().FullName}");
            sb.AppendLine($"异常消息：{ex.Message}");
        }
        return sb.ToString();
    }

    private static void AppendException(StringBuilder sb, Exception ex)
    {
        var current = ex;
        var depth = 0;
        while (current != null && depth < 8)
        {
            if (depth == 0)
            {
                sb.AppendLine($"{current.GetType().FullName}: {current.Message}");
            }
            else
            {
                sb.AppendLine($"  --- Inner[{depth}] {current.GetType().Name}: {current.Message}");
            }
            sb.AppendLine(current.StackTrace ?? string.Empty);
            current = current.InnerException;
            depth++;
        }
    }

    private static string GetAppVersion()
    {
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? typeof(CrashGuard).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info)) return info;
            return asm.GetName().Version?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static void TryFlushLogs()
    {
        try { LogService.Instance.Flush(); }
        catch { /* 忽略 */ }
    }

    private static void TryDirectAppend(string path, string line)
    {
        try
        {
            var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var sw = new StreamWriter(fs, Encoding.UTF8);
            sw.WriteLine(line);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>是否使用轻量 minidump（环境变量 PROMPTCRAFT_CRASH_DUMP_MINI=1 时启用）。</summary>
    private static bool UseMinidump =>
        string.Equals(Environment.GetEnvironmentVariable("PROMPTCRAFT_CRASH_DUMP_MINI"), "1",
            StringComparison.OrdinalIgnoreCase);

    private static uint ManagedDumpType() => UseMinidump
        ? MiniDumpNormal | MiniDumpWithDataSegs | MiniDumpWithHandleData | MiniDumpWithUnloadedModules | MiniDumpWithThreadInfo
        : MiniDumpWithFullMemory | MiniDumpWithHandleData | MiniDumpWithUnloadedModules | MiniDumpWithThreadInfo | MiniDumpWithFullMemoryInfo;

    private static uint NativeDumpType() =>
        MiniDumpWithDataSegs | MiniDumpWithHandleData | MiniDumpWithUnloadedModules | MiniDumpWithThreadInfo;

    // ---------------------------------------------------------------------
    // P/Invoke（Windows only；仅在 OperatingSystem.IsWindows() 分支调用）
    // ---------------------------------------------------------------------

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr SetUnhandledExceptionFilter(IntPtr lpTopLevelExceptionFilter);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(IntPtr hProcess, uint processId, IntPtr hFile, uint dumpType,
        IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);
}
