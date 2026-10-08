using PromptCraft.Models.Inference;
using System.Text.RegularExpressions;

namespace PromptCraft.Service.Inference.Reverse;

// ============================================================
// 按提示词格式（caption.type）路由到对应提示词工程模块
// 1:1 移植自 app/electron/config/captionPromptEngineering.js
// ============================================================

public static class CaptionPromptBlocks
{
    public static readonly HashSet<string> SupportedTypes = new()
    {
        "Descriptive",
        "Stable_Diffusion_Prompt",
        "Danbooru_tag_list",
    };

    /// <summary>SD / Danbooru 且用户勾选「强化 ANIMA」时为 true。</summary>
    public static bool UseAnima3Enhance(ReverseCaptionRequest caption)
    {
        var t = caption.Type ?? "";
        if (t != "Stable_Diffusion_Prompt" && t != "Danbooru_tag_list")
            return false;
        return caption.Anima3Enhance;
    }

    public static bool IsTagLineCaptionType(ReverseCaptionRequest caption)
    {
        var t = caption.Type ?? "";
        return t == "Stable_Diffusion_Prompt" || t == "Danbooru_tag_list";
    }

    public static bool UseQualityPromptPrefix(ReverseCaptionRequest caption)
    {
        if (!IsTagLineCaptionType(caption))
            return false;
        return caption.QualityPromptEnabled;
    }

    /// <summary>反推完成后在标签行前追加质量词（脚本侧拼接，不要求模型输出质量 tag）。</summary>
    public static string ApplyQualityPrefixToCaption(string text, ReverseCaptionRequest caption)
    {
        var body = (text ?? "").Trim();
        if (!UseQualityPromptPrefix(caption))
            return body;

        var prefix = (caption.QualityPromptPrefix ?? "").Trim();
        if (prefix.Length == 0)
            return body;
        if (body.Length == 0)
            return prefix;

        var normPrefix = Regex.Replace(prefix, "[,，]\\s*$", "").Trim();
        var normBody = Regex.Replace(body, "^[,，]\\s*", "").Trim();

        var prefixHead = Regex.Split(normPrefix, "[,，]\\s*")[0]?.Trim().ToLowerInvariant();
        var bodyHead = Regex.Split(normBody, "[,，]\\s*")[0]?.Trim().ToLowerInvariant();

        if (!string.IsNullOrEmpty(prefixHead) && bodyHead == prefixHead)
            return normBody;

        var sep = normBody.Contains("，") && !normBody.Contains(",") ? "，" : ", ";
        return $"{normPrefix}{sep}{normBody}";
    }

    private static bool UsesDedicatedEngine(ReverseCaptionRequest caption)
        => SupportedTypes.Contains(caption.Type ?? "");

    public static bool IsComfyuiSd(ReverseCaptionRequest caption)
        => (caption.Type ?? "") == "Stable_Diffusion_Prompt";

    public static bool IsNaturalLanguage(ReverseCaptionRequest caption)
        => (caption.Type ?? "") == "Descriptive";

    public static bool IsProseCaptionType(ReverseCaptionRequest caption)
        => ComfyuiPromptEngineering.ProseTypes.Contains(caption.Type ?? "");

    public static bool IsAltProseCaption(ReverseCaptionRequest caption)
        => IsProseCaptionType(caption) && !IsNaturalLanguage(caption);

    public static bool IsDanbooru(ReverseCaptionRequest caption)
        => (caption.Type ?? "") == "Danbooru_tag_list";

    /// <summary>根据提示词格式 + 标签长度推算 max_new_tokens（前端隐藏滑块后与此保持一致）。</summary>
    public static int ResolveMaxNewTokens(ReverseCaptionRequest caption, int? reverseMax = null)
        => CaptionLength.ResolveCaptionMaxNewTokens(caption, reverseMax);

    private static string ApplyJoyExtraOptionCaptionSanitize(string? text, ReverseCaptionRequest caption)
    {
        var out_ = text;
        if (JoyCaptionExtraOptions.IsSceneOnlyNoCharacterAppearance(caption))
            out_ = CaptionExtraOptionSanitize.SanitizeNoCharacterAppearanceCaption(out_);
        if (JoyCaptionExtraOptions.HasNoGlassesHeadwearOption(caption))
            out_ = CaptionExtraOptionSanitize.SanitizeNoGlassesHeadwearCaption(out_);
        return out_ ?? "";
    }

