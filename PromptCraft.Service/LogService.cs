using PromptCraft.Interfaces;
using PromptCraft.Models;
using Ke.Bee.Localization.Localizer;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace PromptCraft.Service;

/// <summary>
/// 日志服务实现。
/// <para>
/// 特性：
/// </para>
/// <list type="bullet">
/// <item>每日轮转文件写入：<c>%AppData%/PromptCraft/logs/log-yyyyMMdd.log</c>。</item>
/// <item>错误（Error / Fatal）单独持久化到 <c>errors.log</c>，并保留内存快照 <see cref="RecentErrors"/>，供错误信息收集。</item>
/// <item>内存环形缓冲 <see cref="RecentEntries"/> + <see cref="LogWritten"/> 事件，供 UI 实时查看。</item>
/// <item>后台 Channel 队列异步落盘，调用线程不阻塞；队列满时降级为同步写入，避免丢失严重日志。</item>
/// <item>Debug 构建下同步输出到控制台，便于开发期观察。</item>
/// </list>
/// 注意：内部所有写文件操作均已做异常保护，日志系统自身异常不会向外抛出（避免递归），
/// 仅尽力写入错误文件后静默。
/// </summary>
public sealed class LogService : IBaseLogService, IDisposable
{
    private static readonly Lazy<LogService> _lazyInstance = new(() => new LogService());

    /// <summary>静态引导实例：DI 就绪前（Program / App.Initialize）也可安全使用。</summary>
    public static LogService Instance => _lazyInstance.Value;

    /// <summary>本次进程启动时刻，用于加载历史时过滤掉本次运行已写入的记录（避免与实时缓冲重复）。</summary>
    public DateTime BootTime { get; } = DateTime.Now;

    private readonly object _lock = new();
    private readonly Channel<LogEntry> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerTask;
    private readonly List<LogEntry> _entries = new();   // 内存缓冲（新在前）
    private readonly List<LogEntry> _errors = new();    // 错误收集（新在前）
    private const int MaxBuffer = 2000;
    private const int MaxErrors = 500;

    private StreamWriter? _writer;
    private string _currentFile = string.Empty;
    private DateTime _currentDate;
    private bool _disposed;

    public LogService()
    {
        LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PromptCraft", "logs");
        Directory.CreateDirectory(LogDirectory);
        ErrorFile = Path.Combine(LogDirectory, "errors.log");

        // 有界通道：容量 2048，队列满时丢弃写入（丢弃的是本次写入动作，
        // 但 WriteEntrySync 的降级路径保证内存缓冲/事件仍发生，仅文件层可被丢弃）。
        _channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(2048)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
        _writerTask = Task.Run(ProcessQueueAsync);
        EnsureDailyWriter(DateTime.Now);
    }

    public LogLevel MinLevel { get; set; } = LogLevel.Info;
    public string LogDirectory { get; }
    public string ErrorFile { get; }
    public string CurrentLogFile
    {
        get { lock (_lock) return _currentFile; }
    }

    public event Action<LogEntry>? LogWritten;

    public IReadOnlyList<LogEntry> RecentEntries
    {
        get { lock (_lock) return _entries.ToArray(); }
    }

    public IReadOnlyList<LogEntry> RecentErrors
    {
        get { lock (_lock) return _errors.ToArray(); }
    }

    public int ErrorCount
    {
        get { lock (_lock) return _errors.Count; }
    }

    public void Trace(string message, string? category = null, Exception? exception = null) => Log(LogLevel.Trace, message, category, exception);
    public void Debug(string message, string? category = null, Exception? exception = null) => Log(LogLevel.Debug, message, category, exception);
    public void Info(string message, string? category = null, Exception? exception = null) => Log(LogLevel.Info, message, category, exception);
    public void Warn(string message, string? category = null, Exception? exception = null) => Log(LogLevel.Warn, message, category, exception);
    public void Error(string message, string? category = null, Exception? exception = null) =>
        Log(LogLevel.Error, message, category, exception);
    public void Fatal(string message, string? category = null, Exception? exception = null) =>
        Log(LogLevel.Fatal, message, category, exception);

