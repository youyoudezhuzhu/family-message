using FamilyAgent.Core.Commands;
using FamilyAgent.Core.Commands.Abstractions;
using FamilyAgent.Core.Protocol.Frames;

namespace FamilyAgent.Platform;

/// <summary>
/// Windows 侧的 <see cref="IUnlockGuard"/> 实现
/// （docs/CORE-REFACTOR-PLAN.md §3.2 / §4）。
///
/// 本类**只是调用点**：真正的校验（归属 → 动作 → 时效 → 一次性重放）
/// 与重放缓存的落盘仍在既有的 <c>FamilyAgent.UnlockGuard</c> 里，一个字没动 ——
/// 「校验逻辑放 Core 还是放平台层」这个归属问题（§8.12）**仍待拍板**，
/// Phase 3 只把它包成 Core 能调用的形状，好让命令派发（<c>Core/Commands/CommandRouter.cs</c>）
/// 对三种命令的处理方式一致。
///
/// 将来若要按 §8.12 的建议把纯协议校验上移进 Core，删除本类即可
/// （Core 侧接口与派发代码都不用动）。
///
/// ⚠ 本阶段 <c>unlock</c> 能力**仍然不上报**（<c>DeviceCapabilities</c> 里的开关是 false）：
///   能不能校验 ≠ 能不能解锁 —— 凭据存储 / Credential Provider 都还没做，
///   上报了就是谎报（网页端会给出一个点了必然失败的按钮）。
/// </summary>
public sealed class WindowsUnlockGuard : IUnlockGuard
{
    /// <inheritdoc />
    public UnlockReply? Evaluate(UnlockRequestFrame request, string? myDeviceId) =>
        UnlockGuard.Evaluate(request.Raw, myDeviceId);
}
