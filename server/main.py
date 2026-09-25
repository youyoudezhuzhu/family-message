"""家庭消息与设备控制 · 服务端

设计要点（与规格一一对应）：
- Web Sender 只有一个身份，浏览器之间不区分用户，昵称只是发送时选的标签
- Device Agent 是受信任的家庭设备，不需要登录账号、不需要申请权限
- 消息与设备管理是两层逻辑，但共用同一条长连接

⚠️ 消息是**群聊**（docs/GROUP-CHAT-MODEL.md）：一条消息发进一个共享空间，
所有已注册设备都能看到，空间里以昵称区分谁说的。
  · POST /api/messages 不再需要 targets（保留但忽略）→ 自动广播给全部已注册设备
  · 消息对外只有一个状态 status="sent"（逐设备状态只留在服务端内部记账）
  · 推给 PC 的 message 帧 = 广播给所有已连接设备，排除发起者自己（sender_device_id）
"""
from __future__ import annotations

import base64
import binascii
import json
import os
import uuid
from pathlib import Path
from typing import Any, Optional

from fastapi import (Depends, FastAPI, HTTPException, Request, Response, WebSocket,
                     WebSocketDisconnect)
from fastapi.responses import FileResponse, JSONResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel, Field

import db
import permissions
from config import CONFIG, save_config
from hub import HUB, DeviceOffline
from services import devices as dev_svc
from services import messages as msg_svc
from services import unlock as unlock_svc
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


def require_perm(perm: str):
    """要求某个权限。**后端强制** —— 前端隐藏按钮只是体验优化，不是安全边界。

    现在还没有用户/角色（登录即全部权限），但接口先建好：
    将来加"客人只能发消息、不能解锁/关机"这类角色时，只改 permissions.for_session()。
    """

    def dep(request: Request) -> None:
        require_web(request)
        if not permissions.has(permissions.for_session(), perm):
            raise HTTPException(status_code=403, detail=f"无权限：{perm}")

    return dep


UnlockAuth = Depends(require_perm(permissions.DEVICE_UNLOCK))


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
        # 前端据此决定设备操作按钮的可用性（后端仍会再查一遍）
        "permissions": permissions.for_session(),
    }


# ============================================================
# 设备
# ============================================================
@app.get("/api/devices", dependencies=[WebAuth])
async def api_devices():
    rows = dev_svc.list_devices()
    out = []
    for r in rows:
        # ★ 统一走 _public：它负责去掉 token、补 online、
        #   把 capabilities 的 JSON 字符串解析成数组、把 windows_state 规范化。
        #   以前这里各写一遍，改字段就会漏（远程解锁那次就漏了 capabilities）。
        d = _public(r)
        # 绑定了米家开关规则 → 网页端这张卡片才显示「开机」按钮
        d["xiaomi"] = xiaomi_svc.power_for_device(d["device_id"])
        out.append(d)
    return out


@app.get("/api/devices/{device_id}", dependencies=[WebAuth])
async def api_device(device_id: str):
    row = dev_svc.get_device(device_id)
    if not row:
        raise HTTPException(404, "设备不存在")
    d = _public(row)
    d["xiaomi"] = xiaomi_svc.power_for_device(device_id)
    return d


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
        # 网页端靠它判断「能不能解锁」/「为什么不能」
        "windows_state": (row.get("windows_state") or "unknown"),
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

    # capabilities 在库里是 JSON 字符串，对前端给数组（前端直接 includes 判断）
    caps = row.get("capabilities") or ""
    try:
        row["capabilities"] = json.loads(caps) if caps else []
    except Exception:
        row["capabilities"] = []
    if not isinstance(row["capabilities"], list):
        row["capabilities"] = []

    # 老版本 Agent 从没上报过会话状态 → unknown（网页端据此显示"状态未知"）
    row["windows_state"] = (row.get("windows_state") or "unknown").strip() or "unknown"
    return row


def _apply_reported_state(device_id: str, data: dict) -> bool:
    """把 PC 上报的 Windows 会话状态 / 能力清单写库。

    接受两种来源：
      · WS 连接串上的查询参数（PC 一连上就报，网页端不用等第一次心跳）
      · heartbeat / device_info 帧里的字段

    返回是否发生变化 —— 只有变化时才广播给网页端，否则每 15 秒一次的心跳
    会把网页端刷屏。
    """
    state = (data.get("windows_state") or "").strip().lower()
    caps = data.get("capabilities")

    # 查询串上是逗号分隔的字符串
    if isinstance(caps, str):
        caps = [c for c in (x.strip() for x in caps.split(",")) if c]
    if not isinstance(caps, list):
        caps = None

    if not state and caps is None:
        return False

    return dev_svc.set_session_state(
        device_id,
        windows_state=state or None,
        capabilities=caps,
    )


