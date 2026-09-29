namespace FamilyAgent.Core.Messaging;

/// <summary>
/// 「这条消息走到哪一步了」的四态（需求 §8 原文：<c>已发送 / 已投递 / 已 ACK / 已显示</c>）。
///
/// 线性递进，<see cref="None"/> 表示连第一条都没到。
/// </summary>
public enum DeliveryStage
{
    /// <summary>本机还不知道这条消息。</summary>
    None = 0,

    /// <summary>已发送：本机已收到并**落盘**（进程此刻死掉也不会丢）。</summary>
    Sent = 1,

    /// <summary>已投递：已经交给界面去渲染（页面收到了这一条）。</summary>
    Delivered = 2,

    /// <summary>已 ACK：对应的 <c>ack</c> 帧已经交给传输层。</summary>
    Acked = 3,

    /// <summary>已显示：界面回报「真的画到屏幕上了」（或原生通知回落已提醒到人）。</summary>
    Displayed = 4,
}

/// <summary>
/// 一条消息的四态快照（需求 §8 的「因此需要区分：已发送 / 已投递 / 已 ACK / 已显示」）。
///
/// <code>
/// 需求的说法        本类的属性                          与 §3.2 DeliveryState 的对应
/// 已发送            Sent（本机已落盘）                  Persisted 及以上
/// 已投递            Delivered（已交给界面渲染）         「已交给界面」那一步（NotifyDisplayDeferred / Displayed）
/// 已 ACK            Acked（ack 帧已交给传输层）          AckSent（只出现在事件流里，不落盘）
/// 已显示            Displayed（界面确认真的画出来）      Displayed / NotifiedFallback / Read
/// </code>
///
/// ★ 为什么要有它：需求要的是一句能回答的问题 ——「这条到底发出去了没、送到了没、
///   对方 ACK 了没、显示出来了没」。以前这四个答案散在三个地方
///   （<c>WebHostWindow._pending</c>、<c>MessageManager._handedToUi</c>、
///   服务端 <c>message_targets</c>），而且 UI 那条腿一断就全丢了。
///   现在**全部由 Core 回答**（<see cref="MessageManager.Report"/>）：
///   <list type="bullet">
///     <item>已发送 / 已显示 —— 来自本地库（跨进程存活）；</item>
///     <item>已投递 / 已 ACK —— 来自本进程的事实（跨进程的 ACK 事实由服务端记账 + 重连补齐重新对齐，
///       本机不假装知道，见 <see cref="MessageManager.Report"/> 的说明）。</item>
///   </list>
///
/// ⚠ 对外（服务端 / 网页端）**仍然只有单一状态 <c>sent</c>**（群聊模型，
///   <c>messages.py:27-31/135-146</c>）：这个四态是**本机的**记账，不是要改协议。
/// </summary>
public sealed class DeliveryReport
{
    internal DeliveryReport(long messageId, DeliveryState persistedState, bool sent, bool delivered,
                            bool acked, bool displayed)
    {
        MessageId = messageId;
        PersistedState = persistedState;
        Sent = sent;
        Delivered = delivered;
        Acked = acked;
        Displayed = displayed;
    }

    public long MessageId { get; }

    /// <summary>本地库里记的状态（<see cref="MessageManager.Report"/> 的补充信息；库里没有时是 <see cref="DeliveryState.Received"/>）。</summary>
    public DeliveryState PersistedState { get; }

    /// <summary>已发送：本机已收到并落盘。</summary>
    public bool Sent { get; }

    /// <summary>已投递：已经交给界面渲染。</summary>
    public bool Delivered { get; }

    /// <summary>已 ACK：<c>ack</c> 帧已交给传输层（证据在日志的 <c>→ ack:…</c> 行）。</summary>
    public bool Acked { get; }

    /// <summary>已显示：界面确认画到屏幕上了（或原生通知回落已提醒到人）。</summary>
    public bool Displayed { get; }

    /// <summary>已经到达的最高阶段（四态的线性答案）。</summary>
    public DeliveryStage Stage =>
        Displayed ? DeliveryStage.Displayed
        : Acked ? DeliveryStage.Acked
        : Delivered ? DeliveryStage.Delivered
        : Sent ? DeliveryStage.Sent
        : DeliveryStage.None;

    /// <summary>
    /// 一句话四态，直接可贴日志/界面：<c>已发送✓ 已投递✓ 已 ACK✓ 已显示—</c>。
    /// （未到达的用 <c>—</c>：一眼能看出卡在哪一步。）
    /// </summary>
    public string Describe() =>
        $"已发送{(Sent ? "✓" : "—")} 已投递{(Delivered ? "✓" : "—")}"
        + $" 已 ACK{(Acked ? "✓" : "—")} 已显示{(Displayed ? "✓" : "—")}";

    public override string ToString() => $"message_id={MessageId} [{Describe()}] 本地状态={PersistedState}";
}
