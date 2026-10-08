using PromptCraft.Models.Inference;
using PromptCraft.Service.Inference.Minimax;
using System.Text.RegularExpressions;

namespace PromptCraft.Service.Inference;

/// <summary>
/// 智谱媒体扩写视觉预读（对齐 prompt_master.js _zhipuDescribeMediaForExpand :797-891、
/// _zhipuPostVisionDescribe :769-795、zhipuChatText.js isTrivialZhipuOutput / zhipuChatPayloadExtraForModel）。
/// 仅 zhipu provider + 媒体型 + 纯图片时启用：先用视觉模型把参考图描述一遍，
/// 再切换文本模型写作（避免 VLM 1024 token 上限与长 system 冲突）。
/// </summary>
public static class ZhipuMediaVision
{
    /// <summary>对齐 _zhipuModelLooksVision（4v / 4.5v / 4.6v / 5v / 6v / vision / vl）。</summary>
    public static bool ModelLooksVision(string? model)
        => model != null
           && Regex.IsMatch(model, "4v|4\\.5v|4\\.6v|5v|6v|vision|(?:^|[-_.])vl(?:[-_.]|$)", RegexOptions.IgnoreCase);

    /// <summary>对齐 _zhipuMaxTokensCap + _zhipuClampMaxTokens。</summary>
    public static int ClampMaxTokens(string? model, int maxTokens)
    {
        var m = (model ?? "").ToLowerInvariant();
        int cap;
        if (Regex.IsMatch(m, "4\\.5v|4\\.6v|4\\.7v|4\\.1v-thinking|4v-plus")) cap = 4096;
        else if (ModelLooksVision(m)) cap = 1024;
        else cap = 4096;
        return Math.Min(Math.Max(1, maxTokens), cap);
    }

    /// <summary>对齐 zhipuChatPayloadExtraForModel：纯思考型视觉模型不强制关思考，其余 thinking disabled。</summary>
    public static Dictionary<string, object?> PayloadExtraForModel(string? model)
    {
        var m = (model ?? "").ToLowerInvariant();
        var thinkingOnly = Regex.IsMatch(m, "4\\.1v-thinking|(?:^|[-_.])z1(?:[-_.]|$)")
            && !Regex.IsMatch(m, "4\\.5v|4\\.6v|4\\.7v");
        if (thinkingOnly) return new Dictionary<string, object?>();
        return new Dictionary<string, object?>
        {
            ["thinking"] = new Dictionary<string, object?> { ["type"] = "disabled" },
        };
    }

    /// <summary>对齐 isTrivialZhipuOutput（剥 artifacts 后为空 / 纯 token / 长度&lt;12）。</summary>
    public static bool IsTrivialOutput(string? text)
    {
        var s = ExpandService.StripThinkingTags(text ?? "");
        if (string.IsNullOrEmpty(s)) return true;
        if (Regex.IsMatch((text ?? "").Trim(), @"^<\|[^|]+\|>$", RegexOptions.IgnoreCase)) return true;
        return s.Length < 12;
    }

    /// <summary>对齐 _zhipuVisionModelCandidates（configured 优先 + glm-4.5v/glm-4v/glm-4v-flash 去重）。</summary>
    public static List<string> VisionModelCandidates(string? configured)
    {
        var list = new[] { configured ?? "", "glm-4.5v", "glm-4v", "glm-4v-flash" };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outList = new List<string>();
        foreach (var m in list)
        {
            var key = (m ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(key) || !seen.Add(key)) continue;
            outList.Add(m?.Trim() ?? "");
        }
        return outList;
    }

    /// <summary>对齐 _zhipuTextExpandModel：当前模型是视觉 → glm-4-flash-250414，否则保持。</summary>
    public static string TextExpandModel(string? currentModel)
        => ModelLooksVision(currentModel) ? "glm-4-flash-250414" : (currentModel ?? "glm-4-flash-250414");

    /// <summary>对齐 _zhipuReverseModel 兜底：当前模型非视觉 → glm-4v-flash。</summary>
    public static string VisionFallbackModel(string? currentModel)
        => ModelLooksVision(currentModel) ? (currentModel ?? "glm-4v-flash") : "glm-4v-flash";

    /// <summary>纯图片判定（对齐 _zhipuMediaExpandImageOnly）。</summary>
    public static bool MediaExpandImageOnly(IReadOnlyList<string> mediaPaths)
    {
        var items = ExpandMedia.EnumerateTaggedMedia(mediaPaths);
        return items.Count > 0 && items.All(x => x.Kind == "image");
    }

