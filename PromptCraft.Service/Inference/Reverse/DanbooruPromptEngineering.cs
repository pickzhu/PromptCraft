using PromptCraft.Models.Inference;
using System.Text.RegularExpressions;

namespace PromptCraft.Service.Inference.Reverse;

// ============================================================
// Danbooru 标签列表（caption.type === Danbooru_tag_list）专用提示词工程
// 1:1 移植自 app/electron/config/danbooruPromptEngineering.js
// ============================================================

public static class DanbooruPromptEngineering
{
    public const string DanbooruType = "Danbooru_tag_list";

    public static bool Applies(ReverseCaptionRequest caption)
        => (caption.Type ?? "") == DanbooruType;

    private static bool IsZhCaption(ReverseCaptionRequest caption)
        => (caption.CaptionLang ?? "en") == "zh";

    public static string BuildSystemPrompt(ReverseCaptionRequest caption, string mediaTarget)
    {
        var video = (mediaTarget ?? caption.MediaTarget ?? "image") == "video";
        if (!IsZhCaption(caption))
        {
            var media = video ? "video key frame" : "image";
            return
                " You are a Danbooru-style TAG LIST annotator for this " +
                media +
                ". Your entire reply MUST be exactly ONE line of comma-separated English tags—never sentences, never paragraphs, never JSON, never main_text." +
                " Format: artist:unknown, copyright:original, character:none, meta:none, 1girl, solo, long hair, blue eyes, white shirt, outdoors" +
                " Rules: lowercase; comma+space between tags; spaces inside multi-word tags (long hair)—NOT underscores; order artist:/copyright:/character:/meta: then general tags." +
                " Include counts (1girl), appearance, clothing, pose, expression, background. Visible facts only.";
        }
        var mediaZh = video ? "视频关键帧" : "图像";
        return
            " 你是 Danbooru 标签标注专家，须为当前" +
            mediaZh +
            "输出可直接用于动漫类图库检索与 LoRA/SD 训练的英文标签。" +
            " 【标签规则】仅一行；英文小写；逗号+空格分隔；短语内用空格连接多词（如 long hair, brown hair, looking at viewer），勿用 long_hair 式下划线。" +
            " 严格顺序：artist:、copyright:、character:、meta:（若有）→ 通用标签。" +
            " 通用标签须覆盖：人数(1girl/1boy等)、外观、发型、服装、配饰、姿态、表情、动作、背景、镜头景别、风格媒介。" +
            " 只写画面中可见事实；禁止叙事句、禁止 Markdown、禁止中文（角色名等专有名词可用英文或罗马音）。" +
            " 不确定的 artist/copyright 用 artist:unknown、copyright:original；无角色用 character:none。" +
            " 禁止输出自检、分析或小标题，只输出一行 tag。" +
            " 禁止用方括号包裹标签（勿写 [girl]、[black hair]，应写 1girl, black hair）。";
    }

    public static string BuildOutputConstraints(ReverseCaptionRequest caption)
    {
        if (!IsZhCaption(caption))
            return " [CONTRACT] Exactly one Danbooru tag line with required prefix tokens; no self-check, chain-of-thought, or markdown headings.";
        return " 【输出契约】有且仅有一行 Danbooru 标签；含必需前缀 token；禁止分步说明、思维链与 Markdown 标题。";
    }

