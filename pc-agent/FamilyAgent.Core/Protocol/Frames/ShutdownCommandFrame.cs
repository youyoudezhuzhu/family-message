namespace FamilyAgent.Core.Protocol.Frames;

/// <summary>
/// <c>shutdown</c>：网页端点了「关机」→ 由本机执行。
/// </summary>
public sealed class ShutdownCommandFrame : CoreFrame
{
    /// <summary>
    /// 帧里要求的延迟秒数（<c>delay_seconds</c>）；帧里没带、或不是数字时为 null。
    ///
    /// ⚠ 「缺省值取几秒」不在这里决定：那是平台层的值
    ///   （Windows 侧 <c>PowerControl.DefaultDelaySeconds = 5</c>，
    ///   见 <c>App.OnShutdownRequested</c>）。Core 不认识平台的默认值，
    ///   所以原实现里 <c>var delay = PowerControl.DefaultDelaySeconds;</c> 这一句
    ///   留在宿主，行为不变。
    /// </summary>
    public int? DelaySeconds { get; init; }
}
