using PromptCraft.Models;
using PromptCraft.Models.Inference;

namespace PromptCraft.Interfaces;

/// <summary>提示词仿写服务（单次生成：原始提示词 + 仿写要求 → 新提示词）。</summary>
public interface IImitationService
{
    /// <summary>仿写。未配置默认模型时抛 InferencesException(NoSuchProvider)。</summary>
    Task<string> ImitateAsync(ImitationRequest req, CancellationToken ct);
    /// <summary>指定 provider+model 仿写（复用扩写默认模型）。</summary>
    Task<string> ImitateAsync(ImitationRequest req, ProviderConfig? provider, ProviderModel? model, CancellationToken ct);
}
