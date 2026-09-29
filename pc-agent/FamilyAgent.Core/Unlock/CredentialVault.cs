using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace FamilyAgent.Core.Unlock;

/// <summary>
/// 「Windows 远程解锁凭据」的保险箱（docs/REMOTE-UNLOCK-PLAN.md §16.2 交付物之一）。
///
/// 三条硬规矩：
/// <list type="number">
///   <item><b>不落明文</b>：落盘的是 <see cref="ISecretProtector"/> 产出的密文（真机 = DPAPI），
///         整个文件里任何地方都不出现口令本身；</item>
///   <item><b>读不出就当没有</b>：密文被篡改 / 换机器 / 换熵 → <see cref="Load"/> 返回
///         <c>null</c> 而不是抛异常（页面不该因为凭据文件坏了就崩）；</item>
///   <item><b>清除要彻底</b>：<see cref="Clear"/> 删文件 + 清内存里的缓存，
///         卸载程序也会调它（「卸载残留凭据」比没配更严重）。</item>
/// </list>
///
/// ⚠ 文件 ACL（只给服务账户读）由 Windows 侧在写完之后设 —— 那是平台细节，
///   本类只负责「内容永远不是明文」这条与平台无关的判据。
/// </summary>
public sealed class CredentialVault
{
    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly byte[] _entropy;

    /// <param name="path">凭据文件路径（服务侧一般是 <c>%ProgramData%\FamilyAgent\credential.json</c>）。</param>
    /// <param name="protector">密文实现（真机 DPAPI / 测试假实现）。</param>
    /// <param name="entropy">附加熵：App 专属常量，避免同机其它程序解开。</param>
    public CredentialVault(string path, ISecretProtector protector, byte[]? entropy = null)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _entropy = entropy ?? DefaultEntropy;
    }

    /// <summary>默认附加熵。**改它就等于让已存的凭据全部失效**（换熵读不出旧密文）。</summary>
    public static byte[] DefaultEntropy =>
        Encoding.UTF8.GetBytes("FamilyAgent.RemoteUnlock.v1");

    /// <summary>保险箱文件路径（诊断/测试用）。</summary>
    public string Path => _path;

    /// <summary>已经配过凭据吗（只看文件在不在，不解密）。</summary>
    public bool HasCredential => File.Exists(_path);

    /// <summary>存入凭据：加密后落盘，覆盖旧的。返回落盘的记录（含 <see cref="CredentialRecord.ProtectedSecretBase64"/>）。</summary>
    public CredentialRecord Store(string user, string secret, string nowIso)
    {
        if (string.IsNullOrWhiteSpace(user))
            throw new ArgumentException("用户名不能为空", nameof(user));
        if (string.IsNullOrEmpty(secret))
            throw new ArgumentException("口令不能为空", nameof(secret));

        var cipher = _protector.Protect(Encoding.UTF8.GetBytes(secret), _entropy);
        var rec = new CredentialRecord(user.Trim(), Convert.ToBase64String(cipher), nowIso, null);
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(rec), new UTF8Encoding(false));
        return rec;
    }

    /// <summary>读出凭据（用户名 + **明文口令**）。读不出（没配 / 坏了）返回 <c>null</c>。</summary>
    public (string User, string Secret)? Load()
    {
        if (!File.Exists(_path))
            return null;
        try
        {
            var rec = JsonSerializer.Deserialize<CredentialRecord>(File.ReadAllText(_path));
            if (rec is null || string.IsNullOrEmpty(rec.ProtectedSecretBase64))
                return null;
            var plain = _protector.Unprotect(Convert.FromBase64String(rec.ProtectedSecretBase64), _entropy);
            return (rec.User, Encoding.UTF8.GetString(plain));
        }
        catch (Exception)
        {
            // 密文坏了 / 换了机器 / 换了熵：按「没配凭据」处理，绝不把异常抛给页面
            return null;
        }
    }

    /// <summary>记一次成功校验的时间（审计用）。凭据不存在时什么都不做。</summary>
    public void MarkVerified(string nowIso)
    {
        if (!File.Exists(_path))
            return;
        try
        {
            var rec = JsonSerializer.Deserialize<CredentialRecord>(File.ReadAllText(_path));
            if (rec is null)
                return;
            File.WriteAllText(_path, JsonSerializer.Serialize(rec with { VerifiedAt = nowIso }),
                              new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // 审计时间写不进去不影响功能
        }
    }

    /// <summary>清除凭据（卸载程序、设置页「清除凭据」都走这里）。返回是否真的删了东西。</summary>
    public bool Clear()
    {
        if (!File.Exists(_path))
            return false;
        try
        {
            File.Delete(_path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
