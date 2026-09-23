using System;
using System.Collections.Generic;
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
    private const string AgentVersion = "cs-0.9.0";

    /// <summary>对外暴露的版本号，启动日志和排查时用。</summary>
    public static string ReportedVersion => AgentVersion;

    private readonly AgentConfig _config;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;

    // ── 发送侧健康度 ─────────────────────────────────────────────────
    // 真实故障：连接「能收不能发」—— 网页发来的消息照收，本机发出去的却上不去，
    // 而且不会有任何异常，必须手动点「保存并连接」才恢复。
    // 下面这几个计时器专门用来发现这种半死状态并自动重连。
    private DateTime _lastAckUtc = DateTime.UtcNow;      // 最近一次收到心跳回执
    private DateTime _lastBeatUtc = DateTime.MinValue;   // 最近一次发出心跳
    private DateTime _outboxSinceUtc = DateTime.MinValue; // 积压开始的时间
    private int _sendFailStreak;                          // 连续发送失败次数

    public event Action<bool, string>? ConnectionChanged;
    public event Action<JsonElement>? MessageReceived;
    public event Action<string>? ScreenshotRequested;
    public event Action<JsonElement>? ReplyAcked;
    public event Action<JsonElement>? HistoryReceived;
    public event Action<int>? ShutdownRequested;
    public event Action<string>? Log;

    public AgentClient(AgentConfig config) => _config = config;

    public bool Connected => _ws is { State: WebSocketState.Open };

    /// <summary>给日志用的连接状态描述（排查时能直接看出卡在哪）。</summary>
    public string DescribeConnection()
    {
        var ws = _ws;
        if (ws is null)
            return "无连接对象";
        return ws.State.ToString();
    }

    /// <summary>积压未发出的消息条数。</summary>
    public int OutboxCount
    {
        get { lock (_outboxGate) { return _outbox.Count; } }
    }

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
        _lastBeatUtc = DateTime.UtcNow;
        _lastAckUtc = DateTime.UtcNow;

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                var now = DateTime.UtcNow;

                // ① 心跳是「发出去 + 收回来」的完整往返。收不到回执就说明发送链路
                //    已经死了 —— 哪怕还能正常收到网页发来的消息。这是唯一能发现
                //    「能收不能发」的手段，不能再等用户手动去点「保存并连接」。
                if (now - _lastAckUtc > TimeSpan.FromSeconds(45))
                {
                    AgentLog.Write("⚠ 45 秒没收到心跳回执，判定发送链路已断，强制重连");
                    TryAbort(ws);
                    return;
                }

                // ② 有消息积压却迟迟发不出去 → 同样强制重连
                if (OutboxCount > 0 && _outboxSinceUtc != DateTime.MinValue &&
                    now - _outboxSinceUtc > TimeSpan.FromSeconds(12))
                {
                    AgentLog.Write($"⚠ {OutboxCount} 条消息积压超过 12 秒仍未发出，强制重连");
                    TryAbort(ws);
                    return;
                }

                // ③ 正常心跳（每 15 秒一次）
                if (now - _lastBeatUtc >= TimeSpan.FromSeconds(15))
                {
                    _lastBeatUtc = now;
                    await SendRawAsync(ws, "{\"type\":\"heartbeat\"}", ct).ConfigureAwait(false);
                }

                if (OutboxCount > 0)
                    FlushOutbox();
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            // 心跳发不出去也要掐掉连接触发重连，否则主循环会一直卡在接收上
            AgentLog.Write("心跳循环结束：" + ex.Message);
            TryAbort(ws);
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
                _lastAckUtc = DateTime.UtcNow;      // 连接刚建立，重置看门狗
                Interlocked.Exchange(ref _sendFailStreak, 0);
                FlushOutbox();
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

            case "shutdown":
                // 网页端点了「关机」→ 由本机执行
                var delay = PowerControl.DefaultDelaySeconds;
                if (root.TryGetProperty("delay_seconds", out var dEl) &&
                    dEl.ValueKind == JsonValueKind.Number)
                {
                    delay = dEl.GetInt32();
                }
                ShutdownRequested?.Invoke(delay);
                break;

            case "heartbeat_ack":
                // 心跳回执是「发送链路还活着」的唯一证据
                _lastAckUtc = DateTime.UtcNow;
                break;
        }

        if (kind != "heartbeat_ack")
            AgentLog.Write($"← {kind}");
    }

    private async Task SendRawAsync(ClientWebSocket ws, string json, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open)
            throw new InvalidOperationException($"连接不可用（{ws.State}）");

        // 加超时：发送有可能永久卡住（TCP 缓冲写满、对端不读），
        // 卡住比抛异常更糟 —— 外面完全看不出来。
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            await _sendLock.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("等待发送锁超时（10 秒）");
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token)
                    .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // ---------------- 发送队列 ----------------
    //
    // 教训：之前用 `if (!Connected) 报失败` 做前置判断，结果连接正常但状态判断
    // 出过一次偏差，回复就永远发不出去。现在改成「不预判，直接发；失败才入队」，
    // 并在心跳里定期重试补发 —— 断网期间的回也不会丢。

    private readonly List<(string Json, string Kind)> _outbox = new();
    private readonly object _outboxGate = new();

    /// <summary>发送一条消息。返回 true = 已进入发送流程；false = 当前无连接，已入队。</summary>
    public bool SendOrQueue<T>(T payload, string kind)
    {
        var json = JsonSerializer.Serialize(payload);
        var ws = _ws;

        if (ws is not null)
        {
            _ = SendNowAsync(ws, json, kind);   // 内部失败会自己入队
            return true;
        }

        EnqueueOutbox(json, kind);
        AgentLog.Write($"… {kind} 无连接对象，已入队等待补发");
        return false;
    }

    private async Task SendNowAsync(ClientWebSocket ws, string json, string kind)
    {
        try
        {
            await SendRawAsync(ws, json, CancellationToken.None).ConfigureAwait(false);
            Interlocked.Exchange(ref _sendFailStreak, 0);
            AgentLog.Write($"→ {kind}（{Encoding.UTF8.GetByteCount(json)} 字节）");
        }
        catch (Exception ex)
        {
            AgentLog.Write($"✗ {kind} 发送失败（{DescribeConnection()}）：{ex.GetType().Name} {ex.Message}");
            EnqueueOutbox(json, kind);

            // 连续发不出去 → 连接已经不可用，直接掐掉重连。
            // 「能收不能发」时不会有任何异常冒出来，只有这里能发现。
            if (Interlocked.Increment(ref _sendFailStreak) >= 2)
            {
                AgentLog.Write("⚠ 连续发送失败，强制重连");
                TryAbort(ws);
            }
        }
    }

    /// <summary>记一条待发消息，并开始计时（积压超时会被看门狗判定为发送链路已死）。</summary>
    private void EnqueueOutbox(string json, string kind)
    {
        lock (_outboxGate)
        {
            _outbox.Add((json, kind));
            if (_outboxSinceUtc == DateTime.MinValue)
                _outboxSinceUtc = DateTime.UtcNow;
        }
    }

    /// <summary>掐掉连接，逼主循环重连（发送侧卡死时唯一能自救的手段）。</summary>
    private static void TryAbort(ClientWebSocket ws)
    {
        try { ws.Abort(); } catch { }
    }

    /// <summary>把积压的消息补发出去（连上时、以及心跳里定期调用）。</summary>
    public void FlushOutbox()
    {
        List<(string Json, string Kind)> pending;
        lock (_outboxGate)
        {
            if (_outbox.Count == 0)
                return;
            pending = new List<(string, string)>(_outbox);
            _outbox.Clear();
            _outboxSinceUtc = DateTime.MinValue;   // 正在尝试发送，失败会重新计时
        }

        var ws = _ws;
        if (ws is null)
        {
            lock (_outboxGate)
            {
                _outbox.AddRange(pending);   // 还是没连接，原样放回
            }
            return;
        }

        AgentLog.Write($"⇡ 补发 {pending.Count} 条积压消息");
        foreach (var (json, kind) in pending)
            _ = SendNowAsync(ws, json, kind);
    }

    public void Ack(long messageId, string status) =>
        SendOrQueue(new { type = "ack", message_id = messageId, status }, $"ack:{status}");

    /// <summary>把弹窗里的回复发给服务端（昵称由本机本地维护）。
    /// 返回 true = 已尝试发送；false = 无连接，已入队等重连自动补发。</summary>
    public bool Reply(string senderName, string content, string clientId) =>
        SendOrQueue(new
        {
            type = "reply",
            sender_name = senderName,
            content,
            client_id = clientId,
        }, "reply");

    /// <summary>主动拉一次历史对话（从托盘打开对话窗口时用）。</summary>
    public void RequestHistory(int limit = 30) =>
        SendOrQueue(new
        {
            type = "history_request",
            request_id = Guid.NewGuid().ToString("N")[..12],
            limit,
        }, "history_request");

    /// <summary>回传截图。截图有时效性，连不上就丢弃并记日志，不排队。</summary>
    public async Task SendScreenshotAsync(string requestId, string? base64, int width, int height,
                                          string? error)
    {
        var ws = _ws;
        if (ws is null)
        {
            AgentLog.Write("✗ screenshot_response 无连接对象，丢弃");
            return;
        }

        object payload = string.IsNullOrEmpty(base64)
            ? new
            {
                type = "screenshot_response",
                request_id = requestId,
                error = string.IsNullOrEmpty(error) ? "截图失败" : error,
            }
            : new
            {
                type = "screenshot_response",
                request_id = requestId,
                format = "jpeg",
                data_base64 = base64,
                width,
                height,
                screen_locked = false,
            };

        var kind = string.IsNullOrEmpty(base64)
            ? "screenshot_response:error"
            : $"screenshot_response:{width}x{height}";

        await SendNowAsync(ws, JsonSerializer.Serialize(payload), kind).ConfigureAwait(false);
    }
}