    public static string BuildSystemPrompt(ReverseCaptionRequest caption, string mediaTarget)
    {
        if (IsNaturalLanguage(caption))
            return NaturalLanguagePromptEngineering.BuildSystemPrompt(caption, mediaTarget);
        if (IsAltProseCaption(caption))
            return AltProsePromptEngineering.BuildSystemPrompt(caption, mediaTarget);
        if (IsDanbooru(caption))
        {
            if (UseAnima3Enhance(caption))
                return Anima3PromptEngineering.BuildPrimarySystemPrompt(caption, mediaTarget, danbooru: true);
            return DanbooruPromptEngineering.BuildSystemPrompt(caption, mediaTarget);
        }
        if (IsComfyuiSd(caption))
        {
            if (UseAnima3Enhance(caption))
                return Anima3PromptEngineering.BuildPrimarySystemPrompt(caption, mediaTarget);
            return ComfyuiPromptEngineering.BuildExpertSystemPrefix(caption, mediaTarget) +
                ComfyuiPromptEngineering.BuildFaithfulReproductionBlock(caption, mediaTarget);
        }
        return ComfyuiPromptEngineering.BuildExpertSystemPrefix(caption, mediaTarget);
    }

    public static string BuildSystemAddons(ReverseCaptionRequest caption, string mediaTarget)
    {
        var lengthBlock = UsesDedicatedEngine(caption)
            ? CaptionLength.BuildCaptionLengthBlock(caption)
            : "";
        var out_ = "";

        if (UseAnima3Enhance(caption) && (IsComfyuiSd(caption) || IsDanbooru(caption)))
        {
            out_ = Anima3PromptEngineering.BuildSystemAddons(caption, mediaTarget) + lengthBlock;
        }
        else if (IsComfyuiSd(caption))
        {
            out_ = ComfyuiPromptEngineering.BuildTypeOutputAddon(caption) +
                ComfyuiPromptEngineering.BuildSystemWorkflowBlock(caption, mediaTarget) +
                lengthBlock;
        }
        else if (IsDanbooru(caption))
        {
            var tagLenHint = CaptionLength.ResolveDanbooruLenHint(caption.Len, caption.CaptionLenChars, caption.CaptionLang);
            var lenBlock = tagLenHint.Length > 0
                ? $"\n\n[Tag volume] {tagLenHint} Output remains ONE comma-separated tag line—not sentences."
                : "";
            out_ = DanbooruPromptEngineering.BuildTypeOutputAddon(caption) + lenBlock;
        }
        else if (IsAltProseCaption(caption))
        {
            out_ = ComfyuiPromptEngineering.BuildSystemWorkflowBlock(caption, mediaTarget) + lengthBlock;
        }
        else
        {
            out_ = lengthBlock;
        }

        out_ += JoyCaptionExtraPromptEngineering.BuildJoyExtraSystemEnforcementBlock(caption, mediaTarget);
        return out_;
    }

    public static string BuildOutputConstraints(ReverseCaptionRequest caption)
    {
        if (IsNaturalLanguage(caption))
            return NaturalLanguagePromptEngineering.BuildOutputConstraints(caption);
        if (IsAltProseCaption(caption))
            return AltProsePromptEngineering.BuildOutputConstraints(caption);
        if (UseAnima3Enhance(caption))
            return Anima3PromptEngineering.BuildOutputConstraints(caption);
        if (IsDanbooru(caption))
            return DanbooruPromptEngineering.BuildOutputConstraints(caption);
        if (IsComfyuiSd(caption))
        {
            var zh = (caption.CaptionLang ?? "en") == "zh";
            return zh
                ? " 【输出契约】仅一行逗号分隔标签（中文用「，」）；短语内可含空格，勿用 woman_角色_描述 式下划线长串；禁止自检、分析、Markdown 标题与思维链。"
                : " [CONTRACT] One comma-separated SD tag line—short phrases with spaces allowed; do NOT chain descriptions with underscores (woman_..., man_...); no self-check headings or markdown.";
        }
        return "";
    }

