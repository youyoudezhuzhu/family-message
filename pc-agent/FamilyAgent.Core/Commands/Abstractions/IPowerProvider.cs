using System.Threading;
using System.Threading.Tasks;

namespace FamilyAgent.Core.Commands.Abstractions;

/// <summary>
/// 平台能力：**电源（关机）**（docs/CORE-REFACTOR-PLAN.md §6 Phase 3-2 / §3.2）。
///
/// Windows 侧实现 = <c>Platform/WindowsPowerProvider.cs</c>（包住现成的 <c>PowerControl</c>）；
/// Android 侧将来实现自己的（需求 §9）。
///
/// ★ **不支持就不报能力**：没注册实现 → 能力表里不出现 <c>shutdown</c>（需求 §8）。
///   会话 0（headless，开机后无人登录）由 Core 结合 <see cref="IPlatformInfo.IsHeadless"/>
///   一起判断 —— 那时关机没有意义，也没有桌面可以弹提示。
///
/// ⚠ 与 <see cref="IScreenshotProvider"/> 的区别：关机**会改机器状态**，所以实现必须
///   自己保证「只执行一次」；Core 只负责把请求路由到这里，不重试、不排队补发。
/// </summary>
public interface IPowerProvider
{
    /// <summary>
    /// 命令帧里**没带** <c>delay_seconds</c> 时用几秒。
    ///
    /// 这个默认值属于平台（Windows = <c>PowerControl.DefaultDelaySeconds</c>，现在 5 秒），
    /// Core 不认识它 —— 原实现里这一句也在宿主（<c>App.xaml.cs</c> 的
    /// <c>frame.DelaySeconds ?? PowerControl.DefaultDelaySeconds</c>）。
    /// </summary>
    int DefaultDelaySeconds { get; }

    /// <summary>
    /// 计划关机（延迟 <paramref name="delaySeconds"/> 秒）。
    ///
    /// 返回 <see cref="PowerResult.Ok"/> = false 时 <see cref="PowerResult.Detail"/> 是中文原因
    /// （会原样进 <c>event{kind=shutdown_failed}</c>）。实现**不该**抛异常；
    /// 万一抛了，Core 会兜成同一条失败事件，不会让指令静默丢掉。
    /// </summary>
    Task<PowerResult> ShutdownAsync(int delaySeconds, CancellationToken cancellationToken = default);
}

/// <summary>一次关机指令的执行结果（是否已下发 + 给界面看的中文说明）。</summary>
public sealed class PowerResult
{
    public PowerResult(bool ok, string detail)
    {
        Ok = ok;
        Detail = detail ?? "";
    }

    /// <summary>已下发给操作系统（真正关机由系统在延迟到点后执行）。</summary>
    public bool Ok { get; }

    /// <summary>中文说明（成功时形如「已下发关机，5 秒后执行…」）。</summary>
    public string Detail { get; }

    public static PowerResult Succeeded(string detail) => new(true, detail);

    public static PowerResult Failed(string detail) => new(false, detail);

    /// <summary>给日志用。</summary>
    public string Describe() => (Ok ? "已下发" : "失败") + "：" + Detail;
}
