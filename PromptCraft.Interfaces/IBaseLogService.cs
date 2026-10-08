using PromptCraft.Models;

namespace PromptCraft.Interfaces;

/// <summary>
/// 应用日志服务：负责日志记录、每日轮转文件持久化、控制台输出、
/// 内存缓冲以及错误信息收集。
/// <para>
/// 通过 DI 注册为单例（见 <c>PromptCraft.Service.ConfigService.InitPromptCraftServices</c>）；
/// 在 DI 构建之前（Program / App.Initialize 阶段）可使用
/// <c>PromptCraft.Service.LogService.Instance</c> 静态引导实例。
/// </para>
/// </summary>
public interface IBaseLogService
{
    /// <summary>最小输出级别，低于该级别的日志被丢弃。</summary>
    LogLevel MinLevel { get; set; }

    /// <summary>日志文件目录（按天轮转）。</summary>
    string LogDirectory { get; }

    /// <summary>当前正在写入的日志文件路径。</summary>
    string CurrentLogFile { get; }

    /// <summary>错误收集文件路径（持久保存 Error / Fatal）。</summary>
    string ErrorFile { get; }

    void Trace(string message, string? category = null, Exception? exception = null);
    void Debug(string message, string? category = null, Exception? exception = null);
    void Info(string message, string? category = null, Exception? exception = null);
    void Warn(string message, string? category = null, Exception? exception = null);
    void Error(string message, string? category = null, Exception? exception = null);
    void Fatal(string message, string? category = null, Exception? exception = null);

    /// <summary>统一日志入口。</summary>
    void Log(LogLevel level, string message, string? category = null, Exception? exception = null);

    /// <summary>有新日志写入时触发（供 UI 实时刷新，可能在线程池线程上触发）。</summary>
    event Action<LogEntry>? LogWritten;

    /// <summary>内存中最近的日志快照（新在前）。</summary>
    IReadOnlyList<LogEntry> RecentEntries { get; }

    /// <summary>收集到的错误（Error / Fatal）快照（新在前）。</summary>
    IReadOnlyList<LogEntry> RecentErrors { get; }

    /// <summary>当前内存中收集到的错误数量。</summary>
    int ErrorCount { get; }

    /// <summary>清空内存缓冲（不影响已落盘文件）。</summary>
    void ClearBuffers();

    /// <summary>清空全部日志：清空内存缓冲，并删除磁盘上所有历史日志文件（含当日与错误收集文件）。</summary>
    void ClearAll();

    /// <summary>清理过期日志文件：只保留最近 7 天（按天轮转文件按文件名日期，错误收集文件按最后写入时间），建议启动 / 关闭时调用。</summary>
    void CleanupOldLogs();

    /// <summary>
    /// 加载指定日期的日志文件历史（解析 <c>log-yyyyMMdd.log</c>，时间升序）。
    /// 自动过滤掉本次进程启动之后写入的记录（这些已在实时缓冲中，避免重复显示）。
    /// 文件不存在或解析失败时返回空列表。
    /// </summary>
    IReadOnlyList<LogEntry> LoadDailyHistory(DateTime date);

    /// <summary>导出收集到的错误为文本文件（按时间升序）。返回实际写入路径。</summary>
    Task<string> ExportErrorsAsync(string targetFilePath);

    /// <summary>将队列中的日志立即刷入磁盘（异步写入器尽快落盘）。</summary>
    void Flush();

    /// <summary>安装全局异常处理器（AppDomain / TaskScheduler），把未捕获异常写入日志并收集。</summary>
    void InstallGlobalExceptionHandlers();
}
