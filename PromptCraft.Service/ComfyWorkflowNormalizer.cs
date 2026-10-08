using PromptCraft.Data;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PromptCraft.Service;

/// <summary>
/// 工作流"结构 / 参数"分离器。
/// 同步入库时工作流只保存结构（剔除节点 widget 参数与提示词文本，统一填默认值），
/// 因此同一工作流结构无论参数怎么改都只保存一份（WorkflowHash 按结构计算）；
/// 图片的真实参数由提示词表（ImagePrompts.NodesJsonBlob / 各参数列）按图片保存，
/// 详情展示时用 <see cref="Rehydrate"/> 把该图参数组装回完整工作流 JSON。
/// </summary>
public static class ComfyWorkflowNormalizer
{
    /// <summary>
    /// 剔除参数后的结构工作流 JSON（UI 格式）。
    /// 每个节点的 widgets_values 统一替换为默认值：string→""、number→0、bool→false、null→null，
    /// 保证同一结构工作流的哈希一致；节点 id/type/links/标题/布局等结构字段全部保留。
    /// 非 UI 格式（无 nodes 数组，如 API 格式）原样返回。
    /// </summary>
    public static string ToStructure(JsonElement workflow)
    {
        if (workflow.ValueKind != JsonValueKind.Object) return workflow.GetRawText();
        var root = JsonNode.Parse(workflow.GetRawText()) as JsonObject;
        if (root == null) return workflow.GetRawText();
        if (root["nodes"] is not JsonArray nodes) return workflow.GetRawText(); // API 格式等：无 widgets_values 概念

        foreach (var node in nodes.OfType<JsonObject>())
        {
            // ① 顺序 widget 值（传统格式）
            if (node["widgets_values"] is JsonArray widgets)
            {
                for (int i = 0; i < widgets.Count; i++)
                {
                    widgets[i] = DefaultValue(widgets[i]);
                }
            }

            // ② 命名 widget 值（新版 ComfyUI 格式，同样含参数，必须归一化否则去重失效）
            if (node["widgets_values_named"] is JsonObject named)
            {
                foreach (var key in named.Select(k => k.Key).ToList())
                    named[key] = DefaultValue(named[key]);
            }

            // ③ properties.models 是模型下载清单（含模型名/URL 等参数），剔除
            if (node["properties"] is JsonObject props && props["models"] is not null)
                props.Remove("models");
        }
        return root.ToJsonString();
    }

    /// <summary>
    /// 把结构工作流与某张图的节点参数组装回完整工作流 JSON。
    /// <paramref name="nodes"/> 来自提示词表的 NodesJsonBlob（<see cref="NodeSnapshot"/> 列表，
    /// 含每节点的 UI widget 值），按 NodeId 匹配回填 widgets_values；
    /// 匹配不到或 Widgets 为空的节点保持结构中的默认值。
    /// </summary>
    public static string Rehydrate(string? structureJson, IReadOnlyList<NodeSnapshot>? nodes)
    {
        if (string.IsNullOrWhiteSpace(structureJson)) return structureJson ?? "";
        var map = nodes?.Where(n => !string.IsNullOrEmpty(n.NodeId))
            .ToDictionary(n => n.NodeId!, StringComparer.OrdinalIgnoreCase);
        if (map == null || map.Count == 0) return structureJson;

        try
        {
            var root = JsonNode.Parse(structureJson) as JsonObject;
            if (root == null || root["nodes"] is not JsonArray nodesArr) return structureJson;

            foreach (var node in nodesArr.OfType<JsonObject>())
            {
                var id = node["id"]?.ToString(); // UI 工作流 id 为数字，ToString 与 NodeSnapshot.NodeId 一致
                if (id == null || !map.TryGetValue(id, out var snap)) continue;

                // 顺序 widget 值（传统格式）
                if (node["widgets_values"] is JsonArray widgets && snap.Widgets.Count > 0)
                {
                    int count = Math.Min(widgets.Count, snap.Widgets.Count);
                    for (int i = 0; i < count; i++)
                        widgets[i] = CloneValue(snap.Widgets[i]);
                }

                // 命名 widget 值（新版 ComfyUI 格式）
                if (node["widgets_values_named"] is JsonObject named && snap.WidgetsNamed.Count > 0)
                {
                    foreach (var (key, val) in snap.WidgetsNamed)
                        named[key] = CloneValue(val);
                }
            }
            return root.ToJsonString();
        }
        catch
        {
            return structureJson; // 组装失败退回结构版，不阻塞展示
        }
    }

    /// <summary>参数剔除后统一填入的默认值（保证结构哈希一致）</summary>
    private static JsonNode? DefaultValue(JsonNode? value) => value switch
    {
        JsonValue v when v.TryGetValue<string>(out _) => "",
        JsonValue v when v.TryGetValue<int>(out _) => 0,
        JsonValue v when v.TryGetValue<bool>(out _) => false,
        _ => null, // 空/其他（含嵌套对象数组）统一置 null
    };

    private static JsonNode? CloneValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => JsonValue.Create(el.GetString()),
        JsonValueKind.Number => el.TryGetInt64(out var l) ? JsonValue.Create(l) : JsonValue.Create(el.GetDouble()),
        JsonValueKind.True => JsonValue.Create(true),
        JsonValueKind.False => JsonValue.Create(false),
        JsonValueKind.Null => null,
        _ => JsonNode.Parse(el.GetRawText()),
    };
}
