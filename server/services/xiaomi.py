"""米家（Xiaomi MIoT）模块 —— 官方 OAuth2 方式。

**实现严格对照 XiaoMi/ha_xiaomi_home（小米官方 HA 集成）的 `miot_cloud.py`**，
不引入 Home Assistant，只保留本项目需要的最小实现。逐条对照如下：

  授权页   https://account.xiaomi.com/oauth2/authorize
          ?redirect_uri&client_id&response_type=code&device_id&state&skip_confirm
  换 token GET https://ha.api.io.mi.com/app/v2/ha/oauth/get_token?data=<json>
          code 换发：{client_id, redirect_uri, code, device_id}
          续期：    {client_id, redirect_uri, refresh_token}
          返回：    {code:0, result:{access_token, refresh_token, expires_in, ...}}
  调用 MIoT POST https://ha.api.io.mi.com/app/v2/...
          请求头：X-Client-BizId: haapi / X-Client-AppId: <client_id>
                  Authorization: Bearer<access_token>
          取属性 /app/v2/miotspec/prop/get   {datasource:1, params:[{did,siid,piid}]}
          设属性 /app/v2/miotspec/prop/set   {params:[{did,siid,piid,value}]}
          设备表 /app/v2/home/device_list_page {limit, get_split_device, get_third_device, dids}

几点必须注意（都是官方实现里的真实约定，不是我们发明的）：

1. `redirect_uri` 必须是小米 OAuth 服务里**为该 client_id 注册过的地址**。
   我们复用官方集成的注册地址，所以用户授权后浏览器会跳到
   `homeassistant.local`（打不开），但**地址栏里带着 `code`** —— 让用户整段粘贴回来。
2. `Authorization` 头是 `Bearer<token>`（**官方实现里 Bearer 和 token 之间没有空格**），
   这里照抄，不要"顺手修正"成 `Bearer <token>`。
3. token 在剩余寿命的 70% 处就提前续期（官方 `TOKEN_EXPIRES_TS_RATIO = 0.7`）。

注意：XiaomiDevice（米家插座等 IoT 设备）与 Device（本系统的 PC 终端）是两类
完全不同的实体，绝不混用同一张表。
"""
from __future__ import annotations

import hashlib
import json
import time
import uuid as uuidlib
from typing import Any, Optional
from urllib.parse import urlencode

import httpx

import db
from config import CONFIG, save_config

# ── 官方常量（对照 ha_xiaomi_home/miot/const.py 与 miot_cloud.py）──
CLIENT_ID = "2882303761520251711"
AUTH_URL = "https://account.xiaomi.com/oauth2/authorize"
DEFAULT_OAUTH2_API_HOST = "ha.api.io.mi.com"
DEFAULT_REDIRECT_URL = "http://homeassistant.local:8123"
TOKEN_EXPIRES_TS_RATIO = 0.7
HTTP_TIMEOUT = 30


class XiaomiError(Exception):
    pass


class NeedLogin(XiaomiError):
    """需要用户重新走一次浏览器授权。"""


# ------------------------------------------------------------
# 本地持久化（token 存库；device_id / redirect_url 存 config）
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
                None,                       # OAuth2 模式不用 ssecurity
                fields.get("user_id"),
                db.now_iso(),
            ),
        )
        return

    merged = {
        "access_token": fields.get("access_token") or cur["access_token"],
        "refresh_token": fields.get("refresh_token") or cur["refresh_token"],
        "expires_at": int(fields.get("expires_at") or cur["expires_at"] or 0),
        "user_id": fields.get("user_id") or cur["user_id"],
    }
    db.execute(
        """UPDATE xiaomi_auth SET access_token=?, refresh_token=?, expires_at=?,
                                  user_id=?, updated_at=? WHERE id=1""",
        (merged["access_token"], merged["refresh_token"], merged["expires_at"],
         merged["user_id"], db.now_iso()),
    )


def clear_auth() -> None:
    db.execute("DELETE FROM xiaomi_auth WHERE id=1")


