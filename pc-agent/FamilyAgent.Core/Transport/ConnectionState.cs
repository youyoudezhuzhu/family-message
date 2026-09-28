namespace FamilyAgent.Core.Transport;

/// <summary>
/// WebSocket 连接的四个状态（docs/CORE-REFACTOR-PLAN.md §3.2）。
///
/// Phase 1 只会发出 <see cref="Connected"/> 与 <see cref="Disconnected"/>：
/// 原实现的 <c>ConnectionChanged(bool, string)</c> 也只有这两个上报点
/// （连上 / 退出时按代次清理）。另外两个取值留给 Phase 2 起把状态机显式化时用
/// （<c>Connecting</c> 覆盖「已创建对象、还没握手完」，<c>Closing</c> 覆盖主动断开），
/// 这一阶段不引入新状态、不改行为。
/// </summary>
public enum ConnectionState
{
    /// <summary>没有连接对象（或已按代次清理干净）。</summary>
    Disconnected,

    /// <summary>正在握手（Phase 2 起使用）。</summary>
    Connecting,

    /// <summary>连接可用（已握手成功）。</summary>
    Connected,

    /// <summary>正在关闭（Phase 2 起使用）。</summary>
    Closing,
}
