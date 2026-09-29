"""共享昵称的**逻辑色池**：常量表 + 分配算法（NAS 是唯一权威）。

对应 docs/NICKNAME-SYSTEM-PLAN.md §4.3（池子的落法）与 §10 已定 16。

三条硬口径（改之前先读 §4.3 / §3.2.1）：

1. ★ **存逻辑色 ID，不存 HEX**：库里 / 协议里 / 消息快照里的 `color` 一律是
   `color_01`…`color_16` 这样的**逻辑色 ID**；右边的 HEX 只是「基础色值」，
   是客户端算显示色的起点（客户端按 `theme + ID` 得到最终 CSS / ARGB，§4.4）。
   → NAS 不必为浅色 / 深色主题各存一套色，也不破坏「昵称颜色全局统一」。
2. **书写顺序 = 分配优先级**：`pick_first_available()` 取「池子里第一个没被 active 昵称占用的 ID」。
   可预测、可测试（规格 §10）。
   ⚠ **只有「新建」走这条顺序规则**；「重新分配颜色」不是取第一个，而是取**与旧色视觉差异最大**的
   （见下面第 4 点，`pick_farthest_from()`）—— 两处口径不同是**有意的**。
3. **灰 `gray` 是第 17 个逻辑色 ID，不在池里**：它只属于「本地临时昵称」
   （还没选用共享昵称的客户端：PC 用 `ComputerName`、Web 用「默认用户」）。
   → `gray` 不在本模块的 `COLOR_POOL` 里、不在 `nicknames.color` 的 DDL `CHECK` 枚举里，
     永远不被分配给共享昵称，`is_valid_shared_color("gray")` 返回 `False`（§3.2.1）。

★ **4. 「重新分配颜色」= 在「够远」的候选里随机**（`pick_random_recolor()`，v0.18.1 修）：
   候选 = 池中**没被 active 占用、且不等于旧色**的逻辑色；先筛掉与旧色 CIEDE2000 距离
   < `MIN_RECOLOR_DISTANCE`(35) 的「看着几乎一样」的色，**在剩下的里面随机取一个**。

   *为什么改成随机*：v0.18.0 用的是「取与旧色差异最大者」（`pick_farthest_from()`），
   它是**确定性**的 —— 同一条昵称反复点，候选集合不变、结果也不变：`color_01`(紫) 的最远色是
   `color_08`/`color_07`(黄绿/绿)，再点又回 `color_01`。**v0.18.0 实测反馈就是「只在绿色和原本色
   之间互换，不是真正的随机颜色」**。现在同一场景连点多次会给出一串不同的色。
   `pick_farthest_from()` **保留**，但只在「所有候选都离旧色太近（< 35）」时兜底：那时随机已无意义，
   取最远的至少保证「看得出变了」。与「新建」走 `pick_first_available()`（池子顺序第一个可用）
   仍是两套口径，这是有意的。

   *为什么不挂显示色映射表*：客户端圆点 / 头像底是基础色的**固定线性混合**
   （§4.4 规则 1：浅色 `mix(base,#000,35%)`、深色 `mix(base,#FFF,30%)`），在 Lab 里几乎保序 ——
   实测「按基础色取最大 ΔE」选出的色与「按浅色圆点取最大 ΔE」选出的色
   在 16 个旧色里 **15 个一致**，唯一不一致的 `color_15` 两种口径也只差 3.6；
   且这些选择在**深色圆点**上同样成立（最小 ΔE2000 34.5 ≫ 旧算法的 7.5）。
   → 服务端不必持有显示色表（主题变体仍是客户端渲染职责），只用自己这 16 个基础色值。

本模块**只放常量与不碰业务语义的小函数**；事务边界、错误语义、重试全在
`server/services/nicknames.py`（§6）。
"""
from __future__ import annotations

import math
import random
import re
import sqlite3

