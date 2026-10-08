using System.Text;
using PromptCraft.Models.Inference.Novel;

namespace PromptCraft.Service.Inference.Novel;

/// <summary>
/// MiniMax H3 提示词组装：中文直投（自然中文段落）。
/// 六段式通用提示词（Minimax六段式通用提示词）与 连续剧情（导演台）统一走
/// <see cref="PromptCraft.Service.Inference.Minimax.MiniMaxAssembler"/>（与扩写页同一套场景组装，
/// 保证输出格式与扩写页对应选项完全一致）。
/// </summary>
public static class H3PromptBuilder
{
    /// <summary>为指定镜头生成 H3「中文直投」的 system 指令。</summary>
    public static string BuildSystem(ShotItem shot, string aspectRatio, NovelOutputLanguage lang)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是一名专业的 MiniMax H3 视频提示词工程师。针对给定的单镜头设计，输出一条可直接投入 MiniMax H3 的提示词。");
        sb.AppendLine();
        sb.AppendLine("硬性要求：");
        sb.AppendLine("1. 只输出提示词正文，不要解释、不要思考过程、不要 Markdown 小标题。");
        sb.AppendLine("2. 对白使用用户确认的准确原文，不得翻译、润色或压缩；说话者用稳定 (S1)/(S2) 标记，对白格式 <d>[Chinese] 准确台词</d>。");
        sb.AppendLine("3. 禁止“上一镜头、继续、同上、仍然、保持原样、和之前一样”等上下文依赖词；每条提示词必须能脱离聊天独立理解，首帧给出完整物理描述。");
        sb.AppendLine("4. 镜头总时长 {0} 秒、画幅 {1}。".Replace("{0}", shot.Duration.ToString("0.#")).Replace("{1}", aspectRatio));
        sb.AppendLine("5. 输出语言（最高优先级）：除对白使用用户确认的原文外，提示词的全部正文必须使用{0}，不得使用其他语言。".Replace("{0}", NovelPromptEngineering.LangName(lang)));
        sb.AppendLine();
        sb.AppendLine("输出采用「中文直投」：写一条可直接复制的自然中文提示词，按顺序写清——1. 镜头总时长与画幅；2. 首帧构图、人物站位、场景和光线；3. 按时间推进的动作、摄影机、表演与准确对白；4. 环境声、动作声和配乐要求；5. 角色、道具、轴线、口型和首尾帧连续性限制。");
        return sb.ToString();
    }
}
