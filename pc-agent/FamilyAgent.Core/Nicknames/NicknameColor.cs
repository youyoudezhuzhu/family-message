using System;
using System.Collections.Generic;

namespace FamilyAgent.Core.Nicknames;

/// <summary>
/// ★ 客户端渲染契约：**逻辑色 ID → 显示色**（docs/NICKNAME-SYSTEM-PLAN.md §4.4）。
///
/// 这是 r6 拍板第 ④ 条的落地：NAS 只发**逻辑色 ID**（<c>color_01</c>…<c>color_16</c> / <c>gray</c>），
/// 库 / 协议 / 广播 / 消息快照里**都只有 ID**；「这个色在浅色主题下到底长什么样」
/// 由客户端按 <c>theme + ID</c> **渲染时**算 —— 所以 NAS 不必为浅/深各存一套色，
/// 而「昵称颜色是全局统一的」这个概念也不受影响。
///
/// **四条规则（与 §4.2 一一对应）**
/// <list type="number">
///   <item><see cref="Dot"/>：浅色 <c>mix(base,#000,35%)</c>；深色 <c>mix(base,#FFF,30%)</c>。</item>
///   <item><see cref="AvatarBg"/> / <see cref="AvatarFg"/>：**优先原色底** ——
///     白字对比 ≥ 4.5 → 白字；否则 0.7 黑字对比 ≥ 4.5 → 0.7 黑字。</item>
///   <item>两头都不达标（<c>color_03/05/06/11/12</c>）→ 底取 <c>mix(base,#000,18%)</c> + 白字。</item>
///   <item>昵称文本 / 下拉项**不用色值当字色**（那是界面的事，见 <c>web/shell/pc.js</c>）。</item>
/// </list>
///
/// **与 Web 端的一致性是硬要求**（R7）：这份实现与 <c>web/shell/pc.js</c> 的
/// <c>FMNick.color</c> 必须逐字一致 —— 两端各实现一份是刻意的（PC 页面与网页端
/// 各跑各的运行时），但改一处必须同时改另一处，否则会出现「网页端一个色、PC 弹窗另一个色」。
/// <c>NicknameColorMapTests</c> 用 §4.4 的映射表把 17 行全部钉住（含灰）。
///
/// ⚠ **未知逻辑色 ID**（脏数据 / 比 <c>pool_version</c> 更新的 ID）→ 走 <c>gray</c> 兜底；
/// 绝不允许把收到的字符串直接塞进 CSS / 画笔（R2）。
///
/// **取整口径**：<c>mix</c> 用 .NET 默认的 <see cref="Math.Round(double)"/>（**四舍六入五成双**）。
/// 这不是随手写的 —— §4.4 的表就是按这个口径重算出来的，换一种取整会有 4 行差 1。
/// </summary>
public static class NicknameColor
{
    /// <summary>本地临时昵称的逻辑色 ID。**不在 16 个共享色池里**，永远不分配给共享昵称（§3.1）。</summary>
    public const string LocalTempColorId = "gray";

    /// <summary>本地临时昵称的基色（<c>gray</c>）。</summary>
    public const string LocalTempBaseColor = "#8A8A8A";

    /// <summary>共享色池上限 = 活跃昵称上限（颜色不重复的必然结果，§3.1）。</summary>
    public const int MaxActiveNicknames = 16;

    /// <summary>逻辑色池的语义版本：换 ID / 改基础色值时必须 +1（与 NAS 的 <c>COLOR_POOL_VERSION</c> 对齐）。</summary>
    public const int PoolVersion = 1;

    /// <summary>头像用白字时的字色（CSS 值）。</summary>
    public const string AvatarFgWhite = "#FFFFFF";

    /// <summary>头像用深字时的字色（CSS 值）—— 与 <c>pc.css</c> / <c>style.css</c> 里原来写死的值同值。</summary>
    public const string AvatarFgDark = "rgba(0,0,0,0.7)";

