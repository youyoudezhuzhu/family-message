using System;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FamilyAgent;

/// <summary>Agent 设置窗口：服务端地址、设备名、注册口令、本机回复昵称。</summary>
public partial class SettingsWindow : Window
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private bool _renderingNames;

    public SettingsWindow()
    {
        InitializeComponent();
        LoadFromConfig();
        RenderReplyNames();
        UpdateStatus(App.Client?.Connected ?? false,
                     App.Client?.Connected == true ? "已连接" : "未连接");
    }

    private void LoadFromConfig()
    {
        var cfg = App.Config;
        ServerBox.Text = cfg.ServerUrl;
        NameBox.Text = cfg.DeviceName;
        IdBox.Text = cfg.DeviceId;
        EnrollBox.Text = cfg.EnrollToken;
        AutoStartBox.IsChecked = cfg.AutoStart;
    }

    public void UpdateStatus(bool connected, string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateStatus(connected, text));
            return;
        }

        StatusDot.Fill = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(connected ? "#5DDBA0" : "#F2705E"));
        StatusText.Text = connected ? "已连接" : "未连接";
        if (!string.IsNullOrEmpty(text) && !connected)
            HintText.Text = text;
    }

    public void SetHint(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetHint(text));
            return;
        }
        HintText.Text = text;
    }

    private void CollectInto(AgentConfig cfg)
    {
        cfg.ServerUrl = ServerBox.Text.Trim();
        cfg.DeviceName = NameBox.Text.Trim();
        cfg.DeviceId = IdBox.Text.Trim();
        cfg.EnrollToken = EnrollBox.Text.Trim();
        cfg.AutoStart = AutoStartBox.IsChecked == true;
        cfg.Normalize();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        CollectInto(App.Config);
        App.Config.Save();
        AutoStart.Apply(App.Config.AutoStart);
        LoadFromConfig(); // 显示 Normalize 后的默认值
        App.Client.Restart();
        SetHint($"已保存到 {AgentConfig.FilePath}，正在重新连接…");
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        CollectInto(App.Config);
        var url = App.Config.ServerUrl.TrimEnd('/');
        if (url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            url = "http://" + url[5..];
        else if (url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url[6..];
        if (url.EndsWith("/ws"))
            url = url[..^3];

        SetHint("正在测试 " + url + "/healthz …");
        try
        {
            var body = await Http.GetStringAsync(url + "/healthz");
            SetHint("连接成功：" + body);
        }
        catch (Exception ex)
        {
            SetHint("连接失败：" + ex.Message);
        }
    }

    private void ConsoleButton_Click(object sender, RoutedEventArgs e)
    {
        var url = (ServerBox.Text ?? "").Trim().TrimEnd('/');
        if (url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            url = "http://" + url[5..];
        else if (url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url[6..];
        if (url.EndsWith("/ws"))
            url = url[..^3];

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetHint("打开浏览器失败：" + ex.Message);
        }
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void LogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AgentLog.FilePath}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetHint("打开日志失败：" + ex.Message + "（文件在 " + AgentLog.FilePath + "）");
        }
    }

    // ---------------- 回复昵称（本机维护）----------------

    private void RenderReplyNames()
    {
        if (_renderingNames)
            return;

        _renderingNames = true;
        try
        {
            ReplyNamesPanel.Children.Clear();

            foreach (var name in App.Config.ReplyNames)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };

                var del = new Button
                {
                    Content = "删除",
                    Padding = new Thickness(14, 6, 14, 6),
                    Tag = name,
                    Margin = new Thickness(8, 0, 0, 0),
                };
                del.Click += DeleteReplyName_Click;
                DockPanel.SetDock(del, Dock.Right);

                var box = new TextBox
                {
                    Text = name,
                    Tag = name,
                    Margin = new Thickness(0),
                };
                box.LostFocus += ReplyName_LostFocus;

                row.Children.Add(del);
                row.Children.Add(box);
                ReplyNamesPanel.Children.Add(row);
            }
        }
        finally
        {
            _renderingNames = false;
        }
    }

    private void AddReplyName_Click(object sender, RoutedEventArgs e)
    {
        var value = NewReplyNameBox.Text.Trim();
        if (value.Length == 0)
            return;

        if (!App.Config.ReplyNames.Contains(value))
            App.Config.ReplyNames.Add(value);

        NewReplyNameBox.Clear();
        App.Config.Normalize();
        App.Config.Save();
        RenderReplyNames();
        App.RefreshReplyNames();
        SetHint($"已添加昵称「{value}」，弹窗里可直接选用。");
    }

    private void DeleteReplyName_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string name)
            return;

        App.Config.ReplyNames.Remove(name);
        App.Config.Normalize();   // 保证至少留一个
        App.Config.Save();
        RenderReplyNames();
        App.RefreshReplyNames();
        SetHint($"已删除昵称「{name}」。");
    }

    private void ReplyName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_renderingNames)
            return;
        if (sender is not TextBox box || box.Tag is not string oldName)
            return;

        var value = box.Text.Trim();
        var index = App.Config.ReplyNames.IndexOf(oldName);
        if (index < 0)
            return;

        if (value.Length == 0 || App.Config.ReplyNames.Contains(value))
        {
            box.Text = oldName;   // 空值或重名，回滚
            return;
        }
        if (value == oldName)
            return;

        App.Config.ReplyNames[index] = value;
        if (App.Config.ReplyName == oldName)
            App.Config.ReplyName = value;
        App.Config.Save();
        RenderReplyNames();
        App.RefreshReplyNames();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 关窗口不退出程序，继续在托盘后台运行
        e.Cancel = true;
        Hide();
    }
}
