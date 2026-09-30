using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace FamilyAgent.Core.Unlock;

/// <summary>
/// armed 凭证（一次性解锁指令）—— PC 端 Guard 决定"武装"时写、CP 消费后删。
///
/// 放在 Core 而不是宿主：它只依赖文件与时间，于是"过期不算数 / 消费即失效 /
/// 手写的坏文件当没有"这些判据可以在这里用单测钉死（NAS 上 1 秒跑一轮），
/// 不用把真机登录界面当试验场。
///
/// ⚠ 文件格式必须与 C++ 侧（<c>cp/src/FamilyAgentCp.cpp</c> 的 <c>ReadArm</c>）**逐字对齐**：
///   <c>{"request_id":"…","nonce":"…","user":"…","expires_unix":1234567890}</c>
///   C++ 那边只做"找 key → 取引号串/整数"的极简解析，所以这里**不能**输出嵌套对象、
///   转义序列或数组。改字段名 = 同时改两边。
/// </summary>
public sealed record ArmRecord(string RequestId, string Nonce, string User, long ExpiresUnix)
{
    /// <summary>凭证有效期（秒）。CP 只在窗口内认它，过后当没有。</summary>
    public const int TtlSeconds = 120;

    /// <summary>允许的时钟偏差（秒）—— 写入端与消费端可能差几秒。</summary>
    public const int ClockSkewSeconds = 5;

    /// <summary>
    /// 序列化成**简单 JSON**（不转义）。与 C++ 侧的极简解析同一个约定：
    ///   · 值里**不许**出现 <c>"</c>、换行、回车 —— <see cref="UnlockArming.Arm"/> 会拒绝这类输入；
    ///   · 反斜杠**原样写**（`PC-01\jzh` 是常态，C++ 那边也是取到下一个引号为止）。
    /// 这样两边都不需要实现转义/反转义，格式契约最小、最不容易写岔。
    /// </summary>
    public string ToJson() =>
        $"{{\"request_id\":\"{RequestId}\",\"nonce\":\"{Nonce}\","
        + $"\"user\":\"{User}\",\"expires_unix\":{ExpiresUnix}}}";

    /// <summary>值里是不是含了会破坏"简单 JSON"的字符。</summary>
    internal static bool IsSimpleValue(string? s) =>
        s is null || (s.IndexOf('"') < 0 && s.IndexOf('\n') < 0 && s.IndexOf('\r') < 0);

    /// <summary>
    /// 极简解析（与 C++ 同口径）。坏文件 / 缺字段 → <c>null</c>，
    /// **绝不抛异常** —— 调用方是解锁链路，抛出去会把"没武装"变成"解锁流程崩了"。
    /// </summary>
    public static ArmRecord? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        var requestId = ReadString(json!, "request_id") ?? "";
        var nonce = ReadString(json!, "nonce");
        var user = ReadString(json!, "user");
        var expires = ReadLong(json!, "expires_unix");
        if (string.IsNullOrEmpty(nonce) || string.IsNullOrEmpty(user) || expires is null)
            return null;

        return new ArmRecord(requestId, nonce!, user!, expires.Value);
    }

    private static string ReadString(string json, string key)
    {
        var pat = "\"" + key + "\"";
        var p = json.IndexOf(pat, StringComparison.Ordinal);
        if (p < 0) return null;
        var c = json.IndexOf(':', p + pat.Length);
        if (c < 0) return null;
        var q1 = json.IndexOf('"', c + 1);
        if (q1 < 0) return null;
        var q2 = json.IndexOf('"', q1 + 1);
        if (q2 < 0) return null;
        return json.Substring(q1 + 1, q2 - q1 - 1);
    }

    private static long? ReadLong(string json, string key)
    {
        var pat = "\"" + key + "\"";
        var p = json.IndexOf(pat, StringComparison.Ordinal);
        if (p < 0) return null;
        var c = json.IndexOf(':', p + pat.Length);
        if (c < 0) return null;
        var i = c + 1;
        while (i < json.Length && (json[i] == ' ' || json[i] == '\t')) i++;
        var start = i;
        if (i < json.Length && (json[i] == '-' || json[i] == '+')) i++;
        while (i < json.Length && char.IsAsciiDigit(json[i])) i++;
        if (i == start) return null;
        return long.TryParse(json.AsSpan(start, i - start), NumberStyles.Integer,
                             CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}

/// <summary>
/// armed 凭证的读写（一次性语义）。
///
/// 三条硬规矩：
/// <list type="number">
///   <item><b>写就覆盖</b>：同一时刻只允许一次待解锁请求（旧的自然作废）；</item>
///   <item><b>读要新鲜</b>：过期的当没有（<see cref="Read"/> 返回 <c>null</c>）—— 这是
///         "忘了删也不会一直能被解锁"的兜底；</item>
///   <item><b>消费即删</b>：<see cref="Disarm"/> 在 CP 消费后、失败后、关闭功能时都调。</item>
/// </list>
/// </summary>
public sealed class UnlockArming
{
    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;

    /// <param name="path">凭证文件路径（真机 = <c>%ProgramData%\FamilyAgent\unlock-arm.json</c>）。</param>
    /// <param name="clock">时钟（测试注入用；默认 <see cref="DateTimeOffset.UtcNow"/>）。</param>
    public UnlockArming(string path, Func<DateTimeOffset>? clock = null)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string Path => _path;

    /// <summary>写一张凭证（覆盖旧的）。失败返回 false，不抛 —— 授权方不该因为磁盘问题崩。</summary>
    public bool Arm(string requestId, string nonce, string user, int ttlSeconds = ArmRecord.TtlSeconds)
    {
        if (string.IsNullOrWhiteSpace(nonce) || string.IsNullOrWhiteSpace(user))
            return false;
        // 简单 JSON 不许有引号/换行（两边都不实现转义），宁可拒绝也不写出坏文件
        if (!ArmRecord.IsSimpleValue(requestId) || !ArmRecord.IsSimpleValue(nonce)
            || !ArmRecord.IsSimpleValue(user))
            return false;

        try
        {
            var rec = new ArmRecord(requestId ?? "", nonce, user,
                                    _clock().ToUnixTimeSeconds() + ttlSeconds);
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_path, rec.ToJson(), new UTF8Encoding(false));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>读并校验新鲜度。没有 / 坏了 / 过期 → <c>null</c>。</summary>
    public ArmRecord? Read()
    {
        try
        {
            if (!File.Exists(_path))
                return null;
            var rec = ArmRecord.Parse(File.ReadAllText(_path));
            if (rec is null)
                return null;
            return rec.ExpiresUnix + ArmRecord.ClockSkewSeconds < _clock().ToUnixTimeSeconds()
                ? null
                : rec;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool IsArmed => Read() is not null;

    /// <summary>删掉凭证（消费 / 失败 / 关闭功能）。删不掉也无所谓：过期就自然失效。</summary>
    public void Disarm()
    {
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch (Exception)
        {
            // 交给过期兜底
        }
    }
}
