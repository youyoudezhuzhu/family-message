#!/usr/bin/env python3
"""PC 端**本地界面**验证：假桥 + Playwright 抓图 + 断言。

验证对象不是「看着像」而是可证的事实：
  · 起始视图由 host.hello.mode 决定（不是 URL 参数）
  · 页面只依赖 shell/ 目录里的本地文件（所有请求同源、无 NAS 网页）
  · 群聊气泡由 web/static/chat.js 的 FMChat 渲染，自己的靠右、别人的靠左
  · 气泡颜色 = tokens.css 的 --fluent-color-* 令牌（没有自算颜色）
  · 三个视图的交互都发出正确的桥消息（web.open_settings / web.reply /
    web.save_config / web.close / web.ack）
  · 控制台零报错

页面从 tools/pack_shell.py 组装出来的目录加载（http://127.0.0.1:<随机端口>），
不依赖任何正在运行的 NAS 服务。

用法：python3 tools/shot_pc.py [输出目录]
      默认写到 docs/（pc-*.png），失败时退出码 1。
"""
from __future__ import annotations

import functools
import http.server
import json
import sys
import threading
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import pack_shell  # noqa: E402

from playwright.sync_api import sync_playwright  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
OUT = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "docs"
OUT.mkdir(parents=True, exist_ok=True)

FAILS: list[str] = []
CHECKS = 0
MADE: list[Path] = []


def check(ok, label, extra=""):
    global CHECKS
    CHECKS += 1
    if ok:
        print(f"  ✓ {label}")
    else:
        print(f"  ✗ {label}" + (f"   ← {extra}" if extra else ""))
        FAILS.append(label)
    return ok


# ── 假桥：伪装成 WebView2 宿主 ────────────────────────────────────────
# 页面加载前注入。页面发 web.ready 时回 host.hello（形态取配置里的 mode），
# 再补 history / connection / session / runtime；页面动作按协议回执。
BRIDGE = r"""
(() => {
  const CFG = __CFG__;
  const sent = [];
  const listeners = [];

  window.chrome = Object.assign(window.chrome || {}, {
    webview: {
      postMessage(o) {
        let m = o;
        if (typeof o === 'string') { try { m = JSON.parse(o); } catch (_) { return; } }
        sent.push(m); onSent(m);
      },
      addEventListener(t, cb) { if (t === 'message') listeners.push(cb); },
      removeEventListener(t, cb) { const i = listeners.indexOf(cb); if (i >= 0) listeners.splice(i, 1); },
    },
  });

  function dispatch(obj) { listeners.forEach((cb) => cb({ data: JSON.stringify(obj) })); }
  window.__sent = () => sent;
  window.__types = () => sent.map((m) => m.type);
  window.__last = (t) => { for (let i = sent.length - 1; i >= 0; i--) if (sent[i].type === t) return sent[i]; return null; };
  window.__count = (t) => sent.filter((m) => m.type === t).length;
  window.__dispatch = dispatch;

  function onSent(msg) {
    if (!msg || !msg.type) return;
    switch (msg.type) {
      case 'web.ready':
        dispatch(Object.assign({ type: 'host.hello' }, CFG.hello));
        if (CFG.history) dispatch({ type: 'host.history', messages: CFG.history });
        dispatch({ type: 'host.connection', connected: CFG.connected !== false, detail: CFG.conn_detail || '已连接' });
        dispatch({ type: 'host.session', windows_state: 'active', can_unlock: false, can_screenshot: true, can_shutdown: true });
        if (CFG.runtime) dispatch(Object.assign({ type: 'host.runtime' }, CFG.runtime));
        break;

      case 'web.open_settings':
        dispatch({ type: 'host.mode', mode: msg.open === false ? (CFG.back_mode || 'client') : 'settings' });
        break;

      case 'web.save_config':
        dispatch({ type: 'host.config_saved', ok: true,
                   detail: msg.server_url ? '服务端地址已保存，正在重连' : '设置已保存' });
        break;

      case 'web.ack':
        window.__ack = {
          message_id: msg.message_id,
          leadInDom: !!document.querySelector('#popup-list .chat-row--lead'),
          leadIsLast: (() => {
            const rows = document.querySelectorAll('#popup-list .chat-row--lead');
            return rows.length === 1 && rows[0] === document.querySelector('#popup-list .chat-row:last-child');
          })(),
        };
        break;

      case 'web.close': window.__closed = true; break;
      case 'web.quit': window.__quit = true; break;
    }
  }

  window.__pushMessage = (m) => dispatch({ type: 'host.message', message: m });
  window.__setMode = (mode) => dispatch({ type: 'host.mode', mode });
})();
"""


def msg(mid, who, text, when):
    return {"id": mid, "message_id": mid, "sender_name": who, "content": text,
            "created_at": when, "device_id": "pc_dev", "status": "sent"}


