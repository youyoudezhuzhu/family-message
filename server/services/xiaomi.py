"""米家（Xiaomi MIoT）轻量模块。

设计参考 XiaoMi/ha_xiaomi_home 的认证与 MIoT 云端调用方式，但刻意不引入
Home Assistant，只保留本项目需要的最小实现：

  1. 登录一次拿到 ssecurity / userId / passToken，落地到 xiaomi_auth 表
  2. 每次调用走签名请求（HMAC-SHA256 + nonce），不存明文账号密码
  3. 启动与调用前检查 expires_at，提前用 refresh_token 续期
  4. 只有 refresh_token 也失效时，才需要用户重新登录

注意：XiaomiDevice（米家插座等 IoT 设备）与 Device（本系统的 PC 终端）
是两类完全不同的实体，绝不混用同一张表。
"""
from __future__ import annotations

import base64
import hashlib
import hmac
import json
import os
import random
import time
from typing import Any, Optional

import httpx

import db
from config import CONFIG

REGION_HOSTS = {
    "cn": "https://api.io.mi.com/app",
    "de": "https://de.api.io.mi.com/app",
    "us": "https://us.api.io.mi.com/app",
    "sg": "https://sg.api.io.mi.com/app",
    "ru": "https://ru.api.io.mi.com/app",
    "i2": "https://i2.api.io.mi.com/app",
}

ACCOUNT_HOST = "https://account.xiaomi.com"
# Xiaomi Home Integration 使用的公开 OAuth 客户端（仅用于 refresh_token 换发）
OAUTH_CLIENT_ID = "2882303761520736984"
OAUTH_REDIRECT_URI = "https://home.miot-spec.com/oauth/callback"


class XiaomiError(Exception):
    pass


class NeedLogin(XiaomiError):
    pass


# ------------------------------------------------------------
# 存储
# ------------------------------------------------------------
def _get_auth() -> Optional[dict]:
    return db.query_one("SELECT * FROM xiaomi_auth WHERE id=1")


def _save_auth(fields: dict[str, Any]) -> None:
    cur = _get_auth()
    if cur is None:
        db.execute(
            """INSERT INTO xiaomi_auth (id, access_token, refresh_token, expires_at,
                                        ssecurity, user_id, updated_at)
               VALUES (1,?,?,?,?,?,?)""",
            (
                fields.get("access_token"),
                fields.get("refresh_token"),
                int(fields.get("expires_at") or 0),
                fields.get("ssecurity"),
                fields.get("user_id"),
                db.now_iso(),
            ),
        )
    else:
        merged = {
            "access_token": fields.get("access_token", cur["access_token"]),
            "refresh_token": fields.get("refresh_token", cur["refresh_token"]),
            "expires_at": int(fields.get("expires_at") or cur["expires_at"] or 0),
            "ssecurity": fields.get("ssecurity", cur["ssecurity"]),
            "user_id": fields.get("user_id", cur["user_id"]),
        }
        db.execute(
            """UPDATE xiaomi_auth SET access_token=?, refresh_token=?, expires_at=?,
                                      ssecurity=?, user_id=?, updated_at=? WHERE id=1""",
            (merged["access_token"], merged["refresh_token"], merged["expires_at"],
             merged["ssecurity"], merged["user_id"], db.now_iso()),
        )


def auth_status() -> dict:
    row = _get_auth()
    if not row or not row["ssecurity"]:
        return {"logged_in": False, "reason": "尚未登录米家账号"}
    remain = int(row["expires_at"] or 0) - int(time.time())
    return {
        "logged_in": True,
        "user_id": row["user_id"],
        "expires_at": row["expires_at"],
        "expires_in_seconds": remain,
        "updated_at": row["updated_at"],
        "needs_refresh": remain < int(CONFIG["xiaomi"]["refresh_ahead_seconds"]),
    }


# ------------------------------------------------------------
# 登录 / 续期
# ------------------------------------------------------------
def _md5(s: str) -> str:
    return hashlib.md5(s.encode()).hexdigest()


