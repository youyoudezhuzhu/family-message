"""截图链路压力测试：验证不同体积的截图能否完整往返。

重点验证「真实 Windows 桌面截图」（几百 KB ~ 几 MB）会不会在某一环被截断或超时。

用法（服务端需先跑在 FM_TEST_BASE，默认 127.0.0.1:18899）：
    python tools/test_screenshot.py
"""
from __future__ import annotations

import asyncio
import base64
import io
import json
import os
import platform
import random
import sys
import time
import uuid
from urllib.parse import urlencode

import httpx
import websockets

BASE = os.environ.get("FM_TEST_BASE", "http://127.0.0.1:18899")
WS = BASE.replace("https://", "wss://").replace("http://", "ws://")
DEVICE_ID = f"pc_shot_{uuid.uuid4().hex[:6]}"
ENROLL = os.environ.get("FM_TEST_ENROLL", "family-2026")

PASS, FAIL = [], []


def check(name: str, ok: bool, detail: str = "") -> None:
    (PASS if ok else FAIL).append(name)
    print(f"  {'✅' if ok else '❌'} {name}{('  — ' + detail) if detail else ''}")


def make_jpeg(width: int, height: int, noise: bool) -> tuple[str, int]:
    """生成一张 JPEG 并返回 (base64, 原始字节数)。noise=True 模拟真实桌面（体积大得多）。"""
    from PIL import Image

    if noise:
        rnd = random.Random(1234)
        raw = bytes(rnd.getrandbits(8) for _ in range(width * height * 3))
        img = Image.frombytes("RGB", (width, height), raw)
    else:
        img = Image.new("RGB", (width, height), (24, 26, 33))

    buf = io.BytesIO()
    img.save(buf, format="JPEG", quality=75)
    data = buf.getvalue()
    return base64.b64encode(data).decode(), len(data)


class Next:
    """下一次收到截图请求时要回传的内容。"""
    b64 = ""
    nbytes = 0


async def main() -> int:
    url = f"{WS}/ws/device/{DEVICE_ID}?" + urlencode({
        "token": "", "name": "截图压测机", "type": "pc",
        "platform": platform.platform(), "agent_version": "test",
        "enroll_token": ENROLL,
    })

    async with websockets.connect(url, max_size=None) as ws:
        await ws.recv()  # hello
        print(f"  设备已接入: {DEVICE_ID}\n")

        async def responder():
            async for raw in ws:
                m = json.loads(raw)
                if m.get("type") == "screenshot_request":
                    await ws.send(json.dumps({
                        "type": "screenshot_response",
                        "request_id": m["request_id"],
                        "format": "jpeg",
                        "data_base64": Next.b64,
                        "width": 0, "height": 0,
                        "screen_locked": False,
                    }))

        task = asyncio.create_task(responder())

        try:
            async with httpx.AsyncClient(base_url=BASE, timeout=180) as http:
                for label, (w, h, noise) in [
                    ("小图 800x600", (800, 600, False)),
                    ("标清 1920x1080", (1920, 1080, False)),
                    ("真实感 1920x1080", (1920, 1080, True)),
                    ("4K 3840x2160 噪点", (3840, 2160, True)),
                ]:
                    Next.b64, Next.nbytes = make_jpeg(w, h, noise)
                    print(f"  ── {label}：JPEG {Next.nbytes/1024:.0f} KB"
                          f" → base64 {len(Next.b64)/1024:.0f} KB")

                    t0 = time.time()
                    try:
                        resp = await http.post(f"/api/devices/{DEVICE_ID}/screenshot")
                    except Exception as e:
                        check(f"{label} 往返", False, f"请求异常 {type(e).__name__}: {e}")
                        continue
                    elapsed = time.time() - t0

                    if resp.status_code != 200:
                        check(f"{label} 往返", False,
                              f"HTTP {resp.status_code} {resp.text[:200]}")
                        continue

                    body = resp.json()
                    got = len(base64.b64decode(body["data_url"].split(",", 1)[1]))
                    check(f"{label} 往返完整",
                          body.get("ok") is True and got == Next.nbytes,
                          f"回传 {got/1024:.0f} KB，耗时 {elapsed:.2f}s")
        finally:
            task.cancel()

    async with httpx.AsyncClient(base_url=BASE, timeout=20) as http:
        await http.delete(f"/api/devices/{DEVICE_ID}")

    print()
    print(f"通过 {len(PASS)} / 失败 {len(FAIL)}")
    for f in FAIL:
        print("  失败:", f)
    return 1 if FAIL else 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
