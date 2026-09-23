"""家庭消息与设备控制 · 服务端

设计要点（与规格一一对应）：
- Web Sender 只有一个身份，浏览器之间不区分用户，昵称只是发送时选的标签
- Device Agent 是受信任的家庭设备，不需要登录账号、不需要申请权限
- 消息与设备管理是两层逻辑，但共用同一条长连接
"""
from __future__ import annotations

import base64
import binascii
import os
import uuid
from pathlib import Path
from typing import Optional

from fastapi import (Depends, FastAPI, HTTPException, Request, Response, WebSocket,
                     WebSocketDisconnect)
from fastapi.responses import FileResponse, JSONResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel, Field

import db
from config import CONFIG
from hub import HUB, DeviceOffline
from services import devices as dev_svc
from services import messages as msg_svc
from services import xiaomi as xiaomi_svc

BASE_DIR = Path(__file__).resolve().parent


def _find_web_dir() -> Path:
    """开发时前端在 ../web；打包进 fpk 后在 ../www。"""
    for cand in (BASE_DIR.parent / "web", BASE_DIR.parent / "www", BASE_DIR / "www"):
        if cand.exists():
            return cand
    return BASE_DIR.parent / "web"


WEB_DIR = _find_web_dir()
SHOT_DIR = Path(CONFIG["data_dir"]) / "screenshots"
SHOT_DIR.mkdir(parents=True, exist_ok=True)

app = FastAPI(title="Family Message Server", version="0.1.0")

# ------------------------------------------------------------
# fnOS 统一网关适配
# 网关会把 /app/<appname>/xxx 原样透传给应用（实测），这里统一剥掉前缀，
# 让同一份代码在「裸端口 http://nas:18801/」和「网关 /app/family-message/」
# 两种入口下行为一致。
# ------------------------------------------------------------
GATEWAY_PREFIX = os.environ.get("GATEWAY_PREFIX", "").rstrip("/")


class GatewayPrefixMiddleware:
    """fnOS 统一网关会把 /app/<appname>/xxx 原样透传进来。

    注意 Starlette 1.x 的挂载模型：靠 root_path 做前缀剥离，scope["path"] 必须保留
    完整路径（包含前缀）。所以这里只设 root_path，不能改写 path——改了反而会让
    /static 这类 Mount 找不到文件。
    """

    def __init__(self, app, prefix: str):
        self.app = app
        self.prefix = prefix

    async def __call__(self, scope, receive, send):
        if self.prefix and scope.get("type") in ("http", "websocket"):
            path = scope.get("path", "")
            if path == self.prefix:
                # 没有结尾斜杠时补一个，否则前端相对路径会解析错
                if scope["type"] == "http":
                    from starlette.responses import RedirectResponse
                    await RedirectResponse(self.prefix + "/", status_code=307)(
                        scope, receive, send
                    )
                return
            if path.startswith(self.prefix + "/"):
                scope = dict(scope)
                scope["root_path"] = self.prefix
        await self.app(scope, receive, send)


if GATEWAY_PREFIX:
    app.add_middleware(GatewayPrefixMiddleware, prefix=GATEWAY_PREFIX)

db.init_db()


# ============================================================
# Web 认证：家庭内网可留空；一旦配置口令，所有 /api 都需要会话
# ============================================================
import hashlib
import hmac

SESSION_COOKIE = "fm_session"


def _session_token() -> str:
    pwd = CONFIG["web"]["password"]
    return hmac.new(pwd.encode(), b"family-message", hashlib.sha256).hexdigest()


def require_web(request: Request) -> None:
    pwd = CONFIG["web"]["password"]
    if not pwd:
        return
    token = request.cookies.get(SESSION_COOKIE) or request.headers.get("x-fm-token", "")
    if not hmac.compare_digest(token, _session_token()):
        raise HTTPException(status_code=401, detail="需要访问口令")


WebAuth = Depends(require_web)


class LoginBody(BaseModel):
    password: str


@app.post("/api/login")
async def api_login(body: LoginBody, response: Response):
    pwd = CONFIG["web"]["password"]
    if not pwd:
        return {"ok": True, "auth_required": False}
    if not hmac.compare_digest(body.password, pwd):
        raise HTTPException(status_code=401, detail="口令错误")
    response.set_cookie(
        SESSION_COOKIE, _session_token(),
        max_age=int(CONFIG["web"]["session_hours"]) * 3600,
        httponly=True, samesite="lax",
    )
    return {"ok": True, "auth_required": True}


