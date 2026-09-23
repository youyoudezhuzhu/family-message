using System;

namespace FamilyAgent;

/// <summary>弹窗右侧历史对话里的一条。</summary>
public sealed class HistoryItem
{
    public long MessageId { get; init; }
    public string SenderName { get; init; } = "";
    public string Content { get; init; } = "";
    public string Time { get; init; } = "";
    /// <summary>true = 这台 PC 发出去的（自己的回复）。</summary>
    public bool IsOut { get; init; }
    /// <summary>true = 就是当前正在显示的这一条。</summary>
    public bool IsCurrent { get; init; }

    public string Header =>
        SenderName + (string.IsNullOrEmpty(Time) ? "" : " · " + Time);

    public static HistoryItem FromJson(System.Text.Json.JsonElement el, long currentId)
    {
        var id = el.TryGetProperty("message_id", out var idEl) && idEl.ValueKind ==
                 System.Text.Json.JsonValueKind.Number
            ? idEl.GetInt64()
            : 0;
        var created = el.TryGetProperty("created_at", out var t) ? t.GetString() ?? "" : "";
        return new HistoryItem
        {
            MessageId = id,
            SenderName = el.TryGetProperty("sender_name", out var s) ? s.GetString() ?? "" : "",
            Content = el.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "",
            Time = created.Length > 11 ? created[11..] : created,
            IsOut = el.TryGetProperty("direction", out var d) && d.GetString() == "out",
            IsCurrent = id != 0 && id == currentId,
        };
    }

    public static HistoryItem Sent(string senderName, string content, DateTime at) => new()
    {
        SenderName = senderName,
        Content = content,
        Time = at.ToString("HH:mm:ss"),
        IsOut = true,
    };
}
