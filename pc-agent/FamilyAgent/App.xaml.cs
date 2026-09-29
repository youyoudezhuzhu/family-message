using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FamilyAgent.Core.Commands;
using FamilyAgent.Core.Commands.Abstractions;
using FamilyAgent.Core.Config;
using FamilyAgent.Core.Devices;
using FamilyAgent.Core.Diagnostics;
using FamilyAgent.Core.Events;
using FamilyAgent.Core.Messaging;
using FamilyAgent.Core.Nicknames;
using FamilyAgent.Core.Protocol;
using FamilyAgent.Core.Protocol.Frames;
using FamilyAgent.Core.Transport;
using FamilyAgent.Platform;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace FamilyAgent;

public partial class App : Application
{
    public static AgentConfig Config { get; private set; } = null!;

    /// <summary>
    /// 到服务端的那条连接（Core 的连接编排，见 docs/CORE-REFACTOR-PLAN.md §3.2）。
    ///
    /// 名字从 <c>Client</c> 改成 <c>Core</c> 是刻意的：Phase 1 之后它不再只是
    /// 「一个客户端类」，而是 Core 层的入口（连接 / 心跳 / 协议 / 发送队列 / 能力上报）。
    /// 宿主侧的回调体保持不变，只换订阅的事件类型。
    /// </summary>
    public static ConnectionManager Core { get; private set; } = null!;
    public static bool IsSystemShuttingDown { get; private set; }

    /// <summary>
    /// 是否以「登录前」模式运行（<c>--headless</c>）。此时没有交互式桌面，
    /// 不能建窗口；截图/弹窗这类需要桌面的功能要给出明确原因而不是静默失败。
    /// </summary>
    public static bool IsHeadless { get; private set; }

    private WinForms.NotifyIcon? _tray;
    private WebHostWindow? _host;
    private string? _pendingReplyClientId;

    /// <summary>
    /// 消息生命周期与 ACK 决策（docs/CORE-REFACTOR-PLAN.md §6 Phase 2）。
    ///
    /// ⚠ Phase 2 起**宿主不再订阅 <c>Core.MessageReceived</c>**：消息先经过它
    /// （落盘 → 发事件），宿主订阅的是它的 <c>MessageReceived</c>。两边都订阅
    /// 会让同一条消息被处理两遍。
    /// </summary>
    private MessageManager? _messaging;

    /// <summary>
    /// WebView2 不可用时的原生通知回落（<see cref="FallbackNotifier"/>，§6 Phase 2-5 / §8.11）。
    ///
    /// Phase 3：字段类型是 Core 的 <see cref="INotificationSink"/>（「通知」正式登记为
    /// 一项平台能力的数据形状），实现仍是 Windows 的 <see cref="FallbackNotifier"/>。
    /// </summary>
    private INotificationSink? _fallback;

    /// <summary>命令派发（Phase 3）：截图 / 关机 / 解锁的应答统一由 Core 发出。</summary>
    private CommandRouter? _router;

