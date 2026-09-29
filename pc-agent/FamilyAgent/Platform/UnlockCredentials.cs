using System;
using System.IO;
using FamilyAgent.Core.Unlock;
using FamilyAgent.Windows.Secrets;
using FamilyAgent.Windows.Security;

namespace FamilyAgent.Platform;

/// <summary>
/// PC 端「远程解锁凭据」的门面（docs/REMOTE-UNLOCK-PLAN.md §16.2 交付物之一）：
/// 把 Core 的保险箱 + 校验闸门 + Windows 真实现（DPAPI / LogonUser）拼起来，
/// 并负责审计落盘。设置页的「凭据」卡与解锁请求链路都只跟它打交道。
///
/// 几条刻意的取舍：
/// <list type="bullet">
///   <item><b>先验证再保存</b>：<see cref="Save"/> 会先 <c>LogonUser</c> 校验一次，
///         口令不对**不落盘**并如实回复 —— 否则 capability 会据"有凭据"上报 <c>unlock</c>，
///         而真到解锁时才发现口令是错的（计划 §16.2 的措辞是"已配置**且校验通过**"）。</item>
///   <item><b>LocalMachine 作用域 + 文件 ACL</b>：Phase 2 的 Service 是另一个账户，
///         CurrentUser 作用域它读不到；LocalMachine 作用域"本机都能解"，所以必须配 ACL
///         把文件收到 SYSTEM + Administrators + 当前交互用户（§16.3 决策 3）。</item>
///   <item><b>凭据值只进不出</b>：本门面**没有任何**把口令读出来往外给的方法 ——
///         唯一读口令的地方是解锁链路内部（Phase 3 交给 Credential Provider）。</item>
///   <item><b>审计只记事实</b>：谁、什么时候、结果如何；**不记口令**。</item>
/// </list>
/// </summary>
public sealed class UnlockCredentials
{
    private readonly CredentialVault _vault;
    private readonly UnlockGate _gate;
    private readonly ILogonVerifier _verifier;
    private readonly Action<string> _log;

    private static UnlockCredentials? _current;
    private static readonly object CurrentGate = new();

    /// <summary>
    /// 进程内单例（用默认路径 + 宿主日志）。凭据是**机器级**的状态，一个进程里只该有一份；
    /// 桥（设置页操作）与解锁校验链都取它，省掉"谁先构造"的时序问题。
    /// </summary>
    public static UnlockCredentials Current
    {
        get
        {
            lock (CurrentGate)
            {
                return _current ??= new UnlockCredentials(log: Core.Diagnostics.AgentLog.Write);
            }
        }
    }

    /// <param name="log">宿主日志出口（同时写审计文件）。</param>
    /// <param name="verifier">校验实现（默认 Windows <c>LogonUser</c>）。</param>
    public UnlockCredentials(Action<string>? log = null, ILogonVerifier? verifier = null)
        : this(DefaultCredentialPath, log, verifier)
    {
    }

    public UnlockCredentials(string credentialPath, Action<string>? log = null, ILogonVerifier? verifier = null)
    {
        _verifier = verifier ?? new LogonUserVerifier();
        _log = log ?? (_ => { });
        _vault = new CredentialVault(credentialPath, DpapiSecretProtector.ForLocalMachine());
        _gate = new UnlockGate(_vault, _verifier, audit: Audit);
    }

