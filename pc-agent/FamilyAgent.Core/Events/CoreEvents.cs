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
///   （命令族信封 + <c>respond</c> 回执回调）—— 它们分别属于 Phase 2（MessageManager
///   接管 ACK 决策）和 Phase 3（CommandRouter 接管命令派发）。Phase 1 是**纯搬运 +
///   依赖倒置**，不合并现有那三个命令事件：合并会动到宿主侧的回调分工，
///   而「行为完全等价」是这一阶段的验收前提（§8.1）。
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
