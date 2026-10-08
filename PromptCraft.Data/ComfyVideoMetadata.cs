using PromptCraft.Models.ComfyUI;
using System.Text;
using System.Text.Json;

namespace PromptCraft.Data;

/// <summary>
/// 从 ComfyUI 视频文件读取嵌入元数据（工作流/提示词 JSON），与图片读取器产出同构文档，
/// 下游提取/入库链路（ComfyMetadataExtractor / AttachMetadataAsync / 缩略图）完全复用。
/// 支持容器：
///  - WebM / Matroska（.webm/.mkv）：EBML Segment→Tags→Tag→SimpleTag（ffmpeg / VideoHelperSuite 写入任意键，主流方案）
///  - MP4 / MOV（.mp4/.mov）：moov→udta→meta→ilst（iTunes 风格键，尽力而为）
/// 判定规则：只要含 "prompt" 或 "workflow" 文本即视为 ComfyUI 生成，否则返回 null（整文件跳过不入库）。
/// </summary>
public static class ComfyVideoMetadataReader
{
    private const uint MaxElementSize = 32 * 1024 * 1024; // 防异常元素撑爆内存

    public static ImageMetadataDocument? TryRead(string filePath)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
            using var fs = File.OpenRead(filePath);
            byte[] header = new byte[8];
            if (ReadExactly(fs, header, 0, 8) < 8) return null;

            // EBML 头 0x1A45DFA3 → WebM/Matroska（前 4 字节即元素 ID）
            if (header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3)
                return ParseMatroska(fs);