def _device_id() -> str:
    """OAuth 用的 device_id，形如 ha.<uuid>。生成一次后固定，存在 config 里。"""
    cfg = CONFIG["xiaomi"]
    did = (cfg.get("oauth_device_id") or "").strip()
    if not did:
        did = f"ha.{uuidlib.uuid4().hex}"
        cfg["oauth_device_id"] = did
        save_config()
    return did


def _redirect_url() -> str:
    return (CONFIG["xiaomi"].get("redirect_url") or "").strip() or DEFAULT_REDIRECT_URL


def auth_status() -> dict:
    row = _get_auth()
    if not row or not row["access_token"]:
        return {"logged_in": False, "reason": "尚未完成米家授权"}
    remain = int(row["expires_at"] or 0) - int(time.time())
    return {
        "logged_in": True,
        "user_id": row["user_id"],
        "expires_at": row["expires_at"],
        "expires_in_seconds": remain,
        "updated_at": row["updated_at"],
        "needs_refresh": remain <= 0,
    }


# ------------------------------------------------------------
# OAuth2：授权 → 换 token → 续期
# ------------------------------------------------------------
def build_auth_url() -> dict:
    """生成授权地址。用户在浏览器里打开并同意后，地址栏会带回 code。"""
    device_id = _device_id()
    state = hashlib.sha1(f"d={device_id}".encode()).hexdigest()
    params = {
        "redirect_uri": _redirect_url(),
        "client_id": CLIENT_ID,
        "response_type": "code",
        "device_id": device_id,
        "state": state,
        "skip_confirm": True,
    }
    return {
        "auth_url": f"{AUTH_URL}?{urlencode(params)}",
        "redirect_url": _redirect_url(),
        "device_id": device_id,
        "state": state,
        "hint": ("在浏览器打开上面的地址并同意授权。页面会跳到 "
                 f"{_redirect_url()}（打不开是正常的），"
                 "把地址栏里 code= 后面那一段（到 & 之前）复制回来。"),
    }


async def _get_token(data: dict) -> dict:
    """官方 `__get_token_async`：GET /app/v2/ha/oauth/get_token?data=<json>"""
    url = f"https://{DEFAULT_OAUTH2_API_HOST}/app/v2/ha/oauth/get_token"
    async with httpx.AsyncClient(timeout=HTTP_TIMEOUT) as c:
        r = await c.get(
            url,
            params={"data": json.dumps(data)},
            headers={"content-type": "application/x-www-form-urlencoded"},
        )
    if r.status_code == 401:
        raise NeedLogin("授权已失效（401），需要重新授权")
    if r.status_code != 200:
        raise XiaomiError(f"换发 token 失败：HTTP {r.status_code}")

    try:
        obj = r.json()
    except Exception:
        raise XiaomiError(f"换发 token 返回非 JSON：{r.text[:200]}")

    result = obj.get("result")
    if obj.get("code") != 0 or not isinstance(result, dict) or \
            not all(k in result for k in ("access_token", "refresh_token", "expires_in")):
        raise XiaomiError(f"换发 token 响应异常：{r.text[:300]}")

    return {
        **result,
        # 按官方做法：在剩余寿命 70% 处就到期，提前续
        "expires_at": int(time.time() + int(result.get("expires_in") or 0) * TOKEN_EXPIRES_TS_RATIO),
    }


async def exchange_code(code: str) -> dict:
    """用浏览器授权拿到的 code 换 token。"""
    code = (code or "").strip()
    if not code:
        raise XiaomiError("code 为空")
    # 用户可能整段粘贴了回调地址，这里容错地把 code 抠出来
    if "code=" in code:
        frag = code.split("code=", 1)[1]
        code = frag.split("&", 1)[0].strip()

    tok = await _get_token({
        "client_id": int(CLIENT_ID),
        "redirect_uri": _redirect_url(),
        "code": code,
        "device_id": _device_id(),
    })
    _save_auth({
        "access_token": tok["access_token"],
        "refresh_token": tok["refresh_token"],
        "expires_at": tok["expires_at"],
        "user_id": str(tok.get("user_id") or tok.get("userId") or ""),
    })
    db.log_event(None, "xiaomi_login", "oauth2 code exchange ok")
    return auth_status()