async def login(username: str, password: str) -> dict:
    """用米家账号密码换取 ssecurity / userId / passToken。密码不落库。"""
    if not username or not password:
        raise NeedLogin("配置里没有米家账号密码")

    headers = {"User-Agent": "MiHome/6.0.0 (Android)"}
    data = {
        "_json": "true",
        "qs": "%3Fsid%3Dxiaomiio%26_json%3Dtrue",
        "sid": "xiaomiio",
        "callback": "https://sts.api.io.mi.com/sts",
        "user": username,
        "hash": _md5(password),
    }
    async with httpx.AsyncClient(timeout=20, headers=headers, follow_redirects=True) as c:
        r = await c.post(f"{ACCOUNT_HOST}/pass/serviceLoginAuth2", data=data)
        try:
            payload = json.loads(r.text.replace("&&&START&&&", ""))
        except Exception:
            raise XiaomiError(f"登录响应无法解析（HTTP {r.status_code}）")

        if payload.get("code") != 0:
            desc = payload.get("desc") or payload.get("description") or str(payload)
            raise NeedLogin(f"米家登录失败: {desc}")
        if payload.get("notificationUrl"):
            raise NeedLogin("该账号开启了二次验证，需要在支持二次验证的客户端完成一次登录后再导出 token")

        ssecurity = payload.get("ssecurity")
        user_id = str(payload.get("userId") or "")
        location = payload.get("location")
        if not ssecurity or not location:
            raise XiaomiError("登录响应缺少 ssecurity/location")

        # 用 location 换 serviceToken
        r2 = await c.get(location)
        service_token = c.cookies.get("serviceToken") or ""

    expires_at = int(time.time()) + 30 * 24 * 3600  # 服务端会话按 30 天保守估计
    _save_auth({
        "access_token": service_token,
        "ssecurity": ssecurity,
        "user_id": user_id,
        "expires_at": expires_at,
        "refresh_token": payload.get("passToken") or "",
    })
    db.log_event(None, "xiaomi_login", f"user={user_id}")
    return auth_status()


async def refresh() -> dict:
    """优先用 refresh_token 续期；失败则退回账号密码登录。"""
    row = _get_auth()
    cfg = CONFIG["xiaomi"]

    if row and row.get("refresh_token"):
        try:
            async with httpx.AsyncClient(timeout=20) as c:
                r = await c.post(f"{ACCOUNT_HOST}/oauth2/token", data={
                    "client_id": OAUTH_CLIENT_ID,
                    "redirect_uri": OAUTH_REDIRECT_URI,
                    "grant_type": "refresh_token",
                    "refresh_token": row["refresh_token"],
                })
                payload = r.json() if r.headers.get("content-type", "").startswith("application/json") else {}
            if payload.get("access_token"):
                _save_auth({
                    "access_token": payload["access_token"],
                    "refresh_token": payload.get("refresh_token") or row["refresh_token"],
                    "expires_at": int(time.time()) + int(payload.get("expires_in") or 86400),
                    "ssecurity": payload.get("ssecurity") or row["ssecurity"],
                    "user_id": str(payload.get("user_id") or row["user_id"]),
                })
                db.log_event(None, "xiaomi_refresh", "oauth ok")
                return auth_status()
        except Exception as e:
            db.log_event(None, "xiaomi_refresh_failed", str(e)[:200])

    # 退回账号密码登录
    return await login(cfg["username"], cfg["password"])


async def ensure_auth() -> dict:
    row = _get_auth()
    if not row or not row["ssecurity"]:
        if not CONFIG["xiaomi"]["enabled"]:
            raise NeedLogin("米家模块未启用（config.yaml → xiaomi.enabled）")
        return await refresh()
    ahead = int(CONFIG["xiaomi"]["refresh_ahead_seconds"])
    if int(row["expires_at"] or 0) - int(time.time()) < ahead:
        try:
            return await refresh()
        except Exception:
            pass  # 续期失败仍先用现有凭证试一次
    return auth_status()


# ------------------------------------------------------------
# 签名请求
# ------------------------------------------------------------
def _nonce() -> str:
    raw = bytes(random.getrandbits(8) for _ in range(8)) + int(time.time()).to_bytes(4, "big")
    return base64.b64encode(raw).decode()


