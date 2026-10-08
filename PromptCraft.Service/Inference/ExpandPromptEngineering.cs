using PromptCraft.Models.Inference;
using PromptCraft.Service.Inference.Minimax;
using PromptCraft.Service.Inference.Reverse;

namespace PromptCraft.Service.Inference;

/// <summary>
/// 提示词工程（PE）解析层：1:1 迁移自 app/electron/config/promptEngineeringResolver.js
/// （resolveExpandPrompts）+ expandReverseMirror.js（resolveExpandFromReverseMirror / mapLegacyExpandPeId）。
///
/// 内置扩写 profile = 3 反推镜像（Descriptive / Stable_Diffusion_Prompt / Danbooru_tag_list）
///                    + 10 torii 结构化镜像（pe_expand_torii_*）
///                    + 10 MiniMax 场景（pe_expand_h3_* / pe_expand_minimax_*）。
/// </summary>
public static class ExpandPromptEngineering
{
    /// <summary>旧扩写工程 id → 反推同步后的 id（LEGACY_EXPAND_PE_ID_MAP）。</summary>
    private static readonly Dictionary<string, string> LegacyExpandPeIdMap = new(StringComparer.Ordinal)
    {
        ["pe_expand_natural"] = "pe_expand_descriptive",
        ["expand_natural"] = "pe_expand_descriptive",
        ["natural"] = "pe_expand_descriptive",
        ["pe_expand_compact"] = "pe_expand_sd",
        ["expand_compact"] = "pe_expand_sd",
        ["compact"] = "pe_expand_sd",
        ["pe_expand_cinematic"] = "pe_expand_sd",
        ["expand_cinematic"] = "pe_expand_sd",
        ["cinematic"] = "pe_expand_sd",
        ["pe_expand_photographer"] = "pe_expand_descriptive",
        ["expand_photographer"] = "pe_expand_descriptive",
        ["photographer"] = "pe_expand_descriptive",
        ["pe_expand_descriptive_en"] = "pe_expand_descriptive",
        ["expand_descriptive_en"] = "pe_expand_descriptive",
        ["descriptive_en"] = "pe_expand_descriptive",
        ["pe_expand_danbooru"] = "pe_expand_danbooru",
        ["expand_danbooru"] = "pe_expand_danbooru",
        ["danbooru"] = "pe_expand_danbooru",
        ["pe_expand_structured_md"] = "pe_expand_torii_min_structured_md",
        ["expand_structured_md"] = "pe_expand_torii_min_structured_md",
        ["pe_expand_structured_json"] = "pe_expand_torii_min_structured_json",
        ["expand_structured_json"] = "pe_expand_torii_min_structured_json",
    };

    public static string MapLegacyExpandPeId(string? peId)
    {
        var raw = (peId ?? "").Trim();
        if (raw.Length == 0) return "pe_expand_descriptive";
        if (LegacyExpandPeIdMap.TryGetValue(raw, out var mapped)) return mapped;
        if (raw.StartsWith("pe_", StringComparison.Ordinal)) return raw;
        var withPe = $"pe_{raw}";
        return LegacyExpandPeIdMap.TryGetValue(withPe, out var mapped2) ? mapped2 : withPe;
    }

    // ============================================================
    // 内置 profile 清单（供解析与 UI 共用）
    // ============================================================

    public enum ExpandProfileKind { Mirror, Torii, Minimax }

    public sealed record ExpandProfile(
        string Id, string Name, string Description, string OutputFormat,
        string? BuiltinKey, ExpandProfileKind Kind);

