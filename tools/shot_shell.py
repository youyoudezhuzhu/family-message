#!/usr/bin/env python3
"""WebView2 壳模式验证截图 + 自检。

用 Playwright 注入一个**假桥**（window.chrome.webview）模拟 PC 端的壳：
页面以为自己在 WebView2 里，宿主事件由脚本自己派发。

覆盖：
  · 全屏消息弹窗：单条 / 多条堆叠 / 长文本 / 明暗
  · 壳里的控制台：桥驱动连接状态、本机会话徽标、退出程序、不连 /ws/web
  · 从弹窗切回控制台（web.switch_mode → host.mode，不刷新页面）
  · 壳的本地页：web/shell/boot.html、web/shell/offline.html
  · 回归：浏览器模式（不带 ?shell=1）行为不变

用法：
    python tools/shot_shell.py [base_url] [out_dir]
默认 http://127.0.0.1:18899（开发实例）→ docs/shell-*.png
"""
import sys
from pathlib import Path

from playwright.sync_api import sync_playwright

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18899"
OUT = Path(sys.argv[2] if len(sys.argv) > 2 else "/vol1/1000/workspace/family-message/docs")
ROOT = Path(__file__).resolve().parent.parent
SHELL_DIR = ROOT / "web" / "shell"
OUT.mkdir(parents=True, exist_ok=True)

# ── 假桥：伪装成 WebView2 宿主 ────────────────────────────────────────
# 页面加载前注入。web.ready → 回 host.hello（形态取 ?mode=）；
# 再补 host.connection / host.session；页面发请求就回对应的回执。
INIT_FAKE_BRIDGE = r"""
(() => {
  const params = new URLSearchParams(location.search);
  const mode = params.get('mode') === 'popup' ? 'popup' : 'console';
  const hello = params.get('hello');                 // 毫秒数 / 'never'：模拟宿主迟应答
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
  window.__replySeq = 0;
  window.__replyStatus = 'ok';

  function dispatch(obj) { window.__shellDispatch(obj); }

  function onSent(msg) {
    if (!msg || !msg.type) return;
    if (msg.type === 'web.ready') {
      if (hello === 'never') return;
      const delay = hello ? parseInt(hello, 10) : 40;
      setTimeout(() => {
        dispatch({ type: 'host.hello', mode, version: 'cs-0.12.0',
                   platform: 'Windows 11 Pro 10.0.26100', server: location.origin,
                   theme_mode: 'system' });
        dispatch({ type: 'host.connection', connected: true, detail: '已连接' });
        dispatch({ type: 'host.session', windows_state: 'locked',
                   can_unlock: true, can_screenshot: true, can_shutdown: true });
      }, delay);
    }
    if (msg.type === 'web.switch_mode') {
      setTimeout(() => dispatch({ type: 'host.mode', mode: msg.mode }), 60);
    }
    if (msg.type === 'web.reply') {
      const status = window.__replyStatus;
      window.__replySeq += 1;
      setTimeout(() => dispatch({ type: 'host.reply_ack', client_id: msg.client_id,
                                  status, message_id: 900000 + window.__replySeq,
                                  detail: status === 'error' ? '服务端没有接受这条回复（示例）' : '' }), 90);
    }
    if (msg.type === 'web.request_screenshot') {
      setTimeout(() => dispatch({ type: 'host.screenshot', request_id: msg.request_id,
                                  ok: true, data_url: 'data:image/gif;base64,R0lGODlhAQABAAAAACw=' }), 60);
    }
    if (msg.type === 'web.request_action') {
      setTimeout(() => dispatch({ type: 'host.action_result', action: msg.action,
                                  ok: true, detail: '已执行（示例）' }), 60);
    }
    // 「已显示」语义取证：收到 web.ack 时消息节点必须已经在 DOM 里
    if (msg.type === 'web.ack') {
      window.__ackInfo = { message_id: msg.message_id,
                           nodeInDom: !!document.querySelector('.popup-msg'),
                           leadInDom: !!document.querySelector('.popup-msg--lead') };
    }
  }
})();
"""

MSGS = [
    (101, '妈妈', '下来吃饭了，菜都凉了', '2026-09-24 18:32:00'),
    (102, '爸爸', '我把车钥匙放鞋柜上了', '2026-09-24 18:41:12'),
    (103, '妈妈', '记得把牛奶带回来', '2026-09-24 19:05:33'),
    (104, '妹妹', '哥，我的耳机是不是在你那', '2026-09-24 19:20:07'),
]
LONG_MSG = (105, '妈妈',
            '明天上午十点要去学校开家长会，老师说这次要聊一下下学期选课的事，'
            '你如果有空就一起去，没空我自己去也行，记得回我一句。',
            '2026-09-24 19:31:45')

