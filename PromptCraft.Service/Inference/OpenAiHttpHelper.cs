using PromptCraft.Models.Inference;
using Ke.Bee.Localization.Localizer;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PromptCraft.Service.Inference;

/// <summary>
/// OpenAI 兼容 HTTP 统一调用层（T0.6，落地设计 §1.1）。
/// 静态工具类，不注册 DI。职责：URL 拼接 / Bearer 头 / JSON 序列化 /
/// 超时（纯文本 180s、含图 300s 可配）/ 429·5xx 指数退避重试（1s·2s×2）/ 错误归一化。
/// 不做：模型列表缓存、连接测试页、独立客户端接口。
/// </summary>
public static class OpenAiHttpHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    // 静态共享 HttpClient（无 IHttpClientFactory，对齐裸 DI 范式）。
    private static readonly HttpClient SharedClient = new()
    {
        // 实际超时由每次调用的 CancellationTokenSource 控制（180/300s），
        // 这里留一个兜底大数即可。
        Timeout = TimeSpan.FromMinutes(10),
    };

    /// <summary>
    /// 测试钩子：生产为 null；测试注入 mock 传输层，不走真实网络。
    /// </summary>
    internal static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? SenderOverride;

    private static Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        return SenderOverride != null
            ? SenderOverride(req, ct)
            : SharedClient.SendAsync(req, ct);
    }

    /// <summary>
    /// POST {baseUrl}/chat/completions。
    /// </summary>
    /// <param name="baseUrl">如 https://api.deepseek.com/v1（自动去尾斜杠后拼 /chat/completions）</param>
    /// <param name="apiKey">Bearer 密钥（可空，兼容 Ollama 本地）</param>
    /// <param name="req">请求体</param>
    /// <param name="timeoutSeconds">纯文本 180、含图/视频 300</param>
    /// <param name="ct">外部取消</param>
    public static async Task<ChatCompletionResponse> ChatAsync(
        string baseUrl, string apiKey, ChatCompletionRequest req,
        int timeoutSeconds, CancellationToken ct)
    {
        var url = CombineUrl(baseUrl, "chat/completions");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(req, JsonOptions), Encoding.UTF8, "application/json"),
            };
            ApplyAuth(request, apiKey);

            HttpResponseMessage resp;
            try
            {
                resp = await SendAsync(request, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new InferencesException(InferenceErrorKind.TimeoutError,
                    string.Format(Localizer.Instance?["RequestTimeout"] ?? "", timeoutSeconds));
            }
            catch (HttpRequestException ex)
            {
                throw new InferencesException(InferenceErrorKind.NetworkError,
                    string.Format(Localizer.Instance?["CannotConnect"] ?? "", baseUrl, ex.Message), ex);
            }

            using (resp)
            {
                var status = (int)resp.StatusCode;
                if (status is 401 or 403)
                {
                    throw new InferencesException(InferenceErrorKind.AuthError,
                        Localizer.Instance?["ApiKeyInvalid"] ?? "");
                }
                if (status == 404)
                {
                    throw new InferencesException(InferenceErrorKind.ModelNotFound,
                        string.Format(Localizer.Instance?["ModelNotFound"] ?? "", req.Model));
                }
                if (status == 400)
                {
                    // 400 可能是 ExtraBody 里有服务商不认识的字段（如 enable_thinking）。
                    // 若请求带 ExtraBody，剥掉重试一次；否则按网络错误抛出。
                    var body = await SafeReadBody(resp, cts.Token);
                    if (req.ExtraBody.Count > 0 && LooksLikeUnknownParamError(body))
                    {
                        req.ExtraBody.Clear();
                        attempt--; // 不占重试次数
                        continue;
                    }
                    throw new InferencesException(InferenceErrorKind.NetworkError,
                        string.Format(Localizer.Instance?["Http400Rejected"] ?? "", Truncate(body)));
                }
                if (status == 429 || status >= 500)
                {
                    if (attempt < maxAttempts)
                    {
                        // 1s / 2s 退避
                        await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
                        continue;
                    }
                    throw new InferencesException(InferenceErrorKind.ProviderBusy,
                        Localizer.Instance?["ProviderBusy"] ?? "");
                }
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await SafeReadBody(resp, cts.Token);
                    throw new InferencesException(InferenceErrorKind.NetworkError,
                        string.Format(Localizer.Instance?["UnknownHttpError"] ?? "", status, Truncate(body)));
                }

                var json = await resp.Content.ReadAsStringAsync(cts.Token);
                var result = JsonSerializer.Deserialize<ChatCompletionResponse>(json, JsonOptions)
                       ?? throw new InferencesException(InferenceErrorKind.NetworkError, Localizer.Instance?["EmptyResponse"] ?? "");

                // 智谱等 reasoning 模型把正文放在 message.reasoning_content 之外的 content；
                // 若 content 为空但 reasoning_content 有值，直接返回 reasoning_content 不现实（那是思考过程）。
                // 这里只做一个兜底：如果 content 为空但 choices 里有 message，把 reasoning_content 当正文返回。
                if (result.Choices.Count > 0)
                {
                    var m = result.Choices[0].Message;
                    if (m != null && string.IsNullOrEmpty(m.Content as string) && !string.IsNullOrEmpty(m.ReasoningContent))
                    {
                        m.Content = m.ReasoningContent;
                    }
                }
                return result;
            }
        }

        throw new InferencesException(InferenceErrorKind.ProviderBusy, Localizer.Instance?["ProviderBusy"] ?? "");
    }

    /// <summary>
    /// 判断 400 错误是不是"未知参数"类（硅基/DeepSeek 等不认识 enable_thinking 时会报这个）。
    /// 对齐 isUnknownChatParamError：错误消息里含 unknown / unexpected / unsupported parameter / extra field。
    /// </summary>
    private static bool LooksLikeUnknownParamError(string body)
    {
        if (string.IsNullOrEmpty(body)) return false;
        var b = body.ToLowerInvariant();
        return b.Contains("unknown parameter") || b.Contains("unexpected parameter")
            || b.Contains("unsupported parameter") || b.Contains("unknown field")
            || b.Contains("extra field") || b.Contains("unrecognized request")
            || b.Contains("enable_thinking");
    }

    /// <summary>
    /// 思考模型启发式（对齐 PromptMaster isUnknownChatParamError 契约）：
    /// 模型名命中 deepseek / qwen3 / glm-4.[5-9] / glm-5 / hunyuan / thinking 时，
    /// 第一次带 enable_thinking:false 调用；若 400 报未知参数，去掉 ExtraBody 重发一次。
    /// </summary>
    private static readonly Regex ThinkingModelRegex =
        new(@"deepseek|qwen3|glm-4\.[5-9]|glm-5|hunyuan|thinking", RegexOptions.IgnoreCase);

    public static bool LooksLikeThinkingModel(string model) =>
        !string.IsNullOrEmpty(model) && ThinkingModelRegex.IsMatch(model);

    /// <summary>
    /// 包装 ChatAsync：思考模型自动加 enable_thinking:false；400 未知参数错误去参重发一次。
    /// </summary>
    public static async Task<ChatCompletionResponse> ChatWithThinkingFallbackAsync(
        string baseUrl, string apiKey, ChatCompletionRequest req,
        int timeoutSeconds, CancellationToken ct)
    {
        if (LooksLikeThinkingModel(req.Model) && !req.ExtraBody.ContainsKey("enable_thinking"))
        {
            req.ExtraBody["enable_thinking"] = false;
            try
            {
                return await ChatAsync(baseUrl, apiKey, req, timeoutSeconds, ct);
            }
            catch (InferencesException ex) when (ex.Kind == InferenceErrorKind.NetworkError && IsUnknownParamError(ex.Message))
            {
                req.ExtraBody.Remove("enable_thinking");
            }
        }
        return await ChatAsync(baseUrl, apiKey, req, timeoutSeconds, ct);
    }

    private static bool IsUnknownParamError(string? msg)
    {
        if (string.IsNullOrEmpty(msg)) return false;
        return msg.Contains("unknown", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("unsupported parameter", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("unrecognized", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>GET {baseUrl}/models（连接测试 + 模型列表下拉）。</summary>
    public static async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(        string baseUrl, string apiKey, int timeoutSeconds, CancellationToken ct)
    {
        var url = CombineUrl(baseUrl, "models");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(request, apiKey);

        HttpResponseMessage resp;
        try
        {
            resp = await SendAsync(request, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new InferencesException(InferenceErrorKind.TimeoutError,
                string.Format(Localizer.Instance?["RequestTimeout"] ?? "", timeoutSeconds));
        }
        catch (HttpRequestException ex)
        {
            throw new InferencesException(InferenceErrorKind.NetworkError,
                string.Format(Localizer.Instance?["CannotConnect"] ?? "", baseUrl, ex.Message), ex);
        }

        using (resp)
        {
            var status = (int)resp.StatusCode;
            if (status is 401 or 403)
                throw new InferencesException(InferenceErrorKind.AuthError, Localizer.Instance?["ApiKeyInvalid"] ?? "");
            if (status == 404)
                throw new InferencesException(InferenceErrorKind.ModelNotFound, Localizer.Instance?["Models404"] ?? "");
            if (!resp.IsSuccessStatusCode)
                throw new InferencesException(InferenceErrorKind.ProviderBusy, string.Format(Localizer.Instance?["ConnectionTestFailed"] ?? "", status));

            var json = await resp.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            var result = new List<ModelInfo>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                    result.Add(ParseModel(el));
            }
            else if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in data.EnumerateArray())
                    result.Add(ParseModel(el));
            }
            return result;
        }
    }

    private static ModelInfo ParseModel(JsonElement el)
    {
        var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        string? ownedBy = el.TryGetProperty("owned_by", out var obEl) ? obEl.GetString() : null;
        return new ModelInfo(id, ownedBy);
    }

    private static void ApplyAuth(HttpRequestMessage request, string apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    private static string CombineUrl(string baseUrl, string path)
    {
        var trimmed = (baseUrl ?? "").Trim().TrimEnd('/');
        return $"{trimmed}/{path}";
    }

    private static async Task<string> SafeReadBody(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return ""; }
    }

    private static string Truncate(string s, int max = 200) =>
        s.Length <= max ? s : s[..max];
}
