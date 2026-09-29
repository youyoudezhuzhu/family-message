"""SQLite 存储层。

设计取舍：
- 用标准库 sqlite3，不引入 ORM，容器镜像小、启动快、排障直接看文件。
- 全局单连接 + 互斥锁：家庭级并发（几十个连接）完全够用，避免连接池复杂度。
- 所有时间统一用本地时间 ISO8601 字符串存储，前端直接展示。
"""
from __future__ import annotations

import contextlib
import sqlite3
import threading
from datetime import datetime
from pathlib import Path
from typing import Any, Iterable, Optional

from config import CONFIG

DATA_DIR = Path(CONFIG["data_dir"])
DB_PATH = DATA_DIR / "family.db"
SHOT_DIR = DATA_DIR / "screenshots"

_lock = threading.RLock()
_conn: Optional[sqlite3.Connection] = None


def now_iso() -> str:
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")


SCHEMA = """
CREATE TABLE IF NOT EXISTS devices (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id     TEXT UNIQUE NOT NULL,
    name          TEXT NOT NULL,
    type          TEXT NOT NULL DEFAULT 'pc',
    platform      TEXT DEFAULT '',
    status        TEXT NOT NULL DEFAULT 'offline',
    last_seen     TEXT,
    token         TEXT,
    ip            TEXT DEFAULT '',
    agent_version TEXT DEFAULT '',
    created_at    TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS messages (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    sender_name      TEXT NOT NULL,
    content          TEXT NOT NULL,
    message_type     TEXT NOT NULL DEFAULT 'text',
    created_at       TEXT NOT NULL,
    sender_kind      TEXT NOT NULL DEFAULT 'web',
    sender_device_id TEXT
);

CREATE TABLE IF NOT EXISTS message_targets (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    message_id   INTEGER NOT NULL,
    device_id    TEXT NOT NULL,
    status       TEXT NOT NULL DEFAULT 'created',
    received_at  TEXT,
    displayed_at TEXT,
    read_at      TEXT,
    UNIQUE (message_id, device_id)
);

CREATE TABLE IF NOT EXISTS xiaomi_devices (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    name             TEXT NOT NULL,
    urn              TEXT DEFAULT '',
    miot_device_id   TEXT DEFAULT '',
    device_type      TEXT DEFAULT '',
    power_capability TEXT DEFAULT 'power',
    target_device_id TEXT DEFAULT '',
    power_siid       INTEGER NOT NULL DEFAULT 2,
    power_piid       INTEGER NOT NULL DEFAULT 1,
    power_action     TEXT NOT NULL DEFAULT 'on',
    power_value      TEXT,

    enabled          INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS xiaomi_auth (
    id            INTEGER PRIMARY KEY CHECK (id = 1),
    access_token  TEXT,
    refresh_token TEXT,
    expires_at    INTEGER DEFAULT 0,
    ssecurity     TEXT,
    user_id       TEXT,
    updated_at    TEXT
);

CREATE TABLE IF NOT EXISTS events (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id  TEXT,
    kind       TEXT NOT NULL,
    detail     TEXT DEFAULT '',
    created_at TEXT NOT NULL
);

-- ── 远程解锁：一次性请求 ──────────────────────────────────────────
-- 令牌里**不含任何 Windows 密码**，NAS 全程不接触密码。
-- 一次性语义：used_at 一旦写下，同一个 request_id 再来就直接拒（防重放）。
CREATE TABLE IF NOT EXISTS unlock_requests (
    request_id TEXT PRIMARY KEY,
    device_id  TEXT NOT NULL,
    nonce      TEXT NOT NULL,
    action     TEXT NOT NULL DEFAULT 'device.unlock',
    created_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    used_at    TEXT,
    result     TEXT,           -- success | expired | replay | denied | timeout | error | no_credential
    reason     TEXT
);

CREATE INDEX IF NOT EXISTS idx_unlock_dev ON unlock_requests(device_id, created_at);

-- ── 远程解锁：失败限流 ────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS unlock_guard (
    device_id    TEXT PRIMARY KEY,
    fail_count   INTEGER NOT NULL DEFAULT 0,
    locked_until TEXT
);

CREATE INDEX IF NOT EXISTS idx_targets_msg ON message_targets(message_id);
CREATE INDEX IF NOT EXISTS idx_targets_dev ON message_targets(device_id);
CREATE INDEX IF NOT EXISTS idx_messages_created ON messages(created_at);

-- ── 共享昵称（NAS 是唯一权威；docs/NICKNAME-SYSTEM-PLAN.md §3.1）────────────
-- 设计要点（逐条对应方案 §3.1，改之前先读那节）：
--   · **只有这 6 列**：不许出现任何「归属 / 设备 / 客户端」身份列
--     —— 「哪个客户端拥有这个昵称」这个概念不存在（§0.6 / §10 已定 1）；
--   · nickname_id 才是稳定身份，display_name 只是「当前显示名」（改名不改 id）；
--   · color 存**逻辑色 ID**（color_01 … color_32），**不存 HEX**：HEX 是「显示色」，
--     由客户端按「逻辑色 ID + 主题」算出来（§4.4）。NAS 不必为浅/深主题各存一套色，
--     同时「昵称颜色全局统一」仍然成立；
--   · ★ 色表自 v0.19 起是**数据**（表 `nickname_palette` 是权威，可由网页端增删）——
--     所以这里**不再写死 16 个 ID 的枚举 CHECK**（写死就加不了新色），
--     只留一个**形状** CHECK（`color_NN`，拦住乱值；'gray' 因此天然进不来）。
--     合法性（ID 在不在池里 / 有没有被停用）由服务层查 `nickname_palette` 判定；
--   · 灰（本地临时昵称用）的逻辑色 ID 是 `gray`，**不在这张表里、也不符合形状 CHECK**，
--     它永远不会被分配给共享昵称（§3.2.1）；
--   · 删除 = status='inactive'（软删）：行永久保留，颜色立即释放（§3.5）。
CREATE TABLE IF NOT EXISTS nicknames (
    nickname_id  INTEGER PRIMARY KEY AUTOINCREMENT,
    display_name TEXT    NOT NULL CHECK (length(trim(display_name)) BETWEEN 1 AND 32),
    color        TEXT    NOT NULL CHECK (color GLOB 'color_[0-9][0-9]'),
    status       TEXT    NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'inactive')),
    created_at   TEXT    NOT NULL,
    updated_at   TEXT    NOT NULL
);

-- ① 一个 display_name 在活跃期只有一行 = 「同名只有一个对象」的唯一保证。
--    inactive 的历史行不占位 → 软删后同名可再建（拿新的 nickname_id）。
--    ⚠ nickname_id 绝不能按 display_name 去查（重创同名不得污染历史，§3.4.1）。
CREATE UNIQUE INDEX IF NOT EXISTS ux_nicknames_name_active
    ON nicknames(display_name) WHERE status = 'active';

-- ② 一个 color 在活跃期只属于一行 → 「不重色」的最后防线（绝不重色、池满即 503）。
CREATE UNIQUE INDEX IF NOT EXISTS ux_nicknames_color_active
    ON nicknames(color) WHERE status = 'active';

-- 常用查询：列全表按状态（判重 SELECT 与列表查询都走索引①）
CREATE INDEX IF NOT EXISTS ix_nicknames_status ON nicknames(status, nickname_id);

-- ══ 共享昵称颜色表（v0.19 起「色表是数据」；docs/COLOR-TABLE-PLAN.md §3.1）══════
--   ⚠ 表名**故意不叫 `nickname_colors`**：那是 r3 试过的「归属层」的名字，已废弃，
--     并且 `tools/test_nicknames.py` 里有守卫断言「这张表不许存在」。
--     色表与归属层是两件事，不要混（颜色属于昵称，不属于任何客户端，§0.6）。
--   · `color_id` **只增不复用**：加色 = INSERT 一个新 id；删色 = status='retired'
--     （**绝不 DELETE 行**）—— 历史消息快照只存 ID，删行 / 复用 ID 会让老消息的颜色漂移，
--     这是本方案的头号红线（docs/NICKNAME-SYSTEM-PLAN.md §3.4.1）；
--   · `hex` 是**基础色值**（客户端算显示色的起点，§4.4），不是显示色本身；
--   · 同 hex 不许重复：服务层会先查再报 409，这里用 UNIQUE 兜并发。
CREATE TABLE IF NOT EXISTS nickname_palette (
    color_id   TEXT PRIMARY KEY CHECK (color_id GLOB 'color_[0-9][0-9]'),
    hex        TEXT NOT NULL UNIQUE
                    CHECK (hex GLOB '#[0-9A-F][0-9A-F][0-9A-F][0-9A-F][0-9A-F][0-9A-F]'),
    sort       INTEGER NOT NULL,
    status     TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'retired')),
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_nickname_palette_status ON nickname_palette(status, sort);

-- 应用级键值对。当前只有一条：`color_pool_version`（色表一变 +1，客户端据此重拉表）
CREATE TABLE IF NOT EXISTS app_meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
"""