results = []
made = []
errors = []
bad_status = []


def check(name, ok, extra=""):
    results.append((name, bool(ok), extra))
    print(f"  {'PASS' if ok else 'FAIL'}  {name}" + (f"   → {extra}" if extra else ""))


def shot(page, name, full=False):
    p = OUT / f"shell-{name}.png"
    page.screenshot(path=str(p), full_page=full)
    made.append(p)
    print(f"  {p.name}")


def wire(page, tag):
    page.on("pageerror", lambda e: errors.append(f"[{tag}][pageerror] {str(e)[:200]}"))
    page.on("console", lambda m: errors.append(f"[{tag}][console.error] {m.text[:180]}")
            if m.type == "error" else None)
    page.on("response", lambda r: bad_status.append(f"[{tag}] {r.status} {r.url}")
            if r.status >= 400 and "favicon" not in r.url else None)


def msg_json(x):
    i, who, content, at = x
    return ("{type:'host.message', message:{id:%d, sender_name:%r, content:%r,"
            " created_at:%r, device_id:'pc_study', status:'device_received'}}" % (i, who, content, at))


def push(page, *msgs):
    for x in msgs:
        page.evaluate("() => window.__shellDispatch(%s)" % msg_json(x))


def new_page(browser, w=1440, h=900, mode=None, color_scheme="light", dark=False):
    ctx = browser.new_context(viewport={"width": w, "height": h}, device_scale_factor=1,
                              locale="zh-CN", color_scheme=color_scheme)
    page = ctx.new_page()
    page.add_init_script(INIT_FAKE_BRIDGE)
    if dark:
        page.goto(BASE, wait_until="domcontentloaded")
        page.evaluate("() => localStorage.setItem('fm.mode', 'dark')")
    return ctx, page


def open_shell(browser, mode, w=1440, h=900, dark=False, tag="shell", extra=""):
    """打开壳模式页面。返回 (ctx, page, ws_urls)——ws_urls 在 goto 之前就挂上了监听，
    这样「壳模式下有没有偷偷连 /ws/web」才测得准。"""
    ctx, page = new_page(browser, w, h, mode, dark=dark)
    wire(page, tag)
    ws_urls = []
    page.on("websocket", lambda ws: ws_urls.append(ws.url))
    page.goto(f"{BASE}/?shell=1&mode={mode}{extra}", wait_until="load")
    return ctx, page, ws_urls


def wait_popup(page):
    page.wait_for_function("() => window.__shellSent && window.__shellSent.some(m => m.type === 'web.ready')",
                           timeout=5000)
    page.wait_for_selector("#shell-popup:not([hidden])", timeout=5000)


def sent_types(page):
    return page.evaluate("() => window.__shellSentTypes()")


