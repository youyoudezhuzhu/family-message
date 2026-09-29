using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using FamilyAgent.Core.Nicknames;
using FamilyAgent.Core.Protocol;
using FamilyAgent.Core.Protocol.Frames;
using Xunit;

namespace FamilyAgent.Core.Tests;

/// <summary>
/// PC 端「昵称操作的 pending 状态机」契约 —— 两个真机 bug 的防回归钉子。
///
/// 背景（真机复现，docs 里记着全套原始输出）：
/// <list type="number">
///   <item><b>popup 形态选昵称没反应</b>：<c>web/shell/pc.js</c> 的 <c>onSenderChange</c>
///     写死读 <c>#client-sender</c>，而同一个 handler 同时挂在 client / popup 两条下拉上 →
///     全屏弹窗里选的新昵称被忽略，发出去的还是旧 <c>nickname_id</c>。</item>
///   <item><b>改名后提示永远停在「等待服务端应答…」</b>：<c>onNicknameResult</c> 收到
///     「请求被接受」的回执时就 <c>clearTimeout</c> 掉了看门狗，而**成功一路上没有任何
///     文案落地** → 即便服务端真的改成功了（其它端也同步了），PC 端提示永远不终结。</item>
/// </list>
///
/// ⚠ 为什么这个文件在 <b>Core.Tests</b> 里读 JS 源码：pending 状态机的宿主是页面
///   （<c>web/shell/pc.js</c>），C# 侧没有等价的运行时对象，而 WPF/WebView2 壳在这台
///   开发机（Linux）和 CI 上**都跑不起来**（Core.Tests 的存在本身就为这件事，见
///   docs/CORE-REFACTOR-PLAN.md §8.8）。所以这里做两件事：
///     · 逻辑层：四个管理操作**无论在线还是离线，都必然给出一条终结性答复**
///       （accepted / rejected + 原因）—— 页面据此才可能终结 pending；
///     · 源码层：钉住 pc.js 里那几处「会挂死」的写法，防止被改回去。
///   真正的行为验证（真浏览器 + 真桥）在 <c>tools/test_pc_sender_nickname.py</c>。
/// </summary>
public class PcShellNicknamePendingTests
{
    // ---------------- 逻辑层：每个操作都必须给出终结性答复 ----------------

    /// <summary>在线：四个操作各发恰好一个帧，并回 accepted（不是 offline）—— 页面能进入「等结果」。</summary>
    [Fact]
    public void Online_EveryManagementOp_GivesTerminalAccepted()
    {
        foreach (var (kind, result) in RunAll(new FakeNicknameTransport { Connected = true }))
        {
            Assert.True(result.Accepted, $"{kind} 在线时应被接受");
            Assert.False(result.Offline, $"{kind} 在线时不该是离线拒绝");
            Assert.False(string.IsNullOrWhiteSpace(result.Message), $"{kind} 必须给页面一句可显示的文案");
        }
    }

    /// <summary>
    /// 离线：四个操作一律拒绝并给出中文原因，且**一个帧都不发** ——
    /// 页面收到 rejected 回执必须立刻终结 pending（不能挂着等一个永远不来的结果）。
    /// </summary>
    [Fact]
    public void Offline_EveryManagementOp_GivesTerminalRejection_WithoutSending()
    {
        var transport = new FakeNicknameTransport { Connected = false };
        var sent = new List<string>();
        var (service, _) = New(transport);

        var results = new (string Kind, NicknameOpResult Result)[]
        {
            ("create", service.Create("新名字")),
            ("rename", service.Rename(2, "改名后")),
            ("reassign_color", service.ReassignColor(2)),
            ("delete", service.Delete(2)),
        };

        foreach (var (kind, result) in results)
        {
            Assert.False(result.Accepted, $"{kind} 离线时必须被拒");
            Assert.True(result.Offline, $"{kind} 离线时必须标 offline");
            Assert.False(string.IsNullOrWhiteSpace(result.Message), $"{kind} 离线时必须说明原因");
        }
        Assert.Empty(transport.Sent);       // 离线改昵称：完全拒绝（§5.2），不排队不补发
        Assert.Empty(sent);
    }

