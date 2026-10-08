using PromptCraft.Models.Inference;
using PromptCraft.Service.Inference;

namespace PromptCraft.Test;

[TestClass]
public sealed class ThinkingModelHeuristicTests
{
    [DataRow("deepseek-chat", true)]
    [DataRow("DeepSeek-R1", true)]
    [DataRow("qwen3-72b", true)]
    [DataRow("glm-4.6", true)]
    [DataRow("glm-4.5", true)]
    [DataRow("glm-5-air", true)]
    [DataRow("hunyuan-pro", true)]
    [DataRow("o1-thinking-preview", true)]
    [DataRow("gpt-4o", false)]
    [DataRow("glm-4-air", false)]   // 4.x 不带点小版本号
    [DataRow("qwen2.5-72b", false)]
    [DataRow("", false)]
    [DataRow(null, false)]
    [DataTestMethod]
    public void LooksLikeThinkingModel_Detects(string? name, bool expected)
    {
        Assert.AreEqual(expected, ThinkingModelHeuristic.LooksLikeThinkingModel(name!));
    }

    [TestMethod]
    public void ApplyThinkingOff_InjectsExtraBody_OnlyForThinking()
    {
        var thinking = new ChatCompletionRequest { Model = "deepseek-chat" };
        ThinkingModelHeuristic.ApplyThinkingOff(thinking);
        Assert.IsTrue(thinking.ExtraBody.ContainsKey("enable_thinking"));

        var normal = new ChatCompletionRequest { Model = "gpt-4o" };
        ThinkingModelHeuristic.ApplyThinkingOff(normal);
        Assert.IsFalse(normal.ExtraBody.ContainsKey("enable_thinking"));
    }
}
