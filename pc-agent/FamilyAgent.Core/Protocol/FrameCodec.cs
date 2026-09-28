using System.Text.Json;
using FamilyAgent.Core.Protocol.Frames;

namespace FamilyAgent.Core.Protocol;

/// <summary>
/// 一次收包解析的结果。
///
/// <see cref="Type"/> 为 null = 整个帧里**没有 type 属性**：原实现（<c>AgentClient.Dispatch</c>）
/// 此时什么都不做（连 RX 日志都不记），接收循环也一样。
/// </summary>
public sealed class ParsedFrame
{
    /// <summary>帧里的 type 字面量；没有 type 属性时为 null。</summary>
    public string? Type { get; init; }

    /// <summary>RX 日志的附加段（message 帧带 <c> message_id=N</c>），与重构前逐字一致。</summary>
    public string LogExtra { get; init; } = "";

    /// <summary>强类型载荷；type 不认识、或该帧按原判据不该派发时为 null。</summary>
    public CoreFrame? Frame { get; init; }
}

/// <summary>
/// 下行帧的解析：<c>type</c> → 强类型 DTO（原 <c>AgentClient.Dispatch</c> 的映射部分，
/// 见 docs/CORE-REFACTOR-PLAN.md §3.2 / §4）。
///
/// 三条与原实现一致的性质：
/// <list type="number">
///   <item>**先有 type 才处理**：没有 type 属性 → <see cref="ParsedFrame.Type"/> 为 null，
///     调用方直接返回（原实现如此）。</item>
///   <item>**收包日志的判据不变**：非 <c>heartbeat_ack</c> 都记一行 RX，
///     message 帧附上 <c>message_id</c>（原文取值，日志格式逐字一致）。</item>
///   <item>**非法 JSON 照旧抛**：<c>JsonDocument.Parse</c> 的异常由接收循环里的
///     同一条 catch 兜住并记「消息解析失败」——不在这里吞掉。</item>
/// </list>
///
/// ⚠ 与旧实现唯一的差别（刻意的，属 Phase 1 的「解析归 Core」）：**字段取值改成容错**。
///   旧实现里 <c>type</c> 之外的字段时间接 <c>JsonElement.GetString()/GetInt64()</c>，
///   字段类型不对会抛异常 → 整帧被丢掉。现在除了 <c>type</c> 本身（保持原样严格），
///   其余字段类型不对一律当缺省值（空串 / null / 0），不会因为服务端多塞一个字段类型
///   就把一帧消息丢掉。对真实服务端（字段类型稳定）**行为完全一致**。
/// </summary>
public static class FrameCodec
{
    /// <summary>
    /// 解析一帧。不认识的 <c>type</c> 返回的 <see cref="ParsedFrame.Frame"/> 为 null
    /// （= 原 switch 落空，仍会记 RX 日志）。
    /// </summary>
    public static ParsedFrame Parse(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeEl))
            return new ParsedFrame();

        // ⚠ 这一句保持原来的写法：type 不是字符串时 GetString() 会抛，
        //   由接收循环记「消息解析失败」，与重构前逐字一致。
        var kind = typeEl.GetString() ?? "";

        // ★ 收包日志的附加段：message 帧带上 message_id（原文取值，日志格式不变）
        var logExtra = "";
        if (kind == FrameTypes.Message && root.TryGetProperty("message_id", out var midEl))
            logExtra = " message_id=" + midEl.ToString();

        var rawEl = root.Clone();
        CoreFrame? frame;

        switch (kind)
        {
            case FrameTypes.Hello:
                frame = new HelloFrame
                {
                    Type = kind,
                    Raw = rawEl,
                    Token = ReadStringOrNull(root, "token"),
                };
                break;

            case FrameTypes.Message:
                frame = new MessageFrame
                {
                    Type = kind,
                    Raw = rawEl,
                    MessageId = ReadInt64(root, "message_id"),
                    SenderName = ReadString(root, "sender_name"),
                    Content = ReadString(root, "content"),
                    MessageType = ReadString(root, "message_type"),
                    CreatedAt = ReadString(root, "created_at"),
                    AutoCloseSeconds = ReadInt32OrNull(root, "auto_close_seconds"),
                    History = ReadElement(root, "history"),
                    Redelivered = ReadBool(root, "redelivered"),
                };
                break;

            case FrameTypes.ScreenshotRequest:
            {
                // 原判据：拿不到 request_id 就无法应答，不派发事件 —— 这里不产出帧
                var requestId = ReadString(root, "request_id");
                frame = string.IsNullOrEmpty(requestId)
                    ? null
                    : new ScreenshotRequestFrame
                    {
                        Type = kind,
                        Raw = rawEl,
                        RequestId = requestId,
                    };
                break;
            }

            case FrameTypes.ReplyAck:
                frame = new ReplyAckFrame
                {
                    Type = kind,
                    Raw = rawEl,
                    ClientId = ReadString(root, "client_id"),
                    MessageId = ReadInt64OrNull(root, "message_id"),
                    Status = ReadString(root, "status"),
                    CreatedAt = ReadString(root, "created_at"),
                };
                break;

            case FrameTypes.HistoryResponse:
                frame = new HistoryFrame
                {
                    Type = kind,
                    Raw = rawEl,
                    RequestId = ReadString(root, "request_id"),
                    DeviceId = ReadString(root, "device_id"),
                    Messages = ReadElement(root, "messages"),
                };
                break;

            case FrameTypes.Shutdown:
                frame = new ShutdownCommandFrame
                {
                    Type = kind,
                    Raw = rawEl,
                    DelaySeconds = ReadInt32OrNull(root, "delay_seconds"),
                };
                break;

            case FrameTypes.UnlockRequest:
                frame = new UnlockRequestFrame
                {
                    Type = kind,
                    Raw = rawEl,
                    RequestId = ReadStringOrNull(root, "request_id"),
                };
                break;

            case FrameTypes.HeartbeatAck:
                frame = new HeartbeatAckFrame { Type = kind, Raw = rawEl };
                break;

            default:
                frame = null;      // 不认识的 type：原实现落到 switch 之外，什么都不做
                break;
        }

        return new ParsedFrame { Type = kind, LogExtra = logExtra, Frame = frame };
    }

    // ---------------- 字段取值（容错：类型不对当缺省）----------------

    private static string ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? ""
            : "";

    private static string? ReadStringOrNull(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static long ReadInt64(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number &&
        el.TryGetInt64(out var value)
            ? value
            : 0;

    private static long? ReadInt64OrNull(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number &&
        el.TryGetInt64(out var value)
            ? value
            : null;

    private static int? ReadInt32OrNull(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number &&
        el.TryGetInt32(out var value)
            ? value
            : null;

    private static bool ReadBool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;

    /// <summary>取原文子元素（已 Clone，脱离 JsonDocument 的生命周期）；没有这个字段时返回未定义值。</summary>
    private static JsonElement ReadElement(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) ? el.Clone() : default(JsonElement);
}

/// <summary>
/// 协议里的帧类型字面量（上/下行共用一份，避免各处拼字符串 —— 拼错不会报错、只会静默失效）。
/// </summary>
public static class FrameTypes
{
    // ── 服务端 → 客户端 ──
    public const string Hello = "hello";
    public const string Message = "message";
    public const string ReplyAck = "reply_ack";
    public const string HistoryResponse = "history_response";
    public const string ScreenshotRequest = "screenshot_request";
    public const string Shutdown = "shutdown";
    public const string UnlockRequest = "unlock_request";
    public const string HeartbeatAck = "heartbeat_ack";

    // ── 客户端 → 服务端 ──
    public const string Heartbeat = "heartbeat";
    public const string Ack = "ack";
    public const string Reply = "reply";
    public const string HistoryRequest = "history_request";
    public const string ScreenshotResponse = "screenshot_response";
    public const string UnlockResult = "unlock_result";
    public const string Event = "event";
}
