using PromptCraft.Models.Inference;
using System.Text.RegularExpressions;

namespace PromptCraft.Service.Inference.Reverse;

// ============================================================
// 反推 tag 行后处理
// 1:1 移植自 app/electron/config/tagLineSanitize.js
// 剥离 [tag] 方括号、十六进制色值碎片、孤立数字、去重、质量词去重。
// Danbooru / SD / ComfyUI：均保留短语内空格（如 long hair）；勿压成 long_hair 长串。
// ============================================================

public static class TagLineSanitize
{
    private static readonly Regex CountTagRe = new(@"^\d+(?:girl|girls|boy|boys|other|others)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsDanbooruCaption(ReverseCaptionRequest? caption)
        => caption != null && (caption.Type ?? "") == "Danbooru_tag_list";

    private static bool IsCountTag(string t)
        => CountTagRe.IsMatch((t ?? "").Trim());

    /// <summary>纯十六进制或「数字+十六进制」泄漏（如 1f1f3b、1f1f3b1f1f3b、2）。</summary>
    public static bool IsHexGarbageToken(string t)
    {
        var s = (t ?? "").Trim().ToLowerInvariant().Replace(" ", "");
        if (s.Length == 0)
            return true;
        if (Regex.IsMatch(s, @"^(artist|copyright|character|meta|score_):"))
            return false;
        if (IsCountTag(s))
            return false;
        if (Regex.IsMatch(s, @"^\d+$"))
            return true;
        if (Regex.IsMatch(s, @"^[0-9a-f]{6,}$"))
            return true;
        if (Regex.IsMatch(s, @"^\d+[0-9a-f]{4,}$"))
            return true;
        return false;
    }

    private static string NormalizeBooruValue(string value)
        => Regex.Replace((value ?? "").Trim().ToLowerInvariant().Replace("_", " "), @"\s+", " ").Trim();

    private static string NormalizeToken(string raw, bool danbooru)
    {
        var t = (raw ?? "").Trim();
        if (t.Length == 0)
            return "";
        if (Regex.IsMatch(t, @"^(artist|copyright|character|meta):", RegexOptions.IgnoreCase))
        {
            var idx = t.IndexOf(':');
            return t[..(idx + 1)].ToLowerInvariant() + NormalizeBooruValue(t[(idx + 1)..]);
        }
        if (danbooru)
            return NormalizeBooruValue(t.Replace("_", " "));
        return Regex.Replace(t.ToLowerInvariant().Replace("/", " "), @"\s+", " ").Trim();
    }

    private static string BracketInnerToTag(string inner, bool danbooru)
    {
        var raw = (inner ?? "").Trim();
        if (raw.Length == 0)
            return "";
        if (Regex.IsMatch(raw, @"^(artist|copyright|character|meta):", RegexOptions.IgnoreCase))
        {
            var idx = raw.IndexOf(':');
            return raw[..(idx + 1)].ToLowerInvariant() + NormalizeBooruValue(raw[(idx + 1)..]);
        }
        return NormalizeToken(raw, danbooru);
    }

    /// <summary>仅把「数字/十六进制垃圾」串按空格拆开，勿拆 best quality、anime screenshot 等多词 tag。</summary>
    private static List<string> SplitLooseTokens(string chunk)
    {
        var c = (chunk ?? "").Trim();
        if (c.Length == 0)
            return new List<string>();
        if (Regex.IsMatch(c, @"^(?:\d+|[0-9a-f]{4,})(?:\s+(?:\d+|[0-9a-f]{4,}))+$", RegexOptions.IgnoreCase))
        {
            var list = new List<string>();
            foreach (var w in c.Split(' '))
                if (!string.IsNullOrWhiteSpace(w))
                    list.Add(w.Trim());
            return list;
        }
        return new List<string> { c };
    }

    private static List<string> DedupeTokens(IEnumerable<string> parts)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outList = new List<string>();
        foreach (var p in parts)
        {
            var k = p.ToLowerInvariant();
            if (k.Length == 0 || seen.Contains(k))
                continue;
            seen.Add(k);
            outList.Add(p);
        }
        return outList;
    }

    private static string UnwrapBracketNotation(string text, bool danbooru)
    {
        var s = (text ?? "").Trim();
        if (!Regex.IsMatch(s, @"\[[^\]]+\]"))
            return s;

        var bracketParts = new List<string>();
        var re = new Regex(@"\[([^\]]+)\]");
        foreach (Match m in re.Matches(s))
        {
            var tag = BracketInnerToTag(m.Groups[1].Value, danbooru);
            if (tag.Length > 0 && !IsHexGarbageToken(tag))
                bracketParts.Add(tag);
        }

        var remainder = Regex.Replace(re.Replace(s, " "), @"\s+", " ").Trim();
        var remParts = new List<string>();
        if (remainder.Length > 0)
        {
            foreach (var chunk in Regex.Split(remainder, @"[,，]+"))
            {
                foreach (var raw in SplitLooseTokens(chunk))
                {
                    var tag = NormalizeToken(raw, danbooru);
                    if (tag.Length > 0 && !IsHexGarbageToken(tag))
                        remParts.Add(tag);
                }
            }
        }

        var merged = new List<string>();
        merged.AddRange(remParts);
        merged.AddRange(bracketParts);
        return string.Join(", ", DedupeTokens(merged));
    }

    private static string TokenKey(string t)
        => Regex.Replace((t ?? "").Trim().ToLowerInvariant().Replace("_", " "), @"\s+", " ").Trim();

    private static HashSet<string>? QualityPrefixTokenSet(ReverseCaptionRequest? caption)
    {
        var prefix = (caption?.QualityPromptPrefix ?? "").Trim();
        if (prefix.Length == 0)
            return null;
        var danbooru = IsDanbooruCaption(caption);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Regex.Split(prefix, @"[,，]\s*"))
        {
            var k = TokenKey(t);
            if (k.Length > 0)
                set.Add(k);
        }
        return set.Count > 0 ? set : null;
    }

    private static bool UseQualityPrefix(ReverseCaptionRequest? caption)
        => caption != null && caption.QualityPromptEnabled;

    /// <summary>
    /// 清洗 tag 行（cleanTagLineBody）：去方括号标注、去 hex 垃圾、去重、去质量词重复。
    /// </summary>
    public static string CleanTagLineBody(string? text, ReverseCaptionRequest? caption = null)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0)
            return s;

        var danbooru = IsDanbooruCaption(caption);

        if (Regex.IsMatch(s, @"\[[^\]]+\]"))
            s = UnwrapBracketNotation(s, danbooru);

        var parts = new List<string>();
        foreach (var chunk in Regex.Split(s, @"[,，]+"))
        {
            foreach (var raw in SplitLooseTokens(chunk))
            {
                var tag = NormalizeToken(raw, danbooru);
                if (tag.Length > 0 && !IsHexGarbageToken(tag))
                    parts.Add(tag);
            }
        }

        var outList = DedupeTokens(parts);

        if (caption != null && UseQualityPrefix(caption))
        {
            var qset = QualityPrefixTokenSet(caption);
            if (qset != null)
            {
                outList = outList.FindAll(t => !qset.Contains(TokenKey(t)));
            }
        }

        return string.Join(", ", outList);
    }
}
