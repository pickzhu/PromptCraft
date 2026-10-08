using System.Text.RegularExpressions;

namespace PromptCraft.Service.Inference.Reverse;

// ============================================================
// 按 Extra Options 勾选，对打标结果做可选后处理（仅剔除与选项对应的子句）
// 1:1 移植自 app/electron/config/captionExtraOptionSanitize.js
// ============================================================

public static class CaptionExtraOptionSanitize
{
    private static readonly Regex[] GlassesHeadwearClausePatterns =
    {
        new Regex("眼镜"),
        new Regex("护目镜"),
        new Regex("墨镜"),
        new Regex("太阳镜"),
        new Regex("镜框"),
        new Regex("粗框镜"),
        new Regex("目镜"),
        new Regex("头戴.*镜"),
        new Regex("佩戴.*镜"),
        new Regex("头饰"),
        new Regex("发饰"),
        new Regex("发箍"),
        new Regex("头箍"),
        new Regex("头带"),
        new Regex("发带"),
        new Regex("尖刺发饰"),
        new Regex("(?:鸭舌|棒球|贝雷|礼|安全|针织)?帽"),
        new Regex("头盔"),
        new Regex("皇冠"),
        new Regex("王冠"),
        new Regex("头冠"),
        new Regex("面罩"),
        new Regex("头套"),
    };

    private static readonly Regex[] AppearanceClothingClausePatterns =
    {
        new Regex("皮肤"),
        new Regex("肤色"),
        new Regex("肤质"),
        new Regex("斑(块|纹|点)"),
        new Regex("胎记"),
        new Regex("疤痕"),
        new Regex("痣"),
        new Regex("胡须"),
        new Regex("妆容"),
        new Regex("五官"),
        new Regex("面部特征"),
        new Regex("脸型"),
        new Regex("发型"),
        new Regex("头发"),
        new Regex("发丝"),
        new Regex("刘海"),
        new Regex("秃"),
        new Regex("服装"),
        new Regex("衣着"),
        new Regex("穿着"),
        new Regex("身穿"),
        new Regex("上衣"),
        new Regex("下装"),
        new Regex("裤子"),
        new Regex("裙子"),
        new Regex("连衣裙"),
        new Regex("外套"),
        new Regex("大衣"),
        new Regex("鞋"),
        new Regex("靴"),
        new Regex("袜"),
        new Regex("衣料"),
        new Regex("布料"),
        new Regex("体型"),
        new Regex("身材"),
        new Regex("肌肉线条"),
        new Regex("[腹胸臀背肩颈腰].{0,12}(斑|痣|疤痕|皮肤|肤色|毛|肌)"),
        new Regex("(?:浅|深|淡)[色色调].{0,8}斑"),
        new Regex("\\b(skin|complexion|freckle|mole|scar|birthmark)\\b", RegexOptions.IgnoreCase),
        new Regex("\\b(hair|hairstyle|bangs|beard|mustache|facial features?)\\b", RegexOptions.IgnoreCase),
        new Regex("\\b(shirt|pants|dress|skirt|outfit|garment|clothing|jacket|shoes?|boots?)\\b", RegexOptions.IgnoreCase),
        new Regex("\\b(abdomen|belly|torso).{0,20}(patch|mark|spot|freckle|mole)\\b", RegexOptions.IgnoreCase),
        new Regex("\\b(chest|breast).{0,20}(patch|mark|spot|freckle|mole)\\b", RegexOptions.IgnoreCase),
    };

    private static List<string> SplitCaptionClauses(string text)
        => new List<string>(
            Regex.Split(text ?? "", "[，,、；;。．.!！?？\n]+")
                .Select(s => s.Trim())
                .Where(s => s.Length > 0));

    private static string PickClauseSeparator(string raw)
    {
        if (raw.Contains("，")) return "，";
        if (raw.Contains("、")) return "、";
        if (raw.Contains(";")) return ";";
        return ", ";
    }

    private static bool IsGlassesHeadwearClause(string clause)
    {
        var s = (clause ?? "").Trim();
        if (s.Length == 0) return false;
        foreach (var re in GlassesHeadwearClausePatterns)
            if (re.IsMatch(s)) return true;
        return false;
    }

    private static bool IsAppearanceClothingClause(string clause)
    {
        var s = (clause ?? "").Trim();
        if (s.Length == 0) return false;
        foreach (var re in AppearanceClothingClausePatterns)
            if (re.IsMatch(s)) return true;
        return false;
    }

    private static string FilterCaptionClauses(string? text, Func<string, bool> shouldDropClause)
    {
        var raw = (text ?? "").Trim();
        if (raw.Length == 0) return raw;

        var clauses = SplitCaptionClauses(raw);
        if (clauses.Count == 0) return raw;

        var kept = clauses.Where(c => !shouldDropClause(c)).ToList();
        if (kept.Count == 0) return raw;

        return string.Join(PickClauseSeparator(raw), kept);
    }

    /// <summary>勾选「不描述人物的眼镜与头饰」时剔除相关子句。</summary>
    public static string SanitizeNoGlassesHeadwearCaption(string? text)
        => FilterCaptionClauses(text, IsGlassesHeadwearClause);

    /// <summary>勾选「不写人物外貌与衣着」时剔除相关子句。</summary>
    public static string SanitizeNoCharacterAppearanceCaption(string? text)
        => FilterCaptionClauses(text, IsAppearanceClothingClause);
}
