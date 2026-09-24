"""设备服务：注册、心跳、在线状态。"""
from __future__ import annotations

import secrets
from typing import Optional

import json

import db
from config import CONFIG


def list_devices() -> list[dict]:
    return db.query("SELECT * FROM devices ORDER BY type, name")


def get_device(device_id: str) -> Optional[dict]:
    return db.query_one("SELECT * FROM devices WHERE device_id=?", (device_id,))


def enroll(
    device_id: str,
    name: str,
    device_type: str = "pc",
    platform: str = "",
    enroll_token: str = "",
    agent_version: str = "",
    ip: str = "",
) -> tuple[dict, str]:
    """注册或更新设备。返回 (设备记录, 设备token)。

    已存在的设备必须带正确 token 才能复用（防止别人冒名顶替同一 device_id）。
    """
    existing = get_device(device_id)
    if existing:
        return existing, existing["token"] or ""

    if not CONFIG["device"]["auto_register"]:
        raise PermissionError("服务端未开启自动注册，请先在后台添加设备")
    expected = CONFIG["device"]["enroll_token"]
    if expected and enroll_token != expected:
        raise PermissionError("注册口令错误")

    token = secrets.token_urlsafe(24)
    db.execute(
        """INSERT INTO devices (device_id, name, type, platform, status, last_seen,
                                token, ip, agent_version, created_at)
           VALUES (?,?,?,?,?,?,?,?,?,?)""",
        (device_id, name, device_type, platform, "offline", None,
         token, ip, agent_version, db.now_iso()),
    )
    db.log_event(device_id, "enrolled", f"{name} ({platform})")
    return get_device(device_id), token


def set_session_state(
    device_id: str,
    windows_state: Optional[str] = None,
    capabilities: Optional[list] = None,
) -> bool:
    """写入 PC 上报的 Windows 会话状态与能力清单。

    返回**是否发生变化** —— 调用方据此决定要不要给网页端广播，
    免得每 15 秒一次心跳都推一遍（网页端会被无意义的刷新刷屏）。

    状态取值：unknown | logon_screen | locked | unlocked
    """
    cur = get_device(device_id)
    if not cur:
        return False

    new_state = (windows_state or cur.get("windows_state") or "unknown").strip().lower()
    if new_state not in ("unknown", "logon_screen", "locked", "unlocked"):
        new_state = "unknown"

    if capabilities is not None:
        new_caps = json.dumps([str(c)[:32] for c in capabilities][:16], ensure_ascii=False)
    else:
        new_caps = cur.get("capabilities") or ""

    if new_state == (cur.get("windows_state") or "") and new_caps == (cur.get("capabilities") or ""):
        return False

    db.execute(
        "UPDATE devices SET windows_state=?, capabilities=? WHERE device_id=?",
        (new_state, new_caps, device_id),
    )

    # 会话状态变化值得记一笔：排查"为什么解不了锁"时这是第一现场
    if new_state != (cur.get("windows_state") or ""):
        db.log_event(device_id, "session_state", f"{cur.get('windows_state')} → {new_state}")
    return True


def verify_token(device_id: str, token: str) -> bool:
    row = get_device(device_id)
    if row is None:
        return False
    if not row["token"]:
        return True
    return secrets.compare_digest(row["token"], token or "")


def set_online(device_id: str, ip: str = "", agent_version: str = "") -> dict:
    db.execute(
        """UPDATE devices SET status='online', last_seen=?, ip=COALESCE(NULLIF(?,''), ip),
           agent_version=COALESCE(NULLIF(?,''), agent_version) WHERE device_id=?""",
        (db.now_iso(), ip, agent_version, device_id),
    )
    db.log_event(device_id, "online", ip)
    return get_device(device_id) or {}


def touch(device_id: str) -> None:
    db.execute("UPDATE devices SET last_seen=? WHERE device_id=?", (db.now_iso(), device_id))


def set_offline(device_id: str, reason: str = "") -> None:
    db.execute("UPDATE devices SET status='offline' WHERE device_id=?", (device_id,))
    db.log_event(device_id, "offline", reason)


def update_device(device_id: str, name: Optional[str] = None, enabled_type: Optional[str] = None) -> Optional[dict]:
    if name:
        db.execute("UPDATE devices SET name=? WHERE device_id=?", (name, device_id))
    return get_device(device_id)


def delete_device(device_id: str) -> None:
    db.execute("DELETE FROM message_targets WHERE device_id=?", (device_id,))
    db.execute("DELETE FROM devices WHERE device_id=?", (device_id,))
    db.log_event(device_id, "deleted", "")