def _device_payload(msg: dict, device_id: str, redelivered: bool = False) -> dict:
    """推给 PC Agent 的消息体。

    附带**群聊最近的往来**（不再局限于该设备与 Web Sender 的往来）：
    一条消息进的是共享空间，弹窗右侧的上下文就该是空间里最近发生的事。
    Agent 直接渲染，不需要再多一次往返请求。

    对外只有单一状态：status = "sent"（逐设备的已送达/已显示只存在于服务端内部）。
    """
    payload = {
        "type": "message",
        "message_id": msg["id"],
        "sender_name": msg["sender_name"],
        "content": msg["content"],
        "message_type": msg["message_type"],
        "created_at": msg["created_at"],
        "status": msg_svc.STATUS_SENT,
        "auto_close_seconds": CONFIG["message"]["popup_auto_close_seconds"],
        "history": msg_svc.group_history(
            limit=int(CONFIG["message"].get("history_limit", 30)),
            viewer_device_id=device_id,
        ),
    }
    if redelivered:
        payload["redelivered"] = True
    return payload


async def _broadcast_message(msg: dict) -> list[str]:
    """群聊广播：把消息推给**所有已连接设备**，但排除发起者自己。

    群聊里你不该看到自己的消息弹自己的窗 —— 发起者判定用消息的
    `sender_device_id`（PC 回复时带上来）；网页端发的消息它是 NULL，
    所以对设备侧是「全员广播」。

    离线设备不在这一层处理：它们靠 message_targets 记账，
    上线握手时由 msg_svc.pending_for_device() 补投（见 ws_device）。
    返回真正推出去的 device_id 列表。
    """
    sender = (msg.get("sender_device_id") or "").strip()
    delivered: list[str] = []
    for device_id in list(HUB.devices.keys()):
        if sender and device_id == sender:
            continue  # 自己发的不回显给自己
        if await HUB.send_to_device(device_id, _device_payload(msg, device_id)):
            # 内部投递记账照旧推进（网页端发起的消息才有 target 行；
            # 设备回复没有 target 行，advance() 自然 no-op）
            msg_svc.advance(msg["id"], device_id, "device_received")
            delivered.append(device_id)
    return delivered


# ============================================================
# 消息
# ============================================================
class MessageBody(BaseModel):
    sender_name: str = Field(min_length=1, max_length=32)
    content: str = Field(min_length=1, max_length=2000)
    # 群聊模型：不再选接收设备。targets 保留但**忽略**（向后兼容老前端：
    # 老前端还在传 targets，传了不报错、不生效；不传也完全正常）。
    targets: list[str] = Field(default_factory=list)
    message_type: str = "text"


@app.post("/api/messages", dependencies=[WebAuth])
async def api_send_message(body: MessageBody):
    """发一条消息进群聊：**自动广播给所有已注册设备**。

    一条消息进一个共享空间，谁都能看到 —— 服务端自己把接收方算成
    「全部已注册设备」，不再由前端指定。已连接设备立刻推帧；
    离线设备靠 message_targets 记账，上线时补投（pending_for_device）。
    """
    targets = [d["device_id"] for d in dev_svc.list_devices()]

    msg = msg_svc.create_message(body.sender_name, body.content, targets, body.message_type)

    # 群聊广播：所有已连接设备（网页端发的消息 sender_device_id 为 NULL → 不排除任何设备）
    delivered = await _broadcast_message(msg)
    offline = [t for t in targets if t not in delivered]

    public = msg_svc.public_message(msg)
    await HUB.broadcast_web({"type": "message", "message": public})
    # delivered/offline 是**本次投递的即时回执**（老前端用它提示「离线设备会补投」），
    # 不是消息状态 —— 消息对外只有一个状态：已发送（public["status"]）。
    return {"message": public, "delivered": delivered, "offline": offline}


