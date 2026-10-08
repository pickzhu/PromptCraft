using PromptCraft.Data;
using PromptCraft.Interfaces;
using PromptCraft.Models;
using PromptCraft.Models.Inference;
using PromptCraft.Service.Inference;
using Microsoft.EntityFrameworkCore;

namespace PromptCraft.Service;

/// <summary>
/// 提供商管理（T0.7）。落 ComfyDB（业务库 comfyui.db），DI 注入 IDbContextFactory&lt;ComfyDbContext&gt;。
/// SaveAsync 语义：ApiKey 传空串不覆盖已存密钥；模型列表整体替换；
/// 默认标记全局唯一：本次声明了「扩写默认/反推默认」时，自动取消其他提供商里的同角色默认（各角色至多一个，允许没有）。
/// </summary>
public class ProviderService : IProviderService
{
    private readonly IDbContextFactory<ComfyDbContext> _dbFactory;

    public ProviderService(IDbContextFactory<ComfyDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<ProviderConfig>> GetAllAsync(CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.ProviderConfigs
            .Include(p => p.Models)
            .OrderBy(p => p.Sort).ThenBy(p => p.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<ProviderConfig?> GetByIdAsync(string id, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        return await db.ProviderConfigs
            .Include(p => p.Models)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task<(ProviderConfig, ProviderModel)?> GetDefaultExpandAsync(CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        var providers = await db.ProviderConfigs
            .Include(x => x.Models)
            .Where(x => x.Enabled)
            .OrderBy(x => x.Sort)
            .ToListAsync(ct);
        foreach (var p in providers)
        {
            // 全局唯一：全库至多一个 IsDefaultExpand=true；没有则返回 null（不再回退到第一个扩写模型）
            var def = p.Models.FirstOrDefault(m => m.UseForExpand && m.IsDefaultExpand);
            if (def != null) return (p, def);
        }
        return null;
    }

    public async Task<(ProviderConfig, ProviderModel)?> GetDefaultReverseAsync(CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        var providers = await db.ProviderConfigs
            .Include(x => x.Models)
            .Where(x => x.Enabled)
            .OrderBy(x => x.Sort)
            .ToListAsync(ct);
        foreach (var p in providers)
        {
            // 全局唯一：全库至多一个 IsDefaultReverse=true；没有则返回 null（不再回退到第一个反推模型）
            var def = p.Models.FirstOrDefault(m => m.UseForReverse && m.IsDefaultReverse);
            if (def != null) return (p, def);
        }
        return null;
    }

    public async Task SaveAsync(ProviderConfig p, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        var existing = await db.ProviderConfigs.FirstOrDefaultAsync(x => x.Id == p.Id, ct);

        // 本提供商内部规整：能力约束 + 同角色默认去重（不再自动把第一个模型提升为默认，允许「一个没有」）
        NormalizeLocalDefaultFlags(p.Models);

        // 全局唯一：只要本次保存的提供商声明了某个角色的默认，就取消其他所有提供商里同角色的默认，
        // 实现「默认跟随本次保存迁移，全应用各自至多一个」
        var wantExpand = p.Models.Any(m => m.IsDefaultExpand);
        var wantReverse = p.Models.Any(m => m.IsDefaultReverse);
        if (wantExpand || wantReverse)
        {
            var others = await db.ProviderModels
                .Where(m => m.ProviderId != p.Id
                    && ((wantExpand && m.IsDefaultExpand) || (wantReverse && m.IsDefaultReverse)))
                .ToListAsync(ct);
            foreach (var m in others)
            {
                if (wantExpand && m.IsDefaultExpand) m.IsDefaultExpand = false;
                if (wantReverse && m.IsDefaultReverse) m.IsDefaultReverse = false;
            }
        }

        if (existing == null)
        {
            p.CreatedAt = DateTime.UtcNow;
            p.UpdatedAt = DateTime.UtcNow;
            foreach (var m in p.Models) m.ProviderId = p.Id;
            db.ProviderConfigs.Add(p);
        }
        else
        {
            if (string.IsNullOrEmpty(p.ApiKey))
                p.ApiKey = existing.ApiKey;

            existing.Name = p.Name;
            existing.BaseUrl = p.BaseUrl;
            existing.ApiKey = p.ApiKey;
            existing.Enabled = p.Enabled;
            existing.Sort = p.Sort;
            existing.UpdatedAt = DateTime.UtcNow;

            var oldModels = await db.ProviderModels.Where(m => m.ProviderId == p.Id).ToListAsync(ct);
            db.ProviderModels.RemoveRange(oldModels);
            foreach (var m in p.Models)
            {
                m.ProviderId = p.Id;
                if (string.IsNullOrEmpty(m.Id)) m.Id = Guid.NewGuid().ToString("N");
            }
            db.ProviderModels.AddRange(p.Models);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        var existing = await db.ProviderConfigs.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (existing == null) return;
        var models = await db.ProviderModels.Where(m => m.ProviderId == id).ToListAsync(ct);
        db.ProviderModels.RemoveRange(models);
        db.ProviderConfigs.Remove(existing);
        await db.SaveChangesAsync(ct);
    }

    public Task<IReadOnlyList<ModelInfo>> TestConnectionAsync(ProviderConfig p, CancellationToken ct)
    {
        return OpenAiHttpHelper.GetModelsAsync(p.BaseUrl, p.ApiKey, 30, ct);
    }

    /// <summary>
    /// 修正历史脏数据：扩写默认 / 反推默认全库各自至多一个。
    /// 按提供商 Sort（其次 CreatedAt）顺序保留第一个带默认标记的模型，其余同角色默认全部清除；
    /// 同时清掉「未勾选能力却带默认」的非法标记。仅在确有变更时写库。
    /// </summary>
    public async Task NormalizeGlobalDefaultsAsync(CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        var providers = await db.ProviderConfigs
            .Include(x => x.Models)
            .OrderBy(x => x.Sort).ThenBy(x => x.CreatedAt)
            .ToListAsync(ct);

        var changed = false;
        ProviderModel? keepExpand = null;
        ProviderModel? keepReverse = null;
        foreach (var p in providers)
        {
            foreach (var m in p.Models)
            {
                if (m.IsDefaultExpand && !m.UseForExpand) { m.IsDefaultExpand = false; changed = true; }
                if (m.IsDefaultReverse && !m.UseForReverse) { m.IsDefaultReverse = false; changed = true; }
                if (m.IsDefaultExpand)
                {
                    if (keepExpand == null) keepExpand = m;
                    else { m.IsDefaultExpand = false; changed = true; }
                }
                if (m.IsDefaultReverse)
                {
                    if (keepReverse == null) keepReverse = m;
                    else { m.IsDefaultReverse = false; changed = true; }
                }
            }
        }
        if (changed) await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 单个提供商模型列表内的默认规整：不能对未勾选能力的模型设默认；
    /// 同一提供商内同角色默认至多一个（保留第一个声明的）。不自动提升默认。
    /// </summary>
    private static void NormalizeLocalDefaultFlags(List<ProviderModel> models)
    {
        var seenExpand = false;
        var seenReverse = false;
        foreach (var m in models)
        {
            if (!m.UseForExpand) m.IsDefaultExpand = false;
            if (!m.UseForReverse) m.IsDefaultReverse = false;
            if (m.IsDefaultExpand)
            {
                if (seenExpand) m.IsDefaultExpand = false;
                else seenExpand = true;
            }
            if (m.IsDefaultReverse)
            {
                if (seenReverse) m.IsDefaultReverse = false;
                else seenReverse = true;
            }
        }
    }
}
