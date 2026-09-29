using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FamilyAgent.Core.Commands;
using FamilyAgent.Core.Commands.Abstractions;
using FamilyAgent.Core.Config;
using FamilyAgent.Core.Devices;
using FamilyAgent.Core.Diagnostics;
using FamilyAgent.Core.Events;
using FamilyAgent.Core.Protocol;
using FamilyAgent.Core.Protocol.Frames;
using FamilyAgent.Core.Sync;

namespace FamilyAgent.Core.Transport;

/// <summary>
/// 与 NAS 服务端的长连接客户端。
///
/// 行为：连接 → 心跳 → 收消息 → 回 ACK → 收截图请求 → 回图。
/// 断线自动重连（2s 起、指数退避、30s 封顶）。
///
/// 唯一的例外：服务端用关闭码 4000 表示「另一个实例已经接管了这个 device_id」
/// （server/hub.py 的 bind_device），这种**不重连** —— 抢回来只会和对面无限对踢。
/// 退出循环后由平台层（Windows 侧 <c>Presence</c>）决定什么时候再接管。
///
/// 位置与分层：本类就是原 <c>pc-agent/FamilyAgent/AgentClient.cs</c> 的整体搬迁
/// （见 docs/CORE-REFACTOR-PLAN.md §3.2 / §4），按职责拆成
/// <c>WebSocketTransport</c>（收发原语）/ <c>Outbox</c>（发送队列）/
/// <c>HeartbeatLoop</c>（心跳与看门狗）/ <c>BackoffPolicy</c>（退避）/
/// <c>Protocol.FrameCodec</c>（帧解析），本类只留「连接编排 + 事件派发」。
///
/// ★ 与原文件的差别只有两类，且都是本阶段明确要求的：
///   ① **依赖倒置**：不再直读宿主静态成员 <c>App.IsHeadless</c> 与
///      <c>SessionState.Current</c>（那两条让它贴着 UI/Windows 层），改为注入
///      <see cref="IPlatformInfo"/>；
///   ② **事件强类型**：不再向外抛裸 <c>JsonElement</c>，改为强类型帧
///      （<c>Protocol.Frames.*</c> 与 <see cref="ConnectionStateChangedArgs"/>）。
///   收发路径、日志文案、退避与看门狗阈值**逐字未变**（Phase 1 的验收前提是行为等价）。
///
/// ★ 连接代次（epoch）语义原样保留（工作区那版修复）：全类只有这一条连接循环，
///   每次 <c>Start()</c> 让代次 +1，循环只认自己那一代 —— 见 <see cref="ConnectionEpoch{TConn}"/>。
///
/// ★ Phase 4：本类**同时是** <see cref="ISyncConnection"/>（那个接口只是本类的一个视图 ——
///   连接状态 / <c>hello</c> / <c>history_response</c> / 发 <c>history_request</c> 这四件事
///   本来就在这里）。为此本阶段**只新增**两项：<see cref="HelloReceived"/> 与
///   <see cref="RequestHistoryTracked"/>；连接代次、退避、心跳、Outbox、握手行为一律未动
///   （原 <see cref="RequestHistory"/> 只是改道到新的那个方法上，发出的帧逐字相同）。
/// </summary>
public sealed class ConnectionManager : ICommandChannel, ISyncConnection
{
    /// <summary>服务端「被新连接顶替」用的关闭码（见 server/hub.py 的 bind_device）。
    /// 4000 属私有区间，.NET 的 WebSocketCloseStatus 里没有对应枚举名，只能按数值比。</summary>
    private const int ReplacedCloseCode = 4000;

    private readonly AgentConfig _config;
    private readonly IPlatformInfo _platform;
    private readonly CapabilityReporter _capabilities;
    private readonly WebSocketTransport _transport;
    private readonly Outbox _outbox;
    private readonly ConnectionEpoch<ClientWebSocket> _epoch = new();
    private readonly BackoffPolicy _backoff = new();

