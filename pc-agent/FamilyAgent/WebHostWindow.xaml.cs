using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace FamilyAgent;

/// <summary>
/// PC 端唯一窗口：一个 WebView2 宿主（壳）。**界面完全由网页端页面渲染**，
/// 见 docs/PC-WEBVIEW2-REWRITE.md。
///
/// 宿主职责（页面的东西一概不做）：
/// - 窗口两种形态：Popup（全屏 / 无边框 / 置顶 / 不进任务栏）与
///   Window（标准窗口 / 可缩放 / 进任务栏 / 不置顶 / 居中，约 1000×760）
/// - 初始化 WebView2（用户数据目录、虚拟域名映射、屏蔽浏览器默认行为）
/// - 本地页面（boot / offline / 缺运行时的提示）与正式页面的加载与切换
/// - 把页面帧交给 <see cref="Bridge"/>，把宿主能力经桥暴露出去
///
/// 消息收发 / 截图 / 关机 / 会话状态 / 开机自启 / 解锁校验这些逻辑一行都没动，
/// 全在各自原来的文件里；这里只负责「界面」。
/// </summary>
public partial class WebHostWindow : Window
{
    /// <summary>
    /// 窗口的两种形态。**这是之前「设置对话框孤零零浮在整屏中央」的根因** ——
    /// 以前窗口永远是全屏无边框，设置只是叠在上面的一个弹层，
    /// 于是在 2560 宽的屏幕上就变成一个悬在正中的小方块，比例完全不对。
    /// </summary>
    public enum WindowMode
    {
        /// <summary>强提醒：全屏、无边框、置顶。只有「有消息要展示」时用。</summary>
        Popup,
        /// <summary>普通窗口：设置 / 对话。标准标题栏、居中、可缩放、不置顶。</summary>
        Window,
    }

    /// <summary>壳当前加载到哪一步（决定能不能把消息推给页面）。</summary>
    private enum Stage
    {
        /// <summary>还没加载任何页面。</summary>
        None,
        /// <summary>本地 boot 页（首屏占位，不闪白）。</summary>
        Boot,
        /// <summary>本地兜底页（没配置 / 连不上服务端）。</summary>
        Offline,
        /// <summary>服务端的正式页面 —— 只有这个阶段才算「页面能看到消息」。</summary>
        Server,
        /// <summary>缺 WebView2 Runtime，退回 WPF 提示页。</summary>
        Hint,
    }

    /// <summary>本地页面映射到的虚拟域名（比 file:// 干净，且能用相对路径）。</summary>
    private const string LocalHostName = "familyagent.local";

    private const string LocalOrigin = "https://" + LocalHostName;

    private WindowMode _mode = WindowMode.Popup;
    private Stage _stage = Stage.None;

    private readonly DispatcherTimer _topmostTimer;
    private DispatcherTimer? _autoCloseTimer;
    private int _autoCloseSeconds;

    private bool _webViewReady;      // WebView2 是否已初始化完成
    private bool _initializing;      // 正在初始化（防重入）
    private bool _allowClose;        // 真的允许关窗（退出时）
    private bool _connected;
    private string _connectionDetail = "未连接";
    private string _view = "";       // 控制台要打开的视图（网址参数，可选）

    /// <summary>还没收到 web.ack 的消息 id（关窗/自动关闭时按「已读」回报）。</summary>
    private readonly List<long> _pending = new();

    /// <summary>页面还没就绪时的消息缓存，等正式页面 web.ready 后补推。</summary>
    private readonly List<(JsonElement Message, int AutoClose)> _queued = new();

    public JsBridge Bridge { get; }

    /// <summary>页面回报「这条消息真的画到屏幕上了」（宿主据此回报 popup_displayed）。</summary>
    public event Action<long>? MessageAcked;

    /// <summary>窗口被页面关掉/自动关闭/Alt+F4 → 这些消息按「已读」回报。</summary>
    public event Action<long>? Dismissed;