    /// <summary>模型泄漏自然语言 caption 时返回 true（非 tag 行）。</summary>
    public static bool LooksLikeTagProse(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0)
            return false;
        if (Regex.IsMatch(t, @"^(artist|copyright|character|meta):", RegexOptions.IgnoreCase))
            return false;
        var commas = Regex.Matches(t, ",").Count;
        if (commas >= 3 && Regex.IsMatch(t, @"\b(1girl|1boy|solo|long hair|short hair)\b", RegexOptions.IgnoreCase))
            return false;
        if (Regex.IsMatch(t, @"\b(she|he|they|the image|features a|stands|standing|wearing|dressed in|portrait of|a young)\b", RegexOptions.IgnoreCase))
            return true;
        if (Regex.IsMatch(t, @"\. [a-z]"))
            return true;
        if (!t.Contains(',') && t.Length > 90)
            return true;
        return false;
    }

    public static string BuildTypeOutputAddon(ReverseCaptionRequest caption)
    {
        var zh = IsZhCaption(caption);
        return zh
            ? " 【Danbooru】直接输出一行标签正文，勿写分析过程。"
            : " [Danbooru] ONE tag line only—NO sentences, NO JSON, NO main_text, NO analysis.";
    }

    public static string BuildUserTaskLead(ReverseCaptionRequest caption, string mediaTarget)
    {
        var video = (mediaTarget ?? caption.MediaTarget ?? "image") == "video";
        if (!IsZhCaption(caption))
            return video
                ? "List Danbooru-style tags for this video key frame as ONE line."
                : "List Danbooru-style tags for this image as ONE line.";
        return video
            ? "请为当前视频画面输出一行 Danbooru 英文标签。"
            : "请为当前图像输出一行 Danbooru 英文标签。";
    }

    public static string BuildUserTaskBody(ReverseCaptionRequest caption)
    {
        var lenHint = CaptionLength.ResolveDanbooruLenHint(caption.Len, caption.CaptionLenChars, caption.CaptionLang);
        if (string.IsNullOrEmpty(lenHint))
            lenHint = "Keep tags complete but concise.";
        if (!IsZhCaption(caption))
        {
            var hintEn = Regex.IsMatch(lenHint, @"[\u4e00-\u9fff]")
                ? "Include enough tags for appearance, clothing, pose, and background."
                : lenHint;
            return $"Start with artist:unknown, copyright:original, character:none, meta:none, then general tags. {hintEn} Example shape: artist:unknown, copyright:original, character:none, meta:none, 1girl, solo, long hair, blue eyes, outdoors. Tags only—no sentences.";
        }
        return $"按 artist:/copyright:/character:/meta: 顺序，再写通用标签；{lenHint}仅一行，英文逗号+空格分隔（如 1girl, long hair, outdoors）。";
    }

    public static string BuildUserTailAddon(ReverseCaptionRequest caption)
    {
        if (!IsZhCaption(caption))
            return " Output ONLY the tag line. Do NOT describe the image in prose.";
        return " 输出前确认：仅一行、全英文小写、短语内空格（勿下划线）、含前缀、无多余解释。";
    }

    /// <summary>从模型输出中抽出 tag 行（sanitizeTagOutput）。</summary>
    public static string SanitizeTagOutput(string text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0)
            return s;

        var lines = Regex.Split(s, @"\r?\n").Select(l => Regex.Replace(l, @"^\*\*|\*\*$", "").Trim()).ToArray();
        bool IsTagLine(string l) =>
            l.Length > 0
            && !Regex.IsMatch(l, @"^#{1,6}\s")
            && !Regex.IsMatch(l, @"^(?:tags?|danbooru)\s*:", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(l, @"self[- ]?check|fidelity\s*analysis", RegexOptions.IgnoreCase)
            && !LooksLikeTagProse(l)
            && (Regex.IsMatch(l, @"^(artist|copyright|character|meta):", RegexOptions.IgnoreCase)
                || (Regex.Matches(l, ",").Count >= 2
                    && Regex.IsMatch(l, @"\b(1girl|1boy|solo|long hair|short hair)\b", RegexOptions.IgnoreCase)));

        var line = lines.FirstOrDefault(IsTagLine)
            ?? lines.FirstOrDefault(l => l.Length > 0 && Regex.IsMatch(l, @"^(artist|copyright|character|meta):", RegexOptions.IgnoreCase) && !LooksLikeTagProse(l));

        s = line ?? "";
        if (s.Length == 0 && lines.Length > 0 && !LooksLikeTagProse(lines[0]))
            s = lines[0];
        s = Regex.Replace(s, @"^[`'""]+|[`'""]+$", "").Trim();
        if (LooksLikeTagProse(s))
            return "";
        return s;
    }
}
