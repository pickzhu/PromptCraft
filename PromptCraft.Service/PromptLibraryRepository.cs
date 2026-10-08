using PromptCraft.Data;
using PromptCraft.Interfaces;
using PromptCraft.Models;
using Microsoft.EntityFrameworkCore;

namespace PromptCraft.Service;

/// <summary>
/// 词库数据访问层。提示词库已并入 comfyui.db 单库（表/列大驼峰）：
/// 保留源 Id 主键、删除 prompt 连带 PromptTagMaps 映射、upsert 语义。
/// </summary>
public class PromptLibraryRepository : IPromptRepository
{
    private readonly IDbContextFactory<ComfyDbContext> _dbFactory;

    public PromptLibraryRepository(IDbContextFactory<ComfyDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<Prompt?> GetAsync(string id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Prompts
            .Include(p => p.PromptTags)
            .Include(p => p.FolderMaps)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task<(IReadOnlyList<Prompt> Items, int Total)> QueryAsync(PromptQuery q, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        IQueryable<Prompt> query = db.Prompts.Include(p => p.PromptTags).Include(p => p.FolderMaps);

        // 文件夹筛选（多对多）：0=未分类（无任何文件夹关联）；>0=包含该文件夹关联
        if (q.FolderId.HasValue)
            query = q.FolderId.Value == 0
                ? query.Where(p => !p.FolderMaps.Any())
                : query.Where(p => p.FolderMaps.Any(m => m.FolderId == q.FolderId.Value));

        if (!string.IsNullOrEmpty(q.TagId))
            query = query.Where(p => p.PromptTags.Any(t => t.TagId == q.TagId));

        if (!string.IsNullOrWhiteSpace(q.Keyword))
        {
            var kw = q.Keyword.Trim();
            query = query.Where(p =>
                EF.Functions.Like(p.Title, $"%{kw}%") ||
                EF.Functions.Like(p.Positive, $"%{kw}%"));
        }

        var total = await query.CountAsync(ct);
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 500);

        var items = await query
            .OrderByDescending(p => p.UpdatedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        return (items, total);
    }

    public async Task<Prompt> SaveAsync(Prompt p, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Prompts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == p.Id, ct);
        // 新建走 Add / 已存在走 Update（Update 对不存在实体会产生影响 0 行的 UPDATE → 并发异常）
        if (existing != null)
            db.Prompts.Update(p);
        else
            db.Prompts.Add(p);
        await db.SaveChangesAsync(ct);
        return p;
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.Prompts.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (entity == null) return;
        db.Prompts.Remove(entity);
        await db.SaveChangesAsync(ct); // 关联的 PromptTagMaps 行随级联删除
    }
}
