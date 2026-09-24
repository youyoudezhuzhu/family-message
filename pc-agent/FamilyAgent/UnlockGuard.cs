using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace FamilyAgent;

/// <summary>一条 <c>unlock_request</c> 的校验结论，也就是要回给服务端的应答内容。</summary>
public sealed class UnlockReply
{
    public UnlockReply(string requestId, string status, string reason)
    {
        RequestId = requestId;
        Status = status;
        Reason = reason;
    }

    public string RequestId { get; }

    /// <summary>armed | success | failed</summary>
    public string Status { get; }

    /// <summary>expired | replay | not_mine | bad_action | no_credential | cp_error | timeout | ok</summary>
    public string Reason { get; }
}

/// <summary>
/// 一次性解锁请求的本地校验（远程解锁 Phase 1）。
///
/// 本阶段**不做真正的解锁**：凭据存储与 Credential Provider 都还没实现，
/// 这里只把「归属 → 动作 → 时效 → 一次性」这条校验链走完并给出应答，
/// 目的是让整条链路（下发 → 校验 → 应答 → 审计）可以先跑通、可验证。
///
/// ★ 一次性语义：任何带 <c>request_id</c> 的帧（**包括校验失败的**）都会立刻记进
///   重放缓存。这样拿同一个 id 反复试探，第二次必然是 <c>replay</c> —— 不会因为
///   第一次失败就获得无限次重试机会。
///
/// ★ 失败一律往安全侧倒：<c>expires_at</c> 缺失或解析不出来都按「已过期」处理。
/// </summary>
public static class UnlockGuard
{
    /// <summary>本机认识的解锁动作。</summary>
    public const string ActionUnlock = "device.unlock";

    // ── 应答码（与 NAS 侧冻结的协议一致，勿改字面量）──────────────────
    public const string StatusArmed = "armed";
    public const string StatusSuccess = "success";
    public const string StatusFailed = "failed";

    public const string ReasonExpired = "expired";
    public const string ReasonReplay = "replay";
    public const string ReasonNotMine = "not_mine";
    public const string ReasonBadAction = "bad_action";
    public const string ReasonNoCredential = "no_credential";
    public const string ReasonCpError = "cp_error";
    public const string ReasonTimeout = "timeout";
    public const string ReasonOk = "ok";

    /// <summary>重放缓存的保留时长。一次性令牌本身只活 30 秒，留 24 小时足够覆盖
    /// 「服务端重发同一 request_id」的场景，又不会让文件无限增长。</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>缓存条数上限（被反复试探时的兜底），超了丢最旧的。</summary>
    private const int MaxEntries = 500;

    private static readonly object Gate = new();
    private static Dictionary<string, DateTime>? _seen;   // request_id → 记录时刻（UTC）

    /// <summary>重放缓存文件：与 config.json 同目录 —— headless 与交互式实例
    /// 用的是同一个 <c>--config</c>，所以两边共享同一份缓存。</summary>
    private static string CachePath =>
        Path.Combine(Path.GetDirectoryName(AgentConfig.FilePath) ?? ".", "unlock_replay.json");

