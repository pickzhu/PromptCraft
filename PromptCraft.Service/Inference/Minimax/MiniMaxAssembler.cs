// 迁移基准：app/electron/config/minimaxScenarios/assemble.js（1:1）
// buildSystem / buildUser / normalizeForm / buildMediaTagLock / resolveMinimaxScenarioExpand

using System.Text.RegularExpressions;

namespace PromptCraft.Service.Inference.Minimax;

public static class MiniMaxAssembler
{
    /// <summary>DEFAULT_LOCAL_TOKEN_LIMITS.mediaExpandMin（localTokenLimits.js）。</summary>
    public const int MediaExpandMin = 4096;

    private static readonly Dictionary<string, string> CachedFullRefGuides = new();

    /// <summary>loadFullReferenceGuide：按输出语言加载 Full-Reference 指南（中/英两套模板，EmbeddedResource 读取）。</summary>
    public static string LoadFullReferenceGuide(string? lang)
    {
        var key = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";
        if (CachedFullRefGuides.TryGetValue(key, out var cached)) return cached;
        var fileName = key == "en" ? "h3-full-reference-rewrite.en.md" : "h3-full-reference-rewrite.zh.md";
        string text;
        try
        {
            // 指南文件随项目作为 EmbeddedResource 打包（PromptCraft.Service.csproj 声明 LogicalName=PromptCraft.Service.prompts.*）
            using var stream = typeof(MiniMaxAssembler).Assembly.GetManifestResourceStream($"PromptCraft.Service.prompts.{fileName}");
            if (stream == null)
            {
                text = "";
            }
            else
            {
                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
            }
        }
        catch
        {
            text = ""; // 与 readPromptMd 抛错语义不同：缺失时降级为空指南（调用方依赖 try/catch）
        }
        CachedFullRefGuides[key] = text.Trim();
        return CachedFullRefGuides[key];
    }

    private static List<string> FormatMediaList(IEnumerable<string>? mediaPaths) =>
        ExpandMedia.EnumerateTaggedMedia(mediaPaths)
            .Select(item => $"- {item.Tag} {item.Base}（路径：{item.Path}）")
            .ToList();

    /// <summary>isDirectorCustomPlan：plan_mode == custom。</summary>
    public static bool IsDirectorCustomPlan(IReadOnlyDictionary<string, string> form) =>
        string.Equals((form.TryGetValue("plan_mode", out var v) ? v : "") ?? "", "custom", StringComparison.Ordinal);

    /// <summary>normalizeForm：空值用字段 default 填充（catalog.js normalizeForm）。</summary>
    public static Dictionary<string, string> NormalizeForm(MiniMaxScenario scenario, IReadOnlyDictionary<string, string>? rawForm)
    {
        var form = new Dictionary<string, string>(rawForm ?? new Dictionary<string, string>());
        foreach (var f in scenario.FormFields)
        {
            var has = form.TryGetValue(f.Key, out var cur);
            if (!has || string.IsNullOrEmpty(cur))
            {
                if (!string.IsNullOrEmpty(f.Default)) form[f.Key] = f.Default;
            }
        }
        return form;
    }

    /// <summary>fieldVisible：showIf 表达式评估（catalog.js fieldVisible → matchShowIf）。</summary>
    public static bool FieldVisible(MiniMaxFormField field, IReadOnlyDictionary<string, string> form)
    {
        if (field.ShowIf == null) return true;
        return field.ShowIf.Match(form);
    }

    /// <summary>formToLines：可见且非空字段 → 「- label: display(label (value))」。</summary>
    public static List<string> FormToLines(MiniMaxScenario scenario, IReadOnlyDictionary<string, string> form)
    {
        var lines = new List<string>();
        foreach (var f in scenario.FormFields)
        {
            if (!FieldVisible(f, form)) continue;
            var v = form.TryGetValue(f.Key, out var val) ? val : "";
            if (string.IsNullOrWhiteSpace(v)) continue;
            var display = v;
            if (f.Options.Count > 0)
            {
                var opt = f.Options.FirstOrDefault(o => string.Equals(o.Value, v, StringComparison.Ordinal));
                if (opt != null && opt.Label.Length > 0) display = $"{opt.Label} ({opt.Value})";
            }
            lines.Add($"- {f.Label}: {display}");
        }
        return lines;
    }

