using PromptCraft.Models;
using PromptCraft.Models.Inference;

namespace PromptCraft.Interfaces;

/// <summary>反推服务：按 PromptMaster joycaption.js runPromptMasterImageCaption 流程一比一实现。</summary>
public interface IReverseCaptionService
{
    /// <summary>单条反推（旧 UI 兼容，自动选默认反推提供商）。</summary>
    Task<string> CaptionAsync(ReverseCaptionRequest req, CancellationToken ct);

    /// <summary>单条反推（指定提供商/模型）。</summary>
    Task<string> CaptionAsync(ReverseCaptionRequest req, ProviderConfig? provider, ProviderModel? model, CancellationToken ct);

    /// <summary>批量反推（逐条构建 → 调用 → 清洗 → sidecar，进度回调；provider/model 为空时走默认反推提供商）。</summary>
    Task<IReadOnlyList<ReverseCaptionResult>> CaptionBatchAsync(
        IReadOnlyList<ReverseCaptionRequest> reqs,
        CancellationToken ct,
        IProgress<ReverseProgressEvent>? progress = null);

    /// <summary>批量反推（指定提供商/模型，对齐扩写页显式传参）。</summary>
    Task<IReadOnlyList<ReverseCaptionResult>> CaptionBatchAsync(
        IReadOnlyList<ReverseCaptionRequest> reqs,
        ProviderConfig? provider,
        ProviderModel? model,
        CancellationToken ct,
        IProgress<ReverseProgressEvent>? progress = null);
}
