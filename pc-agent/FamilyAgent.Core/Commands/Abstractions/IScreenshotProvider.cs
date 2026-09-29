using System.Threading;
using System.Threading.Tasks;

namespace FamilyAgent.Core.Commands.Abstractions;

/// <summary>
/// 平台能力：**截图**（docs/CORE-REFACTOR-PLAN.md §6 Phase 3-2 / §3.2）。
///
/// 分层判据：Core 只认识这个接口，不认识 <c>ScreenCapture</c>、也不认识 GDI/WinForms。
/// Windows 侧实现 = <c>Platform/WindowsScreenshotProvider.cs</c>（包住现成的
/// <c>ScreenCapture</c>，行为逐字不变）；将来 Android 侧各自实现（<c>adb exec-out screencap</c>
/// 那类），不存在「不支持」以外的第三种情况。
///
/// ★ **不支持就不报能力**（需求 §8）：
/// <list type="bullet">
///   <item>平台**没有**提供实现（没注册）→ <see cref="Devices.PlatformCapabilities.SupportsScreenshot"/>
///     为 false → 能力表里**不出现** <c>screenshot</c>，网页端根本不会给出「查看桌面」按钮；</item>
///   <item>平台**有**实现但此刻截不出来（会话 0 / 锁屏 / GDI 失败）→ 如实返回
///     <see cref="ScreenshotResult.Failed"/>，由 Core 回一条带原因的 <c>screenshot_response{error}</c>，
///     **不是**让网页端干等到超时（这正是 Phase 3 的验收判据之一）。</item>
/// </list>
///
/// ⚠ 实现必须是**线程安全**的：请求来自连接层（收包线程），同一时刻可能有多条。
/// </summary>
public interface IScreenshotProvider
{
    /// <summary>
    /// 截一张图。**不抛异常**是推荐做法（异常会被 Core 兜成 error 应答，但那样就丢了
    /// 「为什么失败」的中文原因）；失败请返回 <see cref="ScreenshotResult.Failed"/>。
    /// </summary>
    Task<ScreenshotResult> CaptureAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 一次截图的结果。<see cref="Ok"/> 为 true 时 <see cref="Base64"/> 必定非空，
/// 否则 <see cref="Error"/> 是给用户看的中文原因（会原样进 <c>screenshot_response.error</c>）。
/// </summary>
public sealed class ScreenshotResult
{
    private ScreenshotResult(bool unsupported, string? base64, int width, int height, string? error)
    {
        IsUnsupported = unsupported;
        Base64 = base64;
        Width = width;
        Height = height;
        Error = error;
    }

    /// <summary>截到了。<paramref name="base64"/> 是 JPEG；宽高用于网页端占位。</summary>
    public static ScreenshotResult Captured(string base64, int width, int height) =>
        new(false, base64, width, height, null);

    /// <summary>实现**存在**、这次没截成（例如锁屏时 CopyFromScreen 抛异常）。</summary>
    public static ScreenshotResult Failed(string error) =>
        new(false, null, 0, 0, string.IsNullOrWhiteSpace(error) ? "截图失败" : error);

    /// <summary>
    /// 平台**根本没有**截图能力（没注册实现）。与 <see cref="Failed"/> 分开是因为排查时
    /// 要能一眼分清「能力不存在」和「能力在、这次失败」——前者永远修不好，是产品层面的决定。
    /// </summary>
    public static ScreenshotResult Unsupported(string reason) =>
        new(true, null, 0, 0, string.IsNullOrWhiteSpace(reason) ? "本机不支持截图" : reason);

    /// <summary>平台层级就把这条路堵死了（能力未注册）。</summary>
    public bool IsUnsupported { get; }

    /// <summary>JPEG 的 base64；失败时为 null。</summary>
    public string? Base64 { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>失败原因（中文，原样回给服务端）；成功时为 null。</summary>
    public string? Error { get; }

    /// <summary>这一张图算不算截成了（<see cref="Base64"/> 非空）。</summary>
    public bool Ok => !string.IsNullOrEmpty(Base64);

    /// <summary>给日志用的短描述（不打印 base64，那会淹掉整个日志文件）。</summary>
    public string Describe() =>
        Ok ? $"{Width}x{Height} {Base64!.Length} 字节(base64)"
           : IsUnsupported ? "本机不支持截图：" + Error
           : "截图失败：" + Error;
}