    /// <summary>buildMediaTagLock：参考素材标记块（含无附件分支与 r2vLock 分支）。</summary>
    private static List<string> BuildMediaTagLock(IEnumerable<string>? mediaPaths, string lang, string outputMode, IReadOnlyDictionary<string, string> form)
    {
        var items = ExpandMedia.EnumerateTaggedMedia(mediaPaths);
        var lines = new List<string>();
        var r2vLock =
            outputMode == "full_reference" ||
            (outputMode == "director_segments" && string.Equals(
                (form.TryGetValue("prompt_kind", out var v) ? v : "") ?? "", "r2v", StringComparison.Ordinal));
        var isZh = lang == "zh";
        if (items.Count == 0)
        {
            if (outputMode == "director_segments")
            {
                if (isZh)
                {
                    lines.Add("## 参考素材（当前：无附件）");
                    lines.Add("用户未附带图片/视频/音频。公共「主体定义」可用 <Subject N> 文字锁身份，严禁编造 <Picture N>/<Video N>/<Audio N>。");
                }
                else
                {
                    lines.Add("## Reference media (none attached)");
                    lines.Add("No media attached. Public subject_definitions may use <Subject N> in prose. Do NOT invent <Picture N>, <Video N>, or <Audio N>.");
                }
                lines.Add("");
                return lines;
            }
            if (isZh)
            {
                lines.Add("## 参考素材（当前：无附件）");
                lines.Add("用户未附带任何图片/视频/音频。严禁编造或输出 <Picture N>、<Subject N>、<Video N>、<Audio N> 等参考标签。直接用自然语言描述主体即可。");
            }
            else
            {
                lines.Add("## Reference media (none attached)");
                lines.Add("No media attached. Do NOT invent <Picture N>, <Subject N>, <Video N>, or <Audio N> tags. Describe subjects in plain language only.");
            }
            lines.Add("");
            return lines;
        }

        var tags = items.Select(x => x.Tag).ToList();
        if (isZh)
        {
            lines.Add("## 参考素材标记（最高优先级，有素材时强制）");
            lines.Add($"用户已附带 {items.Count} 个参考素材：{string.Join("、", tags)}。输出中必须使用这些尖括号标签，禁止写成 Picture 1 / picture1 / 图1 等变体。");
            if (r2vLock)
            {
                lines.Add("在「主体定义」中必须逐条定义每个素材标签，并建立主体映射，例如：");
                lines.Add("<Picture 1> 是……（说明该图角色/用途）");
                lines.Add("<Subject 1> 是 <Picture 1> 中的……（外观特征）");
                lines.Add("在「保留分析」「详细描述」中引用同一套 <Picture N>/<Subject N>；禁止只在文字里描述角色却不出现标签。");
            }
            else
            {
                lines.Add("在正文中按需引用上述标签锚定外观；不要额外虚构未提供的 <Picture N>。");
            }
        }
        else
        {
            lines.Add("## Reference media tags (ABSOLUTE when media attached)");
            lines.Add($"User attached {items.Count} media asset(s): {string.Join(", ", tags)}. Use these exact angle-bracket tags; never write Picture 1 / picture1 without brackets.");
            if (r2vLock)
            {
                lines.Add("In subject_definitions, define every media tag and map subjects, e.g. <Picture 1> / <Subject 1>.");
                lines.Add("Reuse the same tags in retention_analysis and detailed_description.");
            }
            else
            {
                lines.Add("Reference these tags in the prompt body to ground appearance; do not invent extra <Picture N> not provided.");
            }
        }
        lines.Add("");
        return lines;
    }

