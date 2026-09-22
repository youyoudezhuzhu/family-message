"""用飞牛 Chrome 的 CDP 端口给 Web 控制台截图。"""
import asyncio
import sys
from pathlib import Path

from playwright.async_api import async_playwright

URL = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18801/"
OUT = sys.argv[2] if len(sys.argv) > 2 else "/tmp/fm_ui.png"
CDP = sys.argv[3] if len(sys.argv) > 3 else "http://127.0.0.1:16002"


async def main() -> None:
    async with async_playwright() as p:
        browser = await p.chromium.connect_over_cdp(CDP)
        ctx = browser.contexts[0] if browser.contexts else await browser.new_context()
        page = await ctx.new_page()
        await page.set_viewport_size({"width": 1080, "height": 1500})
        await page.goto(URL, timeout=30000)
        await page.wait_for_timeout(3000)
        Path(OUT).parent.mkdir(parents=True, exist_ok=True)
        await page.screenshot(path=OUT, full_page=True)
        print("TITLE:", await page.title())
        print("--- 页面文本 ---")
        print((await page.inner_text("body"))[:1200])
        await page.close()
        await browser.close()  # 断开 CDP，不关闭用户浏览器


asyncio.run(main())
print("SAVED:", OUT)
