# 设计令牌（Design Tokens）

> 本项目的视觉系统遵循 **Material Design 3（Material You）**。
> 网页端与 Windows 端**共用同一份令牌生成器**，改配色只需改生成器再重新生成，
> 两端永远不会走偏。

---

## 1. 单一数据源

```
tools/gen_tokens.py          ← 唯一的人工维护点
        │
        ├── web/static/tokens.css          （网页端 CSS 变量）
        └── pc-agent/FamilyAgent/MdPalette.g.cs （Windows 端 C# 常量）
```

重新生成：

```bash
python3 tools/gen_tokens.py --cs pc-agent/FamilyAgent/MdPalette.g.cs \
        > web/static/tokens.css
```

`MdPalette.g.cs` 与 `tokens.css` 都是**生成产物，不要手改**。

### 一致性校验

生成后应验证两端数值相同（共 8 配色 × 2 模式 × 31 角色 = 496 项）：

```bash
python3 - <<'EOF'
import re
css = open('web/static/tokens.css').read()
cs  = open('pc-agent/FamilyAgent/MdPalette.g.cs').read()
bad = 0
for sch in ['indigo','violet','teal','green','amber','coral','pink','cyan']:
    for mode, sel in [('light', f'[data-scheme="{sch}"]'),
                      ('dark',  f'[data-scheme="{sch}"][data-mode="dark"]')]:
        m = re.search(re.escape(sel) + r'\{(.*?)\n\}', css, re.S)
        cssmap = dict(re.findall(r'--md-([\w-]+):\s*(#[0-9A-F]{6});', m.group(1)))
        blk = re.search(r'\["'+sch+r'"\].*?\["'+mode+r'"\] = new\(\)\s*\{(.*?)\n\s*\},', cs, re.S)
        csmap = dict(re.findall(r'\["([\w-]+)"\] = "(#[0-9A-F]{6})"', blk.group(1)))
        bad += sum(1 for k, v in cssmap.items() if csmap.get(k) != v)
print('OK' if not bad else f'{bad} 项不一致')
EOF
```

---

## 2. 配色怎么算出来的

不是手挑颜色，而是按 MD3 的做法推导：

1. 取源色，转 **CIELAB**，得到色相 H 与彩度 C
2. 在固定的一系列 **tone**（= L\*）上取色，超出 sRGB 色域就逐步降饱和
3. primary 板用原色相（彩度上限 44），secondary 压低彩度（16），tertiary 色相 +60°
4. neutral / neutral-variant 用极低彩度（4 / 8）带一点源色"气味"

亮暗两套角色的 tone 取值依据 MD3 规范：

| 角色 | Light | Dark |
|---|---|---|
| primary | 40 | 80 |
| on-primary | 100 | 20 |
| primary-container | 90 | 30 |
| on-primary-container | 10 | 90 |
| surface | 98 | 6 |
| surface-container-lowest / low / container / high / highest | 100/96/94/92/90 | 4/10/12/17/22 |
| on-surface | 10 | 90 |
| on-surface-variant | 30 | 80 |
| outline / outline-variant | 50 / 80 | 60 / 30 |

error 用 M3 官方值（不随配色变），保证「危险」的语义在任何配色下都稳定。

### 可达性

生成后跑对比度审计，**240/240 全部达标**（正文 ≥4.5:1，非文本 ≥3:1）。
新增配色或改源色后必须重跑。

---

## 3. 8 套配色

| id | 名称 |
|---|---|
| `indigo` | 靛蓝（默认） |
| `violet` | 紫罗 |
| `teal` | 青碧 |
| `green` | 松绿 |
| `amber` | 琥珀 |
| `coral` | 珊瑚 |
| `pink` | 品红 |
| `cyan` | 天青 |

配色与明暗模式**只存本地**，服务端不记录：
- 网页端 → `localStorage`（`fm.scheme` / `fm.mode`）
- Windows 端 → `%APPDATA%\FamilyAgent\config.json`（`theme` / `theme_mode`）

