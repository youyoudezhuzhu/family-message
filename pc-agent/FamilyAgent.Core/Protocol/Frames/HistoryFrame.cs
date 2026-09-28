using System.Text.Json;

namespace FamilyAgent.Core.Protocol.Frames;

/// <summary>
/// <c>history_response</c>：本机主动拉历史（<c>history_request</c>）的应答
/// （server/main.py:895-905）。
/// </summary>
public sealed class HistoryFrame : CoreFrame
{
    /// <summary>请求时带的 request_id（现状宿主未做配对，留着与协议对齐）。</summary>
    public string RequestId { get; init; } = "";

    /// <summary>服务端判定的设备号（现状宿主未使用）。</summary>
    public string DeviceId { get; init; } = "";

    /// <summary>消息数组原文（宿主原样转给页面渲染，见 <c>App.OnHistoryReceived</c>）。</summary>
    public JsonElement Messages { get; init; }
}
