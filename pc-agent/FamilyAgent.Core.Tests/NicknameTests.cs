using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FamilyAgent.Core.Nicknames;
using FamilyAgent.Core.Protocol;
using FamilyAgent.Core.Protocol.Frames;
using Xunit;

namespace FamilyAgent.Core.Tests;

/// <summary>
/// 一个可编程的假传输：能随时切「在线 / 离线」，并把**真的发出去的帧**记下来。
///
/// 它是本轮最关键的验收工具 —— 「完全拒绝离线改昵称」这条定稿的唯一机器判据就是
/// <see cref="Sent"/> 在离线期间**一条都不涨**（§5.2 / §7 Phase 4 验收 2、3）。
/// </summary>
internal sealed class FakeNicknameTransport : INicknameTransport
{
    public bool Connected { get; set; }

    /// <summary>发出去的上行帧（按顺序）。</summary>
    public List<string> Sent { get; } = new();

    /// <summary>最后一条发出帧的 JSON（断言字段用）。</summary>
    public string LastJson => Sent.Count == 0 ? "" : Sent[^1];

    public bool Send(string type, object payload)
    {
        if (!Connected)
            return false;
        Sent.Add(JsonSerializer.Serialize(payload));
        return true;
    }
}

internal static class NicknameJson
{
    /// <summary>造一条服务端形状的昵称对象（6 列，`color` 是**逻辑色 ID**）。</summary>
    public static string Row(long id, string name, string color, string status = "active") =>
        $"{{\"nickname_id\":{id},\"display_name\":\"{name}\",\"color\":\"{color}\","
        + $"\"status\":\"{status}\",\"created_at\":\"2026-09-29 12:00:00\","
        + "\"updated_at\":\"2026-09-29 12:00:00\"}";

    public static string List(string rows, int poolVersion = 1) =>
        $"{{\"type\":\"nickname_list_response\",\"nicknames\":[{rows}],\"pool_version\":{poolVersion}}}";

    public static string ListSync(string rows) =>
        $"{{\"type\":\"nickname_list_sync\",\"nicknames\":[{rows}],\"pool_version\":1}}";

    public static string Delta(string type, long id, string name, string color,
                              string status = "active", string extra = "") =>
        $"{{\"type\":\"{type}\",\"nickname\":{Row(id, name, color, status)}{extra}}}";

    public static string Error(string code, string request, string message, long? existing = null) =>
        existing is null
            ? $"{{\"type\":\"nickname_error\",\"request\":\"{request}\",\"code\":\"{code}\",\"message\":\"{message}\"}}"
            : $"{{\"type\":\"nickname_error\",\"request\":\"{request}\",\"code\":\"{code}\","
              + $"\"message\":\"{message}\",\"existing_nickname_id\":{existing.Value}}}";

    /// <summary>把一段 JSON 走一遍真的解析器（FrameCodec），再交给服务 —— 不绕过协议层。</summary>
    public static CoreFrame Frame(string json)
    {
        var parsed = FrameCodec.Parse(json);
        Assert.NotNull(parsed.Frame);
        return parsed.Frame!;
    }
}

// ══════════════════════════════════════════════════════════════════
//  ① 逻辑色 ID → 显示色（§4.4 的映射表，17 行全部钉住）
// ══════════════════════════════════════════════════════════════════

public class NicknameColorMapTests
{
    /// <summary>§4.4 的映射表原文：ID → (基色, 浅色圆点, 深色圆点, 头像底, 头像字色)。</summary>
    public static readonly (string Id, string Base, string Light, string Dark, string Bg, bool WhiteFg)[] Table =
    {
        ("color_01", "#5E35B1", "#3D2273", "#8E72C8", "#5E35B1", true),
        ("color_02", "#3949AB", "#252F6F", "#7480C4", "#3949AB", true),
        ("color_03", "#1E88E5", "#145895", "#62ACED", "#1970BC", true),
        ("color_04", "#039BE5", "#026595", "#4FB9ED", "#039BE5", false),
        ("color_05", "#00897B", "#005950", "#4CACA3", "#007065", true),
        ("color_06", "#43A047", "#2C682E", "#7BBC7E", "#37833A", true),
        ("color_07", "#7CB342", "#51742B", "#A3CA7B", "#7CB342", false),
        ("color_08", "#C0CA33", "#7D8321", "#D3DA70", "#C0CA33", false),
        ("color_09", "#F9A825", "#A26D18", "#FBC266", "#F9A825", false),
        ("color_10", "#FB8C00", "#A35B00", "#FCAE4C", "#FB8C00", false),
        ("color_11", "#F4511E", "#9F3514", "#F78562", "#C84219", true),
        ("color_12", "#E53935", "#952522", "#ED7472", "#BC2F2B", true),
        ("color_13", "#D81B60", "#8C123E", "#E45F90", "#D81B60", true),
        ("color_14", "#8E24AA", "#5C176E", "#B066C4", "#8E24AA", true),
        ("color_15", "#6D4C41", "#47312A", "#99827A", "#6D4C41", true),
        ("color_16", "#546E7A", "#37484F", "#879AA2", "#546E7A", true),
        ("gray", "#8A8A8A", "#5A5A5A", "#ADADAD", "#717171", true),
    };

