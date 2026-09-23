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

        Config = AgentConfig.Load();
        MdTheme.Apply(Config.ThemeId, Config.ThemeMode);
        AgentLog.Write($"=== FamilyAgent 启动 device={Config.DeviceId} server={Config.ServerUrl} theme={MdTheme.CurrentId} agent={AgentClient.ReportedVersion} ===");
        Client = new AgentClient(Config);
        Client.ConnectionChanged += OnConnectionChanged;
        Client.MessageReceived += OnMessageReceived;
        Client.ScreenshotRequested += OnScreenshotRequested;
        Client.ReplyAcked += OnReplyAcked;
        Client.HistoryReceived += OnHistoryReceived;
        Client.ShutdownRequested += OnShutdownRequested;

        AutoStart.Apply(Config.AutoStart);
        Client.Start();
        SetupTray();

        // --tray：开机自启时静默进托盘；手动启动则直接打开消息界面
        var silent = HasArg(e.Args, "--tray");
        if (!Config.IsConfigured)
            ShowSettings();          // 还没配置过 → 直接进设置页
        else if (!silent)
            ShowConversation();      // 打开就是消息界面，设置在右上角
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
        AutoStart.Apply(Config.AutoStart);
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
