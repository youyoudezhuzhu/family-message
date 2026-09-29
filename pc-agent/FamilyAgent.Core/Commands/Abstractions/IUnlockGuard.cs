using FamilyAgent.Core.Protocol.Frames;

namespace FamilyAgent.Core.Commands.Abstractions;

/// <summary>
/// 平台能力：**远程解锁**（docs/CORE-REFACTOR-PLAN.md §3.2 的
/// <c>Commands/Abstractions/IUnlockGuard.cs</c>）。
///
/// 它做的是**协议层校验**（归属 → 动作 → 时效 → 一次性重放），几乎不碰 Windows API；
/// 真正的凭据执行（Credential Provider / 凭据存储）将来才需要平台 API。
/// 本轮**只把调用点抽象出来**，实现仍由 Windows 侧包住既有
/// <c>FamilyAgent.UnlockGuard</c>（见 <c>Platform/WindowsUnlockGuard.cs</c>）——
/// 「校验逻辑放 Core 还是放平台层」这个归属问题（§8.12）留给定夺，
/// **既有的校验顺序与应答码一个都没动**。
///
/// ⚠ 与能力上报的关系：本阶段 <c>unlock</c> **仍然不上报**
///   （<c>Devices.DeviceCapabilities</c> 里的开关是 false），
///   所以这个接口在当前版本上只有「应答链路」的作用，不承诺任何可用性。
/// </summary>
public interface IUnlockGuard
{
    /// <summary>
    /// 本机现在**真的能参与远程解锁**吗（凭据已配好 + 未被失败冷却锁住）。
    ///
    /// 它决定 <c>capabilities</c> 里报不报 <c>unlock</c>：
    /// 「有实现」不等于「能用」—— 没配凭据就上报，等于给网页端一个点了必然失败的按钮。
    /// 每次现算（用户随时可能在设置页存/清凭据）。
    /// </summary>
    bool Ready { get; }

    /// <summary>
    /// 校验一条 <c>unlock_request</c>。
    ///
    /// 返回 null 表示这帧连 <c>request_id</c> 都没有 —— 没法应答，也没法记重放缓存，
    /// 调用方只能记日志（与原宿主实现一致）。
    /// </summary>
    UnlockReply? Evaluate(UnlockRequestFrame request, string? myDeviceId);
}
