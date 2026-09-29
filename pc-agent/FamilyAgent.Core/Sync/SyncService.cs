using System;
using FamilyAgent.Core.Diagnostics;
using FamilyAgent.Core.Events;
using FamilyAgent.Core.Messaging;
using FamilyAgent.Core.Protocol.Frames;

namespace FamilyAgent.Core.Sync;

/// <summary>
/// 上线握手编排：<c>Connect → Authenticate(hello) → Sync/History 补齐 → Live</c>
/// （docs/CORE-REFACTOR-PLAN.md §6 Phase 4，需求 §8）。
///
/// 它解决的是「**其它设备在断线期间说的话永远不会到本机**」这个真实缺口
/// （方案 §8.2：服务端不写设备消息的 <c>message_targets</c>，广播只推在线设备）——
/// 本轮在客户端侧兜住：**每次重连都拉一次最近 N 条历史，与本地库合并去重，
/// 把漏掉的补进来**。
///
/// 一次会话的完整轨迹（每个阶段都进 <c>agent.log</c>，验收标准 §7-D-10）：
/// <code>
/// ① 未连接 → 连接中           Enter()：开始跟连接状态
/// ② 连接中 → 认证中           ConnectionStateChanged(Connected)
/// ③ 认证中 → 补齐中           HelloReceived：发 history_request，记住 request_id
/// ④ 补齐中 → 实时模式         HistoryReceived（配对成功 → 合并去重）或 Tick 超时放行
/// 断开                        ConnectionStateChanged(Disconnected) → 未连接（下次重连重跑全程）
/// </code>
///
/// ★ 三条已定的行为（主管拍板，见 <see cref="MessageManager.HandleBackfill"/> 的说明）：
/// <list type="number">
///   <item><b>补投的历史消息只落盘 + 只进列表，不弹全屏、不发 ACK</b> ——
///     所以本类一律调 <see cref="MessageManager.HandleBackfill"/>，绝不调用
///     <see cref="MessageManager.HandleIncoming"/>（那条路会弹窗、会发 ack）；</item>
///   <item><b>首次同步只建水位线，一条都不补投</b> ——
///     <see cref="HistoryBackfill.Plan"/> 在 <c>watermarkKnown=false</c> 时直接返回全量基线；</item>
///   <item><b>绝不补投 <c>direction=out</c></b>（本机自己发的）—— 只推进水位，
///     否则会把自己的话当新消息补一遍。</item>
/// </list>
///
/// ★ 为什么它只认 <see cref="ISyncConnection"/> 而不是直接吃 <c>ConnectionManager</c>：
///   单测要造「断开 → 服务端补投 → 重连后重新投递」这种现场，而真的
///   <c>ConnectionManager</c> 里是一条 <c>ClientWebSocket</c> 连接循环
///   （Linux 单测里造不出来）。Android 客户端将来实现同一套语义也能原样复用本类。
/// </summary>
public sealed class SyncService
{
    /// <summary>
    /// 一次补齐拉多少条历史。200 = **服务端的上限**
    /// （<c>server/main.py:903</c> 的 <c>min(limit, 200)</c>）——
    /// 要更多就得改服务端（本轮不改），所以这里就按上限要。
    /// </summary>
    public const int DefaultHistoryLimit = 200;

    private readonly ISyncConnection _connection;
    private readonly MessageManager _messaging;
    private readonly object _gate = new();

    private SyncPhase _phase = SyncPhase.Disconnected;
    private bool _entered;

    /// <summary>本次补齐请求的 <c>request_id</c>（用来与 <c>history_response</c> 配对）；空串 = 没有在等的请求。</summary>
    private string _pendingRequestId = "";

    /// <summary>进入补齐阶段的时刻（UTC；超时放行按它算）。</summary>
    private DateTime _syncStartedUtc = DateTime.MinValue;

