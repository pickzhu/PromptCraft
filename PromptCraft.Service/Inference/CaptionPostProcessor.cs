using System.Text;
using System.Text.RegularExpressions;

namespace PromptCraft.Service.Inference;

/// <summary>
/// 反推输出后处理（T2.3 精简版）：
/// 1. 从模型输出里截取第一个平衡的 JSON 对象（去掉前后散文）；
/// 2. prose 类输出剥 Markdown 标题/项目符号/序号。
/// 完整 torii_gate_extract 10 种 extractor 留后续。
/// </summary>
public static partial class CaptionPostProcessor
{
    [GeneratedRegex(@"^#{1,6}\s*", RegexOptions.Multiline)]
    private static partial Regex MarkdownHeading();
    [GeneratedRegex(@"^\s*[-•*]\s+", RegexOptions.Multiline)]
    private static partial Regex MarkdownBullet();
    [GeneratedRegex(@"^\s*\d+[.)]\s+", RegexOptions.Multiline)]
    private static partial Regex MarkdownNumber();

    /// <summary>从含散文的输出里截取第一个平衡 JSON 对象；找不到则原样返回。</summary>
    public static string TryExtractJsonBlob(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var start = text.IndexOf('{');
        if (start < 0) return text;
        var depth = 0;
        var inStr = false;
        var escape = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (escape) { escape = false; continue; }
            if (ch == '\\' && inStr) { escape = true; continue; }
            if (ch == '"') { inStr = !inStr; continue; }
            if (inStr) continue;
            if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0) return text[start..(i + 1)];
            }
        }
        return text;
    }

    /// <summary>prose 类输出：剥 Markdown 标题、项目符号、序号，合并成一段。</summary>
    public static string SanitizeProse(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = MarkdownHeading().Replace(text, "");
        s = MarkdownBullet().Replace(s, "");
        s = MarkdownNumber().Replace(s, "");
        s = s.Replace("**", "").Replace("*", "");
        // 合并换行
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(t);
        }
        return sb.ToString();
    }
}
