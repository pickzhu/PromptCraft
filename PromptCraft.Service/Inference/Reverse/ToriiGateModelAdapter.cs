using PromptCraft.Models.Inference;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PromptCraft.Service.Inference.Reverse;

// ============================================================
// ToriiGate 模型适配
// 1:1 移植自 app/electron/service/inference/torii_gate_model_adapter.js
// ============================================================

public static class ToriiGateModelAdapter
{
    private static string NormalizeBooruValue(string value)
        => Regex.Replace((value ?? "").Trim().ToLowerInvariant().Replace("_", " "), @"\s+", " ").Trim();

    private static bool TagLikeString(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0 || v.ToLowerInvariant() == "none")
            return false;
        if (DanbooruPromptEngineering.LooksLikeTagProse(v))
            return false;
        return Regex.Matches(v, ",").Count >= 1 || v.Length <= 48;
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

    /// <summary>一行式标签工程（Danbooru / SD），与 Torii 自然语言 shell 冲突。</summary>
    public static bool IsTagLineReversePe(ReverseCaptionRequest caption)
    {
        if (caption == null)
            return false;
        var type = (caption.Type ?? "").Trim();
        var peFmt = (caption.PeOutputFormatValue ?? "").Trim();
        return peFmt == "danbooru_tags"
            || peFmt == "sd_tags"
            || type == "Danbooru_tag_list"
            || type == "Stable_Diffusion_Prompt";
    }

    /// <summary>自然语言类标准 PE 在 ToriiGate 上使用的 shell（仅 prose，不含 tag 工程）。</summary>
    private static string ToriiShellFormatForStandardPe(ReverseCaptionRequest _caption) => "long";

    /// <summary>
    /// ToriiGate 模型常输出 JSON（main_text / General 等），非 structured_json 工程时展平为正文。
    /// 标签类工程勿提取 main_text 叙事句。
    /// </summary>
    public static string UnwrapToriiGateJsonToProse(string text, bool keepStructuredJson = false, bool tagLine = false)
    {
        if (keepStructuredJson)
            return (text ?? "").Trim();
        var raw = (text ?? "").Trim();
        var obj = TryParseJsonBlob(raw);

        if (tagLine)
        {
            return obj != null ? UnwrapToriiGateJsonToTagLine(raw, obj) : raw;
        }

        if (!raw.StartsWith('{') && !raw.Contains("\"main_text\""))
            return raw;
        if (obj == null)
            return raw;

        if (obj.TryGetValue("main_text", out var mt) && mt is string mts && !string.IsNullOrWhiteSpace(mts))
            return mts.Trim();

        var parts = new List<string>();
#pragma warning disable CS8602 // 解引用可能出现空引用。
        if (obj.TryGetValue("General", out var gen) && gen != null && Convert.ToString(gen).Trim().Length > 0)
            parts.Add(Convert.ToString(gen).Trim());
#pragma warning restore CS8602 // 解引用可能出现空引用。
        foreach (var (k, v) in obj)
        {
            if (v == null || v is JsonElement { ValueKind: JsonValueKind.Object or JsonValueKind.Array })
                continue;
            var val = Convert.ToString(v)!.Trim();
            if (val.Length == 0 || val.ToLowerInvariant() == "none")
                continue;
            if (Regex.IsMatch(k, @"^character", RegexOptions.IgnoreCase))
                parts.Add(val);
        }
        foreach (var key in new[] { "background", "atmosphere", "image_effects", "texts" })
        {
            if (obj.TryGetValue(key, out var v) && v is string vs && !string.IsNullOrWhiteSpace(vs) && vs.Trim().ToLowerInvariant() != "none")
                parts.Add(vs.Trim());
        }
        if (parts.Count > 0)
            return string.Join("\n\n", parts).Trim();
        return raw;
    }

