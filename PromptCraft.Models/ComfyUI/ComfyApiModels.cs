using System.Text.Json;
using System.Text.Json.Serialization;

namespace PromptCraft.Models.ComfyUI;

// ============================================================
// ComfyUI HTTP API 契约模型（从 PromptCraft.Service.ComfyServices / ComfyUIService 提升）。
// 纯 BCL 依赖，供 PromptCraft.Interfaces 契约与 Service 实现共同引用。
// ============================================================

/// <summary>
/// ComfyUI 连接配置（ComfyUI 页面使用；ComfyOutputDir 由启动期注入）。
/// 业务库固定位于 %LOCALAPPDATA%/PromptCraft/comfy_data/comfyui.db；
/// 缩略图目录由工作空间 cache\thumbnails 在 DI 组装时注入。
/// </summary>
public class ComfySettings
{
    public string ComfyApiUrl { get; set; } = "http://127.0.0.1:8188";
    public string ComfyOutputDir { get; set; } = "";
    public int ThumbMaxDimension { get; set; } = 300;
    public int ThumbQuality { get; set; } = 80;

    /// <summary>业务库绝对路径：%LOCALAPPDATA%/PromptCraft/comfy_data/comfyui.db。</summary>
    public static string DbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PromptCraft", "comfy_data", "comfyui.db");

    /// <summary>缩略图目录（由 DI 组装为 &lt;工作空间&gt;\cache\thumbnails）。</summary>
    public string ThumbDir { get; set; } = "";
}

/// <summary>图片同步结果统计（ImageSyncService.SyncAsync 返回）。</summary>
public class SyncResult
{
    public int NewFiles { get; set; }
    public int UpdatedFiles { get; set; }
    public int DeletedFiles { get; set; }
    public int Errors { get; set; }

    /// <summary>无 ComfyUI 元数据而被跳过的图片数（判定为非 ComfyUI 生成）</summary>
    public int SkippedFiles { get; set; }
    public List<string> ErrorMessages { get; set; } = new();
}

/// <summary>图片删除结果（单张/批量共用）</summary>
public class ImageDeleteResult
{
    /// <summary>成功删除并已从数据库移除的图片数</summary>
    public int Deleted { get; set; }

    /// <summary>因文件删除失败而跳过数据库删除的图片数</summary>
    public int Failed { get; set; }

    /// <summary>失败明细（文件路径 + 原因）</summary>
    public List<string> Errors { get; set; } = new();
}

/// <summary>工作流同步结果统计（ComfyUIService.SyncWorkflowsFromComfyAsync 返回）。</summary>
public class WorkflowSyncResult
{
    public int New { get; set; }
    public int Updated { get; set; }
    public int Errors { get; set; }
    public List<string> ErrorMessages { get; set; } = new();
}

/// <summary>一次执行任务的进度快照（JobProgressChanged 事件载荷，后台线程触发）。</summary>
public sealed class WorkflowJobProgress
{
    public int JobId { get; set; }
    public string Status { get; set; } = "";
    public int ProgressValue { get; set; }
    public int ProgressMax { get; set; }
    public string? NodeLogLine { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>队列状态响应（GET /queue）。</summary>
public class QueueStatusResponse
{
    public List<RunningItem> Running { get; set; } = new();
    public List<PendingItem> Pending { get; set; } = new();
    public List<HistoryItem> History { get; set; } = new();
}

public class RunningItem
{
    public string PromptId { get; set; } = string.Empty;
    public int NodeCount { get; set; }
    public int Completed { get; set; }
    public Dictionary<string, object> Prompt { get; set; } = new();
    public Dictionary<string, object> Nodes { get; set; } = new();
}

public class PendingItem
{
    public string PromptId { get; set; } = string.Empty;
    public Dictionary<string, object> Prompt { get; set; } = new();
    public Dictionary<string, object> Nodes { get; set; } = new();
}

public class HistoryItem
{
    public string PromptId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public Dictionary<string, object> Prompt { get; set; } = new();
    public Dictionary<string, object> Nodes { get; set; } = new();
    public DateTime CompletedAt { get; set; }
    public Dictionary<string, object> Outputs { get; set; } = new();
}

/// <summary>节点定义集合（GET /object_info）：节点类型名 → 定义。</summary>
public class ObjectInfoResponse
{
    public Dictionary<string, NodeObjectInfo> Nodes { get; set; } = new();
}

/// <summary>单个节点类型定义（input.required/optional 决定 widget 输入映射）。</summary>
public class NodeObjectInfo
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Category { get; set; }
    public NodeObjectInput Input { get; set; } = new();
    public Dictionary<string, object>? Output { get; set; }

    public static NodeObjectInfo Parse(string name, JsonElement el)
    {
        var info = new NodeObjectInfo { Name = name };
        if (el.ValueKind != JsonValueKind.Object) return info;

        if (el.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String)
            info.DisplayName = dn.GetString() ?? name;
        if (el.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String)
            info.Description = desc.GetString();
        if (el.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.String)
            info.Category = cat.GetString();

        // input: { required: { 字段: [type, options] }, optional: { ... } }
        if (el.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
        {
            foreach (var group in input.EnumerateObject())
            {
                var target = group.Name == "optional" ? info.Input.Optional : info.Input.Required;
                if (group.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var field in group.Value.EnumerateObject())
                    target[field.Name] = NodeField.Parse(field.Name, field.Value);
            }
        }

        if (el.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object)
        {
            var dict = new Dictionary<string, object>();
            foreach (var p in output.EnumerateObject()) dict[p.Name] = p.Value.Clone();
            info.Output = dict;
        }
        return info;
    }
}

public class NodeObjectInput
{
    public Dictionary<string, NodeField> Required { get; set; } = new();
    public Dictionary<string, NodeField> Optional { get; set; } = new();

    /// <summary>required + optional 的字段名（保持定义顺序），供 widget 值按序映射。</summary>
    public List<string> AllFieldNames()
    {
        var names = new List<string>(Required.Keys);
        foreach (var k in Optional.Keys)
        {
            if (!names.Contains(k)) names.Add(k);
        }
        return names;
    }
}

public class NodeField
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public object? Default { get; set; }
    public object? Options { get; set; }

    /// <summary>type 以 "*" 开头的是连线输入（模型/图像等），不是 widget 输入。</summary>
    public bool IsLinkType => Type.StartsWith("*", StringComparison.Ordinal);

    public static NodeField Parse(string name, JsonElement el)
    {
        var field = new NodeField { Name = name };
        // object_info 字段值为数组: [type, {default, min, max, ...}]
        if (el.ValueKind != JsonValueKind.Array || el.GetArrayLength() == 0) return field;
        if (el[0].ValueKind == JsonValueKind.String)
            field.Type = el[0].GetString() ?? "";
        if (el.GetArrayLength() >= 2 && el[1].ValueKind == JsonValueKind.Object)
        {
            if (el[1].TryGetProperty("default", out var def))
                field.Default = def.Clone();
            field.Options = el[1].Clone();
        }
        return field;
    }
}

/// <summary>ComfyUI 用户数据文件信息。Name 为相对 workflows 目录的路径；MTime 为 Unix 毫秒时间戳。</summary>
public class UserDataFileInfo
{
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public long MTime { get; set; }
}

public class SubmitWorkflowJsonResponse
{
    // ComfyUI 返回 snake_case（prompt_id/number/node_errors），用 JsonPropertyName 显式映射
    [JsonPropertyName("prompt_id")]
    public string? PromptId { get; set; }

