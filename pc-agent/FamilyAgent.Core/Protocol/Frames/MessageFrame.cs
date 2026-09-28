using System.Text.Json;

namespace FamilyAgent.Core.Protocol.Frames;

/// <summary>
/// <c>message</c>：服务端推来的新消息（群聊广播，见 server/main.py 的 <c>_device_payload</c>）。
///
/// 字段与服务端实际发出的形状一一对应（server/main.py:300-316）：
/// <c>message_id / sender_name / content / message_type / created_at / status /
/// auto_close_seconds / history</c>，补投时额外带 <c>redelivered: true</c>。
/// </summary>
public sealed class MessageFrame : CoreFrame
{
    /// <summary>服务端消息 id（SQLite INTEGER 自增，客户端统一用 long）。</summary>
    public long MessageId { get; init; }

    /// <summary>发送者昵称（页面弹窗标题用它）。</summary>
    public string SenderName { get; init; } = "";

    /// <summary>正文。</summary>
    public string Content { get; init; } = "";

    /// <summary>消息类型（现状恒为 <c>text</c>）。</summary>
    public string MessageType { get; init; } = "";

    /// <summary>服务端创建时间（ISO 字符串，页面直接显示）。</summary>
    public string CreatedAt { get; init; } = "";

    /// <summary>服务端希望的弹窗存活秒数；帧里没带时为 null（宿主原按 0 处理）。</summary>
    public int? AutoCloseSeconds { get; init; }

    /// <summary>
    /// 群聊最近的往来（服务端按 viewer 设备裁剪过的数组）。**仍是原文数组**：
    /// 强类型化为 <c>HistoryEntry</c> 列表是 Phase 4（SyncService）的事，
    /// Phase 1 宿主侧那条「逐字保留」的投递路径读的就是它。
    /// </summary>
    public JsonElement History { get; init; }

    /// <summary>true = 这是离线补投的历史消息（服务端只在补投时带这个字段）。</summary>
    public bool Redelivered { get; init; }
}
