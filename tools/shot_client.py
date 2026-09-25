#!/usr/bin/env python3
"""WebView2 壳 `mode=client`（PC 消息客户端窗口）验证截图。

背景：从托盘打开 PC 端窗口时，壳以前加载的是 `mode=console`（完整网页管理后台，
带 首页/消息/设备/截图/设置 侧边栏）—— 看着就像"打开了一个网页"。
改成 `mode=client`：只有消息记录 + 回复栏。

这个脚本注入一个假桥（只认 client 形态），抓图并打印**真实 DOM 取证**：
真的没有侧边栏、消息区在、回复栏在、点「控制台」真的发了 web.switch_mode。

用法：python tools/shot_client.py [base_url] [device_id] [empty_device_id]
"""
from __future__ import annotations

import sys
from pathlib import Path

from playwright.sync_api import sync_playwright

BASE = (sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18899").rstrip("/")
DEVICE = sys.argv[2] if len(sys.argv) > 2 else "pc_study"        # 有历史消息的
EMPTY_DEVICE = sys.argv[3] if len(sys.argv) > 3 else "pc_old"    # 没有消息的
OUT = Path("/vol1/1000/workspace/family-message/docs")
OUT.mkdir(parents=True, exist_ok=True)

errors: list[str] = []
made: list[Path] = []

# 假桥：模拟 WebView2 宿主。mode 固定 client，并把 device_id 带上
# —— 页面靠它去拉「这台机器」的往来记录。
BRIDGE = r"""
(() => {
  const MODE = '__MODE__';
  const DEVICE_ID = '__DEVICE__';
  const DEVICE_NAME = '__DEVNAME__';
  const sent = [];
  const listeners = [];

  window.chrome = Object.assign(window.chrome || {}, {
    webview: {
      postMessage(obj) { sent.push(obj); onSent(obj); },
      addEventListener(type, cb) { if (type === 'message') listeners.push(cb); },
      removeEventListener(type, cb) {
        const i = listeners.indexOf(cb); if (i >= 0) listeners.splice(i, 1);
      },
    },
  });

  window.__shellSent = sent;
  window.__shellSentTypes = () => sent.map((m) => m.type);
  window.__shellDispatch = (obj) => listeners.forEach((cb) => cb({ data: JSON.stringify(obj) }));

  function dispatch(obj) { window.__shellDispatch(obj); }

  function onSent(msg) {
    if (!msg || !msg.type) return;
    if (msg.type === 'web.ready') {
      setTimeout(() => {
        dispatch({ type: 'host.hello', mode: MODE, version: 'cs-0.12.2',
                   platform: 'Windows 11 Pro 10.0.26100', server: location.origin,
                   theme_mode: 'system',
                   device_id: DEVICE_ID, device_name: DEVICE_NAME,
                   reply_names: ['妈妈', '爸爸', DEVICE_NAME], reply_name: DEVICE_NAME });
        dispatch({ type: 'host.connection', connected: true, detail: '已连接' });
        dispatch({ type: 'host.session', windows_state: 'locked',
                   can_unlock: true, can_screenshot: true, can_shutdown: true });
      }, 40);
    }
    if (msg.type === 'web.switch_mode') {
      setTimeout(() => dispatch({ type: 'host.mode', mode: msg.mode }), 60);
    }
  }
})();
"""


def bridge_for(mode: str, device: str, name: str) -> str:
    return (BRIDGE.replace("__MODE__", mode)
                  .replace("__DEVICE__", device)
                  .replace("__DEVNAME__", name))


def open_page(browser, mode="client", device=DEVICE, name="书房电脑",
              w=1000, h=760, theme="light"):
    ctx = browser.new_context(viewport={"width": w, "height": h},
                              device_scale_factor=1, locale="zh-CN")
    page = ctx.new_page()
    page.add_init_script(bridge_for(mode, device, name))
    page.on("console", lambda m: errors.append(f"[{m.type}] {m.text[:160]}")
            if m.type == "error" else None)
    page.on("pageerror", lambda e: errors.append(f"[pageerror] {str(e)[:200]}"))
    page.goto(f"{BASE}/?shell=1&mode={mode}", wait_until="domcontentloaded")
    page.evaluate("t => localStorage.setItem('fm.mode', t)", theme)
    page.reload(wait_until="load")
    page.wait_for_timeout(1200)
    return ctx, page


def probe(page) -> dict:
    """真实 DOM 取证：客户端视图该有的要有，不该有的不能有。"""
    return page.evaluate("""() => {
      const cs = (sel) => { const e = document.querySelector(sel); if (!e) return null;
        const s = getComputedStyle(e); const r = e.getBoundingClientRect();
        return { visible: s.display !== 'none' && s.visibility !== 'hidden' && r.width > 0 && r.height > 0,
                 w: Math.round(r.width), h: Math.round(r.height) }; };
      const vis = (sel) => { const p = cs(sel); return !!(p && p.visible); };
      return {
        client_layer: cs('#shell-client'),
        // 侧边栏 / 导航：客户端视图里必须不可见
        nav_pane: cs('.nav-pane'),
        sidebar_visible: vis('.nav-pane'),
        console_shell_visible: vis('.app-shell'),
        // 客户端该有的
        stack: cs('.client-stack') || cs('#client-stack'),
        reply_input: cs('#client-reply-text') || cs('#shell-client input.text-input'),
        console_btn: vis('#client-console') || !!document.querySelector('[data-open-console]'),
        // 消息条数
        msg_count: document.querySelectorAll('#shell-client .popup-msg').length,
        first_msg_text: (document.querySelector('#shell-client .popup-msg__body') || {}).textContent || '',
        data_shell_mode: document.documentElement.getAttribute('data-shell-mode'),
        body_text_head: (document.body.innerText || '').slice(0, 120).replace(/\\n/g, ' | '),
      };
    }""")


def shoot(page, name):
    p = OUT / f"shell-client-{name}.png"
    page.screenshot(path=str(p))
    made.append(p)
    print(f"  ✔ {p.name}")
    return p


def main() -> int:
    with sync_playwright() as pw:
        b = pw.chromium.launch()

        # ── ① 有历史消息：明色 / 暗色 ──
        for theme, label in (("light", "history-light"), ("dark", "history-dark")):
            ctx, page = open_page(b, theme=theme)
            d = probe(page)
            if theme == "light":
                print("\n═══ DOM 取证（有历史）═══")
                print(f"  data-shell-mode        = {d['data_shell_mode']}")
                print(f"  客户端层可见           = {bool(d['client_layer'] and d['client_layer']['visible'])}")
                print(f"  侧边栏(.nav-pane)可见  = {d['sidebar_visible']}   ← 必须是 False")
                print(f"  控制台外壳(.app-shell)可见 = {d['console_shell_visible']}   ← 必须是 False")
                print(f"  「控制台」按钮在       = {d['console_btn']}")
                print(f"  消息条数               = {d['msg_count']}")
                print(f"  首条正文               = {d['first_msg_text'][:40]!r}")
                print(f"  可见文本开头           = {d['body_text_head']!r}")
                ok = (d['data_shell_mode'] == 'client'
                      and not d['sidebar_visible']
                      and not d['console_shell_visible']
                      and d['msg_count'] > 0)
                print(f"\n  {'✅' if ok else '❌'} 客户端视图形态正确（无侧边栏 + 有消息）")
            shoot(page, label)
            ctx.close()

        # ── ② 空会话（没消息的设备）──
        ctx, page = open_page(b, device=EMPTY_DEVICE, name="旧笔记本")
        d = probe(page)
        print(f"\n═══ 空会话：消息条数 = {d['msg_count']}，文本 = {d['body_text_head'][:80]!r}")
        print(f"  {'✅' if not d['sidebar_visible'] else '❌'} 空会话下仍无侧边栏")
        shoot(page, "empty-light")
        ctx.close()

        # ── ③ 点「控制台」→ 必须发 web.switch_mode{console} ──
        ctx, page = open_page(b)
        clicked = False
        for sel in ("#client-console", "[data-open-console]", "#shell-client .btn"):
            try:
                el = page.query_selector(sel)
                if el and el.is_visible():
                    el.click()
                    clicked = True
                    print(f"\n═══ 点「控制台」用的选择器: {sel}")
                    break
            except Exception:
                pass
        page.wait_for_timeout(600)
        types = page.evaluate("() => window.__shellSentTypes()")
        sw = page.evaluate(
            "() => window.__shellSent.filter(m => m.type === 'web.switch_mode')")
        print(f"  点到了按钮     = {clicked}")
        print(f"  桥收到的消息   = {types}")
        print(f"  web.switch_mode = {sw}")
        print(f"  {'✅' if sw and sw[0].get('mode') == 'console' else '❌'} 切控制台请求正确")
        shoot(page, "after-console-click-light")
        ctx.close()

        # ── ④ 消息推送：host.message 进来要出现在列表里 ──
        ctx, page = open_page(b)
        before = probe(page)["msg_count"]
        page.evaluate("""() => window.__shellDispatch({
            type: 'host.message',
            message: { id: 987654, sender_name: '妈妈', content: '这是一条刚推过来的测试消息',
                       created_at: '2026-09-25 20:49:00', device_id: 'pc_study',
                       status: 'device_received' } })""")
        page.wait_for_timeout(800)
        after = probe(page)
        print(f"\n═══ 实时推送：条数 {before} → {after['msg_count']}")
        print(f"  末条正文 = {page.evaluate('() => { const a = document.querySelectorAll(\"#shell-client .popup-msg__body\"); return a.length ? a[a.length-1].textContent : \"\" }')[:40]!r}")
        print(f"  {'✅' if after['msg_count'] > before else '❌'} 新消息追加进列表")
        shoot(page, "pushed-light")
        ctx.close()

        # ── ⑤ 浏览器模式未受影响（不带壳标记）──
        ctx = b.new_context(viewport={"width": 1440, "height": 940}, locale="zh-CN")
        page = ctx.new_page()
        page.on("pageerror", lambda e: errors.append(f"[pageerror] {str(e)[:200]}"))
        page.goto(BASE, wait_until="networkidle")
        page.wait_for_timeout(900)
        nav = page.query_selector(".nav-pane")
        print(f"\n═══ 浏览器模式：侧边栏存在 = {bool(nav and nav.is_visible())}  ← 必须是 True")
        shoot(page, "browser-mode-unchanged-light")
        ctx.close()
        b.close()

    print(f"\n控制台/页面错误：{errors if errors else '无'}")
    print(f"\n共 {len(made)} 张 → {OUT}")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
