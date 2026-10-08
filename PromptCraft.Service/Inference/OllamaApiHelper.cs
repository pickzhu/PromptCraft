using PromptCraft.Models.Inference;
using Ke.Bee.Localization.Localizer;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PromptCraft.Service.Inference;

/// <summary>
/// Ollama 原生 /api/chat 链路（对齐 prompt_master.js _ollamaNativeChat 551-617 + _resolveOllamaModelName 523-549）。
/// 关键差异：PromptMaster 对 Ollama 走 /api/chat + think:false（注释：避免 Qwen3.5 等在 /v1 下 content 为空），
/// 而不是 OpenAI 兼容 /v1/chat/completions —— 这正是「Local 返回为空」的根因（PromptCraft 旧链路走 /v1）。
/// </summary>
public static class OllamaApiHelper
{
    private static readonly HttpClient Shared = new()
    {
        Timeout = TimeSpan.FromSeconds(300),
    };

    /// <summary>测试钩子：生产为 null；测试注入 mock 传输层（对齐 OpenAiHttpHelper.SenderOverride）。</summary>
    internal static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? SenderOverride;

    private static Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        => SenderOverride != null ? SenderOverride(req, ct) : Shared.SendAsync(req, ct);

    /// <summary>判定服务商是否为 Ollama（对齐 PromptMaster provider==='ollama'；PromptCraft 无 kind 字段，按名/URL 判定）。</summary>
    public static bool IsOllama(string? providerName, string? baseUrl)
        => providerName?.Contains("ollama", StringComparison.OrdinalIgnoreCase) == true;

    public sealed record OllamaChatResult(string Text, string FinishReason, string? Error);

