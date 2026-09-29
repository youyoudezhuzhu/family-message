using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using FamilyAgent.Core.Events;
using FamilyAgent.Core.Messaging;
using FamilyAgent.Core.Protocol.Frames;
using FamilyAgent.Core.Sync;
using FamilyAgent.Core.Transport;
using Xunit;

namespace FamilyAgent.Core.Tests;

/// <summary>
/// 一条 <c>history_response.messages[]</c> 的记录 —— 形状与服务端
/// <c>group_history()</c> 给的一模一样（<b>只有</b>
/// <c>message_id / sender_name / content / created_at / direction</c>，
/// 没有 <c>auto_close_seconds</c> / <c>status</c> / <c>history</c>）。
/// </summary>
internal sealed class HistoryRow
{
    [JsonPropertyName("message_id")] public long MessageId { get; set; }
    [JsonPropertyName("sender_name")] public string SenderName { get; set; } = "爸爸";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "2026-09-29T12:00:00";
    [JsonPropertyName("direction")] public string Direction { get; set; } = "in";
}

/// <summary>
/// 假连接：精确控制「什么时候算连上 / 什么时候算认证完成 / history 里有什么」。
///
/// 真的 <c>ConnectionManager</c> 里是一条 <c>ClientWebSocket</c> 连接循环，
/// 在 Linux 单测里造不出「断开 → 服务端补投 → 重连后重新投递」这种现场 ——
/// 这正是 <see cref="ISyncConnection"/> 存在的理由。
/// </summary>
internal sealed class FakeSyncConnection : ISyncConnection
{
    public event Action<ConnectionStateChangedArgs>? ConnectionStateChanged;
    public event Action<HelloFrame>? HelloReceived;
    public event Action<HistoryFrame>? HistoryReceived;

    /// <summary>每次 <c>history_request</c> 要的 limit（按顺序）。</summary>
    public List<int> HistoryRequests { get; } = new();

    public string RequestHistoryTracked(int limit)
    {
        HistoryRequests.Add(limit);
        return "req-" + HistoryRequests.Count;      // req-1 / req-2 / …
    }

    public void Connect(string connectionId = "c1") =>
        ConnectionStateChanged?.Invoke(
            new ConnectionStateChangedArgs(ConnectionState.Connected, connectionId, "已连接"));

    public void Disconnect(string connectionId = "c1") =>
        ConnectionStateChanged?.Invoke(
            new ConnectionStateChangedArgs(ConnectionState.Disconnected, connectionId, "未连接"));

    public void Hello(string? token = null) =>
        HelloReceived?.Invoke(new HelloFrame { Type = "hello", Raw = JsonSerializer.SerializeToElement(new { type = "hello" }), Token = token });

    /// <summary>回一份 <c>history_response</c>，<c>request_id</c> 配对到**最近一次**请求。</summary>
    public void SendHistory(HistoryRow[] rows) => SendHistoryWithId("req-" + HistoryRequests.Count, rows);

    public void SendHistoryWithId(string requestId, HistoryRow[] rows) =>
        HistoryReceived?.Invoke(new HistoryFrame
        {
            Type = "history_response",
            Raw = JsonSerializer.SerializeToElement(new { type = "history_response", request_id = requestId }),
            RequestId = requestId,
            DeviceId = "dev-1",
            Messages = JsonSerializer.SerializeToElement(rows),
        });
}

/// <summary>一个只记录的假传输层（ack 发到哪儿去、发了什么）。</summary>
internal sealed class AckRecorder
{
    public List<(long Id, string Status)> Sent { get; } = new();

    public void Send(long id, string status) => Sent.Add((id, status));

    public long[] Ids()
    {
        var ids = new long[Sent.Count];
        for (var i = 0; i < Sent.Count; i++)
            ids[i] = Sent[i].Id;
        return ids;
    }
}