# ── ① 「逻辑色 ID → 基础色值」常量表 ─────────────────────────────────
#    入库 / 协议 / 广播 / 消息快照里出现的**只有左边的 ID**；
#    右边的 HEX 是客户端算显示色的起点，**不进库、不进协议**（§4.3 / §4.4）。
#    ⚠ 顺序有意义：它就是分配优先级。
#    ★ v0.19.1（用户定稿）：默认配色换成 31 个 —— 前 15 个是 Material **400 系**（浅），
#      后 16 个是 **800/900 系**（深）。用户给的清单里 `#26A69A` 出现了两次（第 7 个与
#      第 16 个位置），而 `nickname_palette.hex` 有 UNIQUE 约束、色表里也不该有重复色，
#      所以按去重后的 31 个建表（要补第 32 个 / 换成别的，改这里重发即可）。
#    ⚠ 老库升级：`nicknames.color` 存的是**逻辑色 ID**（昵称只与编号关联），
#      所以改这张表**不需要动昵称**；已存在的槽位由 db.py 的播种改成 upsert 改指到新色。
LOGICAL_COLORS: dict[str, str] = {
    "color_01": "#EF5350", "color_02": "#FF7043", "color_03": "#FFA726", "color_04": "#FFCA28",
    "color_05": "#66BB6A", "color_06": "#9CCC65", "color_07": "#26A69A", "color_08": "#26C6DA",
    "color_09": "#42A5F5", "color_10": "#5C6BC0", "color_11": "#7E57C2", "color_12": "#AB47BC",
    "color_13": "#EC407A", "color_14": "#8D6E63", "color_15": "#78909C",
    "color_16": "#B71C1C", "color_17": "#D84315", "color_18": "#E65100", "color_19": "#F57F17",
    "color_20": "#2E7D32", "color_21": "#558B2F", "color_22": "#00695C", "color_23": "#00838F",
    "color_24": "#1565C0", "color_25": "#283593", "color_26": "#4527A0", "color_27": "#6A1B9A",
    "color_28": "#AD1457", "color_29": "#4E342E", "color_30": "#37474F", "color_31": "#004D40",
}

#: 池子本体 = 31 个逻辑色 ID；元组顺序 = 分配顺序（= 上面字典的书写顺序）
COLOR_POOL: tuple[str, ...] = tuple(LOGICAL_COLORS)

#: 内置色 = 老客户端（exe 自带色表）能认得的全部 + 客户端兜底表 + 建库时的初始数据。
#: ★ 自 v0.19 起「色表是**数据**」（表 `nickname_palette` 才是权威，可由网页端增删），
#: `LOGICAL_COLORS` 只剩两个身份：① 建库种子 ② 客户端认不出某个 ID 时的兜底色。
BUILTIN_COLORS: tuple[str, ...] = COLOR_POOL

#: 颜色池上限：池子大小与「活跃昵称上限」**解耦** —— 后者固定 16（见下），
#: 池子可以自己增删。★ v0.19.1：默认就有 31 个内置色，上限跟着抬到 64
#: （否则「加颜色」只剩 1 个名额，v0.19 那个「自己加颜色」的功能等于废掉）。
#: 池满时「加颜色」返回 503，**不是**无限膨胀。
MAX_COLOR_POOL = 64

#: 逻辑色 ID 的形状：`color_NN`。`gray` 不符合 → 永远不会被当成共享昵称的颜色（§3.2.1）。
COLOR_ID_RE = re.compile(r"^color_(\d{2})$")

#: 本地临时昵称（灰）的逻辑色 ID —— **不在池里**（§3.2.1）。
#: 基础色值 #8A8A8A；判「是不是灰临时」只看消息快照的 sender_color == 这个值。
LOCAL_TEMP_COLOR_ID = "gray"

#: 灰的基础色值（只给客户端映射表 / 文档用；同样不进库、不进协议）
LOCAL_TEMP_BASE_COLOR = "#8A8A8A"

#: 池子语义版本：换 ID、改基础色值时必须 +1。
#: 客户端拿它判断自己那份「逻辑色 ID → 显示色」映射表是否落后（§4.3 / §4.4）。
COLOR_POOL_VERSION = 1

#: 活跃昵称上限 = 16（**与池子大小解耦**：v0.19 起颜色池可增删到 16…32，
#: 但「同时活跃的共享昵称」仍是最多 16 条 —— 额度文案「x/16」和 UI 都按这个数，§4.3）。
#: 池子满（池里没有空闲色）是另一条独立的闸，两者都报 `NoAvailableColor`（503）。
MAX_ACTIVE_NICKNAMES = 16


def is_valid_shared_color(color_id: str) -> bool:
    """服务层白名单：这个逻辑色 ID 能不能分配给**共享昵称**？

    只有池子里的 16 个 ID 可以。`"gray"`（本地临时昵称）返回 `False` ——
    灰与池子是**两个命名空间**，灰永远不占色位（§3.2.1 / §4.3）。

    注意是**区分大小写**的精确匹配：`"COLOR_01"` → `False`
    （客户端本来就不能上传颜色，池子里的 ID 也一律由服务端取出，§5.5）。
    """
    return color_id in COLOR_POOL


