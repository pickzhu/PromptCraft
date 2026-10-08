using PromptCraft.Models.ComfyUI;

namespace PromptCraft.Interfaces;

/// <summary>
/// ComfyUI 业务服务：API 调用、工作流同步/执行/删除与数据持久化。
/// 所有关键操作均有日志埋点（category 统一用 "Workflow" / "ComfyUI"）。
/// </summary>
public interface IComfyUIService
{
    /// <summary>执行进度变化事件（后台线程触发，UI 需 Dispatcher 封送）。按 JobId 区分任务。</summary>
    event Action<WorkflowJobProgress>? JobProgressChanged;

    /// <summary>获取当前队列状态</summary>
    Task<QueueStatusResponse?> GetQueueStatusAsync();

    /// <summary>获取所有节点定义（供 UI→API 转换）</summary>
    Task<ObjectInfoResponse?> GetObjectInfoAsync();

    /// <summary>从 ComfyUI 拉取并同步工作流到本地库。</summary>
    Task<WorkflowSyncResult> SyncWorkflowsFromComfyAsync();

    /// <summary>提交并跟踪工作流执行（直到完成/失败）。</summary>
    Task<SubmitWorkflowResponse> SubmitAndTrackWorkflowAsync(int workflowId, string? overrideUiJson = null);

    /// <summary>提交图片工作流执行（图库页发起）。</summary>
    Task<(SubmitWorkflowResponse Response, int WorkflowId)> SubmitAndTrackImageWorkflowAsync(string workflowJson, string title);

    /// <summary>取某工作流的历史执行任务。</summary>
    Task<List<WorkflowJob>> GetWorkflowJobsAsync(int workflowId);

    /// <summary>删除图片工作流及其任务。</summary>
    Task<bool> DeleteImageWorkflowAsync(int imageWorkflowId);

    /// <summary>直接提交 prompt JSON（调试台/中转）。</summary>
    Task<SubmitWorkflowResponse> SubmitWorkflowAsync(string promptJson);

    /// <summary>删除工作流（连带任务与输出记录）。</summary>
    Task DeleteWorkflowAsync(Workflow workflow);
}
