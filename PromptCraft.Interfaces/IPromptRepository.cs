using PromptCraft.Models;
using PromptCraft.Models.ComfyUI;

namespace PromptCraft.Interfaces;

/// <summary>词库查询条件。FolderId 为统一 Folder 表主键（null=全部；0=未分类即无任何文件夹关联；&gt;0=包含该文件夹关联，多对多）。</summary>
public sealed record PromptQuery(
    string? Keyword = null,
    int? FolderId = null,
    string? TagId = null,
    int Page = 1,
    int PageSize = 50);

/// <summary>提示词库仓储契约（对齐源 pm_library_db.js 的 upsert 与删除连带语义）。</summary>
public interface IPromptRepository
{
    Task<Prompt?> GetAsync(string id, CancellationToken ct);
    Task<(IReadOnlyList<Prompt> Items, int Total)> QueryAsync(PromptQuery q, CancellationToken ct);
    Task<Prompt> SaveAsync(Prompt p, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
}
