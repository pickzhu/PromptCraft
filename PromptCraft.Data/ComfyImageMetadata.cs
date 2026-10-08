using PromptCraft.Models.ComfyUI;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PromptCraft.Data;

/// <summary>
/// 从 ComfyUI 图片中提取的完整元数据文档。
/// 版本化设计：序列化为 JSON 后整体 GZip 压缩存库（<see cref="ImageInfo.MetadataBlob"/>），
/// 读取时用 <see cref="ImageMetadataExtensions.GetMetadata"/> 解压还原。
/// </summary>
public class ImageMetadataDocument
{
    /// <summary>结构版本号（结构变更时递增，便于未来迁移）</summary>
    public int Version { get; set; } = 1;

    /// <summary>API 格式 prompt JSON（节点 id → {class_type, inputs}），ComfyUI 的 "prompt" 文本块</summary>
    public JsonElement? Prompt { get; set; }

    /// <summary>完整工作流图 JSON（nodes/links/layout），ComfyUI 的 "workflow" 文本块</summary>
    public JsonElement? Workflow { get; set; }

    /// <summary>提取出的常用参数（正负提示词、模型、seed 等，便于详情页直接展示）</summary>
    public ExtractedMetadata? Extracted { get; set; }

    /// <summary>工作流 JSON 的 SHA256（用于识别"同一工作流生成的图片"）</summary>
    public string? WorkflowHash { get; set; }

    /// <summary>提取时间（UTC）</summary>
    public DateTime ExtractedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>从元数据中提取的常用参数快照</summary>
public class ExtractedMetadata
{
    public string? PositivePrompt { get; set; }
    public string? NegativePrompt { get; set; }
    public string? Checkpoint { get; set; }
    public string? Seed { get; set; }
    public string? Steps { get; set; }
    public string? Cfg { get; set; }
    public string? Sampler { get; set; }
    public string? Scheduler { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }

    /// <summary>LoRA 模型列表</summary>
    public List<string> LoraNames { get; set; } = new();

    /// <summary>全量节点快照（尽可能全的参数，含 workflow widget 值）</summary>
    public List<NodeSnapshot> Nodes { get; set; } = new();
}

/// <summary>单个节点的参数快照</summary>
public class NodeSnapshot
{
    public string? NodeId { get; set; }
    public string? ClassType { get; set; }
    public string? Title { get; set; }

    /// <summary>API inputs（含连接引用与基础值）</summary>
    public Dictionary<string, JsonElement> Inputs { get; set; } = new();

    /// <summary>UI widget 值（来自 workflow.nodes[].widgets_values，保持节点定义顺序）</summary>
    public List<JsonElement> Widgets { get; set; } = new();

    /// <summary>UI 命名 widget 值（来自 workflow.nodes[].widgets_values_named，新版 ComfyUI 格式）</summary>
    public Dictionary<string, JsonElement> WidgetsNamed { get; set; } = new();
}

/// <summary>
/// 从图片文件读取 ComfyUI 嵌入元数据。
/// 支持：PNG（tEXt / iTXt / zTXt 文本块）、WebP（EXIF 块内 JSON，尽力而为）。
/// 判定规则：只要含 "prompt" 或 "workflow" 文本块即视为 ComfyUI 生成，否则返回 null。
/// </summary>
public static class ComfyImageMetadataReader
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private const uint MaxChunkLength = 32 * 1024 * 1024; // 防异常 chunk 撑爆内存

    /// <summary>读取图片中的 ComfyUI 元数据；无元数据或格式不支持返回 null</summary>
    public static ImageMetadataDocument? TryRead(string filePath)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
            using var fs = File.OpenRead(filePath);

            // 只读 8 字节判断 PNG 签名；PNG 的 chunk 解析必须从位置 8 开始（IHDR length 字段还没消费）
            byte[] header = new byte[8];
            if (ReadExactly(fs, header, 0, header.Length) < 8) return null;
            if (header.AsSpan(0, 8).SequenceEqual(PngSignature))
                return ParsePng(fs);

            // WebP："RIFF" + size(4) + "WEBP"，需要再补读 4 字节
            byte[] riffTail = new byte[4];
            if (ReadExactly(fs, riffTail, 0, riffTail.Length) == 4
                && header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F'
                && riffTail[0] == 'W' && riffTail[1] == 'E' && riffTail[2] == 'B' && riffTail[3] == 'P')
                return ParseWebp(fs);

