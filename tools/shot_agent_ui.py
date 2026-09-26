"""抓 PC 端两个界面（消息界面 + 设置页）的预览图，给用户确认用。

复用飞牛自带的 Chrome（CDP 端口 16002），不启动新的浏览器实例。
"""
import asyncio
from pathlib import Path

from playwright.async_api import async_playwright

CDP = "http://127.0.0.1:16002"
OUT = Path("/vol1/1000/workspace/family-message/docs")

SHOTS = [
    ("mockup-agent-popup.html", "ui-agent-popup.png"),
    ("mockup-agent-settings.html", "ui-agent-settings.png"),
]


async def main() -> None:
    async with async_playwright() as p:
        browser = await p.chromium.connect_over_cdp(CDP)
        ctx = browser.contexts[0] if browser.contexts else await browser.new_context()
        page = await ctx.new_page()
        await page.set_viewport_size({"width": 1920, "height": 1080})

        for src, dst in SHOTS:
            await page.goto(f"file://{OUT / src}", timeout=30000)
            await page.wait_for_timeout(900)
            await page.screenshot(path=str(OUT / dst))
            print(f"saved {dst}")

        await page.close()
        await browser.close()   # 只断开 CDP，不关用户浏览器


asyncio.run(main())
