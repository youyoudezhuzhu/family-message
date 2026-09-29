using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace FamilyAgent.Windows.Secrets;

/// <summary>
/// 把凭据文件的 ACL 收到「只有该看的人能看」（docs/REMOTE-UNLOCK-PLAN.md §16.2 第 1 条）。
///
/// 为什么必须有它：DPAPI 用 <c>LocalMachine</c> 作用域时，"同机任何账户都能解开"，
/// 所以**文件权限就是第二道门** —— 只留 SYSTEM + Administrators（+ 可选的服务/交互账户）。
/// 用 <c>CurrentUser</c> 作用域时也照样收一遍：多一层不吃亏。
///
/// 实现要点：<b>断开继承</b>（<c>SetAccessRuleProtection(true, false)</c>），
/// 否则从父目录继承来的 Everyone / Users 读权限会把这道门直接架空。
/// </summary>
public static class CredentialFileAcl
{
    /// <summary>
    /// 按「SYSTEM + Administrators + 可选额外账户」设权限：**先断继承，再只留白名单**。
    /// 文件不存在时返回 false（由调用方决定要不要先创建）。
    /// </summary>
    /// <param name="path">凭据文件路径。</param>
    /// <param name="extraIdentities">
    /// 额外允许的账户（写权限 + 读权限）。例如 PC 端要让**当前交互用户**也能读写
    /// （Service 用 LocalMachine 加密、交互进程用 LocalMachine 解密，两边都得能访问文件）。
    /// </param>
    public static bool Restrict(string path, params IdentityReference?[] extraIdentities)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        if (!File.Exists(path))
            return false;

        try
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            Allow(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
            Allow(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            if (extraIdentities is not null)
            {
                foreach (var id in extraIdentities)
                    if (id is not null)
                        Allow(security, id);
            }

            new FileInfo(path).SetAccessControl(security);
            return true;
        }
        catch (Exception)
        {
            // 设权限失败不该让"存凭据"整个失败：凭据本身已经是密文了，
            // 调用方会记一条审计，让用户知道这道门没关上。
            return false;
        }
    }

    /// <summary>当前交互用户的标识（PC 端 Agent 给自己放行时用）。</summary>
    public static IdentityReference? CurrentUserIdentity()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            return WindowsIdentity.GetCurrent().User;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Allow(FileSecurity security, IdentityReference identity)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            identity, FileSystemRights.FullControl, AccessControlType.Allow));
    }
}
