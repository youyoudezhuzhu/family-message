using System.Text.Json;

namespace FamilyAgent.Core.Protocol.Frames;

/// <summary>
/// 服务端 → 客户端 的一帧，**强类型**（取代原来到处传的裸 <c>JsonElement</c>）。
///
/// <c>FrameCodec.Parse</c> 把收到的 JSON 映射成这些派生类型；认识的 type 才产出实例，
/// 不认识的 type 返回 <c>null</c>（与原 <c>Dispatch</c> 里 switch 落空等价）。
///
/// ⚠ <see cref="Raw"/> 是 Phase 1 的**过渡桥**：宿主侧现有的渲染与校验实现
///   （页面投递、解锁校验）仍然按字段名直接读 JSON，Phase 1 要求这些回调体
///   「逐字保留」（§Phase 1-4），所以强类型帧同时带上原文。
///   Phase 2 起 MessageDisplaySink / CommandRouter 接过去之后，这个属性会逐步退出。
/// </summary>
public abstract class CoreFrame
{
    /// <summary>帧里的 <c>type</c> 字面量（取值见 <see cref="FrameTypes"/>）。</summary>
    public string Type { get; init; } = "";

    /// <summary>原始 JSON 对象（已 Clone，脱离 JsonDocument 生命周期）。</summary>
    public JsonElement Raw { get; init; }
}
