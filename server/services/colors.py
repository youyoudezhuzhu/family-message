"""共享昵称的**颜色表**服务（v0.19：色表是数据，不是写死的常量）。

规格：`docs/COLOR-TABLE-PLAN.md` §3.1 / §3.2；红线见该文 §2。要点：

- **权威 = 表 `nickname_palette`**（`server/db.py` 建表 + 播种内置 16 色）。`nicknames.py` 里那份
  `LOGICAL_COLORS` 只剩两个身份：建库种子 + 客户端认不出某 ID 时的兜底色。
- **`color_id` 只增不复用**（红线 §2.1）：加色 = 现有最大序号 +1；删色 = `status='retired'`
  （**绝不 DELETE 行**）。历史消息快照只存 ID，复用 ID 会让老消息的颜色凭空换掉（§3.4.1）。
- **`hex` 唯一**：同一色值不许有两行（`nickname_palette.hex` 上有 UNIQUE 兜并发）。
- **停用有两条闸**：① 正在被 active 昵称用着 → `COLOR_IN_USE`(409)；② 不许把可用色停到一个不剩。
- 任何变更 → `app_meta.color_pool_version` **+1**：客户端（PC / 网页 / 将来 Android）据此知道
  「我手上那份色表旧了，重拉一次」（老客户端看到 1 = 与内置表一致，什么都不用做）。

模块依赖方向：`services/colors.py` → `nicknames.py`（纯常量/纯函数）；**反向不依赖**
（`services/nicknames.py` 会 import 本模块）。所以这里的错误类与 `NicknameError` **同构但不同族**
（同一套 `code` / `message` / `http_status` / `to_detail()` 接口，HTTP 层统一映射）。
"""
from __future__ import annotations

import sqlite3

import db
from nicknames import (COLOR_ID_RE, LOGICAL_COLORS, MAX_COLOR_POOL,   # noqa: F401
                       is_color_id_shape, next_color_id, normalize_hex)

STATUS_ACTIVE = "active"
STATUS_RETIRED = "retired"


# ── 错误（与 services/nicknames.py 的 NicknameError 同构，§5.3 的映射口径一致）──
class ColorError(Exception):
    """颜色表操作的业务错误：`code` + `message` + `http_status`（与 `NicknameError` 同接口）。

    ⚠ 为什么不直接继承 `NicknameError`：模块依赖是单向的（nicknames → colors），
    反向继承会成环。HTTP 层对两者用同一个转换函数。
    """

    def __init__(self, code: str, message: str, http_status: int, **extra):
        super().__init__(f"{code}: {message}")
        self.code = code
        self.message = message
        self.http_status = http_status
        self.extra = extra

    def to_detail(self) -> dict:
        detail = {"code": self.code, "message": self.message}
        detail.update({k: v for k, v in self.extra.items() if v is not None})
        return detail


class InvalidColorHex(ColorError):
    """输入不是合法颜色 → 422。收 `#RRGGBB` / `RRGGBB` / `#RGB` / `rgb(r,g,b)` / `r,g,b`。"""

    def __init__(self, value: str = ""):
        super().__init__(
            "INVALID_COLOR_HEX",
            f"颜色不合法：{value!r} 认不出来，支持 #RRGGBB、RRGGBB、#RGB、rgb(94,53,177)、94,53,177",
            422)


class InvalidColorId(ColorError):
    """`color_id` 形状不对（不是 `color_NN`；`gray` 走这里）→ 422。"""

    def __init__(self, color_id: str = ""):
        super().__init__("INVALID_COLOR_ID",
                         f"颜色 ID 不合法：{color_id!r}（只能是 color_01 … color_{MAX_COLOR_POOL:02d}）",
                         422)


class ColorAlreadyExists(ColorError):
    """这个色值已经在表里 → 409，并把已存在的 `color_id` 带回去（UI 直接指着它说「已经在了」）。"""

    def __init__(self, hex_value: str, existing_color_id: str | None = None):
        super().__init__("COLOR_ALREADY_EXISTS", f"颜色 {hex_value} 已经在颜色表里了", 409,
                         existing_color_id=existing_color_id, hex=hex_value)


class ColorPoolFull(ColorError):
    """池子到顶（`MAX_COLOR_POOL` = 32）→ 503，**不是** 无限膨胀。"""

    def __init__(self):
        super().__init__("COLOR_POOL_FULL",
                         f"颜色表已满（最多 {MAX_COLOR_POOL} 个），请先停用不再使用的颜色", 503)


