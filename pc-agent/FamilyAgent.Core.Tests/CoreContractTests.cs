using System.Collections.Generic;
using FamilyAgent.Core.Commands.Abstractions;
using FamilyAgent.Core.Devices;
using FamilyAgent.Core.Transport;
using Xunit;

namespace FamilyAgent.Core.Tests;

/// <summary>一个假的平台实现：证明 Core 只认接口、不需要任何 Windows 东西。</summary>
internal sealed class FakePlatformInfo : IPlatformInfo
{
    public bool IsHeadless { get; init; }
    public string Platform { get; init; } = "Fake 1.0";
    public string SessionState { get; init; } = "unknown";
}

/// <summary>
/// 能力表与退避策略的取值 —— 都是「一眼看得出改坏」的常量，
/// 用单测钉住，避免以后有人顺手改掉（能力名上协议后就是事实契约，见 §8.6）。
///
/// ⚠ Phase 3 给 <c>Build()</c> / <c>BuildHeartbeatJson()</c> 加了第二个参数
///   （平台注册了哪些实现，见 <c>CapabilityReporter</c>）：下面这些断言**逐字未变**，
///   只是把「Windows 那样注册了截图 + 关机实现」的平台显式传进去 ——
///   等价性判据（宿主的取值不变）与「不支持的平台不报能力」的判据分别在
///   本文件与 <c>PlatformCapabilityTests</c> 里。
/// </summary>
public class DeviceCapabilitiesTests
{
    [Fact]
    public void InteractiveInstance_ReportsMessageScreenshotShutdown()
    {
        var caps = DeviceCapabilities.Build(
            new FakePlatformInfo { IsHeadless = false }, FullCapabilities.Create());

        Assert.Equal(new[] { "message", "screenshot", "shutdown" }, caps);
    }

    /// <summary>会话 0（开机后无人登录）关机没有意义，也没有桌面可以弹窗提示。</summary>
    [Fact]
    public void HeadlessInstance_DoesNotReportShutdown()
    {
        var caps = DeviceCapabilities.Build(
            new FakePlatformInfo { IsHeadless = true }, FullCapabilities.Create());

        Assert.Equal(new[] { "message", "screenshot" }, caps);
    }

    /// <summary>⚠ 本阶段故意不上报 unlock：凭据存储还没做，报了就是谎报（Phase 3 才允许打开）。</summary>
    [Fact]
    public void UnlockIsNeverReportedYet()
    {
        Assert.DoesNotContain(DeviceCapabilities.Unlock,
            DeviceCapabilities.Build(new FakePlatformInfo { IsHeadless = false }, FullCapabilities.Create()));
        Assert.DoesNotContain(DeviceCapabilities.Unlock,
            DeviceCapabilities.Build(new FakePlatformInfo { IsHeadless = true }, FullCapabilities.Create()));
    }

    [Fact]
    public void HeartbeatJson_CarriesWindowsStateAndCapabilities()
    {
        var json = HeartbeatLoop.BuildHeartbeatJson(new FakePlatformInfo
        {
            IsHeadless = true,
            SessionState = "logon_screen",
        }, FullCapabilities.Create());

        Assert.Contains("\"type\":\"heartbeat\"", json);
        Assert.Contains("\"windows_state\":\"logon_screen\"", json);
        Assert.Contains("\"capabilities\":[\"message\",\"screenshot\"]", json);
    }
}

public class BackoffPolicyTests
{
    [Fact]
    public void DoublesUpToThirtySeconds()
    {
        var backoff = new BackoffPolicy();

        Assert.Equal(2, backoff.CurrentSeconds);

        var seen = new List<int> { backoff.CurrentSeconds };
        for (var i = 0; i < 6; i++)
        {
            backoff.Next();
            seen.Add(backoff.CurrentSeconds);
        }

        Assert.Equal(new[] { 2, 4, 8, 16, 30, 30, 30 }, seen);
    }

    [Fact]
    public void ResetGoesBackToTwoSeconds()
    {
        var backoff = new BackoffPolicy();
        backoff.Next();
        backoff.Next();

        Assert.Equal(8, backoff.CurrentSeconds);

        backoff.Reset();

        Assert.Equal(2, backoff.CurrentSeconds);
    }
}
