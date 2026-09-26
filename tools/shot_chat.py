#!/usr/bin/env python3
"""群聊气泡布局验证：网页端与 PC 端**长得一样** —— 别人发的靠左、自己发的靠右。

判据（不是"看着像"，是 DOM 上真的分了阵营）：
  · .chat-row 有 chat-row--out 的 = 自己发的
  · 自己发的 sender_name 必须等于「我」的昵称
  · 网页端与 PC 端渲染出的 DOM 结构完全一致（同一个 chat.js 组件）

用法：python tools/shot_chat.py [base_url]
"""
from __future__ import annotations

import json
import sys
import urllib.request
from pathlib import Path

from playwright.sync_api import sync_playwright

BASE = (sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18899").rstrip("/")
OUT = Path("/vol1/1000/workspace/family-message/docs")
OUT.mkdir(parents=True, exist_ok=True)

ME_WEB = "爸爸"          # 网页端「我」的昵称
ME_PC = "爸爸"           # PC 端回复栏选中的昵称（同一昵称 = 同一个人）

errors: list[str] = []
made: list[Path] = []


def api(path, data=None):
    url = BASE + path
    body = json.dumps(data).encode() if data is not None else None
    req = urllib.request.Request(url, data=body,
                                headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=8) as r:
        return json.loads(r.read().decode())


def seed():
    """造一段像样的对话：交替的双方，能看出左右分栏"""
    convo = [
        ("妈妈", "今晚几点回来"),
        ("爸爸", "大概七点半，路上有点堵"),
        ("妈妈", "路上买袋米"),
        ("爸爸", "好，记得关窗，要下雨"),
    ]
    have = {(m.get("sender_name"), m.get("content")) for m in api("/api/messages?limit=200")}
    added = 0
    for name, text in convo:
        if (name, text) not in have:
            api("/api/messages", {"sender_name": name, "content": text})
            added += 1
    print(f"  造了 {added} 条，共 {len(api('/api/messages?limit=200'))} 条")


def shoot(page, name):
    p = OUT / f"chat-{name}.png"
    page.screenshot(path=str(p))
    made.append(p)
    print(f"  ✔ {p.name}")


def probe(page, scope):
    """读 DOM：谁靠左、谁靠右"""
    return page.evaluate("""(scope) => {
      const root = document.querySelector(scope);
      if (!root) return { missing: true };
      const rows = [...root.querySelectorAll('.chat-row')];
      return {
        total: rows.length,
        out: rows.filter(r => r.classList.contains('chat-row--out')).length,
        in_: rows.filter(r => !r.classList.contains('chat-row--out')).length,
        out_names: [...new Set(rows.filter(r => r.classList.contains('chat-row--out'))
                       .map(r => r.querySelector('.chat-name')?.textContent.trim()))],
        in_names: [...new Set(rows.filter(r => !r.classList.contains('chat-row--out'))
                       .map(r => r.querySelector('.chat-name')?.textContent.trim()))],
        out_texts: rows.filter(r => r.classList.contains('chat-row--out'))
                       .map(r => r.querySelector('.chat-bubble')?.textContent.trim()).slice(0,4),
        order: rows.map(r => (r.querySelector('.chat-time')?.textContent.trim() || '')
                           + '|' + (r.querySelector('.chat-bubble')?.textContent.trim() || '')),
        has_avatar: !!rows[0]?.querySelector('.chat-avatar'),
        has_bubble: !!rows[0]?.querySelector('.chat-bubble'),
      };
    }""", scope)


BRIDGE = r"""
(() => {
  const sent = [], listeners = [];
  window.chrome = Object.assign(window.chrome || {}, { webview: {
    postMessage(o) { sent.push(o); onSent(o); },
    addEventListener(t, cb) { if (t === 'message') listeners.push(cb); },
    removeEventListener() {},
  }});
  function dispatch(o) { listeners.forEach(cb => cb({ data: JSON.stringify(o) })); }
  window.__bridge = dispatch;
  function onSent(m) {
    if (m && m.type === 'web.ready') setTimeout(() => {
      dispatch({ type:'host.hello', mode:'client', version:'cs-0.12.4',
                 platform:'Windows 11 Pro 10.0.26100', server: location.origin,
                 theme_mode:'light', device_id:'pc_study', device_name:'书房电脑',
                 reply_names:['爸爸','妈妈','书房电脑'], reply_name:'爸爸' });
      dispatch({ type:'host.connection', connected:true, detail:'已连接' });
    }, 40);
  }
})();
"""


def main() -> int:
    seed()
    with sync_playwright() as pw:
        b = pw.chromium.launch()

        # ═══ 网页端「消息」页 ═══
        ctx = b.new_context(viewport={"width": 1440, "height": 940}, locale="zh-CN")
        pg = ctx.new_page()
        pg.on("pageerror", lambda e: errors.append(f"[pageerror] {str(e)[:200]}"))
        pg.on("console", lambda m: errors.append(f"[{m.type}] {m.text[:160]}")
              if m.type == "error" else None)
        # 昵称表是**纯本地**的（localStorage），全新浏览器里只有默认的 ['我']
        # → 对不上任何消息，所有气泡都会靠左（这是**正确行为**，不是 bug）。
        # 这里先把昵称表种好，让「我」= 爸爸 有消息可对。
        pg.add_init_script(
            "localStorage.setItem('fm.names', %s);"
            "localStorage.setItem('fm.lastSender', %s);"
            % (json.dumps(json.dumps(["爸爸", "妈妈", "书房电脑"], ensure_ascii=False)),
               json.dumps(ME_WEB)))
        pg.goto(BASE, wait_until="networkidle")
        pg.wait_for_timeout(600)
        pg.reload(wait_until="networkidle")
        pg.evaluate("() => { const n = document.querySelector('.nav-item[data-page=messages], [data-page=messages]'); if (n) n.click(); }")
        pg.wait_for_timeout(1200)
        d = probe(pg, '#log')
        print("\n═══ 网页端「消息」页（我 = %s）═══" % ME_WEB)
        for k, v in d.items():
            print(f"  {k} = {v}")
        ok = (d.get("total", 0) >= 4 and d.get("out", 0) >= 1 and d.get("in_", 0) >= 1
              and d.get("out_names") == [ME_WEB])
        print(f"  {'✅' if ok else '❌'} 自己发的靠右、别人发的靠左（右边全是「%s」）" % ME_WEB)
        times = [o.split('|')[0] for o in d.get("order", [])]
        asc = times == sorted(times)
        print(f"  {'✅' if asc else '❌'} 顺序是旧→新（最新在最下面）  {times}")
        print(f"     内容顺序：{[o.split('|',1)[1] for o in d.get('order', [])]}")
        shoot(pg, "web-messages-light")

        # 明色/暗色都看一眼
        pg.evaluate("() => { document.documentElement.setAttribute('data-theme','dark'); }")
        pg.wait_for_timeout(400)
        shoot(pg, "web-messages-dark")
        pg.evaluate("() => { document.documentElement.setAttribute('data-theme','light'); }")

        # ═══ 网页端首页「最近消息」 ═══
        pg.evaluate("() => { const n = document.querySelector('[data-page=home]'); if (n) n.click(); }")
        pg.wait_for_timeout(900)
        d2 = probe(pg, '#home-recent')
        print("\n═══ 网页端首页「最近消息」═══")
        for k, v in d2.items():
            print(f"  {k} = {v}")
        print(f"  {'✅' if d2.get('has_bubble') else '❌'} 首页也用的同一套气泡")
        shoot(pg, "web-home-recent-light")
        ctx.close()

        # ═══ PC 端客户端窗口 ═══
        ctx = b.new_context(viewport={"width": 1000, "height": 760}, locale="zh-CN")
        pg = ctx.new_page()
        pg.add_init_script(BRIDGE)
        pg.on("pageerror", lambda e: errors.append(f"[pageerror] {str(e)[:200]}"))
        pg.on("console", lambda m: errors.append(f"[{m.type}] {m.text[:160]}")
              if m.type == "error" else None)
        pg.goto(f"{BASE}/?shell=1&mode=client", wait_until="domcontentloaded")
        pg.wait_for_timeout(1800)
        d3 = probe(pg, '#client-stack')
        print("\n═══ PC 端客户端窗口（我 = %s）═══" % ME_PC)
        for k, v in d3.items():
            print(f"  {k} = {v}")
        ok3 = (d3.get("total", 0) >= 4 and d3.get("out", 0) >= 1 and d3.get("in_", 0) >= 1
               and d3.get("out_names") == [ME_PC])
        print(f"  {'✅' if ok3 else '❌'} 两端布局一致（同样靠昵称分左右）")
        t3 = [o.split('|')[0] for o in d3.get("order", [])]
        print(f"  {'✅' if t3 == sorted(t3) else '❌'} PC 端顺序也是旧→新")
        same = (d3.get("out_names") == d.get("out_names")
                and d3.get("in_names") == d.get("in_names")
                and d3.get("total") == d.get("total"))
        print(f"  {'✅' if same else '❌'} 同一批消息在两端分到**同一侧**（组件是同一份）")
        shoot(pg, "shell-client-chat-light")
        pg.evaluate("() => { document.documentElement.setAttribute('data-theme','dark'); }")
        pg.wait_for_timeout(400)
        shoot(pg, "shell-client-chat-dark")
        ctx.close()

        b.close()

    print(f"\n控制台/页面错误：{errors if errors else '无'}")
    print(f"\n共 {len(made)} 张 → {OUT}")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