@app.get("/api/config", dependencies=[WebAuth])
async def api_config():
    return {
        "auth_required": bool(CONFIG["web"]["password"]),
        "popup_auto_close_seconds": CONFIG["message"]["popup_auto_close_seconds"],
        "public_url": CONFIG["server"]["public_url"],
        "xiaomi_enabled": bool(CONFIG["xiaomi"]["enabled"]),
        "server_time": db.now_iso(),
    }


# ============================================================
# 设备
# ============================================================
@app.get("/api/devices", dependencies=[WebAuth])
async def api_devices():
    rows = dev_svc.list_devices()
    for r in rows:
        r.pop("token", None)
        r["online"] = HUB.is_online(r["device_id"])
    return rows


@app.get("/api/devices/{device_id}", dependencies=[WebAuth])
async def api_device(device_id: str):
    row = dev_svc.get_device(device_id)
    if not row:
        raise HTTPException(404, "设备不存在")
    row.pop("token", None)
    row["online"] = HUB.is_online(device_id)
    row["xiaomi"] = xiaomi_svc.power_for_device(device_id)
    return row


@app.get("/api/devices/{device_id}/status", dependencies=[WebAuth])
async def api_device_status(device_id: str):
    row = dev_svc.get_device(device_id)
    if not row:
        raise HTTPException(404, "设备不存在")
    return {
        "device_id": device_id,
        "name": row["name"],
        "online": HUB.is_online(device_id),
        "status": "online" if HUB.is_online(device_id) else "offline",
        "last_seen": row["last_seen"],
    }


class DevicePatch(BaseModel):
    name: Optional[str] = None


@app.patch("/api/devices/{device_id}", dependencies=[WebAuth])
async def api_device_patch(device_id: str, body: DevicePatch):
    row = dev_svc.get_device(device_id)
    if not row:
        raise HTTPException(404, "设备不存在")
    out = dev_svc.update_device(device_id, name=body.name)
    await HUB.broadcast_web({"type": "device_updated", "device": _public(out)})
    return _public(out)


@app.delete("/api/devices/{device_id}", dependencies=[WebAuth])
async def api_device_delete(device_id: str):
    dev_svc.delete_device(device_id)
    await HUB.broadcast_web({"type": "device_deleted", "device_id": device_id})
    return {"ok": True}


def _public(row: Optional[dict]) -> dict:
    if not row:
        return {}
    row = dict(row)
    row.pop("token", None)
    row["online"] = HUB.is_online(row.get("device_id", ""))
    return row


def _device_payload(msg: dict, device_id: str, redelivered: bool = False) -> dict:
    """推给 PC Agent 的消息体。

    附带该设备与 Web Sender 的最近往来，Agent 弹窗右侧直接渲染，
    不需要再多一次往返请求。
    """
    payload = {
        "type": "message",
        "message_id": msg["id"],
        "sender_name": msg["sender_name"],
        "content": msg["content"],
        "message_type": msg["message_type"],
        "created_at": msg["created_at"],
        "auto_close_seconds": CONFIG["message"]["popup_auto_close_seconds"],
        "history": msg_svc.history_for_device(
            device_id, limit=int(CONFIG["message"].get("history_limit", 30))
        ),
    }
    if redelivered:
        payload["redelivered"] = True
    return payload


# ============================================================
# 消息
# ============================================================
class MessageBody(BaseModel):
    sender_name: str = Field(min_length=1, max_length=32)
    content: str = Field(min_length=1, max_length=2000)
    targets: list[str] = Field(default_factory=list)
    message_type: str = "text"


@app.post("/api/messages", dependencies=[WebAuth])
async def api_send_message(body: MessageBody):
    targets = [t for t in body.targets if t]
    if not targets:
        raise HTTPException(400, "至少选择一个接收设备")
    if len(targets) > int(CONFIG["message"]["max_targets"]):
        raise HTTPException(400, "接收设备过多")

    known = {d["device_id"] for d in dev_svc.list_devices()}
    unknown = [t for t in targets if t not in known]
    if unknown:
        raise HTTPException(400, f"设备不存在: {unknown}")

    msg = msg_svc.create_message(body.sender_name, body.content, targets, body.message_type)

    delivered, failed = [], []
    for dev_id in targets:
        ok = await HUB.send_to_device(dev_id, _device_payload(msg, dev_id))
        if ok:
            msg_svc.advance(msg["id"], dev_id, "device_received")
            delivered.append(dev_id)
        else:
            failed.append(dev_id)

    msg = msg_svc.get_message(msg["id"]) or msg
    await HUB.broadcast_web({"type": "message", "message": msg})
    return {"message": msg, "delivered": delivered, "offline": failed}


