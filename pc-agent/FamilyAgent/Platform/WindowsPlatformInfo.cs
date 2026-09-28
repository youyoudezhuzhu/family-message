using System;
using FamilyAgent.Core.Commands.Abstractions;

namespace FamilyAgent.Platform;

/// <summary>
/// Windows 侧的 <see cref="IPlatformInfo"/> 实现 —— 把原来 Core 直读的两个 UI / 平台静态成员
/// **包起来**，行为逐字不变（docs/CORE-REFACTOR-PLAN.md §3.2 / §4）：
///
/// <list type="bullet">
///   <item><see cref="IsHeadless"/> ← <c>App.IsHeadless</c>（原 <c>AgentClient.cs:381</c> 的直接读）；</item>
///   <item><see cref="SessionState"/> ← <c>SessionState.Current</c>（原 <c>AgentClient.cs:401</c>）。
///     那个属性**每次都真实检测**（OpenInputDesktop），本类照旧只做转发，不加缓存 ——
///     报旧状态会让网页端显示错的状态。</item>
/// </list>
///
/// 单例不是必须的：两个属性都现读现算，随便 new 几个结果一样。
/// </summary>
public sealed class WindowsPlatformInfo : IPlatformInfo
{
    /// <summary>见 <c>App.IsHeadless</c>：<c>--headless</c>（会话 0，无人登录）。</summary>
    public bool IsHeadless => App.IsHeadless;

    /// <summary>连接查询串里的 <c>platform=</c>，与搬迁前 <c>BuildUri()</c> 的取值一致。</summary>
    public string Platform => "Windows " + Environment.OSVersion.Version;

    /// <summary>
    /// 当前 Windows 会话状态（<c>unknown</c> / <c>logon_screen</c> / <c>locked</c> / <c>unlocked</c>）。
    ///
    /// ⚠ 用全限定名 <c>global::FamilyAgent.SessionState</c> 是刻意的：
    ///   本接口的属性也叫 SessionState，直接写 <c>SessionState.Current</c> 会落到属性上。
    /// </summary>
    public string SessionState => global::FamilyAgent.SessionState.Current;
}
