namespace PromptCraft.Models;

/// <summary>
/// 日志级别，数值越大越严重。
/// </summary>
public enum LogLevel
{
    /// <summary>最细粒度，用于跟踪执行路径。</summary>
    Trace = 0,

    /// <summary>调试信息，用于开发期诊断。</summary>
    Debug = 1,

    /// <summary>一般运行信息。</summary>
    Info = 2,

    /// <summary>警告，可能存在问题但不影响运行。</summary>
    Warn = 3,

    /// <summary>错误，功能受损但应用仍可继续。</summary>
    Error = 4,

    /// <summary>致命错误，通常伴随应用退出。</summary>
    Fatal = 5
}