/// <summary>
/// ★ Phase 4 的验收单测（docs/CORE-REFACTOR-PLAN.md §6 Phase 4）：
///
/// <list type="bullet">
///   <item><b>D-9（核心）</b>：断线期间到达的消息，重连后**不丢也不重复** ——
///     假连接模拟「断开 → 服务端补投 → 重连后重新投递」，断言补投的条数、顺序、去重；</item>
///   <item><b>D-10</b>：握手序列固定为 <c>Connect → Authenticate → Sync/History → Live</c>；</item>
///   <item>主管已定的三条行为：补投不弹窗/不发 ACK、首次同步只建水位、绝不补投 <c>direction=out</c>。</item>
/// </list>
/// </summary>
public class SyncServiceTests
{
    // ── 夹具 ────────────────────────────────────────────────────

    private sealed class Rig
    {
        public MemoryMessageStore Store { get; } = new();
        public AckRecorder Ack { get; } = new();
        public MessageManager Messaging { get; }
        public FakeSyncConnection Connection { get; } = new();
        public SyncService Sync { get; }

        /// <summary>被要求弹全屏窗的消息（实时路径）——补投一条都不许出现在这里。</summary>
        public List<long> Popups { get; } = new();

        /// <summary>被要求进对话列表的消息（补投路径）。</summary>
        public List<long> Listed { get; } = new();

        public List<SyncPhase> Phases { get; } = new();

        public Rig()
        {
            Messaging = new MessageManager(Store, Ack.Send);
            Messaging.MessageReceived += frame => Popups.Add(frame.MessageId);
            Messaging.HistoryBackfilled += frame => Listed.Add(frame.MessageId);

            Sync = new SyncService(Connection, Messaging);
            Sync.PhaseChanged += args => Phases.Add(args.Current);
        }

        /// <summary>一次完整上线：连上 → 认证 → 拿到这份历史 → 进实时模式。</summary>
        public void ConnectSyncAndRespond(HistoryRow[] history, string connectionId = "c1")
        {
            Connection.Connect(connectionId);
            Connection.Hello();
            Connection.SendHistory(history);
        }

        public long[] StoredIds()
        {
            var records = Store.All();
            var ids = new long[records.Count];
            for (var i = 0; i < records.Count; i++)
                ids[i] = records[i].MessageId;
            return ids;
        }
    }

    private static HistoryRow Row(long id, string direction = "in", string content = "吃饭了", string sender = "妈妈") =>
        new() { MessageId = id, SenderName = sender, Content = content, Direction = direction };

    /// <summary>实时 <c>message</c> 帧（形状见 MessageFrame.cs）。</summary>
    private static MessageFrame LiveFrame(long id, string content = "实时消息", int autoClose = 30) => new()
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

    // ── ★ 核心：断线期间的消息，重连后不丢也不重复 ───────────────

