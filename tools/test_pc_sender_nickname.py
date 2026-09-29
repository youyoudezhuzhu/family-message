#!/usr/bin/env python3
"""PC 端本地界面（WebView2 壳）· 昵称相关交互的行为回归（三形态全覆盖）。

覆盖三个真机 bug（都在 web/shell/pc.js）：

  B1  popup 形态选昵称没反应 —— 两个回复栏（#client-sender / #popup-sender）共用
      同一个 change handler，而老代码**写死读 client 那条**，于是在全屏弹窗里选了
      新昵称，发出去的却是 client 下拉里停着的旧身份（宿主的选用状态当然不变）。
  B2  选昵称没有本地乐观写回 —— 老路径有 `sel.value = v`、昵称路径没有；
      宿主不推 host.nickname（离线 / 不应答）时界面「纹丝不动」。
  B3  改名「永远挂着」—— 宿主回的 host.nickname_result(accepted) 只是「请求被接受了」，
      老页面却在收到它时就把 8s 看门狗 clearTimeout 了，而成功一路没有任何文案落地 →
      提示永远停在「已请求把该昵称改名为「…」（全局生效），等待服务端应答…」。
      撞名（409 NAME_TAKEN）时更糟：设置页那行在弹窗 / 客户端形态下是隐藏的，
      用户**什么都看不到**。

外加查清「重新分配颜色」（web.nickname_reassign_color）在 PC 侧三个形态的实际行为：
入口只在设置页；client / popup 形态只有「服务端广播 → 宿主推 host.nickname → 重画」。

判据全是**可证的事实**（页面真的发出去的帧 + DOM 真的画出来的值），不是「看着像」：
  T1  三形态：从**本形态自己的**下拉选昵称 → 帧里的 nickname_id 必须是新值
  T2  宿主不回推时：两个下拉 + FM_PC.state.nick.current 也必须已经跟手（乐观写回）
  T3  乐观值不作数：宿主随后推来的 host.nickname 仍然是权威值（界面跟着回去）
  T4  旧路径（昵称开关关闭）：popup 形态选名字同样要同步两个下拉 + S.myName
  T5  换色：三形态（设置页入口发出的帧 / 服务端广播后设置页与气泡有没有重画 /
      换色不改变「我的名义」）
  T6  改名 pending 必须终结：成功（广播整份状态）/ 失败（409 NAME_TAKEN）/
      服务端一直不答（看门狗兜底）三种收尾，且失败文案在**当前形态可见的那一行**也出现
  T7  新建 / 换色 / 删除的中间态同样必须终结
  全程控制台零报错

只读地组装 shell/（tools/pack_shell.py），不起 NAS 服务、不碰 18801。

用法：python3 tools/test_pc_sender_nickname.py [截图输出目录]
      （截图默认写到 /vol1/@appdata/hermes-agent/tmp/fm-pc-nick-shots）
退出码 0 = 全通过，1 = 有断言失败。
"""
from __future__ import annotations

import functools
import http.server
import json
import sys
import threading
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import pack_shell  # noqa: E402
from playwright.sync_api import sync_playwright  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_OUT = Path("/vol1/@appdata/hermes-agent/tmp/fm-pc-nick-shots")

SHELL = pack_shell.pack(tempfile.mkdtemp(prefix="fm-shell-nicksel-"), quiet=True)

