using System.Collections.Generic;
using FamilyAgent.Core.Commands.Abstractions;

namespace FamilyAgent.Core.Devices;

/// <summary>
/// 本机能力清单的唯一权威定义（服务端不做取值校验：拼错能力名不会报错、只会静默失效，
/// 见 §8.6，所以名字只在这里写一次）。
///
/// 位置：Phase 1 从 <c>AgentClient.cs</c> 的 <c>BuildCapabilities()</c>（原 :376-388）
/// 搬进来（见 docs/CORE-REFACTOR-PLAN.md §3.2 / §4）—— 唯一的改动是
/// **不再直接读宿主静态属性** <c>App.IsHeadless</c>，改为读注入进来的
/// <see cref="IPlatformInfo"/>。
///
/// ★ Phase 3 的第二处改动：能力清单不再「平台声明什么就报什么」，而是**由注册了哪些
///   实现推导出来**（<see cref="PlatformCapabilities"/>）—— 不在当前平台上的能力不报：
/// <list type="bullet">
///   <item><c>message</c>：Core 自带的消息链路，任何平台都有；</item>
///   <item><c>screenshot</c>：**注册了** <see cref="IScreenshotProvider"/> 才有；</item>
///   <item><c>shutdown</c>：注册了 <see cref="IPowerProvider"/> **且**非 headless
///     （会话 0 关机没有意义，也没有桌面可以弹提示）；</item>
///   <item><c>unlock</c>：注册了 <see cref="IUnlockGuard"/> **且** 真的能用
///     （凭据已配、未被失败冷却锁住，见 <see cref="IUnlockGuard.Ready"/>）**且**非 headless。</item>
/// </list>
/// 顺序（message → screenshot → shutdown → unlock）与原实现逐字一致 —— 网页端与日志都在看它。
///
/// 能力名一旦上协议就是事实契约（Phase 3 会把它们写进 PROTOCOL.md）：
/// <c>message</c> / <c>screenshot</c> / <c>shutdown</c> / <c>unlock</c>。
/// </summary>
public static class DeviceCapabilities
{
    public const string Message = "message";
    public const string Screenshot = "screenshot";
    public const string Shutdown = "shutdown";
    public const string Unlock = "unlock";

    /// <summary>
    /// 本机当前具备的能力清单（集中定义）。
    ///
    /// 每次现算：headless 与交互式实例的结果不同；<paramref name="capabilities"/>
    /// 决定「这个平台到底有没有这个能力」（没有实现的能力名绝不会出现在结果里）。
    /// </summary>
    public static string[] Build(IPlatformInfo platform, PlatformCapabilities capabilities)
    {
        var caps = new List<string> { Message };

        // 注册了截图实现才算有这个能力（Android / 未来平台可以只给消息）
        if (capabilities.SupportsScreenshot)
            caps.Add(Screenshot);

        // 会话 0（开机后无人登录）关机没有意义，也没有桌面可以弹窗提示
        if (capabilities.SupportsShutdown && !platform.IsHeadless)
            caps.Add(Shutdown);

        // 解锁：注册了实现 **且** 真的能用（凭据配好、没被失败冷却锁住）**且** 不是会话 0
        // —— 会话 0 没有桌面，解锁请求进来也无处施加（与 shutdown 同一理由）。
        // 「有实现」≠「能用」：凭据没配就上报，网页端会出现一个点了必然失败的按钮。
        if (capabilities.SupportsUnlock && !platform.IsHeadless
            && capabilities.Unlock is { Ready: true })
            caps.Add(Unlock);

        return caps.ToArray();
    }
}