class ColorPoolTooSmall(ColorError):
    """不许把可用色停到一个不剩 → 409（否则连新建昵称都没色可分了）。"""

    def __init__(self):
        super().__init__("COLOR_POOL_TOO_SMALL", "至少要保留一个可用颜色", 409)


class ColorNotFound(ColorError):
    def __init__(self, color_id: str = ""):
        super().__init__("COLOR_NOT_FOUND", f"颜色 {color_id} 不存在", 404, color_id=color_id)


class ColorRetired(ColorError):
    """这个颜色已经停用（行还在，只是不再参与分配）→ 409，不是 404。"""

    def __init__(self, color_id: str = ""):
        super().__init__("COLOR_RETIRED", f"颜色 {color_id} 已停用，不能再用", 409, color_id=color_id)


class ColorInUse(ColorError):
    """这个颜色正被某个 active 昵称用着 → 409（停用 / 指定都会被它挡住）。"""

    def __init__(self, color_id: str = "", nickname_id: int | None = None, display_name: str | None = None):
        who = f"「{display_name}」" if display_name else (f"#{nickname_id}" if nickname_id else "某个昵称")
        super().__init__("COLOR_IN_USE", f"颜色 {color_id} 正被 {who} 用着，先换掉它再操作", 409,
                         color_id=color_id, nickname_id=nickname_id, display_name=display_name)


# ── 读 ───────────────────────────────────────────────────────────────────
def pool_version() -> int:
    """色表版本（`app_meta.color_pool_version`）：色表一变 +1；v0.18 及以前 = 1。"""
    row = db.query_one("SELECT value FROM app_meta WHERE key='color_pool_version'")
    try:
        return int(row["value"]) if row else 1
    except (TypeError, ValueError):
        return 1


def _bump_version(conn: sqlite3.Connection) -> int:
    """在**调用方的事务里**给版本 +1，返回新版本号。"""
    row = conn.execute("SELECT value FROM app_meta WHERE key='color_pool_version'").fetchone()
    cur = int(row[0]) if row else 1
    nxt = cur + 1
    conn.execute("UPDATE app_meta SET value=? WHERE key='color_pool_version'", (str(nxt),))
    if conn.total_changes is not None and conn.execute(
            "SELECT COUNT(*) FROM app_meta WHERE key='color_pool_version'").fetchone()[0] == 0:
        conn.execute("INSERT INTO app_meta (key, value) VALUES ('color_pool_version', ?)", (str(nxt),))
    return nxt


def pool_ids(conn: sqlite3.Connection | None = None) -> list[str]:
    """可用（`status='active'`）的逻辑色 ID，**按 sort** —— 新建昵称的取色顺序就是它。"""
    rows = _query(conn, "SELECT color_id FROM nickname_palette WHERE status='active' ORDER BY sort, color_id")
    return [r["color_id"] for r in rows]


def hex_of(color_id: str, conn: sqlite3.Connection | None = None) -> str | None:
    """这个逻辑色 ID 的基础色值（HEX）；查不到 → `None`（老 exe 的兜底层自己处理）。"""
    rows = _query(conn, "SELECT hex FROM nickname_palette WHERE color_id=?", (color_id,))
    return rows[0]["hex"] if rows else None


def rows(status: str = "all") -> list[dict]:
    """列整张色表（含 `used_by`：哪些 active 昵称正在用）。

    `status`：`active`（可用）/ `retired`（已停用）/ `all`（默认，UI 两个区都要画）。
    `used_by` 是 `[{nickname_id, display_name}]` —— 停用按钮据此置灰并说明「谁在用」。
    """
    st = (status or "all").strip().lower()
    if st not in ("active", "retired", "all"):
        raise InvalidColorId(f"status={status!r}")
    sql = "SELECT color_id, hex, sort, status, created_at FROM nickname_palette"
    args: tuple = ()
    if st != "all":
        sql += " WHERE status=?"
        args = (st,)
    sql += " ORDER BY sort, color_id"
    out = db.query(sql, args)
    used: dict[str, list[dict]] = {}
    for r in db.query("SELECT nickname_id, display_name, color FROM nicknames WHERE status='active'"):
        used.setdefault(r["color"], []).append(
            {"nickname_id": r["nickname_id"], "display_name": r["display_name"]})
    for row in out:
        row["used_by"] = used.get(row["color_id"], [])
        row["in_use"] = bool(row["used_by"])
    return out


