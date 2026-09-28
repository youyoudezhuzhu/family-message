namespace FamilyAgent.Core.Protocol.Frames;

/// <summary>
/// <c>heartbeat_ack</c>：心跳回执。
///
/// 它没有业务载荷 —— 唯一的含义是「发送链路还活着」，是发现
/// 「能收不能发」半死连接的唯一证据（见 <c>HeartbeatLoop</c> 的 45 秒往返看门狗）。
/// 单独一个类型是为了让接收侧的 switch 不再靠字符串比较分派。
/// </summary>
public sealed class HeartbeatAckFrame : CoreFrame
{
}
