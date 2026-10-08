namespace PromptCraft.Service.Inference;

/// <summary>
/// 提示词仿写的提示词工程组装层。
/// system：仿写专家角色 + 输出语言锁定 + 输出格式提示 + 【仿写要求】；
/// user：仅携带【原始提示词】，交给模型按要求改写。
/// 复用 ExpandRules.GetExpandFormatHint 取现有 6 类输出格式的格式提示。
/// </summary>
public static class ImitationPromptEngineering
{
    private const string OutputLangLockZh = """
        【输出语言锁定：中文】（最高优先级，覆盖前文一切关于「跟随输入语言」的说明）
        1. 用户已在界面指定「输出语言 = 中文」，你必须全程用中文（汉字）书写改写后的提示词正文。
        2. 即使用户输入的原始提示词是英文单词、Danbooru tag 或中英混杂，也须理解语义后用中文自然语言改写，不得整段输出英文 tag 列表或英文句子。
        3. 允许少量必要英文专有名词（如 Stable Diffusion、MiniMax、LoRA），但描写主体必须是中文。
        """;

    private const string OutputLangLockEn = """
        【Output language lock: English】（Highest priority; overrides any "match input language" rule above）
        1. User selected English output; write the entire rewritten prompt in English.
        2. Even if the source prompt is Chinese or mixed, output must be English prose or phrases.
        """;

    private const string OutputLangSuffixZh = "\n\n只输出改写后的提示词正文，不要解释、不要 Markdown、不要编号列表。";
    private const string OutputLangSuffixEn = "\n\nOutput only the rewritten prompt text. No explanation, no markdown, no numbered lists.";

    private static string GetMinimaxHint(string? outputLang)
    {
        var zh = "【格式】输出为 MiniMax-H3 视频提示词。若使用官方六段结构，请按顺序写：主体定义 / 摘要 / 保留分析 / 详细描述 / 整体声景 / 非叙事配乐；禁止英文章节标题。对白与画面文字保持原文语言。";
        var en = "【Format】Output as a MiniMax-H3 video prompt. If using the official structure, keep the order: subject_definitions / summary / retention_analysis / detailed_description / overall_soundscape / non_diegetic_music. Keep dialogue and on-screen text in their original language.";
        return (outputLang == "en") ? en : zh;
    }

    /// <summary>仿写角色 + 语言锁 + 格式提示 + 硬规则 + 性别专项改写。</summary>
    public static string BuildSystem(string requirement, string? formatId, string? outputLang, string? targetGender = null)
    {
        var lang = (outputLang ?? "zh").ToLowerInvariant();
        var useEn = lang == "en";

        var role = useEn
            ? """
            You are an expert at rewriting and imitating image-generation prompts. The user will give you an original prompt and an imitation requirement; rewrite the original prompt into a new prompt that strictly satisfies the requirement.

            Hard rules:
            1. The imitation requirement is the HIGHEST priority. Rewrite only what the requirement touches; keep aspects it does not touch (identity, medium, frame, background, composition, pose, proportions, etc.) as-is.
            2. When the requirement conflicts with a description in the original prompt (gender, era, style, temperament, clothing, hairstyle/hair ornaments, face/jaw/eyes, body build, height, weight, etc.), rewrite that description to match the requirement. Never keep wording that contradicts it.
               Example — requirement says "change to male / Tang dynasty clothing": temperament, face shape, eyebrows/eyes, hairstyle, hair ornaments, clothing cut, and body metrics (height/weight/build) must all become typically male / era-consistent descriptions (e.g. "gentle-cool / oval face / apricot eyes / high bun / step-sway beaded hairpin / sheer long trailing skirt / 165cm 45kg" → "clear-handsome / sharp brows & bright eyes / top-knot or futou headwrap / round-collar robe & long robe / tall broad-shouldered build"), not just swap the single gender word.
            3. You may add descriptions needed to satisfy the requirement (e.g. male hairstyle/clothing details after a gender change), but do not add content unrelated to the requirement.
            4. Follow the Output language lock below (highest priority).
            5. Output ONLY the rewritten prompt text — no explanation, no chain-of-thought, no markdown headings.
            """
            : """
            你是一位提示词仿写与改写专家。用户会提供一段「原始提示词」和一条「仿写要求」，你需要严格按仿写要求，把原始提示词改写为一条符合要求的新提示词。

            绝对要求：
            1. 以「仿写要求」为最高优先级：只改写与仿写要求相关的内容；原始提示词中与要求无关的要素（身份、媒介、画幅、背景、构图、站姿、比例等）保持原样。
            2. 当仿写要求与原始提示词某处描述冲突时（如性别、朝代、风格、气质、服装、发型发饰、脸型眉目、体型体格、身高体重等），必须以仿写要求为准改写该处，使其与要求一致，禁止保留与要求冲突的原始措辞。
               例如要求「改成男性、穿唐装」：气质、脸型、眉目、发型、发饰、服装剪裁以及身高、体重、体型等性别/朝代相关描述必须相应改写为男性与目标朝代的典型描述（如「温婉清冷/鹅蛋脸/杏眼/高髻/步摇珠翠/薄纱长裙拖尾/身高165cm体重45kg」→「清俊英朗/剑眉星目/束发幞头/圆领袍长衫/身高约178cm体重约68kg、肩宽挺拔」等），不得只在性别词上做文字替换。
            3. 允许为满足仿写要求补充必要描述（如改性别后补充男性发型/服饰细节），但不得加入与要求无关的新增内容。
            4. 输出语言以用户在界面选择的「输出语言」为准（见 system 中的【输出语言锁定】），优先级高于输入语种。
            5. 只输出改写后的提示词正文，不要解释、不要思考过程、不要 Markdown 小标题。
            """;

        var sb = new System.Text.StringBuilder();
        sb.Append(role);

        // 输出格式提示（现有 6 类复用；minimax 走 H3 专属文案）
        var formatIdNorm = (formatId ?? "").Trim();
        var formatHint = ExpandRules.GetExpandFormatHint(formatIdNorm, outputLang);
        if (formatIdNorm == "minimax")
        {
            sb.Append('\n').Append(GetMinimaxHint(outputLang));
        }
        else if (!string.IsNullOrEmpty(formatHint))
        {
            sb.Append('\n').Append(useEn ? "【Format】" : "【格式】").Append(formatHint);
        }

        // 语言锁
        sb.Append(useEn ? OutputLangLockEn : OutputLangLockZh);

        // 性别专项改写（识别到“改成男性/女性”时追加，最高优先级、高显式）
        if (targetGender == "male" || targetGender == "female")
        {
            sb.Append('\n').Append(useEn ? GenderFlipBlockEn(targetGender) : GenderFlipBlockZh(targetGender));
        }

        // 仿写要求
        var req = (requirement ?? "").Trim();
        if (req.Length > 0)
            sb.Append('\n').Append(useEn ? "【Imitation requirement】" : "【仿写要求】").Append('\n').Append(req);

        // 后缀
        sb.Append(useEn ? OutputLangSuffixEn : OutputLangSuffixZh);
        return sb.ToString();
    }

