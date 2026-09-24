using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FamilyAgent;

/// <summary>
/// Agent 本地配置。存在 %APPDATA%\FamilyAgent\config.json，
/// 首次运行时通过设置窗口填写，之后不再需要人工干预。
/// </summary>
public sealed class AgentConfig
{
    [JsonPropertyName("server_url")]
    public string ServerUrl { get; set; } = "http://192.168.31.50:18801";

    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("device_name")]
    public string DeviceName { get; set; } = "";

    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("enroll_token")]
    public string EnrollToken { get; set; } = "family-2026";

    [JsonPropertyName("auto_start")]
    public bool AutoStart { get; set; } = true;

    [JsonPropertyName("popup_auto_close_seconds")]
    public int PopupAutoCloseSeconds { get; set; }

    /// <summary>本机回复时可选的昵称列表（纯本地，不上传服务端）。</summary>
    [JsonPropertyName("reply_names")]
    public List<string> ReplyNames { get; set; } = new();

    /// <summary>当前选中的回复昵称。</summary>
    [JsonPropertyName("reply_name")]
    public string ReplyName { get; set; } = "";

    /// <summary>配色方案 id（纯本地；壳模式下界面在网页里，色调由页面自己决定）。</summary>
    [JsonPropertyName("theme")]
    public string ThemeId { get; set; } = "indigo";

    /// <summary>明暗模式：system / light / dark（纯本地，默认跟随系统）。</summary>
    [JsonPropertyName("theme_mode")]
    public string ThemeMode { get; set; } = "system";

    [JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ServerUrl) &&
        !string.IsNullOrWhiteSpace(DeviceId) &&
        !string.IsNullOrWhiteSpace(DeviceName);

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FamilyAgent");

    /// <summary>
    /// 覆盖配置文件路径。<c>--config</c> 传进来的值。
    ///
    /// 为什么需要：SYSTEM 身份的「开机」计划任务跑在会话 0，它的 %APPDATA%
    /// 不是用户那个，直接用默认路径会**注册成另一个设备**。所以任务参数里
    /// 显式带上用户那份配置的路径，保证登录前/后两个实例共享同一个 device_id。
    /// </summary>
    private static string? _overridePath;

    public static void UseConfigPath(string? path) =>
        _overridePath = string.IsNullOrWhiteSpace(path) ? null : path.Trim().Trim('"');

    public static string FilePath => _overridePath ?? Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AgentConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var cfg = JsonSerializer.Deserialize<AgentConfig>(json, Options);
                if (cfg is not null)
                {
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch
        {
            // 配置损坏时退回默认值，让用户重新填
        }

        var fresh = new AgentConfig();
        fresh.Normalize();
        return fresh;
    }

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(DeviceName))
            DeviceName = Environment.MachineName;
        if (string.IsNullOrWhiteSpace(DeviceId))
        {
            var raw = new string(Array.FindAll(
                Environment.MachineName.ToLowerInvariant().ToCharArray(),
                c => char.IsLetterOrDigit(c) || c == '-' || c == '_'));
            DeviceId = "pc_" + (raw.Length > 0 ? raw : "unknown");
        }

        // 回复昵称列表：至少有一个，默认用设备名
        ReplyNames ??= new List<string>();
        ReplyNames.RemoveAll(string.IsNullOrWhiteSpace);
        if (ReplyNames.Count == 0)
            ReplyNames.Add(DeviceName);
        if (string.IsNullOrWhiteSpace(ReplyName) || !ReplyNames.Contains(ReplyName))
            ReplyName = ReplyNames[0];

        for (var i = 0; i < ReplyNames.Count; i++)
            ReplyNames[i] = ReplyNames[i].Trim();

        // 配色 id / 明暗模式现在由网页端页面决定（壳模式下界面在 WebView2 里渲染），
        // 这里只做「别是空的」兜底：手改坏了配置也不至于起不来。
        if (string.IsNullOrWhiteSpace(ThemeId))
            ThemeId = "indigo";
        if (string.IsNullOrWhiteSpace(ThemeMode))
            ThemeMode = "system";
    }

    public void Save()
    {
        // 写到 FilePath 所在目录 —— 用了 --config 时不能再去建 %APPDATA%
        var path = FilePath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }
}