    /// <summary>
    /// ① 「逻辑色 ID → 基础色值」常量表。书写顺序 = 服务端的**分配优先级**
    /// （<c>server/nicknames.py</c> 的 <c>LOGICAL_COLORS</c>，UUID 一个都不能错）。
    /// 右边的 HEX 只是客户端算显示色的**起点**，**不进协议、不进库**。
    /// </summary>
    private static readonly Dictionary<string, string> Base = new(StringComparer.Ordinal)
    {
        ["color_01"] = "#5E35B1", ["color_02"] = "#3949AB",
        ["color_03"] = "#1E88E5", ["color_04"] = "#039BE5",
        ["color_05"] = "#00897B", ["color_06"] = "#43A047",
        ["color_07"] = "#7CB342", ["color_08"] = "#C0CA33",
        ["color_09"] = "#F9A825", ["color_10"] = "#FB8C00",
        ["color_11"] = "#F4511E", ["color_12"] = "#E53935",
        ["color_13"] = "#D81B60", ["color_14"] = "#8E24AA",
        ["color_15"] = "#6D4C41", ["color_16"] = "#546E7A",
        [LocalTempColorId] = LocalTempBaseColor,
    };

    /// <summary>色池里的逻辑色 ID（按分配优先级排列，不含 <c>gray</c>）。</summary>
    public static readonly IReadOnlyList<string> Pool = new List<string>
    {
        "color_01", "color_02", "color_03", "color_04", "color_05", "color_06",
        "color_07", "color_08", "color_09", "color_10", "color_11", "color_12",
        "color_13", "color_14", "color_15", "color_16",
    };

    /// <summary>这个逻辑色 ID 认不认识（不认识就走灰兜底）。</summary>
    public static bool IsKnown(string? colorId) =>
        !string.IsNullOrEmpty(colorId) && Base.ContainsKey(colorId!);

    /// <summary>
    /// 逻辑色 ID → 基础色值。**不认识的一律返回灰的基色**（兜底，绝不抛异常、
    /// 也绝不把未知字符串当颜色用 —— R2）。
    /// </summary>
    public static string BaseOf(string? colorId) =>
        IsKnown(colorId) ? Base[colorId!] : LocalTempBaseColor;

    /// <summary>
    /// 规则 1：圆点色（8–10px，非文本判据 ≥3.0）。
    /// 浅色主题 <c>mix(base,#000000,35%)</c>；深色主题 <c>mix(base,#FFFFFF,30%)</c>。
    /// </summary>
    public static string Dot(string? colorId, bool darkTheme) =>
        darkTheme ? Mix(BaseOf(colorId), "#FFFFFF", 0.30) : Mix(BaseOf(colorId), "#000000", 0.35);

    /// <summary>
    /// 规则 2 / 3：头像底色。优先原色；两头（白字 / 0.7 黑字）都不达标时才用
    /// <c>mix(base,#000000,18%)</c>。当前 17 个色里只有 <c>color_03/05/06/11/12</c> 与 <c>gray</c> 走变体。
    /// </summary>
    public static string AvatarBg(string? colorId)
    {
        var color = BaseOf(colorId);
        if (Contrast(AvatarFgWhite, color) >= 4.5)
            return color;
        if (Contrast(Black70On(color), color) >= 4.5)
            return color;
        return Mix(color, "#000000", 0.18);
    }

    /// <summary>
    /// 规则 2 / 3：头像字色（自适应，≥4.5 才算达标）。
    /// 返回 <see cref="AvatarFgWhite"/> 或 <see cref="AvatarFgDark"/> ——
    /// 两者都是**实色 CSS 值**，调用方直接写进样式，不要再做透明度叠加。
    /// </summary>
    public static string AvatarFg(string? colorId)
    {
        var color = BaseOf(colorId);
        if (Contrast(AvatarFgWhite, color) >= 4.5)
            return AvatarFgWhite;
        if (Contrast(Black70On(color), color) >= 4.5)
            return AvatarFgDark;
        return AvatarFgWhite;      // 规则 3：底换成 18% 黑变体，字恒用白字
    }

