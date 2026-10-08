using System.Text;
using PromptCraft.Models.Inference.Novel;

namespace PromptCraft.Service.Inference.Novel;

/// <summary>
/// 小说→提示词流水线各阶段的提示词工程：为 S1 概念 / S2 角色 / S3 世界观 / S4 分场 / S6 镜头规划
/// 组装 system（专家角色 + 阶段规则）与 user（携带小说原文 + 前面已确认阶段的输出）。
/// 依据「洋子」skill 的 ai-film-router / film-cinedance 各模块规则（概念锚定、角色圣经、世界观可选、分场只写物理动作、镜头规划门）。
/// </summary>
public static class NovelPromptEngineering
{
    // ---------- system ----------
    public static string BuildStageSystem(NovelStage stage, NovelPromptOptions opts)
    {
        var s = stage switch
        {
            NovelStage.Concept => ConceptSystem,
            NovelStage.Characters => CharactersSystem,
            NovelStage.Worldbuilding => WorldbuildingSystem,
            NovelStage.Treatment => TreatmentSystem(opts),
            NovelStage.ShotPlanning => ShotPlanningSystem(opts),
            _ => "",
        };
        return s + "\n" + LanguageDirective(opts.OutputLanguage);
    }

    /// <summary>输出语言名（用于写进指令的表述）。</summary>
    public static string LangName(NovelOutputLanguage lang) =>
        lang == NovelOutputLanguage.English ? "English（英语）" : "简体中文";

    /// <summary>输出语言最高优先级指令（追加到各 system 提示词末尾，强制全部输出使用所选语言）。</summary>
    public static string LanguageDirective(NovelOutputLanguage lang) =>
        lang == NovelOutputLanguage.English
            ? "【输出语言·最高优先级】本次任务的全部输出文本必须使用 English（英语）；除用户明确要求保留原文的内容（如对白原文、资产原名）外，禁止输出中文或其他语言，不得因输入为中文而改用中文输出。"
            : "【输出语言·最高优先级】本次任务的全部输出文本必须使用简体中文；除用户明确要求保留原文的内容（如对白原文、资产原名）外，禁止输出其他语言，不得因输入为英文而改用英文输出。";

    /// <summary>资产需求提取（S5 前置：从小说/分场/角色里提取角色、场景、道具资产需求表）。</summary>
    private const string AssetExtractionSystem = """
        你是一位影视制作的资产统筹（Lira 资产需求）专员。
        输入：小说原文 + 已确认的分场表 + 角色圣经（+世界观）。
        任务：提取本片拍摄所需的「资产需求表」，供图像资产扫描比对使用。
        输出要求：
        1. 分类列出【角色资产】【场景资产】【道具资产】三组；场景/道具给出简短名称。
        2. 角色资产严格以「角色圣经」为准：圣经中每个角色生成一个基础资产（如 C01）；仅当圣经明确列出该角色存在不同服饰或发型变体（状态 ID，如 C01-A、C01-B）时，才为每个变体单独生成一个资产。严禁根据小说情节新增状态（表情、情绪、伤势、动作姿态、身份阶段等一律不得拆资产）；分场或小说中明确出现的换装/换发型（如换礼服、穿战斗服）可作为补充资产，但必须确实是服饰或发型差异。
        3. 每个角色（含变体）必须给出可作文件名的命名建议（如「C01_少年」「C01-A_常服」「C01-B_晚礼服」）；小说中未给出名字的人物，一律用「与主角的关系 + 括号说明」命名（如「C03_主角的母亲（林母）」「C05_女主的妹妹」），严禁输出「未命名」「无名」等字样。
        4. 每项资产标注使用场次（如 S01、S03-S05）。
        5. 每行格式：资产名 | 类型 | 使用场次 | 命名建议。
        6. 类型列只能填「角色」「场景」「道具」三者之一；严禁输出角色/场景/道具之外的任何其他资产类型（如音效、音乐、特效、服装单品、表情等）。
        7. 只提取真正上镜需要的资产，龙套/可忽略物件合并或省略。
        只输出资产需求表正文，不要解释。
        """;