# 假宿主桥：把页面发出去的帧全记下来，并按 CFG 回推 host.* 帧。
# push_select=false 用来模拟「宿主不推 host.nickname」（离线 / 不应答）——
# 这正是 B2 的复现条件。
BRIDGE = r"""
(() => {
  const CFG = __CFG__;
  const sent = [];
  const listeners = [];
  window.chrome = Object.assign(window.chrome || {}, { webview: {
    postMessage(o) { let m = o; if (typeof o === 'string') { try { m = JSON.parse(o); } catch (_) { return; } } sent.push(m); onSent(m); },
    addEventListener(t, cb) { if (t === 'message') listeners.push(cb); },
    removeEventListener(t, cb) { const i = listeners.indexOf(cb); if (i >= 0) listeners.splice(i, 1); },
  }});
  function dispatch(obj) { listeners.forEach((cb) => cb({ data: JSON.stringify(obj) })); }
  window.__sent = () => sent;
  window.__last = (t) => { for (let i = sent.length - 1; i >= 0; i--) if (sent[i].type === t) return sent[i]; return null; };
  window.__count = (t) => sent.filter((m) => m.type === t).length;
  window.__dispatch = dispatch;
  window.__nick = JSON.parse(JSON.stringify(CFG.nick || null));
  function pushNick() { if (window.__nick) dispatch(Object.assign({ type: 'host.nickname' }, window.__nick)); }
  window.__pushNick = pushNick;
  function onSent(msg) {
    if (!msg || !msg.type) return;
    switch (msg.type) {
      case 'web.ready':
        dispatch(Object.assign({ type: 'host.hello' }, CFG.hello));
        if (CFG.history) dispatch({ type: 'host.history', messages: CFG.history });
        dispatch({ type: 'host.connection', connected: true, detail: '已连接' });
        pushNick();
        break;
      case 'web.open_settings':
        dispatch({ type: 'host.mode', mode: msg.open === false ? (CFG.back_mode || 'client') : 'settings' });
        break;
      case 'web.ack': break;
      case 'web.close': window.__closed = true; break;
      case 'web.nickname_select': {
        if (CFG.push_select === false) break;      // 宿主不回推（B2 的复现条件）
        const id = (msg.nickname_id === null || msg.nickname_id === undefined) ? null : Number(msg.nickname_id);
        const row = id === null ? null : (window.__nick.nicknames || []).find((n) => Number(n.nickname_id) === id);
        window.__nick.current = row
          ? { nickname_id: row.nickname_id, display_name: row.display_name, color: row.color, is_local_temp: false }
          : { nickname_id: null, display_name: window.__nick.default_name, color: 'gray', is_local_temp: true };
        dispatch({ type: 'host.nickname_result', action: 'select', accepted: true, offline: false, detail: '已选用「' + (row ? row.display_name : '本地临时') + '」（只影响这台电脑）' });
        pushNick();
        break;
      }
      case 'web.nickname_reassign_color':
        window.__reassign = msg;                   // 真改色要等服务端广播
        dispatch({ type: 'host.nickname_result', action: 'reassign_color', accepted: true, offline: false, detail: '已请求换一个颜色（全局生效），等待服务端应答…' });
        if (CFG.recolor === 'ok') setTimeout(recolorN, 120);
        break;
      case 'web.nickname_rename': {
        window.__rename = msg;
        dispatch({ type: 'host.nickname_result', action: 'rename', accepted: true, offline: false,
                   detail: '已请求把该昵称改名为「' + msg.display_name + '」（全局生效），等待服务端应答…' });
        if (CFG.rename === 'ok') setTimeout(() => renameN(msg), 120);
        else if (CFG.rename === 'taken') setTimeout(() => dispatch({
          type: 'host.nickname_error', request: 'nickname_rename_request', code: 'NAME_TAKEN',
          message: '「' + msg.display_name + '」已被占用，请换个名字或直接选用它' }), 120);
        /* 'silent' = 宿主拿到帧但服务端/广播一直没回（模拟真机丢帧） */
        break;
      }
      case 'web.nickname_create': {
        window.__create = msg;
        dispatch({ type: 'host.nickname_result', action: 'create', accepted: true, offline: false,
                   detail: '已请求新建「' + msg.display_name + '」，等待服务端应答…' });
        if (CFG.create === 'ok') setTimeout(() => createN(msg), 120);
        else if (CFG.create === 'taken') setTimeout(() => dispatch({
          type: 'host.nickname_error', request: 'nickname_create_request', code: 'NICKNAME_ALREADY_EXISTS',
          message: '同名昵称已存在', existing_nickname_id: 2 }), 120);
        break;
      }
      case 'web.nickname_delete': {
        window.__delete = msg;
        dispatch({ type: 'host.nickname_result', action: 'delete', accepted: true, offline: false,
                   detail: '已请求删除该昵称（全局生效），等待服务端应答…' });
        if (CFG.delete === 'ok') setTimeout(() => deleteN(msg), 120);
        break;
      }
      case 'web.nickname_refresh':
        dispatch({ type: 'host.nickname_result', action: 'refresh', accepted: true, offline: false, detail: '正在向服务端刷新昵称列表…' });
        pushNick();
        break;
    }
  }
  function rows() { return window.__nick.nicknames || []; }
  function syncCur() {
    const r = rows().find((n) => Number(n.nickname_id) === Number(window.__nick.current && window.__nick.current.nickname_id));
    if (r) window.__nick.current.display_name = r.display_name;
  }
  function renameN(msg) {
    const row = rows().find((n) => Number(n.nickname_id) === Number(msg.nickname_id));
    if (row) row.display_name = msg.display_name;
    syncCur(); window.__pushNick();
  }
  function createN(msg) {
    rows().push({ nickname_id: 7, display_name: msg.display_name, color: 'color_05', status: 'active',
                  created_at: '2026-09-29 10:00:00', updated_at: '2026-09-29 10:00:00' });
    window.__pushNick();
  }
  function deleteN(msg) {
    window.__nick.nicknames = rows().filter((n) => Number(n.nickname_id) !== Number(msg.nickname_id));
    window.__pushNick();
  }
  function recolorN() {
    const row = rows().find((n) => n.color === 'color_13');
    if (row) row.color = 'color_14';
    syncCur(); window.__pushNick();
  }
})();
"""