    /// <summary>
    /// 共享昵称在本机的**全部逻辑**（docs/NICKNAME-SYSTEM-PLAN.md §7 Phase 4）。
    ///
    /// 三条口径（§5.2 / r6）：
    /// <list type="bullet">
    ///   <item>本机「当前用谁的名义」是**纯本地状态**（落 <c>nickname.json</c>），
    ///     整表由服务端经设备连接推来 —— PC 不「拥有」任何昵称。</item>
    ///   <item>首次运行**不注册任何东西**（只发 <c>nickname_list_request</c> 拉整表），
    ///     界面显示灰临时昵称（本机名 + 逻辑色 <c>gray</c>），NAS 一行都不多。</item>
    ///   <item>四个管理操作（新建 / 改名 / 删除 / 重新分配颜色）**必须在线**：
    ///     离线时直接拒绝并说明原因，不排队、不落 pending、重连不补发。</item>
    /// </list>
    /// </summary>
    private NicknameService? _nicknames;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ★ 第一件事就是装崩溃兜底。
        // 没有日志的闪退根本没法查 —— 之前那次「回复发不出去」就是因为所有异常
        // 都被静默吞掉，查了很久。这里把三类未处理异常全部落盘。
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AgentLog.Write("!! 未处理异常(AppDomain): " + args.ExceptionObject);
        DispatcherUnhandledException += (_, args) =>
        {
            AgentLog.Write("!! 未处理异常(UI): " + args.Exception);
            args.Handled = true;          // UI 线程的异常别直接把进程杀掉
            // 但也**不能静默**：之前异常被吞掉，用户看到的现象是「点了没反应」，
            // 只能靠翻日志文件才能知道原因。所以第一次出错直接把原因弹出来。
            ReportOnce("界面出错", args.Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AgentLog.Write("!! 未观察的任务异常: " + args.Exception);
            args.SetObserved();
        };
        AgentLog.Write($"=== 进程启动 pid={Environment.ProcessId} exe={Environment.ProcessPath} ===");

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        AgentLog.Rotate();

        // --config：SYSTEM 身份的「开机」计划任务跑在会话 0，%APPDATA% 不是
        // 用户那个，必须由任务参数显式指定配置路径，否则会注册成另一个设备。
        var cfgPath = ArgValue(e.Args, "--config");
        if (!string.IsNullOrWhiteSpace(cfgPath))
            AgentConfig.UseConfigPath(cfgPath);

        // --headless：开机后、还没人登录时由计划任务拉起。会话 0 没有桌面，
        // 不能建窗口也不能建托盘图标，所以只维持连接。
        IsHeadless = HasArg(e.Args, "--headless");

        // ── 单实例：第二个实例不再静默退出 ──────────────────────────────
        // ★ 这里原来是 `new Mutex(true, @"Global\FamilyAgent.SingleInstance", ...)`，
        //   两个毛病：
        //   ① 创建/打开 Global\ 命名对象要 SeCreateGlobalPrivilege，普通用户账号没有，
        //      日志里出现过 `UnauthorizedAccessException: Access to the path
        //      'Global\FamilyAgent.SingleInstance' is denied` —— 直接崩在启动；
        //   ② 判到「已有实例」就悄无声息地退出。用户机器上放着好几份 exe 时，
        //      双击新版不但没反应，屏幕上还是旧界面 → 用户以为压根没重新编译。
        //   现在改成文件式（SingleInstance.cs，跟 Presence.cs 同一套思路）：
        //   第二个实例请正在跑的那个把窗口拉到前面，版本不一致时由它弹托盘气泡说清楚。
        //
        // ★ headless（登录前）实例**不参与**单实例占用：它跟登录后的交互式实例
        //   本来就要共存（谁连接由 Presence 交接）。要是 headless 也占锁，开机后
        //   第一个起来的它就会把界面永远挡在门外 —— 那是比本 bug 更糟的事。
        //
        // ⚠ 必须在 --config / --headless 解析**之后**：仲裁文件放在配置文件旁边，
        //   路径由 --config 决定（SYSTEM 身份的登录前实例读的是用户那份配置）。
        if (!IsHeadless)
        {
            var running = SingleInstance.Running();
            if (running is not null)
            {
                AgentLog.Write($"已有实例在运行（pid={running.Pid} {running.Version}）"
                             + "→ 请求它把窗口拉到前面");

                // ★ 版本不一样 = 屏幕上那个**不是**你刚双击的这份 exe。
                //   只写请求还不够：正在跑的那份如果还是旧版（没有 show.request
                //   轮询这段代码），它根本不会理这个请求，用户看到的仍是「双击没反应」。
                //   所以由本进程在退出前当面说清楚，不用等对方配合。
                var mismatch = !string.IsNullOrWhiteSpace(running.Version)
                               && !string.Equals(running.Version,
                                                 ProtocolVersion.AgentVersion,
                                                 StringComparison.Ordinal);

                SingleInstance.RequestShow(ProtocolVersion.AgentVersion);

                if (mismatch)
                    WarnVersionMismatch(running);

                Shutdown(); return;
            }
            SingleInstance.Claim();
        }

        // ★ 退出清理的订阅**放在单实例判定之后**：第二个实例在上面就 return 了，
        //   不能让它跑一遍 Cleanup —— 那会连累正在跑的那个（Cleanup 里会清掉
        //   交互式实例的心跳，headless 实例会误判「人走了」又抢回连接）。
        SessionEnding += (_, _) => IsSystemShuttingDown = true;
        Exit += (_, _) => Cleanup();

        Config = AgentConfig.Load();

        // ── 启用 WPF 内置 Fluent 主题 ─────────────────────────────
        // 界面现在全在 WebView2 里由网页渲染（真正的明暗由页面的 prefers-color-scheme
        // 决定），这里保留只为「缺 WebView2 Runtime 时的本地提示页」不至于刺眼。
        //
        // ⚠️ 这段必须在 AgentConfig.Load() **之后**：之前写在前面，
        //    读到的是还没加载的默认 Config，用户的明暗偏好根本没生效。
        try
        {
            Application.Current.ThemeMode = ToFluent(Config.ThemeMode);
            AgentLog.Write($"Fluent 主题已启用：pref={Config.ThemeMode} "
                           + $"ThemeMode={Application.Current.ThemeMode}");
        }
        catch (Exception ex)
        {
            AgentLog.Write("启用 Fluent 主题失败（继续用默认主题）：" + ex.Message);
        }

        AgentLog.Write($"=== FamilyAgent 启动 device={Config.DeviceId} server={Config.ServerUrl} "
                       + $"theme={Config.ThemeMode} agent={ProtocolVersion.AgentVersion} ===");
        // ── 平台能力（Phase 3，见 docs/CORE-REFACTOR-PLAN.md §6 Phase 3）──────
        // 把 Windows 的三个实现注册进 Core：**能力表就是从这里推导的** ——
        // 没注册的能力（例如未来 Android 骨架的截图/电源）不会出现在 capabilities 里，
        // 服务端也就不会给这台设备显示那个按钮（需求 §8；服务端不校验取值，见 §8.6）。
        //
        // 三个实现都是无状态转发，就地建一次即可：
        //  · 截图：包住 ScreenCapture（本地页面路径仍直接用那个低层原语）；
        //  · 电源：包住宿主的 ExecuteShutdown（托盘气泡 / event 帧 / 页面回执都在那段里，
        //    页面请求关机也走它）——注入的委托只负责切 UI 线程，与原时序一致；
        //  · 解锁：只是调用点，校验逻辑仍在 UnlockGuard（§8.12 归属待拍板）。
        var capabilities = new PlatformCapabilities(
            screenshot: new WindowsScreenshotProvider(),
            power: new WindowsPowerProvider(delay => Dispatcher.Invoke(() => ExecuteShutdown(delay))),
            unlock: new WindowsUnlockGuard());

        // UI 静态依赖（App.IsHeadless / SessionState.Current）在 Phase 1 收进 IPlatformInfo，
        // Windows 侧实现只做包装（Platform/WindowsPlatformInfo.cs），行为不变（§Phase 1-3）
        Core = new ConnectionManager(Config, new WindowsPlatformInfo(), capabilities);
        Core.ConnectionStateChanged += OnConnectionChanged;
        Core.ReplyAcked += OnReplyAcked;
        Core.HistoryReceived += OnHistoryReceived;
        AgentLog.Write($"[CAP] 本机能力：{Core.Capabilities.Describe()}"
                     + "（连接串 windows_state/capabilities + 心跳帧同源上报）");

        // ── 命令派发（Phase 3）──────────────────────────────────────────
        // 截图 / 关机 / 解锁三种命令的**应答**从此由 Core 负责（CommandRouter）：
        // 平台没有该能力时也要回一条明确的 error，而不是让网页端干等到超时。
        // ⚠ 宿主**不要**再单独订阅 Core 的 ScreenshotRequested / ShutdownRequested /
        //   UnlockRequested（会处理两遍、发两条应答）。
        _router = new CommandRouter(Core, capabilities, Config.DeviceId);
        _router.Attach(Core);
        _router.ScreenshotHandled += OnScreenshotHandled;

        // ── 消息生命周期与 ACK 决策（Phase 2）──────────────────────────
        // 收到 → 归一 message_id → **本地落盘** → 发事件 → 交界面显示 → 等界面回报事实
        // → 由 MessageManager 决定发哪个 ack。整个过程不再依赖 UI 是否存在：
        // 界面挂了消息也不丢，ack 也有终态（见 Core/Messaging/MessageManager.cs）。
        //
        // ⚠ 注意这里**没有** `Core.MessageReceived += ...`：那条事件已经由 Attach 接进来，
        //   宿主再订一次会把同一条消息处理两遍。
        _messaging = new MessageManager(new JsonlMessageStore(), (id, status) => Core.Ack(id, status));
        _messaging.Attach(Core);
        _messaging.MessageReceived += OnMessageReceived;
        _messaging.DeliveryStateChanged += OnDeliveryStateChanged;
        _messaging.DisplayFallbackRequired += OnDisplayFallbackRequired;
        _fallback = new FallbackNotifier(() => _tray, action => Dispatcher.Invoke(action));
        AgentLog.Write($"[MSG] 消息生命周期已搬进 Core：本地消息库={_messaging.Store.Describe()}");

        // ── 共享昵称（docs/NICKNAME-SYSTEM-PLAN.md §7 Phase 4）──────────────
        // 走**同一条设备连接**（§5.4：不新增 HTTP 依赖），但昵称帧走**直发**：
        // 不进 Outbox、不落盘、不补发 —— 否则一次「断网时被拒的改名」会在重连后
        // 被 Outbox 静默补发，违反 r6「完全拒绝离线改昵称」。
        //
        // 本机「当前用谁的名义」落 %APPDATA%\FamilyAgent\nickname.json（与 config.json 同目录，
        // 独立文件 —— 见 NicknameStore 的说明：塞进配置会被「配置损坏回默认」那条路径清掉）。
        // 默认名取 DeviceName（Normalize 后 = ComputerName）；灰临时昵称就用它。
        _nicknames = new NicknameService(
            new ConnectionNicknameTransport(Core),
            new JsonNicknameStore(),
            Config.DeviceName);
        Core.NicknameFrameReceived += OnNicknameFrameReceived;
        _nicknames.Changed += OnNicknamesChanged;
        AgentLog.Write($"[NICK] 共享昵称已接入：本机默认名={Config.DeviceName} "
                     + $"缓存文件={JsonNicknameStore.DefaultPath}（首次运行不注册，只拉整表）");

        // ── Windows 会话状态上报（远程解锁 Phase 1）──────────────────────
        // 在这里（Core 已建好、连接还没开始）启动最合适：第一次心跳就能带
        // 上正确的状态。先 Start() 再订阅 —— Start() 里的首次检测会在默认值
        // 变化时抛通知，让它落在没有订阅者的空窗里，省一次无意义的上报。
        SessionState.Start();
        SessionState.Changed += OnSessionStateChanged;

        // 启动时不提权：SYSTEM 计划任务若已注册过就直接跳过，
        // 否则每次开机都会弹一次 UAC（用户最烦这个）。
        AutoStart.Apply(Config.AutoStart, allowElevation: false);
        Core.Start();

        if (IsHeadless)
        {
            // 登录前模式：当前没有交互式桌面，任何窗口都显示不出来。
            // 这里只做「让设备在线」，等有人登录后由交互式实例接手。
            AgentLog.Write("以 headless 模式运行（登录前）：不显示界面，只维持设备在线；"
                         + "有人登录后自动让位给交互式实例");
            Presence.StartSupervisor(Core);
            return;
        }

        // 交互式实例：持续写心跳，让 headless 实例知道有人在用桌面、该让位了
        Presence.StartHeartbeat();

        // 系统主题变化不再需要在宿主里做任何事：界面在网页里，
        // 页面自己监听 prefers-color-scheme；本地提示页由 WPF 的 ThemeMode.System 跟。

        SetupTray();

        // 第二个实例的「把窗口拉到前面」请求（文件式，每 1 秒看一眼）——
        // 只有交互式实例才有窗口，headless 那条路径上面已经 return 了
        StartShowRequestWatcher();

        // --tray：开机自启时静默进托盘；手动启动则直接打开界面
        var silent = HasArg(e.Args, "--tray");
        if (!Config.IsConfigured)
            ShowSettings();          // 还没配置过 → 开窗口进设置（未配置时宿主显示的是兜底页）
        else if (!silent)
            ShowConversation();      // 打开就是消息界面，设置在右上角
    }

