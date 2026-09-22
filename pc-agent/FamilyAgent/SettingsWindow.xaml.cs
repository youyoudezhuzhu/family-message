using System;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;

namespace FamilyAgent;

/// <summary>Agent 设置窗口：填服务端地址、设备名、注册口令，并显示连接状态。</summary>
public partial class SettingsWindow : Window
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public SettingsWindow()
    {
        InitializeComponent();
        LoadFromConfig();
        UpdateStatus(App.Client?.Connected ?? false, App.Client?.Connected == true ? "已连接" : "未连接");
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

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 关窗口不退出程序，继续在托盘后台运行
        e.Cancel = true;
        Hide();
    }
}
