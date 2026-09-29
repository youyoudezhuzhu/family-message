"""共享昵称的**逻辑色池**：常量表 + 分配算法（NAS 是唯一权威）。

对应 docs/NICKNAME-SYSTEM-PLAN.md §4.3（池子的落法）与 §10 已定 16。

三条硬口径（改之前先读 §4.3 / §3.2.1）：

1. ★ **存逻辑色 ID，不存 HEX**：库里 / 协议里 / 消息快照里的 `color` 一律是
   `color_01`…`color_16` 这样的**逻辑色 ID**；右边的 HEX 只是「基础色值」，
   是客户端算显示色的起点（客户端按 `theme + ID` 得到最终 CSS / ARGB，§4.4）。
   → NAS 不必为浅色 / 深色主题各存一套色，也不破坏「昵称颜色全局统一」。
2. **书写顺序 = 分配优先级**：`pick_first_available()` 取「池子里第一个没被 active 昵称占用的 ID」。
   可预测、可测试（规格 §10）。
3. **灰 `gray` 是第 17 个逻辑色 ID，不在池里**：它只属于「本地临时昵称」
   （还没选用共享昵称的客户端：PC 用 `ComputerName`、Web 用「默认用户」）。
   → `gray` 不在本模块的 `COLOR_POOL` 里、不在 `nicknames.color` 的 DDL `CHECK` 枚举里，
     永远不被分配给共享昵称，`is_valid_shared_color("gray")` 返回 `False`（§3.2.1）。

本模块**只放常量与不碰业务语义的小函数**；事务边界、错误语义、重试全在
`server/services/nicknames.py`（§6）。
"""
from __future__ import annotations

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