    public WebHostWindow()
    {
        InitializeComponent();

        Bridge = new JsBridge();
        Bridge.Sender = PostJson;
        Bridge.ReadyReceived += OnPageReady;
        Bridge.AckReceived += OnPageAck;
        Bridge.CloseRequested += HideFromPage;
        Bridge.ModeRequested += SetModeFromPage;

        // 初始形态先给弹窗（工作区大小，任务栏留给用户）；调用方随后用
        // ShowPopup / ShowConsole 决定真正的形态。
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _topmostTimer.Tick += (_, _) =>
        {
            // ★ 只有全屏强提醒才需要反复重申置顶。
            //   窗口模式下这样做会让对话/设置窗口永远压在别的程序前面，很烦人；
            //   Activate() 还会每 3 秒抢一次焦点 —— 那是更严重的问题。
            if (_mode != WindowMode.Popup || !IsVisible)
                return;
            Topmost = false;
            Topmost = true;
            Activate();
        };

        ApplyWindowMode(WindowMode.Popup);

        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
    }

    // ---------------- 对外接口（App 只用这些）----------------

    /// <summary>有消息要强提醒：切 Popup 形态 → 显示 → 保证页面已加载。</summary>
    public void ShowPopup()
    {
        ApplyWindowMode(WindowMode.Popup);
        ShowWindow(alert: true);
        _ = EnsureLoadedAsync();
    }

    /// <summary>托盘打开对话/设置：切标准窗口形态 → 显示 → 加载控制台页面。</summary>
    public void ShowConsole(string view)
    {
        _view = view ?? "";
        ApplyWindowMode(WindowMode.Window);
        ShowWindow(alert: false);
        _ = EnsureLoadedAsync();
    }

    /// <summary>连接状态变化 → 转给页面（顶栏状态点）。</summary>
    public void SetConnection(bool connected, string detail)
    {
        _connected = connected;
        _connectionDetail = detail ?? "";
        Bridge.PostConnection(_connected, _connectionDetail);
    }

    /// <summary>
    /// 推一条消息给页面。页面还没就绪就先缓存 ——
    /// 早到几秒的消息不能丢（那会连带丢掉 popup_displayed 回报）。
    /// </summary>
    public void PushMessage(JsonElement message, int autoCloseSeconds)
    {
        if (!Bridge.PageReady)
        {
            _queued.Add((message.Clone(), autoCloseSeconds));
            AgentLog.Write($"壳：页面未就绪，缓存消息 {MessageId(message)}，等正式页面就绪后补推");
            return;
        }
        SendMessage(message, autoCloseSeconds);
    }

    /// <summary>服务端来的历史对话 → 页面。</summary>
    public void PushHistory(JsonElement messages) => Bridge.PostHistory(messages);

    /// <summary>会话状态（锁屏/登录）变化 → 页面（解锁/截图按钮可用性）。</summary>
    public void PushSession()
    {
        try { Bridge.PostSession(); }
        catch (Exception ex) { AgentLog.Write("推送会话状态失败：" + ex.Message); }
    }

    /// <summary>截图结果 → 页面。base64 为空表示失败，改回中文原因。</summary>
    public void PushScreenshot(string requestId, string? base64, string? error)
    {
        var dataUrl = string.IsNullOrEmpty(base64) ? null : "data:image/jpeg;base64," + base64;
        Bridge.PostScreenshot(requestId, dataUrl, error);
    }

    /// <summary>服务端地址被兜底页改过了：丢掉当前页，重新探测并加载。</summary>
    public void ReloadAfterServerChange()
    {
        _stage = Stage.None;
        Bridge.PageReady = false;
        _ = EnsureLoadedAsync();
    }

    /// <summary>强制关闭（程序退出时）。</summary>
    public void ForceClose()
    {
        _allowClose = true;
        try { _topmostTimer.Stop(); } catch { }
        try { _autoCloseTimer?.Stop(); } catch { }
        try { Hide(); } catch { }
        try { Close(); }
        catch (Exception ex) { AgentLog.Write("关闭窗口失败：" + ex.Message); }
    }

    // ---------------- 页面（→ 宿主）----------------

    /// <summary>「知道了」：隐藏窗口并把未读按已读回报。</summary>
    internal void HideFromPage()
    {
        _autoCloseTimer?.Stop();
        if (IsVisible)
            Hide();
        RaiseDismissed();
    }