    /// <summary>
    /// ★★ 主管要的那个单测：**断线期间到达的消息，重连后不会丢、也不会重复**。
    ///
    /// 现场（三段时间线）：
    /// <code>
    /// ① 首次上线：本地库为空 → history=[1,2] → 只建立水位线（1、2 是"装之前"的历史）
    /// ② 断线：期间群里多了 3（别的设备说的）、4（网页端发的）、5（本机自己在别处说的）
    /// ③ 重连：history=[1,2,3,4,5]（1、2 与本地重叠）→ 只补投 3、4；5 是 out 只推进水位
    /// ④ 再断开再重连：服务端又把 [1..5] 补一遍 → 一条都不重复
    /// </code>
    ///
    /// 断言四件事：**不丢**（3、4 进了本地库 + 列表）、**不重**（每个 id 只落一条、
    /// 第二次补齐零新增）、**不弹窗不 ACK**（补投路径一条 ack 都不发）、**水位正确**（推进到 5）。
    /// </summary>
    [Fact]
    public void OfflineMessages_SurviveReconnect_ExactlyOnce_AndNeverPopupOrAck()
    {
        var rig = new Rig();
        rig.Sync.Enter();

        // ① 首次同步：本地库为空 → 只建水位（1、2 不落盘、不补投）
        rig.ConnectSyncAndRespond(new[] { Row(1), Row(2, "out", "好") });

        Assert.Equal(SyncPhase.Live, rig.Sync.Phase);
        Assert.Equal(2, rig.Messaging.Watermark);
        Assert.True(rig.Messaging.WatermarkKnown);
        Assert.Empty(rig.Listed);
        Assert.Empty(rig.StoredIds());
        Assert.Equal(new[] { SyncService.DefaultHistoryLimit }, rig.Connection.HistoryRequests);
        Assert.Equal(
            new[] { SyncPhase.Connecting, SyncPhase.Authenticating, SyncPhase.Syncing, SyncPhase.Live },
            rig.Phases);

        // ② 断线（服务端 3、4 在这期间到达；5 是本机自己在别处说的）
        rig.Connection.Disconnect();
        Assert.Equal(SyncPhase.Disconnected, rig.Sync.Phase);
        Assert.Equal(0, rig.Store.Count);

        // ③ 重连：history 里 1、2 与本地重叠，3、4 是断线期间漏掉的，5 是 out
        rig.ConnectSyncAndRespond(new[] { Row(1), Row(2, "out", "好"), Row(3), Row(4), Row(5, "out", "我出门了") }, "c2");

        Assert.Equal(SyncPhase.Live, rig.Sync.Phase);

        // 不丢：断线期间的两条真的进了本地库、也真的进了列表（顺序正确）
        Assert.Equal(new long[] { 3, 4 }, rig.Listed);
        Assert.Equal(new long[] { 3, 4 }, rig.StoredIds());
        Assert.Equal(DeliveryState.Backfilled, rig.Store.Get(3)!.State);
        Assert.Equal(DeliveryState.Backfilled, rig.Store.Get(4)!.State);
        Assert.True(rig.Store.Get(3)!.Redelivered);

        // 不弹窗、不发 ACK：补投一条都没走实时路，一个 ack 都没发
        Assert.Empty(rig.Popups);
        Assert.Empty(rig.Ack.Sent);
        Assert.Empty(rig.Messaging.PendingAcks.Snapshot());

        // 补投的是终态：不会被当"待显示"重放、也不会触发托盘兜底
        Assert.Empty(rig.Messaging.PendingForReplay());
        Assert.Equal(0, rig.Messaging.SweepDisplayTimeouts(DateTime.UtcNow.AddMinutes(5)));

        // 水位推进到 5（含本机自己发的 5：不补投，但必须算"已知"）
        Assert.Equal(5, rig.Messaging.Watermark);
        Assert.Equal(5, rig.Sync.LastPlan!.NewWatermark);
        Assert.Equal(1, rig.Sync.LastPlan.AlreadyKnown);      // 1 与本地重叠
        Assert.Equal(2, rig.Sync.LastPlan.Own);               // 2、5 是 out
        Assert.Equal(2, rig.Sync.LastPlan.ToDeliver.Count);   // 3、4

        // ④ 再说一遍同样的话（服务端把 1..5 又补一遍）：一条都不重复
        var listedSoFar = rig.Listed.Count;                     // 上一轮补进来的 2 条
        rig.Connection.Disconnect();
        rig.ConnectSyncAndRespond(new[] { Row(1), Row(2, "out", "好"), Row(3), Row(4), Row(5, "out", "我出门了") }, "c3");

        Assert.Equal(listedSoFar, rig.Listed.Count);            // 没有新增进列表（去重生效）
        Assert.Equal(2, rig.Store.Count);                      // 库里仍是 2 条（无重复插入）
        Assert.Equal(new long[] { 3, 4 }, rig.StoredIds());
        Assert.Empty(rig.Ack.Sent);
        Assert.Empty(rig.Popups);
        Assert.Equal(5, rig.Messaging.Watermark);
    }

    /// <summary>
    /// 首次同步（本地库为空）时：history 里那几十条是"装之前的历史"，
    /// **只建立水位线，一条都不补投**（否则一装好就被灌进几十条历史）。
    /// </summary>
    [Fact]
    public void FirstSync_OnEmptyStore_OnlyEstablishesBaseline()
    {
        var rig = new Rig();
        rig.Sync.Enter();

        var history = new HistoryRow[50];
        for (var i = 0; i < history.Length; i++)
            history[i] = Row(i + 1);

        rig.ConnectSyncAndRespond(history);

        Assert.Equal(SyncPhase.Live, rig.Sync.Phase);
        Assert.Empty(rig.Listed);
        Assert.Empty(rig.Popups);
        Assert.Equal(0, rig.Store.Count);
        Assert.Equal(50, rig.Messaging.Watermark);             // 水位记住"旧世界最高到哪"
        Assert.Equal(50, rig.Sync.LastPlan!.Baseline);         // 全部记为基线
        Assert.Empty(rig.Sync.LastPlan.ToDeliver);
    }