    private static readonly List<ExpandProfile> BuiltinProfilesList = new()
    {
        // ---- 3 反推镜像 ----
        new("pe_expand_descriptive", "自然语言 · 五点结构式",
            "单段连贯自然语言，按五点结构（构图、主体、环境、文字、风格）扩写，适用于 Flux、MJ 等自然语言提示词模型。",
            "prose", "Descriptive", ExpandProfileKind.Mirror),
        new("pe_expand_sd", "SD 标签 · Stable Diffusion 提示词格式",
            "一行逗号分隔 SD 正向标签，含完整还原检查表，适用于 SD1.5、SDXL、ComfyUI 等标签式模型。",
            "sd_tags", "Stable_Diffusion_Prompt", ExpandProfileKind.Mirror),
        new("pe_expand_danbooru", "Danbooru 标签 · 标准前缀式",
            "一行英文 Danbooru 风格 tag（短语内空格，如 long hair），严格 artist:/copyright:/character: 前缀顺序。",
            "danbooru_tags", "Danbooru_tag_list", ExpandProfileKind.Mirror),
        // ---- 10 torii 结构化镜像 ----
        new("pe_expand_torii_long_thoughts_v2", "结构化 MD · 四段详述",
            "4 段 Markdown：① 角色思考 ② Key details ③ Long description ④ 分角色详述（## 角色名）。",
            "structured_md", "long_thoughts_v2", ExpandProfileKind.Torii),
        new("pe_expand_torii_long_thoughts", "结构化 MD · 六段完整式",
            "6 段 Markdown：① 思考 ② General description ③ 分角色 ④ Individual Parts ⑤ Texts ⑥ Background and effects。",
            "structured_md", "long_thoughts", ExpandProfileKind.Torii),
        new("pe_expand_torii_min_structured_md", "结构化 MD · 极简三段",
            "短结构化 Markdown；扩写正文不含推理段，直接可用。",
            "structured_md", "min_structured_md", ExpandProfileKind.Torii),
        new("pe_expand_torii_min_structured_md_body", "结构化 MD · 仅正文三段",
            "同 min_structured_md，仅保留 §3 Structured description 正文。",
            "structured_md", "min_structured_md", ExpandProfileKind.Torii),
        new("pe_expand_torii_min_structured_json", "结构化 JSON · 极简键值",
            "按角色/General 等键的 JSON；输出为标准 JSON 文本（非 key: 展平）。",
            "structured_json", "min_structured_json", ExpandProfileKind.Torii),
        new("pe_expand_torii_json", "结构化 JSON · 标准字段",
            "character/background/atmosphere 等字段；输出为标准 JSON 文本。",
            "structured_json", "json", ExpandProfileKind.Torii),
        new("pe_expand_torii_long", "自然语言 · 多段长描述",
            "2–5 段自然语言长描述，无 Markdown 结构，支持角色名。",
            "prose", "long", ExpandProfileKind.Torii),
        new("pe_expand_torii_short", "自然语言 · 短描述",
            "简短扼要，覆盖主要对象与细节，无冗长修辞。",
            "prose", "short", ExpandProfileKind.Torii),
        new("pe_expand_torii_md_comic", "结构化 MD · 漫画分镜",
            "漫画/分镜专用 Markdown：格式说明、逐格描述与总结。",
            "structured_md", "md_comic", ExpandProfileKind.Torii),
        new("pe_expand_torii_json_comic", "结构化 JSON · 漫画分帧",
            "按帧与角色的 JSON 漫画描述；输出为标准 JSON 文本。",
            "structured_json", "json_comic", ExpandProfileKind.Torii),
        // ---- 10 MiniMax 场景 ----
        new("pe_expand_h3_full_reference", "MiniMax 六段式通用提示词",
            "通用：将素材/简述改写为 MiniMax-H3 Full-Reference 六段视频提示词（subject_definitions → non_diegetic_music）。",
            "minimax", "h3_full_reference", ExpandProfileKind.Minimax),
        new("pe_expand_minimax_continuous_story", "MiniMax 连续剧情视频",
            "多镜头连续剧情：场景与角色保持一致，节奏与镜头语言完整。",
            "minimax", "continuous_story", ExpandProfileKind.Minimax),
        new("pe_expand_minimax_product_ad", "MiniMax 电商广告视频",
            "商品/广告：产品展示与卖点视觉化，画面干净聚焦。",
            "minimax", "product_ad", ExpandProfileKind.Minimax),
        new("pe_expand_minimax_handdrawn", "MiniMax 手绘涂鸦风",
            "手绘涂鸦风格视频：发光线条、松弛感与 Live 实拍融合。",
            "minimax", "handdrawn", ExpandProfileKind.Minimax),
        new("pe_expand_minimax_coop_game", "MiniMax 双人游戏开场",
            "双人合作游戏开场动画：双角色登场与协作镜头。",
            "minimax", "coop_game", ExpandProfileKind.Minimax),
        new("pe_expand_minimax_paper_collage", "MiniMax 纸拼贴风",
            "纸拼贴/手工质感视频：触觉材质与叙事拼贴。",
            "minimax", "paper_collage", ExpandProfileKind.Minimax),
        new("pe_expand_minimax_brand_promo", "MiniMax 品牌宣传片",
            "品牌宣传：调性统一、产品卖点与品牌符号强化。",
            "minimax", "brand_promo", ExpandProfileKind.Minimax),
        new("pe_expand_minimax_mv", "MiniMax 音乐 MV",
            "音乐 MV：节奏卡点、情绪曲线与歌词画面化。",
            "minimax", "mv_subtitle", ExpandProfileKind.Minimax),
        new("pe_expand_minimax_papercraft", "MiniMax 纸艺定格",
            "纸艺定格动画：手工质感、逐帧叙事。",
            "minimax", "papercraft", ExpandProfileKind.Minimax),
        new("pe_expand_minimax_anim3d", "MiniMax 3D 动画短片",
            "3D 动画短片：角色表演、镜头调度与视觉风格统一。",
            "minimax", "anim3d", ExpandProfileKind.Minimax),
    };

