using FamilyAgent.Core.Messaging;
using FamilyAgent.Core.Transport;

namespace FamilyAgent.Core.Events;

/// <summary>
/// Core 对外事件契约的载荷类型（docs/CORE-REFACTOR-PLAN.md §3.3）。
///
/// Phase 1 落地的部分（事件本身挂在 <c>Transport.ConnectionManager</c> 上）：
///
/// <list type="table">
///   <item>
///     <term>ConnectionStateChanged</term>
///     <description><see cref="ConnectionStateChangedArgs"/>（取代原
///       <c>ConnectionChanged(bool, string)</c>：连接状态从裸 bool 变成枚举 +
///       connection_id + 中文说明）</description>
///   </item>
///   <item>
///     <term>MessageReceived</term>
///     <description><c>Protocol.Frames.MessageFrame</c>（取代裸 <c>JsonElement</c>）</description>
///   </item>
///   <item>
///     <term>ReplyAcked</term>
///     <description><c>Protocol.Frames.ReplyAckFrame</c>（取代裸 <c>JsonElement</c>）</description>
///   </item>
///   <item>
///     <term>HistoryReceived</term>
///     <description><c>Protocol.Frames.HistoryFrame</c>（取代裸 <c>JsonElement</c>）</description>
///   </item>
///   <item>
///     <term>ScreenshotRequested / ShutdownRequested / UnlockRequested</term>
///     <description>各自的强类型帧 —— 原实现分别是 <c>string</c> / <c>int</c> / 裸
///       <c>JsonElement</c></description>
///   </item>
/// </list>
///
/// ⚠ §3.3 里还有 <c>MessageDeliveryStateChanged</c> 与统一的 <c>CommandReceived</c>
///   （命令族信封 + <c>respond</c> 回执回调）：<c>MessageDeliveryStateChanged</c> 已在
///   **Phase 2** 落地（见下面的 <see cref="MessageDeliveryStateChangedArgs"/>，由
///   <c>Messaging.MessageManager</c> 抛出）；统一的 <c>CommandReceived</c> 仍属 Phase 3
///   （CommandRouter 接管命令派发）。Phase 1 是**纯搬运 + 依赖倒置**，不合并现有那三个
///   命令事件：合并会动到宿主侧的回调分工，而「行为完全等价」是那一阶段的验收前提（§8.1）。
/// </summary>
public sealed class ConnectionStateChangedArgs
{
    public ConnectionStateChangedArgs()
    {
    }

    public ConnectionStateChangedArgs(ConnectionState state, string connectionId, string detail)
    {
        State = state;
        ConnectionId = connectionId;
        Detail = detail;
    }

    /// <summary>当前连接状态。Phase 1 只会发出 Connected 与 Disconnected
    /// （原实现也只有这两个上报点：连上 / 退出时按代次清理）。</summary>
    public ConnectionState State { get; init; }

    /// <summary>这条状态属于哪一代连接的 id（完整 GUID，见 §Phase 1-2）。
    /// 两个实例互踢时，日志与状态靠它对齐。</summary>
    public string ConnectionId { get; init; } = "";

    /// <summary>给日志/界面看的中文说明（原来那个 string 参数，值未变：「已连接」/「未连接」）。</summary>
    public string Detail { get; init; } = "";

    /// <summary>等价于原 <c>ConnectionChanged</c> 的 <c>bool connected</c> 参数。</summary>
    public bool Connected => State == ConnectionState.Connected;
}

/// <summary>
/// 一条消息的本地生命周期变化（§3.3 的 <c>MessageDeliveryStateChanged</c>，
/// Phase 2 由 <c>Messaging.MessageManager</c> 抛出）。
///
/// 它同时承担了两件事：
/// <list type="number">
///   <item><b>收到的消息</b>的显示/已读推进 —— 取代原 <c>WebHostWindow.MessageAcked</c> →
///     <c>App.xaml.cs:302-305</c> 直接发 <c>ack</c> 的做法（决策搬进 Core）；</item>
///   <item><b>本机回复</b>被服务端受理（<c>reply_ack</c>）—— 那条路径仍是
///     <c>ConnectionManager.ReplyAcked</c>，Phase 2 未动。</item>
/// </list>
///
/// <see cref="DeliveryState.AckSent"/> 只在这个事件流里出现（不落盘）：用它把
/// 「显示了」和「ack 已交出去」分开看得见 —— 出问题时能一眼判断题在哪一段。
/// </summary>
public sealed class MessageDeliveryStateChangedArgs
{
    public MessageDeliveryStateChangedArgs(long messageId, DeliveryState state, string detail)
    {
        MessageId = messageId;
        State = state;
        Detail = detail;
    }

    /// <summary>服务端 <c>message_id</c>（本机记账主键）。</summary>
    public long MessageId { get; }

    /// <summary>变化后的状态。</summary>
    public DeliveryState State { get; }

    /// <summary>给日志/界面看的中文说明（例如「已落盘（进程崩溃也不会丢）」「ack:popup_displayed」）。</summary>
    public string Detail { get; }
}
