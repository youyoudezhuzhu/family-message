using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FamilyAgent.Core.Protocol;
using FamilyAgent.Core.Protocol.Frames;

namespace FamilyAgent.Core.Sync;

/// <summary>
/// <c>history_response.messages[]</c> 里的一项（服务端 <c>msg_svc.group_history()</c> 的形状：
/// <c>message_id / sender_name / content / created_at / direction</c>）。
///
/// ⚠ 它比实时 <c>message</c> 帧**少**几个字段（没有 <c>message_type</c> /
///   <c>status</c> / <c>auto_close_seconds</c> / <c>history</c>）—— 补齐时缺的字段
///   由 <see cref="HistoryBackfill.ToFrame"/> 补上缺省值，见那个方法的注释。
/// </summary>
public sealed class HistoryMessage
{
    public long MessageId { get; init; }

    public string SenderName { get; init; } = "";

    public string Content { get; init; } = "";

    public string CreatedAt { get; init; } = "";

    /// <summary><c>in</c> = 别人说的，<c>out</c> = 本机说的（服务端按 viewer 设备给的视角）。</summary>
    public string Direction { get; init; } = "";

    /// <summary>本机自己说的 —— 群聊里**不该弹自己的窗**（服务端实时广播也是这样排除发起者的）。</summary>
    public bool IsOwn => string.Equals(Direction, "out", StringComparison.OrdinalIgnoreCase);

