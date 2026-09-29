using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Text;
using FamilyAgent.Core.Unlock;
using FamilyAgent.Windows.Secrets;
using FamilyAgent.Windows.Security;
using Xunit;

namespace FamilyAgent.Windows.Tests;

// ───────────────────────────────────────────────────────────────────
//  Windows 平台能力的**真机**判据（docs/REMOTE-UNLOCK-PLAN.md §16.4 第 1、2 条）
//
//  这些测试在 Linux 上会**早退**（DPAPI / LogonUser 不存在），只在 windows-latest
//  的 CI 上真正执行。跑到的判据：
//    · DPAPI 两种作用域都能「加→解」还原；换了熵 / 密文被改 → 解不开
//    · 保险箱 + 真 DPAPI 端到端：存取一致、且落盘不含明文
//    · 文件 ACL：断继承、只留 SYSTEM + Administrators（+ 指定账户）
//    · LogonUser：不存在的账户 / 空口令 → false 且不抛异常
//
//  ⚠ 安全提醒：LogonUser 的失败用例**只用不存在的账户名**。拿真实账户名去试错口令
//     会累加系统/域的登录失败计数（本地策略或域策略触发账户锁定），
//     那属于「测试把人锁在外面」的事故（§16.3 决策 5 的冷却就是为这个设的）。
// ───────────────────────────────────────────────────────────────────

public sealed class WindowsPlatformTests
{
    private static bool NotWindows() => !OperatingSystem.IsWindows();

    private static byte[] Entropy() => Encoding.UTF8.GetBytes("FamilyAgent.RemoteUnlock.v1");