    /// <summary>本机自己发的消息（<c>direction=out</c>）**永不补投** —— 只推进水位（否则会把自己刚说的话再"补"一遍）。</summary>
    [Fact]
    public void OwnMessages_AreNeverBackfilled_ButStillAdvanceWatermark()
    {
        // 先让水位"已知"（本地库里已经有一条）
        var rig = new Rig();
        rig.Messaging.HandleIncoming(LiveFrame(1, "我先说一句"));
        rig.Popups.Clear();

        rig.Sync.Enter();
        rig.ConnectSyncAndRespond(new[]
        {
            Row(1, "out", "我先说一句"),          // 自己发的
            Row(2, "out", "我又说一句"),          // 自己发的
            Row(3, "in", "妈妈答一句"),           // 别人说的 → 该补
        });

        Assert.Equal(new long[] { 3 }, rig.Listed);
        Assert.Equal(new long[] { 1, 3 }, rig.StoredIds());
        Assert.Equal(2, rig.Sync.LastPlan!.Own);
        Assert.Equal(3, rig.Messaging.Watermark);
        Assert.Equal(3, rig.Sync.LastPlan.NewWatermark);
    }

    // ── 补投不弹窗 / 不发 ACK：真实数据源与四态 ──────────────────

    /// <summary>
    /// 主管要的行为判据：补投的历史消息只落盘 + 只进列表 ——
    /// 不弹全屏、不发任何 ACK；而且**将来界面回报什么都不会**把 ack 发出去
    /// （它走的是另一条入口，压根没有那个分支）。
    /// </summary>
    [Fact]
    public void BackfilledMessages_PersistAndListOnly_NoPopupNoAck()
    {
        var rig = new Rig();
        rig.Messaging.HandleIncoming(LiveFrame(1, "本地已有的一条"));
        rig.Messaging.NotifyDisplayed(1);          // 实时消息正常流程：会发 ack
        rig.Ack.Sent.Clear();
        rig.Popups.Clear();

        rig.Sync.Enter();
        rig.Connection.Connect();
        rig.Connection.Hello();
        rig.Connection.SendHistory(new[] { Row(1, "out"), Row(2), Row(3) });

        Assert.Equal(new long[] { 2, 3 }, rig.Listed);          // 只进列表
        Assert.Empty(rig.Popups);                              // 不弹全屏
        Assert.Empty(rig.Ack.Sent);                            // 不发 ACK
        Assert.Equal(DeliveryState.Backfilled, rig.Store.Get(2)!.State);

        // 四态如实回答：已发送✓ 已投递✓（进列表）已 ACK— 已显示—
        var report = rig.Messaging.Report(2);
        Assert.True(report.Sent);
        Assert.True(report.Delivered);
        Assert.False(report.Acked);
        Assert.False(report.Displayed);
        Assert.Equal(DeliveryStage.Delivered, report.Stage);
        Assert.Equal("", rig.Messaging.AckedStatusOf(2));
    }

    /// <summary>
    /// 补投帧里的 <c>auto_close_seconds</c> 用**本地最近一条实时消息的取值**当参照
    /// （服务端 history 里没有这个字段）—— 保证补投帧形状与实时帧一致。
    /// </summary>
    [Fact]
    public void BackfilledFrame_UsesLastLiveAutoCloseSeconds()
    {
        var rig = new Rig();
        rig.Messaging.HandleIncoming(LiveFrame(1, "实时", autoClose: 25));
        Assert.Equal(25, rig.Messaging.LastAutoCloseSeconds);

        rig.Sync.Enter();
        rig.Connection.Connect();
        rig.Connection.Hello();
        rig.Connection.SendHistory(new[] { Row(1, "out"), Row(2) });

        var record = rig.Store.Get(2)!;
        Assert.Equal(25, record.AutoCloseSeconds);
        Assert.Equal("妈妈", record.SenderName);
        Assert.Equal("text", record.MessageType);
        Assert.Contains("\"redelivered\":true", record.RawJson());
        Assert.Contains("\"auto_close_seconds\":25", record.RawJson());
        Assert.Contains("\"status\":\"sent\"", record.RawJson());
    }

