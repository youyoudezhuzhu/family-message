using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FamilyAgent.Core.Commands;
using FamilyAgent.Core.Commands.Abstractions;
using FamilyAgent.Core.Config;
using FamilyAgent.Core.Devices;
using FamilyAgent.Core.Events;
using FamilyAgent.Core.Protocol.Frames;
using FamilyAgent.Core.Transport;
using Xunit;

namespace FamilyAgent.Core.Tests;

// ───────────────────────────────────────────────────────────────────
//  Phase 3 的假平台实现 —— Core 只认接口，所以这些 fakes 一个 Windows 类型都不需要
//  （这正是「Core 能在 Linux/CI 上跑」这条判据的实证，§8.8）。
// ───────────────────────────────────────────────────────────────────

/// <summary>假的截图实现：可切换「截到了 / 没截成 / 抛异常」，并记录被调用几次。</summary>
internal sealed class FakeScreenshotProvider : IScreenshotProvider
{
    public ScreenshotResult Result { get; set; } = ScreenshotResult.Captured("ZmFrZS1qcGVn", 1920, 1080);

    public bool Throw { get; set; }

    public int Calls { get; private set; }

    public Task<ScreenshotResult> CaptureAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        if (Throw)
            throw new InvalidOperationException("假的 GDI 故障");
        return Task.FromResult(Result);
    }
}

/// <summary>假的电源实现：记录请求的延迟秒数，可切换失败/抛异常。</summary>
internal sealed class FakePowerProvider : IPowerProvider
{
    public int DefaultDelaySeconds { get; init; } = 7;

    public List<int> Requested { get; } = new();

    public bool Ok { get; set; } = true;

    public string Detail { get; set; } = "已下发关机，7 秒后执行";

    public bool Throw { get; set; }

    public Task<PowerResult> ShutdownAsync(int delaySeconds, CancellationToken cancellationToken = default)
    {
        Requested.Add(delaySeconds);
        if (Throw)
            throw new InvalidOperationException("假的关机故障");
        return Task.FromResult(new PowerResult(Ok, Detail));
    }
}

/// <summary>假的解锁校验：只回答一个固定结论（真正的校验逻辑仍在宿主，§8.12 待拍板）。</summary>
internal sealed class FakeUnlockGuard : IUnlockGuard
{
    public UnlockReply? Reply { get; set; } =
        new("r-1", UnlockReply.StatusFailed, UnlockReply.ReasonNoCredential);

    /// <summary>凭据是否就绪（决定 capabilities 里报不报 unlock）。默认 false = 今天的行为。</summary>
    public bool Ready { get; set; }

    public int Calls { get; private set; }

    public string? LastDeviceId { get; private set; }

    public UnlockReply? Evaluate(UnlockRequestFrame request, string? myDeviceId)
    {
        Calls++;
        LastDeviceId = myDeviceId;
        return Reply;
    }
}

/// <summary>把 Core 发出去的每一帧记下来的假应答出口（没有 WebSocket）。</summary>
internal sealed class FakeCommandChannel : ICommandChannel
{
    public List<(string RequestId, string? Base64, int Width, int Height, string? Error)> Screenshots { get; } = new();

    public List<(string RequestId, string Status, string Reason)> Unlocks { get; } = new();

    public List<(string Kind, string Detail)> Events { get; } = new();

    public Task SendScreenshotAsync(string requestId, string? base64, int width, int height, string? error)
    {
        Screenshots.Add((requestId, base64, width, height, error));
        return Task.CompletedTask;
    }

    public void UnlockResult(string requestId, string status, string reason) =>
        Unlocks.Add((requestId, status, reason));

    public void SendEvent(string kind, string detail) => Events.Add((kind, detail));
}

/// <summary>「像 Windows 宿主那样」注册了全部能力的平台（行为等价性用）。</summary>
internal static class FullCapabilities
{
    public static PlatformCapabilities Create() => new(new FakeScreenshotProvider(), new FakePowerProvider());
}

