using System.Collections.Generic;
using System.Text.Json;

namespace FamilyAgent.Core.Protocol.Frames;

/// <summary>
/// 一条**共享昵称**（docs/NICKNAME-SYSTEM-PLAN.md §3.1 的 6 列，一个不多一个不少）。
///
/// ★ <see cref="Color"/> 是**逻辑色 ID**（<c>color_01</c>…<c>color_16</c>），
///   不是 HEX —— 显示色由客户端按主题算（<see cref="Nicknames.NicknameColor"/>，§4.4）。
///   协议里绝不出现 HEX：把 HEX 当数据存/传回来就等于把「显示层」搬进了库。
/// </summary>
public sealed class NicknameDto
{
    /// <summary>唯一身份（永不变）。改名 / 换色都只动别的字段。</summary>
    public long NicknameId { get; init; }

    /// <summary>当前显示名（可改；长度 1–32，服务端做 NFC + 去控制字符）。</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>逻辑色 ID（<c>color_01</c>…<c>color_16</c>）。未知值由客户端走灰兜底。</summary>
    public string Color { get; init; } = "";

    /// <summary><c>active</c> / <c>inactive</c>（软删后的行仍在，供审计）。</summary>
    public string Status { get; init; } = "";

    public string CreatedAt { get; init; } = "";

    public string UpdatedAt { get; init; } = "";

    /// <summary>
    /// 从一段 JSON 解析一条昵称。字段缺失 / 类型不对一律取缺省值 ——
    /// 与 <c>FrameCodec</c> 的容错口径一致：服务端多塞一个字段不该让整帧丢掉。
    /// </summary>
    public static NicknameDto? From(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;

        var id = 0L;
        if (el.TryGetProperty("nickname_id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
        {
            if (!idEl.TryGetInt64(out id))
                id = 0;
        }

        return new NicknameDto
        {
            NicknameId = id,
            DisplayName = Str(el, "display_name"),
            Color = Str(el, "color"),
            Status = Str(el, "status"),
            CreatedAt = Str(el, "created_at"),
            UpdatedAt = Str(el, "updated_at"),
        };
    }

    internal static string Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? ""
            : "";
}

/// <summary>
/// <c>nickname_list_response</c>（点对点应答）与 <c>nickname_list_sync</c>（整表广播校正）**同形**。
///
/// ⚠ 必须**全量**（<c>status=active</c>）：PC 弹窗右侧要显示别人的消息，没有全量就只能退回名字哈希。
///   <see cref="PoolVersion"/> 让客户端判断自己那份「逻辑色 ID → 显示色」映射表是否落后（§4.3）。
/// </summary>
public sealed class NicknameListFrame : CoreFrame
{
    /// <summary>全量活跃昵称（按 <c>nickname_id</c> 升序，服务端保证）。</summary>
    public IReadOnlyList<NicknameDto> Nicknames { get; init; } = new List<NicknameDto>();

    /// <summary>服务端的逻辑色池语义版本（本地映射表落后时只记日志，不改渲染）。</summary>
    public int PoolVersion { get; init; }

    /// <summary>true = 来自 <c>nickname_list_sync</c> 广播（与点对应答只差 type）。</summary>
    public bool IsSync { get; init; }
}

/// <summary>
/// 四个增量广播（<c>nickname_created</c> / <c>nickname_updated</c> /
/// <c>nickname_color_changed</c> / <c>nickname_removed</c>）的统一形状 ——
/// 载荷统一是 <c>{"type":…, "nickname":{完整对象}}</c>（§5.4）。
///
/// 客户端**永远拿整表校正兜底**（<c>nickname_list_sync</c>），增量只是让界面免刷新就变。
/// </summary>
public sealed class NicknameDeltaFrame : CoreFrame
{
    /// <summary>广播里的完整昵称对象；<c>removed</c> 时是软删后的那一行（<c>status=inactive</c>）。</summary>
    public NicknameDto? Nickname { get; init; }

    /// <summary>仅 <c>nickname_removed</c> 带：这次回收的逻辑色 ID（立刻回到可用池）。</summary>
    public string ReleasedColor { get; init; } = "";
}

/// <summary>
/// <c>nickname_error</c>：设备侧昵称操作的错误应答（§5.4）。
///
/// <see cref="Request"/> 回显发起帧的 type，客户端据此把错误配对回它那份请求；
/// <see cref="ExistingNicknameId"/> 只在**创建撞名**（<c>NICKNAME_ALREADY_EXISTS</c>）时出现 ——
/// UI 直接拿它做「已存在，直接选用它？」（§3.3 方式 A）。
/// </summary>
public sealed class NicknameErrorFrame : CoreFrame
{
    /// <summary>发起帧的 type（<c>nickname_create_request</c> 等）。</summary>
    public string Request { get; init; } = "";

    /// <summary>机器可读错误码：<c>NICKNAME_ALREADY_EXISTS</c> / <c>NAME_TAKEN</c> /
    /// <c>NICKNAME_NOT_FOUND</c> / <c>NICKNAME_INACTIVE</c> / <c>NO_AVAILABLE_COLOR</c> /
    /// <c>INVALID_DISPLAY_NAME</c> / <c>INVALID_REQUEST</c>。</summary>
    public string Code { get; init; } = "";

    /// <summary>给界面直接显示的中文说明（服务端给的就是中文）。</summary>
    public string Message { get; init; } = "";

    /// <summary>撞名时那个**既有昵称**的 id（拿它去「选用」，而不是自动复用）。</summary>
    public long? ExistingNicknameId { get; init; }
}