    // ── 握手序列与边界 ──────────────────────────────────────────

    /// <summary>D-10：四个阶段按序上报，且每次重连都重新走一遍（断开 → 未连接）。</summary>
    [Fact]
    public void Phases_FollowConnectAuthenticateSyncLive_OnEveryReconnect()
    {
        var rig = new Rig();
        rig.Sync.Enter();
        rig.ConnectSyncAndRespond(new[] { Row(1) });                 // 空库 → 只建水位
        rig.Connection.Disconnect();
        rig.ConnectSyncAndRespond(new[] { Row(1), Row(2) }, "c2");

        Assert.Equal(new[]
        {
            SyncPhase.Connecting, SyncPhase.Authenticating, SyncPhase.Syncing, SyncPhase.Live,
            SyncPhase.Disconnected,
            SyncPhase.Authenticating, SyncPhase.Syncing, SyncPhase.Live,
        }, rig.Phases);

        Assert.Single(rig.Sync.LastPlan!.ToDeliver);                // 第二条历史被补投（第一条与本地重叠）
    }

    /// <summary>不配对的 <c>history_response</c>（用户点开对话窗口拉列表那条路）不参与合并补齐。</summary>
    [Fact]
    public void HistoryResponse_WithUnmatchedRequestId_IsIgnored()
    {
        var rig = new Rig();
        rig.Messaging.HandleIncoming(LiveFrame(1));
        rig.Sync.Enter();

        rig.Connection.Connect();
        rig.Connection.Hello();                                     // 补齐请求 = req-1
        rig.Connection.SendHistoryWithId("req-99", new[] { Row(2) }); // 别人（列表请求）的应答

        Assert.Equal(1, rig.Store.Count);                             // 没有补投任何东西（库还是那 1 条）
        Assert.Empty(rig.Listed);
    }

    /// <summary>
    /// <c>history_response</c> 永远不来时的兜底：<c>SyncTimeout</c> 到点放行进实时模式，
    /// 不会永远卡在「补齐中」（放行由平台层的轮询调 <c>Tick</c> 触发，不另起定时器）。
    /// </summary>
    [Fact]
    public void MissingHistoryResponse_ReleasesToLiveByTimeout()
    {
        var rig = new Rig();
        rig.Sync.SyncTimeout = TimeSpan.FromSeconds(5);
        rig.Sync.Enter();
        rig.Connection.Connect();
        rig.Connection.Hello();

        Assert.Equal(SyncPhase.Syncing, rig.Sync.Phase);
        Assert.Equal(0, rig.Sync.Tick(DateTime.UtcNow));                        // 还没到点
        Assert.Equal(1, rig.Sync.Tick(DateTime.UtcNow.AddSeconds(6)));          // 到点放行
        Assert.Equal(SyncPhase.Live, rig.Sync.Phase);
        Assert.Equal(0, rig.Sync.Tick(DateTime.UtcNow.AddMinutes(5)));          // 只放行一次
    }