            // MP4/MOV：文件头是首个 box 的 size(4BE)，第 4~8 字节才是类型 "ftyp"
            // （此前误读前 4 字节导致 MP4 判断恒为 false，返回 null）
            if (header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p')
                return ParseMp4(fs);
            return null;
        }
        catch
        {
            return null; // 文件损坏等一律跳过，不阻断同步
        }
    }

    #region Matroska / WebM（EBML Tags）

    private static ImageMetadataDocument? ParseMatroska(Stream fs)
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        fs.Seek(0, SeekOrigin.Begin);

        // 顶层元素：EBML 头直接跳过；进 Segment（0x18538067）后找 Tags（0x1254C367）
        while (true)
        {
            var id = ReadVint(fs);
            if (id < 0) break;
            var size = ReadVint(fs);
            if (size < 0 || size > MaxElementSize) break;

            if (id == 0x18538067) // Segment
            {
                var segmentEnd = fs.Position + size;
                while (fs.Position < segmentEnd)
                {
                    var cid = ReadVint(fs);
                    if (cid < 0) break;
                    var csize = ReadVint(fs);
                    if (csize < 0 || csize > MaxElementSize) break;

                    if (cid == 0x1254C367) // Tags
                    {
                        var tagsEnd = fs.Position + csize;
                        while (fs.Position < tagsEnd)
                        {
                            var tid = ReadVint(fs);
                            if (tid < 0) break;
                            var tsize = ReadVint(fs);
                            if (tsize < 0 || tsize > MaxElementSize) break;

                            if (tid == 0x7373) // Tag
                            {
                                var tagEnd = fs.Position + tsize;
                                string? name = null, value = null;
                                while (fs.Position < tagEnd)
                                {
                                    var sid = ReadVint(fs);
                                    if (sid < 0) break;
                                    var ssize = ReadVint(fs);
                                    if (ssize < 0 || ssize > MaxElementSize) break;

                                    if (sid == 0x67C8) // SimpleTag
                                    {
                                        var simpleEnd = fs.Position + ssize;
                                        while (fs.Position < simpleEnd)
                                        {
                                            var iid = ReadVint(fs);
                                            if (iid < 0) break;
                                            var isize = ReadVint(fs);
                                            if (isize < 0 || isize > MaxElementSize) break;
                                            if (iid == 0x45A3) name = ReadUtf8(fs, (int)isize); // TagName
                                            else if (iid == 0x4487) value = ReadUtf8(fs, (int)isize); // TagString
                                            else fs.Seek(isize, SeekOrigin.Current);
                                        }
                                        if (!string.IsNullOrEmpty(name) && value != null)
                                            texts[name] = value;
                                    }
                                    else fs.Seek(ssize, SeekOrigin.Current);
                                }
                            }
                            else fs.Seek(tsize, SeekOrigin.Current);
                        }
                    }
                    else fs.Seek(csize, SeekOrigin.Current);
                }
                break;
            }
            fs.Seek(size, SeekOrigin.Current);
        }
        return BuildDocument(texts);
    }

    /// <summary>EBML 变长整数（元素 ID 与 size 通用）：首字节高位连续 1 的个数 = 长度。</summary>
    private static long ReadVint(Stream fs)
    {
        int b = fs.ReadByte();
        if (b < 0) return -1;
        int mask = 0x80, len = 1;
        while ((b & mask) == 0 && mask > 1) { mask >>= 1; len++; }
        long value = b & (mask - 1);
        for (int i = 1; i < len; i++)
        {
            int n = fs.ReadByte();
            if (n < 0) return -1;
            value = (value << 8) | n;
        }
        return value;
    }

    #endregion

    #region MP4 / MOV（moov → udta → meta → ilst）

    private static ImageMetadataDocument? ParseMp4(Stream fs)
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        fs.Seek(0, SeekOrigin.Begin);

        while (true)
        {
            var box = ReadBoxHeader(fs);
            if (box == null) break;
            var (size, type) = box.Value;
            var payloadStart = fs.Position;

            if (type == "moov")
                ParseContainerChildren(fs, size - 8, (childType, childPayloadStart, childLength) =>
                {
                    if (childType == "udta")
                        ParseContainerChildren(fs, childLength, (udtaType, udtaStart, udtaLength) =>
                        {
                            if (udtaType == "meta")
                                ParseMetaPayload(fs, udtaStart, udtaLength, texts);
                        });
                });

            TrySkip(fs, payloadStart, size - 8, size - 8);
        }
        return BuildDocument(texts);
    }

    /// <summary>
    /// 解析 meta box 的元数据负载（QuickTime 风格）。兼容三种布局：
    ///  - 标准 ilst（iTunes）：moov/udta/meta/ilst，条目 = key4cc + data box
    ///  - ffmpeg mdta：moov/udta/meta/(hdlr, '=keys', 'vilst')——非标准键（workflow/prompt）写入 '=keys' 键表 +
    ///    'vilst' 值表（实测 ComfyUI 视频输出即此布局，meta 内还可能套一层 '!' box）
    /// 实现上把整个 meta 负载读入内存后按字节模式定位 ilst/=keys/vilst，绕开外层包装差异。
    /// </summary>
    private static void ParseMetaPayload(Stream fs, long start, long length, Dictionary<string, string> texts)
    {
        if (length < 8 || length > MaxElementSize) return;
        var buf = new byte[length];
        fs.Seek(start, SeekOrigin.Begin);
        if (ReadExactly(fs, buf, 0, (int)length) != length) return;

        // 1) 标准 ilst
        var ilstIdx = IndexOfAscii(buf, "ilst");
        if (ilstIdx >= 0)
            ParseIlst(buf, ilstIdx, texts);

        // 2) ffmpeg mdta：'=keys'（键表）+ 'vilst'（值表），按顺序配对
        var keysIdx = IndexOfAscii(buf, "=keys");
        var vilstIdx = IndexOfAscii(buf, "vilst");
        if (keysIdx >= 0 && vilstIdx >= 0)
        {
            var keys = ParseMdtaKeys(buf, keysIdx);
            var values = ParseVilstValues(buf, vilstIdx);
            int count = Math.Min(keys.Count, values.Count);
            for (int i = 0; i < count; i++)
                texts[keys[i]] = values[i];
        }
    }

    /// <summary>解析 ffmpeg '=keys' 键表。实测（ComfyUI 视频输出）该表布局与写入端有关：
    /// '=keys' 标识后存在 3~8 字节的 padding/version/flags 差异，且 count 以 host-endian（小端）写入。
    /// 因此采用暴力容错扫描：count 候选偏移 × 大小端双读 × entry 起点滑动，并要求 count 个 entry
    /// 全部符合 [size]['mdta'][name] 结构才采用（结构校验避免误命中）。</summary>
    private static List<string> ParseMdtaKeys(byte[] buf, int start)
    {
        var keys = new List<string>();
        int count = -1, pos = -1;

        for (int countOff = 4; countOff <= 32 && count < 0 && start + countOff + 4 <= buf.Length; countOff++)
        {
            var cBe = BE32(buf, start + countOff);
            var cLe = (buf[start + countOff + 3] << 24) | (buf[start + countOff + 2] << 16) | (buf[start + countOff + 1] << 8) | buf[start + countOff];
            foreach (var c in new[] { cBe, cLe })
            {
                if (c < 1 || c > 64) continue;
                for (int entryOff = countOff + 4; entryOff < Math.Min(countOff + 12, 48) && count < 0; entryOff++)
                {
                    int p = start + entryOff;
                    bool ok = true;
                    var names = new List<string>();
                    for (int i = 0; i < c; i++)
                    {
                        if (p + 8 > buf.Length) { ok = false; break; }
                        int esBe = BE32(buf, p);
                        int esLe = (buf[p + 3] << 24) | (buf[p + 2] << 16) | (buf[p + 1] << 8) | buf[p];
                        int es = (esBe >= 8 && esBe <= 1024) ? esBe : ((esLe >= 8 && esLe <= 1024) ? esLe : 0);
                        if (es == 0 || p + es > buf.Length
                            || buf[p + 4] != (byte)'m' || buf[p + 5] != (byte)'d' || buf[p + 6] != (byte)'t' || buf[p + 7] != (byte)'a')
                        {
                            ok = false;
                            break;
                        }
                        names.Add(Encoding.ASCII.GetString(buf, p + 8, es - 8));
                        p += es;
                    }
                    if (ok && names.Count > 0)
                    {
                        keys = names;
                        count = c;
                        break;
                    }
                }
            }
        }
        return keys;
    }

    /// <summary>解析 ffmpeg 'vilst' 值表：'vilst'(带 size 头) + entries[size(4) + index(4) + data box]，data box 文本从 +8（fullbox+locale）起。
    /// 'vilst' 标识的 find 定位可能有 1 字节偏差，从标识后 4..28 字节动态扫描首个结构合法的 entry。</summary>
    private static List<string> ParseVilstValues(byte[] buf, int start)
    {
        var values = new List<string>();
        int basePos = -1;
        for (int cand = start + 4; cand <= start + 28 && cand + 16 <= buf.Length; cand++)
        {
            int esize = BE32(buf, cand);
            if (esize < 12 || esize > 200000) continue;
            int dsize = BE32(buf, cand + 8);
            if (dsize < 16 || cand + 16 > buf.Length) continue;
            if (buf[cand + 12] == (byte)'d' && buf[cand + 13] == (byte)'a' && buf[cand + 14] == (byte)'t' && buf[cand + 15] == (byte)'a')
            {
                basePos = cand;
                break;
            }
        }
        if (basePos < 0) return values;

        int pos = basePos;
        while (pos + 8 <= buf.Length)
        {
            int entrySize = BE32(buf, pos);
            if (entrySize < 12) break;
            var box = ReadBoxHeader(buf, pos + 8);
            if (box == null || box.Value.Type != "data" || box.Value.Size < 16) break;
            int dataStart = pos + 8 + 8 + 8; // entry 头(8) + data box 头(8) + fullbox(4) + locale(4)
            int textLen = (int)box.Value.Size - 16;
            if (textLen <= 0 || dataStart + textLen > buf.Length) break;
            values.Add(Encoding.UTF8.GetString(buf, dataStart, textLen));
            pos += entrySize;
        }
        return values;
    }

    private static int IndexOfAscii(byte[] buf, string token)
    {
        var t = System.Text.Encoding.ASCII.GetBytes(token);
        for (int i = 0; i + t.Length <= buf.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < t.Length; j++)
            {
                if (buf[i + j] != t[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }

    private static int BE32(byte[] buf, int start)
    {
        return (buf[start] << 24) | (buf[start + 1] << 16) | (buf[start + 2] << 8) | buf[start + 3];
    }

    private static (uint Size, string Type)? ReadBoxHeader(byte[] buf, int start)
    {
        if (start + 8 > buf.Length) return null;
        uint size = (uint)BE32(buf, start);
        if (size < 8) return null;
        return (size, Encoding.ASCII.GetString(buf, start + 4, 4));
    }

    /// <summary>解析标准 ilst：条目 = key4cc(4B) + data box（文本偏移 +8）。</summary>
    private static void ParseIlst(byte[] buf, int start, Dictionary<string, string> texts)
    {
        var end = buf.Length;
        int pos = start + 4; // 'ilst'
        while (pos + 4 <= end)
        {
            var key = Encoding.ASCII.GetString(buf, pos, 4);
            pos += 4;
            var child = ReadBoxHeader(buf, pos);
            if (child == null) break;
            var (csize, ctype) = child.Value;
            if (ctype == "data" && csize >= 16)
            {
                int textStart = pos + 8 + 8; // data 头(8) + fullbox(4) + locale(4)
                int textLen = (int)csize - 16;
                if (textLen > 0 && textStart + textLen <= end)
                {
                    var text = Encoding.UTF8.GetString(buf, textStart, textLen).TrimEnd('\0');
                    if (!string.IsNullOrEmpty(text))
                        texts[key] = text;
                }
            }
            pos += (int)csize;
        }
    }

    /// <summary>读取 box 头：size(4BE) + type(4)。返回 null 表示流结束/损坏。</summary>
    private static (uint Size, string Type)? ReadBoxHeader(Stream fs)
    {
        var sizeBytes = new byte[4];
        if (ReadExactly(fs, sizeBytes, 0, 4) < 4) return null;
        uint size = (uint)((sizeBytes[0] << 24) | (sizeBytes[1] << 16) | (sizeBytes[2] << 8) | sizeBytes[3]);
        if (size == 0) return null; // 到文件尾
        if (size == 1) return null; // 64-bit 扩展尺寸：不支持（元数据 box 不会用到）
        var typeBytes = new byte[4];
        if (ReadExactly(fs, typeBytes, 0, 4) < 4) return null;
        return (size, Encoding.ASCII.GetString(typeBytes));
    }

    /// <summary>遍历容器 box 的子 box（每个子 box 回调；回调内不应移动流位置，或移动后由本方法归位）。</summary>
    private static void ParseContainerChildren(Stream fs, long length, Action<string, long, long> onChild)
    {
        var start = fs.Position;
        var end = start + length;
        while (fs.Position + 8 <= end)
        {
            var pos = fs.Position;
            var box = ReadBoxHeader(fs);
            if (box == null) break;
            var (size, type) = box.Value;
            if (size < 8 || pos + size > end) break;
            var payloadStart = fs.Position;
            try { onChild(type, payloadStart, size - 8); }
            catch { /* 单个子 box 解析失败不影响整体 */ }
            TrySkip(fs, payloadStart, size - 8, size - 8);
        }
        fs.Seek(start + length, SeekOrigin.Begin);
    }

    private static void TrySkip(Stream fs, long from, long count, long expected)
    {
        if (count <= 0 || fs.Position != from)
        {
            try { fs.Seek(from + expected, SeekOrigin.Begin); } catch { }
        }
        else if (count <= int.MaxValue)
        {
            fs.Seek(count, SeekOrigin.Current);
        }
        else
        {
            try { fs.Seek(from + expected, SeekOrigin.Begin); } catch { }
        }
    }

    #endregion

    #region 文档构建

    private static ImageMetadataDocument? BuildDocument(Dictionary<string, string> texts)
    {
        string? promptJson = texts.TryGetValue("prompt", out var p) ? p : null;
        string? workflowJson = texts.TryGetValue("workflow", out var w) ? w : null;
        if (string.IsNullOrWhiteSpace(promptJson) && string.IsNullOrWhiteSpace(workflowJson))
            return null; // 无 ComfyUI 元数据 → 非 ComfyUI 生成

        var doc = new ImageMetadataDocument
        {
            Prompt = ParseJson(promptJson),
            Workflow = ParseJson(workflowJson),
            Extracted = new ExtractedMetadata(),
        };
        ComfyMetadataExtractor.Extract(doc);
        doc.WorkflowHash = ComfyMetadataCodec.ComputeHash(doc);
        return doc;
    }

    private static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadUtf8(Stream fs, int length)
    {
        if (length <= 0) return null;
        var buf = new byte[length];
        if (ReadExactly(fs, buf, 0, length) != length) return null;
        return Encoding.UTF8.GetString(buf);
    }

    private static int ReadExactly(Stream fs, byte[] buffer, int offset, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = fs.Read(buffer, offset + read, count - read);
            if (n <= 0) break;
            read += n;
        }
        return read;
    }

    #endregion
}
