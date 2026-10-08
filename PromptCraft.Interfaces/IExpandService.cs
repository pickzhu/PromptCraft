using PromptCraft.Models;
using PromptCraft.Models.Inference;

namespace PromptCraft.Interfaces;

/// <summary>提示词扩写服务（T2.1，对齐 prompt_master.js expandTextByLlm 在线链路 + expandOutputContinue.js）。</summary>
public interface IExpandService
{
    /// <summary>扩写。未配置默认扩写模型时抛 InferencesException(NoSuchProvider)。</summary>
    Task<string> ExpandAsync(ExpandRequest req, CancellationToken ct);
    /// <summary>指定 provider+model 扩写。</summary>
    Task<string> ExpandAsync(ExpandRequest req, ProviderConfig? provider, ProviderModel? model, CancellationToken ct);
}
