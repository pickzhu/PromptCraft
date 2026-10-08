namespace PromptCraft.Service.Inference.Reverse;

// ============================================================
// 打标模型家族判定
// 1:1 移植自 app/electron/config/captionModels.js（inferCaptionModelFamily 等）
// ============================================================

/// <summary>打标模型家族。</summary>
public enum CaptionModelFamily
{
    /// <summary>未知/通用（走 OpenAI 兼容默认行为）</summary>
    Generic,
    Zhipu,
    Ollama,
    Custom,
    Hymt,
    Torii,
    Joycaption,
    Qwen35,
    Qwen3Vl,
    Gemma4,
}

public static class CaptionModels
{
    /// <summary>工作空间内本地模型根目录名（models）。</summary>
    public const string WorkspaceModelsDir = "models";

    /// <summary>HY-MT 翻译模型子目录。</summary>
    public const string HyMtModelSubdir = "HY-MT1.5-1.8B";

    /// <summary>
    /// 前端打标模型选项 value ↔ 工作空间 models 子目录名（CAPTION_MODEL_SUBDIR_MAP）。
    /// </summary>
    public static readonly Dictionary<string, string> CaptionModelSubdirMap = new()
    {
        ["joycaption"] = "llama-joycaption-beta-one-hf-llava",
        ["qwen3_vl_8b"] = "Qwen3-VL-8B-Instruct",
        ["qwen3_vl_32b"] = "Qwen3-VL-32B-Instruct",
        ["qwen3_vl_4b"] = "Qwen3-VL-4B-Instruct",
        ["qwen3_vl_8b_abliterated"] = "Qwen3-VL-8B-Instruct-abliterated",
        ["qwen3_vl_4b_abliterated"] = "Qwen3-VL-4B-Instruct-abliterated",
        ["qwen3_5_9b"] = "Qwen3.5-9B",
        ["qwen3_5_4b"] = "Qwen3.5-4B",
        ["qwen3_5_9b_abliterated"] = "Qwen3.5-9B-abliterated",
        ["qwen3_5_4b_abliterated"] = "Qwen3.5-4B-abliterated",
        ["qwen3_5_27b_abliterated"] = "Qwen3.5-27B-abliterated",
        ["gemma4_e4b_it"] = "gemma-4-E4B-it",
        ["gemma4_e4b_it_abliterated"] = "gemma-4-E4B-it-abliterated",
        ["torii_gate_0_5"] = "ToriiGate-0.5",
    };

    private static readonly Dictionary<string, string> SubdirToCaptionModelKey =
        CaptionModelSubdirMap.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>Qwen3.5 系列（含 abliterated）：qwen3_5_(9b|4b|27b)(_abliterated)?</summary>
    private static readonly System.Text.RegularExpressions.Regex Qwen35CaptionModelRe =
        new(@"^qwen3_5_(9b|4b|27b)(_abliterated)?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string CaptionModelKeyFromSubdir(string subdir)
    {
        var name = (subdir ?? "").Trim();
        if (name.Length == 0)
            return "";
        return SubdirToCaptionModelKey.TryGetValue(name, out var key) ? key : name;
    }

    /// <summary>
    /// 从模型 key 或 models 子目录名推断家族（inferCaptionModelFamily）。
    /// </summary>
    public static CaptionModelFamily InferCaptionModelFamily(string captionModel)
    {
        var v = (captionModel ?? "").Trim();
        if (v.Length == 0)
            return CaptionModelFamily.Generic;

        if (v == "zhipu" || v == "ollama")
            return v == "zhipu" ? CaptionModelFamily.Zhipu : CaptionModelFamily.Ollama;

        if (v.StartsWith("custom:", StringComparison.Ordinal))
            return CaptionModelFamily.Custom;

        if (v == "hy_mt" || System.Text.RegularExpressions.Regex.IsMatch(v, @"^hy[\-_]?mt", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return CaptionModelFamily.Hymt;

        if (v == "torii_gate_0_5" || v.Contains("torii", StringComparison.OrdinalIgnoreCase))
            return CaptionModelFamily.Torii;

        if (v == "joycaption" || v.Contains("joycaption", StringComparison.OrdinalIgnoreCase))
            return CaptionModelFamily.Joycaption;

        if (System.Text.RegularExpressions.Regex.IsMatch(v, @"qwen3[\._\-]?vl", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return CaptionModelFamily.Qwen3Vl;

        if (Qwen35CaptionModelRe.IsMatch(v) || System.Text.RegularExpressions.Regex.IsMatch(v, @"qwen3[\._\-]?\d", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return CaptionModelFamily.Qwen35;

        if (v == "gemma4_e4b_it" || v == "gemma4_e4b_it_abliterated" || v.Contains("gemma", StringComparison.OrdinalIgnoreCase))
            return CaptionModelFamily.Gemma4;

        if (CaptionModelSubdirMap.TryGetValue(v, out var mapped) && mapped != v)
            return InferCaptionModelFamily(mapped);

        return CaptionModelFamily.Generic;
    }

    public static bool IsToriiGateCaptionModel(string captionModel)
        => InferCaptionModelFamily(captionModel) == CaptionModelFamily.Torii;

    public static bool IsQwen35CaptionModel(string captionModel)
        => InferCaptionModelFamily(captionModel) == CaptionModelFamily.Qwen35;

    public static bool IsGemma4E4bCaptionModel(string captionModel)
        => InferCaptionModelFamily(captionModel) == CaptionModelFamily.Gemma4;

    /// <summary>是否疑似视觉打标模型（本地模型族）。</summary>
    public static bool IsLikelyVisionCaptionModel(string captionModel)
    {
        var family = InferCaptionModelFamily(captionModel);
        return family is CaptionModelFamily.Qwen35
            or CaptionModelFamily.Qwen3Vl
            or CaptionModelFamily.Gemma4
            or CaptionModelFamily.Torii;
    }

    public static string WorkspaceModelsRoot(string workspaceDir)
        => Path.Combine(workspaceDir, WorkspaceModelsDir);

    public static string WorkspaceHyMtModelDir(string workspaceDir)
        => Path.Combine(workspaceDir, WorkspaceModelsDir, HyMtModelSubdir);

    public static string WorkspaceModelDir(string workspaceDir, string captionModelKey)
    {
        var key = (captionModelKey ?? "").Trim();
        if (key.Length == 0)
            return Path.Combine(workspaceDir, WorkspaceModelsDir, "_unknown");
        if (key == "hy_mt")
            return WorkspaceHyMtModelDir(workspaceDir);
        var subdir = CaptionModelSubdirMap.TryGetValue(key, out var s) ? s : key;
        return Path.Combine(workspaceDir, WorkspaceModelsDir, subdir);
    }
}
