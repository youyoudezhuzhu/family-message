#!/usr/bin/env python3
"""PC 端「消息不实时刷新」的端到端排查。

模拟一个 PC 设备（WebSocket 假 Agent），验证三件事：

  ① 网页端发消息 → 设备是否收到 message 帧（不收到 = 服务端广播的问题）
  ② 设备自己回复   → 设备是否收到自己的回显（现状**故意排除**，但群聊里
                     自己的消息应该出现在自己的界面上 —— 这是产品判断）
  ③ 设备在线时消息的到达延迟（确认是不是「只在重开界面时才拉到」）

用法：python tools/diag_realtime.py [base_url] [enroll_token]
"""
from __future__ import annotations

import asyncio
import json
import sys
import urllib.request
from pathlib import Path

import websockets

BASE = (sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18899").rstrip("/")
ENROLL = sys.argv[2] if len(sys.argv) > 2 else "FAMILY-TEST-TOKEN"
WS = BASE.replace("https://", "wss://").replace("http://", "ws://")
DEVICE_ID = "pc_diag"


def api(path, data=None):
    body = json.dumps(data).encode() if data is not None else None
    req = urllib.request.Request(BASE + path, data=body,
                                headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=8) as r:
        return json.loads(r.read().decode())


async def main() -> int:
    print(f"服务端 {BASE}｜设备 {DEVICE_ID}\n")
    got: list[dict] = []

    url = f"{WS}/ws/device/{DEVICE_ID}?" + "&".join([
        "token=", "device_name=diag", "platform=diag",
        f"enroll_token={ENROLL}", "agent=cs-0.13.2", "session_state=unlocked",
        "can_screenshot=true", "can_shutdown=true", "can_unlock=false",
    ])

    async with websockets.connect(url, open_timeout=8) as ws:
        hello = json.loads(await asyncio.wait_for(ws.recv(), 8))
        print(f"① 握手：{hello.get('type')}  device_id={hello.get('device_id')}")
        assert hello.get("type") == "hello", hello

        async def pump():
            try:
                async for raw in ws:
                    d = json.loads(raw)
                    got.append(d)
                    t = d.get("type")
                    extra = ""
                    if t == "message":
                        m = d.get("message") or {}
                        extra = f" sender={m.get('sender_name')!r} content={m.get('content')!r}"
                    print(f"   ← 收到 {t}{extra}")
            except Exception as e:
                print("   （连接结束：", e, "）")

        task = asyncio.create_task(pump())
        await asyncio.sleep(0.5)

        # ── ① 网页端发消息 ──
        print("\n① 从网页端（HTTP API）发一条消息…")
        r = api("/api/messages", {"sender_name": "妈妈", "content": "实时性测试-A"})
        mid_a = (r.get("message") or {}).get("id")
        print(f"   已发送 id={mid_a}")
        await asyncio.sleep(2.0)
        got_a = [g for g in got if g.get("type") == "message"
                 and ((g.get("message") or {}).get("content") == "实时性测试-A")]
        print(f"   {'✅' if got_a else '❌'} 设备收到网页端发的消息：{len(got_a)} 条"
              f"  {'（不收到 = 服务端广播有问题）' if not got_a else ''}")

        # ── ② 设备自己回复 ──
        print("\n② 设备自己回一条（带 sender_device_id）…")
        await ws.send(json.dumps({
            "type": "reply", "client_id": "diag-1",
            "sender_name": "爸爸", "content": "实时性测试-B",
            "sender_device_id": DEVICE_ID,
        }))
        await asyncio.sleep(2.0)
        got_b = [g for g in got if g.get("type") == "message"
                 and ((g.get("message") or {}).get("content") == "实时性测试-B")]
        print(f"   设备收到**自己**发的消息：{len(got_b)} 条")
        print(f"   → 现状：{'✅ 有回显' if got_b else '❌ 没有回显（被排除）'}")
        print("      群聊里自己的消息应该出现在自己的界面上 ——"
              " 若这里是「没有回显」，PC 端就永远看不到自己发的话。")

        # ── ③ 历史里能不能查到（解释「重开界面才有」）──
        print("\n③ 历史接口里有没有这两条（重开界面走的是这条路）…")
        hist = api("/api/messages?limit=20")
        ids = [m.get("id") for m in hist]
        print(f"   历史共 {len(hist)} 条，含 A：{mid_a in ids}")
        print(f"   → 重开界面能拉到 ⇒ 消息进了库，只是**没被实时推出来**")

        task.cancel()
    return 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