    public static IEnumerable<object[]> AllRows()
    {
        foreach (var row in Table)
            yield return new object[] { row.Id, row.Base, row.Light, row.Dark, row.Bg, row.WhiteFg };
    }

    [Theory]
    [MemberData(nameof(AllRows))]
    public void BaseColor_MatchesSpecTable(string id, string baseColor, string lightDot,
                                           string darkDot, string avatarBg, bool whiteFg)
    {
        Assert.Equal(baseColor, NicknameColor.BaseOf(id));
        Assert.Equal(lightDot, NicknameColor.Dot(id, darkTheme: false));
        Assert.Equal(darkDot, NicknameColor.Dot(id, darkTheme: true));
        Assert.Equal(avatarBg, NicknameColor.AvatarBg(id));
        Assert.Equal(whiteFg ? "#FFFFFF" : "rgba(0,0,0,0.7)", NicknameColor.AvatarFg(id));
    }

    [Theory]
    [MemberData(nameof(AllRows))]
    public void DotsReachThreeTo_AvatarReachesFourFive(string id, string baseColor, string lightDot,
                                                       string darkDot, string avatarBg, bool whiteFg)
    {
        // 判据（§4.4）：圆点（非文本图形）≥ 3.0；头像底↔字（小字）≥ 4.5
        Assert.Equal(baseColor, NicknameColor.BaseOf(id));
        Assert.True(NicknameColor.Contrast(lightDot, "#FFFFFF") >= 3.0, id + " 浅色圆点不达标");
        Assert.True(NicknameColor.Contrast(darkDot, "#2B2B2B") >= 3.0, id + " 深色圆点不达标");

        var fg = whiteFg ? "#FFFFFF" : NicknameColor.Black70On(avatarBg);
        Assert.True(NicknameColor.Contrast(fg, avatarBg) >= 4.5, id + " 头像对比度不达标");
    }

    /// <summary>★ 未知逻辑色 ID（脏数据 / 比 pool_version 新的 ID）→ 灰兜底，绝不崩、绝不原样塞进样式。</summary>
    [Theory]
    [InlineData("color_99")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("#FF00FF")]
    [InlineData("red; background:url(x)")]
    public void UnknownColorId_FallsBackToGray(string? id)
    {
        Assert.False(NicknameColor.IsKnown(id));
        Assert.Equal("#8A8A8A", NicknameColor.BaseOf(id));
        Assert.Equal(NicknameColor.Dot("gray", false), NicknameColor.Dot(id, false));
        Assert.Equal(NicknameColor.Dot("gray", true), NicknameColor.Dot(id, true));
        Assert.Equal(NicknameColor.AvatarBg("gray"), NicknameColor.AvatarBg(id));
        Assert.Equal(NicknameColor.AvatarFg("gray"), NicknameColor.AvatarFg(id));
    }

    /// <summary>池子恰好 16 个、且不含灰 —— 活跃上限 16 与「灰不在池里」这两条都由它钉住。</summary>
    [Fact]
    public void Pool_HasSixteenColorsAndNoGray()
    {
        Assert.Equal(16, NicknameColor.Pool.Count);
        Assert.Equal(16, NicknameColor.MaxActiveNicknames);
        Assert.DoesNotContain("gray", NicknameColor.Pool);
        Assert.DoesNotContain(NicknameColor.LocalTempColorId, NicknameColor.Pool);
    }
}

// ══════════════════════════════════════════════════════════════════
//  ② 首次运行：不注册、能直接发消息、本地文件不出现
// ══════════════════════════════════════════════════════════════════

