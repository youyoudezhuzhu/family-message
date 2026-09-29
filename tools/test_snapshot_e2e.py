#!/usr/bin/env python3
"""昵称快照 · **三端端到端**验收：网页端发消息 → 服务端写快照 → 三端上色一致（含重创同名）。

为什么要单独一条 E2E：单测只能证明「库里有值」，证明不了「三条渲染路径拿到的是同一份值」。
三端是：
  ① **Web A**（真浏览器 + 真 WebSocket）—— 选昵称、发消息的发起端
  ② **Web B**（第二个真浏览器上下文，**全程不刷新**）—— 靠 `/ws/web` 广播收到同一条
  ③ **PC 壳页**（`web/shell/app.html` + 假桥）—— 喂**服务端真实发给设备的那一帧**
     （从一条真设备 WS 上抓的，不是手写的 payload）

判据（全部可证）：
  E1 落库快照 = (nickname_id, 该昵称的逻辑色 ID)                ← HTTP 读回
  E2 Web B **不刷新**就收到该消息（DOM 里出现）
  E3 三端对**同一条消息**解析出**同一个逻辑色 ID**                ← 各自页面里的官方解析钩子
     （Web: `window.FMNickResolver.colorIdForMessage`；PC: `window.FM_PC.nickColorIdForMessage`）
  E4 三端**渲染出来的头像底色一致**（网页端与 PC 端共用 `static/chat.js` 的 `.chat-avatar`）
  E5 服务端→设备的那一帧本身带快照字段（与落库一致）              ← 真设备 WS 抓帧
  E6 ★**重创同名**：删「妈妈」→ 重创「妈妈」（新 id / 新色）→
     老消息在 Web B（不刷新）与 PC 端**仍然解析/渲染成旧色**（快照驱动，不是名字反查，§3.4.1）
  E7 控制台零报错（A / B / PC 三处）

⚠ 昵称的「本机当前选用」用**文档化的本地机制**设置（localStorage `fm.lastSender` = nickname_id，
   docs/NICKNAME-SYSTEM-PLAN §5.2 写明它是纯本地状态）；昵称选择器本身的 UI 由 Phase 3 套件覆盖
   （`tools/test_nickname_web.py`）。
⚠ 只起测试实例（默认 18899）；生产 18801 硬拒绝。
用法：python3 tools/test_snapshot_e2e.py [截图输出目录]　退出码 0 = 全过。
"""
from __future__ import annotations

import functools
import http.server as http_server
import json
import os
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SERVER_DIR = ROOT / "server"
PROD_PORT = 18801
OUT = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("/vol1/@appdata/hermes-agent/tmp/fm-snapshot-e2e")
FAILS: list[str] = []

sys.path.insert(0, str(ROOT / "tools"))
import pack_shell  # noqa: E402

SHELL = pack_shell.pack(tempfile.mkdtemp(prefix="fm-e2e-shell-"), quiet=True)

# PC 壳页的假桥：只做「宿主该做的事」—— 把壳要的几帧按顺序喂进去，把页面发出来的帧记下来
PC_BRIDGE = r"""
(() => {
  const listeners = [];
  const sent = [];
  window.chrome = Object.assign(window.chrome || {}, { webview: {
    postMessage(o) { let m = o; if (typeof o === 'string') { try { m = JSON.parse(o); } catch (_) { return; } } sent.push(m); },
    addEventListener(t, cb) { if (t === 'message') listeners.push(cb); },
    removeEventListener(t, cb) { const i = listeners.indexOf(cb); if (i >= 0) listeners.splice(i, 1); },
  }});
  window.__sent = () => sent;
  window.__dispatch = (obj) => listeners.forEach((cb) => cb({ data: JSON.stringify(obj) }));
  window.addEventListener('load', () => {});
  // 等页面 web.ready 再喂（宿主真实行为）；用轮询兜住 ready 早于监听的情况
  const t = setInterval(() => {
    if (sent.some((m) => m.type === 'web.ready')) { clearInterval(t); window.__ready = true; }
  }, 10);
})();
"""


def check(name: str, ok: bool, got) -> None:
    print(f"    [{'PASS' if ok else 'FAIL'}] {name} → {json.dumps(got, ensure_ascii=False)}")
    if not ok:
        FAILS.append(name)


def http(method: str, url: str, body=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(url, data=data, method=method,
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=10) as r:
            return r.status, json.loads(r.read().decode() or "null")
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read().decode() or "null")


def serve_shell(d: Path):
    class Q(http_server.SimpleHTTPRequestHandler):
        def log_message(self, *a):
            pass

    srv = http_server.ThreadingHTTPServer(("127.0.0.1", 0),
                                          functools.partial(Q, directory=str(d)))
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    return srv, f"http://127.0.0.1:{srv.server_address[1]}"


