using System.Text.RegularExpressions;
using PromptCraft.Models.Inference;

namespace PromptCraft.Service.Inference;

/// <summary>
/// 思考模型启发式（对齐 PromptMaster looksLikeThinkingModel）：
/// deepseek / qwen3 / glm-4.5+ / glm-5 / hunyuan / thinking 命名的模型默认带思考链，
/// 调用时透传 enable_thinking:false 关掉；如果服务商不认识该参数，OpenAiHttpHelper 会自动剥掉重试。
/// </summary>
public static partial class ThinkingModelHeuristic
{
    [GeneratedRegex(@"deepseek|qwen3|glm-4\.[5-9]|glm-5|hunyuan|thinking", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public static bool LooksLikeThinkingModel(string modelName)
        => Pattern().IsMatch(modelName ?? "");

    /// <summary>给 ChatCompletionRequest 注入 enable_thinking:false（仅思考模型）。</summary>
    public static void ApplyThinkingOff(ChatCompletionRequest req)
    {
        if (LooksLikeThinkingModel(req.Model))
        {
            req.ExtraBody["enable_thinking"] = false;
        }
    }
}