@app.get("/api/messages", dependencies=[WebAuth])
async def api_messages(limit: int = 50, device_id: Optional[str] = None):
    return msg_svc.list_messages(limit=min(limit, 200), device_id=device_id)


@app.get("/api/conversations/{device_id}", dependencies=[WebAuth])
async def api_conversation(device_id: str, limit: int = 50):
    """某台设备与 Web Sender 的双向对话（含 PC 端回复）。"""
    if not dev_svc.get_device(device_id):
        raise HTTPException(404, "设备不存在")
    return msg_svc.conversation(device_id, limit=min(limit, 200))


@app.post("/api/messages/{message_id}/read", dependencies=[WebAuth])
async def api_mark_read(message_id: int, device_id: str):
    row = msg_svc.advance(message_id, device_id, "read")
    if not row:
        raise HTTPException(404, "目标不存在")
    await HUB.broadcast_web(
        {"type": "message_status", "message_id": message_id, "device_id": device_id,
         "status": row["status"], "target": row}
    )
    return row


# ============================================================
# 设备管理：桌面截图（不需要 PC 用户授权，Server 信任的 Agent）
# ============================================================
@app.post("/api/devices/{device_id}/screenshot", dependencies=[WebAuth])
async def api_screenshot(device_id: str, timeout: float = 25.0):
    if not dev_svc.get_device(device_id):
        raise HTTPException(404, "设备不存在")
    if not HUB.is_online(device_id):
        raise HTTPException(409, "设备离线，无法截图")

    # 全链路留痕，失败时能直接看出卡在哪一环
    db.log_event(device_id, "screenshot_request", "sending")
    try:
        result = await HUB.request_screenshot(device_id, timeout=timeout)
    except DeviceOffline as e:
        db.log_event(device_id, "screenshot_failed", f"offline: {e}")
        raise HTTPException(409, str(e))
    except TimeoutError as e:
        db.log_event(device_id, "screenshot_failed", f"timeout: {e}")
        raise HTTPException(504, str(e))

    raw = None
    fmt = (result.get("format") or "jpeg").lower()
    if result.get("data_base64"):
        try:
            raw = base64.b64decode(result["data_base64"])
        except (binascii.Error, ValueError):
            db.log_event(device_id, "screenshot_failed", "bad base64")
            raise HTTPException(502, "设备返回的图片数据损坏")
    if raw is None:
        err = result.get("error") or "设备未返回截图"
        db.log_event(device_id, "screenshot_failed", str(err)[:200])
        raise HTTPException(502, err)

    name = f"{device_id}_{db.now_iso().replace(':', '').replace(' ', '_')}_{uuid.uuid4().hex[:6]}.jpg"
    (SHOT_DIR / name).write_bytes(raw)
    db.log_event(device_id, "screenshot", name)
    return {
        "ok": True,
        "device_id": device_id,
        # 相对路径：裸端口和网关前缀下都能用（前端再拼 BASE）
        "url": f"shots/{name}",
        "data_url": f"data:image/{fmt};base64,{result['data_base64']}",
        "width": result.get("width"),
        "height": result.get("height"),
        "bytes": len(raw),
        "taken_at": db.now_iso(),
        "screen_locked": bool(result.get("screen_locked", False)),
    }


# ============================================================
# 设备管理：米家远程开机
# ============================================================
class WakeBody(BaseModel):
    device_id: Optional[str] = None


@app.post("/api/devices/{device_id}/wake", dependencies=[WebAuth])
async def api_wake(device_id: str, body: WakeBody | None = None):
    if not dev_svc.get_device(device_id):
        raise HTTPException(404, "设备不存在")
    if HUB.is_online(device_id):
        return {"ok": True, "already_online": True, "message": "设备已经在线"}
    try:
        outcome = await xiaomi_svc.power_on_for_device(device_id)
    except xiaomi_svc.XiaomiError as e:
        raise HTTPException(502, str(e))
    await HUB.broadcast_web({"type": "wake", "device_id": device_id, "result": outcome})
    return outcome


@app.get("/api/xiaomi/devices", dependencies=[WebAuth])
async def api_xiaomi_devices():
    return xiaomi_svc.list_xiaomi_devices()


@app.get("/api/xiaomi/status", dependencies=[WebAuth])
async def api_xiaomi_status():
    return xiaomi_svc.auth_status()


