using PromptCraft.Models;

namespace PromptCraft.Interfaces;

/// <summary>
/// 一条日志记录。不可变对象，用于日志文件、控制台输出、内存缓冲与 UI 展示。
/// </summary>
public sealed class LogEntry
{
    /// <summary>记录时间（本地时间）。</summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>日志级别。</summary>
    public LogLevel Level { get; init; }

    /// <summary>来源类别（如服务名 / 模块名 / 调用方类名），用于归类。</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>日志正文。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>格式化的异常信息（含内部异常与堆栈），无异常则为 null。</summary>
    public string? ExceptionText { get; init; }

    /// <summary>产生该日志的线程名。</summary>
    public string ThreadName { get; init; } = string.Empty;

    /// <summary>是否为错误级（Error / Fatal）。</summary>
    public bool IsError => Level >= LogLevel.Error;

    /// <summary>级别的短名称（用于文件与控制台）。</summary>
    public string LevelText => Level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warn => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Fatal => "FATAL",
        _ => "UNKNOWN"
    };

    /// <summary>
    /// 生成写入日志文件 / 控制台的单行（或含异常的多行）文本。
    /// </summary>
    public string ToLine() =>
        $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{LevelText}] " +
        $"{(string.IsNullOrEmpty(Category) ? string.Empty : $"[{Category}] ")}" +
        $"{Message}" +
        (string.IsNullOrEmpty(ExceptionText) ? string.Empty : Environment.NewLine + ExceptionText);
}
