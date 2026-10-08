using PromptCraft.Interfaces;
using PromptCraft.Models;
using Microsoft.EntityFrameworkCore;

namespace PromptCraft.Data;

/// <summary>
/// 全局业务 KV 配置仓储（comfyui.db 的 AppSettings 表），对齐 PromptMaster settingOperation。
/// 与其它 Comfy 仓储一致：注入 IDbContextFactory，每操作独立 DbContext，避免追踪脏读。
/// </summary>
public class AppSettingsRepository : IAppSettingsRepository
{
    private readonly IDbContextFactory<ComfyDbContext> _dbFactory;
    public AppSettingsRepository(IDbContextFactory<ComfyDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.AppSettings.AsNoTracking()
            .Where(x => x.Key == key)
            .Select(x => (string?)x.Value)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.AppSettings.AsNoTracking().ToDictionaryAsync(x => x.Key, x => x.Value, ct);
    }

    /// <summary>UPSERT 单条 KV（对齐 PromptMaster settingOperation.setItem 语义）。</summary>
    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (existing != null)
        {
            existing.Value = value ?? "";
            db.AppSettings.Update(existing);
        }
        else
        {
            db.AppSettings.Add(new AppSetting { Key = key, Value = value ?? "" });
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>删除单条 KV。</summary>
    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (existing != null)
        {
            db.AppSettings.Remove(existing);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>批量写入（debounce flush 用），避免逐条建连。</summary>
    public async Task SetManyAsync(IEnumerable<KeyValuePair<string, string>> items, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        foreach (var kv in items)
        {
            var existing = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == kv.Key, ct);
            if (existing != null)
            {
                existing.Value = kv.Value ?? "";
                db.AppSettings.Update(existing);
            }
            else
            {
                db.AppSettings.Add(new AppSetting { Key = kv.Key, Value = kv.Value ?? "" });
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
