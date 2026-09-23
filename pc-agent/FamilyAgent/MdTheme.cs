using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace FamilyAgent;

/// <summary>
/// Material Design 3 设计令牌（Windows 端）。
///
/// 颜色一律来自 MdPalette.g.cs —— 那份文件由 tools/gen_tokens.py 生成，
/// 与网页端的 web/static/tokens.css **同源**，两端配色永远一致。
///
/// ⚠️ 关键设计：**画刷是不可变的，换主题时换新实例，绝不改旧实例的颜色。**
///
/// 为什么：XAML 里如果用 {x:Static} 把画刷引用进 Style / ControlTemplate，
/// WPF 在密封这些样式时会顺手把其中的 Freezable（画刷）冻结，之后
/// brush.Color = ... 会抛「无法在该对象上设置属性，因为它处于只读状态」。
/// 所以这里改为：
///   XAML 用 {DynamicResource MdPrimary} 引用 → 由 XAML 负责跟着资源变化刷新
///   换主题 → 生成一批全新画刷 → 写进 Application.Resources → 界面自动更新
/// </summary>
public static class MdTheme
{
    public static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

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

    // ── 资源键 → 颜色角色 ────────────────────────────────────────
    // XAML 里用 {DynamicResource <键>} 引用；键名 = "Md" + 下面的角色名。
    private static readonly (string Key, string Role)[] Map =
    {
        ("MdPrimary",              "primary"),
        ("MdOnPrimary",            "on-primary"),
        ("MdPrimaryContainer",     "primary-container"),
        ("MdOnPrimaryContainer",   "on-primary-container"),
        ("MdSecondary",            "secondary"),
        ("MdSecondaryContainer",   "secondary-container"),
        ("MdOnSecondaryContainer", "on-secondary-container"),
        ("MdTertiary",             "tertiary"),
        ("MdOnTertiary",           "on-tertiary"),
        ("MdTertiaryContainer",    "tertiary-container"),
        ("MdOnTertiaryContainer",  "on-tertiary-container"),
        ("MdError",                "error"),
        ("MdOnError",              "on-error"),
        ("MdErrorContainer",       "error-container"),
        ("MdOnErrorContainer",     "on-error-container"),
        ("MdSurface",              "surface"),
        ("MdSurfaceDim",           "surface-dim"),
        ("MdSurfaceLowest",        "surface-container-lowest"),
        ("MdSurfaceLow",           "surface-container-low"),
        ("MdSurfaceC",             "surface-container"),
        ("MdSurfaceHigh",          "surface-container-high"),
        ("MdSurfaceHighest",       "surface-container-highest"),
        ("MdOnSurface",            "on-surface"),
        ("MdOnVariant",            "on-surface-variant"),
        ("MdOutline",              "outline"),
        ("MdOutlineVariant",       "outline-variant"),
        ("MdInverseSurface",       "inverse-surface"),
        ("MdInverseOnSurface",     "inverse-on-surface"),
        ("MdInversePrimary",       "inverse-primary"),
    };

    // 语义别名（指向上面某个键，方便按含义使用）
    private static readonly Dictionary<string, string> Alias = new(StringComparer.Ordinal)
    {
        ["MdBad"] = "MdError",             // 危险 / 失败
        ["MdOk"] = "MdTertiary",           // 在线 / 成功
        ["MdTimeText"] = "MdOnVariant",    // 时间戳
    };

    // 不随配色变的固定色
    private static readonly Dictionary<string, string> Fixed = new(StringComparer.Ordinal)
    {
        ["MdScrim"] = "#B3000000",         // 对话框遮罩
    };

    /// <summary>当前这批画刷。换主题时整体换成新实例，旧实例不再改动。</summary>
    private static Dictionary<string, SolidColorBrush> _brushes = new(StringComparer.Ordinal);

    /// <summary>取画刷；键不存在时返回一个安全的兜底色。</summary>
    private static SolidColorBrush Get(string key)
    {
        if (_brushes.TryGetValue(key, out var b)) return b;
        if (Alias.TryGetValue(key, out var target)) return Get(target);
        return new SolidColorBrush(Colors.Magenta);   // 明显不对的颜色，方便发现问题
    }

    // ── C# 侧访问器（XAML 侧请用 {DynamicResource MdXxx}）─────────
    public static SolidColorBrush Primary => Get("MdPrimary");
    public static SolidColorBrush OnPrimary => Get("MdOnPrimary");
    public static SolidColorBrush PrimaryContainer => Get("MdPrimaryContainer");
    public static SolidColorBrush OnPrimaryContainer => Get("MdOnPrimaryContainer");
    public static SolidColorBrush Secondary => Get("MdSecondary");
    public static SolidColorBrush SecondaryContainer => Get("MdSecondaryContainer");
    public static SolidColorBrush OnSecondaryContainer => Get("MdOnSecondaryContainer");
    public static SolidColorBrush Tertiary => Get("MdTertiary");
    public static SolidColorBrush Error => Get("MdError");
    public static SolidColorBrush OnError => Get("MdOnError");
    public static SolidColorBrush ErrorContainer => Get("MdErrorContainer");
    public static SolidColorBrush OnErrorContainer => Get("MdOnErrorContainer");

