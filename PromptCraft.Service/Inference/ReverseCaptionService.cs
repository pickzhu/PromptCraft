using PromptCraft.Interfaces;
using PromptCraft.Models;
using PromptCraft.Models.Inference;
using PromptCraft.Service.Inference.Reverse;
using Ke.Bee.Localization.Localizer;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace PromptCraft.Service.Inference;

/// <summary>
/// 反推实现（T2.2 重写版）：完整承载 PromptMaster 提示词工程体系。
/// 流程：解析模型家族 → 应用能力矩阵 → 组装提示词（torii 走原生模板，其余走标准 PE）→ API 调用 → 清洗 → sidecar → 日志。
/// </summary>
public sealed class ReverseCaptionService : IReverseCaptionService
{
    private readonly IServiceProvider _services;
    private readonly IBaseLogService _log;

    public ReverseCaptionService(IServiceProvider services, IBaseLogService? log = null)
    {
        _services = services;
        _log = log ?? PromptCraft.Service.LogService.Instance;
    }

    public Task<string> CaptionAsync(ReverseCaptionRequest req, CancellationToken ct)
        => CaptionAsync(req, null, null, ct);

    public Task<string> CaptionAsync(ReverseCaptionRequest req, ProviderConfig? provider, ProviderModel? model, CancellationToken ct)
        => CaptionCoreAsync(req, provider, model, ct);

    public async Task<IReadOnlyList<ReverseCaptionResult>> CaptionBatchAsync(
        IReadOnlyList<ReverseCaptionRequest> reqs,
        CancellationToken ct,
        IProgress<ReverseProgressEvent>? progress = null)
        => await CaptionBatchAsync(reqs, null, null, ct, progress);

