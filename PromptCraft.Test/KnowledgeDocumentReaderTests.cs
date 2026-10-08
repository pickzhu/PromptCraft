using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptCraft.Service.Documents;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PromptCraft.Test;

/// <summary>
/// 知识库文档深度解析冒烟测试（v2 #2）：
/// .md/.txt 直读、.docx ZIP 解包、图片 OCR（SimdPaddleOCR 离线）、PDF 渲染管线不崩溃。
/// </summary>
[TestClass]
public class KnowledgeDocumentReaderTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "promptcraft_kb_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [TestMethod]
    public async Task Txt_ReadsContent()
    {
        using var reader = new KnowledgeDocumentReader();
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "guide.md");
            await File.WriteAllTextAsync(path, "# 标题\n这里是知识库正文。", Encoding.UTF8);
            var text = await reader.ExtractTextAsync(path);
            Assert.IsNotNull(text);
            Assert.IsTrue(text!.Contains("知识库正文"), "应读到 txt 内容");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task Docx_ExtractsParagraphText()
    {
        using var reader = new KnowledgeDocumentReader();
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "doc.docx");
            await CreateMinimalDocxAsync(path, "第一章\n角色设定：主角林晓");
            var text = await reader.ExtractTextAsync(path);
            Assert.IsNotNull(text);
            Assert.IsTrue(text!.Contains("第一章"), "应读到段落文本");
            Assert.IsTrue(text.Contains("主角林晓"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task Pdf_NoException_OnMinimalDocument()
    {
        using var reader = new KnowledgeDocumentReader();
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "minimal.pdf");
            await File.WriteAllBytesAsync(path, BuildValidMinimalPdf());
            // 程序化生成合法 xref 的极简 PDF：渲染 + OCR 管线不得抛异常（Docnet + SimdPaddleOCR 集成验证）
            var text = await reader.ExtractTextAsync(path);
            Assert.IsNotNull(text, "PDF 解析管线应返回文本（可为空）而不抛异常");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task Image_Ocr_ChineseSmoke()
    {
        using var reader = new KnowledgeDocumentReader();
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "scan.png");
            using (var bmp = new SkiaSharp.SKBitmap(480, 120))
            {
                using var canvas = new SkiaSharp.SKCanvas(bmp);
                canvas.Clear(SkiaSharp.SKColors.White);
                using var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.FromFamilyName("Microsoft YaHei"), 42);
                using var paint = new SkiaSharp.SKPaint
                {
                    Color = SkiaSharp.SKColors.Black,
                    IsAntialias = true,
                };
                canvas.DrawText("测试OCR离线识别", 24, 78, font, paint);

                using var img = SkiaSharp.SKImage.FromBitmap(bmp);
                using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
                using var fs = File.Create(path);
                data.SaveTo(fs);
            }

            var text = await reader.ExtractTextAsync(path, CancellationToken.None);
            Assert.IsNotNull(text, "图片 OCR 应返回文本");
            if (string.IsNullOrWhiteSpace(text))
                return; // 模型缺失时降级为 null/空，不视为失败（CI 环境可无模型资源）
            Assert.IsTrue(text.Contains("OCR"), $"应识别出画面文字，实际：{text}");
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- 构造工具 ----------

    private static async Task CreateMinimalDocxAsync(string path, string content)
    {
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body>{BuildDocxParagraphs(content)}</w:body>
            </w:document>
            """;
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("word/document.xml", CompressionLevel.Fastest);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(xml);
        await stream.WriteAsync(bytes);
    }

    private static string BuildDocxParagraphs(string content)
    {
        var sb = new StringBuilder();
        foreach (var line in content.Split('\n'))
            sb.Append("<w:p><w:r><w:t>").Append(line).Append("</w:t></w:r></w:p>");
        return sb.ToString();
    }

    /// <summary>程序化生成含合法 xref 的极简单页 PDF（偏移自动计算，PDFium 可解析渲染）。</summary>
    private static byte[] BuildValidMinimalPdf()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        var offsets = new List<long> { 0 };

        void WriteObj(string body)
        {
            offsets.Add(sb.Length);
            sb.Append(offsets.Count - 1).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        WriteObj("<< /Type /Catalog /Pages 2 0 R >>");
        WriteObj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        WriteObj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 150] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        WriteObj("<< /Length 44 >>\nstream\nBT /F1 12 Tf 20 100 Td (PromptCraft test) Tj ET\nendstream");
        WriteObj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        var xrefPos = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count).Append('\n');
        sb.Append("0000000000 65535 f \n");
        for (var i = 1; i < offsets.Count; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(offsets.Count).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xrefPos).Append("\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
