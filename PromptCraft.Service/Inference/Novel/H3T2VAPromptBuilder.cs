using System.Text;
using PromptCraft.Models.Inference.Novel;

namespace PromptCraft.Service.Inference.Novel;

/// <summary>
/// MiniMax H3 T2VA 三字段模式（官方 base-en 规范）：无参考图时按开关降级使用。
/// 三字段顺序与标题随输出语言切换：中文「综合多模态描述: / 整体声景: / 非叙事配乐:」，
/// English「integrated_multimodal_description: / overall_soundscape: / non_diegetic_music:」。
/// 组装复用官方 T2VA 指南（h3-base-video-prompt.en/zh.md，嵌入资源），
/// 覆盖镜头时间码、运镜三要素、说话人 (Sx)/对白 &lt;d&gt;、旁白口型锁定、跨切 &lt;scenetrans&gt;/截断 &lt;cutoff&gt; 等规范。
/// </summary>
public static class H3T2VAPromptBuilder
{
    private static readonly Dictionary<string, string> CachedGuides = new();

    /// <summary>按输出语言加载 T2VA 指南（中文/英文两套，EmbeddedResource 读取）。</summary>
    public static string LoadGuide(string? lang)
    {
        var key = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";
        if (CachedGuides.TryGetValue(key, out var cached)) return cached;
        var fileName = key == "en" ? "h3-base-video-prompt.en.md" : "h3-base-video-prompt.zh.md";
        string text;
        try
        {
            using var stream = typeof(H3T2VAPromptBuilder).Assembly.GetManifestResourceStream($"PromptCraft.Service.prompts.{fileName}");
            using var reader = stream == null ? null : new StreamReader(stream);
            text = reader?.ReadToEnd() ?? "";
        }
        catch
        {
            text = "";
        }
        CachedGuides[key] = text.Trim();
        return CachedGuides[key];
    }

    /// <summary>三字段标题（随输出语言切换，正文语言亦随选项强制）。</summary>
    public static string[] FieldTitles(NovelOutputLanguage lang) => lang == NovelOutputLanguage.English
        ? new[] { "integrated_multimodal_description:", "overall_soundscape:", "non_diegetic_music:" }
        : new[] { "综合多模态描述:", "整体声景:", "非叙事配乐:" };

    /// <summary>为指定镜头生成 T2VA 三字段模式的 system 指令（含官方指南全文）。</summary>
    public static string BuildSystem(NovelPipelineContext ctx, ShotItem shot, string aspectRatio, NovelOutputLanguage lang)
    {
        var isEn = lang == NovelOutputLanguage.English;
        var titles = FieldTitles(lang);
        var sb = new StringBuilder();
        sb.AppendLine(isEn
            ? "You are a professional MiniMax H3 video prompt engineer (T2VA text-to-video, three-field mode). For the given single-shot design, output ONE ready-to-use three-field H3 prompt."
            : "你是一名专业的 MiniMax H3 视频提示词工程师（T2VA 文生视频·三字段模式）。针对给定的单镜头设计，输出一条可直接投入 MiniMax H3 的三字段提示词。");
        sb.AppendLine();
        sb.AppendLine(isEn ? "Output language (HIGHEST priority):" : "输出语言（最高优先级）：");
        sb.AppendLine(NovelPromptEngineering.LanguageDirective(lang));
        sb.AppendLine();
        sb.AppendLine(isEn ? "Hard requirements:" : "硬性要求：");
        sb.AppendLine($"1. {(isEn ? "Output ONLY the three fields in this exact order" : "只输出以下三个字段，按此严格顺序")}: {titles[0]} / {titles[1]} / {titles[2]}。{(isEn ? "Never use six-section headers (subject_definitions/summary/retention_analysis/detailed_description)." : "禁止使用六段式标题（主体定义/摘要/保留分析/详细描述等）。")}");
        sb.AppendLine($"2. {(isEn ? "Total duration" : "镜头总时长")} {shot.Duration.ToString("0.#")} {(isEn ? "seconds" : "秒")}、{(isEn ? "aspect ratio" : "画幅")} {aspectRatio}。{(isEn ? "Match the total duration of the description to this shot length (4–15 seconds); cut times must never exceed it." : "整段描述的总时长必须与本镜头时长一致（4–15 秒）；切点不得超出镜头时长。")}");
        sb.AppendLine($"3. {(isEn ? "Dialogue uses the exact user-confirmed original text" : "对白使用用户确认的准确原文，不得翻译、润色或压缩")}；{(isEn ? "stable speaker IDs" : "说话者用稳定 ID")} (S1)/(S2)；{(isEn ? "dialogue format" : "对白格式")} <d>[Chinese]…</d> / <d>[English]…</d>；{(isEn ? "voiceover must lock lips closed" : "旁白必须锁定嘴唇闭合")}；{(isEn ? "cross-cut dialogue uses <scenetrans>, end truncation uses <cutoff>" : "跨切对白用 <scenetrans>，结尾截断用 <cutoff>")}。");
        sb.AppendLine($"4. {(isEn ? "Shot 1 has no timestamp; later shots use [Shot N] At MM:SS.mmm with strictly increasing cut times" : "首镜 [Shot 1] 不加时间戳；后续镜头 [Shot N] At MM:SS.mmm，切点严格递增")}。");
        sb.AppendLine($"5. {(isEn ? "Camera motion: motion type + amplitude + speed written as natural English sentences, never stacked labels" : "运镜按「类型+幅度+速度」写成自然句，禁止标签堆叠")}。");
        sb.AppendLine($"6. {(isEn ? "No reference image in this mode: NEVER invent <Picture N>/<Subject N>/<Video N>/<Audio N>; describe subjects in plain language only" : "本模式无参考图：禁止编造 <Picture N>/<Subject N>/<Video N>/<Audio N>，用自然语言描述主体")}。");
        sb.AppendLine($"7. {(isEn ? "Forbid context-dependent words (previous shot / continue / same as before); every prompt must be independently understandable, with a full physical description of the first frame" : "禁止“上一镜头、继续、同上、仍然、保持原样、和之前一样”等上下文依赖词；每条提示词必须能脱离聊天独立理解，首帧给出完整物理描述")}。");
        sb.AppendLine();
        sb.AppendLine(isEn ? "## T2VA Guide (follow strictly)" : "## T2VA 指南（请严格遵循）");
        sb.AppendLine(LoadGuide(isEn ? "en" : "zh"));
        return sb.ToString();
    }

    /// <summary>为指定短文本（角色圣经+已确认资产+本镜头+上镜尾帧）生成 T2VA 三字段模式的 user 指令。</summary>
    public static string BuildUser(string shortText, NovelOutputLanguage lang)
    {
        var titles = FieldTitles(lang);
        var langName = NovelPromptEngineering.LangName(lang);
        return $"{shortText.Trim()}\n\n请以三字段 T2VA 结构输出（字段名：{titles[0]} / {titles[1]} / {titles[2]}），正文使用{langName}；不要输出六段式标题，不要解释。";
    }
}