    /// <summary>
    /// 视觉预读（对齐 _zhipuDescribeMediaForExpand）：返回图片描述文本（batch 或逐图拼 lines.join('\n\n')）；
    /// 无图片 / 全部模型失败 → null。
    /// </summary>
    public static async Task<string?> DescribeMediaForExpandAsync(
        string baseUrl, string apiKey, IReadOnlyList<string> mediaPaths,
        string outputLang, string? configuredModel, CancellationToken ct)
    {
        var lang = string.Equals(outputLang, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";
        var items = ExpandMedia.EnumerateTaggedMedia(mediaPaths)
            .Where(x => x.Kind == "image" && File.Exists(x.Path))
            .ToList();
        if (items.Count == 0) return null;

        var system = lang == "zh"
            ? "你是视觉描述助手。只描述图片中看得见的内容，输出简洁、具体、可用于提示词写作。"
            : "You describe images for prompt writing. Visible facts only; concise and specific.";
        var batchPrompt = lang == "zh"
            ? "请逐条描述下列参考图片的可见细节（人物/服装/材质/姿态/光线/背景）。每条必须以对应的 <Picture N>: 开头，用简体中文，只写看得见的内容，不要编故事。"
            : "Describe each reference image. Start each block with <Picture N>:. English only; visible facts.";
        string OnePrompt(string tag) => lang == "zh"
            ? $"请描述这张参考图 {tag} 的可见细节（人物/服装/材质/姿态/光线/背景）。以「{tag}:」开头，用简体中文，只写看得见的内容。"
            : $"Describe reference image {tag}. Start with \"{tag}:\". English only; visible facts.";

        foreach (var model in VisionModelCandidates(configuredModel))
        {
            if (!ModelLooksVision(model)) continue;

            // batch：多图一次送（对齐 _buildMediaExpandMultimodalUser zhipu 分支）
            try
            {
                var mm = await ExpandService.BuildMediaExpandMultimodalUserAsync(
                    batchPrompt, items.Select(x => x.Path).ToList(), "zhipu", baseUrl);
                if (mm.Mode == "openai")
                {
                    var batchOut = await PostVisionDescribeAsync(
                        baseUrl, apiKey, model, system, mm.Content!, 768, ct);
                    if (!IsTrivialOutput(batchOut)) return batchOut;
                }
            }
            catch
            {
                // 尝试逐图 / 下一模型
            }

            // 逐图
            var lines = new List<string>();
            foreach (var item in items)
            {
                try
                {
                    var oneContent = new List<ContentPart>
                    {
                        new() { Type = "text", Text = OnePrompt(item.Tag) },
                        new() { Type = "image_url", ImageUrl = new ImageUrlPart { Url = (MediaVision.PrepareVisionImageDataUrl(item.Path) ?? "") } },
                    };
                    var oneOut = await PostVisionDescribeAsync(
                        baseUrl, apiKey, model, system, oneContent, 512, ct);
                    if (!IsTrivialOutput(oneOut)) lines.Add(oneOut);
                }
                catch
                {
                    // 下一张
                }
            }
            if (lines.Count >= Math.Min(items.Count, 1))
                return string.Join("\n\n", lines);
        }
        return null;
    }

    /// <summary>对齐 _zhipuPostVisionDescribe：POST /chat/completions，temp 0.2 / top_p 0.8 / max_tokens clamp / thinking 控制，180s 超时。</summary>
    private static async Task<string> PostVisionDescribeAsync(
        string baseUrl, string apiKey, string model, string system,
        object userContent, int maxTokens, CancellationToken ct)
    {
        var req = new ChatCompletionRequest
        {
            Model = model,
            Temperature = 0.2,
            TopP = 0.8,
            MaxTokens = ClampMaxTokens(model, maxTokens),
            Messages =
            {
                new ChatMessage { Role = "system", Content = system },
                new ChatMessage { Role = "user", Content = userContent },
            },
        };
        foreach (var (k, v) in PayloadExtraForModel(model)) req.ExtraBody[k] = v;

        var resp = await OpenAiHttpHelper.ChatAsync(baseUrl, apiKey, req, 180, ct);
        if (resp.Choices.Count == 0) return "";
        var msg = resp.Choices[0].Message;
        var text = msg?.Content as string
                   ?? (msg?.Content as List<ContentPart>)?.FirstOrDefault(x => x.Type == "text")?.Text
                   ?? "";
        if (string.IsNullOrEmpty(text) && msg?.ReasoningContent != null) text = msg.ReasoningContent;
        return ExpandService.StripThinkingTags(text);
    }
}
