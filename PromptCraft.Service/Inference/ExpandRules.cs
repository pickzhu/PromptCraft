namespace PromptCraft.Service.Inference;

/// <summary>
/// 提示词扩写规则（1:1 迁移自 app/electron/config/promptExpandRules.js）。
/// 8 条 EXPAND_RULES 原文 + 5 档篇幅 + 自定义字数 + 输出语言锁定/后缀 + 格式提示 + user 消息完整拼装。
/// 除 C# 语法外，所有提示词文案与原文逐字一致，禁止改写。
/// </summary>
public static class ExpandRules
{
    /// <summary>本地默认 expandMax（DEFAULT_LOCAL_TOKEN_LIMITS.expandMax）。</summary>
    public const int DefaultExpandMax = 8192;

    public sealed record ExpandRule(string Id, string Name, string Category, string Description, string System);

    /// <summary>EXPAND_RULES（promptExpandRules.js:12-129）8 条原文。</summary>
    public static readonly IReadOnlyList<ExpandRule> All = new List<ExpandRule>
    {
        new("expand_natural", "自然语言 · 通用扩写", "通用",
            "连贯自然语言扩写，禁止 Danbooru/SD tag 串，适合 Flux、MJ 等模型。",
            """
            你是一位 AI 绘图提示词扩写专家。根据用户给出的简短主题，扩写为完整、可执行的绘图提示词。

            绝对要求：
            1. 输出语言以用户在界面选择的「输出语言」为准（见 system 中的【输出语言锁定】），优先级高于输入语种；禁止因输入为英文或 tag 就改用英文输出。
            2. 输出必须是连贯的自然语言（完整句子或自然衔接的短句段落），不是标签列表。
            3. 严禁 Danbooru / SD 标签串：禁止输出 1girl、solo、masterpiece、best quality 等 tag 式英文关键词列表；禁止整段仅用英文逗号分隔的单词堆砌。
            4. 禁止使用 Markdown、编号、引号包裹全文；不要解释过程。
            5. 严格保留用户原始关键词，不得删改核心词。
            6. 禁止空泛形容词，转化为可感知的视觉细节。
            7. 根据主题自动判断领域（摄影/产品/平面/二次元/3D/插画等），补充该领域专业术语。

            输出：一段连贯的自然语言描述，细节丰富，适合作为文生图正向提示词。
            """),
        new("expand_compact", "SD 标签 · 简洁关键词", "Stable Diffusion",
            "单行中文逗号关键词，补充画质与光线，适合 SD/ComfyUI。",
            """
            你是 Stable Diffusion 提示词专家。将用户简短描述扩写为简洁中文提示词。

            要求：
            1. 单行或短句，中文逗号分隔关键词，不要分段落。
            2. 保留用户关键词，补充画质词（如：精细细节、柔和光线、高对比等，按需）。
            3. 不要思维链、不要解释、不要 Markdown。
            4. 严格遵守用户在界面指定的「输出语言」（见【输出语言锁定】），不得因输入语种改变输出语种。
            """),
        new("expand_cinematic", "SD 标签 · 电影镜头感", "摄影",
            "偏电影镜头语言：景别、构图、色彩分级、景深与情绪。",
            """
            你是电影感视觉提示词专家。将用户主题扩写为偏电影镜头语言的中文/英文绘图提示词。

            强调：镜头景别、构图、光线氛围、色彩分级、质感层次、景深与情绪。
            格式：逗号分隔关键词为主，可夹杂短句；禁止解释与 Markdown。
            保留用户核心词；输出语言遵守界面「输出语言」与【输出语言锁定】。
            """),
        new("expand_danbooru", "Danbooru 标签 · 英文 tag", "二次元",
            "输出英文逗号分隔 Danbooru tags，覆盖角色、动作、场景等维度。",
            """
            你是 Danbooru 风格标签提示词专家。将用户描述转为英文 tag 列表（若用户输入为中文，先理解语义再输出英文 tags）。

            要求：
            1. 输出为英文逗号分隔 tags，如：1girl, solo, white hair, outdoors, ...
            2. 不要句子、不要 Markdown、不要解释。
            3. 包含角色、动作、服装、场景、光照、画风等维度。
            4. 保留用户意图中的关键元素。
            """),
        new("expand_photographer", "自然语言 · 摄影细节式", "摄影",
            "3–5 句摄影向细节描写，强调材质、光线、焦段与真实质感。",
            """
            你是追求自然真实与极致细节的专业摄影提示词专家。将用户简单描述扩写为具有微观细节、丰富质感、自然光影的摄影类提示词。

            要求：
            1. 3-5 句细节丰富的描述，每句含具体材质/光线/焦段等信息（按需）。
            2. 禁止中英混杂乱译；禁止 Markdown。
            3. 保留用户关键词；输出语言遵守界面「输出语言」与【输出语言锁定】。
            4. 只输出提示词正文。
            """),
        new("expand_descriptive_en", "自然语言 · 英文描述", "通用",
            "English prose only; detailed visual description, no tag dumps.",
            """
            You are an expert at expanding short prompts into detailed English image generation prompts.

            Rules:
            1. Output in English only.
            2. Use natural descriptive prose (full sentences). Do NOT output Danbooru-style tags (e.g. 1girl, solo) or comma-separated keyword lists only.
            3. Preserve all user keywords; add concrete visual details.
            4. No markdown, no explanation, no chain-of-thought.
            """),
        new("expand_structured_md", "结构化 Markdown · 三段正文", "结构化",
            "扩写为 Markdown 结构化正文（General / 分角色 / Image effects），不含推理段，适合复杂场景。",
            """
            你是结构化 Markdown 提示词扩写专家。将用户简短描述扩写为可直接使用的 Markdown 结构化正向提示词。

            输出必须且仅能包含以下结构（无内容的小节可省略）：
            # 3. Structured description
            ## General
            整体构图、背景、非角色主体内容
            ## 主体或角色名
            每个主要角色或主体各一节：外观、服装、姿态、互动
            ## Image effects
            风格、镜头、光照；无明显特效可写 none

            要求：只输出上述 Markdown 正文；不要 #1 Thoughts / #2 Key details；不要 JSON；不要解释过程。
            """),
        new("expand_structured_json", "结构化 JSON · 极简键值", "结构化",
            "扩写为一个 JSON 对象（General、character 等键），值为短短语或 tag 片段，便于解析复用。",
            """
            你是结构化 JSON 提示词扩写专家。将用户简短描述扩写为一个 JSON 对象。

            结构示例：
            {"General":"short phrases about composition and scene",
            "character_1":"name, traits, clothing, pose",
            "background":"...",
            "image_effects":"..."}

            要求：只输出一个合法 JSON 对象；字符串值用短短语或 tag 片段，避免长叙事句；不要 Markdown；不要解释过程。
            """),
    };