# 已有安装的增量迁移（老库没有 sender_kind / sender_device_id）
MIGRATIONS = [
    ("messages", "sender_kind", "ALTER TABLE messages ADD COLUMN sender_kind TEXT NOT NULL DEFAULT 'web'"),
    ("messages", "sender_device_id", "ALTER TABLE messages ADD COLUMN sender_device_id TEXT"),
    # 不同型号插座的电源属性 siid/piid 不一样，硬编码只对部分型号成立
    ("xiaomi_devices", "power_siid",
     "ALTER TABLE xiaomi_devices ADD COLUMN power_siid INTEGER NOT NULL DEFAULT 2"),
    ("xiaomi_devices", "power_piid",
     "ALTER TABLE xiaomi_devices ADD COLUMN power_piid INTEGER NOT NULL DEFAULT 1"),
    # 绑定时要能选「执行开还是关」的动作
    ("xiaomi_devices", "power_action",
     "ALTER TABLE xiaomi_devices ADD COLUMN power_action TEXT NOT NULL DEFAULT 'on'"),
    # 新版绑定存具体值（JSON），不再局限开/关两种
    ("xiaomi_devices", "power_value",
     "ALTER TABLE xiaomi_devices ADD COLUMN power_value TEXT"),
    # ── 远程解锁（Phase 1）──
    # Windows 会话状态：unknown | logon_screen | locked | unlocked
    ("devices", "windows_state",
     "ALTER TABLE devices ADD COLUMN windows_state TEXT NOT NULL DEFAULT 'unknown'"),
    # 能力清单（JSON 数组字符串），网页端据此决定按钮可用性
    ("devices", "capabilities",
     "ALTER TABLE devices ADD COLUMN capabilities TEXT NOT NULL DEFAULT ''"),
    # ── 共享昵称：消息快照（Phase 1，docs/NICKNAME-SYSTEM-PLAN.md §3.2）──
    # 三快照列：sender_name（已有，就是 display_name_snapshot，不动、不改名）
    # + 下面这两列。两列都**可空、无 DEFAULT**：
    #   · 灰临时昵称（还没选共享昵称）= NULL（nickname_id 的语义就是「没有共享身份」）；
    #   · 改造前的老消息也只能是 NULL，**不回填**（§3.4：回填＝按名字猜身份，会污染历史）。
    #   · 判「是不是灰临时」的唯一依据是 sender_color = 'gray'，不是 IS NULL（§3.2.1）。
    ("messages", "sender_nickname_id",
     "ALTER TABLE messages ADD COLUMN sender_nickname_id INTEGER"),
    ("messages", "sender_color",
     "ALTER TABLE messages ADD COLUMN sender_color TEXT"),  # 值 = 逻辑色 ID（color_01…color_16 / gray）
]

