"""决定性测试：网页端【实时】收到设备回复时会不会渲染出来。

流程：开浏览器 → 打开控制台 → 用设备 WS 发一条 reply → 看 DOM 里有没有出现。
"""
import asyncio
import json
import sys
from uuid import uuid4

import websockets
from playwright.async_api import async_playwright

CDP = "http://127.0.0.1:16002"
WEB = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18899/"
WS = WEB.replace("http://", "ws://").replace("https://", "wss://")
DEV = "pc_probe"
REPLY_TEXT = "实时渲染测试-收到马上来"


async def send_device_reply() -> None:
    url = (f"{WS}/ws/device/{DEV}?name=%E6%8E%A2%E9%92%88%E7%94%B5%E8%84%91&type=pc"
           f"&enroll_token=family-2026")
    async with websockets.connect(url) as ws:
        await asyncio.wait_for(ws.recv(), 10)          # hello
        frame = {
            "type": "reply",
            "sender_name": "探针的我",
            "content": REPLY_TEXT,
            "client_id": uuid4().hex[:12],
        }
        await ws.send(json.dumps(frame, ensure_ascii=False))
        try:
            while True:
                m = json.loads(await asyncio.wait_for(ws.recv(), 8))
                if m.get("type") == "reply_ack":
                    print(f"  设备侧 reply_ack: {m.get('status')}")
                    break
        except asyncio.TimeoutError:
            print("  设备侧 ❌ 无 reply_ack")
        await asyncio.sleep(1)


async def main() -> None:
    async with async_playwright() as p:
        browser = await p.chromium.connect_over_cdp(CDP)
        ctx = browser.contexts[0] if browser.contexts else await browser.new_context()
        page = await ctx.new_page()
        page.on("console", lambda m: print(f"  [浏览器 {m.type}] {m.text[:160]}")
                if m.type in ("error", "warning") else None)
        page.on("pageerror", lambda e: print(f"  [页面异常] {str(e)[:200]}"))

        await page.goto(WEB, timeout=30000)
        await page.wait_for_timeout(2500)

        ws_dot = await page.evaluate(
            "document.getElementById('ws-dot')?.classList.contains('on')")
        print(f"① 网页 WS 已连接: {ws_dot}")

        before = await page.evaluate("document.querySelectorAll('.msg').length")
        print(f"② 发送前页面消息条数: {before}")

        print("③ 设备端发送回复…")
        await send_device_reply()

        await page.wait_for_timeout(2500)
        after = await page.evaluate("document.querySelectorAll('.msg').length")
        print(f"④ 发送后页面消息条数: {after}")

        found = await page.evaluate(
            "document.body.innerText.includes(%s)" % json.dumps(REPLY_TEXT))
        print(f"⑤ 页面上出现回复文本: {found}")

        html = await page.evaluate(
            "Array.from(document.querySelectorAll('.msg')).map(e=>e.innerText.replace(/\\n/g,' | ')).join('\\n')")
        print("⑥ 页面上实际渲染的消息：")
        for line in (html or "").splitlines():
            print("   ", line[:150])

        await page.screenshot(path="/vol1/1000/workspace/family-message/docs/ui-live-reply.png")
        await page.close()
        await browser.close()

        print("\n结论:", "✅ 网页实时渲染正常 → 问题在 C# Agent 发送侧"
              if found else "❌ 网页没有实时渲染设备回复 —— 找到 bug 了（前端）")


asyncio.run(main())
