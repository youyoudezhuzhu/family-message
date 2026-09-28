namespace FamilyAgent.Core.Protocol.Frames;

/// <summary>
/// <c>screenshot_request</c>：网页端点了「查看桌面」（server/hub.py:199-201）。
///
/// ⚠ 原实现只在 <c>request_id</c> 存在且非空时才派发事件（拿不到 id 就无法应答），
///   <c>FrameCodec</c> 保持同一判据：此时**不产出本帧**（= 不派发）。
///   应答走 <c>ConnectionManager.SendScreenshotAsync</c>（含错误路径，见 PROTOCOL.md）。
/// </summary>
public sealed class ScreenshotRequestFrame : CoreFrame
{
    /// <summary>这次截图请求的 id，应答时必须原样带上。</summary>
    public string RequestId { get; init; } = "";
}