    // ComfyUI 成功响应中 number 为整数（如 9），声明为 long 避免数字→string 反序列化失败
    [JsonPropertyName("number")]
    public long? Number { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("node_errors")]
    public Dictionary<string, object>? NodeErrors { get; set; }
}

public class SubmitWorkflowResponse
{
    public bool Success { get; set; }
    public string? PromptId { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>执行历史详情（GET /history/{prompt_id}），完成后可从中取输出图片。</summary>
public class HistoryDetailResponse
{
    public string? StatusStr { get; set; }
    public bool Completed { get; set; }
    public List<HistoryOutputImage> OutputImages { get; set; } = new();
    public string? StatusMessage { get; set; }

    public static HistoryDetailResponse Parse(JsonElement el)
    {
        var result = new HistoryDetailResponse();
        if (el.ValueKind != JsonValueKind.Object) return result;

        if (el.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object)
        {
            if (status.TryGetProperty("status_str", out var s) && s.ValueKind == JsonValueKind.String)
                result.StatusStr = s.GetString();
            if (status.TryGetProperty("completed", out var c) && c.ValueKind == JsonValueKind.True)
                result.Completed = true;
            if (status.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
            {
                foreach (var msg in msgs.EnumerateArray())
                {
                    if (msg.ValueKind != JsonValueKind.Array || msg.GetArrayLength() < 2) continue;
                    if (msg[0].ValueKind == JsonValueKind.String && msg[0].GetString() == "execution_error")
                    {
                        var data = msg[1];
                        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("exception_message", out var em)
                            && em.ValueKind == JsonValueKind.String)
                            result.StatusMessage = em.GetString();
                    }
                }
            }
        }

        if (el.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Object)
        {
            foreach (var node in outputs.EnumerateObject())
            {
                if (node.Value.ValueKind != JsonValueKind.Object) continue;
                if (node.Value.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
                {
                    foreach (var img in images.EnumerateArray())
                    {
                        if (img.ValueKind != JsonValueKind.Object) continue;
                        var output = new HistoryOutputImage { NodeId = node.Name };
                        if (img.TryGetProperty("filename", out var fn) && fn.ValueKind == JsonValueKind.String)
                            output.FileName = fn.GetString() ?? "";
                        if (img.TryGetProperty("subfolder", out var sf) && sf.ValueKind == JsonValueKind.String)
                            output.SubFolder = sf.GetString();
                        if (img.TryGetProperty("type", out var tp) && tp.ValueKind == JsonValueKind.String)
                            output.Type = tp.GetString();
                        result.OutputImages.Add(output);
                    }
                }
            }
        }
        return result;
    }
}

public class HistoryOutputImage
{
    public string NodeId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string? SubFolder { get; set; }
    public string? Type { get; set; }
}

/// <summary>一条 WebSocket 执行消息（类型 + prompt_id + 原始 data）。</summary>
public sealed record WsExecutionMessage(string Type, string PromptId, JsonElement Data);

