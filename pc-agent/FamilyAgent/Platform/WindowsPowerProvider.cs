using System;
using System.Threading;
using System.Threading.Tasks;
using FamilyAgent.Core.Commands.Abstractions;

namespace FamilyAgent.Platform;

/// <summary>
/// Windows 侧的 <see cref="IPowerProvider"/> 实现
/// （docs/CORE-REFACTOR-PLAN.md §3.2 / §4：<c>PowerControl.cs</c> 的「包接口」那一半）。
///
/// 职责边界（故意的，为了「行为逐字不变」）：
/// <list type="bullet">
///   <item><b>执行体</b>仍在宿主里 —— <c>App.ExecuteShutdown()</c>：写日志、弹托盘气泡
///     （WinForms 组件，必须切 UI 线程）、<c>PowerControl.Shutdown()</c> 下发给系统、
///     回 <c>event{kind=shutdown}</c> / <c>shutdown_failed</c>、给页面回
///     <c>host.action_result</c>。这一段**页面请求关机**那条路也共用，
///     搬走它就会动到那条路的行为；</item>
///   <item><b>本类</b>把这一整套包成 Core 认识的「一个平台能力」：
///     Core 只调 <see cref="ShutdownAsync"/>，不认识托盘、不认识 <c>shutdown.exe</c>。</item>
/// </list>
///
/// ⚠ 于是「关机指令的回执由谁发」的答案是：**有实现时由实现负责**（就是上面那段既有逻辑），
///   Core 只在**平台没有这个能力**时补一条 <c>shutdown_failed</c>
///   （见 <c>Core/Commands/CommandRouter.cs</c>）—— 这样才不会同一件事报两遍。
///
/// 线程：<paramref name="execute"/> 由宿主注入，注入方负责切线程；本类**同步**调用它
/// （与搬迁前 <c>App.OnShutdownRequested</c> 里那句 <c>Dispatcher.Invoke</c> 的执行时序一致）。
/// </summary>
public sealed class WindowsPowerProvider : IPowerProvider
{
    private readonly Func<int, (bool Ok, string Detail)> _execute;

    /// <param name="execute">
    /// 宿主注入的关机执行体，签名与原 <c>App.ExecuteShutdown</c> 一致：
    /// 返回（是否已下发, 给界面看的中文说明）。
    /// </param>
    public WindowsPowerProvider(Func<int, (bool Ok, string Detail)> execute)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    /// <summary>帧里没带 <c>delay_seconds</c> 时用几秒 —— 仍是 <c>PowerControl</c> 说了算（现在 5 秒）。</summary>
    public int DefaultDelaySeconds => PowerControl.DefaultDelaySeconds;

    /// <inheritdoc />
    public Task<PowerResult> ShutdownAsync(int delaySeconds, CancellationToken cancellationToken = default)
    {
        var (ok, detail) = _execute(delaySeconds);
        return Task.FromResult(ok ? PowerResult.Succeeded(detail) : PowerResult.Failed(detail));
    }
}
