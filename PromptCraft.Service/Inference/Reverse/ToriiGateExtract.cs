using System.Text.Json;

namespace PromptCraft.Service.Inference.Reverse;

// ============================================================
// ToriiGate 结构化输出抽取（extract mode）
// 1:1 移植自 app/electron/service/inference/torii_gate_extract.js
// ============================================================

public static class ToriiGateExtract
{
    private static string ExtractFull(string? text) => (text ?? "").Trim();

    private static string ExtractLongDescription(string text)
    {
        var t = text ?? "";
        var patterns = new[]
        {
            new System.Text.RegularExpressions.Regex(@"#\s*3\.\s*Long description\s*\n+(.*?)(?=\n#\s*4\.|\Z)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline),
            new System.Text.RegularExpressions.Regex(@"#\s*3\.\s*Structured description\s*\n+(.*?)(?=\n##\s*Image effects|\Z)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline),
            new System.Text.RegularExpressions.Regex(@"#\s*3\.\s*Long description\s*\n+(.*?)(?=\n#\s|\Z)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline),
        };
        foreach (var pat in patterns)
        {
            var m = pat.Match(t);
            if (m.Success)
                return m.Groups[1].Value.Trim();
        }
        return ExtractFull(t);
    }

    private static string ExtractMjStyle(string text)
    {
        var t = text ?? "";
        var m = System.Text.RegularExpressions.Regex.Match(
            t,
            @"###\s*3\.\s*Midjourney-Style Summary:\s*\n+(.*?)(?=\n###\s*4\.|\Z)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        if (m.Success)
            return m.Groups[1].Value.Trim();
        return ExtractFull(t);
    }

    private static string ExtractKeyDetails(string text)
    {
        var t = text ?? "";
        var m = System.Text.RegularExpressions.Regex.Match(
            t,
            @"#\s*2\.\s*Key details\s*\n+(.*?)(?=\n#\s*3\.|\Z)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        if (m.Success)
            return m.Groups[1].Value.Trim();
        return ExtractFull(t);
    }

    private static string ExtractMinMdBody(string? text)
    {
        var t = (text ?? "").Trim();
        var m = System.Text.RegularExpressions.Regex.Match(t, @"(#\s*3\.\s*Structured description.*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        if (m.Success)
            return m.Groups[1].Value.Trim();
        t = System.Text.RegularExpressions.Regex.Replace(t, @"^#\s*1\.\s*Thoughts about characters[\s\S]*?(?=#\s*3\.)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        t = System.Text.RegularExpressions.Regex.Replace(t, @"^#\s*2\.\s*Key details[\s\S]*?(?=#\s*3\.)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return t.Trim().Length > 0 ? t.Trim() : ExtractFull(text);
    }

    private static Dictionary<string, object?>? TryParseJsonBlob(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0)
            return null;
        var start = t.IndexOf('{');
        if (start < 0)
            return null;
        var depth = 0;
        for (var i = start; i < t.Length; i++)
        {
            if (t[i] == '{')
                depth += 1;
            else if (t[i] == '}')
            {
                depth -= 1;
                if (depth == 0)
                {
                    var chunk = t.Substring(start, i - start + 1);
                    try
                    {
                        return JsonSerializer.Deserialize<Dictionary<string, object?>>(chunk);
                    }
                    catch
                    {
                        var chunk2 = chunk.Replace(",\n}", "\n}").Replace(",\n]", "\n]");
                        try
                        {
                            return JsonSerializer.Deserialize<Dictionary<string, object?>>(chunk2);
                        }
                        catch
                        {
                            return null;
                        }
                    }
                }
            }
        }
        return null;
    }

    private static string ExtractJsonFlat(string text)
    {
        var obj = TryParseJsonBlob(text);
        if (obj == null)
            return ExtractFull(text);
        var parts = new List<string>();
        foreach (var (k, v) in obj)
        {
            if (v == null || string.IsNullOrWhiteSpace(Convert.ToString(v)) || Convert.ToString(v)!.Trim().ToLowerInvariant() == "none")
                continue;
            parts.Add($"{k}: {Convert.ToString(v)!.Trim()}");
        }
        return parts.Count > 0 ? string.Join("\n\n", parts).Trim() : ExtractFull(text);
    }

    private static string ExtractJsonRaw(string text)
    {
        var obj = TryParseJsonBlob(text);
        if (obj != null)
            return JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
        return ExtractFull(text);
    }

    private static readonly Dictionary<string, Func<string, string>> Extractors = new(StringComparer.Ordinal)
    {
        ["full"] = ExtractFull,
        ["long_desc"] = ExtractLongDescription,
        ["mj_style"] = ExtractMjStyle,
        ["key_details"] = ExtractKeyDetails,
        ["min_md_body"] = ExtractMinMdBody,
        ["json_flat"] = ExtractJsonFlat,
        ["json_raw"] = ExtractJsonRaw,
    };

    /// <summary>按 extract mode 抽取（applyExtractMode）。</summary>
    public static string ApplyExtractMode(string raw, string? mode)
    {
        var fn = Extractors.TryGetValue(mode ?? "full", out var f) ? f : ExtractFull;
        var outText = fn(raw);
        return string.IsNullOrEmpty(outText) ? ExtractFull(raw) : outText;
    }
}
