using System.Text.Json;
using FamilyAgent.Core.Protocol;
using FamilyAgent.Core.Protocol.Frames;
using Xunit;

namespace FamilyAgent.Core.Tests;

/// <summary>
/// 帧解析（<see cref="FrameCodec"/>）的行为与字段映射。
///
/// 这里的 JSON 都是服务端**真实发过的形状**：
/// <list type="bullet">
///   <item><c>message</c> 取自 docs/BUG-PC-REALTIME.md §三① 里抓到的实帧；</item>
///   <item>其余按 server/main.py 与 server/hub.py 的构造代码逐字段对照。</item>
/// </list>
/// 除了类型映射，还锁住两件容易改坏的事：收包日志的附加段（<c>type=… message_id=…</c>，
/// 排障全靠它）与「拿不到 request_id 就不派发截图请求」的判据。
/// </summary>
public class FrameCodecTests
{
    [Fact]
    public void MessageFrame_CarriesTypedFields()
    {
        const string raw = """
            {"type":"message","message_id":3,"sender_name":"妈妈","content":"结构测试",
             "message_type":"text","created_at":"2026-09-26T15:04:05","status":"sent",
             "auto_close_seconds":0,"history":[{"message_id":1,"content":"上一条"}]}
            """;

        var parsed = FrameCodec.Parse(raw);
        var frame = Assert.IsType<MessageFrame>(parsed.Frame);

        Assert.Equal("message", parsed.Type);
        Assert.Equal(3, frame.MessageId);
        Assert.Equal("妈妈", frame.SenderName);
        Assert.Equal("结构测试", frame.Content);
        Assert.Equal("text", frame.MessageType);
        Assert.Equal("2026-09-26T15:04:05", frame.CreatedAt);
        Assert.Equal(0, frame.AutoCloseSeconds);
        Assert.False(frame.Redelivered);
        Assert.Equal(JsonValueKind.Array, frame.History.ValueKind);

        // 原始帧仍随帧带上（Phase 1 宿主侧渲染仍按字段名读它）
        Assert.Equal(JsonValueKind.Object, frame.Raw.ValueKind);
        Assert.Equal("结构测试", frame.Raw.GetProperty("content").GetString());
    }

    [Fact]
    public void MessageFrame_RedeliveredFlag_IsRead()
    {
        const string raw = """{"type":"message","message_id":9,"redelivered":true}""";

        var frame = Assert.IsType<MessageFrame>(FrameCodec.Parse(raw).Frame);

        Assert.True(frame.Redelivered);
        Assert.Equal(9, frame.MessageId);
    }

    /// <summary>排障用的 RX 日志行：message 帧要带 message_id，其余帧不带附加段。</summary>
    [Fact]
    public void LogExtra_IsOnlyAttachedToMessageFrames()
    {
        Assert.Equal(" message_id=42",
            FrameCodec.Parse("""{"type":"message","message_id":42}""").LogExtra);
        Assert.Equal("",
            FrameCodec.Parse("""{"type":"hello","token":"t"}""").LogExtra);
    }

    [Fact]
    public void MissingType_YieldsNoTypeAndNoFrame()
    {
        var parsed = FrameCodec.Parse("""{"message_id":1}""");

        Assert.Null(parsed.Type);
        Assert.Null(parsed.Frame);
    }

    /// <summary>不认识的 type：记 RX 日志（Type 有值），但不产出载荷 —— 与原 switch 落空一致。</summary>
    [Fact]
    public void UnknownType_HasTypeButNoFrame()
    {
        var parsed = FrameCodec.Parse("""{"type":"something_new","x":1}""");

        Assert.Equal("something_new", parsed.Type);
        Assert.Null(parsed.Frame);
    }

    [Fact]
    public void HelloFrame_ReadsToken()
    {
        var frame = Assert.IsType<HelloFrame>(FrameCodec.Parse("""{"type":"hello","token":"abc"}""").Frame);

        Assert.Equal("abc", frame.Token);
    }

