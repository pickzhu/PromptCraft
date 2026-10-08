using BaseClassLib.Models;
using SukiUI.Enums;

namespace PromptCraft.Models;

/// <summary>
/// 应用配置实体，对应 BaseClassLib.Models.Config，
/// 用 SQLite 替代 JSON 文件存储。
/// 单行表（Id=1）。
/// </summary>
public class AppConfig
{
    public int Id { get; set; }

    // ---- 主题 ----
    public bool IsLight { get; set; } = true;
    public bool BackgroundAnimations { get; set; }
    public SukiBackgroundStyle BackgroundStyle { get; set; } = SukiBackgroundStyle.Flat;
    public bool BackgroundTransitions { get; set; }
    public bool ShowBottomBar { get; set; } = true;
    public bool ShowTitleBar { get; set; } = true;

    // ---- 语言 ----
    public LanguageEnum Language { get; set; } = LanguageEnum.zh_CN;

    // ---- 主题色 ----
    public string? SukiColorThemeName { get; set; }
    public string? AddedThemeColorsJson { get; set; } // JSON 序列化的颜色列表

    // ---- ComfyUI ----
    public string ComfyApiUrl { get; set; } = "http://127.0.0.1:8188";
    public string ComfyOutputDir { get; set; } = string.Empty;
    public int ThumbMaxDimension { get; set; } = 300;
    public int ThumbQuality { get; set; } = 80;

    // ---- 工作空间（PromptMaster 迁移 T0.1）----
    /// <summary>
    /// 用户可配置的工作空间根目录（绝对路径）。
    /// 每段仅允许 [A-Za-z0-9_-]，禁空格/中文/UNC（对齐 PromptMaster P 报告 5.4）。
    /// 为空时使用默认目录 %APPDATA%/PromptCraft/workspace。
    /// </summary>
    public string WorkspaceDir { get; set; } = string.Empty;

    // ---- 日志 ----
    /// <summary>日志输出级别（低于该级别的日志被过滤）</summary>
    public LogLevel LogLevel { get; set; } = LogLevel.Info;

    // ---- 翻译引擎（T0.4）----
    /// <summary>翻译引擎：openai / baidu，默认 openai。</summary>
    public string TranslateEngine { get; set; } = "openai";
    public string BaiduAppId { get; set; } = string.Empty;
    public string BaiduAppKey { get; set; } = string.Empty;
}