    public static ExpandRule GetById(string? id)
    {
        var raw = (id ?? "").Trim();
        if (raw.StartsWith("pe_", StringComparison.Ordinal)) raw = raw[3..]; // pe_expand_natural → expand_natural
        if (raw.StartsWith("expand_", StringComparison.Ordinal)) raw = raw["expand_".Length..]; // expand_structured_md → structured_md
        return All.FirstOrDefault(r => r.Id == $"expand_{raw}") ?? All[0];
    }

    /// <summary>EXPAND_LENGTH_PRESETS（promptExpandRules.js:188-234）5 档原文。</summary>
    public sealed record LengthPreset(string Key, string Label, string LabelUi, int MaxTokens, string HintZh, string HintEn);

    public static readonly IReadOnlyList<LengthPreset> Lengths = new List<LengthPreset>
    {
        new("very_short", "极简", "极简（80 字以内）", 128,
            "打标/扩写结果控制在 80 字以内（或等价英文词数），只保留最关键信息，禁止冗长描述与重复堆砌。",
            "Keep output within 80 words or equivalent; essential details only, no verbosity."),
        new("short", "简短", "简短（约 80～150 字）", 256,
            "扩写结果控制在约 80～150 字（或等价英文词数），以关键词、短句为主，避免冗长段落与重复堆砌。",
            "Keep output concise: about 80–150 words or equivalent, keyword-focused; no long paragraphs."),
        new("medium", "标准", "标准（约 150～300 字）", 512,
            "扩写结果约 150～300 字，细节适中，兼顾画面信息量与可读性，不要明显偏短或偏长。",
            "Target about 150–300 words or equivalent detail level; balanced, not too brief or verbose."),
        new("long", "详细", "详细（约 300～500 字）", 768,
            "扩写结果约 300～500 字，补充丰富的视觉细节、材质、光线、构图与氛围，仍只输出提示词正文。",
            "Target about 300–500 words with rich visual details (materials, lighting, mood); prompt text only."),
        new("very_long", "超长", "超长（约 500～800 字）", 1024,
            "扩写结果约 500～800 字，尽可能充实画面层次与专业术语，禁止废话、解释与思维链。",
            "Target about 500–800 words, maximize visual depth; no filler, explanation, or chain-of-thought."),
    };

