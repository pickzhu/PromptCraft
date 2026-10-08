// 迁移基准：app/electron/service/inference/vision_utils.js（prepareVisionImageDataUrl）
// sharp 缩边（VISION_MAX_EDGE=1280）→ JPEG 85 → data URL；失败 fallback 原文件 base64。
// PromptCraft 用 SkiaSharp（Avalonia 传递依赖）实现等价；不做 EXIF rotate（sharp .rotate 行为差异，影响极小）。

using SkiaSharp;

namespace PromptCraft.Service.Inference.Minimax;

public static class MediaVision
{
    /// <summary>config/llamaContext.js VISION_MAX_EDGE。</summary>
    public const int VisionMaxEdge = 1280;

    private static readonly Dictionary<string, string> MimeByExt = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png",
        [".webp"] = "image/webp", [".gif"] = "image/gif", [".bmp"] = "image/bmp",
    };

    public static string ImageMimeForPath(string? filePath)
    {
        var ext = Path.GetExtension(filePath ?? "").ToLowerInvariant();
        return MimeByExt.TryGetValue(ext, out var mime) ? mime : "image/jpeg";
    }

    public static string ImageFileToDataUrl(string filePath)
    {
        var mime = ImageMimeForPath(filePath);
        var b64 = Convert.ToBase64String(File.ReadAllBytes(filePath));
        return $"data:{mime};base64,{b64}";
    }

    private static string? EncodeJpegDataUrl(SKBitmap bmp, int quality)
    {
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, quality);
        if (data == null || data.IsEmpty) return null;
        return $"data:image/jpeg;base64,{Convert.ToBase64String(data.ToArray())}";
    }

    /// <summary>prepareVisionImageDataUrl：缩边到 maxEdge 后转 JPEG 85 data URL；失败回退原图。</summary>
    public static string? PrepareVisionImageDataUrl(string? filePath, int maxEdge = VisionMaxEdge)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
        var edge = maxEdge > 0 ? maxEdge : VisionMaxEdge;
        try
        {
            using var src = SKBitmap.Decode(filePath);
            if (src == null || src.Width <= 0 || src.Height <= 0) return ImageFileToDataUrl(filePath);
            var scale = Math.Min(1.0, (double)edge / Math.Max(src.Width, src.Height));
            if (scale < 1.0)
            {
                var nw = Math.Max(1, (int)Math.Round(src.Width * scale));
                var nh = Math.Max(1, (int)Math.Round(src.Height * scale));
                using var dst = new SKBitmap(nw, nh, src.ColorType, src.AlphaType);
                using var canvas = new SKCanvas(dst);
                canvas.DrawBitmap(src, new SKRect(0, 0, nw, nh));
                var url = EncodeJpegDataUrl(dst, 85);
                return url ?? ImageFileToDataUrl(filePath);
            }
            var direct = EncodeJpegDataUrl(src, 85);
            return direct ?? ImageFileToDataUrl(filePath);
        }
        catch
        {
            return ImageFileToDataUrl(filePath);
        }
    }
}