def pick_first_available(conn: sqlite3.Connection) -> str | None:
    """在**调用方的事务里**查 active 占用，返回池中第一个可用的**逻辑色 ID**。

    返回 `None` = 池子满了 → 调用方抛 `NoAvailableColor` → HTTP 503；
    **绝不回退到重色或哈希**（规格 §11）。

    占用查询只需要这一张表（`nicknames`）：一个 active 色只属于一行，由
    `ux_nicknames_color_active` 保证（§2.C4 / §6.3）。返回的是 ID，不是 HEX。
    """
    used = {row[0] for row in conn.execute(
        "SELECT color FROM nicknames WHERE status='active'")}
    return next((c for c in COLOR_POOL if c not in used), None)


# ══════════════════════════════════════════════════════════════════════
# ② 感知距离（CIEDE2000）与「换色取最远」
#
# 纯函数，**不碰数据库、不碰事务**：喂进两个逻辑色 ID，出一个数。
# 度量在 **CIE L\*a\*b\*** 空间上算 —— sRGB → 线性 → XYZ(D65) → Lab，
# 这是「人眼看到的差异」而不是 RGB 数值差（`color_01` 与 `color_02` 的
# RGB 差看着不小，Lab 里其实是同一种紫蓝，ΔE2000 只有 7.5）。
#
# 为什么度量用**基础色值**、不用客户端算出来的圆点 / 头像色：
# 那些显示色是基础色的固定线性混合（§4.4 规则 1/3），在 Lab 里近似保序，
# 所以「按基础色取最远」与「按显示色取最远」选出的结果基本一致
# （实测 16 个旧色里 15 个一致，见 tools/test_nicknames.py 的保序断言）。
# 好处：NAS 不必持有显示色映射表（那是客户端渲染职责，§4.4）。
# ══════════════════════════════════════════════════════════════════════

#: 换色选色用的距离口径（写进日志 / 测试断言用；换口径时改这里）
FARTHEST_METRIC = "CIEDE2000@Lab(D65)"


def _hex_rgb(hex_color: str) -> tuple[float, float, float]:
    """`'#5E35B1'` → `(94.0, 53.0, 177.0)`。大写小写都认，格式不对抛 `ValueError`。"""
    s = hex_color.strip().lstrip("#")
    if len(s) != 6:
        raise ValueError(f"不是 6 位 HEX：{hex_color!r}")
    return (float(int(s[0:2], 16)), float(int(s[2:4], 16)), float(int(s[4:6], 16)))


def _to_lab(hex_color: str) -> tuple[float, float, float]:
    """sRGB HEX → CIE L\\*a\\*b\\*（D65 白点，与 CSS 一致的那套换算）。"""
    def lin(v: float) -> float:
        v /= 255.0
        return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4

    r, g, b = (lin(c) for c in _hex_rgb(hex_color))
    x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047
    y = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 1.00000
    z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883

    def f(t: float) -> float:
        return t ** (1.0 / 3.0) if t > 0.008856 else 7.787 * t + 16.0 / 116.0

    fx, fy, fz = f(x), f(y), f(z)
    return (116.0 * fy - 16.0, 500.0 * (fx - fy), 200.0 * (fy - fz))


def delta_e76(hex_a: str, hex_b: str) -> float:
    """两个 HEX 的 CIE76 色差（欧氏距离）。**只用于报告 / 对照**，
    选色用的是感知更准的 `delta_e2000`（两者在 16 个旧色里有 7 个的最远色不同，
    见 tools/test_nicknames.py 的口径断言）。"""
    a, b = _to_lab(hex_a), _to_lab(hex_b)
    return sum((x - y) ** 2 for x, y in zip(a, b)) ** 0.5


