using System;
using FamilyAgent.Core.Commands;
using FamilyAgent.Core.Commands.Abstractions;
using FamilyAgent.Core.Diagnostics;
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
    private readonly Func<bool> _canApply;
    private readonly Func<string, string, bool>? _arm;

    /// <param name="ready">"凭据已配好且未冷却"的现算来源（一般传 <c>UnlockCredentials.Ready</c>）。</param>
    /// <param name="canApply">
    /// "本机现在真的能把锁解开吗"的现算来源 —— 即 Phase 3 的 Credential Provider 是否已安装
    /// （一般传 <c>CredentialProviderProbe.IsInstalled</c>）。默认 false：
    /// 没有它就只能如实回 <c>cp_missing</c>，不能回 <c>armed</c> 让网页端白等。
    /// </param>
    /// <param name="arm">
    /// 决定回 <c>armed</c> 时写"一次性 arm 凭证"（<c>(requestId, nonce) =&gt; 写好没有</c>），
    /// CP 会来消费它。写不进去就不能回 armed —— 那会变成"界面说正在解锁、其实没人动"，
    /// 所以返回 false 时如实回 <c>cp_error</c>。
    /// </param>
    public WindowsUnlockGuard(Func<bool>? ready = null, Func<bool>? canApply = null,
                              Func<string, string, bool>? arm = null)
    {
        _ready = ready ?? (() => false);   // 没接线时保持"不能解锁"= 今天的行为
        _canApply = canApply ?? (() => false);
        _arm = arm;
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

        // 协议过了：分三种如实回，**不谎报**
        //   ① 凭据没就绪                → no_credential（界面："PC 尚未配置解锁凭据"）
        //   ② 凭据就绪但缺解锁组件       → cp_missing（界面："请求到了、凭据有、缺组件"）
        //   ③ 两者都有                  → armed（等 Credential Provider 完成解锁）
        if (!Ready)
            return reply;

        if (!CanApply)
            return new UnlockReply(reply.RequestId, UnlockReply.StatusFailed, UnlockReply.ReasonCpMissing);

        // 要回 armed 了 —— 必须先把一次性 arm 凭证写下去，否则 CP 不会动。
        // 写不进去就如实说 cp_error（"界面说正在解锁、其实没人动"是最坏的体验）。
        var nonce = UnlockGuard.ReadStringValue(request.Raw, "nonce") ?? "";
        if (_arm is null || !_arm(reply.RequestId, nonce))
        {
            AgentLog.Write($"✗ 解锁请求 {reply.RequestId} 无法写入 arm 凭证 → cp_error");
            return new UnlockReply(reply.RequestId, UnlockReply.StatusFailed, UnlockReply.ReasonCpError);
        }

        AgentLog.Write($"✓ 解锁请求 {reply.RequestId} 已武装（arm 凭证已写，等 CP 消费）");
        return new UnlockReply(reply.RequestId, UnlockReply.StatusArmed, UnlockReply.ReasonOk);
    }

    /// <summary>
    /// 本机现在真能把锁解开（Phase 3 组件在不在）。**不影响能力上报** ——
    /// 能力位描述的是"这台机器能不能参与解锁"，而 cp_missing 恰恰是"参与了但差最后一环"，
    /// 用户需要看到这句话，所以入口照常给。
    /// </summary>
    public bool CanApply
    {
        get
        {
            try
            {
                return _canApply();
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
