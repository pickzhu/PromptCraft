using System.Net;
using System.Text;
using PromptCraft.Models.Inference;
using PromptCraft.Service.Inference;

namespace PromptCraft.Test;

[TestClass]
public sealed class OpenAiHttpHelperTests
{
    [TestCleanup]
    public void Cleanup()
    {
        OpenAiHttpHelper.SenderOverride = null;
    }

    private static HttpResponseMessage JsonResp(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string SampleChatResp = """
    {
      "id": "chatcmpl-xxx",
      "choices": [
        { "index": 0,
          "message": { "role": "assistant", "content": "一只橘猫趴在窗台上" },
          "finish_reason": "stop" }
      ]
    }
    """;

    [TestMethod]
    public async Task Chat_Text_EndToEnd_SerializesSnakeCase_AndParsesResponse()
    {
        string? seenUrl = null;
        string? seenAuth = null;
        string? seenBody = null;

        OpenAiHttpHelper.SenderOverride = (req, ct) =>
        {
            seenUrl = req.RequestUri!.ToString();
            seenAuth = req.Headers.Authorization?.ToString();
            seenBody = req.Content!.ReadAsStringAsync(ct).Result;
            return Task.FromResult(JsonResp(HttpStatusCode.OK, SampleChatResp));
        };

        var req = new ChatCompletionRequest
        {
            Model = "deepseek-chat",
            Messages =
            {
                new ChatMessage { Role = "system", Content = "你是扩写助手" },
                new ChatMessage { Role = "user", Content = "一只猫在窗台" },
            },
            Temperature = 0.7,
            TopP = 0.9,
            MaxTokens = 512,
        };

        var resp = await OpenAiHttpHelper.ChatAsync("https://api.deepseek.com/v1/", "sk-test", req, 180, default);

        Assert.AreEqual("https://api.deepseek.com/v1/chat/completions", seenUrl);
        Assert.AreEqual("Bearer sk-test", seenAuth);
        // snake_case 契约：max_tokens / top_p / finish_reason
        StringAssert.Contains(seenBody!, "\"max_tokens\":512");
        StringAssert.Contains(seenBody!, "\"top_p\":0.9");
        StringAssert.Contains(seenBody!, "\"stream\":false");
        // 解析响应
        Assert.AreEqual(1, resp.Choices.Count);
        Assert.AreEqual("stop", resp.Choices[0].FinishReason);
        Assert.AreEqual("一只橘猫趴在窗台上", resp.Choices[0].Message!.Content!.ToString());
    }

    [TestMethod]
    public async Task Chat_Visual_ContentArray_SerializesImageUrlPart()
    {
        string? seenBody = null;
        OpenAiHttpHelper.SenderOverride = (req, ct) =>
        {
            seenBody = req.Content!.ReadAsStringAsync(ct).Result;
            return Task.FromResult(JsonResp(HttpStatusCode.OK, SampleChatResp));
        };

        var req = new ChatCompletionRequest
        {
            Model = "glm-4v-plus",
            Messages =
            {
                new ChatMessage
                {
                    Role = "user",
                    Content = new List<ContentPart>
                    {
                        new() { Type = "text", Text = "# Captioning format" },
                        new() { Type = "image_url", ImageUrl = new ImageUrlPart { Url = "data:image/jpeg;base64,/9j/4AAQ" } },
                    },
                },
            },
            Temperature = 0.7,
            TopP = 0.9,
            MaxTokens = 1024,
        };

        await OpenAiHttpHelper.ChatAsync("https://open.bigmodel.cn/api/paas/v4", "k", req, 300, default);

        StringAssert.Contains(seenBody!, "\"type\":\"image_url\"");
        StringAssert.Contains(seenBody!, "\"image_url\":{\"url\":\"data:image/jpeg;base64,/9j/4AAQ\"}");
    }

    [TestMethod]
    public async Task Chat_401_ThrowsAuthError()
    {
        OpenAiHttpHelper.SenderOverride = (_, _) =>
            Task.FromResult(JsonResp(HttpStatusCode.Unauthorized, "{\"error\":\"bad key\"}"));

        var ex = await Assert.ThrowsExceptionAsync<InferencesException>(() =>
            OpenAiHttpHelper.ChatAsync("https://x/v1", "k", new ChatCompletionRequest { Model = "m" }, 180, default));
        Assert.AreEqual(InferenceErrorKind.AuthError, ex.Kind);
    }

    [TestMethod]
    public async Task Chat_404_ThrowsModelNotFound()
    {
        OpenAiHttpHelper.SenderOverride = (_, _) =>
            Task.FromResult(JsonResp(HttpStatusCode.NotFound, "{}"));

        var ex = await Assert.ThrowsExceptionAsync<InferencesException>(() =>
            OpenAiHttpHelper.ChatAsync("https://x/v1", "k", new ChatCompletionRequest { Model = "nope" }, 180, default));
        Assert.AreEqual(InferenceErrorKind.ModelNotFound, ex.Kind);
        StringAssert.Contains(ex.Message, "nope");
    }

    [TestMethod]
    public async Task Chat_429Once_Then200_RetriesOnceAndSucceeds()
    {
        var calls = 0;
        OpenAiHttpHelper.SenderOverride = (_, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? JsonResp((HttpStatusCode)429, "{}")
                : JsonResp(HttpStatusCode.OK, SampleChatResp));
        };

        var resp = await OpenAiHttpHelper.ChatAsync("https://x/v1", "k", new ChatCompletionRequest { Model = "m" }, 180, default);
        Assert.AreEqual(2, calls);
        Assert.AreEqual("stop", resp.Choices[0].FinishReason);
    }

    [TestMethod]
    public async Task Chat_5xxThreeTimes_ThrowsProviderBusy()
    {
        var calls = 0;
        OpenAiHttpHelper.SenderOverride = (_, _) =>
        {
            calls++;
            return Task.FromResult(JsonResp(HttpStatusCode.InternalServerError, "{}"));
        };

        var ex = await Assert.ThrowsExceptionAsync<InferencesException>(() =>
            OpenAiHttpHelper.ChatAsync("https://x/v1", "k", new ChatCompletionRequest { Model = "m" }, 180, default));
        Assert.AreEqual(InferenceErrorKind.ProviderBusy, ex.Kind);
        Assert.AreEqual(3, calls);
    }

    [TestMethod]
    public async Task Chat_NetworkError_MapsToNetworkError()
    {
        OpenAiHttpHelper.SenderOverride = (_, _) =>
            throw new HttpRequestException("connection refused");

        var ex = await Assert.ThrowsExceptionAsync<InferencesException>(() =>
            OpenAiHttpHelper.ChatAsync("https://x/v1", "k", new ChatCompletionRequest { Model = "m" }, 180, default));
        Assert.AreEqual(InferenceErrorKind.NetworkError, ex.Kind);
    }

    [TestMethod]
    public async Task GetModels_ParsesDataArray()
    {
        OpenAiHttpHelper.SenderOverride = (_, _) =>
            Task.FromResult(JsonResp(HttpStatusCode.OK, """
            { "object":"list",
              "data": [
                {"id":"deepseek-chat","owned_by":"deepseek"},
                {"id":"deepseek-reasoner"}
              ] }
            """));

        var models = await OpenAiHttpHelper.GetModelsAsync("https://api.deepseek.com/v1", "sk", 30, default);
        Assert.AreEqual(2, models.Count);
        Assert.AreEqual("deepseek-chat", models[0].Id);
        Assert.AreEqual("deepseek", models[0].OwnedBy);
        Assert.IsNull(models[1].OwnedBy);
    }
}
