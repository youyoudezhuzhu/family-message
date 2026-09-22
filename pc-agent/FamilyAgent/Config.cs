using System;
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

    [JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ServerUrl) &&
        !string.IsNullOrWhiteSpace(DeviceId) &&
        !string.IsNullOrWhiteSpace(DeviceName);

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FamilyAgent");

    public static string FilePath => Path.Combine(Dir, "config.json");

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
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
    }
}
