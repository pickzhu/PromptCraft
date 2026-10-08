using PromptCraft.Models.Inference;
using PromptCraft.Service.Inference;
using System.Net;
using System.Text;

namespace PromptCraft.Test;

// Ollama 原生 /api/chat 链路迁移验证（prompt_master.js _ollamaNativeChat 551-617 + _extractOllamaMessageText 511-521）
[TestClass]
public sealed class OllamaApiHelperTests
{
    private static Task<HttpResponseMessage> MockResponse(HttpStatusCode status, string json)
        => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    private static ChatMessage Msg(string role, object? content) => new() { Role = role, Content = content };

    [TestMethod]
    public void IsOllama_Detects_ByName_And_Port()
    {
        Assert.IsTrue(OllamaApiHelper.IsOllama("Local", "http://127.0.0.1:11434"));
        Assert.IsTrue(OllamaApiHelper.IsOllama("Ollama", "http://localhost:11434"));
        Assert.IsTrue(OllamaApiHelper.IsOllama("本地模型", "http://127.0.0.1:11434/v1"));
        Assert.IsTrue(OllamaApiHelper.IsOllama("Anything", "http://192.168.1.5:11434"));
        Assert.IsFalse(OllamaApiHelper.IsOllama("智谱", "https://open.bigmodel.cn/api/paas/v4"));
        Assert.IsFalse(OllamaApiHelper.IsOllama("DeepSeek", "https://api.deepseek.com/v1"));
    }

    [TestMethod]
    public async Task ChatAsync_Returns_Content_And_Strips_ThinkTags()
    {
        // 对齐 _extractOllamaMessageText：content trim → stripThinkingTags（`</think>` 精确匹配，删 `<think>…</think>` 块）
        string? body = null;
        OllamaApiHelper.SenderOverride = (req, ct) =>
        {
            if (req.Method == HttpMethod.Get) return MockResponse(HttpStatusCode.InternalServerError, "{}");
            body = req.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult() ?? "";
            Assert.AreEqual("/api/chat", req.RequestUri?.AbsolutePath);
            Assert.IsTrue(body.Contains("\"think\":false"), "无图时必须带 think:false（对齐 576-578）");
            return MockResponse(HttpStatusCode.OK,
                """{"model":"qwen3:8b","message":{"role":"assistant","content":"<think>思考</think>扩写结果正文"},"done_reason":"stop"}""");
        };
        try
        {
            var r = await OllamaApiHelper.ChatAsync("http://127.0.0.1:11434", "", "qwen3:8b",
                new List<ChatMessage> { Msg("user", "扩写：女将军") }, 0.7, 0.9, 1024, 30, CancellationToken.None);
            Assert.IsNull(r.Error);
            Assert.AreEqual("扩写结果正文", r.Text); // `<think>…</think>` 被 StripThinkingTags 删掉
            Assert.AreEqual("stop", r.FinishReason);
        }
        finally { OllamaApiHelper.SenderOverride = null; }
    }

    [TestMethod]
    public async Task ChatAsync_ThinkingField_FallsBack_To_ThinkingText()
    {
        // 对齐 511-521：content 空但 message.thinking 有正文 → 用 thinking 当正文（不报错）
        OllamaApiHelper.SenderOverride = (req, ct) =>
        {
            if (req.Method == HttpMethod.Get) return MockResponse(HttpStatusCode.InternalServerError, "{}");
            return MockResponse(HttpStatusCode.OK,
                """{"model":"qwen3:8b","message":{"role":"assistant","content":"","thinking":"思考过程正文"},"done_reason":"stop"}""");
        };
        try
        {
            var r = await OllamaApiHelper.ChatAsync("http://127.0.0.1:11434", "", "qwen3:8b",
                new List<ChatMessage> { Msg("user", "扩写") }, 0.7, 0.9, 1024, 30, CancellationToken.None);
            Assert.IsNull(r.Error);
            Assert.AreEqual("思考过程正文", r.Text);
        }
        finally { OllamaApiHelper.SenderOverride = null; }
    }