    /// <summary>buildSystem（assemble.js buildSystem 全量）。</summary>
    public static string BuildSystem(MiniMaxScenario scenario, IReadOnlyDictionary<string, string> form, string? outputLang, IEnumerable<string>? mediaPaths)
    {
        var lang = string.Equals(outputLang, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";
        var isZh = lang == "zh";
        var mediaPathList = mediaPaths is null ? new List<string>() : mediaPaths.ToList();
        var parts = new List<string>();

        if (isZh)
        {
            parts.Add("你是 PromptMaster 的 MiniMax-H3 场景提示词工程师。");
            parts.Add($"场景：{scenario.Name}（{scenario.Id}）");
            parts.Add($"Skill 来源：{(scenario.SkillSource.Length > 0 ? scenario.SkillSource : scenario.Id)}");
        }
        else
        {
            parts.Add("You are PromptMaster's MiniMax-H3 scenario prompt engineer.");
            parts.Add($"Scenario: {scenario.Name} ({scenario.Id})");
            parts.Add($"Skill source: {(scenario.SkillSource.Length > 0 ? scenario.SkillSource : scenario.Id)}");
        }
        parts.Add("");
        parts.AddRange(BuildMediaTagLock(mediaPathList, lang, scenario.OutputMode, form));

        parts.Add(isZh ? "## 输出语言（最高优先级）" : "## Output language (highest priority)");
        if (scenario.OutputMode == "full_reference")
        {
            if (isZh)
            {
                parts.Add("用户选择【中文】。六段标题与正文都必须用简体中文。");
                parts.Add("六段中文标题（强制）：主体定义: / 摘要: / 保留分析: / 详细描述: / 整体声景: / 非叙事配乐: —— 禁止输出 subject_definitions:、summary: 等英文章节标题。");
                parts.Add("仅在有参考素材时才使用 <Picture N>/<Subject N>；关系标记 fully_preserved、镜头 [Shot N]、任务前缀 [reference generation] 保持英文。禁止整段英文叙述。");
                parts.Add("画面广告文案：约 4–12 字、单行、Apple 风（除非用户已提供英文文案）。");
            }
            else
            {
                parts.Add("User selected 【English】. Write all six section bodies, shot descriptions, sound, and on-screen advertising copy in English.");
                parts.Add("In-frame advertising copy: 3–5 English words, single line, Apple-style tone.");
            }
        }
        else if (scenario.OutputMode == "director_segments")
        {
            if (isZh)
            {
                parts.Add("用户选择【中文】。章节标题必须用中文六段式：主体定义 / 摘要 / 保留分析 / 详细描述 / 整体声景 / 非叙事配乐。");
                parts.Add("禁止输出 subject_definitions:、summary: 等英文章节标题。正文简体中文。");
                parts.Add("参考标签 <Subject N>/<Picture N>、关系标记 fully_preserved、镜头 [Shot N] 保持英文。");
            }
            else
            {
                parts.Add("User selected 【English】. Use English Full-Reference keys. Write all section bodies in English.");
            }
        }
        else if (isZh)
        {
            parts.Add("用户选择【中文】。正文必须用简体中文。禁止整段英文叙述。");
            parts.Add("本场景不是 Full-Reference 六段式：不要输出「主体定义/摘要/保留分析/详细描述/整体声景/非叙事配乐」章节标题。");
            if (mediaPathList.Count > 0)
            {
                parts.Add("用户已提供参考素材：正文必须用 <Picture N>/<Subject N> 锚定外观，并写完整分镜、动作、运镜与声景的**自然语言段落**（可用 [Shot N] 标记镜头）；禁止只列标签名、禁止 XML/空壳结构而无正文。");
            }
            else
            {
                parts.Add("无参考素材时不要编造 <Picture N>/<Subject N>。");
            }
        }
        else
        {
            parts.Add("User selected 【English】. Write the prompt body in English.");
            parts.Add("This scenario is NOT Full-Reference six-section format. Do not use subject_definitions:/summary: section headers.");
            if (mediaPathList.Count > 0)
            {
                parts.Add("Reference media attached: anchor with <Picture N>/<Subject N> and write full shot/action/camera/audio as **prose paragraphs** ([Shot N] allowed). No tag-only lists, no XML shells without body text.");
            }
            else
            {
                parts.Add("Do not invent <Picture N>/<Subject N> when no media is attached.");
            }
        }
        parts.Add("");

        if (scenario.OutputMode == "full_reference")
        {
            if (isZh)
            {
                parts.Add("## 输出格式（最高优先级）");
                parts.Add("仅输出 MiniMax-H3 Full-Reference 标准六段（中文标题）：主体定义 → 摘要 → 保留分析 → 详细描述 → 整体声景 → 非叙事配乐。");
                parts.Add("绝对纯净输出：以「主体定义:」开头，以「非叙事配乐:」内容结束。不要 Markdown 代码围栏，不要寒暄。");
                parts.Add("章节标题用中文；参考标签/关系标记/镜头标记保持英文；正文以中文指南为准。");
                if (string.Equals((form.TryGetValue("expand_mode", out var em) ? em : "") ?? "", "expand", StringComparison.Ordinal))
                {
                    parts.Add("用户允许扩写：可补充必要的视觉/听觉/时间细节，使提示词可直接生成。");
                }
                else
                {
                    parts.Add("默认严格改写：不要编造用户未给出或未强烈暗示的情节/动作/角色。");
                }
                parts.Add("");
                parts.Add("## Full-Reference 中文指南（请严格遵循）");
                parts.Add(LoadFullReferenceGuide("zh"));
            }
            else
            {
                parts.Add("## Output format (HIGHEST PRIORITY)");
                parts.Add("Produce MiniMax-H3 Full-Reference Mode standardized 6 sections ONLY: subject_definitions → summary → retention_analysis → detailed_description → overall_soundscape → non_diegetic_music.");
                parts.Add("Absolute pure output: start with subject_definitions: end with non_diegetic_music:. No markdown fences, no greetings.");
                parts.Add("Keep English keys/labels/tags as in the guide. Section bodies in English.");
                if (string.Equals((form.TryGetValue("expand_mode", out var em) ? em : "") ?? "", "expand", StringComparison.Ordinal))
                {
                    parts.Add("User allowed expansion: you may add necessary visual/audio/temporal details to make a playable prompt.");
                }
                else
                {
                    parts.Add("Strict rewrite by default: do NOT invent plot/actions/characters not stated or strongly implied.");
                }
                parts.Add("");
                parts.Add("## Full-Reference Guide (English edition — follow strictly)");
                parts.Add(LoadFullReferenceGuide("en"));
            }
        }
        else if (scenario.OutputMode == "director_segments")
        {
            var n = Math.Max(2, int.TryParse((form.TryGetValue("segment_count", out var sc) ? sc : "") ?? "", out var sn) && sn > 0 ? sn : 4);
            var kind = string.Equals((form.TryGetValue("prompt_kind", out var pk) ? pk : "") ?? "", "r2v", StringComparison.Ordinal) ? "r2v" : "t2v";
            var customPlan = IsDirectorCustomPlan(form);
            var sec = (form.TryGetValue("segment_seconds", out var ss) ? ss : "") ?? "5";
            if (isZh)
            {
                parts.Add("## 输出格式（最高优先级）");
                parts.Add($"严格按「公共设定 + {n} 组六段式（去主体定义）」输出，不要 Markdown，不要寒暄。");
                if (customPlan)
                {
                    parts.Add("分段方式=自定义：每段剧情严格按用户填写的「第 N 段在干什么」；不要擅自改情节主线。");
                }
                else
                {
                    parts.Add("分段方式=AI智能分段：用户只给了总段数和「创作需求」。你必须自行把创作需求拆成恰好 " + n + " 段连续剧情（起承转合，段间无硬切），并为每段选定合适时长（5 / 10 / 15 秒，导演台常用）。");
                    parts.Add("在每组「摘要:」里写明本段约几秒。补全运镜、动作、对白、声画。禁止向用户追问各段内容，禁止输出空壳或「待补充」。");
                }
                parts.Add("### 第一部分：公共设定（只出现一次，可贴导演台「公共参数」）");
                parts.Add("分隔行：===== 公共设定 =====");
                parts.Add("本块只写「主体定义:」，不要摘要/分镜/声景。");
                if (kind == "r2v" || (mediaPathList.Count > 0))
                {
                    parts.Add("有参考图时逐条：<Subject N> 来自 <Picture N> 的……身份与穿着完全锁定参考图：…… 可同时定义 <Picture N> 用途。");
                }
                else
                {
                    parts.Add("无参考图时用自然语言定义 <Subject N> 的身份与穿着，禁止编造 <Picture N>。");
                }
                parts.Add(customPlan
                    ? $"### 第二部分：{n} 段提示词（每段约 {sec} 秒，可贴导演台各「提示词组」）"
                    : $"### 第二部分：{n} 段提示词（每段时长由你定，5/10/15 秒，可贴导演台各「提示词组」）");
                parts.Add("每组分隔行：===== 提示词组 k =====");
                parts.Add("每组必须按 MiniMax 六段式输出，但禁止再写「主体定义:」。固定顺序：摘要: → 保留分析: → 详细描述: → 整体声景: → 非叙事配乐:");
                parts.Add("各段只引用公共设定里已有的 <Subject N>，不要重复外貌/服装圣经。保留分析只写本段出场主体及 fully_preserved 等关系。");
                parts.Add("第 2 组起，「详细描述:」第一句必须是「无硬切。紧接上一段。」本段时间码从 00:00 起算。段末写「段末停在…」。详细描述最后一行：不要乱说话");
                parts.Add("配乐主题跨组连续；除最后一组外段末不要淡出。");
                parts.Add("");
                parts.Add("## Full-Reference 中文指南（章节写法请遵循；主体定义只允许出现在第一部分）");
                parts.Add(LoadFullReferenceGuide("zh"));
            }
            else
            {
                parts.Add("## Output format (HIGHEST PRIORITY)");
                parts.Add($"Output public settings once, then exactly {n} Full-Reference groups without repeating subject_definitions.");
                if (customPlan)
                {
                    parts.Add("Plan mode=custom: follow each user-provided segment beat and duration.");
                }
                else
                {
                    parts.Add($"Plan mode=AI: user only gave segment count {n} and the creative brief. Split it into exactly {n} continuous beats yourself; pick 5/10/15s per group; write the duration in each summary. Do not ask for per-segment notes.");
                }
                parts.Add("Part 1: ===== 公共设定 ===== containing ONLY subject_definitions:");
                parts.Add("If media attached: <Subject N> is from <Picture N>, identity and wardrobe fully locked to the reference.");
                parts.Add(customPlan
                    ? $"Part 2: ===== 提示词组 k ===== × {n} (~{sec}s each)"
                    : $"Part 2: ===== 提示词组 k ===== × {n} (you pick 5/10/15s each)");
                parts.Add("Each group: summary → retention_analysis → detailed_description → overall_soundscape → non_diegetic_music. NEVER repeat subject_definitions in a group.");
                parts.Add("Group 2+ detailed_description starts with \"No hard cut. Immediately following the previous section.\" End with a freeze-frame handoff and the line 不要乱说话.");
                parts.Add("");
                parts.Add("## Full-Reference Guide (English edition — subject_definitions only in Part 1)");
                parts.Add(LoadFullReferenceGuide("en"));
            }
        }
        else if (scenario.OutputMode == "timeline_template")
        {
            if (isZh)
            {
                parts.Add("## 输出格式（最高优先级）");
                parts.Add("输出一份可直接生成的完整 MiniMax-H3 时间轴/事件框架视频提示词。不要 Markdown 代码围栏，不要闲聊。");
                parts.Add("正文用简体中文（若场景要求 CONTINUE 等固定英文 UI 词可保留）。");
            }
            else
            {
                parts.Add("## Output format (HIGHEST PRIORITY)");
                parts.Add("Output ONE complete MiniMax-H3 timeline / event-framework video prompt ready to generate. No markdown fences, no chat.");
                parts.Add("Prompt body language: English.");
            }
        }
        else
        {
            if (isZh)
            {
                parts.Add("## 输出格式（最高优先级）");
                parts.Add("输出一份完整、可直接使用的场景化 MiniMax-H3 视频提示词。不要 Markdown 代码围栏，不要闲聊，不要过程叙述。");
                parts.Add("严格按本场景硬约束与 Assembly hints 的段落顺序输出；不要套用 Full-Reference 六段标题。");
            }
            else
            {
                parts.Add("## Output format (HIGHEST PRIORITY)");
                parts.Add("Output ONE complete scenario-structured MiniMax-H3 video prompt ready to use. No markdown fences, no chat, no process narration.");
                parts.Add("Follow this scenario hard constraints and assembly hints section order; do NOT wrap into Full-Reference six sections.");
            }
        }

        parts.Add("");
        parts.Add("## Scenario hard constraints");
        foreach (var c in scenario.HardConstraints)
        {
            // 产品广告里写死 English copy 时，按输出语言改写该条
            if (isZh && Regex.IsMatch(c, @"In-frame copy:\s*English", RegexOptions.IgnoreCase))
            {
                parts.Add("- In-frame copy: concise Simplified Chinese (约 4–12 字), single line only; never two rows or bottom subtitles.");
                continue;
            }
            parts.Add($"- {c}");
        }

        if (scenario.AssembleHints.Length > 0)
        {
            parts.Add("");
            parts.Add("## Assembly hints");
            parts.Add(scenario.AssembleHints);
        }

        return string.Join("\n", parts);
    }

    /// <summary>buildUser（assemble.js buildUser 全量）。</summary>
    public static string BuildUser(MiniMaxScenario scenario, IReadOnlyDictionary<string, string> form, string? shortText, IEnumerable<string>? mediaPaths, string? outputLang)
    {
        var lang = string.Equals(outputLang, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";
        var isZh = lang == "zh";
        var userParts = new List<string>();
        userParts.Add("【场景】" + scenario.Name);
        userParts.Add("【输出语言】" + (isZh ? "中文" : "英文") + " — 这是强制要求，不是偏好；正文语言必须与此一致。");

        if (scenario.OutputMode == "full_reference")
        {
            userParts.Add(isZh
                ? "【强制】六段标题用中文（主体定义/摘要/保留分析/详细描述/整体声景/非叙事配乐），正文与画面文案用简体中文；禁止英文章节标题。无附图时禁止编造 <Picture N>。"
                : "【强制】Write detailed_description / summary / retention_analysis / sound sections and on-screen copy in English. Do not invent <Picture N> when no media is attached.");
        }
        else if (scenario.OutputMode == "director_segments")
        {
            var n = Math.Max(2, int.TryParse((form.TryGetValue("segment_count", out var sc) ? sc : "") ?? "", out var sn) && sn > 0 ? sn : 4);
            var kind = string.Equals((form.TryGetValue("prompt_kind", out var pk) ? pk : "") ?? "", "r2v", StringComparison.Ordinal) ? "r2v" : "t2v";
            var customPlan = IsDirectorCustomPlan(form);
            userParts.Add(isZh
                ? $"【强制】先输出 ===== 公共设定 =====（仅「主体定义:」），再输出 {n} 组 ===== 提示词组 k =====。每组按六段式但禁止再写主体定义，只写摘要/保留分析/详细描述/整体声景/非叙事配乐。方式={(kind == "r2v" ? "参考图锁角色" : "文生锁角色")}。分段={(customPlan ? "自定义（按各段说明）" : "AI智能分段（按创作需求自行拆成 " + n + " 段）")}。"
                : $"【REQUIRED】Part 1 ===== 公共设定 ===== (subject_definitions only). Part 2 = {n} Full-Reference groups without repeating subject_definitions. Mode={kind}. Plan={(customPlan ? "custom beats" : "AI split from brief")}.");
            if (!customPlan)
            {
                userParts.Add(isZh
                    ? "【智能分段】不要等待「第 N 段在干什么」。根据下方创作需求自行设计每段事件、时长与衔接。"
                    : "【AI split】Do not wait for per-segment beats. Invent each group from the brief below.");
            }
        }
        else
        {
            userParts.Add(isZh
                ? "【强制】按本场景段落顺序输出纯视频提示词正文（简体中文）。禁止套用六段式标题，禁止在无附图时编造 <Picture N>/<Subject N>。"
                : "【强制】Output a plain scenario video prompt in English. Do NOT use Full-Reference six sections. Do not invent <Picture N>/<Subject N> without attached media.");
        }

        userParts.Add("【表单参数】");
        var formLines = FormToLines(scenario, form);
        userParts.Add(formLines.Count > 0 ? string.Join("\n", formLines) : "- （无额外表单填写）");

        userParts.Add("");
        userParts.Add("【创作需求 / User request】");
        userParts.Add(string.IsNullOrWhiteSpace(shortText) ? "(empty)" : shortText.Trim());

        var mediaItems = ExpandMedia.EnumerateTaggedMedia(mediaPaths);
        var mediaLines = FormatMediaList(mediaPaths);
        if (mediaLines.Count > 0)
        {
            userParts.Add("");
            userParts.Add("【参考素材 / Reference media】");
            userParts.AddRange(mediaLines);
            userParts.Add("");
            if (isZh)
            {
                userParts.Add("【强制·素材标签】主体定义必须以如下标签逐条起笔（可按实际素材增减）：");
                foreach (var item in mediaItems) userParts.Add($"{item.Tag} …");
                if (scenario.OutputMode == "full_reference")
                {
                    userParts.Add("并为每个主要角色/产品写 <Subject N> 是 <Picture N> 中的……；后续段落必须复用这些标签。禁止只写角色名而不出现 <Picture N>。");
                }
                else
                {
                    userParts.Add("在正文中引用上述标签锚定外观；不要虚构额外未提供的素材标签。");
                }
            }
            else
            {
                userParts.Add("【REQUIRED media tags】Use these exact tags in the prompt body:");
                foreach (var item in mediaItems) userParts.Add($"{item.Tag} …");
                if (scenario.OutputMode == "full_reference")
                {
                    userParts.Add("Also define <Subject N> is from <Picture N> … and reuse these tags in later sections.");
                }
                else
                {
                    userParts.Add("Do not invent extra media tags beyond the list above.");
                }
            }
        }
        else
        {
            userParts.Add("");
            if (scenario.OutputMode == "director_segments")
            {
                userParts.Add(isZh
                    ? "【强制】本次无参考图：公共设定可用 <Subject N> 文字锁身份，严禁编造 <Picture N>/<Video N>/<Audio N>。"
                    : "【REQUIRED】No media: public subject_definitions may use <Subject N> in prose. Do not invent <Picture N>/<Video N>/<Audio N>.");
            }
            else
            {
                userParts.Add(isZh
                    ? "【强制】本次无参考素材附件：输出中不得出现 <Picture N>/<Subject N>/<Video N>/<Audio N>。"
                    : "【REQUIRED】No media attached: do not output <Picture N>/<Subject N>/<Video N>/<Audio N>.");
            }
        }

        userParts.Add("");
        userParts.Add("【任务】根据场景硬约束 + 表单参数 + 创作需求 + 参考素材，输出最终提示词。");
        return string.Join("\n", userParts);
    }

    /// <summary>resolveMinimaxScenarioExpand（assemble.js resolveMinimaxScenarioExpand）。</summary>
    public static ExpandPromptEngineering.ExpandPromptResult? ResolveMinimaxScenarioExpand(
        ExpandPromptEngineering.ExpandProfile profile,
        ExpandPromptEngineering.ExpandResolveParams p)
    {
        // getScenarioByPeId 支持 peId 或 builtinKey；profile.Id 即 peId（catalog.getScenarioByPeId 第一分支）
        var scenario = MiniMaxScenarios.GetScenarioByPeId(profile?.Id);
        if (scenario == null) return null;

        var form = NormalizeForm(scenario, p?.MinimaxForm ?? new Dictionary<string, string>());
        var shortText = (p?.ShortText ?? "").Trim();
        var mediaPaths = p?.MediaPaths is null ? new List<string>() : p.MediaPaths.ToList();
        var expandLen = string.IsNullOrEmpty(p?.ExpandLen) ? "long" : p.ExpandLen;
        var userExtraPrompt = (p?.UserExtraPrompt ?? "").Trim();
        var outputLang = string.Equals(p?.OutputLang, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";

        var system = BuildSystem(scenario, form, outputLang, mediaPaths);
        if (userExtraPrompt.Length > 0)
        {
            system += $"\n\n【User additional requirements】{userExtraPrompt}";
        }

        var maxTokens = Math.Max(
            ExpandRules.ResolveExpandMaxTokens(expandLen, p?.ExpandLenChars),
            MediaExpandMin);

        return new ExpandPromptEngineering.ExpandPromptResult(
            system,
            BuildUser(scenario, form, shortText, mediaPaths, outputLang),
            maxTokens,
            scenario.PeId);
    }

    /// <summary>isMinimaxScenarioProfile（assemble.js：minimaxScenarioId / outputFormat=minimax / getScenarioByPeId 命中）。</summary>
    public static bool IsMinimaxScenarioProfile(ExpandPromptEngineering.ExpandProfile? profile)
    {
        if (profile == null || profile.Kind != ExpandPromptEngineering.ExpandProfileKind.Minimax) return false;
        if (string.Equals(profile.OutputFormat ?? "", "minimax", StringComparison.Ordinal)) return true;
        return MiniMaxScenarios.GetScenarioByPeId(profile.Id) != null;
    }
}
