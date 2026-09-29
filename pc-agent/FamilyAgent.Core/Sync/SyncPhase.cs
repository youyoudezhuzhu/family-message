namespace FamilyAgent.Core.Sync;

/// <summary>
/// 客户端上线后的握手阶段（docs/CORE-REFACTOR-PLAN.md §6 Phase 4，需求 §8）。
///
/// 需求原文：
/// <code>
/// Connect → Authenticate → Sync / History → 补齐未确认消息 → 进入实时模式
/// </code>
/// 「因此需要区分：已发送 / 已投递 / 已 ACK / 已显示」
/// （四态在本机由 <c>Messaging.DeliveryReport</c> 回答；本枚举只负责**握手走到哪一步**）。
///
/// ★ 为什么值得单独一个状态机：以前客户端只有「连上了 / 没连上」两个状态，
///   「连上了但还没补齐」这段窗口是隐形的 —— 期间到达的消息既不进任何补齐流程，
///   日志里也看不出来客户端到底有没有完成「离线补投」。Android 客户端将来要复用同一套
///   语义（需求 §9：同一 ACK / Sync 机制），所以它必须是显式的。
/// </summary>
public enum SyncPhase
{
    /// <summary>没有连接（启动前、断开后、被服务端顶替后）。</summary>
    Disconnected = 0,

    /// <summary>连接对象已建立、还没握手完（Phase 4 内部用；对外以 <see cref="Authenticating"/> 上报）。</summary>
    Connecting = 1,

    /// <summary>WebSocket 已连上，等服务端下发的第一帧 <c>hello</c>（＝认证完成）。</summary>
    Authenticating = 2,

    /// <summary>认证完成，已发出 <c>history_request</c>，等 <c>history_response</c> 做补齐去重。</summary>
    Syncing = 3,

    /// <summary>补齐完成（或超时放行），进入实时模式。</summary>
    Live = 4,
}

/// <summary>阶段变化事件（Core 对外只抛事实，怎么显示由平台层决定）。</summary>
public sealed class SyncPhaseChangedArgs
{
    public SyncPhaseChangedArgs(SyncPhase previous, SyncPhase current, string detail)
    {
        Previous = previous;
        Current = current;
        Detail = detail ?? "";
    }

    public SyncPhase Previous { get; }

    public SyncPhase Current { get; }

    /// <summary>给日志/界面看的中文说明（含「这是第几步」的编号，见 <c>SyncService.Enter</c>）。</summary>
    public string Detail { get; }

    /// <summary>形如 <c>未连接 → 认证中</c>。</summary>
    public string Describe() => Previous.Describe() + " → " + Current.Describe();
}

/// <summary>阶段取值的中文说法（集中一处，避免各处拼字符串）。</summary>
public static class SyncPhaseExtensions
{
    public static string Describe(this SyncPhase phase) => phase switch
    {
        SyncPhase.Disconnected => "未连接",
        SyncPhase.Connecting => "连接中",
        SyncPhase.Authenticating => "认证中",
        SyncPhase.Syncing => "补齐中",
        SyncPhase.Live => "实时模式",
        _ => phase.ToString(),
    };
}