// ───────────────────────────────────────────────────────────────────
//  capabilities 上报：不在当前平台上的能力不能报
// ───────────────────────────────────────────────────────────────────

/// <summary>
/// ★ Phase 3 的核心判据（§6 Phase 3 验收标准 / §7G-17、18）：
/// **能力表由「注册了哪些实现」推导，而不是由平台自己声明。**
///
/// 与 Phase 1 的差别只有一处：那时 <c>Build()</c> 恒报 <c>message + screenshot</c>，
/// 只看 <c>IsHeadless</c> 决定要不要加 <c>shutdown</c>；现在一个「只会收消息」的平台
/// （未来 Android 骨架、或任何还没实现截图的平台）**不可能**报出截图能力 ——
/// 报了就是谎报：网页端会给出一个点了必然失败的「查看桌面」按钮，而服务端不校验取值
/// （§8.6），这种错**不会**被任何一层拦下来。
/// </summary>
public class CapabilityReportingTests
{
    [Fact]
    public void PlatformWithoutProviders_ReportsOnlyMessage()
    {
        foreach (var headless in new[] { false, true })
        {
            var caps = DeviceCapabilities.Build(
                new FakePlatformInfo { IsHeadless = headless }, PlatformCapabilities.None);

            Assert.Equal(new[] { "message" }, caps);
            Assert.DoesNotContain("screenshot", caps);
            Assert.DoesNotContain("shutdown", caps);
            Assert.DoesNotContain("unlock", caps);
        }
    }

    [Fact]
    public void PlatformWithScreenshotOnly_ReportsMessageAndScreenshot_NeverShutdown()
    {
        var capabilities = new PlatformCapabilities(screenshot: new FakeScreenshotProvider());

        Assert.Equal(new[] { "message", "screenshot" },
            DeviceCapabilities.Build(new FakePlatformInfo { IsHeadless = false }, capabilities));

        // 会话 0 也不影响：截图能力在不在，看的是实现有没有注册
        Assert.Equal(new[] { "message", "screenshot" },
            DeviceCapabilities.Build(new FakePlatformInfo { IsHeadless = true }, capabilities));
    }

    [Fact]
    public void PlatformWithPowerOnly_ReportsShutdownOnlyWhenInteractive()
    {
        var capabilities = new PlatformCapabilities(power: new FakePowerProvider());

        Assert.Equal(new[] { "message", "shutdown" },
            DeviceCapabilities.Build(new FakePlatformInfo { IsHeadless = false }, capabilities));

        // 会话 0（开机后无人登录）关机没有意义 → 不报，但 message 仍在
        Assert.Equal(new[] { "message" },
            DeviceCapabilities.Build(new FakePlatformInfo { IsHeadless = true }, capabilities));
    }

    /// <summary>
    /// 等价性判据：Windows 宿主（三个实现都注册 + 非 headless）报出来的东西
    /// 与 Phase 1/2 逐字一致 —— 顺序也一样（网页端与日志都在看这一串）。
    /// </summary>
    [Fact]
    public void WindowsLikePlatform_KeepsTheOriginalListAndOrder()
    {
        Assert.Equal(new[] { "message", "screenshot", "shutdown" },
            DeviceCapabilities.Build(new FakePlatformInfo { IsHeadless = false }, FullCapabilities.Create()));

        Assert.Equal(new[] { "message", "screenshot" },
            DeviceCapabilities.Build(new FakePlatformInfo { IsHeadless = true }, FullCapabilities.Create()));
    }

