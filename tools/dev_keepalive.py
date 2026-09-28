#!/usr/bin/env python3
"""开发实例保活：占着一条设备长连接，让「设备」页的关机/解锁按钮可用。

shot_fluent.py 需要至少一台**在线**设备（否则 .dev__acts 的关机按钮是 disabled）
+ 至少一台离线设备（截图页的失败态用它）。用法：
    python3 tools/dev_keepalive.py [base_url]
"""
from __future__ import annotations

import asyncio
import json
import sys
from urllib.parse import urlencode

import httpx
import websockets

BASE = (sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18899").rstrip("/")
WS = BASE.replace("https://", "wss://").replace("http://", "ws://")

ONLINE = [("dev_online_a", "书房电脑"), ("dev_online_b", "客厅电脑")]
OFFLINE = [("dev_offline", "卧室笔记本")]


async def hold(dev: str, name: str) -> None:
    url = f"{WS}/ws/device/{dev}?" + urlencode({
        "token": "", "name": name, "type": "pc",
        "platform": "seed", "agent_version": "0.13.3", "enroll_token": "FAMILY-TEST-TOKEN"})
    async with websockets.connect(url) as ws:
        print(f"  ✓ {name} 已上线", flush=True)
        while True:
            try:
                await asyncio.wait_for(ws.recv(), timeout=1.0)
            except asyncio.TimeoutError:
                await ws.send(json.dumps({"type": "ping"}))


async def register_offline() -> None:
    for dev, name in OFFLINE:
        url = f"{WS}/ws/device/{dev}?" + urlencode({
            "token": "", "name": name, "type": "pc",
            "platform": "seed", "agent_version": "0.13.3", "enroll_token": "FAMILY-TEST-TOKEN"})
        async with websockets.connect(url) as ws:
            await asyncio.wait_for(ws.recv(), timeout=5)
        print(f"  · {name} 注册后断开（离线态，供截图失败用例）", flush=True)


async def main() -> None:
    async with httpx.AsyncClient(base_url=BASE, timeout=15) as http:
        await http.get("/api/devices")
    await register_offline()
    await asyncio.gather(*(hold(d, n) for d, n in ONLINE))


if __name__ == "__main__":
    asyncio.run(main())