    public async Task<IReadOnlyList<ReverseCaptionResult>> CaptionBatchAsync(
        IReadOnlyList<ReverseCaptionRequest> reqs,
        ProviderConfig? provider,
        ProviderModel? model,
        CancellationToken ct,
        IProgress<ReverseProgressEvent>? progress = null)
    {
        var results = new List<ReverseCaptionResult>();
        var total = reqs.Count;
        for (var i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var req = reqs[i];
            var mediaPath = req.MediaPath ?? req.ImageUrl ?? $"item {i + 1}";
            progress?.Report(new ReverseProgressEvent { Index = i, Total = total, MediaPath = mediaPath, Status = "processing" });
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var text = await CaptionCoreAsync(req, provider, model, ct);
                sw.Stop();
                var sidecar = req.WriteCaptionSidecar ? WriteSidecar(req, text) : null;
                results.Add(new ReverseCaptionResult
                {
                    MediaPath = mediaPath,
                    Caption = text,
                    Success = true,
                    DurationSec = sw.Elapsed.TotalSeconds,
                    SidecarPath = sidecar,
                });
                progress?.Report(new ReverseProgressEvent { Index = i, Total = total, MediaPath = mediaPath, Status = "done", Caption = text });
            }
            catch (Exception ex)
            {
                sw.Stop();
                _log.Warn(string.Format(Localizer.Instance?["ReverseBatchItemFailed"] ?? "反推失败: {0}", ex.Message), "ReverseCaption", ex);
                results.Add(new ReverseCaptionResult
                {
                    MediaPath = mediaPath,
                    Success = false,
                    Error = ex.Message,
                    DurationSec = sw.Elapsed.TotalSeconds,
                });
                progress?.Report(new ReverseProgressEvent { Index = i, Total = total, MediaPath = mediaPath, Status = "error", Error = ex.Message });
            }
        }
        return results;
    }

    // ==================== 核心流程 ====================

    private async Task<string> CaptionCoreAsync(ReverseCaptionRequest req, ProviderConfig? provider, ProviderModel? model, CancellationToken ct)
    {
        if (req == null)
            throw new ArgumentNullException(nameof(req));
        var mediaPath = req.MediaPath ?? req.ImageUrl;
        if (string.IsNullOrWhiteSpace(mediaPath))
            throw new ArgumentException(Localizer.Instance?["ReverseNoAssetError"] ?? "");

        // 1. 提供商选择
        var providerSvc = _services.GetRequiredService<IProviderService>();
        if (provider == null || model == null)
        {
            var pick = await providerSvc.GetDefaultReverseAsync(ct);
            if (pick == null)
            {
                throw new InferencesException(InferenceErrorKind.NoVisionProvider,
                    Localizer.Instance?["NoVisionModelConfigured"] ?? "");
            }
            (provider, model) = pick.Value;
        }

        var captionModel = req.CaptionModel ?? model.ModelName;
        var family = CaptionModels.InferCaptionModelFamily(captionModel);
        _log.Info(string.Format(Localizer.Instance?["ReverseStartLogFormat"] ?? "", captionModel, family, mediaPath, req.MediaTarget ?? "image", req.PeId ?? req.Type ?? "-"), "ReverseCaption");

        // 对齐 joycaption.js startPromptMasterBatchReverse（1631-1653）三段媒体/模型校验
        var mediaTarget = req.MediaTarget ?? "image";
        if ((mediaTarget == "video" || mediaTarget == "mixed") && family == CaptionModelFamily.Joycaption)
        {
            _log.Warn(string.Format(Localizer.Instance?["ReverseRejectJoyCaptionFormat"] ?? "", mediaPath, captionModel), "ReverseCaption");
            throw new InferencesException(InferenceErrorKind.InvalidArgument,
                Localizer.Instance?["ReverseJoyCaptionHint"] ?? "");
        }
        if (!CaptionModelCapabilityProvider.IsMediaTargetAllowed(captionModel, mediaTarget))
        {
            _log.Warn(string.Format(Localizer.Instance?["ReverseRejectMediaFormat"] ?? "", mediaPath, mediaTarget), "ReverseCaption");
            throw new InferencesException(InferenceErrorKind.InvalidArgument,
                Localizer.Instance?["ReverseMediaUnsupported"] ?? "");
        }
        if (family == CaptionModelFamily.Joycaption)
        {
            _log.Warn(string.Format(Localizer.Instance?["ReverseRejectDisabledFormat"] ?? "", mediaPath, captionModel), "ReverseCaption");
            throw new InferencesException(InferenceErrorKind.InvalidArgument,
                Localizer.Instance?["ReverseJoyCaptionDisabled"] ?? "");
        }

        // 2. 模型能力：锁定语言 / 默认采样
        CaptionModelCapabilityProvider.ApplyModelCapabilitiesToCaption(req, captionModel,
            hasExplicitTemperature: req.Temperature != null,
            hasExplicitTopP: req.TopP != null);

        // 3. 组装提示词
        var prompts = BuildPrompts(req, family);

        // 4. max tokens
        var maxTokens = req.MaxNewTokens ?? CaptionPromptBlocks.ResolveMaxNewTokens(req, null);

        // 5. API 调用（对齐 prompt_master.js _callOpenAiCompatMultimodalCaption：
        //    本地素材读为 data:{mime};base64 内联传给 OpenAI 兼容接口，而不是直传本地路径）
        var mediaKind = IsMediaVideo(mediaPath) ? "video" : "image";
        if (!File.Exists(mediaPath))
        {
            _log.Warn(string.Format(Localizer.Instance?["ReverseAssetMissingLog"] ?? "", mediaPath), "ReverseCaption");
            throw new InferencesException(InferenceErrorKind.InvalidArgument, string.Format(Localizer.Instance?["ReverseAssetMissingFormat"] ?? "", mediaPath));
        }
        var mediaBytes = await File.ReadAllBytesAsync(mediaPath, ct);
        var mediaBase64 = Convert.ToBase64String(mediaBytes);
        var mime = mediaKind == "video" ? VideoMimeForPath(mediaPath) : ImageMimeForPath(mediaPath);
        var dataUrl = $"data:{mime};base64,{mediaBase64}";
        var mediaPart = mediaKind == "video"
            ? new ContentPart { Type = "video_url", VideoUrl = new VideoUrlPart { Url = dataUrl } }
            : new ContentPart { Type = "image_url", ImageUrl = new ImageUrlPart { Url = dataUrl } };

        var body = new ChatCompletionRequest
        {
            Model = model.ModelName,
            Temperature = req.Temperature ?? 0.7,
            TopP = req.TopP ?? 0.8,
            MaxTokens = maxTokens,
            Messages =
            {
                new ChatMessage { Role = "system", Content = prompts.SystemPrompt },
                new ChatMessage
                {
                    Role = "user",
                    Content = new List<ContentPart>
                    {
                        new() { Type = "text", Text = prompts.UserPrompt },
                        mediaPart,
                    },
                },
            },
        };
        ThinkingModelHeuristic.ApplyThinkingOff(body);

        var resp = await OpenAiHttpHelper.ChatAsync(provider.BaseUrl, provider.ApiKey, body, 300, ct);
        var text = resp.Choices.Count > 0 ? resp.Choices[0].Message?.Content?.ToString() ?? "" : "";
        text = ExpandService.StripThinkingTags(text);

        // 6. 清洗
        text = SanitizeOutput(text, req, family);

        _log.Info(string.Format(Localizer.Instance?["ReverseSucceeded"] ?? "反推完成: {0}", mediaPath), "ReverseCaption");
        return text;
    }

    /// <summary>素材是否为视频（对齐前端 isVideo 扩展名判断）。</summary>
    private static bool IsMediaVideo(string? filePath)
    {
        var ext = Path.GetExtension(filePath ?? "").ToLowerInvariant();
        return ext is ".mp4" or ".mov" or ".avi" or ".webm" or ".mkv" or ".m4v" or ".wmv" or ".flv" or ".ts";
    }

    /// <summary>图片 MIME（对齐 prompt_master.js _imageMimeForPath）。</summary>
    private static string ImageMimeForPath(string filePath)
    {
        return Path.GetExtension(filePath ?? "").ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "image/jpeg",
        };
    }

    /// <summary>视频 MIME（对齐 prompt_master.js _videoMimeForPath）。</summary>
    private static string VideoMimeForPath(string filePath)
    {
        return Path.GetExtension(filePath ?? "").ToLowerInvariant() switch
        {
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".avi" => "video/x-msvideo",
            ".mkv" => "video/x-matroska",
            _ => "video/mp4",
        };
    }

    private static CaptionPrompts BuildPrompts(ReverseCaptionRequest req, CaptionModelFamily family)    {
        var mt = req.MediaTarget ?? "image";
        if (family == CaptionModelFamily.Torii)
        {
            // ToriiGate 模型：标签式工程走原生 JSON 模板；标准 PE 用 Torii shell 承载
            if (ToriiGateModelAdapter.IsTagLineReversePe(req))
                return ToriiGateModelAdapter.BuildToriiGateTagLinePrompts(req, mt);
            var standard = ToriiGateModelAdapter.BuildToriiGateStandardPePrompts(req, mt);
            if (standard != null)
                return standard;
        }

        // 自定义 PE（applyReverseToCaption 已写入 pe_custom_*）
        if (!string.IsNullOrWhiteSpace(req.PeCustomSystem))
        {
            var customSystem = string.Join("\n\n", new[]
            {
                req.PeCustomSystem,
                req.PeCustomOutputConstraints,
            }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var customParts = new List<string>();
            var customBody = (req.PeCustomUserBody ?? "").Trim();
            if (customBody.Length > 0)
                customParts.Add(customBody);
            var customExtra = (req.ExtraPrompt ?? "").Trim();
            if (customExtra.Length > 0)
                customParts.Add(customExtra);
            return new CaptionPrompts
            {
                SystemPrompt = customSystem,
                UserPrompt = string.Join("\n\n", customParts),
            };
        }

        // 标准 PE 组装（与 prompt_master.js makeMessagesForImageCaption 一致）
        var system = string.Join("\n\n", new[]
        {
            CaptionPromptBlocks.BuildSystemPrompt(req, mt),
            CaptionPromptBlocks.BuildSystemAddons(req, mt),
            CaptionPromptBlocks.BuildOutputConstraints(req),
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        var userParts = new List<string>
        {
            CaptionPromptBlocks.BuildUserTaskLead(req, mt),
        };
        var body = CaptionPromptBlocks.BuildUserTaskBody(req);
        if (!string.IsNullOrWhiteSpace(body))
            userParts.Add(body);
        var tail = CaptionPromptBlocks.BuildUserTailAddon(req);
        if (!string.IsNullOrWhiteSpace(tail))
            userParts.Add(tail);
        var joyTail = CaptionPromptBlocks.BuildJoyExtraUserEnforcementTail(req);
        var extra = (req.ExtraPrompt ?? "").Trim();
        if (extra.Length > 0)
            userParts.Add(string.Join(" ", new[] { joyTail, extra }.Where(x => !string.IsNullOrWhiteSpace(x))));
        else if (joyTail.Length > 0)
            userParts.Add(joyTail);

        return new CaptionPrompts { SystemPrompt = system, UserPrompt = string.Join("\n\n", userParts) };
    }

    private static string SanitizeOutput(string text, ReverseCaptionRequest req, CaptionModelFamily family)
    {
        var raw = (text ?? "").Trim();
        if (raw.Length == 0)
            return raw;

        if (family == CaptionModelFamily.Torii)
        {
            var extractMode = req.ToriiExtractModeValue
                ?? ((req.StructuredFormatValue ?? "").Contains("json") ? "json_raw" : "full");
            var outText = ToriiGateExtract.ApplyExtractMode(raw, extractMode);
            return CaptionPromptBlocks.SanitizeFinalCaption(outText, req);
        }

        return CaptionPromptBlocks.SanitizeFinalCaption(raw, req);
    }

    /// <summary>写训练打标 sidecar（.txt / .json），返回旁车路径。</summary>
    private static string? WriteSidecar(ReverseCaptionRequest req, string caption)
    {
        var mediaPath = req.MediaPath ?? req.ImageUrl;
        if (string.IsNullOrWhiteSpace(mediaPath))
            return null;
        var dir = req.WorkspaceDir ?? Path.GetDirectoryName(mediaPath);
        if (string.IsNullOrWhiteSpace(dir))
            return null;
        var name = Path.GetFileNameWithoutExtension(mediaPath);
        var ext = (req.CaptionFileFormat ?? "txt").ToLowerInvariant() == "json" ? "json" : "txt";
        var sidecarPath = Path.Combine(dir, $"{name}.{ext}");
        var content = ext == "json"
            ? JsonSerializer.Serialize(new { caption }, new JsonSerializerOptions { WriteIndented = true })
            : caption;
        File.WriteAllText(sidecarPath, content);
        return sidecarPath;
    }
}
