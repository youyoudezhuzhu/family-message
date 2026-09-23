# 视觉规范（Material Design 3 · 设计令牌）

> 网页端、Windows Agent、以及未来的 Android 端**共用同一套令牌**。
> 改颜色只改这里的值，三端同步。
>
> 现状对照：网页端在 `web/static/style.css` 的 `:root`；
> Windows 端在 `pc-agent/FamilyAgent/MdTheme.cs`。

---

## 0. 风格基调

**Material Design 3（Material You）暗色主题。** 不是「深色皮」——要按 MD3 的规范来：

- 用 **surface 层级**表达高度（surface → containerLow → container → containerHigh），
  而不是靠阴影和描边。MD3 暗色下阴影几乎不可见，层次由「颜色亮度」承担。
- 圆角偏大：卡片 12、对话框 28、按钮/输入框**胶囊或 12**（不再用 9）
- 按钮：**filled（实心）/ outlined（描边）/ text（纯文字）** 三种，胶囊形
- 输入框：**outlined 风格**——1px 描边 + 聚焦时描边变主题色并加粗到 2px
- 状态反馈用 **state layer**（悬停 +8%、按下 +12% 的白色覆盖），不做复杂渐变
- 字体用系统默认（Roboto / 微软雅黑 / PingFang），**不打包字体文件**
- 节奏：MD3 的动效是 **emphasized** 曲线，位移短、时间略长

---

## 1. 基础色板（MD3 暗色 surface 体系）

| 令牌 | 值 | 用途 |
|---|---|---|
| `surface` | `#141218` | 页面底 |
| `surfaceContainerLow` | `#1D1B20` | 卡片、面板 |
| `surfaceContainer` | `#211F26` | 输入框底、次级面板 |
| `surfaceContainerHigh` | `#2B2930` | 对话框、下拉浮层、悬停态 |
| `surfaceContainerHighest` | `#36343B` | 最高层浮层 |
| `onSurface` | `#E6E0E9` | 主文字 |
| `onSurfaceVariant` | `#CAC4D0` | 次要文字、说明 |
| `outline` | `#938F99` | 输入框描边 |
| `outlineVariant` | `#49454F` | 分隔线、弱描边 |

> 这套是 MD3 baseline dark 的官方值，别自己调。

---

## 2. 配色方案（设置里可切换）

主题色只由 **primary 组**定义，surface 体系共用 §1。切换主题 = 换 primary 组。

| id | 名称 | primary | primaryContainer | onPrimaryContainer |
|---|---|---|---|---|
| `indigo` | 靛蓝（默认） | `#A8C7FA` | `#0842A0` | `#D3E3FD` |
| `violet` | 紫罗 | `#D0BCFF` | `#4F378B` | `#EADDFF` |
| `teal` | 青碧 | `#80DEEA` | `#004F58` | `#B2EBF2` |
| `green` | 松绿 | `#A5D6A7` | `#1B5E20` | `#C8E6C9` |
| `amber` | 琥珀 | `#FFD54F` | `#6D4C00` | `#FFECB3` |
| `coral` | 珊瑚 | `#FFB4AB` | `#93000A` | `#FFDAD6` |
| `pink` | 品红 | `#F8BBD0` | `#880E4F` | `#FCE4EC` |
| `cyan` | 天青 | `#90CAF9` | `#0B4F6C` | `#CDE7FF` |

用法：
- `primary` → 按钮/选中态/聚焦描边的**前景**；胶囊实心按钮的**底色**
  （注意 MD3 暗色下 primary 是**浅色**，按钮文字要用深色 `#0B1020`）
- `primaryContainer` → 选中项的底
- `onPrimaryContainer` → 选中项的字

主题**只存本地**（网页 localStorage / PC 配置文件），服务端不参与 —— 和昵称一个道理。

---

## 3. 昵称色（按昵称分配，跨端一致）

**同一条消息的颜色由「昵称」决定，不再由「从哪端发来」决定。**
同一个昵称在任何一端、任何时候都是同一个颜色。

12 色调色板（在 §1 的深色 surface 上都有足够对比度）：

```
0  #90CAF9  蓝        6  #EF9A9A  红
1  #CE93D8  紫        7  #F48FB1  粉
2  #80CBC4  青绿      8  #9FA8DA  靛
3  #A5D6A7  绿        9  #80DEEA  天青
4  #FFE082  黄       10  #C5E1A5  黄绿
5  #FFCC80  橙       11  #FFAB91  深橙
```

**取色算法（三端必须完全一致，否则同一昵称在不同端颜色不同）**：

```text
h = 0
对昵称的每个 UTF-16 码元 c：
    h = (h * 31 + c) mod 2^32          # 32 位无符号回绕
索引 = h mod 12
昵称为空 → 索引 0
```

