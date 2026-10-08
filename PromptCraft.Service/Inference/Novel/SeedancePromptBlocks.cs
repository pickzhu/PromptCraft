using System.Text;
using PromptCraft.Models.Inference.Novel;

namespace PromptCraft.Service.Inference.Novel;

/// <summary>
/// Seedance 2.0 提示词固定区块模板（对齐「08-CINEDANCE」Seedance 区块规范）。
/// 中文区块逐一输出，不能与 H3 官方英文字段混写。
/// </summary>
public static class SeedancePromptBlocks
{
    /// <summary>Seedance 2.0 固定区块标题（顺序即输出顺序）。</summary>
    public static readonly string[] Blocks =
    {
        "场景语境",
        "活跃参考",
        "场地映射",
        "首帧与空间调度",
        "光学镜头",
        "摄影机机位",
        "动作时间轴",
        "物理法则",
        "灯光",
        "音频",
        "正向约束",
    };

    public const string ChineseDirect = "中文直投";

    /// <summary>为指定镜头生成「Seedance 2.0 区块模板」system 指令（区块名列表）。</summary>
    public static string BuildSystem(ShotItem shot, string aspectRatio, NovelOutputLanguage lang)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是一名专业的 Seedance 2.0 视频提示词工程师。针对给定的单镜头设计，输出一条可直接投入 Seedance 2.0 的中文提示词。");
        sb.AppendLine();
        sb.AppendLine("必须严格按下述 11 个中文区块依次输出，不得增删区块、不得使用英文标题、不得与 H3 官方字段混写：");
        for (var i = 0; i < Blocks.Length; i++)
            sb.AppendLine($"  {i + 1}. {Blocks[i]}");
        sb.AppendLine();
        sb.AppendLine("硬性要求：");
        sb.AppendLine("1. 镜头总时长与画幅必须写入「场景语境」区块：本镜头时长 {0} 秒、画幅 {1}。".Replace("{0}", shot.Duration.ToString("0.#")).Replace("{1}", aspectRatio));
        sb.AppendLine("2. 「首帧与空间调度」必须给出完整、可测量、可独立理解的首帧物理描述（人物世界位置、画面位置、身体方向、视线、道具、光源），不得使用“上一镜头、继续、同上、和之前一样”等上下文依赖词。");
        sb.AppendLine("3. 「动作时间轴」使用阿拉伯数字时间码：起始时间 至 结束时间：内容，首尾连续并覆盖完整镜头，按剧情需要分长短，不机械等分。");
        sb.AppendLine("4. 对白使用用户在镜头设计中确认的准确原文，不得润色、压缩或翻译；标注说话者与口型。");
        sb.AppendLine("5. 只输出提示词正文，不要解释、不要思考过程。");
        sb.AppendLine("6. 输出语言（最高优先级）：除对白使用用户确认的原文外，提示词的全部正文必须使用{0}，不得使用其他语言。".Replace("{0}", NovelPromptEngineering.LangName(lang)));
        return sb.ToString();
    }
}