def delta_e2000(hex_a: str, hex_b: str) -> float:
    """两色 CIEDE2000 色差（ΔE00）。经验刻度：1 几乎不可见 / 2–5 轻微 / >10 一眼可辨。"""
    l1, a1, b1 = _to_lab(hex_a)
    l2, a2, b2 = _to_lab(hex_b)

    c1 = (a1 * a1 + b1 * b1) ** 0.5
    c2 = (a2 * a2 + b2 * b2) ** 0.5
    c_bar = (c1 + c2) / 2.0
    g = 0.5 * (1.0 - ((c_bar ** 7) / (c_bar ** 7 + 25.0 ** 7)) ** 0.5)
    a1p, a2p = (1.0 + g) * a1, (1.0 + g) * a2
    c1p = (a1p * a1p + b1 * b1) ** 0.5
    c2p = (a2p * a2p + b2 * b2) ** 0.5

    def _h(a: float, bb: float) -> float:
        if a == 0.0 and bb == 0.0:
            return 0.0
        return math.degrees(math.atan2(bb, a)) % 360.0

    h1p, h2p = _h(a1p, b1), _h(a2p, b2)
    dlp = l2 - l1
    dcp = c2p - c1p
    if c1p * c2p == 0.0:
        dhp = 0.0
    elif abs(h2p - h1p) <= 180.0:
        dhp = h2p - h1p
    elif h2p - h1p > 180.0:
        dhp = h2p - h1p - 360.0
    else:
        dhp = h2p - h1p + 360.0
    dhp_ = 2.0 * (c1p * c2p) ** 0.5 * math.sin(math.radians(dhp) / 2.0)

    l_bp = (l1 + l2) / 2.0
    c_bp = (c1p + c2p) / 2.0
    if c1p * c2p == 0.0:
        h_bp = h1p + h2p
    elif abs(h1p - h2p) <= 180.0:
        h_bp = (h1p + h2p) / 2.0
    elif h1p + h2p < 360.0:
        h_bp = (h1p + h2p + 360.0) / 2.0
    else:
        h_bp = (h1p + h2p - 360.0) / 2.0

    t = (1.0 - 0.17 * math.cos(math.radians(h_bp - 30.0))
         + 0.24 * math.cos(math.radians(2.0 * h_bp))
         + 0.32 * math.cos(math.radians(3.0 * h_bp + 6.0))
         - 0.20 * math.cos(math.radians(4.0 * h_bp - 63.0)))
    d_theta = 30.0 * math.exp(-(((h_bp - 275.0) / 25.0) ** 2))
    rc = 2.0 * ((c_bp ** 7) / (c_bp ** 7 + 25.0 ** 7)) ** 0.5
    sl = 1.0 + (0.015 * (l_bp - 50.0) ** 2) / (20.0 + (l_bp - 50.0) ** 2) ** 0.5
    sc = 1.0 + 0.045 * c_bp
    sh = 1.0 + 0.015 * c_bp * t
    rt = -math.sin(math.radians(2.0 * d_theta)) * rc
    return (((dlp / sl) ** 2 + (dcp / sc) ** 2 + (dhp_ / sh) ** 2
             + rt * (dcp / sc) * (dhp_ / sh)) ** 0.5)


def visual_distance(color_id_a: str, color_id_b: str) -> float:
    """两个**逻辑色 ID** 之间的感知距离（ΔE2000，基于基础色值）。

    ★ 纯函数：只查 `LOGICAL_COLORS` 常量表，不碰库、不碰事务。
    未知 ID（`'gray'`、脏数据）→ `KeyError`：宁可炸也不要静默按「距离 0」排序。
    """
    try:
        return delta_e2000(LOGICAL_COLORS[color_id_a], LOGICAL_COLORS[color_id_b])
    except KeyError as exc:
        raise KeyError(f"不是池子里的逻辑色 ID：{exc}") from exc


def pick_farthest_from(old_color: str, candidates) -> str | None:
    """在 `candidates`（可迭代的逻辑色 ID，**不重复且已排除被占用 / 自身**）里，
    取与 `old_color` **感知距离最大**的那个；`candidates` 为空 → `None`（= 池满，调用方抛 503）。

    ⚠ 现在**不直接用于「换色」**（换色走 `pick_random_recolor()`，见模块文档第 4 点）；
    它只剩一个用途：换色时候选**全都**离旧色太近（< `MIN_RECOLOR_DISTANCE`）时兜底 ——
    那时随机已无意义，取最远的至少保证「看得出变了」。

    两条确定性规则（可测试、可复现）：
      · **同分取迭代顺序靠前者** —— 调用方传的是 `COLOR_POOL` 顺序，所以同分时等价于
        「池子书写顺序」，与 `pick_first_available()` 的口径一致；
      · `old_color` 不在池子里（脏数据 / 历史值）→ 退回**第一个候选**，
        不抛异常、也不按「距离 0」随便挑。

    返回的是逻辑色 ID，不是 HEX（HEX 永不出本模块，§4.3）。
    """
    items = list(candidates)
    if not items:
        return None
    if old_color not in LOGICAL_COLORS:
        return items[0]
    best = items[0]
    best_d = visual_distance(old_color, best)
    for c in items[1:]:
        d = visual_distance(old_color, c)
        if d > best_d:                      # 严格大于 → 同分保留先出现的（确定性）
            best, best_d = c, d
    return best