@app.get("/api/messages", dependencies=[WebAuth])
async def api_messages(limit: int = 50, device_id: Optional[str] = None):
    """群聊流：全部消息按时间（新→旧）返回。

    每条消息对外只有一个状态 status="sent"，不带逐设备的已送达/已显示。
    `device_id` 是遗留的「按设备过滤」参数（老客户端可能还在用），主流程不传。
    """
    return msg_svc.public_messages(
        msg_svc.list_messages(limit=min(limit, 200), device_id=device_id)
    )


@app.get("/api/conversations/{device_id}", dependencies=[WebAuth])
async def api_conversation(device_id: str, limit: int = 50):
    """[老视图] 某台设备与 Web Sender 的双向对话。

    群聊模型下**不再作为主流程**（主流程走 /api/messages 的群聊流），
    这里保留只是「按设备过滤的视图」，给老浏览器书签 / 老客户端兜底：
    返回体仍带 targets，老前端靠它渲染每台设备的投递状态。
    """
    if not dev_svc.get_device(device_id):
        raise HTTPException(404, "设备不存在")
    return msg_svc.conversation(device_id, limit=min(limit, 200))


@app.post("/api/messages/{message_id}/read", dependencies=[WebAuth])
async def api_mark_read(message_id: int, device_id: str):
    """[内部链路] 标记某设备已读。

    群聊模型下逐设备状态不再对外展示，所以只落库、不广播；
    接口保留是因为数据链路一点没变（以后想恢复展示很容易）。
    """
    row = msg_svc.advance(message_id, device_id, "read")
    if not row:
        raise HTTPException(404, "目标不存在")
    return {"ok": True, "message_id": message_id, "status": msg_svc.STATUS_SENT}


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
    """网页端「开机」按钮：执行绑定的米家开关动作（默认是「开」）。"""
    if not dev_svc.get_device(device_id):
        raise HTTPException(404, "设备不存在")
    if HUB.is_online(device_id):
        return {"ok": True, "already_online": True, "message": "设备已经在线"}
    try:
        outcome = await xiaomi_svc.apply_for_device(device_id)
    except xiaomi_svc.NeedLogin as e:
        raise HTTPException(401, str(e))
    except xiaomi_svc.XiaomiError as e:
        raise HTTPException(502, str(e))
    await HUB.broadcast_web({"type": "wake", "device_id": device_id, "result": outcome})
    return outcome


@app.post("/api/devices/{device_id}/shutdown", dependencies=[WebAuth])
async def api_shutdown(device_id: str):
    """网页端「关机」按钮：让 PC 端 Agent 执行关机。

    这是「设备管理」能力：PC Agent 是受 Server 信任的家庭设备 Agent，
    收到指令即执行，不需要在 PC 上再点一次确认（与本项目 §13 的权限模型一致）。
    """
    if not dev_svc.get_device(device_id):
        raise HTTPException(404, "设备不存在")
    if not HUB.is_online(device_id):
        raise HTTPException(409, "设备离线，无法发送关机指令")

    ok = await HUB.send_to_device(device_id, {"type": "shutdown", "delay_seconds": 5})
    if not ok:
        raise HTTPException(502, "指令下发失败（连接已断开）")

    db.log_event(device_id, "shutdown", "web 下发关机指令")
    await HUB.broadcast_web({"type": "shutdown_sent", "device_id": device_id})
    return {"ok": True, "device_id": device_id, "message": "关机指令已下发，PC 将在数秒后关机"}


# ============================================================
# 米家（官方 OAuth2 方式，实现对照 XiaoMi/ha_xiaomi_home）
# ============================================================
class XiaomiCodeBody(BaseModel):
    code: str = Field(default="", max_length=4000)


class XiaomiBindBody(BaseModel):
    name: str = Field(default="", max_length=64)
    miot_device_id: str = Field(default="", max_length=128)
    urn: str = Field(default="", max_length=128)
    device_type: str = Field(default="plug", max_length=32)
    power_siid: int = 2
    power_piid: int = 1
    power_action: str = Field(default="on", max_length=8)
    # 要写入的具体值（bool / int / str）。给了它就以它为准，power_action 只作旧数据兜底。
    power_value: Any = None
    target_device_id: str = Field(default="", max_length=64)


class XiaomiPatchBody(BaseModel):
    name: Optional[str] = None
    device_type: Optional[str] = None
    power_value: Any = None
    target_device_id: Optional[str] = None
    power_siid: Optional[int] = None
    power_piid: Optional[int] = None
    power_action: Optional[str] = None
    enabled: Optional[int] = None


