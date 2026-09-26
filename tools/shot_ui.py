"""抓 MD 改版后的界面预览图，给用户确认用。

用 Playwright 自带的 Chromium（headless），不依赖本机浏览器。
"""
import asyncio
import sys
from pathlib import Path

from playwright.async_api import async_playwright

OUT = Path("/vol1/1000/workspace/family-message/docs")
WEB = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18899/"


async def shoot_mockups(page) -> None:
    await page.set_viewport_size({"width": 1920, "height": 1080})
    for src, dst in [
        ("mockup-agent-popup.html", "ui-agent-popup.png"),
        ("mockup-agent-settings.html", "ui-agent-settings.png"),
    ]:
        await page.goto(f"file://{OUT / src}", timeout=30000)
        await page.wait_for_timeout(700)
        await page.screenshot(path=str(OUT / dst))
        print(f"  saved {dst}")


async def shoot_web(page) -> None:
    await page.set_viewport_size({"width": 1440, "height": 1000})
    await page.goto(WEB, timeout=30000)
    await page.wait_for_timeout(2500)

    # 页面主体（含消息记录）
    await page.screenshot(path=str(OUT / "ui-console.png"))
    print("  saved ui-console.png")

    # 设置对话框（配色 + 昵称）
    try:
        await page.click("#btn-settings", timeout=5000)
        await page.wait_for_timeout(900)
        await page.screenshot(path=str(OUT / "ui-settings.png"))
        print("  saved ui-settings.png")
        await page.keyboard.press("Escape")
        await page.click("#settings-close", timeout=3000)
        await page.wait_for_timeout(400)
    except Exception as exc:
        print("  设置对话框截图失败:", exc)

    # 对话视图
    try:
        btn = page.locator(".dev-acts button", has_text="对话").first
        if await btn.count() > 0:
            await btn.click()
            await page.wait_for_timeout(1500)
            await page.screenshot(path=str(OUT / "ui-conversation.png"))
            print("  saved ui-conversation.png")
    except Exception as exc:
        print("  对话视图截图失败:", exc)


async def main() -> None:
    async with async_playwright() as p:
        browser = await p.chromium.launch(headless=True)
        page = await browser.new_page()
        print("PC 端预览（mockup）:")
        await shoot_mockups(page)
        page2 = await browser.new_page()
        print("网页端:")
        try:
            await shoot_web(page2)
        except Exception as exc:
            print("  网页端截图失败:", exc)
        await browser.close()


asyncio.run(main())
