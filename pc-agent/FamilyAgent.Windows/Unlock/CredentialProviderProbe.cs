using System;
using Microsoft.Win32;

namespace FamilyAgent.Windows.Unlock;

/// <summary>
/// 「真正施加解锁的那个组件装没装」—— 也就是 Phase 3 的 Credential Provider。
///
/// 为什么需要它：Phase 2 做完之后，凭据存好了、请求也到了 PC，但**没有人把锁解开** ——
/// Credential Provider 才是跑在登录界面（安全桌面）里、能替用户提交凭据的那个组件。
/// 没有它却回 <c>armed</c>，网页端只会一直等到超时（用户看到"没反应"）。
/// 所以这里如实探测，Guard 据此回 <c>cp_missing</c>。
///
/// 探测方式：只读注册表。Windows 的凭据提供程序必须注册在
/// <c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{GUID}</c>
/// 下（键在 = 装了）。**只读、无副作用、失败一律按"没装"**（宁可说缺组件，也不谎报能解锁）。
/// GUID 与 Phase 3 安装器共用同一个常量，避免两边写岔。
/// </summary>
public static class CredentialProviderProbe
{
    /// <summary>
    /// 家庭消息远程解锁 CP 的固定 GUID（Phase 3 的安装器用它注册；改这个就等于换了个组件）。
    /// </summary>
    public const string ProviderGuid = "{8F1E6D2A-4C3B-45A7-9E10-2B7C5D91A3F4}";

    private const string CpRoot =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers";

    /// <summary>本机是否已注册该 Credential Provider。任何异常都当作"没装"。</summary>
    public static bool IsInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{CpRoot}\{ProviderGuid}", false);
            return key is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
