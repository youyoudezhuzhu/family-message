#!/usr/bin/env python3
"""从 8 个源色生成 Material Design 3 完整色调板（Light + Dark 两套角色）。

MD3 的做法是：把源色转到感知均匀的色彩空间，取其色相，然后按固定的
「tone」（≈CIELAB 的 L*）生成一整条色调板。本脚本用 CIELAB / LCh 实现
同等过程 —— 与 HCT 略有差异，但视觉效果一致，且零依赖、可复现。

产出：web/static/tokens.css 里的 [data-scheme] × [data-mode] 变量块。

用法：python3 tools/gen_tokens.py > web/static/tokens.css   （或加 --check）
"""
import math
import sys

# ── sRGB ↔ CIELAB（D65）────────────────────────────────────────
_M = (
    (0.4123908, 0.3575843, 0.1804808),
    (0.2126390, 0.7151687, 0.0721923),
    (0.0193308, 0.1191948, 0.9505322),
)
_MI = (
    ( 3.2409699, -1.5373832, -0.4986108),
    (-0.9692436,  1.8759675,  0.0415551),
    ( 0.0556301, -0.2039770,  1.0569715),
)
_WHITE = (0.9504559, 1.0, 1.0890578)


def _to_linear(c: float) -> float:
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def _to_gamma(c: float) -> float:
    return 12.92 * c if c <= 0.0031308 else 1.055 * c ** (1 / 2.4) - 0.055


def hex_to_lab(h: str):
    h = h.lstrip("#")
    rgb = [_to_linear(int(h[i:i + 2], 16) / 255) for i in (0, 2, 4)]
    xyz = [sum(_M[i][j] * rgb[j] for j in range(3)) for i in range(3)]
    xyz = [xyz[i] / _WHITE[i] for i in range(3)]

    def f(t):
        return t ** (1 / 3) if t > 216 / 24389 else (24389 / 27 * t + 16) / 116

    fx, fy, fz = f(xyz[0]), f(xyz[1]), f(xyz[2])
    return 116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz)


def lab_to_hex(L: float, a: float, b: float) -> str:
    fy = (L + 16) / 116
    fx, fz = fy + a / 500, fy - b / 200

    def finv(t):
        t3 = t ** 3
        return t3 if t3 > 216 / 24389 else (116 * t - 16) * 27 / 24389

    xyz = [finv(fx) * _WHITE[0], finv(fy) * _WHITE[1], finv(fz) * _WHITE[2]]
    lin = [sum(_MI[i][j] * xyz[j] for j in range(3)) for i in range(3)]
    rgb = [min(1.0, max(0.0, _to_gamma(v))) for v in lin]
    return "#" + "".join(f"{round(v * 255):02X}" for v in rgb)


def in_gamut(L, C, H):
    """把 LCh 转成 sRGB，若任一分量越界则返回 None。"""
    a = C * math.cos(math.radians(H))
    b = C * math.sin(math.radians(H))
    fy = (L + 16) / 116
    fx, fz = fy + a / 500, fy - b / 200

    def finv(t):
        t3 = t ** 3
        return t3 if t3 > 216 / 24389 else (116 * t - 16) * 27 / 24389

    xyz = [finv(fx) * _WHITE[0], finv(fy) * _WHITE[1], finv(fz) * _WHITE[2]]
    lin = [sum(_MI[i][j] * xyz[j] for j in range(3)) for i in range(3)]
    return all(-0.001 <= v <= 1.001 for v in lin)


def tone(L: float, chroma: float, hue: float) -> str:
    """在给定 tone(L*) 上，尽量取满 chroma；超出色域就逐步降饱和。"""
    c = chroma
    while c > 0 and not in_gamut(L, c, hue):
        c -= 0.5
    return lab_to_hex(L, c * math.cos(math.radians(hue)), c * math.sin(math.radians(hue)))


