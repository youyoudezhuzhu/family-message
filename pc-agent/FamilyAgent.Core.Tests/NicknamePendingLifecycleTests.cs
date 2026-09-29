using System;
using System.IO;
using FamilyAgent.Core.Nicknames;
using Xunit;

namespace FamilyAgent.Core.Tests;

/// <summary>
/// 昵称**管理操作的生命周期**（§5.4）：一次改名 / 换色 / 新建 / 删除的请求发出去之后，
/// 「等结果」这个状态**必须能终结** —— 无论成功、被拒还是服务端一直不答。
///
/// 真机 bug（2026-09-29）：在设置页把昵称改名为「用户6」后，界面永远停在
/// 「已请求把该昵称改名为「用户6」（全局生效），等待服务端应答…」——
/// 既不报成功也不报失败。根因在页面侧（web/shell/pc.js）：
///   ① 宿主那条 <c>host.nickname_result</c>（= <see cref="NicknameService"/> 的 “SentOk”
///      回执）只是「请求被接受了」，页面却在收到它时就把 8s 看门狗 `clearTimeout` 了；
///   ② 成功一路上没有任何文案落地 —— 服务端随后推来的整份状态只重画列表，不动提示行。
/// 服务端撞名（409 <c>NAME_TAKEN</c>）时更糟：错误帧到了，但设置页那行提示在
/// 弹窗 / 客户端形态下是隐藏的 → 用户什么都看不到。
///
/// 本文件钉两件事：
///   A. Core 侧的**事实**：回执≠成功（<see cref="NicknameService"/> 只发一次上行帧就返回
///      “等待服务端应答”），权威结果只有两条路 —— 整表/增量（成功）或 <c>nickname_error</c>（失败），
///      两条路都必须把状态变化抛出去（<c>Changed</c>），宿主才有东西推给页面。
///   B. 页面侧的**契约**：web/shell/pc.js 的源码断言（pending 只能被权威结果 / 看门狗收尾，
///      不能被 accepted 回执清掉；回复栏取值必须按事件来源）。Core.Tests 里没有 JS 引擎，
///      所以这里做的是源码契约；**行为**由 tools/test_pc_sender_nickname.py（真 Chromium）验证。
/// </summary>
public class NicknamePendingLifecycleTests
{
    private const string DeviceName = "LIVINGROOM-PC";

    private static (NicknameService svc, FakeNicknameTransport transport, MemoryNicknameStore store)
        New(bool connected)
    {
        var transport = new FakeNicknameTransport { Connected = connected };
        var store = new MemoryNicknameStore(DeviceName);
        return (new NicknameService(transport, store, DeviceName), transport, store);
    }

    /// <summary>离线改名：直接拒绝 + **一个帧都不发**（§5.2 完全拒绝离线改昵称）→ pending 有结论。</summary>
    [Fact]
    public void RenameOffline_IsRejectedWithReason_AndSendsNothing()
    {
        var (svc, transport, _) = New(connected: false);

        var r = svc.Rename(2, "用户6");

        Assert.False(r.Accepted);
        Assert.True(r.Offline);
        Assert.NotEqual("", r.Message);
        Assert.Empty(transport.Sent);
    }

    /// <summary>
    /// 在线改名：发出 <c>nickname_rename_request</c>（带 id + 新名字），但返回的只是**回执** ——
    /// 文案必须是「等待服务端应答…」。页面绝不能把这句当成功（它就是 bug 的文案来源）。
    /// </summary>
    [Fact]
    public void RenameOnline_SendsFrameAndReturnsOnlyAnAck()
    {
        var (svc, transport, _) = New(connected: true);

        var r = svc.Rename(2, "用户6");

        Assert.True(r.Accepted);
        Assert.False(r.Offline);
        Assert.Contains("等待服务端应答", r.Message);
        Assert.Single(transport.Sent);
        Assert.Contains("nickname_rename_request", transport.LastJson);
        /* System.Text.Json 默认把非 ASCII 转义成 \uXXXX —— 按 JSON 取值断言，别比对原文 */
        using var frame = System.Text.Json.JsonDocument.Parse(transport.LastJson);
        Assert.Equal(2L, frame.RootElement.GetProperty("nickname_id").GetInt64());
        Assert.Equal("用户6", frame.RootElement.GetProperty("display_name").GetString());
    }

