using System;
using FamilyAgent.Core.Protocol.Frames;

namespace FamilyAgent.Core.Nicknames;

/// <summary>
/// 「本机当前在用谁的名义」——**纯客户端本地状态**（docs/NICKNAME-SYSTEM-PLAN.md §5.2 的「选择昵称」）。
///
/// ★ 只有三个字段：<see cref="NicknameId"/>（**可空 = 还没选**）/ <see cref="DisplayName"/> /
///   <see cref="Color"/>（**逻辑色 ID**）。
///   **没有 `sync_state`** —— r6 定稿「完全拒绝离线改昵称」，pending / synced 那套状态机整段删除（§0.13）。
///
/// ★ 这个对象**不上传、不广播、不参与同步**：改它「只影响这个客户端」，
///   NAS 的 <c>nicknames</c> 表一个字节都不动（§5.2）。所以它是唯一**离线也能改**的东西。
///
/// 灰临时态（<see cref="IsLocalTemp"/> = <c>NicknameId is null</c>）的
/// <see cref="Color"/> **恒为 <c>gray</c>**：它不来自色池、也不进池（§3.1），
/// 只在客户端本地当常量用。
/// </summary>
public sealed class NicknameState
{
    /// <summary>本机当前选用的共享昵称 id；<c>null</c> = 没选（灰临时昵称）。</summary>
    public long? NicknameId { get; init; }

    /// <summary>当前显示名。灰临时态 = 本机名（PC 是 <c>ComputerName</c>）。</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>逻辑色 ID；灰临时态恒为 <c>gray</c>。</summary>
    public string Color { get; init; } = NicknameColor.LocalTempColorId;

    /// <summary>true = 灰临时昵称（还没选任何共享昵称）。</summary>
    public bool IsLocalTemp => NicknameId is null;

    /// <summary>
    /// 灰临时昵称：**首次运行 / 昵称被删后的回退态**（§3.3 第 3 步、§3.5）。
    /// 名字取本机名，颜色取客户端本地常量 <c>gray</c> ——
    /// 此时 NAS 一行都不多（不建行、不占色、不参与同步）。
    /// </summary>
    public static NicknameState LocalTemp(string defaultName) => new()
    {
        NicknameId = null,
        DisplayName = (defaultName ?? "").Trim(),
        Color = NicknameColor.LocalTempColorId,
    };

    /// <summary>从一条共享昵称生成「本机选用它」的状态（颜色取**逻辑色 ID**）。</summary>
    public static NicknameState FromN(NicknameDto nickname) => new()
    {
        NicknameId = nickname.NicknameId,
        DisplayName = nickname.DisplayName,
        Color = string.IsNullOrEmpty(nickname.Color)
            ? NicknameColor.LocalTempColorId
            : nickname.Color,
    };

    /// <summary>换值的便捷写法（本类刻意不可变，照 <c>MessageRecord</c> 的风格）。</summary>
    public NicknameState With(long? nicknameId, string displayName, string color) => new()
    {
        NicknameId = nicknameId,
        DisplayName = displayName ?? "",
        Color = string.IsNullOrEmpty(color) ? NicknameColor.LocalTempColorId : color,
    };

    /// <summary>两个状态是不是同一个（用来判断「要不要落盘」—— 没变就不写文件）。</summary>
    public bool SameAs(NicknameState? other) =>
        other is not null
        && NicknameId == other.NicknameId
        && string.Equals(DisplayName, other.DisplayName, StringComparison.Ordinal)
        && string.Equals(Color, other.Color, StringComparison.Ordinal);

    public override string ToString() =>
        IsLocalTemp ? $"灰临时({DisplayName}/{Color})" : $"#{NicknameId} {DisplayName}/{Color}";
}
