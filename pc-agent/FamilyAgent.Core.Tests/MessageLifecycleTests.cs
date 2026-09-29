using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FamilyAgent.Core.Events;
using FamilyAgent.Core.Messaging;
using FamilyAgent.Core.Protocol.Frames;
using Xunit;

namespace FamilyAgent.Core.Tests;

/// <summary>
/// Phase 2 的验收单测（docs/CORE-REFACTOR-PLAN.md §6 Phase 2 的测试 A/B/C）：
///
/// <list type="bullet">
///   <item><b>A</b>（WebView2 失效仍可工作）—— Core 侧可测的部分：界面**完全不存在**
///     （没有订阅者 / 订阅者抛异常 / 界面建不起来 → 平台层回报「无法显示」）时，
///     消息仍落盘、ack 仍发出、且不会永远挂在「未 ACK」。真实 WebView2 故障的手工验证
///     步骤见本文件末尾的注释与交付说明。</item>
///   <item><b>B</b>（没有 UI 订阅者也不丢）—— 注入抛异常的订阅者，断言 MessageStore 已写入
///     且 ack 仍发出。</item>
///   <item><b>C</b>（重放）—— 进程重启（新建 Store + MessageManager 读同一个文件）后，
///     此前未显示的消息在界面就绪时被重新投递且**不重复**（按 message_id 去重）。</item>
/// </list>
/// </summary>
public class MessageLifecycleTests
{
    /// <summary>服务端认识的全部 ack 状态（server/services/messages.py 的 STATES）—— 拿它当白名单。</summary>
    private static readonly string[] ServerKnownStates =
    {
        "created", "server_received", "device_received", "popup_displayed", "read",
    };

    private static MessageFrame Frame(long id, string content = "晚饭好了", int autoClose = 0) => new()
    {
        Type = "message",
        Raw = JsonSerializer.SerializeToElement(new
        {
            type = "message",
            message_id = id,
            sender_name = "爸爸",
            content,
            message_type = "text",
            created_at = "2026-09-29T12:00:00",
            status = "sent",
            auto_close_seconds = autoClose,
        }),
        MessageId = id,
        SenderName = "爸爸",
        Content = content,
        MessageType = "text",
        CreatedAt = "2026-09-29T12:00:00",
        AutoCloseSeconds = autoClose,
    };

    /// <summary>一个可切换「发不出去」的假传输层：同时当 ack 记录器。</summary>
    private sealed class FakeAckSink
    {
        public List<(long Id, string Status)> Sent { get; } = new();

        /// <summary>true = 交给传输层时抛异常（模拟发送链路故障）。</summary>
        public bool Failing { get; set; }

        public void Send(long id, string status)
        {
            if (Failing)
                throw new InvalidOperationException("模拟传输层故障");
            Sent.Add((id, status));
        }

        public string[] Statuses()
        {
            var result = new List<string>();
            foreach (var (_, status) in Sent)
                result.Add(status);
            return result.ToArray();
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "familyagent-tests-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public string File_ => System.IO.Path.Combine(Path, "messages.jsonl");

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }

    // ── 生命周期主体 ────────────────────────────────────────────

    [Fact]
    public void IncomingMessage_IsPersistedBeforeAnyUiWork()
    {
        var store = new MemoryMessageStore();
        var sink = new FakeAckSink();
        var manager = new MessageManager(store, sink.Send);
        var states = new List<DeliveryState>();
        manager.DeliveryStateChanged += e => states.Add(e.State);

        var received = 0;
        manager.MessageReceived += _ => received++;

        manager.HandleIncoming(Frame(42, autoClose: 30));

        // ① 落盘（事件顺序：先 Received/Persisted，才轮到平台层显示）
        var record = store.Get(42);
        Assert.NotNull(record);
        Assert.Equal(DeliveryState.Persisted, record!.State);
        Assert.Equal("晚饭好了", record.Content);
        Assert.Equal(30, record.AutoCloseSeconds);
        Assert.Equal(new[] { DeliveryState.Received, DeliveryState.Persisted }, states);
        Assert.Equal(1, received);

        // ② 还没显示 → 一条 ack 都不许发（popup_displayed 不得提前，§7E-13）
        Assert.Empty(sink.Sent);
    }