    public void Log(LogLevel level, string message, string? category = null, Exception? exception = null)
    {
        if (_disposed) return;
        if (level < MinLevel) return;
        if (string.IsNullOrEmpty(message)) message = "<empty>";

        var entry = new LogEntry
        {
            Level = level,
            Category = category ?? string.Empty,
            Message = message,
            ExceptionText = exception == null ? null : FormatException(exception),
            ThreadName = Thread.CurrentThread.Name ?? $"#{Environment.CurrentManagedThreadId}"
        };

        // 非阻塞入队；队列满（DropWrite 被丢弃）时直接同步处理，保证内存/事件不丢。
        if (!_channel.Writer.TryWrite(entry))
        {
            WriteEntrySync(entry);
        }
    }

    public void ClearBuffers()
    {
        lock (_lock)
        {
            _entries.Clear();
            _errors.Clear();
        }
    }

    /// <summary>
    /// 清空全部日志：清空内存缓冲，并删除日志目录下所有日志文件
    /// （含当日 <c>log-yyyyMMdd.log</c> 与错误收集 <c>errors.log</c>），随后重建当日写入句柄。
    /// 删除当日文件前先释放写句柄，避免句柄占用导致删除失败。
    /// </summary>
    public void ClearAll()
    {
        ClearBuffers();
        lock (_lock)
        {
            try { _writer?.Dispose(); }
            catch { /* 忽略 */ }
            _writer = null;

            try
            {
                foreach (var f in Directory.GetFiles(LogDirectory, "log-*.log"))
                    File.Delete(f);
                if (File.Exists(ErrorFile)) File.Delete(ErrorFile);
            }
            catch (Exception ex)
            {
                // 部分文件可能正被外部工具占用而删除失败，记一条 Warn 不阻塞
                Warn(string.Format(Localizer.Instance?["ClearLogFilesFailed"] ?? "", ex.Message), "LogService");
            }

            _currentDate = DateTime.MinValue; // 强制重建当日写句柄
            EnsureDailyWriter(DateTime.Now);
        }
    }

    /// <summary>
    /// 清理过期日志文件：只保留最近 7 天（含今天）。
    /// 按天轮转文件 <c>log-yyyyMMdd.log</c> 从文件名解析日期，早于 7 天前删除；
    /// 错误收集文件 <c>errors.log</c> 无日期粒度，按最后写入时间判断（7 天内无新错误则清理，避免无限增长）。
    /// 删除失败（被外部工具占用 / 权限不足）时跳过该文件，不影响其他文件与应用运行。
    /// </summary>
    public void CleanupOldLogs()
    {
        try
        {
            var keepFrom = DateTime.Today.AddDays(-7); // 保留最近 7 天
            foreach (var f in Directory.GetFiles(LogDirectory, "log-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(f); // log-yyyyMMdd
                if (!name.StartsWith("log-", StringComparison.OrdinalIgnoreCase)) continue;
                if (DateTime.TryParseExact(name.AsSpan(4), "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date)
                    && date < keepFrom)
                {
                    TryDeleteFile(f);
                }
            }

            // errors.log 无日期粒度：最后写入时间早于 7 天（期间未产生新错误）则清理
            if (File.Exists(ErrorFile) && File.GetLastWriteTime(ErrorFile) < keepFrom)
            {
                TryDeleteFile(ErrorFile);
            }
        }
        catch (Exception ex)
        {
            // 清理失败不影响应用运行
            Warn(string.Format(Localizer.Instance?["CleanOldLogsFailed"] ?? "", ex.Message), "LogService");
        }
    }

    /// <summary>尽力删除单个文件；被占用 / 权限不足时静默跳过。</summary>
    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 文件被占用（如外部工具正在读取），跳过
        }
        catch (UnauthorizedAccessException)
        {
            // 权限不足，跳过
        }
    }

    /// <summary>
    /// 加载指定日期的日志文件历史（时间升序）。
    /// 自动跳过本次进程启动之后写入的行（那些记录已在实时内存缓冲中，避免 UI 重复显示）。
    /// </summary>
    public IReadOnlyList<LogEntry> LoadDailyHistory(DateTime date)
    {
        var path = Path.Combine(LogDirectory, $"log-{date:yyyyMMdd}.log");
        if (!File.Exists(path)) return Array.Empty<LogEntry>();

        try
        {
            // 关键：必须用 FileShare.ReadWrite 打开（与写入句柄一致）。
            // File.ReadAllLines 默认 FileShare.Read 只共享"读"，与写者的
            // FileShare.ReadWrite 双向不兼容：应用运行期间读取会抛 IOException，
            // 导致历史日志永远加载不出来。改为手动 FileStream 逐行读。
            var lines = new List<string>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
            {
                string? line;
                while ((line = reader.ReadLine()) != null) lines.Add(line);
            }

            var entries = ParseLogLines(lines.ToArray());
            // 只取本次启动之前的历史；本次启动的日志由实时缓冲提供
            return entries.Where(e => e.Timestamp < BootTime).ToList();
        }
        catch (Exception ex)
        {
            // 历史加载失败不阻塞页面，但不再静默吞掉——记一条 Warn 便于排查
            Warn(string.Format(Localizer.Instance?["ReadHistoryLogFailed"] ?? "", path), "LogService", ex);
            return Array.Empty<LogEntry>();
        }
    }

