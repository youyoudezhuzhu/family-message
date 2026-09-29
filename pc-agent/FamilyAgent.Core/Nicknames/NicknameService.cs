using System;
using System.Collections.Generic;
using System.Text;
using FamilyAgent.Core.Diagnostics;
using FamilyAgent.Core.Protocol;
using FamilyAgent.Core.Protocol.Frames;

namespace FamilyAgent.Core.Nicknames;

/// <summary>
/// 昵称帧的**直发**通道（docs/NICKNAME-SYSTEM-PLAN.md §5.4 的限制注）。
///
/// ★ 为什么不是直接吃 <c>ConnectionManager</c>：单测要能造出「离线」现场并
///   **断言一个帧都没发出去**（验收判据「离线期间不产生任何待发帧」），
///   而真的 <c>ConnectionManager</c> 里是一条 <c>ClientWebSocket</c> 连接循环，Linux 单测里造不出来。
///
/// ★ 与消息收发**共用同一条连接**，但**不共用 Outbox**：昵称帧走直发，
///   离线时**根本不构造请求** —— 否则一次「断网时被拒的改名」可能被 Outbox 在重连后
///   静默补发，直接违反 r6「完全拒绝离线改昵称」（§7 Phase 4 风险 ②）。
/// </summary>
public interface INicknameTransport
{
    /// <summary>此刻是否连着服务端。</summary>
    bool Connected { get; }

    /// <summary>
    /// 立刻发一个上行昵称帧。**不进离线队列、不落盘、不补发**。
    /// 返回 false = 当前没连接（调用方据此拒绝这次操作）。
    /// </summary>
    bool Send(string type, object payload);
}

/// <summary>
/// <see cref="INicknameTransport"/> 的真实现：转发给 Core 的 <c>ConnectionManager</c>。
///
/// 它只做一件事 —— 「有没有连接」与「直发一帧」，所有业务判断都在
/// <see cref="NicknameService"/> 里（那才是能被 Linux 单测覆盖的部分）。
/// </summary>
public sealed class ConnectionNicknameTransport : INicknameTransport
{
    private readonly Transport.ConnectionManager _connection;

    public ConnectionNicknameTransport(Transport.ConnectionManager connection) => _connection = connection;

    public bool Connected => _connection.Connected;

    public bool Send(string type, object payload) =>
        _connection.TrySendDirect(payload, type);      // 直发：不进 Outbox、不补发
}

/// <summary>一次昵称操作的结果（**不用异常**：界面要的是「为什么不能做」这句话）。</summary>
public sealed class NicknameOpResult
{
    private NicknameOpResult(bool accepted, bool sent, bool offline, string message, long? existingId)
    {
        Accepted = accepted;
        Sent = sent;
        Offline = offline;
        Message = message;
        ExistingNicknameId = existingId;
    }

    /// <summary>这次操作被接受了（本地校验过了、请求也发出去了）。</summary>
    public bool Accepted { get; }

    /// <summary>请求真的发出去了（<see cref="Accepted"/> 为 false 时恒为 false）。</summary>
    public bool Sent { get; }

    /// <summary>被拒的原因是不是「离线」——界面据此禁用入口并明示原因（§5.2）。</summary>
    public bool Offline { get; }

    /// <summary>给界面直接显示的中文说明。</summary>
    public string Message { get; }

    /// <summary>撞名时那个**既有昵称**的 id（引导「已存在，直接选用它？」）。</summary>
    public long? ExistingNicknameId { get; }

    /// <summary>离线拒绝（**没有发任何帧、没有落盘、没有排队**）。</summary>
    public static NicknameOpResult RejectOffline(string message) =>
        new(false, false, true, message, null);

    /// <summary>本地校验不通过（长度 / 空 / 控制字符）——同样一个帧都没发。</summary>
    public static NicknameOpResult Reject(string message) => new(false, false, false, message, null);

    /// <summary>请求已直发；结果等 <c>nickname_list_response</c>（整表校正）或 <c>nickname_error</c>。</summary>
    public static NicknameOpResult SentOk(string message) => new(true, true, false, message, null);
}

