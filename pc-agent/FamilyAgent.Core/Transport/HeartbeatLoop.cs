using System;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FamilyAgent.Core.Commands.Abstractions;
using FamilyAgent.Core.Devices;
using FamilyAgent.Core.Diagnostics;
using FamilyAgent.Core.Protocol;

namespace FamilyAgent.Core.Transport;

/// <summary>
/// 心跳循环（原 <c>AgentClient.HeartbeatLoopAsync</c> :304-357 原样搬来，
/// 见 docs/CORE-REFACTOR-PLAN.md §3.2 / §4）。
///
/// 它有两个不靠异常也能发现「发送链路已死」的看门狗：
/// <list type="number">
///   <item>45 秒收不到心跳回执 → 判定发送链路断了（真实故障：连接「能收不能发」，
///     网页发来的消息照收、本机发出去的却上不去，而且不会有任何异常）；</item>
///   <item>有消息积压超过 12 秒仍发不出去 → 同样强制重连。</item>
/// </list>
///
/// ★ 看门狗的时间字段（<c>_lastAckUtc</c> / <c>_lastBeatUtc</c>）现在是**本类的实例字段**，
///   每个连接一代一个实例 —— 原实现里它们是 <c>AgentClient</c> 的共享字段、不受代次保护，
///   两代循环短暂重叠时会互相干扰（§8.7）。搬迁之后这条自动消解，**语义未改**：
///   字段的读写点、判定阈值与日志文案都与原来逐字一致。
/// </summary>
public sealed class HeartbeatLoop
{
    private readonly WebSocketTransport _transport;
    private readonly Outbox _outbox;
    private readonly IPlatformInfo _platform;
    private readonly PlatformCapabilities _capabilities;

    /// <summary>最近一次收到心跳回执（含 <c>hello</c>，它也算「链路活着」的证据）。</summary>
    private DateTime _lastAckUtc = DateTime.UtcNow;

    /// <summary>最近一次发出心跳。</summary>
    private DateTime _lastBeatUtc = DateTime.MinValue;

    /// <param name="capabilities">
    /// 本平台注册了哪些能力实现（Phase 3）—— 心跳里的 <c>capabilities</c> 由它推导，
    /// 与连接串上的那个参数**同源**（都来自 <c>CapabilityReporter</c>），
    /// 免得两处报出不一样的能力表。
    /// </param>
    public HeartbeatLoop(WebSocketTransport transport, Outbox outbox, IPlatformInfo platform,
                         PlatformCapabilities capabilities)
    {
        _transport = transport;
        _outbox = outbox;
        _platform = platform;
        _capabilities = capabilities;
    }

    /// <summary>
    /// 收到心跳回执（或 <c>hello</c>）→ 重置往返看门狗。
    /// 由 <c>ConnectionManager</c> 在解析到对应帧时通知**本代**的循环实例。
    /// </summary>
    public void NotifyAckReceived() => _lastAckUtc = DateTime.UtcNow;

    public async Task RunAsync(ClientWebSocket ws, string connId, CancellationToken ct)
    {
        _lastBeatUtc = DateTime.UtcNow;
        _lastAckUtc = DateTime.UtcNow;

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                var now = DateTime.UtcNow;

                // ① 心跳是「发出去 + 收回来」的完整往返。收不到回执就说明发送链路
                //    已经死了 —— 哪怕还能正常收到网页发来的消息。这是唯一能发现
                //    「能收不能发」的手段，不能再等用户手动去点「保存并连接」。
                if (now - _lastAckUtc > TimeSpan.FromSeconds(45))
                {
                    AgentLog.Write($"[WS] connection={connId} ⚠ 45 秒没收到心跳回执，判定发送链路已断，强制重连");
                    WebSocketTransport.TryAbort(ws);
                    return;
                }

                // ② 有消息积压却迟迟发不出去 → 同样强制重连
                var outboxCount = _outbox.Count;
                var backlogSince = _outbox.BacklogSinceUtc;
                if (outboxCount > 0 && backlogSince != DateTime.MinValue &&
                    now - backlogSince > TimeSpan.FromSeconds(12))
                {
                    AgentLog.Write($"[WS] connection={connId} ⚠ {outboxCount} 条消息积压超过 12 秒仍未发出，强制重连");
                    WebSocketTransport.TryAbort(ws);
                    return;
                }

                // ③ 正常心跳（每 15 秒一次）。帧里除了 type 还带 Windows 会话状态和
                //    本机能力（远程解锁 Phase 1）；状态一变会另发一次即时上报。
                if (now - _lastBeatUtc >= TimeSpan.FromSeconds(15))
                {
                    _lastBeatUtc = now;
                    await _transport.SendRawAsync(ws, BuildHeartbeatJson(_platform, _capabilities), ct)
                                  .ConfigureAwait(false);
                }

                if (_outbox.Count > 0)
                    _outbox.Flush();
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            // 心跳发不出去也要掐掉连接触发重连，否则主循环会一直卡在接收上
            AgentLog.Write($"[WS] connection={connId} 心跳循环结束：" + ex.Message);
            WebSocketTransport.TryAbort(ws);
        }
    }

    // ── Windows 会话状态 / 能力上报（远程解锁 Phase 1）────────────────
    //
    // 两个新字段都挂在原有心跳帧上，不新增帧类型，也不改心跳周期。

    /// <summary>
    /// 心跳帧（PC → NAS）。<c>windows_state</c> / <c>capabilities</c> 是远程解锁
    /// Phase 1 新增的字段。
    ///
    /// 状态每次现检测（<see cref="IPlatformInfo.SessionState"/> 的实现必须现算，不吃缓存）——
    /// 会话状态可能刚在这 15 秒里变过，报旧值会让网页端显示错的状态。
    /// </summary>
    /// <param name="capabilities">
    /// Phase 3 起能力清单由「注册了哪些实现」推导（<see cref="PlatformCapabilities"/>）——
    /// 不在当前平台上的能力**不会**出现在这一帧里，与连接串上的 <c>capabilities=</c> 同源。
    /// </param>
    public static string BuildHeartbeatJson(IPlatformInfo platform, PlatformCapabilities capabilities) =>
        JsonSerializer.Serialize(new
        {
            type = FrameTypes.Heartbeat,
            windows_state = platform.SessionState,
            capabilities = DeviceCapabilities.Build(platform, capabilities),
        });
}
