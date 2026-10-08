using PromptCraft.Models;
using PromptCraft.Models.ComfyUI;

namespace PromptCraft.Interfaces;

/// <summary>
/// 提示词库服务（提示词 + 标签；文件夹已抽到统一的 <see cref="IFolderService"/>）。
/// 对齐源库 pm_library_db.js：文件夹/标签同名拒绝；删除即物理删除；
/// prompt 保存为 upsert（保留 created_at）。标签使用全局标签池（Tag，与图库/工作流共用）。
/// </summary>
public interface IPromptLibraryService
{
    /// <summary>取全局标签池（与图库/工作流共用同一套标签）。</summary>
    Task<List<Tag>> GetTagsAsync(CancellationToken ct = default);

    /// <summary>保存全局标签（同名拒绝，忽略大小写）。</summary>
    Task<Tag> SaveGlobalTagAsync(string? id, string name, CancellationToken ct = default);

    /// <summary>删除全局标签（同时清理所有映射）。</summary>
    Task DeleteGlobalTagAsync(string id, CancellationToken ct = default);

    Task<Prompt?> GetPromptAsync(string id, CancellationToken ct = default);
    Task<(IReadOnlyList<Prompt> Items, int Total)> QueryAsync(PromptQuery q, CancellationToken ct = default);
    Task<Prompt> SavePromptAsync(Prompt p, CancellationToken ct = default);
    Task DeletePromptsAsync(IReadOnlyList<string> ids, CancellationToken ct = default);

    /// <summary>批量打标签（对齐图库批量打标签语义：勾选集 = 最终标签集，替换式；空标签集 = 清空这批提示词的标签）。</summary>
    Task AssignTagsAsync(IReadOnlyList<string> promptIds, IReadOnlyList<string> tagIds, CancellationToken ct = default);
}
