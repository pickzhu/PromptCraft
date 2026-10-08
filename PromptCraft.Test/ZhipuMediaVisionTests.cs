using PromptCraft.Service.Inference;

namespace PromptCraft.Test;

// zhipu 视觉预读迁移验证（prompt_master.js _zhipuDescribeMediaForExpand 相关纯逻辑）
[TestClass]
public sealed class ZhipuMediaVisionTests
{
    [TestMethod]
    public void ModelLooksVision_Matches_PromptMaster_Regex()
    {
        Assert.IsTrue(ZhipuMediaVision.ModelLooksVision("glm-4v"));
        Assert.IsTrue(ZhipuMediaVision.ModelLooksVision("glm-4.5v"));
        Assert.IsTrue(ZhipuMediaVision.ModelLooksVision("glm-4.6v"));
        Assert.IsTrue(ZhipuMediaVision.ModelLooksVision("glm-5v"));
        Assert.IsTrue(ZhipuMediaVision.ModelLooksVision("glm-4v-flash"));
        Assert.IsTrue(ZhipuMediaVision.ModelLooksVision("qwen-vl-max"));
        Assert.IsTrue(ZhipuMediaVision.ModelLooksVision("vision-pro"));
        Assert.IsFalse(ZhipuMediaVision.ModelLooksVision("glm-4-flash"));
        Assert.IsFalse(ZhipuMediaVision.ModelLooksVision("glm-4-flash-250414"));
        Assert.IsFalse(ZhipuMediaVision.ModelLooksVision(null));
    }

    [TestMethod]
    public void ClampMaxTokens_Caps_By_Model()
    {
        Assert.AreEqual(4096, ZhipuMediaVision.ClampMaxTokens("glm-4.5v", 9999));
        Assert.AreEqual(1024, ZhipuMediaVision.ClampMaxTokens("glm-4v", 9999));
        Assert.AreEqual(768, ZhipuMediaVision.ClampMaxTokens("glm-4v", 768));
        Assert.AreEqual(4096, ZhipuMediaVision.ClampMaxTokens("glm-4-flash", 9999));
        Assert.AreEqual(1, ZhipuMediaVision.ClampMaxTokens("glm-4v", 0));
    }

    [TestMethod]
    public void PayloadExtra_ThinkingDisabled_Except_ThinkingOnlyVision()
    {
        var m1 = ZhipuMediaVision.PayloadExtraForModel("glm-4v");
        Assert.IsNotNull(m1["thinking"]);
        var m2 = ZhipuMediaVision.PayloadExtraForModel("glm-4.5v");
        Assert.IsNotNull(m2["thinking"]);
        var m3 = ZhipuMediaVision.PayloadExtraForModel("glm-4.1v-thinking");
        Assert.IsFalse(m3.ContainsKey("thinking"));
    }

    [TestMethod]
    public void IsTrivialOutput_Detects_Empty_TokenOnly_Short()
    {
        Assert.IsTrue(ZhipuMediaVision.IsTrivialOutput(""));
        Assert.IsTrue(ZhipuMediaVision.IsTrivialOutput("   "));
        Assert.IsTrue(ZhipuMediaVision.IsTrivialOutput("<|assistant|>"));
        Assert.IsTrue(ZhipuMediaVision.IsTrivialOutput("好的"));
        Assert.IsFalse(ZhipuMediaVision.IsTrivialOutput("画面主体是一位古代女将军，身披金甲，背景是战场"));
    }

    [TestMethod]
    public void VisionModelCandidates_Dedup_ConfiguredFirst()
    {
        var list = ZhipuMediaVision.VisionModelCandidates("glm-4v-flash");
        Assert.AreEqual("glm-4v-flash", list[0]);
        Assert.AreEqual(3, list.Count); // glm-4v-flash 去重后剩 glm-4.5v/glm-4v
        var list2 = ZhipuMediaVision.VisionModelCandidates(null);
        Assert.AreEqual("glm-4.5v", list2[0]);
        Assert.AreEqual(3, list2.Count);
    }

    [TestMethod]
    public void TextAndVisionModel_Switch_Correctly()
    {
        Assert.AreEqual("glm-4-flash-250414", ZhipuMediaVision.TextExpandModel("glm-4v"));
        Assert.AreEqual("gpt-4o", ZhipuMediaVision.TextExpandModel("gpt-4o"));
        Assert.AreEqual("glm-4v-flash", ZhipuMediaVision.VisionFallbackModel("glm-4-flash"));
        Assert.AreEqual("glm-4v", ZhipuMediaVision.VisionFallbackModel("glm-4v"));
    }

    [TestMethod]
    public void MediaExpandImageOnly_Requires_All_Images_And_NonEmpty()
    {
        Assert.IsFalse(ZhipuMediaVision.MediaExpandImageOnly(new List<string>()));
        Assert.IsFalse(ZhipuMediaVision.MediaExpandImageOnly(new List<string> { "a.mp4" }));
        Assert.IsFalse(ZhipuMediaVision.MediaExpandImageOnly(new List<string> { "a.jpg", "a.mp4" }));
        Assert.IsTrue(ZhipuMediaVision.MediaExpandImageOnly(new List<string> { "a.jpg", "b.png" }));
    }
}
