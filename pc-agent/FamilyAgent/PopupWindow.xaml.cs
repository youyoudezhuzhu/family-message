using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace FamilyAgent;

/// <summary>
/// 收到消息后立即显示的全屏弹窗：无边框、置顶、铺满主屏。
///
/// 左栏 = 消息堆叠（最早的在上、最新的在下，带插入动画与字号分级）+ 回复框
/// 右栏 = 与 Web Sender 的历史对话
///
/// 窗口只创建一次、反复复用：连续来消息时是「接着往下堆」，
/// 而不是关掉再弹一个新的（那是旧版会闪屏的原因）。
/// </summary>
public partial class PopupWindow : Window
{
    /// <summary>堆叠区最多保留多少条，超出丢弃最早的。</summary>
    private const int MaxCards = 12;

    private readonly DispatcherTimer _topmostTimer;
    private DispatcherTimer? _autoCloseTimer;
    private readonly ObservableCollection<HistoryItem> _history = new();
    private readonly List<MessageCard> _cards = new();
    private readonly List<long> _pending = new();   // 还没点「知道了」的消息
    private readonly HashSet<long> _seen = new();   // 防止同一条被重复推送

    private bool _acknowledged;
    private bool _idle;

    /// <summary>用户点了「知道了」，对每条消息各触发一次（参数是 message_id）。</summary>
    public event Action<long>? Acknowledged;
    public event Action<long>? RetryAck;
    /// <summary>用户点了回复，参数是回复内容。</summary>
    public event Action<string>? ReplyRequested;

    public PopupWindow()
    {
        InitializeComponent();
        HistoryList.ItemsSource = _history;

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
    }

    // ---------------- 对外接口 ----------------

    /// <summary>
    /// 追加一条消息到堆叠区。窗口已开着就往下堆；已关闭过则重新开始一堆。
    /// </summary>
    public void AppendMessage(long messageId, string senderName, string content,
                              string createdAt, int autoCloseSeconds,
                              IReadOnlyList<HistoryItem>? history)
    {
        if (!IsVisible)
            ResetStack();   // 上一轮已「知道了」→ 从空堆叠开始

        if (messageId != 0 && !_seen.Add(messageId))
            return;         // 重复推送，忽略

        var time = string.IsNullOrWhiteSpace(createdAt)
            ? DateTime.Now.ToString("HH:mm")
            : createdAt;

        var card = new MessageCard(messageId, senderName, content, time);
        card.PlayEnterAnimation(1.0);
        MessageStack.Children.Add(card);
        _cards.Add(card);

        if (messageId != 0)
            _pending.Add(messageId);

        // 超出上限：丢掉最早那条，避免无限堆积
        while (_cards.Count > MaxCards)
        {
            MessageStack.Children.Remove(_cards[0]);
            _cards.RemoveAt(0);
        }

        Restack(animate: true);
        UpdateBadge();

        if (history is not null)
            ReplaceHistory(history);

        _acknowledged = false;
        _idle = false;
        HintText.Text = autoCloseSeconds > 0
            ? $"{autoCloseSeconds} 秒后自动关闭 · Enter 回复"
            : "Enter 直接回复 · 点「知道了」关闭";

        EnsureShown();
        ArmAutoClose(autoCloseSeconds);
        ScrollToNewest();
    }

    /// <summary>从托盘打开：没有待处理消息，只看历史 + 回复。</summary>
    public void PresentIdle()
    {
        _acknowledged = true;
        _idle = true;
        ResetStack();

        HintText.Text = "Enter 直接回复";
        EnsureShown();
    }

    public void ReplaceHistory(IReadOnlyList<HistoryItem> items)
    {
        _history.Clear();
        foreach (var item in items)
            _history.Add(item);
        ScrollHistoryToEnd();
    }

    public void MarkReplyDelivered() => HintText.Text = "回复已送达服务器";

    public void MarkReplyFailed(string reason) => HintText.Text = "回复失败：" + reason;

    /// <summary>强制关闭（程序退出时）。</summary>
    public void ForceClose()
    {
        IsSystemClosing = true;
        Acknowledge(ack: false);
    }

    private bool IsSystemClosing { get; set; }

    // ---------------- 内部 ----------------

    private void ResetStack()
    {
        MessageStack.Children.Clear();
        _cards.Clear();
        _pending.Clear();
        _seen.Clear();
        UpdateBadge();
    }

    /// <summary>重排层级：越靠后的越新、字号越大、越醒目。</summary>
    private void Restack(bool animate)
    {
        for (var i = 0; i < _cards.Count; i++)
        {
            var distance = _cards.Count - 1 - i;      // 0 = 最新那条
            _cards[i].ApplyProminence(
                isNewest: distance == 0,
                distance: distance,
                // 最新那条的透明度由入场动画负责，这里不动它
                animate: animate && distance > 0);
        }
    }

    private void UpdateBadge()
    {
        var count = _pending.Count;
        CountText.Text = $"{count} 条新消息";
        CountBadge.Visibility = count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        OkButton.Content = count >= 2 ? $"知道了（{count}）" : "知道了";
    }

    private void EnsureShown()
    {
        if (!IsVisible)
        {
            Loaded -= OnLoaded;
            Loaded += OnLoaded;
            Show();
            try
            {
                System.Media.SystemSounds.Exclamation.Play();
            }
            catch
            {
                // 提示音失败不影响显示
            }
        }

        Topmost = true;
        Activate();
        ReplyBox.Focus();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        _topmostTimer.Start();
        ReplyBox.Focus();
    }

    private void ScrollToNewest() =>
        Dispatcher.BeginInvoke(new Action(() => MessageScroll.ScrollToEnd()),
                               DispatcherPriority.Background);

    private void ScrollHistoryToEnd() =>
        Dispatcher.BeginInvoke(new Action(() => HistoryScroll.ScrollToEnd()),
                               DispatcherPriority.Background);

    private void ArmAutoClose(int seconds)
    {
        _autoCloseTimer?.Stop();
        if (seconds <= 0)
            return;

        _autoCloseTimer ??= CreateAutoCloseTimer();
        _autoCloseTimer.Interval = TimeSpan.FromSeconds(seconds);
        _autoCloseTimer.Start();
    }

    private DispatcherTimer CreateAutoCloseTimer()
    {
        var timer = new DispatcherTimer();
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Acknowledge(ack: true);
        };
        return timer;
    }

    private void ReplyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SendReply();
        }
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => SendReply();

    private void SendReply()
    {
        var text = (ReplyBox.Text ?? "").Trim();
        if (text.Length == 0)
            return;

        ReplyBox.Clear();
        _history.Add(HistoryItem.Sent(text, DateTime.Now));
        ScrollHistoryToEnd();

        HintText.Text = "发送中…";
        ReplyRequested?.Invoke(text);
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Acknowledge(ack: true);

    private void Acknowledge(bool ack)
    {
        _topmostTimer.Stop();
        _autoCloseTimer?.Stop();
        _acknowledged = true;

        if (ack && !_idle)
        {
            foreach (var id in _pending.ToArray())
                Acknowledged?.Invoke(id);
        }

        _pending.Clear();
        Hide();
        ResetStack();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 不允许通过 Alt+F4 绕过「知道了」；但系统关机/注销要放行
        if (!_acknowledged && !App.IsSystemShuttingDown && !IsSystemClosing)
        {
            e.Cancel = true;
            Topmost = true;
            foreach (var id in _pending.ToArray())
            {
                try { RetryAck?.Invoke(id); } catch { }
            }
            return;
        }
        base.OnClosing(e);
    }
}