# 依赖迁移后才能建的索引（老库在 CREATE TABLE IF NOT EXISTS 时不会补列）
POST_MIGRATION_DDL = [
    "CREATE INDEX IF NOT EXISTS idx_messages_sender_dev ON messages(sender_device_id)",
    # 按昵称找历史消息（快照列只用于「取那一行（若还在）」，绝不按 display_name 反查，§3.4.1）
    "CREATE INDEX IF NOT EXISTS idx_messages_sender_nick ON messages(sender_nickname_id)",
]


def _nicknames_has_color_enum(conn: sqlite3.Connection) -> bool:
    """老库的 `nicknames.color` 是不是还写着 16 色枚举 CHECK？（v0.18 及以前的建表语句）"""
    row = conn.execute(
        "SELECT sql FROM sqlite_master WHERE type='table' AND name='nicknames'").fetchone()
    sql = (row[0] or "") if row else ""
    return "CHECK (color IN" in sql


def _rebuild_nicknames_without_color_enum(conn: sqlite3.Connection) -> None:
    """把 `nicknames` 重建一遍，去掉写死的 16 色枚举 CHECK（否则加不了新色）。

    SQLite 不支持改 CHECK，只能走官方的「新建 → 搬数据 → 删旧 → 改名 → 重建索引」流程。
    · 幂等：只在 `sqlite_master` 里还能看到 `CHECK (color IN` 时才动手（迁移过就不重复搬）；
    · 没有任何外键指向 `nicknames`（`messages.sender_nickname_id` 只是普通整数列），
      仍然按官方建议关掉 FK 再动表，避免意外；
    · `nickname_id` 原样搬过去（含 AUTOINCREMENT 的 sqlite_sequence：搬入显式 id 会自动抬高水位）。
    """
    if not _nicknames_has_color_enum(conn):
        return
    conn.commit()                                   # PRAGMA 不能在事务里生效
    conn.execute("PRAGMA foreign_keys=OFF")
    conn.executescript("""
        CREATE TABLE nicknames_new (
            nickname_id  INTEGER PRIMARY KEY AUTOINCREMENT,
            display_name TEXT    NOT NULL CHECK (length(trim(display_name)) BETWEEN 1 AND 32),
            color        TEXT    NOT NULL CHECK (color GLOB 'color_[0-9][0-9]'),
            status       TEXT    NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'inactive')),
            created_at   TEXT    NOT NULL,
            updated_at   TEXT    NOT NULL
        );
        INSERT INTO nicknames_new (nickname_id, display_name, color, status, created_at, updated_at)
            SELECT nickname_id, display_name, color, status, created_at, updated_at FROM nicknames;
        DROP TABLE nicknames;
        ALTER TABLE nicknames_new RENAME TO nicknames;
        CREATE UNIQUE INDEX IF NOT EXISTS ux_nicknames_name_active
            ON nicknames(display_name) WHERE status = 'active';
        CREATE UNIQUE INDEX IF NOT EXISTS ux_nicknames_color_active
            ON nicknames(color) WHERE status = 'active';
        CREATE INDEX IF NOT EXISTS ix_nicknames_status ON nicknames(status, nickname_id);
    """)
    conn.execute("PRAGMA foreign_keys=ON")
    conn.commit()


