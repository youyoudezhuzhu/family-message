using System.Threading.Tasks;

namespace FamilyAgent.Core.Commands;

/// <summary>
/// 命令应答的**出口**：Core 把 <c>screenshot_response</c> / <c>unlock_result</c> / <c>event</c>
/// 交出去的方式。
///
/// 为什么要有这一层（而不是让 <see cref="CommandRouter"/> 直接拿
/// <c>Transport.ConnectionManager</c>）：Core 的单测必须能在 Linux / CI 上跑
/// （§8.8 —— 那台机器连 WebView2 都没有，更不会给你一个真实的 WebSocket），
/// 没有这层就只能靠「联机手工验证」，而 Phase 3 的核心判据恰恰是
/// 「**不支持的能力要回明确 error，而不是超时**」，它必须可单测。
///
/// 实现：<c>Transport.ConnectionManager</c>（方法名与它原有的公开方法一致，没有新增语义）。
/// 单测：<c>FamilyAgent.Core.Tests</c> 里的假实现。
/// </summary>
public interface ICommandChannel
{
    /// <summary>
    /// 回一条 <c>screenshot_response</c>。<paramref name="base64"/> 为空 =
    /// 走 error 分支（带上 <paramref name="error"/> 里的中文原因），与
    /// <c>AgentClient.SendScreenshotAsync</c> 原实现的判定完全一致。
    /// </summary>
    Task SendScreenshotAsync(string requestId, string? base64, int width, int height, string? error);

    /// <summary>回一条 <c>unlock_result</c>。</summary>
    void UnlockResult(string requestId, string status, string reason);

    /// <summary>
    /// 回一条 <c>event</c>（<c>{type:"event", kind, detail}</c>）。
    /// 例如关机成功 <c>kind="shutdown"</c>、失败 <c>kind="shutdown_failed"</c>。
    /// </summary>
    void SendEvent(string kind, string detail);
}