    /// <summary>页面要求切形态（弹窗里点「打开设置」）。</summary>
    private void SetModeFromPage(string mode)
    {
        var target = string.Equals(mode, "popup", StringComparison.OrdinalIgnoreCase)
            ? WindowMode.Popup
            : WindowMode.Window;

        ApplyWindowMode(target);
        if (!IsVisible)
            ShowWindow(alert: false);
        Bridge.PostMode();
    }

    private void OnPageReady()
    {
        // 本地 boot / offline 页也会发 web.ready，但那个文档马上就会被替换掉：
        // 这时候**不能**把它当成「页面能看到消息」，否则消息会推进一个随即销毁的
        // 文档里丢掉（连带 popup_displayed 永远不回报）。
        if (_stage != Stage.Server)
        {
            AgentLog.Write($"壳：本地页面就绪（阶段 {_stage}），等正式页面");
            return;
        }

        Bridge.PageReady = true;
        Bridge.PostHello();
        Bridge.PostSession();
        Bridge.PostConnection(_connected, _connectionDetail);
        Bridge.PostMode();
        FlushQueued();
    }

    private void OnPageAck(long messageId)
    {
        _pending.Remove(messageId);
        MessageAcked?.Invoke(messageId);
    }

    // ---------------- WebView2 ----------------

    private async Task EnsureLoadedAsync()
    {
        if (_stage == Stage.Hint)
            return;

        try
        {
            if (!await InitWebViewAsync())
                return;

            // 已经在正式页面上了：切形态用 host.mode 通知，别重新加载（会丢页面状态）
            if (_stage == Stage.Server)
            {
                Bridge.PostMode();
                return;
            }

            if (_stage == Stage.None)
            {
                NavigateLocal("boot.html");     // 首屏占位，窗口不闪白
                _stage = Stage.Boot;
            }

            var cfg = App.Config;
            if (cfg is null || !cfg.IsConfigured)
            {
                GoOffline();
                return;
            }

            if (await ProbeAsync(cfg.ServerUrl))
                GoServer();
            else
                GoOffline();
        }
        catch (Exception ex)
        {
            // 壳初始化失败也不能把进程带崩：日志留痕，窗口留着（页面上会有兜底页）
            AgentLog.Write("!! WebView2 壳加载失败：" + ex);
        }
    }

