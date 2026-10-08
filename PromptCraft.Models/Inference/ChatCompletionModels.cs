using System.Text.Json.Serialization;

namespace PromptCraft.Models.Inference;

// ============================================================
// OpenAI 兼容 Chat Completions 契约（T0.6，落地设计 §1.1 / 附录 C）
// 命名策略：snake_case（max_tokens / top_p / finish_reason / image_url / owned_by）
// ============================================================

/// <summary>Chat Completions 请求体。</summary>
public sealed class ChatCompletionRequest
{
    public string Model { get; set; } = "";
    public List<ChatMessage> Messages { get; set; } = new();
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? MaxTokens { get; set; }
    public bool Stream { get; set; } = false;

    /// <summary>
    /// Provider 特有字段透传（如 { "enable_thinking": false } / { "thinking": { "type": "disabled" } }）。
    /// [JsonExtensionData] 会平铺到请求体顶层。
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object?> ExtraBody { get; set; } = new();
}

public sealed class ChatMessage
{
    public string Role { get; set; } = "user";

    /// <summary>文本请求为 string；视觉请求为 List&lt;ContentPart&gt;。响应侧可能是 string 或 [{type:text,text}]。</summary>
    public object? Content { get; set; }

    /// <summary>响应侧兜底字段（智谱等 reasoning_content）。</summary>
    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; set; }
}

/// <summary>视觉消息内容片段。</summary>
public sealed class ContentPart
{
    public string Type { get; set; } = "text"; // text | image_url | video_url
    public string? Text { get; set; }
    [JsonPropertyName("image_url")]
    public ImageUrlPart? ImageUrl { get; set; }
    [JsonPropertyName("video_url")]
    public VideoUrlPart? VideoUrl { get; set; }
}

public sealed class ImageUrlPart
{
    /// <summary>形如 "data:image/jpeg;base64,&lt;b64&gt;"。</summary>
    public string Url { get; set; } = "";
}

/// <summary>智谱 video_url 片段（对齐 _buildMediaExpandMultimodalUser zhipu 视频内联分支）。</summary>
public sealed class VideoUrlPart
{
    /// <summary>形如 "data:video/mp4;base64,&lt;b64&gt;"。</summary>
    public string Url { get; set; } = "";
}

/// <summary>Chat Completions 响应体。</summary>
public sealed class ChatCompletionResponse
{
    public string? Id { get; set; }
    public List<Choice> Choices { get; set; } = new();
}

public sealed class Choice
{
    public ChatMessage? Message { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }

    /// <summary>兼容 choice.text（旧协议）。</summary>
    public string? Text { get; set; }
}

/// <summary>GET /models 返回的单个模型。</summary>
public sealed record ModelInfo(string Id, string? OwnedBy);

/// <summary>归一化错误类别（UI 直接按 Kind 提示）。</summary>
public enum InferenceErrorKind
{
    AuthError,
    ModelNotFound,
    ProviderBusy,
    TimeoutError,
    NetworkError,
    NoVisionProvider,
    NoSuchProvider,
    /// <summary>参数不合法（如输入为空）。</summary>
    InvalidArgument,
    /// <summary>功能未实现/暂不支持。</summary>
    NotSupported,
}

/// <summary>归一化推理异常，Message 为用户可读中文提示。</summary>
public sealed class InferencesException : Exception
{
    public InferenceErrorKind Kind { get; }

    public InferencesException(InferenceErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }
}
