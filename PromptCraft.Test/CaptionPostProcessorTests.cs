using PromptCraft.Service.Inference;

namespace PromptCraft.Test;

[TestClass]
public sealed class CaptionPostProcessorTests
{
    [TestMethod]
    public void TryExtractJsonBlob_ParsesBareJson()
    {
        var raw = "{\"tags\":\"1girl, solo, long hair\"}";
        var json = CaptionPostProcessor.TryExtractJsonBlob(raw);
        Assert.IsNotNull(json);
        StringAssert.Contains(json, "1girl");
    }

    [TestMethod]
    public void TryExtractJsonBlob_StripsMarkdownWrapper()
    {
        var raw = "好的，结果如下：\n```json\n{\"artist\":\"unknown\",\"tags\":\"1girl\"}\n```\n希望对你有帮助";
        var json = CaptionPostProcessor.TryExtractJsonBlob(raw);
        Assert.IsNotNull(json);
        StringAssert.Contains(json, "artist");
    }

    [TestMethod]
    public void TryExtractJsonBlob_HandlesBracesInStrings()
    {
        var raw = "前导废话 { \"a\": \"use {curly} in string\", \"b\": 1 } 尾巴";
        var json = CaptionPostProcessor.TryExtractJsonBlob(raw);
        Assert.IsNotNull(json);
        StringAssert.Contains(json, "curly");
    }

    [TestMethod]
    public void SanitizeProse_StripsMarkdown()
    {
        var raw = "# 标题\n## 小节\n**加粗** 和 *斜体*\n- 列表项\n- 另一项\n1. 序号\n正文继续";
        var clean = CaptionPostProcessor.SanitizeProse(raw);
        Assert.IsFalse(clean.Contains("#"));
        Assert.IsFalse(clean.Contains("**"));
        Assert.IsFalse(clean.Contains("- "));
    }
}
