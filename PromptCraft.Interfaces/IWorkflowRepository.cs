using PromptCraft.Models.ComfyUI;

namespace PromptCraft.Interfaces;

/// <summary>工作流仓储：CRUD、从 ComfyUI 同步 upsert、逻辑删除。</summary>
public interface IWorkflowRepository
{
    Task<List<Workflow>> GetAllAsync();
    Task<Workflow?> GetByIdAsync(int id);
    Task<Workflow?> GetBySourcePathAsync(string sourcePath);
    Task AddAsync(Workflow w);
    Task UpdateAsync(Workflow w);
    Task DeleteAsync(Workflow w);
    Task<(bool Added, Workflow Workflow)> UpsertFromComfyAsync(string sourcePath, string name, string workflowJson, DateTime updatedAt);
    Task<bool> SoftDeleteAsync(int workflowId);
}
