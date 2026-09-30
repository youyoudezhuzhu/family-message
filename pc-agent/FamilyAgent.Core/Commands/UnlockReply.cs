namespace FamilyAgent.Core.Commands;

/// <summary>
/// 一条 <c>unlock_request</c> 的校验结论，也就是要回给服务端的应答内容。
///
/// 位置：Phase 3 从宿主 <c>pc-agent/FamilyAgent/UnlockGuard.cs</c> 搬进 Core
/// （见 docs/CORE-REFACTOR-PLAN.md §4 的映射表第 430 行：<c>UnlockGuard.cs</c> 的归属
/// 仍待拍板 §8.12 —— **校验逻辑**这轮仍留在宿主，只有「应答的数据形状」提前上移到 Core，
/// 因为命令派发（<see cref="CommandRouter"/>）需要通过
/// <see cref="Abstractions.IUnlockGuard"/> 拿到它，而 Core 不能引用宿主）。
///
/// 同时也是**协议词汇表的唯一权威定义**：状态与原因码与 NAS 侧冻结
/// （<c>server/services/devices.py</c> / <c>mobile-channel</c> 的解锁链路），
/// 改字面量等于改协议。宿主 <c>UnlockGuard</c> 的公开常量现在引用这里，不再各写一份。
/// </summary>
public sealed class UnlockReply
{
    public UnlockReply(string requestId, string status, string reason)
    {
        RequestId = requestId;
        Status = status;
        Reason = reason;
    }

    // ── 应答码（与 NAS 侧冻结的协议一致，勿改字面量）──────────────────

    /// <summary>校验通过、等待（本阶段不会用到：还没有凭据可以「武装」）。</summary>
    public const string StatusArmed = "armed";

    /// <summary>解锁成功。</summary>
    public const string StatusSuccess = "success";

    /// <summary>失败（本阶段所有请求都是它）。</summary>
    public const string StatusFailed = "failed";

    public const string ReasonExpired = "expired";
    public const string ReasonReplay = "replay";
    public const string ReasonNotMine = "not_mine";
    public const string ReasonBadAction = "bad_action";

    /// <summary>本机没有可用的解锁凭据（本阶段的终态：凭据存储 / Credential Provider 都还没做）。</summary>
    public const string ReasonNoCredential = "no_credential";

    public const string ReasonCpError = "cp_error";

    /// <summary>
    /// 凭据已就绪，但本机**还没有能真正施加解锁的组件**（Phase 3 的 Credential Provider 未安装）。
    ///
    /// 新增这个取值而不是复用 <c>armed</c>：<c>armed</c> 的语义是"校验通过、等待解锁完成"，
    /// 而 Phase 2 阶段既没有组件去完成它，回 armed 只会让网页端一直等、最后超时 ——
    /// 用户看到的是"没反应"。如实回这个码，界面就能说清"请求到了、凭据有、缺组件"。
    /// </summary>
    public const string ReasonCpMissing = "cp_missing";

    public const string ReasonTimeout = "timeout";
    public const string ReasonOk = "ok";

    public string RequestId { get; }

    /// <summary>armed | success | failed</summary>
    public string Status { get; }

    /// <summary>expired | replay | not_mine | bad_action | no_credential | cp_missing | cp_error | timeout | ok</summary>
    public string Reason { get; }
}