    private static string ComposeDanbooruTagLineFromJson(Dictionary<string, object?>? parsed)
    {
        if (parsed == null)
            return "";
        var defaults = new Dictionary<string, string>
        {
            ["artist"] = "unknown",
            ["copyright"] = "original",
            ["character"] = "none",
            ["meta"] = "none",
        };
        var prefixes = new List<string>();
        foreach (var k in new[] { "artist", "copyright", "character", "meta" })
        {
#pragma warning disable CS8604 // 引用类型参数可能为 null。
            string val;
#pragma warning disable CS8602 // 解引用可能出现空引用。
            if (parsed.TryGetValue(k, out var raw) && raw != null && Convert.ToString(raw).Trim().Length > 0 && Convert.ToString(raw).Trim().ToLowerInvariant() != "none")
                val = NormalizeBooruValue(Convert.ToString(raw));
#pragma warning restore CS8604 // 引用类型参数可能为 null。
            else
                val = defaults[k];
#pragma warning restore CS8602 // 解引用可能出现空引用。
            prefixes.Add($"{k}:{val}");
        }
        string general = "";
        foreach (var key in new[] { "tags", "general_tags", "general", "General" })
        {
            if (parsed.TryGetValue(key, out var v) && v is string vs && vs.Trim().Length > 0 && TagLikeString(vs))
            {
                general = vs.Trim().ToLowerInvariant().Replace("_", " ");
                break;
            }
        }
        if (general.Length == 0)
            return "";
        general = Regex.Replace(general, @"^(artist|copyright|character|meta):\s*[^,]+,\s*", "", RegexOptions.IgnoreCase);
        var joined = Regex.Replace($"{string.Join(", ", prefixes)}, {Regex.Replace(general, @"^,\s*", "")}", @",\s*,", ", ");
        return joined;
    }

    private static string ComposeSdTagLineFromJson(Dictionary<string, object?>? parsed)
    {
        if (parsed == null)
            return "";
        foreach (var key in new[] { "tags", "general_tags", "General", "general" })
        {
            if (parsed.TryGetValue(key, out var v) && v is string vs && vs.Trim().Length > 0 && !DanbooruPromptEngineering.LooksLikeTagProse(vs))
                return vs.Trim();
        }
        return "";
    }

    private static string UnwrapToriiGateJsonToTagLine(string raw, Dictionary<string, object?>? obj)
    {
        var parsed = obj ?? TryParseJsonBlob(raw);
        var fromDanbooru = ComposeDanbooruTagLineFromJson(parsed);
        if (fromDanbooru.Length > 0)
            return fromDanbooru;
        var fromSd = ComposeSdTagLineFromJson(parsed);
        if (fromSd.Length > 0)
            return fromSd;
        if (parsed == null)
            return raw;

        var skipKeys = new HashSet<string>
        {
            "main_text", "bounding_box", "General", "background", "atmosphere",
            "image_effects", "texts", "watermarks",
        };
        var tags = new List<string>();
        void PushPrefixed(string key, object? val)
        {
            if (val == null || val is JsonElement { ValueKind: JsonValueKind.Object or JsonValueKind.Array })
                return;
            var v = Convert.ToString(val)!.Trim();
            if (v.Length == 0 || v.ToLowerInvariant() == "none")
                return;
            var k = key.Trim().ToLowerInvariant();
            if (k.StartsWith("character", StringComparison.Ordinal))
                k = "character";
            if (k is "artist" or "copyright" or "character" or "meta")
                tags.Add($"{k}:{v.Replace("_", " ")}");
        }

        foreach (var p in new[] { "artist", "copyright", "character", "meta" })
        {
            foreach (var (k, v) in parsed)
            {
                var lk = k.ToLowerInvariant();
                if (lk == p || lk.StartsWith($"{p}_", StringComparison.Ordinal))
                    PushPrefixed(p, v);
            }
        }
        foreach (var (k, v) in parsed)
        {
            if (skipKeys.Contains(k))
                continue;
            var lk = k.ToLowerInvariant();
            if (lk is "artist" or "copyright" or "character" or "meta" || lk.StartsWith("character_", StringComparison.Ordinal))
                continue;
            if (v is string vs && vs.Trim().Length > 0 && vs.Length <= 48 && !Regex.IsMatch(vs, @"\.\s"))
                tags.Add(vs.Trim().ToLowerInvariant().Replace("_", " "));
        }
        if (tags.Count >= 3)
            return string.Join(", ", tags);
        return raw;
    }