    /// <summary>
    /// POST {root}/api/chat（对齐 _ollamaNativeChat）。
    /// 无 images 时传 think:false；400 且无 images 时去掉 think 重试一次。
    /// </summary>
    public static async Task<OllamaChatResult> ChatAsync(
        string baseUrl, string apiKey, string model, IReadOnlyList<ChatMessage> messages,
        double temperature, double topP, int maxTokens, int timeoutSeconds, CancellationToken ct)
    {
        var root = ApiRoot(baseUrl);
        var resolvedModel = await ResolveOllamaModelNameAsync(model, root, ct);
        var (ollamaMessages, hasImages) = ToOllamaMessages(messages);

        var options = new Dictionary<string, object?>
        {
            ["temperature"] = temperature,
            ["top_p"] = topP,
            ["num_predict"] = maxTokens,
        };
        var payload = new Dictionary<string, object?>
        {
            ["model"] = resolvedModel,
            ["messages"] = ollamaMessages,
            ["stream"] = false,
            ["options"] = options,
        };
        if (!hasImages) payload["think"] = false;

        var (statusCode, body) = await PostAsync(root, payload, timeoutSeconds, ct);
        if (statusCode == 400 && !hasImages)
        {
            // 部分 Ollama / Qwen3.5 组合不认识 think 参数 → 去掉重试（对齐 591-597）
            payload.Remove("think");
            (statusCode, body) = await PostAsync(root, payload, timeoutSeconds, ct);
        }
        if (statusCode is < 200 or >= 300)
        {
            throw new InferencesException(InferenceErrorKind.NetworkError, ParseOllamaHttpError(statusCode, body));
        }

        using var doc = JsonDocument.Parse(body ?? "{}");
        var data = doc.RootElement;
        var message = data.TryGetProperty("message", out var m) ? m : default;
        var content = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? ""
            : "";
        var thinking = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("thinking", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? ""
            : "";
        var doneReason = data.TryGetProperty("done_reason", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString() ?? ""
            : "";

        var text = content.Trim();
        text = ExpandService.StripThinkingTags(text);
        if (string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(thinking))
        {
            text = ExpandService.StripThinkingTags(thinking);
        }
        if (string.IsNullOrEmpty(text))
        {
            var err = !string.IsNullOrEmpty(thinking)
                ? (doneReason == "length"
                    ? (Localizer.Instance?["OllamaThinkBudgetFull"] ?? "")
                    : (Localizer.Instance?["OllamaNoFinalText"] ?? ""))
                : (Localizer.Instance?["OllamaEmptyResponse"] ?? "");
            return new OllamaChatResult("", doneReason, err);
        }
        return new OllamaChatResult(text, doneReason, null);
    }

    /// <summary>Ollama 原生 API 根地址（去 /v1 后缀与尾斜杠，对齐 _ollamaApiRoot 352-357）。</summary>
    private static string ApiRoot(string baseUrl)
        => Regex.Replace((baseUrl ?? "").Trim(), @"/v1/?$", "", RegexOptions.IgnoreCase).TrimEnd('/');

    /// <summary>把 OpenAI 兼容消息转 Ollama 消息；多模态 ContentPart 的 image_url data URL 提取为 images base64 数组（对齐 images 字段）。</summary>
    private static (List<Dictionary<string, object?>> Messages, bool HasImages) ToOllamaMessages(IReadOnlyList<ChatMessage> messages)
    {
        var list = new List<Dictionary<string, object?>>();
        var hasImages = false;
        foreach (var m in messages ?? Array.Empty<ChatMessage>())
        {
            var msg = new Dictionary<string, object?> { ["role"] = m.Role };
            switch (m.Content)
            {
                case string s:
                    msg["content"] = s;
                    break;
                case List<ContentPart> parts:
                {
                    var sb = new StringBuilder();
                    var images = new List<string>();
                    foreach (var p in parts)
                    {
                        if (p.Type == "text" && !string.IsNullOrEmpty(p.Text))
                        {
                            sb.Append(p.Text);
                        }
                        else if (p.Type == "image_url" && p.ImageUrl?.Url is { } url)
                        {
                            var b64 = ExtractDataUrlBase64(url);
                            if (!string.IsNullOrEmpty(b64))
                            {
                                images.Add(b64);
                                hasImages = true;
                            }
                        }
                    }
                    msg["content"] = sb.ToString();
                    if (images.Count > 0) msg["images"] = images;
                    break;
                }
            }
            list.Add(msg);
        }
        return (list, hasImages);
    }

    /// <summary>从 data:image/...;base64,XXX 提取 base64 段。</summary>
    private static string? ExtractDataUrlBase64(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var idx = url.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? url[(idx + 7)..] : null;
    }

    /// <summary>POST 并返回 (状态码, 响应体)。超时/网络错误抛 InferencesException（对齐 _parseOllamaHttpError）。</summary>
    private static async Task<(int Status, string? Body)> PostAsync(
        string root, Dictionary<string, object?> payload, int timeoutSeconds, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, root + "/api/chat")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            var resp = await SendAsync(req, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            return ((int)resp.StatusCode, body);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new InferencesException(InferenceErrorKind.TimeoutError, string.Format(Localizer.Instance?["OllamaTimeoutFormat"] ?? "", timeoutSeconds));
        }
        catch (HttpRequestException ex)
        {
            throw new InferencesException(InferenceErrorKind.NetworkError, string.Format(Localizer.Instance?["OllamaConnectFailedFormat"] ?? "", root, ex.Message), ex);
        }
    }

    /// <summary>解析 Ollama HTTP 错误（对齐 _parseOllamaHttpError 271-288：error string / {message} / message）。</summary>
    private static string ParseOllamaHttpError(int status, string? body)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var err))
                {
                    if (err.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(err.GetString()))
                        return err.GetString()!;
                    if (err.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var key in new[] { "message", "code" })
                        {
                            if (err.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                                return v.GetString()!;
                        }
                    }
                }
                if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(msg.GetString()))
                    return msg.GetString()!;
            }
            catch
            {
                // 非 JSON 响应体（如 HTML）直接用截断文本
            }
        }
        var snippet = body != null && body.Length > 200 ? body[..200] + "…" : body ?? "";
        return string.Format(Localizer.Instance?["OllamaHttpFailedFormat"] ?? "", status, snippet);
    }

    /// <summary>模型名解析（对齐 _resolveOllamaModelName 523-549：GET /api/tags 精确→前缀匹配；失败用配置名）。</summary>
    internal static async Task<string> ResolveOllamaModelNameAsync(string name, string root, CancellationToken ct)
    {
        var want = (name ?? "").Trim();
        if (string.IsNullOrEmpty(want)) return want;
        List<string>? names = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, root + "/api/tags"), cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(cts.Token);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                {
                    names = models.EnumerateArray()
                        .Where(x => x.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetProperty("name").GetString() ?? "")
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToList();
                }
            }
        }
        catch
        {
            // 列举失败仍用配置名（对齐 545-547）
        }
        if (names == null || names.Count == 0) return want;
        if (names.Contains(want)) return want;
        var exactTag = names.FirstOrDefault(n => n.Split(':')[0] == want);
        if (exactTag != null) return exactTag;
        var prefixed = names.FirstOrDefault(n => n.StartsWith(want + ":", StringComparison.Ordinal));
        if (prefixed != null) return prefixed;
        return want;
    }
}
