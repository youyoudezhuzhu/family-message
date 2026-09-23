"""验证服务端对坏帧的容错：处理某一帧出错时，连接不能断。

背景：设备侧报告「能收不能发，而且很随机」。如果服务端处理某帧时抛异常且不捕获，
整条 ws 会被关闭 —— 每发一次就把自己搞掉线，症状和连接故障一模一样。
"""
import asyncio
import json
import sys
from uuid import uuid4

import websockets

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18899"
WS = BASE.replace("http://", "ws://").replace("https://", "wss://")
DEV = "pc_robust"
PASS = 0
FAIL = 0


def ok(m):
    global PASS
    PASS += 1
    print(f"  ✅ {m}")


def bad(m):
    global FAIL
    FAIL += 1
    print(f"  ❌ {m}")


async def expect_reply_ack(ws, tag):
    """发一条合法 reply，看能不能拿到回执（能拿到 = 连接还活着）。"""
    cid = uuid4().hex[:12]
    await ws.send(json.dumps({
        "type": "reply", "sender_name": "容错探针",
        "content": f"存活检查-{tag}", "client_id": cid,
    }, ensure_ascii=False))
    try:
        while True:
            m = json.loads(await asyncio.wait_for(ws.recv(), 6))
            if m.get("type") == "reply_ack" and m.get("client_id") == cid:
                return m.get("status")
    except asyncio.TimeoutError:
        return None


async def main():
    url = (f"{WS}/ws/device/{DEV}?name=%E5%AE%B9%E9%94%99%E6%8E%A2%E9%92%88&type=pc"
           f"&enroll_token=family-2026")
    async with websockets.connect(url) as ws:
        hello = json.loads(await asyncio.wait_for(ws.recv(), 10))
        print(f"① 握手: {hello.get('type')}")
        if hello.get("type") == "hello":
            ok("设备接入成功")
        else:
            bad("握手失败")
            return

        st = await expect_reply_ack(ws, "基线")
        ok(f"基线回复可用（status={st}）") if st == "ok" else bad(f"基线就不可用：{st}")

        print("② 发送坏帧：history_request limit='abc'（会让 int() 抛异常）")
        await ws.send(json.dumps({"type": "history_request", "request_id": "x",
                                  "limit": "abc"}))
        await asyncio.sleep(1.5)
        st = await expect_reply_ack(ws, "坏帧后")
        ok(f"坏帧之后连接仍可用（status={st}）") if st == "ok" \
            else bad(f"坏帧把连接搞死了：{st}")

        print("③ 发送坏帧：ack message_id='xx'（会让 int() 抛异常）")
        await ws.send(json.dumps({"type": "ack", "message_id": "xx", "status": "read"}))
        await asyncio.sleep(1.5)
        st = await expect_reply_ack(ws, "第二个坏帧后")
        ok(f"第二个坏帧之后仍可用（status={st}）") if st == "ok" \
            else bad(f"连接又断了：{st}")

        print("④ 发送无法解析的内容（非 JSON）")
        await ws.send("这不是 JSON {{{")
        await asyncio.sleep(1.5)
        st = await expect_reply_ack(ws, "非JSON后")
        ok(f"非 JSON 帧之后仍可用（status={st}）") if st == "ok" \
            else bad(f"非 JSON 帧把连接搞死了：{st}")

        print("⑤ 未知类型的帧")
        await ws.send(json.dumps({"type": "完全不认识的类型"}))
        await asyncio.sleep(1.0)
        st = await expect_reply_ack(ws, "未知类型后")
        ok(f"未知类型之后仍可用（status={st}）") if st == "ok" \
            else bad(f"未知类型把连接搞死了：{st}")

    print(f"\n结果：{PASS} 项通过，{FAIL} 项失败")
    return 0 if FAIL == 0 else 1


sys.exit(asyncio.run(main()))