def nick(nid, name, color):
    return {"nickname_id": nid, "display_name": name, "color": color, "status": "active",
            "created_at": "2026-09-01 10:00:00", "updated_at": "2026-09-29 10:00:00"}


def nick_state(current_id=2, available=True):
    rows = [nick(1, "爸爸", "color_03"), nick(2, "妈妈", "color_13"), nick(3, "朵朵", "color_06")]
    cur = next((r for r in rows if r["nickname_id"] == current_id), None)
    return {
        "available": available, "online": True, "can_manage": True,
        "offline_reason": "当前未连接服务器，昵称管理不可用",
        "default_name": "书房电脑", "local_temp_color": "gray", "max_active": 16, "pool_version": 1,
        "current": ({"nickname_id": cur["nickname_id"], "display_name": cur["display_name"],
                     "color": cur["color"], "is_local_temp": False} if (cur and available) else
                    {"nickname_id": None, "display_name": "书房电脑", "color": "gray", "is_local_temp": True}),
        "nicknames": rows if available else [], "notice": "",
    }


CONVO = [{"id": 101, "message_id": 101, "sender_name": "妈妈", "content": "今晚几点回来？",
          "created_at": "18:42", "status": "sent"},
         {"id": 102, "message_id": 102, "sender_name": "爸爸", "content": "七点半左右",
          "created_at": "18:43", "status": "sent"}]

FAILS: list[str] = []


def check(name: str, ok: bool, got):
    mark = "PASS" if ok else "FAIL"
    if not ok:
        FAILS.append(name)
    print(f"    [{mark}] {name} → {json.dumps(got, ensure_ascii=False)}")


def serve(d: Path):
    class Q(http.server.SimpleHTTPRequestHandler):
        def log_message(self, *a):
            pass

    h = functools.partial(Q, directory=str(d))
    srv = http.server.ThreadingHTTPServer(("127.0.0.1", 0), h)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    return srv, f"http://127.0.0.1:{srv.server_address[1]}"


def hint(pg, sel):
    """读一行提示：文案 + class + 是否可见（offsetParent 为 null = 隐藏）。"""
    return pg.evaluate("""(s) => { const el = document.querySelector(s);
        if (!el) return null;
        return { text: el.textContent, cls: el.className, visible: !!el.offsetParent }; }""", sel)


def open_page(b, base, mode, *, push_select=True, available=True, out=None, shot="", **extra):
    hello = {"mode": mode, "version": "0.17.1", "platform": "Windows 10.0.26100",
             "server": "http://192.168.1.50:18801", "theme_mode": "light",
             "device_id": "pc_shufang", "device_name": "书房电脑",
             "reply_names": ["爸爸", "妈妈", "朵朵"], "reply_name": "妈妈",
             "enroll_configured": True, "autostart": True}
    cfg = {"mode": mode, "hello": hello, "history": CONVO,
           "nick": nick_state(2, available), "push_select": push_select}
    cfg.update(extra)
    ctx = b.new_context(viewport={"width": 1180, "height": 780}, device_scale_factor=2)
    ctx.add_init_script(BRIDGE.replace("__CFG__", json.dumps(cfg, ensure_ascii=False)))
    pg = ctx.new_page()
    errs = []
    pg.on("console", lambda m: errs.append(m.text) if m.type == "error" else None)
    pg.on("pageerror", lambda e: errs.append(str(e)))
    pg.goto(f"{base}/app.html")
    pg.wait_for_timeout(400)
    return ctx, pg, errs