    /// <summary>实时消息的路不受 Phase 4 影响：照旧弹窗、照旧走 ACK、并推进水位。</summary>
    [Fact]
    public void LiveMessages_StillPopupAndAck_AndAdvanceWatermark()
    {
        var rig = new Rig();
        rig.Sync.Enter();
        rig.Connection.Connect();
        rig.Connection.Hello();
        rig.Connection.SendHistory(new[] { Row(1, "out"), Row(2) });   // 水位 = 2

        rig.Messaging.HandleIncoming(LiveFrame(3, "刚到的实时消息"));

        Assert.Equal(new long[] { 3 }, rig.Popups);
        Assert.Equal(3, rig.Messaging.Watermark);

        rig.Messaging.NotifyDisplayDeferred(3, "已投给页面");
        rig.Messaging.NotifyDisplayed(3);
        Assert.Equal(new[] { (3L, "popup_displayed") }, rig.Ack.Sent);

        var report = rig.Messaging.Report(3);
        Assert.True(report.Sent);
        Assert.True(report.Delivered);
        Assert.True(report.Acked);
        Assert.True(report.Displayed);
        Assert.Equal(DeliveryStage.Displayed, report.Stage);
        Assert.Equal("popup_displayed", rig.Messaging.AckedStatusOf(3));

        // 重连时服务端把 3 再推一次（pending_for_device）→ 已在本地，一条都不补
        rig.Connection.Disconnect();
        rig.ConnectSyncAndRespond(new[] { Row(1, "out"), Row(2), Row(3, "in") }, "c2");
        Assert.Empty(rig.Listed);
        Assert.Equal(1, rig.Store.Count);
    }

    // ── 纯函数：合并计划 ────────────────────────────────────────

    /// <summary>合并计划：排序 + 按 <c>message_id</c> 去重 + 丢掉无效项（服务端给的是升序，但正确性不建立在"对端守规矩"上）。</summary>
    [Fact]
    public void Plan_SortsDedupesAndDropsInvalidIds()
    {
        var incoming = new List<HistoryMessage>
        {
            new() { MessageId = 5, Direction = "in" },
            new() { MessageId = 3, Direction = "in" },
            new() { MessageId = 3, Direction = "in" },      // 重复
            new() { MessageId = 0, Direction = "in" },      // 无有效 id
            new() { MessageId = 4, Direction = "out" },     // 本机自己发的
        };

        var plan = HistoryBackfill.Plan(incoming, watermark: 1, watermarkKnown: true, contains: _ => false);

        Assert.Equal(new long[] { 3, 5 }, Ids(plan.ToDeliver));
        Assert.Equal(3, plan.Total);                        // 0 被丢掉；重复的 3 只算一次
        Assert.Equal(1, plan.Own);
        Assert.Equal(5, plan.NewWatermark);
    }

    /// <summary>本地库里已有的（<c>contains</c>）与水位之下的都算「已在本地」，不重复补投。</summary>
    [Fact]
    public void Plan_SkipsKnownAndBelowWatermark()
    {
        var incoming = new List<HistoryMessage>
        {
            new() { MessageId = 1, Direction = "in" },      // 水位之下
            new() { MessageId = 2, Direction = "in" },      // 本地已有
            new() { MessageId = 3, Direction = "in" },      // 新的 → 补
        };

        var plan = HistoryBackfill.Plan(incoming, watermark: 1, watermarkKnown: true, contains: id => id == 2);

        Assert.Equal(new long[] { 3 }, Ids(plan.ToDeliver));
        Assert.Equal(2, plan.AlreadyKnown);                 // 水位之下的 1 + 本地已有的 2
    }

    /// <summary><c>direction=out</c> 在 <see cref="HistoryMessage"/> 上可识别（判据只有一处）。</summary>
    [Fact]
    public void HistoryMessage_KnowsItsDirection()
    {
        using var outDoc = JsonDocument.Parse(JsonSerializer.Serialize(Row(9, "out")));
        using var inDoc = JsonDocument.Parse(JsonSerializer.Serialize(Row(10, "in")));
        using var badDoc = JsonDocument.Parse(JsonSerializer.Serialize(new { message_id = 0 }));

        Assert.True(HistoryMessage.Parse(outDoc.RootElement)!.IsOwn);      // out = 本机自己发的
        Assert.False(HistoryMessage.Parse(inDoc.RootElement)!.IsOwn);
        Assert.Null(HistoryMessage.Parse(badDoc.RootElement));             // 没有有效 id → 丢弃
    }

    private static long[] Ids(IReadOnlyList<HistoryMessage> messages)
    {
        var ids = new long[messages.Count];
        for (var i = 0; i < messages.Count; i++)
            ids[i] = messages[i].MessageId;
        return ids;
    }
}
