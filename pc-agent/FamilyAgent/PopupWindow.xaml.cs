using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace FamilyAgent;

/// <summary>
/// 收到消息后立即显示的全屏弹窗：无边框、置顶、铺满主屏。
///
/// 布局：
///   左栏 = 主对话区（聊天样式，网页发来的靠左、本机回复的靠右，带头像）
///   右栏 = 弹幕式历史流（窄条，往上渐隐）
///
/// 窗口只创建一次并反复复用：连续来消息是往对话区接着追加，
/// 而不是关掉再弹一个新的（那是 v0.1.0 闪屏的原因）。
/// </summary>
public partial class PopupWindow : Window
{
    /// <summary>主对话区最多保留多少条，超出丢弃最早的。</summary>
    private const int MaxCards = 60;

    private readonly DispatcherTimer _topmostTimer;
    private DispatcherTimer? _autoCloseTimer;
    private readonly ObservableCollection<HistoryItem> _history = new();
    private readonly List<MessageCard> _cards = new();
    private readonly List<long> _pending = new();   // 还没点「关闭窗口」的消息
    private readonly HashSet<long> _seen = new();

    private bool _acknowledged;
    private bool _idle;

    /// <summary>关闭窗口时对每条未读消息各触发一次（参数是 message_id）。</summary>
    public event Action<long>? Acknowledged;
    public event Action<long>? RetryAck;
    /// <summary>用户点了回复：(昵称, 内容)。</summary>
    public event Action<string, string>? ReplyRequested;
    /// <summary>用户在弹窗里换了回复昵称。</summary>
    public event Action<string>? ReplyNameChanged;

    public PopupWindow()
    {
        InitializeComponent();
        HistoryList.ItemsSource = _history;

        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _topmostTimer.Tick += (_, _) =>
        {
            Topmost = false;
            Topmost = true;
            Activate();
        };
    }

    // ---------------- 对外接口 ----------------

    /// <summary>收到一条新消息：窗口没开就用历史铺满对话区，开着就往下追加。</summary>
    public void AppendMessage(long messageId, string senderName, string content,
                              string createdAt, int autoCloseSeconds,
                              IReadOnlyList<HistoryItem>? history)
    {
        var time = string.IsNullOrWhiteSpace(createdAt)
            ? DateTime.Now.ToString("HH:mm")
            : createdAt;

        var alreadySeen = messageId != 0 && _seen.Contains(messageId);

        if (!IsVisible)
        {
            SeedThread(history, messageId);
        }
        else if (!alreadySeen)
        {
            AddCard(messageId, senderName, content, time, isOut: false, animate: true);
        }

        if (messageId != 0)
        {
            _seen.Add(messageId);
            if (!_pending.Contains(messageId))
                _pending.Add(messageId);
        }

        UpdateBadge();

        if (history is not null)
            ReplaceHistory(history);

        _acknowledged = false;
        _idle = false;
        HintText.Text = autoCloseSeconds > 0
            ? $"{autoCloseSeconds} 秒后自动关闭 · Enter 回复"
            : "Enter 直接回复";

        EnsureShown();
        ArmAutoClose(autoCloseSeconds);
        ScrollToNewest();
    }

    /// <summary>从托盘打开：没有待处理消息，只看对话 + 回复。</summary>
    public void PresentIdle()
    {
        _acknowledged = true;
        _idle = true;
        _pending.Clear();
        UpdateBadge();
        HintText.Text = "Enter 直接回复";
        EnsureShown();
    }

    /// <summary>本机回复成功后，把这条也加进主对话区（靠右）。</summary>
    public void AppendOutgoing(string senderName, string content)
    {
        AddCard(0, senderName, content, DateTime.Now.ToString("HH:mm:ss"),
                isOut: true, animate: true);
        ScrollToNewest();
    }

    public void SetReplyNames(IReadOnlyList<string> names, string selected)
    {
        ReplyNameBox.SelectionChanged -= ReplyNameBox_SelectionChanged;
        ReplyNameBox.Items.Clear();
        foreach (var n in names)
            ReplyNameBox.Items.Add(n);

        if (!string.IsNullOrEmpty(selected) && Contains(names, selected))
            ReplyNameBox.SelectedItem = selected;
        else if (ReplyNameBox.Items.Count > 0)
            ReplyNameBox.SelectedIndex = 0;

        ReplyNameBox.SelectionChanged += ReplyNameBox_SelectionChanged;
    }

    private static bool Contains(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
                return true;
        }
        return false;
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

    // ---------------- 对话区 ----------------

    private void SeedThread(IReadOnlyList<HistoryItem>? history, long currentId)
    {
        MessageStack.Children.Clear();
        _cards.Clear();
        _seen.Clear();
        _pending.Clear();

        if (history is null || history.Count == 0)
            return;

        foreach (var h in history)
        {
            var card = new MessageCard(h.MessageId, h.SenderName, h.Content, h.Time, h.IsOut);
            if (h.MessageId != 0 && h.MessageId == currentId)
                card.PlayEnterAnimation(1.0);
            MessageStack.Children.Add(card);
            _cards.Add(card);
            if (h.MessageId != 0)
                _seen.Add(h.MessageId);
        }

        Trim();
        Restack(animate: false);
    }

    private void AddCard(long id, string senderName, string content, string time,
                         bool isOut, bool animate)
    {
        var card = new MessageCard(id, senderName, content, time, isOut);
        if (animate)
            card.PlayEnterAnimation(1.0);
        MessageStack.Children.Add(card);
        _cards.Add(card);
        Trim();
        Restack(animate);
    }

    private void Trim()
    {
        while (_cards.Count > MaxCards)
        {
            MessageStack.Children.Remove(_cards[0]);
            _cards.RemoveAt(0);
        }
    }

    /// <summary>重排层级：最新的最醒目，越旧越淡（由 MessageCard 自己算）。</summary>
    private void Restack(bool animate)
    {
        for (var i = 0; i < _cards.Count; i++)
        {
            var distance = _cards.Count - 1 - i;
            _cards[i].ApplyProminence(
                isNewest: distance == 0,
                distance: distance,
                animate: animate && distance > 0);
        }
    }

    // ---------------- 窗口 ----------------

    private void UpdateBadge()
    {
        var count = _pending.Count;
        CountText.Text = $"{count} 条新消息";
        CountBadge.Visibility = count >= 2 ? Visibility.Visible : Visibility.Collapsed;
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
            Topmost = true;
        }

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

    // ---------------- 回复 ----------------

    private void ReplyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SendReply();
        }
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => SendReply();

    private void ReplyNameBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReplyNameBox.SelectedItem is string name)
            ReplyNameChanged?.Invoke(name);
    }

    private void SendReply()
    {
        var text = (ReplyBox.Text ?? "").Trim();
        if (text.Length == 0)
            return;

        var who = ReplyNameBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(who))
            who = App.Config.ReplyName;

        ReplyBox.Clear();

        // 右侧弹幕 + 左侧对话区各留一条
        _history.Add(HistoryItem.Sent(who, text, DateTime.Now));
        ScrollHistoryToEnd();
        AppendOutgoing(who, text);

        HintText.Text = "发送中…";
        ReplyRequested?.Invoke(who, text);
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
        UpdateBadge();
        Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 不允许通过 Alt+F4 绕过；但系统关机/注销要放行
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
