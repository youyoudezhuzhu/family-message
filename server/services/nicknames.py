"""共享昵称服务：创建 / 改名 / 软删 / 重新分配颜色 / 列表。

对应 docs/NICKNAME-SYSTEM-PLAN.md §3（数据模型）、§6（事务安全）、§7 Phase 1。

**核心口径（改之前先读 §0 的硬约束）**

- **昵称是全局对象，没有 owner**：这张表里不许出现任何「归属 / 设备 / 客户端」身份列；
  「谁在用这个昵称」是客户端本地状态，一个字节都不进 NAS（§0.6 / §5.2）。
  → 所以这里**没有** `ensure()` / `ensure_default()`：按名幂等创建、空表懒建默认用户都已作废。
- **昵称长度统一 32**（`trim()` 后 1–32，入库前 NFC 规范化、禁控制字符）（§5.5 / §10 已定 13）。
- **颜色只能由服务端从池子里取**：请求体里不接受任何颜色（HEX 或 ID 都不接受）（§5.5）。
- **选择 ≠ 修改**：用户「以后用别的昵称」是纯客户端行为，本模块只做**改共享昵称本身**的四个写操作。

**并发（§6）**：四个写操作都走 `db.tx()`（一个 `BEGIN IMMEDIATE` 事务）。
`CHECK` / 唯一索引是最后防线，而**冲突不是「静默复用」**：

| 冲突 | 谁报错 | 处理 |
|---|---|---|
| 创建时撞名 | 索引① `ux_nicknames_name_active` | `NicknameAlreadyExists(existing_nickname_id)` → 409（**不重试**，重试＝静默复用） |
| 改名时撞名 | 索引① | `NameTaken` → 409（**不重试**，语义是「换个名字」而不是「并入」） |
| 颜色被并发抢走 | 索引② `ux_nicknames_color_active` | `IntegrityError` → **重试下一个色**（最多 `len(COLOR_POOL)+2` 轮） |
| 池子满 | 应用层（DDL 表达不了「还有没有色可取」） | `NoAvailableColor` → 503，**绝不重色** |

★ 历史冻结（硬规则第 8 条 / §3.4.1）：改名 / 改色 / 删除**一律不回写历史消息**；
`nickname_id` **绝不能按 `display_name` 去查**（重创同名不得污染历史）。
本模块里唯一一处按名字查 id 是**创建撞名时的报错**（§6.3 的 `_active_id_of`），
它只用于告诉客户端「已存在的是哪一个，去选用它」——**不是**历史读取路径。
"""
from __future__ import annotations

import sqlite3
import unicodedata

import db

# 逻辑色池的**唯一定义点**在 server/nicknames.py（§4.3）；
# 这里转发一遍，业务层只 import 这一个模块就够（常量本身不复制）。
from nicknames import (COLOR_POOL, COLOR_POOL_VERSION, LOCAL_TEMP_BASE_COLOR,  # noqa: F401
                       LOCAL_TEMP_COLOR_ID, LOGICAL_COLORS, MAX_ACTIVE_NICKNAMES, MAX_COLOR_POOL,
                       is_valid_shared_color, pick_random_recolor,
                       visual_distance)
# ★ v0.19：色表是**数据**（表 nickname_colors）。依赖方向 color_svc → nicknames（纯常量），
#   反向不依赖 —— 本模块是那个「反向」的拥有者，所以取色一律问 color_svc。
from services import colors as color_svc

#: 昵称长度上限：NAS / API / PC / Web 统一 32（§5.5 / §10 已定 13）
NAME_MAX_LEN = 32

#: 对外对象的字段 = 表的 6 列，一个不多一个不少（§3.1）
FIELDS = ("nickname_id", "display_name", "color", "status", "created_at", "updated_at")

STATUSES = ("active", "inactive")

#: 列表返回值的类型别名 —— 提前绑好内置的 list，
#: 免得下面那个（规格要求的）`def list()` 把它遮蔽掉（运行时靠 `from __future__` 也没事，但静态检查会报）。
_Rows = list[dict]


