namespace FamilyAgent.Core.Messaging;

/// <summary>
/// 一条消息在本机的生命周期状态（docs/CORE-REFACTOR-PLAN.md §3.2 / §6 Phase 2）。
///
/// 与「服务端的逐设备状态」是两回事：服务端那套
/// （<c>created → server_received → device_received → popup_displayed → read</c>，
/// 见 <c>server/services/messages.py</c> 的 <c>STATES</c>）只在服务端内部记账、
/// 不对外广播（群聊模型下对外只有单一 <c>sent</c>）。这里记的是**本机自己的**
/// 生命周期，用来回答两件必须由 Core 回答的问题：
///
/// <list type="number">
///   <item>这条消息要不要重放给界面（<see cref="DeliveryStateExtensions.IsBeforeDisplay"/>）；</item>
///   <item>该给服务端回报哪个 ack（决策在 <see cref="MessageManager"/>，不在 UI 层）。</item>
/// </list>
///
/// ★ 与 UI 解耦的关键：**「显示失败」也有终态**。
///   没有界面（headless / 订阅者异常 / 界面建不起来）时不会永远挂在
///   「未 ACK」上：记 <see cref="NotDisplayed"/> 并如实回报 <c>device_received</c>；
///   WebView2 不可用时回落到原生通知，记 <see cref="NotifiedFallback"/> 并回报
///   <c>popup_displayed</c>（§8.11 建议的做法 (a)：可靠优先，且本地状态名能区分
///   「真弹窗」与「只发了通知」，不会把回落说成弹窗）。
///
/// ⚠ 取值顺序有意义：<c>Received &lt; Persisted &lt; NotDisplayed &lt; NotifiedFallback &lt; Displayed &lt; Read</c>
///   （<see cref="AckSent"/> 例外，见下），重放判据直接按数值比。
///   Phase 4 追加的 <see cref="Backfilled"/> 也例外：它 > <c>Read</c>，见它自己的说明。
/// </summary>
public enum DeliveryState
{
    /// <summary>Core 收到了 <c>message</c> 帧并把 <c>message_id</c> 归一（<= 0 的 id 不走这条路）。</summary>
    Received = 0,

    /// <summary>已写入本地 <see cref="IMessageStore"/> —— 进程此刻死掉也不会丢。</summary>
    Persisted = 1,

    /// <summary>
    /// 本机无法显示（headless 无人登录 / 没有显示订阅者 / 订阅者抛异常 / 界面建不起来）。
    /// 如实回报 <c>device_received</c>：语义是「设备收到了」，不谎报
    /// <c>popup_displayed</c>，也不再用原来那个服务端不认识的状态 <c>delivered</c>
    /// （§8.4：<c>delivered</c> 的 rank 是 -1，服务端直接 no-op）。
    /// 这类消息仍属「没显示过」，界面恢复后可重放。
    /// </summary>
    NotDisplayed = 2,

    /// <summary>
    /// 用**原生通知**提醒过（WebView2 不可用时的回落，§8.11 方案 (a)，Windows 侧
    /// <c>Platform/FallbackNotifier.cs</c> 的托盘气泡）。用户确实被提醒到了，
    /// 所以回报 <c>popup_displayed</c>；本地状态区别于 <see cref="Displayed"/>，
    /// 日志与排查上能看出「这条不是弹窗，是通知」。
    /// </summary>
    NotifiedFallback = 3,

    /// <summary>页面回报「真的画到屏幕上了」（<c>web.ack</c> → <c>MessageManager.NotifyDisplayed</c>）→ 回报 <c>popup_displayed</c>。</summary>
    Displayed = 4,

    /// <summary>
    /// 「这条状态对应的 ack 已经交给传输层了」——**只有事件流里会出现这一步**，
    /// 不落盘（落盘的是它前一步的显示状态）。存在的意义是把「显示」与
    /// 「ACK 已交出去」分开看得见：ACK 决策在 Core 里，出问题时要能一眼看出
    /// 是「没显示」还是「显示了但 ack 没发出去」。
    /// </summary>
    AckSent = 5,

