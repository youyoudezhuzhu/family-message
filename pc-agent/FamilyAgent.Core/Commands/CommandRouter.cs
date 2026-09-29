using System;
using System.Threading.Tasks;
using FamilyAgent.Core.Commands.Abstractions;
using FamilyAgent.Core.Devices;
using FamilyAgent.Core.Diagnostics;
using FamilyAgent.Core.Events;
using FamilyAgent.Core.Protocol.Frames;
using FamilyAgent.Core.Transport;

namespace FamilyAgent.Core.Commands;

/// <summary>
/// 命令派发（docs/CORE-REFACTOR-PLAN.md §3.2 的 <c>Commands/CommandRouter.cs</c>、
/// §6 Phase 3-3）—— 服务端下发的三种命令（截图 / 关机 / 解锁）在 Core 里
/// **只表现为「查能力 → 调平台接口 → 应答」这三步**：
///
/// <code>
/// screenshot_request → IScreenshotProvider  → screenshot_response（成功 / error 两条路都回）
/// shutdown           → IPowerProvider       → 平台实现自己执行并回 event
/// unlock_request     → IUnlockGuard         → unlock_result
/// </code>
///
/// ★ 为什么应答必须留在 Core：**能力不支持时也得有一条明确的应答**。
///   原来的实现里，平台层（WPF 宿主）如果没有处理该帧，网页端就是干等到超时
///   （<c>server/main.py:420-468</c> 的截图超时）—— 用户看到的是「转圈」，日志里什么都没有。
///   现在没有实现 = 立刻回 <c>screenshot_response{error:"本机不支持截图"}</c>，
///   网页端能直接告诉用户原因（§6 Phase 3 验收标准）。
///
/// ★ 平台层在这里的职责（Windows：<c>App.xaml.cs</c>）：
///   <list type="bullet">
///     <item>注册实现（<see cref="PlatformCapabilities"/>）；</item>
///     <item>订阅 <see cref="CommandReceived"/> 做界面动作（可选）；</item>
///     <item>订阅 <see cref="ScreenshotHandled"/> 把截图**镜像给本地页面**
///       （壳模式下页面收不到服务端的广播，不推的话本机的「查看桌面」会一直转圈）。</item>
///   </list>
///
/// ⚠ 本类不重试、不排队：命令是有时效的（截图）或会改机器状态的（关机），
///   补发一条 3 分钟前的请求比丢掉更糟。发不出去由 <c>Outbox</c> 按它自己的规则处理。
/// </summary>
public sealed class CommandRouter
{
    /// <summary>没有实现时回给网页端的中文原因（会原样出现在「查看桌面」的提示里）。</summary>
    public const string UnsupportedScreenshotReason = "本机不支持截图（该平台没有提供截图能力）";

    /// <summary>没有电源实现时的中文原因（进 <c>event{kind=shutdown_failed}</c>）。</summary>
    public const string UnsupportedShutdownReason = "本机不支持关机（该平台没有提供电源能力）";

    private readonly ICommandChannel _channel;
    private readonly PlatformCapabilities _capabilities;
    private readonly string _deviceId;