    /// <summary>
    /// unlock 只在**真的能用**时上报（Phase 2 起）：注册了实现但凭据没配 → 不报；
    /// 凭据配好（<c>Ready</c>）→ 报；会话 0（headless）→ 不报（没有桌面，解锁请求无处施加）。
    /// </summary>
    [Fact]
    public void UnlockIsReportedOnlyWhenCredentialReady()
    {
        var guard = new FakeUnlockGuard();
        var capabilities = new PlatformCapabilities(
            screenshot: new FakeScreenshotProvider(),
            power: new FakePowerProvider(),
            unlock: guard);

        Assert.True(capabilities.SupportsUnlock);      // 实现是有的

        // ① 有实现、但凭据没配好（Ready=false）→ 不报（报了就谎报）
        Assert.DoesNotContain("unlock",
            DeviceCapabilities.Build(new FakePlatformInfo { IsHeadless = false }, capabilities));

        // ② 凭据配好 → 报
        guard.Ready = true;
        Assert.Contains("unlock",
            DeviceCapabilities.Build(new FakePlatformInfo { IsHeadless = false }, capabilities));

        // ③ 会话 0 → 即使凭据配好也不报（与 shutdown 同一理由：没有桌面）
        Assert.DoesNotContain("unlock",
            DeviceCapabilities.Build(new FakePlatformInfo { IsHeadless = true }, capabilities));
    }

    /// <summary>连接串取值的形状必须与服务端 `_apply_reported_state()` 的切法一致（逗号分隔）。</summary>
    [Fact]
    public void CapabilityReporter_QueryValueIsCommaSeparated()
    {
        var reporter = new CapabilityReporter(
            new FakePlatformInfo { IsHeadless = false }, FullCapabilities.Create());

        Assert.Equal("message,screenshot,shutdown", reporter.QueryValue());
        Assert.Equal("message, screenshot, shutdown", reporter.Describe());
        Assert.True(reporter.Has("screenshot"));
        Assert.False(reporter.Has("unlock"));
    }

    [Fact]
    public void HeartbeatJson_CarriesTheInjectedCapabilities()
    {
        var json = HeartbeatLoop.BuildHeartbeatJson(
            new FakePlatformInfo { IsHeadless = true, SessionState = "logon_screen" },
            PlatformCapabilities.None);

        Assert.Contains("\"type\":\"heartbeat\"", json);
        Assert.Contains("\"windows_state\":\"logon_screen\"", json);
        Assert.Contains("\"capabilities\":[\"message\"]", json);       // 没实现的平台就只报 message
        Assert.DoesNotContain("screenshot", json);
    }
}

// ───────────────────────────────────────────────────────────────────
//  ★ 那条一直空转的路：连接串里的 windows_state / capabilities
// ───────────────────────────────────────────────────────────────────

/// <summary>
/// Phase 3-4 的判据：服务端**早就有**「一连上就报」的通道
/// （<c>server/main.py:740-741</c> 读参数、<c>:772-773</c> 立刻写库并广播），
/// 但客户端 <c>BuildUri()</c> 一直没带这两个参数，所以那条路是空转的 ——
/// 能力/状态要等连上后的第一次心跳（最长 15 秒，网页端会先显示「未知」）。
/// </summary>
public class DeviceQueryStringTests
{
    private static AgentConfig Config() => new()
    {
        ServerUrl = "http://192.168.1.50:18801",
        DeviceId = "pc_test",
        DeviceName = "测试机",
        Token = "tok",
        EnrollToken = "FAMILY-TEST-TOKEN",
    };

    [Fact]
    public void BuildUri_CarriesSessionStateAndCapabilities()
    {
        var connection = new ConnectionManager(
            Config(),
            new FakePlatformInfo { IsHeadless = false, SessionState = "locked" },
            FullCapabilities.Create());

        var uri = connection.BuildUri();
        var text = uri.AbsoluteUri;

        Assert.Contains("/ws/device/pc_test", text);
        Assert.Contains("windows_state=locked", text);
        // 逗号会被转义成 %2C —— 服务端接的是转义后的查询串，切之前先解码，天经地义
        Assert.Contains("capabilities=message%2Cscreenshot%2Cshutdown", text);

        // 原有参数一个都不能少（回归）
        Assert.Contains("token=tok", text);
        Assert.Contains("name=", text);
        Assert.Contains("type=pc", text);
        Assert.Contains("platform=", text);
        Assert.Contains("agent_version=", text);
        Assert.Contains("enroll_token=FAMILY-TEST-TOKEN", text);
    }

