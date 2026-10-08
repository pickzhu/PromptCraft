using PromptCraft.Models.ComfyUI;

namespace PromptCraft.Interfaces;

/// <summary>
/// ComfyUI 原始 HTTP API 客户端：连接测试、队列状态、节点定义、用户数据工作流读写、prompt 提交与历史查询。
/// </summary>
public interface IComfyUIClient
{
    /// <summary>测试与 ComfyUI 服务的连接。</summary>
    Task<bool> TestConnectionAsync();

    /// <summary>获取当前队列状态。</summary>
    Task<QueueStatusResponse?> GetQueueStatusAsync();

    /// <summary>获取所有节点定义（供 UI→API 转换）。</summary>
    Task<ObjectInfoResponse?> GetObjectInfoAsync();

    /// <summary>列出用户数据目录下的工作流文件。</summary>
    Task<List<UserDataFileInfo>> ListUserDataWorkflowsAsync();

    /// <summary>读取用户数据文件内容。</summary>
    Task<string?> GetUserDataFileAsync(string file);

    /// <summary>保存用户数据文件。</summary>
    Task<bool> SaveUserDataFileAsync(string file, string content);

    /// <summary>删除用户数据文件。</summary>
    Task<bool> DeleteUserDataFileAsync(string file);

    /// <summary>提交 prompt JSON 到 ComfyUI 执行。</summary>
    Task<SubmitWorkflowResponse> SubmitWorkflowAsync(string promptJson, string clientId, string? uiWorkflowJson = null);

    /// <summary>查询某次提交的历史执行详情。</summary>
    Task<HistoryDetailResponse?> GetHistoryAsync(string promptId);
}
