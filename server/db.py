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
--   · color 存**逻辑色 ID**（color_01 … color_16），**不存 HEX**：HEX 是「显示色」，
--     由客户端按「逻辑色 ID + 主题」算出来（§4.4）。NAS 不必为浅/深主题各存一套色，
--     同时「昵称颜色全局统一」仍然成立；
--   · 「逻辑色 ID → 基础色值」常量表在 server/nicknames.py（逻辑色池的唯一定义点），
--     DB 只做「枚举 + 唯一」约束 → 以后改基础色值不需要 DDL 迁移；
--   · 灰（本地临时昵称用）的逻辑色 ID 是 `gray`，**不在这张表里、也不在下面的 CHECK 枚举里**，
--     它永远不会被分配给共享昵称（§3.2.1）；
--   · 删除 = status='inactive'（软删）：行永久保留，颜色立即释放（§3.5）。
CREATE TABLE IF NOT EXISTS nicknames (
    nickname_id  INTEGER PRIMARY KEY AUTOINCREMENT,
    display_name TEXT    NOT NULL CHECK (length(trim(display_name)) BETWEEN 1 AND 32),
    color        TEXT    NOT NULL CHECK (color IN (
                     'color_01','color_02','color_03','color_04','color_05','color_06','color_07','color_08',
                     'color_09','color_10','color_11','color_12','color_13','color_14','color_15','color_16')),
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


def _migrate(conn: sqlite3.Connection) -> None:
    for table, column, ddl in MIGRATIONS:
        cols = {row[1] for row in conn.execute(f"PRAGMA table_info({table})")}
        if column not in cols:
            conn.execute(ddl)
    for ddl in POST_MIGRATION_DDL:
        conn.execute(ddl)
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