CONVO = [
    msg(101, "妈妈", "今晚几点回来？我把菜先洗上了", "18:42"),
    msg(102, "爸爸", "七点半左右，路上有点堵", "18:43"),
    msg(103, "妈妈", "路过超市带袋米，小袋的就行", "18:44"),
    msg(104, "朵朵", "爸爸我今天数学考了 96 分！", "18:47"),
    msg(105, "妈妈", "家里酱油也没了，一起买", "18:48"),
]

SHOT_DARK = {"mode": "popup", "hello": {"mode": "popup", "version": "cs-0.13.0", "platform": "Windows 10.0.26100",
             "server": "http://192.168.31.50:18801", "theme_mode": "dark", "device_id": "pc_shufang",
             "device_name": "书房电脑", "reply_names": ["爸爸", "妈妈", "朵朵"], "reply_name": "爸爸",
             "enroll_configured": True, "autostart": True}}


# ── 环境 ─────────────────────────────────────────────────────────────
def serve(directory: Path):
    class Quiet(http.server.SimpleHTTPRequestHandler):
        def log_message(self, format, *args):   # noqa: A002 - 覆盖父类签名
            pass

    handler = functools.partial(Quiet, directory=str(directory))
    httpd = http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler)
    threading.Thread(target=httpd.serve_forever, daemon=True).start()
    return httpd, f"http://127.0.0.1:{httpd.server_address[1]}"


class Case:
    """一个用例：配置 + 页面句柄 + 控制台/请求记录。"""

    def __init__(self, browser, base, cfg, width=1180, height=780, name=""):
        self.name = name
        self.errors: list[str] = []
        self.requests: list[str] = []
        ctx = browser.new_context(viewport={"width": width, "height": height}, device_scale_factor=2)
        ctx.add_init_script(BRIDGE.replace("__CFG__", json.dumps(cfg, ensure_ascii=False)))
        self.page = ctx.new_page()
        self.page.set_default_timeout(8000)
        self.page.on("console", self._on_console)
        self.page.on("pageerror", lambda e: self.errors.append(f"pageerror: {e}"))
        self.page.on("request", lambda r: self.requests.append(r.url))
        self.base = base
        self.page.goto(f"{base}/app.html")

    def _on_console(self, m):
        if m.type == "error":
            self.errors.append(f"console.error: {m.text}")

    def wait(self, ms=350):
        self.page.wait_for_timeout(ms)
        return self

    def view(self):
        return self.page.get_attribute("html", "data-view")

    def shot(self, filename):
        path = OUT / filename
        self.page.screenshot(path=str(path))
        MADE.append(path)
        print(f"  → {path}")
        return path

    def close(self):
        self.page.context.close()


# ── 断言集 ───────────────────────────────────────────────────────────
def assert_common(c: Case, cfg, label=""):
    print(f"\n[{label}] 通用")
    check(c.page.evaluate("window.__types()[0]") == "web.ready",
          "页面第一帧就发了 web.ready", str(c.page.evaluate("window.__types()")))
    check(c.view() == cfg["hello"]["mode"],
          f"起始视图 = host.hello.mode（{cfg['hello']['mode']}）", str(c.view()))

    # 没有控制台/侧边栏 —— 旧做法（加载 NAS 网页再 CSS 隐藏）会在这里挂掉
    leftovers = c.page.evaluate("""() => {
        const sel = ['nav', 'aside', '.app-shell', '.nav', '.nav-item', '#sidebar', '.titlebar'];
        const hit = {};
        sel.forEach((s) => { const n = document.querySelectorAll(s).length; if (n) hit[s] = n; });
        return hit;
    }""")
    check(not leftovers, "页面里没有任何控制台/侧边栏残留", json.dumps(leftovers))
    check(c.page.evaluate("document.querySelectorAll('.view').length") == 3,
          "三个视图容器都在（client / popup / settings）")

    # 只加载本地文件：所有请求同源，且没有绝对外链
    outside = [u for u in c.requests if not u.startswith(c.base)]
    check(not outside, "所有网络请求都来自本地 shell 目录（没加载 NAS 网页）", str(outside[:3]))
    refs = c.page.evaluate("""() => Array.from(document.querySelectorAll('link[href],script[src]'))
        .map((e) => e.getAttribute('href') || e.getAttribute('src'))""")
    check(all("://" not in r for r in refs), "引用的样式/脚本都是相对路径", str(refs))

    # tokens.css 真的生效 + 气泡颜色就是令牌值
    tok = c.page.evaluate("""() => {
        const cs = getComputedStyle(document.documentElement);
        const probe = document.createElement('div');
        probe.style.background = cs.getPropertyValue('--fluent-color-primary-fill').trim();
        document.body.appendChild(probe);
        const expected = getComputedStyle(probe).backgroundColor;
        probe.remove();
        const bubble = document.querySelector('#client-list .chat-row--out .chat-bubble');
        return { token: cs.getPropertyValue('--fluent-color-primary-fill').trim(), expected,
                 radius: cs.getPropertyValue('--radius-large').trim(),
                 actual: bubble ? getComputedStyle(bubble).backgroundColor : '' };
    }""")
    check(bool(tok["token"]), "tokens.css 已加载（--fluent-color-primary-fill 可读到）", json.dumps(tok))
    if tok["actual"]:
        check(tok["actual"] == tok["expected"],
              "自己的气泡底色 = 令牌 --fluent-color-primary-fill（没有自算颜色）", json.dumps(tok))

    # 群聊气泡结构 = chat.js 生成的结构
    shape = c.page.evaluate("""() => {
        const row = document.querySelector('#client-list .chat-row');
        if (!row) return null;
        return {
          isAttr: row.tagName === 'ARTICLE',
          avatar: !!row.querySelector(':scope > .chat-avatar'),
          main: !!row.querySelector(':scope > .chat-main'),
          meta: !!row.querySelector('.chat-main > .chat-meta'),
          name: !!row.querySelector('.chat-meta > .chat-name'),
          time: !!row.querySelector('.chat-meta > .chat-time'),
          bubble: !!row.querySelector('.chat-main > .chat-bubble'),
          listClass: document.querySelector('#client-list').className,
          fmClass: window.FMChat && window.FMChat.className,
        };
    }""")
    check(shape and all([shape["isAttr"], shape["avatar"], shape["main"], shape["meta"],
                         shape["name"], shape["time"], shape["bubble"]]),
          "群聊行结构与 chat.js 的 row() 一致", json.dumps(shape, ensure_ascii=False))
    check(shape and shape["listClass"] == "chat-list" and shape["fmClass"] == "chat-list",
          "消息容器用的是 FMChat 的 chat-list", json.dumps(shape, ensure_ascii=False) if shape else "")

    check(not c.errors, "控制台零报错", "; ".join(c.errors[:3]))


