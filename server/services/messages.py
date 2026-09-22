"""消息服务：创建消息、投递、状态流转。

状态机（每个「消息 → 设备」目标各自独立走一遍）：

    created           消息已入库
      ↓
    server_received   服务端已接收并准备投递
      ↓
    device_received   Agent 长连接已送达
      ↓
    popup_displayed   弹窗已显示在屏幕上
      ↓
    read              用户点了「知道了」
"""
from __future__ import annotations

from typing import Iterable, Optional

import db

STATES = ["created", "server_received", "device_received", "popup_displayed", "read"]
RANK = {s: i for i, s in enumerate(STATES)}


def create_message(sender_name: str, content: str, targets: Iterable[str],
                   message_type: str = "text") -> dict:
    targets = [t for t in dict.fromkeys(targets) if t]
    msg_id = db.execute(
        """INSERT INTO messages (sender_name, content, message_type, created_at,
                                 sender_kind, sender_device_id)
           VALUES (?,?,?,?, 'web', NULL)""",
        (sender_name, content, message_type, db.now_iso()),
    )
    for dev in targets:
        db.execute(
            """INSERT OR IGNORE INTO message_targets (message_id, device_id, status)
               VALUES (?,?, 'created')""",
            (msg_id, dev),
        )
    # 消息已入库并进入投递流程 → 目标状态推进到 server_received
    db.execute(
        "UPDATE message_targets SET status='server_received' WHERE message_id=?",
        (msg_id,),
    )
    return get_message(msg_id) or {}


def advance(message_id: int, device_id: str, status: str) -> Optional[dict]:
    """推进目标状态（只前进，不回退）。"""
    row = db.query_one(
        "SELECT * FROM message_targets WHERE message_id=? AND device_id=?",
        (message_id, device_id),
    )
    if not row:
        return None
    if RANK.get(status, -1) <= RANK.get(row["status"], -1):
        return row

    col = {
        "device_received": "received_at",
        "popup_displayed": "displayed_at",
        "read": "read_at",
    }.get(status)
    if col:
        db.execute(
            f"UPDATE message_targets SET status=?, {col}=? WHERE message_id=? AND device_id=?",
            (status, db.now_iso(), message_id, device_id),
        )
    else:
        db.execute(
            "UPDATE message_targets SET status=? WHERE message_id=? AND device_id=?",
            (status, message_id, device_id),
        )
    db.log_event(device_id, f"msg_{status}", str(message_id))
    return db.query_one(
        "SELECT * FROM message_targets WHERE message_id=? AND device_id=?",
        (message_id, device_id),
    )


def get_message(message_id: int) -> Optional[dict]:
    msg = db.query_one("SELECT * FROM messages WHERE id=?", (message_id,))
    if not msg:
        return None
    msg["targets"] = db.query(
        "SELECT * FROM message_targets WHERE message_id=? ORDER BY id", (message_id,)
    )
    return msg


def list_messages(limit: int = 50, device_id: Optional[str] = None) -> list[dict]:
    if device_id:
        rows = db.query(
            """SELECT m.* FROM messages m
               JOIN message_targets t ON t.message_id = m.id
               WHERE t.device_id=? ORDER BY m.id DESC LIMIT ?""",
            (device_id, limit),
        )
    else:
        rows = db.query("SELECT * FROM messages ORDER BY id DESC LIMIT ?", (limit,))
    out = []
    for m in rows:
        m["targets"] = db.query(
            "SELECT * FROM message_targets WHERE message_id=? ORDER BY id", (m["id"],)
        )
        out.append(m)
    return out


def pending_for_device(device_id: str) -> list[dict]:
    """设备刚上线时补投那些还没送达的消息。"""
    rows = db.query(
        """SELECT m.*, t.status AS target_status FROM messages m
           JOIN message_targets t ON t.message_id = m.id
           WHERE t.device_id=? AND t.status IN ('created','server_received')
           ORDER BY m.id""",
        (device_id,),
    )
    return rows


# ============================================================
# 双向对话：Device → Server
# ============================================================

def create_reply(device_id: str, sender_name: str, content: str) -> dict:
    """设备（PC Agent）发出的消息。

    不写 message_targets —— 它的接收方是「Web Sender」这个统一入口，
    不是某台设备。对话串由 conversation() 双向查询拼出来。
    """
    msg_id = db.execute(
        """INSERT INTO messages (sender_name, content, message_type, created_at,
                                 sender_kind, sender_device_id)
           VALUES (?,?,?,?, 'device', ?)""",
        (sender_name, content, "text", db.now_iso(), device_id),
    )
    db.log_event(device_id, "reply", content[:120])
    return get_message(msg_id) or {}


def conversation(device_id: str, limit: int = 50) -> list[dict]:
    """一台设备与 Web Sender 之间的双向对话，按时间正序返回。"""
    rows = db.query(
        """SELECT m.* FROM messages m
           WHERE (m.sender_kind = 'device' AND m.sender_device_id = ?)
              OR (m.sender_kind = 'web' AND EXISTS (
                    SELECT 1 FROM message_targets t
                    WHERE t.message_id = m.id AND t.device_id = ?))
           ORDER BY m.id DESC LIMIT ?""",
        (device_id, device_id, limit),
    )
    rows.reverse()
    for m in rows:
        m["targets"] = db.query(
            "SELECT * FROM message_targets WHERE message_id=? ORDER BY id", (m["id"],)
        )
    return rows


def history_for_device(device_id: str, limit: int = 30) -> list[dict]:
    """给 PC Agent 用的历史（视角已翻转：direction 表示对这台设备是收到还是发出）。"""
    out = []
    for m in conversation(device_id, limit=limit):
        is_device = m.get("sender_kind") == "device"
        out.append({
            "message_id": m["id"],
            "sender_name": m["sender_name"],
            "content": m["content"],
            "created_at": m["created_at"],
            "direction": "out" if is_device else "in",
        })
    return out
