using PromptCraft.Models.Inference;

namespace PromptCraft.Service.Inference.Reverse;

// ============================================================
// 反推篇幅计算
// 1:1 移植自 app/electron/config/captionLength.js（依赖 promptExpandRules.js 的 spec 逻辑）
// ============================================================

/// <summary>篇幅规格。</summary>
public sealed record CaptionLengthSpec(
    bool Custom,
    int? Chars,
    int MaxTokens,
    string HintZh,
    string HintEn);

public static class CaptionLength
{
    /// <summary>本地默认 reverseMax（DEFAULT_LOCAL_TOKEN_LIMITS.reverseMax）。</summary>
    public const int DefaultReverseMax = 1024;

    /// <summary>reverseMax 设置范围（localTokenLimits.js BOUNDS）。</summary>
    public const int ReverseMaxMin = 128;
    public const int ReverseMaxMax = 8192;

    /// <summary>兼容旧 len 值映射（LEGACY_LEN_MAP）。</summary>
    private static readonly Dictionary<string, string> LegacyLenMap = new()
    {
        ["very short"] = "very_short",
        ["very_short"] = "very_short",
        ["short"] = "short",
        ["medium length"] = "medium",
        ["medium"] = "medium",
        ["long"] = "long",
        ["very long"] = "very_long",
        ["any"] = "medium",
    };

    /// <summary>界面字数范围标签（WORD_COUNT_RANGE）。</summary>
    private static readonly Dictionary<string, string> WordCountRange = new()
    {
        ["very_short"] = "80以内",
        ["short"] = "80～150",
        ["medium"] = "150～300",
        ["long"] = "300～500",
        ["very_long"] = "500～800",
    };

    /// <summary>按 type 的 token 缩放（TYPE_TOKEN_SCALE）。</summary>
    private static readonly Dictionary<string, double> TypeTokenScale = new()
    {
        ["Descriptive"] = 1.25,
        ["Stable_Diffusion_Prompt"] = 1,
        ["Danbooru_tag_list"] = 0.75,
    };

    private static int CapTokens(int n, int min, int max)
        => Math.Min(max, Math.Max(min, n));

    /// <summary>解析自定义字数（parseCustomCharCount）。</summary>
    public static int? ParseCustomCharCount(string? len, int? lenChars)
    {
        if (lenChars != null && lenChars > 0)
            return lenChars.Value;
        var k = (len ?? "").Trim();
        if (int.TryParse(k, out var n) && n > 0)
            return n;
        return null;
    }

    /// <summary>规范化 len → (lenKey, lenChars)（normalizeCaptionLenKeys）。</summary>
    public static (string LenKey, int? LenChars) NormalizeCaptionLenKeys(string? len, int? lenChars)
    {
        var l = string.IsNullOrEmpty(len) ? "medium" : len!.Trim();
        var charsFromField = ParseCustomCharCount(l, lenChars);
        if (charsFromField != null)
            return ("custom", charsFromField);
        if (int.TryParse(l, out var n2) && l.Length > 0 && !l.StartsWith("-"))
            return ("custom", n2);
        if (l == "custom")
            return ("custom", null);
        var mapped = LegacyLenMap.TryGetValue(l, out var m) ? m
            : l is "very_short" or "short" or "medium" or "long" or "very_long" ? l
            : "medium";
        return (mapped, null);
    }

    /// <summary>解析篇幅规格（resolveCaptionLengthSpec，含 custom 字数 ×1.8 上限）。</summary>
    public static CaptionLengthSpec ResolveCaptionLengthSpec(string? len, int? lenChars, string outputLang, int? reverseMax = null)
    {
        var maxCap = ResolveReverseMaxCap(reverseMax);
        var (lenKey, chars) = NormalizeCaptionLenKeys(len, lenChars);
        var useEn = (outputLang ?? "zh").ToLowerInvariant() == "en";

        if (chars != null)
        {
            var mt = CapTokens((int)Math.Ceiling(chars.Value * 1.8), 128, maxCap);
            return new CaptionLengthSpec(
                true,
                chars,
                mt,
                $"扩写结果目标约 {chars} 字（按中文字符计）或等价英文篇幅，请尽量接近该长度，不要明显过短或远超。",
                $"Target output length about {chars} characters/words; match this scale closely.");
        }

        var preset = LengthPresetByKey(lenKey);
        return new CaptionLengthSpec(false, null, preset.MaxTokens, preset.HintZh, preset.HintEn);
    }

    public sealed record LengthPresetDef(string Key, int MaxTokens, string HintZh, string HintEn);

    private static readonly IReadOnlyList<LengthPresetDef> Presets = new List<LengthPresetDef>
    {
        new("very_short", 128,
            "打标/扩写结果控制在 80 字以内（或等价英文词数），只保留最关键信息，禁止冗长描述与重复堆砌。",
            "Keep output within 80 words or equivalent; essential details only, no verbosity."),
        new("short", 256,
            "扩写结果控制在约 80～150 字（或等价英文词数），以关键词、短句为主，避免冗长段落与重复堆砌。",
            "Keep output concise: about 80–150 words or equivalent, keyword-focused; no long paragraphs."),
        new("medium", 512,
            "扩写结果约 150～300 字，细节适中，兼顾画面信息量与可读性，不要明显偏短或偏长。",
            "Target about 150–300 words or equivalent detail level; balanced, not too brief or verbose."),
        new("long", 768,
            "扩写结果约 300～500 字，补充丰富的视觉细节、材质、光线、构图与氛围，仍只输出提示词正文。",
            "Target about 300–500 words with rich visual details (materials, lighting, mood); prompt text only."),
        new("very_long", 1024,
            "扩写结果约 500～800 字，尽可能充实画面层次与专业术语，禁止废话、解释与思维链。",
            "Target about 500–800 words, maximize visual depth; no filler, explanation, or chain-of-thought."),
    };