def assert_sides(c: Case, cfg, expected_msgs):
    """别人发的靠左、自己发的靠右 —— 只认昵称。"""
    info = c.page.evaluate("""() => Array.from(document.querySelectorAll('#client-list .chat-row')).map((r) => ({
        out: r.classList.contains('chat-row--out'),
        name: r.querySelector('.chat-name').textContent,
        dir: getComputedStyle(r).flexDirection,
    }))""")
    me = cfg["hello"]["reply_name"]
    want_out = [m["sender_name"] == me for m in expected_msgs]
    check([r["out"] for r in info] == want_out,
          f"左右分阵营正确（我自己 = {me}）", json.dumps(info, ensure_ascii=False))
    check(all(r["dir"] == ("row-reverse" if r["out"] else "row") for r in info),
          "靠右的行确实翻转了排布（row-reverse）", json.dumps([r["dir"] for r in info]))
    check(all(r["name"] == me for r in info if r["out"]),
          "靠右的每一条署名都是我", json.dumps([r["name"] for r in info if r["out"]], ensure_ascii=False))
    check(c.page.evaluate("document.querySelectorAll('#client-list .chat-row').length") == len(expected_msgs),
          f"消息条数正确（{len(expected_msgs)}）")


# ── 用例 ─────────────────────────────────────────────────────────────
def case_client_light(browser, base):
    print("\n════ 用例 1：client · 有历史消息 · 浅色 ════")
    cfg = {"mode": "client", "history": CONVO,
           "hello": {"mode": "client", "version": "cs-0.13.0", "platform": "Windows 10.0.26100",
                     "server": "http://192.168.31.50:18801", "theme_mode": "light",
                     "device_id": "pc_shufang", "device_name": "书房电脑",
                     "reply_names": ["爸爸", "妈妈", "朵朵"], "reply_name": "爸爸",
                     "enroll_configured": True, "autostart": True}}
    c = Case(browser, base, cfg, name="client-light").wait()
    assert_common(c, cfg, "client-light")
    assert_sides(c, cfg, CONVO)

    print("\n[client-light] 界面要素")
    check(c.page.inner_text("#client-conn-text") == "已连接", "顶栏显示连接状态", c.page.inner_text("#client-conn-text"))
    check(c.page.inner_text("#client-device") == "· 书房电脑", "顶栏显示本机名", c.page.inner_text("#client-device"))
    check(c.page.inner_text("#client-ver") == "cs-0.13.0", "顶栏显示版本号", c.page.inner_text("#client-ver"))
    check(c.page.get_attribute("html", "data-mode") == "light", "浅色主题生效")
    check(not c.page.is_hidden("#client-list"), "消息列表可见")
    check(c.page.is_hidden("#client-empty"), "有消息时不显示空态")
    vis = c.page.evaluate("""() => {
        const stage = document.querySelector('#client-stage');
        const rows = document.querySelectorAll('#client-list .chat-row');
        const last = rows[rows.length - 1].getBoundingClientRect();
        const box = stage.getBoundingClientRect();
        return { scrollable: stage.scrollHeight > stage.clientHeight + 2, scrollTop: stage.scrollTop,
                 fullyVisible: last.top >= box.top - 2 && last.bottom <= box.bottom + 2 };
    }""")
    check(vis["fullyVisible"] and (not vis["scrollable"] or vis["scrollTop"] > 0),
          "最新一条在可视区里（需要滚动时已滚到底）", json.dumps(vis))

    print("\n[client-light] 交互：点设置按钮 → web.open_settings（形态由宿主切）")
    c.page.click("#btn-open-settings")
    c.wait(250)
    check(c.page.evaluate("window.__last('web.open_settings')") is not None,
          "发出了 web.open_settings", str(c.page.evaluate("window.__types()")))
    check(c.view() == "settings", "宿主回 host.mode=settings 后切到设置视图（不刷新页面）", str(c.view()))
    check(c.page.evaluate("document.querySelectorAll('.chat-row').length") > 0,
          "切视图没有重建页面（消息还在 DOM 里）")
    print("\n[client-light] 交互：返回")
    c.page.click("#settings-back")
    c.wait(250)
    check(c.view() == "client", "返回客户端视图", str(c.view()))
    check(c.page.evaluate("window.__last('web.open_settings').open") is False,
          "返回时告知宿主（web.open_settings{open:false}）")

    print("\n[client-light] 交互：回复 → web.reply → host.reply_ack")
    c.page.fill("#client-text", "好，我下班顺路买")
    c.page.click("#client-send")
    c.wait(150)
    rep = c.page.evaluate("window.__last('web.reply')")
    check(rep is not None, "发出了 web.reply")
    check(rep and rep["content"] == "好，我下班顺路买", "web.reply 带上内容", json.dumps(rep, ensure_ascii=False))
    check(rep and rep["sender_name"] == "爸爸", "web.reply 带上昵称", json.dumps(rep, ensure_ascii=False))
    check(rep and len(str(rep.get("client_id", ""))) >= 8, "web.reply 带上 client_id", json.dumps(rep, ensure_ascii=False))
    check(c.page.inner_text("#client-hint") == "正在发送…", "发送中提示", c.page.inner_text("#client-hint"))
    c.page.evaluate("""(id) => window.__dispatch({type:'host.reply_ack', client_id:id, status:'ok', message_id:9001, detail:''})""",
                    rep["client_id"])
    c.wait(120)
    check(c.page.inner_text("#client-hint") == "已回复", "收到回执后提示「已回复」", c.page.inner_text("#client-hint"))
    check(c.page.input_value("#client-text") == "", "回执到了就清空输入框")
    check(c.errors == [], "交互后控制台仍零报错", "; ".join(c.errors[:3]))

    c.page.click("#client-text")
    c.page.fill("#client-text", "")
    c.shot("pc-client-light.png")
    c.close()


