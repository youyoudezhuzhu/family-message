using System;
using FamilyAgent.Core.Diagnostics;
using WinForms = System.Windows.Forms;

namespace FamilyAgent.Platform;

/// <summary>
/// WebView2 不可用时的**原生通知回落**（docs/CORE-REFACTOR-PLAN.md §6 Phase 2-5 / §8.11 方案 (a)）。
///
/// 为什么需要它：Core 已经做到「消息落盘 + ACK 有终态」，但「收到消息」这件事对用户
/// 仍然应当**看得见**。界面（WebView2 + 本地页）挂了、或压根没装 Runtime 时，
/// 唯一还能用的呈现方式就是托盘气泡（<c>NotifyIcon.ShowBalloonTip</c>，
/// 宿主里已有现成用法：<c>App.xaml.cs</c> 的关机提示与版本不一致提示）。
///
/// 边界：本类**不认识 MessageManager，也不发 ack** —— 它只回答「通知弹出去了没有」，
/// 由 <c>App</c> 根据返回值决定回报 <c>NotifyFallbackDisplayed</c>（弹出去了 →
/// <c>popup_displayed</c>）还是 <c>NotifyDisplayUnavailable</c>（弹不出去 →
/// <c>device_received</c>）。**不谎报**是这里的第一原则（§8.11）。
///
/// 托盘图标可能还不存在（headless 实例没有托盘；交互式实例在 <c>SetupTray()</c>
/// 之前也可能没有）—— 那种情况如实返回 <c>false</c>，不要假装提醒过了。
/// </summary>
public sealed class FallbackNotifier
{
    /// <summary>气泡停留时长（毫秒）。比关机提示（6000）长一点：消息要看得清。</summary>
    private const int BalloonMs = 8000;

    private readonly Func<WinForms.NotifyIcon?> _tray;
    private readonly Action<Action> _dispatch;

    /// <param name="tray">取当前托盘图标（可能还没建出来 → null）。</param>
    /// <param name="dispatch">把动作切到 UI 线程执行（托盘气泡是 WinForms 组件）。</param>
    public FallbackNotifier(Func<WinForms.NotifyIcon?> tray, Action<Action> dispatch)
    {
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
    }

    /// <summary>
    /// 用托盘气泡提醒一条消息。返回 true = 真的弹出去了；false = 没有可用的通知手段
    /// （调用方据此如实回报「无法显示」，不要谎报 <c>popup_displayed</c>）。
    /// </summary>
    public bool Notify(string? senderName, string? content, string reason)
    {
        if (_tray() is null)
        {
            AgentLog.Write($"[MSG] 原生通知回落不可用（没有托盘图标）：{reason}");
            return false;
        }

        var shown = false;
        try
        {
            _dispatch(() =>
            {
                var tip = _tray();
                if (tip is null)
                    return;
                tip.ShowBalloonTip(BalloonMs, Title(senderName), Trim(content), WinForms.ToolTipIcon.Info);
                shown = true;
            });
        }
        catch (Exception ex)
        {
            AgentLog.Write("[MSG] 原生通知回落失败：" + ex.Message);
            return false;
        }

        if (shown)
            AgentLog.Write($"[MSG] 已用原生通知提醒（{reason}）：{Title(senderName)}");
        return shown;
    }

    private static string Title(string? senderName) =>
        string.IsNullOrWhiteSpace(senderName) ? "家庭消息" : "家庭消息 · " + senderName.Trim();

    /// <summary>气泡正文有长度限制（超长会显示不全），截断并给出省略号。</summary>
    private static string Trim(string? content)
    {
        var text = (content ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= 200 ? text : text[..200] + "…";
    }
}
