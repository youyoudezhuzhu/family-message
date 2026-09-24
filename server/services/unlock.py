"""远程解锁的一次性令牌服务（Phase 1）。

── 安全设计不变式 ────────────────────────────────────────────
**NAS 全程不接触 Windows 密码。** 这里签发和传递的令牌里只有
request_id / nonce / 过期时间；真正的凭据只存在 PC 本地（Phase 2 用 DPAPI 存）。
网页负责「授权」，PC 负责「用本地凭据执行」。

── 一次性语义 ────────────────────────────────────────────────
`unlock_requests.used_at` 一旦写下，同一个 request_id 再来就直接拒（防重放）。
PC 侧还有一份本地重放缓存 —— 两边都记，任何一边漏了另一边兜底。

── 限流（需求 §15.3）─────────────────────────────────────────
同一设备连续失败 `MAX_FAILS` 次 → 锁 `LOCK_SECONDS` 秒。成功后清零。
"""
from __future__ import annotations

import os
import secrets
import uuid
from datetime import datetime, timedelta
from typing import Optional

import db


# ⚠️ 时间串格式必须和 db.now_iso() **完全一致**（空格分隔、不带时区），
# 否则 unlock.py 里用字符串比较判断过期会出错 —— isoformat() 出来的是 T 分隔，
# 和 "2026-09-24 11:30:00" 直接比会得到错误结果。
_TS_FMT = "%Y-%m-%d %H:%M:%S"


def _fmt(dt: datetime) -> str:
    return dt.strftime(_TS_FMT)

# ── 可调参数 ──────────────────────────────────────────────────
TTL_SECONDS = int(os.environ.get("FM_UNLOCK_TTL", "30"))   # 令牌有效期（需求 §10 建议 30 秒）
MAX_FAILS = int(os.environ.get("FM_UNLOCK_MAX_FAILS", "3"))   # 连续失败几次就锁
LOCK_SECONDS = 300        # 锁多久

ACTION = "device.unlock"

# 允许发起解锁的 Windows 会话状态：
#   locked       —— 已登录但锁屏
#   logon_screen —— 停在 Windows 登录界面（还没登录）
# 其他状态（unlocked）说明人已经在用，没必要也没道理去解锁。
UNLOCKABLE_STATES = ("locked", "logon_screen")


# ══════════════════════════════════════════════════════════════
#  限流
# ══════════════════════════════════════════════════════════════

def guard_state(device_id: str) -> Optional[str]:
    """被限流则返回 locked_until 时间串，否则 None。"""
    row = db.query_one("SELECT locked_until FROM unlock_guard WHERE device_id=?", (device_id,))
    if not row:
        return None
    until = row.get("locked_until")
    if not until:
        return None
    if until <= db.now_iso():
        # 已过期，顺手清掉，避免下次还要比一次
        db.execute("UPDATE unlock_guard SET locked_until=NULL, fail_count=0 WHERE device_id=?",
                   (device_id,))
        return None
    return until


def note_result(device_id: str, ok: bool) -> None:
    """记录一次解锁结果：成功清零，失败累计到阈值就锁。"""
    if ok:
        db.execute(
            "INSERT INTO unlock_guard(device_id, fail_count, locked_until) VALUES(?,0,NULL) "
            "ON CONFLICT(device_id) DO UPDATE SET fail_count=0, locked_until=NULL",
            (device_id,),
        )
        return

    db.execute(
        "INSERT INTO unlock_guard(device_id, fail_count) VALUES(?,1) "
        "ON CONFLICT(device_id) DO UPDATE SET fail_count=fail_count+1",
        (device_id,),
    )
    row = db.query_one("SELECT fail_count FROM unlock_guard WHERE device_id=?", (device_id,))
    if row and int(row.get("fail_count") or 0) >= MAX_FAILS:
        until = _fmt(datetime.now() + timedelta(seconds=LOCK_SECONDS))
        db.execute(
            "UPDATE unlock_guard SET locked_until=?, fail_count=0 WHERE device_id=?",
            (until, device_id),
        )
        db.log_event(device_id, "unlock_locked",
                     f"连续失败 {MAX_FAILS} 次，禁用到 {until}")


# ══════════════════════════════════════════════════════════════
#  前置检查
# ══════════════════════════════════════════════════════════════

def can_unlock(device: Optional[dict], online: bool) -> tuple[bool, str]:
    """能否对该设备发起解锁。返回 (是否允许, 不允许的原因)。

    前端也有一份同样的判断（用于按钮可用性），但**后端这份是权威** ——
    前端隐藏按钮只是体验优化，不能当安全边界（需求 §13）。
    """
    if not device:
        return False, "设备不存在"
    if not online:
        return False, "设备离线，无法下发解锁请求"

    state = (device.get("windows_state") or "unknown").strip().lower()
    if state in UNLOCKABLE_STATES:
        return True, ""
    if state == "unlocked":
        return False, "Windows 已登录，无需解锁"
    # unknown / 空：PC 还没上报会话状态（老版本 Agent，或刚上线还没心跳）
    return False, "尚未获取到 Windows 会话状态，请稍候重试"


# ══════════════════════════════════════════════════════════════
#  令牌
# ══════════════════════════════════════════════════════════════

def create(device_id: str) -> dict:
    """签发一次性解锁请求。返回给网页端的字段（**不含任何秘密**）。"""
    sweep()

    rid = str(uuid.uuid4())
    nonce = secrets.token_hex(16)
    now = datetime.now()
    expires = now + timedelta(seconds=TTL_SECONDS)

    db.execute(
        "INSERT INTO unlock_requests(request_id, device_id, nonce, action, created_at, expires_at) "
        "VALUES(?,?,?,?,?,?)",
        (rid, device_id, nonce, ACTION,
         _fmt(now), _fmt(expires)),
    )
    db.log_event(device_id, "unlock_requested", rid)

    return {
        "request_id": rid,
        "device_id": device_id,
        "action": ACTION,
        "nonce": nonce,
        "expires_at": _fmt(expires),
    }


def get(request_id: str) -> Optional[dict]:
    return db.query_one("SELECT * FROM unlock_requests WHERE request_id=?", (request_id,))


def mark(request_id: str, result: str, reason: str = "", device_id: str = "") -> Optional[dict]:
    """结单：写下结果并标记已使用。同一个 request_id 只应该成功结单一次。"""
    row = get(request_id)
    if not row:
        return None
    if row.get("used_at"):
        # 已经结过单了 —— 说明有人重放，审计留痕但不覆盖原结果
        db.log_event(device_id or row.get("device_id"), "unlock_replay",
                     f"{request_id} 重复结单（原结果 {row.get('result')}）")
        return row

    db.execute(
        "UPDATE unlock_requests SET used_at=?, result=?, reason=? WHERE request_id=?",
        (db.now_iso(), result, reason[:200], request_id),
    )
    db.log_event(device_id or row.get("device_id"), "unlock_result",
                 f"{request_id} → {result}"
                 + (f"（{reason}）" if reason else ""))
    return get(request_id)


def is_expired(row: dict) -> bool:
    if not row:
        return True
    # 时间串都是本地同时区、同格式，字符串比较即可（与 db.now_iso 一致）
    return (row.get("expires_at") or "") <= db.now_iso()


def sweep() -> int:
    """把过期且从未结单的请求标记掉。避免表无限增长，也让审计能看出"过期没用到"。"""
    return db.execute(
        "UPDATE unlock_requests SET used_at=?, result='expired', reason='超时未被设备接收' "
        "WHERE used_at IS NULL AND expires_at <= ?",
        (db.now_iso(), db.now_iso()),
    )
