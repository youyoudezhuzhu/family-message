using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using Microsoft.Win32;

namespace FamilyAgent;

/// <summary>
/// Material Design 3 设计令牌（Windows 端）。
///
/// 颜色一律来自 MdPalette.g.cs —— 那份文件由 tools/gen_tokens.py 生成，
/// 与网页端的 web/static/tokens.css **同源**，所以两端的配色永远一致，
/// 不会出现「网页改了色、PC 端还是旧的」这种漂移。
///
/// 用法：启动时调用 MdTheme.Apply(配色id, 明暗偏好)，
/// 之后所有色刷的颜色会被就地改写 —— XAML 里引用的是同一个对象，
/// 所以不需要重建界面就能换色。
/// </summary>
public static class MdTheme
{
    public static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    // 所有色刷都登记在这里，Apply 时统一改写颜色
    private static readonly Dictionary<string, SolidColorBrush> _brushes = new(StringComparer.Ordinal);

    private static SolidColorBrush Reg(string role, string fallback)
    {
        var b = new SolidColorBrush(Parse(fallback));
        _brushes[role] = b;
        return b;
    }

    // ── 颜色角色（MD3 完整角色集）───────────────────────────────
    public static readonly SolidColorBrush Primary = Reg("primary", "#B6C4FF");
    public static readonly SolidColorBrush OnPrimary = Reg("on-primary", "#002F67");
    public static readonly SolidColorBrush PrimaryContainer = Reg("primary-container", "#174589");
    public static readonly SolidColorBrush OnPrimaryContainer = Reg("on-primary-container", "#DCE1FF");

    public static readonly SolidColorBrush Secondary = Reg("secondary", "#C0C5E3");
    public static readonly SolidColorBrush OnSecondary = Reg("on-secondary", "#292F47");
    public static readonly SolidColorBrush SecondaryContainer = Reg("secondary-container", "#40465F");
    public static readonly SolidColorBrush OnSecondaryContainer = Reg("on-secondary-container", "#DCE1FF");

    public static readonly SolidColorBrush Tertiary = Reg("tertiary", "#EFB6D4");
    public static readonly SolidColorBrush OnTertiary = Reg("on-tertiary", "#4E203B");
    public static readonly SolidColorBrush TertiaryContainer = Reg("tertiary-container", "#673752");
    public static readonly SolidColorBrush OnTertiaryContainer = Reg("on-tertiary-container", "#FED8EB");

    public static readonly SolidColorBrush Error = Reg("error", "#F2B8B5");
    public static readonly SolidColorBrush OnError = Reg("on-error", "#601410");
    public static readonly SolidColorBrush ErrorContainer = Reg("error-container", "#8C1D18");
    public static readonly SolidColorBrush OnErrorContainer = Reg("on-error-container", "#F9DEDC");

    // 五级 surface container —— MD3 靠这个建立层次，而不是靠阴影
    public static readonly SolidColorBrush Surface = Reg("surface", "#121319");
    public static readonly SolidColorBrush SurfaceDim = Reg("surface-dim", "#121319");
    public static readonly SolidColorBrush SurfaceLowest = Reg("surface-container-lowest", "#0D0E15");
    public static readonly SolidColorBrush SurfaceLow = Reg("surface-container-low", "#1A1B21");
    public static readonly SolidColorBrush SurfaceC = Reg("surface-container", "#1E1F25");
    public static readonly SolidColorBrush SurfaceHigh = Reg("surface-container-high", "#292A2F");
    public static readonly SolidColorBrush SurfaceHighest = Reg("surface-container-highest", "#33343A");

    public static readonly SolidColorBrush OnSurface = Reg("on-surface", "#E1E2EA");
    public static readonly SolidColorBrush OnVariant = Reg("on-surface-variant", "#C3C6D5");
    public static readonly SolidColorBrush Outline = Reg("outline", "#8E909E");
    public static readonly SolidColorBrush OutlineVariant = Reg("outline-variant", "#41424A");

    public static readonly SolidColorBrush InverseSurface = Reg("inverse-surface", "#E1E2EA");
    public static readonly SolidColorBrush InverseOnSurface = Reg("inverse-on-surface", "#1A1B21");
    public static readonly SolidColorBrush InversePrimary = Reg("inverse-primary", "#3A5CA4");

    // 语义别名：XAML 里按含义用，颜色仍来自 MD3 令牌
    public static readonly SolidColorBrush Ok = Tertiary;      // 在线/成功 → tertiary
    public static readonly SolidColorBrush Bad = Error;        // 失败/危险 → error
    public static readonly SolidColorBrush TimeText = OnVariant;