    /// <summary>改名的失败帧必须能被宿主拿到（LastError + Changed）→ 宿主才推得出 host.nickname_error。</summary>
    [Fact]
    public void RenameConflict_ErrorMessageIsSurfacedToHost()
    {
        var (service, transport) = New(new FakeNicknameTransport { Connected = true });
        var changed = 0;
        service.Changed += () => changed++;

        service.Rename(2, "爸爸");          // 请求发出（帧已上路）
        Assert.Single(transport.Sent);

        changed = 0;
        service.OnFrame(FrameCodec.Parse(
            NicknameJson.Error("NAME_TAKEN", "nickname_rename_request", "「爸爸」已被占用，请换个名字或直接选用它"))
            .Frame!);

        Assert.NotNull(service.LastError);
        Assert.Equal("NAME_TAKEN", service.LastError!.Code);
        Assert.Equal("nickname_rename_request", service.LastError.Request);
        Assert.False(string.IsNullOrWhiteSpace(service.LastError.Message));
        Assert.True(changed >= 1, "失败也要抛 Changed，宿主才能把错误推回页面（不然页面永远等不到结果）");
    }

    /// <summary>改名成功后宿主能拿到新表（→ 推 host.nickname → 页面 resolveNickPending 判成功）。</summary>
    [Fact]
    public void RenameSuccess_UpdatesTable_SoPageCanResolve()
    {
        var (service, _) = New(new FakeNicknameTransport { Connected = true });
        service.OnFrame(FrameCodec.Parse(NicknameJson.List(NicknameJson.Row(2, "妈妈", "color_13"))).Frame!);
        service.Select(2);

        var changed = 0;
        service.Changed += () => changed++;
        service.OnFrame(FrameCodec.Parse(
            NicknameJson.Delta(FrameTypes.NicknameUpdated, 2, "用户6", "color_13")).Frame!);

        Assert.Equal("用户6", service.Current.DisplayName);        // 正在用它的人跟着改
        Assert.Equal(2L, service.Current.NicknameId);
        Assert.True(changed >= 1);
        Assert.Null(service.LastError);
    }

    // ---------------- 源码层：钉住 pc.js 的 pending 生命周期 ----------------