def snapshot(pg, mode):
    return pg.evaluate("""(mode) => ({
      view: document.documentElement.getAttribute('data-view'),
      clientVal: (document.getElementById('client-sender')||{}).value,
      popupVal: (document.getElementById('popup-sender')||{}).value,
      cur: JSON.parse(JSON.stringify(window.FM_PC.state.nick.current || {})),
      myName: window.FM_PC.state.myName,
      avatars: Array.from(document.querySelectorAll('#' + mode + '-list .chat-avatar')).map(a => getComputedStyle(a).backgroundColor),
    })""", mode)


def shot_region(pg, mode, path):
    """截「形态里的昵称区」：client/popup = 回复栏；settings = 昵称卡。

    目标不可见时（例如在 popup 形态下截设置页的昵称卡）**跳过元素截图** ——
    对隐藏元素调 screenshot() 会 30s 超时并把整轮跑挂掉。
    """
    if mode == "settings":
        el = pg.query_selector("#nick-card")
    else:
        el = pg.query_selector(f"#{mode}-sender")
        if el:
            el = el.evaluate_handle("e => e.closest('.reply')").as_element()
    if el and el.is_visible():
        el.screenshot(path=str(path))


def case_select(b, base, mode, out, push_select=True, available=True, pick="1", expected_state=1):
    label = f"{mode}（宿主{'回推' if push_select else '不回推'} · 昵称{'开' if available else '关'}）"
    ctx, pg, errs = open_page(b, base, mode, push_select=push_select, available=available)
    try:
        before = snapshot(pg, mode)
        print(f"  初始：{json.dumps(before, ensure_ascii=False)}")
        pg.select_option(f"#{mode}-sender", pick)
        pg.wait_for_timeout(250)
        after = snapshot(pg, mode)
        frame = pg.evaluate("() => window.__last('web.nickname_select')")
        if available:
            print(f"  帧：{json.dumps(frame, ensure_ascii=False)}")
            check(f"{label} 发出的帧带的是新值（nickname_id={pick}）",
                  bool(frame) and frame.get("nickname_id") == int(pick), frame)
            check(f"{label} 两个下拉都跟到新值", after["clientVal"] == pick and after["popupVal"] == pick,
                  {"client": after["clientVal"], "popup": after["popupVal"]})
            check(f"{label} 本地状态 current.nickname_id == {pick}（无需宿主回推）",
                  (after["cur"] or {}).get("nickname_id") == expected_state, after["cur"])
            check(f"{label} 当前名义的显示名跟着换",
                  after["myName"] == {"1": "爸爸", "2": "妈妈", "3": "朵朵", "": "书房电脑"}[pick],
                  after["myName"])
        else:
            check(f"{label} 旧路径不发昵称帧", frame is None, frame)
            check(f"{label} 旧路径同步两个下拉 + S.myName",
                  after["clientVal"] == "爸爸" and after["popupVal"] == "爸爸" and after["myName"] == "爸爸",
                  {"client": after["clientVal"], "popup": after["popupVal"], "myName": after["myName"]})
        shot_region(pg, mode, out / f"pc-nick-{mode}{'' if push_select else '-nopush'}-after-select.png")
        pg.screenshot(path=str(out / f"pc-nick-{mode}{'' if push_select else '-nopush'}-page.png"))
        check(f"{label} 控制台零报错", not errs, errs[:3])

        if available and push_select is False:
            # T3：宿主随后推来的权威值必须仍然说了算
            pg.evaluate("""() => { const n = window.__nick;
                n.current = { nickname_id: 2, display_name: '妈妈', color: 'color_13', is_local_temp: false };
                window.__pushNick(); }""")
            pg.wait_for_timeout(200)
            back = snapshot(pg, mode)
            check(f"{label} 宿主权威值回推后界面跟着回去（client/popup 都 = 2）",
                  back["clientVal"] == "2" and back["popupVal"] == "2"
                  and (back["cur"] or {}).get("nickname_id") == 2, back)
    finally:
        ctx.close()