    [Fact]
    public void NotifyDisplayed_SendsPopupDisplayedAndMarksDisplayed()
    {
        var store = new MemoryMessageStore();
        var sink = new FakeAckSink();
        var manager = new MessageManager(store, sink.Send);
        manager.MessageReceived += _ => { };
        var states = new List<DeliveryState>();
        manager.DeliveryStateChanged += e => states.Add(e.State);

        manager.HandleIncoming(Frame(7));
        manager.NotifyDisplayed(7);

        Assert.Equal(new[] { (7L, "popup_displayed") }, sink.Sent);
        Assert.Equal(DeliveryState.Displayed, store.Get(7)!.State);
        Assert.Contains(DeliveryState.AckSent, states);
        Assert.Equal(new List<DeliveryState>
        {
            DeliveryState.Received, DeliveryState.Persisted,
            DeliveryState.Displayed, DeliveryState.AckSent,
        }, states);

        // 重复确认不再重复发 ack
        manager.NotifyDisplayed(7);
        Assert.Single(sink.Sent);
    }

    [Fact]
    public void NotifyDismissed_SendsRead()
    {
        var store = new MemoryMessageStore();
        var sink = new FakeAckSink();
        var manager = new MessageManager(store, sink.Send);
        manager.MessageReceived += _ => { };

        manager.HandleIncoming(Frame(8));
        manager.NotifyDismissed(8);

        Assert.Equal(new[] { (8L, "read") }, sink.Sent);
        Assert.Equal(DeliveryState.Read, store.Get(8)!.State);
        Assert.Equal(new[] { "read" }, sink.Statuses());
    }

    // ── 测试 B：没有 UI 订阅者也不丢 ────────────────────────────

    /// <summary>
    /// ★ 测试 B：订阅者**抛异常**时 —— ① <c>MessageStore</c> 已写入；② ack 仍发出。
    /// 这是「ACK 与 UI 显示解耦」的核心断言：UI 这条腿断了，消息与记账都不受影响。
    /// </summary>
    [Fact]
    public void SubscriberThrows_MessageStillPersisted_AndAckStillSent()
    {
        var store = new MemoryMessageStore();
        var sink = new FakeAckSink();
        var manager = new MessageManager(store, sink.Send);
        manager.MessageReceived += _ => throw new InvalidOperationException("UI 崩了");

        manager.HandleIncoming(Frame(100));

        var record = store.Get(100);
        Assert.NotNull(record);
        Assert.Equal(DeliveryState.NotDisplayed, record!.State);
        Assert.Equal(new[] { (100L, "device_received") }, sink.Sent);
    }

    /// <summary>完全没有订阅者（UI 根本没起来）时同样：落盘 + 终态 ack，不会永远「未 ACK」。</summary>
    [Fact]
    public void NoSubscriber_MessageStillPersisted_AndAckStillSent()
    {
        var store = new MemoryMessageStore();
        var sink = new FakeAckSink();
        var manager = new MessageManager(store, sink.Send);

        manager.HandleIncoming(Frame(101));

        Assert.Equal(DeliveryState.NotDisplayed, store.Get(101)!.State);
        Assert.Equal(new[] { (101L, "device_received") }, sink.Sent);
    }

    /// <summary>
    /// headless（会话 0，无人登录）→ 平台层回报「无法显示」。
    /// 原实现这里发的是非法状态 <c>delivered</c>（<c>App.xaml.cs:767</c>，服务端 rank = -1 直接 no-op）；
    /// Phase 2 起如实回报 <c>device_received</c>（§7E-14 / §8.4 选项 (a)）。
    /// </summary>
    [Fact]
    public void HeadlessUnavailable_ReportsDeviceReceived_NeverDelivered()
    {
        var store = new MemoryMessageStore();
        var sink = new FakeAckSink();
        var manager = new MessageManager(store, sink.Send);
        manager.MessageReceived += _ => { };

        manager.HandleIncoming(Frame(102));
        manager.NotifyDisplayUnavailable(102, "headless：无人登录，没有可显示的桌面");

        Assert.Equal(new[] { (102L, "device_received") }, sink.Sent);
        Assert.DoesNotContain("delivered", sink.Statuses());
        Assert.Equal(DeliveryState.NotDisplayed, store.Get(102)!.State);

        // 客户端发出的每一个 ack 状态都必须是服务端认识的状态
        foreach (var status in sink.Statuses())
            Assert.Contains(status, ServerKnownStates);
    }

