using System;
using System.Collections.Generic;
using FamilyAgent.Core.Diagnostics;
using FamilyAgent.Core.Events;
using FamilyAgent.Core.Protocol.Frames;
using FamilyAgent.Core.Transport;

namespace FamilyAgent.Core.Messaging;

/// <summary>
/// 协议里 <c>ack</c> 帧的 <c>status</c> 取值 —— **只列服务端认识的状态**
/// （<c>server/services/messages.py</c> 的 <c>STATES</c>：
/// <c>created / server_received / device_received / popup_displayed / read</c>）。
///
/// ⚠ 原实现里 headless 分支发的是 <c>"delivered"</c>（<c>App.xaml.cs:767</c>），
///   它**不在** <c>STATES</c> 里：服务端 <c>advance()</c> 用
///   <c>RANK.get(status, -1) &lt;= RANK.get(当前, -1)</c> 比较，rank = -1 直接 no-op ——
///   也就是一条死值（§2.7-④ / §8.4）。Phase 2 起不再发它：无界面可显示时如实回报
///   <see cref="DeviceReceived"/>（服务端投递成功时本来就已经记为 <c>device_received</c>，
///   这条 ack 是幂等的；语义上也只说「设备收到了」，不谎报「弹窗显示了」）。
/// </summary>
public static class AckStatuses
{
    /// <summary>设备已收到（服务端投递成功时自行记为它；客户端再发一次是幂等的）。</summary>
    public const string DeviceReceived = "device_received";

    /// <summary>弹窗/提醒真的呈现给用户了（**不得提前**，见 §7E-13）。</summary>
    public const string PopupDisplayed = "popup_displayed";

    /// <summary>用户已经看过（关窗 / 自动关闭）。</summary>
    public const string Read = "read";
}

/// <summary>
/// 消息生命周期与 ACK 决策（docs/CORE-REFACTOR-PLAN.md §3.2 / §6 Phase 2）。
///
/// 这是 Phase 2 的核心：把「收到消息之后该发生什么」从 WPF 宿主搬进 Core ——
///
/// <code>
/// 收到 message 帧
///   → 归一 message_id（&lt;= 0 的帧无法去重/无法 ACK，单独处理）
///   → 本地持久化（IMessageStore）            ← 进程此刻死掉也不丢
///   → 发 DeliveryStateChanged(Received/Persisted)
///   → 触发 MessageReceived 交给平台层显示     ← 订阅者抛异常也不影响前面两步
///   → 平台层回报事实：NotifyDisplayed / NotifyFallbackDisplayed /
///     NotifyDisplayUnavailable / NotifyDismissed
///   → 由**本类**决定发哪个 ack（popup_displayed / read / device_received）
/// </code>
///
/// ★ 一句话判据：**ACK 不再与 UI 显示强绑定**。
///   <list type="bullet">
///     <item>UI 崩了 / WebView2 挂了 / 根本没有显示订阅者 → 消息仍在 <see cref="Store"/> 里
///       （重启后由 <see cref="PendingForReplay"/> 重放），ACK 仍有终态
///       （<c>popup_displayed</c> 或 <c>device_received</c>），不会永远挂在「未 ACK」；</item>
///     <item>本类**不认识**窗口、WebView2、页面、Dispatcher —— 只认识「事实回报」；
///       怎么弹是平台层的事。</item>
///   </list>
///
/// 与现状的对应关系（§4 迁移映射表）：
/// <list type="bullet">
///   <item><c>App.xaml.cs:302-311</c>（<c>MessageAcked</c>/<c>Dismissed</c> → 直接发 ack）
///     的**决策**搬到这里；宿主只把 UI 事实转发成 <c>Notify*</c>；</item>
///   <item><c>App.xaml.cs:762-768</c> 的 headless 分支（发非法状态 <c>delivered</c>）→
///     <see cref="NotifyDisplayUnavailable"/>（终态 <see cref="DeliveryState.NotDisplayed"/>）；</item>
///   <item><c>WebHostWindow.xaml.cs:98</c> <c>_queued</c> 的「页面未就绪就只堆内存」→
///     落盘 + <see cref="PendingForReplay"/> 重放（宿主那份缓存降级为纯投递缓冲）。</item>
/// </list>
/// </summary>
public sealed class MessageManager
{
    private readonly IMessageStore _store;
    private readonly Action<long, string> _sendAck;
    private readonly PendingAckQueue _pendingAcks = new();

