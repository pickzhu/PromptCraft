using PromptCraft.Consts.Event;
using PromptCraft.Data;
using PromptCraft.Interfaces;
using PromptCraft.Models;
using PromptCraft.Models.ComfyUI;
using Ke.Bee.Localization.Localizer;
using Microsoft.EntityFrameworkCore;

namespace PromptCraft.Service;

/// <summary>
/// 提示词库服务（提示词 + 标签；文件夹由统一的 <see cref="FolderService"/> 管理）。
/// 对齐源库 <c>electron/service/pm_library_db.js</c> 的语义：
/// 文件夹/标签同名拒绝（大小写不敏感 trim）；删除即物理删除；
/// prompt 保存为 upsert（保留 created_at）。所有写操作发布 <see cref="EventNameConst.PromptLibraryChangedEvent"/>。
/// 标签使用全局标签池（<see cref="Tag"/>，与图库/工作流共用同一套标签）。
/// </summary>
public class PromptLibraryService : IPromptLibraryService
{
    private readonly IDbContextFactory<ComfyDbContext> _dbFactory;
    private readonly ITagRepository _tagRepository;
    private readonly IPromptRepository _promptRepository;
    private readonly IBaseNotice _notice;
    private readonly IBaseLogService _log;

    public PromptLibraryService(
        IDbContextFactory<ComfyDbContext> dbFactory,
        ITagRepository tagRepository,
        IPromptRepository promptRepository,
        IBaseNotice notice,
        IBaseLogService log)
    {
        _dbFactory = dbFactory;
        _tagRepository = tagRepository;
        _promptRepository = promptRepository;
        _notice = notice;
        _log = log;
    }

    // ---- 标签（全局标签池，与图库/工作流共用） ----