    public void Flush()
    {
        // 等待队列排空（尽力），随后刷盘
        for (var i = 0; i < 200 && _channel.Reader.Count > 0; i++)
        {
            Thread.Sleep(2);
        }
        lock (_lock)
        {
            try { _writer?.Flush(); } catch { /* 忽略 */ }
        }
    }

    /// <summary>导出收集到的错误（按时间升序）到指定文本文件。</summary>
    public Task<string> ExportErrorsAsync(string targetFilePath)
    {
        LogEntry[] snapshot;
        lock (_lock)
        {
            snapshot = _errors.OrderBy(e => e.Timestamp).ToArray();
        }

        return Task.Run(() =>
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine(string.Format(Localizer.Instance?["LogExportFileNameFormat"] ?? "", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
                sb.AppendLine(string.Format(Localizer.Instance?["LogExportSummaryFormat"] ?? "", snapshot.Length));
                sb.AppendLine(new string('-', 80));
                foreach (var entry in snapshot)
                {
                    sb.AppendLine(entry.ToLine());
                    sb.AppendLine(new string('-', 80));
                }
                File.WriteAllText(targetFilePath, sb.ToString(), Encoding.UTF8);
                return targetFilePath;
            }
            catch (Exception ex)
            {
                // 导出失败记入日志并抛出，交由调用方处理
                Error(string.Format(Localizer.Instance?["ExportErrorsLogFailed"] ?? "", targetFilePath), "LogService", ex);
                throw;
            }
        });
    }

