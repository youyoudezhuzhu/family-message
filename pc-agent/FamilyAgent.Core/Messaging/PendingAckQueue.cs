using System;
using System.Collections.Generic;
using FamilyAgent.Core.Diagnostics;

namespace FamilyAgent.Core.Messaging;

/// <summary>一条「已决策、但还没交给传输层」的 ACK。</summary>
public sealed class PendingAck
{
    internal PendingAck(long messageId, string status)
    {
        MessageId = messageId;
        Status = status;
    }

    public long MessageId { get; }

    /// <summary>协议里的 ack 状态字面量（<c>device_received</c> / <c>popup_displayed</c> / <c>read</c>）。</summary>
    public string Status { get; }

    /// <summary>尝试补发过几次（<see cref="PendingAckQueue.Flush"/> 每次尝试 +1）。</summary>
    public int Attempts { get; internal set; }

    public override string ToString() => $"ack:{Status}#{MessageId}(尝试 {Attempts})";
}

/// <summary>
/// 「已收未 ACK」的补发队列（docs/CORE-REFACTOR-PLAN.md §3.2 / §6 Phase 2-1）。
///
/// 它替掉的是原来 UI 层那套 <c>_pending</c>（<c>WebHostWindow.xaml.cs:95</c>）语义：
/// **ACK 该不该发、发什么状态，是 Core 的判断**；UI 只回报事实
/// （「画出来了」/「关窗了」），不再自己决定发什么 ack。
///
/// 三层兜底里的最内一层（另外两层是 <c>Transport.Outbox</c> 的断线入队与
/// 服务端的投递记账）：
/// <list type="number">
///   <item>ACK 决策做出 → <see cref="Enqueue"/> 登记 → 交给传输层，成功就 <see cref="MarkSent"/> 移除；</item>
///   <item>交给传输层**失败**（抛异常）→ 留在队列里，重连/心跳时由 <see cref="Flush"/> 补发；</item>
///   <item>交给传输层成功但连接是断的 → 由 <c>Outbox</c> 入队、重连后补发（那条路不变）。</item>
/// </list>
///
/// ⚠ 它**不是**持久化队列：进程重启后队列为空 —— 跨重启的补齐靠服务端
///   <c>pending_for_device</c> 补投 + <see cref="IMessageStore"/> 按 <c>message_id</c> 去重
///   （§6 Phase 4 的 SyncService 会在这之上做 history 补齐；本轮不假装它已经有）。
/// </summary>
public sealed class PendingAckQueue
{
    private readonly object _gate = new();
    private readonly List<PendingAck> _items = new();

    /// <summary>还没交出去的 ACK 条数。</summary>
    public int Count
    {
        get { lock (_gate) { return _items.Count; } }
    }

    /// <summary>
    /// 登记一条待发 ACK（同一条消息 + 同一状态不重复登记）。
    /// 返回 true = 是新登记的；false = 这条已经在队列里了。
    /// </summary>
    public bool Enqueue(long messageId, string status)
    {
        if (messageId <= 0 || string.IsNullOrWhiteSpace(status))
            return false;

        lock (_gate)
        {
            foreach (var item in _items)
            {
                if (item.MessageId == messageId && item.Status == status)
                    return false;
            }
            _items.Add(new PendingAck(messageId, status));
            return true;
        }
    }

    /// <summary>这条已经交给传输层了 → 从队列里移掉（返回是否找到并移除）。</summary>
    public bool MarkSent(long messageId, string status)
    {
        lock (_gate)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (_items[i].MessageId == messageId && _items[i].Status == status)
                {
                    _items.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }
    }

    public bool IsPending(long messageId, string status)
    {
        lock (_gate)
        {
            foreach (var item in _items)
            {
                if (item.MessageId == messageId && item.Status == status)
                    return true;
            }
            return false;
        }
    }

    /// <summary>当前待发快照（按入队顺序；日志与单测用，改它不影响队列）。</summary>
    public IReadOnlyList<PendingAck> Snapshot()
    {
        lock (_gate) { return new List<PendingAck>(_items); }
    }

    /// <summary>
    /// 补发所有待发 ACK，返回成功交出去的条数。
    ///
    /// 每条的失败互不影响（一条抛异常不会挡后面的），失败的原样留在队列里等下次。
    /// </summary>
    public int Flush(Action<long, string> send)
    {
        if (send is null)
            return 0;

        List<PendingAck> pending;
        lock (_gate)
        {
            if (_items.Count == 0)
                return 0;
            pending = new List<PendingAck>(_items);
        }

        AgentLog.Write($"[MSG] 补发 {pending.Count} 条待发 ACK");
        var done = 0;
        foreach (var item in pending)
        {
            item.Attempts++;
            try
            {
                send(item.MessageId, item.Status);
                MarkSent(item.MessageId, item.Status);
                done++;
            }
            catch (Exception ex)
            {
                AgentLog.Write($"[MSG] {item} 补发失败（留在队列里）：{ex.Message}");
            }
        }
        return done;
    }
}
