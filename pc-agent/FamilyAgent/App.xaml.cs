using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace FamilyAgent;

public partial class App : Application
{
    public static AgentConfig Config { get; private set; } = null!;
    public static AgentClient Client { get; private set; } = null!;
    public static bool IsSystemShuttingDown { get; private set; }

    /// <summary>
    /// 是否以「登录前」模式运行（<c>--headless</c>）。此时没有交互式桌面，
    /// 不能建窗口；截图/弹窗这类需要桌面的功能要给出明确原因而不是静默失败。
    /// </summary>
    public static bool IsHeadless { get; private set; }

    private WinForms.NotifyIcon? _tray;
    private WebHostWindow? _host;
    private string? _pendingReplyClientId;

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
                                                 AgentClient.ReportedVersion,
                                                 StringComparison.Ordinal);

                SingleInstance.RequestShow(AgentClient.ReportedVersion);

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
                       + $"theme={Config.ThemeMode} agent={AgentClient.ReportedVersion} ===");
        Client = new AgentClient(Config);
        Client.ConnectionChanged += OnConnectionChanged;
        Client.MessageReceived += OnMessageReceived;
        Client.ScreenshotRequested += OnScreenshotRequested;
        Client.ReplyAcked += OnReplyAcked;
        Client.HistoryReceived += OnHistoryReceived;
        Client.ShutdownRequested += OnShutdownRequested;
        Client.UnlockRequested += OnUnlockRequested;

        // ── Windows 会话状态上报（远程解锁 Phase 1）──────────────────────
        // 在这里（Client 已建好、连接还没开始）启动最合适：第一次心跳就能带
        // 上正确的状态。先 Start() 再订阅 —— Start() 里的首次检测会在默认值
        // 变化时抛通知，让它落在没有订阅者的空窗里，省一次无意义的上报。
        SessionState.Start();
        SessionState.Changed += OnSessionStateChanged;

        // 启动时不提权：SYSTEM 计划任务若已注册过就直接跳过，
        // 否则每次开机都会弹一次 UAC（用户最烦这个）。
        AutoStart.Apply(Config.AutoStart, allowElevation: false);
        Client.Start();

        if (IsHeadless)
        {
            // 登录前模式：当前没有交互式桌面，任何窗口都显示不出来。
            // 这里只做「让设备在线」，等有人登录后由交互式实例接手。
            AgentLog.Write("以 headless 模式运行（登录前）：不显示界面，只维持设备在线；"
                         + "有人登录后自动让位给交互式实例");
            Presence.StartSupervisor(Client);
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
                + $"　你刚启动：{AgentClient.ReportedVersion}\n\n"
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

        // 页面回报「这条消息真的画到屏幕上了」→ 才回报 popup_displayed。
        // （不能提前回报：页面一次渲染都没发生就回报，是谎报。）
        host.MessageAcked += id =>
        {
            try { Client.Ack(id, "popup_displayed"); }
            catch (Exception ex) { AgentLog.Write("回报 popup_displayed 失败：" + ex.Message); }
        };

        // 关窗 / 自动关闭 / Alt+F4（含页面点「知道了」）→ 这些未读按「已读」回报
        host.Dismissed += id =>
        {
            try { Client.Ack(id, "read"); }
            catch (Exception ex) { AgentLog.Write("回报已读失败：" + ex.Message); }
        };

        // ── 页面 → 宿主：每一条都落到原来那套逻辑上，语义不变 ──
        host.Bridge.ReplyReceived += OnReplyRequested;
        host.Bridge.ScreenshotRequested += OnLocalScreenshotRequested;
        host.Bridge.ActionRequested += OnLocalActionRequested;
        host.Bridge.ServerChanged += OnServerSettingsChanged;
        host.Bridge.ConfigSaveRequested += OnSaveConfigRequested;
        host.Bridge.SettingsRequested += ShowSettings;
        host.Bridge.QuitRequested += ExitApp;

        return host;
    }

    /// <summary>
    /// 窗口标题：<c>家庭消息 · 设备名（版本号）</c>。
    ///
    /// 版本号常驻写在这里是有原因的：用户机器上可能同时放着好几份 exe
    /// （旧的在桌面、新的在别的目录），而「屏幕上跑的是哪一版」以前完全看不出来。
    /// 版本号一律取 <see cref="AgentClient.ReportedVersion"/>，**不另写死字符串**。
    /// </summary>
    private static string WindowTitle()
    {
        var version = AgentClient.ReportedVersion;
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
            while (!IsSystemShuttingDown)
            {
                try
                {
                    var remote = SingleInstance.TakeShowRequest();
                    if (remote is not null)
                    {
                        var mine = AgentClient.ReportedVersion;
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
                        + $"{AgentClient.ReportedVersion}。\n请退出后重新运行新版 exe。",
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
    ///   · 改了服务端地址 / 口令 → 重连（<c>Client.Restart</c>）
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
            Client.Restart();

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
        var dispatched = Client.Reply(who, content, clientId);
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
                AgentLog.Write($"✗ 回复 12 秒内未收到 reply_ack（连接状态：{Client.DescribeConnection()}）");
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
            _host?.PushScreenshot(requestId, null, HeadlessScreenshotReason);
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
        Client.Restart();
    }

    // ---------------- 服务端事件 ----------------

    private void OnConnectionChanged(bool connected, string message)
    {
        AgentLog.Write(connected ? "== 已连接 ==" : $"== 断开：{message} ==");

        // 刚连上就上报一次会话状态：服务端要靠它判断远程解锁是否可用，
        // 等第一个 15 秒周期心跳太慢（等效于「hello 阶段就带上状态」）。
        if (connected)
        {
            try { Client.ReportSessionState(); }
            catch (Exception ex) { AgentLog.Write("上线时上报会话状态失败：" + ex.Message); }
        }

        Dispatcher.Invoke(() =>
        {
            _host?.SetConnection(connected, connected ? "已连接" : "未连接");
            if (_tray is not null)
                _tray.Text = connected ? "家庭消息 Agent · 已连接" : "家庭消息 Agent · 未连接";
        });
    }

    private void OnMessageReceived(JsonElement el)
    {
        Dispatcher.Invoke(() =>
        {
            long messageId = 0;
            if (el.TryGetProperty("message_id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                messageId = el.GetInt64();

            var autoClose = el.TryGetProperty("auto_close_seconds", out var aEl)
                            && aEl.ValueKind == JsonValueKind.Number
                ? aEl.GetInt32()
                : 0;

            if (IsHeadless)
            {
                // 登录前没有交互式桌面，弹窗显示不出来。如实回报「已送达」——
                // 谎报 popup_displayed 会让网页端显示一个根本不存在过的状态。
                AgentLog.Write($"headless：收到消息 {messageId}，无人登录无法显示弹窗，回报 delivered");
                Client.Ack(messageId, "delivered");
                return;
            }

            var host = EnsureHost();
            if (host is null) return;

            // 有消息来了 → 回到全屏强提醒形态，把整帧推给页面去渲染。
            // popup_displayed **不在这里回报**：等页面 web.ack
            // （「真的画到屏幕上了」）到达才算数，见 CreateHost。
            host.ShowPopup();
            host.PushMessage(el, autoClose);
        });
    }

    private void OnReplyAcked(JsonElement el)
    {
        Dispatcher.Invoke(() =>
        {
            _pendingReplyClientId = null;

            var status = el.TryGetProperty("status", out var sEl) ? sEl.GetString() ?? "" : "";
            var clientId = el.TryGetProperty("client_id", out var cEl) ? cEl.GetString() ?? "" : "";
            long messageId = el.TryGetProperty("message_id", out var mEl)
                             && mEl.ValueKind == JsonValueKind.Number
                ? mEl.GetInt64()
                : 0;

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

    private void OnHistoryReceived(JsonElement el)
    {
        Dispatcher.Invoke(() =>
        {
            // 壳模式下页面不连 /ws/web，服务端发来的历史直接转给页面渲染
            // （页面自己也能走 HTTP 拉，这条是省一次往返的新增帧）。
            if (el.TryGetProperty("messages", out var arr) && arr.ValueKind == JsonValueKind.Array)
                _host?.PushHistory(arr);
        });
    }

    /// <summary>headless（会话 0）下没桌面可截，统一用这句中文原因回过去。</summary>
    private const string HeadlessScreenshotReason = "电脑已开机但尚无人登录，当前没有可截取的桌面";

    private void OnScreenshotRequested(string requestId)
    {
        if (IsHeadless)
        {
            // 会话 0 没有桌面，截出来只会是黑屏。直接说明原因，
            // 比回一张黑图让用户以为电脑坏了要好。
            AgentLog.Write("headless：尚无人登录，无法截图");
            _ = Client.SendScreenshotAsync(requestId, null, 0, 0, HeadlessScreenshotReason);
            return;
        }

        _ = Task.Run(async () =>
        {
            var (base64, width, height) = ScreenCapture.CaptureJpeg();
            var error = ScreenCapture.LastError;

            // ① 回给服务端（网页端其他会话靠它看到图）
            await Client.SendScreenshotAsync(requestId, base64, width, height, error);

            // ② 顺手推给本地页面：壳模式下页面不连 /ws/web，收不到服务端的截图广播，
            //    只回服务端的话「查看桌面」在那台电脑自己的界面上会一直转圈。
            Dispatcher.Invoke(() => _host?.PushScreenshot(requestId, base64, error));
        });
    }

    /// <summary>
    /// 网页端点了「关机」：本机执行。给一个托盘气泡提示，并留出几秒取消时间。
    /// 这是设备管理能力 —— PC Agent 是受 Server 信任的家庭设备 Agent，不再二次确认。
    /// </summary>
    private void OnShutdownRequested(int delaySeconds)
    {
        // 托盘气泡是 WinForms 组件，统一回到 UI 线程再动它
        Dispatcher.Invoke(() => ExecuteShutdown(delaySeconds));
    }

    /// <summary>
    /// 执行关机的唯一实现（服务端指令与页面请求共用）。
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
            Client.SendOrQueue(new
            {
                type = "event",
                kind = "shutdown",
                detail = $"delay={delaySeconds}s",
            }, "event");

            return (true, $"已下发关机，{delaySeconds} 秒后执行（命令行 shutdown /a 可取消）");
        }
        catch (Exception ex)
        {
            AgentLog.Write("执行关机失败：" + ex.Message);
            Client.SendOrQueue(new
            {
                type = "event",
                kind = "shutdown_failed",
                detail = ex.Message,
            }, "event");

            return (false, "执行关机失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 网页端发起的「远程解锁」请求（远程解锁 Phase 1）。
    ///
    /// 本阶段**不做真正的解锁**：凭据存储还没有，这里只把校验链走完并把应答
    /// 回给服务端 —— 用来验证「归属校验 + 动作校验 + 时效 + 一次性令牌」整条链路。
    /// 校验顺序与各应答码见 <see cref="UnlockGuard"/>。
    ///
    /// 没有界面操作，所以不需要切 Dispatcher；headless 实例同样能应答。
    /// </summary>
    private void OnUnlockRequested(JsonElement el)
    {
        var reply = UnlockGuard.Evaluate(el, Config.DeviceId);
        if (reply is null)
        {
            // 连 request_id 都没有的帧：没法应答，也没有 id 可以记进重放缓存
            AgentLog.Write("✗ unlock_request 缺少 request_id，无法应答");
            return;
        }

        Client.UnlockResult(reply.RequestId, reply.Status, reply.Reason);
    }

    /// <summary>
    /// 会话状态变化（锁屏 / 解锁 / 登录 / 注销）→ 立刻上报一次，
    /// 不等下一个 15 秒心跳周期：网页端的「远程解锁」按钮可用性靠它及时更新。
    /// 回调已在 UI 线程上（SessionState 内部切过 Dispatcher），这里只多推一份给页面。
    /// </summary>
    private void OnSessionStateChanged(string state)
    {
        try
        {
            Client.ReportSessionState();
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
        menu.Items.Add("重新连接", null, (_, _) => Client.Restart());
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
        Client.RequestHistory(50);
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

        try { Client?.Stop(); } catch { }
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