# ── 8 套配色：源色 + 名称 ───────────────────────────────────────
SCHEMES = [
    ("indigo", "靛蓝", "#4C6FBF"),
    ("violet", "紫罗", "#7B5FC4"),
    ("teal",   "青碧", "#1F7A80"),
    ("green",  "松绿", "#3F7D4E"),
    ("amber",  "琥珀", "#8A6200"),
    ("coral",  "珊瑚", "#B4544A"),
    ("pink",   "品红", "#A8487A"),
    ("cyan",   "天青", "#256C93"),
]

# tone 取值（MD3 官方调色板用的档位）
T = dict(t4=4, t6=6, t10=10, t12=12, t17=17, t20=20, t22=22, t24=24,
         t30=30, t40=40, t50=50, t60=60, t70=70, t80=80,
         t87=87, t90=90, t92=92, t94=94, t95=95, t96=96, t98=98, t99=99, t100=100)


def roles_for(src_hex: str):
    """返回该源色在 light / dark 两种模式下的全部 MD3 颜色角色。"""
    L0, a0, b0 = hex_to_lab(src_hex)
    hue = math.degrees(math.atan2(b0, a0)) % 360
    chroma = math.hypot(a0, b0)

    # MD3：主色板饱和度较高，次色板压低，第三色板换色相
    C_P, C_S, C_T = min(chroma, 44), 16, 26
    C_N, C_NV = 4, 8          # neutral / neutral-variant：极低饱和（带一点源色气味）
    hue_t = (hue + 60) % 360

    p = {k: tone(v, C_P, hue) for k, v in T.items()}
    s = {k: tone(v, C_S, hue) for k, v in T.items()}
    t = {k: tone(v, C_T, hue_t) for k, v in T.items()}
    n = {k: tone(v, C_N, hue) for k, v in T.items()}
    nv = {k: tone(v, C_NV, hue) for k, v in T.items()}

    # error 用 M3 官方值（不随配色变），保证「危险」的语义稳定
    E_L = {"e40": "#B3261E", "e90": "#F9DEDC", "e10": "#410E0B", "e100": "#FFFFFF"}
    E_D = {"e80": "#F2B8B5", "e30": "#8C1D18", "e90": "#F9DEDC", "e20": "#601410"}

    light = {
        "primary": p["t40"], "on-primary": p["t100"],
        "primary-container": p["t90"], "on-primary-container": p["t10"],
        "secondary": s["t40"], "on-secondary": s["t100"],
        "secondary-container": s["t90"], "on-secondary-container": s["t10"],
        "tertiary": t["t40"], "on-tertiary": t["t100"],
        "tertiary-container": t["t90"], "on-tertiary-container": t["t10"],
        "error": E_L["e40"], "on-error": E_L["e100"],
        "error-container": E_L["e90"], "on-error-container": E_L["e10"],
        "surface": n["t98"], "surface-dim": n["t87"],
        "surface-container-lowest": n["t100"],
        "surface-container-low": n["t96"],
        "surface-container": n["t94"],
        "surface-container-high": n["t92"],
        "surface-container-highest": n["t90"],
        "on-surface": n["t10"], "on-surface-variant": nv["t30"],
        "outline": nv["t50"], "outline-variant": nv["t80"],
        "inverse-surface": n["t20"], "inverse-on-surface": n["t95"],
        "inverse-primary": p["t80"],
        "surface-tint": p["t40"],
    }
    dark = {
        "primary": p["t80"], "on-primary": p["t20"],
        "primary-container": p["t30"], "on-primary-container": p["t90"],
        "secondary": s["t80"], "on-secondary": s["t20"],
        "secondary-container": s["t30"], "on-secondary-container": s["t90"],
        "tertiary": t["t80"], "on-tertiary": t["t20"],
        "tertiary-container": t["t30"], "on-tertiary-container": t["t90"],
        "error": E_D["e80"], "on-error": E_D["e20"],
        "error-container": E_D["e30"], "on-error-container": E_D["e90"],
        "surface": n["t6"], "surface-dim": n["t6"],
        "surface-container-lowest": n["t4"],
        "surface-container-low": n["t10"],
        "surface-container": n["t12"],
        "surface-container-high": n["t17"],
        "surface-container-highest": n["t22"],
        "on-surface": n["t90"], "on-surface-variant": nv["t80"],
        "outline": nv["t60"], "outline-variant": nv["t30"],
        "inverse-surface": n["t90"], "inverse-on-surface": n["t20"],
        "inverse-primary": p["t40"],
        "surface-tint": p["t80"],
    }
    return light, dark