public class NicknameFirstRunTests
{
    private const string DeviceName = "LIVINGROOM-PC";

    private static (NicknameService Service, FakeNicknameTransport Transport, MemoryNicknameStore Store) New()
    {
        var transport = new FakeNicknameTransport();
        var store = new MemoryNicknameStore(DeviceName);
        return (new NicknameService(transport, store, DeviceName), transport, store);
    }

    /// <summary>★ 首次运行 = 灰临时昵称（本机名 + 逻辑色 gray），NAS 一行都不多。</summary>
    [Fact]
    public void FirstRun_ShowsGrayLocalTemp_AndRegistersNothing()
    {
        var (service, transport, store) = New();

        Assert.True(service.Current.IsLocalTemp);
        Assert.Null(service.Current.NicknameId);
        Assert.Equal("LIVINGROOM-PC", service.Current.DisplayName);
        Assert.Equal("gray", service.Current.Color);
        Assert.Empty(service.Table);
        Assert.Equal(0, store.SaveCount);          // 首次运行连本地文件都不写

        // 连上之后只「拉整表」，绝不创建任何东西
        transport.Connected = true;
        service.OnConnected();

        Assert.Single(transport.Sent);
        Assert.Contains("nickname_list_request", transport.LastJson);
        Assert.DoesNotContain("nickname_create_request", transport.LastJson);
        Assert.Equal(0, store.SaveCount);
        Assert.Empty(service.Table);               // 服务端空表 → 客户端也是空，绝不假造
    }

    /// <summary>还没选也能发消息：走灰临时昵称（nickname_id 为空 + 默认名 + 灰快照）。</summary>
    [Fact]
    public void LocalTemp_CanPostWithDefaultNameAndGraySnapshot()
    {
        var (service, _, _) = New();

        // 宿主拼 reply 帧时读的就是这三个值（PC 端「能直接发消息」的凭据）
        Assert.Null(service.Current.NicknameId);
        Assert.Equal(DeviceName, service.Current.DisplayName);
        Assert.Equal("gray", SnapshotColor(service));
    }

    /// <summary>reply 帧里的颜色快照：灰临时恒为 gray，选了共享昵称就是那个逻辑色 ID。</summary>
    private static string SnapshotColor(NicknameService service) =>
        service.Current.IsLocalTemp ? "gray" : service.Current.Color;

    /// <summary>「选用」是纯本地动作：一个帧都不发、NAS 零变化，但本地写下 3 个字段。</summary>
    [Fact]
    public void Select_IsLocalOnly_AndPersists()
    {
        var (service, transport, store) = New();
        transport.Connected = true;
        service.OnFrame(NicknameJson.Frame(NicknameJson.List(
            NicknameJson.Row(7, "妈妈", "color_12"))));

        var sendsBefore = transport.Sent.Count;
        var result = service.Select(7);

        Assert.True(result.Accepted);
        Assert.Equal(sendsBefore, transport.Sent.Count);        // ★ 一个帧都不发
        Assert.Equal(7L, service.Current.NicknameId);
        Assert.Equal("妈妈", service.Current.DisplayName);
        Assert.Equal("color_12", service.Current.Color);        // 存的是逻辑色 ID
        Assert.Equal(1, store.SaveCount);
        Assert.Equal("color_12", store.Saved!.Color);
    }
}

// ══════════════════════════════════════════════════════════════════
//  ③ ★ 完全拒绝离线：四个管理操作一个帧都不发、文件逐字节不变
// ══════════════════════════════════════════════════════════════════

public class NicknameOfflineTests
{
    private static (NicknameService Service, FakeNicknameTransport Transport, MemoryNicknameStore Store) Offline()
    {
        var transport = new FakeNicknameTransport { Connected = false };
        var store = new MemoryNicknameStore("LIVINGROOM-PC");
        return (new NicknameService(transport, store, "LIVINGROOM-PC"), transport, store);
    }

    [Fact]
    public void AllFourManagementOps_AreRejectedOffline_WithReason()
    {
        var (service, transport, store) = Offline();

        var results = new[]
        {
            service.Create("妈妈"),
            service.Rename(1, "老妈"),
            service.ReassignColor(1),
            service.Delete(1),
        };

        foreach (var r in results)
        {
            Assert.False(r.Accepted);
            Assert.False(r.Sent);
            Assert.True(r.Offline);
            Assert.Equal(NicknameService.OfflineMessage, r.Message);
        }

        Assert.Empty(transport.Sent);      // ★ 一个帧都没发出去（没有 pending、没有排队）
        Assert.Equal(0, store.SaveCount);  // ★ 本地文件一个字节都没动
    }