/// <summary>
/// 共享昵称在本机的**全部逻辑**（docs/NICKNAME-SYSTEM-PLAN.md §8.2 的落地）。
///
/// 三条硬口径（改之前先读 §5.2）
/// <list type="number">
///   <item><b>首次运行什么都不注册</b>：没有「PC 注册建行」这回事 ——
///     连上之后只发 <c>nickname_list_request</c> 拉整表，界面显示灰临时昵称
///     （本机名 + 逻辑色 <c>gray</c>），NAS 一行都不多（§3.3 / §3.5）。</item>
///   <item><b>四个管理操作（新建 / 改名 / 删除 / 重新分配颜色）只在线可用</b>：
///     离线时**直接拒绝**，不本地生效、不入队、不生成 pending、重连不补发（§5.2）。</item>
///   <item><b>只有「选择昵称」能离线</b>（<see cref="Select"/>）：它是纯本地状态，
///     一个字节都不上服务端（§5.2 上表）。</item>
/// </list>
///
/// 界面**永远不乐观地宣布成功**：管理操作只回报「请求已发出」，真正的成功判据是
/// 服务端回的那一帧（整表 <see cref="Table"/> 变了 / <see cref="LastError"/> 被填上）。
/// </summary>
public sealed class NicknameService
{
    /// <summary>离线时给用户的原话（§5.2 的推荐文案例）。</summary>
    public const string OfflineMessage = "当前未连接服务器，昵称管理不可用";

    /// <summary>本地临时昵称的说明文案（界面用）。</summary>
    public const string LocalTempHint = "本地临时昵称：还没选用共享昵称，消息里的颜色是灰色。";

    private readonly INicknameTransport _transport;
    private readonly INicknameStore _store;
    private readonly string _defaultName;
    private readonly object _gate = new();
    private readonly List<NicknameDto> _table = new();

    private NicknameState _current;
    private bool _available;
    private int _poolVersion;
    private IReadOnlyList<NicknameColorEntry> _colorTable = new List<NicknameColorEntry>();
    private string _notice = "";

    public NicknameService(INicknameTransport transport, INicknameStore store, string defaultName)
    {
        _transport = transport;
        _store = store;
        _defaultName = (defaultName ?? "").Trim();
        _current = store.Load(_defaultName);      // 没有文件 → 灰临时（首次运行就是这个）
    }

    /// <summary>整表 / 选用状态 / 可用性有任何变化（宿主据此推 <c>host.nickname</c>）。</summary>
    public event Action? Changed;

    /// <summary>活跃共享昵称的整表快照（按 <c>nickname_id</c> 升序，服务端保证）。</summary>
    public IReadOnlyList<NicknameDto> Table
    {
        get { lock (_gate) { return _table.ToArray(); } }
    }

    /// <summary>本机当前选用（灰临时 = <see cref="NicknameState.IsLocalTemp"/>）。</summary>
    public NicknameState Current
    {
        get { lock (_gate) { return _current; } }
    }

    /// <summary>本机名（灰临时昵称用它；PC 端 = <c>ComputerName</c>）。</summary>
    public string DefaultName => _defaultName;

    /// <summary>服务端是否**已经应答过**昵称帧（据此判断它有没有启用共享昵称）。</summary>
    public bool Available
    {
        get { lock (_gate) { return _available; } }
    }

    /// <summary>此刻能不能做管理操作（= 连接还在；§5.2 只认这一个条件）。</summary>
    public bool CanManage => _transport.Connected;

    public bool Online => _transport.Connected;

    /// <summary>服务端的逻辑色池版本（和本地映射表比对，落后只记日志）。</summary>
    public int PoolVersion
    {
        get { lock (_gate) { return _poolVersion; } }
    }

    /// <summary>活跃上限（= 色池大小 16；界面据此提示额度，§3.1）。</summary>
    public static int MaxActive => NicknameColor.MaxActiveNicknames;

