"""造一批演示数据，用于 UI 预览截图（开发用）。

连一台虚拟设备，发几条消息、回几条，让网页端和弹窗都有内容可看。

用法：python tools/seed_demo.py [base_url]
"""
from __future__ import annotations

import asyncio
import json
import platform
import sys
import uuid
from urllib.parse import urlencode

import httpx
import websockets

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18899"
WS = BASE.replace("https://", "wss://").replace("http://", "ws://")

DEVICE_ID = "pc_demo"
DEVICE_NAME = "书房电脑"
REPLY_NAME = "书房电脑"

# 每项：(方向, 发送人, 内容)。in = 网页发来的，out = 本机回复的
SCRIPT = [
    ("in", "妈妈", "今晚几点回来"),
    ("out", None, "大概七点"),
    ("in", "妈妈", "路上买袋米"),
    ("out", None, "好"),
    ("in", "爸爸", "把路由器重启一下"),
    ("out", None, "已经重启了"),
    ("in", "妈妈", "晚上想吃什么"),
    ("out", None, "随便，你做什么我吃什么"),
    ("in", "爸爸", "把阳台的衣服收一下"),
    ("out", None, "好，马上"),
    ("in", "妈妈", "快递放门口了"),
    ("in", "妈妈", "下来吃饭了"),
]


async def main() -> None:
    url = f"{WS}/ws/device/{DEVICE_ID}?" + urlencode({
        "token": "", "name": DEVICE_NAME, "type": "pc",
        "platform": platform.platform(), "agent_version": "seed",
        "enroll_token": "family-2026",
    })

    async with websockets.connect(url) as ws:
        await ws.recv()  # hello

        async def recv(timeout: float = 8.0) -> dict:
            raw = await asyncio.wait_for(ws.recv(), timeout=timeout)
            msg = json.loads(raw)
            print(f"    ← 收到 {msg.get('type')}", flush=True)
            return msg

        async with httpx.AsyncClient(base_url=BASE, timeout=20) as http:
            for direction, sender, content in SCRIPT:
                if direction == "in":
                    print(f"  ← 网页发「{content}」", flush=True)
                    await http.post("/api/messages", json={
                        "sender_name": sender, "content": content,
                        "targets": [DEVICE_ID],
                    })
                    await recv()
                else:
                    print(f"  → 设备回「{content}」", flush=True)
                    await ws.send(json.dumps({
                        "type": "reply", "content": content,
                        "client_id": uuid.uuid4().hex[:12],
                        "sender_name": REPLY_NAME,
                    }))
                    await recv()
                await asyncio.sleep(0.05)

        print(f"演示数据已写入：设备 {DEVICE_ID}（{DEVICE_NAME}），共 {len(SCRIPT)} 条往来")


if __name__ == "__main__":
    asyncio.run(main())
