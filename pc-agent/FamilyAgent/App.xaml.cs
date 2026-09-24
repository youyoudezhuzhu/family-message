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

    private Mutex? _singleInstance;
    private WinForms.NotifyIcon? _tray;
    private PopupWindow? _popup;
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

        // 单实例：重复启动（开机自启 + 手动双击）时直接退出
        _singleInstance = new Mutex(true, @"Global\FamilyAgent.SingleInstance", out var isNew);
        if (!isNew)
        {
            AgentLog.Write("已有实例在运行，本进程退出");
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SessionEnding += (_, _) => IsSystemShuttingDown = true;
        Exit += (_, _) => Cleanup();

        AgentLog.Rotate();

        // --config：SYSTEM 身份的「开机」计划任务跑在会话 0，%APPDATA% 不是
        // 用户那个，必须由任务参数显式指定配置路径，否则会注册成另一个设备。
        var cfgPath = ArgValue(e.Args, "--config");
        if (!string.IsNullOrWhiteSpace(cfgPath))
            AgentConfig.UseConfigPath(cfgPath);

        // --headless：开机后、还没人登录时由计划任务拉起。会话 0 没有桌面，
        // 不能建窗口也不能建托盘图标，所以只维持连接。
        IsHeadless = HasArg(e.Args, "--headless");

        Config = AgentConfig.Load();

        // ── 启用 WPF 内置 Fluent 主题 ─────────────────────────────
        // 只设 ThemeMode 还不够：真正生效的前提是**不要再给控件手写 ControlTemplate**，
        // 否则自定义模板会盖过 Fluent 的样式。所以 PopupWindow.xaml 里那些
        // 手写的 Button/TextBox/ComboBox/CheckBox 模板已全部删除。
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

        MdTheme.Apply(Config.ThemeId, Config.ThemeMode);
        AgentLog.Write($"=== FamilyAgent 启动 device={Config.DeviceId} server={Config.ServerUrl} theme={MdTheme.CurrentId} agent={AgentClient.ReportedVersion} ===");
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

        // 系统主题/强调色变化时重新取一次色。
        // MdTheme 的颜色来自 Fluent 主题字典，是**当时**取到的画刷对象；
        // Windows 切换明暗或主题色后，不去重取就会一直用旧颜色。
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    MdTheme.Apply(Config.ThemeId, Config.ThemeMode);
                    _popup?.RefreshCardThemes();
                });
            }
            catch (Exception ex)
            {
                AgentLog.Write("跟随系统主题变化失败：" + ex.Message);
            }
        };

        SetupTray();

        // --tray：开机自启时静默进托盘；手动启动则直接打开消息界面
        var silent = HasArg(e.Args, "--tray");
        if (!Config.IsConfigured)
            ShowSettings();          // 还没配置过 → 直接进设置页
        else if (!silent)
            ShowConversation();      // 打开就是消息界面，设置在右上角
    }

    /// <summary>
    /// 把本机的明暗偏好转成 WPF 的 ThemeMode。
    ///
    /// ⚠️ 必须和 MdTheme 用同一个偏好：应用自己的面板颜色由 MdTheme 决定，
    /// 而按钮/输入框等标准控件由 Fluent 决定 —— 两边明暗不一致的话，
    /// 会出现「浅色控件压在深色背景上」的错配。
    /// </summary>
    internal static ThemeMode ToFluent(string? pref) => (pref ?? "system").Trim().ToLowerInvariant() switch
    {
        "light" => ThemeMode.Light,
        "dark" => ThemeMode.Dark,
        _ => ThemeMode.System,
    };

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

    // ---------------- 弹窗（全局复用同一个窗口）----------------

    /// <summary>
    /// 取（必要时创建）唯一的消息窗口。
    ///
    /// 创建失败时**不能只是静默**：之前窗口建不起来，用户看到的现象是
    /// 「点菜单没反应」，而异常被全局兜底吞掉，连日志都要翻文件才知道 ——
    /// 所以这里既写日志也弹框，把原因直接摆到用户面前。
    /// </summary>
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

    private PopupWindow? EnsurePopup()
    {
        if (_popup is not null)
            return _popup;

        try
        {
            return CreatePopup();
        }
        catch (Exception ex)
        {
            AgentLog.Write("!! 创建消息窗口失败：" + ex);
            ReportOnce("打开消息窗口失败", ex);
            return null;
        }
    }

    private PopupWindow CreatePopup()
    {
        var popup = new PopupWindow();
        popup.Acknowledged += id => Client.Ack(id, "read");
        popup.RetryAck += id => Client.Ack(id, "read");
        popup.ReplyRequested += OnReplyRequested;
        popup.ReplyNameChanged += OnReplyNameChanged;
        popup.SettingsSaved += OnSettingsSaved;
        popup.SetReplyNames(Config.ReplyNames, Config.ReplyName);
        popup.SetConfig(Config);
        _popup = popup;

        return popup;
    }

    private void OnReplyRequested(string senderName, string content)
    {
        var clientId = Guid.NewGuid().ToString("N")[..12];
        _pendingReplyClientId = clientId;

        // 不再预判连接状态：直接尝试发送，发不出去会自动入队，重连后补发。
        // （之前 `if (!Connected) 报失败` 会把能发的回复也拦下来。）
        var dispatched = Client.Reply(senderName, content, clientId);
        if (!dispatched)
        {
            _popup?.MarkReplyQueued();
            return;
        }

        _ = Task.Run(async () =>
        {
            // 服务端迟迟不回执时给个明确提示，别让界面永远停在「发送中…」
            await Task.Delay(TimeSpan.FromSeconds(12));
            if (_pendingReplyClientId == clientId)
            {
                AgentLog.Write($"✗ 回复 12 秒内未收到 reply_ack（连接状态：{Client.DescribeConnection()}）");
                Dispatcher.Invoke(() => _popup?.MarkReplyQueued());
            }
        });
    }

    /// <summary>弹窗设置页保存后：落盘、应用自启、重连。</summary>
    private void OnSettingsSaved()
    {
        Config.Save();
        // 用户在设置里主动开关 → 允许弹一次 UAC 来注册 SYSTEM 计划任务
        // （「开机后未登录也能连上」靠的就是它）
        AutoStart.Apply(Config.AutoStart, allowElevation: true);
        Client.Restart();
    }

    /// <summary>弹窗里换了回复昵称 → 存到本地配置（服务端不参与）。</summary>
    private void OnReplyNameChanged(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        Config.ReplyName = name;
        Config.Save();
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
            _popup?.SetConnectionStatus(connected, connected ? "已连接" : "未连接");
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
                messageId = idEl.GetInt64();

            var sender = el.TryGetProperty("sender_name", out var sEl) ? sEl.GetString() ?? "" : "";
            var content = el.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? "" : "";
            var created = el.TryGetProperty("created_at", out var tEl) ? tEl.GetString() ?? "" : "";
            var autoClose = el.TryGetProperty("auto_close_seconds", out var aEl)
                            && aEl.ValueKind == JsonValueKind.Number
                ? aEl.GetInt32()
                : 0;

            var history = new List<HistoryItem>();
            if (el.TryGetProperty("history", out var hEl) && hEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in hEl.EnumerateArray())
                    history.Add(HistoryItem.FromJson(h, messageId));
            }

            if (IsHeadless)
            {
                // 登录前没有交互式桌面，弹窗显示不出来。如实回报「已送达」——
                // 谎报 popup_displayed 会让网页端显示一个根本不存在过的状态。
                AgentLog.Write($"headless：收到消息 {messageId}，无人登录无法显示弹窗，回报 delivered");
                Client.Ack(messageId, "delivered");
                return;
            }

            var win = EnsurePopup();
            if (win is null) return;
            win.AppendMessage(messageId, sender, content, created, autoClose, history);

            // 弹窗已经显示在屏幕上 → 回报 popup_displayed
            Client.Ack(messageId, "popup_displayed");
        });
    }

    private void OnReplyAcked(JsonElement el)
    {
        Dispatcher.Invoke(() =>
        {
            _pendingReplyClientId = null;
            var status = el.TryGetProperty("status", out var sEl) ? sEl.GetString() : "";
            if (status == "ok")
                _popup?.MarkReplyDelivered();
            else if (status == "empty")
                _popup?.MarkReplyFailed("内容为空");
            else
                _popup?.MarkReplyFailed(status ?? "未知错误");
        });
    }

    private void OnHistoryReceived(JsonElement el)
    {
        Dispatcher.Invoke(() =>
        {
            var items = new List<HistoryItem>();
            if (el.TryGetProperty("messages", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in arr.EnumerateArray())
                    items.Add(HistoryItem.FromJson(h, 0));
            }
            // 右侧历史弹幕已移除（和网页端消息记录重复），历史现在直接铺进对话区
            _popup?.SeedFromHistory(items);
        });
    }

    private void OnScreenshotRequested(string requestId)
    {
        if (IsHeadless)
        {
            // 会话 0 没有桌面，截出来只会是黑屏。直接说明原因，
            // 比回一张黑图让用户以为电脑坏了要好。
            AgentLog.Write("headless：尚无人登录，无法截图");
            _ = Client.SendScreenshotAsync(requestId, null, 0, 0,
                "电脑已开机但尚无人登录，当前没有可截取的桌面");
            return;
        }

        _ = Task.Run(async () =>
        {
            var (base64, width, height) = ScreenCapture.CaptureJpeg();
            await Client.SendScreenshotAsync(requestId, base64, width, height,
                ScreenCapture.LastError);
        });
    }

    /// <summary>
    /// 网页端点了「关机」：本机执行。给一个托盘气泡提示，并留出几秒取消时间。
    /// 这是设备管理能力 —— PC Agent 是受 Server 信任的家庭设备 Agent，不再二次确认。
    /// </summary>
    private void OnShutdownRequested(int delaySeconds)
    {
        Dispatcher.Invoke(() =>
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

            try
            {
                PowerControl.Shutdown(delaySeconds);
                Client.SendOrQueue(new
                {
                    type = "event",
                    kind = "shutdown",
                    detail = $"delay={delaySeconds}s",
                }, "event");
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
            }
        });
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
    /// 回调已在 UI 线程上（SessionState 内部切过 Dispatcher），这里不碰界面。
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
        var win = EnsurePopup();
        if (win is null) return;
        try
        {
            win.PresentIdle();
            AgentLog.Write($"托盘打开会话：窗口已显示 visible={win.IsVisible} "
                           + $"state={win.WindowState} size={win.Width}x{win.Height} "
                           + $"at=({win.Left},{win.Top})");
        }
        catch (Exception ex)
        {
            AgentLog.Write("!! 显示消息窗口失败：" + ex);
            ReportOnce("显示消息窗口失败", ex);
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

    private void ShowSettings()
    {
        var popup = EnsurePopup();
        if (popup is null) return;
        try { popup.ShowSettingsPage(); }
        catch (Exception ex)
        {
            AgentLog.Write("!! 打开设置页失败：" + ex);
            ReportOnce("打开设置页失败", ex);
        }
    }

    // ---------------- 退出 ----------------

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
        try { _popup?.ForceClose(); } catch { }
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
    }
}