#: 「换色」时新色与旧色的**最小感知差异**（CIEDE2000）。
#: 低于它的候选被排除，否则随机可能挑到肉眼看不出变化的色（`color_01`↔`color_02` 只有 9.2、
#: `color_01`↔`color_14` 9.3）→ 用户以为「点了没反应」。
#: 取 35 的依据：16 个旧色里，每个旧色至少还有 **7** 个候选满足它（最多 11 个）——
#: 既保证「一步看得出变化」，又保证随机池足够大（阈值 45 时最少只剩 2 个候选，随机就没意义了）。
MIN_RECOLOR_DISTANCE = 35.0


def pick_random_recolor(old_color: str, candidates, *, rnd: random.Random | None = None) -> str | None:
    """「重新分配颜色」的选色：在**够远**的候选里**随机**取一个。

    `candidates` 由调用方给出（= 池中没被 active 占用、且不等于旧色的逻辑色 ID）。
    筛选：只保留与 `old_color` 的 CIEDE2000 距离 ≥ `MIN_RECOLOR_DISTANCE` 的；
    若一个都没有（池子快被占满 / 只剩邻近色）→ 退回 `pick_farthest_from()` 取最远的。
    候选为空 → `None`（= 池满，调用方抛 503，绝不回退同色）。

    `rnd` 只为单测注入固定种子用；生产走模块级 `random`。
    """
    items = list(candidates)
    if not items:
        return None
    far = [c for c in items if c in LOGICAL_COLORS
           and old_color in LOGICAL_COLORS
           and visual_distance(old_color, c) >= MIN_RECOLOR_DISTANCE]
    if not far:                              # 全都很近 → 至少给个最远的（仍不等于旧色）
        return pick_farthest_from(old_color, items)
    return (rnd or random).choice(far)


def is_color_id_shape(color_id: str) -> bool:
    """这个字符串是不是一个**形状合法**的逻辑色 ID（`color_NN`）？

    ⚠ 只判形状，不判「在不在池里 / 有没有被停用」—— 那是服务层查 `nickname_colors` 的事
    （docs/COLOR-TABLE-PLAN.md §3.1）。`gray` 形状就不合法：它不是共享昵称的颜色（§3.2.1）。
    """
    return bool(isinstance(color_id, str) and COLOR_ID_RE.match(color_id))


def next_color_id(existing) -> str | None:
    """给「加一个新颜色」分配 ID：**现有最大序号 + 1**，**只增不复用**（方案红线 §2.1）。

    已经停用（retired）的 ID 也占着序号 —— 不复活、不给新颜色复用，否则历史消息快照
    里那个 ID 的颜色会凭空换掉（§3.4.1）。返回 `None` = 池子到顶（`MAX_COLOR_POOL`）→ 调用方 503。
    """
    used = set()
    for cid in existing or ():
        m = COLOR_ID_RE.match(cid) if isinstance(cid, str) else None
        if m:
            used.add(int(m.group(1)))
    nxt = (max(used) + 1) if used else 1
    return None if nxt > MAX_COLOR_POOL else "color_%02d" % nxt


def normalize_hex(value: str) -> str | None:
    """把用户输入归一成 `#RRGGBB`（**一律大写**）；认不出来 → `None`。

    收这些写法（网页端输入框直接贴就行）：
      `#5E35B1` · `5E35B1` · `#5E3`（三位缩写）· `rgb(94, 53, 177)` · `94,53,177`
    统一大写是为了查重口径一致：`#5e35b1` 与 `#5E35B1` 必须是同一个色。
    """
    if not isinstance(value, str):
        return None
    s = value.strip()
    rgb = (re.match(r"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(?:,\s*[\d.]+\s*)?\)$", s, re.I)
           or re.match(r"^(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})$", s))
    if rgb:
        parts = [int(g) for g in rgb.groups()]
        if any(p > 255 for p in parts):
            return None
        return "#%02X%02X%02X" % tuple(parts)
    t = s[1:].strip() if s.startswith("#") else s
    if re.match(r"^[0-9a-fA-F]{3}$", t):
        t = "".join(c * 2 for c in t)
    if not re.match(r"^[0-9a-fA-F]{6}$", t):
        return None
    return "#" + t.upper()