    /// <summary>取全局标签池全部标签（按名称排序；词库/图库/工作流共用）。</summary>
    public async Task<List<Tag>> GetTagsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Tags.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
    }

    /// <summary>
    /// 保存全局标签（新建或重命名）：同名拒绝（忽略大小写）；新建自动从调色板取色（避开已用色）。
    /// 与图库/工作流标签池完全一致（同一张 Tags 表）。
    /// </summary>
    public async Task<Tag> SaveGlobalTagAsync(string? id, string name, CancellationToken ct = default)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed)) throw new InvalidOperationException(Localizer.Instance?["PromptTagNameEmpty"] ?? "");
        if (trimmed.Length > 40) throw new InvalidOperationException(Localizer.Instance?["PromptTagNameTooLong"] ?? "");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var key = trimmed.ToLowerInvariant();
        // SQLite 无法翻译 Trim/ToLowerInvariant/ToString：先物化到内存再比较（修复 LINQ 报错）
        var all = await db.Tags.AsNoTracking().Select(t => new { t.Id, t.Name }).ToListAsync(ct);
        var dup = all.FirstOrDefault(t =>
            t.Name?.Trim().ToLowerInvariant() == key && t.Id.ToString() != id);
        if (dup != null) throw new InvalidOperationException(Localizer.Instance?["PromptTagNameExists"] ?? "");

        if (!string.IsNullOrWhiteSpace(id) && int.TryParse(id, out var tagId))
        {
            var existing = await db.Tags.FirstOrDefaultAsync(t => t.Id == tagId, ct);
            if (existing != null)
            {
                existing.Name = trimmed;
                await db.SaveChangesAsync(ct);
                await PublishChangedAsync(ct);
                _log.Info(string.Format(Localizer.Instance?["PromptTagSaved"] ?? "", trimmed), "PromptLibrary");
                return existing;
            }
        }

        // 新建：调色板随机取色（与图库 TagManagerDrawerModel 同逻辑）
        var used = TagPalette.NormalizeUsedColors(
            await db.Tags.Where(t => t.Color != null).Select(t => t.Color).ToListAsync(ct));
        var tag = new Tag { Name = trimmed, Category = "general", Color = TagPalette.PickDistinctColorHex(used) };
        db.Tags.Add(tag);
        await db.SaveChangesAsync(ct);

        await PublishChangedAsync(ct);
        _log.Info(string.Format(Localizer.Instance?["PromptTagSaved"] ?? "", trimmed), "PromptLibrary");
        return tag;
    }

    /// <summary>删除全局标签：清理词库 PromptTagMaps 关联 + 图库/工作流关联（TagRepository）+ 标签本体。</summary>
    public async Task DeleteGlobalTagAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !int.TryParse(id, out var tagId)) return;

        // 词库关联映射（PromptTagMap.TagId 为无外键自由列，存全局 Tag.Id 字符串）
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            await db.PromptTagMaps.Where(m => m.TagId == id).ExecuteDeleteAsync(ct);
        }

        // 图库/工作流关联 + 标签本体（TagRepository 级联清理）
        await _tagRepository.DeleteAsync(tagId);

        await PublishChangedAsync(ct);
        _log.Info(string.Format(Localizer.Instance?["PromptTagDeleted"] ?? "", id), "PromptLibrary");
    }

    // ---- 提示词 ----

    public Task<Prompt?> GetPromptAsync(string id, CancellationToken ct = default)
        => _promptRepository.GetAsync(id, ct);

    public Task<(IReadOnlyList<Prompt> Items, int Total)> QueryAsync(PromptQuery q, CancellationToken ct = default)
        => _promptRepository.QueryAsync(q, ct);

    /// <summary>
    /// 保存提示词（upsert：保留已有 created_at，更新 updated_at）。对齐源库 savePrompt 语义。
    /// 新建走 Add / 已存在走 Update（Update 对不存在实体会产生影响 0 行的 UPDATE → 并发异常，勿无条件 Update）。
    /// 文件夹为多对多：按 <see cref="Prompt.FolderIds"/>（非映射属性）重建 PromptFolderMaps。
    /// </summary>
    public async Task<Prompt> SavePromptAsync(Prompt p, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var id = string.IsNullOrWhiteSpace(p.Id) ? Guid.NewGuid().ToString("N") : p.Id;
        var existing = await db.Prompts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        var now = DateTime.UtcNow;
        var tagMaps = (p.PromptTags ?? new List<PromptTagMap>())
            .Select(t => new PromptTagMap { PromptId = id, TagId = t.TagId })
            .ToList();
        var entity = new Prompt
        {
            Id = id,
            Title = string.IsNullOrWhiteSpace(p.Title) ? Localizer.Instance?["PromptUntitled"] ?? "" : p.Title,
            Positive = p.Positive ?? string.Empty,
            Negative = p.Negative ?? string.Empty,
            Cover = p.Cover ?? string.Empty,
            Note = p.Note ?? string.Empty,
            Seed = p.Seed ?? string.Empty,
            Models = p.Models ?? string.Empty,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
            PromptTags = new List<PromptTagMap>(), // 映射走显式 AddRange，避免 Update 把导航集合标 Modified 引起 0 行
            FolderMaps = new List<PromptFolderMap>(),
        };

        // 文件夹 Id 必须指向存在的文件夹（Scope=Prompt 或 All，All 可容纳任意类型资产），无效的忽略（对齐源库 folderRow 校验）
        var folderIds = (p.FolderIds ?? Array.Empty<int>()).Distinct().ToList();
        var validFolderIds = folderIds.Count == 0
            ? new HashSet<int>()
            : (await db.Folders.AsNoTracking()
                .Where(f => (f.Scope == FolderScopes.Prompt || f.Scope == FolderScopes.All) && folderIds.Contains(f.Id))
                .Select(f => f.Id).ToListAsync(ct)).ToHashSet();

        // 对齐源库 savePrompt 语义：先删除旧标签映射与旧文件夹映射，再写入新映射（避免残留已取消的关联）
        await db.PromptTagMaps.Where(m => m.PromptId == id).ExecuteDeleteAsync(ct);
        await db.PromptFolderMaps.Where(m => m.PromptId == id).ExecuteDeleteAsync(ct);

        if (existing != null)
            db.Prompts.Update(entity);
        else
            db.Prompts.Add(entity);

        foreach (var tm in tagMaps) db.PromptTagMaps.Add(tm);
        foreach (var fid in validFolderIds) db.PromptFolderMaps.Add(new PromptFolderMap { PromptId = id, FolderId = fid });
        await db.SaveChangesAsync(ct);

        await PublishChangedAsync(ct);
        _log.Info(string.Format(Localizer.Instance?["PromptSavedLog"] ?? "", entity.Title), "PromptLibrary");
        return entity;
    }

    /// <summary>批量删除提示词（连带标签映射）。</summary>
    public async Task DeletePromptsAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        var list = (ids ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        if (list.Count == 0) return;

        foreach (var id in list)
        {
            await _promptRepository.DeleteAsync(id, ct);
        }

        await PublishChangedAsync(ct);
        _log.Info(string.Format(Localizer.Instance?["PromptBatchDeletedLog"] ?? "", list.Count), "PromptLibrary");
    }

    /// <summary>
    /// 批量打标签（对齐图库批量打标签语义：勾选集 = 最终标签集，替换式；空标签集 = 清空这批提示词的标签）。
    /// 只处理仍存在的提示词（避免给已删除提示词写入孤儿映射）。
    /// </summary>
    public async Task AssignTagsAsync(IReadOnlyList<string> promptIds, IReadOnlyList<string> tagIds, CancellationToken ct = default)
    {
        var ids = (promptIds ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        if (ids.Count == 0) return;
        var tags = (tagIds ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existingIds = await db.Prompts.Where(p => ids.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct);
        if (existingIds.Count == 0) return;

        // 替换式：先清掉这批提示词现有全部标签映射，再写入勾选集
        foreach (var pid in existingIds)
        {
            await db.PromptTagMaps.Where(m => m.PromptId == pid).ExecuteDeleteAsync(ct);
            foreach (var tid in tags)
                db.PromptTagMaps.Add(new PromptTagMap { PromptId = pid, TagId = tid });
        }
        await db.SaveChangesAsync(ct);

        await PublishChangedAsync(ct);
        _log.Info(string.Format(Localizer.Instance?["PromptBatchTagLog"] ?? "", existingIds.Count, tags.Count), "PromptLibrary");
    }

    // ---- 内部 ----

    private async Task PublishChangedAsync(CancellationToken ct)
    {
        await Task.CompletedTask;
        try
        {
            _notice.Publish(EventNameConst.PromptLibraryChangedEvent, null);
        }
        catch (Exception ex)
        {
            _log.Warn(string.Format(Localizer.Instance?["PromptPublishChangedFailed"] ?? "", ex.Message), "PromptLibrary", ex);
        }
    }
}
