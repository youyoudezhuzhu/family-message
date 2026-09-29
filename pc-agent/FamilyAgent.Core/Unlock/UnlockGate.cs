using System;
using System.Collections.Generic;

namespace FamilyAgent.Core.Unlock;

/// <summary>
/// 「这个 Windows 账户 + 口令能不能登录」这件事的抽象（真机实现走 <c>LogonUser</c>）。
///
/// ⚠ 它**只验证，不登录**：`LOGON32_LOGON_NETWORK` 这类验证型登录不会建立交互会话，
///   所以「测试凭据」按钮不会把用户的桌面顶掉（§16.2 那条「不真登录」的判据）。
/// </summary>
public interface ILogonVerifier
{
    /// <summary>验证用户名 + 口令。实现不抛异常（内部失败一律返回 false）。</summary>
    bool Verify(string user, string secret);
}

/// <summary>一次校验的结果。</summary>
public enum UnlockCheckResult
{
    /// <summary>口令对，凭据可用。</summary>
    Ok,

    /// <summary>口令不对（已记一次失败）。</summary>
    BadCredential,

    /// <summary>失败次数超限，暂时不接受校验（防爆破）。</summary>
    LockedOut,

    /// <summary>压根没配过凭据。</summary>
    NoCredential,
}

/// <summary>
/// 凭据校验的闸门：**失败计数 + 冷却窗口**（docs/REMOTE-UNLOCK-PLAN.md §16.3 决策 5）。
///
/// 规则：连续 <see cref="MaxFailures"/> 次失败后的 <see cref="LockoutWindow"/> 内不再接受校验；
/// 窗口过后计数清零、重新允许。成功则立即清零并记一次 <c>verified_at</c>。
///
/// 为什么要它：凭据是"能登录这台机器的东西"，设置页的「测试凭据」如果可以被无限次尝试，
/// 就等于给了一条离线爆破通道。**失败原因只写本地审计，不上行细节**（上行只说"失败"）。
/// </summary>
public sealed class UnlockGate
{
    /// <summary>连续失败多少次后进入冷却。</summary>
    public const int MaxFailures = 3;

    /// <summary>冷却窗口长度。</summary>
    public static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(10);

    private readonly CredentialVault _vault;
    private readonly ILogonVerifier _verifier;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<string>? _audit;
    private readonly List<DateTimeOffset> _failures = new();

    /// <param name="vault">凭据保险箱（成功时记 <c>verified_at</c>）。</param>
    /// <param name="verifier">平台校验实现（真机 = LogonUser）。</param>
    /// <param name="now">时钟（可注入，测试里用来跨过冷却窗口）。</param>
    /// <param name="audit">审计回调（真机写 <c>%APPDATA%\FamilyAgent\unlock-audit.log</c>）。</param>
    public UnlockGate(CredentialVault vault, ILogonVerifier verifier,
                      Func<DateTimeOffset>? now = null, Action<string>? audit = null)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _now = now ?? (() => DateTimeOffset.Now);
        _audit = audit;
    }

    /// <summary>当前是否处于冷却中（只读，不改变状态）。</summary>
    public bool IsLockedOut => RemainingFailures.Count >= MaxFailures;

    /// <summary>窗口内的失败次数（冷却判断只看窗口内的）。</summary>
    public int FailureCount => RemainingFailures.Count;

    private List<DateTimeOffset> RemainingFailures
    {
        get
        {
            var cutoff = _now() - LockoutWindow;
            _failures.RemoveAll(t => t < cutoff);
            return _failures;
        }
    }

    /// <summary>
    /// 校验一次凭据。
    ///
    /// ⚠ 语义先写清楚：本方法校验的是**调用方递进来的**用户名 + 口令（设置页的「测试凭据」、
    ///   或定时复查）；真正被远程解锁请求用到的口令，是保险箱里那份
    ///   （<see cref="CredentialVault.Load"/>），不会经网络传递。
    /// </summary>
    public UnlockCheckResult Check(string user, string secret)
    {
        if (RemainingFailures.Count >= MaxFailures)
        {
            _audit?.Invoke($"locked_out user={user} failures={RemainingFailures.Count}");
            return UnlockCheckResult.LockedOut;
        }

        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(secret))
        {
            RecordFailure(user);
            return UnlockCheckResult.BadCredential;
        }

        bool ok;
        try
        {
            ok = _verifier.Verify(user, secret);
        }
        catch (Exception)
        {
            ok = false;      // 校验实现出问题按「不对」处理，绝不因此放行
        }

        if (!ok)
        {
            RecordFailure(user);
            return UnlockCheckResult.BadCredential;
        }

        _failures.Clear();
        _vault.MarkVerified(_now().ToString("yyyy-MM-dd HH:mm:ss"));
        _audit?.Invoke($"ok user={user}");
        return UnlockCheckResult.Ok;
    }

    /// <summary>凭据已配好、且不在冷却中 → 可以接受远程解锁请求。</summary>
    public bool ReadyForRemoteUnlock =>
        _vault.HasCredential && !IsLockedOut;

    private void RecordFailure(string user)
    {
        var n = RemainingFailures.Count + 1;
        _failures.Add(_now());
        _audit?.Invoke($"bad_credential user={user} attempt={n}");
    }
}
