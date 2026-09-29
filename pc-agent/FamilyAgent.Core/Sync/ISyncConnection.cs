using System;
using FamilyAgent.Core.Events;
using FamilyAgent.Core.Protocol.Frames;

namespace FamilyAgent.Core.Sync;

/// <summary>
/// <see cref="SyncService"/> 眼里的「一条连接」（docs/CORE-REFACTOR-PLAN.md §6 Phase 4）。
///
/// 为什么要这层接口而不是直接吃 <c>Transport.ConnectionManager</c>：
/// <list type="number">
///   <item><b>可测</b>：单测要能造出「断开 → 服务端补投 → 重连后重新投递」这种现场，
///     而真的 <c>ConnectionManager</c> 里是一条 <c>ClientWebSocket</c> 的连接循环，
///     在 Linux 单测里造不出来（也没必要）。假连接（<c>FakeSyncConnection</c>）
///     能精确控制「什么时候算连上、什么时候算认证完成、history 里有什么」。</item>
///   <item><b>Android 可复用</b>：需求 §9 要求未来的 Android 客户端实现同一套
///     Sync 语义 —— 只要它提供这四个成员，就能原样用同一个 <see cref="SyncService"/>。</item>
/// </list>
///
/// ⚠ 这只是**连接层的一个视图**，不替代 <c>ConnectionManager</c>：
///   连接代次、退避、心跳、Outbox 仍归它管（Phase 1 已绿）。
/// </summary>
public interface ISyncConnection
{
    /// <summary>连接状态变化（连上 / 断开）—— 与 Core 的强类型事件同源。</summary>
    event Action<ConnectionStateChangedArgs>? ConnectionStateChanged;

    /// <summary>服务端下发第一帧 <c>hello</c> ＝ 认证完成（进入补齐阶段的信号）。</summary>
    event Action<HelloFrame>? HelloReceived;

    /// <summary><c>history_response</c> 到达（补齐的数据源）。</summary>
    event Action<HistoryFrame>? HistoryReceived;

    /// <summary>
    /// 发一条 <c>history_request</c> 并把 <c>request_id</c> 还给调用方。
    ///
    /// 一条连接上可能同时存在两次历史请求（用户打开窗口时拉的 + 重连补齐时拉的），
    /// 不配对就无法知道该把哪一份拿去做合并去重 —— 所以这里必须能拿到 id。
    /// </summary>
    /// <returns>本次请求的 <c>request_id</c>（用来和 <c>history_response.request_id</c> 配对）。</returns>
    string RequestHistoryTracked(int limit);
}
