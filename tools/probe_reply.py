"""复现 C# Agent 的 reply 帧，验证服务端收不收、网页列表看不看得到。

C# 端发的是：
    {"type":"reply","sender_name":..,"content":..,"client_id":..}
这里逐字段等价地发一遍，再分别检查 reply_ack / 落库 / /api/messages。
"""
import asyncio
import json
import sys
import urllib.request
from uuid import uuid4

import websockets

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18899"
WS = BASE.replace("http://", "ws://").replace("https://", "wss://")
DEV = "pc_probe"
ENROLL = "family-2026"


def http_get(path: str):
    with urllib.request.urlopen(BASE + path, timeout=10) as r:
        return json.loads(r.read())


async def main() -> int:
    url = (f"{WS}/ws/device/{DEV}?name=%E6%8E%A2%E9%92%88%E7%94%B5%E8%84%91&type=pc"
           f"&platform=windows&agent_version=probe-1.0&enroll_token={ENROLL}")

    async with websockets.connect(url) as ws:
        hello = json.loads(await asyncio.wait_for(ws.recv(), 10))
        print(f"① 握手: type={hello.get('type')} token={'有' if hello.get('token') else '无'}")

        # —— 逐字段复刻 C# 的 reply 帧 ——
        client_id = uuid4().hex[:12]
        frame = {
            "type": "reply",
            "sender_name": "探针的我",
            "content": "收到，马上来",
            "client_id": client_id,
        }
        raw = json.dumps(frame, ensure_ascii=False)
        print(f"② 发送帧: {raw}")
        await ws.send(raw)

        # 等服务端回执
        ack = None
        try:
            while True:
                msg = json.loads(await asyncio.wait_for(ws.recv(), 8))
                if msg.get("type") == "reply_ack":
                    ack = msg
                    break
        except asyncio.TimeoutError:
            pass

        if ack:
            print(f"③ reply_ack: status={ack.get('status')} message_id={ack.get('message_id')} "
                  f"client_id 回显={'一致' if ack.get('client_id') == client_id else '不一致'}")
        else:
            print("③ ❌ 8 秒内没收到 reply_ack")

    # 落库 + 网页列表
    msgs = http_get("/api/messages?limit=10")
    if isinstance(msgs, dict):
        msgs = msgs.get("messages", [])

    replies = [m for m in msgs if m.get("sender_kind") == "device"]
    print(f"④ /api/messages 共 {len(msgs)} 条，其中设备回复 {len(replies)} 条")
    for m in msgs:
        print(f"     id={m.get('id')} kind={m.get('sender_kind')} "
              f"sender={m.get('sender_name')!r} content={str(m.get('content'))[:18]!r}")

    conv = http_get(f"/api/conversations/{DEV}")
    items = conv.get("messages", conv) if isinstance(conv, dict) else conv
    print(f"⑤ /api/conversations/{DEV} 共 {len(items)} 条")
    for m in items:
        print(f"     id={m.get('id')} out={m.get('is_out')} "
              f"sender={m.get('sender_name')!r} content={str(m.get('content'))[:18]!r}")

    ok = bool(ack) and bool(replies) and bool(items)
    print("\n结论:", "✅ 服务端与网页数据源均正常 —— 问题在 C# Agent 侧" if ok
          else "❌ 服务端链路有问题，需查服务端")
    return 0 if ok else 1


sys.exit(asyncio.run(main()))