    public SyncService(ISyncConnection connection, MessageManager messaging)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _messaging = messaging ?? throw new ArgumentNullException(nameof(messaging));
    }

    /// <summary>一次补齐要多少条历史（默认 <see cref="DefaultHistoryLimit"/>）。</summary>
    public int HistoryLimit { get; set; } = DefaultHistoryLimit;

    /// <summary>
    /// 补齐阶段最多等多久；超时就放行进实时模式（<see cref="Tick"/> 判它）。
    ///
    /// 为什么必须有超时：<c>history_response</c> 可能永远不来（服务端那侧异常 /
    /// 连接在对端处理前就断了）。没有超时的话阶段会永远停在「补齐中」——
    /// 而卡在补齐阶段意味着日志永远说不清"到底进实时了没"。
    /// 设成 <c>TimeSpan.Zero</c> 或负值 = 关闭超时（永远等）。
    /// </summary>
    public TimeSpan SyncTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>当前握手阶段。</summary>
    public SyncPhase Phase
    {
        get { lock (_gate) { return _phase; } }
    }

    /// <summary>是否已经在实时模式（＝补齐走完或超时放行）。</summary>
    public bool IsLive => Phase == SyncPhase.Live;

    /// <summary>最近一次合并的结果（还没做过补齐时为 null）—— 日志与单测看它。</summary>
    public HistoryMergePlan? LastPlan { get; private set; }

    /// <summary>阶段变化（Core 只抛事实，怎么显示由平台层决定）。</summary>
    public event Action<SyncPhaseChangedArgs>? PhaseChanged;

    /// <summary>
    /// 开始跟连接状态（**必须在 <c>ConnectionManager.Start()</c> 之前调用**）。
    ///
    /// 为什么要有这个前置条件：认证信号是"服务端下发的第一帧 <c>hello</c>"，
    /// 它每次连接只来一次。如果连接已经建立、<c>hello</c> 也已经消费掉了才调用本方法，
    /// 本次会话就等不到那个信号了（会停在「认证中」直到下一次重连 —— 不会错，但会慢一拍）。
    /// 幂等：重复调用不会重复订阅。
    /// </summary>
    public void Enter()
    {
        lock (_gate)
        {
            if (_entered)
                return;
            _entered = true;
        }

        _connection.ConnectionStateChanged += OnConnectionStateChanged;
        _connection.HelloReceived += OnHelloReceived;
        _connection.HistoryReceived += OnHistoryReceived;

        SetPhase(SyncPhase.Connecting, "① 建立连接（CONNECT → AUTHENTICATE → SYNC → LIVE）");
    }

    /// <summary>停止跟连接状态（退出 / 换连接对象时）。</summary>
    public void Leave(string reason = "")
    {
        lock (_gate)
        {
            if (!_entered)
                return;
            _entered = false;
            _pendingRequestId = "";
        }

        _connection.ConnectionStateChanged -= OnConnectionStateChanged;
        _connection.HelloReceived -= OnHelloReceived;
        _connection.HistoryReceived -= OnHistoryReceived;

        SetPhase(SyncPhase.Disconnected, "已停止同步" + (string.IsNullOrEmpty(reason) ? "" : "：" + reason));
    }

    /// <summary>
    /// 补齐超时检查 —— 由平台层按自己的节奏调用（与
    /// <see cref="MessageManager.SweepDisplayTimeouts"/> 同一个做法：挂在宿主已有的
    /// 1 秒轮询里，不另起定时器）。返回 1 = 本次真的放行了。
    /// </summary>
    public int Tick(DateTime utcNowUtc)
    {
        if (Phase != SyncPhase.Syncing || SyncTimeout <= TimeSpan.Zero)
            return 0;

        DateTime started;
        lock (_gate) { started = _syncStartedUtc; }
        if (started == default || utcNowUtc - started < SyncTimeout)
            return 0;

        lock (_gate) { _pendingRequestId = ""; }
        AgentLog.Write($"[SYNC] 历史补齐超时（{SyncTimeout.TotalSeconds:0} 秒没有 history_response）→ 放行");
        SetPhase(SyncPhase.Live, "④ 补齐超时放行，进入实时模式（未拿到 history_response）");
        return 1;
    }

    // ---------------- 各阶段 ----------------

    private void OnConnectionStateChanged(ConnectionStateChangedArgs args)
    {
        if (args is null)
            return;

        if (!args.Connected)
        {
            lock (_gate) { _pendingRequestId = ""; }
            SetPhase(SyncPhase.Disconnected,
                "连接断开（" + (string.IsNullOrEmpty(args.Detail) ? "未连接" : args.Detail) + "）→ 下次连上重新走全程");
            return;
        }

        SetPhase(SyncPhase.Authenticating, "② 已连接，等服务端 hello（认证完成）");
    }

    /// <summary><c>hello</c> ＝ 认证完成 → 开始一次补齐。</summary>
    private void OnHelloReceived(HelloFrame frame)
    {
        bool entered;
        lock (_gate) { entered = _entered; }
        if (!entered)
            return;

        var known = _messaging.WatermarkKnown;
        var watermark = known ? _messaging.Watermark.ToString() : "未知（本地库为空 → 首次同步只建水位线）";

        var requestId = _connection.RequestHistoryTracked(HistoryLimit);
        lock (_gate)
        {
            _pendingRequestId = requestId;
            _syncStartedUtc = DateTime.UtcNow;
        }

        SetPhase(SyncPhase.Syncing,
            $"③ 已认证，拉最近 {HistoryLimit} 条历史做补齐（request_id={requestId}，水位={watermark}）");
    }

    /// <summary>
    /// <c>history_response</c> 到达 —— 只认自己那次请求的应答
    /// （用户点开对话窗口那条路也走同一个事件，不配对就交给宿主当列表数据）。
    /// </summary>
    private void OnHistoryReceived(HistoryFrame frame)
    {
        if (frame is null)
            return;

        if (Phase != SyncPhase.Syncing)
        {
            AgentLog.Write("[SYNC] 收到 history_response，但当前不在补齐阶段（用户主动拉列表）→ 不参与合并");
            return;
        }

        string pending;
        lock (_gate) { pending = _pendingRequestId; }

        if (pending.Length > 0 && !string.Equals(frame.RequestId, pending, StringComparison.Ordinal))
        {
            AgentLog.Write($"[SYNC] history_response 的 request_id={frame.RequestId} 与本次补齐"
                         + $"（{pending}）不配对 → 忽略（不参与合并去重）");
            return;
        }

        Apply(frame);
    }

    /// <summary>合并去重 + 把该补投的补进正常流程，然后进实时模式。</summary>
    private void Apply(HistoryFrame frame)
    {
        var incoming = HistoryBackfill.ParseAll(frame.Messages);
        var watermarkKnown = _messaging.WatermarkKnown;
        var plan = HistoryBackfill.Plan(
            incoming, _messaging.Watermark, watermarkKnown, _messaging.Store.Contains);
        LastPlan = plan;

        AgentLog.Write($"[SYNC] 合并：{plan.Describe()}");

        if (!watermarkKnown)
        {
            // 首次同步：只建立水位线，一条历史都不补投（新装客户端不该被灌历史）
            _messaging.EstablishBaseline(plan.NewWatermark);
        }
        else
        {
            // 补投：只落盘 + 只进列表（不弹全屏、不发 ACK）。
            // auto_close_seconds 用「本地最近一条实时消息的取值」当参照 ——
            // history 里没有这个字段（HistoryBackfill.ToFrame 的说明）。
            var autoCloseSeconds = _messaging.LastAutoCloseSeconds;
            foreach (var message in plan.ToDeliver)
                _messaging.HandleBackfill(HistoryBackfill.ToFrame(message, frame.Messages, autoCloseSeconds));

            // 水位推进到本次合并结果（含本机自己发的那些：它们不补投，但必须算"已知"）
            _messaging.AdvanceWatermark(plan.NewWatermark);
            if (plan.ToDeliver.Count > 0)
                AgentLog.Write($"[SYNC] 断线期间漏掉 {plan.ToDeliver.Count} 条，已补投进本地库与列表"
                             + "（不弹全屏、不发 ACK）");
        }

        lock (_gate) { _pendingRequestId = ""; }
        SetPhase(SyncPhase.Live, $"④ 补齐完成，进入实时模式（{plan.Describe()}）");
    }

    // ---------------- 阶段与日志 ----------------

    private void SetPhase(SyncPhase next, string detail)
    {
        SyncPhase previous;
        lock (_gate)
        {
            if (_phase == next)
            {
                AgentLog.Write($"[SYNC] 阶段不变（{next.Describe()}）：{detail}");
                return;
            }
            previous = _phase;
            _phase = next;
        }

        var args = new SyncPhaseChangedArgs(previous, next, detail);
        AgentLog.Write($"[SYNC] {args.Describe()}：{detail}");

        var handler = PhaseChanged;
        if (handler is null)
            return;

        try
        {
            handler(args);
        }
        catch (Exception ex)
        {
            AgentLog.Write("[SYNC] 阶段变化订阅者抛异常（已忽略）：" + ex.GetType().Name + " " + ex.Message);
        }
    }
}