def _seed_color_table(conn: sqlite3.Connection) -> None:
    """把内置色写进 `nickname_palette`（幂等），并在**内置色值变了**时把版本号 +1。

    ★ v0.19.1：播种从「`INSERT OR IGNORE`，已有行一律不动」升级成 **upsert** —— 因为
    默认配色会变（用户把 16 色换成 31 色），而老库里 color_01…16 这些**槽位已经存在**，
    光靠 OR IGNORE 会让它们永远停在旧色值上（昵称只与编号关联，槽位在 = 认得到它）。
    规则：
      · 槽位不存在 → 插（sort = 在内置清单里的位次）；
      · 槽位存在、色值不同 → 改指到新色（记为 changed）；
      · 槽位存在、色值相同 → 不动（幂等，重跑无副作用）；
      · 新色值已被**别的**槽位占用（UNIQUE(hex) / 用户自加过同色）→ 跳过并返回，
        不抛异常、不动别人那行（宁可少一个内置槽，也不要破坏用户数据）。
    `changed` 为真 → `color_pool_version` +1，客户端（网页 / PC / 将来 Android）据此重拉表。
    """
    from nicknames import BUILTIN_COLORS, COLOR_POOL_VERSION, LOGICAL_COLORS   # 延迟导入避免环
    changed = 0
    for idx, cid in enumerate(BUILTIN_COLORS, start=1):
        want = LOGICAL_COLORS[cid]
        row = conn.execute("SELECT hex FROM nickname_palette WHERE color_id = ?", (cid,)).fetchone()
        if row is None:
            try:
                conn.execute(
                    "INSERT INTO nickname_palette (color_id, hex, sort, status, created_at)"
                    " VALUES (?, ?, ?, 'active', ?)",
                    (cid, want, idx, now_iso()))
                changed += 1
            except sqlite3.IntegrityError:
                continue                     # 这个 HEX 已被别人占用：跳过，不动别人的行
        elif row[0] != want:
            try:
                conn.execute("UPDATE nickname_palette SET hex = ?, sort = ? WHERE color_id = ?",
                             (want, idx, cid))
                changed += 1
            except sqlite3.IntegrityError:
                continue
    row = conn.execute("SELECT value FROM app_meta WHERE key = 'color_pool_version'").fetchone()
    if row is None:
        conn.execute("INSERT INTO app_meta (key, value) VALUES ('color_pool_version', ?)",
                     (str(COLOR_POOL_VERSION),))
    elif changed:
        v = int(row[0] or "0") + 1
        conn.execute("UPDATE app_meta SET value = ? WHERE key = 'color_pool_version'", (str(v),))


