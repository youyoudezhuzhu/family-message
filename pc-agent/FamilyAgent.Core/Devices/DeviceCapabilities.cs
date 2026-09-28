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
/// <see cref="IPlatformInfo"/>。取值与顺序逐字未变。
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
    /// 是否上报 <c>unlock</c> 能力。
    ///
    /// ⚠ 本阶段**故意不上报**：能力声明等于「这台机器真能解锁」，而凭据存储和
    ///   Credential Provider 都还没做，报了就是谎报 —— 网页端会给出一个点了
    ///   必然失败的按钮。Phase 2 落地后把这里改成 true 即可，其余不用动。
    ///   （宿主侧 <c>JsBridge.CanUnlock</c> 必须与它一致。）
    /// </summary>
    private static readonly bool ReportUnlockCapability = false;

    /// <summary>
    /// 本机当前具备的能力清单（集中定义，方便后续打开 unlock）。
    /// 每次现算：headless 与交互式实例的结果不同。
    /// </summary>
    public static string[] Build(IPlatformInfo platform)
    {
        var caps = new List<string> { Message, Screenshot };

        // 会话 0（开机后无人登录）关机没有意义，也没有桌面可以弹窗提示
        if (!platform.IsHeadless)
            caps.Add(Shutdown);

        if (ReportUnlockCapability)
            caps.Add(Unlock);

        return caps.ToArray();
    }
}
