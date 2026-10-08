using PromptCraft.Interfaces;
using PromptCraft.Models.ComfyUI;
using Microsoft.EntityFrameworkCore;

namespace PromptCraft.Data;

/// <summary>
/// Repository 统一约定：注入 <see cref="IDbContextFactory{TContext}"/>，每个方法内部
/// <c>using var db = await _dbFactory.CreateDbContextAsync();</c> 使用独立 DbContext。
/// 原因：Repository 注册为单例（被 ComfyUIService/ViewModel 单例持有），若构造注入长活 DbContext，
/// EF 查询会返回被追踪实体的内存旧值而非 DB 最新值（如执行历史 DB 已 completed，查询仍读到 queued）。
/// 每操作独立上下文 = 等效"瞬时数据库服务"，彻底消除追踪脏读与实体累积泄漏。
/// </summary>
public class ImageMetadataRepository : IImageMetadataRepository
{
    private readonly IDbContextFactory<ComfyDbContext> _dbFactory;
    public ImageMetadataRepository(IDbContextFactory<ComfyDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<List<ImageInfo>> GetAllAsync()
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ImageMetadata.AsNoTracking()
            .Include(x => x.Status).Include(x => x.ImageTags).ThenInclude(x => x.Tag)
            .OrderByDescending(x => x.CreatedAt).ToListAsync();
    }

    public async Task<ImageInfo?> GetByHashAsync(string hash)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ImageMetadata.AsNoTracking().FirstOrDefaultAsync(x => x.Hash == hash);
    }

    public async Task AddAsync(ImageInfo img)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        db.ImageMetadata.Add(img);
        await db.SaveChangesAsync();
    }

    public async Task AddRangeAsync(IEnumerable<ImageInfo> items)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        db.ImageMetadata.AddRange(items);
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(ImageInfo img)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        db.ImageMetadata.Remove(img);
        await db.SaveChangesAsync();
    }
}

public class ImageStatusRepository : IImageStatusRepository
{
    private readonly IDbContextFactory<ComfyDbContext> _dbFactory;
    public ImageStatusRepository(IDbContextFactory<ComfyDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task UpdateAsync(ImageStatus status)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        db.ImageStatuses.Update(status);
        await db.SaveChangesAsync();
    }
}

public class TagRepository : ITagRepository
{
    private readonly IDbContextFactory<ComfyDbContext> _dbFactory;
    public TagRepository(IDbContextFactory<ComfyDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<List<Tag>> GetAllAsync()
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Tags.AsNoTracking().ToListAsync();
    }

    public async Task<Tag?> GetByNameAsync(string name)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Tags.AsNoTracking().FirstOrDefaultAsync(x => x.Name == name);
    }

    public async Task<Tag> GetOrCreateAsync(string name, string category = "general")
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var tag = await db.Tags.FirstOrDefaultAsync(x => x.Name == name);
        if (tag != null) return tag;
        // 创建时从调色板随机取色（避开已用颜色），保存到 Tags.Color 供全局展示
        var used = TagPalette.NormalizeUsedColors(
            await db.Tags.Where(x => x.Color != null).Select(x => x.Color).ToListAsync());
        tag = new Tag { Name = name, Category = category, Color = TagPalette.PickDistinctColorHex(used) };
        db.Tags.Add(tag);
        await db.SaveChangesAsync();
        return tag;
    }

    /// <summary>删除标签：联动清理图片关联（ImageTag）与工作流关联（WorkflowTag，标签池共用），再删标签本体。</summary>
    public async Task DeleteAsync(int tagId)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        await db.ImageTags.Where(x => x.TagId == tagId).ExecuteDeleteAsync();
        await db.WorkflowTags.Where(x => x.TagId == tagId).ExecuteDeleteAsync();
        var tag = await db.Tags.FirstOrDefaultAsync(x => x.Id == tagId);
        if (tag != null)
        {
            db.Tags.Remove(tag);
            await db.SaveChangesAsync();
        }
    }
}

