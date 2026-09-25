#!/usr/bin/env python3
"""Fluent 2（Windows 11）改版验证截图。

覆盖：桌面宽屏 Light / Dark、平板 1024 / 768（导航收成图标条）、
手机 390 / 360（顶部标题栏 + 底部导航 + 抽屉）、
以及截图页的 加载中 / 成功 / 失败 三态、Toast、Dialog、空状态。

用法：
    python tools/shot_fluent.py [base_url] [out_dir]
默认 http://127.0.0.1:18899（开发实例）→ docs/fluent-*.png
"""
import sys
from pathlib import Path

from playwright.sync_api import sync_playwright

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18899"
OUT = Path(sys.argv[2] if len(sys.argv) > 2 else "/vol1/1000/workspace/family-message/docs")
OUT.mkdir(parents=True, exist_ok=True)

errors = []
made = []


def shoot(page, name, full=False):
    p = OUT / f"fluent-{name}.png"
    page.screenshot(path=str(p), full_page=full)
    made.append(p)
    print(f"  {p.name}")


def open_page(browser, w=1440, h=940, mode="light", page_id="home", init=None):
    ctx = browser.new_context(viewport={"width": w, "height": h},
                              device_scale_factor=1, locale="zh-CN")
    page = ctx.new_page()
    if init:
        page.add_init_script(init)
    page.on("console", lambda m: errors.append(f"[{m.type}] {m.text[:160]}")
            if m.type == "error" else None)
    page.on("pageerror", lambda e: errors.append(f"[pageerror] {str(e)[:200]}"))
    page.goto(BASE, wait_until="networkidle")
    page.evaluate("m => localStorage.setItem('fm.mode', m)", mode)
    page.evaluate("p => localStorage.setItem('fm.page', p)", page_id)
    page.reload(wait_until="networkidle")
    page.wait_for_timeout(700)
    return ctx, page


def nav(page, pid, mobile=False):
    sel = ".bottom-nav__item" if mobile else ".nav-item"
    page.click(f'{sel}[data-page="{pid}"]')
    page.wait_for_timeout(450)


def main():
    with sync_playwright() as pw:
        browser = pw.chromium.launch()

        # ── 桌面宽屏 1440：Light 各页面 ─────────────────────────
        print("桌面 1440 Light")
        ctx, page = open_page(browser, 1440, 940, "light", "home")
        shoot(page, "1440-home-light")

        nav(page, "messages")
        shoot(page, "1440-messages-light")          # 群聊流：每条一个「已发送」
        nav(page, "devices")
        shoot(page, "1440-devices-light")

        # 关机确认（Fluent ContentDialog）
        page.locator(".dev__acts .btn", has_text="关机").first.click()
        page.wait_for_timeout(500)
        shoot(page, "dialog-confirm-light")
        page.click("#confirm-cancel")
        page.wait_for_timeout(400)

        # Toast
        page.click("#btn-refresh")
        page.wait_for_timeout(500)
        shoot(page, "toast-light")
        page.wait_for_timeout(4200)

        # 截图页：真实链路（假 Agent 回传合成桌面）
        nav(page, "shot")
        page.click("#shot-picks .chip")
        page.wait_for_timeout(1500)
        shoot(page, "1440-shot-light")

        # 截图页：加载中（拦截 fetch 延迟 2.5s）
        page.click("#shot-picks .chip")
        page.wait_for_timeout(120)
        page.evaluate("""() => {
          const of = window.fetch;
          window.fetch = (u, o) => String(u).includes('/screenshot')
            ? new Promise((r) => setTimeout(() => r(of(u, o)), 2500)) : of(u, o);
        }""")
        page.click("#shot-picks .chip")
        page.wait_for_timeout(600)
        shoot(page, "shot-loading-light")
        page.wait_for_timeout(3000)

        # 截图页：失败状态（选一台离线设备）
        chips = page.locator("#shot-picks .chip")
        if chips.count() > 1:
            chips.nth(chips.count() - 1).click()
            page.wait_for_timeout(1800)
            shoot(page, "shot-error-light")

        nav(page, "settings")
        shoot(page, "1440-settings-light")
        ctx.close()

        # ── 桌面宽屏 1440：Dark ─────────────────────────────────
        print("桌面 1440 Dark")
        ctx, page = open_page(browser, 1440, 940, "dark", "home")
        shoot(page, "1440-home-dark")
        nav(page, "devices")
        shoot(page, "1440-devices-dark")
        nav(page, "shot")
        page.click("#shot-picks .chip")
        page.wait_for_timeout(1500)
        shoot(page, "1440-shot-dark")
        nav(page, "settings")
        shoot(page, "1440-settings-dark")
        nav(page, "messages")
        shoot(page, "1440-messages-dark")
        ctx.close()

        # ── 平板 1024 / 768：导航收成图标条 ─────────────────────
        print("平板 1024 / 768")
        for w, h in ((1024, 900), (768, 1024)):
            ctx, page = open_page(browser, w, h, "light", "home")
            shoot(page, f"{w}-home-light")
            ctx.close()

        # ── 手机 390 / 360 ──────────────────────────────────────
        print("手机 390 / 360")
        ctx, page = open_page(browser, 390, 844, "light", "home")
        shoot(page, "390-home-light")
        nav(page, "messages", mobile=True)
        shoot(page, "390-messages-light")
        nav(page, "devices", mobile=True)
        shoot(page, "390-devices-light")
        nav(page, "settings", mobile=True)
        shoot(page, "390-settings-light")
        # 抽屉导航
        page.click("#nav-toggle")
        page.wait_for_timeout(450)
        shoot(page, "390-drawer-light")
        page.click("#nav-scrim")
        page.wait_for_timeout(400)
        ctx.close()

        ctx, page = open_page(browser, 360, 780, "dark", "home")
        shoot(page, "360-home-dark")
        ctx.close()

        # ── 空状态（拦截接口返回空数据）─────────────────────────
        print("空状态")
        ctx = browser.new_context(viewport={"width": 1440, "height": 940}, locale="zh-CN")
        page = ctx.new_page()
        page.route("**/api/devices", lambda r: r.fulfill(
            status=200, content_type="application/json", body="[]"))
        page.route("**/api/messages?*", lambda r: r.fulfill(
            status=200, content_type="application/json", body="[]"))
        page.goto(BASE, wait_until="networkidle")
        page.wait_for_timeout(900)
        shoot(page, "empty-home-light")
        page.click('.nav-item[data-page="devices"]')
        page.wait_for_timeout(400)
        shoot(page, "empty-devices-light")
        ctx.close()

        browser.close()

    print()
    print(f"共 {len(made)} 张 → {OUT}")
    if errors:
        print(f"⚠️  浏览器控制台有 {len(errors)} 条报错：")
        for e in errors[:15]:
            print("   ", e)
    else:
        print("✅ 无控制台报错")


if __name__ == "__main__":
    main()