- JavaScript：`h = (Math.imul(h, 31) + c) >>> 0`
- C#：`unchecked { h = h * 31 + c; }`，`h` 是 `uint`
- Kotlin/Java：`h = h * 31 + c`（int 自然回绕），取 `(h & 0xFFFFFFFFL) % 12`

**用在哪里**：昵称文字色、头像底/字、气泡描边或气泡淡色底。
**不要**把正文染成昵称色 —— 正文保持 `onSurface` 的高对比度，只让「身份标识」带色。

---

## 4. 圆角与间距

| 元素 | 圆角 |
|---|---|
| 卡片 / 面板 | 12 |
| 对话框 / 大浮层 | 28 |
| 按钮（胶囊） | 999 |
| 按钮（方形样式） | 12 |
| 输入框 | 12 |
| 气泡 | 16（贴角一侧 4） |
| 小标签 | 8 |

间距基准 4 的倍数：`8`（紧邻）、`12`、`16`、`24`（分区）、`32`。

---

## 5. 字号（MD3 type scale 精简版）

| 场景 | 字号 |
|---|---|
| 页面标题 | 22（titleLarge） |
| 卡片/区块标题 | 16（titleMedium） |
| 正文 | 14–15（bodyMedium） |
| 说明/时间戳 | 12（bodySmall / labelSmall） |
| 按钮 | 14（labelLarge） |
| PC 弹窗消息正文 | 20–30，随内容长度自适应 |

---

## 6. 滚动条（MD 风格）

**默认白色滚动条是必须消灭的**（浏览器/WPF 默认在暗色下非常刺眼）。

规范：**细、无轨道底、圆头、半透明，悬停变亮**。

- 宽/高：`8px`
- 滑道：透明
- 滑块：`rgba(230,224,233,0.28)`，圆角 4；悬停 `rgba(230,224,233,0.45)`
- 网页：`::-webkit-scrollbar` + `scrollbar-width: thin`（Firefox）
- WPF：**默认 ScrollBar 模板无法只靠设属性改观**，必须整体替换 `ControlTemplate`
  （Thumb 去箭头、去轨道底、设圆角），做法见 §8

---

## 7. 组件规范

### 按钮

| 类型 | 底 | 字 | 用途 |
|---|---|---|---|
| filled | `primary` | `#0B1020` | 主操作（发送、保存） |
| tonal | `primaryContainer` | `onPrimaryContainer` | 次主操作 |
| outlined | 透明 + `outline` 描边 | `primary` | 次要操作 |
| text | 透明 | `primary` | 轻量操作、取消 |
| danger | `#FFB4AB` | `#0B1020` | 删除 |

胶囊圆角，内边距 `20×10`。悬停加 8% 白，按下 12%。

### 输入框（outlined）

- 底 `surfaceContainer`，1px `outline` 描边，圆角 12
- 聚焦：描边 `primary` 2px
- 光标色 `primary`

### 卡片

`surfaceContainerLow` 底，圆角 12，**无描边**，靠亮度与底色区分。
MD3 暗色下不要用重阴影。

### 对话框

`surfaceContainerHigh` 底，圆角 28，宽 `min(560px, 92vw)`。

### 状态点

直径 10 圆点。在线 `#A5D6A7`，离线 `#EF9A9A`。

---

## 8. WPF 端特别提醒

1. **ComboBox 只设 `Background` 无效** —— 默认模板的浮层用系统画刷，暗色下必然白底。
   必须整体替换 `ControlTemplate`（ToggleButton + Popup + ComboBoxItem 三层）。
2. **ScrollBar 同理** —— 默认模板带箭头按钮和白色轨道，必须整体替换。
   最省事的做法是替换 `ScrollViewer` 内部 `ScrollBar` 的样式（隐式样式 + `x:Key`）。
3. `WindowStyle="None"` 全屏弹窗 + `Topmost` 的组合下，动画用 `CubicEase/EaseOut`，
   时长 240–360ms，别用更长的（全屏滑块会显得迟钝）。

---

## 9. 移植检查清单（Android）

- [ ] 用 MD3 主题（`Theme.Material3.DayNight`），surface 体系按 §1
- [ ] 主题色切换映射到 §2 的 primary 组（动态色可另加，但默认要跟这两端一致）
- [ ] 昵称色用 §3 的算法（三端一致才能保证同一昵称同色）
- [ ] 对话框/下拉用 `surfaceContainerHigh`，别用系统默认白底
- [ ] 滚动条/滚动行为按 §6（Android 原生无滚动条，保留惯性滚动即可）
- [ ] 昵称与主题都存本地（`SharedPreferences`），不请求服务端
