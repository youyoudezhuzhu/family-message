using System;
using System.IO;
using System.Text;
using FamilyAgent.Core.Unlock;
using Xunit;

namespace FamilyAgent.Core.Tests;

// ───────────────────────────────────────────────────────────────────
//  远程解锁 Phase 2 · 凭据保险箱与校验闸门（docs/REMOTE-UNLOCK-PLAN.md §16）
//
//  这一组跑在 **Linux/CI** 上：Core 只认 ISecretProtector / ILogonVerifier 两个接口，
//  所以 DPAPI 与 LogonUser 的真实现（Windows 侧）不参与，判据是平台无关的那几条：
//    · 存进去 / 读出来一致
//    · **磁盘上没有明文**
//    · 密文坏了 / 换熵 → 按「没配凭据」处理，不抛异常
//    · 连续 3 次失败 → 冷却 10 分钟；过窗口后恢复；成功清零并记时间戳
//    · 审计行写得对（谁、第几次、结果），且失败细节只在本地、不上行
// ───────────────────────────────────────────────────────────────────

/// <summary>测试用假保护器：**魔数 + XOR**（真可逆；魔数不对/长度不够 → 抛，跟 DPAPI 一样能检出篡改）。</summary>
internal sealed class FakeProtector : ISecretProtector
{
    private static readonly byte[] Magic = { (byte)'F', (byte)'M', (byte)'K', (byte)'1' };

    public int ProtectCalls { get; private set; }
    public int UnprotectCalls { get; private set; }

    public byte[] Protect(byte[] plaintext, byte[] entropy)
    {
        ProtectCalls++;
        var raw = new byte[Magic.Length + plaintext.Length];
        Array.Copy(Magic, raw, Magic.Length);
        Array.Copy(plaintext, 0, raw, Magic.Length, plaintext.Length);
        var outp = new byte[raw.Length];
        for (int i = 0; i < raw.Length; i++)
            outp[i] = (byte)(raw[i] ^ entropy[i % entropy.Length]);
        return outp;
    }

    public byte[] Unprotect(byte[] ciphertext, byte[] entropy)
    {
        UnprotectCalls++;
        var raw = new byte[ciphertext.Length];
        for (int i = 0; i < ciphertext.Length; i++)
            raw[i] = (byte)(ciphertext[i] ^ entropy[i % entropy.Length]);
        if (raw.Length < Magic.Length)
            throw new InvalidOperationException("密文太短");
        for (int i = 0; i < Magic.Length; i++)
            if (raw[i] != Magic[i])
                throw new InvalidOperationException("密文校验不过（被改过 / 换了机器或熵）");
        var plain = new byte[raw.Length - Magic.Length];
        Array.Copy(raw, Magic.Length, plain, 0, plain.Length);
        return plain;
    }
}

/// <summary>测试用假校验器：只认「约定的那一个口令」。</summary>
internal sealed class FakeVerifier : ILogonVerifier
{
    private readonly string _user;
    private readonly string _secret;
    public int Calls { get; private set; }
    public bool Throw { get; set; }

    public FakeVerifier(string user, string secret)
    {
        _user = user;
        _secret = secret;
    }

    public bool Verify(string user, string secret)
    {
        Calls++;
        if (Throw)
            throw new InvalidOperationException("模拟平台校验炸了");
        return user == _user && secret == _secret;
    }
}

