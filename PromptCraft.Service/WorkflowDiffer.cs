using PromptCraft.Data;
using Ke.Bee.Localization.Localizer;
using System.Text.Json;

namespace PromptCraft.Service;

/// <summary>单个字段/参数的差异（Left=图A值，Right=图B值）</summary>
public sealed record FieldDiff(string Field, string? Left, string? Right);

/// <summary>一个工作流节点的差异汇总</summary>
public sealed record NodeDiff(string NodeId, string? Title, string ClassType, List<FieldDiff> Changes);

/// <summary>
/// 工作流节点级 diff：解析两个 workflow JSON，按节点 id 对齐，
/// 对比每个节点的标题、类型与 widgets_values（参数值），输出存在差异的节点列表。
/// 供图片对比弹窗展示"这两个工作流改了什么"。
/// </summary>
public static class WorkflowDiffer
{
    /// <summary>两个工作流是否完全一致（按原始 JSON 的 SHA256 判断）</summary>
    public static bool SameWorkflow(JsonElement? a, JsonElement? b)
    {
        if (a is not JsonElement x || x.ValueKind != JsonValueKind.Object) return false;
        if (b is not JsonElement y || y.ValueKind != JsonValueKind.Object) return false;
        return ComfyMetadataCodec.ComputeJsonHash(x.GetRawText())
            == ComfyMetadataCodec.ComputeJsonHash(y.GetRawText());
    }

    /// <summary>逐节点对比，返回有差异的节点（含仅存在于单方的节点）</summary>
    public static List<NodeDiff> Diff(JsonElement? a, JsonElement? b)
    {
        var result = new List<NodeDiff>();
        var nodesA = GetNodes(a);
        var nodesB = GetNodes(b);
        if (nodesA.Count == 0 && nodesB.Count == 0) return result;

        var ids = new SortedSet<string>(
            nodesA.Keys.Concat(nodesB.Keys),
            Comparer<string>.Create((x, y) =>
                int.TryParse(x, out var xi) && int.TryParse(y, out var yi)
                    ? xi.CompareTo(yi)
                    : string.CompareOrdinal(x, y)));

        foreach (var id in ids)
        {
            nodesA.TryGetValue(id, out var elA);
            nodesB.TryGetValue(id, out var elB);

            var changes = new List<FieldDiff>();

            // 存在性
            if (elA.ValueKind == JsonValueKind.Undefined)
            {
                changes.Add(new FieldDiff(Localizer.Instance?["NodeExists"] ?? "", null, Localizer.Instance?["RightOnly"] ?? ""));
                result.Add(new NodeDiff(id, GetTitle(elB), GetType(elB) ?? "", changes));
                continue;
            }
            if (elB.ValueKind == JsonValueKind.Undefined)
            {
                changes.Add(new FieldDiff(Localizer.Instance?["NodeExists"] ?? "", Localizer.Instance?["LeftOnly"] ?? "", null));
                result.Add(new NodeDiff(id, GetTitle(elA), GetType(elA) ?? "", changes));
                continue;
            }

            // 标题 / 类型
            string? titleA = GetTitle(elA), titleB = GetTitle(elB);
            if (titleA != titleB) changes.Add(new FieldDiff(Localizer.Instance?["NodeTitle"] ?? "", titleA, titleB));
            string? typeA = GetType(elA), typeB = GetType(elB);
            if (typeA != typeB) changes.Add(new FieldDiff(Localizer.Instance?["NodeType"] ?? "", typeA, typeB));

            // widgets_values（节点参数值，按索引对齐）
            var wa = GetWidgets(elA);
            var wb = GetWidgets(elB);
            if (wa is not null || wb is not null)
            {
                int max = Math.Max(wa?.Count ?? 0, wb?.Count ?? 0);
                for (int i = 0; i < max; i++)
                {
                    string? va = wa is not null && i < wa.Count ? Short(wa[i]) : null;
                    string? vb = wb is not null && i < wb.Count ? Short(wb[i]) : null;
                    if (va != vb) changes.Add(new FieldDiff(string.Format(Localizer.Instance?["ParamIndex"] ?? "", i), va, vb));
                }
            }

            if (changes.Count > 0)
                result.Add(new NodeDiff(id, titleA ?? titleB, typeA ?? typeB ?? "", changes));
        }

        return result;
    }

    /// <summary>解析 workflow JSON 的 nodes 数组为 id → 节点</summary>
    private static Dictionary<string, JsonElement> GetNodes(JsonElement? workflow)
    {
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (workflow is not JsonElement wf || wf.ValueKind != JsonValueKind.Object) return dict;
        if (!wf.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array) return dict;
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            if (!node.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number) continue;
            dict[id.GetRawText()] = node;
        }
        return dict;
    }

    private static string? GetTitle(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) return null;
        if (node.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            if (props.TryGetProperty("Node name for S&R", out var t) && t.ValueKind == JsonValueKind.String)
                return t.GetString();
            if (props.TryGetProperty("node", out var t2) && t2.ValueKind == JsonValueKind.String)
                return t2.GetString();
        }
        if (node.TryGetProperty("title", out var nt) && nt.ValueKind == JsonValueKind.String)
            return nt.GetString();
        return null;
    }

    private static string? GetType(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) return null;
        return node.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;
    }

    private static List<JsonElement>? GetWidgets(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) return null;
        if (!node.TryGetProperty("widgets_values", out var wv) || wv.ValueKind != JsonValueKind.Array)
            return null;
        return wv.EnumerateArray().Select(x => x.Clone()).ToList();
    }

    /// <summary>值文本（字符串取原文、其余取 JSON 原文，过长截断，避免列表爆炸）</summary>
    private static string? Short(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => null,
        JsonValueKind.String => el.GetString(),
        _ => Truncate(el.GetRawText(), 200),
    };

    private static string? Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Length <= max ? s : s[..max] + "…";
    }
}
