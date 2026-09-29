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

★ **4. 「重新分配颜色」按感知距离选**（`pick_farthest_from()`，本轮修复）：
   在「没被 active 占用、且不等于旧色」的候选里，取**与旧色 CIEDE2000 距离最大**的那个。

   *为什么换算法*：原来 reassign 也走「池子顺序第一个可用色」。库里只有 1 条昵称时，
   可用色 = 池中除自己以外的全部，于是永远取到 `color_02`；再点一次又取回 `color_01` ——
   在 `color_01`/`color_02` 之间来回跳。这两个色在浅色主题圆点上是 `#3D2273` 与 `#252F6F`，
   **ΔE76 只有 15.2（ΔE2000 7.5）**，肉眼看不出变化 → 用户报「点重新分配颜色没反应」。

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
import sqlite3

# ── ① 「逻辑色 ID → 基础色值」常量表 ─────────────────────────────────
#    入库 / 协议 / 广播 / 消息快照里出现的**只有左边的 ID**；
#    右边的 HEX 是客户端算显示色的起点，**不进库、不进协议**（§4.3 / §4.4）。
#    ⚠ 顺序有意义：它就是分配优先级。
LOGICAL_COLORS: dict[str, str] = {
    "color_01": "#5E35B1", "color_02": "#3949AB", "color_03": "#1E88E5", "color_04": "#039BE5",
    "color_05": "#00897B", "color_06": "#43A047", "color_07": "#7CB342", "color_08": "#C0CA33",
    "color_09": "#F9A825", "color_10": "#FB8C00", "color_11": "#F4511E", "color_12": "#E53935",
    "color_13": "#D81B60", "color_14": "#8E24AA", "color_15": "#6D4C41", "color_16": "#546E7A",
}

#: 池子本体 = 16 个逻辑色 ID；元组顺序 = 分配顺序（= 上面字典的书写顺序）
COLOR_POOL: tuple[str, ...] = tuple(LOGICAL_COLORS)

#: 本地临时昵称（灰）的逻辑色 ID —— **不在池里**（§3.2.1）。
#: 基础色值 #8A8A8A；判「是不是灰临时」只看消息快照的 sender_color == 这个值。
LOCAL_TEMP_COLOR_ID = "gray"

#: 灰的基础色值（只给客户端映射表 / 文档用；同样不进库、不进协议）
LOCAL_TEMP_BASE_COLOR = "#8A8A8A"

#: 池子语义版本：换 ID、改基础色值时必须 +1。
#: 客户端拿它判断自己那份「逻辑色 ID → 显示色」映射表是否落后（§4.3 / §4.4）。
COLOR_POOL_VERSION = 1

#: 活跃昵称上限 = 池子大小（16）：逻辑色在 active 昵称之间唯一 ⇒ 同时最多 16 条 active。
#: 满额时新增一律 503 NO_AVAILABLE_COLOR，**绝不重色**（§3.1 / §4.3）。
MAX_ACTIVE_NICKNAMES = len(COLOR_POOL)


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

