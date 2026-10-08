using PromptCraft.Interfaces;
using Ke.Bee.Localization.Localizer;

namespace PromptCraft.Service;

/// <summary>
/// 扩写参数记忆（pm_expand_*）的内存缓存 + comfyui.db 落库（AppSettings KV 表）。
/// 对齐 PromptMaster 前端：confirm 后 watch 变更写入、customRule/userExtraPrompt 400ms debounce；
/// 本 Store 统一提供 Get/Set，所有 Set 经 400ms debounce 合并落库。
/// 单例注册：VM 每次创建都从缓存读到最新值，LoadAsync 幂等。
/// </summary>
public class ExpandSettingsStore : IExpandSettingsStore
{
    private readonly IAppSettingsRepository _repo;
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private CancellationTokenSource? _flushCts;
    private const int FlushDelayMs = 400;

    public ExpandSettingsStore(IAppSettingsRepository repo) => _repo = repo;

    public string? Get(string key)
    {
        lock (_gate)
            return _cache.TryGetValue(key, out var v) ? v : null;
    }

    public void Set(string key, string value)
    {
        lock (_gate)
        {
            _cache[key] = value ?? "";
        }
        ScheduleFlush();
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            _cache.Remove(key);
        }
        // flush 是 UPSERT 语义不会删行，直接落库删除（使用方极少调用，不计 debounce）
        _ = Task.Run(async () =>
        {
            try { await _repo.DeleteAsync(key); }
            catch (Exception ex) { LogService.Instance.Warn(string.Format(Localizer.Instance?["ExpandDeleteFailedLog"] ?? "", ex.Message), "Expand", ex); }
        });
    }

    /// <summary>启动时从 comfyui.db 全量载入缓存（幂等）。</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var all = await _repo.GetAllAsync(ct);
        lock (_gate)
        {
            _cache.Clear();
            foreach (var kv in all)
                _cache[kv.Key] = kv.Value;
        }
    }

    private void ScheduleFlush()
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            // 只 Cancel 不 Dispose：旧任务可能仍在 Task.Delay 参数求值/执行中，
            // Dispose 后访问其 Token 会抛 ObjectDisposedException（此前线上警告的根因）。
            // 被取消且无引用的 CTS 交由 GC 回收即可。
            _flushCts?.Cancel();
            cts = _flushCts = new CancellationTokenSource();
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(FlushDelayMs, cts.Token);
                await FlushAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // 被下一次 Set 合并，忽略
            }
            catch (Exception ex)
            {
                LogService.Instance.Warn(string.Format(Localizer.Instance?["ExpandPersistFailedLog"] ?? "", ex.Message), "Expand", ex);
            }
        });
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        Dictionary<string, string> snapshot;
        lock (_gate)
            snapshot = new Dictionary<string, string>(_cache);
        if (snapshot.Count == 0) return;
        await _repo.SetManyAsync(snapshot, ct);
    }
}
