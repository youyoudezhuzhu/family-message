"""连接中心：设备长连接、Web 订阅者、请求/响应配对、离线巡检。

这是整套系统的中枢：
- Device Agent 通过 /ws/device/{device_id} 建立长连接
- 浏览器通过 /ws/web 订阅实时事件（设备上下线、消息状态流转）
- 截图等「一问一答」的请求按 request_id 配对 Future，实现同步语义
"""
from __future__ import annotations

import asyncio
import itertools
import uuid
import weakref
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

        # ── 连接台账（**只用于观测/日志**，不参与任何投递或踢人决策）──────
        # 同一 device_id 上可能先后/同时挂着多条连接（开机自启的 headless 与
        # 登录后的交互式就是这种情况），光看 device_id 分不出「这句话到底
        # 写到了哪条 WebSocket 上」。给每条连接发一个短号，日志里就能对上号：
        #   conn_ids     **连接对象本身（弱引用）** -> 连接短号 c0007
        #   conn_device  连接短号     -> device_id
        #   device_conns device_id   -> {连接短号}（当前**登记在册**的所有连接，
        #                              正常只有 1 条；对踢瞬间会短暂出现 2 条）
        # ⚠ 2026-09-29 修：这里原来用 `id(ws)` 当键 —— CPython 会**复用对象地址**，
        #   对踢压测（~4000 次顶替）时新连接会把旧条目覆盖掉，旧短号便永远留在
        #   device_conns 里（实测残留 32 条、18 秒不降，`test_ws_kick` 的「台账排空」
        #   判据就是被这个卡红的）。改用 WeakKeyDictionary：键是连接对象、被回收即自动
        #   清掉，与 unbind 的显式清理形成双保险。
        self._conn_seq = itertools.count(1)
        self.conn_ids: "weakref.WeakKeyDictionary[Any, str]" = weakref.WeakKeyDictionary()
        self.conn_device: dict[str, str] = {}
        self.device_conns: dict[str, set[str]] = {}

    # ---------------- 连接台账（观测用） ----------------

    def conn_id(self, ws: Optional[WebSocket]) -> str:
        """取某条 WebSocket 的连接短号；没登记过返回 c----。"""
        if ws is None:
            return "c----"
        return self.conn_ids.get(ws, "c----")

    def conn_count(self, device_id: str) -> int:
        """该 device_id 当前登记在册的连接数（>1 说明有两条连接叠着）。"""
        return len(self.device_conns.get(device_id) or ())

    def _conn_register(self, device_id: str, ws: WebSocket) -> str:
        cid = f"c{next(self._conn_seq):04d}"
        self.conn_ids[ws] = cid
        self.conn_device[cid] = device_id
        self.device_conns.setdefault(device_id, set()).add(cid)
        return cid

    def _conn_unregister(self, device_id: str, ws: WebSocket) -> str:
        cid = self.conn_ids.pop(ws, "c----")
        self.conn_device.pop(cid, None)
        conns = self.device_conns.get(device_id)
        if conns is not None:
            conns.discard(cid)
            if not conns:
                self.device_conns.pop(device_id, None)
        return cid

    # ---------------- 设备连接 ----------------

    async def bind_device(self, device_id: str, ws: WebSocket) -> None:
        cid = self._conn_register(device_id, ws)
        async with self._lock:
            old = self.devices.get(device_id)
            self.devices[device_id] = ws
        if old is not None and old is not ws:
            old_cid = self.conn_id(old)
            # 这一行是「对踢」现场的第一现场证据：谁顶掉了谁、当时叠了几条。
            print(
                f"[WS][BIND] device={device_id} conn={cid} replaced={old_cid} "
                f"connections={self.conn_count(device_id)}",
                flush=True,
            )
            try:
                await old.close(code=4000, reason="replaced by new connection")
            except Exception:
                pass
        else:
            print(
                f"[WS][BIND] device={device_id} conn={cid} replaced=- "
                f"connections={self.conn_count(device_id)}",
                flush=True,
            )

    async def unbind_device(self, device_id: str, ws: WebSocket) -> None:
        was_active = False
        async with self._lock:
            if self.devices.get(device_id) is ws:
                self.devices.pop(device_id, None)
                was_active = True
        cid = self._conn_unregister(device_id, ws)
        # ★ 2026-09-29：顺手清残留。有少数连接（对踢压测 4524 条里 17 条）的关闭处理
        #   没跑到 finally（socket 卡在 close 上），它们的短号会永远留在 device_conns 里。
        #   conn_ids 是**弱引用**表（连接被回收即自动消失），所以它的 values 就是
        #   「此刻真的还活着的连接」这个权威集合 —— 在这里对一次账，台账能自愈。
        self._prune_ledger()
        print(
            f"[WS][UNBIND] device={device_id} conn={cid} "
            f"was_active={'true' if was_active else 'false'} "
            f"connections={self.conn_count(device_id)}",
            flush=True,
        )

    def _prune_ledger(self) -> int:
        """清掉「短号还在 device_conns 里、但连接已经没了」的残留登记，返回清掉几条。"""
        live = set(self.conn_ids.values())
        dropped = 0
        for dev, conns in list(self.device_conns.items()):
            stale = conns - live
            if stale:
                conns -= stale
                dropped += len(stale)
                for cid in stale:
                    self.conn_device.pop(cid, None)
            if not conns:
                self.device_conns.pop(dev, None)
        return dropped

    def is_online(self, device_id: str) -> bool:
        return device_id in self.devices

    # ---------------- 实时推送日志 ----------------

    def _msg_log(self, device_id: str, payload: dict, stage: str,
                 ws: Optional[WebSocket] = None, error: str = "") -> None:
        """一条投递一行日志，回答「到底有没有真的写进那条 WebSocket」。

        stage 四态（三种结果 + 一个开始点，严格可分）：
          start   —— 准备写（online/connections 是写之前的快照）
          queued  —— 该设备**没有任何在线连接**，这一帧没有落点（交离线补投）
          success —— send_json 真的返回了（字节已交给事件循环写出去）
          failed  —— 有连接但写失败，error= 里是异常

        conn=   这一帧实际写到（或打算写到）的连接短号
        active= 此刻 hub 里持有的连接短号
        两者不相等 ⇒ 写的时候它已经不是 active 了（对踢现场）。
        另外 type= 标明帧类型：心跳/ack 这类高频帧只在 queued/failed 时留痕，
        不逐条刷屏；带 message_id 的消息帧才逐条留 start/success。
        """
        active_ws = self.devices.get(device_id)
        extra = f" type={payload.get('type')}"
        if payload.get("redelivered"):
            extra += " redelivered=true"
        err = f" error={error}" if error else ""
        print(
            f"[WS][MESSAGE] message_id={payload.get('message_id')} target={device_id} "
            f"online={'true' if active_ws is not None else 'false'} "
            f"connections={self.conn_count(device_id)} "
            f"conn={self.conn_id(ws if ws is not None else active_ws)} "
            f"active={self.conn_id(active_ws)} send={stage}{extra}{err}",
            flush=True,
        )

    async def send_to_device(self, device_id: str, payload: dict) -> bool:
        ws = self.devices.get(device_id)
        # 带 message_id 的是「真的在发消息」，逐条留痕；其余（heartbeat_ack 等）
        # 只在没连接/写失败时留一行，避免刷屏。
        traced = "message_id" in payload
        if traced:
            self._msg_log(device_id, payload, "start")
        if ws is None:
            self._msg_log(device_id, payload, "queued")
            return False
        try:
            await ws.send_json(payload)
            if traced:
                self._msg_log(device_id, payload, "success", ws=ws)
            return True
        except Exception as e:
            self._msg_log(device_id, payload, "failed", ws=ws,
                          error=f"{type(e).__name__}: {e}")
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
