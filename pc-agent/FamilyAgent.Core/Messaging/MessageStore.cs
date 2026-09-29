using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FamilyAgent.Core.Messaging;

/// <summary>
/// 一条消息在本机的记账（框架无关：只存原始帧的字段 + 本地状态，不认识任何 UI）。
///
/// <see cref="Raw"/> 是服务端那一帧的原文副本 —— 重放（重启后把没显示过的消息
/// 重新投给界面）时按它回放，界面拿到的字段与实时路径**完全一致**
/// （<c>history</c> / <c>message_type</c> / <c>created_at</c> / <c>auto_close_seconds</c> /
/// <c>redelivered</c> 全在），不需要界面另写一套「历史数据长什么样」的解析。
/// </summary>
public sealed class MessageRecord
{
    /// <summary>服务端 <c>message_id</c>（SQLite INTEGER 自增）。**本地主键**：去重、重放、ACK 都按它对齐。</summary>
    public long MessageId { get; init; }

    public string SenderName { get; init; } = "";
    public string Content { get; init; } = "";
    public string MessageType { get; init; } = "";
    public string CreatedAt { get; init; } = "";

    /// <summary>服务端希望弹窗存活几秒（原样存，重放时继续生效 —— <c>auto_close_seconds</c> 不能因为重放就丢）。</summary>
    public int AutoCloseSeconds { get; init; }

    /// <summary>服务端补投的历史消息（原样存）。</summary>
    public bool Redelivered { get; init; }

    /// <summary>收到的原始帧（JSON 值等价；字段一个不少，见类注释）。</summary>
    public JsonElement Raw { get; init; }

    /// <summary>本地生命周期状态（<see cref="DeliveryState.AckSent"/> 不会出现在这里，见该枚举的注释）。</summary>
    public DeliveryState State { get; init; } = DeliveryState.Received;

    /// <summary>本机收到它的时刻（UTC；重放与超时判定都按它算）。</summary>
    public DateTime ReceivedAtUtc { get; init; }

    /// <summary>本条记账最后一次变化的时刻（UTC）。</summary>
    public DateTime UpdatedAtUtc { get; init; }

    /// <summary>原始帧的 JSON 文本（落盘用；<see cref="Raw"/> 未定义时为空串）。</summary>
    public string RawJson() =>
        Raw.ValueKind == JsonValueKind.Undefined ? "" : Raw.GetRawText();

    /// <summary>复制一份并换状态（不可变对象，避免两个线程看到半改的记录）。</summary>
    public MessageRecord With(DeliveryState state, DateTime utcNow, JsonElement? raw = null) => new()
    {
        MessageId = MessageId,
        SenderName = SenderName,
        Content = Content,
        MessageType = MessageType,
        CreatedAt = CreatedAt,
        AutoCloseSeconds = AutoCloseSeconds,
        Redelivered = Redelivered,
        Raw = raw ?? Raw,
        State = state,
        ReceivedAtUtc = ReceivedAtUtc,
        UpdatedAtUtc = utcNow,
    };

    /// <summary>从服务端 <c>message</c> 帧建一条新记账（<see cref="MessageManager"/> 用）。</summary>
    public static MessageRecord FromFrame(Protocol.Frames.MessageFrame frame, DateTime utcNow) => new()
    {
        MessageId = frame.MessageId,
        SenderName = frame.SenderName,
        Content = frame.Content,
        MessageType = frame.MessageType,
        CreatedAt = frame.CreatedAt,
        AutoCloseSeconds = frame.AutoCloseSeconds ?? 0,
        Redelivered = frame.Redelivered,
        Raw = frame.Raw,
        State = DeliveryState.Persisted,
        ReceivedAtUtc = utcNow,
        UpdatedAtUtc = utcNow,
    };
}

