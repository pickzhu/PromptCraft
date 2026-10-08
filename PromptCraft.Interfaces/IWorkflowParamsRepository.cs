using PromptCraft.Models.ComfyUI;

namespace PromptCraft.Interfaces;

/// <summary>工作流参数配置仓储：按工作流查询与 upsert。</summary>
public interface IWorkflowParamsRepository
{
    Task<WorkflowParams?> GetByWorkflowAsync(int workflowId);
    Task<WorkflowParams> UpsertAsync(int workflowId, string paramsJson);
}
