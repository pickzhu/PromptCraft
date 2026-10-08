namespace PromptCraft.Models.Inference;

/// <summary>
/// 提示词仿写请求参数：输入一段「原始提示词」+ 一条「仿写要求」，
/// 按选定输出格式生成一条符合要求的新提示词（单次生成）。
/// </summary>
public sealed class ImitationRequest
{
    /// <summary>用户输入的原始提示词（必须非空）。</summary>
    public string SourcePrompt { get; set; } = "";
    /// <summary>仿写要求：用户希望如何改写（风格/侧重/限制等）。</summary>
    public string Requirement { get; set; } = "";
    /// <summary>输出语言 zh/en/auto，默认 zh。</summary>
    public string OutputLang { get; set; } = "zh";
    /// <summary>目标输出格式：prose/sd_tags/danbooru_tags/structured_md/structured_json/minimax（可空=纯自由改写）。</summary>
    public string? FormatId { get; set; }
}