# ── 错误 ────────────────────────────────────────────────────────────────
class NicknameError(Exception):
    """昵称操作的业务错误：带机器可读 `code` + HTTP 状态码（§5.3 的映射表）。

    Phase 2 的 HTTP / WS 层据此构造响应：
        raise HTTPException(e.http_status, detail=e.to_detail())
    错误体按 §5.1 的建议形状 `{"code": ..., "message": ..., ...}`。
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


class InvalidDisplayName(NicknameError):
    def __init__(self, message: str = ""):
        super().__init__(
            "INVALID_DISPLAY_NAME",
            message or f"昵称不合法：去掉首尾空白后需 1–{NAME_MAX_LEN} 个字符，且不能含换行 / 制表等控制字符",
            422)


class InvalidStatus(NicknameError):
    def __init__(self, status: str = ""):
        super().__init__("INVALID_STATUS", f"status 只能是 active / inactive / all（收到 {status!r}）", 422)


class NicknameAlreadyExists(NicknameError):
    """**创建**时撞名（§3.3 / §10 已定 2）：UI 拿 `existing_nickname_id` 引导用户「直接选用它」。

    ⚠ 与 `NameTaken` 是两个不同的引导：创建撞名 → 「选用既有的」；改名撞名 → 「换个名字」。
    """

    def __init__(self, existing_nickname_id: int | None = None):
        super().__init__("NICKNAME_ALREADY_EXISTS", "这个昵称已经存在，请直接选用它", 409,
                         existing_nickname_id=existing_nickname_id)
        self.existing_nickname_id = existing_nickname_id


class NameTaken(NicknameError):
    """**改名**时撞名（§5.2 / §10 已定 9）：拒绝，不静默覆盖、不自动加后缀。"""

    def __init__(self, display_name: str = ""):
        super().__init__("NAME_TAKEN", f"「{display_name}」已被占用，请换个名字或直接选用它", 409,
                         display_name=display_name or None)


class NicknameNotFound(NicknameError):
    def __init__(self, nickname_id: int | None = None):
        super().__init__("NICKNAME_NOT_FOUND", "这个昵称不存在（可能已被删除）", 404,
                         nickname_id=nickname_id)


class NicknameInactive(NicknameError):
    """行还在，但已经软删（`status='inactive'`）→ 409，不是 404（§5.5 / §10 已定 10）。"""

    def __init__(self, nickname_id: int | None = None):
        super().__init__("NICKNAME_INACTIVE", "这个昵称已被删除", 409, nickname_id=nickname_id)


class NoAvailableColor(NicknameError):
    """池子满了（16 条 active）→ 503，**绝不重色**（规格 §11 / §4.3）。

    `message` 逐字使用用户给定的满额文案（§4.3 / §5.5）：UI 直接展示它。
    """

    def __init__(self):
        super().__init__("NO_AVAILABLE_COLOR",
                         "已达到共享昵称上限，请删除不再使用的昵称后再添加。", 503)


# ── 规范化与视图 ────────────────────────────────────────────────────────
def normalize(display_name: str) -> str:
    """`strip()` + NFC + 拒控制字符 + 长度 1–32（§5.5）。

    三个容易漏的点，都有对应的实测判据（§2.C4 表格第 1 / 2 行）：
      · SQLite 默认 `BINARY` 排序：`'妈妈'` 与 `'妈妈 '` 是两个值 → 写入前必须 `strip()`；
      · SQLite 不做 Unicode 规范化：`'é'`(U+00E9) 与 `'e'+U+0301` 必须归一，否则能建出两个「同一个名字」；
      · 控制字符（`\\n` / `\\r` / `\\t` 等，含 `Cc` 类）**一律拒**（不是静默删掉）。
    大小写**有意不做**归一：显示名的大小写是有意义的（§2.C4 第 3 行）。
    """
    if not isinstance(display_name, str):
        raise InvalidDisplayName("昵称必须是字符串")
    name = unicodedata.normalize("NFC", display_name)
    if any(ch in "\r\n\t" or unicodedata.category(ch) == "Cc" for ch in name):
        raise InvalidDisplayName("昵称不能包含换行 / 制表等控制字符")
    name = name.strip()
    if not name or len(name) > NAME_MAX_LEN:
        raise InvalidDisplayName()
    return name


def _public(row) -> dict:
    """对外的完整对象：**只有表的 6 列**（没有 owner / device 字段，§0.6 / R8）。"""
    return {k: (int(row[k]) if k == "nickname_id" else row[k]) for k in FIELDS}


def _active_id_of(display_name: str) -> int | None:
    """按名字取**当前 active 那一行**的 id —— 只用于创建撞名时回给客户端。

    ★ 这不是历史读取路径：历史消息只读自己行内的快照三列（§3.4.1）。
    """
    row = db.query_one(
        "SELECT nickname_id FROM nicknames WHERE display_name=? AND status='active'",
        (display_name,))
    return int(row["nickname_id"]) if row else None


def _row_or_raise(conn: sqlite3.Connection, nickname_id: int):
    """按 id 取行（**永远按 id，不按名字**，§3.4.1）；区分 404 与 409（§5.5）。"""
    row = conn.execute("SELECT * FROM nicknames WHERE nickname_id=?", (nickname_id,)).fetchone()
    if row is None:
        raise NicknameNotFound(nickname_id)
    if row["status"] != "active":
        raise NicknameInactive(nickname_id)
    return row


def _fetch(conn: sqlite3.Connection, nickname_id: int) -> dict:
    return _public(conn.execute("SELECT * FROM nicknames WHERE nickname_id=?",
                                (nickname_id,)).fetchone())


# ── 动态色表（v0.19：池子从 DB 读，见 services/colors.py）──────────────────
def pick_first_available(conn: sqlite3.Connection) -> str | None:
    """在**调用方的事务里**查 active 占用，返回**动态色表**里第一个可用的逻辑色 ID。

    ⚠ 与 `nicknames.pick_first_available` **同名是有意的**：
      ① 老测试会 monkeypatch `nk.pick_first_available`（`tools/test_nicknames.py` 用它造「色被抢走」的冲突）；
      ② 业务层只需要一个「取第一个可用色」的口径 —— 只是现在这个池子来自 DB，按 `sort` 排序，
         用户新加的颜色也能被分配（v0.19）。
    返回 `None` = 没色可给 → 调用方抛 `NoAvailableColor`（503），**绝不重色**（§11）。
    """
    used = {row[0] for row in conn.execute(
        "SELECT color FROM nicknames WHERE status='active'")}
    return next((c for c in color_svc.pool_ids(conn) if c not in used), None)


def active_count(conn: sqlite3.Connection) -> int:
    """当前 active 昵称条数（额度提示与上限判定都用它）。"""
    return int(conn.execute("SELECT COUNT(*) FROM nicknames WHERE status='active'").fetchone()[0])


# ── 创建（§3.3 / §6.2 / §6.3）────────────────────────────────────────────
def create(display_name: str) -> dict:
    """创建一个**新的**共享昵称，返回完整对象（`color` 是服务端分配的逻辑色 ID）。

    名字已被某个 active 昵称占用 → `NicknameAlreadyExists(existing_nickname_id)`（**不是幂等复用**）。
    池子满 → `NoAvailableColor`。

    重试只针对**颜色**冲突（`IntegrityError` 命中 `nicknames.color`）：
    下一轮会看到别人新占用的色，于是换下一个。
    名字冲突**绝不重试** —— 重试就会变成「静默复用」，语义就不是「创建」了（§6.3）。
    """
    name = normalize(display_name)

    for _ in range(MAX_COLOR_POOL + 2):
        try:
            row = _create_once(name)
        except sqlite3.IntegrityError as exc:
            msg = str(exc)
            if "nicknames.display_name" in msg:      # 索引①：名字被并发抢先建了
                raise NicknameAlreadyExists(_active_id_of(name)) from exc
            if "nicknames.color" in msg:             # 索引②：色被并发抢走 → 重来一轮
                continue
            raise                                    # 别的完整性错误与并发无关，直接抛
        # 审计（§10 已定 19）—— **必须在事务之后**写，tx 里的语句不能自己 commit
        db.log_event(None, "nickname_created",
                     f"#{row['nickname_id']} {row['display_name']} ({row['color']})")
        return row

    raise NoAvailableColor()


def _create_once(name: str) -> dict:
    """一个事务里完成「查重名 → 取第一个未占用色 → 插入」（§6.2）。"""
    with db.tx() as conn:
        # ① 显式判重：命中就明确报错（不静默复用），而且要**立刻返回、绝不碰颜色池**
        #    —— 否则池子将满时会把「名字已存在」误报成 503（§7 Phase 1 的易错点 ②）
        row = conn.execute(
            "SELECT nickname_id FROM nicknames WHERE display_name=? AND status='active'",
            (name,)).fetchone()
        if row:
            raise NicknameAlreadyExists(int(row["nickname_id"]))

        # ② 活跃上限 16：与池子大小**解耦**（池子可以更长，但活跃昵称最多
        #    MAX_ACTIVE_NICKNAMES 条 —— 额度文案「x/16」与 UI 都按它，§4.3）
        if active_count(conn) >= MAX_ACTIVE_NICKNAMES:
            raise NoAvailableColor()

        # ③ 同一事务里取第一个可用色（查**动态色表**，按 sort；返回逻辑色 ID，不是 HEX）
        color = pick_first_available(conn)
        if color is None:
            raise NoAvailableColor()

        # ④ 插入
        now = db.now_iso()
        cur = conn.execute(
            "INSERT INTO nicknames (display_name, color, status, created_at, updated_at)"
            " VALUES (?,?, 'active', ?, ?)", (name, color, now, now))
        return _fetch(conn, int(cur.lastrowid))


# ── 改名（§3.5 / §6.4）──────────────────────────────────────────────────
def rename(nickname_id: int, display_name: str) -> dict:
    """改**共享昵称本身**的名字（「妈妈」→「老妈」）：`nickname_id` 不变、**颜色不变**、
    历史消息不变（快照）；名字已被别的 active 昵称占用 → `NameTaken`（409，不合并、不加后缀）。

    ⚠ 这不是「改我这台设备的昵称」—— 那个概念不存在（§5.2）。
    """
    name = normalize(display_name)

    with db.tx() as conn:
        row = _row_or_raise(conn, nickname_id)
        old_name = row["display_name"]
        if name != old_name:
            clash = conn.execute(
                "SELECT nickname_id FROM nicknames WHERE display_name=? AND status='active'"
                " AND nickname_id<>?", (name, nickname_id)).fetchone()
            if clash:
                raise NameTaken(name)
            now = db.now_iso()
            try:
                conn.execute("UPDATE nicknames SET display_name=?, updated_at=?"
                             " WHERE nickname_id=?", (name, now, nickname_id))
            except sqlite3.IntegrityError as exc:          # 并发下索引①兜底
                if "nicknames.display_name" in str(exc):
                    raise NameTaken(name) from exc
                raise
        out = _fetch(conn, nickname_id)

    if out["display_name"] != old_name:
        db.log_event(None, "nickname_renamed", f"#{nickname_id} {old_name} → {out['display_name']}")
    return out


# ── 软删（§3.5 / §6.2）──────────────────────────────────────────────────
def remove(nickname_id: int) -> dict:
    """软删：`status='inactive'`；行永久保留（审计价值），**颜色立刻回池**。

    返回 `released_color`（逻辑色 ID）—— 广播 `nickname_removed` 时要带上它（§5.4）。
    **没有**「同名还有别的 active 行吗」这种判断，也没有颜色归属表的连动：
    一个活跃名字只有一行（§6.2）。
    """
    with db.tx() as conn:
        row = _row_or_raise(conn, nickname_id)
        conn.execute("UPDATE nicknames SET status='inactive', updated_at=? WHERE nickname_id=?",
                     (db.now_iso(), nickname_id))
        out = {"nickname_id": int(row["nickname_id"]),
               "display_name": row["display_name"],
               "released_color": row["color"]}

    db.log_event(None, "nickname_removed",
                 f"#{nickname_id} {out['display_name']} 释放 {out['released_color']}")
    return out


# ── 重新分配颜色（§3.5 / §6.4）──────────────────────────────────────────
def reassign_color(nickname_id: int) -> dict:
    """给这一行换一个**未被任何 active 占用**的逻辑色 ID；**新色必与旧色不同**。

    ★ 选色 = 「在够远的候选里**随机**取一个」（`pick_random_recolor`）：
    先把候选里与旧色 CIEDE2000 距离 < 35（肉眼看不出变化）的剔掉，再从剩下的随机取。

    *为什么是随机*：v0.18.0 用的是「取与旧色差异最大者」，确定性 → 对同一条昵称反复点，
    结果在「原色 ↔ 最远色」之间来回跳（用户实测反馈：**「只在绿色和原本色之间互换」**）。
    现在同一场景连点多次会给出一串不同的色（`tools/test_nicknames.py` §6b 有连点回归）。

    池满（16 个色全被 active 占着）→ `NoAvailableColor`（503）—— 这是正确结果：
    没有别的色可换，绝不回退到「就用原来的色」假装成功（规格 §11）。
    """
    for _ in range(MAX_COLOR_POOL + 2):
        try:
            row = _reassign_once(nickname_id)
        except sqlite3.IntegrityError as exc:
            if "nicknames.color" in str(exc):       # 色被并发抢走 → 重来一轮
                continue
            raise
        db.log_event(None, "nickname_color_changed",
                     f"#{row['nickname_id']} {row['display_name']} → {row['color']}")
        return row

    raise NoAvailableColor()


def _reassign_once(nickname_id: int) -> dict:
    with db.tx() as conn:
        row = _row_or_raise(conn, nickname_id)
        old_color = row["color"]

        used = {r[0] for r in conn.execute(
            "SELECT color FROM nicknames WHERE status='active'")}
        used.discard(old_color)                      # 排除自身（§6.4 最后一行）
        # ★ 候选来自**动态色表**（用户增删的颜色一样参与），但不是取第一个、也不是永远取最远的：
        #   在「与旧色 ΔE00 ≥ MIN_RECOLOR_DISTANCE」的候选里**随机**取一个 ——
        #   取最远是确定性的，单条昵称时会 原色↔最远色 两色互跳（v0.18.0 实测反馈）。
        candidates = [c for c in color_svc.pool_ids(conn) if c not in used and c != old_color]
        color = pick_random_recolor(old_color, candidates)
        if color is None:
            raise NoAvailableColor()

        conn.execute("UPDATE nicknames SET color=?, updated_at=? WHERE nickname_id=?",
                     (color, db.now_iso(), nickname_id))
        return _fetch(conn, nickname_id)


# ── 指定颜色（v0.19 新能力，docs/COLOR-TABLE-PLAN.md §3.2）───────────────
def set_color(nickname_id: int, color_id: str) -> dict:
    """**人为**把某个昵称指定成表里的某个颜色（与「换色 = 随机换一个够远的」并列）。

    校验链（任何一条不过就明确报错，不静默）：
      ① `color_id` 形状（`color_NN`，`gray` 不算）→ `INVALID_COLOR_ID`(422)；
      ② 在色表里且没被停用 → `COLOR_NOT_FOUND`(404) / `COLOR_RETIRED`(409)（`color_svc.require_assignable`）；
      ③ 昵称存在且是 active → `NICKNAME_NOT_FOUND`(404) / `NICKNAME_INACTIVE`(409)（`_row_or_raise`）；
      ④ 该色**没被别的 active 昵称占着** → `COLOR_IN_USE`(409)（DDL 唯一索引是并发时的最后防线）。

    已经就是这个色 → **原样返回**（幂等，不写审计、不广播）。历史消息**不回写**（快照冻结，§3.4.1）。
    """
    if not isinstance(nickname_id, int):
        raise NicknameNotFound(None)
    with db.tx() as conn:
        row = _row_or_raise(conn, nickname_id)
        color_svc.require_assignable(conn, color_id)
        if row["color"] == color_id:
            return _public(row)
        holder = conn.execute(
            "SELECT nickname_id, display_name FROM nicknames"
            " WHERE status='active' AND color=? AND nickname_id<>?",
            (color_id, nickname_id)).fetchone()
        if holder:
            raise color_svc.ColorInUse(color_id, int(holder["nickname_id"]), holder["display_name"])
        conn.execute("UPDATE nicknames SET color=?, updated_at=? WHERE nickname_id=?",
                     (color_id, db.now_iso(), nickname_id))
        out = _fetch(conn, nickname_id)
    db.log_event(None, "nickname_color_changed",
                 f"#{nickname_id} {out['display_name']} → {color_id}（指定）")
    return out


# ── 读（纯读，绝不创建任何东西，§0.9 / §6.2 末）─────────────────────────
def list(status: str = "active") -> _Rows:
    """列昵称，按 `nickname_id` 升序。`status ∈ {active, inactive, all}`，默认 `active`。

    **空表就返回空数组**（不建行、不分配颜色）：没有共享昵称可选的客户端用灰临时昵称发消息。
    列表长度就是「活跃昵称计数」，UI 的额度提示（`x/16`）直接用它（§4.3）。
    """
    st = (status or "active").strip().lower()
    if st not in STATUSES + ("all",):
        raise InvalidStatus(status)
    if st == "all":
        rows = db.query("SELECT * FROM nicknames ORDER BY nickname_id")
    else:
        rows = db.query("SELECT * FROM nicknames WHERE status=? ORDER BY nickname_id", (st,))
    return [_public(r) for r in rows]


def get(nickname_id: int) -> dict | None:
    """按 id 取一行（含已软删的行）；不存在返回 `None`。

    ★ 只按 `nickname_id`：按 `display_name` 反查会把重创同名的历史误挂到新行上（§3.4.1）。
    """
    row = db.query_one("SELECT * FROM nicknames WHERE nickname_id=?", (nickname_id,))
    return _public(row) if row else None
