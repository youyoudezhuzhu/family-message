"""双向对话链路自动化测试。

验证：Web → Device 投递（含历史） → Device 回复 → 服务器落库 → 网页可见 → 下一条消息带上回复。

用法（服务端需先跑在 127.0.0.1:18801）：
    python tools/test_bidirectional.py
"""
from __future__ import annotations

import asyncio
import json
import os
import platform
import sys
import uuid
from urllib.parse import urlencode

import httpx
import websockets

BASE = os.environ.get("FM_TEST_BASE", "http://127.0.0.1:18801")
WS = BASE.replace("https://", "wss://").replace("http://", "ws://")
# 每次跑用独立设备，保证可重复执行且不污染既有数据
RUN = uuid.uuid4().hex[:6]
DEVICE_ID = os.environ.get("FM_TEST_DEVICE", f"pc_test_{RUN}")
DEVICE_NAME = "双向测试机"
ENROLL = os.environ.get("FM_TEST_ENROLL", "family-2026")

PASS, FAIL = [], []


def check(name: str, ok: bool, detail: str = "") -> None:
    (PASS if ok else FAIL).append(name)
    print(f"  {'✅' if ok else '❌'} {name}{('  — ' + detail) if detail else ''}")


class Device:
    def __init__(self, ws):
        self.ws = ws
        self.inbox: asyncio.Queue = asyncio.Queue()

    async def pump(self):
        async for raw in self.ws:
            self.inbox.put_nowait(json.loads(raw))

    async def wait_for(self, mtype: str, timeout: float = 8.0) -> dict:
        deadline = asyncio.get_event_loop().time() + timeout
        while True:
            left = deadline - asyncio.get_event_loop().time()
            if left <= 0:
                raise TimeoutError(f"等待 {mtype} 超时")
            msg = await asyncio.wait_for(self.inbox.get(), timeout=left)
            if msg.get("type") == mtype:
                return msg


async def main() -> int:
    url = f"{WS}/ws/device/{DEVICE_ID}?" + urlencode({
        "token": "",
        "name": DEVICE_NAME,
        "type": "pc",
        "platform": platform.platform(),
        "agent_version": "test",
        "enroll_token": ENROLL,
    })

    async with websockets.connect(url) as ws:
        dev = Device(ws)
        pump = asyncio.create_task(dev.pump())
        try:
            hello = await dev.wait_for("hello")
            check("设备注册并收到 hello", bool(hello.get("device_id")), hello.get("device_id"))

            async with httpx.AsyncClient(base_url=BASE, timeout=10) as http:
                # ---- 1. Web → Device，首条消息应带上 history ----
                r = await http.post("/api/messages", json={
                    "sender_name": "妈妈", "content": "下来吃饭了", "targets": [DEVICE_ID],
                })
                check("网页发送消息成功", r.status_code == 200, str(r.status_code))

                m1 = await dev.wait_for("message")
                check("设备收到消息", m1.get("content") == "下来吃饭了", m1.get("content", ""))
                h1 = m1.get("history") or []
                check("消息体带 history 字段", isinstance(h1, list) and len(h1) >= 1,
                      f"{len(h1)} 条")
                check("history 视角正确（本条对设备是 in）",
                      h1 and h1[-1]["direction"] == "in",
                      h1[-1]["direction"] if h1 else "-")

                # ---- 2. Device → Web：回复 ----
                cid = uuid.uuid4().hex[:12]
                await ws.send(json.dumps({
                    "type": "reply", "content": "好，马上下来", "client_id": cid,
                }))
                ack = await dev.wait_for("reply_ack")
                check("服务器确认回复已接收",
                      ack.get("status") == "ok" and ack.get("client_id") == cid,
                      f"message_id={ack.get('message_id')}")
                check("回复落库并返回 message_id", isinstance(ack.get("message_id"), int))

                # ---- 3. 对话串应包含双向两条 ----
                r = await http.get(f"/api/conversations/{DEVICE_ID}")
                conv = r.json()
                kinds = [(m["sender_kind"], m["content"]) for m in conv]
                check("会话接口返回 2 条", len(conv) == 2, json.dumps(kinds, ensure_ascii=False))
                check("包含 web→device 的一条",
                      any(k == "web" and c == "下来吃饭了" for k, c in kinds))
                check("包含 device→web 的一条",
                      any(k == "device" and c == "好，马上下来" for k, c in kinds))
                check("发送人名字取自设备名",
                      any(m["sender_name"] == "双向测试机" for m in conv
                          if m["sender_kind"] == "device"))

                # ---- 4. 下一条消息的历史里应能看到刚才的回复 ----
                r = await http.post("/api/messages", json={
                    "sender_name": "爸爸", "content": "顺便把垃圾带下去",
                    "targets": [DEVICE_ID],
                })
                m2 = await dev.wait_for("message")
                h2 = m2.get("history") or []
                check("第二条消息的历史变长", len(h2) == 3, f"{len(h2)} 条")
                check("历史里含设备发出的回复",
                      any(h["direction"] == "out" and h["content"] == "好，马上下来"
                          for h in h2))
                check("历史按时间正序",
                      [h["content"] for h in h2] ==
                      ["下来吃饭了", "好，马上下来", "顺便把垃圾带下去"],
                      json.dumps([h["content"] for h in h2], ensure_ascii=False))

                # ---- 5. 网页消息流里也能看到设备回复 ----
                r = await http.get("/api/messages?limit=10")
                all_msgs = r.json()
                check("全局消息流包含设备回复",
                      any(m["sender_kind"] == "device" and m["content"] == "好，马上下来"
                          for m in all_msgs))

                # ---- 6. 空回复应被忽略 ----
                await ws.send(json.dumps({"type": "reply", "content": "   ", "client_id": "x"}))
                await asyncio.sleep(0.6)
                r = await http.get(f"/api/conversations/{DEVICE_ID}")
                check("空白回复被拒绝", len(r.json()) == 3, f"{len(r.json())} 条")

                # ---- 7. 连发多条：必须是逐条实时推送，不能合并 ----
                burst = ["第一条", "第二条", "第三条"]
                for text in burst:
                    await http.post("/api/messages", json={
                        "sender_name": "妈妈", "content": text, "targets": [DEVICE_ID],
                    })
                got = []
                ids = []
                for _ in burst:
                    m = await dev.wait_for("message", timeout=5)
                    got.append(m["content"])
                    ids.append(m["message_id"])
                check("连发 3 条 → 收到 3 个独立帧", got == burst,
                      json.dumps(got, ensure_ascii=False))
                check("三帧的 message_id 互不相同且递增",
                      len(set(ids)) == 3 and ids == sorted(ids), str(ids))
                check("最后一条的 history 含全部 6 条往来",
                      len(m.get("history") or []) == 6,
                      f"{len(m.get('history') or [])} 条")

                # ---- 8. 清理测试设备 ----
                d = await http.delete(f"/api/devices/{DEVICE_ID}")
                check("测试设备已清理", d.status_code == 200, str(d.status_code))

        finally:
            pump.cancel()

    print()
    print(f"通过 {len(PASS)} / 失败 {len(FAIL)}")
    for f in FAIL:
        print("  失败:", f)
    return 1 if FAIL else 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