def _migrate(conn: sqlite3.Connection) -> None:
    for table, column, ddl in MIGRATIONS:
        cols = {row[1] for row in conn.execute(f"PRAGMA table_info({table})")}
        if column not in cols:
            conn.execute(ddl)
    for ddl in POST_MIGRATION_DDL:
        conn.execute(ddl)
    _rebuild_nicknames_without_color_enum(conn)     # 放开颜色枚举（v0.19）
    _seed_color_table(conn)                         # 内置 16 色入库（幂等）
    conn.commit()


def get_conn() -> sqlite3.Connection:
    global _conn
    with _lock:
        if _conn is None:
            DATA_DIR.mkdir(parents=True, exist_ok=True)
            SHOT_DIR.mkdir(parents=True, exist_ok=True)
            _conn = sqlite3.connect(str(DB_PATH), check_same_thread=False)
            _conn.row_factory = sqlite3.Row
            _conn.execute("PRAGMA journal_mode=WAL")
            _conn.execute("PRAGMA synchronous=NORMAL")
            _conn.execute("PRAGMA foreign_keys=ON")
            # 跨进程并发（多 worker、或运维脚本 tools/*.py 直连同一个 family.db）时，
            # 抢不到写锁要**等待**而不是立刻 SQLITE_BUSY（docs/NICKNAME-SYSTEM-PLAN.md §6.3）。
            _conn.execute("PRAGMA busy_timeout=5000")
            _conn.executescript(SCHEMA)
            _migrate(_conn)
            _conn.commit()
        return _conn


def init_db() -> None:
    get_conn()


def query(sql: str, args: Iterable[Any] = ()) -> list[dict]:
    with _lock:
        cur = get_conn().execute(sql, tuple(args))
        rows = [dict(r) for r in cur.fetchall()]
        cur.close()
        return rows


def query_one(sql: str, args: Iterable[Any] = ()) -> Optional[dict]:
    rows = query(sql, args)
    return rows[0] if rows else None


def execute(sql: str, args: Iterable[Any] = ()) -> int:
    """执行写操作，返回 lastrowid。"""
    with _lock:
        conn = get_conn()
        cur = conn.execute(sql, tuple(args))
        conn.commit()
        last = cur.lastrowid
        cur.close()
        return int(last or 0)


@contextlib.contextmanager
def tx():
    """写事务：`BEGIN IMMEDIATE` … `COMMIT` / `ROLLBACK`（docs/NICKNAME-SYSTEM-PLAN.md §6.2）。

    为什么需要它：`execute()` **每条语句自己 commit**，「读可用色 → 写新行」于是成了两次独立提交
    —— 检查 + 分配 + 写入必须落在**一个**事务里（§6.1 / §6.2）。

    为什么持 `_lock`：本进程的读也走同一条连接（`db.query`），
    不把整段独占住，别的线程的 SELECT 会看到本事务未提交的中间态。
    为什么用 `IMMEDIATE`：一上来就抢写锁，避免「读到可用色、真写时锁不上」的经典竞态。

    ⚠ **事务体内不要调用 `execute()` / `query()` / `log_event()`** ——
    那条路会自己 `commit()`，把本事务提前提交掉（部分写入就落库了）。
    要在事务里写审计，请用 yield 出来的 conn 直接 execute；否则请在 `with` 块**之后**再写。
    """
    with _lock:
        conn = get_conn()
        conn.execute("BEGIN IMMEDIATE")
        try:
            yield conn
            conn.commit()
        except BaseException:
            conn.rollback()
            raise


def log_event(device_id: Optional[str], kind: str, detail: str = "") -> None:
    execute(
        "INSERT INTO events (device_id, kind, detail, created_at) VALUES (?,?,?,?)",
        (device_id, kind, detail, now_iso()),
    )