            // JPG / BMP 等：无 ComfyUI 元数据约定 → 视为非 ComfyUI 生成
            return null;
        }
        catch
        {
            return null; // 文件损坏等一律跳过，不阻断同步
        }
    }

    #region PNG 解析

    /// <summary>
    /// 遍历 PNG chunk，收集 tEXt / iTXt / zTXt 文本块。
    /// 结构：4B length(大端) + 4B type + data + 4B CRC。
    /// </summary>
    private static ImageMetadataDocument? ParsePng(Stream fs)
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var lengthBuf = new byte[4];
        var typeBuf = new byte[4];
        var crcBuf = new byte[4];

        while (true)
        {
            if (ReadExactly(fs, lengthBuf, 0, 4) != 4) break;
            uint length = (uint)((lengthBuf[0] << 24) | (lengthBuf[1] << 16) | (lengthBuf[2] << 8) | lengthBuf[3]);
            if (length > MaxChunkLength) break;
            if (ReadExactly(fs, typeBuf, 0, 4) != 4) break;

            string type = Encoding.ASCII.GetString(typeBuf);
            byte[] data = length > 0 ? new byte[length] : Array.Empty<byte>();
            if (length > 0 && ReadExactly(fs, data, 0, (int)length) != length) break;
            if (ReadExactly(fs, crcBuf, 0, 4) != 4) break; // CRC 校验忽略（不阻断读取）

            switch (type)
            {
                case "tEXt": AddText(texts, ParseTextChunk(data)); break;
                case "iTXt": AddText(texts, ParseITxtChunk(data)); break;
                case "zTXt": AddText(texts, ParseZtxtChunk(data)); break;
                case "IEND": return BuildDocument(texts);
            }
        }
        return BuildDocument(texts);
    }

    /// <summary>tEXt：keyword\0text（Latin-1 编码；ComfyUI 的 json.dumps 默认 ASCII 转义，中文以 \uXXXX 形式存在）</summary>
    private static KeyValuePair<string, string>? ParseTextChunk(byte[] data)
    {
        int sep = IndexOf(data, 0);
        if (sep < 0) return null;
        var keyword = Encoding.Latin1.GetString(data, 0, sep);
        var text = Encoding.Latin1.GetString(data, sep + 1, data.Length - sep - 1);
        return new KeyValuePair<string, string>(keyword, text);
    }

    /// <summary>
    /// iTXt：keyword\0 + 压缩标志(1B) + 压缩方法(1B) + 语言标签\0 + 翻译关键字\0 + 文本(UTF-8，标志=1 时 zlib 压缩)
    /// </summary>
    private static KeyValuePair<string, string>? ParseITxtChunk(byte[] data)
    {
        int sep = IndexOf(data, 0);
        if (sep < 0) return null;
        int pos = sep + 1;
        if (pos + 2 > data.Length) return null;
        byte compressed = data[pos++];
        pos++; // compression method，PNG 规范固定为 0（deflate）
        int langEnd = IndexOf(data, pos);
        if (langEnd < 0) return null;
        int transEnd = IndexOf(data, langEnd + 1);
        if (transEnd < 0) return null;

        var keyword = Encoding.Latin1.GetString(data, 0, sep);
        byte[] textBytes = data[(transEnd + 1)..];
        string text = compressed == 1
            ? ZlibDecompress(textBytes)
            : Encoding.UTF8.GetString(textBytes);
        return new KeyValuePair<string, string>(keyword, text);
    }

    /// <summary>zTXt：keyword\0 + 压缩方法(1B) + zlib 压缩文本</summary>
    private static KeyValuePair<string, string>? ParseZtxtChunk(byte[] data)
    {
        int sep = IndexOf(data, 0);
        if (sep < 0) return null;
        int pos = sep + 1;
        if (pos + 1 > data.Length) return null;
        var keyword = Encoding.Latin1.GetString(data, 0, sep);
        byte[] compressed = data[(pos + 1)..];
        return new KeyValuePair<string, string>(keyword, ZlibDecompress(compressed));
    }

    private static string ZlibDecompress(byte[] data)
    {
        using var input = new MemoryStream(data, writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(zlib, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    #endregion

    #region WebP 解析

    /// <summary>
    /// WebP：RIFF 容器，chunk = 4B FourCC + 4B size(小端，奇数补 1 字节 pad) + data。
    /// ComfyUI 将元数据写入 EXIF chunk，EXIF 数据内嵌 prompt/workflow JSON（尽力而为提取）。
    /// 调用时流位置已在第一个 chunk 的 FourCC 处（RIFF 头 12 字节已消费）。
    /// </summary>
    private static ImageMetadataDocument? ParseWebp(Stream fs)
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var fourCc = new byte[4];
        var sizeBuf = new byte[4];

        while (true)
        {
            if (ReadExactly(fs, fourCc, 0, 4) != 4) break;
            if (ReadExactly(fs, sizeBuf, 0, 4) != 4) break;
            uint size = (uint)(sizeBuf[0] | (sizeBuf[1] << 8) | (sizeBuf[2] << 16) | (sizeBuf[3] << 24));
            if (size > MaxChunkLength) break;

            string tag = Encoding.ASCII.GetString(fourCc);
            byte[] data = size > 0 ? new byte[size] : Array.Empty<byte>();
            if (size > 0 && ReadExactly(fs, data, 0, (int)size) != size) break;
            if ((size & 1) == 1) fs.Seek(1, SeekOrigin.Current); // RIFF 奇数长度补 1 字节

            if (tag == "EXIF")
            {
                var json = TryExtractJson(data);
                if (json is JsonElement el && el.ValueKind == JsonValueKind.Object)
                {
                    TryAddText(texts, el, "prompt");
                    TryAddText(texts, el, "workflow");
                }
                break;
            }
            if (tag == "VP8X" || tag == "VP8 " || tag == "VP8L" || tag == "ANIM" || tag == "ANMF")
                continue; // 图像数据块，跳过
        }
        return BuildDocument(texts);
    }

    /// <summary>从 EXIF chunk 数据中尽力提取 JSON 对象（兼容带 "Exif\0\0" 前缀 / 前后填充 / 嵌套 JSON）</summary>
    private static JsonElement? TryExtractJson(byte[] data)
    {
        // 方案 1：整体解析（跳过可能的 "Exif\0\0" TIFF 前缀）
        foreach (var start in new[] { 0, 6 })
        {
            if (start >= data.Length) continue;
            var candidate = data[start..];
            if (TryParseJson(candidate, out var el)) return el;
        }
        // 方案 2：定位第一个 '{'，用大括号计数截取到匹配的 '}' 再解析
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] != (byte)'{') continue;
            int depth = 0, end = -1;
            for (int j = i; j < data.Length; j++)
            {
                if (data[j] == (byte)'{') depth++;
                else if (data[j] == (byte)'}') { depth--; if (depth == 0) { end = j; break; } }
            }
            if (end > i && TryParseJson(data[i..(end + 1)], out var el2)) return el2;
        }
        return null;
    }

    private static bool TryParseJson(byte[] bytes, out JsonElement element)
    {
        element = default;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            element = doc.RootElement.Clone();
            return element.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }

    private static void TryAddText(Dictionary<string, string> texts, JsonElement obj, string key)
    {
        if (obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            texts[key] = v.GetString() ?? "";
    }

    #endregion

    #region 文档构建与提取

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
            return doc.RootElement.Clone(); // Clone 后脱离 JsonDocument 生命周期
        }
        catch
        {
            return null;
        }
    }

    private static void AddText(Dictionary<string, string> texts, KeyValuePair<string, string>? pair)
    {
        if (pair is { } kv && !string.IsNullOrEmpty(kv.Key))
            texts[kv.Key] = kv.Value;
    }

    private static int IndexOf(byte[] data, int start)
    {
        for (int i = start; i < data.Length; i++)
            if (data[i] == 0) return i;
        return -1;
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

/// <summary>
/// 从 prompt/workflow JSON 中提取常用参数（尽可能全）。
/// 正/负提示词判定：优先 KSampler(Advanced) 的 positive/negative 连接；
/// 无连接时按 workflow links 的槽位（positive=1, negative=2）反推；仍无则按节点 id 排序启发式（第一个正、第二个负）。
/// </summary>
public static class ComfyMetadataExtractor
{
    public static void Extract(ImageMetadataDocument doc)
    {
        var result = doc.Extracted ??= new ExtractedMetadata();

        // ---- 第一来源：API prompt（节点 id → {class_type, inputs}）----
        var prompt = doc.Prompt;
        if (prompt is JsonElement p && p.ValueKind == JsonValueKind.Object)
        {
            var nodes = new Dictionary<string, JsonElement>();
            foreach (var prop in p.EnumerateObject())
                nodes[prop.Name] = prop.Value;

            string? positiveNodeId = null, negativeNodeId = null;
            foreach (var (id, node) in nodes)
            {
                var classType = GetString(node, "class_type");
                switch (classType)
                {
                    case "KSampler":
                    case "KSamplerAdvanced":
                        result.Seed ??= GetString(node, "inputs", "seed");
                        result.Steps ??= GetString(node, "inputs", "steps");
                        result.Cfg ??= GetString(node, "inputs", "cfg");
                        result.Sampler ??= GetString(node, "inputs", "sampler_name");
                        result.Scheduler ??= GetString(node, "inputs", "scheduler");
                        positiveNodeId ??= GetLinkNodeId(node, "positive");
                        negativeNodeId ??= GetLinkNodeId(node, "negative");
                        break;
                    case "CheckpointLoaderSimple":
                        result.Checkpoint ??= GetString(node, "inputs", "ckpt_name");
                        break;
                    case "UNETLoader":
                        result.Checkpoint ??= GetString(node, "inputs", "unet_name");
                        break;
                    case "LoraLoader":
                    case "LoraLoaderModelOnly":
                        AddLora(result, GetString(node, "inputs", "lora_name"));
                        break;
                    case "RandomNoise":
                        result.Seed ??= GetString(node, "inputs", "noise_seed");
                        break;
                    case "EmptyLatentImage":
                        result.Width ??= GetInt(node, "inputs", "width");
                        result.Height ??= GetInt(node, "inputs", "height");
                        break;
                }

                result.Nodes.Add(new NodeSnapshot
                {
                    NodeId = id,
                    ClassType = classType,
                    Inputs = GetInputs(node),
                });
            }

            // 文本编码节点 → 正/负提示词（按连接优先，其次启发式）。
            // 两类节点字段不同：
            //  - CLIPTextEncode（SD 系）：inputs.text
            //  - TextEncode*（Qwen 系，如 TextEncodeQwenImage21）：inputs.prompt 为正提示词、inputs.negative_prompt 为负提示词（同一节点自带两者）
            var encodeNodes = nodes
                .Where(kv =>
                {
                    var ct = GetString(kv.Value, "class_type");
                    return ct == "CLIPTextEncode"
                        || (ct != null && ct.StartsWith("TextEncode", StringComparison.Ordinal));
                })
                .OrderBy(kv => int.TryParse(kv.Key, out var i) ? i : int.MaxValue)
                .ToList();
            foreach (var (id, node) in encodeNodes)
            {
                var isQwen = GetString(node, "class_type")?.StartsWith("TextEncode", StringComparison.Ordinal) == true;
                var text = GetString(node, "inputs", isQwen ? "prompt" : "text");
                var neg = GetString(node, "inputs", "negative_prompt");
                if (id == positiveNodeId)
                {
                    if (!string.IsNullOrEmpty(text)) result.PositivePrompt = text;
                    if (!string.IsNullOrEmpty(neg) && result.NegativePrompt == null) result.NegativePrompt = neg;
                }
                else if (id == negativeNodeId)
                {
                    // Qwen 系节点（TextEncode*）自带 prompt/negative_prompt 两字段：连在负面槽时应优先取 negative_prompt；
                    // SD 系 CLIPTextEncode 无 negative_prompt（neg=null）时回落取 text。防"把该节点正面文本当负面"。
                    var v = !string.IsNullOrEmpty(neg) ? neg : text;
                    if (!string.IsNullOrEmpty(v)) result.NegativePrompt = v;
                }
            }
            // 启发式兜底：仅当完全没有采样器连接信息（positive/negative 均为 null）时才按节点顺序猜正/负；
            // 有 positive 连接而 negative 悬空 = 工作流确实无负面提示词，绝不能猜第二个文本节点当负面（否则负面=正面误判）。
            var remaining = encodeNodes.Where(kv => kv.Key != positiveNodeId && kv.Key != negativeNodeId).ToList();
            if (result.PositivePrompt == null && remaining.Count > 0)
            {
                var first = remaining[0];
                var isQwen = GetString(first.Value, "class_type")?.StartsWith("TextEncode", StringComparison.Ordinal) == true;
                result.PositivePrompt = GetString(first.Value, "inputs", isQwen ? "prompt" : "text");
            }
            if (result.NegativePrompt == null && positiveNodeId == null && negativeNodeId == null && remaining.Count > 1)
            {
                var second = remaining[1];
                var isQwen = GetString(second.Value, "class_type")?.StartsWith("TextEncode", StringComparison.Ordinal) == true;
                var v = GetString(second.Value, "inputs", isQwen ? "prompt" : "text");
                result.NegativePrompt = string.IsNullOrEmpty(v) ? GetString(second.Value, "inputs", "negative_prompt") : v;
            }
        }

        // ---- 第二来源：workflow（title、widgets_values、links）----
        ApplyWorkflowDetails(result, doc.Workflow);
        ResolvePromptsFromWorkflowLinks(result, doc.Workflow);

        // ---- 兜底过滤：无负面提示词的工作流被启发式误判为"负面=正面"时清除 ----
        if (result.NegativePrompt != null && result.PositivePrompt != null
            && string.Equals(result.NegativePrompt.Trim(), result.PositivePrompt.Trim(), StringComparison.Ordinal))
        {
            result.NegativePrompt = null;
        }
    }

    /// <summary>补充节点 Title 与 widget 值，并用 widget 值兜底补全常用参数</summary>
    private static void ApplyWorkflowDetails(ExtractedMetadata result, JsonElement? workflow)
    {
        if (workflow is not JsonElement wf || wf.ValueKind != JsonValueKind.Object) return;
        if (!wf.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array) return;

        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            if (!node.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) continue;
            var id = idEl.GetRawText(); // workflow 节点 id 为数字，raw text 与 prompt key 一致
            var snapshot = result.Nodes.FirstOrDefault(n => n.NodeId == id);
            if (snapshot == null) continue;

            // Title：S&R 名称或旧版 "node" 字段
            if (node.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                if (props.TryGetProperty("Node name for S&R", out var t) && t.ValueKind == JsonValueKind.String)
                    snapshot.Title = t.GetString();
                else if (props.TryGetProperty("node", out var t2) && t2.ValueKind == JsonValueKind.String)
                    snapshot.Title = t2.GetString();
            }

            // widgets_values（节点定义顺序）
            if (node.TryGetProperty("widgets_values", out var wv) && wv.ValueKind == JsonValueKind.Array)
                snapshot.Widgets = wv.EnumerateArray().Select(x => x.Clone()).ToList();

            // widgets_values_named（新版 ComfyUI 命名 widget 值）
            if (node.TryGetProperty("widgets_values_named", out var wvn) && wvn.ValueKind == JsonValueKind.Object)
            {
                foreach (var kv in wvn.EnumerateObject())
                    snapshot.WidgetsNamed[kv.Name] = kv.Value.Clone();
            }

            // widget 兜底：prompt 里缺的参数从 widget 顺序值补全
            switch (snapshot.ClassType)
            {
                case "KSampler": // [seed, control_after_generate, steps, cfg, sampler_name, scheduler, denoise]
                    if (snapshot.Widgets.Count >= 6)
                    {
                        result.Seed ??= WidgetString(snapshot.Widgets[0]);
                        result.Steps ??= WidgetString(snapshot.Widgets[2]);
                        result.Cfg ??= WidgetString(snapshot.Widgets[3]);
                        result.Sampler ??= WidgetString(snapshot.Widgets[4]);
                        result.Scheduler ??= WidgetString(snapshot.Widgets[5]);
                    }
                    break;
                case "KSamplerAdvanced": // [add_noise, noise_seed, steps, cfg, sampler_name, scheduler, ...]
                    if (snapshot.Widgets.Count >= 6)
                    {
                        result.Seed ??= WidgetString(snapshot.Widgets[1]);
                        result.Steps ??= WidgetString(snapshot.Widgets[2]);
                        result.Cfg ??= WidgetString(snapshot.Widgets[3]);
                        result.Sampler ??= WidgetString(snapshot.Widgets[4]);
                        result.Scheduler ??= WidgetString(snapshot.Widgets[5]);
                    }
                    break;
                case "CheckpointLoaderSimple":
                    if (snapshot.Widgets.Count >= 1) result.Checkpoint ??= WidgetString(snapshot.Widgets[0]);
                    break;
                case "LoraLoader":
                case "LoraLoaderModelOnly":
                    if (snapshot.Widgets.Count >= 1) AddLora(result, WidgetString(snapshot.Widgets[0]));
                    break;
                case "CLIPTextEncode":
                    // text 与 prompt inputs 同源，这里不重复处理
                    break;
            }
        }
    }

    /// <summary>
    /// 仅当 prompt 缺失时，从 workflow links 反推正/负提示词。
    /// link = [id, originNode, originSlot, targetNode, targetSlot, type]；
    /// KSampler 输入槽：0=model, 1=positive, 2=negative, 3=latent_image。
    /// </summary>
    private static void ResolvePromptsFromWorkflowLinks(ExtractedMetadata result, JsonElement? workflow)
    {
        if (result.PositivePrompt != null && result.NegativePrompt != null) return;
        if (workflow is not JsonElement wf || wf.ValueKind != JsonValueKind.Object) return;
        if (!wf.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array) return;

        var samplerNodeId = result.Nodes
            .FirstOrDefault(n => n.ClassType is "KSampler" or "KSamplerAdvanced")?.NodeId;
        if (samplerNodeId == null) return;

        foreach (var link in links.EnumerateArray())
        {
            if (link.ValueKind != JsonValueKind.Array || link.GetArrayLength() < 6) continue;
            var arr = link.EnumerateArray().ToArray();
            if (arr[3].ValueKind != JsonValueKind.Number || arr[3].GetRawText() != samplerNodeId) continue;
            if (arr[4].ValueKind != JsonValueKind.Number) continue;
            if (arr[1].ValueKind != JsonValueKind.Number) continue;

            var originId = arr[1].GetRawText();
            var origin = result.Nodes.FirstOrDefault(n => n.NodeId == originId);
            // 注意：origin?.Widgets.Count < 1 在 origin 为 null 时整个表达式为 null，
            // null < 1 是 false，不会 continue，导致下一行 origin!.Widgets 空引用。必须显式判空。
            if (origin is null || origin.Widgets.Count < 1) continue;
            var text = WidgetString(origin.Widgets[0]);
            if (string.IsNullOrEmpty(text)) continue;

            switch (arr[4].GetInt32())
            {
                case 1 when result.PositivePrompt == null: result.PositivePrompt = text; break;
                case 2 when result.NegativePrompt == null: result.NegativePrompt = text; break;
            }
        }
    }

    private static void AddLora(ExtractedMetadata result, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name) && !result.LoraNames.Contains(name))
            result.LoraNames.Add(name);
    }

    /// <summary>
    /// 从 workflow JSON 提取工作流标题（便于按工作流分类展示）。
    /// 优先节点的 S&amp;R 名称，其次节点 title；无则 null。
    /// </summary>
    public static string? ExtractTitle(JsonElement? workflow)
    {
        if (workflow is not JsonElement wf || wf.ValueKind != JsonValueKind.Object) return null;
        if (!wf.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array) return null;
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            if (node.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                if (props.TryGetProperty("Node name for S&R", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    var s = t.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
                if (props.TryGetProperty("node", out var t2) && t2.ValueKind == JsonValueKind.String)
                {
                    var s = t2.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            if (node.TryGetProperty("title", out var nt) && nt.ValueKind == JsonValueKind.String)
            {
                var s = nt.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }

    private static Dictionary<string, JsonElement> GetInputs(JsonElement node)
    {
        var result = new Dictionary<string, JsonElement>();
        if (node.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in inputs.EnumerateObject())
                result[p.Name] = p.Value.Clone();
        }
        return result;
    }

    private static string? GetString(JsonElement node, params string[] path)
    {
        JsonElement cur = node;
        foreach (var key in path)
        {
            if (!cur.TryGetProperty(key, out cur)) return null;
        }
        return cur.ValueKind switch
        {
            JsonValueKind.String => cur.GetString(),
            // 数字优先按整数输出（大种子如 3.48E+18 不能转成科学计数法显示/存储，会丢失可读性）；
            // 仅当 JSON 中就是科学计数法文本（TryGetInt64 失败）时按整数格式展开，如 3.4813102064070886E+18 → 3481310206407088640
            JsonValueKind.Number => cur.TryGetInt64(out var l)
                ? l.ToString(CultureInfo.InvariantCulture)
                : cur.TryGetDouble(out var d)
                    ? d.ToString("0", CultureInfo.InvariantCulture)
                    : cur.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static int? GetInt(JsonElement node, params string[] path)
    {
        JsonElement cur = node;
        foreach (var key in path)
        {
            if (!cur.TryGetProperty(key, out cur)) return null;
        }
        return cur.ValueKind == JsonValueKind.Number && cur.TryGetInt32(out var v) ? v : null;
    }

    /// <summary>inputs.positive/negative 的连接引用形如 ["3", 0]，取第一个元素（节点 id）</summary>
    private static string? GetLinkNodeId(JsonElement node, string inputKey)
    {
        if (!node.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Object) return null;
        if (!inputs.TryGetProperty(inputKey, out var link) || link.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in link.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) return item.GetString();
        }
        return null;
    }

    private static string? WidgetString(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };
}

/// <summary>
/// 元数据压缩 / 解压 Codec。
/// 存储策略：文档 JSON 用 GZip 压缩成单 BLOB 入库，显著节省空间（workflow JSON 常压缩 5~20 倍）；
/// 读取时解压还原。压缩为确定性格式，后续可自由调整存储介质。
/// </summary>
public static class ComfyMetadataCodec
{
    /// <summary>压缩 JSON 文本为 GZip 字节</summary>
    public static byte[] Compress(string json)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            gz.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    /// <summary>解压 GZip 字节为 UTF-8 文本；无效或空输入返回 null</summary>
    public static string? Decompress(byte[]? blob)
    {
        if (blob is null || blob.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(blob, writable: false);
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            using var reader = new StreamReader(gz, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>序列化文档并压缩</summary>
    public static byte[] Serialize(ImageMetadataDocument doc) =>
        Compress(JsonSerializer.Serialize(doc));

    /// <summary>解压并反序列化文档；无数据或损坏返回 null</summary>
    public static ImageMetadataDocument? Deserialize(byte[]? blob)
    {
        var json = Decompress(blob);
        if (json == null) return null;
        try
        {
            return JsonSerializer.Deserialize<ImageMetadataDocument>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>以 workflow（缺失则 prompt）的原始 JSON 文本计算 SHA256，用于同工作流识别</summary>
    public static string ComputeHash(ImageMetadataDocument doc)
    {
        string? raw = null;
        if (doc.Workflow is JsonElement wf && wf.ValueKind == JsonValueKind.Object) raw = wf.GetRawText();
        else if (doc.Prompt is JsonElement p && p.ValueKind == JsonValueKind.Object) raw = p.GetRawText();
        if (raw == null) return string.Empty;
        return ComputeJsonHash(raw);
    }

    /// <summary>对 JSON 原始文本计算 SHA256（拆表后工作流/提示词去重的统一键）</summary>
    public static string ComputeJsonHash(string json) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
}

/// <summary>
/// 图片提取数据的解压入口（拆表后按需解压，详情页等场景复用）。
/// 存储侧：workflow / prompt / 节点快照 JSON 均 GZip 压缩为 BLOB，读取时才解压。
/// </summary>
public static class ImageMetadataExtensions
{
    /// <summary>解压工作流的 JSON（用户工作流 / 图片提取工作流统一 GZip 压缩存 <see cref="Workflow.WorkflowJsonBlob"/>，
    /// 图片提取为结构版，展示时组装）。无数据返回 null</summary>
    public static string? GetWorkflowJson(this Workflow workflow) =>
        workflow.WorkflowJsonBlob is { Length: > 0 }
            ? ComfyMetadataCodec.Decompress(workflow.WorkflowJsonBlob)
            : null;

    /// <summary>解压图片提取的 prompt API JSON；无数据返回 null</summary>
    public static string? GetPromptJson(this ImagePrompt prompt) =>
        ComfyMetadataCodec.Decompress(prompt.PromptJsonBlob);

    /// <summary>解压节点快照 JSON（ExtractedMetadata.Nodes）；无数据返回 null</summary>
    public static string? GetNodesJson(this ImagePrompt prompt) =>
        ComfyMetadataCodec.Decompress(prompt.NodesJsonBlob);
}
