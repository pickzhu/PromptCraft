namespace PromptCraft.Interfaces;

/// <summary>全局 KV 配置仓储（comfyui.db AppSettings 表）。</summary>
public interface IAppSettingsRepository
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
    Task SetManyAsync(IEnumerable<KeyValuePair<string, string>> items, CancellationToken ct = default);
}