    /// <summary>「循环在跑」标志：<c>_cts</c> 非空 = 循环在跑。只保护它自己 ——
    /// 连接身份与代次归 <see cref="_epoch"/> 管（原来两者共用一把锁，
    /// 抽开后锁顺序恒为 life → epoch，不存在反向嵌套）。</summary>
    private readonly object _lifeGate = new();
    private CancellationTokenSource? _cts;

    /// <summary>连接状态变化（取代原 <c>ConnectionChanged(bool, string)</c>，见 §3.3）。</summary>
    public event Action<ConnectionStateChangedArgs>? ConnectionStateChanged;

    /// <summary>收到新消息（强类型帧取代原来的裸 <c>JsonElement</c>）。</summary>
    public event Action<MessageFrame>? MessageReceived;

    /// <summary>
    /// 服务端下发第一帧 <c>hello</c> ＝ **认证完成**（<see cref="ISyncConnection"/> 的成员）。
    ///
    /// 为什么必须单独一个事件：原实现里 <c>hello</c> 只做两件事（存 token / 重置心跳看门狗），
    /// 对外没有任何信号 —— 于是 Phase 4 的 SyncService 无法知道"现在可以拉历史补齐了"，
    /// 只能靠猜时间。它在 <c>hello</c> 的既有处理**之后**抛出。
    /// </summary>
    public event Action<HelloFrame>? HelloReceived;

    /// <summary>服务端请求截图（原来只传 request_id 字符串）。</summary>
    public event Action<ScreenshotRequestFrame>? ScreenshotRequested;

    /// <summary>本机回复被服务端受理（<c>reply_ack</c>）。</summary>
    public event Action<ReplyAckFrame>? ReplyAcked;

    /// <summary>主动拉的历史到达（<c>history_response</c>）。</summary>
    public event Action<HistoryFrame>? HistoryReceived;

    /// <summary>服务端下发关机指令。</summary>
    public event Action<ShutdownCommandFrame>? ShutdownRequested;

    /// <summary>收到远程解锁请求（<c>unlock_request</c>，远程解锁 Phase 1）。
    /// 校验与应答都不需要桌面，headless（登录界面）下同样能走通。</summary>
    public event Action<UnlockRequestFrame>? UnlockRequested;

    /// <summary>
    /// 收到**共享昵称**相关的一帧（docs/NICKNAME-SYSTEM-PLAN.md §5.4）。
    ///
    /// 载荷是三类之一：<see cref="NicknameListFrame"/>（整表：<c>nickname_list_response</c> /
    /// <c>nickname_list_sync</c>）、<see cref="NicknameDeltaFrame"/>（四个增量广播）、
    /// <see cref="NicknameErrorFrame"/>（<c>nickname_error</c>）。
    ///
    /// 为什么合成**一个**事件而不是七个：昵称在客户端只有一份消费者（<c>Nicknames.NicknameService</c>），
    /// 七条对称的事件只会让宿主多写七行订阅；连接层的职责只是「把这三类帧交给它」。
    /// </summary>
    public event Action<CoreFrame>? NicknameFrameReceived;

    public event Action<string>? Log;

    /// <param name="capabilities">
    /// 本平台注册了哪些能力实现（Phase 3）。**必填**，没有默认值：
    /// 能力上协议就是事实契约，一个「忘了传」的默认值只会让本机在服务端悄悄变成
    /// 「什么都不会」的哑设备（服务端不校验取值，见 §8.6）。
    /// 不支持的平台请显式传 <see cref="PlatformCapabilities.None"/>。
    /// </param>
    public ConnectionManager(AgentConfig config, IPlatformInfo platform, PlatformCapabilities capabilities)
    {
        _config = config;
        _platform = platform;
        _capabilities = new CapabilityReporter(platform, capabilities);
        _transport = new WebSocketTransport(WriteLog);
        _outbox = new Outbox(_transport, CurrentSocket);
    }