    /// <summary>解析一项；<c>message_id</c> 不是正整数的项返回 null（无法去重/无法 ACK，交给上层计数即可）。</summary>
    public static HistoryMessage? Parse(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;

        long id = 0;
        if (el.TryGetProperty("message_id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
        {
            try { id = idEl.GetInt64(); } catch { id = 0; }
        }
        if (id <= 0)
            return null;

        return new HistoryMessage
        {
            MessageId = id,
            SenderName = ReadString(el, "sender_name"),
            Content = ReadString(el, "content"),
            CreatedAt = ReadString(el, "created_at"),
            Direction = ReadString(el, "direction"),
        };
    }

    private static string ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? ""
            : "";
}

/// <summary>
/// 一次「本地库 + history_response」合并的结果（纯数据，不含副作用 —— 见 <see cref="HistoryBackfill.Plan"/>）。
/// </summary>
public sealed class HistoryMergePlan
{
    internal HistoryMergePlan(IReadOnlyList<HistoryMessage> toDeliver, int total, int alreadyKnown,
                              int own, int baseline, long newWatermark)
    {
        ToDeliver = toDeliver;
        Total = total;
        AlreadyKnown = alreadyKnown;
        Own = own;
        Baseline = baseline;
        NewWatermark = newWatermark;
    }

    /// <summary>需要补投的消息（按 <c>message_id</c> 升序、已去重）。</summary>
    public IReadOnlyList<HistoryMessage> ToDeliver { get; }

    /// <summary>history_response 里可用的条数（没 id 的已算掉）。</summary>
    public int Total { get; }

    /// <summary>本地库里已经有（＝重复投递，必须去重）的条数。</summary>
    public int AlreadyKnown { get; }

    /// <summary>本机自己发的（<c>direction=out</c>，只推进水位、不补投）的条数。</summary>
    public int Own { get; }

    /// <summary>首次同步时只用来建立水位线、不补投的条数（见 <see cref="SyncService"/> 的说明）。</summary>
    public int Baseline { get; }

    /// <summary>合并后的水位（本机已知的最大 <c>message_id</c>）。</summary>
    public long NewWatermark { get; }

    /// <summary>给日志用的一句话。</summary>
    public string Describe() =>
        $"共 {Total} 条：补投 {ToDeliver.Count}、已在本地 {AlreadyKnown}（去重）、"
        + $"本机自己发的 {Own}、首次水位 {Baseline}；水位={NewWatermark}";
}

/// <summary>
/// 「用 history 补齐未确认消息」的纯逻辑（docs/CORE-REFACTOR-PLAN.md §6 Phase 4-1/2）。
///
/// 它要解决的问题（方案 §8.2 的**真实服务端缺口**）：设备发起的群聊消息服务端不写
/// <c>message_targets</c>（<c>messages.py:183-197</c>），而广播只推当前在线设备
/// （<c>main.py:319-340</c>）—— 于是「另一台 PC 在断线期间说的话」**永远不会**被补投给本机。
/// 本轮在客户端侧兜住：重连后用 <c>history_request</c> 拉最近 N 条，
/// 与本地 <c>MessageStore</c> 合并去重，把漏掉的补进正常流程（落盘 → 显示 → ACK）。
///
/// ★ 为什么还要水位（watermark）而不是「凡是本地没有的就补投」：
///   一台**新装**的客户端本地库是空的，而 history 里是最近 50 条**装之前就发生过**的事。
///   无条件补投 = 一装好就先弹 50 个全屏弹窗。所以第一次同步只建立水位线
///   （记住「我看到的旧世界最高到哪条」），此后只补投**水位之后**的消息
///   —— 语义上正好对应需求里的「补齐**未确认**消息」。
/// </summary>
public static class HistoryBackfill
{
    /// <summary>把 <c>history_response.messages</c>（JSON 数组原文）解析成列表；不是数组时返回空表。</summary>
    public static IReadOnlyList<HistoryMessage> ParseAll(JsonElement messages)
    {
        var result = new List<HistoryMessage>();
        if (messages.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var el in messages.EnumerateArray())
        {
            var parsed = HistoryMessage.Parse(el);
            if (parsed is not null)
                result.Add(parsed);
        }
        return result;
    }

    /// <summary>
    /// 合并计划：决定「哪些历史消息要补投」，并算出新水位。
    ///
    /// 规则（顺序即优先级）：
    /// <list type="number">
    ///   <item>丢掉没有有效 <c>message_id</c> 的项、以及同一 <c>message_id</c> 的重复项；</item>
    ///   <item><c>direction=out</c>（本机自己发的）**永不补投** —— 只推进水位（否则会弹自己的窗）；</item>
    ///   <item>首次同步（<paramref name="watermarkKnown"/> = false）只建立水位线，什么都不补投；</item>
    ///   <item>其余项：<c>message_id &lt;= 水位</c> 或本地库里已有 → 记「已在本地」（去重）；
    ///     否则进补投列表（升序）。</item>
    /// </list>
    /// </summary>
    /// <param name="incoming">本次 history_response 里的消息。</param>
    /// <param name="watermark">本机已知的最大 message_id。</param>
    /// <param name="watermarkKnown">false = 本机还没有任何水位（本地库为空且从未同步过）。</param>
    /// <param name="contains">本地库里是否已有这条（宿主传 <c>store.Contains</c>；单测传假 store）。</param>
    public static HistoryMergePlan Plan(IReadOnlyList<HistoryMessage> incoming, long watermark,
                                        bool watermarkKnown, Func<long, bool> contains)
    {
        if (incoming is null)
            return new HistoryMergePlan(Array.Empty<HistoryMessage>(), 0, 0, 0, 0, watermark);

        // ① 排序 + 去重（服务端给的是升序，但**不依赖**它：合并的正确性不能建立在“对端守规矩”上）
        var ordered = new List<HistoryMessage>(incoming);
        ordered.Sort((a, b) => a.MessageId.CompareTo(b.MessageId));

        var unique = new List<HistoryMessage>(ordered.Count);
        var seen = new HashSet<long>();
        foreach (var item in ordered)
        {
            if (item is null || item.MessageId <= 0 || !seen.Add(item.MessageId))
                continue;
            unique.Add(item);
        }

        var newWatermark = watermark;
        foreach (var item in unique)
            if (item.MessageId > newWatermark)
                newWatermark = item.MessageId;

        var toDeliver = new List<HistoryMessage>();
        var alreadyKnown = 0;
        var own = 0;

        // ② 首次同步：只建立水位线
        if (!watermarkKnown)
            return new HistoryMergePlan(toDeliver, unique.Count, 0, 0, unique.Count, newWatermark);

        // ③ 常规合并
        foreach (var item in unique)
        {
            if (item.IsOwn)
            {
                own++;
                continue;
            }
            if (item.MessageId <= watermark || (contains is not null && contains(item.MessageId)))
            {
                alreadyKnown++;
                continue;
            }
            toDeliver.Add(item);
        }

        return new HistoryMergePlan(toDeliver, unique.Count, alreadyKnown, own, 0, newWatermark);
    }

    /// <summary>
    /// 把一条历史消息变成一帧**形状与实时 <c>message</c> 帧一致**的强类型帧 ——
    /// 这样补投走的就是与实时消息完全相同的那条路（<c>MessageManager.HandleIncoming</c>：
    /// 落盘 → 去重 → 显示 → ACK），界面拿到的字段也与实时路径一致。
    ///
    /// 字段对照（对齐服务端 <c>_device_payload()</c>，<c>main.py:291-316</c>）：
    /// <list type="bullet">
    ///   <item><c>message_type</c> 补 <c>"text"</c>（现状恒为 text，服务端历史里没这个字段）；</item>
    ///   <item><c>status</c> 补 <c>"sent"</c>（群聊模型对外只有这一个状态）；</item>
    ///   <item><c>auto_close_seconds</c> 用 <paramref name="autoCloseSeconds"/> —— 历史里没有这个值，
    ///     由 <see cref="SyncService"/> 取「本地最近一条实时消息的取值」当参照，
    ///     保证补投的弹窗**也会自动关闭**（不会因为补投就变成永不关闭）；</item>
    ///   <item><c>history</c> 用本次 history_response 的整段（服务端实时帧里带的也是同一份
    ///     <c>group_history(viewer_device_id=本机)</c>，视角一致）；</item>
    ///   <item><c>redelivered: true</c> —— 如实标明这是补投，不是实时到达。</item>
    /// </list>
    /// </summary>
    public static MessageFrame ToFrame(HistoryMessage message, JsonElement history, int autoCloseSeconds)
    {
        var raw = BuildJson(message, history, autoCloseSeconds);

        return new MessageFrame
        {
            Type = FrameTypes.Message,
            Raw = raw,
            MessageId = message.MessageId,
            SenderName = message.SenderName,
            Content = message.Content,
            MessageType = "text",
            CreatedAt = message.CreatedAt,
            AutoCloseSeconds = autoCloseSeconds,
            History = history,
            Redelivered = true,
        };
    }

    private static JsonElement BuildJson(HistoryMessage message, JsonElement history, int autoCloseSeconds)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", FrameTypes.Message);
            writer.WriteNumber("message_id", message.MessageId);
            writer.WriteString("sender_name", message.SenderName);
            writer.WriteString("content", message.Content);
            writer.WriteString("message_type", "text");
            writer.WriteString("created_at", message.CreatedAt);
            writer.WriteString("status", "sent");
            writer.WriteNumber("auto_close_seconds", autoCloseSeconds);
            if (history.ValueKind == JsonValueKind.Array)
            {
                writer.WritePropertyName("history");
                history.WriteTo(writer);
            }
            writer.WriteBoolean("redelivered", true);
            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }
}
