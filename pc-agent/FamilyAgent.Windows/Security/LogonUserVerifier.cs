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
/// 用户名可以写 <c>用户</c>、<c>机器名\用户</c> 或 <c>域\用户</c>；
/// 带反斜杠的会自动拆成"域/机器 + 用户名"，不带就当作**本机**账户验
/// （§16.3 决策 1：第一版只承诺本地账户，MSA 尽力）。
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

    /// <summary>把 <c>域\用户</c> / <c>机器\用户</c> / <c>用户</c> 拆成 (域, 用户)。</summary>
    internal static (string? Domain, string Account) SplitUser(string user)
    {
        var trimmed = user.Trim();
        var idx = trimmed.IndexOf('\\');
        if (idx <= 0 || idx == trimmed.Length - 1)
            return (null, trimmed);

        var domain = trimmed.Substring(0, idx);
        var account = trimmed.Substring(idx + 1);
        return (domain, account);
    }
}
