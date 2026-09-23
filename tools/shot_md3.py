#!/usr/bin/env python3
"""多断点 × 明暗双主题截图（规范 §18 要求验证 360/390/768/1024/1280/1440+）。

用法：python3 tools/shot_md3.py [base_url] [out_dir]
"""
import sys
from pathlib import Path

from playwright.sync_api import sync_playwright

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18899"
OUT = Path(sys.argv[2] if len(sys.argv) > 2 else "/vol1/1000/workspace/family-message/docs")
OUT.mkdir(parents=True, exist_ok=True)

VIEWPORTS = [
    ("360", 360, 780),
    ("390", 390, 844),
    ("768", 768, 1024),
    ("1024", 1024, 900),
    ("1440", 1440, 960),
]

errors = []


def shoot(page, name, full=True):
    p = OUT / f"md3-{name}.png"
    page.screenshot(path=str(p), full_page=full)
    print(f"  {p.name}")


def main():
    with sync_playwright() as pw:
        browser = pw.chromium.launch()

        for mode in ("dark", "light"):
            for label, w, h in VIEWPORTS:
                ctx = browser.new_context(
                    viewport={"width": w, "height": h},
                    device_scale_factor=1,
                    locale="zh-CN",
                )
                page = ctx.new_page()
                page.on("console", lambda m: errors.append(f"[{m.type}] {m.text}")
                        if m.type in ("error", "warning") else None)
                page.on("pageerror", lambda e: errors.append(f"[pageerror] {e}"))

                page.goto(BASE, wait_until="networkidle")
                page.evaluate(
                    "m => { localStorage.setItem('fm.mode', m); }", mode)
                page.reload(wait_until="networkidle")
                page.wait_for_timeout(700)

                # 桌面/平板只截两档配色，手机截全部断点
                if label in ("360", "1024", "1440") or mode == "light":
                    shoot(page, f"{label}-{mode}")

                # 设置面板（只在一个断点截，避免图太多）
                if label == "1440":
                    page.click("#btn-settings")
                    page.wait_for_timeout(500)
                    shoot(page, f"settings-{mode}", full=False)
                    page.keyboard.press("Escape")
                    page.wait_for_timeout(400)

                # 关机确认对话框（展示 MD3 alert dialog）
                if label == "1440" and mode == "dark":
                    page.click('.dev__acts .md-btn--danger-text')
                    page.wait_for_timeout(500)
                    shoot(page, "confirm-shutdown", full=False)
                    page.click("#confirm-cancel")
                    page.wait_for_timeout(300)

                # Snackbar
                if label == "1440" and mode == "light":
                    page.evaluate(
                        "window.__snack && window.__snack()")
                    page.wait_for_timeout(300)

                ctx.close()

        # 移动端设置面板单独来一张（手机上是全屏 sheet 感觉）
        ctx = browser.new_context(viewport={"width": 390, "height": 844}, locale="zh-CN")
        page = ctx.new_page()
        page.goto(BASE, wait_until="networkidle")
        page.wait_for_timeout(600)
        page.click("#btn-settings")
        page.wait_for_timeout(600)
        shoot(page, "390-settings", full=False)
        ctx.close()

        browser.close()

    print()
    if errors:
        print(f"⚠️  浏览器控制台有 {len(errors)} 条消息：")
        for e in errors[:15]:
            print("   ", e[:150])
    else:
        print("✅ 无控制台报错")


if __name__ == "__main__":
    main()
