using PromptCraft.Models.Inference;

namespace PromptCraft.Service.Inference.Reverse;

// ============================================================
// 非 Descriptive 的自然语言描述式（Straightforward、Descriptive_Casual 等）
// 1:1 移植自 app/electron/config/proseCaptionPromptEngineering.js
// 中文输出专用 PE，避免走 ComfyUI 标签专家 + 英文 CAPTION_TYPE_MAP。
// ============================================================

public static class AltProsePromptEngineering
{
    public static readonly HashSet<string> AltProseTypes = new()
    {
        "Descriptive_Casual",
        "Straightforward",
        "Art_Critic",
        "Product_Listing",
        "Social_Media_Post",
    };

    public static bool Applies(ReverseCaptionRequest caption)
        => AltProseTypes.Contains(CaptionType(caption));

    private static string CaptionType(ReverseCaptionRequest caption)
        => caption.Type ?? "";

    private static bool IsZhCaption(ReverseCaptionRequest caption)
        => (caption.CaptionLang ?? "en") == "zh";

    private static string ResolveWordCountHint(ReverseCaptionRequest caption)
        => CaptionLength.ResolveCaptionLengthLabel(caption.Len, caption.CaptionLenChars, caption.CaptionLang);

    private static readonly Dictionary<string, string> RoleZh = new()
    {
        ["Straightforward"] = "简洁直述式视觉描述专家",
        ["Descriptive_Casual"] = "口语化图像描述专家",
        ["Art_Critic"] = "艺术评论写作专家",
        ["Product_Listing"] = "商品图文案专家",
        ["Social_Media_Post"] = "社交媒体配文专家",
    };

    private static readonly Dictionary<string, string> StyleZh = new()
    {
        ["Straightforward"] =
            "以主体与媒介起笔；用肯定语气写清关键人物/物体/场景及颜色、形状、质感、空间关系与互动；不写情绪臆测；画面文字照录；有水印/签名/压缩痕迹则注明；勿以「这是一张…」开头。",
        ["Descriptive_Casual"] = "语气轻松口语化，像向朋友介绍画面；仍只写可见事实，不编造。",
        ["Art_Critic"] = "从构图、风格、象征、色彩与光影、可能的艺术流派等角度评论；可含审美判断，但须基于画面可见信息。",
        ["Product_Listing"] = "像电商商品详情描述：突出主体卖点、材质、颜色、规格感与使用场景（仅写图中可见者）。",
        ["Social_Media_Post"] = "像社交平台发帖配文：自然、有吸引力，仍忠实于画面内容。",
    };

    private static readonly Dictionary<string, string> BodyZh = new()
    {
        ["Straightforward"] =
            "根据当前图像输出简洁直述式中文描述：{style} 篇幅约 {wc} 字；最终仅一段连贯正文。禁止英文逗号分隔标签行、禁止 ComfyUI 关键词列表、禁止 Markdown 与思维链。",
        ["Descriptive_Casual"] =
            "根据当前图像用轻松口语写一段中文描述：{style} 篇幅约 {wc} 字；单段正文，禁止英文标签行与 Markdown。",
        ["Art_Critic"] =
            "根据当前图像写一段中文艺术评论：{style} 篇幅约 {wc} 字；单段正文，禁止提纲分点与 Markdown。",
        ["Product_Listing"] =
            "根据当前图像写一段中文商品文案：{style} 篇幅约 {wc} 字；单段或短条目式正文，禁止英文标签行。",
        ["Social_Media_Post"] =
            "根据当前图像写一段中文社交配文：{style} 篇幅约 {wc} 字；单段正文，禁止英文标签行。",
    };

    public static string BuildSystemPrompt(ReverseCaptionRequest caption, string mediaTarget)
    {
        var zh = IsZhCaption(caption);
        var t = CaptionType(caption);
        var wc = ResolveWordCountHint(caption);
        var mt = mediaTarget ?? caption.MediaTarget ?? "image";
        var video = mt == "video";
        var media = video ? (zh ? "视频画面" : "video") : zh ? "图像" : "image";

        if (zh)
        {
            var role = RoleZh.TryGetValue(t, out var r) ? r : "图像描述专家";
            var style = StyleZh.TryGetValue(t, out var st) ? st : "客观描写可见事实。";
            return $"你是{role}。针对当前{media}输出简体中文连贯正文（约 {wc} 字，可多句）。{style}" +
                " 禁止英文 comma-separated tags、禁止 ComfyUI 一行标签、禁止 Markdown 标题与思维链；画面内可见中文照录。" +
                ComfyuiPromptEngineering.BuildFaithfulReproductionBlock(caption, mediaTarget);
        }

        return ComfyuiPromptEngineering.BuildExpertSystemPrefix(caption, mediaTarget);
    }

    public static string BuildOutputConstraints(ReverseCaptionRequest caption)
    {
        if (!IsZhCaption(caption))
            return "";
        return " 【输出契约】仅一段简体中文正文（可多句）；禁止英文逗号分隔关键词列表；禁止 Markdown、自检标题与思维链。";
    }

    public static string BuildUserTaskLead(ReverseCaptionRequest caption, string mediaTarget)
    {
        var zh = IsZhCaption(caption);
        var t = CaptionType(caption);
        var wc = ResolveWordCountHint(caption);
        var mt = mediaTarget ?? caption.MediaTarget ?? "image";
        var video = mt == "video";

        if (zh)
        {
            var media = video ? "视频画面" : "图像";
            var roleHint = RoleZh.TryGetValue(t, out var rh) ? rh : "描述";
            return $"请观察当前{media}，按系统提示输出约 {wc} 字的{roleHint}中文正文。";
        }

        return ComfyuiPromptEngineering.BuildUserTaskFaithfulLead(caption);
    }

    public static string? BuildUserTaskBody(ReverseCaptionRequest caption)
    {
        if (!IsZhCaption(caption))
            return null;
        var t = CaptionType(caption);
        if (!BodyZh.TryGetValue(t, out var template))
            return null;
        var wc = ResolveWordCountHint(caption);
        var style = StyleZh.TryGetValue(t, out var st) ? st : "";
        return template.Replace("{wc}", wc).Replace("{style}", style);
    }

    public static string BuildUserTailAddon(ReverseCaptionRequest caption)
    {
        if (!IsZhCaption(caption))
            return ComfyuiPromptEngineering.BuildUserTailAddon(caption);
        return " 输出前确认：全文为简体中文连贯段落；无英文逗号标签行；无 Markdown/分节标题；勿复述提示示例。";
    }

    public static string SanitizeProseOutput(string text)
        => NaturalLanguagePromptEngineering.SanitizeProseOutput(text);
}
