using FamilyAgent.Core.Commands.Abstractions;

namespace FamilyAgent.Core.Devices;

/// <summary>
/// **平台能力的来源**：这个平台注册了哪些能力实现
/// （docs/CORE-REFACTOR-PLAN.md §6 Phase 3-1 / §3.2）。
///
/// 关键设计：能力清单**由这里推导**，不是由平台自己声明一个字符串数组。
/// 一个平台能做什么 = 它给 Core 注册了哪些实现 —— 这样「不在当前平台上的能力」
/// 在结构上就**不可能**被报出去（需求 §8 的语义），也不会出现
/// 「报了 screenshot 但根本没有实现」这种谎报（§8.6：服务端不校验取值，
/// 拼错或谎报的能力名不会报错、只会静默失效，那就太糟了）。
///
/// 取值示例：
/// <list type="bullet">
///   <item>Windows 宿主（<c>App.xaml.cs</c>）：三个实现全给 → 报
///     <c>message, screenshot, shutdown</c>（headless 时没有 shutdown）；</item>
///   <item>只做消息的平台（Android 骨架、或者单测里的假平台）：只给一个空集 → 只报 <c>message</c>；</item>
///   <item><c>unlock</c>：即使注册了 <see cref="Unlock"/>，本阶段也**不上报**
///     （<see cref="DeviceCapabilities"/> 里的开关仍是 false，§6 Phase 3 验收标准）。</item>
/// </list>
///
/// ⚠ 不可变：连接建立后能力表就是协议事实，运行中不换（换平台实现 = 换进程）。
/// </summary>
public sealed class PlatformCapabilities
{
    /// <summary>什么都不支持（只剩 Core 自带的 <c>message</c>）。</summary>
    public static readonly PlatformCapabilities None = new();

    public PlatformCapabilities(IScreenshotProvider? screenshot = null,
                                IPowerProvider? power = null,
                                IUnlockGuard? unlock = null)
    {
        Screenshot = screenshot;
        Power = power;
        Unlock = unlock;
    }

    /// <summary>截图实现；为 null = 本平台没有这个能力。</summary>
    public IScreenshotProvider? Screenshot { get; }

    /// <summary>电源实现；为 null = 本平台没有这个能力。</summary>
    public IPowerProvider? Power { get; }

    /// <summary>解锁校验实现；为 null = 本平台没有这个能力（本阶段不上报，见类注释）。</summary>
    public IUnlockGuard? Unlock { get; }

    /// <summary>有没有截图能力（决定能力表里出不出现 <c>screenshot</c>）。</summary>
    public bool SupportsScreenshot => Screenshot is not null;

    /// <summary>有没有关机能力（真正上报还要看 <see cref="IPlatformInfo.IsHeadless"/>）。</summary>
    public bool SupportsShutdown => Power is not null;

    /// <summary>有没有解锁能力。</summary>
    public bool SupportsUnlock => Unlock is not null;
}