    public static LengthPreset GetLength(string? key)
    {
        var k = (key ?? "").Trim().ToLowerInvariant();
        if (k == "custom" || (k.Length > 0 && k.All(char.IsDigit))) return Lengths[2]; // 自定义字数走 spec
        return Lengths.FirstOrDefault(l => l.Key == k) ?? Lengths[2]; // medium default
    }

    // ============================================================
    // 输出语言：OUTPUT_LANG_SUFFIX / OUTPUT_LANG_LOCK（:135-157）
    // ============================================================

    private const string OutputLangSuffixZh = "\n\n【硬性要求】你必须仅用中文输出扩写结果，不要 Markdown、不要编号列表、不要解释过程。";
    private const string OutputLangSuffixEn = "\n\n【Hard requirement】You must output in English only. No markdown, no numbered lists, no explanation.";

    private const string OutputLangLockZh = """

        【输出语言锁定：中文】（最高优先级，覆盖前文一切关于「跟随输入语言」的说明）
        1. 用户已在界面指定「输出语言 = 中文」，你必须全程用中文（汉字）书写扩写正文。
        2. 即使用户输入为英文单词、Danbooru tag（如 1girl、solo、masterpiece）或中英混杂，也须理解语义后用中文自然语言扩写，不得整段输出英文 tag 列表或英文句子。
        3. 允许少量必要英文专有名词（如 Stable Diffusion、LoRA），但描写主体必须是中文。
        4. 禁止用「输入是英文所以用英文输出」为由切换语种。
        """;

    private const string OutputLangLockEn = """

        【Output language lock: English】（Highest priority; overrides any "match input language" rule above）
        1. User selected English output; write the entire expanded prompt in English.
        2. Even if input is Chinese or mixed, output must be English prose or phrases, not Chinese characters as main content.
        3. Do not switch to Chinese because the input is Chinese.
        4. For natural-language rules: no Danbooru tag dumps unless the active rule explicitly requires tags.
        """;

    /// <summary>resolveUseEnglishOutput（:159-168）。</summary>
    public static bool ResolveUseEnglishOutput(string? outputLang, string shortText)
    {
        var lang = (outputLang ?? "zh").ToLowerInvariant();
        if (lang == "en") return true;
        if (lang == "zh") return false;
        return !ContainsCjk(shortText);
    }

    private static bool ContainsCjk(string s)
    {
        foreach (var c in (s ?? ""))
            if (c >= '\u4e00' && c <= '\u9fff') return true;
        return false;
    }

    /// <summary>getOutputLangUserLock（:170-185）：写入 user 消息的锁定文案。</summary>
    private static string GetOutputLangUserLock(string? outputLang)
    {
        var lang = (outputLang ?? "").ToLowerInvariant();
        if (lang == "zh")
            return "【输出语言】已锁定为中文（最高优先级）。即使用户输入是英文单词或 tag（如 1girl、solo），" +
                   "也必须用中文自然语言扩写；禁止输出英文 tag 串、禁止整段英文逗号关键词列表。";
        if (lang == "en")
            return "【Output language】Locked to English (highest priority). Even if input is Chinese, " +
                   "output must be English only; no Chinese sentences as main content.";
        return "";
    }

    /// <summary>结构化规则专用语言锁定/后缀（applyOutputLanguage :338-358）。</summary>
    private static readonly HashSet<string> StructuredRuleIds = new(StringComparer.Ordinal)
    {
        "expand_structured_md",
        "expand_structured_json",
    };

    public static string ApplyOutputLanguage(string systemText, string? outputLang, string? ruleId)
    {
        var lang = (outputLang ?? "zh").ToLowerInvariant();
        if (lang != "zh" && lang != "en") return systemText;
        var rid = (ruleId ?? "").Trim();
        if (StructuredRuleIds.Contains(rid))
        {
            var lockText = lang == "zh"
                ? "\n\n【输出语言锁定：中文】Markdown 小节标题可按模板英文（如 ## General）；描述正文用简体中文。"
                : "\n\n【Output language lock: English】Keep JSON keys as specified; string values in English.";
            var suffix = lang == "zh"
                ? "\n\n只输出要求的 Markdown 或 JSON 结构正文，不要解释过程。"
                : "\n\nOutput only the required Markdown or JSON body; no explanation.";
            return (systemText ?? "").Trim() + lockText + suffix;
        }
        return (systemText ?? "").Trim()
               + (lang == "zh" ? OutputLangLockZh : OutputLangLockEn)
               + (lang == "zh" ? OutputLangSuffixZh : OutputLangSuffixEn);
    }