REFERENCE = """
/* ═══════════════════════════════════════════════════════════════
   非颜色令牌：排版 / 形状 / 高度 / 间距 / 动效 / 状态层
   规范 §6 §7 §8 §25 —— 组件一律从这里取值，不允许自己写死
   ═══════════════════════════════════════════════════════════════ */
:root{
  /* ── Typography（MD3 Type Scale，字号/行高/字距）──────────── */
  --md-font: "Roboto","Segoe UI",-apple-system,BlinkMacSystemFont,
             "PingFang SC","Hiragino Sans GB","Microsoft YaHei",sans-serif;
  --md-font-mono: "Roboto Mono",ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;

  --md-display-large-size:3.5625rem;  --md-display-large-lh:4rem;    --md-display-large-ls:-0.25px;
  --md-display-medium-size:2.8125rem; --md-display-medium-lh:3.25rem;--md-display-medium-ls:0;
  --md-display-small-size:2.25rem;    --md-display-small-lh:2.75rem; --md-display-small-ls:0;
  --md-headline-large-size:2rem;      --md-headline-large-lh:2.5rem; --md-headline-large-ls:0;
  --md-headline-medium-size:1.75rem;  --md-headline-medium-lh:2.25rem;--md-headline-medium-ls:0;
  --md-headline-small-size:1.5rem;    --md-headline-small-lh:2rem;   --md-headline-small-ls:0;
  --md-title-large-size:1.375rem;     --md-title-large-lh:1.75rem;   --md-title-large-ls:0;
  --md-title-medium-size:1rem;        --md-title-medium-lh:1.5rem;   --md-title-medium-ls:0.15px;
  --md-title-small-size:0.875rem;     --md-title-small-lh:1.25rem;  --md-title-small-ls:0.1px;
  --md-body-large-size:1rem;          --md-body-large-lh:1.5rem;    --md-body-large-ls:0.5px;
  --md-body-medium-size:0.875rem;     --md-body-medium-lh:1.25rem;  --md-body-medium-ls:0.25px;
  --md-body-small-size:0.75rem;       --md-body-small-lh:1rem;      --md-body-small-ls:0.4px;
  --md-label-large-size:0.875rem;     --md-label-large-lh:1.25rem;  --md-label-large-ls:0.1px;
  --md-label-medium-size:0.75rem;     --md-label-medium-lh:1rem;    --md-label-medium-ls:0.5px;
  --md-label-small-size:0.6875rem;    --md-label-small-lh:1rem;     --md-label-small-ls:0.5px;

  /* ── Shape（按组件类型分级，不是全都大圆角）─────────────── */
  --md-shape-none:0;
  --md-shape-xs:4px;        /* 输入框 / 菜单 / Snackbar / 工具提示 */
  --md-shape-sm:8px;        /* Chip / 小卡片 */
  --md-shape-md:12px;       /* Card / 列表容器 */
  --md-shape-lg:16px;       /* FAB / 大卡片 / 面板 */
  --md-shape-xl:28px;       /* Dialog / Bottom Sheet 顶部 */
  --md-shape-full:999px;    /* Button / Switch / 徽标 */

  /* ── Elevation（克制；层次主要靠 surface 容器）──────────── */
  --md-elev-0:none;
  --md-elev-1:0 1px 2px rgba(0,0,0,.30), 0 1px 3px 1px rgba(0,0,0,.15);
  --md-elev-2:0 1px 2px rgba(0,0,0,.30), 0 2px 6px 2px rgba(0,0,0,.15);
  --md-elev-3:0 4px 8px 3px rgba(0,0,0,.15), 0 1px 3px rgba(0,0,0,.30);
  --md-elev-4:0 6px 10px 4px rgba(0,0,0,.15), 0 2px 3px rgba(0,0,0,.30);
  --md-elev-5:0 8px 12px 6px rgba(0,0,0,.15), 0 4px 4px rgba(0,0,0,.30);

  /* ── Spacing（4dp 栅格）────────────────────────────────── */
  --md-sp-1:4px; --md-sp-2:8px; --md-sp-3:12px; --md-sp-4:16px;
  --md-sp-5:24px; --md-sp-6:32px; --md-sp-7:48px; --md-sp-8:64px;

  /* ── Motion（快、自然、克制）──────────────────────────── */
  --md-dur-short:150ms; --md-dur-medium:250ms; --md-dur-long:400ms;
  --md-ease-standard:cubic-bezier(.2,0,0,1);
  --md-ease-emphasized:cubic-bezier(.2,0,0,1);
  --md-ease-decelerate:cubic-bezier(0,0,0,1);

  /* ── State layer（交互反馈叠加层不透明度）───────────────── */
  --md-state-hover:0.08; --md-state-focus:0.10;
  --md-state-press:0.10; --md-state-drag:0.16;

  /* ── 布局 ─────────────────────────────────────────────── */
  --md-maxw:1440px;
  --md-tap:48px;                 /* 最小可点区域，无障碍要求 */
}
"""