    public static SolidColorBrush Surface => Get("MdSurface");
    public static SolidColorBrush SurfaceDim => Get("MdSurfaceDim");
    public static SolidColorBrush SurfaceLowest => Get("MdSurfaceLowest");
    public static SolidColorBrush SurfaceLow => Get("MdSurfaceLow");
    public static SolidColorBrush SurfaceC => Get("MdSurfaceC");
    public static SolidColorBrush SurfaceHigh => Get("MdSurfaceHigh");
    public static SolidColorBrush SurfaceHighest => Get("MdSurfaceHighest");
    public static SolidColorBrush OnSurface => Get("MdOnSurface");
    public static SolidColorBrush OnVariant => Get("MdOnVariant");
    public static SolidColorBrush Outline => Get("MdOutline");
    public static SolidColorBrush OutlineVariant => Get("MdOutlineVariant");
    public static SolidColorBrush InverseSurface => Get("MdInverseSurface");
    public static SolidColorBrush InverseOnSurface => Get("MdInverseOnSurface");
    public static SolidColorBrush InversePrimary => Get("MdInversePrimary");

    public static SolidColorBrush Ok => Get("MdOk");
    public static SolidColorBrush Bad => Get("MdBad");
    public static SolidColorBrush TimeText => Get("MdTimeText");
    public static SolidColorBrush Scrim => Get("MdScrim");
    public static SolidColorBrush StateHover => Get("MdStateHover");
    public static SolidColorBrush StateFocus => Get("MdStateFocus");
    public static SolidColorBrush StatePress => Get("MdStatePress");
    public static SolidColorBrush ScrollThumb => Get("MdScrollThumb");
    public static SolidColorBrush ScrollThumbHover => Get("MdScrollThumbHover");

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
    // 会报 MC3050。所以把 XAML 需要的档位以扁平名字再暴露一次；
    // C# 里仍用 MdTheme.Type / MdTheme.Shape。
    // ⚠️ 这里必须是 CornerRadius 而不是 double。
    //
    // XAML 里 {x:Static} 的返回值**不会走类型转换**：传一个 double 常量给
    // Border.CornerRadius（类型是 CornerRadius 结构），WPF 会把 double 原样
    // 存进属性；等到布局阶段 Border.ArrangeOverride → get_CornerRadius()
    // 拆箱时才炸，抛 InvalidCastException: Specified cast is not valid。
    // 异常发生在 Show() 内部，所以现象是「窗口永远打不开」——
    // 而它出现在 EnsureShown 里，跟窗口逻辑本身看起来毫无关系，极难定位。
    // （写死字面量 CornerRadius="999" 反而没事，因为字面量会走类型转换。）
    public static readonly CornerRadius RadiusNone = new(Shape.None);
    public static readonly CornerRadius RadiusExtraSmall = new(Shape.ExtraSmall);
    public static readonly CornerRadius RadiusSmall = new(Shape.Small);
    public static readonly CornerRadius RadiusMedium = new(Shape.Medium);
    public static readonly CornerRadius RadiusLarge = new(Shape.Large);
    public static readonly CornerRadius RadiusExtraLarge = new(Shape.ExtraLarge);
    public static readonly CornerRadius RadiusFull = new(Shape.Full);

    // Font* 保持 double —— FontSize 属性本身就是 double，类型是对得上的。
    public const double FontDisplayLarge = Type.DisplayLarge;
    public const double FontHeadlineLarge = Type.HeadlineLarge;
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
    /// <summary>用户选择的模式偏好：system / light / dark</summary>
    public static string CurrentModePref { get; private set; } = "system";