def _signature(url_path: str, params: dict, ssecurity: str) -> str:
    def sign_nonce(n: str) -> str:
        sha = hashlib.sha256()
        sha.update(base64.b64decode(n) + ssecurity.encode())
        return base64.b64encode(sha.digest()).decode()

    signed_nonce = sign_nonce(params["_nonce"])
    mac = hmac.new(
        base64.b64decode(signed_nonce),
        f"{url_path}&{params['data']}".encode(),
        hashlib.sha256,
    )
    return base64.b64encode(mac.digest()).decode()


async def miot_call(path: str, payload: dict) -> Any:
    """调用 MIoT 云端接口。path 例如 /home/device_list、/miotspec/prop/set。"""
    st = await ensure_auth()
    if not st.get("logged_in"):
        raise NeedLogin("米家未登录")

    row = _get_auth()
    region = CONFIG["xiaomi"]["region"]
    base = REGION_HOSTS.get(region, REGION_HOSTS["cn"])
    url_path = f"/app{path}"

    params = {"data": json.dumps(payload, separators=(",", ":")), "_nonce": _nonce()}
    params["signature"] = _signature(url_path, params, row["ssecurity"])

    cookies = {
        "userId": str(row["user_id"]),
        "serviceToken": row["access_token"] or "",
    }
    async with httpx.AsyncClient(timeout=25) as c:
        r = await c.get(base + path, params=params, cookies=cookies)
        if r.status_code == 401:
            await refresh()
            raise NeedLogin("米家鉴权失效，已尝试刷新，请重试")
        try:
            data = r.json()
        except Exception:
            raise XiaomiError(f"米家返回非 JSON（HTTP {r.status_code}）")

    if data.get("code") == 401:
        await refresh()
        raise NeedLogin("米家鉴权失效，已尝试刷新，请重试")
    if data.get("code") not in (0, None):
        raise XiaomiError(f"米家接口错误: {data.get('message') or data}")
    return data.get("result")


# ------------------------------------------------------------
# 米家设备（与 PC Device 分开管理）
# ------------------------------------------------------------
async def discover_devices() -> list[dict]:
    result = await miot_call("/home/device_list", {"getVirtualModel": False, "getHuamiDevices": 0})
    out = []
    for d in (result or {}).get("list", []):
        out.append({
            "name": d.get("name"),
            "urn": d.get("model"),
            "miot_device_id": d.get("did"),
            "device_type": "plug" if (d.get("model") or "").startswith(("cu", "cuco", "mi.plug")) else d.get("model"),
            "is_online": d.get("isOnline"),
        })
    return out


def list_xiaomi_devices() -> list[dict]:
    return db.query("SELECT * FROM xiaomi_devices ORDER BY id")


def add_xiaomi_device(name: str, miot_device_id: str, urn: str = "",
                      device_type: str = "plug", target_device_id: str = "") -> dict:
    did = db.execute(
        """INSERT INTO xiaomi_devices (name, urn, miot_device_id, device_type,
                                       power_capability, target_device_id, enabled)
           VALUES (?,?,?,?, 'power', ?, 1)""",
        (name, urn, miot_device_id, device_type, target_device_id),
    )
    return db.query_one("SELECT * FROM xiaomi_devices WHERE id=?", (did,)) or {}


def power_for_device(device_id: str) -> Optional[dict]:
    return db.query_one(
        "SELECT * FROM xiaomi_devices WHERE target_device_id=? AND enabled=1", (device_id,)
    )


async def set_power(miot_device_id: str, on: bool, siid: int = 2, piid: int = 1) -> Any:
    """米家插座开关。siid=2/piid=1 是多数智能插座的通用电源属性。"""
    payload = {"params": [{"did": miot_device_id, "siid": siid, "piid": piid, "value": bool(on)}]}
    return await miot_call("/miotspec/prop/set", payload)


async def power_on_for_device(device_id: str) -> dict:
    plug = power_for_device(device_id)
    if not plug:
        raise XiaomiError(f"设备 {device_id} 未绑定米家插座（web 端「设备管理」里绑定）")
    await set_power(plug["miot_device_id"], True)
    db.log_event(device_id, "xiaomi_power_on", plug["name"])
    return {
        "ok": True,
        "device_id": device_id,
        "plug": plug["name"],
        "message": f"已开启米家设备「{plug['name']}」，等待 PC 上线",
    }