    /// <summary>
    /// 服务端拒绝（撞名 409 <c>NAME_TAKEN</c>）：错误帧必须被 Core 接住、留痕、并抛 Changed ——
    /// 宿主据此推 <c>host.nickname_error</c>，页面才有「失败文案」可显示（错误必须可见）。
    /// </summary>
    [Fact]
    public void ServerError_MustBeExposedToTheHost_SoThePageCanShowIt()
    {
        var (svc, _, _) = New(connected: true);
        svc.OnFrame(NicknameJson.Frame(NicknameJson.List(NicknameJson.Row(2, "妈妈", "color_13"))));

        svc.Rename(2, "用户6");
        var raised = 0;
        svc.Changed += () => raised++;

        var handled = svc.OnFrame(NicknameJson.Frame(
            NicknameJson.Error("NAME_TAKEN", "nickname_rename_request", "「用户6」已被占用，请换个名字或直接选用它")));

        Assert.True(handled);
        Assert.NotNull(svc.LastError);
        Assert.Equal("NAME_TAKEN", svc.LastError!.Code);
        Assert.Equal("nickname_rename_request", svc.LastError.Request);
        Assert.Contains("已被占用", svc.LastError.Message);
        Assert.True(raised > 0, "错误帧必须触发 Changed（宿主靠它把错误推给页面）");
    }

    /// <summary>
    /// 成功路：服务端广播 <c>nickname_updated</c> → 整表里那行的名字真的变了 + 抛 Changed。
    /// 页面就是靠「权威整份状态到了」这一条收尾 pending 的（判定成功的唯一依据）。
    /// </summary>
    [Fact]
    public void ServerBroadcast_AppliesTheRename_AndRaises_SoThePageCanEndPending()
    {
        var (svc, _, store) = New(connected: true);
        svc.OnFrame(NicknameJson.Frame(NicknameJson.List(NicknameJson.Row(2, "妈妈", "color_13"))));
        svc.Select(2);

        svc.Rename(2, "用户6");
        var raised = 0;
        svc.Changed += () => raised++;

        svc.OnFrame(NicknameJson.Frame(NicknameJson.Delta("nickname_updated", 2, "用户6", "color_13")));

        Assert.Equal("用户6", svc.Current.DisplayName);
        Assert.True(raised > 0, "权威状态变化必须触发 Changed");
        Assert.Equal("用户6", store.Saved!.DisplayName);
    }
}