    /// <summary>只支持消息的平台：连上即报的就只能是 message（不谎报 screenshot/shutdown）。</summary>
    [Fact]
    public void BuildUri_ReportsOnlyWhatThePlatformActuallyHas()
    {
        var connection = new ConnectionManager(
            Config(),
            new FakePlatformInfo { IsHeadless = true, SessionState = "logon_screen" },
            PlatformCapabilities.None);

        var text = connection.BuildUri().AbsoluteUri;

        Assert.Contains("windows_state=logon_screen", text);
        Assert.Contains("capabilities=message", text);
        Assert.DoesNotContain("screenshot", text);
        Assert.DoesNotContain("shutdown", text);
    }
}

// ───────────────────────────────────────────────────────────────────
//  命令派发：能力不支持要有明确的 error，而不是静默超时
// ───────────────────────────────────────────────────────────────────

/// <summary>
/// §6 Phase 3 验收标准之三：把实现换成「返回 unsupported 的假实现」时，
/// 网页端点的「查看桌面」必须拿到明确的 error，而不是 <c>server/main.py:420-468</c>
/// 的 30 秒超时。
/// </summary>
public class CommandRouterTests
{
    private static CommandRouter Router(FakeCommandChannel channel,
                                        PlatformCapabilities capabilities,
                                        out FakeScreenshotProvider? screenshot,
                                        out FakePowerProvider? power,
                                        out FakeUnlockGuard? guard)
    {
        screenshot = capabilities.Screenshot as FakeScreenshotProvider;
        power = capabilities.Power as FakePowerProvider;
        guard = capabilities.Unlock as FakeUnlockGuard;
        return new CommandRouter(channel, capabilities, "pc_test");
    }

    private static ScreenshotRequestFrame Screenshot(string requestId = "r-1") =>
        new() { Type = "screenshot_request", RequestId = requestId };

    // ── 截图 ───────────────────────────────────────────────────

    /// <summary>★ 没有截图实现 → 立刻回 error，且**不会**去问任何实现。</summary>
    [Fact]
    public async Task Screenshot_WithoutProvider_AnswersWithError_InsteadOfStayingSilent()
    {
        var channel = new FakeCommandChannel();
        var router = Router(channel, PlatformCapabilities.None, out _, out _, out _);

        await router.HandleScreenshotAsync(Screenshot());

        var reply = Assert.Single(channel.Screenshots);
        Assert.Equal("r-1", reply.RequestId);
        Assert.Null(reply.Base64);
        Assert.Equal(CommandRouter.UnsupportedScreenshotReason, reply.Error);
    }

    /// <summary>实现说「这次截不成」→ 原因原样回给服务端（不是一句笼统的「截图失败」）。</summary>
    [Fact]
    public async Task Screenshot_WithFailingProvider_PassesTheReasonThrough()
    {
        var channel = new FakeCommandChannel();
        var provider = new FakeScreenshotProvider { Result = ScreenshotResult.Failed("找不到主显示器") };
        var router = Router(channel, new PlatformCapabilities(screenshot: provider),
                            out var captured, out _, out _);

        await router.HandleScreenshotAsync(Screenshot("r-2"));

        Assert.NotNull(captured);
        Assert.Equal(1, captured!.Calls);
        var reply = Assert.Single(channel.Screenshots);
        Assert.Null(reply.Base64);
        Assert.Equal("找不到主显示器", reply.Error);
    }

