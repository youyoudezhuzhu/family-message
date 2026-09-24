"""SQLite 存储层。

设计取舍：
- 用标准库 sqlite3，不引入 ORM，容器镜像小、启动快、排障直接看文件。
- 全局单连接 + 互斥锁：家庭级并发（几十个连接）完全够用，避免连接池复杂度。
- 所有时间统一用本地时间 ISO8601 字符串存储，前端直接展示。
"""
from __future__ import annotations

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
]

# 依赖迁移后才能建的索引（老库在 CREATE TABLE IF NOT EXISTS 时不会补列）
POST_MIGRATION_DDL = [
    "CREATE INDEX IF NOT EXISTS idx_messages_sender_dev ON messages(sender_device_id)",
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


def log_event(device_id: Optional[str], kind: str, detail: str = "") -> None:
    execute(
        "INSERT INTO events (device_id, kind, detail, created_at) VALUES (?,?,?,?)",
        (device_id, kind, detail, now_iso()),
    )
