using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FamilyAgent;

/// <summary>
/// 与 NAS 服务端的长连接客户端。
///
/// 行为：连接 → 心跳 → 收消息 → 回 ACK → 收截图请求 → 回图。
/// 断线自动重连（2s 起、指数退避、30s 封顶）。
/// </summary>
public sealed class AgentClient
{
    private const string AgentVersion = "cs-0.1.0";

    private readonly AgentConfig _config;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;

    public event Action<bool, string>? ConnectionChanged;
    public event Action<JsonElement>? MessageReceived;
    public event Action<string>? ScreenshotRequested;
    public event Action<JsonElement>? ReplyAcked;
    public event Action<JsonElement>? HistoryReceived;
    public event Action<string>? Log;

    public AgentClient(AgentConfig config) => _config = config;

    public bool Connected => _ws is { State: WebSocketState.Open };

    public void Start()
    {
        if (_cts is not null)
            return;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _ws?.Abort(); } catch { }
        _cts = null;
    }

    public void Restart()
    {
        Stop();
        Start();
    }

    private Uri BuildUri()
    {
        var baseUrl = (_config.ServerUrl ?? "").Trim().TrimEnd('/');
        if (baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            baseUrl = "wss://" + baseUrl[8..];
        else if (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            baseUrl = "ws://" + baseUrl[7..];
        else if (!baseUrl.StartsWith("ws", StringComparison.OrdinalIgnoreCase))
            baseUrl = "ws://" + baseUrl;
        if (baseUrl.EndsWith("/ws"))
            baseUrl = baseUrl[..^3];

        var q = new StringBuilder();
        void Add(string key, string value)
        {
            q.Append(q.Length == 0 ? '?' : '&');
            q.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value ?? ""));
        }

        Add("token", _config.Token ?? "");
        Add("name", _config.DeviceName ?? "");
        Add("type", "pc");
        Add("platform", $"Windows {Environment.OSVersion.Version}");
        Add("agent_version", AgentVersion);
        Add("enroll_token", _config.EnrollToken ?? "");

        return new Uri($"{baseUrl}/ws/device/{Uri.EscapeDataString(_config.DeviceId)}{q}");
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var delaySeconds = 2;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                _ws = ws;
                await ws.ConnectAsync(BuildUri(), ct).ConfigureAwait(false);

                delaySeconds = 2;
                ConnectionChanged?.Invoke(true, "已连接");

                var heartbeat = HeartbeatLoopAsync(ws, ct);
                await ReceiveLoopAsync(ws, ct).ConfigureAwait(false);
                await SafeAwait(heartbeat).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log?.Invoke("连接异常：" + ex.Message);
            }
            finally
            {
                _ws = null;
                ConnectionChanged?.Invoke(false, "未连接");
            }

            if (ct.IsCancellationRequested)
                break;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            delaySeconds = Math.Min(delaySeconds * 2, 30);
        }
    }

    private static async Task SafeAwait(Task task)
    {
        try { await task.ConfigureAwait(false); } catch { /* 心跳任务取消属正常 */ }
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                await SendRawAsync(ws, "{\"type\":\"heartbeat\"}", ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // 连接关闭或取消，交给主循环处理
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            sb.Clear();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    try
                    {
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", ct)
                                .ConfigureAwait(false);
                    }
                    catch { }
                    return;
                }
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            try
            {
                Dispatch(sb.ToString());
            }
            catch (Exception ex)
            {
                Log?.Invoke("消息解析失败：" + ex.Message);
            }
        }
    }

    private void Dispatch(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var typeEl))
            return;

        var kind = typeEl.GetString() ?? "";

        switch (kind)
        {
            case "hello":
                if (root.TryGetProperty("token", out var tokenEl))
                {
                    var token = tokenEl.GetString();
                    if (!string.IsNullOrEmpty(token) && token != _config.Token)
                    {
                        _config.Token = token!;
                        _config.Save();
                        Log?.Invoke("已保存设备令牌");
                    }
                }
                Log?.Invoke("已上线");
                break;

            case "message":
                MessageReceived?.Invoke(root.Clone());
                break;

            case "screenshot_request":
                if (root.TryGetProperty("request_id", out var ridEl))
                {
                    var rid = ridEl.GetString();
                    if (!string.IsNullOrEmpty(rid))
                        ScreenshotRequested?.Invoke(rid!);
                }
                break;

            case "reply_ack":
                ReplyAcked?.Invoke(root.Clone());
                break;

            case "history_response":
                HistoryReceived?.Invoke(root.Clone());
                break;

            case "heartbeat_ack":
                break;
        }

        if (kind != "heartbeat_ack")
            AgentLog.Write($"← {kind}");
    }

    private async Task SendRawAsync(ClientWebSocket ws, string json, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open)
            throw new InvalidOperationException($"连接不可用（{ws.State}）");

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct)
                    .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>发送一条 JSON。失败会抛异常，不再静默吞掉。</summary>
    public async Task SendJsonAsync<T>(T payload, string kind)
    {
        var ws = _ws;
        if (ws is null)
            throw new InvalidOperationException("尚未建立连接");

        var json = JsonSerializer.Serialize(payload);
        await SendRawAsync(ws, json, CancellationToken.None).ConfigureAwait(false);
        AgentLog.Write($"→ {kind}（{Encoding.UTF8.GetByteCount(json)} 字节）");
    }

    /// <summary>发送但不抛异常，只记日志 —— 给 ACK 这类「发不出去也不该崩」的场景用。</summary>
    private async Task TrySendJsonAsync<T>(T payload, string kind)
    {
        try
        {
            await SendJsonAsync(payload, kind).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AgentLog.Write($"✗ {kind} 发送失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    public Task AckAsync(long messageId, string status) =>
        TrySendJsonAsync(new { type = "ack", message_id = messageId, status }, $"ack:{status}");

    /// <summary>把弹窗里的回复发给服务端（昵称由本机本地维护）。</summary>
    public Task ReplyAsync(string senderName, string content, string clientId) =>
        SendJsonAsync(new
        {
            type = "reply",
            sender_name = senderName,
            content,
            client_id = clientId,
        }, "reply");

    /// <summary>主动拉一次历史对话（从托盘打开对话窗口时用）。</summary>
    public Task RequestHistoryAsync(int limit = 30) =>
        TrySendJsonAsync(new
        {
            type = "history_request",
            request_id = Guid.NewGuid().ToString("N")[..12],
            limit,
        }, "history_request");

    public async Task SendScreenshotAsync(string requestId, string? base64, int width, int height,
                                          string? error)
    {
        if (string.IsNullOrEmpty(base64))
        {
            await TrySendJsonAsync(new
            {
                type = "screenshot_response",
                request_id = requestId,
                error = string.IsNullOrEmpty(error) ? "截图失败" : error,
            }, "screenshot_response:error").ConfigureAwait(false);
            return;
        }

        await TrySendJsonAsync(new
        {
            type = "screenshot_response",
            request_id = requestId,
            format = "jpeg",
            data_base64 = base64,
            width,
            height,
            screen_locked = false,
        }, $"screenshot_response:{width}x{height}").ConfigureAwait(false);
    }
}