    public void InstallGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Fatal(Localizer.Instance?["UnhandledAppDomainException"] ?? "", "Global", ex);
            }
            else
            {
                Fatal(string.Format(Localizer.Instance?["UnhandledAppDomainExceptionObject"] ?? "", e.ExceptionObject), "Global");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // 标记为已观察，防止进程被终结；异常本身记录并收集。
            e.SetObserved();
            Error(Localizer.Instance?["UnobservedTaskException"] ?? "", "Global", e.Exception);
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        try { _writerTask.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* 写入器已被取消或超时 */ }

        // 排空剩余日志
        while (_channel.Reader.TryRead(out var entry))
        {
            WriteEntrySync(entry);
        }

        lock (_lock)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
        _cts.Dispose();
    }

    // ---------------------------------------------------------------------
    // 私有实现
    // ---------------------------------------------------------------------

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var entry in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                WriteEntrySync(entry);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关闭
        }
        catch (Exception ex)
        {
            // 写入器自身异常不能递归日志，直接尽力追加到错误文件
            TryDirectAppend(ErrorFile,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [FATAL] [LogService] 日志写入器异常: {ex}");
        }
    }

    /// <summary>
    /// 同步处理一条日志：更新内存缓冲、落盘、触发事件。
    /// 可在调用线程（降级路径）或后台写入器线程上执行。
    /// </summary>
    private void WriteEntrySync(LogEntry entry)
    {
        lock (_lock)
        {
            _entries.Insert(0, entry);
            if (_entries.Count > MaxBuffer) _entries.RemoveAt(_entries.Count - 1);

            if (entry.IsError)
            {
                _errors.Insert(0, entry);
                if (_errors.Count > MaxErrors) _errors.RemoveAt(_errors.Count - 1);
            }
        }

        try
        {
            lock (_lock)
            {
                EnsureDailyWriter(entry.Timestamp);
                _writer!.WriteLine(entry.ToLine());
                _writer.Flush();
            }
        }
        catch
        {
            // 日志文件不可用（磁盘满、权限等）时静默，避免递归
        }

        if (entry.IsError)
        {
            TryAppendErrorFile(entry);
        }

#if DEBUG
        WriteToConsole(entry);
#endif

        LogWritten?.Invoke(entry);
    }

    /// <summary>确保打开当日日志文件（若跨天则切换）。调用方需持有 <see cref="_lock"/>。</summary>
    private void EnsureDailyWriter(DateTime now)
    {
        var date = now.Date;
        if (_currentDate == date && _writer != null) return;

        _currentDate = date;
        _writer?.Dispose();
        var path = Path.Combine(LogDirectory, $"log-{date:yyyyMMdd}.log");
        // FileShare.ReadWrite：允许外部工具/进程在应用运行期间读取/复制日志，便于查看与收集
        var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };
        _currentFile = path;
    }

    private void TryAppendErrorFile(LogEntry entry)
    {
        try
        {
            var fs = new FileStream(ErrorFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var err = new StreamWriter(fs, Encoding.UTF8);
            err.WriteLine(entry.ToLine());
        }
        catch
        {
            // 忽略
        }
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

#if DEBUG
    private static void WriteToConsole(LogEntry entry)
    {
        var previous = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = entry.Level switch
            {
                LogLevel.Trace => ConsoleColor.DarkGray,
                LogLevel.Debug => ConsoleColor.Gray,
                LogLevel.Info => ConsoleColor.White,
                LogLevel.Warn => ConsoleColor.Yellow,
                LogLevel.Error => ConsoleColor.Red,
                LogLevel.Fatal => ConsoleColor.Magenta,
                _ => ConsoleColor.Gray
            };
            Console.WriteLine(entry.ToLine());
        }
        catch
        {
            // 忽略
        }
        finally
        {
            Console.ForegroundColor = previous;
        }
    }
#endif

    private static string FormatException(Exception ex)
    {
        var sb = new StringBuilder();
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
        return sb.ToString();
    }

    // ---------------------------------------------------------------------
    // 历史文件解析（LogEntry.ToLine 的逆操作）
    // ---------------------------------------------------------------------

    /// <summary>
    /// 单行记录头：<c>yyyy-MM-dd HH:mm:ss.fff [LEVEL] ([Category] )?Message</c>。
    /// 异常/堆栈为后续非头部行，解析时并入上一条记录的 ExceptionText。
    /// </summary>
    private static readonly Regex HeaderRegex = new(
        @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) \[([A-Z]+)\] (?:\[([^\]]*)\] )?(.*)$",
        RegexOptions.Compiled);

    /// <summary>解析日志文件行，返回时间升序的日志记录列表。</summary>
    private static List<LogEntry> ParseLogLines(string[] lines)
    {
        var result = new List<LogEntry>();
        PendingEntry? pending = null;

        foreach (var line in lines)
        {
            var m = HeaderRegex.Match(line);
            if (m.Success)
            {
                // 上一段记录结束，提交
                if (pending != null) result.Add(pending.Build());

                pending = new PendingEntry
                {
                    Timestamp = DateTime.ParseExact(m.Groups[1].Value,
                        "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    Level = ParseLevel(m.Groups[2].Value),
                    Category = m.Groups[3].Success ? m.Groups[3].Value : string.Empty,
                    Message = m.Groups[4].Value
                };
            }
            else if (pending != null)
            {
                // 异常 / 堆栈续行，并入上一条记录
                pending.ExceptionText ??= new StringBuilder();
                pending.ExceptionText.AppendLine(line);
            }
        }

        if (pending != null) result.Add(pending.Build());
        return result;
    }

    /// <summary>级别短名 → 枚举（无法识别时按 Info 处理）。</summary>
    private static LogLevel ParseLevel(string text) => text switch
    {
        "TRACE" => LogLevel.Trace,
        "DEBUG" => LogLevel.Debug,
        "INFO" => LogLevel.Info,
        "WARN" => LogLevel.Warn,
        "ERROR" => LogLevel.Error,
        "FATAL" => LogLevel.Fatal,
        _ => LogLevel.Info
    };

    /// <summary>解析过程中的可变中间对象（LogEntry 为 init-only，最后一次性提交）。</summary>
    private sealed class PendingEntry
    {
        public DateTime Timestamp { get; init; }
        public LogLevel Level { get; init; }
        public string Category { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public StringBuilder? ExceptionText { get; set; }

        public LogEntry Build() => new()
        {
            Timestamp = Timestamp,
            Level = Level,
            Category = Category,
            Message = Message,
            ExceptionText = ExceptionText?.ToString()
        };
    }
}