    public static IReadOnlyList<ExpandProfile> BuiltinProfiles => BuiltinProfilesList;

    public static ExpandProfile? FindProfile(string? peId)
    {
        var id = MapLegacyExpandPeId(peId);
        return BuiltinProfilesList.FirstOrDefault(p => p.Id == id);
    }

    public static bool IsMinimaxProfile(string? peId)
        => FindProfile(peId) is { Kind: ExpandProfileKind.Minimax };

    // ============================================================
    // 解析主入口：resolveExpandPrompts（promptEngineeringResolver.js:94）
    // ============================================================

    public sealed record ExpandPromptResult(string System, string User, int MaxTokens, string RuleId);

    public sealed record ExpandResolveParams(
        string ShortText,
        string OutputLang,
        string ExpandLen,
        string? ExpandLenChars,
        string UserExtraPrompt,
        IReadOnlyList<string>? MediaPaths = null,
        IReadOnlyDictionary<string, string>? MinimaxForm = null);

    /// <summary>
    /// 解析扩写提示词。MiniMax 场景走 assemble.js 等价链路（resolveMinimaxScenarioExpand），
    /// 其余分支与 PromptMaster 逐字一致。
    /// </summary>
    public static ExpandPromptResult? ResolveExpand(string? peId, ExpandResolveParams p)
    {
        var profile = FindProfile(peId);
        if (profile == null) return null;

        if (profile.Kind == ExpandProfileKind.Minimax)
            return MiniMaxAssembler.ResolveMinimaxScenarioExpand(profile, p);

        if (profile.Kind == ExpandProfileKind.Torii)
            return ResolveToriiExpand(profile, p);

        // 镜像：Descriptive / Stable_Diffusion_Prompt / Danbooru_tag_list
        return ResolveMirrorExpand(profile, p);
    }

    /// <summary>镜像分支：resolveExpandFromReverseMirror（非结构化）。</summary>
    private static ExpandPromptResult ResolveMirrorExpand(ExpandProfile profile, ExpandResolveParams p)
    {
        var caption = new ReverseCaptionRequest
        {
            Type = profile.BuiltinKey ?? "Descriptive",
            CaptionLang = p.OutputLang,
            Len = p.ExpandLen,
            CaptionLenChars = TryParseChars(p.ExpandLen, p.ExpandLenChars, out var c) ? c : null,
            MediaTarget = "image",
        };
        var system = CaptionPromptBlocks.BuildSystemPrompt(caption, "image");
        system += CaptionPromptBlocks.BuildSystemAddons(caption, "image");
        system = AdaptReversePeSystemForExpand(system, p.OutputLang);
        system = ExpandRules.ApplyExpandLength(system, p.ExpandLen, p.OutputLang, p.ExpandLenChars);
        system = ApplyUserExtraPrompt(system, p.UserExtraPrompt, p.OutputLang);

        var user = ExpandRules.BuildUserPrompt(
            p.ShortText, p.ExpandLen, p.OutputLang, p.ExpandLenChars,
            profile.BuiltinKey ?? "Descriptive", p.UserExtraPrompt);

        return new ExpandPromptResult(
            system, user,
            ExpandRules.ResolveExpandMaxTokens(p.ExpandLen, p.ExpandLenChars),
            profile.Id);
    }

