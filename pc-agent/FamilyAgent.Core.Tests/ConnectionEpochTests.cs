using FamilyAgent.Core.Transport;
using Xunit;

namespace FamilyAgent.Core.Tests;

/// <summary>
/// 连接代次闸门（epoch）的行为 —— docs/CORE-REFACTOR-PLAN.md Phase 1 验收标准里
/// 点名的那条单测。
///
/// 被测的场景就是这个仓库真踩过的竞态（§2.7-①）：
/// **旧连接正要退出、新连接已经连上**。原来的写法在旧连接的 finally 里无条件
/// <c>_ws = null</c>，于是：① 新连接的引用被抹掉 → 日志里「无连接对象，已入队等待补发」
/// （明明连着却发不出去）；② 顺手把已经连上的新连接报成「未连接」。
///
/// 为什么用泛型闸门（TConn = object）而不是真的 ClientWebSocket：
/// Linux 上没办法把 ClientWebSocket 弄成 Open 状态（没法只握手不连服务端），
/// 而这条判据与 socket 状态无关 —— 它比的只有**代次**与**连接对象身份**。
/// </summary>
public class ConnectionEpochTests
{
    /// <summary>★ 验收点：旧代次退出不得清掉新连接，也不得报出假的「未连接」。</summary>
    [Fact]
    public void StaleRetire_DoesNotClearNewConnection()
    {
        var epoch = new ConnectionEpoch<object>();
        var oldConnection = new object();
        var newConnection = new object();

        // 第一代连上（Start → TryPublish）
        var first = epoch.Begin();
        Assert.True(epoch.TryPublish(first, oldConnection, "conn-old"));
        Assert.True(epoch.HasConnection);

        // Stop() + Start()：新的一代起来，新连接挂上
        var second = epoch.Begin();
        Assert.True(epoch.TryPublish(second, newConnection, "conn-new"));
        Assert.NotEqual(first, second);

        // 旧循环的 finally 这时才跑（真实时序：旧连接收尾比新连接建立慢）
        var retired = epoch.TryRetire(first, oldConnection);

        // 返回 false = 调用方**不该**上报 ConnectionStateChanged(Disconnected)
        Assert.False(retired);
        // 而且门里仍然持着新连接 —— 等价于原来那句「IsConnected 仍为 true」
        Assert.True(epoch.HasConnection);
        Assert.Same(newConnection, epoch.Current);
        Assert.Equal("conn-new", epoch.CurrentConnectionId);
    }

    /// <summary>反面对照：自己那一代的正常退出仍然要清干净（否则会漏报「未连接」）。</summary>
    [Fact]
    public void OwnRetire_DoesClearOwnConnection()
    {
        var epoch = new ConnectionEpoch<object>();
        var connection = new object();

        var generation = epoch.Begin();
        Assert.True(epoch.TryPublish(generation, connection, "conn-1"));

        Assert.True(epoch.TryRetire(generation, connection));
        Assert.False(epoch.HasConnection);
        Assert.Null(epoch.Current);
        Assert.Null(epoch.CurrentConnectionId);
    }

    /// <summary>过期代次连上来的连接不许挂进门里（Stop 之后紧接着 Start 的那段窗口）。</summary>
    [Fact]
    public void StalePublish_IsRejected()
    {
        var epoch = new ConnectionEpoch<object>();
        var stale = new object();

        var first = epoch.Begin();
        epoch.Begin();      // 新的一代接管

        Assert.False(epoch.TryPublish(first, stale, "conn-stale"));
        Assert.False(epoch.HasConnection);
    }

    /// <summary>同一代里换了连接对象（理论上不会发生）：不认识的连接不许把它清掉。</summary>
    [Fact]
    public void Retire_OfUnknownConnection_IsRejected()
    {
        var epoch = new ConnectionEpoch<object>();
        var connection = new object();
        var other = new object();

        var generation = epoch.Begin();
        Assert.True(epoch.TryPublish(generation, connection, "conn-1"));

        Assert.False(epoch.TryRetire(generation, other));
        Assert.Same(connection, epoch.Current);
    }
}
