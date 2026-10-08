using LibVLCSharp.Shared;
using System;

namespace PromptCraft.Service;

/// <summary>
/// 全局共享 LibVLC 实例：应用启动时后台预热（首次构造会加载插件库，可能数百毫秒），
/// 所有视频组件（MediaVideoView 等）共用同一实例，避免每个视频卡各自重复初始化造成首次添加卡顿。
/// 实例应用生命周期内常驻，不主动 Dispose（进程退出时随进程释放）。
/// 线程安全：LibVLC 实例可被多个 MediaPlayer 同时使用（LibVLCSharp 标准用法）。
/// </summary>
public static class LibVlcProvider
{
    private static LibVLC? _instance;
    private static readonly object Sync = new();

    public static LibVLC Instance
    {
        get
        {
            lock (Sync)
            {
                return _instance ??= new LibVLC(enableDebugLogs: false, "--avcodec-hw=any", "--file-caching=300");
            }
        }
    }

    /// <summary>预热：触发首次构造（含插件库加载），供应用启动时后台线程调用。</summary>
    public static void WarmUp() => _ = Instance;
}
