using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace FamilyAgent;

/// <summary>
/// Material Design 3 令牌 + 配色方案 + 昵称色。
///
/// 令牌定义与网页端 / Android 端完全一致，改颜色只改这里（对照 docs/DESIGN-TOKENS.md）。
///
/// 实现要点：所有画刷都是**静态可变画刷**，XAML 里用 {x:Static} 引用。
/// 切换配色时只改这些画刷的 Color，界面上所有引用点自动跟着变，不需要重建控件树。
/// </summary>
public static class MdTheme
{
    private static SolidColorBrush New(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    // ── MD3 surface 体系（不随配色变）────────────────────────
    public static readonly SolidColorBrush Surface = New("#141218");
    public static readonly SolidColorBrush SurfaceLow = New("#1D1B20");
    public static readonly SolidColorBrush SurfaceC = New("#211F26");
    public static readonly SolidColorBrush SurfaceHigh = New("#2B2930");
    public static readonly SolidColorBrush SurfaceHighest = New("#36343B");
    public static readonly SolidColorBrush OnSurface = New("#E6E0E9");
    public static readonly SolidColorBrush OnVariant = New("#CAC4D0");
    public static readonly SolidColorBrush Outline = New("#938F99");
    public static readonly SolidColorBrush OutlineVariant = New("#49454F");
    public static readonly SolidColorBrush Ok = New("#A5D6A7");
    public static readonly SolidColorBrush Bad = New("#EF9A9A");
    public static readonly SolidColorBrush TimeText = New("#8B8695");

    // ── 主题色（随配色切换）──────────────────────────────────
    public static readonly SolidColorBrush Primary = New("#A8C7FA");
    public static readonly SolidColorBrush PrimaryContainer = New("#0842A0");
    public static readonly SolidColorBrush OnPrimaryContainer = New("#D3E3FD");
    public static readonly SolidColorBrush OnPrimary = New("#0B1020");

    /// <summary>配色方案（primary / primaryContainer / onPrimaryContainer / onPrimary）</summary>
    public sealed record Scheme(string Id, string Name, string Primary, string PrimaryC,
                                string OnPrimaryC, string OnPrimary);

    public static readonly IReadOnlyList<Scheme> Schemes = new[]
    {
        new Scheme("indigo", "靛蓝", "#A8C7FA", "#0842A0", "#D3E3FD", "#0B1020"),
        new Scheme("violet", "紫罗", "#D0BCFF", "#4F378B", "#EADDFF", "#0B1020"),
        new Scheme("teal",   "青碧", "#80DEEA", "#004F58", "#B2EBF2", "#001A1E"),
        new Scheme("green",  "松绿", "#A5D6A7", "#1B5E20", "#C8E6C9", "#00210A"),
        new Scheme("amber",  "琥珀", "#FFD54F", "#6D4C00", "#FFECB3", "#221A00"),
        new Scheme("coral",  "珊瑚", "#FFB4AB", "#93000A", "#FFDAD6", "#2B0002"),
        new Scheme("pink",   "品红", "#F8BBD0", "#880E4F", "#FCE4EC", "#2B0016"),
        new Scheme("cyan",   "天青", "#90CAF9", "#0B4F6C", "#CDE7FF", "#001A28"),
    };

    public static string CurrentId { get; private set; } = "indigo";

    public static Scheme Current =>
        Find(CurrentId) ?? Schemes[0];

    public static Scheme? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        foreach (var s in Schemes)
        {
            if (s.Id == id)
                return s;
        }
        return null;
    }

    /// <summary>切换配色。只改画刷颜色，界面自动跟随。</summary>
    public static void Apply(string? id)
    {
        var s = Find(id) ?? Schemes[0];
        CurrentId = s.Id;
        Primary.Color = Parse(s.Primary);
        PrimaryContainer.Color = Parse(s.PrimaryC);
        OnPrimaryContainer.Color = Parse(s.OnPrimaryC);
        OnPrimary.Color = Parse(s.OnPrimary);
    }

    public static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    // ── 昵称色 ────────────────────────────────────────────────
    //
    // 同一条消息的颜色由「昵称」决定，与「从哪端发来」无关。
    // 取色算法必须与网页端 / Android 端完全一致，否则同一昵称在不同端颜色不同
    // （见 docs/DESIGN-TOKENS.md §3）。

    public static readonly IReadOnlyList<string> NickPalette = new[]
    {
        "#90CAF9", "#CE93D8", "#80CBC4", "#A5D6A7", "#FFE082", "#FFCC80",
        "#EF9A9A", "#F48FB1", "#9FA8DA", "#80DEEA", "#C5E1A5", "#FFAB91",
    };

    public static int NickIndex(string? name)
    {
        var s = (name ?? "").Trim();
        if (s.Length == 0)
            return 0;

        var h = 0u;
        unchecked
        {
            foreach (var c in s)
                h = h * 31 + c;      // 32 位无符号回绕，与 JS 的 >>> 0 等价
        }
        return (int)(h % (uint)NickPalette.Count);
    }

    public static Color NickColor(string? name) => Parse(NickPalette[NickIndex(name)]);

    /// <summary>把昵称色按比例混进底色 —— 气泡淡色底，保证正文对比度。</summary>
    public static Color Blend(Color back, Color front, double amount)
    {
        var a = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(back.R + (front.R - back.R) * a),
            (byte)Math.Round(back.G + (front.G - back.G) * a),
            (byte)Math.Round(back.B + (front.B - back.B) * a));
    }
}