    private static string PcJs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "web", "shell", "pc.js");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "找不到 web/shell/pc.js（本测试要在仓库树里跑）。起点=" + AppContext.BaseDirectory);
    }

    /// <summary>取出 <c>function name(...) { ... }</c> 的函数体（按花括号配对，忽略字符串里的括号）。</summary>
    private static string BodyOf(string js, string name)
    {
        var start = Regex.Match(js, @"function\s+" + Regex.Escape(name) + @"\s*\(");
        Assert.True(start.Success, $"pc.js 里找不到函数 {name}（被改名了？测试要跟着改）");

        var open = js.IndexOf('{', start.Index);
        Assert.True(open > 0, $"{name} 后面没找到函数体");

        var depth = 0;
        for (var i = open; i < js.Length; i++)
        {
            var c = js[i];
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return js.Substring(open + 1, i - open - 1);
            }
        }
        throw new InvalidOperationException($"pc.js: {name} 的花括号不配对");
    }

    /// <summary>
    /// ★ bug ②（popup 选昵称）的钉子：onSenderChange 必须**按事件来源取值**，
    /// 不能再写死读 client 那条下拉。
    /// </summary>
    [Fact]
    public void PcJs_SenderChange_TakesValueFromEventSource()
    {
        var js = PcJs();
        var body = BodyOf(js, "onSenderChange");

        Assert.Contains("senderBarOf(ev)", body);
        Assert.DoesNotContain("BARS.client.sender", body);

        // 事件来源 → 回复栏的映射函数本身也要在，并且认识 popup 那条
        var map = BodyOf(js, "senderBarOf");
        Assert.Contains("BARS[k].sender === t.id", map);
        Assert.Contains("'popup'", map);

        // 两个下拉 + 身份 + 气泡的本地乐观写回：不能只回写一个
        var local = BodyOf(js, "applyNickSelectionLocally");
        Assert.Contains("Object.keys(BARS)", local);
        Assert.Contains("syncNickIdentity()", local);
        Assert.Contains("applyNickSelectionLocally(v)", body);
    }

    /// <summary>
    /// ★ bug ①（改名 pending 永远挂着）的钉子：accepted 回执**绝不能**清掉看门狗。
    /// </summary>
    [Fact]
    public void PcJs_AcceptedAck_MustNotClearPending()
    {
        var js = PcJs();
        var body = BodyOf(js, "onNicknameResult");

        Assert.DoesNotContain("clearTimeout(S.nickTimer)", body);
        Assert.Contains("endNickOp()", body);              // 被拒（离线 / 校验不过）也要收尾
        Assert.Contains("accepted", body);
    }

    /// <summary>权威整份状态到了 → 页面必须判定 pending 的成败并落文案（否则中途态不走）。</summary>
    [Fact]
    public void PcJs_AuthoritativeState_ResolvesPending()
    {
        var js = PcJs();
        Assert.Contains("resolveNickPending()", BodyOf(js, "onNickname"));

        var resolve = BodyOf(js, "resolveNickPending");
        Assert.Contains("endNickOp()", resolve);           // 成功也必须收尾（清 pending + 停看门狗）
        Assert.Contains("rename", resolve);                // 改名有专门的判定
        Assert.Contains("nickHint", resolve);              // 成功要**看得见**
    }

    /// <summary>服务端拒绝 → 页面必须收尾 pending，并把原因显示出来（撞名不能静默）。</summary>
    [Fact]
    public void PcJs_ServerError_EndsPendingAndShowsReason()
    {
        var js = PcJs();
        var body = BodyOf(js, "onNicknameError");

        Assert.Contains("endNickOp()", body);
        Assert.Contains("nickHint(", body);
        Assert.Contains("message", body);

        // 四个管理操作（含改名）都记 pending
        var begin = BodyOf(js, "beginNickOp");
        Assert.Contains("S.nickPending", begin);
        Assert.Contains("setTimeout", begin);              // 看门狗：绝不永远挂着
        Assert.Contains("NICK_OP_TIMEOUT", begin);

        // 看门狗文案必须说「没等到应答」，而不是宿主那句「等待服务端应答…」
        Assert.Contains("还没收到服务端的应答", begin);
    }

    /// <summary>四个管理操作都走 opNick（= 都会登记 pending），一个都不能漏。</summary>
    [Fact]
    public void PcJs_AllFourOps_GoThroughOpNick()
    {
        var js = PcJs();
        Assert.Contains("beginNickOp(kind, msg)", BodyOf(js, "opNick"));
        foreach (var kind in new[] { "create", "rename", "reassign_color", "delete" })
            Assert.Contains("opNick('" + kind + "'", js);
    }

    /// <summary>
    /// 失败文案三形态都要看得见：设置页那行在 client / popup 形态下是隐藏的，
    /// 所以 nickHint 必须同时写到当前形态的那一行。
    /// </summary>
    [Fact]
    public void PcJs_NickHint_IsVisibleInCurrentView()
    {
        var body = BodyOf(PcJs(), "nickHint");
        Assert.Contains("settings-nick-hint", body);
        Assert.Contains("S.view !== 'settings'", body);
        Assert.Contains("BARS[S.view]", body);
    }

    // ---------------- 助手 ----------------

    private (NicknameService Service, FakeNicknameTransport Transport) New(FakeNicknameTransport transport)
    {
        var service = new NicknameService(transport, new MemoryNicknameStore("LIVINGROOM-PC"), "LIVINGROOM-PC");
        service.OnFrame(FrameCodec.Parse(NicknameJson.List(
            NicknameJson.Row(1, "爸爸", "color_03") + "," + NicknameJson.Row(2, "妈妈", "color_13"))).Frame!);
        return (service, transport);
    }

    private static IEnumerable<(string Kind, NicknameOpResult Result)> RunAll(FakeNicknameTransport transport)
    {
        var (service, _) = new PcShellNicknamePendingTests().New(transport);
        yield return ("create", service.Create("用户6"));
        yield return ("rename", service.Rename(2, "用户6"));
        yield return ("reassign_color", service.ReassignColor(2));
        yield return ("delete", service.Delete(2));
    }
}