    /// <summary>只在极短临界区里用（<c>_handedToUi</c> / <c>_fallbackTried</c> 两个集合；库本身自带锁）。</summary>
    private readonly object _gate = new();

    /// <summary>本次会话里已经交给界面的消息（页面就在渲染 / 已确认）。兜底提醒与重放都不再盯它们。</summary>
    private readonly HashSet<long> _handedToUi = new();

    /// <summary>本次会话里已经为它触发过兜底提醒的消息（订阅者抛异常也不会反复提醒）。</summary>
    private readonly HashSet<long> _fallbackTried = new();

    /// <summary>
    /// ACK 事实：<c>message_id → 最后一个真的交给传输层的 ack 状态</c>
    /// （Phase 4 的「投递/ACK 事实记录」，<see cref="Report"/> 的唯一数据源）。
    ///
    /// ⚠ 与 <see cref="PendingAckQueue"/> 的区别：那里是**待办**（发出即移除，
    ///   重启即清空），这里是**已办**（只增不改，用来回答「这条 ACK 了没、ACK 的什么」）。
    ///   <c>popup_displayed</c> 之后再发 <c>read</c> 时以最新的为准 —— 四态问的是
    ///   「走到哪一步了」，不是「发过几条」。
    /// </summary>
    private readonly Dictionary<long, string> _ackedStatus = new();

    /// <summary>
    /// 水位线（Phase 4）：本机已经"知道"的最大的服务端 <c>message_id</c>。
    ///
    /// 它回答的是「哪些历史是我以前就见过（或该算旧账）的」——
    /// <see cref="Sync.HistoryBackfill.Plan"/> 只补投 <c>&gt; </c> 它的消息。
    /// 三条路径会推进它：收到实时消息、补投一条、首次同步建立水位。
    ///
    /// 初值从本地库里取最大 <c>message_id</c>（<see cref="JsonlMessageStore"/> 跨重启存活），
    /// 所以「进程重启后重连」不会把已经处理过的消息当新消息补一遍。
    /// </summary>
    private long _baseline;

    /// <summary>false = 本地库为空且从未同步过 → 首次同步**只建水位线、不补投任何历史**（见 SyncService）。</summary>
    private bool _watermarkKnown;

    /// <summary>最近一条**实时**消息带的 <c>auto_close_seconds</c>（补投帧缺这个字段时的参照值）。</summary>
    private int _lastAutoCloseSeconds;

    /// <summary>
    /// 建一个消息管理器。
    /// <paramref name="sendAck"/> 是「把 ack 交给传输层」的方式（Windows 宿主传
    /// <c>(id, status) =&gt; Core.Ack(id, status)</c>）；它抛异常 = 这次没交出去，
    /// ack 会留在 <see cref="PendingAcks"/> 里等 <see cref="FlushPendingAcks"/> 补发。
    /// </summary>
    public MessageManager(IMessageStore store, Action<long, string> sendAck)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sendAck = sendAck ?? throw new ArgumentNullException(nameof(sendAck));