class XiaomiPowerBody(BaseModel):
    on: bool = True


@app.post("/api/devices/{device_id}/unlock", dependencies=[UnlockAuth])
async def api_device_unlock(device_id: str):
    """远程解锁：签发一次性令牌并下发给 PC。

    **这里不接受也不传递任何 Windows 密码** —— 令牌里只有 request_id / nonce /
    过期时间。真正的凭据只存在于目标 PC 本地，由 PC 自己使用。

    结果不经 HTTP 返回：PC 的应答走 WebSocket 回来，再广播给所有浏览器
    （前端监听 unlock_result）。
    """
    dev = dev_svc.get_device(device_id)
    online = HUB.is_online(device_id)

    # 限流优先判断：被锁了就别说别的了，免得给探测者反馈设备状态
    until = unlock_svc.guard_state(device_id)
    if until:
        raise HTTPException(status_code=429,
                            detail=f"解锁尝试过于频繁，请于 {until} 之后再试")

    ok, reason = unlock_svc.can_unlock(dev, online)
    if not ok:
        raise HTTPException(status_code=404 if not dev else 409, detail=reason)

    req = unlock_svc.create(device_id)
    sent = await HUB.send_to_device(device_id, {
        "type": "unlock_request",
        "request_id": req["request_id"],
        "device_id": device_id,
        "action": req["action"],
        "nonce": req["nonce"],
        "expires_at": req["expires_at"],
    })
    if not sent:
        # 在线状态是缓存的，真正下发时对方可能刚好断了
        unlock_svc.mark(req["request_id"], "error", "下发失败：设备连接不可用", device_id=device_id)
        unlock_svc.note_result(device_id, ok=False)
        raise HTTPException(status_code=409, detail="设备连接不可用，解锁请求未送达")

    await HUB.broadcast_web({
        "type": "unlock_pending",
        "device_id": device_id,
        "request_id": req["request_id"],
        "expires_at": req["expires_at"],
    })
    return {"ok": True, "request_id": req["request_id"], "expires_at": req["expires_at"]}


@app.get("/api/xiaomi/status", dependencies=[WebAuth])
async def api_xiaomi_status():
    st = xiaomi_svc.auth_status()
    st["enabled"] = bool(CONFIG["xiaomi"]["enabled"])
    st["redirect_url"] = xiaomi_svc._redirect_url()
    return st


@app.get("/api/xiaomi/auth-url", dependencies=[WebAuth])
async def api_xiaomi_auth_url():
    """第一步：拿授权地址，让用户在浏览器里打开并同意。"""
    return xiaomi_svc.build_auth_url()


@app.post("/api/xiaomi/exchange", dependencies=[WebAuth])
async def api_xiaomi_exchange(body: XiaomiCodeBody):
    """第二步：用户把回调地址（或其中的 code）粘回来，换 token。"""
    try:
        st = await xiaomi_svc.exchange_code(body.code)
    except xiaomi_svc.XiaomiError as e:
        raise HTTPException(400, str(e))

    if not CONFIG["xiaomi"]["enabled"]:
        CONFIG["xiaomi"]["enabled"] = True
        save_config()
    return st


@app.post("/api/xiaomi/logout", dependencies=[WebAuth])
async def api_xiaomi_logout():
    xiaomi_svc.clear_auth()
    return {"ok": True}


@app.post("/api/xiaomi/discover", dependencies=[WebAuth])
async def api_xiaomi_discover():
    try:
        return await xiaomi_svc.discover_devices()
    except xiaomi_svc.NeedLogin as e:
        raise HTTPException(401, str(e))
    except xiaomi_svc.XiaomiError as e:
        raise HTTPException(502, str(e))


@app.get("/api/xiaomi/spec", dependencies=[WebAuth])
async def api_xiaomi_spec(urn: str = ""):
    """某型号有哪些「可写」属性 —— 让绑定不再局限于开/关。

    规格来自米家公开的 miot-spec（与官方 ha_xiaomi_home 用的是同一份）。
    """
    try:
        spec = await xiaomi_svc.fetch_spec(urn)
    except xiaomi_svc.XiaomiError as e:
        raise HTTPException(502, str(e))
    return {"urn": urn, "props": xiaomi_svc.controllable_props(spec)}


