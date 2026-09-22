using System;
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

        // 单实例：重复启动（例如开机自启 + 手动双击）时直接退出
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

            // 上一条还没关就被新消息顶掉时，先关掉旧的
            if (_popup is not null)
            {
                try { _popup.ForceClose(); } catch { }
                _popup = null;
            }

            var popup = new PopupWindow();
            popup.Acknowledged += id =>
            {
                _popup = null;
                _ = Client.AckAsync(id, "read");
            };
            popup.RetryAck += id => _ = Client.AckAsync(id, "read");

            _popup = popup;
            popup.ShowMessage(messageId, sender, content, created, autoClose);

            // 弹窗已经显示在屏幕上 → 回报 popup_displayed
            _ = Client.AckAsync(messageId, "popup_displayed");
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
        menu.Items.Add("设置…", null, (_, _) => ShowSettings());
        menu.Items.Add("重新连接", null, (_, _) => Client.Restart());
        menu.Items.Add("打开控制台", null, (_, _) => OpenConsole());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowSettings();
    }

    private void OpenConsole()
    {
        var url = (Config.ServerUrl ?? "").Trim().TrimEnd('/');
        if (url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            url = "http://" + url[5..];
        else if (url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url[6..];
        if (url.EndsWith("/ws"))
            url = url[..^3];

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
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
    }
}
