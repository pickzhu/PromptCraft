using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace PromptCraft
{
    public partial class VideoItemView : UserControl
    {
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

        private readonly string _videoPath;
        // 固定的本地视频路径
        //private const string LocalVideoPath = "F:\\Comfy-Desktop\\ComfyUI-Shared\\output\\video\\minimaxh3\\MiniMax_H3_00002_.mp4";

        // 用于向父级传递“移除自身”的事件
        public event EventHandler? RemoveRequested;

        public VideoItemView(string videoPath)
        {
            InitializeComponent();
            _videoPath = videoPath; // 存储路径
            this.DetachedFromVisualTree += (s, e) => ReleasePlayer();

            // 组件加载后自动初始化播放器并加载视频
            AttachedToVisualTree += (_, _) =>
            {
                Dispatcher.UIThread.Post(() => {
                    InitializePlayer();
                    AutoLoadVideo();
                }, DispatcherPriority.Background);
            };
        }

        private void InitializePlayer()
        {
            if (MyVideoView == null || _mediaPlayer != null) return;

            _libVLC = new LibVLC(enableDebugLogs: false, "--avcodec-hw=any", "--file-caching=300");
            _mediaPlayer = new MediaPlayer(_libVLC);
            MyVideoView.MediaPlayer = _mediaPlayer;

            // 🎯 核心改进：监听视频播放完毕事件
            _mediaPlayer.EndReached += (s, e) =>
            {
                // 1. 立刻切回 UI 线程，用第一帧画面盖住视频视口，防止闪烁/黑屏
                Dispatcher.UIThread.Post(() =>
                {
                    if (_firstFrameData != null)
                    {
                        FirstFramePopup.IsOpen = true;
                    }
                });

                // 2. 异步执行 Stop()。LibVLC 官方建议不要在事件回调中同步调用 Stop() 以免阻塞
                //    Stop() 会将播放器状态从 Ended 重置回 Stopped，进度也会自动归 0
                Task.Run(() =>
                {
                    try
                    {
                        _mediaPlayer?.Stop();
                        System.Diagnostics.Debug.WriteLine("[VLC] 视频播放完毕，已自动 Stop 重置进度。");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[VLC] 播放完毕自动重置失败: {ex.Message}");
                    }
                });
            };
            // 预热 Popup
            Dispatcher.UIThread.Post(() =>
            {
                FirstFramePopup.IsOpen = true;
                Dispatcher.UIThread.Post(() =>
                {
                    FirstFramePopup.IsOpen = false;
                }, DispatcherPriority.Background);
            }, DispatcherPriority.Background);
        }

        private async void AutoLoadVideo()
        {
            if (_libVLC == null || _mediaPlayer == null) return;

            var firstFrame = await GrabFirstFrameAsync(_videoPath);
            if (firstFrame != null)
            {
                FirstFrameImage.Source = firstFrame;
                FirstFramePopup.IsOpen = true;
            }

            var media = new Media(_libVLC, _videoPath, FromType.FromPath);
            _mediaPlayer.Media = media;
            _mediaPlayer.Volume = 100;
        }

        private async void OnPauseClicked(object? sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null) return;

            if (_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Pause();
            }
            else
            {
                _mediaPlayer.Play();
                await Task.Delay(250);
                FirstFramePopup.IsOpen = false;
            }
        }

        private async void OnStopClicked(object? sender, RoutedEventArgs e)
        {
            if (_mediaPlayer == null || _isStopping) return;
            _isStopping = true;

            if (sender is Button stopBtn) stopBtn.IsEnabled = false;

            try
            {
                try { _mediaPlayer.Pause(); } catch { }

                if (_firstFrameData != null)
                {
                    FirstFramePopup.IsOpen = true;
                }

                await Task.Delay(50);

                await Task.Run(() =>
                {
                    try { _mediaPlayer.Stop(); } catch { }
                });
            }
            finally
            {
                _isStopping = false;
                if (sender is Button btn) btn.IsEnabled = true;
            }
        }

        private void OnRemoveClicked(object? sender, RoutedEventArgs e)
        {
            ReleasePlayer();
            RemoveRequested?.Invoke(this, EventArgs.Empty);
        }

        // ================== 以下为原汁原味的底层抓帧代码 ==================
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

        public void ReleasePlayer()
        {
            if (MyVideoView != null) MyVideoView.MediaPlayer = null;
            var player = _mediaPlayer;
            var vlc = _libVLC;

            Task.Run(() =>
            {
                try { player?.Stop(); } catch { }
                try { player?.Dispose(); } catch { }
                try { vlc?.Dispose(); } catch { }
            });
            _mediaPlayer = null;
            _libVLC = null;
        }
    }
}