    /// <summary>
    /// 把本机的明暗偏好转成 WPF 的 ThemeMode（只影响缺 WebView2 Runtime 时的本地提示页；
    /// 页面主题由页面自己决定）。
    /// </summary>
    internal static ThemeMode ToFluent(string? pref) => (pref ?? "system").Trim().ToLowerInvariant() switch
    {
        "light" => ThemeMode.Light,
        "dark" => ThemeMode.Dark,
        _ => ThemeMode.System,
    };

    /// <summary>
    /// 第二个实例：发现「正在跑的是另一个版本」时的当面提示（模态，用户一定能看到）。
    ///
    /// 为什么不只靠对方弹托盘气泡：对方完全可能是**旧版**，压根没有 show.request
    /// 这段逻辑 —— 只写请求等于继续让用户看到「双击没反应」，那正是要根治的毛病。
    /// 所以本进程自己弹一个，不看对方脸色。
    /// </summary>
    private static void WarnVersionMismatch(SingleInstance.InstanceInfo running)
    {
        try
        {
            WinForms.MessageBox.Show(
                "家庭消息已经在运行，但并不是你刚启动的这个版本：\n\n"
                + $"　正在运行：{running.Version}（进程 {running.Pid}）\n"
                + $"　你刚启动：{ProtocolVersion.AgentVersion}\n\n"
                + "已经请它把窗口拉到前面。如果屏幕上出现的还是老界面，\n"
                + "说明占着位置的是旧版：请在托盘图标上右键 →「退出」，\n"
                + "然后重新运行你刚双击的那个 exe。",
                "家庭消息 · 版本不一致",
                WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            AgentLog.Write("提示两版并存失败（不影响退出）：" + ex.Message);
        }
    }

    /// <summary>取 <c>--key value</c> 形式的参数值；没有就返回 null。</summary>
    private static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static bool HasArg(string[] args, string name)
    {
        foreach (var a in args)
        {
            if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ---------------- 壳窗口（全局复用同一个窗口）----------------

    private static bool _reportedOnce;

    /// <summary>把异常摆到用户面前（只弹第一次，避免连环弹窗刷屏）。</summary>
    private static void ReportOnce(string what, Exception? ex)
    {
        if (_reportedOnce) return;
        _reportedOnce = true;
        try
        {
            WinForms.MessageBox.Show(
                what + "：\n\n" + (ex?.Message ?? "未知错误") +
                "\n\n完整堆栈已写入日志：\n" + AgentLog.FilePath,
                "家庭消息", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Error);
        }
        catch { /* 连弹框都失败就只留日志 */ }
    }

    /// <summary>
    /// 取（必要时创建）唯一的界面窗口。
    ///
    /// 创建失败时**不能只是静默**：之前窗口建不起来，用户看到的现象是
    /// 「点菜单没反应」，而异常被全局兜底吞掉，连日志都要翻文件才知道 ——
    /// 所以这里既写日志也弹框，把原因直接摆到用户面前。
    /// </summary>
    private WebHostWindow? EnsureHost()
    {
        if (_host is not null)
            return _host;

        try
        {
            _host = CreateHost();
            return _host;
        }
        catch (Exception ex)
        {
            AgentLog.Write("!! 创建 WebView2 壳窗口失败：" + ex);
            ReportOnce("打开界面失败", ex);
            return null;
        }
    }

    private WebHostWindow CreateHost()
    {
        var host = new WebHostWindow();

        // 标题带上设备名和版本号（常驻可见）：用户机器上可能同时放着好几份 exe，
        // 「屏幕上跑的是哪一版」以前只能靠猜 —— 那正是「你根本没编译」误会的土壤。
        host.Title = WindowTitle();

        // 页面回报「这条消息真的画到屏幕上了」→ 交给 Core 决定并发 ack。
        // （原来这里是宿主直接 `Core.Ack(id, "popup_displayed")`；Phase 2 起 ACK 决策
        //   在 MessageManager 里，宿主只回报 UI 事实，§6 Phase 2-2。）
        host.MessageAcked += id =>
        {
            try { _messaging?.NotifyDisplayed(id); }
            catch (Exception ex) { AgentLog.Write("回报 popup_displayed 失败：" + ex.Message); }
        };

        // 关窗 / 自动关闭 / Alt+F4（含页面点「知道了」）→ 这些未读按「已读」回报
        host.Dismissed += id =>
        {
            try { _messaging?.NotifyDismissed(id); }
            catch (Exception ex) { AgentLog.Write("回报已读失败：" + ex.Message); }
        };

        // 消息已经投给页面（页面正在渲染它）→ 告诉 Core，别再为它触发原生通知兜底；
        // 终态仍由页面 web.ack 或关窗给出。
        host.MessageHandedToPage += id =>
        {
            try { _messaging?.NotifyDisplayDeferred(id, "已投给页面"); }
            catch (Exception ex) { AgentLog.Write("回报「已投给页面」失败：" + ex.Message); }
        };

        // 本地正式界面加载失败（shell\ 被改名 → 404 那类）：立刻用原生通知把库里
        // 「还没显示过」的消息提醒掉，不等 20 秒的超时兜底。
        host.LocalPageFailed += OnLocalPageFailed;

        // 页面就绪时补齐「还没显示过」的消息 —— 来源是 Core 的 MessageStore，
        // 所以进程重启前遗留的也在其中（§6 Phase 2-3 / 测试 C）。
        host.ReplaySource = TakePendingForReplay;

        // ── 页面 → 宿主：每一条都落到原来那套逻辑上，语义不变 ──
        host.Bridge.ReplyReceived += OnReplyRequested;
        host.Bridge.ScreenshotRequested += OnLocalScreenshotRequested;
        host.Bridge.ActionRequested += OnLocalActionRequested;
        host.Bridge.ServerChanged += OnServerSettingsChanged;
        host.Bridge.ConfigSaveRequested += OnSaveConfigRequested;
        host.Bridge.SettingsRequested += ShowSettings;
        host.Bridge.QuitRequested += ExitApp;

        // 共享昵称（§7 Phase 4）：
        //  · 页面 → 宿主：六个 web.nickname_* 由桥归一成一次本事件
        //  · 宿主 → 页面：页面就绪 / 进设置视图时按需推整份状态（NicknameSource）
        host.Bridge.NicknameRequested += OnNicknameRequested;
        host.NicknameSource = () => _nicknames;

        return host;
    }

    /// <summary>
    /// 窗口标题：<c>家庭消息 · 设备名（版本号）</c>。
    ///
    /// 版本号常驻写在这里是有原因的：用户机器上可能同时放着好几份 exe
    /// （旧的在桌面、新的在别的目录），而「屏幕上跑的是哪一版」以前完全看不出来。
    /// 版本号一律取 <see cref="ProtocolVersion.AgentVersion"/>，**不另写死字符串**。
    /// </summary>
    private static string WindowTitle()
    {
        var version = ProtocolVersion.AgentVersion;
        var device = Config?.DeviceName;
        return string.IsNullOrWhiteSpace(device)
            ? $"家庭消息（{version}）"
            : $"家庭消息 · {device}（{version}）";
    }

    // ---------------- 第二个实例的请求：把窗口拉到前面 ----------------

    /// <summary>
    /// 每 1 秒看一眼 show.request（第二个实例写的），有请求就把窗口拉到前面。
    ///
    /// 为什么要在这边轮询：新实例只能写文件，拉窗口必须由**真正拥有窗口**的进程做；
    /// 而且两边版本可能不一样，得由这边把「你双击的是新版，但屏幕上这个是旧版」
    /// 当面说清楚（托盘气泡），不能让用户继续以为「新版没生效」。
    /// </summary>
    private void StartShowRequestWatcher()
    {
        _ = Task.Run(async () =>
        {
            var ticks = 0;
            while (!IsSystemShuttingDown)
            {
                try
                {
                    var remote = SingleInstance.TakeShowRequest();
                    if (remote is not null)
                    {
                        var mine = ProtocolVersion.AgentVersion;
                        // 版本读不出来（空）就不当成「不一致」——不能因此虚报一个气泡
                        var mismatch = !string.IsNullOrWhiteSpace(remote)
                                       && !string.Equals(remote, mine, StringComparison.Ordinal);
                        AgentLog.Write($"收到「把窗口拉到前面」请求"
                                     + $"（请求方 {remote}，本实例 {mine}）");
                        if (mismatch)
                            AgentLog.Write("!! 两个实例版本不一致：屏幕上正在跑的这个不是最新那份 exe");
                        Dispatcher.Invoke(() => BringWindowToFront(mismatch ? remote : null));
                    }
                }
                catch (Exception ex)
                {
                    AgentLog.Write("show.request 轮询异常（继续）：" + ex.Message);
                }

                // ★ Phase 2：每 5 秒扫一次「消息落盘后迟迟没被任何界面处理」的情况
                //   （WebView2「没报错但页面永远不就绪」）→ 触发原生通知兜底。
                //   挂在这条已有的轮询上，不另起定时器；headless 实例在这之前就 return 了
                //   （没有界面可显示，那条路径由「无法显示」终态覆盖）。
                if (++ticks >= 5)
                {
                    ticks = 0;
                    try { _messaging?.SweepDisplayTimeouts(DateTime.UtcNow); }
                    catch (Exception ex) { AgentLog.Write("兜底提醒扫描异常（继续）：" + ex.Message); }
                }

                try { await Task.Delay(TimeSpan.FromSeconds(1)); }
                catch { return; }
            }
        });
    }

    /// <summary>
    /// 把（必要时先建出来的）窗口显示成消息界面并拉到前面。
    /// <paramref name="otherVersion"/> 非空 = 请求方版本与本实例不一致 → 额外弹托盘气泡。
    /// </summary>
    private void BringWindowToFront(string? otherVersion)
    {
        var host = EnsureHost();
        if (host is null) return;

        try
        {
            host.ShowClient();                    // 客户端形态（消息界面）
            if (host.WindowState == WindowState.Minimized)
                host.WindowState = WindowState.Normal;
            host.Activate();
            AgentLog.Write($"窗口已拉到前面 visible={host.IsVisible} state={host.WindowState}");

            if (otherVersion is not null)
            {
                try
                {
                    _tray?.ShowBalloonTip(8000, "家庭消息",
                        $"检测到新版 {otherVersion} 已启动，但正在运行的是 "
                        + $"{ProtocolVersion.AgentVersion}。\n请退出后重新运行新版 exe。",
                        WinForms.ToolTipIcon.Warning);
                }
                catch
                {
                    // 气泡提示失败不影响拉窗口
                }
            }
        }
        catch (Exception ex)
        {
            AgentLog.Write("!! 把窗口拉到前面失败：" + ex);
        }
    }

    /// <summary>
    /// 页面在设置视图里点了「保存」（<c>web.save_config</c>）—— 本机设置的唯一写入点。
    ///
    /// 规矩（docs/PC-LOCAL-UI.md）：
    ///   · **只处理传了的字段**：没传 = 不改（不是改成空）
    ///   · 落盘走 <see cref="AgentConfig.Save"/>
    ///   · 改了服务端地址 / 口令 → 重连（<c>Core.Restart</c>）
    ///   · 改了开机自启 → <c>AutoStart.Apply</c>（可能要弹一次 UAC），
    ///     **失败要如实写进 detail**，不许回一个漂亮的 ok
    ///   · 最后一定回 <c>host.config_saved</c>，页面等着它给提示
    /// </summary>
    private void OnSaveConfigRequested(ConfigPatch patch)
    {
        if (patch is null || patch.IsEmpty)
        {
            _host?.Bridge.PostConfigSaved(false, "没有要保存的设置项");
            return;
        }

        var notes = new List<string>();
        var ok = true;
        var reconnect = false;

        // ① 服务端地址（http/ws 都收，落盘前统一去掉结尾斜杠）
        if (patch.ServerUrl is not null)
        {
            var clean = patch.ServerUrl.Trim().TrimEnd('/');
            if (clean.Length == 0)
            {
                ok = false;
                notes.Add("服务端地址不能为空");
            }
            else if (!string.Equals(clean, Config.ServerUrl, StringComparison.OrdinalIgnoreCase))
            {
                Config.ServerUrl = clean;
                reconnect = true;
                notes.Add("服务端地址已保存，正在重连");
            }
            else
            {
                notes.Add("服务端地址没变");
            }
        }

        // ② 注册口令：界面留空 = 没填，不当成"清空口令"
        if (patch.EnrollToken is not null)
        {
            var token = patch.EnrollToken.Trim();
            if (token.Length == 0)
            {
                notes.Add("注册口令留空，未修改");
            }
            else if (!string.Equals(token, Config.EnrollToken, StringComparison.Ordinal))
            {
                Config.EnrollToken = token;
                reconnect = true;      // 口令变了要重新注册
                notes.Add("注册口令已保存");
            }
            else
            {
                notes.Add("注册口令没变");
            }
        }

        // ③ 回复昵称：页面是自由输入，不在本地列表里就加进去
        if (patch.ReplyName is not null)
        {
            var name = patch.ReplyName.Trim();
            if (name.Length == 0)
            {
                notes.Add("回复昵称留空，未修改");
            }
            else
            {
                var names = Config.ReplyNames ?? new List<string>();
                if (!names.Contains(name))
                    names.Add(name);
                Config.ReplyNames = names;
                Config.ReplyName = name;
                notes.Add($"回复昵称已保存：{name}");
            }
        }

        // ④ 明暗模式：只认 system / light / dark，别的直接拒绝（别把配置写脏）
        if (patch.ThemeMode is not null)
        {
            var theme = patch.ThemeMode.Trim().ToLowerInvariant();
            if (theme != "system" && theme != "light" && theme != "dark")
            {
                ok = false;
                notes.Add($"明暗模式不认识：{patch.ThemeMode}");
            }
            else
            {
                Config.ThemeMode = theme;
                notes.Add($"主题已保存：{theme}");
            }
        }

        // ⑤ 开机自启：真去注册/删除，回报**实际**落地情况（可能弹一次 UAC）
        if (patch.AutoStart.HasValue)
        {
            var wantAuto = patch.AutoStart.Value;
            Config.AutoStart = wantAuto;
            AutoStart.Status actual;
            try
            {
                actual = AutoStart.Apply(wantAuto, allowElevation: wantAuto);
            }
            catch (Exception ex)
            {
                AgentLog.Write("!! 应用开机自启失败：" + ex.Message);
                actual = AutoStart.Query();
                ok = false;
                notes.Add("开机自启设置失败：" + ex.Message);
            }

            if (wantAuto && !actual.Any)
            {
                ok = false;
                notes.Add("开机自启没能注册（建 SYSTEM 计划任务需要管理员权限，" +
                          "提权也没成功）；登录时仍会启动，但「只开机未登录」覆盖不到");
            }
            else if (!wantAuto && actual.Any)
            {
                ok = false;
                notes.Add($"自启项没删干净（当前：{actual.Describe()}）");
            }
            else
            {
                notes.Add(wantAuto ? $"开机自启已启用：{actual.Describe()}" : "开机自启已关闭");
            }
        }

        Config.Normalize();

        try
        {
            Config.Save();
        }
        catch (Exception ex)
        {
            AgentLog.Write("!! 保存配置失败：" + ex);
            _host?.Bridge.PostConfigSaved(false, "写入配置文件失败：" + ex.Message);
            return;
        }

        // 主题同步给 WPF 一侧（缺 WebView2 Runtime 时的本地提示页按它上色；
        // 页面自己的明暗由页面跟系统走，不靠这里）
        try
        {
            var app = Application.Current;
            if (app is not null)
                app.ThemeMode = ToFluent(Config.ThemeMode);
        }
        catch (Exception ex)
        {
            AgentLog.Write("应用主题失败（保存本身已成功）：" + ex.Message);
        }

        if (reconnect)
            Core.Restart();

        AgentLog.Write($"设置已保存：server={Config.ServerUrl} name={Config.ReplyName} "
                     + $"theme={Config.ThemeMode} autostart={Config.AutoStart} 重连={reconnect}");
        _host?.Bridge.PostConfigSaved(ok, string.Join("；", notes));
        _host?.ReloadAfterServerChange();   // 配置变了 → 界面阶段跟着纠正（不导航则只刷新信息）
    }

    /// <summary>
    /// 页面发来的回复。client_id 由页面给（页面要拿它对应哪条气泡在「发送中」），
    /// 页面没给就现生成一个。
    /// </summary>
    private void OnReplyRequested(string senderName, string content, string clientId)
    {
        content = (content ?? "").Trim();
        if (content.Length == 0)
        {
            _host?.Bridge.PostReplyAck(clientId, "empty", 0, "内容为空");
            return;
        }

        var who = (senderName ?? "").Trim();
        if (who.Length == 0)
            who = Config.ReplyName;         // 页面没给昵称就用本机配置里选的那个

        if (string.IsNullOrWhiteSpace(clientId))
            clientId = Guid.NewGuid().ToString("N")[..12];
        _pendingReplyClientId = clientId;

        // 不再预判连接状态：直接尝试发送，发不出去会自动入队，重连后补发。
        // （之前 `if (!Connected) 报失败` 会把能发的回复也拦下来。）
        var dispatched = Core.Reply(who, content, clientId);
        if (!dispatched)
        {
            _host?.Bridge.PostReplyAck(clientId, "queued", 0,
                "暂时没连上服务器，已排队，恢复后自动发送");
            return;
        }

        _ = Task.Run(async () =>
        {
            // 服务端迟迟不回执时给个明确提示，别让界面永远停在「发送中…」
            await Task.Delay(TimeSpan.FromSeconds(12));
            if (_pendingReplyClientId == clientId)
            {
                AgentLog.Write($"✗ 回复 12 秒内未收到 reply_ack（连接状态：{Core.DescribeConnection()}）");
                Dispatcher.Invoke(() => _host?.Bridge.PostReplyAck(clientId, "queued", 0,
                    "服务器 12 秒内没有回执，已排队，恢复后自动发送"));
            }
        });
    }

    /// <summary>
    /// 页面自己点了「查看桌面」：本地和「服务端请求截图」走同一套截图能力。
    /// headless（会话 0）下没有桌面，直接回中文原因，不让用户拿到一张黑图。
    /// </summary>
    private void OnLocalScreenshotRequested(string requestId)
    {
        if (IsHeadless)
        {
            AgentLog.Write("headless：尚无人登录，无法截图（页面请求）");
            // 原因文案与「服务端请求截图」那条路同源（WindowsScreenshotProvider.HeadlessReason），
            // 免得同一个意思在两处各写一份、哪天改一处漏一处。
            _host?.PushScreenshot(requestId, null, WindowsScreenshotProvider.HeadlessReason);
            return;
        }

        _ = Task.Run(() =>
        {
            var (base64, _, _) = ScreenCapture.CaptureJpeg();
            var error = ScreenCapture.LastError;
            Dispatcher.Invoke(() => _host?.PushScreenshot(requestId, base64, error));
        });
    }

    /// <summary>页面要求执行动作（关机 / 解锁）。</summary>
    private void OnLocalActionRequested(string action, string deviceId)
    {
        // 宿主自己再校验一次归属：带 device_id 的动作必须是指向本机的
        // （页面上可能有别的设备的卡片；协议约定「宿主收到后自己校验」）
        if (!string.IsNullOrWhiteSpace(deviceId)
            && !string.Equals(deviceId.Trim(), Config.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            AgentLog.Write($"✗ 页面要求的动作 {action} 指向 {deviceId}，本机是 {Config.DeviceId}，拒绝");
            _host?.Bridge.PostActionResult(action, false, "这条动作不是发给本机的（device_id 不匹配）");
            return;
        }

        if (string.Equals(action, "shutdown", StringComparison.OrdinalIgnoreCase))
        {
            var (ok, detail) = ExecuteShutdown(PowerControl.DefaultDelaySeconds);
            _host?.Bridge.PostActionResult("shutdown", ok, detail);
            return;
        }

        if (string.Equals(action, "unlock", StringComparison.OrdinalIgnoreCase))
        {
            // Phase 1 的解锁必须由服务端发起：一次性令牌、时效、request_id 全在服务端，
            // 页面直接点「解锁」在这里给不出合法请求。如实回绝，不假装成功。
            _host?.Bridge.PostActionResult("unlock", false,
                "远程解锁由服务端发起：请在网页端调用解锁接口，PC 端只接受服务端下发的 unlock_request");
            return;
        }

        AgentLog.Write($"✗ 页面要求了不支持的动作：{action}");
        _host?.Bridge.PostActionResult(action, false, "不支持的动作：" + action);
    }

    /// <summary>兜底页保存服务端配置：落盘 + 应用自启 + 重连（语义同旧的「保存并连接」）。</summary>
    private void OnServerSettingsChanged(string url, string enrollToken)
    {
        var clean = (url ?? "").Trim().TrimEnd('/');
        if (clean.Length == 0)
        {
            _host?.Bridge.PostActionResult("set_server", false, "服务端地址不能为空");
            _host?.Bridge.PostConfigSaved(false, "服务端地址不能为空");
            return;
        }

        Config.ServerUrl = clean;
        if (!string.IsNullOrWhiteSpace(enrollToken))
            Config.EnrollToken = enrollToken.Trim();
        Config.Normalize();

        SaveAndReconnect();
        AgentLog.Write($"兜底页保存配置：server={Config.ServerUrl} device={Config.DeviceId}");
        // 两条回执都发：host.action_result 是兜底页一直在用的老帧，
        // host.config_saved 是新增的统一回执（docs/PC-LOCAL-UI.md）——「只加不删」。
        _host?.Bridge.PostActionResult("set_server", true, "已保存，正在连接…");
        _host?.Bridge.PostConfigSaved(true, "服务端地址已保存，正在重连");
        _host?.ReloadAfterServerChange();
    }

    /// <summary>
    /// 配置落盘 + 应用自启 + 重连。
    ///
    /// ⚠ 这里是「用户在界面上主动改设置」那条路，<c>allowElevation: true</c> ——
    /// 允许弹一次 UAC 去注册 SYSTEM 计划任务（「开机后未登录也能连上」靠的就是它）。
    /// </summary>
    private void SaveAndReconnect()
    {
        Config.Save();
        AutoStart.Apply(Config.AutoStart, allowElevation: true);
        Core.Restart();
    }

    // ---------------- 服务端事件 ----------------

    /// <summary>
    /// 连接状态变化。Phase 1 后订阅的是 Core 的强类型事件
    /// （<see cref="ConnectionStateChangedArgs"/>，取代原来的 <c>ConnectionChanged(bool, string)</c>）：
    /// 回调体逐字保留，只把两个入参从事件参数里取出来（<c>Connected</c> / <c>Detail</c>）。
    /// </summary>
    private void OnConnectionChanged(ConnectionStateChangedArgs e)
    {
        var connected = e.Connected;
        var message = e.Detail;

        AgentLog.Write(connected ? "== 已连接 ==" : $"== 断开：{message} ==");

        // 刚连上就上报一次会话状态：服务端要靠它判断远程解锁是否可用，
        // 等第一个 15 秒周期心跳太慢（等效于「hello 阶段就带上状态」）。
        if (connected)
        {
            try { Core.ReportSessionState(); }
            catch (Exception ex) { AgentLog.Write("上线时上报会话状态失败：" + ex.Message); }

            // 共享昵称（§7 Phase 4）：重连后**只做一件事** —— 发 nickname_list_request 拉整表校正。
            // 没有待补发的改名、没有 synced/pending 状态机（r6 把整套离线 pending 昵称同步删了）。
            // 整表回来后 NicknameService 会自己校正「本机选用的那条还在不在」（不在 → 回退灰临时 + 提示）。
            try { _nicknames?.OnConnected(); }
            catch (Exception ex) { AgentLog.Write("[NICK] 上线后拉昵称整表失败：" + ex.Message); }

            // Phase 2：把「已决策但没交给传输层」的 ACK 补发一遍
            // （交给传输层时抛过异常的那种；断线期间入队的那部分由 Outbox 自己补发）
            try
            {
                var resent = _messaging?.FlushPendingAcks() ?? 0;
                if (resent > 0)
                    AgentLog.Write($"[MSG] 上线后补发 {resent} 条待发 ACK");
            }
            catch (Exception ex) { AgentLog.Write("补发待发 ACK 失败（下次上线再试）：" + ex.Message); }
        }

        Dispatcher.Invoke(() =>
        {
            _host?.SetConnection(connected, connected ? "已连接" : "未连接");
            if (_tray is not null)
                _tray.Text = connected ? "家庭消息 Agent · 已连接" : "家庭消息 Agent · 未连接";
        });
    }

    // ---------------- 共享昵称（docs/NICKNAME-SYSTEM-PLAN.md §7 Phase 4）----------------

    /// <summary>
    /// Core 收到一帧昵称相关的下行帧（整表应答 / 四个增量广播 / <c>nickname_error</c>）。
    ///
    /// 昵称在客户端**只有一份消费者**（<see cref="NicknameService"/>），所以这里转发一次：
    /// 服务会更新整表 / 本机选用，并抛 <c>Changed</c> → <see cref="OnNicknamesChanged"/>
    /// 把新的整份状态经桥推给页面（**免刷新重画**）。
    ///
    /// <c>nickname_error</c> 额外单独推一条 <c>host.nickname_error</c> ——
    /// 页面靠它把错误配对回自己发起的那次请求（撞名时带 <c>existing_nickname_id</c>，
    /// 引导「已存在，直接选用它？」）。
    /// </summary>
    private void OnNicknameFrameReceived(CoreFrame frame)
    {
        var svc = _nicknames;
        if (svc is null)
            return;

        try
        {
            svc.OnFrame(frame);      // 认识的帧返回 true；不认识的原样忽略
            if (frame is NicknameErrorFrame err)
            {
                RunOnUi(() => _host?.Bridge.PostNicknameError(
                    err.Request, err.Code, err.Message, err.ExistingNicknameId));
            }
        }
        catch (Exception ex)
        {
            // 一帧坏数据绝不能把连接循环带崩
            AgentLog.Write("[NICK] 处理昵称帧失败（连接不受影响）：" + ex);
        }
    }

    /// <summary>整表 / 本机选用 / 可用性有变化 → 把整份状态推给页面（页面免刷新重画）。</summary>
    private void OnNicknamesChanged()
    {
        var svc = _nicknames;
        if (svc is null)
            return;
        RunOnUi(() => _host?.PushNickname(svc));
    }

    /// <summary>
    /// 页面发来的一次昵称请求（<c>web.nickname_*</c>）。**由桥校验过帧形状**后才到这里。
    ///
    /// 分派语义（§5.2 / §5.4）：
    /// <list type="bullet">
    ///   <item><c>select</c>：**纯本地**动作，一个帧都不发，因此**离线也能做**。</item>
    ///   <item><c>refresh</c> / <c>create</c> / <c>rename</c> / <c>reassign_color</c> / <c>delete</c>：
    ///     全部要求在线 —— 服务离线时 <see cref="NicknameService"/> 直接拒绝并给出中文原因，
    ///     不排队、不落盘、不补发。</item>
    /// </list>
    ///
    /// ⚠ 这里回的是「**请求被接受了吗**」，不是「改成功了吗」：真正的成功判据是服务端随后回的
    ///   那一帧（整表变了 / <c>host.nickname_error</c>）。宿主**绝不乐观地宣布成功**。
    /// </summary>
    private void OnNicknameRequested(NicknameRequest req)
    {
        var svc = _nicknames;
        if (svc is null || req is null)
            return;

        var bridge = _host?.Bridge;
        try
        {
            switch (req.Kind)
            {
                case "refresh":
                {
                    var sent = svc.RequestList();
                    bridge?.PostNicknameResult("refresh", sent, !sent,
                        sent ? "正在向服务端刷新昵称列表…" : NicknameService.OfflineMessage);
                    break;
                }

                case "select":
                {
                    // 桥已把「显式传 null（= 切回灰临时）」和「根本没带字段（坏帧，已被拦）」分开。
                    // 这里 nickname_id <= 0 只可能是显式 null 的落点 → 交给 Select(null)。
                    long? id = req.NicknameId > 0 ? req.NicknameId : (long?)null;
                    var r = svc.Select(id);
                    bridge?.PostNicknameResult("select", r.Accepted, r.Offline, r.Message,
                                               r.ExistingNicknameId);
                    break;
                }

                case "create":
                {
                    var r = svc.Create(req.DisplayName);
                    bridge?.PostNicknameResult("create", r.Accepted, r.Offline, r.Message);
                    break;
                }

                case "rename":
                {
                    var r = svc.Rename(req.NicknameId, req.DisplayName);
                    bridge?.PostNicknameResult("rename", r.Accepted, r.Offline, r.Message);
                    break;
                }

                case "reassign_color":
                {
                    var r = svc.ReassignColor(req.NicknameId);
                    bridge?.PostNicknameResult("reassign_color", r.Accepted, r.Offline, r.Message);
                    break;
                }

                case "delete":
                {
                    var r = svc.Delete(req.NicknameId);
                    bridge?.PostNicknameResult("delete", r.Accepted, r.Offline, r.Message);
                    break;
                }

                default:
                    AgentLog.Write($"[NICK] 桥送来了不认识的昵称操作（{req.Kind}，已忽略）");
                    return;
            }

            // 选用是纯本地动作（服务端不会回帧）→ 主动推一次，页面立刻重画；
            // 其余五个的整表变化由 OnNicknamesChanged 负责，这里不重复推。
            if (req.Kind == "select")
                _host?.PushNickname(svc);
        }
        catch (Exception ex)
        {
            AgentLog.Write($"[NICK] 处理页面昵称请求 {req.Kind} 出错（已忽略）：" + ex);
        }
    }

    /// <summary>
    /// 把 <paramref name="action"/> 切到 UI 线程执行（WinForms 托盘 / WPF 窗口都只认 UI 线程）。
    /// 昵称帧来自连接的接收循环（后台线程），而桥的推送最终会碰 WebView2 —— 必须切回去。
    /// </summary>
    private void RunOnUi(Action action)
    {
        try
        {
            if (Dispatcher.CheckAccess())
                action();
            else
                Dispatcher.Invoke(action);
        }
        catch (Exception ex)
        {
            AgentLog.Write("[NICK] UI 线程调度失败（已忽略）：" + ex.Message);
        }
    }

    /// <summary>
    /// 收到新消息（Phase 2 起订阅的是 <see cref="MessageManager.MessageReceived"/>：
    /// 消息**已经落盘**，这里只负责「把它画出来」，并把 UI 事实回报给 Core）。
    ///
    /// 与 Phase 1 的差别只有三处，其余逐字保留：
    /// <list type="number">
    ///   <item>消息不是连接层直推的，而是 Core 落盘之后再交过来的（顺序保证：先持久化后显示）；</item>
    ///   <item>headless 分支不再发服务端不认识的 <c>"delivered"</c>，改为如实回报「无法显示」
    ///     由 <see cref="MessageManager"/> 决定 ack（§2.7-④ / §7E-14）；</item>
    ///   <item>窗口建不起来 / WebView2 不可用时，走原生通知回落而不是静默丢掉。</item>
    /// </list>
    ///
    /// ⚠ 下面这行 <c>var el = frame.Raw;</c> 仍是给页面的原始帧（字段原样透传，含 history），
    ///   与 Phase 1 一致；<c>messageId</c> 直接读强类型字段（原实现里那处对整帧调
    ///   <c>GetInt64()</c> 的笔误已随之消失，见 docs/BUG-PC-REALTIME.md）。
    /// </summary>
    private void OnMessageReceived(MessageFrame frame)
    {
        var el = frame.Raw;      // 推给页面时仍用原始帧（字段原样透传，含 history）

        Dispatcher.Invoke(() =>
        {
            var messageId = frame.MessageId;
            var autoClose = frame.AutoCloseSeconds ?? 0;

            if (IsHeadless)
            {
                // 登录前没有交互式桌面，弹窗显示不出来。ACK 决策在 Core：这里只如实回报
                // 「本机无法显示」→ Core 记 NotDisplayed 并回报 device_received。
                // ⚠ 原实现这里发的是服务端不认识的状态 "delivered"（rank=-1，纯 no-op）。
                AgentLog.Write($"headless：收到消息 {messageId}，无人登录无法显示弹窗"
                             + " → 如实回报 device_received（不再发非法的 delivered）");
                _messaging?.NotifyDisplayUnavailable(messageId, "headless：无人登录，没有可显示的桌面");
                return;
            }

            var host = EnsureHost();
            if (host is null)
            {
                // 界面根本建不起来（异常已在 EnsureHost 里记过日志）→ 同样是「无法显示」的终态。
                // 消息已经在 Core 落盘，界面修好后还能重放 —— 这就是 ACK 与 UI 解耦的意义。
                _messaging?.NotifyDisplayUnavailable(messageId, "界面创建失败");
                return;
            }

            // WebView2 这条腿**确定**不可用（缺 Runtime，已退到 WPF 提示页）→ 直接用
            // 原生通知提醒，并把结果如实回报给 Core（§6 Phase 2-5）。
            if (host.WebViewUnavailable)
            {
                NotifyFallback(frame.MessageId, frame.SenderName, frame.Content, "WebView2 不可用");
                return;
            }

            // 有消息来了 → 回到全屏强提醒形态，把整帧推给页面去渲染。
            // popup_displayed **不在这里回报**：等页面 web.ack
            // （「真的画到屏幕上了」）到达才算数，见 CreateHost。
            host.ShowPopup();
            host.PushMessage(el, autoClose);
        });
    }

    /// <summary>
    /// Core 的消息生命周期变化 → 宿主日志。
    ///
    /// 这一行是「ACK 决策确实发生在 Core」的现场证据：排查「消息收到没显示 / 显示了没 ack」
    /// 这类问题时，日志里能直接看到 <c>Persisted → Displayed → AckSent</c>（或
    /// <c>NotDisplayed → AckSent</c>）这条链，不必再靠猜。
    /// </summary>
    private void OnDeliveryStateChanged(MessageDeliveryStateChangedArgs e) =>
        AgentLog.Write($"[APP] message_id={e.MessageId} 投递状态={e.State}（{e.Detail}）");

    /// <summary>
    /// Core 要求兜底提醒：消息落盘后在超时窗口内**没有任何界面处理过**它
    /// （WebView2「没报错但页面永远不就绪」那种情况）。
    ///
    /// 这是 Core 与 UI 解耦之后仍然能提醒到用户的最后一段路：用托盘原生通知提醒，
    /// 并如实回报（弹得出去 → <c>popup_displayed</c>；弹不出去 → <c>device_received</c>）。
    /// </summary>
    private void OnDisplayFallbackRequired(MessageRecord record, string reason) =>
        NotifyFallback(record.MessageId, record.SenderName, record.Content, reason);

    /// <summary>
    /// 本地正式界面加载失败（例如 exe 目录下 <c>shell\</c> 被改名 → 404）：
    /// 立刻把库里「还没显示过」的消息用原生通知提醒掉，不必干等超时兜底
    /// （用户等 20 秒才收到通知 vs 1 秒内收到，差别很大）。
    /// </summary>
    private void OnLocalPageFailed(string detail)
    {
        var messaging = _messaging;
        if (messaging is null)
            return;

        foreach (var record in messaging.PendingForReplay())
            NotifyFallback(record.MessageId, record.SenderName, record.Content,
                "本地界面加载失败（" + detail + "）");
    }

    /// <summary>
    /// 用原生通知提醒一条上不了界面的消息，并**如实**回报结果：
    /// 弹得出去 → <c>NotifyFallbackDisplayed</c>（Core 回报 <c>popup_displayed</c>）；
    /// 弹不出去（没有托盘 / headless）→ <c>NotifyDisplayUnavailable</c>
    /// （Core 回报 <c>device_received</c>，不谎报弹窗）。§8.11 方案 (a)。
    /// </summary>
    private void NotifyFallback(long messageId, string? sender, string? content, string reason)
    {
        var shown = false;
        try { shown = _fallback?.Notify(sender, content, reason) ?? false; }
        catch (Exception ex) { AgentLog.Write("原生通知回落异常（按未提醒处理）：" + ex.Message); }

        if (shown)
            _messaging?.NotifyFallbackDisplayed(messageId, reason);
        else
            _messaging?.NotifyDisplayUnavailable(messageId, reason + "（原生通知也不可用）");
    }

    /// <summary>
    /// 页面就绪时给壳的补齐清单：Core 里「还没显示过」的消息（原始帧 + auto_close_seconds）。
    /// 超过 50 条只取最近的 —— 更老的让页面自己走 HTTP 拉历史（壳模式下的既有做法）。
    /// </summary>
    private IReadOnlyList<(JsonElement Message, int AutoClose)> TakePendingForReplay()
    {
        var result = new List<(JsonElement Message, int AutoClose)>();
        var messaging = _messaging;
        if (messaging is null)
            return result;

        foreach (var record in messaging.PendingForReplay())
        {
            if (record.Raw.ValueKind != JsonValueKind.Object)
                continue;
            result.Add((record.Raw, record.AutoCloseSeconds));
        }

        if (result.Count > 0)
            AgentLog.Write($"壳：页面就绪，从 Core 的 MessageStore 取到 {result.Count} 条未显示消息");
        return result;
    }

    /// <summary>
    /// 本机回复被服务端受理（Core 的强类型 <see cref="ReplyAckFrame"/>，取代原来的裸 <c>JsonElement</c>）。
    ///
    /// ⚠ 回调体**逐字保留**：只把原来对 JsonElement 的三次 <c>TryGetProperty</c>
    ///   换成读 DTO 的属性 —— <c>Status</c> / <c>ClientId</c> / <c>MessageId</c>
    ///   就是同一批值（缺字段 / 类型不对时的缺省值由 FrameCodec 给出，见 ReplyAckFrame.cs）。
    /// </summary>
    private void OnReplyAcked(ReplyAckFrame frame)
    {
        Dispatcher.Invoke(() =>
        {
            _pendingReplyClientId = null;

            var status = frame.Status;
            var clientId = frame.ClientId;
            long messageId = frame.MessageId ?? 0;

            var bridge = _host?.Bridge;
            if (bridge is null) return;

            if (status == "ok")
                bridge.PostReplyAck(clientId, "ok", messageId, "回复已送达服务器");
            else if (status == "empty")
                bridge.PostReplyAck(clientId, "empty", messageId, "内容为空");
            else
                bridge.PostReplyAck(clientId, "error", messageId,
                    status.Length > 0 ? status : "未知错误");
        });
    }

    /// <summary>
    /// 主动拉的历史到达（Core 的强类型 <see cref="HistoryFrame"/>，取代原来的裸 <c>JsonElement</c>）。
    ///
    /// ⚠ 回调体**逐字保留**：只把「取 messages 数组」换成 DTO 上的同一个属性
    ///   （<c>Messages</c> 就是那一段原文；缺字段时 FrameCodec 给的是未定义值，
    ///   与原 <c>TryGetProperty</c> 判据一样落不进数组分支）。
    /// </summary>
    private void OnHistoryReceived(HistoryFrame frame)
    {
        Dispatcher.Invoke(() =>
        {
            // 壳模式下页面不连 /ws/web，服务端发来的历史直接转给页面渲染
            // （页面自己也能走 HTTP 拉，这条是省一次往返的新增帧）。
            if (frame.Messages.ValueKind == JsonValueKind.Array)
                _host?.PushHistory(frame.Messages);
        });
    }

    /// <summary>
    /// 截图请求处理完了（成功、失败、本机不支持都算）→ 把结果**镜像给本地页面**。
    ///
    /// Phase 3 起「截哪张图、回什么应答」都在 Core 里（<see cref="CommandRouter"/> →
    /// <see cref="WindowsScreenshotProvider"/>），宿主只剩这一件事：
    /// 壳模式下页面**不连** <c>/ws/web</c>，收不到服务端广播的截图，
    /// 只回服务端的话「查看桌面」在那台电脑自己的界面上会一直转圈
    /// （原实现是在 <c>OnScreenshotRequested</c> 里顺手推的，行为等价）。
    ///
    /// headless（会话 0）没有窗口、更没有页面 —— 原实现在那个分支里也是直接 return，
    /// 所以这里同样不推（推到不存在的页面上没有意义）。
    /// </summary>
    private void OnScreenshotHandled(ScreenshotHandledArgs args)
    {
        if (IsHeadless)
            return;

        try
        {
            Dispatcher.Invoke(() => _host?.PushScreenshot(args.RequestId, args.Base64, args.Error));
        }
        catch (Exception ex)
        {
            AgentLog.Write("推送截图给页面失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 执行关机的唯一实现（页面请求关机走它；服务端指令经 Core 的
    /// <see cref="CommandRouter"/> → <see cref="WindowsPowerProvider"/> 也落到这里）。
    ///
    /// Phase 3 起它同时是**电源能力**的执行体（<c>Platform/WindowsPowerProvider.cs</c>
    /// 包着它）：日志、托盘气泡、<c>event</c> 帧、给页面的中文说明全部逐字保留 ——
    /// 这套东西页面请求那条路也在用，搬走就会动到那条路的行为。
    /// 返回 (是否已下发, 给界面看的中文说明)。
    /// </summary>
    private (bool Ok, string Detail) ExecuteShutdown(int delaySeconds)
    {
        try
        {
            AgentLog.Write($"收到关机指令，{delaySeconds} 秒后执行");

            try
            {
                _tray?.ShowBalloonTip(6000, "家庭消息",
                    $"收到远程关机指令，{delaySeconds} 秒后关机（可在命令行 shutdown /a 取消）",
                    WinForms.ToolTipIcon.Warning);
            }
            catch
            {
                // 气泡提示失败不影响关机
            }

            PowerControl.Shutdown(delaySeconds);
            Core.SendEvent("shutdown", $"delay={delaySeconds}s");

            return (true, $"已下发关机，{delaySeconds} 秒后执行（命令行 shutdown /a 可取消）");
        }
        catch (Exception ex)
        {
            AgentLog.Write("执行关机失败：" + ex.Message);
            Core.SendEvent("shutdown_failed", ex.Message);

            return (false, "执行关机失败：" + ex.Message);
        }
    }

    // ⚠ 远程解锁的请求处理（原 OnUnlockRequested）在 Phase 3 搬进了 Core 的命令派发：
    //   CommandRouter → WindowsUnlockGuard（包住既有 UnlockGuard，§8.12 归属仍待拍板）
    //   → Core.UnlockResult。校验顺序、应答码、日志逐字未变，只是调用点从宿主移到了 Core。

    /// <summary>
    /// 会话状态变化（锁屏 / 解锁 / 登录 / 注销）→ 立刻上报一次，
    /// 不等下一个 15 秒心跳周期：网页端的「远程解锁」按钮可用性靠它及时更新。
    /// 回调已在 UI 线程上（SessionState 内部切过 Dispatcher），这里只多推一份给页面。
    /// </summary>
    private void OnSessionStateChanged(string state)
    {
        try
        {
            Core.ReportSessionState();
            AgentLog.Write($"会话状态已上报：{state}");
        }
        catch (Exception ex)
        {
            // 上报失败不致命：下一个周期心跳（≤15 秒）会带上最新状态
            AgentLog.Write("上报会话状态失败（下个心跳周期会补上）：" + ex.Message);
        }

        try { _host?.PushSession(); }
        catch (Exception ex) { AgentLog.Write("推送会话状态给页面失败：" + ex.Message); }
    }

    /// <summary>用系统默认程序打开一个路径（文件或目录）。</summary>
    private void OpenPath(string path, string what)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AgentLog.Write($"打开{what}失败：{ex.Message}");
            ReportOnce($"打开{what}失败", ex);
        }
    }

    /// <summary>打开 agent.log。窗口打不开时，这是唯一能拿到诊断信息的入口。</summary>
    private void OpenLogFile()
    {
        try
        {
            // 文件不存在就建一个，免得「打开」直接报错
            if (!System.IO.File.Exists(AgentLog.FilePath))
                System.IO.File.WriteAllText(AgentLog.FilePath,
                    "（日志暂时是空的）\r\n", System.Text.Encoding.UTF8);
        }
        catch { /* 建不了就让下面的 Process.Start 去报错 */ }
        OpenPath(AgentLog.FilePath, "日志文件");
    }

    /// <summary>打开配置目录（%APPDATA%\FamilyAgent）。</summary>
    private void OpenConfigDir()
    {
        var dir = System.IO.Path.GetDirectoryName(AgentLog.FilePath) ?? ".";
        OpenPath(dir, "配置目录");
    }

    // ---------------- 托盘 ----------------

    private void SetupTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Text = "家庭消息 Agent",
            Visible = true,
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("打开对话窗口", null, (_, _) => ShowConversation());
        menu.Items.Add("设置…", null, (_, _) => ShowSettings());
        menu.Items.Add("重新连接", null, (_, _) => Core.Restart());
        menu.Items.Add("打开控制台", null, (_, _) => OpenConsole());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        // 放在托盘里而不是只放设置页：窗口一旦打不开，设置页就进不去，
        // 日志也就拿不到 —— 那样连排查的入口都没有了。
        menu.Items.Add("打开日志文件", null, (_, _) => OpenLogFile());
        menu.Items.Add("打开配置目录", null, (_, _) => OpenConfigDir());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowConversation();
    }

    /// <summary>从托盘打开对话窗口：没有待处理消息也能看历史并回复。</summary>
    private void ShowConversation()
    {
        IsSystemShuttingDown = false;
        var host = EnsureHost();
        if (host is null) return;
        try
        {
            // 从托盘打开 → **消息界面**（本地页 app.html 的 client 视图）：
            // 只有消息记录 + 回复栏。以前这里是网页端的 console 视图，
            // 结果把带侧边栏的管理后台整个搬到 PC 上，看着就像「打开了一个网页」。
            // 现在界面是 exe 自带的，物理上没有控制台；网页控制台另有托盘入口。
            host.ShowClient();
            AgentLog.Write($"托盘打开会话：窗口已显示 visible={host.IsVisible} "
                           + $"state={host.WindowState} size={host.Width}x{host.Height} "
                           + $"at=({host.Left},{host.Top})");
        }
        catch (Exception ex)
        {
            AgentLog.Write("!! 显示界面失败：" + ex);
            ReportOnce("显示界面失败", ex);
        }
        Core.RequestHistory(50);
    }

    private void OpenConsole()
    {
        var url = ConsoleUrl();
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // 打不开浏览器就算了，不影响 Agent
        }
    }

    private static string ConsoleUrl()
    {
        var url = (Config.ServerUrl ?? "").Trim().TrimEnd('/');
        if (url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            url = "http://" + url[5..];
        else if (url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url[6..];
        if (url.EndsWith("/ws"))
            url = url[..^3];
        return url;
    }

    /// <summary>
    /// 打开**本机设置视图**（托盘「设置…」、启动时还没配置过、页面顶栏齿轮走
    /// <c>web.open_settings</c> 也汇到这儿）。
    ///
    /// 设置是本地页 app.html 的一个视图：宿主只切视图 + 发 host.mode，
    /// 不再去加载网页端那个带侧边栏的管理后台（那正是「PC 上弹出后台」的来源）。
    /// </summary>
    private void ShowSettings()
    {
        var host = EnsureHost();
        if (host is null) return;
        try { host.ShowSettings(); }
        catch (Exception ex)
        {
            AgentLog.Write("!! 打开设置失败：" + ex);
            ReportOnce("打开设置失败", ex);
        }
    }

    // ---------------- 退出 ----------------

    /// <summary>页面点了「退出程序」（或缺运行时的提示页上的退出按钮）。</summary>
    internal void ForceQuit() => ExitApp();

    private void ExitApp()
    {
        IsSystemShuttingDown = true;
        Cleanup();
        Shutdown();
    }

    private void Cleanup()
    {
        // 取消会话事件订阅：进程都在收尾了，再回调进来没有意义
        try { SessionState.Stop(); } catch { }

        // 清掉心跳：注销/退出后 headless 实例能马上重新接管，
        // 不用干等 45 秒过期
        if (!IsHeadless)
        {
            Presence.Stop();
            Presence.ClearHeartbeat();
        }

        try { Core?.Stop(); } catch { }
        try { _host?.ForceClose(); } catch { }
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        // 清掉单实例锁（pid 不是自己就留着，别把别人的锁删了）。
        // 删不掉也无所谓 —— 下次启动靠 pid 判活绕过。
        SingleInstance.Release();
    }
}
