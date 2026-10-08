// 迁移基准：app/electron/config/minimaxScenarios/catalog.js（逐字段 1:1）
// MiniMax 场景目录：每个场景 = formFields + hardConstraints + assembleHints + outputMode。
// outputMode: full_reference / plain_video_prompt / timeline_template / director_segments

namespace PromptCraft.Service.Inference.Minimax;

/// <summary>showIf 表达式（catalog.js matchShowIf：all/any/equals/notEquals/gte/lte）。</summary>
public sealed class MiniMaxShowIf
{
    public string? Key { get; init; }
    public new string? Equals { get; init; }
    public string? NotEquals { get; init; }
    public string? Gte { get; init; }
    public string? Lte { get; init; }
    public List<MiniMaxShowIf>? All { get; init; }
    public List<MiniMaxShowIf>? Any { get; init; }

    public bool Match(IReadOnlyDictionary<string, string> form)
    {
        if (All is { Count: > 0 }) return All.All(c => c.Match(form));
        if (Any is { Count: > 0 }) return Any.Any(c => c.Match(form));
        if (Key == null) return true;
        var cur = form.TryGetValue(Key, out var v) ? v : "";
        if (Equals != null) return string.Equals(cur, Equals, StringComparison.Ordinal);
        if (NotEquals != null) return !string.Equals(cur, NotEquals, StringComparison.Ordinal);
        if (Gte != null)
        {
            return double.TryParse(cur, out var n) && double.TryParse(Gte, out var t) && n >= t;
        }
        if (Lte != null)
        {
            return double.TryParse(cur, out var n) && double.TryParse(Lte, out var t) && n <= t;
        }
        return true;
    }
}

/// <summary>表单下拉选项（catalog.js options: {value,label}）。</summary>
public sealed record MiniMaxOption(string Value, string Label);

/// <summary>场景表单字段（catalog.js formFields）。</summary>
public sealed class MiniMaxFormField
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Type { get; init; } = "text"; // select | text | textarea
    public List<MiniMaxOption> Options { get; init; } = new();
    public string Default { get; init; } = "";
    public bool Required { get; init; }
    public string Placeholder { get; init; } = "";
    public int Rows { get; init; } = 0;
    public MiniMaxShowIf? ShowIf { get; init; }
}

/// <summary>MiniMax 场景（catalog.js MINIMAX_SCENARIOS 条目）。</summary>
public sealed class MiniMaxScenario
{
    public string Id { get; init; } = "";
    public string PeId { get; init; } = "";
    public string BuiltinKey { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public int Sort { get; init; }
    public string OutputMode { get; init; } = ""; // full_reference | plain_video_prompt | timeline_template | director_segments
    public bool MediaExpandLayout { get; init; }
    public string SkillSource { get; init; } = "";
    public List<MiniMaxFormField> FormFields { get; init; } = new();
    public List<string> HardConstraints { get; init; } = new();
    public string AssembleHints { get; init; } = "";
}

/// <summary>场景目录：10 个 MiniMax 场景（catalog.js MINIMAX_SCENARIOS 逐条迁移）。</summary>
public static class MiniMaxScenarios
{
    public const int ContinuousStoryMaxSegments = 8;

    private static readonly MiniMaxOption[] AspectCommon =
    {
        new("16:9", "16:9 横屏"),
        new("9:16", "9:16 竖屏"),
        new("1:1", "1:1 方形"),
        new("4:3", "4:3"),
        new("3:4", "3:4"),
    };

    private static MiniMaxShowIf CustomPlanShowIf(MiniMaxShowIf? extra = null)
    {
        var conds = new List<MiniMaxShowIf> { new() { Key = "plan_mode", Equals = "custom" } };
        if (extra != null) conds.Add(extra);
        return conds.Count == 1 ? conds[0] : new MiniMaxShowIf { All = conds };
    }

    private static List<MiniMaxFormField> BuildContinuousStoryBeatFields(int max)
    {
        var fields = new List<MiniMaxFormField>();
        for (var i = 1; i <= max; i += 1)
        {
            var field = new MiniMaxFormField
            {
                Key = $"segment_{i}_beat",
                Label = $"第 {i} 段在干什么",
                Type = "textarea",
                Rows = 2,
                Placeholder = i == 1
                    ? "例如：傍晚室内，短发女人坐在桌边看手机，望向窗外说「再等我五分钟」"
                    : "例如：无硬切接上段，特写她转头微笑说「好」",
                Required = true,
                ShowIf = i >= 2
                    ? CustomPlanShowIf(new MiniMaxShowIf { Key = "segment_count", Gte = i.ToString() })
                    : CustomPlanShowIf(),
            };
            fields.Add(field);
        }
        return fields;
    }

    public static IReadOnlyList<MiniMaxScenario> All { get; } = Build();