    // ============================================================
    // 篇幅：预设 + 自定义字数（:254-324）
    // ============================================================

    private static int CapTokens(int n, int min, int max) => Math.Min(max, Math.Max(min, n));

    /// <summary>parseCustomCharCount（:255-267）。</summary>
    public static bool TryParseCustomCharCount(string? expandLen, string? expandLenChars, out int chars)
    {
        chars = 0;
        if (!string.IsNullOrEmpty(expandLenChars))
        {
            if (int.TryParse((expandLenChars).Trim(), out var n) && n > 0) { chars = n; return true; }
        }
        var k = (expandLen ?? "").Trim();
        if (k.Length > 0 && k.All(char.IsDigit) && int.TryParse(k, out var d) && d > 0) { chars = d; return true; }
        return false;
    }

    public sealed record LengthSpec(bool Custom, int? Chars, int MaxTokens, string HintZh, string HintEn);

    /// <summary>resolveExpandLengthSpec（:285-312）。</summary>
    public static LengthSpec ResolveLengthSpec(string? expandLen, string? expandLenChars, string? outputLang)
    {
        const int expandMax = DefaultExpandMax;
        var chars = 0;
        if (TryParseCustomCharCount(expandLen, expandLenChars, out chars))
        {
            var useEn = ResolveUseEnglishOutput(outputLang, "");
            return new LengthSpec(
                Custom: true,
                Chars: chars,
                MaxTokens: CapTokens((int)Math.Ceiling(chars * 1.8), 128, expandMax),
                HintZh: $"扩写结果目标约 {chars} 字（按中文字符计）或等价英文篇幅，请尽量接近该长度，不要明显过短或远超。",
                HintEn: $"Target output length about {chars} characters/words; match this scale closely.");
        }
        var preset = GetLength(expandLen);
        return new LengthSpec(false, null, preset.MaxTokens, preset.HintZh, preset.HintEn);
    }

    public static int ResolveExpandMaxTokens(string? expandLen, string? expandLenChars)
        => ResolveLengthSpec(expandLen, expandLenChars, "auto").MaxTokens;

    /// <summary>applyExpandLength（:314-320）：篇幅写入 system。</summary>
    public static string ApplyExpandLength(string systemText, string? expandLen, string? outputLang, string? expandLenChars)
    {
        var spec = ResolveLengthSpec(expandLen, expandLenChars, outputLang);
        var useEn = ResolveUseEnglishOutput(outputLang, "");
        var hint = useEn ? spec.HintEn : spec.HintZh;
        var title = useEn ? "Length requirement" : "篇幅要求";
        return (systemText ?? "").Trim() + $"\n\n【{title}】{hint}";
    }

    // ============================================================
    // 附加要求 / system 拼装（:360-389）
    // ============================================================

    private static string ApplyUserExtraPrompt(string text, string userExtraPrompt, string? outputLang)
    {
        var extra = (userExtraPrompt ?? "").Trim();
        if (extra.Length == 0) return text;
        var useEn = ResolveUseEnglishOutput(outputLang, "");
        var title = useEn ? "User additional requirements" : "用户附加要求";
        return $"{(text ?? "").Trim()}\n\n【{title}】{extra}";
    }

    /// <summary>resolveExpandSystemMessage（:370-389）：规则原文（或自定义规则）→ 语言 → 篇幅 → 附加要求。</summary>
    public static string ResolveExpandSystemMessage(string? ruleId, string customRuleContent, string? outputLang,
        string? expandLen, string? expandLenChars, string userExtraPrompt)
    {
        var custom = (customRuleContent ?? "").Trim();
        string baseText;
        if (custom.Length > 0)
        {
            baseText = custom;
        }
        else
        {
            var rule = GetById(ruleId);
            baseText = rule.System;
        }
        baseText = ApplyOutputLanguage(baseText, outputLang, GetById(ruleId).Id);
        baseText = ApplyExpandLength(baseText, expandLen, outputLang, expandLenChars);
        return ApplyUserExtraPrompt(baseText, userExtraPrompt, outputLang);
    }

    // ============================================================
    // 格式提示 getExpandFormatHint（:392-438）
    // ============================================================