public sealed class UnlockCredentialTests
{
    private static string TempPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fm-unlock-test-" + Guid.NewGuid().ToString("N"));
        return Path.Combine(dir, "credential.json");
    }

    // ── 保险箱 ──────────────────────────────────────────────────────

    [Fact]
    public void 存进去读出来一致()
    {
        var path = TempPath();
        var vault = new CredentialVault(path, new FakeProtector());
        Assert.False(vault.HasCredential);

        vault.Store("home\\jzh", "pa55word!", "2026-09-29 18:00:00");
        Assert.True(vault.HasCredential);

        var got = vault.Load();
        Assert.NotNull(got);
        Assert.Equal("home\\jzh", got!.Value.User);
        Assert.Equal("pa55word!", got.Value.Secret);
    }

    [Fact]
    public void 磁盘上没有明文()
    {
        var path = TempPath();
        var vault = new CredentialVault(path, new FakeProtector());
        const string secret = "S3cret-明文不许出现-2026";
        vault.Store("home\\jzh", secret, "2026-09-29 18:00:00");

        var raw = File.ReadAllText(path);
        // 明文口令、以及它的 base64，都不许出现在文件里
        Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)), raw, StringComparison.Ordinal);
        // 但保护器确实被调用过（说明存的是密文，不是"没存"）
        Assert.Contains("ProtectedSecretBase64", raw);
    }

    [Fact]
    public void 密文被篡改_按没配凭据处理()
    {
        var path = TempPath();
        var vault = new CredentialVault(path, new FakeProtector());
        vault.Store("home\\jzh", "pa55word!", "2026-09-29 18:00:00");

        // 把密文改成合法 base64 但内容垃圾
        var raw = File.ReadAllText(path);
        File.WriteAllText(path, raw.Replace(
            System.Text.Json.JsonSerializer.Deserialize<CredentialRecord>(raw)!.ProtectedSecretBase64,
            Convert.ToBase64String(new byte[] { 1, 2, 3 })));

        Assert.Null(vault.Load());          // 不抛异常
        Assert.True(vault.HasCredential);   // 文件还在，只是读不出
    }

    [Fact]
    public void 换熵读不出旧密文()
    {
        var path = TempPath();
        new CredentialVault(path, new FakeProtector(), new byte[] { 1, 2, 3, 4 })
            .Store("home\\jzh", "pa55word!", "2026-09-29 18:00:00");

        var other = new CredentialVault(path, new FakeProtector(), new byte[] { 9, 8, 7, 6 });
        Assert.Null(other.Load());          // 换了熵 → 校验不过 → 按「没配凭据」处理
    }

    [Fact]
    public void 清除凭据要彻底()
    {
        var path = TempPath();
        var vault = new CredentialVault(path, new FakeProtector());
        vault.Store("home\\jzh", "pa55word!", "2026-09-29 18:00:00");
        Assert.True(vault.Clear());
        Assert.False(vault.HasCredential);
        Assert.Null(vault.Load());
        Assert.False(vault.Clear());        // 幂等
    }

    [Fact]
    public void 空用户名或空口令不许存()
    {
        var vault = new CredentialVault(TempPath(), new FakeProtector());
        Assert.Throws<ArgumentException>(() => vault.Store("", "x", "t"));
        Assert.Throws<ArgumentException>(() => vault.Store("u", "", "t"));
    }

    [Fact]
    public void 校验成功会记时间戳()
    {
        var path = TempPath();
        var vault = new CredentialVault(path, new FakeProtector());
        vault.Store("home\\jzh", "pa55word!", "2026-09-29 18:00:00");
        vault.MarkVerified("2026-09-29 19:30:00");

        var rec = System.Text.Json.JsonSerializer.Deserialize<CredentialRecord>(File.ReadAllText(path));
        Assert.Equal("2026-09-29 19:30:00", rec!.VerifiedAt);
        Assert.Equal("home\\jzh", rec.User);              // 其它字段没被写坏
    }

    // ── 校验闸门（失败计数 + 冷却）───────────────────────────────────

    [Fact]
    public void 连续三次失败后进入冷却_窗口过后恢复()
    {
        var path = TempPath();
        var vault = new CredentialVault(path, new FakeProtector());
        vault.Store("home\\jzh", "right", "2026-09-29 18:00:00");
        var verifier = new FakeVerifier("home\\jzh", "right");
        var now = new DateTimeOffset(2026, 9, 29, 18, 0, 0, TimeSpan.FromHours(8));
        var gate = new UnlockGate(vault, verifier, () => now);

        Assert.Equal(UnlockCheckResult.BadCredential, gate.Check("home\\jzh", "wrong1"));
        Assert.Equal(UnlockCheckResult.BadCredential, gate.Check("home\\jzh", "wrong2"));
        Assert.Equal(UnlockCheckResult.BadCredential, gate.Check("home\\jzh", "wrong3"));
        Assert.True(gate.IsLockedOut);
        Assert.False(gate.ReadyForRemoteUnlock);

        // 冷却中：连正确口令都不接受（否则锁定形同虚设）
        Assert.Equal(UnlockCheckResult.LockedOut, gate.Check("home\\jzh", "right"));
        Assert.Equal(3, verifier.Calls);                 // 冷却期间没有再去戳平台校验

        // 跨过窗口 → 恢复
        now = now.Add(UnlockGate.LockoutWindow + TimeSpan.FromSeconds(1));
        Assert.False(gate.IsLockedOut);
        Assert.Equal(UnlockCheckResult.Ok, gate.Check("home\\jzh", "right"));
    }

    [Fact]
    public void 成功一次就清零失败计数()
    {
        var path = TempPath();
        var vault = new CredentialVault(path, new FakeProtector());
        vault.Store("home\\jzh", "right", "t");
        var gate = new UnlockGate(vault, new FakeVerifier("home\\jzh", "right"));

        Assert.Equal(UnlockCheckResult.BadCredential, gate.Check("home\\jzh", "wrong"));
        Assert.Equal(UnlockCheckResult.BadCredential, gate.Check("home\\jzh", "wrong"));
        Assert.Equal(2, gate.FailureCount);

        Assert.Equal(UnlockCheckResult.Ok, gate.Check("home\\jzh", "right"));
        Assert.Equal(0, gate.FailureCount);
        Assert.False(gate.IsLockedOut);
    }

    [Fact]
    public void 校验实现抛异常按不对处理_绝不放行()
    {
        var path = TempPath();
        var vault = new CredentialVault(path, new FakeProtector());
        vault.Store("home\\jzh", "right", "t");
        var verifier = new FakeVerifier("home\\jzh", "right") { Throw = true };
        var gate = new UnlockGate(vault, verifier);

        Assert.Equal(UnlockCheckResult.BadCredential, gate.Check("home\\jzh", "right"));
        Assert.Equal(1, gate.FailureCount);
    }

    [Fact]
    public void 没配凭据时不能接受远程解锁()
    {
        var vault = new CredentialVault(TempPath(), new FakeProtector());
        var gate = new UnlockGate(vault, new FakeVerifier("home\\jzh", "right"));
        Assert.False(gate.ReadyForRemoteUnlock);

        vault.Store("home\\jzh", "right", "t");
        Assert.True(gate.ReadyForRemoteUnlock);
    }

    [Fact]
    public void 审计行写得对且不含口令()
    {
        var path = TempPath();
        var vault = new CredentialVault(path, new FakeProtector());
        vault.Store("home\\jzh", "right", "t");
        var lines = new System.Collections.Generic.List<string>();
        var gate = new UnlockGate(vault, new FakeVerifier("home\\jzh", "right"), audit: lines.Add);

        gate.Check("home\\jzh", "wrong-secret-value");
        gate.Check("home\\jzh", "right");

        Assert.Equal(2, lines.Count);
        Assert.Contains("bad_credential", lines[0]);
        Assert.Contains("attempt=1", lines[0]);
        Assert.Contains("ok", lines[1]);
        // 审计里**不许**出现口令本身
        Assert.DoesNotContain(lines, l => l.Contains("wrong-secret-value") || l.Contains("right"));
    }
}
