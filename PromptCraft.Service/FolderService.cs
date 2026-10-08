using PromptCraft.Consts.Event;
using PromptCraft.Data;
using PromptCraft.Interfaces;
using PromptCraft.Models;
using PromptCraft.Models.ComfyUI;
using Ke.Bee.Localization.Localizer;
using Microsoft.EntityFrameworkCore;

namespace PromptCraft.Service;

/// <summary>
/// 统一单级文件夹服务（工作流 / 提示词 / 图库，<see cref="Folder.Scope"/> 区分）。
/// 只支持一级文件夹；同 Scope 内名称唯一；资产与文件夹多对多（关联表 PromptFolderMaps / WorkflowFolderMaps / GalleryFolderMaps）。
/// 删除文件夹时可选择是否连带删除其中资产（默认保留，仅解除关联）。
/// </summary>
public class FolderService : IFolderService
{
    private readonly IDbContextFactory<ComfyDbContext> _dbFactory;
    private readonly IBaseNotice _notice;
    private readonly IBaseLogService _log;
    private readonly ComfySettings _settings;

    public FolderService(
        IDbContextFactory<ComfyDbContext> dbFactory,
        IBaseNotice notice,
        IBaseLogService log,
        ComfySettings settings)
    {
        _dbFactory = dbFactory;
        _notice = notice;
        _log = log;
        _settings = settings;
    }