/// <summary>
/// 页面（web/shell/pc.js）的源码契约 —— Core.Tests 跑在 Linux 上、没有 JS 引擎，
/// 所以这里只钉「**结构**不能退回去」：
///   ① 回复栏共用 handler 时，值只能从**触发事件的那条下拉**取（写死读 client 那条 =
///      popup 形态选昵称发出去的还是旧身份，2026-09-29 真机 bug）；
///   ② 管理操作的 pending 只能被**权威结果**（host.nickname / host.nickname_error）
///      或看门狗收尾，**不能**被 accepted 回执清掉（否则提示永远停在「等待服务端应答…」）；
///   ③ 结果 / 失败文案必须同时写到当前形态可见的那一行（设置页那行在弹窗形态下是隐藏的）。
///
/// 行为验证（真 Chromium，三形态）在 tools/test_pc_sender_nickname.py，本文件不重复造浏览器。
/// </summary>
public class PcShellNicknameSourceContractTests
{
    private static string ShellJs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "web", "shell", "pc.js")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "web", "shell", "pc.js"));
    }

    /// <summary>取一个顶层具名函数的函数体（按花括号配平；先把注释去掉 —— 注释里会提到
    /// 被禁掉的写法，比如「老实现在这里 clearTimeout」，那是说明不是代码）。</summary>
    private static string BodyOf(string source, string signature)
    {
        return BodyOfRaw(StripComments(source), signature);
    }

    /// <summary>去掉 // 行注释与 /* … */ 块注释（字符串字面量里的 // 不作数：pc.js 里没有 URL 字面量）。</summary>
    private static string StripComments(string source)
    {
        var sb = new System.Text.StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                sb.Append('\n');
            }
            else if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i++;
            }
            else
            {
                sb.Append(source[i]);
            }
        }
        return sb.ToString();
    }

    private static string BodyOfRaw(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"pc.js 里找不到 {signature}");

        var i = source.IndexOf('{', start);
        Assert.True(i > 0, $"{signature} 没有函数体");

        var depth = 0;
        for (var j = i; j < source.Length; j++)
        {
            if (source[j] == '{') depth++;
            else if (source[j] == '}')
            {
                depth--;
                if (depth == 0) return source.Substring(i, j - i + 1);
            }
        }
        Assert.Fail($"{signature} 的花括号不配平");
        return "";
    }

    /// <summary>
    /// 回复栏取值：必须走 senderBarOf(ev)（事件来源），不许再出现「写死读 client 下拉」。
    /// </summary>
    [Fact]
    public void ReplyBarChange_ReadsValueFromTheEventSource_NotFromTheClientBar()
    {
        var js = ShellJs();
        var body = BodyOf(js, "function onSenderChange(");

        Assert.Contains("senderBarOf(ev)", body);
        Assert.DoesNotContain("BARS.client.sender", body);
        Assert.DoesNotContain("clearTimeout", body);
    }

    /// <summary>senderBarOf 必须真的按 id 反查两条回复栏，并有「没有事件来源」的兜底。</summary>
    [Fact]
    public void SenderBarOf_MapsBothReplyBarsByElementId()
    {
        var body = BodyOf(ShellJs(), "function senderBarOf(");

        Assert.Contains("BARS[k].sender", body);
        Assert.Contains("ev.target", body);
    }

    /// <summary>
    /// pending 的收尾：accepted 回执**不许**清看门狗；权威状态与错误帧必须收尾；
    /// 成功判定必须由 resolveNickPending 从整份状态里得出来。
    /// </summary>
    [Fact]
    public void PendingIsEndedByAuthorityOrWatchdog_NeverByTheAcceptedAck()
    {
        var js = ShellJs();

        var result = BodyOf(js, "function onNicknameResult(");
        Assert.DoesNotContain("clearTimeout", result);      // ★ bug ① 不许回来
        Assert.Contains("endNickOp()", result);

        var error = BodyOf(js, "function onNicknameError(");
        Assert.Contains("endNickOp()", error);

        var nickname = BodyOf(js, "function onNickname(");
        Assert.Contains("resolveNickPending()", nickname);

        var resolve = BodyOf(js, "function resolveNickPending(");
        Assert.Contains("endNickOp()", resolve);
        Assert.Contains("'rename'", resolve);               // 改名成功判定
        Assert.Contains("'reassign_color'", resolve);       // 换色「颜色变了才算成功」
        Assert.Contains("'create'", resolve);
        Assert.Contains("'delete'", resolve);
        Assert.Contains("'refresh'", resolve);

        var opNick = BodyOf(js, "function opNick(");
        Assert.Contains("beginNickOp(", opNick);

        var watchdog = BodyOf(js, "function beginNickOp(");
        Assert.Contains("NICK_OP_TIMEOUT", watchdog);
        Assert.Contains("nickHint(", watchdog);             // 兜底也要落文案，不许静默
    }

    /// <summary>结果 / 失败文案必须写到当前形态可见的那一行（弹窗 / 客户端形态也要看得见）。</summary>
    [Fact]
    public void NickHint_IsAlsoWrittenToTheVisibleBar()
    {
        var body = BodyOf(ShellJs(), "function nickHint(");

        Assert.Contains("settings-nick-hint", body);
        Assert.Contains("S.view !== 'settings'", body);
        Assert.Contains("BARS[S.view]", body);
    }
}