def case_client_dark(browser, base):
    print("\n════ 用例 2：client · 有历史消息 · 深色 ════")
    cfg = {"mode": "client", "history": CONVO,
           "hello": {"mode": "client", "version": "cs-0.13.0", "platform": "Windows 10.0.26100",
                     "server": "http://192.168.31.50:18801", "theme_mode": "dark",
                     "device_id": "pc_shufang", "device_name": "书房电脑",
                     "reply_names": ["爸爸", "妈妈"], "reply_name": "爸爸",
                     "enroll_configured": False, "autostart": False}}
    c = Case(browser, base, cfg, name="client-dark").wait()
    assert_common(c, cfg, "client-dark")
    assert_sides(c, cfg, CONVO)
    check(c.page.get_attribute("html", "data-mode") == "dark", "深色主题生效")
    # 深色下对比度：正文与它的气泡底色必须不同，且不是同一档灰
    contrast = c.page.evaluate("""() => {
        const out = getComputedStyle(document.querySelector('.chat-row--out .chat-bubble'));
        const inc = getComputedStyle(document.querySelector('.chat-row:not(.chat-row--out) .chat-bubble'));
        const bg = getComputedStyle(document.documentElement).getPropertyValue('--fluent-color-background').trim();
        return { outColor: out.color, outBg: out.backgroundColor, incColor: inc.color, incBg: inc.backgroundColor, bg };
    }""")
    check(contrast["outColor"] != contrast["outBg"] and contrast["incColor"] != contrast["incBg"],
          "深色下文字与气泡底色可区分", json.dumps(contrast))
    check(c.page.inner_text("#client-conn-text") == "已连接", "深色下顶栏状态正常")
    c.shot("pc-client-dark.png")
    c.close()


