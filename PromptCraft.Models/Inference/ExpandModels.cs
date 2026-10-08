namespace PromptCraft.Models.Inference;

/// <summary>
/// 扩写请求参数（T2.1，对齐 PromptMaster expandText info 字段）。
/// </summary>
public sealed class ExpandRequest
{
    /// <summary>用户输入的简短描述。</summary>
    public string ShortText { get; set; } = "";
    /// <summary>旧规则 id（fallback，如 expand_natural）。优先使用 PeId。</summary>
    public string? RuleId { get; set; }
    /// <summary>提示词工程 profile id（如 pe_expand_descriptive）。</summary>
    public string? PeId { get; set; }
    /// <summary>篇幅档 very_short/short/medium/long/very_long，默认 medium。</summary>
    public string? LengthKey { get; set; } = "medium";
    /// <summary>自定义目标字数（length=custom 时，1~20000 任意整数）。</summary>
    public string? LengthChars { get; set; }
    /// <summary>输出语言 zh/en/auto，默认 zh。</summary>
    public string OutputLang { get; set; } = "zh";
    /// <summary>用户自定义附加提示词。</summary>
    public string? CustomPrompt { get; set; }
    /// <summary>质量词前缀（tag 类格式用，对齐 _applyExpandQualityPrefix）。</summary>
    public bool QualityPromptEnabled { get; set; }
    public string? QualityPromptPrefix { get; set; }
    /// <summary>质量词前缀格式：Danbooru_tag_list / Stable_Diffusion_Prompt（默认 SD）。</summary>
    public string? Type { get; set; }
    /// <summary>强化 ANIMA (anima3) 标签增强（tag 类格式用）。</summary>
    public bool Anima3Enhance { get; set; }
    /// <summary>参考素材路径（媒体型扩写，下一迭代使用）。</summary>
    public IReadOnlyList<string>? MediaPaths { get; set; }
    /// <summary>MiniMax 场景表单字段（媒体型扩写，下一迭代使用）。</summary>
    public IReadOnlyDictionary<string, string>? MinimaxForm { get; set; }
}

/// <summary>反推提示词解析结果（PmPromptEngineeringService.ResolveReversePrompts 返回）。</summary>
public sealed class ReverseResolvedPrompts
{
    public string CaptionType { get; set; } = "Stable_Diffusion_Prompt";
    public bool Builtin { get; set; }
    public string System { get; set; } = "";
    public string UserLead { get; set; } = "";
    public string UserBody { get; set; } = "";
    public string OutputConstraints { get; set; } = "";
    public string UserTail { get; set; } = "";
}
