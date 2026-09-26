using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace FamilyAgent;

/// <summary>
/// PC 端唯一窗口：一个 WebView2 宿主（壳）。**界面是 exe 自带的本地页**
/// （<c>shell/app.html</c>），见 docs/PC-LOCAL-UI.md。
///
/// ★ 与旧做法的根本区别：以前加载的是 NAS 的 index.html（完整网页控制台），
///   靠 CSS 把控制台藏起来 —— 实测藏不掉，用户机器上一直显示带侧边栏的管理后台。
///   现在界面就在本地，**物理上不存在控制台**；NAS 挂掉界面照常，只是收不到消息。
///
/// 宿主职责（页面的东西一概不做）：
/// - 窗口两种形态：Popup（全屏 / 无边框 / 置顶 / 不进任务栏）与
///   Window（标准窗口 / 可缩放 / 进任务栏 / 不置顶 / 居中，约 1000×760）
/// - 初始化 WebView2（用户数据目录、虚拟域名映射、屏蔽浏览器默认行为）
/// - 本地页面（boot / offline / app / 缺运行时的提示）的加载与切换
/// - **形态只走桥**：ShowPopup / ShowClient / ShowSettings 只改 <see cref="JsBridge.Mode"/>
///   并发 <c>host.mode</c>，**不重新导航** —— 形态与 URI 彻底解耦，那类
///   「URL 没生效 → 界面不对」的 bug 从根上消失
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
    ///
    /// ⚠ 这是**窗口**形态，跟页面视图（<see cref="_shellMode"/>）是两件事。
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
        /// <summary>本地兜底页（**从未配置过服务端**时的诊断页）。</summary>
        Offline,
        /// <summary>本机界面 app.html —— 唯一的正式页面，只有这个阶段才算「页面能看到消息」。</summary>
        App,
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
    private string _runtimeVersion = "";   // WebView2 Runtime 版本（host.runtime 要报给设置视图）

    /// <summary>
    /// 页面视图：client（消息界面）/ popup（全屏强提醒）/ settings（本机设置）。
    ///
    /// 和 <see cref="_mode"/>（窗口形态）是两件事，别混：
    /// 窗口形态说的是「窗口长什么样」，页面视图说的是「页面里显示哪一块」。
    /// 三个视图在**同一个文档**（app.html）里，切换只过桥（host.mode），不重新导航。
    /// </summary>
    private string _shellMode = "client";

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
        // ShowPopup / ShowClient / ShowSettings 决定真正的形态。
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

    /// <summary>有消息要强提醒：切 popup 视图 + Popup 窗口形态 → 显示 → 保证本地页已加载。</summary>
    public void ShowPopup()
    {
        SetShellMode("popup");
        ApplyWindowMode(WindowMode.Popup);
        ShowWindow(alert: true);
        _ = EnsureLoadedAsync();
    }

    /// <summary>
    /// 托盘打开对话（双击托盘 / 启动）：**消息界面** —— 只有消息记录 + 回复栏。
    /// </summary>
    public void ShowClient()
    {
        SetShellMode("client");
        ApplyWindowMode(WindowMode.Window);
        ShowWindow(alert: false);
        _ = EnsureLoadedAsync();
    }

    /// <summary>
    /// 托盘「设置…」（页面顶栏齿轮走的是 <c>web.open_settings</c> → 同一个入口）：
    /// **本机设置视图** —— 服务端地址 / 注册口令 / 回复昵称 / 开机自启 / 主题 / 版本信息。
    ///
    /// 以前这里是 "console"（完整的网页端控制台，带侧边栏那套）—— 那正是
    /// 「PC 上弹出管理后台」的由来。现在设置只是本地页 app.html 的一个视图。
    /// </summary>
    public void ShowSettings()
    {
        SetShellMode("settings");
        ApplyWindowMode(WindowMode.Window);
        ShowWindow(alert: false);
        _ = EnsureLoadedAsync();
    }

    /// <summary>
    /// 切页面视图。
    ///
    /// ★ **只改状态 + 发 host.mode，绝不重新导航**：三个视图都在同一个文档里，
    ///   页面自己切。重新导航会丢页面状态、闪一下白；而在旧架构里「界面切没切对」
    ///   还取决于 URL 拼得对不对 —— 那类 bug 现在从根上没有了。
    /// </summary>
    private void SetShellMode(string mode)
    {
        if (mode != "popup" && mode != "client" && mode != "settings")
        {
            AgentLog.Write($"✗ 不认识的页面视图（{mode}，已忽略）");
            return;
        }

        var changed = _shellMode != mode;
        _shellMode = mode;
        Bridge.Mode = mode;          // 与桥里的字段保持同步（host.hello / host.mode 都读它）
        if (changed)
            AgentLog.Write($"页面视图 → {mode}");

        if (_stage == Stage.App)
        {
            Bridge.PostMode();
            if (mode == "settings")
                PushRuntime();       // 设置视图要读的运行时信息（版本 / 路径 / 自启…）
            return;
        }

        _ = EnsureLoadedAsync();      // 还没加载过本地页 → 先把页面拉起来
    }

    /// <summary>
    /// 把设置视图要的运行时信息推给页面（进设置视图时 / 配置变化后主动推）。
    ///
    /// 页面上「版本号 + 当前形态 + 设备号 + 日志/配置路径」就是拿它填的 ——
    /// 需求：界面上要能直接看到版本号，省得再出现「跑的是哪一版」的困惑。
    /// </summary>
    public void PushRuntime() => Bridge.PostRuntime(_runtimeVersion);

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

    /// <summary>
    /// 配置变了（兜底页保存了服务端地址 / 设置视图保存了配置）：按当前配置纠正界面所在阶段。
    ///
    /// 三种情况：
    ///   · 已经在本机界面上 → 只推一次运行时信息（设置视图的只读项要跟着变），不导航
    ///   · 配好了但还在兜底页/首屏 → 切到 app.html
    ///   · 还是没配好 → 留在兜底页（别把用户刚填进去的内容冲掉）
    /// </summary>
    public void ReloadAfterServerChange()
    {
        if (App.Config is null || !App.Config.IsConfigured)
        {
            if (_stage != Stage.Offline)
                GoOffline();
            return;
        }

        if (_stage == Stage.App)
        {
            PushRuntime();
            return;
        }

        if (_stage == Stage.Offline)
        {
            // 刚从「从未配置过」变成配好了（用户在兜底页填的地址）：落到**消息界面** ——
            // 用户下一步是想看消息，不是继续看设置。视图状态直接置位，不再绕一圈
            // SetShellMode 的「拉页面」分支（那会多推一次 host.mode）。
            _shellMode = "client";
            Bridge.Mode = "client";
        }

        GoApp();
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

    /// <summary>页面要求切视图（弹窗里点「打开设置」/ 顶栏齿轮）。</summary>
    private void SetModeFromPage(string mode)
    {
        var m = (mode ?? "").Trim().ToLowerInvariant();
        if (m != "popup" && m != "client" && m != "settings")
        {
            AgentLog.Write($"✗ 页面请求切到不认识的视图（{mode}，已忽略）");
            return;
        }

        // 窗口形态跟着页面视图走：popup 全屏置顶，client / settings 用普通窗口
        ApplyWindowMode(m == "popup" ? WindowMode.Popup : WindowMode.Window);
        if (!IsVisible)
            ShowWindow(alert: false);

        // ★ 这里**也不重新导航**：页面自己已经换好界面了，宿主只对齐状态 + 回发 host.mode。
        //   （重新导航会把刚切好的界面冲掉，并且闪一下白。）
        _shellMode = m;
        Bridge.Mode = m;
        Bridge.PostMode();
    }

    private void OnPageReady()
    {
        // 本地 boot / offline 页也会发 web.ready，但那些文档马上就会被替换掉：
        // 这时候**不能**把它当成「页面能看到消息」，否则消息会推进一个随即销毁的
        // 文档里丢掉（连带 popup_displayed 永远不回报）。
        if (_stage != Stage.App)
        {
            AgentLog.Write($"壳：本地页面就绪（阶段 {_stage}），等正式界面");
            return;
        }

        Bridge.PageReady = true;
        Bridge.PostHello();
        Bridge.PostSession();
        Bridge.PostConnection(_connected, _connectionDetail);
        Bridge.PostMode();
        if (_shellMode == "settings")
            PushRuntime();          // 首屏就停在设置视图时，把它要的运行时信息一起给全
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

            // 已经在本机界面上了：切视图用 host.mode 通知，别重新加载（会丢页面状态）
            if (_stage == Stage.App)
            {
                Bridge.PostMode();
                if (_shellMode == "settings")
                    PushRuntime();
                return;
            }

            if (_stage == Stage.None)
            {
                NavigateLocal("boot.html");     // 首屏占位，窗口不闪白
                _stage = Stage.Boot;
            }

            // ★ 界面就在本地，**不再导航到 NAS 网址、也不再探测服务端**（docs/PC-LOCAL-UI.md）：
            //   · 配置过 → 直接进 app.html；连不连得上由顶栏状态点显示（host.connection），
            //     NAS 挂掉界面照常，只是收不到消息
            //   · 从没配过 → 兜底诊断页（那儿有填服务端地址 / 口令的入口）
            var cfg = App.Config;
            if (cfg is null || !cfg.IsConfigured)
            {
                GoOffline();
                return;
            }

            GoApp();
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
            _runtimeVersion = runtime;      // host.runtime 要报给设置视图（界面上显示它）
            AgentLog.Write($"WebView2 就绪：runtime={runtime} 用户数据目录={dataDir} 本地页目录={ShellDir()}");
            return true;
        }
        finally
        {
            _initializing = false;
        }
    }

    /// <summary>
    /// 加载本机界面 <c>app.html</c>（exe 自带，经虚拟域名
    /// <c>https://familyagent.local/shell/</c> 读）。
    ///
    /// ★ 取代旧的 GoServer()：以前这里是导航到 NAS 的
    ///   <c>index.html?shell=1&amp;mode=…</c>，那正是「PC 端界面依赖于网页端」的根源
    ///   （控制台藏不掉、NAS 一挂就没界面），已彻底删掉。
    /// </summary>
    private void GoApp()
    {
        if (_stage == Stage.App)
        {
            // 已经在本地面上了：只把状态刷一遍，不重新导航（会丢状态、闪白）
            Bridge.PostMode();
            if (_shellMode == "settings")
                PushRuntime();
            return;
        }

        _stage = Stage.App;
        Bridge.PageReady = false;      // 等 app.html 自己的 web.ready 到达才置位
        AgentLog.Write("壳：加载本机界面 app.html（exe 自带，不加载 NAS 网页）");
        NavigateLocal("app.html");
    }

    /// <summary>
    /// 兜底诊断页 <c>offline.html</c>：**只在从未配置过服务端**时用
    /// （那一页有填地址/口令的入口）。
    ///
    /// 配置过但连不上**不用**它 —— 界面照常进 app.html，顶栏自己显示「未连接」。
    /// 否则 NAS 一挂 PC 端就没界面可用，那正是这次架构更改要根除的问题。
    /// </summary>
    private void GoOffline()
    {
        _stage = Stage.Offline;
        AgentLog.Write("壳：还没配置过服务端 → 加载本地兜底页 offline.html");
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
        // ⚠ 这里**不碰** Bridge.Mode：窗口形态（_mode）和页面视图（_shellMode）是两件事。
        //    以前在这里顺手把 Bridge.Mode 设成 popup/console，结果「一改窗口形态，
        //    页面视图就被跟着写坏」。视图只由 SetShellMode / SetModeFromPage 决定，
        //    而这两条都只发 host.mode、不重新导航。

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
