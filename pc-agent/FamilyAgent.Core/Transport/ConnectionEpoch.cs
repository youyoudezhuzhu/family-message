namespace FamilyAgent.Core.Transport;

/// <summary>
/// 连接代次（epoch）闸门 —— 把「谁有资格碰共享连接状态」这条规则收进一个地方，
/// 语义与原 <c>AgentClient</c> 里的 <c>_lifeGate</c> / <c>_epoch</c> /
/// <c>TryPublish</c> / <c>TryRetire</c> **逐字一致**（工作区那版修复，
/// 见 docs/CORE-REFACTOR-PLAN.md §2.7-①、§8.7）。
///
/// 背景（原注释保留）：全类**只有一条**连接循环。每次 <c>Start()</c> 让代次 +1，
/// 循环只认自己那一代 —— 于是「旧循环刚被停掉、新循环已经连上」的那段窗口里，
/// 旧循环既不会清空新连接的引用（原来是无条件 <c>_ws = null</c> 的竞态），
/// 也不会把已经连上的新连接报成「未连接」。
///
/// 为什么单独成类：这段判据是本项目最难复现的一类 bug（两个实例互踢、能收不能发）
/// 的根，而 <c>ClientWebSocket</c> 没法在 Linux 上伪造「Open」状态 ——
/// 抽成泛型闸门之后，<c>FamilyAgent.Core.Tests/ConnectionEpochTests</c> 才能在
/// ubuntu 的 CI 上直接验证「旧连接 dispose 不会污染新连接」。
/// </summary>
/// <typeparam name="TConn">连接对象类型（生产用 <c>ClientWebSocket</c>，单测用 <c>object</c>）。</typeparam>
public sealed class ConnectionEpoch<TConn> where TConn : class
{
    private readonly object _gate = new();
    private TConn? _connection;
    private string? _connectionId;
    private int _epoch;

    /// <summary>开一代：代次 +1 并返回它（<c>Start()</c> 时调用）。</summary>
    public int Begin()
    {
        lock (_gate)
        {
            _epoch++;
            return _epoch;
        }
    }

    /// <summary>本代是否仍是「当前那一代」（<c>Stop()</c>/<c>Start()</c> 之后旧循环即为过期）。</summary>
    public bool IsCurrent(int epoch)
    {
        lock (_gate)
        {
            return epoch == _epoch;
        }
    }

    /// <summary>
    /// 把连接挂成当前连接，并记下它的日志 id。
    /// 返回 false = 已被新的一代取代，这次连接该放弃。
    /// </summary>
    public bool TryPublish(int epoch, TConn connection, string connectionId)
    {
        lock (_gate)
        {
            if (epoch != _epoch)
                return false;
            _connection = connection;
            _connectionId = connectionId;
            return true;
        }
    }

    /// <summary>
    /// 退出时清空当前连接 —— ★ 只在自己仍是当前连接时才清。
    ///
    /// 原来是无条件 `_ws = null`：旧连接退出时新连接可能已经挂上了，这一下会把
    /// **新**连接的引用抹掉，现象就是日志里的「无连接对象，已入队等待补发」——
    /// 明明连着却发不出去。
    ///
    /// 返回 true 才表示「确实清掉了自己的连接」，调用方才该上报「未连接」。
    /// </summary>
    public bool TryRetire(int epoch, TConn connection)
    {
        lock (_gate)
        {
            if (epoch != _epoch || !ReferenceEquals(_connection, connection))
                return false;
            _connection = null;
            _connectionId = null;
            return true;
        }
    }

    /// <summary>当前连接对象；没有连接时为 null。</summary>
    public TConn? Current
    {
        get { lock (_gate) { return _connection; } }
    }

    /// <summary>当前连接的 id（完整 GUID 的 N 形式），日志与状态事件用它；没有连接时为 null。</summary>
    public string? CurrentConnectionId
    {
        get { lock (_gate) { return _connectionId; } }
    }

    /// <summary>
    /// 门里是否持有一条连接 —— 即「有没有当前连接对象」那一半判据
    /// （另一半是 socket 自身的状态，由 <c>ConnectionManager.Connected</c> 判）。
    /// 单测断言的就是它：旧代次退出不该把它变成 false。
    /// </summary>
    public bool HasConnection => Current is not null;
}
