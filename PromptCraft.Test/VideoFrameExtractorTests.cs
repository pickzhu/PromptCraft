using PromptCraft.Service.Inference;

namespace PromptCraft.Test;

// 视频抽帧链路验证：zhipu 判定 / MIME 推断（VideoFrameExtractor 依赖本机 ffmpeg，环境相关不在此断言）
[TestClass]
public sealed class VideoFrameExtractorTests
{
    [TestMethod]
    public void ZhipuProvider_Detected_ByName_BaseUrl()
    {
        Assert.IsTrue(ExpandService.IsZhipuProvider("zhipu", "https://open.bigmodel.cn/api/paas/v4"));
        Assert.IsTrue(ExpandService.IsZhipuProvider("智谱AI", "https://x"));
        Assert.IsTrue(ExpandService.IsZhipuProvider("OpenAI 兼容", "https://open.bigmodel.cn/api/paas/v4"));
        Assert.IsFalse(ExpandService.IsZhipuProvider("OpenAI", "https://api.openai.com/v1"));
        Assert.IsFalse(ExpandService.IsZhipuProvider(null, "https://api.deepseek.com"));
    }

    [TestMethod]
    public void VideoMime_By_Extension()
    {
        Assert.AreEqual("video/webm", ExpandService.VideoMimeForPath("a.webm"));
        Assert.AreEqual("video/quicktime", ExpandService.VideoMimeForPath("a.mov"));
        Assert.AreEqual("video/x-msvideo", ExpandService.VideoMimeForPath("a.avi"));
        Assert.AreEqual("video/x-matroska", ExpandService.VideoMimeForPath("a.mkv"));
        Assert.AreEqual("video/mp4", ExpandService.VideoMimeForPath("a.mp4"));
        Assert.AreEqual("video/mp4", ExpandService.VideoMimeForPath("a.unknown"));
    }
}