@app.get("/api/xiaomi/devices", dependencies=[WebAuth])
async def api_xiaomi_devices():
    return xiaomi_svc.list_xiaomi_devices()


@app.post("/api/xiaomi/devices", dependencies=[WebAuth])
async def api_xiaomi_bind(body: XiaomiBindBody):
    if not body.miot_device_id:
        raise HTTPException(400, "缺少米家设备 ID")
    row = xiaomi_svc.bind_xiaomi_device(
        body.name or body.miot_device_id, body.miot_device_id, body.urn,
        body.device_type, body.power_siid, body.power_piid, body.target_device_id,
        body.power_action, body.power_value,
    )
    return row


@app.patch("/api/xiaomi/devices/{row_id}", dependencies=[WebAuth])
async def api_xiaomi_update(row_id: int, body: XiaomiPatchBody):
    row = xiaomi_svc.update_xiaomi_device(
        row_id, name=body.name, device_type=body.device_type,
        target_device_id=body.target_device_id, power_siid=body.power_siid,
        power_piid=body.power_piid, power_action=body.power_action,
        power_value=body.power_value, enabled=body.enabled,
    )
    if not row:
        raise HTTPException(404, "米家设备不存在")
    return row


@app.delete("/api/xiaomi/devices/{row_id}", dependencies=[WebAuth])
async def api_xiaomi_unbind(row_id: int):
    if not xiaomi_svc.unbind_xiaomi_device(row_id):
        raise HTTPException(404, "米家设备不存在")
    return {"ok": True}


@app.get("/api/xiaomi/devices/{row_id}/state", dependencies=[WebAuth])
async def api_xiaomi_state(row_id: int):
    row = xiaomi_svc.get_xiaomi_device(row_id)
    if not row:
        raise HTTPException(404, "米家设备不存在")
    try:
        on = await xiaomi_svc.get_power(row["miot_device_id"],
                                        row["power_siid"] or 2, row["power_piid"] or 1)
    except xiaomi_svc.NeedLogin as e:
        raise HTTPException(401, str(e))
    except xiaomi_svc.XiaomiError as e:
        raise HTTPException(502, str(e))
    return {"id": row_id, "on": on}