        // 水位初值 = 本地库里最大的 message_id（跨重启存活的那份记账）。
        // 空库 → 水位未知 → 首次同步只建水位线（不会把「装之前的历史」当新消息补投）。
        foreach (var record in _store.All())
        {
            if (record.MessageId > _baseline)
                _baseline = record.MessageId;
        }
        _watermarkKnown = _baseline > 0;
        if (_watermarkKnown)
            AgentLog.Write($"[MSG] 本地已有 {_store.Count} 条记账，水位={_baseline}（重连只补投它之后的消息）");
    }

    /// <summary>
    /// 消息落盘后多久还没被任何界面处理，就触发兜底提醒（托盘原生通知）。
    ///
    /// 为什么要这个：WebView2「没报错但也永远不就绪」时（例如 exe 目录下
    /// <c>shell\</c> 被改名 → 页面 404 → <c>web.ready</c> 永不到达），光靠
    /// 「等平台层回报」是等不到的，这条消息会永远停在「未 ACK」（现状即如此）。
    /// 默认 20 秒：正常的页面就绪在 1~2 秒内完成，不会误触发。
    /// 设成 <c>TimeSpan.Zero</c> 或负值 = 关闭超时兜底。
    /// </summary>
    public TimeSpan DisplayTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>本地消息库（重启后重放的来源）。</summary>
    public IMessageStore Store => _store;

    /// <summary>「已决策、还没交给传输层」的 ACK 队列。</summary>
    public PendingAckQueue PendingAcks => _pendingAcks;

    /// <summary>水位线：本机已知的最大的 <c>message_id</c>（0 = 还不知道）。</summary>
    public long Watermark
    {
        get { lock (_gate) { return _baseline; } }
    }

    /// <summary>
    /// 水位是否已知。<c>false</c> = 本地库为空且从未同步过（新装客户端）——
    /// 此时 <see cref="Sync.HistoryBackfill.Plan"/> 只建水位线、一条历史都不补投。
    /// </summary>
    public bool WatermarkKnown
    {
        get { lock (_gate) { return _watermarkKnown; } }
    }

    /// <summary>
    /// 最近一条**实时**消息的 <c>auto_close_seconds</c>（没有收到过实时消息时为 0）。
    ///
    /// 用途：<c>history_response</c> 里**没有**这个字段（服务端 <c>group_history()</c>
    /// 只给 <c>message_id/sender_name/content/created_at/direction</c>），
    /// 补投时用本地最近一次的取值当参照 —— 这样补投出来的弹窗（若将来允许弹）
    /// 会与实时消息一样自动关闭，而不是"永不关闭"。
    /// </summary>
    public int LastAutoCloseSeconds
    {
        get { lock (_gate) { return _lastAutoCloseSeconds; } }
    }

    /// <summary>收到新消息（已落盘）→ 交给平台层显示。**取代原来宿主直连的连接层事件**（§3.3）。</summary>
    public event Action<MessageFrame>? MessageReceived;

    /// <summary>
    /// ★ Phase 4：**补投的历史消息**（只落盘 + 只进对话列表，见 <see cref="HandleBackfill"/>）。
    ///
    /// 与 <see cref="MessageReceived"/> 分开是刻意的 —— 平台层订阅这个事件时
    /// **只能把它推进列表**（页面里那段对话流），不得弹全屏、不得回报任何显示事实。
    /// 主管已定：补投的字段比实时帧少（没有 <c>auto_close_seconds</c>），
    /// 弹窗的自动关闭时长会与实时不一致，且新装客户端会被一次灌进几十个全屏窗。
    /// </summary>
    public event Action<MessageFrame>? HistoryBackfilled;

    /// <summary>消息的本地生命周期变化（Received / Persisted / Displayed / NotifiedFallback / NotDisplayed / AckSent / Read）。</summary>
    public event Action<MessageDeliveryStateChangedArgs>? DeliveryStateChanged;

    /// <summary>
    /// 需要平台层用**原生通知**兜底（消息落盘后 <see cref="DisplayTimeout"/> 内
    /// 没有任何界面处理过）。平台层应弹一次通知并回报
    /// <see cref="NotifyFallbackDisplayed"/>；连通知都发不出去时才回报
    /// <see cref="NotifyDisplayUnavailable"/>（§8.11 方案 (a)）。
    /// </summary>
    public event Action<MessageRecord, string>? DisplayFallbackRequired;

    /// <summary>
    /// 接上连接层：<c>connection.MessageReceived</c> 直接进 <see cref="HandleIncoming"/>。
    /// 宿主**不要**再单独订阅 <c>connection.MessageReceived</c>（会收到两遍）：显示走本类的
    /// <see cref="MessageReceived"/>。
    /// </summary>
    public void Attach(ConnectionManager connection) =>
        connection.MessageReceived += HandleIncoming;

    /// <summary>断开与连接层的订阅（退出/换连接对象时）。</summary>
    public void Detach(ConnectionManager connection) =>
        connection.MessageReceived -= HandleIncoming;

    // ---------------- 入口：收到一条消息 ----------------

    /// <summary>
    /// 消息入口（连接层事件 / 将来的 Sync 补齐都走这里）。
    ///
    /// 顺序是刻意的：**先落盘、再发事件、最后才等 UI 回报**。任何一步
    /// （订阅者抛异常、没有订阅者、界面根本不存在）都不会让前面已经发生的事实回退，
    /// 也不会让这条消息失去终态。
    /// </summary>
    public void HandleIncoming(MessageFrame frame)
    {
        if (frame is null)
            return;

        var now = DateTime.UtcNow;
        var id = frame.MessageId;

        // 记下最近一条实时消息的 auto_close_seconds：补投帧缺这个字段时用它当参照
        // （见 LastAutoCloseSeconds 与 HistoryBackfill.ToFrame）。
        if (frame.AutoCloseSeconds is int autoClose)
            lock (_gate) { _lastAutoCloseSeconds = autoClose; }

        // 归一 message_id：没有有效 id 的消息无法去重、也无法 ACK（ack 帧按 id 记账）
        if (id <= 0)
        {
            AgentLog.Write("[MSG] 收到一条没有有效 message_id 的消息：无法落盘/去重/ACK，仅投给界面");
            RaiseMessageReceived(frame);
            return;
        }

        var existing = _store.Get(id);
        if (existing is not null)
        {
            // 服务端重连补投（pending_for_device）会把同一条再推一遍：
            // 按 message_id 去重 —— 不重复弹窗；原文刷新一次（服务端可能补了字段）
            _store.Put(existing.With(existing.State, now, frame.Raw));
            AgentLog.Write($"[MSG] message_id={id} 已存在（state={existing.State.ToStoreValue()}）"
                         + "→ 按 message_id 去重，不再投递界面");
            RaiseState(id, existing.State, "重复投递（已按 message_id 去重）");
            return;
        }

        RaiseState(id, DeliveryState.Received, "Core 已收到");

        var record = MessageRecord.FromFrame(frame, now);
        _store.Put(record);
        AdvanceWatermark(id);                 // ★ 水位跟着实时消息走（补投只补它之后的）
        AgentLog.Write($"[MSG] 已落盘 message_id={id} state=persisted sender={record.SenderName}"
                     + $" ← {_store.Describe()}");
        RaiseState(id, DeliveryState.Persisted, "已落盘（进程崩溃也不会丢）");

        RaiseMessageReceived(frame);
    }

    /// <summary>
    /// ★★ Phase 4 的补投入口：**只落盘 + 只进对话列表；绝不弹全屏、绝不发 ACK**。
    ///
    /// 主管已定的行为（docs/CORE-REFACTOR-PLAN.md §6 Phase 4 的两个理由）：
    /// <list type="number">
    ///   <item><c>group_history()</c> 返回的字段里**没有** <c>auto_close_seconds</c> ——
    ///     补投若弹窗，自动关闭时长会与实时消息不一致，而为此改协议不值得；</item>
    ///   <item>新装客户端本地库为空时，一次补投几十条 = 一装好就弹几十个全屏窗。</item>
    /// </list>
    ///
    /// 实现上「不弹窗」不是靠约定，而是靠**走一条没有 <see cref="MessageReceived"/> 的路**：
    /// 平台层只从 <see cref="HistoryBackfilled"/> 拿到它，而那个事件的订阅者只负责进列表。
    ///
    /// ★ 顺带修掉的正确性问题：以前补投若走 <see cref="HandleIncoming"/> 那条路，
    ///   「没有显示订阅者 / 界面建不起来」时会落到 <see cref="NotifyDisplayUnavailable"/>，
    ///   而它**无条件**发 <c>ack:device_received</c> —— 对一条历史消息而言，这条 ack 会在
    ///   服务端**新建** <c>message_targets</c> 行（历史消息本来没有投递记账），
    ///   凭空污染服务端的投递统计。本入口一个 ack 都不发，这个问题不复存在。
    ///
    /// 本地状态记 <see cref="DeliveryState.Backfilled"/>（终态）：它不是「待显示」，
    /// 于是 <see cref="PendingForReplay"/> 不会重放它、<see cref="SweepDisplayTimeouts"/>
    /// 不会为它触发托盘兜底提醒。
    /// </summary>
    public void HandleBackfill(MessageFrame frame)
    {
        if (frame is null)
            return;

        var id = frame.MessageId;
        if (id <= 0)
        {
            // 无有效 id 的历史项在 HistoryBackfill.Plan 里已经被丢掉；这里只防御
            AgentLog.Write("[MSG] 补投帧没有有效 message_id：无法去重，直接丢弃（不发 ACK、不进界面）");
            return;
        }

        AdvanceWatermark(id);

        var existing = _store.Get(id);
        if (existing is not null)
        {
            // 本地已有：实时收到过 / 上一轮补投过 → 不重复落盘、不重复进列表、不发 ACK
            AgentLog.Write($"[MSG] 补投 message_id={id} 本地已有（state={existing.State.ToStoreValue()}）"
                         + "→ 去重：不重复进列表、不发 ACK");
            RaiseState(id, existing.State, "补投去重（本地已有，不再进列表）");
            return;
        }

        var now = DateTime.UtcNow;
        var record = MessageRecord.FromFrame(frame, now).With(DeliveryState.Backfilled, now);
        _store.Put(record);
        MarkHandedToUi(id);                    // 事实：确实交给了界面（进列表），不是弹窗
        AgentLog.Write($"[MSG] 补投已落盘 message_id={id} state=backfilled sender={record.SenderName}"
                     + "（只进列表：不弹全屏、不发 ACK）" + $" ← {_store.Describe()}");
        RaiseState(id, DeliveryState.Backfilled, "补投：只落盘/进列表，不弹全屏、不发 ACK");

        var handler = HistoryBackfilled;
        if (handler is null)
        {
            AgentLog.Write($"[MSG] 补投 message_id={id} 没有列表订阅者（已在本机库里，界面恢复后可见）");
            return;
        }

        try
        {
            handler(frame);
        }
        catch (Exception ex)
        {
            AgentLog.Write($"[MSG] 补投的列表订阅者抛异常（已忽略，不影响落盘）："
                         + $"{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>交给平台层显示；订阅者不存在或抛异常都按「无法显示」走终态（消息已经落盘，不会丢）。</summary>
    private void RaiseMessageReceived(MessageFrame frame)
    {
        var handler = MessageReceived;
        if (handler is null)
        {
            AgentLog.Write($"[MSG] message_id={frame.MessageId} 没有显示订阅者"
                         + " → 如实回报 device_received（消息已落盘，界面恢复后可重放）");
            NotifyDisplayUnavailable(frame.MessageId, "没有显示订阅者");
            return;
        }

        try
        {
            handler(frame);
        }
        catch (Exception ex)
        {
            // ★ 测试 B 的判据：订阅者抛异常时，落盘已经发生、ACK 仍然要发出去
            AgentLog.Write($"[MSG] 显示订阅者抛异常（已忽略，不影响落盘与 ACK）："
                         + $"{ex.GetType().Name} {ex.Message}");
            NotifyDisplayUnavailable(frame.MessageId, "显示订阅者抛异常");
        }
    }

    // ---------------- 平台层回报的「事实」 ----------------

    /// <summary>
    /// 真的画到屏幕上了（页面 <c>web.ack</c> / 页面自己确认的历史首屏）。
    /// 只有到这一步才回报 <c>popup_displayed</c>（不得提前，§7E-13）。
    /// </summary>
    public void NotifyDisplayed(long messageId, string detail = "")
    {
        if (messageId <= 0)
            return;

        var current = CurrentState(messageId);
        if (current == DeliveryState.Displayed || current == DeliveryState.Read)
        {
            AgentLog.Write($"[MSG] message_id={messageId} 重复的显示确认（当前 state={current.ToStoreValue()}），忽略");
            return;
        }

        MarkHandedToUi(messageId);
        Transition(messageId, DeliveryState.Displayed,
            detail.Length > 0 ? detail : "界面确认已画到屏幕上");
        SendAck(messageId, AckStatuses.PopupDisplayed, "确实显示之后才回报");
    }

    /// <summary>
    /// 已经交给界面了、但界面还没确认画出来（页面就绪后已投递 / 客户端视图正在渲染）。
    ///
    /// 这一步只是**免掉超时兜底**（否则会把一条已经进了页面列表的消息再弹一次托盘通知）：
    /// 状态仍是 <see cref="DeliveryState.Persisted"/>，终态由界面后续的
    /// <see cref="NotifyDisplayed"/> 或关窗时的 <see cref="NotifyDismissed"/> 给出。
    /// </summary>
    public void NotifyDisplayDeferred(long messageId, string detail = "")
    {
        if (messageId <= 0)
            return;

        MarkHandedToUi(messageId);
        AgentLog.Write($"[MSG] message_id={messageId} 已交给界面，等它确认是否真的画出来"
                     + (detail.Length > 0 ? $"（{detail}）" : ""));
    }

    /// <summary>
    /// WebView2 不可用，已用**原生通知**提醒过（§8.11 方案 (a)）。
    /// 用户确实被提醒到了 → 回报 <c>popup_displayed</c>；本地状态记
    /// <see cref="DeliveryState.NotifiedFallback"/>（排查时能看出「这条不是弹窗」）。
    /// </summary>
    public void NotifyFallbackDisplayed(long messageId, string reason)
    {
        if (messageId <= 0)
            return;

        MarkHandedToUi(messageId);
        Transition(messageId, DeliveryState.NotifiedFallback, $"原生通知已提醒：{reason}");
        SendAck(messageId, AckStatuses.PopupDisplayed, $"WebView2 不可用，回落原生通知（{reason}）");
    }

    /// <summary>
    /// 本机确实无法显示（headless 无人登录 / 没有订阅者 / 界面建不起来 / 通知也发不出去）。
    ///
    /// 终态 <see cref="DeliveryState.NotDisplayed"/> + 如实回报 <c>device_received</c>：
    /// 语义只说「设备收到了」，不谎报 <c>popup_displayed</c>；也不再用那个服务端不认识的
    /// <c>delivered</c>（§8.4 选项 (a)）。消息仍在库里，界面恢复后能重放。
    /// </summary>
    public void NotifyDisplayUnavailable(long messageId, string reason)
    {
        if (messageId <= 0)
            return;

        var current = CurrentState(messageId);
        if (current == DeliveryState.Read)
            return;                       // 已经看过的不再回退

        Transition(messageId, DeliveryState.NotDisplayed, $"无法显示：{reason}");
        SendAck(messageId, AckStatuses.DeviceReceived,
            $"无法显示时如实回报（{reason}）——不再发非法的 delivered");
    }

    /// <summary>
    /// 用户已经看过（关窗 / 自动关闭 / Alt+F4）→ 回报 <c>read</c>。
    ///
    /// 哪些消息算「看过」仍由界面决定（它才知道自己推过哪些还没被页面确认），
    /// 但**发什么 ack 由这里决定**。
    /// </summary>
    public void NotifyDismissed(long messageId, string detail = "")
    {
        if (messageId <= 0)
            return;

        if (CurrentState(messageId) == DeliveryState.Read)
        {
            AgentLog.Write($"[MSG] message_id={messageId} 已回报过 read，忽略重复");
            return;
        }

        MarkHandedToUi(messageId);
        Transition(messageId, DeliveryState.Read, detail.Length > 0 ? detail : "窗口已关闭/自动关闭");
        SendAck(messageId, AckStatuses.Read, "用户已经看过");
    }

    // ---------------- 水位线与四态（Phase 4）----------------

    /// <summary>
    /// 推进水位线到一个新的 <c>message_id</c>（幂等：只在更大时前进）。
    ///
    /// 收敛到一处的原因：水位有三个推进源（实时消息 / 补投一条 / 一次同步的合并结果），
    /// 分散写就会漏掉「本机自己发的消息也要推进」这条 —— 那会让下次重连把它当新消息，
    /// 于是补投自己的话、弹自己的窗。
    /// </summary>
    /// <returns>true = 水位真的前进了。</returns>
    public bool AdvanceWatermark(long messageId)
    {
        if (messageId <= 0)
            return false;

        lock (_gate)
        {
            var moved = messageId > _baseline;
            if (moved)
                _baseline = messageId;
            _watermarkKnown = true;          // 见过任何一条真实记账 ⇒ 水位就是"已知"的
            return moved;
        }
    }

    /// <summary>
    /// 首次同步专用：**只建立水位线，一条历史都不补投**。
    ///
    /// 为什么需要它：新装客户端本地库为空，而 history 里是最近 50 条**装之前就发生过**的事。
    /// 无条件补投 = 一装好就先"补"进几十条历史。走这条路的语义是
    /// 「记住我看到旧世界的最高一条」，此后只补它之后的消息 —— 正好对应
    /// 需求里的「补齐**未确认**消息」。
    /// </summary>
    public bool EstablishBaseline(long watermark)
    {
        var moved = AdvanceWatermark(watermark);
        AgentLog.Write($"[MSG] 首次同步：只建立水位线 watermark={Watermark}"
                     + (moved ? "（前进）" : "（无变化）") + "；不补投任何历史消息");
        return moved;
    }

    /// <summary>
    /// 一条消息的四态快照（需求 §8：已发送 / 已投递 / 已 ACK / 已显示）。
    ///
    /// 数据来源与边界（**本机不假装知道跨进程的事实**）：
    /// <list type="bullet">
    ///   <item><b>已发送</b>：本地库里有这条记账（<c>state ≥ Persisted</c>）—— 跨进程存活，最硬的事实；</item>
    ///   <item><b>已投递</b>：本次会话里确实交给了界面（页面渲染 / 补投进列表）；</item>
    ///   <item><b>已 ACK</b>：<c>ack</c> 帧真的交给了传输层（<see cref="SendAck"/> /
    ///     <see cref="FlushPendingAcks"/> 的成功分支才记账；进程重启后这些事实清零，
    ///     跨进程的 ACK 事实由服务端 <c>message_targets</c> 负责，本机不假装知道）；</item>
    ///   <item><b>已显示</b>：界面确认画出来了 / 原生通知回落提醒过 / 用户已读 ——
    ///     来自本地库的状态（跨进程存活）。</item>
    /// </list>
    /// </summary>
    /// <param name="messageId">要问的那条消息。</param>
    public DeliveryReport Report(long messageId)
    {
        var record = messageId > 0 ? _store.Get(messageId) : null;
        var state = record?.State ?? DeliveryState.Received;

        bool delivered, acked;
        lock (_gate)
        {
            delivered = _handedToUi.Contains(messageId);
            acked = _ackedStatus.ContainsKey(messageId);
        }

        var sent = record is not null && state >= DeliveryState.Persisted;
        var displayed = state == DeliveryState.Displayed
                        || state == DeliveryState.NotifiedFallback
                        || state == DeliveryState.Read;

        return new DeliveryReport(messageId, state, sent, delivered, acked, displayed);
    }

    /// <summary>已 ACK 事实里那条消息用的 ack 状态（没 ACK 过时为空串）—— 排查用。</summary>
    public string AckedStatusOf(long messageId)
    {
        lock (_gate) { return _ackedStatus.TryGetValue(messageId, out var status) ? status : ""; }
    }

    // ---------------- 重放与兜底 ----------------

    /// <summary>
    /// 还没显示过的消息（按收到时间升序，取最近 <paramref name="limit"/> 条）——
    /// 界面就绪时由平台层取走重新投递（**测试 C / §7-B-5**）。
    ///
    /// 去重：本次会话已经交给界面的（<see cref="NotifyDisplayDeferred"/> /
    /// <see cref="NotifyDisplayed"/>）不再返回，避免同一条弹两次气泡。
    /// 进程重启后这些记录来自文件（<see cref="JsonlMessageStore"/>），所以
    /// 「重启前没显示完的消息」在新进程里仍然会被重放。
    /// </summary>
    public IReadOnlyList<MessageRecord> PendingForReplay(int limit = 50)
    {
        var pending = _store.PendingReplay();          // 已按收到时间升序
        var result = new List<MessageRecord>();

        var start = limit > 0 && pending.Count > limit ? pending.Count - limit : 0;
        for (var i = start; i < pending.Count; i++)
        {
            var record = pending[i];
            bool handed;
            lock (_gate) { handed = _handedToUi.Contains(record.MessageId); }
            if (handed)
                continue;
            result.Add(record);
        }

        if (start > 0)
            AgentLog.Write($"[MSG] 重放候选 {pending.Count} 条，只取最近 {limit} 条（跳过最旧 {start} 条）");
        return result;
    }

    /// <summary>
    /// 扫一遍「落盘后迟迟没有任何界面处理」的消息，触发兜底提醒。
    /// 由平台层按自己的节奏调用（Windows 宿主挂在已有的 1 秒轮询里，不另起定时器）。
    /// 返回本次触发的条数。
    /// </summary>
    public int SweepDisplayTimeouts(DateTime utcNowUtc)
    {
        if (DisplayTimeout <= TimeSpan.Zero)
            return 0;

        var fired = 0;
        foreach (var record in _store.PendingReplay())
        {
            if (!record.State.IsAwaitingDisplay())
                continue;                     // 已经有终态（无界面/已回落提醒）的不用再兜底
            if (record.ReceivedAtUtc == default)
                continue;

            // ⚠ 先判超时、再记「已提醒」：反过来的话，第一次扫（还没到点）就会把它标记掉，
            //   到点的那次反而被跳过 —— 兜底永远不触发。
            if (utcNowUtc - record.ReceivedAtUtc < DisplayTimeout)
                continue;

            lock (_gate)
            {
                if (_handedToUi.Contains(record.MessageId) || _fallbackTried.Contains(record.MessageId))
                    continue;
                _fallbackTried.Add(record.MessageId);     // 先记账：订阅者抛异常也不会反复提醒
            }

            AgentLog.Write($"[MSG] message_id={record.MessageId} 落盘后 {DisplayTimeout.TotalSeconds:0} 秒"
                         + "仍未被界面处理 → 触发兜底提醒");
            var handler = DisplayFallbackRequired;
            if (handler is null)
            {
                NotifyDisplayUnavailable(record.MessageId, "没有兜底提醒的订阅者");
            }
            else
            {
                try
                {
                    handler(record, "display_timeout");
                }
                catch (Exception ex)
                {
                    AgentLog.Write($"[MSG] 兜底提醒订阅者抛异常（已忽略）：{ex.GetType().Name} {ex.Message}");
                    NotifyDisplayUnavailable(record.MessageId, "兜底提醒订阅者抛异常");
                }
            }
            fired++;
        }
        return fired;
    }

    /// <summary>把「已决策但没交出去」的 ACK 补发一遍；返回成功交出去的条数（连接建立/心跳时调用）。</summary>
    public int FlushPendingAcks() => _pendingAcks.Flush(HandOverAck);

    // ---------------- 内部 ----------------

    private DeliveryState CurrentState(long messageId) =>
        _store.Get(messageId)?.State ?? DeliveryState.Received;

    private void MarkHandedToUi(long messageId)
    {
        lock (_gate) { _handedToUi.Add(messageId); }
    }

    /// <summary>改状态（落盘 + 发事件）。<see cref="DeliveryState.AckSent"/> 只出现在事件流里，不落盘。</summary>
    private void Transition(long messageId, DeliveryState state, string detail)
    {
        if (messageId <= 0)
            return;

        if (state != DeliveryState.AckSent)
        {
            var record = _store.Get(messageId);
            if (record is null)
            {
                // 库里没有（例如页面回报了一条不在本机记账里的 id）：事件照发，ack 照走
                AgentLog.Write($"[MSG] message_id={messageId} 不在本地库里（state={state.ToStoreValue()}），只记事件");
            }
            else if (record.State != state)
            {
                _store.Put(record.With(state, DateTime.UtcNow));
            }
        }

        RaiseState(messageId, state, detail);
    }

    private void RaiseState(long messageId, DeliveryState state, string detail)
    {
        var handler = DeliveryStateChanged;
        if (handler is null)
            return;

        try
        {
            handler(new MessageDeliveryStateChangedArgs(messageId, state, detail ?? ""));
        }
        catch (Exception ex)
        {
            AgentLog.Write($"[MSG] 投递状态订阅者抛异常（已忽略）：{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>ACK 决策的唯一出口：登记 → 交给传输层 → 成功了才从待发队列移除。</summary>
    private void SendAck(long messageId, string status, string why)
    {
        if (messageId <= 0 || string.IsNullOrWhiteSpace(status))
            return;

        _pendingAcks.Enqueue(messageId, status);

        var handed = false;
        try
        {
            HandOverAck(messageId, status);
            _pendingAcks.MarkSent(messageId, status);
            handed = true;
            AgentLog.Write($"[MSG] message_id={messageId} → ack:{status}（{why}）");
        }
        catch (Exception ex)
        {
            AgentLog.Write($"[MSG] message_id={messageId} ack:{status} 交给传输层失败，留在待发队列"
                         + $"（重连/心跳时补发）：{ex.GetType().Name} {ex.Message}");
        }

        RaiseState(messageId, DeliveryState.AckSent,
            handed ? $"ack:{status}" : $"ack:{status}（待补发）");
    }

    /// <summary>
    /// 把一条 ack **真的**交给传输层（Phase 4 的 ACK 事实记录点）。
    ///
    /// 成功时才记 <see cref="_ackedStatus"/> —— 它抛异常就是"没交出去"，
    /// 由调用方决定是留在待发队列（<see cref="SendAck"/>）还是下次再试（<see cref="FlushPendingAcks"/>）。
    /// 这样 <see cref="Report"/> 的「已 ACK」才等于「服务端真的可能收到了」，
    /// 而不是「我们决定要发」。
    /// </summary>
    private void HandOverAck(long messageId, string status)
    {
        _sendAck(messageId, status);
        lock (_gate) { _ackedStatus[messageId] = status; }
    }
}