def _query(conn: sqlite3.Connection | None, sql: str, args: tuple = ()) -> list[dict]:
    """优先走调用方的事务连接（在 tx 里读自己刚写的行），否则走 db 的全局连接。"""
    if conn is None:
        return db.query(sql, args)
    cur = conn.execute(sql, args)
    rows = [dict(r) for r in cur.fetchall()]
    cur.close()
    return rows


def _row_or_raise(conn: sqlite3.Connection, color_id: str) -> dict:
    row = _query(conn, "SELECT color_id, hex, sort, status FROM nickname_palette WHERE color_id=?",
                 (color_id,))
    if not row:
        raise ColorNotFound(color_id)
    return row[0]


# ── 写（都在调用方的事务里）────────────────────────────────────────────
def add(value: str, conn: sqlite3.Connection | None = None) -> dict:
    """加一个新颜色：`value` 可以是 `#RRGGBB` / `RRGGBB` / `#RGB` / `rgb(94,53,177)` / `94,53,177`。

    归一 → 查重（同色值 409）→ 新 ID = 现有最大序号 +1（**只增不复用**，含已停用的序号）
    → 池满（32）503 → 写行 + 版本 +1。返回新行（与 `rows()` 的行同形）。
    """
    hex_value = normalize_hex(value)
    if not hex_value:
        raise InvalidColorHex(value if isinstance(value, str) else repr(value))

    def _do(c: sqlite3.Connection) -> dict:
        dup = _query(c, "SELECT color_id FROM nickname_palette WHERE hex=?", (hex_value,))
        if dup:
            raise ColorAlreadyExists(hex_value, dup[0]["color_id"])
        ids = [r["color_id"] for r in _query(c, "SELECT color_id FROM nickname_palette")]
        new_id = next_color_id(ids)
        if new_id is None:
            raise ColorPoolFull()
        nxt_sort = 1 + max([r["sort"] for r in _query(c, "SELECT sort FROM nickname_palette")] or [0])
        c.execute("INSERT INTO nickname_palette (color_id, hex, sort, status, created_at)"
                  " VALUES (?, ?, ?, 'active', ?)", (new_id, hex_value, nxt_sort, db.now_iso()))
        _bump_version(c)
        row = _row_or_raise(c, new_id)
        row["hex"] = hex_value
        row["used_by"] = []
        row["in_use"] = False
        return row

    if conn is not None:
        return _do(conn)
    with db.tx() as c:
        return _do(c)


def retire(color_id: str, conn: sqlite3.Connection | None = None) -> dict:
    """停用一个颜色：行**保留**、`status='retired'`（只增不复用，历史消息颜色不动）。

    闸门：形状不对 422 / 不存在 404 / 已停用 409 / **正被 active 昵称用着 409** /
    停完一个可用色都不剩 409。成功 → 版本 +1，返回该行。
    """
    if not is_color_id_shape(color_id):
        raise InvalidColorId(color_id)

    def _do(c: sqlite3.Connection) -> dict:
        row = _row_or_raise(c, color_id)
        if row["status"] != STATUS_ACTIVE:
            raise ColorRetired(color_id)
        holder = _query(c, "SELECT nickname_id, display_name FROM nicknames"
                           " WHERE status='active' AND color=? LIMIT 1", (color_id,))
        if holder:
            raise ColorInUse(color_id, holder[0]["nickname_id"], holder[0]["display_name"])
        left = _query(c, "SELECT COUNT(*) AS n FROM nickname_palette WHERE status='active' AND color_id<>?",
                      (color_id,))[0]["n"]
        if left < 1:
            raise ColorPoolTooSmall()
        c.execute("UPDATE nickname_palette SET status='retired' WHERE color_id=?", (color_id,))
        _bump_version(c)
        out = _row_or_raise(c, color_id)
        out["used_by"] = []
        out["in_use"] = False
        return out

    if conn is not None:
        return _do(conn)
    with db.tx() as c:
        return _do(c)


def require_assignable(conn: sqlite3.Connection, color_id: str) -> dict:
    """给「人为指定某个昵称的颜色」用：形状 / 存在 / 未停用的三道校验（在调用方事务里）。"""
    if not is_color_id_shape(color_id):
        raise InvalidColorId(color_id)
    row = _row_or_raise(conn, color_id)
    if row["status"] != STATUS_ACTIVE:
        raise ColorRetired(color_id)
    return row


def max_pool_size() -> int:
    return MAX_COLOR_POOL