/// <summary>
/// 本地消息库（docs/CORE-REFACTOR-PLAN.md §3.2 / §8.3）。
///
/// 存在的意义：**消息只存在内存里 = 任何「不丢消息」的承诺都是假的**
/// （现状 <c>WebHostWindow._queued</c> 无上限、进程一退就没了）。它同时是
/// 「ACK 与 UI 解耦」的地基：消息先落盘，界面在不在都改变不了这一点。
/// </summary>
public interface IMessageStore
{
    /// <summary>按 <c>message_id</c> 插入或更新（后写覆盖先写）；返回落盘后的记账。</summary>
    MessageRecord Put(MessageRecord record);

    MessageRecord? Get(long messageId);

    bool Contains(long messageId);

    /// <summary>全部记账（按收到时间升序）。</summary>
    IReadOnlyList<MessageRecord> All();

    /// <summary>还没显示过的消息（<see cref="DeliveryStateExtensions.IsBeforeDisplay"/>），按收到时间升序 —— 界面就绪时的重放来源。</summary>
    IReadOnlyList<MessageRecord> PendingReplay();

    int Count { get; }

    /// <summary>给日志用的一句话（文件实现报路径，内存实现报 <c>memory</c>）。</summary>
    string Describe();

    /// <summary>按条数 / 天数裁剪（文件实现会重写文件）。</summary>
    void Compact();
}

/// <summary>
/// 默认实现：**单文件 JSONL**（每行一条 JSON，追加写）。
///
/// 为什么不用 SQLite：Core 是 net9.0 零依赖库，加 <c>Microsoft.Data.Sqlite</c>
/// 会引入原生库（云编译 / Linux CI / Android 各要一份），而这里的规模（几条到几百条、
/// 只在收到消息时写一行）用不上数据库。JSONL 的三个好处：
/// <list type="bullet">
///   <item>追加写，进程被杀也不会写出半个库（最多丢最后一行，读取时按行容错跳过）；</item>
///   <item>人可直接打开看（排查现场比二进制库省事）；</item>
///   <item>同一 <c>message_id</c> 后写覆盖先写 = 天然「upsert 日志」。</item>
/// </list>
///
/// 文件位置：<c>%APPDATA%\FamilyAgent\messages.jsonl</c>（与 <c>config.json</c> /
/// <c>agent.log</c> 同一目录）。构造时传路径可覆盖（单测用临时目录）。
///
/// 裁剪策略（最小实现，§6 Phase 2「风险」里要求的最小策略）：
/// 最多 <see cref="MaxRecords"/> 条、最多 <see cref="MaxAgeDays"/> 天、
/// 文件超过 <see cref="MaxFileBytes"/> 就重写一次（只留裁剪后的记录）。
/// </summary>
public sealed class JsonlMessageStore : IMessageStore
{
    /// <summary>最多留多少条（超出按收到时间丢最旧的）。够覆盖「网页端一次拉 50 条」好几个来回。</summary>
    public const int MaxRecords = 500;

    /// <summary>最多留多少天。</summary>
    public const int MaxAgeDays = 7;

    /// <summary>文件超过这个大小就重写（正文很长时靠它控制体积）。</summary>
    public const long MaxFileBytes = 4 * 1024 * 1024;

    /// <summary>落盘格式的 schema 序号（**不是**客户端版本号；改字段含义才 +1）。</summary>
    private const int Schema = 1;

    private readonly object _gate = new();
    private readonly Dictionary<long, MessageRecord> _records = new();
    private readonly string _path;

    /// <summary>用 <paramref name="filePath"/> 指定的文件；传空 = 用默认位置。</summary>
    public JsonlMessageStore(string? filePath = null)
    {
        _path = string.IsNullOrWhiteSpace(filePath) ? DefaultFilePath : filePath!;
        Load();
    }

    /// <summary>默认文件：<c>%APPDATA%\FamilyAgent\messages.jsonl</c>（与日志/配置同目录）。</summary>
    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FamilyAgent", "messages.jsonl");

    public string FilePath => _path;

    public int Count
    {
        get { lock (_gate) { return _records.Count; } }
    }

