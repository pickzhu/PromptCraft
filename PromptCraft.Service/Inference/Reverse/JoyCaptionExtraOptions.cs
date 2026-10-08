using PromptCraft.Models.Inference;
using System.Text.RegularExpressions;

namespace PromptCraft.Service.Inference.Reverse;

// ============================================================
// JoyCaption「更多设置」选项判定
// 1:1 移植自 app/electron/config/joyCaptionExtraOptions.js
// 与前端 src/constants/joyCaptionExtraOptions.js 选项语义对齐
// ============================================================

public static class JoyCaptionExtraOptions
{
    public const string JoyCharacterNameOptionId = "character_name";
    public const string JoySceneOnlyNoCharacterAppearanceId = "scene_only_no_character_appearance";
    public const string JoyNoGlassesHeadwearOptionId = "no_glasses_headwear";
    public const string JoyNoArtisticStyleOptionId = "no_artistic_style";

    private const string JoySceneOnlyNoCharacterAppearanceEn =
        "Do NOT describe any character appearance traits or clothing. Do not write any visible physical appearance features—such as face, facial features, body, skin, hair, body shape, or similar traits—or any garments, outfits, or clothing items.";

    private const string JoyNoGlassesHeadwearOptionEn =
        "Do NOT describe any glasses, goggles, eyewear, sunglasses, or headwear on the person/character (including hats, helmets, headbands, crowns, and hair accessories worn on the head).";

    private const string JoyNoArtisticStyleEn =
        "Do NOT describe artistic style, rendering style, image medium, quality tags, aesthetic terms, or visual style labels. Do not mention anime, cartoon, realistic, painting, illustration, 3D render, cinematic, digital art, concept art, or similar style-related terms.";

    public static List<string> NormalizeJoyExtraOptionIds(IEnumerable<string>? ids)
    {
        var result = new List<string>();
        if (ids == null)
            return result;
        foreach (var id in ids)
        {
            var s = (id ?? "").Trim();
            if (s.Length > 0)
                result.Add(s);
        }
        return result;
    }

    /// <summary>从 caption 解析已启用的选项 id：显式列表优先，否则由 bool 字段推导。</summary>
    public static List<string> ResolveJoyExtraOptionIds(ReverseCaptionRequest? caption)
    {
        if (caption == null)
            return new List<string>();
        var ids = NormalizeJoyExtraOptionIds(caption.JoyExtraOptions);
        if (ids.Count > 0)
            return ids;
        var result = new List<string>();
        if (caption.JoySceneOnlyNoCharacterAppearance)
            result.Add(JoySceneOnlyNoCharacterAppearanceId);
        if (caption.JoyNoGlassesHeadwear)
            result.Add(JoyNoGlassesHeadwearOptionId);
        if (caption.JoyNoArtisticStyle)
            result.Add(JoyNoArtisticStyleOptionId);
        if (caption.JoyCharacterName)
            result.Add(JoyCharacterNameOptionId);
        return result;
    }

    public static bool HasJoyExtraOption(ReverseCaptionRequest caption, string optionId)
    {
        var ids = ResolveJoyExtraOptionIds(caption);
        if (ids.Contains(optionId))
            return true;
        var ep = (caption?.ExtraPrompt ?? "").Trim();
        if (ep.Length == 0)
            return false;
        if (optionId == JoySceneOnlyNoCharacterAppearanceId)
        {
            return ep.Contains(JoySceneOnlyNoCharacterAppearanceEn) ||
                Regex.IsMatch(ep, "Do NOT describe any character appearance traits or clothing", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(ep, "Do NOT describe any character appearance or clothing", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(ep, "Do not describe any character appearance", RegexOptions.IgnoreCase);
        }
        if (optionId == JoyNoGlassesHeadwearOptionId)
        {
            return ep.Contains(JoyNoGlassesHeadwearOptionEn) ||
                Regex.IsMatch(ep, "Do NOT describe any glasses, goggles, eyewear", RegexOptions.IgnoreCase);
        }
        if (optionId == JoyNoArtisticStyleOptionId)
        {
            return ep.Contains(JoyNoArtisticStyleEn) ||
                Regex.IsMatch(ep, "Do NOT describe artistic style", RegexOptions.IgnoreCase);
        }
        if (optionId == JoyCharacterNameOptionId)
        {
            return Regex.IsMatch(ep, "refer to them as", RegexOptions.IgnoreCase);
        }
        return false;
    }

    public static bool IsSceneOnlyNoCharacterAppearance(ReverseCaptionRequest caption)
        => HasJoyExtraOption(caption, JoySceneOnlyNoCharacterAppearanceId);

    public static bool HasJoyCharacterNameOption(ReverseCaptionRequest caption)
        => HasJoyExtraOption(caption, JoyCharacterNameOptionId);

    public static bool HasNoGlassesHeadwearOption(ReverseCaptionRequest caption)
        => HasJoyExtraOption(caption, JoyNoGlassesHeadwearOptionId);

    public static bool HasNoArtisticStyleOption(ReverseCaptionRequest caption)
        => HasJoyExtraOption(caption, JoyNoArtisticStyleOptionId);

    public static string ResolveJoyCharacterName(ReverseCaptionRequest caption)
    {
        var fromField = (caption?.JoyCharacterNameValue ?? "").Trim();
        if (fromField.Length > 0)
            return fromField;
        var ep = caption?.ExtraPrompt ?? "";
        var m = Regex.Match(ep, "refer to them as ([A-Za-z0-9_\u4e00-\u9fff]+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }
}