    /// <summary>初始化 WebView2。返回 false = 没装 Runtime（已切到提示页）。</summary>
    private async Task<bool> InitWebViewAsync()
    {
        if (_webViewReady)
            return true;
        if (_initializing)
            return false;
        _initializing = true;

        try
        {
            // 运行时自检：没装 WebView2 Runtime 时 EnsureCoreWebView2Async 会直接抛，
            // 而且 WebView2 控件本身就是块白板 —— 只能退回 WPF 提示页把原因说清楚。
            string runtime;
            try
            {
                // ⚠️ 必须传两个参数：这个类有**两个重载**且都带可选参数
                      //    GetAvailableBrowserVersionString(string = default)
                      //    GetAvailableBrowserVersionString(string = default, CoreWebView2EnvironmentOptions = default)
                      //    无参调用会因歧义编译失败（CS0121，官方 issue #4352 已确认）。
                      //    显式传两个 null 只会匹配第二个重载，彻底消歧。
                      runtime = CoreWebView2Environment.GetAvailableBrowserVersionString(null, null);
            }
            catch (Exception ex)
            {
                AgentLog.Write("✗ 未检测到 WebView2 Runtime：" + ex.Message);
                ShowRuntimeHint("未检测到 WebView2 Runtime。");
                return false;
            }

            // 用户数据目录放在 %LOCALAPPDATA%\FamilyAgent\webview2：
            // 不用默认位置（默认在 exe 同级，装 Program Files 时根本没权限写）
            var dataDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FamilyAgent", "webview2");
            System.IO.Directory.CreateDirectory(dataDir);

            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null, userDataFolder: dataDir, options: null);
            await Web.EnsureCoreWebView2Async(env);

            var core = Web.CoreWebView2;
            if (core is null)
            {
                ShowRuntimeHint("WebView2 初始化没有返回可用实例。");
                return false;
            }

            var settings = core.Settings;
            settings.AreDefaultContextMenusEnabled = false;     // 禁右键菜单
            settings.AreDevToolsEnabled = false;                // 禁开发者工具
            settings.IsZoomControlEnabled = false;              // 禁 Ctrl+滚轮缩放
            settings.AreBrowserAcceleratorKeysEnabled = false;  // 禁 F5 / Ctrl+R / F12 等浏览器快捷键
            settings.IsStatusBarEnabled = false;
            settings.IsWebMessageEnabled = true;                // 桥靠它（默认就是 true，显式写出来）

            // 本地页面走 https://familyagent.local/... 加载
            core.SetVirtualHostNameToFolderMapping(
                LocalHostName, ShellDir(), CoreWebView2HostResourceAccessKind.Allow);

            core.WebMessageReceived += OnWebMessageReceived;
            core.NewWindowRequested += OnNewWindowRequested;

            _webViewReady = true;
            AgentLog.Write($"WebView2 就绪：runtime={runtime} 用户数据目录={dataDir} 本地页目录={ShellDir()}");
            return true;
        }
        finally
        {
            _initializing = false;
        }
    }

    private void GoServer()
    {
        var core = Web.CoreWebView2;
        if (core is null)
            return;

        _stage = Stage.Server;
        Bridge.PageReady = false;      // 正式页面自己的 web.ready 到达后才置位
        var url = ServerPageUrl();
        AgentLog.Write("壳：加载正式页面 " + url);
        try { core.Navigate(url); }
        catch (Exception ex) { AgentLog.Write("✗ 加载正式页面失败：" + ex.Message); }
    }

    private void GoOffline()
    {
        _stage = Stage.Offline;
        AgentLog.Write("壳：服务端不可达或还没配置 → 加载本地兜底页");
        NavigateLocal("offline.html");
    }

    private void NavigateLocal(string file)
    {
        var core = Web.CoreWebView2;
        if (core is null)
            return;

        // 换文档 = 页面就绪状态作废：新文档要自己发一次 web.ready。
        // 缓存的消息不动它 —— 正式页面就绪后会补推。
        Bridge.PageReady = false;
        try { core.Navigate($"{LocalOrigin}/shell/{file}"); }
        catch (Exception ex) { AgentLog.Write($"✗ 加载本地页面 {file} 失败：{ex.Message}"); }
    }

    private void ShowRuntimeHint(string reason)
    {
        _stage = Stage.Hint;
        try
        {
            Web.Visibility = Visibility.Collapsed;
            RuntimeHint.Visibility = Visibility.Visible;
            RuntimeHintText.Text = reason
                + " 从 https://developer.microsoft.com/microsoft-edge/webview2/ 下载安装"
                + "「Evergreen 常青版引导程序」后点「重试检测」。";
        }
        catch (Exception ex)
        {
            AgentLog.Write("显示运行时提示页失败：" + ex.Message);
        }
    }

    private static string ShellDir() => System.IO.Path.Combine(AppContext.BaseDirectory, "shell");

    private string ServerPageUrl()
    {
        var mode = _mode == WindowMode.Popup ? "popup" : "console";
        var view = string.IsNullOrWhiteSpace(_view) ? "" : "&view=" + Uri.EscapeDataString(_view);
        return $"{ServerBaseUrl(App.Config is null ? "" : App.Config.ServerUrl)}/?shell=1&mode={mode}{view}";
    }

    /// <summary>
    /// 把配置里的服务端地址规范成网页端根地址。
    ///
    /// ⚠ 与 App.ConsoleUrl 同一套规则（ws→http、wss→https、去掉结尾 /ws），
    ///   两边不一致会变成「Agent 连得上、壳加载不出来」这种很难查的现象。
    /// </summary>
    private static string ServerBaseUrl(string? serverUrl)
    {
        var url = (serverUrl ?? "").Trim().TrimEnd('/');
        if (url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            url = "http://" + url[5..];
        else if (url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url[6..];
        if (url.EndsWith("/ws"))
            url = url[..^3];
        return url;
    }

    private static async Task<bool> ProbeAsync(string? serverUrl)
    {
        var baseUrl = ServerBaseUrl(serverUrl);
        if (baseUrl.Length == 0)
            return false;

        try
        {
            // 与设置页「测试连接」同一套判定：HTTP 通就算可达
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            var resp = await http.GetAsync(baseUrl + "/healthz");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            AgentLog.Write("壳：服务端探测失败：" + ex.Message);
            return false;
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            // 页面发来的一切都在桥里做 try/catch + 类型校验：一个坏帧不能让宿主崩
            Bridge.HandleFromWeb(e.WebMessageAsJson);
        }
        catch (Exception ex)
        {
            AgentLog.Write("✗ 桥：读取页面消息失败（已忽略）：" + ex.Message);
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // 页面里的外链一律交给系统浏览器，不在壳里开新窗口
        e.Handled = true;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AgentLog.Write("打开外链失败：" + ex.Message);
        }
    }

    private void PostJson(string json)
    {
        var core = Web.CoreWebView2;
        if (core is null)
            return;
        try
        {
            core.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            // 文档还没建好（或正在导航）时 PostWebMessageAsJson 会抛：
            // 桥的推送都不致命，等页面 web.ready 后会全量重推一次。
            AgentLog.Write("✗ 桥：投递失败：" + ex.Message);
        }
    }

    // ---------------- 窗口形态 ----------------

    /// <summary>
    /// 切换窗口形态。弹窗模式全屏置顶（强提醒），窗口模式是标准的 Windows 窗口。
    ///
    /// WPF 运行期改 WindowStyle 需要先隐藏 —— 直接改多半不生效或留残影，
    /// 所以这里统一「记状态 → Hide → 改 → 按需 Show」。
    ///
    /// 置顶重申定时器只在 Popup 形态跑：窗口形态下不退它会让对话/设置窗口永远压在
    /// 别的程序前面，而且每 3 秒抢一次焦点。
    /// </summary>
    internal void ApplyWindowMode(WindowMode mode)
    {
        var wasVisible = IsVisible;
        _mode = mode;
        Bridge.Mode = mode == WindowMode.Popup ? "popup" : "console";

        try
        {
            if (wasVisible) Hide();

            if (mode == WindowMode.Popup)
            {
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                ShowInTaskbar = false;
                Topmost = true;

                var work = SystemParameters.WorkArea;
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = work.Left;
                Top = work.Top;
                Width = work.Width;
                Height = work.Height;

                _topmostTimer.Start();
            }
            else
            {
                // 标准 Windows 窗口：有标题栏、能拖动缩放、出现在任务栏
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                ShowInTaskbar = true;
                Topmost = false;
                _topmostTimer.Stop();

                // 尺寸不要写死：小屏笔记本上也放得下
                var work = SystemParameters.WorkArea;
                Width = Math.Min(1000, Math.Max(720, work.Width * 0.78));
                Height = Math.Min(760, Math.Max(520, work.Height * 0.82));
                // 交给 CenterScreen 居中，不再手算 Left/Top ——
                // 同时设 CenterScreen 和 Left/Top 会互相打架
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            AgentLog.Write($"窗口模式 → {_mode}  {Width:0}x{Height:0} at ({Left:0},{Top:0})");
        }
        catch (Exception ex)
        {
            AgentLog.Write("切换窗口模式失败：" + ex.Message);
        }

        if (wasVisible) Show();
    }

    private void ShowWindow(bool alert)
    {
        if (!IsVisible)
        {
            if (alert)
            {
                try { System.Media.SystemSounds.Exclamation.Play(); }
                catch { /* 提示音失败不影响显示 */ }
            }
            Show();
            Topmost = _mode == WindowMode.Popup;
        }
        Activate();
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // 加载后再确认一次形态：XAML 里的初始 WindowStyle 只是占位值
        ApplyWindowMode(_mode);
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        // Alt+F4 / 标题栏 ×：不让它真的把窗口销毁（托盘还在），只隐藏窗口；
        // 系统关机/注销、以及程序自己退出要放行。
        if (_allowClose || App.IsSystemShuttingDown)
            return;

        e.Cancel = true;
        if (IsVisible)
            Hide();
        RaiseDismissed();
    }

    // ---------------- 消息与自动关闭 ----------------

    private void SendMessage(JsonElement message, int autoCloseSeconds)
    {
        var id = MessageId(message);
        if (id != 0 && !_pending.Contains(id))
            _pending.Add(id);

        Bridge.PostMessage(message, App.Config is null ? "" : App.Config.DeviceId);
        ArmAutoClose(autoCloseSeconds);
    }

    private void FlushQueued()
    {
        if (_queued.Count == 0)
            return;

        var pending = new List<(JsonElement Message, int AutoClose)>(_queued);
        _queued.Clear();
        // 期间又来了新消息的话，它们的顺序在新列表里，补推不会丢
        AgentLog.Write($"壳：正式页面已就绪，补推 {pending.Count} 条缓存消息");
        foreach (var (message, autoClose) in pending)
            SendMessage(message, autoClose);
    }

    private void RaiseDismissed()
    {
        _autoCloseTimer?.Stop();

        var ids = new List<long>(_pending);
        _pending.Clear();
        foreach (var id in ids)
        {
            try { Dismissed?.Invoke(id); }
            catch (Exception ex) { AgentLog.Write("回报已读失败：" + ex.Message); }
        }
    }

    /// <summary>
    /// 自动关闭（服务端在消息帧里带 auto_close_seconds）。
    ///
    /// 放在宿主而不是页面里：这条字段不在冻结协议的消息体里，页面未必认识它，
    /// 但「N 秒后自己关掉」是原来就有的行为，不能因为换界面丢掉。
    /// </summary>
    private void ArmAutoClose(int seconds)
    {
        _autoCloseSeconds = seconds;
        _autoCloseTimer?.Stop();
        if (seconds <= 0)
            return;

        AgentLog.Write($"壳：{seconds} 秒后自动关闭强提醒窗口");
        _autoCloseTimer ??= CreateAutoCloseTimer();
        _autoCloseTimer.Interval = TimeSpan.FromSeconds(_autoCloseSeconds);
        _autoCloseTimer.Start();
    }

    private DispatcherTimer CreateAutoCloseTimer()
    {
        var timer = new DispatcherTimer();
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            HideFromPage();
        };
        return timer;
    }

    private static long MessageId(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object)
            return 0;
        if (!message.TryGetProperty("message_id", out var el) || el.ValueKind != JsonValueKind.Number)
            return 0;
        try { return el.GetInt64(); }
        catch { return 0; }
    }

    // ---------------- 缺运行时提示页 ----------------

    private void RuntimeLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            RuntimeHintText.Text = "打不开浏览器：" + ex.Message
                + "  也可以手动访问 https://developer.microsoft.com/microsoft-edge/webview2/";
        }
    }

    private void RetryRuntime_Click(object sender, RoutedEventArgs e)
    {
        string version;
        try
        {
            // ⚠️ 必须传两个参数：这个类有**两个重载**且都带可选参数
                      //    GetAvailableBrowserVersionString(string = default)
                      //    GetAvailableBrowserVersionString(string = default, CoreWebView2EnvironmentOptions = default)
                      //    无参调用会因歧义编译失败（CS0121，官方 issue #4352 已确认）。
                      //    显式传两个 null 只会匹配第二个重载，彻底消歧。
                      version = CoreWebView2Environment.GetAvailableBrowserVersionString(null, null);
        }
        catch (Exception ex)
        {
            RuntimeHintText.Text = "还是没检测到：" + ex.Message;
            return;
        }

        RuntimeHintText.Text = $"检测到了 WebView2 {version}，正在加载界面…";
        RuntimeHint.Visibility = Visibility.Collapsed;
        Web.Visibility = Visibility.Visible;
        _stage = Stage.None;
        _ = EnsureLoadedAsync();
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
            app.ForceQuit();
    }
}
