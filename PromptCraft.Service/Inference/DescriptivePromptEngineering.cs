using System.Text.RegularExpressions;

namespace PromptCraft.Service.Inference;

/// <summary>
/// 自然语言（Descriptive）提示词工程，对照 descriptivePromptEngineering.js 1:1 移植。
/// 五点结构框架 + 极致还原检查表 + 单段自然文本 + 禁止 Markdown。
/// </summary>
internal static class DescriptivePromptEngineering
{
    private const string FidelityPreambleZh =
        "【极致还原总则】你的职责是「视觉取证式」反推：像为后续文生图/图生图提供可复现的所见要素清单，而非写作文、讲故事或审美评论。写作前须逐项扫视画面；有则必写、无则省略；只写可见事实，禁止臆造。若画面含人物或拟人主体，头/面/视线、双臂双手（含持物与握法）、双腿双脚与重心、服装配饰材质均为硬性必写项，禁止用「美女/帅哥/精致/一个人」等套话代替。";

    private const string FidelityPreambleEn =
        "[MAX FIDELITY] Forensic visual extraction for faithful regeneration—not creative writing or story. Scan the frame before writing; include every visible fact, invent nothing. For people/anthropomorphic subjects: head/face orientation, gaze, expression, both arms/hands (grip if holding), legs/feet/weight, clothing materials are mandatory—no generic praise or category-only labels.";

    public static string BuildSystem(string outputLang, string mediaTarget, int wordCount)
    {
        bool zh = (outputLang ?? "zh").Equals("zh", StringComparison.OrdinalIgnoreCase);
        bool video = (mediaTarget ?? "image").Equals("video", StringComparison.OrdinalIgnoreCase);
        string media = video ? (zh ? "视频画面" : "video") : (zh ? "图像" : "image");

        if (zh)
        {
            return $"你是顶级的视觉取证与画面还原专家，须对当前{media}进行精确、详尽、可复现的描述。\n"
                + FidelityPreambleZh + "\n"
                + $"总字数控制在约 {wordCount} 字，信息高度密集、语义连贯，宁可写细勿概括省略可见要素。\n"
                + FivePointFramework(zh, video) + "\n"
                + "【最终输出格式】将五点分析与检查表要素融合为一整段顺滑流淌的自然文本；"
                + "绝对严禁输出任何 Markdown 标记（禁止 ** 加粗、- 列表、# 标题、序号标题）；"
                + "禁止分点、禁止提纲、禁止「这张图片」「用户需要」「本任务」等元话语。";
        }

        return $"You are an expert forensic visual analyst for maximum faithful reproduction of this {media}.\n"
            + FidelityPreambleEn + "\n"
            + $"One cohesive English paragraph (~{wordCount} words), dense and reproducible—prefer exhaustive visible detail over vague summary.\n"
            + FivePointFramework(zh, video) + "\n"
            + "[OUTPUT] Single flowing paragraph only—no Markdown, bullets, headings, or meta commentary.";
    }

    private static string FivePointFramework(bool zh, bool video)
    {
        if (zh)
        {
            return "【五点结构框架·极致还原】请对传入的图像按下列五点在内心完整扫视并分析后，再写入最终正文（正文里不要出现「一、二、」等标题，须融为一段连贯叙述）："
                + "（1）物理空间与构图逻辑：景别、机位、主体位置、裁切/遮挡、前中后景层次、透视与镜头方位、留白、引导线、景深。"
                + "（2）主体刻画与材质反推：数量与主次；物种/角色；发型与遮挡；头部朝向、视线、表情；躯干、肩线、双臂双手与持物握法；双腿站姿/坐姿与重心；服装剪裁、叠穿、图案、材质、配饰。"
                + "（3）环境建模与光照参数：室内/室外、场所；地面/墙面/天空/道具；焦距与虚化；主辅光方向、硬柔、色温、阴影、高光。"
                + "（4）OCR 级别文本读取：逐字识别画面文字，无则略过，禁止虚构。"
                + "（5）美学风格与统筹：媒介（摄影/插画/3D/动漫等）、线条笔触、细节密度、流派、主辅色与对比。"
                + (video ? " 视频另须写动作过程、速度感、运镜方式、运动模糊方向。" : "");
        }
        return "[FIVE-PART FRAMEWORK] Analyze on five axes mentally, then output ONE flowing paragraph (no headings):"
            + " (1) Space & composition: shot scale, camera angle, subject placement, crop, foreground/mid/background, depth of field."
            + " (2) Subject & materials: count, hair/face/gaze, arms/hands/grip, legs/feet, clothing layers and materials."
            + " (3) Environment & light: location, surfaces, props, key/fill light, shadows, highlights."
            + " (4) Visible text only—never invent OCR."
            + " (5) Medium/style, detail density, mood, palette."
            + (video ? " For video also state action, speed, camera move, motion blur." : "");
    }

    public static string BuildUserLead(string outputLang, string mediaTarget, int wordCount)
    {
        bool zh = (outputLang ?? "zh").Equals("zh", StringComparison.OrdinalIgnoreCase);
        bool video = (mediaTarget ?? "image").Equals("video", StringComparison.OrdinalIgnoreCase);
        if (zh)
            return video
                ? $"请按系统提示中的五点结构框架逐项扫视当前视频画面，输出约 {wordCount} 字的一整段中文描述。"
                : $"请按系统提示中的五点结构框架逐项扫视当前图像，输出约 {wordCount} 字的一整段中文描述。";
        return video
            ? $"Scan this video with the five-part framework; output one English paragraph (~{wordCount} words)."
            : $"Scan this image with the five-part framework; output one English paragraph (~{wordCount} words).";
    }

    /// <summary>后处理：去 Markdown 残留、合并为一段。</summary>
    public static string SanitizeProse(string text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return s;
        s = Regex.Replace(s, "\r\n", "\n");
        s = Regex.Replace(s, @"^#{1,6}\s+", "", RegexOptions.Multiline);
        s = Regex.Replace(s, @"\*\*([^*]+)\*\*", "$1");
        s = Regex.Replace(s, @"\*([^*]+)\*", "$1");
        s = Regex.Replace(s, @"^\s*[-*•]\s+", "", RegexOptions.Multiline);
        s = Regex.Replace(s, @"^\s*\d+[.)）、]\s*", "", RegexOptions.Multiline);
        s = Regex.Replace(s, @"\n{2,}", "\n");
        s = s.Replace("\n", "");
        s = Regex.Replace(s, @"\s{2,}", " ");
        return s.Trim();
    }
}