    /// <summary>torii 结构化分支：buildStructuredExpandSystem（expandReverseMirror.js:73）。</summary>
    private static ExpandPromptResult ResolveToriiExpand(ExpandProfile profile, ExpandResolveParams p)
    {
        var fmt = (profile.BuiltinKey ?? "").Trim();
        var formatBlock = ToriiPromptsData.PromptsB.TryGetValue(fmt, out var block) ? block : "";
        var useEn = ExpandRules.ResolveUseEnglishOutput(p.OutputLang, "");
        var system = useEn
            ? $"You expand brief user text into a structured image-generation prompt.\n\n# Required output structure\n{formatBlock}\n\nExpand ONLY from the user's short text. Output must follow the structure above."
            : $"将用户简短描述扩写为结构化 AI 绘图正向提示词。\n\n# 输出结构\n{formatBlock}\n\n仅根据用户短文本扩写，输出须符合上述结构。";
        system = ExpandRules.ApplyExpandLength(system, p.ExpandLen, p.OutputLang, p.ExpandLenChars);
        system = ApplyUserExtraPrompt(system, p.UserExtraPrompt, p.OutputLang);
        system = ExpandRules.ApplyOutputLanguage(system, p.OutputLang, $"expand_torii_{fmt}");

        var user = ExpandRules.BuildUserPrompt(
            p.ShortText, p.ExpandLen, p.OutputLang, p.ExpandLenChars,
            $"expand_torii_{fmt}", p.UserExtraPrompt);

        return new ExpandPromptResult(
            system, user,
            ExpandRules.ResolveExpandMaxTokens(p.ExpandLen, p.ExpandLenChars),
            profile.Id);
    }

    /// <summary>adaptReversePeSystemForExpand（expandReverseMirror.js:58-71）四组替换 + 前置说明。</summary>
    public static string AdaptReversePeSystemForExpand(string system, string? outputLang)
    {
        var useEn = (outputLang ?? "zh") == "en";
        var pre = useEn
            ? "Expand the user brief text into a ready-to-use image-generation prompt (text expansion—not image captioning).\n\n"
            : "将用户简短描述扩写为完整 AI 绘图正向提示词（文本扩写，非看图反推）。\n\n";
        var s = (system ?? "");
        s = s.Replace("反推", "扩写", StringComparison.Ordinal);
        s = s.Replace("标注专家", "扩写专家", StringComparison.Ordinal);
        s = s.Replace("当前图像", "扩写结果", StringComparison.Ordinal)
             .Replace("当前视频关键帧", "扩写结果", StringComparison.Ordinal)
             .Replace("当前视频画面", "扩写结果", StringComparison.Ordinal)
             .Replace("当前视频", "扩写结果", StringComparison.Ordinal);
        s = s.Replace("图像内容", "扩写内容", StringComparison.Ordinal)
             .Replace("视频画面", "扩写内容", StringComparison.Ordinal);
        return pre + s;
    }

    private static string ApplyUserExtraPrompt(string text, string userExtraPrompt, string? outputLang)
    {
        var extra = (userExtraPrompt ?? "").Trim();
        if (extra.Length == 0) return text;
        var useEn = (outputLang ?? "zh") == "en";
        var title = useEn ? "User additional requirements" : "用户附加要求";
        return $"{(text ?? "").Trim()}\n\n【{title}】{extra}";
    }

    private static bool TryParseChars(string? expandLen, string? expandLenChars, out int chars)
        => ExpandRules.TryParseCustomCharCount(expandLen, expandLenChars, out chars);
}