    /// <summary>user 消息：仅携带原始提示词。</summary>
    public static string BuildUser(string sourcePrompt, string? outputLang)
    {
        var useEn = (outputLang ?? "zh") == "en";
        var src = (sourcePrompt ?? "").Trim();
        var intro = useEn
            ? "Below is the ORIGINAL prompt. Rewrite it according to the imitation requirement above; output only the rewritten prompt."
            : "以下是「原始提示词」。请严格按照上面的「仿写要求」改写它，只输出改写后的提示词：";
        return $"{intro}\n\n【{(useEn ? "Original prompt" : "原始提示词")}】\n{src}";
    }

    /// <summary>目标性别 = 男性时：逐项列出必须删除/替换的女性特征。</summary>
    private static string GenderFlipBlockZh(string gender)
    {
        if (gender == "male")
        {
            return """
                【性别改写·专项（最高优先级）】目标性别：男性。
                原始提示词中所有属于女性特征的描述，必须逐一删除或改写为男性对应，禁止以任何形式保留（包括“男式步摇/唐制高髻”这类别扭组合）：
                - 发型发饰：高髻、步摇、珠翠、流苏发饰、簪花、钿花 → 束发高马尾 / 唐幞头 / 发冠，或直接去掉发饰；
                - 服装：薄纱、半透纱、长裙拖尾、裙裾、襦裙、纱裙 → 男子圆领袍 / 长衫 / 袍服，去掉拖尾与裙摆；
                - 面容气质：鹅蛋脸、杏眼、温婉清冷、柔美、婀娜 → 剑眉星目、清俊英朗、下颌线条硬朗；
                - 体格：过矮过轻的身高体重 → 男性典型体格（如身高175cm以上、体重60kg以上、肩宽挺拔）。
                """;
        }
        return """
            【性别改写·专项（最高优先级）】目标性别：女性。
            原始提示词中所有属于男性特征的描述，必须逐一删除或改写为女性对应，禁止以任何形式保留：
            - 发型发饰：束发高马尾、唐幞头、发冠、短须 → 高髻、盘发、步摇、珠翠流苏发饰、簪花；
            - 服装：圆领袍、长衫、袍服 → 襦裙、长裙、薄纱、拖尾裙裾；
            - 面容气质：剑眉星目、清俊英朗、下颌硬朗、英气 → 鹅蛋脸、杏眼、温婉清冷、柔美；
            - 体格：过高过重的体格 → 女性典型体格（如身高约160cm、体重约45kg、纤细）。
            """;
    }

    private static string GenderFlipBlockEn(string gender)
    {
        if (gender == "male")
        {
            return """
                【Gender flip · specific (HIGHEST priority)】Target: male.
                Every female-specific description in the original prompt must be removed or rewritten to the male equivalent. Do NOT keep any of them in any form (including awkward mixes like "male step-sway hairpin"):
                - Hair/hairstyle ornaments: high bun, step-sway hairpin (buyao), beaded ornaments (zhucui), tassel hair ornaments, hairpins → top-knot ponytail / Tang futou headwrap / hair crown, or drop the ornaments entirely;
                - Clothing: sheer, translucent gauze, long trailing skirt, flowing skirt hem, ru skirt → men's round-collar robe / long robe; remove the train and skirt hem;
                - Face & temperament: oval face, apricot eyes, gentle-cool, soft, graceful → sharp brows & bright eyes, clear-handsome, defined jawline;
                - Build: height/weight that are too small for a male → typical male build (e.g. height 175cm+, weight 60kg+, broad shoulders).
                """;
        }
        return """
            【Gender flip · specific (HIGHEST priority)】Target: female.
            Every male-specific description in the original prompt must be removed or rewritten to the female equivalent. Do NOT keep any of them in any form:
            - Hair/hairstyle ornaments: top-knot ponytail, futou headwrap, hair crown, short beard → high bun, coiled hair, step-sway beaded hairpin, tassel ornaments, hairpins;
            - Clothing: round-collar robe, long robe → ru skirt, long skirt, sheer gauze, trailing hem;
            - Face & temperament: sharp brows & bright eyes, clear-handsome, defined jawline, masculine → oval face, apricot eyes, gentle-cool, soft;
            - Build: too large for a female → typical female build (e.g. height ~160cm, weight ~45kg, slender).
            """;
    }
}