def case_client_empty(browser, base):
    print("\n════ 用例 3：client · 空会话 ════")
    cfg = {"mode": "client", "history": [],
           "conn_detail": "连接断开，重连中…", "connected": False,
           "hello": {"mode": "client", "version": "cs-0.13.0", "platform": "Windows 10.0.26100",
                     "server": "http://192.168.31.50:18801", "theme_mode": "light",
                     "device_id": "pc_shufang", "device_name": "书房电脑",
                     "reply_names": ["爸爸"], "reply_name": "爸爸",
                     "enroll_configured": True, "autostart": False}}
    c = Case(browser, base, cfg, name="client-empty").wait()
    check(c.page.evaluate("window.__types()[0]") == "web.ready", "先发 web.ready")
    check(c.view() == "client", "起始视图 = client")
    check(c.page.evaluate("document.querySelectorAll('#client-list .chat-row').length") == 0, "列表为空")
    check(not c.page.is_hidden("#client-empty"), "显示空态")
    check(c.page.inner_text("#client-conn-text") == "连接断开，重连中…", "断线时顶栏照常显示状态（界面不白屏）")
    check(c.page.evaluate("document.querySelector('#client-conn-dot').className") == "presence presence--offline",
          "状态点是离线的样子")
    check(not c.errors, "控制台零报错（空会话 + 断线）", "; ".join(c.errors[:3]))
    c.shot("pc-client-empty-light.png")
    c.close()


def case_popup_single(browser, base):
    print("\n════ 用例 4：popup · 单条消息（深色）════")
    cfg = {"mode": "popup", "history": [CONVO[3]], **{k: v for k, v in SHOT_DARK.items() if k != "mode"}}
    cfg["hello"]["mode"] = "popup"
    c = Case(browser, base, cfg, name="popup-single").wait(500)
    assert_common(c, cfg, "popup-single")

    print("\n[popup-single] 强提醒形态")
    check(c.page.is_hidden("#v-client") and not c.page.is_hidden("#v-popup"),
          "只显示 popup 视图（client 是 hidden，不是叠着）")
    lead = c.page.evaluate("""() => {
        const rows = Array.from(document.querySelectorAll('#popup-list .chat-row'));
        const l = document.querySelector('#popup-list .chat-row--lead');
        if (!l) return null;
        const b = getComputedStyle(l.querySelector('.chat-bubble'));
        return { rows: rows.length, prev: rows.filter((r) => r.classList.contains('chat-row--prev')).length,
                 leadIdx: rows.indexOf(l), fontSize: parseFloat(b.fontSize), weight: b.fontWeight,
                 text: l.querySelector('.chat-bubble').textContent, color: b.color, bg: b.backgroundColor };
    }""")
    check(lead is not None, "有主条（.chat-row--lead）", json.dumps(lead, ensure_ascii=False))
    check(lead and lead["fontSize"] >= 26, "主条字号够大（>=26px）", json.dumps(lead, ensure_ascii=False))
    check(lead and lead["weight"] in ("600", "bold"), "主条是加粗", str(lead["weight"]) if lead else "")
    check(lead and lead["text"] == CONVO[3]["content"], "主条就是最新那条", str(lead["text"])[:31] if lead else "")
    check(c.page.is_hidden("#popup-empty"), "有消息时不显示空态")

    print("\n[popup-single] web.ack：消息真的上屏了才回")
    ack = c.page.evaluate("window.__ack || null")
    check(ack is not None, "收到了 web.ack", json.dumps(ack))
    check(ack and ack["message_id"] == CONVO[3]["id"], "web.ack 带正确的 message_id", json.dumps(ack))
    check(ack and ack["leadInDom"] and ack["leadIsLast"], "送 ack 时主条已在 DOM 里且是最后一行", json.dumps(ack))
    check(c.page.evaluate("window.__count('web.ack')") == 1, "同一条消息只 ack 一次")

    print("\n[popup-single] 交互：知道了 → web.close")
    c.page.click("#popup-ok")
    c.wait(120)
    check(c.page.evaluate("window.__closed === true") and c.page.evaluate("window.__last('web.close')") is not None,
          "点了知道了就发 web.close")

    print("\n[popup-single] 交互：回复 → web.reply")
    c.page.fill("#popup-text", "收到，马上到家")
    c.page.click("#popup-send")
    c.wait(150)
    rep = c.page.evaluate("window.__last('web.reply')")
    check(rep and rep["content"] == "收到，马上到家", "弹窗里也能回复", json.dumps(rep, ensure_ascii=False))
    c.page.evaluate("""(id) => window.__dispatch({type:'host.reply_ack', client_id:id, status:'ok', message_id:9002, detail:''})""",
                    rep["client_id"])
    c.wait(120)
    check(c.page.inner_text("#popup-hint") == "已回复", "弹窗回复也有回执提示", c.page.inner_text("#popup-hint"))
    check(c.errors == [], "交互后控制台仍零报错", "; ".join(c.errors[:3]))

    c.page.fill("#popup-text", "")
    c.page.evaluate("document.querySelector('#popup-hint').textContent = ''")   # 取「刚收到消息」的干净样子
    c.shot("pc-popup-single-dark.png")
    c.close()


