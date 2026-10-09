using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LibVLCSharp.Shared;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace PromptCraft.Utils;

/// <summary>
/// LibVLC 视频首帧抓取器（UI 版）：像素抓取逻辑复用服务层 PromptCraft.Service.VideoFrameGrab
/// （软渲染 RV32 + Display 回调拷首帧），本类负责把原始像素包装为 Avalonia Bitmap（封面显示）。
/// 注意：vmem setup 回调必须返回 1（该 LibVLCSharp 版本约定），返回 0 会导致解码器不初始化（黑屏）。
/// 每次抓帧使用独立的 MediaPlayer（LibVLC 支持多播放器并发）；播放器与缓冲一律在后台线程释放。
/// </summary>
public static class VideoFrameGrabber
{
    /// <summary>抓取视频首帧并包装为 Bitmap（异步壳：内部后台线程同步抓取，避免阻塞调用线程）。</summary>
    public static Task<Bitmap?> GrabFirstFrameAsync(LibVLC libvlc, string videoPath)
        => Task.Run(() =>
        {
            var d = PromptCraft.Service.VideoFrameGrab.GrabFirstFrameData(libvlc, videoPath);
            if (d == null || d.Data == null || d.Width <= 0 || d.Height <= 0) return null;
            return CreateBitmap(d.Data, d.Width, d.Height);
        });

    private static Bitmap CreateBitmap(byte[] data, uint width, uint height)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize((int)width, (int)height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        using (var fb = bitmap.Lock())
            Marshal.Copy(data, 0, fb.Address, data.Length);
        return bitmap;
    }
}