    // ── 状态层（MD3 用叠加层表达 hover/press，而不是改底色）─────
    // 颜色跟随 on-surface，只调透明度，所以明暗两套主题都自动适配。
    public static readonly SolidColorBrush StateHover = new(Color.FromArgb(0x14, 0xE1, 0xE2, 0xEA));
    public static readonly SolidColorBrush StateFocus = new(Color.FromArgb(0x1A, 0xE1, 0xE2, 0xEA));
    public static readonly SolidColorBrush StatePress = new(Color.FromArgb(0x1A, 0xE1, 0xE2, 0xEA));
    /// <summary>滚动条滑块：outline 半透明</summary>
    public static readonly SolidColorBrush ScrollThumb = new(Color.FromArgb(0x73, 0x8E, 0x90, 0x9E));
    public static readonly SolidColorBrush ScrollThumbHover = new(Color.FromArgb(0xA6, 0x8E, 0x90, 0x9E));
    /// <summary>对话框遮罩（MD3 scrim）</summary>
    public static readonly SolidColorBrush Scrim = new(Color.FromArgb(0xB3, 0x00, 0x00, 0x00));

    // ── Typography（MD3 Type Scale，单位 DIP）────────────────────
    public static class Type
    {
        public const double DisplayLarge = 57, DisplayLargeLh = 64;
        public const double DisplayMedium = 45, DisplayMediumLh = 52;
        public const double DisplaySmall = 36, DisplaySmallLh = 44;
        public const double HeadlineLarge = 32, HeadlineLargeLh = 40;
        public const double HeadlineMedium = 28, HeadlineMediumLh = 36;
        public const double HeadlineSmall = 24, HeadlineSmallLh = 32;
        public const double TitleLarge = 22, TitleLargeLh = 28;
        public const double TitleMedium = 16, TitleMediumLh = 24;
        public const double TitleSmall = 14, TitleSmallLh = 20;
        public const double BodyLarge = 16, BodyLargeLh = 24;
        public const double BodyMedium = 14, BodyMediumLh = 20;
        public const double BodySmall = 12, BodySmallLh = 16;
        public const double LabelLarge = 14, LabelLargeLh = 20;
        public const double LabelMedium = 12, LabelMediumLh = 16;
        public const double LabelSmall = 11, LabelSmallLh = 16;
    }

    // ── Shape（按组件分级，不是全都大圆角）──────────────────────
    public static class Shape
    {
        public const double None = 0;
        public const double ExtraSmall = 4;   // 输入框 / 菜单 / Snackbar
        public const double Small = 8;        // Chip / 小卡片
        public const double Medium = 12;      // 卡片
        public const double Large = 16;       // 大卡片 / 面板
        public const double ExtraLarge = 28;  // 对话框 / 全屏提醒容器
        public const double Full = 999;       // 按钮 / 开关 / 徽标
    }

    // ── 给 XAML 用的扁平常量 ────────────────────────────────────
    // XAML 的 x:Static 不支持访问嵌套类型：{x:Static local:MdTheme.Shape.Full}
    // 会报 MC3050 "Cannot find the type 'MdTheme.Shape'"。
    // 所以把 XAML 需要的档位以扁平名字再暴露一次；C# 代码里仍用 MdTheme.Type / MdTheme.Shape。
    public const double RadiusNone = Shape.None;
    public const double RadiusExtraSmall = Shape.ExtraSmall;
    public const double RadiusSmall = Shape.Small;
    public const double RadiusMedium = Shape.Medium;
    public const double RadiusLarge = Shape.Large;
    public const double RadiusExtraLarge = Shape.ExtraLarge;
    public const double RadiusFull = Shape.Full;

    public const double FontDisplayLarge = Type.DisplayLarge;
    public const double FontHeadlineMedium = Type.HeadlineMedium;
    public const double FontHeadlineSmall = Type.HeadlineSmall;
    public const double FontTitleLarge = Type.TitleLarge;
    public const double FontTitleMedium = Type.TitleMedium;
    public const double FontTitleSmall = Type.TitleSmall;
    public const double FontBodyLarge = Type.BodyLarge;
    public const double FontBodyMedium = Type.BodyMedium;
    public const double FontBodySmall = Type.BodySmall;
    public const double FontLabelLarge = Type.LabelLarge;
    public const double FontLabelMedium = Type.LabelMedium;
    public const double FontLabelSmall = Type.LabelSmall;

    // ── 明暗模式 ────────────────────────────────────────────────
    public static string CurrentSchemeId { get; private set; } = "indigo";
    /// <summary>实际生效的模式：light / dark（system 已经解析过）</summary>
    public static string CurrentModeId { get; private set; } = "dark";