    public static string BuildUserTaskLead(ReverseCaptionRequest caption, string mediaTarget)
    {
        if (IsNaturalLanguage(caption))
            return NaturalLanguagePromptEngineering.BuildUserTaskLead(caption, mediaTarget);
        if (IsAltProseCaption(caption))
            return AltProsePromptEngineering.BuildUserTaskLead(caption, mediaTarget);
        if (UseAnima3Enhance(caption) && (IsDanbooru(caption) || IsComfyuiSd(caption)))
            return Anima3PromptEngineering.BuildUserTaskLead(caption, mediaTarget);
        if (IsDanbooru(caption))
            return DanbooruPromptEngineering.BuildUserTaskLead(caption, mediaTarget);
        if (IsComfyuiSd(caption))
            return ComfyuiPromptEngineering.BuildUserTaskFaithfulLead(caption);
        return ComfyuiPromptEngineering.BuildUserTaskFaithfulLead(caption);
    }

    public static string? BuildUserTaskBody(ReverseCaptionRequest caption)
    {
        if (IsNaturalLanguage(caption))
            return NaturalLanguagePromptEngineering.BuildUserTaskBody(caption);
        if (IsAltProseCaption(caption))
            return AltProsePromptEngineering.BuildUserTaskBody(caption);
        if (UseAnima3Enhance(caption) && IsDanbooru(caption))
            return Anima3PromptEngineering.BuildUserTaskBody(caption, danbooru: true);
        if (UseAnima3Enhance(caption) && IsComfyuiSd(caption))
            return Anima3PromptEngineering.BuildUserTaskBody(caption);
        if (IsDanbooru(caption))
            return DanbooruPromptEngineering.BuildUserTaskBody(caption);
        return null;
    }

    public static string BuildUserTailAddon(ReverseCaptionRequest caption)
    {
        if (IsNaturalLanguage(caption))
            return NaturalLanguagePromptEngineering.BuildUserTailAddon();
        if (IsAltProseCaption(caption))
            return AltProsePromptEngineering.BuildUserTailAddon(caption);
        if (UseAnima3Enhance(caption) && (IsDanbooru(caption) || IsComfyuiSd(caption)))
            return Anima3PromptEngineering.BuildUserTailAddon();
        if (IsDanbooru(caption))
            return DanbooruPromptEngineering.BuildUserTailAddon(caption);
        if (IsComfyuiSd(caption))
            return ComfyuiPromptEngineering.BuildUserTailAddon(caption);
        return "";
    }

    public static string BuildJoyExtraUserEnforcementTail(ReverseCaptionRequest caption)
        => JoyCaptionExtraPromptEngineering.BuildJoyExtraUserEnforcementTail(caption);

    public static string SanitizeFinalCaption(string text, ReverseCaptionRequest caption)
    {
        if (IsNaturalLanguage(caption) || IsAltProseCaption(caption))
        {
            Func<string, string> sanitizer = IsNaturalLanguage(caption)
                ? NaturalLanguagePromptEngineering.SanitizeProseOutput
                : AltProsePromptEngineering.SanitizeProseOutput;

            var out_ = sanitizer(text);
            out_ = ApplyJoyExtraOptionCaptionSanitize(out_, caption);
            return ApplyQualityPrefixToCaption(out_, caption);
        }

        var raw = (text ?? "").Trim();
        var output = raw;

        if (IsDanbooru(caption))
        {
            var base_ = DanbooruPromptEngineering.SanitizeTagOutput(raw);
            if (string.IsNullOrEmpty(base_) && DanbooruPromptEngineering.LooksLikeTagProse(raw))
            {
                output =
                    "[未生成有效 Danbooru 标签行；ToriiGate 对 tag 格式遵从有限，建议改用 Qwen3.5，或将「标签量」调为标准后重试]";
            }
            else if (!UseAnima3Enhance(caption))
            {
                output = base_;
            }
            else
            {
                output = Anima3PromptEngineering.SanitizeTagLine(base_) ?? base_;
            }
        }
        else if (IsComfyuiSd(caption))
        {
            if (!UseAnima3Enhance(caption))
            {
                output = raw;
            }
            else
            {
                output = Anima3PromptEngineering.SanitizeTagLine(raw) ??
                    raw.Replace("**", "").Replace("\r\n", " ").Replace("\n", " ").Trim();
            }
        }

        if (IsTagLineCaptionType(caption))
        {
            if (!(output ?? "").Trim().StartsWith("["))
                output = TagLineSanitize.CleanTagLineBody(output, caption);
        }

        output = ApplyJoyExtraOptionCaptionSanitize(output, caption);
        return ApplyQualityPrefixToCaption(output, caption);
    }
}