    public static string GetExpandFormatHint(string ruleId, string? outputLang)
    {
        var id = (ruleId ?? "expand_natural").Trim();
        var useEn = ResolveUseEnglishOutput(outputLang, "");

        if (id == "Danbooru_tag_list" || id == "expand_danbooru")
            return useEn
                ? "Output format: English comma-separated Danbooru-style tags (artist:/copyright:/… then general tags). No prose paragraphs."
                : "输出格式：英文 Danbooru 风格 tag 行（含 artist:/copyright:/ 等前缀），不要长段落。";
        if (id == "Stable_Diffusion_Prompt" || id == "expand_compact" || id == "expand_cinematic")
            return useEn
                ? "Output format: one line or comma-separated SD prompt tags/phrases; faithful visual detail."
                : "输出格式：一行或逗号分隔 SD 正向标签/短语，强调可视细节。";
        if (id == "Descriptive" || id == "expand_natural" || id == "expand_photographer" || id == "expand_descriptive_en")
            return useEn
                ? "Output format: natural language prose only (single cohesive description). No tag dumps or markdown headings."
                : "输出格式：单段自然语言描写，禁止 tag 串与 Markdown 小标题。";
        if (id == "min_structured_md" || id == "min_structured_md_body" || id == "expand_structured_md" ||
            id.StartsWith("expand_torii_min_structured_md", StringComparison.Ordinal))
            return useEn
                ? "Output format: Markdown only (# 3. Structured description with ## sections). No JSON, no explanation."
                : "输出格式：仅 Markdown 结构化正文（## General、分角色节等），不要 JSON、不要解释。";
        if (id == "min_structured_json" || id == "json" || id == "expand_structured_json" ||
            id.StartsWith("expand_torii_min_structured_json", StringComparison.Ordinal) ||
            id.StartsWith("expand_torii_json", StringComparison.Ordinal))
            return useEn
                ? "Output format: one valid JSON object only. No Markdown wrapper, no explanation."
                : "输出格式：仅一个 JSON 对象，不要 Markdown、不要解释。";
        return "";
    }

    // ============================================================
    // user 消息完整拼装 buildExpandUserPrompt（:440-471）
    // ============================================================

    /// <summary>
    /// buildExpandUserPrompt：intro → 语言锁 → 篇幅 → 格式提示 → 附加要求 → 用户输入。
    /// </summary>
    public static string BuildUserPrompt(string shortText, string? expandLen, string? outputLang,
        string? expandLenChars, string ruleId, string userExtraPrompt)
    {
        var t = (shortText ?? "").Trim();
        var extra = (userExtraPrompt ?? "").Trim();
        var spec = ResolveLengthSpec(expandLen, expandLenChars, outputLang);
        var useEn = ResolveUseEnglishOutput(outputLang, t);
        var lengthLine = useEn ? spec.HintEn : spec.HintZh;
        var formatHint = GetExpandFormatHint(ruleId, outputLang);
        var langLock = GetOutputLangUserLock(outputLang);
        var intro = useEn
            ? "Expand the following brief description into a ready-to-use image generation prompt. Output only the expanded prompt."
            : "请将以下简短描述扩写为完整、可直接用于 AI 绘图的正向提示词。只输出扩写正文，不要解释。";
        var sb = new System.Text.StringBuilder();
        sb.Append(intro).Append("\n\n");
        if (langLock.Length > 0) sb.Append(langLock).Append("\n\n");
        sb.Append($"【{(useEn ? "Length" : "篇幅")}】{lengthLine}\n\n");
        if (formatHint.Length > 0) sb.Append($"【{(useEn ? "Format" : "格式")}】{formatHint}\n\n");
        if (extra.Length > 0) sb.Append($"【{(useEn ? "User additional requirements" : "用户附加要求")}】{extra}\n\n");
        sb.Append($"【{(useEn ? "User input" : "用户输入")}】\n{t}");
        return sb.ToString();
    }

    // ============================================================
    // 兼容旧调用（ViewModel 等仍按旧签名使用）
    // ============================================================

    public static string BuildSystem(string? ruleId, string outputLang)
        => ResolveExpandSystemMessage(ruleId, "", outputLang, "medium", null, "");

    public static string BuildUser(string shortText, string? lengthKey, string outputLang, string? customPrompt = null)
        => BuildUserPrompt(shortText, lengthKey, outputLang, null, "expand_natural", customPrompt ?? "");
}
