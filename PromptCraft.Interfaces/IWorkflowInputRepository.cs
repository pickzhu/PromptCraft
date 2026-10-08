using PromptCraft.Models.ComfyUI;

namespace PromptCraft.Interfaces;

/// <summary>工作流输入字段仓储：批量保存。</summary>
public interface IWorkflowInputRepository
{
    Task SaveRangeAsync(IEnumerable<WorkflowInput> inputs);
}
