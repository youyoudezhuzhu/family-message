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
    private SettingsWindow? _settings;
    private PopupWindow? _popup;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例：重复启动（开机自启 + 手动双击）时直接退出
        _singleInstance = new Mutex(true, @"Global\FamilyAgent.SingleInstance", out var isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SessionEnding += (_, _) => IsSystemShuttingDown = true;
        Exit += (_, _) => Cleanup();

        Config = AgentConfig.Load();
        Client = new AgentClient(Config);
        Client.ConnectionChanged += OnConnectionChanged;
        Client.MessageReceived += OnMessageReceived;
        Client.ScreenshotRequested += OnScreenshotRequested;
        Client.ReplyAcked += OnReplyAcked;
        Client.HistoryReceived += OnHistoryReceived;

        AutoStart.Apply(Config.AutoStart);
        Client.Start();
        SetupTray();

        // --tray：开机自启时静默进托盘；未配置过则无论如何都弹设置窗
        var silent = HasArg(e.Args, "--tray");
        if (!Config.IsConfigured || !silent)
            ShowSettings();
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

    private PopupWindow EnsurePopup()
    {
        if (_popup is not null)
            return _popup;

        var popup = new PopupWindow();
        popup.Acknowledged += id => _ = Client.AckAsync(id, "read");
        popup.RetryAck += id => _ = Client.AckAsync(id, "read");
        popup.ReplyRequested += OnReplyRequested;
        _popup = popup;

        return popup;
    }

    private void OnReplyRequested(string content)
    {
        if (!Client.Connected)
        {
            _popup?.MarkReplyFailed("未连接到服务端");
            return;
        }

        var clientId = Guid.NewGuid().ToString("N")[..12];
        _ = Client.ReplyAsync(content, clientId);
    }

    // ---------------- 服务端事件 ----------------

    private void OnConnectionChanged(bool connected, string message)
    {
        Dispatcher.Invoke(() =>
        {
            _settings?.UpdateStatus(connected, message);
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

            EnsurePopup().AppendMessage(messageId, sender, content, created, autoClose, history);

            // 弹窗已经显示在屏幕上 → 回报 popup_displayed
            _ = Client.AckAsync(messageId, "popup_displayed");
        });
    }

    private void OnReplyAcked(JsonElement el)
    {
        Dispatcher.Invoke(() =>
        {
            var status = el.TryGetProperty("status", out var sEl) ? sEl.GetString() : "";
            if (status == "ok")
                _popup?.MarkReplyDelivered();
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
            _popup?.ReplaceHistory(items);
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
        menu.Items.Add("退出", null, (_, _) => ExitApp());

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowConversation();
    }

    /// <summary>从托盘打开对话窗口：没有待处理消息也能看历史并回复。</summary>
    private void ShowConversation()
    {
        IsSystemShuttingDown = false;
        EnsurePopup().PresentIdle();
        _ = Client.RequestHistoryAsync(50);
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
        if (_settings is null)
        {
            _settings = new SettingsWindow();
            _settings.Closed += (_, _) => _settings = null;
        }
        _settings.Show();
        _settings.WindowState = WindowState.Normal;
        _settings.Activate();
        _settings.UpdateStatus(Client.Connected, "");
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