    /// <summary>★ 重连**不补发**：断网时被拒的改名不会在重连后偷偷生效。</summary>
    [Fact]
    public void Reconnect_DoesNotReplayRejectedRename()
    {
        var (service, transport, _) = Offline();

        service.Rename(1, "老妈");            // 被拒（没发）
        Assert.Empty(transport.Sent);

        transport.Connected = true;
        service.OnConnected();                // 重连：只拉整表

        Assert.Single(transport.Sent);
        Assert.Contains("nickname_list_request", transport.LastJson);
        // 没有任何 nickname_rename_request 被补发
        foreach (var json in transport.Sent)
            Assert.DoesNotContain("nickname_rename_request", json);
    }

    /// <summary>只有「选择昵称」能离线做（纯本地状态，一个字节都不上服务端）。</summary>
    [Fact]
    public void Select_StillWorksOffline()
    {
        var (service, transport, store) = Offline();
        service.Select(null);                 // 已经是灰临时，幂等

        Assert.True(service.Current.IsLocalTemp);
        Assert.Empty(transport.Sent);

        var t2 = new FakeNicknameTransport { Connected = true };
        var s2 = new JsonNicknameStore(Path.Combine(Path.GetTempPath(),
            "fam-nick-" + Guid.NewGuid().ToString("N") + ".json"));
        var svc2 = new NicknameService(t2, s2, "LIVINGROOM-PC");
        svc2.OnFrame(NicknameJson.Frame(NicknameJson.List(NicknameJson.Row(3, "爸爸", "color_02"))));
        t2.Connected = false;                 // 断网
        var before = t2.Sent.Count;

        var ok = svc2.Select(3);
        Assert.True(ok.Accepted);
        Assert.Equal(3L, svc2.Current.NicknameId);
        Assert.Equal("爸爸", svc2.Current.DisplayName);
        Assert.Equal(before, t2.Sent.Count);   // 离线选用一个帧都不发
    }

    /// <summary>在线时四个操作各发**一个**对应帧（直发，不是入队）。</summary>
    [Fact]
    public void Online_EachOpSendsExactlyOneFrame()
    {
        var transport = new FakeNicknameTransport { Connected = true };
        var service = new NicknameService(transport, new MemoryNicknameStore("PC"), "PC");

        service.Create("妈妈");
        service.Rename(1, "老妈");
        service.ReassignColor(1);
        service.Delete(1);

        Assert.Equal(4, transport.Sent.Count);
        Assert.Contains("nickname_create_request", transport.Sent[0]);
        Assert.Contains("nickname_rename_request", transport.Sent[1]);
        Assert.Contains("nickname_reassign_color_request", transport.Sent[2]);
        Assert.Contains("nickname_delete_request", transport.Sent[3]);
        Assert.Contains("display_name", transport.Sent[0]);
        Assert.Contains("\"nickname_id\":1", transport.Sent[1]);
    }
}

// ══════════════════════════════════════════════════════════════════
//  ④ 撞名 409（NICKNAME_ALREADY_EXISTS）+ 32 字符校验
// ══════════════════════════════════════════════════════════════════

public class NicknameConflictTests
{
    [Fact]
    public void Create409_CarriesExistingId_SoUiCanOfferSelect()
    {
        var transport = new FakeNicknameTransport { Connected = true };
        var service = new NicknameService(transport, new MemoryNicknameStore("PC"), "PC");

        service.OnFrame(NicknameJson.Frame(NicknameJson.Error(
            "NICKNAME_ALREADY_EXISTS", "nickname_create_request", "昵称已存在，请直接选用它", 5)));

        Assert.NotNull(service.LastError);
        Assert.Equal("NICKNAME_ALREADY_EXISTS", service.LastError!.Code);
        Assert.Equal(5L, service.LastError.ExistingNicknameId);

        // 「直接用已有的」：服务端已在整表里给出那一条 → 本地选用它（一个帧都不发）
        var before = transport.Sent.Count;
        service.OnFrame(NicknameJson.Frame(NicknameJson.List(NicknameJson.Row(5, "妈妈", "color_12"))));
        var picked = service.Select(service.LastError.ExistingNicknameId);

        Assert.True(picked.Accepted);
        Assert.Equal(5L, service.Current.NicknameId);
        Assert.Equal("color_12", service.Current.Color);
        Assert.Equal(0, transport.Sent.Count - before);
    }