async def refresh() -> dict:
    """用 refresh_token 续期。只有它也失效时才需要用户重新授权。"""
    row = _get_auth()
    if not row or not row["refresh_token"]:
        raise NeedLogin("没有 refresh_token，需要重新授权")

    tok = await _get_token({
        "client_id": int(CLIENT_ID),
        "redirect_uri": _redirect_url(),
        "refresh_token": row["refresh_token"],
    })
    _save_auth({
        "access_token": tok["access_token"],
        "refresh_token": tok.get("refresh_token") or row["refresh_token"],
        "expires_at": tok["expires_at"],
    })
    db.log_event(None, "xiaomi_refresh", "oauth2 refresh ok")
    return auth_status()


async def ensure_auth() -> dict:
    """调用米家接口前确保 token 可用（快到期就先续）。"""
    row = _get_auth()
    if not row or not row["access_token"]:
        if not CONFIG["xiaomi"]["enabled"]:
            raise NeedLogin("米家模块未启用")
        raise NeedLogin("尚未完成米家授权")

    if int(row["expires_at"] or 0) <= int(time.time()):
        return await refresh()
    return auth_status()


# ------------------------------------------------------------
# MIoT 调用（OAuth2 模式：Bearer 头，不再用 nonce 签名）
# ------------------------------------------------------------
def _headers(token: str) -> dict:
    # 注意：Bearer 与 token 之间**没有空格**，这是官方实现的写法，照抄。
    return {
        "Host": DEFAULT_OAUTH2_API_HOST,
        "X-Client-BizId": "haapi",
        "Content-Type": "application/json",
        "Authorization": f"Bearer{token}",
        "X-Client-AppId": CLIENT_ID,
    }


async def _api(method: str, path: str, payload: Optional[dict] = None,
               timeout: int = HTTP_TIMEOUT) -> Any:
    st = await ensure_auth()
    if not st.get("logged_in"):
        raise NeedLogin("米家未登录")
    row = _get_auth()
    if row is None or not row["access_token"]:
        raise NeedLogin("米家未登录")
    token = row["access_token"]
    url = f"https://{DEFAULT_OAUTH2_API_HOST}{path}"

    async with httpx.AsyncClient(timeout=timeout) as c:
        if method == "POST":
            r = await c.post(url, json=payload or {}, headers=_headers(token))
        else:
            r = await c.get(url, params=payload or {}, headers=_headers(token))

    if r.status_code == 401:
        await refresh()
        raise NeedLogin("米家鉴权失效，已尝试续期，请重试")
    if r.status_code != 200:
        raise XiaomiError(f"米家接口失败：HTTP {r.status_code} {r.text[:200]}")

    try:
        obj = r.json()
    except Exception:
        raise XiaomiError(f"米家返回非 JSON：{r.text[:200]}")

    if obj.get("code") != 0:
        raise XiaomiError(f"米家接口错误：code={obj.get('code')} {obj.get('message') or ''}")
    return obj.get("result")


# ------------------------------------------------------------
# 设备发现与开关
# ------------------------------------------------------------
async def discover_devices() -> list[dict]:
    """列出账号下的米家设备（官方 /app/v2/home/device_list_page）。"""
    result = await _api("POST", "/app/v2/home/device_list_page", {
        "limit": 200,
        "get_split_device": True,
        "get_third_device": True,
        "dids": [],
    })
    out = []
    for d in (result or {}).get("list", []) or []:
        model = d.get("model") or ""
        out.append({
            "name": d.get("name"),
            "urn": model,
            "miot_device_id": d.get("did"),
            "model": model,
            "is_online": d.get("isOnline"),
            "room": (d.get("room_name") or d.get("roomName") or ""),
            # 插座/开关类：默认电源属性 siid=2 piid=1，可在绑定后改
            "looks_like_plug": any(k in model.lower()
                                   for k in ("plug", "cu", "switch", "socket")),
        })
    return out