def case_popup_history(browser, base):
    print("\n════ 用例 5：popup · 多条消息（历史弱化）════")
    cfg = {"mode": "popup", "history": CONVO,
           "hello": {"mode": "popup", "version": "cs-0.13.0", "platform": "Windows 10.0.26100",
                     "server": "http://192.168.31.50:18801", "theme_mode": "light",
                     "device_id": "pc_shufang", "device_name": "书房电脑",
                     "reply_names": ["爸爸", "妈妈", "朵朵"], "reply_name": "爸爸",
                     "enroll_configured": True, "autostart": True}}
    c = Case(browser, base, cfg, name="popup-history").wait(500)
    assert_common(c, cfg, "popup-history")
    m = c.page.evaluate("""() => {
        const rows = Array.from(document.querySelectorAll('#popup-list .chat-row'));
        const l = document.querySelector('#popup-list .chat-row--lead');
        const p = document.querySelector('#popup-list .chat-row--prev');
        const lb = getComputedStyle(l.querySelector('.chat-bubble'));
        const pb = getComputedStyle(p.querySelector('.chat-bubble'));
        return { rows: rows.length, prev: document.querySelectorAll('#popup-list .chat-row--prev').length,
                 leadLast: rows[rows.length - 1] === l,
                 leadSize: parseFloat(lb.fontSize), prevSize: parseFloat(pb.fontSize),
                 prevOpacity: getComputedStyle(p).opacity,
                 prevNoBorder: pb.borderStyle + '/' + pb.backgroundColor,
                 prevLines: pb.whiteSpace, leadShadow: lb.boxShadow };
    }""")
    check(m["prev"] >= 1 and m["leadLast"], "历史在上、主条在最后", json.dumps(m, ensure_ascii=False))
    check(m["leadSize"] > m["prevSize"] * 1.8, "主条明显大于历史（强提醒形态）", json.dumps(m, ensure_ascii=False))
    check(float(m["prevOpacity"]) < 1, "历史被弱化（压暗）", json.dumps(m, ensure_ascii=False))
    check("nowrap" in m["prevLines"], "历史是单行字幕式", json.dumps(m, ensure_ascii=False))
    check(m["rows"] == min(len(CONVO), 6), f"popup 里最多留 6 条（实际 {m['rows']}）")
    check(c.page.evaluate("window.__ack ? window.__ack.message_id : null") == CONVO[-1]["id"], "ack 的是最新那条")
    check(c.errors == [], "控制台零报错", "; ".join(c.errors[:3]))
    c.shot("pc-popup-history-light.png")
    c.close()