def emit_cs(out_path: str) -> None:
    """把同一份调色板导成 C#，供 Windows Agent 使用。

    两端共用一个生成器，就不会出现「网页改了色、PC 端还是旧色」这种漂移。
    """
    L = []
    w = L.append
    w("// <auto-generated>")
    w("//   由 tools/gen_tokens.py 生成，请勿手改。")
    w("//   与网页端 web/static/tokens.css 同源 —— 改配色请改生成器再重新生成。")
    w("// </auto-generated>")
    w("namespace FamilyAgent;")
    w("")
    w("internal static class MdPalette")
    w("{")
    w("    /// <summary>配色 id → 模式(light/dark) → 颜色角色 → #RRGGBB</summary>")
    w("    internal static readonly Dictionary<string, Dictionary<string, Dictionary<string, string>>> All = new()")
    w("    {")
    for key, name, src in SCHEMES:
        light, dark = roles_for(src)
        w(f'        ["{key}"] = new()   // {name}')
        w("        {")
        for mode, table in (("light", light), ("dark", dark)):
            w(f'            ["{mode}"] = new()')
            w("            {")
            for k, v in table.items():
                w(f'                ["{k}"] = "{v}",')
            w("            },")
        w("        },")
    w("    };")
    w("}")
    with open(out_path, "w", encoding="utf-8") as fh:
        fh.write("\n".join(L) + "\n")


def main():
    out = []
    w = out.append
    w("/* ═══════════════════════════════════════════════════════════════")
    w("   MD3 色彩令牌 —— 由 tools/gen_tokens.py 生成，请勿手改")
    w("   8 套配色 × Light/Dark。每条色板从源色按 CIELAB 各 tone 推导，")
    w("   与 Material Design 3 的色调板做法一致。")
    w("   ═══════════════════════════════════════════════════════════════ */")
    w("")

    for key, name, src in SCHEMES:
        light, dark = roles_for(src)
        w(f"/* ── {name} ─────────────────────────────────── */")
        w(f'[data-scheme="{key}"]{{')
        for k, v in light.items():
            w(f"  --md-{k}: {v};")
        w("}")
        w(f'[data-scheme="{key}"][data-mode="dark"]{{')
        for k, v in dark.items():
            w(f"  --md-{k}: {v};")
        w("}")
        w("")

    w(REFERENCE)

    if "--cs" in sys.argv:
        target = sys.argv[sys.argv.index("--cs") + 1]
        emit_cs(target)
        print(f"[gen_tokens] C# 调色板已写出：{target}", file=sys.stderr)

    sys.stdout.write("\n".join(out))


if __name__ == "__main__":
    main()