public class BlacklistRepository : IBlacklistRepository
{
    private readonly IDbContextFactory<ComfyDbContext> _dbFactory;
    public BlacklistRepository(IDbContextFactory<ComfyDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<bool> ExistsAsync(string hash)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        return await db.BlacklistedHashes.AnyAsync(x => x.Hash == hash);
    }
}

public class WorkflowRepository : IWorkflowRepository
{
    private readonly IDbContextFactory<ComfyDbContext> _dbFactory;
    public WorkflowRepository(IDbContextFactory<ComfyDbContext> dbFactory) => _dbFactory = dbFactory;

    /// <summary>获取全部未删除的工作流（列表页用），附带输入字段。只读查询用 AsNoTracking。</summary>
    public async Task<List<Workflow>> GetAllAsync()
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Workflows.AsNoTracking().Include(x => x.Inputs)
            .Where(x => !x.IsDeleted)
            .OrderByDescending(x => x.UpdatedAt)
            .ToListAsync();
    }

    public async Task<Workflow?> GetByIdAsync(int id)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Workflows.AsNoTracking().Include(x => x.Inputs).FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<Workflow?> GetBySourcePathAsync(string sourcePath)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Workflows.AsNoTracking().FirstOrDefaultAsync(x => x.SourcePath == sourcePath && !x.IsDeleted);
    }

    public async Task AddAsync(Workflow w)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        db.Workflows.Add(w);
        await db.SaveChangesAsync();
    }

    public async Task UpdateAsync(Workflow w)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        db.Workflows.Update(w);
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(Workflow w)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        db.Workflows.Remove(w);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 按 SourcePath 同步 upsert：已存在（未删除）则更新内容与时间戳，不存在则新增。
    /// 返回 (是否新增, 工作流)。单个独立上下文内完成查询与修改。
    /// </summary>
    public async Task<(bool Added, Workflow Workflow)> UpsertFromComfyAsync(string sourcePath, string name, string workflowJson, DateTime updatedAt)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.Workflows.FirstOrDefaultAsync(x => x.SourcePath == sourcePath && !x.IsDeleted);
        if (existing != null)
        {
            existing.Name = name;
            existing.WorkflowJsonBlob = ComfyMetadataCodec.Compress(workflowJson);
            existing.UpdatedAt = updatedAt.ToUniversalTime();
            await db.SaveChangesAsync();
            return (false, existing);
        }

        var workflow = new Workflow
        {
            Name = name,
            WorkflowJsonBlob = ComfyMetadataCodec.Compress(workflowJson),
            SourcePath = sourcePath,
            CreatedAt = updatedAt.ToUniversalTime(),
            UpdatedAt = updatedAt.ToUniversalTime()
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        return (true, workflow);
    }

    /// <summary>
    /// 逻辑删除工作流及其依赖数据（Inputs / Jobs / Outputs 全部置 IsDeleted）。
    /// 单个事务内完成，图片（ImageInfo）保留——JobOutput 对图片的引用不清除。
    /// </summary>
    public async Task<bool> SoftDeleteAsync(int workflowId)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var workflow = await db.Workflows
            .Include(x => x.Inputs)
            .Include(x => x.Jobs)
                .ThenInclude(x => x.Outputs)
            .FirstOrDefaultAsync(x => x.Id == workflowId && !x.IsDeleted);
        if (workflow == null) return false;

        workflow.IsDeleted = true;
        foreach (var input in workflow.Inputs)
            input.IsDeleted = true;
        foreach (var job in workflow.Jobs)
        {
            job.IsDeleted = true;
            foreach (var output in job.Outputs)
                output.IsDeleted = true;
        }
        await db.SaveChangesAsync();
        // 标签关联物理清除（标签池与图库共用；工作流已逻辑删除，关联无意义）
        await db.WorkflowTags.Where(x => x.WorkflowId == workflowId).ExecuteDeleteAsync();
        return true;
    }
}