    private static LengthPresetDef LengthPresetByKey(string key)
    {
        foreach (var p in Presets)
            if (p.Key == key)
                return p;
        return Presets[2]; // medium default
    }

    /// <summary>reverseMax 上限（设置项未配置时默认 1024）。</summary>
    public static int ResolveReverseMaxCap(int? configured)
    {
        if (configured == null || configured.Value <= 0)
            return DefaultReverseMax;
        return Math.Min(ReverseMaxMax, Math.Max(ReverseMaxMin, configured.Value));
    }

    /// <summary>篇幅标签（自定义返回数字，否则 80以内/80～150/...）。</summary>
    public static string ResolveCaptionLengthLabel(string? len, int? lenChars, string outputLang)
    {
        var spec = ResolveCaptionLengthSpec(len, lenChars, outputLang);
        if (spec.Custom && spec.Chars != null)
            return spec.Chars.Value.ToString();
        return WordCountRange.TryGetValue(specKey(len), out var v) ? v : WordCountRange["medium"];
    }

    private static string specKey(string? len)
    {
        var (key, _) = NormalizeCaptionLenKeys(len, null);
        return key == "custom" ? "medium" : key;
    }

    /// <summary>中英篇幅提示（buildCaptionLengthHint）。</summary>
    public static string BuildCaptionLengthHint(string? len, int? lenChars, string outputLang)
    {
        var spec = ResolveCaptionLengthSpec(len, lenChars, outputLang);
        return (outputLang ?? "zh").ToLowerInvariant() == "zh" ? spec.HintZh : spec.HintEn;
    }

    /// <summary>system 追加篇幅块（buildCaptionLengthBlock）。</summary>
    public static string BuildCaptionLengthBlock(ReverseCaptionRequest caption)
    {
        var hint = BuildCaptionLengthHint(caption.Len, caption.CaptionLenChars, caption.CaptionLang);
        var zh = (caption.CaptionLang ?? "en") == "zh";
        return $"\n\n【{(zh ? "篇幅" : "Length")}】{hint}";
    }

    /// <summary>解析 max_new_tokens（resolveCaptionMaxNewTokens）。</summary>
    public static int ResolveCaptionMaxNewTokens(ReverseCaptionRequest caption, int? reverseMax = null)
    {
        var cap = ResolveReverseMaxCap(reverseMax);
        var type = string.IsNullOrEmpty(caption.Type) ? "Stable_Diffusion_Prompt" : caption.Type;
        var spec = ResolveCaptionLengthSpec(caption.Len, caption.CaptionLenChars, caption.CaptionLang);
        var scale = TypeTokenScale.TryGetValue(type, out var s) ? s : 1;

        if (spec.Custom && spec.Chars != null)
        {
            var n = spec.Chars.Value;
            if (type == "Descriptive")
                return CapTokens((int)Math.Ceiling(n * 2.2), 256, cap);
            if (type == "Danbooru_tag_list")
                return CapTokens((int)Math.Ceiling(n * 1.6), 192, Math.Min(512, cap));
            return CapTokens((int)Math.Ceiling(n * 1.8), 128, Math.Min(768, cap));
        }

        return CapTokens((int)Math.Ceiling(spec.MaxTokens * scale), 128, cap);
    }

    /// <summary>ANIMA3 标签量档（resolveAnima3TagCountBand）。</summary>
    public static (string Band, string Tier) ResolveAnima3TagCountBand(string? len, int? lenChars)
    {
        var (lenKey, chars) = NormalizeCaptionLenKeys(len, lenChars);
        if (chars != null)
        {
            var n = Math.Min(80, Math.Max(16, (int)Math.Round(chars.Value / 10.0)));
            return (n.ToString(), "custom");
        }
        return (lenKey ?? "medium") switch
        {
            "very_short" => ("10-18", "minimal"),
            "short" => ("16-30", "simple"),
            "long" => ("30-48", "complex"),
            "very_long" => ("36-55", "complex"),
            _ => ("22-38", "standard"),
        };
    }

    /// <summary>Danbooru 标签量提示（resolveDanbooruLenHint）。</summary>
    public static string ResolveDanbooruLenHint(string? len, int? lenChars, string outputLang)
    {
        var (lenKey, chars) = NormalizeCaptionLenKeys(len, lenChars);
        var zh = (outputLang ?? "zh") == "zh";
        if (chars != null)
        {
            return zh
                ? $"标签总量约 {chars} 个以内，仍须一行逗号分隔。"
                : $"About {chars} tags max, still one comma-separated line.";
        }
        var band = (lenKey ?? "medium") switch
        {
            "very_short" => zh ? "约 8～15 个 tag" : "about 8–15 tags",
            "short" => zh ? "约 15～25 个 tag" : "about 15–25 tags",
            "long" => zh ? "约 35～55 个 tag" : "about 35–55 tags",
            "very_long" => zh ? "约 45～70 个 tag" : "about 45–70 tags",
            _ => zh ? "约 25～40 个 tag" : "about 25–40 tags",
        };
        return zh
            ? $"标签量 {band}，仍须一行输出。"
            : $"Tag count {band}, one line only.";
    }
}