    /// <summary>
    /// 校验一条 <c>unlock_request</c>。
    ///
    /// 返回 null 表示这帧连 <c>request_id</c> 都没有 —— 没法应答，也没法记重放缓存，
    /// 调用方只能记日志。
    ///
    /// 校验顺序（与方案 §9 一致，不能换）：
    ///   ① device_id 是不是发给本机的？不是 → not_mine
    ///   ② action 是不是 device.unlock？不是 → bad_action
    ///   ③ 过期了吗？→ expired
    ///   ④ 这个 request_id 处理过吗？→ replay
    ///   ⑤ 全过 → 本阶段没有凭据 → failed / no_credential
    /// </summary>
    public static UnlockReply? Evaluate(JsonElement request, string? myDeviceId)
    {
        var requestId = ReadString(request, "request_id").Trim();
        if (requestId.Length == 0)
            return null;

        // ★ 先落缓存再校验：失败的帧也占用这个 request_id（防拿同一个 id 反复试探）
        var wasSeen = MarkSeen(requestId);

        var target = ReadString(request, "device_id").Trim();
        if (target.Length == 0 ||
            !string.Equals(target, (myDeviceId ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
        {
            AgentLog.Write($"✗ 解锁请求 {requestId} 不是发给本机（target={target}）→ not_mine");
            return new UnlockReply(requestId, StatusFailed, ReasonNotMine);
        }

        var action = ReadString(request, "action").Trim();
        if (!string.Equals(action, ActionUnlock, StringComparison.Ordinal))
        {
            AgentLog.Write($"✗ 解锁请求 {requestId} 动作不认识（action={action}）→ bad_action");
            return new UnlockReply(requestId, StatusFailed, ReasonBadAction);
        }

        if (IsExpired(ReadString(request, "expires_at")))
        {
            AgentLog.Write($"✗ 解锁请求 {requestId} 已过期（expires_at={ReadString(request, "expires_at")}）→ expired");
            return new UnlockReply(requestId, StatusFailed, ReasonExpired);
        }

        if (wasSeen)
        {
            AgentLog.Write($"✗ 解锁请求 {requestId} 重复使用 → replay");
            return new UnlockReply(requestId, StatusFailed, ReasonReplay);
        }

        // ⑤ 校验全过 —— 但凭据存储还没做，如实回 no_credential（不谎报 armed）。
        //    nonce 本阶段不参与校验（没有设备密钥对），只记进日志备查。
        AgentLog.Write($"解锁请求 {requestId} 校验通过（nonce={ReadString(request, "nonce")}）"
                     + "，但本机尚未配置解锁凭据 → no_credential");
        return new UnlockReply(requestId, StatusFailed, ReasonNoCredential);
    }

    /// <summary>把 request_id 记进重放缓存；返回「之前是否已经见过」。</summary>
    private static bool MarkSeen(string requestId)
    {
        bool already;
        Dictionary<string, DateTime> snapshot;

        lock (Gate)
        {
            var map = EnsureLoaded();
            already = map.ContainsKey(requestId);
            map[requestId] = DateTime.UtcNow;
            Prune(map);
            snapshot = new Dictionary<string, DateTime>(map, StringComparer.Ordinal);
        }

        Save(snapshot);
        return already;
    }

    /// <summary>过期条目清理 + 条数上限。调用方持有锁。</summary>
    private static void Prune(Dictionary<string, DateTime> map)
    {
        var cutoff = DateTime.UtcNow - Retention;

        var dead = new List<string>();
        foreach (var kv in map)
        {
            if (kv.Value < cutoff)
                dead.Add(kv.Key);
        }
        foreach (var key in dead)
            map.Remove(key);

        if (map.Count <= MaxEntries)
            return;

        var entries = new List<KeyValuePair<string, DateTime>>(map);
        entries.Sort((a, b) => a.Value.CompareTo(b.Value));
        for (var i = 0; i < entries.Count - MaxEntries; i++)
            map.Remove(entries[i].Key);
    }

    /// <summary>首次使用时从磁盘载入（重启不能忘掉用过哪些 request_id）。调用方持有锁。</summary>
    private static Dictionary<string, DateTime> EnsureLoaded()
    {
        if (_seen is not null)
            return _seen;

        var map = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(CachePath))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(CachePath));
                if (loaded is not null)
                {
                    foreach (var kv in loaded)
                    {
                        if (DateTime.TryParse(kv.Value, CultureInfo.InvariantCulture,
                                              DateTimeStyles.AdjustToUniversal |
                                              DateTimeStyles.AssumeUniversal, out var when))
                            map[kv.Key] = when.ToUniversalTime();
                    }
                }
                AgentLog.Write($"重放缓存已载入 {map.Count} 条");
            }
        }
        catch (Exception ex)
        {
            // 缓存文件坏了不能拦路：从空缓存开始。最坏情况是旧 request_id 的
            // 「一次性」语义丢一条，服务端的 used_at 还有一道保险。
            AgentLog.Write("重放缓存读取失败，从空缓存开始：" + ex.Message);
            map.Clear();
        }

        Prune(map);
        _seen = map;
        return map;
    }

    /// <summary>整份缓存落盘（先写临时文件再替换，避免写一半崩溃留下坏文件）。</summary>
    private static void Save(Dictionary<string, DateTime> snapshot)
    {
        try
        {
            var dir = Path.GetDirectoryName(CachePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var plain = new Dictionary<string, string>(snapshot.Count, StringComparer.Ordinal);
            foreach (var kv in snapshot)
                plain[kv.Key] = kv.Value.ToString("O", CultureInfo.InvariantCulture);

            var tmp = CachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(plain));
            File.Move(tmp, CachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // 落盘失败不影响本次判定：内存缓存仍然是权威的
            AgentLog.Write("重放缓存写入失败（本次判定不受影响）：" + ex.Message);
        }
    }

    /// <summary>expires_at 缺失 / 解析不出来 / 已到点 → 都算过期（失败往安全侧倒）。</summary>
    private static bool IsExpired(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        if (!DateTimeOffset.TryParse(raw.Trim(), CultureInfo.InvariantCulture,
                                     DateTimeStyles.None, out var expiresAt))
            return true;

        return expiresAt <= DateTimeOffset.UtcNow;
    }

    private static string ReadString(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return "";
        if (!root.TryGetProperty(name, out var el))
            return "";
        return el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
    }
}