    private static List<MiniMaxFormField> BuildContinuousStoryFields()
    {
        var fields = new List<MiniMaxFormField>
        {
            new MiniMaxFormField
            {
                Key = "prompt_kind", Label = "生成方式", Type = "select",
                    Options =
                    {
                        new("t2v", "文生视频（无参考图，公共设定用文字锁角色）"),
                        new("r2v", "参考图生视频（公共设定锁 <Picture N>/<Subject N>）"),
                    },
                    Default = "t2v", Required = true,
                },
                new MiniMaxFormField
                {
                    Key = "plan_mode", Label = "分段方式", Type = "select",
                    Options =
                    {
                        new("ai", "AI智能分段（只填创作需求，由 AI 拆段）"),
                        new("custom", "自定义（自己写每段内容和时长）"),
                    },
                    Default = "ai", Required = true,
                },
                new MiniMaxFormField
                {
                    Key = "segment_count", Label = "生成几段", Type = "select",
                    Options = Enumerable.Range(2, ContinuousStoryMaxSegments - 1)
                        .Select(i => new MiniMaxOption(i.ToString(), $"{i} 段")).ToList(),
                    Default = "4", Required = true,
                },
                new MiniMaxFormField
                {
                    Key = "segment_seconds", Label = "每段时长", Type = "select",
                    Options =
                    {
                        new("5", "约 5 秒（导演台默认）"), new("10", "约 10 秒"), new("15", "约 15 秒"),
                    },
                    Default = "5", Required = true,
                    ShowIf = new MiniMaxShowIf { Key = "plan_mode", Equals = "custom" },
                },
                new MiniMaxFormField
                {
                    Key = "aspect_ratio", Label = "画幅比例", Type = "select",
                    Options = { new("16:9", "16:9 横屏"), new("9:16", "9:16 竖屏"), new("1:1", "1:1 方形"), new("4:3", "4:3"), new("3:4", "3:4") },
                    Default = "16:9",
                },
                new MiniMaxFormField
                {
                    Key = "visual_style", Label = "视觉风格锁定", Type = "text",
                    Placeholder = "例如：真人实拍，电影感；或赛博朋克夜景", Default = "",
                },
                new MiniMaxFormField
                {
                    Key = "expand_mode", Label = "改写模式", Type = "select",
                    Options =
                    {
                        new("expand", "允许补全运镜/声画细节（推荐）"),
                        new("strict", "严格按各段说明，少编造"),
                    },
                    Default = "expand",
                    ShowIf = new MiniMaxShowIf { Key = "plan_mode", Equals = "custom" },
                },
        };
        fields.AddRange(BuildContinuousStoryBeatFields(ContinuousStoryMaxSegments));
        return fields;
    }


