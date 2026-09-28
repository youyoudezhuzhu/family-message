namespace FamilyAgent.Core.Protocol.Frames;

/// <summary>
/// <c>reply_ack</c>：服务端对本机回复的受理回执（server/main.py:882-888）。
///
/// 页面靠它把「发送中」变成「已发送 / 失败」，宿主原实现直接读原文
/// （<c>App.OnReplyAcked</c>），Phase 1 保留 <see cref="CoreFrame.Raw"/> 供其使用。
/// </summary>
public sealed class ReplyAckFrame : CoreFrame
{
    /// <summary>本机发起回复时带的客户端 id（页面用它把回执对上是哪一条自己发的）。</summary>
    public string ClientId { get; init; } = "";

    /// <summary>服务端为这条回复分配的消息 id；没有时为 null。</summary>
    public long? MessageId { get; init; }

    /// <summary>受理结果：<c>ok</c> / <c>empty</c> / 其它错误串。</summary>
    public string Status { get; init; } = "";

    /// <summary>服务端创建时间（现状宿主未使用，留着与协议对齐）。</summary>
    public string CreatedAt { get; init; } = "";
}