    /// <summary>原判据：拿不到 request_id 就无法应答 → 不派发事件（这里不产出帧）。</summary>
    [Fact]
    public void ScreenshotRequest_WithoutRequestId_IsNotDispatched()
    {
        Assert.Null(FrameCodec.Parse("""{"type":"screenshot_request"}""").Frame);
        Assert.Null(FrameCodec.Parse("""{"type":"screenshot_request","request_id":""}""").Frame);

        var frame = Assert.IsType<ScreenshotRequestFrame>(
            FrameCodec.Parse("""{"type":"screenshot_request","request_id":"r-1"}""").Frame);
        Assert.Equal("r-1", frame.RequestId);
    }

    [Fact]
    public void ReplyAckFrame_ReadsStatusAndNullableMessageId()
    {
        var frame = Assert.IsType<ReplyAckFrame>(FrameCodec.Parse(
            """{"type":"reply_ack","client_id":"c1","message_id":7,"status":"ok"}""").Frame);
        Assert.Equal("c1", frame.ClientId);
        Assert.Equal(7, frame.MessageId);
        Assert.Equal("ok", frame.Status);

        var withoutId = Assert.IsType<ReplyAckFrame>(FrameCodec.Parse(
            """{"type":"reply_ack","client_id":"c2","message_id":null,"status":"empty"}""").Frame);
        Assert.Null(withoutId.MessageId);
        Assert.Equal("empty", withoutId.Status);
    }

    [Fact]
    public void HistoryFrame_CarriesMessagesArray()
    {
        var frame = Assert.IsType<HistoryFrame>(FrameCodec.Parse(
            """{"type":"history_response","request_id":"r","device_id":"pc_1","messages":[{"message_id":1}]}""")
            .Frame);

        Assert.Equal("r", frame.RequestId);
        Assert.Equal("pc_1", frame.DeviceId);
        Assert.Equal(JsonValueKind.Array, frame.Messages.ValueKind);
        Assert.Equal(1, frame.Messages.GetArrayLength());
    }

    /// <summary>「缺省几秒」由平台层决定：帧里没带、或不是数字时是 null。</summary>
    [Fact]
    public void ShutdownFrame_DelaySecondsIsNullable()
    {
        Assert.Equal(30, Assert.IsType<ShutdownCommandFrame>(
            FrameCodec.Parse("""{"type":"shutdown","delay_seconds":30}""").Frame).DelaySeconds);

        Assert.Null(Assert.IsType<ShutdownCommandFrame>(
            FrameCodec.Parse("""{"type":"shutdown"}""").Frame).DelaySeconds);

        Assert.Null(Assert.IsType<ShutdownCommandFrame>(
            FrameCodec.Parse("""{"type":"shutdown","delay_seconds":"soon"}""").Frame).DelaySeconds);
    }

    [Fact]
    public void UnlockRequestFrame_ReadsRequestId()
    {
        var frame = Assert.IsType<UnlockRequestFrame>(FrameCodec.Parse(
            """{"type":"unlock_request","request_id":"u-1","action":"unlock","target":"pc_1"}""").Frame);
        Assert.Equal("u-1", frame.RequestId);

        Assert.Null(Assert.IsType<UnlockRequestFrame>(
            FrameCodec.Parse("""{"type":"unlock_request","action":"unlock"}""").Frame).RequestId);
    }

    [Fact]
    public void HeartbeatAck_YieldsTypedFrame()
    {
        var parsed = FrameCodec.Parse("""{"type":"heartbeat_ack"}""");

        Assert.IsType<HeartbeatAckFrame>(parsed.Frame);
        Assert.Equal("", parsed.LogExtra);
    }

    /// <summary>字段类型不对当缺省（不抛）—— 一帧坏字段不该把整个接收循环打断。</summary>
    [Fact]
    public void WrongFieldTypes_AreTreatedAsMissing()
    {
        var frame = Assert.IsType<MessageFrame>(FrameCodec.Parse(
            """{"type":"message","message_id":"3","sender_name":5,"auto_close_seconds":"x"}""").Frame);

        Assert.Equal(0, frame.MessageId);
        Assert.Equal("", frame.SenderName);
        Assert.Null(frame.AutoCloseSeconds);
    }

    /// <summary>非法 JSON 照旧抛：由接收循环那条 catch 记「消息解析失败」，与原实现一致。</summary>
    [Fact]
    public void InvalidJson_Throws()
    {
        Assert.ThrowsAny<JsonException>(() => FrameCodec.Parse("{ oops"));
    }
}