@app.post("/api/xiaomi/devices/{row_id}/power", dependencies=[WebAuth])
async def api_xiaomi_power(row_id: int, body: XiaomiPowerBody):
    row = xiaomi_svc.get_xiaomi_device(row_id)
    if not row:
        raise HTTPException(404, "米家设备不存在")
    try:
        await xiaomi_svc.set_power(row["miot_device_id"], body.on,
                                   row["power_siid"] or 2, row["power_piid"] or 1)
    except xiaomi_svc.NeedLogin as e:
        raise HTTPException(401, str(e))
    except xiaomi_svc.XiaomiError as e:
        raise HTTPException(502, str(e))

    db.log_event(None, "xiaomi_power", f"{row['name']} -> {'on' if body.on else 'off'}")
    await HUB.broadcast_web({"type": "xiaomi", "id": row_id, "on": body.on})
    return {"ok": True, "id": row_id, "on": body.on, "name": row["name"]}


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
    # Phase 1：PC 一连上就把 Windows 会话状态带上来，
    # 网页端不用干等第一次心跳才知道"停在登录界面"
    reported_state = q.get("windows_state", "")
    reported_caps = q.get("capabilities", "")
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
    if _apply_reported_state(device_id, {"windows_state": reported_state,
                                         "capabilities": reported_caps}):
        row = dev_svc.get_device(device_id) or row
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
        # 会话状态有变化才广播 —— 否则每 15 秒一次的心跳会把网页端刷屏
        if _apply_reported_state(device_id, data):
            await HUB.broadcast_web({
                "type": "device_status", "device_id": device_id, "status": "online",
                "device": _public(dev_svc.get_device(device_id)),
            })
        await HUB.send_to_device(device_id, {"type": "heartbeat_ack", "server_time": db.now_iso()})

    elif mtype == "ack":
        msg_id = data.get("message_id")
        status = data.get("status", "device_received")
        if msg_id is None:
            return
        # 群聊模型：PC 的 ack / popup_displayed 上报**照旧落库**（投递记账），
        # 但不再对外广播逐设备的已送达/已显示 —— 对外只有「已发送」。
        # 以后想恢复展示：放开下面那两行广播即可（数据一直都在，不用改结构）。
        msg_svc.advance(int(msg_id), device_id, status)
        # row = msg_svc.advance(int(msg_id), device_id, status)
        # if row:
        #     await HUB.broadcast_web({
        #         "type": "message_status", "message_id": int(msg_id),
        #         "device_id": device_id, "status": row["status"], "target": row,
        #     })

    elif mtype in ("screenshot_response", "screenshot"):
        rid = data.get("request_id", "")
        if rid:
            HUB.resolve(rid, data)

    elif mtype == "reply":
        # ★ 群聊：PC 端在弹窗里回一句 → 落库 → 广播给所有浏览器 + 其他所有设备
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
        # PC 端可能把 sender_device_id 带上来（群聊广播要据此跳过发起者自己）。
        # 但**身份以连接为准**：不接受一台设备冒充另一台。
        claimed = (data.get("sender_device_id") or "").strip()
        if claimed and claimed != device_id:
            db.log_event(device_id, "reply_sender_mismatch", f"claimed={claimed[:64]}")
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
        await HUB.broadcast_web(
            {"type": "message", "message": msg_svc.public_message(msg), "reply": True}
        )
        # 群里其他人（其他 PC）也要看到这句话；发起者自己由 sender_device_id 排除
        await _broadcast_message(msg)

    elif mtype == "history_request":
        rid = data.get("request_id") or ""
        limit = int(data.get("limit") or 30)
        await HUB.send_to_device(device_id, {
            "type": "history_response",
            "request_id": rid,
            "device_id": device_id,
            "messages": msg_svc.group_history(
                limit=min(limit, 200), viewer_device_id=device_id
            ),
        })

    elif mtype == "unlock_result":
        # ★ 一次性语义的最后一道闸：used_at 已经写过就直接丢，并且审计留痕。
        #   PC 侧也有本地重放缓存，两边都记 —— 任何一边漏了另一边兜底。
        rid = (data.get("request_id") or "").strip()
        status = (data.get("status") or "").strip().lower()
        reason = (data.get("reason") or "").strip()
        row = unlock_svc.get(rid)
        if not row:
            db.log_event(device_id, "unlock_unknown", f"未知 request_id: {rid[:64]}")
            return
        if row.get("device_id") != device_id:
            # 把 A 设备的令牌拿去 B 设备用（需求 §28 场景 7）
            db.log_event(device_id, "unlock_wrong_device",
                         f"{rid} 属于 {row.get('device_id')}")
            await HUB.send_to_device(device_id, {
                "type": "unlock_result_ack", "request_id": rid,
                "status": "rejected", "reason": "not_mine",
            })
            return
        if row.get("used_at"):
            db.log_event(device_id, "unlock_replay", f"{rid} 已经结过单，忽略重复应答")
            return

        # armed 只是中间态（PC 已收到并校验通过，等真正解锁），不结单
        if status == "armed":
            await HUB.broadcast_web({
                "type": "unlock_result", "device_id": device_id,
                "request_id": rid, "status": "armed", "reason": reason,
            })
            return

        ok = status == "success"
        unlock_svc.mark(rid, status or "unknown", reason, device_id=device_id)
        unlock_svc.note_result(device_id, ok=ok)
        await HUB.broadcast_web({
            "type": "unlock_result", "device_id": device_id,
            "request_id": rid, "status": status or "unknown", "reason": reason,
        })

    elif mtype == "event":
        db.log_event(device_id, str(data.get("kind", "event")), str(data.get("detail", ""))[:500])

    elif mtype == "device_info":
        db.execute(
            "UPDATE devices SET platform=COALESCE(NULLIF(?,''), platform), "
            "name=COALESCE(NULLIF(?,''), name) WHERE device_id=?",
            (data.get("platform", ""), data.get("name", ""), device_id),
        )
        if _apply_reported_state(device_id, data):
            await HUB.broadcast_web({
                "type": "device_status", "device_id": device_id, "status": "online",
                "device": _public(dev_svc.get_device(device_id)),
            })


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
    # HTML 必须每次回源校验，否则升级后浏览器会拿缓存的旧页面
    # （旧页面引用的还是旧版 static 资源，米家设置之类的新功能就「看不见」）
    return FileResponse(str(idx), headers={
        "Cache-Control": "no-cache, no-store, must-revalidate",
        "Pragma": "no-cache",
    })


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
