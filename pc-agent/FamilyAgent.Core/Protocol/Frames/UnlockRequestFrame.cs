namespace FamilyAgent.Core.Protocol.Frames;

/// <summary>
/// <c>unlock_request</c>：网页端发起的远程解锁请求（远程解锁 Phase 1）。
///
/// 校验与应答都不需要桌面，headless（登录界面）下同样能走通，所以这一帧是
/// 唯一在两种实例形态下都要处理的命令。
///
/// ⚠ 请求体里还有 target / action / expires_at / nonce 等字段，但它们的**归属/动作/时效/
///   重放校验**属于既有实现 <c>UnlockGuard</c>（宿主，见 §8.12 的归属待拍板），
///   Phase 1 不做搬运，宿主继续读 <see cref="CoreFrame.Raw"/> 走原逻辑。
///   这里只把「应答时必须原样带回来」的 request_id 强类型化。
/// </summary>
public sealed class UnlockRequestFrame : CoreFrame
{
    /// <summary>这次解锁请求的 id；帧里没带时为 null（原实现此时只记一行日志、不应答）。</summary>
    public string? RequestId { get; init; }
}
