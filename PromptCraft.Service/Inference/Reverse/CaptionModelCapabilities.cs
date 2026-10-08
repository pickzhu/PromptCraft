using PromptCraft.Models.Inference;

namespace PromptCraft.Service.Inference.Reverse;

// ============================================================
// 打标模型能力矩阵
// 1:1 移植自 app/electron/config/captionModelCapabilities.js
// 与提示词工程无绑定，仅描述模型固有能力：
//  - MediaTargets：支持的打标对象（image / video / mixed）
//  - LockedOutputLang：非 null 时界面锁定输出语言（torii 锁定英文）
//  - DefaultTemperature / DefaultTopP：未传采样参数时的模型默认值
// ============================================================

public static class CaptionModelCapabilityProvider
{
    public static Models.Inference.CaptionModelCapabilities GetCaptionModelCapabilities(string captionModel)
    {
        var family = CaptionModels.InferCaptionModelFamily(captionModel);
        if (family == CaptionModelFamily.Torii)
        {
            return new Models.Inference.CaptionModelCapabilities
            {
                MediaTargets = new List<string> { "image" },
                LockedOutputLang = "en",
                DefaultTemperature = 0.5,
                DefaultTopP = 0.8,
            };
        }
        if (family == CaptionModelFamily.Joycaption)
        {
            return new Models.Inference.CaptionModelCapabilities
            {
                MediaTargets = new List<string>(),
                LockedOutputLang = "en",
            };
        }
        return new Models.Inference.CaptionModelCapabilities
        {
            MediaTargets = new List<string> { "image", "video", "mixed" },
            LockedOutputLang = null,
        };
    }

    public static bool IsMediaTargetAllowed(string captionModel, string mediaTarget)
    {
        var mt = string.IsNullOrEmpty(mediaTarget) ? "mixed" : mediaTarget;
        return GetCaptionModelCapabilities(captionModel).MediaTargets.Contains(mt);
    }

    public static string? GetLockedOutputLang(string captionModel)
        => GetCaptionModelCapabilities(captionModel).LockedOutputLang;

    /// <summary>应用模型能力到 caption：锁定语言、默认采样（applyModelCapabilitiesToCaption）。</summary>
    public static void ApplyModelCapabilitiesToCaption(ReverseCaptionRequest caption, string captionModel, bool hasExplicitTemperature, bool hasExplicitTopP)
    {
        var caps = GetCaptionModelCapabilities(captionModel);
        if (!string.IsNullOrEmpty(caps.LockedOutputLang))
            caption.CaptionLang = caps.LockedOutputLang!;
        if (!hasExplicitTemperature && caps.DefaultTemperature != null)
            caption.Temperature = caps.DefaultTemperature;
        if (!hasExplicitTopP && caps.DefaultTopP != null)
            caption.TopP = caps.DefaultTopP;
    }
}
