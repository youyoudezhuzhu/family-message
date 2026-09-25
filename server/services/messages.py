"""消息服务：创建消息、投递、状态流转。

⚠️ 产品模型是**群聊**（见 docs/GROUP-CHAT-MODEL.md）：一条消息发进一个共享空间，
所有已注册设备都能看到。**对外只有单一状态 `status = "sent"`**（见 public_message()）。

下面的逐设备状态机是**内部投递记账**，不对外暴露：

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

# ── 群聊模型（见 docs/GROUP-CHAT-MODEL.md）───────────────────────────
# 一条消息进的是一个共享空间，所有人都能看到；对外**只有一个状态**：已发送。
# 上面那套逐设备状态机仍然完整保留在 message_targets 里（投递记账 + 离线补投
# 都靠它），只是不再出现在任何对外返回/推送里 —— 想恢复只改 public_message()。
STATUS_SENT = "sent"


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
# 对外视图：群聊模型下消息只有一个状态
# ============================================================

def public_message(msg: Optional[dict]) -> dict:
    """把消息整形成**对外唯一视图**：单一状态 `status = "sent"`。

    去掉 `targets`（message_targets 的逐设备细节：created / server_received /
    device_received / popup_displayed / read）。那些细节仍然照旧入库、照旧推进，
    只是不出这一层 —— 以后想恢复逐设备状态，只改这里。
    """
    if not msg:
        return {}
    out = {k: v for k, v in msg.items() if k != "targets"}
    out["status"] = STATUS_SENT
    return out


def public_messages(rows: Iterable[dict]) -> list[dict]:
    return [public_message(m) for m in rows]


def group_history(limit: int = 30, viewer_device_id: Optional[str] = None) -> list[dict]:
    """群聊里最近的往来（给 PC 弹窗右侧渲染用）。

    群聊模型下不再是「这台设备与 Web Sender 的往来」—— 一条消息进的是共享空间，
    右侧上下文就该是这个空间里最近的往来。direction 仍是「相对这台设备」的视角：
    本机发的 = out，其余（网页端 / 别的设备发的）= in。
    """
    rows = db.query("SELECT * FROM messages ORDER BY id DESC LIMIT ?", (int(limit),))
    rows.reverse()
    out = []
    for m in rows:
        mine = (
            m.get("sender_kind") == "device"
            and bool(viewer_device_id)
            and m.get("sender_device_id") == viewer_device_id
        )
        out.append({
            "message_id": m["id"],
            "sender_name": m["sender_name"],
            "content": m["content"],
            "created_at": m["created_at"],
            "direction": "out" if mine else "in",
        })
    return out


# ============================================================
# 双向对话：Device → Server
# ============================================================

def create_reply(device_id: str, sender_name: str, content: str) -> dict:
    """设备（PC Agent）发出的消息 —— 群聊模型下就是「群里某个人说了一句」。

    sender_device_id 记下是谁说的（群聊广播据此跳过发起者自己：自己的消息不弹自己的窗）。
    不写 message_targets —— 它进的是共享空间，不是投递给某台设备；
    别的设备是「看到」这条消息，需要逐设备投递状态的是网页端发起的那类消息。
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
    """[老视图] 一台设备与 Web Sender 之间的双向对话，按时间正序返回。

    群聊模型下这不是主流程（主流程是 /api/messages 的群聊流）；
    接口保留只是为了老浏览器书签 / 老客户端还能用 —— 见 main.py 里的说明。
    """
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
    """[兼容别名] 群聊模型下与 group_history() 等价。

    保留这个名字是因为老调用点/老工具可能还在用；语义已跟着群聊模型走
    （给的是群聊往来，不再局限于该设备）。
    """
    return group_history(limit=limit, viewer_device_id=device_id)
