using PromptCraft.Models.Inference;
using System.Text.RegularExpressions;

namespace PromptCraft.Service.Inference.Reverse;

// ============================================================
// ToriiGate 提示词构建
// 1:1 移植自 app/electron/service/inference/torii_llama_helper.js
// ============================================================

public static class ToriiLlamaHelper
{
    private static List<string> SplitGroundingList(string? raw)
    {
        if (raw == null)
            return new List<string>();
        var text = raw.Trim();
        if (text.Length == 0)
            return new List<string>();
        var list = new List<string>();
        foreach (var p in Regex.Split(text, @"[\n,]+"))
        {
            var t = p.Trim();
            if (t.Length > 0)
                list.Add(t);
        }
        return list;
    }

    public static ToriiGroundingItem BuildGroundingItem(string? groundingTags, string? groundingCharacters, bool useNames, bool addTags)
    {
        var tags = SplitGroundingList(groundingTags);
        var chars = SplitGroundingList(groundingCharacters);
        return new ToriiGroundingItem
        {
            Tags = addTags ? tags : new List<string>(),
            Characters = useNames && chars.Count > 0 ? chars : new List<string>(),
            CharPTagsValue = new ToriiGroundingItem.CharPTags(),
            CharDescrValue = new ToriiGroundingItem.CharDescr(),
        };
    }

    /// <summary>输出语言附加块（buildToriiOutputLanguageAddon）。</summary>
    public static (string SystemAddon, string UserAddon) BuildToriiOutputLanguageAddon(string? outputLang, string toriiFormat)
    {
        var lang = (outputLang ?? "en").ToLowerInvariant();
        if (lang != "zh")
            return ("", "");
        var fmt = (toriiFormat ?? "").Trim();
        var systemAddon =
            "\n\n【输出语言锁定：中文】用户已在界面指定「输出语言 = 中文」。\n" +
            "除图中可见的原文引用外，所有描述正文必须使用简体中文（汉字），不得用英文撰写段落。";
        if (fmt is "long" or "short")
        {
            return (systemAddon,
                "\n\n# Output language\n" +
                "Write the entire caption in Simplified Chinese (简体中文). " +
                "Use coherent Chinese paragraphs only; do not output English prose.");
        }
        if (ToriiPromptsData.ToriiJsonFormats.Contains(fmt))
        {
            return (systemAddon,
                "\n\n# Output language\n" +
                "All JSON string values must be in Simplified Chinese (简体中文). " +
                "Keep JSON keys exactly as specified in the schema above.");
        }
        return (systemAddon,
            "\n\n# Output language\n" +
            "Keep the required Markdown section headings and structure exactly as specified above, " +
            "but write all descriptive body text in Simplified Chinese (简体中文). " +
            "Only quote non-Chinese text when it visibly appears in the image.");
    }

    /// <summary>构建 Torii 提示词（buildToriiPrompts）。</summary>
    public static CaptionPrompts BuildToriiPrompts(
        string toriiFormat,
        bool useNames,
        bool addTags,
        string? groundingTags,
        string? groundingCharacters,
        string? extraPrompt,
        string? outputLang)
    {
        var fmt = (toriiFormat ?? "").Trim();
        if (fmt.Length == 0 || !ToriiPromptsData.PromptsB.ContainsKey(fmt))
            throw new InvalidOperationException($"未知结构化格式: {fmt}");

        var item = BuildGroundingItem(groundingTags, groundingCharacters, useNames, addTags);
        var userQuery = ToriiPromptsData.MakeUserQuery(
            item,
            fmt,
            useNames,
            addTags,
            item.Characters.Count > 0,
            false,
            false,
            false);

        var ep = (extraPrompt ?? "").Trim();
        if (ep.Length > 0)
            userQuery += $"\n# Additional instructions\n{ep}\n";
        if (ToriiPromptsData.ToriiJsonFormats.Contains(fmt))
            userQuery += ToriiPromptsData.JsonOutputSuffix;

        var langBlock = BuildToriiOutputLanguageAddon(outputLang, fmt);
        return new CaptionPrompts
        {
            SystemPrompt = ToriiPromptsData.SystemPrompt + langBlock.SystemAddon,
            UserPrompt = userQuery + langBlock.UserAddon,
        };
    }

    public static string ApplyToriiExtractMode(string raw, string? mode)
        => ToriiGateExtract.ApplyExtractMode(raw, mode);
}
