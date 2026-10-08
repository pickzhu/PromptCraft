using LibVLCSharp.Shared;
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PromptCraft.Service;

/// <summary>
/// LibVLC 视频首帧抓取器（服务层版，无 UI 依赖，供图库同步/缩略图生成使用）：
/// 软渲染（vmem RV32）+ Display 回调拷第一帧 → 原始像素 + 尺寸。
/// 注意：vmem setup 回调必须返回 1（该 LibVLCSharp 版本约定），返回 0 会导致解码器不初始化（黑屏）。
/// 每次抓帧使用独立的 MediaPlayer（LibVLC 支持多播放器并发）；播放器与缓冲在后台线程释放，
/// 绝不在 finally 里同步 Stop —— Stop 在异常解码状态可能永久挂起。
/// 主项目 UI 封面版（PromptCraft.Utils.VideoFrameGrabber）复用本类像素数据后自行包装 Bitmap。
/// </summary>
public static class VideoFrameGrab
{
    /// <summary>首帧原始数据（BGRA8888 + 尺寸），供缩放/编码保存封面文件。</summary>
    public sealed class FrameGrabData
    {
        public byte[]? Data;
        public uint Width, Height;
    }

    /// <summary>同步抓取视频首帧像素与尺寸；超时（10s）或失败返回 null。调用方负责在后台线程调用。</summary>
    public static FrameGrabData? GrabFirstFrameData(LibVLC libvlc, string videoPath)
    {
        if (libvlc == null || string.IsNullOrWhiteSpace(videoPath)) return null;

        var state = new GrabState();
        var player = new MediaPlayer(libvlc);
        try
        {
            state.Tcs = new TaskCompletionSource<bool>();
            player.SetVideoFormatCallbacks(state.OnVideoFormat, state.OnVideoCleanup);
            player.SetVideoCallbacks(state.Lock, state.Unlock, state.Display);
            player.EncounteredError += (_, _) => state.Tcs.TrySetResult(false);

            Media media;
            try
            {
                media = new Media(libvlc, videoPath, FromType.FromPath);
            }
            catch
            {
                return null;
            }

            player.Play(media);

            try
            {
                state.Tcs.Task.Wait(TimeSpan.FromSeconds(10));
            }
            catch (AggregateException)
            {
                return null;
            }

            if (!state.Tcs.Task.IsCompletedSuccessfully
                || !state.Tcs.Task.Result
                || state.Data == null
                || state.Width <= 0 || state.Height <= 0)
            {
                return null;
            }

            return new FrameGrabData
            {
                Data = state.Data,
                Width = state.Width,
                Height = state.Height,
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            var buffer = state.Buffer;
            var cleanup = Task.Run(() =>
            {
                try { player.Stop(); } catch { }
                try { player.Dispose(); } catch { }
            });
            cleanup.Wait(TimeSpan.FromSeconds(3));
            if (buffer != IntPtr.Zero)
            {
                try { Marshal.FreeHGlobal(buffer); } catch { }
            }
        }
    }

    /// <summary>单次抓帧的共享状态（回调在 libvlc 视频线程触发）。</summary>
    private sealed class GrabState
    {
        public TaskCompletionSource<bool> Tcs = new();
        public byte[]? Data;
        public uint Width, Height;
        public IntPtr Buffer = IntPtr.Zero;
        public int BufferSize;

        /// <summary>vmem setup：声明 RV32 输出并分配缓冲；必须返回 1（成功），0 会导致解码器不初始化。</summary>
        public uint OnVideoFormat(ref IntPtr opaque, IntPtr chroma,
                                  ref uint width, ref uint height,
                                  ref uint pitches, ref uint lines)
        {
            Width = width;
            Height = height;

            var chromaBytes = Encoding.ASCII.GetBytes("RV32");
            Marshal.Copy(chromaBytes, 0, chroma, 4);

            var pitch = width * 4;
            var size = (int)(pitch * height);
            if (Buffer != IntPtr.Zero && BufferSize != size)
            {
                Marshal.FreeHGlobal(Buffer);
                Buffer = IntPtr.Zero;
            }
            if (Buffer == IntPtr.Zero)
            {
                BufferSize = size;
                Buffer = Marshal.AllocHGlobal(size);
            }

            pitches = pitch;
            lines = height;
            return 1;
        }

        public void OnVideoCleanup(ref IntPtr opaque) { }

        public IntPtr Lock(IntPtr opaque, IntPtr planes)
        {
            if (Buffer != IntPtr.Zero)
                Marshal.WriteIntPtr(planes, Buffer);
            return Buffer;
        }

        public void Unlock(IntPtr opaque, IntPtr picture, IntPtr planes) { }

        public void Display(IntPtr opaque, IntPtr picture)
        {
            if (Data != null || Buffer == IntPtr.Zero || BufferSize <= 0) return;
            try
            {
                var data = new byte[BufferSize];
                Marshal.Copy(picture, data, 0, BufferSize);
                Data = data;
                Tcs.TrySetResult(true);
            }
            catch
            {
                Tcs.TrySetResult(false);
            }
        }
    }
}
