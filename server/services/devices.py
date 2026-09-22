"""设备服务：注册、心跳、在线状态。"""
from __future__ import annotations

import secrets
from typing import Optional

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
