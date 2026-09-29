using System.Threading;
using System.Threading.Tasks;
using FamilyAgent.Core.Commands.Abstractions;
using FamilyAgent.Core.Diagnostics;

namespace FamilyAgent.Platform;

/// <summary>
/// Windows 侧的 <see cref="IScreenshotProvider"/> 实现
/// （docs/CORE-REFACTOR-PLAN.md §3.2 / §4：<c>ScreenCapture.cs</c> 的「包接口」那一半）。
///
/// 职责边界：
/// <list type="bullet">
///   <item><b>取图</b>（GDI 抓屏 + JPEG 编码）仍然是 <c>ScreenCapture</c> —— 一个字没改，
///     它现在是这个能力的**低层原语**（本地页面点「查看桌面」时也直接用它）；</item>
///   <item><b>本类</b>只回答两个 Core 需要的问题：<i>现在能不能截</i>（会话 0 → 不能），
///     以及 <i>这次截没截成、为什么</i>（把 <c>ScreenCapture.LastError</c> 的中文原因带出去）。</item>
/// </list>
///
/// ⚠ 行为等价性：原实现里这两句都在 <c>App.OnScreenshotRequested</c> 里 ——
///   headless 直接回中文原因（「电脑已开机但尚无人登录…」），非 headless 在
///   <c>Task.Run</c> 里抓屏（不阻塞收包线程）。这里逐条保留：
///   <see cref="CaptureAsync"/> 同样跑在线程池上，原因文案一个字没变（<see cref="HeadlessReason"/>）。
/// </summary>
public sealed class WindowsScreenshotProvider : IScreenshotProvider
{
    /// <summary>
    /// 会话 0（开机后尚无人登录）时的中文原因。原实现是 <c>App</c> 里的私有常量
    /// <c>HeadlessScreenshotReason</c>，Phase 3 搬到这里 —— 因为现在**一条链路的两个入口**
    /// （服务端请求截图、本机页面请求截图）都要用它，两处写两份迟早会漂。
    /// </summary>
    public const string HeadlessReason = "电脑已开机但尚无人登录，当前没有可截取的桌面";

    /// <summary>JPEG 压缩质量（与搬迁前 <c>ScreenCapture.CaptureJpeg(quality = 75)</c> 一致）。</summary>
    public const int DefaultJpegQuality = 75;

    private readonly int _jpegQuality;

    public WindowsScreenshotProvider(int jpegQuality = DefaultJpegQuality) => _jpegQuality = jpegQuality;

    /// <summary>
    /// 截一张图。**在线程池上抓**：抓屏要几十毫秒，不能占着收包循环
    /// （原实现同样是 <c>Task.Run</c>）。
    /// </summary>
    public Task<ScreenshotResult> CaptureAsync(CancellationToken cancellationToken = default) =>
        Task.Run(Capture, cancellationToken);

    /// <summary>同步抓一张（本地页面路径在 <c>Task.Run</c> 里调它）。</summary>
    public ScreenshotResult Capture()
    {
        if (App.IsHeadless)
        {
            // 会话 0 没有桌面，截出来只会是黑图。原实现就在这里回一句中文原因，
            // 比发一张黑图让用户以为电脑坏了要好。
            AgentLog.Write("headless：尚无人登录，无法截图");
            return ScreenshotResult.Unsupported(HeadlessReason);
        }

        var (base64, width, height) = ScreenCapture.CaptureJpeg(_jpegQuality);
        var error = ScreenCapture.LastError;

        return string.IsNullOrEmpty(base64)
            ? ScreenshotResult.Failed(string.IsNullOrWhiteSpace(error) ? "截图失败" : error)
            : ScreenshotResult.Captured(base64, width, height);
    }
}