    /// <summary>
    /// ToriiGate + 一行式标签工程：走 Torii 原生 JSON 模板（比纯 prose 指令更易遵从）。
    /// </summary>
    public static CaptionPrompts BuildToriiGateTagLinePrompts(ReverseCaptionRequest caption, string mediaTarget)
    {
        var mt = mediaTarget ?? caption.MediaTarget ?? "image";
        var isDanbooru = (caption.Type ?? "") == "Danbooru_tag_list"
            || (caption.PeOutputFormatValue ?? "") == "danbooru_tags";
        var toriiFmt = isDanbooru ? "danbooru_line" : "sd_tag_line";

        var peAddon = string.Join("\n",
            new[] {
                "# Tag engineering (mandatory)",
                CaptionPromptBlocks.BuildSystemPrompt(caption, mt),
                CaptionPromptBlocks.BuildSystemAddons(caption, mt),
                CaptionPromptBlocks.BuildOutputConstraints(caption),
                CaptionPromptBlocks.BuildUserTaskLead(caption, mt),
                CaptionPromptBlocks.BuildUserTaskBody(caption),
                CaptionPromptBlocks.BuildUserTailAddon(caption),
                isDanbooru
                    ? "Fill JSON values with tag fragments only. The \"tags\" field must be comma-separated Danbooru general tags (spaces inside phrases). Never write sentences in any JSON value."
                    : "The \"tags\" JSON value must be one comma-separated SD tag line only—no sentences.",
            }.Where(s => s != null && s.Trim().Length > 0));

        var r = ToriiLlamaHelper.BuildToriiPrompts(
            toriiFormat: toriiFmt,
            useNames: caption.ToriiUseNames ?? true,
            addTags: false,
            groundingTags: caption.ToriiGroundingTags ?? "",
            groundingCharacters: caption.ToriiGroundingCharacters ?? "",
            extraPrompt: peAddon,
            outputLang: caption.CaptionLang ?? "en");

        return new CaptionPrompts { SystemPrompt = r.SystemPrompt, UserPrompt = r.UserPrompt };
    }

    /// <summary>
    /// ToriiGate 模型适配：用 Torii shell 模板承载标准 PE 指令（不改变用户所选工程语义）。
    /// </summary>
    public static CaptionPrompts? BuildToriiGateStandardPePrompts(ReverseCaptionRequest caption, string mediaTarget)
    {
        if (IsTagLineReversePe(caption))
            return null;
        var mt = mediaTarget ?? caption.MediaTarget ?? "image";
        var toriiFmt = ToriiShellFormatForStandardPe(caption);

        var peAddon = string.Join("\n",
            new[] {
                "# Prompt engineering requirements (from user selection)",
                CaptionPromptBlocks.BuildSystemPrompt(caption, mt),
                CaptionPromptBlocks.BuildSystemAddons(caption, mt),
                CaptionPromptBlocks.BuildOutputConstraints(caption),
                "",
                "# Task",
                CaptionPromptBlocks.BuildUserTaskLead(caption, mt),
                CaptionPromptBlocks.BuildUserTaskBody(caption),
                CaptionPromptBlocks.BuildUserTailAddon(caption),
            }.Where(s => s != null && s.Trim().Length > 0));

        var extraParts = new List<string>();
        var userExtra = (caption.ExtraPrompt ?? "").Trim();
        var extraLead = CaptionPromptBlocks.BuildJoyExtraUserEnforcementTail(caption);
        if (userExtra.Length > 0)
            extraParts.Add(string.Join(" ", new[] { extraLead, userExtra }.Where(x => !string.IsNullOrWhiteSpace(x))));
        if (peAddon.Trim().Length > 0)
            extraParts.Add(peAddon);

        var r = ToriiLlamaHelper.BuildToriiPrompts(
            toriiFormat: toriiFmt,
            useNames: caption.ToriiUseNames ?? true,
            addTags: caption.ToriiAddTags,
            groundingTags: caption.ToriiGroundingTags ?? "",
            groundingCharacters: caption.ToriiGroundingCharacters ?? "",
            extraPrompt: string.Join("\n\n", extraParts),
            outputLang: caption.CaptionLang ?? "en");

        return new CaptionPrompts { SystemPrompt = r.SystemPrompt, UserPrompt = r.UserPrompt };
    }

    /// <summary>解析 JSON blob（与 torii_gate_extract / caption post-process 共用逻辑）。</summary>
    public static Dictionary<string, object?>? ParseJsonBlobPublic(string text) => TryParseJsonBlob(text);
}