    private static string TempCredentialPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fm-win-test-" + Guid.NewGuid().ToString("N"));
        return Path.Combine(dir, "credential.json");
    }

    // ── DPAPI ───────────────────────────────────────────────────────

    [Fact]
    public void DPAPI_当前用户作用域可以往返()
    {
        if (NotWindows()) return;
        var p = DpapiSecretProtector.ForCurrentUser();
        var secret = Encoding.UTF8.GetBytes("pa55word!-中文也要能存");

        var cipher = p.Protect(secret, Entropy());
        Assert.NotEqual(secret, cipher);                    // 确实加密了
        Assert.True(cipher.Length > secret.Length);         // DPAPI 密文带头部
        Assert.Equal("pa55word!-中文也要能存",
                     Encoding.UTF8.GetString(p.Unprotect(cipher, Entropy())));
    }

    [Fact]
    public void DPAPI_本机作用域可以往返()
    {
        if (NotWindows()) return;
        var p = DpapiSecretProtector.ForLocalMachine();
        var secret = Encoding.UTF8.GetBytes("machine-scope-secret");

        var cipher = p.Protect(secret, Entropy());
        Assert.Equal("machine-scope-secret", Encoding.UTF8.GetString(p.Unprotect(cipher, Entropy())));
    }

    [Fact]
    public void DPAPI_换了熵就解不开()
    {
        if (NotWindows()) return;
        var p = DpapiSecretProtector.ForLocalMachine();
        var cipher = p.Protect(Encoding.UTF8.GetBytes("hidden"), Entropy());

        Assert.ThrowsAny<Exception>(() =>
            p.Unprotect(cipher, Encoding.UTF8.GetBytes("另一个熵")));
    }

    [Fact]
    public void DPAPI_密文被改过就解不开()
    {
        if (NotWindows()) return;
        var p = DpapiSecretProtector.ForCurrentUser();
        var cipher = p.Protect(Encoding.UTF8.GetBytes("hidden"), Entropy());

        var tampered = (byte[])cipher.Clone();
        tampered[tampered.Length / 2] ^= 0xFF;              // 改中间一个字节

        Assert.ThrowsAny<Exception>(() => p.Unprotect(tampered, Entropy()));
    }

    // ── 保险箱 + 真 DPAPI 端到端 ────────────────────────────────────

    [Fact]
    public void 保险箱_真DPAPI_存取一致且落盘无明文()
    {
        if (NotWindows()) return;
        var path = TempCredentialPath();
        var vault = new CredentialVault(path, DpapiSecretProtector.ForLocalMachine(), Entropy());
        const string secret = "Pa55w0rd-明文不许出现";

        vault.Store(Environment.UserName, secret, "2026-09-29 18:00:00");
        var got = vault.Load();

        Assert.NotNull(got);
        Assert.Equal(secret, got!.Value.Secret);

        var raw = File.ReadAllText(path);
        Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)), raw, StringComparison.Ordinal);

        Assert.True(vault.Clear());
        Assert.False(vault.HasCredential);
    }

    [Fact]
    public void 保险箱_CurrentUser作用域存的不该被LocalMachine解开()
    {
        if (NotWindows()) return;
        var path = TempCredentialPath();
        new CredentialVault(path, DpapiSecretProtector.ForCurrentUser(), Entropy())
            .Store(Environment.UserName, "only-me", "t");

        // 同一个用户用 LocalMachine 作用域去解 CurrentUser 的密文 → 解不开（作用域不同）
        var other = new CredentialVault(path, DpapiSecretProtector.ForLocalMachine(), Entropy());
        Assert.Null(other.Load());
    }

    // ── 文件 ACL ────────────────────────────────────────────────────

    [Fact]
    public void ACL_断继承且只留白名单()
    {
        if (NotWindows()) return;
        var path = TempCredentialPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");

        Assert.True(CredentialFileAcl.Restrict(path, CredentialFileAcl.CurrentUserIdentity()));

        var security = new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);       // 继承已断
        var ruleText = string.Join(" | ", security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(r => r.IdentityReference.Value));
        Assert.DoesNotContain("Everyone", ruleText);         // 没有人人可读
        Assert.DoesNotContain("Users", ruleText);            // 也没有普通用户组
        Assert.Contains("S-1-5-18", ruleText);                // SYSTEM 在
        Assert.Contains("S-1-5-32-544", ruleText);            // Administrators 在
    }

    [Fact]
    public void ACL_文件不存在返回false()
    {
        if (NotWindows()) return;
        Assert.False(CredentialFileAcl.Restrict(TempCredentialPath()));
    }

    // ── LogonUser ──────────────────────────────────────────────────

    [Fact]
    public void 用户名拆分规则()
    {
        Assert.Equal((null, "jzh"), LogonUserVerifier.SplitUser("jzh"));
        Assert.Equal(("DESKTOP-1", "jzh"), LogonUserVerifier.SplitUser("DESKTOP-1\\jzh"));
        Assert.Equal((null, "jzh"), LogonUserVerifier.SplitUser("  jzh  "));
        Assert.Equal((null, "\\jzh"), LogonUserVerifier.SplitUser("\\jzh"));
    }

    [Fact]
    public void LogonUser_不存在的账户返回false且不抛()
    {
        if (NotWindows()) return;
        var v = new LogonUserVerifier();

        // 只用一个**必然不存在**的账户名：真实账户名 + 错口令会累加失败计数（可能锁账户）
        var ok = v.Verify("fm-no-such-user-" + Guid.NewGuid().ToString("N"), "definitely-wrong");

        Assert.False(ok);
        Assert.NotNull(v.LastFailureReason);
    }

    [Fact]
    public void LogonUser_空用户名或空口令返回false()
    {
        if (NotWindows()) return;
        var v = new LogonUserVerifier();
        Assert.False(v.Verify("", "x"));
        Assert.False(v.Verify("user", ""));
    }

    [Fact]
    public void 闸门_接真LogonUser_失败三次就冷却()
    {
        if (NotWindows()) return;
        var path = TempCredentialPath();
        var vault = new CredentialVault(path, DpapiSecretProtector.ForLocalMachine(), Entropy());
        vault.Store("fm-no-such-user", "whatever", "t");

        var reason = "";
        var gate = new UnlockGate(vault, new LogonUserVerifier(), audit: l => reason = l);
        var bogusUser = "fm-no-such-user-" + Guid.NewGuid().ToString("N");

        Assert.Equal(UnlockCheckResult.BadCredential, gate.Check(bogusUser, "w1"));
        Assert.Equal(UnlockCheckResult.BadCredential, gate.Check(bogusUser, "w2"));
        Assert.Equal(UnlockCheckResult.BadCredential, gate.Check(bogusUser, "w3"));
        Assert.Equal(UnlockCheckResult.LockedOut, gate.Check(bogusUser, "w4"));
        Assert.Contains("locked_out", reason);
        Assert.Contains("bad_credential", reason);
    }
}
