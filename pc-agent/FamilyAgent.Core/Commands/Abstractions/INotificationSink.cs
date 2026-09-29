using System.Threading.Tasks;

namespace FamilyAgent.Core.Commands.Abstractions;

/// <summary>
/// 平台能力：**通知**（WebView2 不可用时的原生提醒回落，docs/CORE-REFACTOR-PLAN.md §8.11 方案 (a)）。
///
/// 与另外三个能力接口的区别：**Core 不调用它**。Core 只抛「这条消息需要提醒」这个
/// **事件**（<c>Messaging.MessageManager.DisplayFallbackRequired</c>），由平台层接住并决定
/// 用什么手段提醒（Windows = 托盘气泡 <c>Platform/FallbackNotifier.cs</c>；Android = 系统通知）。
/// 所以这里定的是「平台层交给 Core 的回报形状」，而不是 Core 依赖的方向。
///
/// 返回值是契约的核心：<c>true</c> = 用户确实被提醒到了（平台层据此回报
/// <c>popup_displayed</c>）；<c>false</c> = 本机没有任何提醒手段（如实回报
/// <c>device_received</c>）。**不谎报**是这里的第一原则 —— 托盘图标还没建出来时必须返回 false。
///
/// ⚠ 能力表里**没有**对应的能力名：服务端的能力白名单只有
///   <c>message / screenshot / shutdown / unlock</c>（§5.2），通知是 message 能力的一部分。
/// </summary>
public interface INotificationSink
{
    /// <summary>
    /// 用原生通知提醒一条消息。返回 true = 真的弹出去了。
    /// </summary>
    /// <param name="senderName">发送者昵称（为空时用本机默认标题）。</param>
    /// <param name="content">消息正文（实现自己负责截断）。</param>
    /// <param name="reason">为什么走回落（例如 <c>display_timeout</c> / <c>webview2_unavailable</c>），只进日志。</param>
    bool Notify(string? senderName, string? content, string reason);
}
