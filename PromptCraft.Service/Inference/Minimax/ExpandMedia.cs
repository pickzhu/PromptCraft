// 迁移基准：app/electron/utils/expandMedia.js（enumerateTaggedMedia / normalizeMediaExpandOutput 等）

using System.Text.RegularExpressions;

namespace PromptCraft.Service.Inference.Minimax;

public sealed class TaggedMediaItem
{
    public string Path { get; init; } = "";
    public string Kind { get; init; } = "file"; // image | video | audio | file
    public int N { get; init; }
    public string Tag { get; init; } = "";
    public string Base { get; init; } = "";
}

/// <summary>媒体路径分类与 <Picture N>/<Video N>/<Audio N> 编号（expandMedia.js）。</summary>
public static class ExpandMedia
{
    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };
    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".avi", ".mkv", ".webm" };
    private static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg" };

    public static string ClassifyMediaPath(string? filePath)
    {
        var ext = Path.GetExtension(filePath ?? "").ToLowerInvariant();
        if (ImageExts.Contains(ext)) return "image";
        if (VideoExts.Contains(ext)) return "video";
        if (AudioExts.Contains(ext)) return "audio";
        return "file";
    }

    /// <summary>enumerateTaggedMedia：分类计数并生成尖括号标签，顺序与输入一致。</summary>
    public static List<TaggedMediaItem> EnumerateTaggedMedia(IEnumerable<string>? mediaPaths)
    {
        var list = mediaPaths is null ? new List<string>() : mediaPaths.ToList();
        var counts = new Dictionary<string, int> { ["image"] = 0, ["video"] = 0, ["audio"] = 0, ["file"] = 0 };
        var items = new List<TaggedMediaItem>();
        foreach (var raw in list)
        {
            var p = (raw ?? "").Trim();
            if (p.Length == 0) continue;
            var kind = ClassifyMediaPath(p);
            counts[kind] += 1;
            var n = counts[kind];
            var tag = kind == "image" ? $"<Picture {n}>"
                : kind == "video" ? $"<Video {n}>"
                : kind == "audio" ? $"<Audio {n}>"
                : $"<File {n}>";
            items.Add(new TaggedMediaItem { Path = p, Kind = kind, N = n, Tag = tag, Base = Path.GetFileName(p) });
        }
        return items;
    }

    // ---------- normalizeMediaExpandOutput ----------

    private const string LegacyGateStart = "<<<SKILL_GATE>>>";
    private const string LegacyGateEnd = "<<<END_SKILL_GATE>>>";

    private static readonly string[] H3FieldLabels =
    {
        "integrated_multimodal_description", "overall_soundscape", "non_diegetic_music",
        "subject_definitions", "summary", "retention_analysis", "detailed_description",
    };

    private static readonly Regex DeliverableFieldRe = new(
        @"product_facts|narrative_spine|director_motion|english_copy|anchor_brief|beat_storyboard|video_prompt|master_prompt|h3_prompt|storyboard|prompt_package|integrated_multimodal|overall_soundscape|subject_definitions|制作简报|分镜|锚定|视频提示|叙事脊柱",
        RegexOptions.IgnoreCase);

    private static string NormalizeOptionValue(object? raw)
    {
        if (raw == null) return "";
        var s = raw.ToString()?.Trim() ?? "";
        return s;
    }

    private static string FieldBody(Dictionary<string, object?> f)
    {
        var fromValue = f.TryGetValue("value", out var v) ? v?.ToString()?.Trim() ?? "" : "";
        if (fromValue.Length > 0) return fromValue;
        var fromDefault = f.TryGetValue("default", out var d) ? d : null;
        if (fromDefault != null)
        {
            var ds = fromDefault.ToString()?.Trim() ?? "";
            if (ds.Length > 0) return ds;
        }
        return "";
    }

    private static bool LooksLikeFinalPackageGate(Dictionary<string, object?> data)
    {
        var title = data.TryGetValue("title", out var t) ? t?.ToString() ?? "" : "";
        var summary = data.TryGetValue("summary", out var s) ? s?.ToString() ?? "" : "";
        if (Regex.IsMatch($"{title} {summary}",
                @"final\s*prompt\s*package|完整提示词包|最终提示词|prompt\s*package|complete\s*blueprint", RegexOptions.IgnoreCase))
        {
            return true;
        }
        var fields = data.TryGetValue("fields", out var fl) && fl is List<object?> fo ? fo : new List<object?>();
        if (fields.Count < 3) return false;
        var deliverableHits = 0;
        var longFilled = 0;
        foreach (var rawF in fields)
        {
            if (rawF is not Dictionary<string, object?> f) continue;
            var id = f.TryGetValue("id", out var i) ? i?.ToString() ?? "" : "";
            var label = f.TryGetValue("label", out var l) ? l?.ToString() ?? "" : "";
            if (DeliverableFieldRe.IsMatch($"{id} {label}")) deliverableHits += 1;
            if (FieldBody(f).Length >= 80) longFilled += 1;
        }
        if (deliverableHits >= 2) return true;
        if (longFilled >= 3 && fields.Count >= 4) return true;
        return false;
    }

    private static string FormatFinalPackageFromGateData(Dictionary<string, object?> data)
    {
        var lines = new List<string>();
        var title = (data.TryGetValue("title", out var t) ? t?.ToString() ?? "" : "").Trim();
        if (title.Length > 0) lines.Add($"## {title}");
        var summary = (data.TryGetValue("summary", out var s) ? s?.ToString() ?? "" : "").Trim();
        if (summary.Length > 0) { lines.Add(""); lines.Add(summary); }
        var fields = data.TryGetValue("fields", out var fl) && fl is List<object?> fo ? fo : new List<object?>();
        foreach (var rawF in fields)
        {
            if (rawF is not Dictionary<string, object?> f) continue;
            var label = ((f.TryGetValue("label", out var l) ? l?.ToString() ?? "" : "")
                ?? (f.TryGetValue("id", out var i) ? i?.ToString() ?? "" : "")).Trim();
            var body = FieldBody(f);
            if (label.Length == 0 && body.Length == 0) continue;
            lines.Add("");
            lines.Add($"### {(label.Length > 0 ? label : "Section")}");
            if (body.Length > 0)
            {
                lines.Add(body);
            }
            else if (f.TryGetValue("type", out var ty) && ty?.ToString() == "select"
                     && f.TryGetValue("options", out var op) && op is List<object?> opts && opts.Count > 0)
            {
                lines.Add(NormalizeOptionValue(opts[0]));
            }
        }
        return string.Join("\n", lines).Trim();
    }

    /// <summary>提取遗留 <<<SKILL_GATE>>> JSON（extractLegacyGateJson）。</summary>
    private static (Dictionary<string, object?>? Data, int Start, int End) ExtractLegacyGateJson(string text)
    {
        var start = text.IndexOf(LegacyGateStart, StringComparison.Ordinal);
        var end = text.IndexOf(LegacyGateEnd, StringComparison.Ordinal);
        if (start < 0 || end < 0 || end <= start) return (null, -1, -1);
        var raw = text.Substring(start + LegacyGateStart.Length, end - start - LegacyGateStart.Length).Trim();
        try
        {
            var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(raw);
            if (data == null) return (null, -1, -1);
            if (data.TryGetValue("fields", out var fl) && fl is not List<object?>) return (null, -1, -1);
            return (data, start, end);
        }
        catch
        {
            return (null, -1, -1);
        }
    }

    /// <summary>去掉遗留 SKILL_GATE 标记；若误包最终包则拆成正文（unwrapLegacyGateOutput）。</summary>
    public static string UnwrapLegacyGateOutput(string text)
    {
        var raw = (text ?? "").ToString();
        var extracted = ExtractLegacyGateJson(raw);
        if (extracted.Data != null)
        {
            var (data, start, end) = extracted;
            if (LooksLikeFinalPackageGate(data))
            {
                return FormatFinalPackageFromGateData(data);
            }
            var before = raw.Substring(0, start).Trim();
            var after = raw.Substring(end + LegacyGateEnd.Length).Trim();
            var combined = string.Join("\n\n", new[] { before, after }.Where(x => x.Length > 0)).Trim();
            if (combined.Length > 0) return combined;
            var summary = (data.TryGetValue("summary", out var s) ? s?.ToString() ?? "" : "").Trim();
            if (summary.Length > 0) return summary;
        }
        var gateRe = new Regex(
            Regex.Escape(LegacyGateStart) + @"[\s\S]*?" + Regex.Escape(LegacyGateEnd), RegexOptions.IgnoreCase);
        var stripped = gateRe.Replace(raw, "").Trim();
        return stripped.Length > 0 ? stripped : raw.Trim();
    }

    private static bool LooksLikeExpandPreambleBlock(string? head)
    {
        var t = (head ?? "").Trim();
        if (t.Length == 0) return false;
        if (Regex.IsMatch(t, @"^#{1,6}\s")) return false;
        if (Regex.IsMatch(t, @"^\*\*[^*]+\*\*")) return false;
        if (Regex.IsMatch(t, @"^(?:integrated_multimodal_description|overall_soundscape|subject_definitions|summary|detailed_description)\s*:", RegexOptions.IgnoreCase))
        {
            return false;
        }
        return Regex.IsMatch(t, @"^(根据已确认|问询历史|前置决策|现在进入|最终交付|以下是|为\s*\*?MiniMax|所有前置)", RegexOptions.IgnoreCase);
    }

    /// <summary>剥离扩写结果前导元信息（stripExpandFinalPreamble）。</summary>
    public static string StripExpandFinalPreamble(string text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return s;

        var hrSplit = Regex.Split(s, @"\n-{3,}\n");
        if (hrSplit.Length >= 2 && LooksLikeExpandPreambleBlock(hrSplit[0]))
        {
            s = string.Join("\n---\n", hrSplit.Skip(1)).Trim();
        }

        var lines = s.Split('\n').ToList();
        var start = 0;
        while (start < lines.Count)
        {
            var line = lines[start].Trim();
            if (line.Length == 0) { start += 1; continue; }
            if (Regex.IsMatch(line, @"^#{1,6}\s") || Regex.IsMatch(line, @"^\*\*[^*]+\*\*")
                || Regex.IsMatch(line, @"^[A-Za-z_][\w]*\s*:"))
            {
                break;
            }
            var isMeta = Regex.IsMatch(line, @"^(根据已确认|问询历史|前置决策|现在进入|最终交付|以下是|为\s*\*?MiniMax|所有前置)", RegexOptions.IgnoreCase);
            if (!isMeta) break;
            start += 1;
        }
        if (start > 0 && start < lines.Count)
        {
            s = string.Join("\n", lines.Skip(start)).Trim();
        }
        return s;
    }

    /// <summary>媒体型扩写结果归一化（normalizeMediaExpandOutput）。</summary>
    public static string NormalizeMediaExpandOutput(string text)
    {
        var s = UnwrapLegacyGateOutput(text);
        s = StripExpandFinalPreamble(s);
        if (s.Length == 0) return s;
        var labelAlt = string.Join("|", H3FieldLabels.Select(x => x.Replace("_", "[_]?")));
        var re = new Regex($"^((?:{labelAlt}))\\s*:\\s*(?=\\S)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        s = re.Replace(s, "$1:\n");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }
}
