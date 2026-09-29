using System.Collections.Generic;
using FamilyAgent.Core.Commands.Abstractions;

namespace FamilyAgent.Core.Devices;

/// <summary>
/// **能力上报**（docs/CORE-REFACTOR-PLAN.md §6 Phase 3-1 / §3.2 的
/// <c>Devices/CapabilityReporter.cs</c>）：把「本机的平台事实」
/// （注入的 <see cref="IPlatformInfo"/> + 注册了哪些能力实现）算成
/// **协议里的那一串能力名**，供连接层在两处使用：
///
/// <list type="number">
///   <item>
///     <b>WS 连接串</b>（<c>Transport.ConnectionManager.BuildUri()</c>，逗号分隔）——
///     服务端 <c>server/main.py:740-741</c> 在 <c>accept()</c> 之前就读它，
///     所以网页端**不用等第一次心跳**就知道这台机器能做什么。
///     ⚠ Phase 3 之前客户端**没有**带这两个参数，服务端为此准备的那条路一直是空转的
///       （§2.6 末尾「能力上报的时延现状」）。这一步就是把它接上。
///   </item>
///   <item>
///     <b>心跳帧</b>（每 15 秒一次 + 会话状态一变就立刻补发一次）——
///     <c>Transport.HeartbeatLoop.BuildHeartbeatJson()</c> 里的 <c>capabilities</c> 字段。
///   </item>
/// </list>
///
/// 两处**必须同源**（都走 <see cref="Current()"/>）：一处报 3 个、一处报 2 个会让
/// 网页端的按钮时有时无，而这种问题在日志里几乎看不出来。
///
/// 每次调用都现算（<see cref="IPlatformInfo.IsHeadless"/> 的实现也是现读）——
/// 15 秒一次的心跳不值得缓存，何况要保证报出去的永远是此刻的真状态。
/// </summary>
public sealed class CapabilityReporter
{
    /// <summary>连接串里的分隔符：与服务端 <c>_apply_reported_state()</c> 的切法一致
    /// （<c>server/main.py:262-288</c>：按 <c>,</c> 切、去空白、丢空项）。</summary>
    public const char QuerySeparator = ',';

    public CapabilityReporter(IPlatformInfo platform, PlatformCapabilities capabilities)
    {
        Platform = platform;
        Capabilities = capabilities;
    }

    /// <summary>注入进来的平台事实（会话状态 / headless / 平台串）。</summary>
    public IPlatformInfo Platform { get; }

    /// <summary>本平台注册了哪些能力实现（能力的唯一来源）。</summary>
    public PlatformCapabilities Capabilities { get; }

    /// <summary>
    /// 本机此刻具备的能力清单（顺序即协议里的顺序，稳定的）。
    /// 完整规则见 <see cref="DeviceCapabilities.Build"/>。
    /// </summary>
    public string[] Current() => DeviceCapabilities.Build(Platform, Capabilities);

    /// <summary>
    /// 连接串里 <c>capabilities=</c> 的取值（逗号分隔，**不转义** —— 由调用方
    /// <c>Uri.EscapeDataString</c>，与其它查询参数走同一条路径）。
    /// </summary>
    public string QueryValue() => string.Join(QuerySeparator, Current());

    /// <summary>给日志用的可读形式，例如 <c>message, screenshot, shutdown</c>。</summary>
    public string Describe() => string.Join(", ", Current());

    /// <summary>
    /// 能力清单里有没有某一项（平台层做页面侧显示时用它，避免各处再拼字符串）。
    /// </summary>
    public bool Has(string capability) => Contains(Current(), capability);

    private static bool Contains(IReadOnlyList<string> values, string capability)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] == capability)
                return true;
        }
        return false;
    }
}