    [Fact]
    public async Task Screenshot_WithWorkingProvider_SendsImageAndDimensions()
    {
        var channel = new FakeCommandChannel();
        var provider = new FakeScreenshotProvider
        {
            Result = ScreenshotResult.Captured("QUJD", 1920, 1080),
        };
        var router = Router(channel, new PlatformCapabilities(screenshot: provider),
                            out _, out _, out _);

        var mirrored = new List<ScreenshotHandledArgs>();
        router.ScreenshotHandled += mirrored.Add;

        await router.HandleScreenshotAsync(Screenshot("r-3"));

        var reply = Assert.Single(channel.Screenshots);
        Assert.Equal("QUJD", reply.Base64);
        Assert.Equal(1920, reply.Width);
        Assert.Equal(1080, reply.Height);
        Assert.Null(reply.Error);

        // 平台层要拿它镜像给本地页面（壳模式下页面收不到服务端广播）
        var handled = Assert.Single(mirrored);
        Assert.Equal("r-3", handled.RequestId);
        Assert.True(handled.Ok);
        Assert.Equal("QUJD", handled.Base64);
    }

    /// <summary>实现抛异常也要变成一条带原因的应答 —— 否则网页端只能等超时。</summary>
    [Fact]
    public async Task Screenshot_ProviderThrows_IsAnsweredAsError()
    {
        var channel = new FakeCommandChannel();
        var provider = new FakeScreenshotProvider { Throw = true };
        var router = Router(channel, new PlatformCapabilities(screenshot: provider),
                            out _, out _, out _);

        await router.HandleScreenshotAsync(Screenshot("r-4"));

        var reply = Assert.Single(channel.Screenshots);
        Assert.Null(reply.Base64);
        Assert.Contains("假的 GDI 故障", reply.Error);
    }

    // ── 关机 ───────────────────────────────────────────────────

    /// <summary>没有电源实现的平台：明确回 <c>shutdown_failed</c>，不静默。</summary>
    [Fact]
    public async Task Shutdown_WithoutProvider_SendsShutdownFailedEvent()
    {
        var channel = new FakeCommandChannel();
        var router = Router(channel, PlatformCapabilities.None, out _, out _, out _);

        await router.HandleShutdownAsync(new ShutdownCommandFrame { Type = "shutdown" });

        var evt = Assert.Single(channel.Events);
        Assert.Equal("shutdown_failed", evt.Kind);
        Assert.Equal(CommandRouter.UnsupportedShutdownReason, evt.Detail);
    }

    /// <summary>帧里没带 <c>delay_seconds</c> → 用**平台**的默认值（Core 不认识它，与原实现一致）。</summary>
    [Fact]
    public async Task Shutdown_UsesProviderDefaultDelay_WhenFrameOmitsIt()
    {
        var channel = new FakeCommandChannel();
        var power = new FakePowerProvider { DefaultDelaySeconds = 9 };
        var router = Router(channel, new PlatformCapabilities(power: power), out _, out var provider, out _);

        await router.HandleShutdownAsync(new ShutdownCommandFrame { Type = "shutdown" });

        Assert.NotNull(provider);
        Assert.Equal(new[] { 9 }, provider!.Requested);
        Assert.Empty(channel.Events);            // 有实现时回执由实现负责，Core 不重复发
    }

    [Fact]
    public async Task Shutdown_UsesTheDelayFromTheFrame_WhenPresent()
    {
        var channel = new FakeCommandChannel();
        var power = new FakePowerProvider();
        var router = Router(channel, new PlatformCapabilities(power: power), out _, out var provider, out _);

        await router.HandleShutdownAsync(new ShutdownCommandFrame { Type = "shutdown", DelaySeconds = 30 });

        Assert.Equal(new[] { 30 }, provider!.Requested);
    }

    /// <summary>实现抛异常（例如切 UI 线程失败）→ 兜底回 <c>shutdown_failed</c>，不让指令石沉大海。</summary>
    [Fact]
    public async Task Shutdown_ProviderThrows_SendsShutdownFailedEvent()
    {
        var channel = new FakeCommandChannel();
        var power = new FakePowerProvider { Throw = true };
        var router = Router(channel, new PlatformCapabilities(power: power), out _, out _, out _);

        await router.HandleShutdownAsync(new ShutdownCommandFrame { Type = "shutdown" });

        var evt = Assert.Single(channel.Events);
        Assert.Equal("shutdown_failed", evt.Kind);
        Assert.Contains("假的关机故障", evt.Detail);
    }