    public string Describe() => $"{_path}（{Count} 条）";

    public MessageRecord Put(MessageRecord record)
    {
        lock (_gate)
        {
            _records[record.MessageId] = record;
            Append(record);
            if (_records.Count > MaxRecords || FileLength() > MaxFileBytes)
                CompactLocked();
            return _records[record.MessageId];
        }
    }

    public MessageRecord? Get(long messageId)
    {
        lock (_gate)
        {
            return _records.TryGetValue(messageId, out var record) ? record : null;
        }
    }

    public bool Contains(long messageId)
    {
        lock (_gate) { return _records.ContainsKey(messageId); }
    }

    public IReadOnlyList<MessageRecord> All()
    {
        lock (_gate) { return Sorted(_records.Values); }
    }

    public IReadOnlyList<MessageRecord> PendingReplay()
    {
        lock (_gate)
        {
            var pending = new List<MessageRecord>();
            foreach (var record in _records.Values)
            {
                if (record.State.IsBeforeDisplay())
                    pending.Add(record);
            }
            return Sorted(pending);
        }
    }

    public void Compact()
    {
        lock (_gate) { CompactLocked(); }
    }

    // ---------------- 内部 ----------------

    private static List<MessageRecord> Sorted(IEnumerable<MessageRecord> source)
    {
        var list = new List<MessageRecord>(source);
        list.Sort((a, b) =>
        {
            var byTime = a.ReceivedAtUtc.CompareTo(b.ReceivedAtUtc);
            return byTime != 0 ? byTime : a.MessageId.CompareTo(b.MessageId);
        });
        return list;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;

            foreach (var line in File.ReadAllLines(_path, Encoding.UTF8))
            {
                var record = ParseLine(line);
                if (record is not null)
                    _records[record.MessageId] = record;   // 后写覆盖先写
            }
        }
        catch (Exception ex)
        {
            Diagnostics.AgentLog.Write($"[MSG] 读取本地消息库失败（当作空库继续）：{ex.Message}");
        }