def case_recolor(b, base, mode, out):
    """bug ① 的 PC 侧：换色入口只在设置页；client / popup 只靠广播重画。

    假宿主按真机链路回帧：回执（accepted）→ 服务端广播（CFG.recolor='ok' → 换色 + 推整份状态）。
    """
    ctx, pg, errs = open_page(b, base, mode, recolor="ok")
    try:
        if mode == "settings":
            print("  昵称行：", json.dumps(pg.evaluate("""() => Array.from(document.querySelectorAll('#nick-list .nick-row')).map(r => ({
                name: r.querySelector('input').value,
                swatch: getComputedStyle(r.querySelector('.nick-row__swatch')).backgroundColor,
                colorBtn: (r.querySelectorAll('button')[1]||{}).id }))"""), ensure_ascii=False))
            swatches = pg.evaluate("""() => ({ swatch: getComputedStyle(document.querySelectorAll('#nick-list .nick-row')[1].querySelector('.nick-row__swatch')).backgroundColor,
                dot: getComputedStyle(document.getElementById('nick-cur-dot')).backgroundColor })""")
            pg.click("#nick-color-2")
            pg.wait_for_timeout(60)
            frame = pg.evaluate("() => window.__last('web.nickname_reassign_color')")
            mid = hint(pg, "#settings-nick-hint")
            print(f"  点「换色」发出的帧：{json.dumps(frame, ensure_ascii=False)}")
            print(f"  中间态提示：{json.dumps(mid, ensure_ascii=False)}")
            check("settings 点「换色」发出 reassign_color 且带正确的 nickname_id",
                  bool(frame) and frame.get("nickname_id") == 2, frame)
            check("换色先给出「等待服务端应答」的中间态",
                  "等待服务端应答" in mid["text"], mid)
            pg.wait_for_timeout(400)
            done = hint(pg, "#settings-nick-hint")
            print(f"  广播回来后提示：{json.dumps(done, ensure_ascii=False)}")
            check("换色成功后中间态被终结为「已换一个新颜色」",
                  "已换一个新颜色" in done["text"] and "等待服务端应答" not in done["text"], done)
            after = pg.evaluate("""() => ({ swatch: getComputedStyle(document.querySelectorAll('#nick-list .nick-row')[1].querySelector('.nick-row__swatch')).backgroundColor,
                dot: getComputedStyle(document.getElementById('nick-cur-dot')).backgroundColor })""")
            print(f"  广播新色（color_13 → color_14）：{json.dumps(swatches, ensure_ascii=False)} → {json.dumps(after, ensure_ascii=False)}")
            check("settings 广播后重画（色点 + 当前名义点都换色）",
                  after["swatch"] != swatches["swatch"] and after["dot"] == after["swatch"], after)
            # 设置页的「选用」按钮（不经过回复栏）也要发新值
            pg.click("#nick-use-1")
            pg.wait_for_timeout(200)
            print(f"  点「选用」发出的帧：{json.dumps(pg.evaluate('() => window.__last(\'web.nickname_select\')'), ensure_ascii=False)}")
            check("settings 点「选用」发出 nickname_id=1",
                  (pg.evaluate("() => window.__last('web.nickname_select')") or {}).get("nickname_id") == 1,
                  pg.evaluate("() => window.__last('web.nickname_select')"))
        else:
            before = snapshot(pg, mode)
            pg.evaluate("""() => { const n = window.__nick.nicknames.find(x => x.nickname_id === 2);
                const old = n.color; n.color = (old === 'color_13' ? 'color_14' : 'color_13'); window.__pushNick(); }""")
            pg.wait_for_timeout(250)
            after = snapshot(pg, mode)
            print(f"  广播换色前后头像色：{json.dumps(before['avatars'], ensure_ascii=False)} → {json.dumps(after['avatars'], ensure_ascii=False)}")
            check(f"{mode} 形态：服务端广播换色后气泡头像跟着重画",
                  bool(before["avatars"]) and before["avatars"] != after["avatars"], after["avatars"])
            check(f"{mode} 形态：换色不改变「我的名义」下拉",
                  after["clientVal"] == before["clientVal"] and after["popupVal"] == before["popupVal"],
                  {"client": after["clientVal"], "popup": after["popupVal"]})
        shot_region(pg, mode, out / f"pc-nick-{mode}-after-recolor.png")
        check(f"{mode} 换色流程控制台零报错", not errs, errs[:3])
    finally:
        ctx.close()