@app.get("/api/events", dependencies=[WebAuth])
async def api_events(limit: int = 100):
    return db.query("SELECT * FROM events ORDER BY id DESC LIMIT ?", (min(limit, 500),))


# ============================================================
# WebSocket：Device Agent
# ============================================================
@app.websocket("/ws/device/{device_id}")
async def ws_device(websocket: WebSocket, device_id: str):
    q = websocket.query_params
    token = q.get("token", "")
    name = q.get("name") or device_id
    dtype = q.get("type", "pc")
    platform = q.get("platform", "")
    agent_version = q.get("agent_version", "")
    enroll_token = q.get("enroll_token", "")
    ip = websocket.client.host if websocket.client else ""

    known = dev_svc.get_device(device_id)
    if known is None:
        try:
            dev_svc.enroll(device_id, name, dtype, platform, enroll_token,
                           agent_version, ip)
        except PermissionError as e:
            await websocket.close(code=4003, reason=str(e))
            return
        known = dev_svc.get_device(device_id)
        if known is None:
            await websocket.close(code=4003, reason="enroll failed")
            return
    else:
        # 已注册设备：能用 token 就用 token，否则要求注册口令（便于重装 Agent 后重新接入）
        if known["token"] and not dev_svc.verify_token(device_id, token):
            expected = CONFIG["device"]["enroll_token"]
            if not expected or enroll_token != expected:
                await websocket.close(code=4001, reason="token invalid")
                return

    await websocket.accept()
    await HUB.bind_device(device_id, websocket)
    row = dev_svc.set_online(device_id, ip=ip, agent_version=agent_version)
    await websocket.send_json({
        "type": "hello",
        "device_id": device_id,
        "token": known["token"],
        "server_time": db.now_iso(),
        "offline_after_seconds": CONFIG["device"]["offline_after_seconds"],
    })
    await HUB.broadcast_web({"type": "device_status", "device_id": device_id,
                             "status": "online", "device": _public(row)})

    try:
        # 补投离线期间未送达的消息
        for m in msg_svc.pending_for_device(device_id):
            ok = await HUB.send_to_device(device_id, _device_payload(m, device_id, redelivered=True))
            if ok:
                msg_svc.advance(m["id"], device_id, "device_received")

        while True:
            try:
                data = await websocket.receive_json()
            except WebSocketDisconnect:
                raise
            except Exception as e:
                # 单帧解析失败不该拖垮整条连接
                db.log_event(device_id, "bad_frame", f"{type(e).__name__}: {e}"[:500])
                print(f"[ws_device:{device_id}] 帧解析失败: {type(e).__name__}: {e}", flush=True)
                continue

            try:
                await handle_device_message(device_id, data)
            except Exception as e:
                # ★ 处理某一帧出错时，如果直接往上抛，整条连接会被关掉。
                # 设备侧表现就是「能收不能发，而且很随机」—— 每发一次就把自己搞掉线，
                # 看起来像连接问题，实际是这里。所以必须吞掉并留痕。
                kind = (data or {}).get("type", "?")
                db.log_event(device_id, "handler_error",
                             f"{kind}: {type(e).__name__}: {e}"[:500])
                print(f"[ws_device:{device_id}] 处理 {kind} 出错: {type(e).__name__}: {e}",
                      flush=True)
    except WebSocketDisconnect:
        pass
    except Exception as e:
        print(f"[ws_device:{device_id}] {type(e).__name__}: {e}", flush=True)
    finally:
        await HUB.unbind_device(device_id, websocket)
        dev_svc.set_offline(device_id, "connection closed")
        await HUB.broadcast_web({"type": "device_status", "device_id": device_id,
                                 "status": "offline"})