    // ── 测试 A：WebView2 失效（原生通知回落）────────────────────

    /// <summary>
    /// ★ 测试 A 的 Core 侧判据：WebView2 不可用、用原生通知回落提醒 —— 回报
    /// <c>popup_displayed</c>（用户确实被提醒到了），但本地状态是
    /// <see cref="DeliveryState.NotifiedFallback"/>，不是 <see cref="DeliveryState.Displayed"/>
    /// （§8.11：不把回落说成弹窗，同时不谎报也不漏 ACK）。
    /// </summary>
    [Fact]
    public void FallbackNotification_AcksPopupDisplayed_ButRecordsNotifiedFallback()
    {
        var store = new MemoryMessageStore();
        var sink = new FakeAckSink();
        var manager = new MessageManager(store, sink.Send);
        manager.MessageReceived += _ => { };              // 界面在，但 WebView2 不可用（平台层自己判断）

        manager.HandleIncoming(Frame(200));
        manager.NotifyFallbackDisplayed(200, "WebView2 不可用，用托盘通知回落");

        Assert.Equal(new[] { (200L, "popup_displayed") }, sink.Sent);
        Assert.Equal(DeliveryState.NotifiedFallback, store.Get(200)!.State);
    }

    /// <summary>
    /// 「WebView2 没报错但页面永远不就绪」（例如 exe 目录下 <c>shell\</c> 被改名 →
    /// 页面 404 → <c>web.ready</c> 永不到达）：超时兜底必须触发，消息不能永远挂在
    /// 「未 ACK」上。触发一次即可，不反复骚扰。
    /// </summary>
    [Fact]
    public void DisplayTimeout_TriggersFallbackOnce_WhenNoUiHandlesTheMessage()
    {
        var store = new MemoryMessageStore();
        var sink = new FakeAckSink();
        var manager = new MessageManager(store, sink.Send)
        {
            DisplayTimeout = TimeSpan.FromSeconds(20),
        };
        manager.MessageReceived += _ => { };              // 装作「没人回报任何事实」

        var fallbacks = new List<long>();
        manager.DisplayFallbackRequired += (record, reason) => fallbacks.Add(record.MessageId);

        manager.HandleIncoming(Frame(300));

        Assert.Equal(0, manager.SweepDisplayTimeouts(DateTime.UtcNow));                     // 还没到点
        Assert.Equal(1, manager.SweepDisplayTimeouts(DateTime.UtcNow.AddSeconds(25)));      // 到点了
        Assert.Equal(new long[] { 300 }, fallbacks);
        Assert.Equal(0, manager.SweepDisplayTimeouts(DateTime.UtcNow.AddSeconds(60)));      // 不重复提醒

        // 平台层弹了原生通知 → 回报 → 有终态 ack
        manager.NotifyFallbackDisplayed(300, "display_timeout");
        Assert.Equal(new[] { (300L, "popup_displayed") }, sink.Sent);
    }

    /// <summary>已经交给界面（页面就绪后投递出去了）的消息不该再触发兜底提醒 —— 否则会把页面列表里已经在看的消息再弹一次通知。</summary>
    [Fact]
    public void DeferredToUi_SuppressesFallbackTimeout()
    {
        var store = new MemoryMessageStore();
        var sink = new FakeAckSink();
        var manager = new MessageManager(store, sink.Send) { DisplayTimeout = TimeSpan.FromSeconds(20) };
        manager.MessageReceived += _ => { };

        manager.HandleIncoming(Frame(301));
        manager.NotifyDisplayDeferred(301, "页面已就绪，已投递");

        Assert.Equal(0, manager.SweepDisplayTimeouts(DateTime.UtcNow.AddSeconds(120)));
        Assert.Empty(sink.Sent);                          // 仍等页面确认；关窗时会有 read 终态
        manager.NotifyDismissed(301);
        Assert.Equal(new[] { "read" }, sink.Statuses());
    }