---

## 4. 明暗模式

三选一：**跟随系统（默认） / 浅色 / 深色**。

网页端的做法是把 `system` 解析成确定的 `light`/`dark` 再写进 `data-mode`，
这样 CSS 只需处理两种确定状态，不用把整套暗色值在 media query 里复制一遍：

```js
mqDark.addEventListener('change', () => { if (loadMode() === 'system') applyMode('system'); });
```

Windows 端读注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme`。

---

## 5. 非颜色令牌

| 组 | 前缀 | 说明 |
|---|---|---|
| 排版 | `--md-{display,headline,title,body,label}-*` | MD3 Type Scale 十五级（size / line-height / letter-spacing） |
| 形状 | `--md-shape-{xs,sm,md,lg,xl,full}` | 4 / 8 / 12 / 16 / 28 / 999 |
| 高度 | `--md-elev-{1..5}` | 克制使用；层次主要靠 surface container |
| 间距 | `--md-sp-{1..8}` | 4dp 栅格：4/8/12/16/24/32/48/64 |
| 动效 | `--md-dur-*` / `--md-ease-*` | 150/250/400ms |
| 状态层 | `--md-state-{hover,focus,press}` | 8% / 10% / 10% 叠加层 |
| 无障碍 | `--md-tap` | 48px 最小可点区域 |

### 形状分级（不是所有元素都大圆角）

| 组件 | 半径 |
|---|---|
| 按钮 / Switch / 徽标 | full（MD3 规范如此，按钮本身就是全圆） |
| 输入框 / 菜单 / Snackbar | 4 |
| Chip | 8 |
| 卡片 | 12 |
| 大面板 | 16 |
| 对话框 / Bottom Sheet | 28 |

---

## 6. 昵称配色（跨端一致）

12 色调色板 + 哈希取模。**同一昵称在任何一端都是同一个颜色**，
算法必须三处完全一致：

```js
// web/static/app.js
let h = 0;
for (let i = 0; i < s.length; i++) h = (Math.imul(h, 31) + s.charCodeAt(i)) >>> 0;
return NICK_COLORS[h % NICK_COLORS.length];
```

```csharp
// pc-agent/FamilyAgent/MdTheme.cs
unchecked {
    uint h = 0;
    foreach (var ch in s) h = h * 31 + ch;
    return Parse(NickPalette[(int)(h % (uint)NickPalette.Length)]);
}
```

调色板：
`#90CAF9 #CE93D8 #80CBC4 #A5D6A7 #FFE082 #FFCC80 #EF9A9A #F48FB1 #9FA8DA #80DEEA #C5E1A5 #FFAB91`

实测：妈妈 `#90CAF9`、书房电脑 `#80DEEA`、爸爸 `#FFE082`。

---

## 7. 图标

统一用 **Material Symbols** 的路径数据，内联成 SVG。

- 网页端：`web/index.html` 里的 `<symbol>` 精灵，用 `<use href="#i-xxx">` 引用
- Windows 端：XAML 里的 `Path Data="..."`

**不引入 Google Fonts CDN** —— 这是家庭内网 NAS，不能依赖外网。
**不用 emoji 当 UI 图标**（emoji 仅作为用户消息内容出现，那是用户输入，不是界面图标）。

---

## 8. 组件约定

- 所有颜色/字号/圆角/阴影/间距**一律取自令牌**，组件层不允许写死十六进制色值
- 交互反馈用**状态层**（叠加 `currentColor` 的低透明度），不直接改底色
- 异步操作必须有反馈：`md-spinner` / `md-linear` / Snackbar
- 空状态 = 图标 + 标题 + 下一步提示
- 不用 `alert` / `confirm` / `prompt`，一律走 MD3 对话框与 Snackbar

检查组件层有没有写死色值：

```bash
grep -nE '(background|color|border-color):\s*#[0-9A-Fa-f]{3,6}' web/static/style.css
```
