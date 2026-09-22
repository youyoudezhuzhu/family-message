using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace FamilyAgent;

/// <summary>
/// 收到消息后立即显示的全屏弹窗：无边框、置顶、铺满主屏。
/// 默认必须点「知道了」才能关闭（除非配置了自动关闭秒数）。
/// </summary>
public partial class PopupWindow : Window
{
    private readonly DispatcherTimer _topmostTimer;
    private readonly DispatcherTimer? _autoCloseTimer;
    private bool _acknowledged;
    private long _messageId;

    public event Action<long>? Acknowledged;
    public event Action<long>? RetryAck;

    public PopupWindow()
    {
        InitializeComponent();

        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        // 定期重申置顶，避免被某些全屏程序盖住
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _topmostTimer.Tick += (_, _) =>
        {
            Topmost = false;
            Topmost = true;
            Activate();
        };

        _autoCloseTimer = new DispatcherTimer();
        _autoCloseTimer.Tick += (_, _) =>
        {
            _autoCloseTimer.Stop();
            Acknowledge(ack: true);
        };
    }

    /// <summary>填充内容并弹出。</summary>
    public void ShowMessage(long messageId, string senderName, string content,
                            string createdAt, int autoCloseSeconds)
    {
        _messageId = messageId;
        _acknowledged = false;

        SenderText.Text = string.IsNullOrWhiteSpace(senderName) ? "家庭消息" : senderName;
        ContentText.Text = content ?? "";
        TimeText.Text = string.IsNullOrWhiteSpace(createdAt)
            ? DateTime.Now.ToString("HH:mm")
            : createdAt;
        HintText.Text = autoCloseSeconds > 0
            ? $"{autoCloseSeconds} 秒后自动关闭，或点击「知道了」"
            : "点击「知道了」关闭";

        // 内容越长字号越小，保证一屏能看清
        var len = ContentText.Text.Length;
        ContentText.FontSize = len <= 12 ? 112 : len <= 30 ? 84 : len <= 80 ? 56 : 40;

        Loaded -= OnLoaded;
        Loaded += OnLoaded;
        Show();

        if (autoCloseSeconds > 0)
        {
            _autoCloseTimer!.Interval = TimeSpan.FromSeconds(autoCloseSeconds);
            _autoCloseTimer.Start();
        }

        try
        {
            Topmost = true;
            Activate();
            Focus();
            System.Media.SystemSounds.Exclamation.Play();
        }
        catch
        {
            // 提示音失败不影响显示
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        _topmostTimer.Start();
        OkButton.Focus();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Acknowledge(ack: true);

    /// <summary>强制关闭（比如来了新消息要顶掉上一条）。</summary>
    public void ForceClose() => Acknowledge(ack: false);

    private void Acknowledge(bool ack)
    {
        if (_acknowledged)
            return;
        _acknowledged = true;
        _topmostTimer.Stop();
        _autoCloseTimer?.Stop();
        if (ack)
            Acknowledged?.Invoke(_messageId);
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 不允许通过 Alt+F4 等绕过「知道了」；但系统关机/注销要放行
        if (!_acknowledged && !App.IsSystemShuttingDown)
        {
            e.Cancel = true;
            Topmost = true;
            try { RetryAck?.Invoke(_messageId); } catch { }
            return;
        }
        base.OnClosing(e);
    }
}