    public async Task<List<Folder>> GetFoldersAsync(string scope, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Folders.AsNoTracking()
            .Where(f => f.Scope == scope)
            .OrderBy(f => f.Sort)
            .ThenBy(f => f.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<List<Folder>> GetAllFoldersAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Folders.AsNoTracking()
            .OrderBy(f => f.Sort)
            .ThenBy(f => f.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>保存文件夹（新建或重命名）：同 Scope 同名拒绝（忽略大小写）；新建 sort 取当前最大 + 1。</summary>
    public async Task<Folder> SaveFolderAsync(Folder folder, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var scope = string.IsNullOrWhiteSpace(folder.Scope) ? FolderScopes.Prompt : folder.Scope;
        var name = folder.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(name)) throw new InvalidOperationException(Localizer.Instance?["PromptFolderNameEmpty"] ?? "");
        if (name.Length > 40) throw new InvalidOperationException(Localizer.Instance?["PromptFolderNameTooLong"] ?? "");

        if (await FolderNameExistsAsync(db, scope, name, folder.Id, ct))
            throw new InvalidOperationException(Localizer.Instance?["PromptFolderNameExists"] ?? "");

        var existing = folder.Id > 0
            ? await db.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == folder.Id, ct)
            : null;
        if (existing != null)
        {
            existing = new Folder
            {
                Id = existing.Id,
                Name = name,
                Scope = existing.Scope,
                Sort = existing.Sort,
                CreatedAt = existing.CreatedAt,
            };
            db.Folders.Update(existing);
        }
        else
        {
            var max = await db.Folders
                .Where(f => f.Scope == scope)
                .MaxAsync(f => (int?)f.Sort, ct) ?? -1;
            existing = new Folder
            {
                Name = name,
                Scope = scope,
                Sort = max + 1,
                CreatedAt = DateTime.UtcNow,
            };
            db.Folders.Add(existing);
        }
        await db.SaveChangesAsync(ct);

        await PublishChangedAsync(ct);
        _log.Info(string.Format(Localizer.Instance?["PromptFolderSaved"] ?? "", name), "Folder");
        return existing;
    }

    /// <summary>
    /// 删除文件夹。deleteAssets=false（默认）：仅删除该文件夹与资产的关联（资产保留，变为未分类）；
    /// deleteAssets=true：提示词物理删、工作流逻辑删、图库物理删（含输出文件与缩略图），并清理这些资产在其他文件夹的关联。
    /// 文件夹删除后其关联表行随外键级联清理。
    /// All 文件夹（文件夹管理页新建）可同时含三类资产：按三类逐一处理。
    /// </summary>
    public async Task DeleteFolderAsync(int id, bool deleteAssets, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (folder == null) return;

        if (folder.Scope == FolderScopes.All)
        {
            // All 文件夹：提示词 / 工作流 / 图库三类资产都可能在其中，逐一按各自语义处理
            await DeletePromptAssetsAsync(db, id, deleteAssets, ct);
            await DeleteGalleryAssetsAsync(db, id, deleteAssets, _settings, ct);
            await DeleteWorkflowAssetsAsync(db, id, deleteAssets, ct);
        }
        else
        {
            switch (folder.Scope)
            {
                case FolderScopes.Prompt:
                    await DeletePromptAssetsAsync(db, id, deleteAssets, ct);
                    break;

                case FolderScopes.Gallery:
                    await DeleteGalleryAssetsAsync(db, id, deleteAssets, _settings, ct);
                    break;

                case FolderScopes.Workflow:
                    await DeleteWorkflowAssetsAsync(db, id, deleteAssets, ct);
                    break;
            }
        }

        db.Folders.Remove(folder);
        await db.SaveChangesAsync(ct);

        await PublishChangedAsync(ct);
        _log.Info(string.Format(Localizer.Instance?["PromptFolderDeleted"] ?? "", folder.Name), "Folder");
    }

    /// <summary>删除文件夹中的提示词：deleteAssets=true 物理删（PromptFolderMaps 级联）；false 仅解除该文件夹关联。</summary>
    private static async Task DeletePromptAssetsAsync(ComfyDbContext db, int folderId, bool deleteAssets, CancellationToken ct)
    {
        if (deleteAssets)
        {
            var ids = await db.Prompts
                .Where(p => p.FolderMaps.Any(m => m.FolderId == folderId))
                .Select(p => p.Id).ToListAsync(ct);
            await db.Prompts.Where(p => ids.Contains(p.Id)).ExecuteDeleteAsync(ct); // PromptTagMaps / PromptFolderMaps 级联
        }
        else
        {
            // 仅移除该文件夹的关联，资产保留（变为未分类）
            await db.PromptFolderMaps.Where(m => m.FolderId == folderId).ExecuteDeleteAsync(ct);
        }
    }

    /// <summary>删除文件夹中的图库图片：deleteAssets=true 物理删文件+缩略图+记录（GalleryFolderMaps 级联）；false 仅解除该文件夹关联。</summary>
    private static async Task DeleteGalleryAssetsAsync(ComfyDbContext db, int folderId, bool deleteAssets, ComfySettings settings, CancellationToken ct)
    {
        if (deleteAssets)
        {
            var images = await db.ImageMetadata
                .Where(i => i.FolderMaps.Any(m => m.FolderId == folderId))
                .ToListAsync(ct);
            var thumbs = new List<string>();
            foreach (var img in images)
            {
                TryDeleteFile(Path.Combine(settings.ThumbDir, GetThumbFileName(img.RelativePath, settings.ThumbMaxDimension)), thumbs);
                if (!string.IsNullOrWhiteSpace(img.RelativePath))
                    TryDeleteFile(Path.Combine(settings.ComfyOutputDir, img.RelativePath), thumbs);
            }
            db.ImageMetadata.RemoveRange(images); // ImageStatus/ImageTags/GalleryFolderMaps 级联
        }
        else
        {
            await db.GalleryFolderMaps.Where(m => m.FolderId == folderId).ExecuteDeleteAsync(ct);
        }
    }

    /// <summary>删除文件夹中的工作流：deleteAssets=true 逻辑删（对齐 WorkflowRepository.SoftDeleteAsync 语义）；false 仅解除该文件夹关联。</summary>
    private static async Task DeleteWorkflowAssetsAsync(ComfyDbContext db, int folderId, bool deleteAssets, CancellationToken ct)
    {
        // 属于该文件夹且未删除的工作流
        var wfIds = await db.Workflows
            .Where(w => w.FolderMaps.Any(m => m.FolderId == folderId) && !w.IsDeleted)
            .Select(w => w.Id)
            .ToListAsync(ct);
        if (deleteAssets)
        {
            // 逻辑删除（对齐 WorkflowRepository.SoftDeleteAsync 语义）：Inputs/Jobs/Outputs 置位，标签关联物理清除
            foreach (var wid in wfIds)
            {
                await db.Workflows.Where(w => w.Id == wid)
                    .ExecuteUpdateAsync(s => s.SetProperty(w => w.IsDeleted, true), ct);
                await db.WorkflowInputs.Where(i => i.WorkflowId == wid)
                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.IsDeleted, true), ct);
                await db.WorkflowJobs.Where(j => j.WorkflowId == wid)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.IsDeleted, true), ct);
                // JobOutput 无 WorkflowId，经 Job 关联定位
                var jobIds = await db.WorkflowJobs.Where(j => j.WorkflowId == wid).Select(j => j.Id).ToListAsync(ct);
                await db.JobOutputs.Where(o => jobIds.Contains(o.JobId))
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.IsDeleted, true), ct);
                await db.WorkflowTags.Where(t => t.WorkflowId == wid).ExecuteDeleteAsync(ct);
            }
            // 逻辑删除的工作流不再保留任何文件夹关联
            await db.WorkflowFolderMaps.Where(m => wfIds.Contains(m.WorkflowId)).ExecuteDeleteAsync(ct);
        }
        else
        {
            await db.WorkflowFolderMaps.Where(m => m.FolderId == folderId).ExecuteDeleteAsync(ct);
        }
    }

    /// <inheritdoc />
    public async Task AddToFoldersAsync(string scope, IReadOnlyList<string> assetIds, IReadOnlyList<int> folderIds, CancellationToken ct = default)
    {
        var assets = (assetIds ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        var folders = (folderIds ?? []).Distinct().ToList();
        if (assets.Count == 0 || folders.Count == 0) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // All 文件夹可容纳任意类型资产（文件夹管理页新建的文件夹统一为 FolderScopes.All）
        var validFolders = (await db.Folders.AsNoTracking()
            .Where(f => (f.Scope == scope || f.Scope == FolderScopes.All) && folders.Contains(f.Id))
            .Select(f => f.Id).ToListAsync(ct)).ToHashSet();
        if (validFolders.Count == 0) return;

        switch (scope)
        {
            case FolderScopes.Prompt:
            {
                // 只关联真实存在的提示词
                var existIds = (await db.Prompts.AsNoTracking()
                    .Where(p => assets.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct)).ToHashSet();
                var existing = (await db.PromptFolderMaps.AsNoTracking()
                    .Where(m => existIds.Contains(m.PromptId) && validFolders.Contains(m.FolderId))
                    .Select(m => m.PromptId + "|" + m.FolderId).ToListAsync(ct)).ToHashSet();
                foreach (var pid in existIds)
                    foreach (var fid in validFolders)
                        if (!existing.Contains(pid + "|" + fid))
                            db.PromptFolderMaps.Add(new PromptFolderMap { PromptId = pid, FolderId = fid });
                break;
            }
            case FolderScopes.Gallery:
            {
                var ids = assets.Where(a => int.TryParse(a, out _)).Select(int.Parse).Distinct().ToList();
                if (ids.Count == 0) return;
                var existIds = (await db.ImageMetadata.AsNoTracking()
                    .Where(i => ids.Contains(i.Id)).Select(i => i.Id).ToListAsync(ct)).ToHashSet();
                var existing = (await db.GalleryFolderMaps.AsNoTracking()
                    .Where(m => existIds.Contains(m.ImageInfoId) && validFolders.Contains(m.FolderId))
                    .Select(m => m.ImageInfoId + "|" + m.FolderId).ToListAsync(ct)).ToHashSet();
                foreach (var iid in existIds)
                    foreach (var fid in validFolders)
                        if (!existing.Contains(iid + "|" + fid))
                            db.GalleryFolderMaps.Add(new GalleryFolderMap { ImageInfoId = iid, FolderId = fid });
                break;
            }
            case FolderScopes.Workflow:
            {
                var ids = assets.Where(a => int.TryParse(a, out _)).Select(int.Parse).Distinct().ToList();
                if (ids.Count == 0) return;
                var existIds = (await db.Workflows.AsNoTracking()
                    .Where(w => ids.Contains(w.Id) && !w.IsDeleted).Select(w => w.Id).ToListAsync(ct)).ToHashSet();
                var existing = (await db.WorkflowFolderMaps.AsNoTracking()
                    .Where(m => existIds.Contains(m.WorkflowId) && validFolders.Contains(m.FolderId))
                    .Select(m => m.WorkflowId + "|" + m.FolderId).ToListAsync(ct)).ToHashSet();
                foreach (var wid in existIds)
                    foreach (var fid in validFolders)
                        if (!existing.Contains(wid + "|" + fid))
                            db.WorkflowFolderMaps.Add(new WorkflowFolderMap { WorkflowId = wid, FolderId = fid });
                break;
            }
            default:
                return;
        }

        await db.SaveChangesAsync(ct);
        _log.Info(string.Format(Localizer.Instance?["AddToFolderDone"] ?? "", assets.Count, validFolders.Count), "Folder");
        await PublishChangedAsync(ct);
    }

    /// <inheritdoc />
    public async Task RemoveFromFoldersAsync(string scope, IReadOnlyList<string> assetIds, IReadOnlyList<int>? folderIds, CancellationToken ct = default)
    {
        var assets = (assetIds ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        if (assets.Count == 0) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        switch (scope)
        {
            case FolderScopes.Prompt:
            {
                var q = db.PromptFolderMaps.Where(m => assets.Contains(m.PromptId));
                if (folderIds != null) q = q.Where(m => folderIds.Contains(m.FolderId));
                await q.ExecuteDeleteAsync(ct);
                break;
            }
            case FolderScopes.Gallery:
            {
                var ids = assets.Where(a => int.TryParse(a, out _)).Select(int.Parse).Distinct().ToList();
                if (ids.Count == 0) return;
                var q = db.GalleryFolderMaps.Where(m => ids.Contains(m.ImageInfoId));
                if (folderIds != null) q = q.Where(m => folderIds.Contains(m.FolderId));
                await q.ExecuteDeleteAsync(ct);
                break;
            }
            case FolderScopes.Workflow:
            {
                var ids = assets.Where(a => int.TryParse(a, out _)).Select(int.Parse).Distinct().ToList();
                if (ids.Count == 0) return;
                var q = db.WorkflowFolderMaps.Where(m => ids.Contains(m.WorkflowId));
                if (folderIds != null) q = q.Where(m => folderIds.Contains(m.FolderId));
                await q.ExecuteDeleteAsync(ct);
                break;
            }
            default:
                return;
        }

        _log.Info(string.Format(Localizer.Instance?["FolderRemoveDone"] ?? "", assets.Count), "Folder");
        await PublishChangedAsync(ct);
    }

    /// <inheritdoc />
    public async Task<HashSet<int>> GetAssetFolderIdsAsync(string scope, string assetId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(assetId)) return new HashSet<int>();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        switch (scope)
        {
            case FolderScopes.Prompt:
                return (await db.PromptFolderMaps.AsNoTracking()
                    .Where(m => m.PromptId == assetId).Select(m => m.FolderId).ToListAsync(ct)).ToHashSet();
            case FolderScopes.Gallery:
                if (!int.TryParse(assetId, out var iid)) return new HashSet<int>();
                return (await db.GalleryFolderMaps.AsNoTracking()
                    .Where(m => m.ImageInfoId == iid).Select(m => m.FolderId).ToListAsync(ct)).ToHashSet();
            case FolderScopes.Workflow:
                if (!int.TryParse(assetId, out var wid)) return new HashSet<int>();
                return (await db.WorkflowFolderMaps.AsNoTracking()
                    .Where(m => m.WorkflowId == wid).Select(m => m.FolderId).ToListAsync(ct)).ToHashSet();
            default:
                return new HashSet<int>();
        }
    }

    /// <summary>按传入顺序持久化某 Scope 文件夹排序（sort 升序重写）。</summary>
    public async Task<List<Folder>> ReorderFoldersAsync(string scope, IReadOnlyList<int> orderedIds, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var idSet = (await db.Folders.AsNoTracking().Where(f => f.Scope == scope).Select(f => f.Id).ToListAsync(ct)).ToHashSet();
        var ordered = orderedIds.Where(idSet.Contains).ToList();
        var others = idSet.Where(id => !ordered.Contains(id)).ToList();

        int sort = 0;
        foreach (var fid in ordered.Concat(others))
        {
            await db.Folders.Where(f => f.Id == fid)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.Sort, sort), ct);
            sort++;
        }

        await PublishChangedAsync(ct);
        return await GetFoldersAsync(scope, ct);
    }

    // ---- 内部 ----

    private static async Task<bool> FolderNameExistsAsync(
        ComfyDbContext db, string scope, string name, int excludeId, CancellationToken ct)
    {
        var key = name.Trim().ToLowerInvariant();
        if (key.Length == 0) return false;
        var rows = await db.Folders.AsNoTracking()
            .Where(f => f.Scope == scope)
            .Select(f => new { f.Id, f.Name })
            .ToListAsync(ct);
        return rows.Any(f => f.Id != excludeId && f.Name.Trim().ToLowerInvariant() == key);
    }

    private static string GetThumbFileName(string relativePath, int maxDimension)
    {
        var name = Path.GetFileNameWithoutExtension(relativePath);
        return $"{name}_{maxDimension}.jpg";
    }

    private static void TryDeleteFile(string path, List<string> errors)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                System.IO.File.Delete(path);
        }
        catch (Exception ex)
        {
            errors.Add($"{path}: {ex.Message}");
        }
    }

    private async Task PublishChangedAsync(CancellationToken ct)
    {
        await Task.CompletedTask;
        try
        {
            _notice.Publish(EventNameConst.PromptLibraryChangedEvent, null);
        }
        catch (Exception ex)
        {
            _log.Warn(string.Format(Localizer.Instance?["PromptPublishChangedFailed"] ?? "", ex.Message), "Folder", ex);
        }
    }
}
