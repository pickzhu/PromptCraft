using PromptCraft.Models;
using PromptCraft.Models.Inference;

namespace PromptCraft.Interfaces;

/// <summary>提供商管理服务（T0.7）。落 ComfyDB，全局默认扩写/反推模型各至多一个。</summary>
public interface IProviderService
{
    Task<IReadOnlyList<ProviderConfig>> GetAllAsync(CancellationToken ct);
    Task<ProviderConfig?> GetByIdAsync(string id, CancellationToken ct);
    /// <summary>扩写默认模型：全应用全局至多一个（IsDefaultExpand=true），在已启用提供商里按 Sort 顺序取；无则返回 null（UI 引导配置）。</summary>
    Task<(ProviderConfig Provider, ProviderModel Model)?> GetDefaultExpandAsync(CancellationToken ct);
    /// <summary>反推默认模型：全应用全局至多一个（IsDefaultReverse=true），在已启用提供商里按 Sort 顺序取；无则返回 null（UI 引导配置）。</summary>
    Task<(ProviderConfig Provider, ProviderModel Model)?> GetDefaultReverseAsync(CancellationToken ct);
    Task SaveAsync(ProviderConfig p, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
    /// <summary>连接测试：GET /models；成功返回模型列表，失败抛 InferencesException。</summary>
    Task<IReadOnlyList<ModelInfo>> TestConnectionAsync(ProviderConfig p, CancellationToken ct);
    /// <summary>修正历史脏数据：扩写/反推默认全库各自至多一个（按 Sort 顺序保留第一个，其余清除）。仅在确有变更时写库。</summary>
    Task NormalizeGlobalDefaultsAsync(CancellationToken ct);
}