    /// <summary>
    /// v0.19：服务端下发的**权威颜色表**（逻辑色 ID → 基础 HEX）。
    ///
    /// 空 = 还没拿到（宿主推给页面时，页面用内置表兜底）；整表帧每来一次就整份替换，
    /// 但**空表不覆盖** —— 服务端没带这个字段时保留手上那份（宁可用旧的，别把界面打回兜底色）。
    /// </summary>
    public IReadOnlyList<NicknameColorEntry> ColorTable
    {
        get { lock (_gate) { return _colorTable; } }
    }

    /// <summary>一次性的界面提示（如「你用的昵称已被删除，已切回本地临时昵称」）；读过即清。</summary>
    public string TakeNotice()
    {
        lock (_gate)
        {
            var n = _notice;
            _notice = "";
            return n;
        }
    }

    /// <summary>服务端最近一次 <c>nickname_error</c> 的内容（没有就是 null）。</summary>
    public NicknameErrorFrame? LastError { get; private set; }

    // ---------------- 连接生命周期 ----------------

    /// <summary>
    /// <c>hello</c> 之后调一次：**只拉整表**，不注册任何东西、不建行、不占色（§3.3 第 2 步）。
    /// 重连后同样只做这一件事 —— **没有待补发的改名**，也没有 <c>synced/pending</c> 状态机（§7 Phase 4）。
    /// </summary>
    public void OnConnected() => RequestList();

    /// <summary>主动拉一次整表（启动 / 重连 / 界面点「刷新」）。</summary>
    public bool RequestList() =>
        _transport.Send(FrameTypes.NicknameListRequest,
            new { type = FrameTypes.NicknameListRequest, });

    // ---------------- 收帧 ----------------

    /// <summary>
    /// 消费一帧昵称相关下行帧（由宿主把 <c>ConnectionManager.NicknameFrameReceived</c> 接过来）。
    /// 认识的帧返回 true；其它帧返回 false（调用方不必先判断）。
    /// </summary>
    public bool OnFrame(CoreFrame frame)
    {
        switch (frame)
        {
            case NicknameListFrame list:
                ApplyTable(list.Nicknames, list.PoolVersion, list.ColorTable);
                return true;

            // v0.19：颜色表被增删（只带版本号）→ 记下版本并重拉整表（整表帧里带权威色表）。
            // 不直接改渲染：等整表回来再一起换，避免「半张表」造成界面闪烁。
            case ColorTableChangedFrame tableChanged:
                lock (_gate) { _poolVersion = tableChanged.PoolVersion; }
                AgentLog.Write($"[NICK] 颜色表变了（v{tableChanged.PoolVersion}）→ 重拉整表");
                RequestList();
                return true;

            case NicknameDeltaFrame delta:
                ApplyDelta(delta);
                return true;

            case NicknameErrorFrame error:
                lock (_gate)
                {
                    _available = true;
                    LastError = error;
                }
                AgentLog.Write($"[NICK] 服务端拒绝 {error.Request}：{error.Code} {error.Message}");
                Raise();
                return true;

            default:
                return false;
        }
    }

    private void ApplyTable(IReadOnlyList<NicknameDto> rows, int poolVersion,
                            IReadOnlyList<NicknameColorEntry>? colorTable = null)
    {
        string? notice = null;
        var adopted = 0;
        lock (_gate)
        {
            _available = true;
            _table.Clear();
            foreach (var row in rows)
                _table.Add(row);
            _poolVersion = poolVersion;
            // v0.19：**空表不覆盖** —— 服务端没带 color_table 时保留手上那份（宁可用旧的，
            // 也别把界面打回兜底色；色表只在真拿到内容时才整份替换）
            if (colorTable is { Count: > 0 })
            {
                _colorTable = colorTable;
                adopted = colorTable.Count;
            }
            notice = ReconcileLocked();
        }

        AgentLog.Write($"[NICK] 整表校正：{rows.Count} 条活跃昵称，pool_version={poolVersion}，"
                     + $"色表={(adopted > 0 ? adopted + " 项" : "这一帧没带（保留原表）")}，"
                     + $"本机当前={Current}");
        Raise(notice);
    }