    /// <summary>
    /// 一次性拿到三个显示色（页面 / 桥一次算完，少三次字典查找）。
    /// </summary>
    public static NicknameDisplay Display(string? colorId, bool darkTheme) =>
        new(colorId ?? LocalTempColorId, darkTheme,
            Dot(colorId, darkTheme), AvatarBg(colorId), AvatarFg(colorId));

    // ---------------- 颜色算术 ----------------

    /// <summary>
    /// 按权重 <paramref name="weight"/> 把 <paramref name="other"/> 混进 <paramref name="color"/>：
    /// <c>out = color*(1-w) + other*w</c>，逐通道取整（五成双）。
    /// </summary>
    public static string Mix(string color, string other, double weight)
    {
        var (r1, g1, b1) = Rgb(color);
        var (r2, g2, b2) = Rgb(other);
        return Hex(
            Clamp(r1 + (r2 - r1) * weight),
            Clamp(g1 + (g2 - g1) * weight),
            Clamp(b1 + (b2 - b1) * weight));
    }

    /// <summary>
    /// <c>rgba(0,0,0,0.7)</c> 叠在 <paramref name="background"/> 上的**实色**（= 底色 × 0.3）。
    /// 「白字还是黑字」必须拿这个实色去算对比度，直接拿 0.7 黑跟底色比会算错。
    /// </summary>
    public static string Black70On(string background)
    {
        var (r, g, b) = Rgb(background);
        return Hex(r * 0.3, g * 0.3, b * 0.3);
    }

    /// <summary>WCAG 2.1 对比度（1…21）。</summary>
    public static double Contrast(string a, string b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return la < lb ? (lb + 0.05) / (la + 0.05) : (la + 0.05) / (lb + 0.05);
    }

    /// <summary>WCAG 2.1 相对亮度（sRGB 线性化后加权）。</summary>
    public static double Luminance(string color)
    {
        var (r, g, b) = Rgb(color);
        return 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
    }

    private static double Linear(int channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>四舍六入五成双（与 §4.4 表的取整口径一致），返回 0…255 的整数。</summary>
    private static int Clamp(double value)
    {
        var v = (int)Math.Round(value, MidpointRounding.ToEven);
        return v < 0 ? 0 : (v > 255 ? 255 : v);
    }

    /// <summary><c>#RRGGBB</c> / <c>#RGB</c> → 三个通道。认不出来的串返回 (0,0,0)（不会崩）。</summary>
    private static (int R, int G, int B) Rgb(string color)
    {
        var s = (color ?? "").Trim();
        if (s.StartsWith("#", StringComparison.Ordinal))
            s = s[1..];
        if (s.Length == 3)
            s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
        if (s.Length != 6)
            return (0, 0, 0);
        return (Hex2(s, 0), Hex2(s, 2), Hex2(s, 4));
    }

    private static int Hex2(string s, int at)
    {
        var hi = HexVal(s[at]);
        var lo = HexVal(s[at + 1]);
        return hi < 0 || lo < 0 ? 0 : hi * 16 + lo;
    }

    private static int HexVal(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }

    private static string Hex(double r, double g, double b) =>
        "#" + Clamp(r).ToString("X2") + Clamp(g).ToString("X2") + Clamp(b).ToString("X2");
}

/// <summary>一个逻辑色 ID 在某个主题下的三个显示色（规则 1–3 的结果）。</summary>
public sealed class NicknameDisplay
{
    public NicknameDisplay(string colorId, bool darkTheme, string dot, string avatarBg, string avatarFg)
    {
        ColorId = colorId;
        DarkTheme = darkTheme;
        Dot = dot;
        AvatarBg = avatarBg;
        AvatarFg = avatarFg;
    }

    /// <summary>逻辑色 ID（原始值，可能就是兜底用的 <c>gray</c>）。</summary>
    public string ColorId { get; }

    public bool DarkTheme { get; }

    /// <summary>圆点 / 昵称字色（浅色主题用深变体、深色主题用浅变体）。</summary>
    public string Dot { get; }

    public string AvatarBg { get; }

    public string AvatarFg { get; }
}