def nick_row_input(pg, idx):
    return f"#nick-list .nick-row:nth-child({idx}) input"


def rename_to(pg, idx, name):
    sel = nick_row_input(pg, idx)
    pg.evaluate("""(a) => { const el = document.querySelector(a.sel);
        el.value = a.name;
        el.dispatchEvent(new Event('change', { bubbles: true })); }""", {"sel": sel, "name": name})


def case_rename_pending(b, base, out, kind, *, in_view="settings"):
    """改名 pending 状态机：成功 / 撞名失败 / 服务端一直不答，三种都必须**终结**。"""
    label = {"ok": "服务端广播改名成功", "taken": "服务端回 409 NAME_TAKEN",
             "silent": "服务端一直不答（看门狗兜底）"}[kind]
    print(f"\n== T6 改名 pending 必须终结：{label} ==")
    ctx, pg, errs = open_page(b, base, "settings", rename=kind, out=out)
    try:
        before = hint(pg, "#settings-nick-hint")
        rename_to(pg, 2, "用户6")
        pg.wait_for_timeout(60)
        pending = hint(pg, "#settings-nick-hint")
        frame = pg.evaluate("() => window.__last('web.nickname_rename')")
        print(f"  改名帧：{json.dumps(frame, ensure_ascii=False)}")
        print(f"  刚点完的提示：{json.dumps(pending, ensure_ascii=False)}")
        check("改名帧带 nickname_id + 新名字",
              bool(frame) and frame.get("nickname_id") == 2 and frame.get("display_name") == "用户6", frame)
        check("刚点完先给出「已请求…等待服务端应答」的中间态",
              "等待服务端应答" in (pending or {}).get("text", ""), pending)
        check("中间态期间 pending 是挂着的（看门狗已起）",
              pg.evaluate("() => (window.FM_PC.state.nickPending || {}).kind || null") == "rename",
              pg.evaluate("() => window.FM_PC.state.nickPending || null"))

        if in_view != "settings":
            pg.evaluate("(m) => window.__dispatch({ type: 'host.mode', mode: m })", in_view)
            pg.wait_for_timeout(80)

        pg.wait_for_timeout(9000 if kind == "silent" else 500)
        after = hint(pg, "#settings-nick-hint")
        if kind == "ok":
            row_name = pg.evaluate("""() => document.querySelectorAll('#nick-list .nick-row')[1].querySelector('input').value""")
            print(f"  结果提示：{json.dumps(after, ensure_ascii=False)}，行上的名字：{row_name!r}")
            check("成功 → 提示终结并改为「已改名」", "已改名" in after["text"] and "等待服务端应答" not in after["text"], after)
            check("成功的提示是 ok 样式（is-ok）", "is-ok" in after["cls"], after["cls"])
            check("行上的名字已是服务端的真名（用户6）", row_name == "用户6", row_name)
        elif kind == "taken":
            print(f"  结果提示：{json.dumps(after, ensure_ascii=False)}")
            check("撞名失败 → 提示终结并显示服务端原因（已被占用 / NAME_TAKEN）",
                  "已被占用" in after["text"] and "NAME_TAKEN" in after["text"], after)
            check("失败的提示是 error 样式（is-error）", "is-error" in after["cls"], after["cls"])
            check("失败后行上的名字回到服务端的真名（妈妈）",
                  pg.evaluate("""() => document.querySelectorAll('#nick-list .nick-row')[1].querySelector('input').value""") == "妈妈",
                  pg.evaluate("""() => document.querySelectorAll('#nick-list .nick-row')[1].querySelector('input').value"""))
        else:
            print(f"  结果提示：{json.dumps(after, ensure_ascii=False)}")
            check("服务端一直不答 → 看门狗兜底，提示不再停在「等待服务端应答」",
                  "还没收到服务端的应答" in after["text"] and "等待服务端应答" not in after["text"], after)
            check("兜底提示是 error 样式（is-error）", "is-error" in after["cls"], after["cls"])
        check("pending 已终结（state.nickPending 为空 + 看门狗已停）",
              pg.evaluate("() => window.FM_PC.state.nickPending === null && window.FM_PC.state.nickTimer === null"),
              pg.evaluate("() => ({p: window.FM_PC.state.nickPending, t: window.FM_PC.state.nickTimer})"))

        if in_view != "settings":
            other = hint(pg, f"#{in_view}-hint")
            print(f"  当前形态（{in_view}）那一行的提示：{json.dumps(other, ensure_ascii=False)}")
            check(f"失败/结果文案在 {in_view} 形态**可见的那一行**也出现",
                  bool(other) and other["visible"] and (after["text"][:12] in other["text"] or other["text"] == after["text"]), other)

        shot_region(pg, "settings", out / f"pc-nick-rename-{kind}{'' if in_view == 'settings' else '-' + in_view}.png")
        if in_view != "settings":
            pg.screenshot(path=str(out / f"pc-nick-rename-{kind}-in-{in_view}-page.png"))
        check(f"改名（{kind}）控制台零报错", not errs, errs[:3])
    finally:
        ctx.close()