def main():
    with sync_playwright() as pw:
        browser = pw.chromium.launch()

        # ══ 1. 弹窗：单条（明）════════════════════════════════════
        print("弹窗 · 单条（浅色）")
        ctx, page, _ws = open_shell(browser, "popup", tag="popup-single")
        wait_popup(page)
        check("弹窗形态：弹窗已就位", page.is_visible("#shell-popup"))
        # 一条消息都还没到时先留在加载态（避免闪一下空弹窗），宽限期内要自己让位给空态
        page.wait_for_selector("#shell-boot", state="hidden", timeout=3000)
        check("没有消息时宽限期一到，加载态换成弹窗空态",
              not page.is_visible("#shell-boot") and page.is_visible("#popup-empty"))
        push(page, MSGS[0])
        page.wait_for_selector(".popup-msg--lead", timeout=3000)
        page.wait_for_timeout(500)
        page.wait_for_function("() => !!window.__ackInfo", timeout=3000)
        ack = page.evaluate("() => window.__ackInfo")
        check("web.ack 的 message_id 正确", ack["message_id"] == 101, str(ack))
        check("web.ack 发出时消息节点已上屏", ack["nodeInDom"] and ack["leadInDom"], str(ack))
        check("弹窗里正文是主条（.popup-msg--lead）", page.is_visible(".popup-msg--lead"))
        check("弹窗里没有第二个密码框", not page.is_visible("#dlg-confirm.is-open"))
        shot(page, "popup-single-light")

        # ══ 2. 弹窗：多条堆叠（明）════════════════════════════════
        print("弹窗 · 多条堆叠（浅色）")
        push(page, MSGS[1], MSGS[2], MSGS[3])
        page.wait_for_timeout(700)
        check("多条消息：4 条都在，最新的是主条",
              page.eval_on_selector_all(".popup-msg", "els => els.length") == 4
              and page.eval_on_selector_all(".popup-msg--prev", "els => els.length") == 3,
              f"all={page.eval_on_selector_all('.popup-msg', 'e=>e.length')} "
              f"prev={page.eval_on_selector_all('.popup-msg--prev', 'e=>e.length')}")
        check("弹窗没有横向溢出",
              page.evaluate("() => document.documentElement.scrollWidth <= window.innerWidth + 1"))
        shot(page, "popup-multi-light")

        # ══ 3. 弹窗：回复（明）════════════════════════════════════
        print("弹窗 · 回复")
        page.fill("#popup-reply-text", "知道了，我这就下来")
        page.click("#popup-reply-send")
        page.wait_for_function("() => (document.getElementById('popup-reply-hint').textContent||'').includes('已回复')",
                               timeout=4000)
        rep = page.evaluate("() => window.__shellSent.filter(m => m.type === 'web.reply')")
        check("web.reply 带 client_id / sender_name / content",
              rep and rep[0].get("client_id") and rep[0].get("sender_name") and rep[0].get("content"),
              str(rep[:1]))
        check("回执到后输入框已清空", page.input_value("#popup-reply-text") == "")
        check("回复提示显示「已回复」", "已回复" in page.inner_text("#popup-reply-hint"))
        page.wait_for_timeout(200)
        shot(page, "popup-replied-light")

        # ══ 4. 弹窗：失败回执（明）════════════════════════════════
        print("弹窗 · 回复失败回执")
        page.evaluate("() => { window.__replyStatus = 'error'; }")
        page.fill("#popup-reply-text", "这条会被服务端拒收")
        page.click("#popup-reply-send")
        page.wait_for_function("() => !!document.querySelector('#popup-reply-hint.is-error')", timeout=4000)
        check("host.reply_ack status=error 时提示失败原因",
              "拒绝" in page.inner_text("#popup-reply-hint") or "示例" in page.inner_text("#popup-reply-hint"),
              page.inner_text("#popup-reply-hint"))

        # ══ 5. 弹窗：长文本（明）══════════════════════════════════
        print("弹窗 · 长文本")
        push(page, LONG_MSG)
        page.wait_for_timeout(700)
        box = page.eval_on_selector("#popup-stack", "el => el.getBoundingClientRect().width")
        body = page.eval_on_selector(".popup-msg--lead .popup-msg__body",
                                     "el => { const r = el.getBoundingClientRect(); return {w: r.width, h: r.height, fs: getComputedStyle(el).fontSize}; }")
        check("长文本主条字号够大（≥26px）", float(body["fs"].replace("px", "")) >= 26, str(body))
        check("长文本没有横向溢出（正文宽度 ≤ 栈宽）", body["w"] <= box + 1, f"{body['w']} <= {box}")
        check("长文本主条在视口内",
              page.eval_on_selector(".popup-msg--lead", "el => { const r = el.getBoundingClientRect(); return r.top >= -1 && r.bottom <= window.innerHeight + 1; }"))
        shot(page, "popup-long-light")

        # ══ 6. 「知道了」→ web.close ══════════════════════════════
        print("弹窗 · 知道了")
        before = len(sent_types(page))
        page.click("#popup-ok")
        page.wait_for_timeout(300)
        check("点「知道了」发出 web.close", "web.close" in sent_types(page)[before:])
        ctx.close()

        # ══ 7. 弹窗：暗色 ════════════════════════════════════════
        print("弹窗 · 暗色（单条 / 多条）")
        ctx, page, _ws = open_shell(browser, "popup", dark=True, tag="popup-dark")
        wait_popup(page)
        push(page, MSGS[0])
        page.wait_for_timeout(600)
        shot(page, "popup-single-dark")
        push(page, MSGS[3], LONG_MSG)
        page.wait_for_timeout(700)
        check("暗色下正文与底色不是同一色（可读）",
              page.evaluate("""() => {
                const b = getComputedStyle(document.querySelector('.popup-msg--lead .popup-msg__body')).color;
                const p = getComputedStyle(document.querySelector('.shell-popup')).backgroundColor;
                return b !== p;
              }"""))
        shot(page, "popup-multi-dark")
        ctx.close()

        # ══ 8. 弹窗专属设备尺寸（全屏弹窗常见分辨率）═══════════════
        print("弹窗 · 1920×1080")
        ctx, page, _ws = open_shell(browser, "popup", w=1920, h=1080, tag="popup-fhd")
        wait_popup(page)
        push(page, MSGS[2])
        page.wait_for_timeout(600)
        shot(page, "popup-1920-light")
        ctx.close()

        # ══ 9. 控制台（壳里）═════════════════════════════════════
        print("控制台 · 壳模式")
        ctx, page, sockets = open_shell(browser, "console", tag="console")
        page.wait_for_selector("#page-home:not([hidden])", timeout=8000)
        page.wait_for_timeout(600)
        check("控制台起来了，加载态收掉", not page.is_visible("#shell-boot"))
        check("壳模式下**没有**连 /ws/web", sockets == [], str(sockets))
        check("连接状态由 host.connection 驱动",
              "已连接" in page.inner_text("#conn-status"), page.inner_text("#conn-status"))
        check("本机会话徽标出现（host.session）",
              page.is_visible("#shell-local") and "已锁屏" in page.inner_text("#shell-local"),
              page.inner_text("#shell-local"))
        check("「退出程序」按钮在壳里可见", page.is_visible("#btn-quit"))
        check("控制台没有横向溢出",
              page.evaluate("() => document.documentElement.scrollWidth <= window.innerWidth + 1"))
        shot(page, "console-light")

        print("控制台 · 消息页 + 桥推来的新消息")
        push(page, MSGS[0])
        page.wait_for_timeout(300)
        page.click('.nav-item[data-page="messages"]')
        page.wait_for_timeout(500)
        check("host.message 进了消息列表", "下来吃饭了" in page.inner_text("#log"))
        shot(page, "console-messages-light")
        ctx.close()

        # ══ 10. 控制台（壳里，暗色）══════════════════════════════
        print("控制台 · 壳模式（暗色）")
        ctx, page, _ws = open_shell(browser, "console", dark=True, tag="console-dark")
        page.wait_for_selector("#page-home:not([hidden])", timeout=8000)
        page.wait_for_timeout(600)
        shot(page, "console-dark")
        ctx.close()

        # ══ 11. 从弹窗切回控制台（不刷新页面）═════════════════════
        print("弹窗 → 控制台（web.switch_mode → host.mode）")
        ctx, page, _ws = open_shell(browser, "popup", tag="switch")
        wait_popup(page)
        push(page, MSGS[1])
        page.wait_for_timeout(500)
        page.click("#popup-console")
        page.wait_for_selector("#page-home:not([hidden])", timeout=8000)
        page.wait_for_timeout(500)
        check("点了「打开控制台」：发 web.switch_mode",
              "web.switch_mode" in sent_types(page), str(sent_types(page)))
        check("宿主回 host.mode=console 后弹窗收起、控制台出现",
              not page.is_visible("#shell-popup") and page.is_visible("#page-home"))
        shot(page, "console-after-switch-light")

        # ══ 12. 启动加载态（宿主迟迟不回应 hello）═════════════════
        print("启动加载态")
        ctx, page, _ws = open_shell(browser, "console", tag="boot-state", extra="&hello=never")
        page.wait_for_timeout(400)
        check("宿主没回应时先显示中性加载态（不是空白也不是控制台）",
              page.is_visible("#shell-boot") and page.inner_text("#shell-boot-hint") != "")
        shot(page, "boot-state-light")
        ctx.close()

        ctx, page, _ws = open_shell(browser, "console", dark=True, tag="boot-state-dark", extra="&hello=never")
        page.wait_for_timeout(400)
        shot(page, "boot-state-dark")
        ctx.close()

        # ══ 13. 壳的本地兜底页（file://，不经服务端）═══════════════
        print("本地页 · boot.html / offline.html")
        for scheme in ("light", "dark"):
            ctx = browser.new_context(viewport={"width": 900, "height": 620}, locale="zh-CN",
                                      color_scheme=scheme)
            page = ctx.new_page()
            page.add_init_script(INIT_FAKE_BRIDGE)
            wire(page, f"boot-{scheme}")
            page.goto(f"file://{SHELL_DIR}/boot.html?hint=正在连接服务端…", wait_until="load")
            page.wait_for_timeout(200)
            shot(page, f"boot-page-{scheme}")
            ctx.close()

        for scheme in ("light", "dark"):
            ctx = browser.new_context(viewport={"width": 900, "height": 700}, locale="zh-CN",
                                      color_scheme=scheme)
            page = ctx.new_page()
            page.add_init_script(INIT_FAKE_BRIDGE)
            wire(page, f"offline-{scheme}")
            page.goto(f"file://{SHELL_DIR}/offline.html?server=http://192.168.31.50:18801"
                      f"&token=family-2026&reason=连接 http://192.168.31.50:18801 超时（3 秒无响应）",
                      wait_until="load")
            page.wait_for_timeout(200)
            if scheme == "light":
                check("兜底页不依赖服务端资源（无外链请求）",
                      page.eval_on_selector_all("link,script[src],img[src]",
                                                "els => els.filter(e => !(e.getAttribute('href')||'').startsWith('data:')).length") == 0)
                page.click("#save")
                page.wait_for_timeout(200)
                srv = page.evaluate("() => window.__shellSent.filter(m => m.type === 'web.set_server')")
                check("「保存并重试」发 web.set_server（url + enroll_token）",
                      srv and srv[0].get("url") == "http://192.168.31.50:18801"
                      and srv[0].get("enroll_token") == "family-2026", str(srv))
                page.click("#quit")
                page.wait_for_timeout(150)
                check("「退出」发 web.quit",
                      "web.quit" in page.evaluate("() => window.__shellSentTypes()"))
                check("点过之后「保存并重试」进入禁用态（防连点）", page.is_disabled("#save"))
                check("保存后提示「正在重新连接…」",
                      "重新连接" in page.inner_text("#hint"), page.inner_text("#hint"))
            shot(page, f"offline-page-{scheme}")
            ctx.close()

        # 没带参数时的兜底页（第一次运行看到的画面）
        ctx = browser.new_context(viewport={"width": 900, "height": 700}, locale="zh-CN")
        page = ctx.new_page()
        page.add_init_script(INIT_FAKE_BRIDGE)
        wire(page, "offline-empty")
        page.goto(f"file://{SHELL_DIR}/offline.html", wait_until="load")
        page.wait_for_timeout(200)
        check("兜底页没参数时提示「还没有配置服务端地址」",
              "还没有配置" in page.inner_text("#reason-text"), page.inner_text("#reason-text"))
        shot(page, "offline-empty-light")
        ctx.close()

        # ══ 14. 回归：浏览器模式一行都没变 ════════════════════════
        print("回归 · 浏览器模式")
        ctx = browser.new_context(viewport={"width": 1440, "height": 900}, locale="zh-CN")
        page = ctx.new_page()
        wire(page, "browser")
        ws_urls = []
        page.on("websocket", lambda ws: ws_urls.append(ws.url))
        page.goto(BASE, wait_until="networkidle")
        page.wait_for_timeout(800)
        check("浏览器模式：控制台正常渲染", page.is_visible("#page-home"))
        check("浏览器模式：连接状态来自 /ws/web",
              any("/ws/web" in u for u in ws_urls), str(ws_urls))
        check("浏览器模式：加载态不可见", not page.is_visible("#shell-boot"))
        check("浏览器模式：弹窗视图不可见", not page.is_visible("#shell-popup"))
        check("浏览器模式：没有「退出程序」按钮", not page.is_visible("#btn-quit"))
        check("浏览器模式：没有本机会话徽标", not page.is_visible("#shell-local"))
        check("浏览器模式：没有壳标记",
              page.evaluate("() => !document.documentElement.hasAttribute('data-shell')"))
        shot(page, "browser-unchanged-light")
        ctx.close()

        browser.close()

    # ── 汇总 ─────────────────────────────────────────────────────
    print("\n──────── 自检汇总 ────────")
    failed = [r for r in results if not r[1]]
    print(f"检查项 {len(results)} 个：通过 {len(results) - len(failed)}，失败 {len(failed)}")
    for name, _, extra in failed:
        print(f"  FAIL {name} {extra}")

    real_errors = [e for e in errors if "favicon" not in e]
    if real_errors:
        print(f"\n控制台/页面错误 {len(real_errors)} 条：")
        for e in real_errors[:20]:
            print("  " + e)
    else:
        print("控制台/页面错误：无")

    if bad_status:
        print(f"\nHTTP ≥400 的请求 {len(bad_status)} 条：")
        for b in bad_status[:20]:
            print("  " + b)

    print(f"\n截图 {len(made)} 张：")
    for p in made:
        print(f"  {p}")


if __name__ == "__main__":
    main()
