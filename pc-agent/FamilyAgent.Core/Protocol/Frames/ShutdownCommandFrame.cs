namespace FamilyAgent.Core.Protocol.Frames;

/// <summary>
/// <c>shutdown</c>：网页端点了「关机」→ 由本机执行。
/// </summary>
public sealed class ShutdownCommandFrame : CoreFrame
{
    /// <summary>
    /// 帧里要求的延迟秒数（<c>delay_seconds</c>）；帧里没带、或不是数字时为 null。
    ///
    /// ⚠ 「缺省值取几秒」不在这里决定：那是平台层的值 —— Phase 3 起由能力接口
    ///   <c>Commands.Abstractions.IPowerProvider.DefaultDelaySeconds</c> 回答
    ///   （Windows 实现 = <c>PowerControl.DefaultDelaySeconds = 5</c>），
    ///   派发时在 <c>Commands.CommandRouter</c> 里取。Core 仍然不认这个数字本身，
    ///   与本帧原来的注释语义一致（原实现里那一句在宿主的
    ///   <c>App.OnShutdownRequested</c>，Phase 3 随命令派发搬进 Core）。
    /// </summary>
    public int? DelaySeconds { get; init; }
}