    /// <summary>凭据文件：<c>%ProgramData%\FamilyAgent\credential.json</c>（机器级，Service 也能读）。</summary>
    public static string DefaultCredentialPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "FamilyAgent", "credential.json");

    /// <summary>审计文件：<c>%APPDATA%\FamilyAgent\unlock-audit.log</c>。</summary>
    public static string AuditPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FamilyAgent", "unlock-audit.log");

    /// <summary>
    /// 本机现在真的能参与远程解锁吗（凭据已配 + 未被失败冷却锁住）。
    /// 直接喂给 <c>DeviceCapabilities</c>：为 false 时 <c>unlock</c> 不上报。
    /// </summary>
    public bool Ready => _vault.HasCredential && !_gate.IsLockedOut;

    /// <summary>设置页要显示的状态（**不含口令**）。</summary>
    public UnlockCredentialStatus Status()
    {
        var user = (string?)null;
        var verifiedAt = (string?)null;
        try
        {
            var rec = File.Exists(_vault.Path)
                ? System.Text.Json.JsonSerializer.Deserialize<CredentialRecord>(File.ReadAllText(_vault.Path))
                : null;
            user = rec?.User;
            verifiedAt = rec?.VerifiedAt;
        }
        catch (Exception)
        {
            // 文件坏了：状态显示成"未配置"，用户重新存一次即可
        }

        return new UnlockCredentialStatus(
            Configured: _vault.HasCredential,
            User: user,
            VerifiedAt: verifiedAt,
            LockedOut: _gate.IsLockedOut,
            FailuresLeft: Math.Max(0, UnlockGate.MaxFailures - _gate.FailureCount));
    }

    /// <summary>
    /// 保存凭据：**先校验**（<c>LogonUser</c>，验证型登录不会顶掉桌面），通过了才落盘 + 收 ACL。
    /// 口令不对 / 冷却中 → 不保存，如实返回原因。
    /// </summary>
    public (bool Ok, string Detail) Save(string user, string secret)
    {
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(secret))
            return (false, "用户名和口令都要填。");

        var check = _gate.Check(user, secret);
        if (check == UnlockCheckResult.LockedOut)
            return (false, $"连续失败次数过多，已暂停校验（{UnlockGate.LockoutWindow.TotalMinutes:0} 分钟后再试）。");
        if (check != UnlockCheckResult.Ok)
            return (false, "口令校验没通过，没有保存（再试一次，或确认用户名写成 机器名\\用户名）。");

        _vault.Store(user, secret, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        var aclOk = CredentialFileAcl.Restrict(_vault.Path, CredentialFileAcl.CurrentUserIdentity());
        Audit($"saved user={user} acl={(aclOk ? "ok" : "failed")}");
        return (true, aclOk
            ? "已保存并校验通过。"
            : "已保存并校验通过（但文件权限没设成功，已记审计）。");
    }

    /// <summary>测试凭据：只校验，不落盘（用于"我记不清口令对不对"）。</summary>
    public (bool Ok, string Detail) Test(string user, string secret)
    {
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(secret))
            return (false, "用户名和口令都要填。");

        var check = _gate.Check(user, secret);
        return check switch
        {
            UnlockCheckResult.Ok => (true, "校验通过。"),
            UnlockCheckResult.LockedOut =>
                (false, $"连续失败次数过多，已暂停校验（{UnlockGate.LockoutWindow.TotalMinutes:0} 分钟后再试）。"),
            _ => (false, "口令校验没通过（注意用户名格式：本机账户写 机器名\\用户名）。"),
        };
    }

    /// <summary>清除凭据（设置页按钮 + 卸载时调用）。</summary>
    public bool Clear()
    {
        var removed = _vault.Clear();
        Audit($"cleared removed={removed}");
        return removed;
    }

    private void Audit(string line)
    {
        var text = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {line}";
        _log("[UNLOCK] " + line);
        try
        {
            var dir = Path.GetDirectoryName(AuditPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.AppendAllText(AuditPath, text + Environment.NewLine);
        }
        catch (Exception)
        {
            // 审计写不进去不影响功能（日志里已经有一条了）
        }
    }
}

/// <summary>设置页显示用的状态（**只有"配了没 / 谁 / 什么时候验的"这些事实，没有口令**）。</summary>
public sealed record UnlockCredentialStatus(
    bool Configured,
    string? User,
    string? VerifiedAt,
    bool LockedOut,
    int FailuresLeft);
