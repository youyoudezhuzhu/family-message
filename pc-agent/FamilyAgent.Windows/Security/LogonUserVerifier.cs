using System;
using FamilyAgent.Core.Unlock;
using FamilyAgent.Windows.Secrets;

namespace FamilyAgent.Windows.Security;

/// <summary>
/// <see cref="ILogonVerifier"/> 的 Windows 真实现：`LogonUser`。
///
/// ⚠ 关键在**登录类型**（docs/REMOTE-UNLOCK-PLAN.md §16.2）：
/// <list type="bullet">
///   <item><c>LOGON32_LOGON_NETWORK</c>（3）：**只验证**，不建交互会话、不加载用户配置文件 ——
///         点「测试凭据」不会把用户正在用的桌面顶掉（这是那一步最容易踩的坑，
///         用 <c>LOGON32_LOGON_INTERACTIVE</c> 就会真登录）。</item>
///   <item>验证成功只拿一个 token，立刻 <c>CloseHandle</c> 关掉，不做任何事。</item>
/// </list>
///
/// 用户名三种写法都认（<see cref="SplitUser"/>）：
/// <list type="number">
///   <item><c>机器名\账户名</c> 或 <c>域\账户名</c> —— 原样拆成"域 + 账户"（<c>whoami</c> 打印的就是这种，最稳）；</item>
///   <item><c>账户名</c>（不带反斜杠）—— 当作**本机**账户验（§16.3 决策 1 的第一版承诺）；</item>
///   <item><c>邮箱地址</c>（含 <c>@</c>）—— 当作**微软账户**，用 Windows 的虚拟域
///         <c>MicrosoftAccount</c> 走云端验证。这是 MSA 机器的正路：直接拿"机器名 + 邮箱"
///         去验必然失败（本地没有账户叫邮箱），而 MSA 账户在本地同时也有一个由
///         Windows 生成/继承的账户名（<c>whoami</c> 可见），两种写法都值得先试一次。</item>
/// </list>
///
/// ⚠ 微软账户的现实限制（写进文档，免得用户以为是自己填错了）：
/// MSA 登录的机器上，能可靠本地验证的通常是**那个本地账户名 + 账户密码**；
/// 如果用户平时用 **PIN / Windows Hello** 登录，本地一般没有可用于
/// <c>LogonUser</c> 的密码材料 —— 这种情况下"用口令远程解锁"这条路本身就不通，
/// 正解是 Phase 3 的 Credential Provider（它拿的是登录界面上用户实际输入的东西）。
/// </summary>
public sealed class LogonUserVerifier : ILogonVerifier
{
    private const int LOGON32_LOGON_NETWORK = 3;
    private const int LOGON32_PROVIDER_DEFAULT = 0;

    /// <summary>本机计算机名（不带反斜杠时作为域传给 LogonUser）。</summary>
    private readonly string _machineName;

    public LogonUserVerifier(string? machineName = null)
    {
        _machineName = string.IsNullOrWhiteSpace(machineName)
            ? Environment.MachineName
            : machineName!;
    }

    /// <summary>最近一次失败的原因（审计用；成功的调用不会改它）。</summary>
    public string? LastFailureReason { get; private set; }

    public bool Verify(string user, string secret)
    {
        LastFailureReason = null;

        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(secret))
        {
            LastFailureReason = "用户名或口令为空";
            return false;
        }
        if (!OperatingSystem.IsWindows())
        {
            LastFailureReason = "非 Windows 平台";
            return false;
        }

        var (domain, account) = SplitUser(user);
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!NativeMethods.LogonUser(account, domain ?? _machineName, secret,
                                         LOGON32_LOGON_NETWORK, LOGON32_PROVIDER_DEFAULT, out token))
            {
                // 1326 = 用户名或密码错误；1327 = 账户限制（空密码 / 登录时段）；
                // 1907 = 必须先改密码。**只记码，不记口令**。
                LastFailureReason = $"LogonUser 失败：{NativeMethods.LastError()}";
                return false;
            }

            // 只验证不登录：拿到 token 也立刻还回去
            return true;
        }
        catch (Exception ex)
        {
            // 平台出问题一律按"不对"处理 —— 绝不放行（Core 的闸门也是这个态度）
            LastFailureReason = $"LogonUser 异常：{ex.GetType().Name}";
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero)
                NativeMethods.CloseHandle(token);
        }
    }

    /// <summary>
    /// 把用户名拆成 (域, 账户)。三种写法：
    /// <list type="bullet">
    ///   <item><c>域\账户</c>（含 <c>机器名\账户</c>）→ 原样拆开；</item>
    ///   <item>含 <c>@</c> 的邮箱 → 用 Windows 的微软账户虚拟域 <c>MicrosoftAccount</c>；</item>
    ///   <item>其它 → 当作本机账户（域 = 机器名）。</item>
    /// </list>
    /// ⚠ 顺序不能反：邮箱里没有反斜杠，但反斜杠形式里也可能出现邮箱
    /// （<c>MicrosoftAccount\user@outlook.com</c>），所以先看反斜杠。
    /// </summary>
    internal static (string? Domain, string Account) SplitUser(string user)
    {
        var trimmed = user.Trim();
        var idx = trimmed.IndexOf('\\');
        if (idx > 0 && idx < trimmed.Length - 1)
        {
            var domain = trimmed.Substring(0, idx);
            var account = trimmed.Substring(idx + 1);
            return (domain, account);
        }

        // 邮箱 = 微软账户（本地没有叫这个的账户，必须走 MicrosoftAccount 虚拟域）
        if (trimmed.IndexOf('@') > 0)
            return (MsaDomain, trimmed);

        return (null, trimmed);
    }

    /// <summary>微软账户在 Windows 上的虚拟域（`LogonUser` 用它走云端验证）。</summary>
    internal const string MsaDomain = "MicrosoftAccount";
}
