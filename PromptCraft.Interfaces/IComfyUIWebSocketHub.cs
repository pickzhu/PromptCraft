using PromptCraft.Models.ComfyUI;

namespace PromptCraft.Interfaces;

/// <summary>
/// ComfyUI WebSocket 进度/日志推送枢纽。单例：ComfyUIService 与 WorkflowViewModel 共用同一连接。
/// UI 订阅事件后需自行 Dispatcher 封送。
/// </summary>
public interface IComfyUIWebSocketHub
{
    /// <summary>当前客户端 id（每次连接生成）。</summary>
    string ClientId { get; }

    /// <summary>是否已连接。</summary>
    bool IsConnected { get; }

    /// <summary>启动后台连接（幂等；已在连接中则忽略）。</summary>
    void Start();

    /// <summary>停止连接并断开 WebSocket。</summary>
    void Stop();

    /// <summary>收到执行消息（原始 JSON，后台线程触发）。</summary>
    event Action<WsExecutionMessage>? MessageReceived;

    /// <summary>连接失败（后台线程触发）。</summary>
    event Action? ConnectionFailed;

    /// <summary>连接意外断开（后台线程触发）。</summary>
    event Action<string?>? ConnectionLost;
}
