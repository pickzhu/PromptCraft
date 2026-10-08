using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Docnet.Core;
using Docnet.Core.Models;
using Sdcb.SimdPaddleOCR;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny;
using SkiaSharp;

namespace PromptCraft.Service.Documents;

/// <summary>
/// 知识库文档深度解析（v2 待办 #2）：
/// - .md/.txt：直接读取；
/// - .docx：解 ZIP 读 word/document.xml 提取段落文本（分节，零第三方依赖）；
/// - .pdf：Docnet.Core（PDFium）渲染每页为 BGRA 位图 → SimdPaddleOCR 离线识别（扫描件与文本层统一走渲染+OCR）；
/// - 常见图片：SkiaSharp 解码 → OCR。
/// OCR 采用 Sdcb.SimdPaddleOCR（纯托管 PP-OCRv6，模型内嵌于程序集，Apache-2.0，离线无云服务）。
/// 文本按 (path, lastWriteTimeUtc) 内存缓存，避免流水线各阶段重复解析耗时。
/// </summary>
public sealed class KnowledgeDocumentReader : IDisposable
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly SemaphoreSlim _ocrGate = new(1, 1); // OCR 推理串行化（模型非线程安全，文档解析本身低频）
    private Lazy<Task<PaddleOcrAll?>>? _ocrLazy;
    private bool _disposed;

    private sealed class CacheEntry
    {
        public DateTime MtimeUtc { get; set; }
        public string Text { get; set; } = "";
    }

    /// <summary>OCR 是否可用（模型包缺失/加载失败时为 false，调用方降级为仅文件名清单）。</summary>
    public bool OcrAvailable => _ocrLazy != null;

    /// <summary>按扩展名提取文档全文；不支持的格式返回 null。</summary>
    public async Task<string?> ExtractTextAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext is not (".md" or ".txt" or ".docx" or ".pdf" or ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp"))
            return null;

        var mtime = File.GetLastWriteTimeUtc(filePath);
        if (_cache.TryGetValue(filePath, out var hit) && hit.MtimeUtc == mtime)
            return hit.Text;

        string? text = ext switch
        {
            ".md" or ".txt" => ReadPlainText(filePath),
            ".docx" => ReadDocxText(filePath),
            ".pdf" => await ExtractPdfTextAsync(filePath, ct),
            _ => await OcrImageAsync(filePath, ct),
        };
        if (text == null) return null;

        _cache[filePath] = new CacheEntry { MtimeUtc = mtime, Text = text };
        return text;
    }

    /// <summary>仅清理缓存（模型实例保留，供后续复用）。</summary>
    public void ClearCache() => _cache.Clear();

    // ---------- 纯文本 ----------
    private static string ReadPlainText(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            return text.Length > MaxExtractChars ? text[..MaxExtractChars] : text;
        }
        catch { return ""; }
    }

    // ---------- docx（零第三方依赖：ZIP + XML） ----------
    private static string ReadDocxText(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("word/document.xml");
            if (entry == null) return "";
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream);
            var sb = new StringBuilder();
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.LocalName == "p")
                {
                    sb.Append('\n');
                }
                else if (reader.LocalName == "t")
                {
                    sb.Append(reader.ReadElementContentAsString());
                }
            }
            var text = sb.ToString();
            return text.Length > MaxExtractChars ? text[..MaxExtractChars] : text;
        }
        catch { return ""; }
    }

    // ---------- PDF：渲染 → OCR ----------
    private const int PdfRenderDpi = 150; // A4 @150DPI ≈ 1240×1754，识别速度与精度均衡
    private static readonly PageDimensions PdfPageSize = new(1240, 1754);

    private async Task<string?> ExtractPdfTextAsync(string path, CancellationToken ct)
    {
        var ocr = await GetOcrAsync(ct);
        if (ocr == null) return null;
        try
        {
            using var docReader = DocLib.Instance.GetDocReader(path, PdfPageSize);
            var pageCount = docReader.GetPageCount();
            if (pageCount <= 0) return "";
            var sb = new StringBuilder();
            for (var i = 0; i < pageCount && sb.Length < MaxExtractChars; i++)
            {
                ct.ThrowIfCancellationRequested();
                using var pageReader = docReader.GetPageReader(i);
                var raw = pageReader.GetImage(); // BGRA
                var w = pageReader.GetPageWidth();
                var h = pageReader.GetPageHeight();
                if (raw == null || raw.Length < w * h * 4) continue;
                var bgr = BgraToBgr(raw, w, h);
                await _ocrGate.WaitAsync(ct);
                try
                {
                    var result = ocr.Run(bgr, w, h);
                    sb.Append("【第 ").Append(i + 1).Append(" 页】\n").AppendLine(result.Text);
                }
                finally { _ocrGate.Release(); }
            }
            var text = sb.ToString();
            return text.Length > MaxExtractChars ? text[..MaxExtractChars] : text;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn($"知识库 PDF 解析失败：{path} - {ex.Message}", "Knowledge");
            return null;
        }
    }

    // ---------- 图片：SkiaSharp 解码 → OCR ----------
    private async Task<string?> OcrImageAsync(string path, CancellationToken ct)
    {
        var ocr = await GetOcrAsync(ct);
        if (ocr == null) return null;
        try
        {
            using var bitmap = SKBitmap.Decode(path);
            if (bitmap == null) return null;
            using var pixmap = bitmap.PeekPixels();
            if (pixmap == null) return null;
            var w = bitmap.Width;
            var h = bitmap.Height;
            // PeekPixels 提供原生像素（Skia 默认 BGRA8888），拷贝为 BGR
            var raw = new byte[w * h * 4];
            System.Runtime.InteropServices.Marshal.Copy(pixmap.GetPixels(), raw, 0, raw.Length);
            var bgr = BgraToBgr(raw, w, h);
            await _ocrGate.WaitAsync(ct);
            try
            {
                return ocr.Run(bgr, w, h).Text;
            }
            finally { _ocrGate.Release(); }
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn($"知识库图片 OCR 失败：{path} - {ex.Message}", "Knowledge");
            return null;
        }
    }

    // ---------- OCR 实例（懒加载单例） ----------
    private Task<PaddleOcrAll?> GetOcrAsync(CancellationToken ct)
    {
        if (_ocrLazy == null)
        {
            var lazy = new Lazy<Task<PaddleOcrAll?>>(() => LoadOcrAsync(ct));
            if (Interlocked.CompareExchange(ref _ocrLazy, lazy, null) == null)
                return lazy.Value;
        }
        return _ocrLazy.Value;
    }

    private static async Task<PaddleOcrAll?> LoadOcrAsync(CancellationToken ct)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ocr = await PaddleOcrAll.LoadAsync(ChineseV6TinyModels.Default).ConfigureAwait(false);
            sw.Stop();
            LogService.Instance.Info($"离线 OCR 模型加载完成，耗时 {sw.Elapsed.TotalSeconds:0.0}s", "Knowledge");
            return ocr;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warn($"离线 OCR 模型加载失败（将降级为仅文件名清单）：{ex.Message}", "Knowledge");
            return null;
        }
    }

    private static byte[] BgraToBgr(byte[] bgra, int w, int h)
    {
        var bgr = new byte[w * h * 3];
        int si = 0, di = 0;
        var pixels = w * h;
        for (var i = 0; i < pixels; i++)
        {
            bgr[di++] = bgra[si];     // B
            bgr[di++] = bgra[si + 1]; // G
            bgr[di++] = bgra[si + 2]; // R
            si += 4;
        }
        return bgr;
    }

    private const int MaxExtractChars = 80_000; // 单文件解析文本上限，防超大文档撑爆内存/上下文

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ocrGate.Dispose();
        _cache.Clear();
    }
}