    /// <summary>资产需求提取（S5 前置）：system 提示词。</summary>
    public static string BuildAssetExtractionSystem(NovelOutputLanguage lang) => AssetExtractionSystem + "\n" + LanguageDirective(lang);

    /// <summary>资产需求提取（S5 前置）：user 携带小说+概念+角色+世界观+分场。</summary>
    public static string BuildAssetExtractionUser(NovelPipelineContext ctx)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("【小说原文】").AppendLine(string.IsNullOrWhiteSpace(ctx.NovelText) ? "(未提供)" : ctx.NovelText.Trim()).AppendLine();
        if (!string.IsNullOrWhiteSpace(ctx.Concept)) sb.AppendLine("【已确认：故事概念】\n" + ctx.Concept.Trim() + "\n");
        if (!string.IsNullOrWhiteSpace(ctx.Characters)) sb.AppendLine("【已确认：角色圣经】\n" + ctx.Characters.Trim() + "\n");
        if (!string.IsNullOrWhiteSpace(ctx.Worldbuilding)) sb.AppendLine("【已确认：世界观】\n" + ctx.Worldbuilding.Trim() + "\n");
        if (!string.IsNullOrWhiteSpace(ctx.Treatment)) sb.AppendLine("【已确认：分场表】\n" + ctx.Treatment.Trim() + "\n");
        sb.AppendLine("请据此输出资产需求表。");
        return sb.ToString();
    }

    private const string ConceptSystem = """
        你是一位专业的短片概念锚定（Story Concept / Synopsis）编剧。用户会提供一段小说或长文本。
        你的任务：只做「概念锚定」，把故事压缩成一张可投入后续拆解的概念卡，不写分场、不改写剧本。
        输出必须包含：
        1. 一句话故事梗概（主角 + 目标 + 核心冲突 + 钩子）。
        2. 主角信息（姓名/身份/核心欲望/最大障碍）。
        3. 主题与情绪基调。
        4. 是否适合改编为 1 分钟短视频/一镜到底/系列短片的初步判断与理由。
        5. 改编建议（可拍性、需精炼的对白区、需拆分的长段）。
        只输出概念卡正文，不要解释、不要思考过程、不要 Markdown 大标题。
        """;

    private const string CharactersSystem = """
        你是一位专业的角色圣经（Character Bible）编剧。
        输入：小说原文 + 前面已确认的故事概念。
        任务：提炼剧中「有镜头价值」的主要角色，输出角色圣经。
        规则：
        1. 同一人物只用一个角色 ID（如 C01）。仅当同一人物存在明显不同的服饰或发型（影响外观资产）时，才允许用状态 ID 区分（如 C01-A 常服、C01-B 晚礼服）；伤势、变身、身份阶段、情绪、表情、动作姿态等一律不得拆分为状态 ID，只在「外貌与服装关键描述」中注明即可。
        2. 每个角色给出：角色 ID、姓名、身份/职业、外貌与服装关键描述（含各状态变体的服饰/发型差异）、性格与说话方式、目标与弧光、与主角关系、可复用为 Lira 图像资产的提示词要点。
        3. 所有角色必须给出明确、可作资产文件名的姓名或称呼；小说中未给出名字的人物，一律用「与主角的关系 + 括号说明」命名（如「主角的母亲（林母）」「女主的妹妹」），严禁输出「未命名」「无名」等字样。
        4. 只提炼对成片有影响的角色；龙套合并说明，不逐一罗列。
        只输出角色圣经正文。
        """;

    private const string WorldbuildingSystem = """
        你是一位专业的世界观（Worldbuilding）顾问。
        输入：小说原文 + 已确认的概念与角色。
        任务：判断该故事是否需要补世界观，仅当存在魔法、超能力、科幻技术、历史制度、复杂组织或特殊空间规则且直接影响剧情时才写；普通现实题材输出一行“无需世界观”即可。
        输出规则卡：规则名称、规则内容、对剧情/镜头的影响、需要视觉化的点。
        只输出世界观正文。
        """;

    private static string TreatmentSystem(NovelPromptOptions opts)
    {
        // 整场时长预算由 skill 在镜头规划时按内容自动核算，无需用户填写
        return $"""
            你是一位专业的分场大纲（Treatment）编剧。
            输入：小说原文 + 已确认的概念、角色与世界观。
            任务：把故事拆成分场表（Scene Beat Sheet），供后续逐镜头规划使用。
            严格规则：
            1. 分场只写【物理动作与场次信息】，不写完整对白；需要台词的位置用〈拟对白主题〉标注（如〈质问〉〈表白〉）。
            2. 每场给出：场次 ID、内外景、地点、时间、出场角色（含状态 ID）、主要动作、情绪转折、建议时长预算（秒）。
            3. 整场总时长预算由你根据内容自动核算（用户未指定固定预算），按剧情重要性分配时长，不得机械均分，也不得为了凑时长注水空镜。
            4. 面向短视频/镜头生成：优先拆出可单镜/少镜完成的短场，避免一场 60 秒以上的超长场（若必须长场，注明拆分建议）。
            只输出分场表正文。
            """;
    }

    private static readonly string ShotJsonExample = """
        [{"shotId":"SH001","sourceScene":"S01","purpose":"...","duration":6,"shotSize":"中景","cameraPosition":"...","focalLength":"50mm","cameraMovement":"固定","worldPosition":"...","screenPosition":"...","gaze":"...","axisSide":"...","action":"...","performance":"...","propsState":"...","dialogue":"...","dialogueCharCount":0,"dialogueSeconds":0,"assetsUsed":"...","firstFrame":"...","lastFrame":"...","continuityRisk":"...","longDurationReason":"","targetModel":"MiniMaxH3"}]
        """;

    private static string ShotPlanningSystem(NovelPromptOptions opts)
    {
        var aspect = string.IsNullOrEmpty(opts.AspectRatio) ? "16:9" : opts.AspectRatio;
        var defSec = opts.DefaultShotSeconds > 0 ? opts.DefaultShotSeconds.ToString("0.#") : "6";
        return $"""
            你是一位专业的镜头规划（Shot Planning）导演。
            输入：已确认的分场表 + 角色圣经 + 世界观（如有）+ 资产清单。
            任务：先做场次时长核算，再输出整场镜头清单（shot plan）。这是「镜头规划门」，只出镜头表，不出视频提示词。
            规则：
            1. 先核算每场时长：顺序动作+对白+停顿+反应依次相加；简单手部动作与对白同时发生时取较长者；复杂走位/打斗/道具变化/摄影机运动按实际完成时间算。内容只够 35 秒时禁止凭空补到 60 秒；需要 75 秒而预算 60 秒时标记冲突。
            2. 镜头以 3 至 10 秒居多（默认 {defSec} 秒，用户给的是“大概时长”，你可按剧情实际调整）；10 至 15 秒只用于确有需要的连续复杂动作/完整长台词/重要沉默/空间建立，且必须写明长时长理由。H3 单镜适合 4 至 15 秒，一场 60 秒必须先拆多镜头再逐镜输出。
            3. 中文对白安全预算：默认自然剧情语速约每秒 2.5 至 3.2 个有效汉字，再叠加标点停顿/思考/呼吸。40 字对白放进 10 秒内判定失败。表内给出每句对白字数与预计说话时间。
            4. 每次切镜必须提供新的叙事/表演/空间信息或剪辑功能；不得一句台词一个镜头，也不得把三分钟场景压成一两个超长生成片段。按需使用主镜头、双人镜头、正反打、过肩、反应、插入、主观、动作匹配、转场。
            5. 画幅 {aspect}；整场时长预算由你根据分场动作与对白自动核算（用户只提供每段大概时长，不指定固定总秒数），按剧情重要性分配，不得为了凑时长注水空镜。
            6. 每个镜头必须完整给出：镜头 ID、来源场次、镜头目的与剪切原因、时长、景别、机位、焦段、摄影机运动、人物世界位置与画面位置、视线与 180 度轴线、动作与表演节拍、道具状态、本镜头完整对白（含字数与预计说话时间）、使用的资产、首帧硬描述、尾帧硬描述、连续性风险、10 秒以上镜头的理由、目标模型建议。
            7. 用【严格 JSON 数组】输出镜头清单（除首尾无多余文字），字段名用英文：
            {ShotJsonExample}
            """;
    }

    // ---------- user ----------
    public static string BuildStageUser(NovelStage stage, NovelPipelineContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("【小说原文】");
        sb.AppendLine(string.IsNullOrWhiteSpace(ctx.NovelText) ? "(未提供)" : ctx.NovelText.Trim());
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(ctx.Concept)) sb.AppendLine("【已确认：故事概念】\n" + ctx.Concept.Trim() + "\n");
        if (!string.IsNullOrWhiteSpace(ctx.Characters)) sb.AppendLine("【已确认：角色圣经】\n" + ctx.Characters.Trim() + "\n");
        if (!string.IsNullOrWhiteSpace(ctx.Worldbuilding)) sb.AppendLine("【已确认：世界观】\n" + ctx.Worldbuilding.Trim() + "\n");
        if (!string.IsNullOrWhiteSpace(ctx.Treatment)) sb.AppendLine("【已确认：分场表】\n" + ctx.Treatment.Trim() + "\n");

        if (ctx.ConfirmedAssets.Count > 0)
            sb.AppendLine("【已确认资产清单】\n" + string.Join("\n", ctx.ConfirmedAssets) + "\n");

        switch (stage)
        {
            case NovelStage.Concept:
                sb.AppendLine("请据此输出故事概念卡。");
                break;
            case NovelStage.Characters:
                sb.AppendLine("请据此输出角色圣经。");
                break;
            case NovelStage.Worldbuilding:
                sb.AppendLine("请判断是否需要世界观并输出规则卡。");
                break;
            case NovelStage.Treatment:
                sb.AppendLine("请据此输出分场表。");
                break;
            case NovelStage.ShotPlanning:
                sb.AppendLine("请先做场次时长核算，再输出镜头清单 JSON。");
                break;
        }
        return sb.ToString();
    }

    /// <summary>镜头规划分批模式：单批指令（限制本批镜头数量 + 已输出进度回传 + CONTINUE/DONE 推进协议）。
    /// nextGlobalNumber 为本批第一镜的全局编号；emittedShots 为已输出的本场镜头（第 2 批起回传进度）；retry=true 表示上一轮本批未产出，提示继续或输出空数组收尾。</summary>
    public static string BuildShotPlanBatchInstruction(int first, int last, int batchNo, int nextGlobalNumber,
        IReadOnlyList<ShotItem>? emittedShots = null, bool retry = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine("输出要求（分批模式）：");
        if (retry)
        {
            sb.AppendLine($"1. 上一轮本批未输出任何镜头。请继续输出本场第 {first} 至第 {last} 个镜头（全局编号从 SH{nextGlobalNumber:000} 开始连续）；若系统判定本场已无剩余镜头，请直接输出空数组 [] 并在其后单独一行输出 DONE，不要输出任何解释。");
            sb.AppendLine($"2. 仍然输出【严格 JSON 数组】（字段与前面要求完全一致），除数组与推进标记外不要多余文字。");
            sb.AppendLine($"3. 输出完本批后，如果本场还有剩余镜头，在 JSON 数组结束后单独一行输出 CONTINUE；如果本场镜头已全部输出，单独一行输出 DONE。");
            sb.AppendLine($"4. 字段名保持英文；字段值（目的/动作/对白/描述等）使用所选输出语言；对白保持用户确认的原文。");
            return sb.ToString();
        }
        sb.AppendLine($"1. 当前是第 {batchNo} 批：只输出本场第 {first} 至第 {last} 个镜头（按分场动作顺序），本批镜头全局编号从 SH{nextGlobalNumber:000} 开始连续（SH{nextGlobalNumber:000}、SH{nextGlobalNumber + 1:000}…），不得重复、跳号或输出本批以外的镜头。");
        if (emittedShots is { Count: > 0 })
        {
            var lastShot = emittedShots[^1];
            var tail = lastShot.LastFrame ?? "";
            if (tail.Length > 60) tail = tail.Substring(0, 60) + "…";
            sb.AppendLine($"2. 进度参考：本场已生成 {emittedShots.Count} 个镜头（最后一个是 {lastShot.ShotId}，其尾帧为「{tail}」）。请在此基础上继续规划，不要重新输出已完成的镜头。");
            sb.AppendLine($"3. 仍然输出【严格 JSON 数组】（字段与前面要求完全一致），除数组与推进标记外不要多余文字。");
            sb.AppendLine($"4. 输出完本批后，如果本场还有剩余镜头，在 JSON 数组结束后单独一行输出 CONTINUE；如果本场镜头已全部输出，单独一行输出 DONE。");
            sb.AppendLine($"5. 字段名保持英文；字段值（目的/动作/对白/描述等）使用所选输出语言；对白保持用户确认的原文。");
        }
        else
        {
            sb.AppendLine($"2. 仍然输出【严格 JSON 数组】（字段与前面要求完全一致），除数组与推进标记外不要多余文字。");
            sb.AppendLine($"3. 输出完本批后，如果本场还有剩余镜头，在 JSON 数组结束后单独一行输出 CONTINUE；如果本场镜头已全部输出，单独一行输出 DONE。");
            sb.AppendLine($"4. 字段名保持英文；字段值（目的/动作/对白/描述等）使用所选输出语言；对白保持用户确认的原文。");
        }
        return sb.ToString();
    }

    /// <summary>镜头总数预估指令：只让模型回一个整数（作为分批锚点，防止镜头表无限膨胀）。</summary>
    public static string BuildShotPlanCountEstimateInstruction()
    {
        return "【镜头总数估算（仅此轮，不输出镜头表）】\n" +
               "请先核算本场总时长与动作/对白节奏，判断该场大概需要多少个镜头（单镜 4–15 秒）。\n" +
               "只输出一个整数（例如：12），不要输出任何其他文字、解释、标点或 JSON。";
    }

    /// <summary>镜头规划最终确认轮指令：已超出预估总数，模型需明确表态（剩余镜头或空数组收尾），防止「满批就继续」导致的无限膨胀。</summary>
    public static string BuildShotPlanFinalCheckInstruction(int nextGlobalNumber,
        IReadOnlyList<ShotItem>? emittedShots = null, int? estimatedTotal = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("输出要求（最终确认轮）：");
        var emitted = emittedShots?.Count ?? 0;
        if (estimatedTotal is > 0)
            sb.AppendLine($"1. 系统预估本场约 {estimatedTotal} 个镜头，目前已生成 {emitted} 个，已达到/超出预估范围。");
        else
            sb.AppendLine($"1. 系统已要求确认本场是否仍有剩余镜头，目前已生成 {emitted} 个。");
        sb.AppendLine($"2. 若本场确实仍有剩余镜头（预估偏低/存在漏规划），请继续输出剩余镜头（全局编号从 SH{nextGlobalNumber:000} 开始连续），" +
                      "并在 JSON 数组结束后单独一行输出 CONTINUE；若本场镜头已全部输出，请直接输出空数组 [] 并单独一行输出 DONE，不要输出任何解释。");
        sb.AppendLine("3. 仍然输出【严格 JSON 数组】（字段与前面要求完全一致），除数组与推进标记外不要多余文字。");
        sb.AppendLine("4. 字段名保持英文；字段值（目的/动作/对白/描述等）使用所选输出语言；对白保持用户确认的原文。");
        return sb.ToString();
    }
}