public class WorkflowInputRepository : IWorkflowInputRepository
{
    private readonly IDbContextFactory<ComfyDbContext> _dbFactory;
    public WorkflowInputRepository(IDbContextFactory<ComfyDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task SaveRangeAsync(IEnumerable<WorkflowInput> inputs)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkflowInputs.AddRange(inputs);
        await db.SaveChangesAsync();
    }
}

public class JobRepository : IJobRepository
{
    private readonly IDbContextFactory<ComfyDbContext> _dbFactory;
    public JobRepository(IDbContextFactory<ComfyDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task AddAsync(WorkflowJob job)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkflowJobs.Add(job);
        await db.SaveChangesAsync();
    }

    public async Task UpdateAsync(WorkflowJob job)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkflowJobs.Update(job);
        await db.SaveChangesAsync();
    }

    /// <summary>工作流的执行历史（未删除，按时间倒序），附带输出记录。只读查询用 AsNoTracking。</summary>
    public async Task<List<WorkflowJob>> GetByWorkflowAsync(int workflowId)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        return await db.WorkflowJobs.AsNoTracking().Include(x => x.Outputs)
            .Where(x => x.WorkflowId == workflowId && !x.IsDeleted)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync();
    }

    public async Task<WorkflowJob?> GetByIdAsync(int jobId)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        return await db.WorkflowJobs.AsNoTracking().Include(x => x.Outputs).FirstOrDefaultAsync(x => x.Id == jobId && !x.IsDeleted);
    }

    /// <summary>逻辑删除单条执行记录及其输出（保留 DB 行以便审计；列表/详情查询均按 IsDeleted 过滤）。</summary>
    public async Task<bool> SoftDeleteJobAsync(int jobId)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var job = await db.WorkflowJobs.Include(x => x.Outputs)
            .FirstOrDefaultAsync(x => x.Id == jobId && !x.IsDeleted);
        if (job == null) return false;
        job.IsDeleted = true;
        foreach (var output in job.Outputs)
            output.IsDeleted = true;
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>逻辑删除某工作流的全部执行记录及其输出（"清空执行历史"）。</summary>
    public async Task<int> SoftDeleteByWorkflowAsync(int workflowId)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var jobs = await db.WorkflowJobs.Include(x => x.Outputs)
            .Where(x => x.WorkflowId == workflowId && !x.IsDeleted).ToListAsync();
        foreach (var job in jobs)
        {
            job.IsDeleted = true;
            foreach (var output in job.Outputs)
                output.IsDeleted = true;
        }
        await db.SaveChangesAsync();
        return jobs.Count;
    }
}

public class WorkflowParamsRepository : IWorkflowParamsRepository
{
    private readonly IDbContextFactory<ComfyDbContext> _dbFactory;
    public WorkflowParamsRepository(IDbContextFactory<ComfyDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<WorkflowParams?> GetByWorkflowAsync(int workflowId)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        return await db.WorkflowParamsSet.AsNoTracking().FirstOrDefaultAsync(x => x.WorkflowId == workflowId);
    }

    /// <summary>按 WorkflowId upsert 参数配置（每工作流至多一条）。</summary>
    public async Task<WorkflowParams> UpsertAsync(int workflowId, string paramsJson)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.WorkflowParamsSet.FirstOrDefaultAsync(x => x.WorkflowId == workflowId);
        if (existing == null)
        {
            existing = new WorkflowParams { WorkflowId = workflowId, ParamsJson = paramsJson, UpdatedAt = DateTime.UtcNow };
            db.WorkflowParamsSet.Add(existing);
        }
        else
        {
            existing.ParamsJson = paramsJson;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        return existing;
    }
}

public class JobOutputRepository : IJobOutputRepository
{
    private readonly IDbContextFactory<ComfyDbContext> _dbFactory;
    public JobOutputRepository(IDbContextFactory<ComfyDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task AddRangeAsync(IEnumerable<JobOutput> outputs)
    {
        using var db = await _dbFactory.CreateDbContextAsync();
        db.JobOutputs.AddRange(outputs);
        await db.SaveChangesAsync();
    }
}