    [Fact]
    public void RenameConflict_UsesNameTakenCode()
    {
        var service = new NicknameService(new FakeNicknameTransport { Connected = true },
            new MemoryNicknameStore("PC"), "PC");
        service.OnFrame(NicknameJson.Frame(NicknameJson.Error("NAME_TAKEN", "nickname_rename_request",
            "这个名字已被占用")));

        Assert.Equal("NAME_TAKEN", service.LastError!.Code);
        Assert.Null(service.LastError.ExistingNicknameId);
    }

    [Fact]
    public void Name_Exactly32Chars_IsAccepted_33IsRejected()
    {
        var ascii32 = new string('a', 32);
        var ascii33 = new string('a', 33);

        Assert.Null(NicknameService.ValidateName(ascii32));
        Assert.Contains("32", NicknameService.ValidateName(ascii33));

        // 中文同样按「字符」算（32 个汉字 = 32）
        Assert.Null(NicknameService.ValidateName(new string('好', 32)));
        Assert.NotNull(NicknameService.ValidateName(new string('好', 33)));

        // 代理对（emoji）算 1 个字符，与 Python 的 len() 同口径
        var emoji32 = string.Concat(System.Linq.Enumerable.Repeat("😀", 32));
        Assert.Null(NicknameService.ValidateName(emoji32));
        Assert.NotNull(NicknameService.ValidateName(emoji32 + "😀"));

        Assert.NotNull(NicknameService.ValidateName(""));
        Assert.NotNull(NicknameService.ValidateName("   "));
    }

    [Fact]
    public void RejectedName_NeverLeavesTheClient()
    {
        var transport = new FakeNicknameTransport { Connected = true };
        var service = new NicknameService(transport, new MemoryNicknameStore("PC"), "PC");

        var bad = service.Create(new string('x', 40));

        Assert.False(bad.Accepted);
        Assert.False(bad.Sent);
        Assert.False(bad.Offline);            // 不是「离线」拒绝，是本地校验拦下的
        Assert.Empty(transport.Sent);
    }
}

// ══════════════════════════════════════════════════════════════════
//  ⑤ 五个广播 → 本地状态免刷新更新（含「正在用它的人」被删的回退）
// ══════════════════════════════════════════════════════════════════

public class NicknameBroadcastTests
{
    private static NicknameService New(FakeNicknameTransport transport, MemoryNicknameStore store) =>
        new(transport, store, "LIVINGROOM-PC");

    [Fact]
    public void FiveFrames_AllUpdateLocalState_AndRaiseChanged()
    {
        var transport = new FakeNicknameTransport { Connected = true };
        var store = new MemoryNicknameStore("LIVINGROOM-PC");
        var service = New(transport, store);
        var changes = 0;
        service.Changed += () => changes++;

        // ① 整表（response）
        service.OnFrame(NicknameJson.Frame(NicknameJson.List(
            NicknameJson.Row(1, "妈妈", "color_12"))));
        Assert.Single(service.Table);
        Assert.True(service.Available);

        // ② 整表（sync 广播）
        service.OnFrame(NicknameJson.Frame(NicknameJson.ListSync(
            NicknameJson.Row(1, "妈妈", "color_12") + "," + NicknameJson.Row(2, "爸爸", "color_02"))));
        Assert.Equal(2, service.Table.Count);

        // ③ created
        service.OnFrame(NicknameJson.Frame(NicknameJson.Delta(
            FrameTypes.NicknameCreated, 3, "奶奶", "color_05")));
        Assert.Equal(3, service.Table.Count);

        // ④ updated（改名：id 与颜色都不变）
        service.OnFrame(NicknameJson.Frame(NicknameJson.Delta(
            FrameTypes.NicknameUpdated, 3, "姥姥", "color_05")));
        Assert.Equal("姥姥", service.Table[2].DisplayName);
        Assert.Equal("color_05", service.Table[2].Color);

        // ⑤ color_changed（换逻辑色 ID）
        service.OnFrame(NicknameJson.Frame(NicknameJson.Delta(
            FrameTypes.NicknameColorChanged, 3, "姥姥", "color_14")));
        Assert.Equal("color_14", service.Table[2].Color);

        // removed（软删；带 released_color）
        service.OnFrame(NicknameJson.Frame(NicknameJson.Delta(
            FrameTypes.NicknameRemoved, 3, "姥姥", "color_14", "inactive",
            ",\"released_color\":\"color_14\"")));
        Assert.Equal(2, service.Table.Count);

        Assert.Equal(6, changes);
    }