    /// <summary>用户关掉了强提醒窗（点了「知道了」/ 自动关闭 / Alt+F4）→ 回报 <c>read</c>。</summary>
    Read = 6,

    /// <summary>
    /// 服务端补投的历史消息（docs/CORE-REFACTOR-PLAN.md §6 Phase 4）。
    ///
    /// ★ 它存在的唯一理由是**把「补投」与「待显示」彻底分开**：主管已定
    ///   「补投的历史消息只落盘 + 只进对话列表，绝不弹全屏、绝不发 ACK」。
    ///   若补投的记录记成 <see cref="Persisted"/>，那么
    ///   <see cref="IMessageStore.PendingReplay"/> / <see cref="MessageManager.PendingForReplay"/>
    ///   会把它当成「还没显示过」的消息 —— 于是①页面就绪时被当弹窗重放一次
    ///   （正是「一装好弹 50 个全屏窗」那条路），②<see cref="MessageManager.SweepDisplayTimeouts"/>
    ///   会为它触发托盘兜底提醒。
    ///
    /// 取值刻意放在 <see cref="Read"/> 之后（<c>&gt; Displayed</c>）：两个判据
    ///   （<see cref="DeliveryStateExtensions.IsBeforeDisplay"/> /
    ///   <see cref="DeliveryStateExtensions.IsAwaitingDisplay"/>）都按大小比，
    ///   于是它自动是**终态**：不重放、不兜底、不再需要任何界面回报。
    ///
    /// ⚠ 「已显示」对补投的消息永远是 false（见 <c>MessageManager.Report</c>）：
    ///   它进的是列表，从来没弹过窗，也没有任何人回报过它被画出来。
    /// </summary>
    Backfilled = 7,
}

/// <summary><see cref="DeliveryState"/> 的判据与落盘字符串（集中一处，避免各处比大小 / 拼字符串）。</summary>
public static class DeliveryStateExtensions
{
    /// <summary>
    /// 还没显示过（= 需要重放给界面的候选）。**含 <see cref="NotDisplayed"/> 与
    /// <see cref="NotifiedFallback"/>**：§6 Phase 2-3 的判据是「status &lt; Displayed」，
    /// 而且 §7-B-5 要求「WebView2 故障期间的消息，界面恢复后仍能显示」——
    /// 只收到过托盘气泡的消息也属「没在界面里显示过」。
    /// </summary>
    public static bool IsBeforeDisplay(this DeliveryState state) => state < DeliveryState.Displayed;

    /// <summary>等界面给结论的那两个状态（收到 / 已落盘）；兜底提醒的超时只看这两个。</summary>
    public static bool IsAwaitingDisplay(this DeliveryState state) =>
        state == DeliveryState.Received || state == DeliveryState.Persisted;

    /// <summary>落盘用的字面量（小写，人在文件里看得懂；不参与协议）。</summary>
    public static string ToStoreValue(this DeliveryState state) => state switch
    {
        DeliveryState.Received => "received",
        DeliveryState.Persisted => "persisted",
        DeliveryState.NotDisplayed => "not_displayed",
        DeliveryState.NotifiedFallback => "notified_fallback",
        DeliveryState.Displayed => "displayed",
        DeliveryState.AckSent => "ack_sent",
        DeliveryState.Read => "read",
        DeliveryState.Backfilled => "backfilled",
        _ => "persisted",
    };

    /// <summary>读回落盘字面量；不认识的字面量当 <see cref="DeliveryState.Persisted"/>（宁可重放，不可静默丢弃）。</summary>
    public static DeliveryState ParseStoreValue(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "received" => DeliveryState.Received,
        "persisted" => DeliveryState.Persisted,
        "not_displayed" => DeliveryState.NotDisplayed,
        "notified_fallback" => DeliveryState.NotifiedFallback,
        "displayed" => DeliveryState.Displayed,
        "ack_sent" => DeliveryState.AckSent,
        "read" => DeliveryState.Read,
        "backfilled" => DeliveryState.Backfilled,
        _ => DeliveryState.Persisted,
    };
}
