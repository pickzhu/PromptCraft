using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace PromptCraft.Controls;

/// <summary>
/// 视频素材卡播放组件 —— 复用 Test 页 VideoItemView 已验证范式：
/// 每个组件独立 LibVLC 实例 + MediaPlayer attach 到 VideoView。
/// 封面首帧（GrabFirstFrameAsync）显示在组件内普通 Image 标签（底层常显，随布局滚动，
/// 无 Popup 顶层窗口滚动不跟随问题）；VideoView（空域窗口）默认隐藏，播放时显示盖住封面，
/// 暂停保留画面，停止/播完再隐藏露出封面。
/// DataTemplate 接入：VideoPath 绑定素材路径；IsPlaying(TwoWay) 与素材状态同步；外部按钮调用 PlayPause()。
/// </summary>
public partial class MediaVideoView : UserControl
{
    /// <summary>素材视频路径（绑定 MediaItem.FilePath）。</summary>
    public static readonly StyledProperty<string> VideoPathProperty =
        AvaloniaProperty.Register<MediaVideoView, string>(nameof(VideoPath));

    public string VideoPath
    {
        get => GetValue(VideoPathProperty);
        set => SetValue(VideoPathProperty, value);
    }

    /// <summary>播放状态（TwoWay 绑定素材 IsPlaying，供卡片播放按钮图标切换）。</summary>
    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<MediaVideoView, bool>(nameof(IsPlaying), defaultBindingMode: BindingMode.TwoWay);

    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    /// <summary>外部封面路径（如工作流缩略图 jpg）。非空且文件存在时直接用作封面，跳过首帧抓取（避免重复解码）。
    /// 扩写/反推素材卡不传此属性，保持组件内自抓首帧。</summary>
    public static readonly StyledProperty<string?> CoverPathProperty =
        AvaloniaProperty.Register<MediaVideoView, string?>(nameof(CoverPath));

    public string? CoverPath
    {
        get => GetValue(CoverPathProperty);
        set => SetValue(CoverPathProperty, value);
    }

    private LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;
    private MediaPlayer? _currentPreviewPlayer;

    private byte[]? _firstFrameData;
    private uint _frameWidth, _frameHeight;
    private uint _pitches, _lines;
    private bool _frameCaptured;

    private IntPtr _frameBuffer = IntPtr.Zero;
    private int _frameBufferSize = 0;
    private TaskCompletionSource<bool>? _frameReadyTcs;
    private bool _isStopping;
    private bool _attached;