def main() -> int:
    port = int(os.environ.get("FM_TEST_PORT", "18899"))
    assert port != PROD_PORT, "拒绝在生产端口上跑测试"
    OUT.mkdir(parents=True, exist_ok=True)

    tmp = Path(tempfile.mkdtemp(prefix="fm-e2e-"))
    (tmp / "data").mkdir(parents=True, exist_ok=True)
    cfg = tmp / "config.yaml"
    cfg.write_text(
        f"data_dir: {tmp / 'data'}\n"
        f"server: {{host: 127.0.0.1, port: {port}, public_url: ''}}\n"
        "web: {password: '', session_hours: 720}\n"
        "device: {enroll_token: FAMILY-TEST-TOKEN, auto_register: true, offline_after_seconds: 45}\n"
        "message: {popup_auto_close_seconds: 0, max_targets: 20, history_limit: 30}\n"
        "nickname: {enabled: true, color_pool_version: 1}\n",
        encoding="utf-8")

    log = open(tmp / "server.log", "w")
    proc = subprocess.Popen([sys.executable, str(SERVER_DIR / "run.py")], cwd=str(SERVER_DIR),
                            env={**os.environ, "FM_CONFIG": str(cfg)},
                            stdout=log, stderr=subprocess.STDOUT)
    shell_srv, shell_base = serve_shell(SHELL)
    base = f"http://127.0.0.1:{port}"
    try:
        for _ in range(60):
            if proc.poll() is not None:
                print("✗ 实例起不来：", (tmp / "server.log").read_text()[-1500:])
                return 1
            try:
                if http("GET", f"{base}/healthz")[0] == 200:
                    break
            except OSError:
                time.sleep(0.5)
        else:
            print("✗ 等 healthz 超时")
            return 1
        print(f"═══ 昵称快照 · 三端 E2E ═══\n  实例 {base}　壳页 {shell_base}")

        def new_nick(name):
            code, resp = http("POST", f"{base}/api/nicknames", {"display_name": name})
            assert code == 201, f"建昵称失败 {code} {resp}"
            return resp["nickname"]

        def nick_payload():
            """构造 host.nickname 的载荷（与 JsBridge.PostNickname 同形）。"""
            _, d = http("GET", f"{base}/api/nicknames")
            rows = d["nicknames"]
            return {"available": True, "online": True, "can_manage": True,
                    "offline_reason": "当前未连接服务器，昵称管理不可用",
                    "default_name": "浏览器", "local_max": 16, "max_active": 16,
                    "pool_version": 1, "current": None, "nicknames": rows, "notice": ""}

        def msgs():
            _, d = http("GET", f"{base}/api/messages")
            rows = d.get("messages") if isinstance(d, dict) else d
            return {m["content"]: m for m in (rows or [])}

        mom = new_nick("妈妈")
        print(f"  昵称：妈妈 id={mom['nickname_id']} color={mom['color']}")

        from playwright.sync_api import sync_playwright
        import websockets.sync.client as wsc

        # ── 真设备 WS：抓「服务端真正发给设备的那一帧」 ──────────────────
        dev_ws = wsc.connect(f"ws://127.0.0.1:{port}/ws/device/pc_e2e"
                             "?name=%E4%B9%A6%E6%88%BF%E7%94%B5%E8%84%91&type=pc&platform=e2e"
                             "&enroll_token=FAMILY-TEST-TOKEN")
        dev_ws.recv()                                   # hello
        device_frames: list[dict] = []

        with sync_playwright() as p:
            b = p.chromium.launch()
            errs = {"A": [], "B": [], "PC": []}

            def new_web(tag, first=False):
                ctx = b.new_context(viewport={"width": 1280, "height": 900}, device_scale_factor=1)
                if first:
                    ctx.add_init_script(
                        f"try {{ localStorage.setItem('fm.lastSender', '{mom['nickname_id']}'); }} catch (e) {{}}")
                pg = ctx.new_page()
                pg.on("console", lambda m: errs[tag].append(m.text) if m.type == "error" else None)
                pg.on("pageerror", lambda e: errs[tag].append(str(e)))
                pg.goto(f"{base}/")
                pg.wait_for_timeout(1200)
                # 发送区在主页（#page-home）；在别的页上 #content 是 hidden 的，fill 会超时
                try:
                    pg.evaluate("() => go('home')")
                    pg.wait_for_selector("#page-home:not([hidden])", timeout=5000)
                except Exception:                       # noqa: BLE001
                    pass
                pg.wait_for_timeout(200)
                return ctx, pg

            ctxA, A = new_web("A", first=True)
            ctxB, B = new_web("B")

            print("\n== E1/E2. Web A 发消息 → 落库快照 ==")
            A.fill("#content", "E2E 三端消息")
            A.click("#btn-send")
            A.wait_for_timeout(1200)
            row = msgs().get("E2E 三端消息")
            print(f"  落库行：{json.dumps({k: row.get(k) for k in ('sender_name','sender_nickname_id','sender_color')}, ensure_ascii=False) if row else None}")
            check("E1 快照 = (妈妈 id, 妈妈色)",
                  bool(row) and row.get("sender_nickname_id") == mom["nickname_id"]
                  and row.get("sender_color") == mom["color"],
                  [row and row.get("sender_nickname_id"), row and row.get("sender_color")])

            B.wait_for_timeout(1500)
            dom_b = B.evaluate("""() => Array.from(document.querySelectorAll('.chat-bubble, .chat-name'))
                 .map(e => e.textContent).join(' | ')""")
            check("E2 Web B 不刷新就收到（DOM 里出现该消息）", "E2E 三端消息" in dom_b, dom_b[:120])

            # ── 设备侧抓帧（E5）──────────────────────────────────────
            got_frame = None
            dev_ws.socket.settimeout(6)
            try:
                while True:
                    f = json.loads(dev_ws.recv())
                    device_frames.append(f)
                    if f.get("type") == "message" and f.get("content") == "E2E 三端消息":
                        got_frame = f
                        break
            except Exception as e:                       # noqa: BLE001
                print("    （抓设备帧超时/异常：", repr(e), "）")
            print(f"  设备帧快照字段：{json.dumps({k: (got_frame or {}).get(k) for k in ('sender_nickname_id','sender_color')}, ensure_ascii=False)}")
            check("E5 服务端→设备的那一帧带快照（与落库一致）",
                  bool(got_frame) and got_frame.get("sender_nickname_id") == mom["nickname_id"]
                  and got_frame.get("sender_color") == mom["color"],
                  [got_frame and got_frame.get("sender_nickname_id"), got_frame and got_frame.get("sender_color")])
            hist = (got_frame or {}).get("history") or []
            check("E5b 帧里 history 的条目也带快照字段（PC 右侧历史同样按快照上色）",
                  all("sender_color" in h for h in hist) if hist else False,
                  hist[:1])

            # ── 三端解析 + 渲染（E3/E4）──────────────────────────────
            print("\n== E3/E4. 三端解析同一个逻辑色 ID + 渲染底色一致 ==")
            web_ids = {}
            for tag, pg in (("A", A), ("B", B)):
                web_ids[tag] = pg.evaluate(
                    """(m) => { const r = window.FMNickResolver.colorIdForMessage(m);
                        const box = document.querySelector('#home-recent');
                        const el = Array.from(box.querySelectorAll('.chat-avatar')).pop();
                        return { id: r, bg: el ? getComputedStyle(el).backgroundColor : null }; }""", row)
            print(f"  Web A={json.dumps(web_ids['A'], ensure_ascii=False)}  Web B={json.dumps(web_ids['B'], ensure_ascii=False)}")

            pc_ctx = b.new_context(viewport={"width": 1180, "height": 780}, device_scale_factor=1)
            pc_ctx.add_init_script(PC_BRIDGE)
            PC = pc_ctx.new_page()
            PC.on("console", lambda m: errs["PC"].append(m.text) if m.type == "error" else None)
            PC.on("pageerror", lambda e: errs["PC"].append(str(e)))
            PC.goto(f"{shell_base}/app.html")
            PC.wait_for_timeout(500)
            PC.evaluate("""(cfg) => {
                window.__dispatch(Object.assign({ type: 'host.hello' }, cfg.hello));
                window.__dispatch({ type: 'host.connection', connected: true, detail: '已连接' });
                window.__dispatch(Object.assign({ type: 'host.nickname' }, cfg.nick));
                window.__dispatch({ type: 'host.message', message: cfg.msg });
            }""", {"hello": {"mode": "client", "version": "0.17.1", "platform": "e2e",
                             "server": base, "theme_mode": "light", "device_id": "pc_e2e",
                             "device_name": "书房电脑", "reply_names": [], "reply_name": ""},
                   "nick": nick_payload(), "msg": got_frame})
            PC.wait_for_timeout(600)
            pc_out = PC.evaluate("""(m) => { const id = window.FM_PC.nickColorIdForMessage(m);
                const el = Array.from(document.querySelectorAll('#client-list .chat-avatar')).pop();
                return { id: id, bg: el ? getComputedStyle(el).backgroundColor : null }; }""", got_frame)
            print(f"  PC 壳页={json.dumps(pc_out, ensure_ascii=False)}")

            check("E3 三端解析出同一个逻辑色 ID（= 妈妈的颜色）",
                  web_ids["A"]["id"] == web_ids["B"]["id"] == pc_out["id"] == mom["color"],
                  {"A": web_ids["A"]["id"], "B": web_ids["B"]["id"], "PC": pc_out["id"]})
            check("E4 三端渲染头像底色一致（共用 chat.js）",
                  bool(web_ids["A"]["bg"]) and web_ids["A"]["bg"] == web_ids["B"]["bg"] == pc_out["bg"],
                  {"A": web_ids["A"]["bg"], "B": web_ids["B"]["bg"], "PC": pc_out["bg"]})

            A.screenshot(path=str(OUT / "e2e-01-webA.png"), full_page=False)
            B.screenshot(path=str(OUT / "e2e-02-webB.png"), full_page=False)
            PC.screenshot(path=str(OUT / "e2e-03-pc-shell.png"), full_page=False)

            # ── E6 ★ 重创同名：老消息必须还是旧色 ─────────────────────
            print("\n== E6. ★ 重创同名：老消息仍按快照解析成旧色 ==")
            http("DELETE", f"{base}/api/nicknames/{mom['nickname_id']}")
            mom2 = new_nick("妈妈")
            # 颜色是「池里第一个可用」→ 删了立刻重建会**又拿到同一个色**，那样断言不可证。
            # 主动换一次色（reassign 保证新色 ≠ 旧色，且不与任何 active 重复）。
            _, mom2b = http("POST", f"{base}/api/nicknames/{mom2['nickname_id']}/reassign-color")
            mom2 = mom2b["nickname"]
            print(f"  重创：新 id={mom2['nickname_id']} 新 color={mom2['color']}（旧 color={mom['color']}）")
            check("E6 前置：重创后换了色，两行颜色不同（断言才可证）",
                  mom2["color"] != mom["color"], [mom["color"], mom2["color"]])
            B.wait_for_timeout(800)                      # B 不刷新，靠广播拿新表
            old_row = msgs().get("E2E 三端消息")
            check("E6a 老消息在库里没被改写",
                  old_row.get("sender_nickname_id") == mom["nickname_id"]
                  and old_row.get("sender_color") == mom["color"],
                  [old_row.get("sender_nickname_id"), old_row.get("sender_color")])
            after_b = B.evaluate("""(m) => { const id = window.FMNickResolver.colorIdForMessage(m);
                const box = document.querySelector('#home-recent');
                const el = Array.from(box.querySelectorAll('.chat-avatar')).pop();
                return { id: id, bg: el ? getComputedStyle(el).backgroundColor : null }; }""", old_row)
            PC.evaluate("""(cfg) => {
                window.__dispatch(Object.assign({ type: 'host.nickname' }, cfg.nick));
            }""", {"nick": nick_payload()})
            PC.wait_for_timeout(300)
            after_pc = PC.evaluate("""(m) => { const id = window.FM_PC.nickColorIdForMessage(m);
                const el = Array.from(document.querySelectorAll('#client-list .chat-avatar')).pop();
                return { id: id, bg: el ? getComputedStyle(el).backgroundColor : null }; }""", old_row)
            print(f"  重创后 Web B={json.dumps(after_b, ensure_ascii=False)}  PC={json.dumps(after_pc, ensure_ascii=False)}")
            check("E6b Web B（不刷新）老消息仍是旧色，且不是新行的色",
                  after_b["id"] == mom["color"] and after_b["id"] != mom2["color"] and after_b["bg"] == web_ids["B"]["bg"],
                  [after_b["id"], mom2["color"]])
            check("E6c PC 端老消息仍是旧色（快照驱动，不走名字反查）",
                  after_pc["id"] == mom["color"] and after_pc["bg"] == after_b["bg"],
                  [after_pc["id"], mom2["color"]])

            for tag in ("A", "B", "PC"):
                check(f"E7 {tag} 控制台零报错", not errs[tag], errs[tag][:2])

            B.screenshot(path=str(OUT / "e2e-04-after-recreate.png"), full_page=False)
            for c in (ctxA, ctxB, pc_ctx):
                c.close()
            b.close()
        dev_ws.close()
        shell_srv.shutdown()
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            proc.kill()
        log.close()

    print("\n截图目录：", OUT)
    if FAILS:
        print(f"✗ 失败 {len(FAILS)} 项：" + "；".join(FAILS))
        return 1
    print("✓ 三端 E2E 全部通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