def case_settings(browser, base):
    print("\n════ 用例 6：settings · 已配置（浅色）════")
    cfg = {"mode": "settings", "history": CONVO,
           "runtime": {"version": "cs-0.13.0", "runtime": "153.0.4234.48", "platform": "Windows 10.0.26100",
                       "device_id": "pc_shufang", "device_name": "书房电脑",
                       "server": "http://192.168.31.50:18801", "enroll_configured": True, "autostart": True,
                       "theme_mode": "light",
                       "log_path": r"C:\Users\<用户名>\AppData\Roaming\FamilyAgent\app.log",
                       "config_path": r"C:\Users\<用户名>\AppData\Roaming\FamilyAgent\config.json"},
           "hello": {"mode": "settings", "version": "cs-0.13.0", "platform": "Windows 10.0.26100",
                     "server": "http://192.168.31.50:18801", "theme_mode": "light",
                     "device_id": "pc_shufang", "device_name": "书房电脑",
                     "reply_names": ["爸爸", "妈妈", "朵朵"], "reply_name": "爸爸",
                     "enroll_configured": True, "autostart": True}}
    c = Case(browser, base, cfg, width=1180, height=1300, name="settings").wait()
    check(c.view() == "settings", "起始视图 = settings（由 hello.mode 决定）")
    check(c.page.evaluate("window.__types()[0]") == "web.ready", "先发 web.ready")

    print("\n[settings] 表单内容")
    f = c.page.evaluate("""() => ({
        server: document.querySelector('#set-server').value,
        tokenPlaceholder: document.querySelector('#set-token').placeholder,
        tokenState: document.querySelector('#set-token-state').textContent,
        tokenStateCls: document.querySelector('#set-token-state').className,
        name: document.querySelector('#set-reply-name').value,
        datalist: Array.from(document.querySelectorAll('#set-reply-names option')).map((o) => o.value),
        autostart: document.querySelector('#set-autostart').checked,
        theme: document.querySelector('#set-theme').value,
    })""")
    check(f["server"] == "http://192.168.31.50:18801", "服务端地址已填", json.dumps(f, ensure_ascii=False))
    check(f["tokenState"] == "已配置" and "is-ok" in f["tokenStateCls"], "注册口令状态=已配置", json.dumps(f, ensure_ascii=False))
    check("已配置" in f["tokenPlaceholder"], "口令框提示「留空表示不修改」", f["tokenPlaceholder"])
    check(f["name"] == "爸爸", "回复昵称已填", f["name"])
    check(f["datalist"] == ["爸爸", "妈妈", "朵朵"], "昵称候选来自 host.hello.reply_names", json.dumps(f["datalist"], ensure_ascii=False))
    check(f["autostart"] is True, "开机自启=开", str(f["autostart"]))
    check(f["theme"] == "light", "主题=浅色", f["theme"])

    info = c.page.evaluate("""() => ({
        version: document.querySelector('#info-version').textContent,
        runtime: document.querySelector('#info-runtime').textContent,
        device: document.querySelector('#info-device').textContent,
        id: document.querySelector('#info-device-id').textContent,
        server: document.querySelector('#info-server').textContent,
        mode: document.querySelector('#info-mode').textContent,
        log: document.querySelector('#info-log').textContent,
        cfg: document.querySelector('#info-config').textContent,
    })""")
    check(info["version"] == "cs-0.13.0", "显示版本号", json.dumps(info, ensure_ascii=False))
    check(info["mode"] == "本机设置", "显示当前形态", info["mode"])
    check(info["runtime"] == "153.0.4234.48", "显示 WebView2 运行版本", info["runtime"])
    check(info["id"] == "pc_shufang" and info["device"] == "书房电脑", "显示本机 ID/名称", json.dumps(info, ensure_ascii=False))
    check("app.log" in info["log"] and "config.json" in info["cfg"], "显示日志与配置路径", json.dumps(info, ensure_ascii=False))

    print("\n[settings] 交互：改配置 → web.save_config")
    c.page.fill("#set-server", "192.168.31.60:18801")
    c.page.uncheck("#set-autostart")
    c.page.select_option("#set-theme", "dark")
    c.page.click("#settings-save")
    c.wait(200)
    sv = c.page.evaluate("window.__last('web.save_config')")
    check(sv is not None, "发出了 web.save_config", str(c.page.evaluate("window.__types()")))
    check(sv and sv["server_url"] == "http://192.168.31.60:18801", "地址补上 http:// 后发出去", json.dumps(sv, ensure_ascii=False))
    check(sv and sv["autostart"] is False, "开机自启的变化也带上", json.dumps(sv, ensure_ascii=False))
    check(sv and sv["theme_mode"] == "dark", "主题的变化也带上", json.dumps(sv, ensure_ascii=False))
    check("enroll_token" not in sv, "口令没改就不发（不会把空口令盖掉）", json.dumps(sv, ensure_ascii=False))
    check(c.page.get_attribute("html", "data-mode") == "dark", "主题立刻生效", str(c.page.get_attribute("html", "data-mode")))
    check("正在重连" in c.page.inner_text("#settings-hint"), "显示宿主回执", c.page.inner_text("#settings-hint"))

    print("\n[settings] 交互：退出程序 → web.quit")
    c.page.click("#btn-quit")
    c.wait(120)
    check(c.page.evaluate("window.__quit === true"), "发了 web.quit")
    check(c.errors == [], "控制台零报错", "; ".join(c.errors[:3]))

    # 截图前恢复成「已配置」的样子（表单最完整的状态）
    c.page.evaluate("""() => window.__dispatch({type:'host.runtime', theme_mode:'light',
        autostart:true, server:'http://192.168.31.50:18801', enroll_configured:true})""")
    c.wait(150)
    c.page.evaluate("document.querySelector('#settings-hint').textContent = ''")
    # Playwright 点按钮时会自动把元素滚进视口 —— 截图前滚回顶部，整页都在画面里
    c.page.evaluate("document.querySelector('.stage--settings').scrollTop = 0")
    check(c.page.evaluate("document.querySelector('.stage--settings').scrollTop") == 0,
          "设置页已回到顶部（截图能看到完整表单）")
    c.wait(150)
    c.shot("pc-settings-light.png")
    c.close()


def case_settings_unconfigured(browser, base):
    print("\n════ 用例 7：settings · 未配置（深色）════")
    cfg = {"mode": "settings",
           "hello": {"mode": "settings", "version": "cs-0.13.0", "platform": "Windows 10.0.26100",
                     "server": "", "theme_mode": "dark", "device_id": "pc_unknown",
                     "device_name": "这台电脑", "reply_names": [], "reply_name": "",
                     "enroll_configured": False, "autostart": False}}
    c = Case(browser, base, cfg, width=1180, height=1300, name="settings-unconfigured").wait()
    f = c.page.evaluate("""() => ({
        server: document.querySelector('#set-server').value,
        token: document.querySelector('#set-token').value,
        tokenPlaceholder: document.querySelector('#set-token').placeholder,
        tokenState: document.querySelector('#set-token-state').textContent,
        tokenStateCls: document.querySelector('#set-token-state').className,
        name: document.querySelector('#set-reply-name').value,
        autostart: document.querySelector('#set-autostart').checked,
        senders: Array.from(document.querySelectorAll('#client-sender option')).map((o) => o.value),
    })""")
    check(f["server"] == "", "没配置时地址是空的")
    check(f["tokenState"] == "未配置" and "is-warn" in f["tokenStateCls"], "口令状态=未配置（用警示色）", json.dumps(f, ensure_ascii=False))
    check(f["autostart"] is False, "开机自启=关")
    check(f["senders"] and f["senders"][0] != "", "昵称表为空时回复栏仍有兜底项", json.dumps(f, ensure_ascii=False))
    check(c.page.get_attribute("html", "data-mode") == "dark", "深色主题生效")
    check(c.page.evaluate("document.querySelector('#info-server').textContent") == "—",
          "未配置的服务端显示为「—」而不是空白")
    check(c.errors == [], "控制台零报错", "; ".join(c.errors[:3]))
    c.shot("pc-settings-unconfigured-dark.png")
    c.close()