async def handle_device_message(device_id: str, data: dict) -> None:
    mtype = data.get("type")

    if mtype == "heartbeat":
        dev_svc.touch(device_id)
        await HUB.send_to_device(device_id, {"type": "heartbeat_ack", "server_time": db.now_iso()})

    elif mtype == "ack":
        msg_id = data.get("message_id")
        status = data.get("status", "device_received")
        if msg_id is None:
            return
        row = msg_svc.advance(int(msg_id), device_id, status)
        if row:
            await HUB.broadcast_web({
                "type": "message_status", "message_id": int(msg_id),
                "device_id": device_id, "status": row["status"], "target": row,
            })

    elif mtype in ("screenshot_response", "screenshot"):
        rid = data.get("request_id", "")
        if rid:
            HUB.resolve(rid, data)

    elif mtype == "reply":
        # ★ 双向对话：PC 端在弹窗里回复 → 落库 → 广播给所有浏览器
        content = (data.get("content") or "").strip()
        client_id = data.get("client_id") or ""
        if not content:
            # 空回复必须回执，否则客户端会一直停在「发送中…」
            await HUB.send_to_device(device_id, {
                "type": "reply_ack",
                "client_id": client_id,
                "message_id": None,
                "status": "empty",
            })
            return
        dev = dev_svc.get_device(device_id)
        # 昵称由 PC 端本地维护并随消息带上来；没带就用设备名兜底
        sender_name = (data.get("sender_name") or "").strip()[:32] \
            or (dev or {}).get("name") or device_id
        msg = msg_svc.create_reply(device_id, sender_name, content[:2000])
        await HUB.send_to_device(device_id, {
            "type": "reply_ack",
            "client_id": client_id,
            "message_id": msg.get("id"),
            "status": "ok",
            "created_at": msg.get("created_at"),
        })
        await HUB.broadcast_web({"type": "message", "message": msg, "reply": True})

    elif mtype == "history_request":
        rid = data.get("request_id") or ""
        limit = int(data.get("limit") or 30)
        await HUB.send_to_device(device_id, {
            "type": "history_response",
            "request_id": rid,
            "device_id": device_id,
            "messages": msg_svc.history_for_device(device_id, limit=min(limit, 200)),
        })

    elif mtype == "event":
        db.log_event(device_id, str(data.get("kind", "event")), str(data.get("detail", ""))[:500])

    elif mtype == "device_info":
        db.execute(
            "UPDATE devices SET platform=COALESCE(NULLIF(?,''), platform), "
            "name=COALESCE(NULLIF(?,''), name) WHERE device_id=?",
            (data.get("platform", ""), data.get("name", ""), device_id),
        )


# ============================================================
# WebSocket：Web 前端（订阅实时事件）
# ============================================================
@app.websocket("/ws/web")
async def ws_web(websocket: WebSocket):
    pwd = CONFIG["web"]["password"]
    if pwd:
        supplied = websocket.cookies.get(SESSION_COOKIE) or websocket.query_params.get("token", "")
        if not hmac.compare_digest(supplied, _session_token()):
            await websocket.close(code=4001, reason="unauthorized")
            return
    await websocket.accept()
    await HUB.add_web(websocket)
    try:
        await websocket.send_json({"type": "ready", "server_time": db.now_iso()})
        while True:
            data = await websocket.receive_json()
            if data.get("type") == "ping":
                await websocket.send_json({"type": "pong", "server_time": db.now_iso()})
    except WebSocketDisconnect:
        pass
    except Exception:
        pass
    finally:
        await HUB.remove_web(websocket)


# ============================================================
# 静态资源
# ============================================================
app.mount("/shots", StaticFiles(directory=str(SHOT_DIR)), name="shots")
if (WEB_DIR / "static").exists():
    app.mount("/static", StaticFiles(directory=str(WEB_DIR / "static")), name="static")


@app.get("/")
async def index():
    idx = WEB_DIR / "index.html"
    if not idx.exists():
        return JSONResponse({"ok": True, "service": "family-message", "web": "not built"})
    return FileResponse(str(idx))


def _detect_version() -> str:
    """当前正在运行的代码版本。

    存在的意义：升级时若旧进程没被杀掉，它会继续用旧代码占着端口服务，
    从外面完全看不出异常。把版本暴露到 /healthz，这类隐形故障一眼可查。
    """
    v = (os.environ.get("FM_VERSION") or "").strip()
    if v:
        return v
    for p in (Path(__file__).resolve().parent.parent / "manifest",
              Path(__file__).resolve().parent / "manifest"):
        try:
            if p.exists():
                for line in p.read_text(encoding="utf-8", errors="ignore").splitlines():
                    if line.strip().startswith("version"):
                        return line.split("=", 1)[1].strip().strip('"').strip("'")
        except Exception:
            continue
    return "unknown"


APP_VERSION = _detect_version()


@app.get("/healthz")
async def healthz():
    return {
        "ok": True,
        "version": APP_VERSION,
        "devices": len(dev_svc.list_devices()),
        "online": len(HUB.devices),
        "web_clients": len(HUB.web_clients),
        "time": db.now_iso(),
    }


@app.on_event("startup")
async def _startup():
    HUB.start_sweeper()
    print(f"[family-message] v{APP_VERSION} data_dir={CONFIG['data_dir']} "
          f"port={CONFIG['server']['port']}", flush=True)