    /// <summary>
    /// 能力上报（Phase 3）：连接串与心跳帧两处取值都从这里来，保证同源。
    /// </summary>
    public CapabilityReporter Capabilities => _capabilities;

    public bool Connected => CurrentSocket() is { State: WebSocketState.Open };

    /// <summary>给日志用的连接状态描述（排查时能直接看出卡在哪）。</summary>
    public string DescribeConnection() => WebSocketTransport.Describe(CurrentSocket());

    /// <summary>积压未发出的消息条数。</summary>
    public int OutboxCount => _outbox.Count;

    /// <summary>
    /// 当前连接的 id（完整 GUID，每次连接一个）。日志与连接状态事件用它，
    /// 两个实例互踢时全靠它对账。Phase 1 **不上协议**（§8.5 的推荐做法：只在客户端本地贯穿）。
    /// </summary>
    public string? CurrentConnectionId => _epoch.CurrentConnectionId;

    private ClientWebSocket? CurrentSocket() => _epoch.Current;

    private void WriteLog(string message) => Log?.Invoke(message);

    /// <summary>启动连接循环。已经在跑就不重复启动（防止出现第二条循环）。</summary>
    public void Start()
    {
        CancellationTokenSource cts;
        int epoch;
        lock (_lifeGate)
        {
            if (_cts is not null)
                return;                       // 已经在跑，绝不起第二条
            cts = new CancellationTokenSource();
            _cts = cts;
            epoch = _epoch.Begin();           // 代次 +1（与原来同在 _lifeGate 里，语义不变）
        }

        _ = Task.Run(() => LoopAsync(cts, epoch));
    }

    /// <summary>
    /// 停掉连接循环：取消 + 掐断连接。
    ///
    /// _cts 立刻置空（= 释放「在跑」标志），紧接着的 Start() 才能起得来；
    /// 旧循环拿的是**自己那个** CTS 的 token，取消后它会自行退出，且因为代次
    /// 已过期，既不会重连也不会碰共享状态 —— 所以不会真的出现两条在跑的循环。
    /// </summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        ClientWebSocket? ws;
        lock (_lifeGate)
        {
            cts = _cts;
            _cts = null;
            ws = CurrentSocket();
        }

