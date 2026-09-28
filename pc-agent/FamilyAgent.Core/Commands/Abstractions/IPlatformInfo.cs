namespace FamilyAgent.Core.Commands.Abstractions;

/// <summary>
/// Core 眼里的「本机」—— **依赖倒置的接口**，用来切断原来 Core 对 UI / 平台静态成员的两条直读：
///
/// <list type="bullet">
///   <item>原来 <c>AgentClient.BuildCapabilities()</c> 直接读宿主静态属性 <c>App.IsHeadless</c>；</item>
///   <item>原来 <c>AgentClient.BuildHeartbeatJson()</c> 直接读 <c>SessionState.Current</c>（仅 Windows 有）。</item>
/// </list>
///
/// 见 docs/CORE-REFACTOR-PLAN.md §2.2（「分层的真实缺口」）与 §3.2。
/// Windows 侧实现：<c>pc-agent/FamilyAgent/Platform/WindowsPlatformInfo.cs</c>
/// （包装现有的两个静态成员，行为不变）；将来 Android 侧各自实现。
///
/// ⚠ 实现必须是**每次现算**、无缓存的：会话状态可能刚在心跳周期之间变过，
///   报旧值会让页面显示错的状态（原 <c>SessionState.Current</c> 的注释同义）。
/// </summary>
public interface IPlatformInfo
{
    /// <summary>
    /// 是否以「登录前」模式运行（<c>--headless</c>，会话 0）。
    /// 此时没有交互式桌面：不能弹窗、不能截图、关机也没有意义，
    /// 所以能力表里不含 <c>shutdown</c>。
    /// </summary>
    bool IsHeadless { get; }

    /// <summary>
    /// 平台描述字符串。连接查询串 <c>platform=</c> 用它，例如
    /// <c>Windows 10.0.19045.0</c>（与 <c>AgentClient.BuildUri()</c> 里的取值一致）。
    /// </summary>
    string Platform { get; }

    /// <summary>
    /// 本机当前的 Windows 会话状态字面量：
    /// <c>unknown</c> / <c>logon_screen</c> / <c>locked</c> / <c>unlocked</c>
    /// （协议取值与 NAS 侧冻结，见 <c>SessionState</c> 的注释与 <c>devices.py:73</c>）。
    /// </summary>
    string SessionState { get; }
}
