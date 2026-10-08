using System.Text;

namespace PromptCraft.Service;

/// <summary>diff 分段类型：相同 / 仅左侧（A）有 / 仅右侧（B）有</summary>
public enum TextDiffKind
{
    Same,
    OnlyLeft,
    OnlyRight,
}

/// <summary>一段 diff 结果：文本 + 归属类型（用于渲染时按类型上色）</summary>
public sealed record TextDiffSegment(string Text, TextDiffKind Kind);

/// <summary>
/// 文本 diff（用于图片对比时提示词差异高亮）。
/// 两级算法：先按标点/空白/换行把文本切成"单元"，对单元序列做最长公共子序列（LCS）找出相同骨架；
/// 对夹在相同单元之间未匹配的两段再逐字符做 LCS，得到字符级"仅A/仅B"分段。
/// 中文提示词通常 1~2KB（几百个单元），单元级 LCS 开销很小；字符级 LCS 只作用于局部未匹配块，可控。
/// </summary>
public static class TextDiff
{
    /// <summary>对两个文本做 diff，输出有序分段（已合并相邻同类型段）</summary>
    public static List<TextDiffSegment> Diff(string? left, string? right)
    {
        string a = left ?? "";
        string b = right ?? "";
        if (a.Length == 0 && b.Length == 0)
            return new List<TextDiffSegment>();

        var ta = Tokenize(a);
        var tb = Tokenize(b);
        int n = ta.Count, m = tb.Count;

        // 单元级 LCS 矩阵
        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                dp[i, j] = ta[i] == tb[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var segs = new List<TextDiffSegment>();
        var bufA = new List<string>();
        var bufB = new List<string>();

        void Flush()
        {
            if (bufA.Count == 0 && bufB.Count == 0) return;
            var rawA = string.Concat(bufA);
            var rawB = string.Concat(bufB);
            if (rawA.Length == 0) segs.Add(new TextDiffSegment(rawB, TextDiffKind.OnlyRight));
            else if (rawB.Length == 0) segs.Add(new TextDiffSegment(rawA, TextDiffKind.OnlyLeft));
            else segs.AddRange(CharDiff(rawA, rawB));
            bufA.Clear();
            bufB.Clear();
        }

        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (ta[x] == tb[y])
            {
                Flush();
                segs.Add(new TextDiffSegment(ta[x], TextDiffKind.Same));
                x++;
                y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                bufA.Add(ta[x]);
                x++;
            }
            else
            {
                bufB.Add(tb[y]);
                y++;
            }
        }
        while (x < n) { bufA.Add(ta[x]); x++; }
        while (y < m) { bufB.Add(tb[y]); y++; }
        Flush();

        return MergeAdjacent(segs);
    }

    /// <summary>按换行/标点/空白切分单元；换行与标点保留为独立单元（保持可读性）</summary>
    private static List<string> Tokenize(string s)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (c == '\n')
            {
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
                result.Add("\n");
            }
            else if (char.IsWhiteSpace(c) || c is '，' or '。' or '！' or '？' or '；' or '：' or '、'
                or ',' or '.' or '!' or '?' or ';' or ':' or '…' or '（' or '）' or '(' or ')')
            {
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
                result.Add(c.ToString());
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    /// <summary>
    /// 对两段短文本做局部细化 diff：剪掉公共前缀与公共后缀，中间未匹配部分直接标为仅A/仅B。
    /// 刻意不用字符级 LCS——LCS 会把 "red" vs "green" 交叉匹配出 "re" 相同（数学正确但视觉反直觉）；
    /// 前缀/后缀剪枝不会产生跨词交叉匹配，符合人眼对"哪里改了"的直觉。
    /// </summary>
    private static List<TextDiffSegment> CharDiff(string a, string b)
    {
        int pre = 0;
        while (pre < a.Length && pre < b.Length && a[pre] == b[pre]) pre++;
        int suf = 0;
        while (suf < a.Length - pre && suf < b.Length - pre &&
               a[a.Length - 1 - suf] == b[b.Length - 1 - suf]) suf++;

        int midALen = a.Length - pre - suf;
        int midBLen = b.Length - pre - suf;

        var segs = new List<TextDiffSegment>();
        if (pre > 0) segs.Add(new TextDiffSegment(a[..pre], TextDiffKind.Same));
        if (midALen > 0) segs.Add(new TextDiffSegment(a.Substring(pre, midALen), TextDiffKind.OnlyLeft));
        if (midBLen > 0) segs.Add(new TextDiffSegment(b.Substring(pre, midBLen), TextDiffKind.OnlyRight));
        if (suf > 0) segs.Add(new TextDiffSegment(a.Substring(a.Length - suf), TextDiffKind.Same));
        return MergeAdjacent(segs);
    }

    /// <summary>合并相邻同类型分段，减少渲染元素数量</summary>
    private static List<TextDiffSegment> MergeAdjacent(List<TextDiffSegment> segs)
    {
        var result = new List<TextDiffSegment>(segs.Count);
        foreach (var s in segs)
        {
            if (result.Count > 0 && result[^1].Kind == s.Kind)
                result[^1] = new TextDiffSegment(result[^1].Text + s.Text, s.Kind);
            else
                result.Add(s);
        }
        return result;
    }
}