    /// <param name="channel">应答出口（Windows = <see cref="ConnectionManager"/>；单测 = 假实现）。</param>
    /// <param name="capabilities">注册了哪些能力实现（决定「有实现/没实现」两条路）。</param>
    /// <param name="deviceId">本机 device_id（解锁校验要判「这帧是不是发给本机的」）。</param>
    /// <remarks>
    /// 特意**不注入** <see cref="IPlatformInfo"/>：派发本身不需要平台事实 ——
    /// 「headless 下截不了图」由截图实现自己回答（它才知道怎么回答），
    /// 「headless 下不报 shutdown 能力」由 <see cref="DeviceCapabilities"/> 结合
    /// <c>IsHeadless</c> 判断。少一个依赖就少一处「两处判断不一致」的可能。
    /// </remarks>
    public CommandRouter(ICommandChannel channel,
                         PlatformCapabilities capabilities,
                         string? deviceId)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _deviceId = deviceId ?? "";
    }

    /// <summary>收到一条命令（派发已经开始；应答由本类负责）。</summary>
    public event Action<CommandReceivedArgs>? CommandReceived;

    /// <summary>截图请求处理完毕（成功/失败都抛）—— 平台层据此镜像给本地页面。</summary>
    public event Action<ScreenshotHandledArgs>? ScreenshotHandled;

    /// <summary>
    /// 接上连接层：三个命令事件直接进本类。
    ///
    /// ⚠ 与 <c>MessageManager.Attach</c> 同理：宿主**不要**再单独订阅
    ///   <c>connection.ScreenshotRequested</c> 等三个事件，否则同一条命令会被处理两遍
    ///   （两遍 = 两条应答帧）。
    /// </summary>
    public void Attach(ConnectionManager connection)
    {
        connection.ScreenshotRequested += HandleScreenshot;
        connection.ShutdownRequested += HandleShutdown;
        connection.UnlockRequested += HandleUnlock;
    }

    /// <summary>断开订阅（退出/换连接对象时）。</summary>
    public void Detach(ConnectionManager connection)
    {
        connection.ScreenshotRequested -= HandleScreenshot;
        connection.ShutdownRequested -= HandleShutdown;
        connection.UnlockRequested -= HandleUnlock;
    }

    // ---------------- 截图 ----------------

    /// <summary>
    /// 连接层入口（同步包装：命令处理不阻塞收包循环）。
    /// public 与下面两个同理 —— 单测不需要真实的 WebSocket 就能走完派发（§8.8）。
    /// </summary>
    public void HandleScreenshot(ScreenshotRequestFrame frame) => _ = HandleScreenshotAsync(frame);

    /// <summary>
    /// 处理一条 <c>screenshot_request</c>。**公开是为了能在 Linux 上单测**
    /// （Core 的单测没有真实 WebSocket，§8.8）—— 宿主不用直接调它。
    ///
    /// 两条路都必须有应答：
    /// <list type="number">
    ///   <item>有实现 → 交给 <see cref="IScreenshotProvider"/>（失败也要把中文原因回过去）；</item>
    ///   <item>没实现 → 立刻回 <c>error</c>（<see cref="UnsupportedScreenshotReason"/>），不发图、不超时。</item>
    /// </list>
    /// </summary>
    public async Task HandleScreenshotAsync(ScreenshotRequestFrame frame)
    {
        var requestId = frame?.RequestId ?? "";
        if (requestId.Length == 0)
            return;                       // 没有 request_id 的帧连不应答（FrameCodec 也不会产出它）

        RaiseCommand(CommandKind.Screenshot, frame!, $"截图请求 {requestId}");

        var provider = _capabilities.Screenshot;
        ScreenshotResult result;

        if (provider is null)
        {
            result = ScreenshotResult.Unsupported(UnsupportedScreenshotReason);
            AgentLog.Write($"✗ screenshot_request {requestId}：{UnsupportedScreenshotReason} → 明确回 error");
        }
        else
        {
            try
            {
                result = await provider.CaptureAsync().ConfigureAwait(false)
                         ?? ScreenshotResult.Failed("截图实现返回了空结果");
            }
            catch (Exception ex)
            {
                // 实现抛异常也要变成一条带原因的 error 应答 —— 不能让它把收包循环带下去
                result = ScreenshotResult.Failed("截图异常：" + ex.Message);
            }
        }

        try
        {
            if (result.Ok)
            {
                AgentLog.Write($"截图完成 {requestId}：{result.Describe()}");
                await _channel.SendScreenshotAsync(requestId, result.Base64, result.Width, result.Height, null)
                              .ConfigureAwait(false);
            }
            else
            {
                AgentLog.Write($"✗ screenshot_request {requestId} 失败：{result.Error} → 回 error（网页端会直接看到原因，不再超时）");
                await _channel.SendScreenshotAsync(requestId, null, 0, 0, result.Error).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            AgentLog.Write($"✗ 回 screenshot_response 失败（{requestId}）：{ex.Message}");
        }

        // 交给平台层镜像给本地页面（headless 下没有页面，平台层自己判断；抛异常不影响应答）
        RaiseScreenshotHandled(new ScreenshotHandledArgs(
            requestId, result.Ok ? result.Base64 : null, result.Width, result.Height,
            result.Ok ? null : result.Error));
    }

    // ---------------- 关机 ----------------

    /// <summary>连接层入口（同步包装，见 <see cref="HandleScreenshot"/>）。</summary>
    public void HandleShutdown(ShutdownCommandFrame frame) => _ = HandleShutdownAsync(frame);

    /// <summary>
    /// 处理一条 <c>shutdown</c>。
    ///
    /// 「延迟几秒」的缺省值来自平台（<see cref="IPowerProvider.DefaultDelaySeconds"/>），
    /// Core 不认识它 —— 与原实现（<c>frame.DelaySeconds ?? PowerControl.DefaultDelaySeconds</c>）
    /// 逐字等价。
    ///
    /// ⚠ 关机指令的**执行体与它的回执**（托盘气泡、<c>event{kind=shutdown}</c> /
    ///   <c>shutdown_failed</c>、页面的 <c>host.action_result</c>）仍在平台实现里 ——
    ///   Windows 侧是包住宿主既有的 <c>ExecuteShutdown</c>，行为逐字不变。
    ///   本类只负责「派发到能力，并在**没有能力**时给出明确的失败回执」。
    /// </summary>
    public async Task HandleShutdownAsync(ShutdownCommandFrame frame)
    {
        RaiseCommand(CommandKind.Shutdown, frame!, "关机指令");

        var provider = _capabilities.Power;
        if (provider is null)
        {
            AgentLog.Write($"✗ 收到关机指令：{UnsupportedShutdownReason} → 回 shutdown_failed（不静默）");
            TrySendEvent("shutdown_failed", UnsupportedShutdownReason);
            return;
        }

        var delaySeconds = frame?.DelaySeconds ?? provider.DefaultDelaySeconds;

        try
        {
            var result = await provider.ShutdownAsync(delaySeconds).ConfigureAwait(false);
            if (result is null)
            {
                AgentLog.Write("✗ 关机实现返回了空结果");
                TrySendEvent("shutdown_failed", "关机实现没有返回结果");
                return;
            }

            // ⚠ 不在这里补发 event：有能力的平台上，回执由**实现**负责 ——
            //   Windows 侧包住宿主既有的 ExecuteShutdown（它发 event{kind=shutdown} /
            //   shutdown_failed 并给页面回 host.action_result），补一份就是同一件事报两遍。
            AgentLog.Write($"关机指令处理完毕：{result.Describe()}");
        }
        catch (Exception ex)
        {
            // 实现抛异常 = 这次关机没成，必须回执，否则网页端的按钮永远停在「已下发」
            AgentLog.Write("✗ 处理关机指令异常：" + ex.Message);
            TrySendEvent("shutdown_failed", ex.Message);
        }
    }

    // ---------------- 解锁 ----------------

    /// <summary>
    /// 处理一条 <c>unlock_request</c>（同步：校验不满毫秒，也没有桌面动作）。
    ///
    /// ⚠ 校验逻辑本身仍在平台实现里（Windows = <c>Platform/WindowsUnlockGuard.cs</c>，
    ///   包住宿主既有的 <c>UnlockGuard</c>）—— 归属问题 §8.12 待拍板，这里只换调用点。
    /// </summary>
    public void HandleUnlock(UnlockRequestFrame frame)
    {
        if (frame is null)
            return;

        RaiseCommand(CommandKind.Unlock, frame, $"解锁请求 {frame.RequestId}");

        var guard = _capabilities.Unlock;
        if (guard is null)
        {
            // 本平台没有解锁实现：如实回 failed/no_credential —— 与「本机还没有解锁凭据」
            // 是同一件事（unlock 能力本来就没上报，服务端正常情况下也不会发这条帧）。
            var requestId = frame.RequestId ?? "";
            if (requestId.Length == 0)
            {
                AgentLog.Write("✗ unlock_request 缺少 request_id，无法应答");
                return;
            }

            AgentLog.Write($"✗ unlock_request {requestId}：本平台没有提供解锁实现 → 回 failed/no_credential");
            TrySendUnlockResult(requestId, UnlockReply.StatusFailed, UnlockReply.ReasonNoCredential);
            return;
        }

        UnlockReply? reply;
        try
        {
            reply = guard.Evaluate(frame, _deviceId);
        }
        catch (Exception ex)
        {
            AgentLog.Write("✗ 解锁校验异常：" + ex.Message);
            return;
        }

        if (reply is null)
        {
            // 连 request_id 都没有的帧：没法应答，也没有 id 可以记进重放缓存（原行为）
            AgentLog.Write("✗ unlock_request 缺少 request_id，无法应答");
            return;
        }

        TrySendUnlockResult(reply.RequestId, reply.Status, reply.Reason);
    }

    // ---------------- 内部 ----------------

    private void RaiseCommand(CommandKind kind, object payload, string summary)
    {
        var handler = CommandReceived;
        if (handler is null)
            return;

        try
        {
            handler(new CommandReceivedArgs(kind, payload, summary));
        }
        catch (Exception ex)
        {
            // 订阅者（平台层）抛异常不影响命令本身的处理
            AgentLog.Write($"命令事件订阅者抛异常（已忽略）：{ex.GetType().Name} {ex.Message}");
        }
    }

    private void RaiseScreenshotHandled(ScreenshotHandledArgs args)
    {
        var handler = ScreenshotHandled;
        if (handler is null)
            return;

        try
        {
            handler(args);
        }
        catch (Exception ex)
        {
            AgentLog.Write($"截图镜像订阅者抛异常（已忽略）：{ex.GetType().Name} {ex.Message}");
        }
    }

    private void TrySendEvent(string kind, string detail)
    {
        try
        {
            _channel.SendEvent(kind, detail);
        }
        catch (Exception ex)
        {
            AgentLog.Write($"✗ 回 event{{kind={kind}}} 失败：{ex.Message}");
        }
    }

    private void TrySendUnlockResult(string requestId, string status, string reason)
    {
        try
        {
            _channel.UnlockResult(requestId, status, reason);
        }
        catch (Exception ex)
        {
            AgentLog.Write($"✗ 回 unlock_result 失败（{requestId}）：{ex.Message}");
        }
    }
}