    private void ApplyDelta(NicknameDeltaFrame delta)
    {
        var dto = delta.Nickname;
        string? notice = null;
        lock (_gate)
        {
            _available = true;

            if (dto is null)
            {
                // 载荷缺 nickname 对象（不该发生）：不动表 —— 下一次整表校正会兜住。
            }
            else if (string.Equals(delta.Type, FrameTypes.NicknameRemoved, StringComparison.Ordinal))
            {
                _table.RemoveAll(n => n.NicknameId == dto.NicknameId);
                if (_current.NicknameId == dto.NicknameId)
                    notice = ReconcileLocked();       // 正在用它的人 → 清空选用 + 回退灰临时（§3.5）
            }
            else
            {
                var index = _table.FindIndex(n => n.NicknameId == dto.NicknameId);
                if (index >= 0)
                    _table[index] = dto;
                else
                    _table.Add(dto);

                // 本机正在用它 → 名字 / 颜色跟着变（历史消息**不**跟着变，那是快照，§3.2）
                if (_current.NicknameId == dto.NicknameId)
                {
                    _current = _current.With(dto.NicknameId, dto.DisplayName, dto.Color);
                    _store.Save(_current);
                }
            }
        }

        AgentLog.Write($"[NICK] 广播 {delta.Type}：{dto?.DisplayName ?? "(空载荷)"}");
        Raise(notice);
    }

    /// <summary>
    /// 校正「本机当前选用的那个还在不在」。
    ///
    /// ★ 判定**只看 <c>nickname_id</c>，绝不按 <c>display_name</c> 反查**（§3.4.1 的硬规则）：
    ///   删掉「妈妈」再重创一个「妈妈」是**新的一行**，按名字反查会把本机悄悄挂到新对象上。
    /// </summary>
    private string? ReconcileLocked()
    {
        if (_current.NicknameId is null)
            return null;

        var stillThere = _table.FindIndex(n => n.NicknameId == _current.NicknameId) >= 0;
        if (stillThere)
            return null;

        _current = NicknameState.LocalTemp(_defaultName);
        _store.Save(_current);
        _notice = "你用的昵称已被删除，已切回本地临时昵称。";
        return _notice;
    }

    // ---------------- 选择（**唯一能离线做的动作**）----------------

    /// <summary>
    /// **选择昵称**：我以后用谁的名义发消息。只改本机状态，一个帧都不发（§5.2）。
    /// <paramref name="nicknameId"/> 传 <c>null</c> = 回到灰临时昵称。
    /// </summary>
    public NicknameOpResult Select(long? nicknameId)
    {
        if (nicknameId is null)
        {
            SetCurrent(NicknameState.LocalTemp(_defaultName));
            return NicknameOpResult.SentOk("已切回本地临时昵称（只影响这台电脑）");
        }

        NicknameDto? hit;
        lock (_gate)
            hit = _table.Find(n => n.NicknameId == nicknameId.Value);

        if (hit is null)
            return NicknameOpResult.Reject("找不到这个昵称（可能已被删除）。请刷新列表后重试。");

        SetCurrent(NicknameState.FromN(hit));
        return NicknameOpResult.SentOk($"已选用「{hit.DisplayName}」（只影响这台电脑）");
    }

    private void SetCurrent(NicknameState next)
    {
        bool changed;
        lock (_gate)
        {
            changed = !next.SameAs(_current);
            _current = next;
        }

        if (!changed)
            return;

        _store.Save(next);         // 只有真的变了才落盘（没变 → 文件逐字节不变）
        AgentLog.Write($"[NICK] 本机选用已更新：{next}（纯本地，未通知服务端）");
        Raise();
    }

    // ---------------- 四个管理操作（**全部要求在线**）----------------

    /// <summary>新建共享昵称（方式 B）。撞名 → 服务端回 <c>NICKNAME_ALREADY_EXISTS</c> + 既有 id。</summary>
    public NicknameOpResult Create(string displayName)
    {
        var problem = ValidateName(displayName);
        if (problem is not null)
            return NicknameOpResult.Reject(problem);

        if (!_transport.Connected)
            return NicknameOpResult.RejectOffline(OfflineMessage);

        var name = Normalize(displayName);
        return Send(FrameTypes.NicknameCreateRequest, new
        {
            type = FrameTypes.NicknameCreateRequest,
            display_name = name,
        }, $"已请求新建「{name}」，等待服务端应答…");
    }

