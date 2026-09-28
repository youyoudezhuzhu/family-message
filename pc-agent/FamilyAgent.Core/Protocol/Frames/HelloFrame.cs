namespace FamilyAgent.Core.Protocol.Frames;

/// <summary>
/// <c>hello</c>：连接建立后服务端下发的第一帧。
///
/// 载荷里可能带一个设备令牌：服务端首次 enroll 后把令牌回给客户端，
/// 客户端存进本地配置（原 <c>Dispatch</c> 的 <c>case "hello"</c>）。
/// </summary>
public sealed class HelloFrame : CoreFrame
{
    /// <summary>服务端回填的令牌；帧里没带（或不是字符串）时为 null。</summary>
    public string? Token { get; init; }
}
