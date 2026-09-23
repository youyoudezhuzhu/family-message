"""Python 参考 Agent。

用途有两个：
1. 作为本项目的「协议参考实现」，Windows C# Agent 完全对齐这套协议
2. 在 NAS / Linux / macOS 上就能跑通端到端链路，方便调试服务端

协议（与服务端 hub.py 对齐）：
  连接  ws://host:port/ws/device/{device_id}?token=&name=&type=&platform=&enroll_token=
  下行  {"type":"hello"} / {"type":"heartbeat_ack"} / {"type":"message"}
        {"type":"screenshot_request","request_id"}
  上行  {"type":"heartbeat"}
        {"type":"ack","message_id":N,"status":"device_received|popup_displayed|read"}
        {"type":"screenshot_response","request_id","format","data_base64","width","height"}
"""
from __future__ import annotations

import argparse
import asyncio
import base64
import io
import json
import os
import platform
import socket
import sys
import time
import uuid
from pathlib import Path

try:
    import websockets
except ImportError:
    print("需要 websockets 库：uv pip install websockets", file=sys.stderr)
    raise

CONFIG_DIR = Path.home() / ".family-agent"
CONFIG_FILE = CONFIG_DIR / "config.json"

DEFAULT_CONFIG = {
    "server_url": "ws://192.168.31.50:18801",
    "device_id": f"pc_{socket.gethostname().lower()[:12]}",
    "device_name": socket.gethostname(),
    "device_type": "pc",
    "token": "",
    "enroll_token": "family-2026",
}


def load_config() -> dict:
    if CONFIG_FILE.exists():
        cfg = json.loads(CONFIG_FILE.read_text(encoding="utf-8"))
        return {**DEFAULT_CONFIG, **cfg}
    CONFIG_DIR.mkdir(parents=True, exist_ok=True)
    CONFIG_FILE.write_text(json.dumps(DEFAULT_CONFIG, ensure_ascii=False, indent=2), encoding="utf-8")
    return dict(DEFAULT_CONFIG)


def save_config(cfg: dict) -> None:
    CONFIG_DIR.mkdir(parents=True, exist_ok=True)
    CONFIG_FILE.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding="utf-8")


# ------------------------------------------------------------
# 截图
# ------------------------------------------------------------
def capture_screen() -> dict:
    """按优先级尝试各种截屏方式，返回 {format, data_base64, width, height, screen_locked}。"""
    # 1) mss（跨平台，Linux/macOS/Windows 都行）
    try:
        import mss  # type: ignore
        import mss.tools  # type: ignore

        with mss.mss() as sct:
            mon = sct.monitors[1]
            shot = sct.grab(mon)
            from PIL import Image  # type: ignore

            img = Image.frombytes("RGB", shot.size, shot.bgra, "raw", "BGRX")
            buf = io.BytesIO()
            img.save(buf, format="JPEG", quality=80)
            return {
                "format": "jpeg",
                "data_base64": base64.b64encode(buf.getvalue()).decode(),
                "width": shot.size[0],
                "height": shot.size[1],
                "screen_locked": False,
            }
    except Exception:
        pass

    # 2) PIL ImageGrab（Windows / macOS）
    try:
        from PIL import ImageGrab  # type: ignore

        img = ImageGrab.grab()
        buf = io.BytesIO()
        img.convert("RGB").save(buf, format="JPEG", quality=80)
        return {
            "format": "jpeg",
            "data_base64": base64.b64encode(buf.getvalue()).decode(),
            "width": img.width,
            "height": img.height,
            "screen_locked": False,
        }
    except Exception:
        pass

    # 3) 无显示环境下生成一张诊断图，保证链路可验证
    return _placeholder_shot()