        try { cts?.Cancel(); } catch { }
        try { ws?.Abort(); } catch { }
    }

    public void Restart()
    {
        Stop();
        Start();
    }

    /// <summary>关闭码的日志写法：4000 这类私有码没有枚举名，直接打数值。</summary>
    private static string DescribeCloseCode(WebSocketCloseStatus? code) =>
        code is null ? "none" : ((int)code.Value).ToString();

    /// <summary>
    /// 连接 URL。
    ///
    /// ★ Phase 3 补上了 <c>windows_state</c> / <c>capabilities</c> 两个查询参数：
    ///   服务端**早就有**这条「一连上就报」的路（<c>server/main.py:740-741</c> 读、
    ///   <c>:772-773</c> 写库并广播），但客户端一直没带 —— 那条路是空转的，
    ///   能力/状态要等连上之后的第一次心跳（可能整整 15 秒）才到服务端。
    ///   现在握手即报，网页端不会先看到「未知设备」再自己变。
    ///
    /// ⚠ 两个参数的**格式**必须与服务端的解析对齐（<c>_apply_reported_state()</c>，
    ///   <c>main.py:262-288</c>）：<c>windows_state</c> 是单个字面量、
    ///   <c>capabilities</c> 是**逗号分隔**的字符串。
    ///
    /// public 是为了单测：这是一个纯函数（只读配置与平台事实），
    /// 而「参数到底有没有带上」是 Phase 3 的验收判据之一（§6 Phase 3-4）。
    /// </summary>
    public Uri BuildUri()
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
        Add("platform", _platform.Platform);
        Add("agent_version", ProtocolVersion.AgentVersion);
        Add("enroll_token", _config.EnrollToken ?? "");
        Add("windows_state", _platform.SessionState);
        Add("capabilities", _capabilities.QueryValue());

        return new Uri($"{baseUrl}/ws/device/{Uri.EscapeDataString(_config.DeviceId)}{q}");
    }

    /// <summary>
    /// 全类**唯一**的连接/重连循环（心跳循环和接收循环只在这条连接上收发，
    /// 不会各自去建连接）。
    ///
    /// 每次迭代 = 一次「连接 → 收发 → 断开」，断开后按退避重连；
    /// 但被服务端顶替（code 4000）时直接退出，不重连。
    /// </summary>
    private async Task LoopAsync(CancellationTokenSource cts, int epoch)
    {
        var ct = cts.Token;

        try
        {
            while (!ct.IsCancellationRequested && _epoch.IsCurrent(epoch))
            {
                // 这次连接的 id：完整 GUID（§Phase 1-2）。日志里带上它才能分清是
                // 哪一代连接收到的（两个实例互踢时全靠它）。
                var connId = Guid.NewGuid().ToString("N");
                ClientWebSocket? ws = null;
                WebSocketCloseStatus? closeCode = null;

                // 本代的心跳循环：看门狗字段是它的实例字段（§8.7），
                // 收到 hello / heartbeat_ack 时由本代连接派发到**同一个**实例上。
                var heartbeat = new HeartbeatLoop(_transport, _outbox, _platform, _capabilities.Capabilities);

                try
                {
                    ws = new ClientWebSocket();

                    // 已被新的一代取代（Stop() 后紧接着 Start() 的那段窗口）就别连了
                    if (!_epoch.TryPublish(epoch, ws, connId))
                        break;

                    AgentLog.Write($"[WS] CONNECTING connection={connId}");
                    await ws.ConnectAsync(BuildUri(), ct).ConfigureAwait(false);
                    AgentLog.Write($"[WS] CONNECTED  connection={connId}");

                    _backoff.Reset();
                    ConnectionStateChanged?.Invoke(new ConnectionStateChangedArgs(
                        ConnectionState.Connected, connId, "已连接"));

                    var beat = heartbeat.RunAsync(ws, connId, ct);
                    closeCode = await _transport.ReceiveLoopAsync(
                        ws, text => HandleIncoming(text, connId, heartbeat), ct).ConfigureAwait(false);
                    await SafeAwait(beat).ConfigureAwait(false);
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
                    try { ws?.Dispose(); } catch { }

                    // ★ 只在自己仍是当前连接时才动共享状态（比身份，不比时间）
                    if (ws is not null && _epoch.TryRetire(epoch, ws))
                        ConnectionStateChanged?.Invoke(new ConnectionStateChangedArgs(
                            ConnectionState.Disconnected, connId, "未连接"));

                    AgentLog.Write($"[WS] CLOSED     connection={connId} code={DescribeCloseCode(closeCode)}");
                }

                // ── 被服务端顶替（code 4000）→ 不重连 ────────────────────
                // hub 的 bind_device 在新实例连上时用 4000 踢掉旧连接，语义是
                // 「另一份实例已经接管了这个 device_id，我不该抢」。以前这里不看
                // 关闭码就重连，于是开机自启的 headless 与登录后的交互式实例互相
                // 顶替、无限对踢（日志里密集的「断开：未连接 / 已连接」），消息还
                // 常常落在没有界面的 headless 上。
                //
                // 现在退出循环，把「什么时候再接管」交给平台层：
                // headless 由 Presence.StartSupervisor 判断（交互式实例不在了才 Start()）。
                // 交互式实例收到 4000 同样不重连 —— 那说明另一个实例抢了设备，
                // 靠对踢解决不了，只把现场写清楚。
                if (closeCode is not null && (int)closeCode.Value == ReplacedCloseCode)
                {
                    AgentLog.Write($"[WS] 被服务端顶替（code={ReplacedCloseCode}）→ 不再重连，交出设备");
                    break;
                }

                if (ct.IsCancellationRequested || !_epoch.IsCurrent(epoch))
                    break;

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_backoff.CurrentSeconds), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                _backoff.Next();
            }
        }
        finally
        {
            // 循环真正结束才释放「在跑」标志；只认自己那一代 —— 接手的
            // 新循环可能已经把自己的 CTS 放进去了，别把它的清掉。
            lock (_lifeGate)
            {
                if (ReferenceEquals(_cts, cts))
                    _cts = null;
            }
        }
    }

    private static async Task SafeAwait(Task task)
    {
        try { await task.ConfigureAwait(false); } catch { /* 心跳任务取消属正常 */ }
    }

    /// <summary>
    /// 会话状态变化（锁屏 / 解锁 / 登录 / 注销）时立即补发一次心跳，
    /// 不必等下一个 15 秒周期 —— 网页端的「远程解锁」可用性就靠这个及时性。
    ///
    /// 只在有连接时发：状态是有时效的，没连接就不发了（不像 ack/reply 那样入队），
    /// 免得重连后补发一条早已过期的旧状态。
    /// </summary>
    public void ReportSessionState()
    {
        var ws = CurrentSocket();
        if (ws is null)
            return;

        _ = _outbox.SendNowAsync(ws,
                HeartbeatLoop.BuildHeartbeatJson(_platform, _capabilities.Capabilities), "heartbeat:state");
    }

    /// <summary>
    /// 回一条 <c>unlock_result</c>（远程解锁 Phase 1）。
    ///
    /// 走和 ack / reply 同一条发送路径：发不出去会入队，重连后补发 ——
    /// 服务端等的是这条应答，丢了它就等于这次解锁请求石沉大海。
    /// </summary>
    public void UnlockResult(string requestId, string status, string reason) =>
        SendOrQueue(new
        {
            type = FrameTypes.UnlockResult,
            request_id = requestId,
            status,
            reason,
        }, $"unlock_result:{status}/{reason}");

    /// <summary>
    /// 收包处理（原 <c>AgentClient.Dispatch</c> 的派发部分；解析已交给
    /// <see cref="FrameCodec"/>，见 §4 的映射）。
    ///
    /// <paramref name="heartbeat"/> 是**本代**的心跳循环实例：hello / heartbeat_ack
    /// 都要重置它的往返看门狗，而它只属于这一代连接（§8.7）。
    /// </summary>
    private void HandleIncoming(string raw, string connId, HeartbeatLoop heartbeat)
    {
        var parsed = FrameCodec.Parse(raw);

        // 没有 type 属性：与原实现一样，什么都不做（连 RX 日志都不记）
        if (parsed.Type is null)
            return;

        // ★ 先落日志、再进下面的分支：message 会走 MessageReceived → 平台层切线程
        //   弹窗，那条链路一旦卡住，没有这行日志就完全看不出「消息到底到了没收」。
        //   带上 connection id 才能分清是哪一代连接收到的（两个实例互踢时全靠它）。
        //   心跳回执每 15 秒一次，记它只会淹掉真正有用的行，跳过。
        if (parsed.Type != FrameTypes.HeartbeatAck)
            AgentLog.Write($"[WS] RX         connection={connId} type={parsed.Type}{parsed.LogExtra}");

        switch (parsed.Frame)
        {
            case HelloFrame hello:
            {
                var token = hello.Token;
                if (!string.IsNullOrEmpty(token) && token != _config.Token)
                {
                    _config.Token = token!;
                    _config.Save();
                    Log?.Invoke("已保存设备令牌");
                }
                Log?.Invoke("已上线");
                heartbeat.NotifyAckReceived();      // 连接刚建立，重置看门狗
                _outbox.ResetFailStreak();
                _outbox.Flush();

                // ★ Phase 4：认证完成对外可见（SyncService 据此进入补齐阶段）。
                //   放在既有处理之后：订阅者抛异常也不会影响 token/心跳/Outbox 那三步。
                try
                {
                    HelloReceived?.Invoke(hello);
                }
                catch (Exception ex)
                {
                    AgentLog.Write("[WS] hello 订阅者抛异常（已忽略）：" + ex.GetType().Name + " " + ex.Message);
                }
                break;
            }

            case MessageFrame message:
                MessageReceived?.Invoke(message);
                break;

            case ScreenshotRequestFrame screenshot:
                // 「拿不到 request_id 就不派发」的判据在 FrameCodec 里：
                // 此时不产出本帧，落到这里自然什么都不做。
                ScreenshotRequested?.Invoke(screenshot);
                break;

            case ReplyAckFrame replyAck:
                ReplyAcked?.Invoke(replyAck);
                break;

            case HistoryFrame history:
                HistoryReceived?.Invoke(history);
                break;

            case ShutdownCommandFrame shutdown:
                // 网页端点了「关机」→ 由平台层执行。
                // ⚠ 「帧里没带 delay_seconds 时用几秒」由平台层决定
                //   （Windows 侧 PowerControl.DefaultDelaySeconds），Core 不认识它。
                ShutdownRequested?.Invoke(shutdown);
                break;

            case UnlockRequestFrame unlock:
                // 远程解锁 Phase 1：交给平台层用 UnlockGuard 校验后回 unlock_result。
                // 校验和应答都不需要桌面，所以 headless（登录界面）下也能走通。
                UnlockRequested?.Invoke(unlock);
                break;

            case HeartbeatAckFrame _:
                // 心跳回执是「发送链路还活着」的唯一证据
                heartbeat.NotifyAckReceived();
                break;

            // ── 共享昵称（§5.4）：整表 / 增量广播 / 错误，三类合成一个事件 ──
            case NicknameListFrame nicknameList:
                NicknameFrameReceived?.Invoke(nicknameList);
                break;

            case NicknameDeltaFrame nicknameDelta:
                NicknameFrameReceived?.Invoke(nicknameDelta);
                break;

            case NicknameErrorFrame nicknameError:
                NicknameFrameReceived?.Invoke(nicknameError);
                break;
        }

        // （收包日志已提前到 switch 之前 —— 见上面的 [WS] RX 行）
    }

    // ---------------- 发送侧（队列在 Outbox 里）----------------

    /// <summary>发送一条消息。返回 true = 已进入发送流程；false = 当前无连接，已入队。</summary>
    public bool SendOrQueue<T>(T payload, string kind) => _outbox.SendOrQueue(payload, kind);

    /// <summary>
    /// **直发**一帧：不预判内容、不进 <see cref="Outbox"/>、不落盘、**不补发**。
    ///
    /// 为什么需要它（docs/NICKNAME-SYSTEM-PLAN.md §5.4 的限制注 / §7 Phase 4 风险 ②）：
    /// 昵称的四个管理操作被 r6 定为「**完全拒绝离线**」，而 <see cref="SendOrQueue{T}"/>
    /// 会在发送失败时入队、并在重连后**静默补发** —— 一次断网时已经被拒的改名
    /// 就会在重连后悄悄生效，直接违反那条定稿。
    /// 所以昵称帧走这条直发路径：**离线时根本不构造请求**，调用方拿到 false 就回界面提示。
    ///
    /// ⚠ 只用于「丢了也无所谓 / 本来就不该离线做」的帧。消息收发仍走
    ///   <see cref="SendOrQueue{T}"/>（那条路的重连补发是既有正确行为，别改）。
    /// </summary>
    public bool TrySendDirect(object payload, string kind)
    {
        var ws = CurrentSocket();
        if (ws is not { State: WebSocketState.Open })
        {
            AgentLog.Write($"[NICK] 无连接，拒绝直发 {kind}（昵称管理要求在线：不排队、不落盘、不补发）");
            return false;
        }

        _ = _outbox.SendNowAsync(ws, JsonSerializer.Serialize(payload), kind);
        return true;
    }

    /// <summary>把积压的消息补发出去（原 <c>FlushOutbox</c>）。</summary>
    public void FlushOutbox() => _outbox.Flush();

    public void Ack(long messageId, string status) =>
        SendOrQueue(new
        {
            type = FrameTypes.Ack,
            message_id = messageId,
            status,
        }, $"ack:{status}");

    /// <summary>把弹窗里的回复发给服务端（昵称由本机本地维护）。
    /// 返回 true = 已尝试发送；false = 无连接，已入队等重连自动补发。</summary>
    /// <param name="nicknameId">
    /// 本机当时选用的**共享昵称** id（`null` = 灰临时）。服务端据此写消息快照
    /// （`sender_nickname_id` / `sender_color`，颜色由服务端查表填）。
    /// 老 exe 不带这个字段 —— 服务端按「老客户端」处理（两列 NULL，走哈希兜底），
    /// 所以这里总是带上（值可能是 null），语义更明确。
    /// </param>
    public bool Reply(string senderName, string content, string clientId, long? nicknameId = null) =>
        SendOrQueue(new
        {
            type = FrameTypes.Reply,
            sender_name = senderName,
            content,
            client_id = clientId,
            nickname_id = nicknameId,
        }, "reply");

    /// <summary>主动拉一次历史对话（从托盘打开对话窗口时用）。</summary>
    public void RequestHistory(int limit = 30) => RequestHistoryTracked(limit);

    /// <summary>
    /// 发一条 <c>history_request</c> 并把 <c>request_id</c> 还给调用方
    /// （<see cref="ISyncConnection"/> 的成员，Phase 4）。
    ///
    /// 为什么需要拿到 id：一条连接上可能同时有两次历史请求 ——
    /// 「用户点开对话窗口」拉的那份（渲染列表用）和「重连补齐」拉的那份（合并去重用）。
    /// 不配对就无法知道 <c>history_response</c> 该归谁，补齐就有把列表数据当补齐数据处理的可能。
    ///
    /// ⚠ 帧形状与 <see cref="RequestHistory"/> 逐字相同（含 <c>request_id</c> 的生成方式），
    ///   只是把 id 提前生成并返回 —— 既有调用方的行为不变。
    /// </summary>
    public string RequestHistoryTracked(int limit)
    {
        var requestId = Guid.NewGuid().ToString("N")[..12];
        SendOrQueue(new
        {
            type = FrameTypes.HistoryRequest,
            request_id = requestId,
            limit,
        }, "history_request");
        return requestId;
    }

    /// <summary>
    /// 回一条 <c>event</c> 帧（<c>{type:"event", kind, detail}</c>）。
    ///
    /// 位置：Phase 3 从宿主里两处内联的匿名对象（<c>App.xaml.cs</c> 的关机回执）
    /// 收进来 —— JSON 形状逐字未变，只是不再各处拼一遍。
    /// </summary>
    public void SendEvent(string kind, string detail) =>
        SendOrQueue(new
        {
            type = FrameTypes.Event,
            kind,
            detail,
        }, "event");

    /// <summary>回传截图。截图有时效性，连不上就丢弃并记日志，不排队。</summary>
    public async Task SendScreenshotAsync(string requestId, string? base64, int width, int height,
                                          string? error)
    {
        var ws = CurrentSocket();
        if (ws is null)
        {
            AgentLog.Write("✗ screenshot_response 无连接对象，丢弃");
            return;
        }

        object payload = string.IsNullOrEmpty(base64)
            ? new
            {
                type = FrameTypes.ScreenshotResponse,
                request_id = requestId,
                error = string.IsNullOrEmpty(error) ? "截图失败" : error,
            }
            : new
            {
                type = FrameTypes.ScreenshotResponse,
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

        await _outbox.SendNowAsync(ws, JsonSerializer.Serialize(payload), kind).ConfigureAwait(false);
    }
}
