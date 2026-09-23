"""给辉哥确认 UI 用：抓网页控制台 + Agent 弹窗布局预览。"""
import asyncio
import sys
from pathlib import Path

from playwright.async_api import async_playwright

CDP = "http://127.0.0.1:16002"
WEB = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18899/"
OUT = Path("/vol1/1000/workspace/family-message/docs")


async def main() -> None:
    async with async_playwright() as p:
        browser = await p.chromium.connect_over_cdp(CDP)
        ctx = browser.contexts[0] if browser.contexts else await browser.new_context()

        # ---- 1. Agent 弹窗布局预览（1920x1080 全屏比例）----
        page = await ctx.new_page()
        await page.set_viewport_size({"width": 1920, "height": 1080})
        await page.goto(f"file://{OUT}/mockup-agent-popup.html", timeout=30000)
        await page.wait_for_timeout(900)
        await page.screenshot(path=str(OUT / "ui-agent-popup.png"))
        print("saved ui-agent-popup.png")

        # ---- 2. 网页控制台（含设备回复）----
        await page.goto(WEB, timeout=30000)
        await page.wait_for_timeout(2500)
        await page.screenshot(path=str(OUT / "ui-console.png"), full_page=True)
        print("saved ui-console.png（含设备回复的消息流）")

        # ---- 3. 网页对话视图 ----
        btn = page.locator(".dev-acts button", has_text="对话").first
        if await btn.count() > 0:
            await btn.click()
            await page.wait_for_timeout(1400)
            await page.screenshot(path=str(OUT / "ui-conversation.png"))
            print("saved ui-conversation.png")
        else:
            print("没找到「对话」按钮")

        # ---- 4. 网页昵称管理 ----
        await page.keyboard.press("Escape")
        await page.evaluate("document.getElementById('conv').classList.remove('show')")
        try:
            await page.click("#btn-names", timeout=4000)
            await page.wait_for_timeout(900)
            await page.screenshot(path=str(OUT / "ui-names.png"))
            print("saved ui-names.png")
        except Exception as exc:
            print("昵称管理弹窗截图失败:", exc)

        await page.close()
        await browser.close()  # 断开 CDP，不关用户浏览器


asyncio.run(main())
