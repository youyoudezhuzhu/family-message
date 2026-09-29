using System;
using System.IO;
using System.Text.Json;

namespace FamilyAgent.Core.Nicknames;

/// <summary>
/// 本机昵称状态的落盘点（docs/NICKNAME-SYSTEM-PLAN.md §8.2）。
///
/// ★ **独立文件 <c>nickname.json</c>，不塞进 <c>AgentConfig</c>**：
///   昵称缓存是**运行时状态**（整表 + 本机选用），不是连接参数；
///   塞进配置会让「配置损坏 → 回默认」那条路径（<c>AgentConfig.Load()</c> 的 catch）
///   把它悄悄清掉。所以 <c>AgentConfig</c> **一个字段都没加**。
///
/// ★ 文件里**只有 3 个字段**：<c>nickname_id</c>（可空）/ <c>nickname_display_name</c> /
///   <c>nickname_color</c>（**逻辑色 ID**）。
///   **不存在 <c>sync_state</c> / <c>pending</c> 任何痕迹** —— r6 把离线 pending 昵称同步整段删了。
/// </summary>
public interface INicknameStore
{
    /// <summary>读上一次的选用状态；没有文件 / 文件损坏 / 字段非法一律回到灰临时（不抛异常）。</summary>
    NicknameState Load(string defaultName);

    /// <summary>落盘。**只在状态真的变了时调用**（没变就不写 → 文件逐字节不变）。</summary>
    void Save(NicknameState state);
}

/// <summary>
/// 落 <c>%APPDATA%\FamilyAgent\nickname.json</c>（与 <c>config.json</c> 同目录）。
///
/// 字段名与 <c>AgentConfig</c> 一样用 snake_case 的 <c>[JsonPropertyName]</c> 风格，
/// 手改文件时和 <c>config.json</c> 观感一致。
/// </summary>
public sealed class JsonNicknameStore : INicknameStore
{
    /// <summary>覆盖文件路径（单测用）。为 null 时用 <see cref="DefaultPath"/>。</summary>
    private readonly string? _overridePath;

    public JsonNicknameStore(string? path = null) => _overridePath = path;

    /// <summary>默认路径：<c>&lt;config.json 所在目录&gt;\nickname.json</c>。</summary>
    public static string DefaultPath
    {
        get
        {
            var dir = Path.GetDirectoryName(Config.AgentConfig.FilePath) ?? ".";
            return Path.Combine(dir, "nickname.json");
        }
    }

    public string FilePath => _overridePath ?? DefaultPath;

    public NicknameState Load(string defaultName)
    {
        var fallback = NicknameState.LocalTemp(defaultName);
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
                return fallback;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return fallback;

            long? id = null;
            if (root.TryGetProperty("nickname_id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                && idEl.TryGetInt64(out var parsed) && parsed > 0)
            {
                id = parsed;
            }

            var name = ReadString(root, "nickname_display_name");
            var color = ReadString(root, "nickname_color");
            if (string.IsNullOrEmpty(color))
                color = NicknameColor.LocalTempColorId;

            // 没选（id 为空）就一定是灰临时：名字与颜色都按本地临时算，
            // 免得「上次留下的共享昵称名字」被当成没选时的显示名。
            return id is null
                ? NicknameState.LocalTemp(string.IsNullOrWhiteSpace(name) ? defaultName : name)
                : new NicknameState { NicknameId = id, DisplayName = name, Color = color };
        }
        catch
        {
            // 文件坏了就当没选 —— 与 AgentConfig.Load() 的「回默认让用户重来」同口径。
            return fallback;
        }
    }

    public void Save(NicknameState state)
    {
        var path = FilePath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // 逐字段写死：**只有这三个**，不加 sync_state（r6）。
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            if (state.NicknameId is null)
                writer.WriteNull("nickname_id");
            else
                writer.WriteNumber("nickname_id", state.NicknameId.Value);
            writer.WriteString("nickname_display_name", state.DisplayName ?? "");
            writer.WriteString("nickname_color", string.IsNullOrEmpty(state.Color)
                ? NicknameColor.LocalTempColorId : state.Color);
            writer.WriteEndObject();
        }

        File.WriteAllBytes(path, stream.ToArray());
    }

    private static string ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? ""
            : "";
}

/// <summary>内存实现（单测用）：同时记下写了多少次，方便断言「离线时一个字节都没动」。</summary>
public sealed class MemoryNicknameStore : INicknameStore
{
    private readonly string _defaultName;

    public MemoryNicknameStore(string defaultName) => _defaultName = defaultName;

    /// <summary>当前落盘内容（内存版本）。</summary>
    public NicknameState? Saved { get; private set; }

    /// <summary>写盘次数 —— 验收判据「本地文件逐字节不变」就是它 == 0。</summary>
    public int SaveCount { get; private set; }

    public NicknameState Load(string defaultName) =>
        Saved ?? NicknameState.LocalTemp(string.IsNullOrEmpty(defaultName) ? _defaultName : defaultName);

    public void Save(NicknameState state)
    {
        Saved = state;
        SaveCount++;
    }
}
