#!/usr/bin/env python3
"""群聊模型改造的验证截图（网页端 + 壳模式）。

只验**模型改动本身**，不重复验其他功能：
  网页端：发送区没有设备选择、消息流是群聊且只有「已发送」、设备页控制按钮完整
  壳模式：client 无侧边栏无控制台按钮、popup 无控制台入口

用法：python tools/shot_groupchat.py [base_url]
"""
from __future__ import annotations

import sys
from pathlib import Path

from playwright.sync_api import sync_playwright

BASE = (sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18899").rstrip("/")
OUT = Path("/vol1/1000/workspace/family-message/docs")
OUT.mkdir(parents=True, exist_ok=True)

errors: list[str] = []
made: list[Path] = []

BRIDGE = r"""
(() => {
  const MODE = '__MODE__';
  const sent = [], listeners = [];
  window.chrome = Object.assign(window.chrome || {}, { webview: {
    postMessage(o) { sent.push(o); onSent(o); },
    addEventListener(t, cb) { if (t === 'message') listeners.push(cb); },
    removeEventListener() {},
  }});
  window.__shellSent = sent;
  window.__shellSentTypes = () => sent.map(m => m.type);
  window.__shellDispatch = (o) => listeners.forEach(cb => cb({ data: JSON.stringify(o) }));
  function dispatch(o) { window.__shellDispatch(o); }
  function onSent(m) {
    if (!m || !m.type) return;
    if (m.type === 'web.ready') {
      setTimeout(() => {
        dispatch({ type:'host.hello', mode: MODE, version:'cs-0.12.3',
                   platform:'Windows 11 Pro 10.0.26100', server: location.origin,
                   theme_mode:'system', device_id:'pc_study', device_name:'书房电脑',
                   reply_names:['妈妈','爸爸','书房电脑'], reply_name:'书房电脑' });
        dispatch({ type:'host.connection', connected:true, detail:'已连接' });
        dispatch({ type:'host.session', windows_state:'locked', can_unlock:true,
                   can_screenshot:true, can_shutdown:true });
      }, 40);
    }
  }
})();
"""


def shoot(page, name):
    p = OUT / f"chat-{name}.png"
    page.screenshot(path=str(p))
    made.append(p)
    print(f"  ✔ {p.name}")


def web_page(b, page_id):
    ctx = b.new_context(viewport={"width": 1440, "height": 940}, locale="zh-CN")
    pg = ctx.new_page()
    pg.on("pageerror", lambda e: errors.append(f"[pageerror] {str(e)[:200]}"))
    pg.on("console", lambda m: errors.append(f"[{m.type}] {m.text[:160]}")
          if m.type == "error" else None)
    pg.goto(BASE, wait_until="networkidle")
    pg.evaluate("p => localStorage.setItem('fm.page', p)", page_id)
    pg.reload(wait_until="networkidle")
    pg.wait_for_timeout(1100)
    return ctx, pg


def shell_page(b, mode, theme="light"):
    ctx = b.new_context(viewport={"width": 1000, "height": 760}, locale="zh-CN")
    pg = ctx.new_page()
    pg.add_init_script(BRIDGE.replace("__MODE__", mode))
    pg.on("pageerror", lambda e: errors.append(f"[pageerror] {str(e)[:200]}"))
    pg.on("console", lambda m: errors.append(f"[{m.type}] {m.text[:160]}")
          if m.type == "error" else None)
    pg.goto(f"{BASE}/?shell=1&mode={mode}", wait_until="domcontentloaded")
    pg.evaluate("t => localStorage.setItem('fm.mode', t)", theme)
    pg.reload(wait_until="load")
    pg.wait_for_timeout(1300)
    return ctx, pg


def main() -> int:
    with sync_playwright() as pw:
        b = pw.chromium.launch()

        # ═══ 网页端：首页（发送区）═══
        ctx, pg = web_page(b, "home")
        d = pg.evaluate("""() => ({
          has_targets: !!document.querySelector('#targets'),
          has_arrow: !!document.querySelector('.compose__arrow'),
          hint: (document.querySelector('.compose__group-hint')||{}).textContent?.trim() || '',
          send_disabled: document.querySelector('#btn-send')?.disabled,
          device_chips: document.querySelectorAll('#targets .chip').length,
        })""")
        print("\n═══ 网页端发送区（群聊模型）═══")
        for k, v in d.items():
            print(f"  {k} = {v}")
        ok = (not d['has_targets'] and not d['has_arrow']
              and d['send_disabled'] is False)
        print(f"  {'✅' if ok else '❌'} 没有设备选择器、发送按钮不依赖选设备")
        shoot(pg, "web-home-compose-light")
        ctx.close()

        # ═══ 网页端：消息流 ═══
        ctx, pg = web_page(b, "messages")
        d = pg.evaluate("""() => {
          const badges = [...document.querySelectorAll('.status')].map(e => e.textContent.trim());
          const uniq = [...new Set(badges)];
          const names = [...document.querySelectorAll('.msg__sender, .msg__name')].map(e=>e.textContent.trim());
          return { badge_kinds: uniq, badge_count: badges.length,
                   senders: [...new Set(names)].slice(0,6),
                   list_len: document.querySelectorAll('.msg-list .msg, .msg').length };
        }""")
        print("\n═══ 网页端消息流（群聊）═══")
        for k, v in d.items():
            print(f"  {k} = {v}")
        clean = d['badge_kinds'] in ([], ['已发送'])
        print(f"  {'✅' if clean else '❌'} 状态只有「已发送」（无逐设备状态）")
        shoot(pg, "web-messages-light")
        ctx.close()

        # ═══ 网页端：设备页（控制按钮必须完整保留）═══
        ctx, pg = web_page(b, "devices")
        d = pg.evaluate("""() => {
          const btns = [...document.querySelectorAll('.dev__acts .btn, .dev__acts button')]
                        .map(e => e.textContent.trim()).filter(Boolean);
          return { buttons: [...new Set(btns)], has_shot: btns.some(t=>t.includes('截图')||t.includes('桌面')),
                   has_power: btns.some(t=>t.includes('关机')), has_unlock: btns.some(t=>t.includes('解锁')),
                   has_conv: btns.some(t=>t.includes('对话')) };
        }""")
        print("\n═══ 网页端设备页（必须完整保留）═══")
        for k, v in d.items():
            print(f"  {k} = {v}")
        keep = d['has_shot'] and d['has_power'] and d['has_unlock']
        print(f"  {'✅' if keep else '❌'} 截图/关机/解锁按钮都在")
        print(f"  {'✅ 已删（群聊模型下不该有单独会话）' if not d['has_conv'] else '⚠ 仍有「对话」按钮'}")
        shoot(pg, "web-devices-light")
        ctx.close()

        # ═══ 壳模式：client ═══
        ctx, pg = shell_page(b, "client")
        d = pg.evaluate("""() => ({
          shell_mode: document.documentElement.getAttribute('data-shell-mode'),
          sidebar: (() => { const e=document.querySelector('.nav-pane'); if(!e) return false;
                            const s=getComputedStyle(e); return s.display!=='none' && e.getBoundingClientRect().width>0; })(),
          console_btn: !!document.querySelector('#client-console, #popup-console, [data-open-console]'),
          msgs: document.querySelectorAll('#shell-client .popup-msg').length,
          badges: [...new Set([...document.querySelectorAll('#shell-client .status')].map(e=>e.textContent.trim()))],
        })""")
        print("\n═══ 壳模式 client（PC 消息客户端）═══")
        for k, v in d.items():
            print(f"  {k} = {v}")
        ok2 = (d['shell_mode'] == 'client' and not d['sidebar'] and not d['console_btn'])
        print(f"  {'✅' if ok2 else '❌'} 无侧边栏、无控制台按钮、是 client 形态")
        shoot(pg, "shell-client-light")
        ctx.close()

        # ═══ 壳模式：popup ═══
        ctx, pg = shell_page(b, "popup", theme="dark")
        d = pg.evaluate("""() => ({
          shell_mode: document.documentElement.getAttribute('data-shell-mode'),
          console_entry: !!document.querySelector('#popup-console, [data-open-console]'),
          msgs: document.querySelectorAll('#shell-popup .popup-msg').length,
          has_ok: !!document.querySelector('#popup-ok'),
        })""")
        print("\n═══ 壳模式 popup（全屏强提醒）═══")
        for k, v in d.items():
            print(f"  {k} = {v}")
        ok3 = (d['shell_mode'] == 'popup' and not d['console_entry'])
        print(f"  {'✅' if ok3 else '❌'} 无控制台入口、弹窗结构完整")
        shoot(pg, "shell-popup-dark")
        ctx.close()

        b.close()

    print(f"\n控制台/页面错误：{errors if errors else '无'}")
    print(f"\n共 {len(made)} 张 → {OUT}")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