    // ── 测试 C：重启后重放，且不重复 ───────────────────────────

    /// <summary>
    /// ★ 测试 C：进程重启后（同一个 JSONL 文件、新的 Store + 新的 MessageManager），
    /// 此前**未显示**的消息在界面就绪时被重新投递，已显示/已读的不会被重放，
    /// 且同一条不会重复投递（按 <c>message_id</c> 去重）。
    /// </summary>
    [Fact]
    public void Restart_ReplaysUndisplayedMessagesOnce()
    {
        using var temp = new TempDir();
        var sink = new FakeAckSink();

        // ── 第一个进程：收到 3 条；#1 显示并已读，#2 收到但界面挂了，#3 收到但界面不见了
        {
            var store = new JsonlMessageStore(temp.File_);
            var manager = new MessageManager(store, sink.Send);
            manager.MessageReceived += _ => { };

            manager.HandleIncoming(Frame(1));
            manager.HandleIncoming(Frame(2));
            manager.HandleIncoming(Frame(3));
            manager.NotifyDisplayed(1);
            manager.NotifyDismissed(1);
            manager.NotifyDisplayUnavailable(2, "WebView2 不可用（原生通知也发不出去）");
            // #3 谁都没回报（进程直接退出）
        }

        // ── 第二个进程：读同一个文件
        var store2 = new JsonlMessageStore(temp.File_);
        Assert.Equal(3, store2.Count);
        sink.Sent.Clear();                                   // 只断言「重启之后」发出的 ack

        var replayed = new List<long>();
        var manager2 = new MessageManager(store2, sink.Send);
        manager2.MessageReceived += frame => replayed.Add(frame.MessageId);

        var pending = manager2.PendingForReplay();
        Assert.Equal(new long[] { 2, 3 }, Ids(pending));      // #1 已读 → 不重放
        Assert.Empty(replayed);                              // 重放由平台层投递，Core 不重复抛 MessageReceived（避免弹两次）

        // 界面就绪：投递（并回报「已交给界面」）→ 再次取候选时为空（不重复）
        foreach (var record in pending)
            manager2.NotifyDisplayDeferred(record.MessageId, "页面就绪重放");
        Assert.Empty(manager2.PendingForReplay());

        // 页面真的画出来了 → 本次会话重放的消息逐个确认
        manager2.NotifyDisplayed(2);
        manager2.NotifyDisplayed(3);
        Assert.Equal(new[] { (2L, "popup_displayed"), (3L, "popup_displayed") }, sink.Sent);

        // 原文本（含 auto_close_seconds）在重放时逐字可用
        var replayedRecord = store2.Get(3)!;
        Assert.Equal(DeliveryState.Displayed, replayedRecord.State);
        Assert.Equal("晚饭好了", replayedRecord.Content);
        Assert.Contains("\"message_id\":3", replayedRecord.RawJson());
    }

    /// <summary>重复投递（服务端重连补投同一条）按 message_id 去重：不重复弹窗、库里仍只有一条。</summary>
    [Fact]
    public void DuplicateDelivery_IsDedupedByMessageId()
    {
        var store = new MemoryMessageStore();
        var sink = new FakeAckSink();
        var manager = new MessageManager(store, sink.Send);
        var delivered = 0;
        manager.MessageReceived += _ => delivered++;

        manager.HandleIncoming(Frame(9));
        manager.HandleIncoming(Frame(9));       // 服务端补投

        Assert.Equal(1, delivered);
        Assert.Equal(1, store.Count);
        Assert.Empty(sink.Sent);
    }

    // ── ACK 待发队列（决策已在 Core，交给传输层失败时补发）──────