    /// <summary>读系统「应用」的浅色/深色设置。读不到就按深色。</summary>
    public static bool SystemPrefersLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var v = key?.GetValue("AppsUseLightTheme");
            if (v is int i) return i != 0;
        }
        catch { /* 读不到就回退默认 */ }
        return false;
    }

    public static string ResolveMode(string? pref)
    {
        var p = (pref ?? "system").Trim().ToLowerInvariant();
        if (p == "light" || p == "dark") return p;
        return SystemPrefersLight() ? "light" : "dark";
    }

    // ── 配色枚举 ────────────────────────────────────────────────
    public sealed record Scheme(string Id, string Name, string Preview);

    public static readonly IReadOnlyList<Scheme> Schemes = new[]
    {
        "indigo", "violet", "teal", "green", "amber", "coral", "pink", "cyan",
    }.Select(id => new Scheme(id, SchemeName(id), string.Empty)).ToArray();

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

    /// <summary>当前生效的配色。</summary>
    public static Scheme Current => Find(CurrentSchemeId) ?? Schemes[0];

    /// <summary>兼容旧调用：当前配色 id。</summary>
    public static string CurrentId => CurrentSchemeId;

    public static Scheme? Find(string? id) =>
        Schemes.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>取某个配色/模式下的颜色角色，取不到返回 null。</summary>
    public static string? Role(string schemeId, string mode, string role)
    {
        if (!MdPalette.All.TryGetValue(schemeId, out var byMode)) return null;
        if (!byMode.TryGetValue(mode, out var table)) return null;
        return table.TryGetValue(role, out var hex) ? hex : null;
    }

    /// <summary>某套配色在**当前明暗模式**下的主色，用于设置界面的色块。</summary>
    public static Color SchemePrimary(string id) =>
        Parse(Role(id, CurrentModeId, "primary") ?? "#888888");

    // ── 昵称配色 ────────────────────────────────────────────────
    /// <summary>昵称配色：同一昵称在网页端 / PC 端永远是同一个颜色。
    /// 哈希算法必须与 web/static/app.js 的 nickColor() 完全一致。</summary>
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

    // ── 生成并发布一批新画刷 ────────────────────────────────────
    /// <summary>
    /// 构建整套画刷。**每次都返回全新实例** —— 旧实例可能已被 WPF 冻结，
    /// 绝不能再改它。
    /// </summary>
    private static Dictionary<string, SolidColorBrush> Build(string schemeId, string mode)
    {
        var map = new Dictionary<string, SolidColorBrush>(StringComparer.Ordinal);

        foreach (var (key, role) in Map)
        {
            var hex = Role(schemeId, mode, role) ?? "#FF00FF";
            map[key] = new SolidColorBrush(Parse(hex));
        }

        // 状态层：跟随 on-surface，只调透明度，明暗两套都自动适配
        var on = map["MdOnSurface"].Color;
        map["MdStateHover"] = new SolidColorBrush(Color.FromArgb(0x14, on.R, on.G, on.B));
        map["MdStateFocus"] = new SolidColorBrush(Color.FromArgb(0x1A, on.R, on.G, on.B));
        map["MdStatePress"] = new SolidColorBrush(Color.FromArgb(0x1A, on.R, on.G, on.B));

        // 滚动条滑块：outline 半透明
        var ol = map["MdOutline"].Color;
        map["MdScrollThumb"] = new SolidColorBrush(Color.FromArgb(0x73, ol.R, ol.G, ol.B));
        map["MdScrollThumbHover"] = new SolidColorBrush(Color.FromArgb(0xA6, ol.R, ol.G, ol.B));

        return map;
    }

    /// <summary>把画刷写进 Application.Resources，XAML 的 DynamicResource 会自动跟上。</summary>
    private static void Publish(Dictionary<string, SolidColorBrush> map)
    {
        var app = Application.Current;
        if (app is null) return;   // 理论上 WPF 里不会为 null，防一手

        foreach (var (key, brush) in map)
            app.Resources[key] = brush;

        foreach (var (alias, target) in Alias)
            if (map.TryGetValue(target, out var b)) app.Resources[alias] = b;

        foreach (var (key, hex) in Fixed)
            app.Resources[key] = new SolidColorBrush(Parse(hex));
    }

    /// <summary>
    /// 换配色 / 换明暗。生成一批**全新**画刷并发布到 Application.Resources，
    /// 界面靠 DynamicResource 自动刷新（不修改任何旧画刷 —— 它们可能已被冻结）。
    /// </summary>
    public static void Apply(string? schemeId, string? modePref = "system")
    {
        var scheme = Find(schemeId) ?? Schemes[0];
        var mode = ResolveMode(modePref);

        CurrentSchemeId = scheme.Id;
        CurrentModeId = mode;
        CurrentModePref = (modePref ?? "system").Trim().ToLowerInvariant();

        var map = Build(scheme.Id, mode);
        _brushes = map;
        Publish(map);
    }

    /// <summary>兼容旧调用：只给配色时沿用当前明暗偏好。</summary>
    public static void Apply(string? id) => Apply(id, CurrentModePref);

    /// <summary>首次使用前的兜底初始化（XAML 解析早于 Apply 时也能取到颜色）。</summary>
    static MdTheme()
    {
        _brushes = Build("indigo", "dark");
        // 也发布一次：XAML 有可能早于 Apply 被解析，DynamicResource 需要键已存在。
        // Application.Current 还没建好时会被内部判空挡掉，下次 Apply 再补。
        Publish(_brushes);
    }
}