def case_create_delete_progress(b, base, out):
    """顺手复查另外三个操作有没有同样的「中间态挂在屏幕上不走」的毛病。"""
    print("\n== T7 新建 / 换色 / 删除：中间态同样必须终结 ==")
    ctx, pg, errs = open_page(b, base, "settings", rename="ok", recolor="ok", create="ok", delete="ok", out=out)
    try:
        # 新建
        pg.fill("#nick-new-name", "用户7")
        pg.click("#nick-create-btn")
        pg.wait_for_timeout(500)
        h = hint(pg, "#settings-nick-hint")
        print(f"  新建后提示：{json.dumps(h, ensure_ascii=False)}")
        check("新建成功 → 提示终结为「已创建」", "已创建" in h["text"] and "等待服务端应答" not in h["text"], h)
        # 换色
        pg.click("#nick-color-2")
        pg.wait_for_timeout(500)
        h = hint(pg, "#settings-nick-hint")
        print(f"  换色后提示：{json.dumps(h, ensure_ascii=False)}")
        check("换色成功 → 提示终结为「已换一个新颜色」", "已换一个新颜色" in h["text"] and "等待服务端应答" not in h["text"], h)
        # 删除
        pg.click("#nick-del-3")
        pg.wait_for_timeout(500)
        h = hint(pg, "#settings-nick-hint")
        print(f"  删除后提示：{json.dumps(h, ensure_ascii=False)}")
        check("删除成功 → 提示终结为「已删除」", "已删除" in h["text"] and "等待服务端应答" not in h["text"], h)
        check("三个操作控制台零报错", not errs, errs[:3])
    finally:
        ctx.close()


def main():
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_OUT
    out.mkdir(parents=True, exist_ok=True)
    srv, base = serve(SHELL)
    print("SHELL =", SHELL)
    with sync_playwright() as p:
        b = p.chromium.launch()
        for mode in ("client", "popup"):
            print(f"\n== T1 本形态下拉选昵称 / host 回推 · mode={mode} ==")
            case_select(b, base, mode, out)
            print(f"\n== T2/T3 宿主不回推时的本地乐观写回 · mode={mode} ==")
            case_select(b, base, mode, out, push_select=False)
            print(f"\n== T4 旧路径（昵称开关关闭）· mode={mode} ==")
            case_select(b, base, mode, out, available=False, pick="爸爸")
        print("\n== T5 换色（client 形态，只靠广播重画）==")
        case_recolor(b, base, "client", out)
        print("\n== T5 换色（popup 形态，只靠广播重画）==")
        case_recolor(b, base, "popup", out)
        print("\n== T5 换色（settings 形态：入口在这里）==")
        case_recolor(b, base, "settings", out)
        case_rename_pending(b, base, out, "ok")
        case_rename_pending(b, base, out, "taken")
        case_rename_pending(b, base, out, "taken", in_view="popup")
        case_rename_pending(b, base, out, "silent")
        case_create_delete_progress(b, base, out)
        b.close()
    srv.shutdown()
    print("\n截图目录：", out)
    if FAILS:
        print(f"\n✗ 失败 {len(FAILS)} 项：")
        for f in FAILS:
            print("   -", f)
        return 1
    print("\n✓ 全部断言通过")
    return 0


sys.exit(main())