        CompactLocked();
    }

    /// <summary>解析一行；坏行返回 null（**绝不因为一行坏了就让整个库读不出来**）。</summary>
    private static MessageRecord? ParseLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            var wire = JsonSerializer.Deserialize<WireRecord>(line!);
            if (wire is null || wire.MessageId <= 0)
                return null;

            var received = wire.ReceivedAtUtc == default ? DateTime.UtcNow : wire.ReceivedAtUtc;
            return new MessageRecord
            {
                MessageId = wire.MessageId,
                SenderName = wire.SenderName ?? "",
                Content = wire.Content ?? "",
                MessageType = wire.MessageType ?? "",
                CreatedAt = wire.CreatedAt ?? "",
                AutoCloseSeconds = wire.AutoCloseSeconds,
                Redelivered = wire.Redelivered,
                Raw = wire.Raw,
                State = DeliveryStateExtensions.ParseStoreValue(wire.State),
                ReceivedAtUtc = received,
                UpdatedAtUtc = wire.UpdatedAtUtc == default ? received : wire.UpdatedAtUtc,
            };
        }
        catch
        {
            return null;
        }
    }

    private void Append(MessageRecord record)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(WireRecord.From(record), WriteOptions);
            File.AppendAllText(_path, json + "\n", new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            // 落盘失败不能影响收消息：日志留痕，内存里那份仍然有效
            Diagnostics.AgentLog.Write($"[MSG] 写入本地消息库失败（内存记账仍有效）：{ex.Message}");
        }
    }

    private void CompactLocked()
    {
        var keep = Sorted(_records.Values);
        var cutoff = DateTime.UtcNow.AddDays(-MaxAgeDays);
        if (keep.Count > MaxRecords)
            keep.RemoveRange(0, keep.Count - MaxRecords);      // 丢最旧的
        keep.RemoveAll(r => r.ReceivedAtUtc < cutoff);          // 丢太旧的

        _records.Clear();
        foreach (var record in keep)
            _records[record.MessageId] = record;

        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            foreach (var record in keep)
                sb.Append(JsonSerializer.Serialize(WireRecord.From(record), WriteOptions)).Append('\n');

            // 先写临时文件再替换：中途失败时旧库还在
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Diagnostics.AgentLog.Write($"[MSG] 重写本地消息库失败（内存记账仍有效）：{ex.Message}");
        }
    }

    private long FileLength()
    {
        try
        {
            var info = new FileInfo(_path);
            return info.Exists ? info.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>落盘一行 JSON 的形状（字段名固定；<c>schema</c> 是文件格式序号，不是客户端版本）。</summary>
    private sealed class WireRecord
    {
        [JsonPropertyName("schema")] public int SchemaVersion { get; set; } = Schema;
        [JsonPropertyName("message_id")] public long MessageId { get; set; }
        [JsonPropertyName("sender_name")] public string? SenderName { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("message_type")] public string? MessageType { get; set; }
        [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
        [JsonPropertyName("auto_close_seconds")] public int AutoCloseSeconds { get; set; }
        [JsonPropertyName("redelivered")] public bool Redelivered { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("received_at")] public DateTime ReceivedAtUtc { get; set; }
        [JsonPropertyName("updated_at")] public DateTime UpdatedAtUtc { get; set; }
        [JsonPropertyName("raw")] public JsonElement Raw { get; set; }

        public static WireRecord From(MessageRecord record) => new()
        {
            SchemaVersion = Schema,
            MessageId = record.MessageId,
            SenderName = record.SenderName,
            Content = record.Content,
            MessageType = record.MessageType,
            CreatedAt = record.CreatedAt,
            AutoCloseSeconds = record.AutoCloseSeconds,
            Redelivered = record.Redelivered,
            State = record.State.ToStoreValue(),
            ReceivedAtUtc = record.ReceivedAtUtc,
            UpdatedAtUtc = record.UpdatedAtUtc,
            Raw = record.Raw,
        };
    }
}

/// <summary>
/// 纯内存实现：单测与「不想落盘」的宿主用（Android 将来也可以先用它跑通契约）。
/// 语义与 <see cref="JsonlMessageStore"/> 完全一致，只是不带文件。
/// </summary>
public sealed class MemoryMessageStore : IMessageStore
{
    private readonly object _gate = new();
    private readonly Dictionary<long, MessageRecord> _records = new();

    public int Count
    {
        get { lock (_gate) { return _records.Count; } }
    }

    public string Describe() => "memory";

    public MessageRecord Put(MessageRecord record)
    {
        lock (_gate)
        {
            _records[record.MessageId] = record;
            return record;
        }
    }

    public MessageRecord? Get(long messageId)
    {
        lock (_gate) { return _records.TryGetValue(messageId, out var r) ? r : null; }
    }

    public bool Contains(long messageId)
    {
        lock (_gate) { return _records.ContainsKey(messageId); }
    }

    public IReadOnlyList<MessageRecord> All()
    {
        lock (_gate) { return Sort(_records.Values); }
    }

    public IReadOnlyList<MessageRecord> PendingReplay()
    {
        lock (_gate)
        {
            var pending = new List<MessageRecord>();
            foreach (var record in _records.Values)
            {
                if (record.State.IsBeforeDisplay())
                    pending.Add(record);
            }
            return Sort(pending);
        }
    }

    public void Compact()
    {
        // 内存实现不裁剪（调用方要控量就自己负责）
    }

    private static List<MessageRecord> Sort(IEnumerable<MessageRecord> source)
    {
        var list = new List<MessageRecord>(source);
        list.Sort((a, b) =>
        {
            var byTime = a.ReceivedAtUtc.CompareTo(b.ReceivedAtUtc);
            return byTime != 0 ? byTime : a.MessageId.CompareTo(b.MessageId);
        });
        return list;
    }
}