    /// <summary>★ 昵称被删时正在用它的人 → 清空本地选用 + 回退灰临时 + 给一句提示（§3.5）。</summary>
    [Fact]
    public void RemovedWhileSelected_RevertsToLocalTemp()
    {
        var transport = new FakeNicknameTransport { Connected = true };
        var store = new MemoryNicknameStore("LIVINGROOM-PC");
        var service = New(transport, store);

        service.OnFrame(NicknameJson.Frame(NicknameJson.List(
            NicknameJson.Row(9, "妈妈", "color_12"))));
        service.Select(9);
        Assert.Equal(9L, service.Current.NicknameId);

        service.OnFrame(NicknameJson.Frame(NicknameJson.Delta(
            FrameTypes.NicknameRemoved, 9, "妈妈", "color_12", "inactive",
            ",\"released_color\":\"color_12\"")));

        Assert.True(service.Current.IsLocalTemp);
        Assert.Equal("LIVINGROOM-PC", service.Current.DisplayName);
        Assert.Equal("gray", service.Current.Color);
        Assert.Contains("已被删除", service.TakeNotice());
    }

    /// <summary>★ 重创同名不污染本机选用：按 **id** 判定，绝不按 display_name 反查（§3.4.1）。</summary>
    [Fact]
    public void RecreatedSameName_DoesNotHijackSelection()
    {
        var transport = new FakeNicknameTransport { Connected = true };
        var service = New(transport, new MemoryNicknameStore("LIVINGROOM-PC"));

        // 我先选了 id=9 的「妈妈」
        service.OnFrame(NicknameJson.Frame(NicknameJson.List(NicknameJson.Row(9, "妈妈", "color_12"))));
        service.Select(9);

        // 整表变成「只有 id=20 的新『妈妈』」（旧的 9 已软删）→ 必须回退灰临时，不许自动挂到 20 上
        service.OnFrame(NicknameJson.Frame(NicknameJson.List(NicknameJson.Row(20, "妈妈", "color_05"))));

        Assert.True(service.Current.IsLocalTemp);
        Assert.NotEqual(20L, service.Current.NicknameId);
    }

    /// <summary>本机正在用的昵称改名 / 换色 → 本地缓存跟着变（存的是逻辑色 ID）。</summary>
    [Fact]
    public void RenameAndColorChange_OfMySelection_FollowsThrough()
    {
        var transport = new FakeNicknameTransport { Connected = true };
        var store = new MemoryNicknameStore("LIVINGROOM-PC");
        var service = New(transport, store);

        service.OnFrame(NicknameJson.Frame(NicknameJson.List(NicknameJson.Row(9, "妈妈", "color_12"))));
        service.Select(9);

        service.OnFrame(NicknameJson.Frame(NicknameJson.Delta(
            FrameTypes.NicknameUpdated, 9, "老妈", "color_12")));
        Assert.Equal("老妈", service.Current.DisplayName);

        service.OnFrame(NicknameJson.Frame(NicknameJson.Delta(
            FrameTypes.NicknameColorChanged, 9, "老妈", "color_16")));
        Assert.Equal("color_16", service.Current.Color);
        Assert.Equal("color_16", store.Saved!.Color);
    }
}

// ══════════════════════════════════════════════════════════════════
//  ⑥ 本地文件：恰好 3 个字段、没有 sync_state / pending
// ══════════════════════════════════════════════════════════════════