    public MediaVideoView()
    {
        InitializeComponent();

        // 组件加载后自动初始化播放器并加载视频（对齐 Test：AttachedToVisualTree 后延迟到 Background，
        // 保证 VideoView 原生句柄（_platformHandle）已创建、IsInitialized，Attach() 才会把 Hwnd 设到 MediaPlayer）
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            Dispatcher.UIThread.Post(() =>
            {
                InitializePlayer();
                AutoLoadVideo();
            }, DispatcherPriority.Background);
        };
        // 组件销毁时释放播放器（对齐 Test：DetachedFromVisualTree → ReleasePlayer）
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            ReleasePlayer();
        };
        // ★灰屏根治：VideoView 是 NativeControlHost（空域窗口），原生句柄（_platformHandle）在布局/滚动/
        // 可见性变化时会销毁重建，且没有“句柄重建”钩子 → 播放器 Hwnd 停在旧句柄 → 画面出不来（灰屏）。
        // 监听 VideoView 自身每次 attach（句柄确定就绪/刚重建），强制 Detach+Attach 重挂最新句柄。
        MyVideoView.AttachedToVisualTree += (_, _) => EnsureAttached();

        // ★空域窗口始终在 Avalonia 内容之上：封面 Image 是普通元素，被 VideoView 压住。
        // 因此 VideoView 默认隐藏（IsVisible=false，XAML），仅播放/暂停时显示；
        // 播放时句柄刚创建，EnsureAttached 重挂后再 Play，保证画面输出到有效句柄。
        Visual.IsVisibleProperty.Changed.AddClassHandler<LibVLCSharp.Avalonia.VideoView>((s, _) =>
        {
            if (s.IsVisible) EnsureAttached();
        });

        // VideoPath 变化：仅已 attach（运行中换素材）时重载；绑定建立阶段由 AttachedToVisualTree 统一加载，
        // 避免 attach 前 Post 早于句柄创建导致 Hwnd 无效（灰屏根因）
        VideoPathProperty.Changed.AddClassHandler<MediaVideoView>((s, _) =>
        {
            if (!s._attached) return;
            Dispatcher.UIThread.Post(() =>
            {
                InitializePlayer();
                AutoLoadVideo();
            }, DispatcherPriority.Background);
        });

        // 外部封面变化：仅替换封面图（播放器/视频不变），已 attach 时生效
        CoverPathProperty.Changed.AddClassHandler<MediaVideoView>((s, _) =>
        {
            if (!s._attached) return;
            Dispatcher.UIThread.Post(() => s.ApplyCover(), DispatcherPriority.Background);
        });
    }

    /// <summary>封面优先用外部路径（CoverPath），否则自抓首帧。绑定建立阶段由 AutoLoadVideo 统一调用。</summary>
    private async void ApplyCover()
    {
        var cover = CoverPath;
        if (!string.IsNullOrWhiteSpace(cover) && File.Exists(cover))
        {
            try
            {
                var bmp = await Task.Run(() => new Bitmap(cover));
                if (bmp != null) FirstFrameImage.Source = bmp;
                return;
            }
            catch
            {
                // 外部封面损坏 → 退回自抓首帧
            }
        }

        var path = VideoPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var firstFrame = await GrabFirstFrameAsync(path);
        if (firstFrame != null) FirstFrameImage.Source = firstFrame;
    }

    /// <summary>强制把播放器重新 attach 到 VideoView 的最新句柄（句柄创建/重建后调用；Detach 清旧 Hwnd 再 Attach）。</summary>
    private void EnsureAttached()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_mediaPlayer == null || MyVideoView == null) return;
            MyVideoView.MediaPlayer = null;         // Detach：清掉可能已失效的旧 Hwnd
            MyVideoView.MediaPlayer = _mediaPlayer;  // Attach：绑定刚就绪/重建后的新句柄
        }, DispatcherPriority.Background);
    }

    private void InitializePlayer()
    {
        if (MyVideoView == null || _mediaPlayer != null) return;

        // 全局共享 LibVLC 实例（应用启动时已预热，避免每卡重复初始化插件库造成首次添加卡顿）
        _libVLC = PromptCraft.Service.LibVlcProvider.Instance;
        _mediaPlayer = new MediaPlayer(_libVLC);
        MyVideoView.MediaPlayer = _mediaPlayer;

        // 播放状态同步到 IsPlaying（TwoWay → 素材 IsPlaying）；
        // Playing 事件触发说明视频已在 VideoView（空域窗口）输出画面 → 显示 VideoView 盖住封面 Image，画面衔接不闪
        _mediaPlayer.Playing += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            IsPlaying = true;
            MyVideoView.IsVisible = true;
        });
        _mediaPlayer.Paused += (_, _) => Dispatcher.UIThread.Post(() => IsPlaying = false);

        // 播放完毕 → 隐藏 VideoView（露出封面 Image）+ 后台 Stop 重置进度（对齐 Test）
        _mediaPlayer.EndReached += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                MyVideoView.IsVisible = false;
                IsPlaying = false;
            });
            Task.Run(() =>
            {
                try { _mediaPlayer?.Stop(); } catch { }
            });
        };
    }

    private async void AutoLoadVideo()
    {
        if (_libVLC == null || _mediaPlayer == null) return;
        var path = VideoPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        // 抓首帧作封面：成功则显示在组件内普通 Image（随布局滚动，不再用 Popup）
        ApplyCover();

        var media = new Media(_libVLC, path, FromType.FromPath);
        _mediaPlayer.Media = media;
        _mediaPlayer.Volume = 100;
    }

    /// <summary>播放/暂停切换（卡片播放按钮调用）。播放：显示 VideoView（空域窗口盖住封面）→ EnsureAttached 重挂句柄 → Play；
    /// 暂停：仅 Pause，保留暂停画面（VideoView 保持显示，不弹遮罩）。</summary>
    public async void PlayPause()
    {
        if (_mediaPlayer == null) return;

        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Pause();
        }
        else
        {
            // 修复竞态：AutoLoadVideo 需先抓帧再挂 Media，期间若用户点播放，Media 还是 null → Play 无效。
            if (_mediaPlayer.Media == null)
            {
                var path = VideoPath;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path) && _libVLC != null)
                {
                    _mediaPlayer.Media = new Media(_libVLC, path, FromType.FromPath);
                }
            }
            MyVideoView.IsVisible = true;
            _mediaPlayer.Play();
        }
    }

    /// <summary>停止并回到封面（隐藏 VideoView 露出封面 Image；对齐 Test OnStopClicked）。</summary>
    public async void Stop()
    {
        if (_mediaPlayer == null || _isStopping) return;
        _isStopping = true;
        try
        {
            try { _mediaPlayer.Pause(); } catch { }
            MyVideoView.IsVisible = false;
            await Task.Delay(50);
            await Task.Run(() =>
            {
                try { _mediaPlayer.Stop(); } catch { }
            });
        }
        finally
        {
            _isStopping = false;
        }
    }

    /// <summary>释放播放器（组件销毁/移除素材时；LibVLC 实例为全局共享，不在此处 Dispose）。</summary>
    public void ReleasePlayer()
    {
        if (MyVideoView != null) MyVideoView.MediaPlayer = null;
        var player = _mediaPlayer;

        Task.Run(() =>
        {
            try { player?.Stop(); } catch { }
            try { player?.Dispose(); } catch { }
        });
        _mediaPlayer = null;
        _libVLC = null;
    }

    // ================== 原汁原味底层抓帧（复制自 Test VideoItemView） ==================

    private async Task<Bitmap?> GrabFirstFrameAsync(string videoPath)
    {
        if (_libVLC == null) return null;

        _firstFrameData = null;
        _frameCaptured = false;
        _frameWidth = _frameHeight = 0;
        _pitches = _lines = 0;
        _frameBufferSize = 0;
        if (_frameBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_frameBuffer);
            _frameBuffer = IntPtr.Zero;
        }

        var previewPlayer = new MediaPlayer(_libVLC);
        _currentPreviewPlayer = previewPlayer;

        previewPlayer.SetVideoFormatCallbacks(OnVideoFormat, OnVideoCleanup);
        previewPlayer.SetVideoCallbacks(Lock, Unlock, Display);

        var frameReadyTcs = new TaskCompletionSource<bool>();
        _frameReadyTcs = frameReadyTcs;

        previewPlayer.EncounteredError += (s, e) => frameReadyTcs.TrySetResult(false);

        var media = new Media(_libVLC, videoPath, FromType.FromPath);
        previewPlayer.Play(media);

        Bitmap? result = null;
        try
        {
            var delay = Task.Delay(TimeSpan.FromSeconds(10));
            var completed = await Task.WhenAny(frameReadyTcs.Task, delay);

            if (completed == frameReadyTcs.Task && frameReadyTcs.Task.Result && _firstFrameData != null && _frameWidth > 0 && _frameHeight > 0)
            {
                result = CreateBitmap(_firstFrameData, _frameWidth, _frameHeight);
            }
        }
        catch { }
        finally
        {
            _frameReadyTcs = null;
            _currentPreviewPlayer = null;

            await Task.Run(() =>
            {
                try { previewPlayer.Stop(); } catch { }
                try { previewPlayer.Dispose(); } catch { }
            });

            if (_frameBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_frameBuffer);
                _frameBuffer = IntPtr.Zero;
                _frameBufferSize = 0;
            }
        }

        return result;
    }

    private uint OnVideoFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        _frameWidth = width;
        _frameHeight = height;
        var chromaBytes = Encoding.ASCII.GetBytes("RV32");
        Marshal.Copy(chromaBytes, 0, chroma, 4);
        pitches = width * 4;
        lines = height;
        _pitches = pitches;
        _lines = lines;

        if (!_frameCaptured)
        {
            int newSize = (int)(pitches * lines);
            if (_frameBuffer != IntPtr.Zero && _frameBufferSize != newSize)
            {
                Marshal.FreeHGlobal(_frameBuffer);
                _frameBuffer = IntPtr.Zero;
            }
            if (_frameBuffer == IntPtr.Zero)
            {
                _frameBufferSize = newSize;
                _frameBuffer = Marshal.AllocHGlobal(newSize);
            }
        }
        return 1;
    }

    private void OnVideoCleanup(ref IntPtr opaque) { }

    private IntPtr Lock(IntPtr opaque, IntPtr planes)
    {
        if (_frameBuffer != IntPtr.Zero) Marshal.WriteIntPtr(planes, _frameBuffer);
        return _frameBuffer;
    }

    private void Unlock(IntPtr opaque, IntPtr picture, IntPtr planes) { }

    private void Display(IntPtr opaque, IntPtr picture)
    {
        if (_frameCaptured) return;
        _frameCaptured = true;

        if (_frameBuffer == IntPtr.Zero || _frameBufferSize <= 0) return;

        try
        {
            _firstFrameData = new byte[_frameBufferSize];
            Marshal.Copy(picture, _firstFrameData, 0, _frameBufferSize);
        }
        catch
        {
            _frameReadyTcs?.TrySetResult(false);
            return;
        }
        _frameReadyTcs?.TrySetResult(true);
    }

    private Bitmap CreateBitmap(byte[] data, uint width, uint height)
    {
        var bitmap = new WriteableBitmap(new PixelSize((int)width, (int)height), new Avalonia.Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = bitmap.Lock()) { Marshal.Copy(data, 0, fb.Address, data.Length); }
        return bitmap;
    }
}
