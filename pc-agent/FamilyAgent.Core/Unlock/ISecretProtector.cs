namespace FamilyAgent.Core.Unlock;

/// <summary>
/// 「把一段明文变成只有本机能解开的密文」这件事的抽象。
///
/// 为什么要抽象（docs/REMOTE-UNLOCK-PLAN.md §16.3 决策 3）：
/// <list type="bullet">
///   <item>真实现是 Windows 的 DPAPI（<c>ProtectedData</c>，LocalMachine 作用域 + App 专属熵），
///         它只能在 Windows 上跑；</item>
///   <item>Core 的目标是「能在 Linux/CI 上跑通」（§8.8），所以 Core 只认这个接口，
///         测试用假实现（见 <c>UnlockCredentialTests</c>）就能把
///         「存进去 / 读出来 / 磁盘上没有明文 / 校验失败会锁」这些判据在 NAS 上跑完；</item>
///   <item>Windows 侧的实现（<c>DpapiSecretProtector</c>）留在 Service 项目里，
///         它不参与单元测试，只参与「真机凭据存取」那条人工验证。</item>
/// </list>
/// </summary>
public interface ISecretProtector
{
    /// <summary>加密。<paramref name="entropy"/> 是附加熵（App 专属，防止同机别的程序解开）。</summary>
    byte[] Protect(byte[] plaintext, byte[] entropy);

    /// <summary>解密。密文被篡改 / 换了机器 / 换了熵 → 抛异常（调用方按「读不出凭据」处理）。</summary>
    byte[] Unprotect(byte[] ciphertext, byte[] entropy);
}

/// <summary>落盘的凭据记录（**只有密文**，永远不落明文）。</summary>
public sealed record CredentialRecord(
    string User,
    string ProtectedSecretBase64,
    string CreatedAt,
    string? VerifiedAt);