    // ── 解锁 ───────────────────────────────────────────────────

    [Fact]
    public void Unlock_RoutesToTheGuard_AndSendsItsReply()
    {
        var channel = new FakeCommandChannel();
        var guard = new FakeUnlockGuard
        {
            Reply = new UnlockReply("u-1", UnlockReply.StatusFailed, UnlockReply.ReasonReplay),
        };
        var router = Router(channel, new PlatformCapabilities(unlock: guard), out _, out _, out var routed);

        router.HandleUnlock(new UnlockRequestFrame { Type = "unlock_request", RequestId = "u-1" });

        Assert.NotNull(routed);
        Assert.Equal(1, routed!.Calls);
        Assert.Equal("pc_test", routed.LastDeviceId);          // 归属校验用的是本机 device_id
        var reply = Assert.Single(channel.Unlocks);
        Assert.Equal(("u-1", "failed", "replay"), reply);
    }

    /// <summary>没有解锁实现的平台：如实回 <c>failed / no_credential</c>（语义等同「本机没有解锁凭据」）。</summary>
    [Fact]
    public void Unlock_WithoutGuard_AnswersFailedNoCredential()
    {
        var channel = new FakeCommandChannel();
        var router = Router(channel, PlatformCapabilities.None, out _, out _, out _);

        router.HandleUnlock(new UnlockRequestFrame { Type = "unlock_request", RequestId = "u-2" });

        var reply = Assert.Single(channel.Unlocks);
        Assert.Equal("u-2", reply.RequestId);
        Assert.Equal(UnlockReply.StatusFailed, reply.Status);
        Assert.Equal(UnlockReply.ReasonNoCredential, reply.Reason);
    }

    /// <summary>连 request_id 都没有的帧：不应答（原行为），也不能炸。</summary>
    [Fact]
    public void Unlock_WithoutRequestId_IsIgnored()
    {
        var channel = new FakeCommandChannel();
        var guard = new FakeUnlockGuard { Reply = null };
        var router = Router(channel, new PlatformCapabilities(unlock: guard), out _, out _, out _);

        router.HandleUnlock(new UnlockRequestFrame { Type = "unlock_request" });

        Assert.Empty(channel.Unlocks);
    }

    // ── 统一事件（§3.3 的 CommandReceived）──────────────────────

    [Fact]
    public async Task CommandReceived_IsRaisedForEveryKind()
    {
        var channel = new FakeCommandChannel();
        var capabilities = FullCapabilities.Create();
        var router = Router(channel, capabilities, out _, out _, out _);
        var kinds = new List<CommandKind>();
        router.CommandReceived += args => kinds.Add(args.Kind);

        await router.HandleScreenshotAsync(Screenshot("r-5"));
        await router.HandleShutdownAsync(new ShutdownCommandFrame { Type = "shutdown" });
        router.HandleUnlock(new UnlockRequestFrame { Type = "unlock_request", RequestId = "u-3" });

        Assert.Equal(new[] { CommandKind.Screenshot, CommandKind.Shutdown, CommandKind.Unlock }, kinds);
    }

    /// <summary>订阅者抛异常不能影响命令本身的处理（应答该发还得发）。</summary>
    [Fact]
    public async Task CommandReceived_SubscriberThrows_DoesNotBreakDispatch()
    {
        var channel = new FakeCommandChannel();
        var router = Router(channel, new PlatformCapabilities(new FakeScreenshotProvider()),
                            out _, out _, out _);
        router.CommandReceived += _ => throw new InvalidOperationException("界面订阅者炸了");

        await router.HandleScreenshotAsync(Screenshot("r-6"));

        Assert.Single(channel.Screenshots);
    }
}
