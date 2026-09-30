using System;
using System.IO;
using FamilyAgent.Core.Unlock;
using Xunit;

namespace FamilyAgent.Core.Tests;

/// <summary>
/// armed 凭证（一次性解锁指令）的行为判据。
///
/// 这些判据在 NAS 上就能跑（纯文件 + 注入时钟），所以"过期不算数 / 消费即失效 /
/// 坏文件当没有"不必拿真机登录界面去试 —— Phase 3 的安全性主要靠它们钉住。
/// </summary>
public sealed class UnlockArmingTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private DateTimeOffset _now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    public UnlockArmingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fm-arm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "unlock-arm.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 清理失败不影响判据 */ }
    }

    private UnlockArming Make() => new(_path, () => _now);

    [Fact]
    public void 写入后能读回且字段一致()
    {
        var a = Make();
        Assert.True(a.Arm("req-1", "nonce-abc", "PC-01\\jzh"));

        var rec = a.Read();
        Assert.NotNull(rec);
        Assert.Equal("req-1", rec!.RequestId);
        Assert.Equal("nonce-abc", rec.Nonce);
        Assert.Equal("PC-01\\jzh", rec.User);
        Assert.Equal(_now.ToUnixTimeSeconds() + ArmRecord.TtlSeconds, rec.ExpiresUnix);
        Assert.True(a.IsArmed);
    }

    [Fact]
    public void 文件格式与_C_侧口径一致_字段名不能悄悄改()
    {
        // C++ 侧（cp/src/FamilyAgentCp.cpp 的 ReadArm）按这几个 key 做极简解析：
        //   改字段名 = 同时改两边；这里钉住契约，免得"改了 C# 忘了 C++"。
        var a = Make();
        a.Arm("req-1", "nonce-abc", "user-x");
        var json = File.ReadAllText(_path);

        Assert.Contains("\"request_id\":", json);
        Assert.Contains("\"nonce\":", json);
        Assert.Contains("\"user\":", json);
        Assert.Contains("\"expires_unix\":", json);
        Assert.DoesNotContain("\n", json);          // 单行（极简解析不跨行）
    }

    [Fact]
    public void 过期后读不到_但未过期时读得到()
    {
        var a = Make();
        a.Arm("req-1", "n1", "u1", ttlSeconds: 10);

        _now = _now.AddSeconds(9);
        Assert.NotNull(a.Read());                    // 还没到

        _now = _now.AddSeconds(30);
        Assert.Null(a.Read());                       // 过期 = 当没有（忘了删也不会一直能被解锁）
        Assert.False(a.IsArmed);
    }

    [Fact]
    public void 时钟偏差五秒内仍认()
    {
        var a = Make();
        a.Arm("req-1", "n1", "u1", ttlSeconds: 0);    // 立刻到期
        _now = _now.AddSeconds(ArmRecord.ClockSkewSeconds);
        Assert.NotNull(a.Read());
        _now = _now.AddSeconds(ArmRecord.ClockSkewSeconds + 1);
        Assert.Null(a.Read());
    }

    [Fact]
    public void 消费即失效()
    {
        var a = Make();
        a.Arm("req-1", "n1", "u1");
        Assert.True(a.IsArmed);

        a.Disarm();                                   // CP 消费后 / 失败后都调它
        Assert.False(a.IsArmed);
        Assert.Null(a.Read());
        a.Disarm();                                   // 幂等：再删一次也不抛
    }

    [Fact]
    public void 坏文件与空字段一律当没有_且不抛()
    {
        File.WriteAllText(_path, "{ 这不是 JSON ");
        Assert.Null(Make().Read());

        File.WriteAllText(_path, "{\"request_id\":\"r\",\"user\":\"u\",\"expires_unix\":9999999999}");
        Assert.Null(Make().Read());                   // 缺 nonce ⇒ 不算 armed

        File.WriteAllText(_path, "");
        Assert.Null(Make().Read());

        var a = Make();
        File.Delete(_path);                           // 先把上面几个坏文件清掉，单看"拒绝写"这条
        Assert.False(a.Arm("r", "", "u"));            // 空 nonce 不许写
        Assert.False(a.Arm("r", "n", ""));            // 空 user 不许写
        Assert.False(a.Arm("req\"x", "n", "u"));      // 简单 JSON 不转义 ⇒ 含引号的值直接拒绝
        Assert.False(a.Arm("r", "n", "u\nv"));        // 换行同理
        Assert.False(a.IsArmed);
        Assert.False(File.Exists(_path));             // 拒绝时不留半成品文件
    }

    [Fact]
    public void 重复武装以最后一次为准()
    {
        var a = Make();
        a.Arm("req-1", "n1", "u1");
        a.Arm("req-2", "n2", "u2");

        var rec = a.Read();
        Assert.NotNull(rec);
        Assert.Equal("req-2", rec!.RequestId);        // 同一时刻只允许一次待解锁请求
        Assert.Equal("n2", rec.Nonce);
    }
}
