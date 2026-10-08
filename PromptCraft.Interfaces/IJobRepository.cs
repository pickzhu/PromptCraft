using PromptCraft.Models.ComfyUI;

namespace PromptCraft.Interfaces;

/// <summary>执行记录仓储：增改、按工作流查询、逻辑删除。</summary>
public interface IJobRepository
{
    Task AddAsync(WorkflowJob job);
    Task UpdateAsync(WorkflowJob job);
    Task<List<WorkflowJob>> GetByWorkflowAsync(int workflowId);
    Task<WorkflowJob?> GetByIdAsync(int jobId);
    Task<bool> SoftDeleteJobAsync(int jobId);
    Task<int> SoftDeleteByWorkflowAsync(int workflowId);
}