public class NicknameStoreTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "fam-nick-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void RoundTrip_KeepsLogicalColorId()
    {
        var path = TempFile();
        try
        {
            var store = new JsonNicknameStore(path);
            store.Save(new NicknameState { NicknameId = 12, DisplayName = "妈妈", Color = "color_12" });

            var loaded = new JsonNicknameStore(path).Load("LIVINGROOM-PC");
            Assert.Equal(12L, loaded.NicknameId);
            Assert.Equal("妈妈", loaded.DisplayName);
            Assert.Equal("color_12", loaded.Color);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            Assert.Equal(3, CountProperties(root));       // ★ 恰好 3 个字段

            var text = File.ReadAllText(path);
            Assert.DoesNotContain("sync_state", text);    // ★ r6：不许有 pending 状态机的痕迹
            Assert.DoesNotContain("pending", text);
            Assert.DoesNotContain("#", text);             // ★ 逻辑色 ID，不是 HEX
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void NoFile_LoadsAsLocalTemp()
    {
        var store = new JsonNicknameStore(TempFile());
        var state = store.Load("LIVINGROOM-PC");

        Assert.True(state.IsLocalTemp);
        Assert.Equal("LIVINGROOM-PC", state.DisplayName);
        Assert.Equal("gray", state.Color);
    }

    [Fact]
    public void CorruptFile_FallsBackToLocalTemp_WithoutThrowing()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "{ 这不是 JSON");
            var state = new JsonNicknameStore(path).Load("LIVINGROOM-PC");
            Assert.True(state.IsLocalTemp);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static int CountProperties(JsonElement obj)
    {
        var count = 0;
        foreach (var _ in obj.EnumerateObject())
            count++;
        return count;
    }
}

// ══════════════════════════════════════════════════════════════════
//  ⑦ 协议解析：7 个昵称下行帧 + 未知 type 仍然什么都不做
// ══════════════════════════════════════════════════════════════════

public class NicknameFrameCodecTests
{
    [Fact]
    public void ListResponse_AndSync_ParseToSameShape()
    {
        var response = FrameCodec.Parse(NicknameJson.List(NicknameJson.Row(1, "妈妈", "color_12"), 3));
        var sync = FrameCodec.Parse(NicknameJson.ListSync(NicknameJson.Row(1, "妈妈", "color_12")));

        var a = Assert.IsType<NicknameListFrame>(response.Frame);
        var b = Assert.IsType<NicknameListFrame>(sync.Frame);

        Assert.False(a.IsSync);
        Assert.True(b.IsSync);
        Assert.Equal(3, a.PoolVersion);
        Assert.Equal(1, b.PoolVersion);
        Assert.Equal("color_12", a.Nicknames[0].Color);
        Assert.Equal("妈妈", a.Nicknames[0].DisplayName);
    }

    [Fact]
    public void EmptyList_StaysEmpty_NoFakeNicknames()
    {
        var parsed = FrameCodec.Parse("{\"type\":\"nickname_list_response\",\"nicknames\":[],\"pool_version\":1}");
        var frame = Assert.IsType<NicknameListFrame>(parsed.Frame);
        Assert.Empty(frame.Nicknames);
    }

    [Fact]
    public void FourBroadcasts_ParseToDeltaFrames()
    {
        foreach (var type in new[] { FrameTypes.NicknameCreated, FrameTypes.NicknameUpdated,
                                     FrameTypes.NicknameColorChanged, FrameTypes.NicknameRemoved })
        {
            var parsed = FrameCodec.Parse(NicknameJson.Delta(type, 4, "奶奶", "color_05"));
            var frame = Assert.IsType<NicknameDeltaFrame>(parsed.Frame);
            Assert.Equal(type, parsed.Type);
            Assert.Equal(4L, frame.Nickname!.NicknameId);
            Assert.Equal("color_05", frame.Nickname.Color);
        }
    }

    [Fact]
    public void Error_ParsesExistingNicknameId()
    {
        var parsed = FrameCodec.Parse(NicknameJson.Error(
            "NICKNAME_ALREADY_EXISTS", "nickname_create_request", "已存在", 5));
        var frame = Assert.IsType<NicknameErrorFrame>(parsed.Frame);

        Assert.Equal("nickname_create_request", frame.Request);
        Assert.Equal("NICKNAME_ALREADY_EXISTS", frame.Code);
        Assert.Equal(5, frame.ExistingNicknameId);
    }

    /// <summary>未知 type 仍然「什么都不做」（Frame == null）—— 现有行为不变。</summary>
    [Fact]
    public void UnknownType_StillProducesNoFrame()
    {
        var parsed = FrameCodec.Parse("{\"type\":\"totally_unknown\",\"x\":1}");
        Assert.Null(parsed.Frame);
        Assert.Equal("totally_unknown", parsed.Type);
    }
}
