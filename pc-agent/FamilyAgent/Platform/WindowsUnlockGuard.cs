using System;
using FamilyAgent.Core.Commands;
using FamilyAgent.Core.Commands.Abstractions;
using FamilyAgent.Core.Protocol.Frames;

namespace FamilyAgent.Platform;

/// <summary>
/// Windows 侧的 <see cref="IUnlockGuard"/> 实现
/// （docs/CORE-REFACTOR-PLAN.md §3.2 / §4；Phase 2 起接凭据）。
///
/// 校验链分两段，**顺序不能换**：
/// <list type="number">
///   <item><b>协议校验</b>（归属 → 动作 → 时效 → 一次性重放）仍在既有的
///         <c>FamilyAgent.UnlockGuard</c> 里，一个字没动 —— 归属问题 §8.12 仍待拍板；</item>
///   <item><b>凭据是否就绪</b>（Phase 2 新增）：协议过了以后，看本机有没有可用凭据 ——
///         有就回 <c>armed/ok</c>（凭据就绪、等 Credential Provider 真正施加解锁），
///         没有就回 <c>failed/no_credential</c>（与原行为一致）。</item>
/// </list>
///
/// ⚠ 回 <c>armed</c> **不等于解锁成功**：Phase 2 只做到"凭据安全存好、请求可达、状态可见"，
///   真正把锁解开要等 Phase 3 的 Credential Provider。协议词汇表是冻结的
///   （<see cref="UnlockReply"/>），所以这里不新增任何状态码 —— <c>armed</c> 的既有定义
///   正是"校验通过、等待"，语义对得上。
///
/// ⚠ <c>unlock</c> 能力只在 <see cref="Ready"/> 为真时上报（Phase 2 起）：
///   没配凭据就上报，网页端会给出一个点了必然失败的按钮。
/// </summary>
public sealed class WindowsUnlockGuard : IUnlockGuard
{
    private readonly Func<bool> _ready;

    /// <param name="ready">"凭据已配好且未冷却"的现算来源（一般传 <c>UnlockCredentials.Ready</c>）。</param>
    public WindowsUnlockGuard(Func<bool>? ready = null)
    {
        _ready = ready ?? (() => false);   // 没接线时保持"不能解锁"= 今天的行为
    }

    /// <inheritdoc />
    public bool Ready
    {
        get
        {
            try
            {
                return _ready();
            }
            catch (Exception)
            {
                return false;   // 状态查询出问题一律按"不能解锁"处理，绝不上报假能力
            }
        }
    }

    /// <inheritdoc />
    public UnlockReply? Evaluate(UnlockRequestFrame request, string? myDeviceId)
    {
        var reply = UnlockGuard.Evaluate(request.Raw, myDeviceId);
        if (reply is null)
            return null;

        // 协议校验没过（过期 / 重放 / 不是给我的 / 动作不对）：原样回，一个字不改
        if (reply.Reason != UnlockReply.ReasonNoCredential)
            return reply;

        // 协议过了：凭据就绪 → armed（等 Phase 3 施加解锁）；没就绪 → 保持 no_credential
        return Ready
            ? new UnlockReply(reply.RequestId, UnlockReply.StatusArmed, UnlockReply.ReasonOk)
            : reply;
    }
}