def _placeholder_shot() -> dict:
    w, h = 1280, 720
    from PIL import Image, ImageDraw  # type: ignore

    img = Image.new("RGB", (w, h), (24, 26, 33))
    d = ImageDraw.Draw(img)
    d.rectangle([40, 40, w - 40, h - 40], outline=(90, 200, 250), width=3)
    lines = [
        "Family Agent · 截屏占位图",
        f"主机: {socket.gethostname()}",
        f"系统: {platform.platform()}",
        f"时间: {time.strftime('%Y-%m-%d %H:%M:%S')}",
        "该设备当前无可用图形桌面",
        "（Windows Agent 会截取真实桌面）",
    ]
    y = 160
    for line in lines:
        d.text((80, y), line, fill=(230, 235, 245))
        y += 60
    buf = io.BytesIO()
    img.save(buf, format="JPEG", quality=85)
    return {
        "format": "jpeg",
        "data_base64": base64.b64encode(buf.getvalue()).decode(),
        "width": w, "height": h, "screen_locked": False,
    }


# ------------------------------------------------------------
# Agent 主体
# ------------------------------------------------------------
class Agent:
    def __init__(self, cfg: dict):
        self.cfg = cfg
        self.ws = None
        self.pending_replies: dict[str, str] = {}

    @property
    def url(self) -> str:
        base = self.cfg["server_url"].rstrip("/")
        if base.startswith("http://"):
            base = "ws://" + base[7:]
        elif base.startswith("https://"):
            base = "wss://" + base[8:]
        from urllib.parse import urlencode

        q = urlencode({
            "token": self.cfg.get("token", ""),
            "name": self.cfg["device_name"],
            "type": self.cfg.get("device_type", "pc"),
            "platform": platform.platform(),
            "agent_version": "py-0.1.0",
            "enroll_token": self.cfg.get("enroll_token", ""),
        })
        return f"{base}/ws/device/{self.cfg['device_id']}?{q}"

    async def run(self) -> None:
        delay = 2
        while True:
            try:
                print(f"[agent] 连接 {self.url}", flush=True)
                async with websockets.connect(self.url, ping_interval=20, ping_timeout=20) as ws:
                    self.ws = ws
                    delay = 2
                    hb = asyncio.create_task(self.heartbeat_loop())
                    stdin = asyncio.create_task(self.stdin_loop())
                    try:
                        async for raw in ws:
                            await self.on_message(json.loads(raw))
                    finally:
                        hb.cancel()
                        stdin.cancel()
            except Exception as e:
                print(f"[agent] 断开: {type(e).__name__}: {e} — {delay}s 后重连", flush=True)
            self.ws = None
            await asyncio.sleep(delay)
            delay = min(delay * 2, 30)

    async def stdin_loop(self) -> None:
        """终端里输入一行 = 在「弹窗」里回复一条。"""
        loop = asyncio.get_running_loop()
        while True:
            line = await loop.run_in_executor(None, sys.stdin.readline)
            if not line:
                return
            text = line.strip()
            if not text:
                continue
            await self.reply(text)

    async def reply(self, text: str) -> None:
        if self.ws is None:
            print("[agent] 未连接，回复未发出", flush=True)
            return
        cid = uuid.uuid4().hex[:12]
        self.pending_replies[cid] = text
        await self.ws.send(json.dumps({
            "type": "reply",
            "content": text,
            "client_id": cid,
            # 昵称由本机本地维护；Windows 端可在设置里随时增删改
            "sender_name": self.cfg.get("reply_name") or self.cfg["device_name"],
        }))
        print(f"[agent] ↑ 已回复: {text}", flush=True)

    async def request_history(self, limit: int = 30) -> None:
        if self.ws is None:
            return
        await self.ws.send(json.dumps(
            {"type": "history_request", "request_id": uuid.uuid4().hex[:12], "limit": limit}
        ))

    async def heartbeat_loop(self) -> None:
        while True:
            await asyncio.sleep(15)
            try:
                await self.ws.send(json.dumps({"type": "heartbeat"}))
            except Exception:
                return

    async def on_message(self, data: dict) -> None:
        mtype = data.get("type")
        if mtype == "hello":
            if data.get("token") and data["token"] != self.cfg.get("token"):
                self.cfg["token"] = data["token"]
                save_config(self.cfg)
                print("[agent] 已保存设备 token", flush=True)
            print(f"[agent] 已上线 device_id={data.get('device_id')} 服务器时间={data.get('server_time')}", flush=True)

        elif mtype == "message":
            await self.show_popup(data)

        elif mtype == "screenshot_request":
            await self.handle_screenshot(data.get("request_id", ""))

        elif mtype == "reply_ack":
            cid = data.get("client_id", "")
            sent = self.pending_replies.pop(cid, None)
            print(f"[agent] ✓ 服务器已接收回复（#{data.get('message_id')}）{sent or ''}", flush=True)

        elif mtype == "history_response":
            print(f"[agent] ↓ 历史对话（{len(data.get('messages', []))} 条）", flush=True)
            for h in data.get("messages", []):
                arrow = "→" if h["direction"] == "in" else "←"
                print(f"    {arrow} [{h['created_at']}] {h['sender_name']}: {h['content']}", flush=True)

    async def show_popup(self, data: dict) -> None:
        """控制台全屏「弹窗」：Windows Agent 用的是真·全屏无边框置顶窗口，
        右侧渲染历史对话、底部是回复输入框。"""
        msg_id = data.get("message_id")
        await self.ack(msg_id, "popup_displayed")

        history = data.get("history") or []
        print("\n" + "═" * 64, flush=True)
        print(f"  【左侧】新消息   来自 {data.get('sender_name')} → {self.cfg['device_name']}", flush=True)
        print(f"     {data.get('content')}", flush=True)
        print(f"     {data.get('created_at')}", flush=True)
        print("─" * 64, flush=True)
        print(f"  【右侧】历史对话（{len(history)} 条）", flush=True)
        for h in history:
            arrow = "←" if h["direction"] == "in" else "→"
            mark = " ◀ 本次" if h.get("message_id") == msg_id else ""
            print(f"     {arrow} [{h['created_at']}] {h['sender_name']}: {h['content']}{mark}",
                  flush=True)
        print("═" * 64, flush=True)
        print("  （直接输入一行文字 + 回车 = 回复）", flush=True)
        await self.ack(msg_id, "read")

    async def ack(self, message_id, status: str) -> None:
        if message_id is None or self.ws is None:
            return
        try:
            await self.ws.send(json.dumps({"type": "ack", "message_id": message_id, "status": status}))
        except Exception:
            pass

    async def handle_screenshot(self, request_id: str) -> None:
        t0 = time.time()
        payload: dict
        try:
            payload = await asyncio.to_thread(capture_screen)
            payload.update({"type": "screenshot_response", "request_id": request_id})
        except Exception as e:
            payload = {"type": "screenshot_response", "request_id": request_id,
                       "error": f"{type(e).__name__}: {e}"}
        print(f"[agent] 截图完成 {time.time() - t0:.2f}s", flush=True)
        try:
            await self.ws.send(json.dumps(payload))
        except Exception as e:
            print(f"[agent] 回传截图失败: {e}", flush=True)


def main() -> None:
    ap = argparse.ArgumentParser(description="Family Message Python Agent")
    ap.add_argument("--server", help="服务端地址，如 ws://192.168.31.50:18801")
    ap.add_argument("--device-id")
    ap.add_argument("--device-name")
    ap.add_argument("--enroll-token")
    ap.add_argument("--reply-name", help="回复时用的昵称（纯本地）")
    args = ap.parse_args()

    cfg = load_config()
    if args.server:
        cfg["server_url"] = args.server
    if args.device_id:
        cfg["device_id"] = args.device_id
    if args.device_name:
        cfg["device_name"] = args.device_name
    if args.enroll_token:
        cfg["enroll_token"] = args.enroll_token
    if args.reply_name:
        cfg["reply_name"] = args.reply_name
    save_config(cfg)
    print(f"[agent] 配置: {CONFIG_FILE}")
    asyncio.run(Agent(cfg).run())


if __name__ == "__main__":
    main()