    /// <summary>读系统「应用」的浅色/深色设置。读不到就按深色（提醒窗口默认深色更合适）。</summary>
    public static bool SystemPrefersLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var v = key?.GetValue("AppsUseLightTheme");
            if (v is int i) return i != 0;
        }
        catch { /* 注册表读不到就回退默认 */ }
        return false;
    }

    public static string ResolveMode(string? pref)
    {
        var p = (pref ?? "system").Trim().ToLowerInvariant();
        if (p == "light" || p == "dark") return p;
        return SystemPrefersLight() ? "light" : "dark";
    }

    // ── 配色枚举（供设置界面列出）───────────────────────────────
    public sealed record Scheme(string Id, string Name, string Preview);

    public static readonly IReadOnlyList<Scheme> Schemes = new[]
    {
        "indigo", "violet", "teal", "green", "amber", "coral", "pink", "cyan",
    }.Select(id => new Scheme(id, SchemeName(id), PreviewColor(id))).ToArray();

    private static string SchemeName(string id) => id switch
    {
        "indigo" => "靛蓝",
        "violet" => "紫罗",
        "teal"   => "青碧",
        "green"  => "松绿",
        "amber"  => "琥珀",
        "coral"  => "珊瑚",
        "pink"   => "品红",
        "cyan"   => "天青",
        _        => id,
    };

    /// <summary>设置界面用的色块预览色（取浅色模式的主色，在明暗两种模式下都看得清）。</summary>
    public static string PreviewColor(string id) => Role(id, "light", "primary") ?? "#888888";

    /// <summary>某套配色在**当前明暗模式**下的主色，用于设置界面的色块。</summary>
    public static Color SchemePrimary(string id) =>
        Parse(Role(id, CurrentModeId, "primary") ?? "#888888");

    /// <summary>当前生效的配色。</summary>
    public static Scheme Current => Find(CurrentSchemeId) ?? Schemes[0];

    /// <summary>兼容旧调用：当前配色 id。</summary>
    public static string CurrentId => CurrentSchemeId;

    /// <summary>昵称配色：同一昵称在网页端 / PC 端永远是同一个颜色。
    /// 哈希算法必须与 web/static/app.js 的 nickColor() 完全一致
    /// （32 位无符号回绕 + 12 色调色板），否则两端会对不上。</summary>
    private static readonly string[] NickPalette =
    {
        "#90CAF9", "#CE93D8", "#80CBC4", "#A5D6A7", "#FFE082", "#FFCC80",
        "#EF9A9A", "#F48FB1", "#9FA8DA", "#80DEEA", "#C5E1A5", "#FFAB91",
    };

    public static Color NickColor(string? name)
    {
        var s = (name ?? "").Trim();
        if (s.Length == 0) return Parse(NickPalette[0]);
        unchecked
        {
            uint h = 0;
            foreach (var ch in s) h = h * 31 + ch;   // 与 JS 的 Math.imul(h,31)+code 等价
            return Parse(NickPalette[(int)(h % (uint)NickPalette.Length)]);
        }
    }

    /// <summary>按比例混合两个颜色（a 占比 1-t，b 占比 t）。</summary>
    public static Color Blend(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    public static Scheme? Find(string? id) =>
        Schemes.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>取某个配色/模式下的颜色角色，取不到返回 null。</summary>
    public static string? Role(string schemeId, string mode, string role)
    {
        if (!MdPalette.All.TryGetValue(schemeId, out var byMode)) return null;
        if (!byMode.TryGetValue(mode, out var table)) return null;
        return table.TryGetValue(role, out var hex) ? hex : null;
    }

    /// <summary>换配色 / 换明暗。就地改写色刷颜色，界面无需重建。</summary>
    public static void Apply(string? schemeId, string? modePref = "system")
    {
        var scheme = Find(schemeId) ?? Schemes[0];
        var mode = ResolveMode(modePref);

        CurrentSchemeId = scheme.Id;
        CurrentModeId = mode;

        foreach (var (role, brush) in _brushes)
        {
            var hex = Role(scheme.Id, mode, role);
            if (hex != null) brush.Color = Parse(hex);
        }

        // 状态层跟着 on-surface 走，只换透明度，明暗两套主题自动适配
        var on = OnSurface.Color;
        StateHover.Color = Color.FromArgb(0x14, on.R, on.G, on.B);
        StateFocus.Color = Color.FromArgb(0x1A, on.R, on.G, on.B);
        StatePress.Color = Color.FromArgb(0x1A, on.R, on.G, on.B);
        var ol = Outline.Color;
        ScrollThumb.Color = Color.FromArgb(0x73, ol.R, ol.G, ol.B);
        ScrollThumbHover.Color = Color.FromArgb(0xA6, ol.R, ol.G, ol.B);
    }

    /// <summary>兼容旧调用：只给配色时沿用当前明暗偏好。</summary>
    public static void Apply(string? id) => Apply(id, CurrentModeId);
}
