using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FamilyAgent.Core.Diagnostics;

namespace FamilyAgent.Core.Transport;

// ---------------- 发送队列 ----------------
//
// 教训：之前用 `if (!Connected) 报失败` 做前置判断，结果连接正常但状态判断
// 出过一次偏差，回复就永远发不出去。现在改成「不预判，直接发；失败才入队」，
// 并在心跳里定期重试补发 —— 断网期间的回也不会丢。
//
// 原样搬自 AgentClient.cs:604-696（见 docs/CORE-REFACTOR-PLAN.md §3.2 / §4）：
// 队列、积压计时、连续失败计数都在这里，**收发路径与日志文案逐字未变**。

/// <summary>
/// 发送队列 + 发送侧健康度（原 <c>AgentClient</c> 的 <c>_outbox</c> 全套）。
///
/// 「当前连接」通过注入的取值函数现取（不是构造时快照）：连接会换代，
/// 快照住旧连接正是「能收不能发」那类故障的成因。
/// </summary>
public sealed class Outbox
{
    private readonly WebSocketTransport _transport;
    private readonly Func<ClientWebSocket?> _socket;

    private readonly List<(string Json, string Kind)> _items = new();
    private readonly object _gate = new();

    /// <summary>积压开始的时间；没有积压时为 <see cref="DateTime.MinValue"/>。
    /// 看门狗靠它判定「有消息却迟迟发不出去」。</summary>
    private DateTime _sinceUtc = DateTime.MinValue;

    /// <summary>连续发送失败次数（连着两次就强制重连）。</summary>
    private int _failStreak;

    public Outbox(WebSocketTransport transport, Func<ClientWebSocket?> socket)
    {
        _transport = transport;
        _socket = socket;
    }

    /// <summary>积压未发出的消息条数。</summary>
    public int Count
    {
        get { lock (_gate) { return _items.Count; } }
    }

    /// <summary>积压开始的时间（心跳看门狗的输入之一）。</summary>
    public DateTime BacklogSinceUtc
    {
        get { lock (_gate) { return _sinceUtc; } }
    }

    /// <summary>发送一条消息。返回 true = 已进入发送流程；false = 当前无连接，已入队。</summary>
    public bool SendOrQueue<T>(T payload, string kind)
    {
        var json = JsonSerializer.Serialize(payload);
        var ws = _socket();

        if (ws is not null)
        {
            _ = SendNowAsync(ws, json, kind);   // 内部失败会自己入队
            return true;
        }

        EnqueueOutbox(json, kind);
        AgentLog.Write($"… {kind} 无连接对象，已入队等待补发");
        return false;
    }

    public async Task SendNowAsync(ClientWebSocket ws, string json, string kind)
    {
        try
        {
            await _transport.SendRawAsync(ws, json, CancellationToken.None).ConfigureAwait(false);
            Interlocked.Exchange(ref _failStreak, 0);
            AgentLog.Write($"→ {kind}（{Encoding.UTF8.GetByteCount(json)} 字节）");
        }
        catch (Exception ex)
        {
            AgentLog.Write($"✗ {kind} 发送失败（{WebSocketTransport.Describe(_socket())}）："
                           + $"{ex.GetType().Name} {ex.Message}");
            EnqueueOutbox(json, kind);

            // 连续发不出去 → 连接已经不可用，直接掐掉重连。
            // 「能收不能发」时不会有任何异常冒出来，只有这里能发现。
            if (Interlocked.Increment(ref _failStreak) >= 2)
            {
                AgentLog.Write("⚠ 连续发送失败，强制重连");
                WebSocketTransport.TryAbort(ws);
            }
        }
    }

    /// <summary>把积压的消息补发出去（连上时、以及心跳里定期调用）。</summary>
    public void Flush()
    {
        List<(string Json, string Kind)> pending;
        lock (_gate)
        {
            if (_items.Count == 0)
                return;
            pending = new List<(string, string)>(_items);
            _items.Clear();
            _sinceUtc = DateTime.MinValue;   // 正在尝试发送，失败会重新计时
        }

        var ws = _socket();
        if (ws is null)
        {
            lock (_gate)
            {
                _items.AddRange(pending);   // 还是没连接，原样放回
            }
            return;
        }

        AgentLog.Write($"⇡ 补发 {pending.Count} 条积压消息");
        foreach (var (json, kind) in pending)
            _ = SendNowAsync(ws, json, kind);
    }

    /// <summary>连接刚建立时清零连续失败计数（原 <c>hello</c> 分支里的 Interlocked.Exchange）。</summary>
    public void ResetFailStreak() => Interlocked.Exchange(ref _failStreak, 0);

    /// <summary>记一条待发消息，并开始计时（积压超时会被看门狗判定为发送链路已死）。</summary>
    private void EnqueueOutbox(string json, string kind)
    {
        lock (_gate)
        {
            _items.Add((json, kind));
            if (_sinceUtc == DateTime.MinValue)
                _sinceUtc = DateTime.UtcNow;
        }
    }
}
