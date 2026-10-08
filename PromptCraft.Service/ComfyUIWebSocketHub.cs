using PromptCraft.Interfaces;
using PromptCraft.Models.ComfyUI;
using Ke.Bee.Localization.Localizer;

namespace PromptCraft.Service;

/// <summary>
/// ComfyUI WebSocket 全局长连接会话（应用级单例，DI 注册）。
/// 应用启动后即从配置地址（ComfySettings.ComfyApiUrl）尝试连接：
/// - 失败自动重试 3 次（每次连接阶段自带 8s 超时），仍失败触发 <see cref="ConnectionFailed"/>（UI 弹自动消失的提示）；
/// - 连接成功后长期保持，意外断线（如 ComfyUI 关闭）触发 <see cref="ConnectionLost"/>（UI 弹自动消失的提示）并自动重连（固定间隔），直至应用退出；
/// - 重试过程不逐条写日志，仅在最终连接成功后记一条"连接成功（尝试 N 次）"，避免日志被刷屏；
/// - client_id 固定（32 位无连字符，与官方前端一致），执行提交复用同一 id，消息实时到达不丢失；
/// - ws/wss 按配置地址 scheme 自动区分（http→ws / https→wss）。
/// </summary>
public sealed class ComfyUIWebSocketHub : IComfyUIWebSocketHub, IDisposable
{
    private readonly ComfyUIWebSocketClient _client;
    private readonly IBaseLogService _log;
    private CancellationTokenSource? _lifeCts;
    private Task? _loopTask;
    private int _started;

    /// <summary>
    /// 全局静态访问点（与 LogService.Instance 同风格）：DI 注册为单例，首个实例构造时赋值，
    /// 之后任意位置可直接 ComfyUIWebSocketHub.Instance.IsConnected / MessageReceived 使用，无需注入。
    /// </summary>
    public static ComfyUIWebSocketHub Instance { get; private set; } = null!;

    /// <summary>固定 client_id（与官方前端一致：32 位无连字符），WS 与 /api/prompt 共用。</summary>
    public string ClientId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>当前是否已建立 WebSocket 连接。</summary>
    public bool IsConnected => _client.IsConnected;

    /// <summary>收到执行消息（转发自底层会话；后台线程触发，UI 需自行 Dispatcher 封送）。</summary>
    public event Action<WsExecutionMessage>? MessageReceived;

    /// <summary>初次连接失败（已重试 3 次仍未通），UI 据此弹自动消失的提示。</summary>
    public event Action? ConnectionFailed;

    /// <summary>已建立连接后意外断开（如 ComfyUI 被关闭），UI 据此弹自动消失的提示；参数为断开原因（正常关闭为 null）。</summary>
    public event Action<string?>? ConnectionLost;

    public ComfyUIWebSocketHub(ComfySettings settings, IBaseLogService? log = null)
    {
        _log = log ?? PromptCraft.Service.LogService.Instance;
        _client = new ComfyUIWebSocketClient(settings, _log);
        _client.MessageReceived += msg => MessageReceived?.Invoke(msg);
        // 意外断线（ComfyUI 关闭 / 网络异常）：记一条日志并通知 UI（提示可自动消失）；
        // 自动重连由下方保持循环负责，重试过程不再逐条记日志
        _client.Disconnected += reason =>
        {
            _log.Warn(string.Format(Localizer.Instance?["WsDisconnectedAutoReconnect"] ?? "", reason ?? (Localizer.Instance?["WsClosedByRemote"] ?? "")), "ComfyUI");
            ConnectionLost?.Invoke(reason);
        };
        Instance = this; // 单例全局访问点：最后一个构造的实例即全局实例
    }

    /// <summary>启动长连接循环（幂等）：先尝试连接（最多 3 次），成功后保持并断线自动重连。</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _lifeCts = new CancellationTokenSource();
        _loopTask = Task.Run(() => ConnectionLoopAsync(_lifeCts.Token));
    }

    /// <summary>停止长连接并释放底层会话（应用退出 / 配置变更时调用）。</summary>
    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _lifeCts, null);
        if (cts == null) return;
        cts.Cancel();
        _client.Disconnect();
        try { _loopTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* 循环被取消退出，忽略 */ }
        Interlocked.Exchange(ref _started, 0);
    }

    private async Task ConnectionLoopAsync(CancellationToken ct)
    {
        // 初次连接：最多尝试 3 次（连接阶段每次自带 8s 超时）；重试过程不逐条记日志，
        // 仅在最终连接成功后记一条（含尝试次数）
        var (connected, attempts) = await TryConnectAsync(ct);
        if (connected)
        {
            _log.Info(string.Format(Localizer.Instance?["WsConnected"] ?? "", attempts), "ComfyUI");
        }
        else if (!ct.IsCancellationRequested)
        {
            _log.Error(Localizer.Instance?["WsConnectFailed"] ?? "", "ComfyUI");
            ConnectionFailed?.Invoke();
        }

        // 保持连接：断线后自动重连（固定 5s 间隔），失败不记日志，成功时记一条含尝试次数
        var reconnectAttempts = 0;
        while (!ct.IsCancellationRequested)
        {
            if (_client.IsConnected)
            {
                reconnectAttempts = 0;
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                continue;
            }

            reconnectAttempts++;
            var ok = await _client.ConnectAsync(ClientId, ct);
            if (ok)
                _log.Info(string.Format(Localizer.Instance?["WsReconnected"] ?? "", reconnectAttempts), "ComfyUI");
            else
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    /// <summary>尝试连接最多 3 次，返回是否成功与已尝试次数（失败不逐条记日志）。</summary>
    private async Task<(bool Connected, int Attempts)> TryConnectAsync(CancellationToken ct)
    {
        var attempts = 0;
        for (var i = 0; i < 3 && !ct.IsCancellationRequested; i++)
        {
            attempts++;
            if (await _client.ConnectAsync(ClientId, ct)) return (true, attempts);
            if (i < 2) await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return (false, attempts);
    }

    public void Dispose() => Stop();
}
