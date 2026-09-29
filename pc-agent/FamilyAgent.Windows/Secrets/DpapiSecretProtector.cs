using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FamilyAgent.Core.Unlock;
using FamilyAgent.Windows.Secrets;

namespace FamilyAgent.Windows.Secrets;

/// <summary>
/// <see cref="ISecretProtector"/> 的 Windows 真实现：DPAPI（Data Protection API）。
///
/// 两个作用域（docs/REMOTE-UNLOCK-PLAN.md §16.3 决策 3）：
/// <list type="bullet">
///   <item><see cref="DpapiScope.CurrentUser"/>：只有**同一个用户**能解。PC 端 Agent
///         自己存自己用，这个最紧。</item>
///   <item><see cref="DpapiScope.LocalMachine"/>：**本机**任何账户都能解（服务账户也能）。
///         Phase 2 的 Service 是另一个账户，所以要和交互进程共用一份凭据时用这个 ——
///         代价是"同机别的账户也解得开"，因此**必须**配文件 ACL
///         （见 <c>CredentialFileAcl</c>：只给服务账户 / Administrators / SYSTEM）。</item>
/// </list>
///
/// 附加熵（entropy）：App 专属常量，防止同机上别的程序拿 DPAPI 顺手解开我们的密文。
/// **改熵 = 已存凭据全部失效**（读不出 → 按"没配凭据"处理，让用户重新输入）。
///
/// ⚠ 实测纠正（2026-09-29，windows-latest CI）：解密时传的 <c>CRYPTPROTECT_LOCAL_MACHINE</c>
///   标志是"**去哪个密钥库找解密密钥**"，**不是**"校验这段密文的作用域"—— 同一个账户下，
///   两种作用域产生的密文都解得开。真正的差别只在**跨账户**时显现：
///   CurrentUser 的密文别的账户解不开；LocalMachine 的密文本机任何账户都能解。
///   所以"只靠作用域隔离 Service 与交互进程"是不成立的 → 必须配文件 ACL（见 CredentialFileAcl）。
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private readonly DpapiScope _scope;

    public DpapiSecretProtector(DpapiScope scope)
    {
        _scope = scope;
    }

    /// <summary>默认实现：CurrentUser 作用域（PC 端 Agent 自存自用）。</summary>
    public static DpapiSecretProtector ForCurrentUser() => new(DpapiScope.CurrentUser);

    /// <summary>Service 与交互进程共用凭据时用：LocalMachine 作用域（**记得配 ACL**）。</summary>
    public static DpapiSecretProtector ForLocalMachine() => new(DpapiScope.LocalMachine);

    public byte[] Protect(byte[] plaintext, byte[] entropy)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI 只在 Windows 上可用。");

        var data = NativeMethods.MakeBlob(plaintext);
        var ent = NativeMethods.MakeBlob(entropy);
        try
        {
            var flags = NativeMethods.CRYPTPROTECT_UI_FORBIDDEN |
                        (_scope == DpapiScope.LocalMachine ? NativeMethods.CRYPTPROTECT_LOCAL_MACHINE : 0);
            if (!NativeMethods.CryptProtectData(ref data, "FamilyAgent", ref ent, IntPtr.Zero, IntPtr.Zero,
                                                flags, out var outp))
                throw new CryptographicException($"DPAPI 加密失败：{NativeMethods.LastError()}");
            try
            {
                return NativeMethods.ReadBlob(outp);
            }
            finally
            {
                NativeMethods.FreeBlob(ref outp);
            }
        }
        finally
        {
            // 明文与熵都是敏感数据：用完立刻抹掉非托管副本
            ZeroBytes(plaintext);
            NativeMethods.FreeBlob(ref data);
            NativeMethods.FreeBlob(ref ent);
        }
    }

    public byte[] Unprotect(byte[] ciphertext, byte[] entropy)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI 只在 Windows 上可用。");

        var data = NativeMethods.MakeBlob(ciphertext);
        var ent = NativeMethods.MakeBlob(entropy);
        try
        {
            var flags = NativeMethods.CRYPTPROTECT_UI_FORBIDDEN |
                        (_scope == DpapiScope.LocalMachine ? NativeMethods.CRYPTPROTECT_LOCAL_MACHINE : 0);
            if (!NativeMethods.CryptUnprotectData(ref data, IntPtr.Zero, ref ent, IntPtr.Zero, IntPtr.Zero,
                                                  flags, out var outp))
                throw new CryptographicException(
                    $"DPAPI 解密失败：{NativeMethods.LastError()}（密文被改过 / 换了机器或账户 / 换了熵）");
            try
            {
                return NativeMethods.ReadBlob(outp);
            }
            finally
            {
                NativeMethods.FreeBlob(ref outp);
            }
        }
        finally
        {
            NativeMethods.FreeBlob(ref data);
            NativeMethods.FreeBlob(ref ent);
        }
    }

    private static void ZeroBytes(byte[] buffer)
    {
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = 0;
    }
}

/// <summary>DPAPI 的绑定范围。</summary>
public enum DpapiScope
{
    /// <summary>只有加密它的那个用户能解。</summary>
    CurrentUser,

    /// <summary>本机所有账户都能解（服务与交互进程共用凭据时用，**必须**配 ACL）。</summary>
    LocalMachine,
}
