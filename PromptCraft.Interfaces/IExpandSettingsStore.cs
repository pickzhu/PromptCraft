namespace PromptCraft.Interfaces;

/// <summary>
/// 扩写参数记忆（pm_expand_*）的内存缓存 + comfyui.db 落库（AppSettings KV 表）。
/// 所有 Set 经 400ms debounce 合并落库；LoadAsync 幂等。
/// </summary>
public interface IExpandSettingsStore
{
    string? Get(string key);
    void Set(string key, string value);
    void Remove(string key);
    Task LoadAsync(CancellationToken ct = default);
}