    [Fact]
    public void AckStaysPending_WhenTransportFails_AndIsFlushedLater()
    {
        var store = new MemoryMessageStore();
        var sink = new FakeAckSink { Failing = true };
        var manager = new MessageManager(store, sink.Send);
        manager.MessageReceived += _ => { };

        manager.HandleIncoming(Frame(50));
        manager.NotifyDisplayed(50);

        // 交给传输层失败 → 留在待发队列（消息状态该推进的仍然推进了）
        Assert.Equal(1, manager.PendingAcks.Count);
        Assert.True(manager.PendingAcks.IsPending(50, "popup_displayed"));
        Assert.Equal(DeliveryState.Displayed, store.Get(50)!.State);

        // 传输层恢复 → 补发
        sink.Failing = false;
        Assert.Equal(1, manager.FlushPendingAcks());
        Assert.Equal(0, manager.PendingAcks.Count);
        Assert.Equal(new[] { (50L, "popup_displayed") }, sink.Sent);
    }

    // ── 本地消息库（JSONL）──────────────────────────────────────

    [Fact]
    public void JsonlStore_RoundTripsRecordsAndStates()
    {
        using var temp = new TempDir();

        var store = new JsonlMessageStore(temp.File_);
        var sink = new FakeAckSink();
        var manager = new MessageManager(store, sink.Send);
        manager.MessageReceived += _ => { };
        manager.HandleIncoming(Frame(11, autoClose: 15));
        manager.NotifyDisplayed(11);

        var reloaded = new JsonlMessageStore(temp.File_);
        var record = reloaded.Get(11);

        Assert.NotNull(record);
        Assert.Equal(DeliveryState.Displayed, record!.State);
        Assert.Equal(15, record.AutoCloseSeconds);
        Assert.Contains("\"content\":\"晚饭好了\"", record.RawJson());
        Assert.Empty(reloaded.PendingReplay());          // 已显示 → 不重放
    }

    [Fact]
    public void JsonlStore_TrimsToMaxRecords()
    {
        using var temp = new TempDir();
        var store = new JsonlMessageStore(temp.File_);

        for (long id = 1; id <= JsonlMessageStore.MaxRecords + 20; id++)
            store.Put(MessageRecord.FromFrame(Frame(id), DateTime.UtcNow.AddMilliseconds(id)));

        Assert.Equal(JsonlMessageStore.MaxRecords, store.Count);
        Assert.Null(store.Get(1));                        // 最旧的被裁掉
        Assert.NotNull(store.Get(JsonlMessageStore.MaxRecords + 20));

        // 裁剪结果真的落盘了（重新加载仍是 MaxRecords 条）
        var reloaded = new JsonlMessageStore(temp.File_);
        Assert.Equal(JsonlMessageStore.MaxRecords, reloaded.Count);
    }

    private static long[] Ids(IReadOnlyList<MessageRecord> records)
    {
        var ids = new long[records.Count];
        for (var i = 0; i < records.Count; i++)
            ids[i] = records[i].MessageId;
        return ids;
    }
}

/*
 * 测试 A 里**做不了单测**的那一半（真实 WebView2 故障），手工验证步骤：
 *
 * 1. 正常启动 PC 端并连上 NAS（网页端能看到设备在线）。
 * 2. 退到托盘 → 退出程序 → 把 <exe 目录>\shell\ 改名成 shell.bak（等价于「界面永远加载不出来」）。
 * 3. 重新启动 exe（保持托盘运行）。
 * 4. 从网页端发一条消息，观察 %APPDATA%\FamilyAgent\agent.log 必须出现：
 *      [WS] RX         connection=<guid> type=message message_id=N
 *      [MSG] 已落盘 message_id=N state=persisted ... ← ...\messages.jsonl（M 条）
 *      [MSG] message_id=N 落盘后 20 秒仍未被界面处理 → 触发兜底提醒
 *      [MSG] message_id=N → ack:popup_displayed（WebView2 不可用，回落原生通知…）
 *      → ack:popup_displayed（… 字节）            ← 由 Outbox 打，证明帧真的发出去了
 *    并且系统必须出现一个托盘通知（FallbackNotifier）。
 * 5. 把 shell\ 改回来 → 重启程序 → 界面里应能看到这条消息（来自 messages.jsonl 的重放，
 *    日志里是「壳：重放 N 条未显示消息」）。
 * 6. 对照点：改动前同样操作只会看到「壳：页面未就绪，缓存消息 N」，
 *    永远没有 ack（服务端 message_targets 停在 device_received）。
 */