    [TestMethod]
    public async Task ChatAsync_Empty_Content_And_Thinking_Returns_OllamaEmptyError()
    {
        // 对齐 604-614：content 与 thinking 都为空 → 「Ollama 返回为空」
        OllamaApiHelper.SenderOverride = (req, ct) =>
        {
            if (req.Method == HttpMethod.Get) return MockResponse(HttpStatusCode.InternalServerError, "{}");
            return MockResponse(HttpStatusCode.OK,
                """{"model":"qwen3:8b","message":{"role":"assistant","content":"","thinking":""},"done_reason":"stop"}""");
        };
        try
        {
            var r = await OllamaApiHelper.ChatAsync("http://127.0.0.1:11434", "", "qwen3:8b",
                new List<ChatMessage> { Msg("user", "扩写") }, 0.7, 0.9, 1024, 30, CancellationToken.None);
            Assert.IsNotNull(r.Error);
            StringAssert.Contains(r.Error, "Ollama 返回为空");
        }
        finally { OllamaApiHelper.SenderOverride = null; }
    }

    [TestMethod]
    public async Task ChatAsync_ThinkingOnly_Length_Returns_Specific_Error()
    {
        // 对齐 605-613：thinking 有值但 strip 后为空 + done_reason=length → 预算占满提示
        OllamaApiHelper.SenderOverride = (req, ct) =>
        {
            if (req.Method == HttpMethod.Get) return MockResponse(HttpStatusCode.InternalServerError, "{}");
            return MockResponse(HttpStatusCode.OK,
                """{"model":"qwen3:8b","message":{"role":"assistant","content":"","thinking":"<think>思考</think>"},"done_reason":"length"}""");
        };
        try
        {
            var r = await OllamaApiHelper.ChatAsync("http://127.0.0.1:11434", "", "qwen3:8b",
                new List<ChatMessage> { Msg("user", "扩写") }, 0.7, 0.9, 1024, 30, CancellationToken.None);
            Assert.IsNotNull(r.Error);
            StringAssert.Contains(r.Error, "模型输出预算被思考过程占满");
        }
        finally { OllamaApiHelper.SenderOverride = null; }
    }

    [TestMethod]
    public async Task ChatAsync_400_Retries_Without_Think()
    {
        var postCalls = 0;
        OllamaApiHelper.SenderOverride = (req, ct) =>
        {
            if (req.Method == HttpMethod.Get) return MockResponse(HttpStatusCode.InternalServerError, "{}");
            postCalls++;
            var body = req.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult() ?? "";
            if (postCalls == 1)
            {
                Assert.IsTrue(body.Contains("\"think\":false"));
                return MockResponse(HttpStatusCode.BadRequest, """{"error":"parameter think not supported"}""");
            }
            Assert.IsFalse(body.Contains("\"think\""), "第二次请求必须去掉 think，实际 body: " + body);
            return MockResponse(HttpStatusCode.OK, """{"model":"qwen3:8b","message":{"role":"assistant","content":"正文"},"done_reason":"stop"}""");
        };
        try
        {
            var r = await OllamaApiHelper.ChatAsync("http://127.0.0.1:11434", "", "qwen3:8b",
                new List<ChatMessage> { Msg("user", "扩写") }, 0.7, 0.9, 1024, 30, CancellationToken.None);
            Assert.AreEqual(2, postCalls);
            Assert.AreEqual("正文", r.Text);
        }
        finally { OllamaApiHelper.SenderOverride = null; }
    }