    /// <summary>改**共享昵称本身**的名字（**全局**：所有用它的人一起变）。必须在线。</summary>
    public NicknameOpResult Rename(long nicknameId, string displayName)
    {
        var problem = ValidateName(displayName);
        if (problem is not null)
            return NicknameOpResult.Reject(problem);

        if (!_transport.Connected)
            return NicknameOpResult.RejectOffline(OfflineMessage);

        var name = Normalize(displayName);
        return Send(FrameTypes.NicknameRenameRequest, new
        {
            type = FrameTypes.NicknameRenameRequest,
            nickname_id = nicknameId,
            display_name = name,
        }, $"已请求把该昵称改名为「{name}」（全局生效），等待服务端应答…");
    }

    /// <summary>重新分配颜色：服务端换一个**未被占用**的逻辑色 ID。必须在线。</summary>
    public NicknameOpResult ReassignColor(long nicknameId)
    {
        if (!_transport.Connected)
            return NicknameOpResult.RejectOffline(OfflineMessage);

        return Send(FrameTypes.NicknameReassignColorRequest, new
        {
            type = FrameTypes.NicknameReassignColorRequest,
            nickname_id = nicknameId,
        }, "已请求换一个颜色（全局生效），等待服务端应答…");
    }

    /// <summary>删除（软删；逻辑色 ID 立刻回池）。必须在线。</summary>
    public NicknameOpResult Delete(long nicknameId)
    {
        if (!_transport.Connected)
            return NicknameOpResult.RejectOffline(OfflineMessage);

        return Send(FrameTypes.NicknameDeleteRequest, new
        {
            type = FrameTypes.NicknameDeleteRequest,
            nickname_id = nicknameId,
        }, "已请求删除该昵称（全局生效），等待服务端应答…");
    }

    private NicknameOpResult Send(string type, object payload, string okMessage)
    {
        // 直发：不进 Outbox、不落盘、不补发（离线根本走不到这里）
        var sent = _transport.Send(type, payload);
        if (!sent)
            return NicknameOpResult.RejectOffline(OfflineMessage);

        AgentLog.Write($"[NICK] → {type}（直发，未入队）");
        return NicknameOpResult.SentOk(okMessage);
    }

    // ---------------- 校验（**32 个字符**，与 NAS / API / Web 同一上限）----------------

    /// <summary>
    /// 本地先校验一遍：**1–32 个字符**（按 Unicode 码点算，与 Python 的 <c>len()</c> 同口径）、
    /// 去掉首尾空白后不许为空、不许带控制字符。
    /// 返回错误文案；<c>null</c> = 通过。服务端仍会再校验一次（它才是权威）。
    /// </summary>
    public static string? ValidateName(string? raw)
    {
        var name = Normalize(raw);
        if (name.Length == 0)
            return "昵称不能为空";

        var count = CountCodePoints(name);
        if (count > MaxNameLength)
            return $"昵称最多 {MaxNameLength} 个字符（当前 {count} 个）";

        foreach (var ch in name)
        {
            if (char.IsControl(ch))
                return "昵称里不能有控制字符";
        }

        return null;
    }

    /// <summary>昵称长度上限（32）—— 与 NAS / API / Web 统一（§10 已定 13）。</summary>
    public const int MaxNameLength = 32;

    /// <summary>把名字规整到与服务端一致的形态：去掉首尾空白 + NFC（服务端 <c>normalize()</c> 同口径）。</summary>
    public static string Normalize(string? raw) =>
        (raw ?? "").Trim().Normalize(NormalizationForm.FormC);

    /// <summary>按 Unicode 码点数长度（emoji 算 1，与 Python 的 <c>len()</c> 一致）。</summary>
    private static int CountCodePoints(string text)
    {
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
            count++;
        return count;
    }

    private void Raise(string? notice = null)
    {
        if (!string.IsNullOrEmpty(notice))
            AgentLog.Write("[NICK] " + notice);
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            AgentLog.Write("[NICK] Changed 订阅者抛异常（已忽略）：" + ex.Message);
        }
    }
}
