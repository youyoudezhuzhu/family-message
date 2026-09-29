#!/usr/bin/env python3
"""消息页布局验证：**消息记录在前、发送框在后**（像聊天应用，输入框在底部）。

判据不是「看着像」，是 DOM 与几何两个可证的事实：
  · document 顺序：`#home-recent` 所在的 section 排在 `#t-compose` 所在的 card 之前
  · 屏幕几何：`#home-recent` 的 y 坐标 < 发送卡片 y 坐标
并顺带确认**发送区内部一点没动**（字段/按钮/文本域都还在原位）。

用法：python3 tools/shot_home_layout.py [base_url] [out_dir]
默认 dev 实例 http://127.0.0.1:18899 → docs/ui-home-*.png
"""
from __future__ import annotations

import asyncio
import json
import sys
import threading
from pathlib import Path
from urllib.parse import urlencode

import httpx
import websockets
from playwright.sync_api import sync_playwright

BASE = (sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18899").rstrip("/")
OUT = Path(sys.argv[2] if len(sys.argv) > 2 else "/vol1/<uid>/workspace/family-message/docs")
OUT.mkdir(parents=True, exist_ok=True)
WS = BASE.replace("https://", "wss://").replace("http://", "ws://")

FAILS: list[str] = []
CHECKS = 0
MADE: list[Path] = []
errors: list[str] = []


def check(ok, label, extra=""):
    global CHECKS
    CHECKS += 1
    print(f"  {'✓' if ok else '✗'} {label}" + (f"   ← {extra}" if extra and not ok else ""))
    if not ok:
        FAILS.append(label)
    return ok


# ── 演示数据：一台常驻在线设备 + 几条群聊消息 ──────────────────────
SEED = [("妈妈", "今晚几点回来？我把菜先洗上了"),
        ("爸爸", "七点半左右，路上有点堵"),
        ("妈妈", "路过超市带袋米，小袋的就行"),
        ("朵朵", "爸爸我今天数学考了 96 分！"),
        ("妈妈", "家里酱油也没了，一起买")]


async def _keep_device_online(stop: threading.Event) -> None:
    """占着一条设备长连接（v0.19.2 起首页不再有「在线设备」区，这里只为让设备页有数据）。"""
    dev = "dev_shot_layout"
    url = f"{WS}/ws/device/{dev}?" + urlencode({
        "token": "", "name": "书房电脑", "type": "pc",
        "platform": "seed", "agent_version": "shot", "enroll_token": "FAMILY-TEST-TOKEN"})
    try:
        async with websockets.connect(url) as ws:
            while not stop.is_set():
                try:
                    await asyncio.wait_for(ws.recv(), timeout=1.0)
                except asyncio.TimeoutError:
                    await ws.send(json.dumps({"type": "ping"}))
    except Exception as e:                                   # noqa: BLE001
        print(f"  ! 设备长连接提前断开：{e}")


def seed():
    stop = threading.Event()
    th = threading.Thread(target=lambda: asyncio.run(_keep_device_online(stop)), daemon=True)
    th.start()

    async def post_all():
        async with httpx.AsyncClient(base_url=BASE, timeout=15) as http:
            for who, text in SEED:
                r = await http.post("/api/messages",
                                    json={"sender_name": who, "content": text})
                r.raise_for_status()
    asyncio.run(post_all())
    print(f"  已写入 {len(SEED)} 条演示消息")
    return stop


# ── 断言 ────────────────────────────────────────────────────────────
def assert_order(page, tag):
    print(f"\n[{tag}] 消息页顺序")
    order = page.evaluate("""() => {
        const recent = document.querySelector('#home-recent');
        const compose = document.querySelector('#t-compose');
        if (!recent || !compose) return null;
        const recentSec = recent.closest('section');
        const composeSec = compose.closest('section.card');
        // DOCUMENT_POSITION_FOLLOWING(4) = composeSec 排在 recentSec 之后
        return {
          domFollowing: !!(recentSec.compareDocumentPosition(composeSec) & 4),
          recentTitle: recentSec.querySelector('.section__title').textContent,
          composeTitle: composeSec.querySelector('.card__title').textContent,
          rY: recentSec.getBoundingClientRect().top + window.scrollY,
          cY: composeSec.getBoundingClientRect().top + window.scrollY,
          rows: document.querySelectorAll('#home-recent .chat-row').length,
        };
    }""")
    check(order is not None, "消息页找到 #home-recent 与 #t-compose")
    if not order:
        return
    check(order["domFollowing"],
          "DOM 顺序：最近消息 section 在 发送消息 card **之前**",
          json.dumps(order, ensure_ascii=False))
    check(order["rY"] < order["cY"],
          "屏幕几何：最近消息的 y 坐标 < 发送卡片的 y 坐标（发送框在下面）",
          f"{order['rY']:.0f} vs {order['cY']:.0f}")
    check(order["rows"] >= 3, f"最近消息确实有内容可读（{order['rows']} 条）")

    # 发送区内部一个字段都没动
    inner = page.evaluate("""() => {
        const c = document.querySelector('#t-compose').closest('section.card');
        return {
          textarea: !!c.querySelector('#content[rows="3"][maxlength="2000"]'),
          quick: !!c.querySelector('#quick'),
          sender: !!c.querySelector('#sender-sel'),
          send: !!c.querySelector('#btn-send'),
          hint: !!c.querySelector('#send-hint'),
          label: c.querySelector('label[for="content"]').textContent.trim(),
          btnText: c.querySelector('#btn-send span').textContent.trim(),
        };
    }""")
    check(all([inner["textarea"], inner["quick"], inner["sender"], inner["send"], inner["hint"]]),
          "发送区内部字段/控件原封不动（textarea/快捷短语/昵称/发送/提示）",
          json.dumps(inner, ensure_ascii=False))
    check(inner["label"] == "消息内容" and inner["btnText"] == "发送",
          "发送区文案未改（「消息内容」/「发送」）", json.dumps(inner, ensure_ascii=False))


def shoot(page, name, full=False):
    p = OUT / f"ui-home-{name}.png"
    page.screenshot(path=str(p), full_page=full)
    MADE.append(p)
    print(f"  → {p}")


def main():
    stop = seed()
    try:
        with sync_playwright() as pw:
            browser = pw.chromium.launch()

            for mode in ("light", "dark"):
                print(f"\n════ 首页 1440 {mode} ════")
                ctx = browser.new_context(viewport={"width": 1440, "height": 940},
                                          device_scale_factor=1, locale="zh-CN")
                page = ctx.new_page()
                page.on("console", lambda m: errors.append(f"[{m.type}] {m.text[:160]}")
                        if m.type == "error" else None)
                page.on("pageerror", lambda e: errors.append(f"[pageerror] {str(e)[:200]}"))
                page.goto(BASE, wait_until="networkidle")
                page.evaluate("m => localStorage.setItem('fm.mode', m)", mode)
                page.evaluate("() => localStorage.setItem('fm.page', 'home')")
                page.reload(wait_until="networkidle")
                page.wait_for_timeout(800)
                shoot(page, f"1440-{mode}", full=True)
                assert_order(page, f"1440-{mode}")
                check(page.evaluate(
                    "document.querySelectorAll('#page-home .page__title').length") == 1,
                    "首页只渲染了一次（没有残留旧顺序的副本）")
                ctx.close()

            # 手机 390：顺序同样成立
            print("\n════ 首页 390 手机 ════")
            ctx = browser.new_context(viewport={"width": 390, "height": 844},
                                      device_scale_factor=1, locale="zh-CN")
            page = ctx.new_page()
            page.goto(BASE, wait_until="networkidle")
            page.evaluate("() => localStorage.setItem('fm.page', 'home')")
            page.reload(wait_until="networkidle")
            page.wait_for_timeout(800)
            shoot(page, "390-light", full=True)
            assert_order(page, "390-light")

            # 实机滚动到底：固定底栏会不会压住发送按钮？
            # （整页截图里 position:fixed 的底栏是画在视口位置的，属于截图假象，
            #   这里用真实滚动 + elementFromPoint 证伪/证实）
            page.evaluate("() => window.scrollTo(0, document.body.scrollHeight)")
            page.wait_for_timeout(500)
            covered = page.evaluate("""() => {
                const nav = document.querySelector('.bottom-nav');
                const btn = document.querySelector('#btn-send');
                const nb = nav.getBoundingClientRect();
                const bb = btn.getBoundingClientRect();
                const cx = bb.left + bb.width / 2, cy = bb.top + bb.height / 2;
                const hit = document.elementFromPoint(cx, cy);
                return { navTop: nb.top, btnBottom: bb.bottom, btnTop: bb.top, viewport: innerHeight,
                         overlap: bb.bottom > nb.top,
                         hit: hit ? (hit.id || hit.className) : null,
                         hitsButton: !!(hit && hit.closest('#btn-send')) };
            }""")
            check(not covered["overlap"] and covered["hitsButton"],
                  "滚到底时固定底栏没压住发送按钮（elementFromPoint 命中的就是它）",
                  json.dumps(covered, ensure_ascii=False))
            page.screenshot(path=str(OUT / "ui-home-390-bottom-light.png"))
            MADE.append(OUT / "ui-home-390-bottom-light.png")
            print(f"  → {OUT / 'ui-home-390-bottom-light.png'}")
            ctx.close()

            browser.close()
    finally:
        stop.set()

    print("\n────────────────────────────────────────────")
    print(f"断言 {CHECKS} 项，失败 {len(FAILS)} 项")
    for f in FAILS:
        print("  ✗ " + f)
    print(f"截图 {len(MADE)} 张：")
    for p in MADE:
        print(f"  {p}")
    if errors:
        print(f"⚠️  浏览器控制台 {len(errors)} 条报错：")
        for e in errors[:10]:
            print("   ", e)
    else:
        print("✅ 控制台零报错")
    return 1 if FAILS else 0


if __name__ == "__main__":
    raise SystemExit(main())