def case_runtime_push(browser, base):
    print("\n════ 用例 8：运行中收到 host.mode / host.message / host.runtime ════")
    cfg = {"mode": "client", "history": CONVO[:2],
           "hello": {"mode": "client", "version": "cs-0.13.0", "platform": "Windows 10.0.26100",
                     "server": "http://192.168.31.50:18801", "theme_mode": "light",
                     "device_id": "pc_shufang", "device_name": "书房电脑",
                     "reply_names": ["爸爸", "妈妈"], "reply_name": "爸爸",
                     "enroll_configured": True, "autostart": True}}
    c = Case(browser, base, cfg, name="runtime").wait()
    # 新消息 → 出现在客户端列表末尾（自己刚回的靠右）
    c.page.evaluate("""() => window.__pushMessage({id:777, message_id:777, sender_name:'爸爸',
        content:'我出发了', created_at:'19:02', device_id:'pc_shufang', status:'sent'})""")
    c.wait(150)
    tail = c.page.evaluate("""() => {
        const rows = document.querySelectorAll('#client-list .chat-row');
        const last = rows[rows.length - 1];
        return { n: rows.length, out: last.classList.contains('chat-row--out'),
                 text: last.querySelector('.chat-bubble').textContent };
    }""")
    check(tail["n"] == 3 and tail["out"] and tail["text"] == "我出发了",
          "运行中来的新消息追加在末尾且按昵称分阵营", json.dumps(tail, ensure_ascii=False))

    # 宿主强切 popup → 该条变成主条并 ack
    c.page.evaluate("window.__setMode('popup')")
    c.wait(250)
    check(c.view() == "popup", "host.mode 切换形态（不重新导航）", str(c.view()))
    check(c.page.evaluate("window.__ack && window.__ack.message_id") == 777,
          "切到强提醒后对已显示的消息补 ack")
    c.page.evaluate("window.__setMode('client')")
    c.wait(150)
    check(c.view() == "client", "再切回客户端", str(c.view()))

    # host.runtime 主动推 → 设置页信息更新
    c.page.evaluate("""() => window.__dispatch({type:'host.runtime', version:'cs-0.13.1',
        runtime:'154.0.1', device_name:'书房电脑', server:'http://192.168.31.50:18801',
        enroll_configured:true, autostart:true, theme_mode:'dark',
        log_path:'C:\\\\log\\\\app.log', config_path:'C:\\\\cfg\\\\config.json'})""")
    c.wait(200)
    check(c.page.get_attribute("html", "data-mode") == "dark", "host.runtime 里的主题也生效")
    c.page.evaluate("window.__setMode('settings')")
    c.wait(250)
    check(c.page.inner_text("#info-version") == "cs-0.13.1", "host.runtime 推来的版本更新了设置页",
          c.page.inner_text("#info-version"))
    check(c.page.inner_text("#info-runtime") == "154.0.1", "WebView2 运行版本更新", c.page.inner_text("#info-runtime"))
    check(c.page.inner_text("#info-log").endswith("app.log"), "日志路径更新", c.page.inner_text("#info-log"))
    check(c.errors == [], "控制台零报错", "; ".join(c.errors[:3]))
    c.close()


def main():
    shell_dir = pack_shell.pack(quiet=True)
    print(f"组装目录：{shell_dir}")
    httpd, base = serve(shell_dir)
    print(f"本地服务：{base}\n")

    try:
        with sync_playwright() as p:
            browser = p.chromium.launch()
            try:
                case_client_light(browser, base)
                case_client_dark(browser, base)
                case_client_empty(browser, base)
                case_popup_single(browser, base)
                case_popup_history(browser, base)
                case_settings(browser, base)
                case_settings_unconfigured(browser, base)
                case_runtime_push(browser, base)
            finally:
                browser.close()
    finally:
        httpd.shutdown()

    print("\n────────────────────────────────────────────")
    print(f"断言 {CHECKS} 项，失败 {len(FAILS)} 项")
    for f in FAILS:
        print("  ✗ " + f)
    print(f"截图 {len(MADE)} 张：")
    for p in MADE:
        print(f"  {p}")
    if FAILS:
        return 1
    print("\n全部通过：本地界面自带三视图、只走本地文件与宿主桥、气泡复用 chat.js、颜色取自 tokens.css。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