    private static List<MiniMaxScenario> Build() => new()
    {
        new MiniMaxScenario
        {
            Id = "full_reference",
            PeId = "pe_expand_h3_full_reference",
            BuiltinKey = "h3_full_reference",
            Name = "Minimax六段式通用提示词",
            Description = "通用：将素材/简述改写为 MiniMax-H3 Full-Reference 六段视频提示词（subject_definitions → non_diegetic_music）。",
            Sort = 5,
            OutputMode = "full_reference",
            MediaExpandLayout = true,
            SkillSource = "h3-prompt-writing / Full-Reference template",
            FormFields =
            {
                new MiniMaxFormField
                {
                    Key = "duration_seconds", Label = "目标时长（秒）", Type = "select",
                    Options =
                    {
                        new("5", "5 秒"), new("10", "10 秒"), new("15", "15 秒"),
                        new("custom", "自定义（写在需求里）"),
                    },
                    Default = "10",
                },
                new MiniMaxFormField
                {
                    Key = "aspect_ratio", Label = "画幅比例", Type = "select",
                    Options = { new("16:9", "16:9 横屏"), new("9:16", "9:16 竖屏"), new("1:1", "1:1 方形"), new("4:3", "4:3"), new("3:4", "3:4") },
                    Default = "16:9",
                },
                new MiniMaxFormField
                {
                    Key = "expand_mode", Label = "改写模式", Type = "select",
                    Options =
                    {
                        new("strict", "严格改写（不编造情节）"),
                        new("expand", "允许扩写补全（帮我写提示词）"),
                    },
                    Default = "strict",
                },
            },
            HardConstraints =
            {
                "Output ONLY six sections in order. ZH titles: 主体定义/摘要/保留分析/详细描述/整体声景/非叙事配乐. EN titles: subject_definitions/summary/retention_analysis/detailed_description/overall_soundscape/non_diegetic_music.",
                "Section titles AND body language follow Output language (zh/en dual templates). Keep English reference tags/markers; preserve original language only inside <d> for dialogue/lyrics/on-screen text.",
                "Keep reference label identities stable: <Subject N>, <Picture N>, <Video N>, <Audio N>.",
                "Do not invent plot unless expand_mode is expand.",
            },
            AssembleHints = "Use the language-matched Full-Reference guide (zh/en) as the primary format.",
        },

        new MiniMaxScenario
        {
            Id = "continuous_story",
            PeId = "pe_expand_minimax_continuous_story",
            BuiltinKey = "minimax_continuous_story",
            Name = "连续剧情（导演台）",
            Description = "多段连续镜头：AI智能分段只需段数+创作需求；自定义则手写每段内容。输出公共「主体定义」+ 每段六段式（去掉重复角色设定）。",
            Sort = 8,
            OutputMode = "director_segments",
            MediaExpandLayout = true,
            SkillSource = "MiniMax H3 Director / 段间引导",
            FormFields = BuildContinuousStoryFields(),
            HardConstraints =
            {
                "Output TWO parts only, nothing else. No markdown fences, no chat, no extra commentary.",
                "Part 1 separator: ===== 公共设定 =====  This block is pasted into Director 公共参数. It contains ONLY 主体定义 / subject_definitions. Do NOT put summary or shots here.",
                "Part 2: EXACTLY N groups (segment_count). Separators: ===== 提示词组 k ===== (k from 1 to N).",
                "ZH titles (forced when Output language is zh): 公共设定 uses 主体定义:. Each 提示词组 uses MiniMax 六段式 but WITHOUT 主体定义: — only 摘要: / 保留分析: / 详细描述: / 整体声景: / 非叙事配乐: in that order. Never output subject_definitions:/summary: English headers when zh.",
                "EN titles (when Output language is en): public block uses subject_definitions:. Each group uses summary / retention_analysis / detailed_description / overall_soundscape / non_diegetic_music. Never repeat subject_definitions inside a group.",
                "Public 主体定义 locks identity/wardrobe once. Example style: <Subject 1> 来自 <Picture 1> 的……身份与穿着完全锁定参考图：……  If no media, describe subjects in prose without inventing <Picture N>.",
                "Each 提示词组 must NOT redefine appearance/wardrobe. Reference <Subject N> only. retention_analysis cites who appears in this group and fully_preserved / partially_preserved — no new character bible.",
                "Group 2+ 详细描述/detailed_description MUST start with ZH「无硬切。紧接上一段。」or EN \"No hard cut. Immediately following the previous section.\"",
                "Each 详细描述 ends on a stable handoff pose (段末停在…). Last group may complete the action. Last line of 详细描述: 不要乱说话",
                "Reuse the same cast, wardrobe, space, and music theme across groups unless the user explicitly changes them.",
                "Music/soundscape: same motif; groups 1..N-1 must not fade out. Final group may resolve lightly.",
                "Timestamps [Shot N] At MM:SS.mmm reset to 0 at the start of EACH group.",
                "Director 段间引导 / 引用上段 is a UI toggle, not a prompt field.",
                "plan_mode=ai: invent each segment beat and duration from 创作需求 only; never ask the user to fill per-segment fields. plan_mode=custom: follow 第 N 段在干什么 and 每段时长.",
            },
            AssembleHints = "Part 1 = Director 公共参数 (主体定义 only). Part 2 = N MiniMax Full-Reference groups minus 主体定义. AI mode splits 创作需求 into N beats; custom mode uses user beats.",
        },

        new MiniMaxScenario
        {
            Id = "product_ad",
            PeId = "pe_expand_minimax_product_ad",
            BuiltinKey = "minimax_product_ad",
            Name = "极简产品广告",
            Description = "电商/新品发布：Apple 风极简产品广告片提示词。需产品图；确认时长、画幅、风格模板与文案。",
            Sort = 10,
            OutputMode = "full_reference",
            MediaExpandLayout = true,
            SkillSource = "minimalist-product-ad-generator",
            FormFields =
            {
                new MiniMaxFormField
                {
                    Key = "duration_seconds", Label = "目标时长", Type = "select",
                    Options = { new("5", "5 秒"), new("10", "10 秒（推荐）"), new("15", "15 秒") },
                    Default = "10", Required = true,
                },
                new MiniMaxFormField
                {
                    Key = "aspect_ratio", Label = "画幅比例", Type = "select",
                    Options = AspectCommon.Append(new MiniMaxOption("match_reference", "匹配参考图")).ToList(),
                    Default = "16:9", Required = true,
                },
                new MiniMaxFormField
                {
                    Key = "apple_style", Label = "苹果风模板", Type = "select",
                    Options =
                    {
                        new("white-tech", "白色科技风"), new("dark-rim-light", "黑底轮廓光"),
                        new("brand-color-field", "品牌色块"), new("light-lifestyle", "生活方式轻场景"),
                    },
                    Default = "white-tech", Required = true,
                },
                new MiniMaxFormField
                {
                    Key = "variant_strategy", Label = "款式策略", Type = "select",
                    Options = { new("single", "单款"), new("multi", "多款/多色") },
                    Default = "single",
                },
                new MiniMaxFormField
                {
                    Key = "main_variant", Label = "主推款 / 主色名", Type = "text",
                    Placeholder = "如：银色单机 / purple（可不填，模型可从素材推断）", Default = "", Required = false,
                },
                new MiniMaxFormField
                {
                    Key = "narrative_spine", Label = "叙事脊柱", Type = "select",
                    Options =
                    {
                        new("Product Launch", "产品发布型（推荐）"),
                        new("Feature Touch", "功能触感型"),
                        new("Color Family", "色彩家族型"),
                    },
                    Default = "Product Launch",
                },
                new MiniMaxFormField
                {
                    Key = "copy_mode", Label = "画面文案", Type = "select",
                    Options =
                    {
                        new("agent_generate", "由模型生成 Apple 风文案（跟随输出语言）"),
                        new("user_provided", "使用我提供的文案"),
                    },
                    Default = "agent_generate",
                },
                new MiniMaxFormField
                {
                    Key = "user_copy", Label = "用户文案（可选）", Type = "text",
                    Placeholder = "跟随输出语言：中文约 4–12 字，或英文 3–5 词", Default = "",
                    ShowIf = new MiniMaxShowIf { Key = "copy_mode", Equals = "user_provided" },
                },
                new MiniMaxFormField
                {
                    Key = "product_selling_points", Label = "卖点 / 可展示动作", Type = "textarea",
                    Placeholder = "材质、开合、旋转、屏幕发光等具体可见动作", Default = "",
                },
            },
            HardConstraints =
            {
                "Apple-style means clean composition, premium light, restrained motion, negative space — NOT recoloring the product body.",
                "Preserve product body color/material from reference media; never recolor to generic silver/white.",
                "In-frame copy: follow Output language lock — English 3–5 words OR Chinese ~4–12 chars; single line only; never two rows or bottom subtitles.",
                "White-tech: first-half text black/dark gray; second-half = specific product color. Dark-rim-light may use white for first half.",
                "No grids, split screens, collage, storyboard boards, or product walls — especially not in the ending.",
                "Prefer one mid-film copy moment + one final copy moment for ~10s films.",
                "Use reference media as <Picture N> anchors; map hero / material detail / closing composition roles when multiple images exist.",
                "Default video model language: MiniMax-H3 premium product film camera language + native Apple-tech BGM direction (~100 BPM pluck/noise bed).",
            },
            AssembleHints = "Deliver a Full-Reference 6-section prompt for a minimalist product ad film. Include beat-aware detailed_description covering opening hero, material detail, mid copy, brake, and final copy hold.",
        },

        new MiniMaxScenario
        {
            Id = "handdrawn_live",
            PeId = "pe_expand_minimax_handdrawn",
            BuiltinKey = "minimax_handdrawn",
            Name = "手绘实拍融合",
            Description = "固定 15s / 16:9：手绘发光动画 × 实拍接触/变形/追拍。以文生视频为主（可不附图）；输出时间轴式 H3 视频提示词，非六段式。",
            Sort = 20,
            OutputMode = "plain_video_prompt",
            MediaExpandLayout = true,
            SkillSource = "handdrawn-live-video-generator",
            FormFields =
            {
                new MiniMaxFormField
                {
                    Key = "contact_object", Label = "接触对象 / 手部动作", Type = "text",
                    Placeholder = "0–3s 必须有清晰实拍接触", Default = "", Required = true,
                },
                new MiniMaxFormField
                {
                    Key = "mood", Label = "情绪氛围", Type = "select",
                    Options =
                    {
                        new("生活感", "生活感"), new("可爱", "可爱"), new("怀旧", "怀旧"), new("温柔", "温柔"),
                        new("略带切感", "略带切感"), new("custom", "自定义（写在需求里）"),
                    },
                    Default = "生活感",
                },
                new MiniMaxFormField
                {
                    Key = "prompt_language", Label = "Prompt 语言", Type = "select",
                    Options = { new("follow_user", "跟随用户输入"), new("zh", "中文"), new("en", "英文"), new("ja", "日文") },
                    Default = "follow_user",
                },
                new MiniMaxFormField { Key = "initial_drawn_form", Label = "手绘初始形态（可选）", Type = "text", Default = "" },
                new MiniMaxFormField { Key = "style_limits", Label = "风格限制（可选）", Type = "textarea", Default = "" },
            },
            HardConstraints =
            {
                "FIXED: 15 seconds, 16:9. Do not change duration/aspect.",
                "Text-to-video first: media is optional. Without attached media, NEVER invent <Picture N>/<Subject N>.",
                "Required section order: opening line → live-action space & phone texture → 0-3 → 3-6 → 6-10 → 10-13 → 13-15 → hand-drawn texture → camera chase → prohibitions → ambience.",
                "FORBIDDEN formats: Full-Reference six sections (主体定义/摘要/保留分析…), subject_definitions, retention_analysis.",
                "0–3s must include clear live-action contact; same entity continuously morphs — no sudden new characters.",
                "Same space or adjacent continuous space; no hard location jumps.",
                "Hand-drawn feel: crayon/chalk/colored pencil/pastel/rough brush. Forbid 3DCG, plush, clean vector, smooth neon.",
                "Forbid horror tropes: giant eyes, ripping mouths, teeth threats, jump scares.",
                "Camera lags half a beat; do not keep subject perfectly centered.",
                "13–15s must include space-level transform + tender aftertaste + cute gag.",
                "Invent fresh content every time; do not recycle stock gags.",
            },
            AssembleHints = "Output ONE ready-to-run plain H3 T2V prompt. Chinese opening:「15秒，16:9横版视频。将实拍的〇〇与手绘发光动画融合的影像。」Then follow the required time-section order. No Full-Reference blocks.",
        },

        new MiniMaxScenario
        {
            Id = "coop_game",
            PeId = "pe_expand_minimax_coop_game",
            BuiltinKey = "minimax_coop_game",
            Name = "双人游戏开场",
            Description = "固定约 15s / 16:9 主机菜单框架：双玩家名、游戏名、风格与角色锚点 → 完整时间轴 H3 提示词。",
            Sort = 30,
            OutputMode = "timeline_template",
            MediaExpandLayout = true,
            SkillSource = "co-op-game-intro-generator",
            FormFields =
            {
                new MiniMaxFormField
                {
                    Key = "visual_style", Label = "视觉风格", Type = "select",
                    Options =
                    {
                        new("赛博朋克", "赛博朋克"), new("像素风", "像素风"), new("水彩手绘", "水彩手绘"), new("粘土风", "粘土风"),
                        new("日系动漫", "日系动漫"), new("暗黑奇幻", "暗黑奇幻"), new("蒸汽朋克", "蒸汽朋克"), new("custom", "自定义"),
                    },
                    Default = "赛博朋克", Required = true,
                },
                new MiniMaxFormField
                {
                    Key = "visual_style_custom", Label = "自定义风格描述", Type = "text", Default = "",
                    ShowIf = new MiniMaxShowIf { Key = "visual_style", Equals = "custom" },
                },
                new MiniMaxFormField { Key = "game_title", Label = "游戏名称", Type = "text", Default = "", Required = true },
                new MiniMaxFormField { Key = "player1_name", Label = "PLAYER 1 名字", Type = "text", Default = "", Required = true },
                new MiniMaxFormField { Key = "player2_name", Label = "PLAYER 2 名字", Type = "text", Default = "", Required = true },
                new MiniMaxFormField
                {
                    Key = "height_contrast", Label = "体型对比备注", Type = "text",
                    Placeholder = "默认：P1 高挑轻量爪 / P2 矮壮重拳", Default = "",
                },
            },
            HardConstraints =
            {
                "FIXED UI framework 16:9 console menu: centered duo, top-left player cards, right vertical menu, bottom caution/decor strip, CONTINUE as visual center.",
                "Style changes appearance only — never layout/information hierarchy.",
                "Character refs are identity anchors only; redraw faces into selected style; do not inherit photo realism.",
                "P1 left/taller lightweight claw; P2 right/shorter heavy fist. Never swap names/sides or merge bodies.",
                "Menu titles single-line ALL CAPS. Palette ≤5 colors; red only for danger/exit.",
                "Forbid third player, split-screen, hard cuts, gore, weapons, official game logos, brand UI clones, watermarks.",
                "Fixed timeline beats: 0–2s menu, 2–4s P1 arm equip, 4–7s P2 heavy arm, 7–8.5s CONFIRM CONFIG, 8.5–10s LOADING world transform, 10–15s third-person enter world.",
                "UI copy tokens: START NEW GAME / CONTINUE / SETTINGS / EXIT GAME / READY / CONFIRM CONFIG / LOADING.",
            },
            AssembleHints = "Output one complete H3 timeline video prompt (~15s) filled with user names/title/style. Prefer plain timeline prompt over Full-Reference unless asked.",
        },

        new MiniMaxScenario
        {
            Id = "paper_collage",
            PeId = "pe_expand_minimax_paper_collage",
            BuiltinKey = "minimax_paper_collage",
            Name = "纸拼贴讲解",
            Description = "口播句/知识点 → 半调纸拼贴停格动画提示词（默认约 4s/段），强调组装运动与色场。",
            Sort = 40,
            OutputMode = "plain_video_prompt",
            MediaExpandLayout = true,
            SkillSource = "paper-collage-explainer-generator",
            FormFields =
            {
                new MiniMaxFormField
                {
                    Key = "aspect_ratio", Label = "画幅", Type = "select",
                    Options = { new("16:9", "16:9 横屏"), new("9:16", "9:16 竖屏"), new("1:1", "1:1 方形"), new("4:3", "4:3"), new("3:4", "3:4") },
                    Default = "16:9",
                },
                new MiniMaxFormField
                {
                    Key = "clip_duration", Label = "单段时长", Type = "select",
                    Options = { new("4", "4 秒（默认）"), new("5", "5 秒"), new("6", "6 秒") },
                    Default = "4",
                },
                new MiniMaxFormField
                {
                    Key = "palette_tone", Label = "色调偏好", Type = "select",
                    Options =
                    {
                        new("burnt_orange", "焦橙/红（紧迫）"), new("mustard", "芥末黄（警示）"), new("dark_green", "墨绿（认知/平静）"),
                        new("deep_purple", "深紫（记忆/神秘）"), new("teal", "青绿（协作）"), new("magenta", "玫红（荒诞/戏剧）"),
                        new("custom", "自定义"),
                    },
                    Default = "dark_green",
                },
                new MiniMaxFormField
                {
                    Key = "audio_plan", Label = "音频方案", Type = "select",
                    Options =
                    {
                        new("sfx_only", "仅拼贴音效（默认）"), new("sfx_plus_bgm", "音效+BGM"), new("sfx_plus_vo", "音效+旁白"),
                        new("sfx_bgm_vo_subs", "音效+BGM+旁白+字幕"), new("silent", "静音"),
                    },
                    Default = "sfx_only",
                },
                new MiniMaxFormField
                {
                    Key = "emotion", Label = "情绪", Type = "select",
                    Options = new[] { "平静", "紧迫", "讽刺", "惊奇", "荒诞", "澄清", "反思", "神秘", "轻快" }
                        .Select(x => new MiniMaxOption(x, x)).ToList(),
                    Default = "澄清",
                },
                new MiniMaxFormField { Key = "visual_metaphor", Label = "视觉隐喻（一句话）", Type = "textarea", Default = "" },
            },
            HardConstraints =
            {
                "Style signature: flat bold color field + B&W halftone cut-outs + selective cardstock + cream keylines + soft paper shadows + stop-motion assembly.",
                "Motion must be stop-motion assembly: slide-in / pop-in / settle / press-flat / pause / lock. Forbid smooth digital pan-zoom, global fades, chaotic scatter.",
                "Default keep collage SFX; do not add BGM/VO/subs unless audio_plan requires.",
                "Forbid readable letters/numbers/UI/subtitles/watermarks/logos unless user explicitly asks.",
                "Opening color field must match approved tone; avoid unapproved kraft/brown paper default.",
                "Paper controllable: not too flat, not overly dirty/wrinkled/brown.",
                "Motion order: clean color field → base structure → main metaphor → secondary objects → lock final composition → brief hold.",
            },
            AssembleHints = "Output a ready H3 stop-motion collage explainer prompt (single clip or multi-beat plan). Not Full-Reference unless asked.",
        },

        new MiniMaxScenario
        {
            Id = "brand_promo",
            PeId = "pe_expand_minimax_brand_promo",
            BuiltinKey = "minimax_brand_promo",
            Name = "品牌宣传短片",
            Description = "基于可核验品牌素材与推广目标，输出品牌事实约束下的宣传短片提示词（强调合规，不虚构主张）。",
            Sort = 50,
            OutputMode = "full_reference",
            MediaExpandLayout = true,
            SkillSource = "brand-promo-video-generator",
            FormFields =
            {
                new MiniMaxFormField
                {
                    Key = "duration_seconds", Label = "目标时长", Type = "select",
                    Options = { new("15", "15 秒（推荐）"), new("20", "20 秒"), new("30", "30 秒") },
                    Default = "15",
                },
                new MiniMaxFormField
                {
                    Key = "aspect_ratio", Label = "画幅比例", Type = "select",
                    Options = { new("16:9", "16:9 横屏"), new("9:16", "9:16 竖屏"), new("1:1", "1:1 方形"), new("4:3", "4:3"), new("3:4", "3:4") },
                    Default = "16:9",
                },
                new MiniMaxFormField { Key = "audience", Label = "目标受众", Type = "text", Default = "", Required = true },
                new MiniMaxFormField { Key = "campaign_focus", Label = "推广重点", Type = "textarea", Default = "", Required = true },
                new MiniMaxFormField
                {
                    Key = "product_type", Label = "叙事脊柱类型", Type = "select",
                    Options =
                    {
                        new("physical", "实体产品：英雄→交互→功能→场景→结果→LOGO"),
                        new("ai_saas", "AI/SaaS：意图→规划→能力→执行→输出→证明→LOGO"),
                        new("service", "服务/公司：背景→流程→证据→成果→承诺→LOGO"),
                        new("image_led", "影像主导：真实影像→母题→利益→情绪→LOGO"),
                    },
                    Default = "physical",
                },
                new MiniMaxFormField
                {
                    Key = "channel", Label = "投放渠道", Type = "select",
                    Options = new[] { "官网", "社交媒体横版", "短视频竖版", "投流", "线下活动", "其他" }
                        .Select(x => new MiniMaxOption(x, x)).ToList(),
                    Default = "短视频竖版",
                },
                new MiniMaxFormField { Key = "cta", Label = "行动号召 CTA", Type = "text", Placeholder = "如：立即体验 / Learn more", Default = "" },
                new MiniMaxFormField
                {
                    Key = "key_claims", Label = "已核验主张（勿编造）", Type = "textarea",
                    Placeholder = "只写有来源的功能点/数据；没有就留空", Default = "",
                },
                new MiniMaxFormField
                {
                    Key = "vo_language", Label = "旁白/文案语言", Type = "select",
                    Options =
                    {
                        new("auto", "跟随品牌/素材"), new("zh", "中文"), new("en", "英文"), new("none", "无旁白"),
                    },
                    Default = "auto",
                },
            },
            HardConstraints =
            {
                "Never invent product claims, metrics, or official statements not provided by the user.",
                "Never forge/redraw/approximate unauthorized logos, wordmarks, UI, packaging, mascots, or real people.",
                "If identity assets cannot be verified from provided media, mark as concept and avoid fake official marks.",
                "Prefer product-specific narrative spine from product_type; end with clear CTA when provided.",
                "Subtitles only if user explicitly asks.",
                "Prefer native H3 audio direction; avoid duplicate VO+native tracks unless requested.",
            },
            AssembleHints = "Deliver Full-Reference 6-section brand promo prompt grounded ONLY in provided assets and verified claims.",
        },

        new MiniMaxScenario
        {
            Id = "mv_subtitle",
            PeId = "pe_expand_minimax_mv",
            BuiltinKey = "minimax_mv",
            Name = "音乐MV动态字幕",
            Description = "音乐/歌词驱动的 MV 分镜与空间贴字方案；字幕是视觉设计层而非普通底栏字幕。",
            Sort = 60,
            OutputMode = "plain_video_prompt",
            MediaExpandLayout = true,
            SkillSource = "mv-subtitle-skill-confirmed",
            FormFields =
            {
                new MiniMaxFormField
                {
                    Key = "aspect_format", Label = "视频格式", Type = "select",
                    Options =
                    {
                        new("9:16_1080x1920", "竖屏 TikTok/Reels/Shorts"), new("16:9_1920x1080", "横屏 YouTube/B站"),
                        new("1:1_1080x1080", "方图信息流"), new("21:9_2560x1080", "宽银幕电影感"),
                    },
                    Default = "9:16_1080x1920",
                },
                new MiniMaxFormField
                {
                    Key = "target_duration", Label = "目标时长", Type = "select",
                    Options = { new("10s", "10 秒测试版"), new("15s", "15 秒 hook 版"), new("30s_plus", "30 秒及以上（多分镜）") },
                    Default = "15s",
                },
                new MiniMaxFormField
                {
                    Key = "lyrics_mode", Label = "歌词来源", Type = "select",
                    Options = { new("user_provided", "用户提供（锁定不可改）"), new("generate_original", "无歌词则生成原创并锁定") },
                    Default = "user_provided",
                },
                new MiniMaxFormField
                {
                    Key = "lyrics_text", Label = "歌词正文", Type = "textarea", Default = "",
                    ShowIf = new MiniMaxShowIf { Key = "lyrics_mode", Equals = "user_provided" },
                },
                new MiniMaxFormField
                {
                    Key = "visual_preset", Label = "视觉预设", Type = "select",
                    Options =
                    {
                        new("Trap", "Trap"), new("Dark-pop", "Dark-pop"), new("Cyber-grunge", "Cyber-grunge"),
                        new("Gospel hip-hop", "Gospel hip-hop"), new("custom", "自定义"),
                    },
                    Default = "Dark-pop",
                },
                new MiniMaxFormField { Key = "mood_temperature", Label = "情绪温度", Type = "text", Placeholder = "如：冷感疏离 / 热血爆发", Default = "" },
                new MiniMaxFormField { Key = "exclusions", Label = "明确排除项", Type = "textarea", Default = "" },
            },
            HardConstraints =
            {
                "On-screen text is a spatial design layer, NOT a bottom subtitle bar.",
                "When vocals exist, match locked lyrics word-by-word; never rewrite/translate user-provided lyrics.",
                "Text must not cover eyes/main expressions; avoid covering mouths during lip-sync; one main text event per shot.",
                "If character/scene/type refs exist, isolate roles — do not cross-contaminate.",
                "For >15s: multi-shot + drum-hit hard cuts; forbid fade/dissolve; avoid cutting mid-lyric unless extreme lip-sync CU.",
                "Uploaded real song defaults as master music bed.",
                "Do not copy copyrighted IP visuals.",
                "Shot script should include Global Aesthetic & Character Lock + per-shot Vocal/Typography/Visual/Camera/Transition.",
            },
            AssembleHints = "Output Complete MV Prompt package: global lock + shot list + typography plan. Plain structured prompt, not necessarily Full-Reference six sections.",
        },

        new MiniMaxScenario
        {
            Id = "papercraft",
            PeId = "pe_expand_minimax_papercraft",
            BuiltinKey = "minimax_papercraft",
            Name = "纸艺定格科普",
            Description = "知识主题 → 纸偶/分层布景/分镜向纸艺定格科普提示词包（可指定单图/5s/分镜等轻量交付）。",
            Sort = 70,
            OutputMode = "plain_video_prompt",
            MediaExpandLayout = true,
            SkillSource = "papercraft-stop-motion-explainer",
            FormFields =
            {
                new MiniMaxFormField
                {
                    Key = "audience", Label = "目标观众", Type = "select",
                    Options = new[] { "儿童", "泛知识用户", "课堂学生", "社媒用户", "品牌教育", "专业观众" }
                        .Select(x => new MiniMaxOption(x, x)).ToList(),
                    Default = "泛知识用户",
                },
                new MiniMaxFormField
                {
                    Key = "duration", Label = "目标时长", Type = "select",
                    Options = { new("5", "5 秒"), new("10", "10 秒"), new("15", "15 秒（推荐）") },
                    Default = "15",
                },
                new MiniMaxFormField
                {
                    Key = "aspect_ratio", Label = "画幅", Type = "select",
                    Options = { new("16:9", "16:9 横屏"), new("9:16", "9:16 竖屏"), new("1:1", "1:1 方形"), new("4:3", "4:3"), new("3:4", "3:4") },
                    Default = "16:9",
                },
                new MiniMaxFormField
                {
                    Key = "deliverable_type", Label = "交付类型", Type = "select",
                    Options =
                    {
                        new("full_package", "完整制作包摘要+主提示词"), new("5s_i2v_prompt", "5 秒图生视频提示词"),
                        new("storyboard", "分镜提示词"), new("single_image_prompt", "单图提示词"),
                    },
                    Default = "5s_i2v_prompt",
                },
                new MiniMaxFormField
                {
                    Key = "creative_direction", Label = "创意方向", Type = "select",
                    Options =
                    {
                        new("立体书旅程", "立体书旅程"), new("纸艺科学家实验室", "纸艺科学家实验室"), new("分层剖面模型", "分层剖面模型"),
                        new("微缩自然剧场", "微缩自然剧场"), new("纸片机关板", "纸片机关板"),
                    },
                    Default = "分层剖面模型",
                },
                new MiniMaxFormField { Key = "learning_goal", Label = "核心学习目标（一句话）", Type = "text", Default = "" },
                new MiniMaxFormField
                {
                    Key = "need_host", Label = "纸偶主持人", Type = "select",
                    Options = { new("yes", "需要"), new("no", "不需要") },
                    Default = "no",
                },
            },
            HardConstraints =
            {
                "Emphasize real paper layers: foreground/midground/background/distant with inter-layer shadows.",
                "Everything looks like real paper materials: thickness, fibers, creases, cut edges, brads/pull-tabs/slides.",
                "Motion = stop-motion steps/pauses/rebounds/hinges/page-turns. Forbid silky CG, plastic 3D, flat vector, large character locomotion.",
                "Transitions obey paper physics: page-turn / pop-up / pull-tab / paper door. Avoid neon glitch/digital shatter unless requested.",
                "Educational labels also made of paper pieces; one knowledge beat per shot.",
                "Preserve user domain terms; do not change the topic.",
                "Include reverse-prompt style protections against photoreal humans / plastic CG / paperless cartoon.",
                "Knowledge path: hook → explain → example/cultural link → memorable line.",
            },
            AssembleHints = "Output the selected deliverable_type as a production-ready papercraft prompt package (concise but complete).",
        },

        new MiniMaxScenario
        {
            Id = "anim_3d",
            PeId = "pe_expand_minimax_anim3d",
            BuiltinKey = "minimax_anim3d",
            Name = "3D动画短片",
            Description = "一句话故事 → 皮克斯感 3D 短片的项目简报级提示词/镜头规划（轻量版：聚焦可投喂分镜与主镜头提示）。",
            Sort = 80,
            OutputMode = "plain_video_prompt",
            MediaExpandLayout = true,
            SkillSource = "3d-animation-short-generator",
            FormFields =
            {
                new MiniMaxFormField
                {
                    Key = "aspect_ratio", Label = "画幅", Type = "select",
                    Options = { new("16:9", "16:9 横屏（推荐）"), new("9:16", "9:16 竖屏"), new("1:1", "1:1"), new("4:5", "4:5") },
                    Default = "16:9",
                },
                new MiniMaxFormField
                {
                    Key = "total_duration", Label = "总时长", Type = "select",
                    Options = { new("5", "5 秒"), new("10", "10 秒"), new("15", "15 秒（推荐）") },
                    Default = "15",
                },
                new MiniMaxFormField
                {
                    Key = "dialogue_mode", Label = "台词需求", Type = "select",
                    Options = { new("none", "无台词"), new("dialogue", "有对白"), new("narration", "旁白") },
                    Default = "none",
                },
                new MiniMaxFormField
                {
                    Key = "dialogue_language", Label = "台词语言", Type = "text",
                    Placeholder = "仅在需要台词时填写；勿默认英文", Default = "",
                    ShowIf = new MiniMaxShowIf { Key = "dialogue_mode", NotEquals = "none" },
                },
                new MiniMaxFormField
                {
                    Key = "q_version", Label = "Q 版比例", Type = "select",
                    Options = { new("auto", "自动"), new("yes", "Q 版 2.5–3 头身"), new("no", "偏写实卡通比例") },
                    Default = "auto",
                },
                new MiniMaxFormField
                {
                    Key = "emotional_premise", Label = "情绪前提", Type = "textarea",
                    Placeholder = "角色想要什么、真正需要什么、缺陷是什么", Default = "",
                },
            },
            HardConstraints =
            {
                "Global style lock: Pixar-like stylized 3D / C4D+Octane feel; warm cinematic lighting.",
                "Forbid photoreal live-action, flat anime 2D, plastic toy skin, stiff anatomy.",
                "Prefer SSS-like skin/materials, expressive Disney-style acting, clear silhouette.",
                "Single shot ≤15s conceptually; important characters per shot ≤3.",
                "When reference images are attached: 主体定义 MUST define each <Picture N> and map <Subject N> to it; reuse tags in 保留分析/详细描述. Never omit angle-bracket tags.",
                "If producing shot list: include per-second action/camera/audio/continuity cues.",
                "Scene cards must not include characters/silhouettes/hands/faces when separating env boards.",
                "Strip storyboard tags from final renderable prompts.",
                "Default model language: MiniMax-H3.",
            },
            AssembleHints = "Output a LIGHT H3-feedable package for the selected duration: brief + beat spine + shot prompts. Use Full-Reference six sections ONLY if user asks; when media attached and six-section is used, open with <Picture N>/<Subject N>. Without media, never invent those tags.",
        },
    };

    public static MiniMaxScenario? GetScenarioById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return All.FirstOrDefault(s => s.Id == id.Trim());
    }

    /// <summary>getScenarioByPeId：按 peId 或 builtinKey 匹配。</summary>
    public static MiniMaxScenario? GetScenarioByPeId(string? peId)
    {
        var id = (peId ?? "").Trim();
        if (id.Length == 0) return null;
        return All.FirstOrDefault(s => s.PeId == id || s.BuiltinKey == id);
    }

    public static IReadOnlyList<MiniMaxScenario> ListScenarios() =>
        All.OrderBy(s => s.Sort).ToList();
}
