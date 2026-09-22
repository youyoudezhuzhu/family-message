"""连接中心：设备长连接、Web 订阅者、请求/响应配对、离线巡检。

这是整套系统的中枢：
- Device Agent 通过 /ws/device/{device_id} 建立长连接
- 浏览器通过 /ws/web 订阅实时事件（设备上下线、消息状态流转）
- 截图等「一问一答」的请求按 request_id 配对 Future，实现同步语义
"""
from __future__ import annotations

import asyncio
import uuid
from typing import Any, Optional

from fastapi import WebSocket

import db
from config import CONFIG


class DeviceOffline(Exception):
    pass


class DeviceHub:
    def __init__(self) -> None:
        self.devices: dict[str, WebSocket] = {}
        self.web_clients: set[WebSocket] = set()
        self.pending: dict[str, asyncio.Future] = {}
        self._lock = asyncio.Lock()
        self._sweeper: Optional[asyncio.Task] = None

    # ---------------- 设备连接 ----------------

    async def bind_device(self, device_id: str, ws: WebSocket) -> None:
        async with self._lock:
            old = self.devices.get(device_id)
            self.devices[device_id] = ws
        if old is not None and old is not ws:
            try:
                await old.close(code=4000, reason="replaced by new connection")
            except Exception:
                pass

    async def unbind_device(self, device_id: str, ws: WebSocket) -> None:
        async with self._lock:
            if self.devices.get(device_id) is ws:
                self.devices.pop(device_id, None)

    def is_online(self, device_id: str) -> bool:
        return device_id in self.devices

    async def send_to_device(self, device_id: str, payload: dict) -> bool:
        ws = self.devices.get(device_id)
        if ws is None:
            return False
        try:
            await ws.send_json(payload)
            return True
        except Exception:
            await self.unbind_device(device_id, ws)
            return False

    # ---------------- Web 订阅者 ----------------

    async def add_web(self, ws: WebSocket) -> None:
        self.web_clients.add(ws)

    async def remove_web(self, ws: WebSocket) -> None:
        self.web_clients.discard(ws)

    async def broadcast_web(self, payload: dict) -> None:
        dead = []
        for ws in list(self.web_clients):
            try:
                await ws.send_json(payload)
            except Exception:
                dead.append(ws)
        for ws in dead:
            self.web_clients.discard(ws)

    # ---------------- 一问一答 ----------------

    async def request_screenshot(self, device_id: str, timeout: float = 25.0) -> dict:
        if device_id not in self.devices:
            raise DeviceOffline(f"设备 {device_id} 当前离线")
        request_id = uuid.uuid4().hex
        loop = asyncio.get_running_loop()
        fut: asyncio.Future = loop.create_future()
        self.pending[request_id] = fut
        try:
            ok = await self.send_to_device(
                device_id, {"type": "screenshot_request", "request_id": request_id}
            )
            if not ok:
                raise DeviceOffline(f"设备 {device_id} 连接已断开")
            return await asyncio.wait_for(fut, timeout=timeout)
        except asyncio.TimeoutError:
            raise TimeoutError(f"设备 {device_id} 截图超时（{timeout:.0f}s）")
        finally:
            self.pending.pop(request_id, None)

    def resolve(self, request_id: str, payload: dict) -> bool:
        fut = self.pending.get(request_id)
        if fut and not fut.done():
            fut.set_result(payload)
            return True
        return False

    # ---------------- 离线巡检 ----------------

    async def mark_offline(self, device_id: str, reason: str) -> None:
        db.execute(
            "UPDATE devices SET status='offline' WHERE device_id=?", (device_id,)
        )
        db.log_event(device_id, "offline", reason)
        await self.broadcast_web(
            {"type": "device_status", "device_id": device_id, "status": "offline",
             "last_seen": db.query_one(
                 "SELECT last_seen FROM devices WHERE device_id=?", (device_id,)
             )["last_seen"] if db.query_one(
                 "SELECT last_seen FROM devices WHERE device_id=?", (device_id,)) else None}
        )

    async def sweep_loop(self, interval: float = 10.0) -> None:
        """定期把心跳超时的设备置为离线（防止 Agent 崩溃后状态假在线）。"""
        limit = int(CONFIG["device"]["offline_after_seconds"])
        while True:
            try:
                await asyncio.sleep(interval)
                from datetime import datetime

                for row in db.query("SELECT device_id, status, last_seen FROM devices"):
                    if row["device_id"] in self.devices:
                        continue
                    if row["status"] != "online":
                        continue
                    stale = True
                    if row["last_seen"]:
                        try:
                            seen = datetime.strptime(row["last_seen"], "%Y-%m-%d %H:%M:%S")
                            stale = (datetime.now() - seen).total_seconds() > limit
                        except Exception:
                            stale = True
                    if stale:
                        await self.mark_offline(row["device_id"], "heartbeat timeout")
            except asyncio.CancelledError:
                raise
            except Exception as e:  # 巡检永不死掉
                print(f"[sweeper] error: {e}", flush=True)

    def start_sweeper(self) -> None:
        if self._sweeper is None or self._sweeper.done():
            self._sweeper = asyncio.create_task(self.sweep_loop())


HUB = DeviceHub()