    [TestMethod]
    public async Task ChatAsync_Image_Messages_No_Think_And_Extracts_Images()
    {
        string? body = null;
        OllamaApiHelper.SenderOverride = (req, ct) =>
        {
            if (req.Method == HttpMethod.Get) return MockResponse(HttpStatusCode.InternalServerError, "{}");
            body = req.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult() ?? "";
            return MockResponse(HttpStatusCode.OK, """{"model":"qwen2.5vl:7b","message":{"role":"assistant","content":"图中是旗袍女性"},"done_reason":"stop"}""");
        };
        try
        {
            var r = await OllamaApiHelper.ChatAsync("http://127.0.0.1:11434", "", "qwen2.5vl:7b",
                new List<ChatMessage>
                {
                    Msg("user", new List<ContentPart>
                    {
                        new() { Type = "text", Text = "这是什么" },
                        new() { Type = "image_url", ImageUrl = new ImageUrlPart { Url = "data:image/jpeg;base64,QUJDREVGRw==" } },
                    }),
                }, 0.7, 0.9, 1024, 30, CancellationToken.None);
            Assert.IsNull(r.Error);
            Assert.AreEqual("图中是旗袍女性", r.Text);
            StringAssert.Contains(body!, "\"images\"");
            Assert.IsFalse(body!.Contains("\"think\""), "含图时不传 think（对齐 552-553/576-578）");
        }
        finally { OllamaApiHelper.SenderOverride = null; }
    }

    [TestMethod]
    public async Task ChatAsync_HttpError_Parses_Ollama_Error()
    {
        OllamaApiHelper.SenderOverride = (req, ct) =>
        {
            if (req.Method == HttpMethod.Get) return MockResponse(HttpStatusCode.InternalServerError, "{}");
            return MockResponse(HttpStatusCode.InternalServerError, """{"error":"model qwen3:8b not found"}""");
        };
        try
        {
            var ex = await Assert.ThrowsExceptionAsync<InferencesException>(() =>
                OllamaApiHelper.ChatAsync("http://127.0.0.1:11434", "", "qwen3:8b",
                    new List<ChatMessage> { Msg("user", "扩写") }, 0.7, 0.9, 1024, 30, CancellationToken.None));
            StringAssert.Contains(ex.Message, "not found");
        }
        finally { OllamaApiHelper.SenderOverride = null; }
    }

    [TestMethod]
    public async Task ResolveModelName_Tags_Prefix_Match_First_And_Fallback()
    {
        // 别名解析失败（tags 列举失败）→ 用配置名（对齐 545-547）
        OllamaApiHelper.SenderOverride = (req, ct) =>
        {
            if (req.Method == HttpMethod.Get) return MockResponse(HttpStatusCode.InternalServerError, "{}");
            return MockResponse(HttpStatusCode.OK, "{}");
        };
        try
        {
            var name = await OllamaApiHelper.ResolveOllamaModelNameAsync("qwen3:8b", "http://127.0.0.1:11434", CancellationToken.None);
            Assert.AreEqual("qwen3:8b", name);
        }
        finally { OllamaApiHelper.SenderOverride = null; }

        // 精确匹配 / 前缀匹配（对齐 534-543：find 返回第一个前缀匹配）
        OllamaApiHelper.SenderOverride = (req, ct) =>
        {
            if (req.Method == HttpMethod.Get) return MockResponse(HttpStatusCode.OK,
                """{"models":[{"name":"qwen3:8b"},{"name":"qwen2.5:7b"},{"name":"qwen3:14b"}]}""");
            return MockResponse(HttpStatusCode.OK, "{}");
        };
        try
        {
            Assert.AreEqual("qwen3:8b", await OllamaApiHelper.ResolveOllamaModelNameAsync("qwen3:8b", "http://127.0.0.1:11434", CancellationToken.None));
            // find 第一个前缀匹配：qwen3 → qwen3:8b（对齐 PromptMaster names.find）
            Assert.AreEqual("qwen3:8b", await OllamaApiHelper.ResolveOllamaModelNameAsync("qwen3", "http://127.0.0.1:11434", CancellationToken.None));
            // startsWith want+':' 无匹配 → 回退配置名
            Assert.AreEqual("qwen", await OllamaApiHelper.ResolveOllamaModelNameAsync("qwen", "http://127.0.0.1:11434", CancellationToken.None));
        }
        finally { OllamaApiHelper.SenderOverride = null; }
    }
}