async def get_power(miot_device_id: str, siid: int = 2, piid: int = 1) -> Optional[bool]:
    result = await _api("POST", "/app/v2/miotspec/prop/get", {
        "datasource": 1,
        "params": [{"did": miot_device_id, "siid": siid, "piid": piid}],
    })
    if not result:
        return None
    value = result[0].get("value")
    return bool(value) if value is not None else None


async def set_power(miot_device_id: str, on: bool, siid: int = 2, piid: int = 1) -> Any:
    return await _api("POST", "/app/v2/miotspec/prop/set", {
        "params": [{"did": miot_device_id, "siid": siid, "piid": piid, "value": bool(on)}],
    }, timeout=15)


# ------------------------------------------------------------
# 本地登记（与 PC Device 分表）
# ------------------------------------------------------------
def list_xiaomi_devices() -> list[dict]:
    return db.query("SELECT * FROM xiaomi_devices ORDER BY id")


def get_xiaomi_device(row_id: int) -> Optional[dict]:
    return db.query_one("SELECT * FROM xiaomi_devices WHERE id=?", (row_id,))


def bind_xiaomi_device(name: str, miot_device_id: str, urn: str = "",
                       device_type: str = "plug", siid: int = 2, piid: int = 1,
                       target_device_id: str = "", action: str = "on") -> dict:
    did = db.execute(
        """INSERT INTO xiaomi_devices (name, urn, miot_device_id, device_type,
                                       power_capability, target_device_id,
                                       power_siid, power_piid, power_action, enabled)
           VALUES (?,?,?,?, 'power', ?, ?, ?, ?, 1)""",
        (name, urn, miot_device_id, device_type, target_device_id, siid, piid,
         "off" if str(action).lower() == "off" else "on"),
    )
    return db.query_one("SELECT * FROM xiaomi_devices WHERE id=?", (did,)) or {}


def update_xiaomi_device(row_id: int, **fields) -> Optional[dict]:
    allowed = {"name", "device_type", "target_device_id", "power_siid",
               "power_piid", "power_action", "enabled"}
    sets, vals = [], []
    for k, v in fields.items():
        if k in allowed and v is not None:
            if k == "power_action":
                v = "off" if str(v).lower() == "off" else "on"
            sets.append(f"{k}=?")
            vals.append(v)
    if sets:
        vals.append(row_id)
        db.execute(f"UPDATE xiaomi_devices SET {', '.join(sets)} WHERE id=?", tuple(vals))
    return get_xiaomi_device(row_id)


def unbind_xiaomi_device(row_id: int) -> bool:
    row = get_xiaomi_device(row_id)
    if not row:
        return False
    db.execute("DELETE FROM xiaomi_devices WHERE id=?", (row_id,))
    return True


def power_for_device(device_id: str) -> Optional[dict]:
    """某台 PC 绑定的米家开关规则（网页端「开机」按钮用它）。"""
    return db.query_one(
        "SELECT * FROM xiaomi_devices WHERE target_device_id=? AND enabled=1", (device_id,)
    )


async def apply_for_device(device_id: str) -> dict:
    """执行绑定的动作（开或关）。

    绑定是「米家设备 + 动作 → 某台 PC」的规则，网页面板上的「开机」按钮
    就是触发这条规则。动作默认是「开」，也可以在设置里改成「关」。
    """
    plug = power_for_device(device_id)
    if not plug:
        raise XiaomiError(f"设备 {device_id} 未绑定米家开关（网页端「设置 → 米家」里绑定）")

    action = (plug["power_action"] or "on").lower()
    on = action != "off"
    await set_power(plug["miot_device_id"], on,
                    plug["power_siid"] or 2, plug["power_piid"] or 1)
    db.log_event(device_id, "xiaomi_action", f"{plug['name']} -> {'on' if on else 'off'}")
    return {
        "ok": True,
        "device_id": device_id,
        "plug": plug["name"],
        "action": "on" if on else "off",
        "message": f"已{'开启' if on else '关闭'}米家设备「{plug['name']}」",
    }


# 兼容旧调用名
power_on_for_device = apply_for_device
