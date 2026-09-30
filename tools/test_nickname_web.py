#!/usr/bin/env python3
"""共享昵称系统 **Phase 3（网页端）** 的真机验收 + 截图。

覆盖 docs/NICKNAME-SYSTEM-PLAN.md §7 Phase 3 的判据：

  A 逻辑色渲染   页面上的圆点 / 头像底 / 头像字色逐行对得上 §4.4 的映射表（浅 / 深主题各一遍）
  B 昵称列表页   空库空态（不假造昵称）/ 添加 / 撞名 409 引导「直接选用它？」/ 换色 / 删除
  C 额度提示     14 条 →「共享昵称颜色即将用尽（14/16）」；16 条 → 满额文案 + 入口置灰（直连仍 503）
  D 5 个广播     三个浏览器上下文同时在线：A 改色 → B / C **不刷新**即变色（记耗时）
  E 当前昵称     本地（localStorage 的 fm.lastSender = nickname_id）；fm.names 不再被读写
  F 新浏览器     灰色临时昵称「默认用户」+ 灰 `gray`，且**也能发消息**
  G 兜底         未知逻辑色 ID → 灰兜底且 style 里不出现非法值；断网 / 开关关闭仍正常
  H 窄屏         375px 手机宽度下昵称卡片不重叠、不溢出

⚠ 只用**测试实例**（18899 / 18897），**生产 18801 绝不被碰**（脚本里硬拒）。

用例隔离约定（每个用例自己造前置状态，绝不依赖前一个用例的残留）：
  ① 起实例前先确认端口没人应答（`Srv.port_is_open`）——残留实例带着上一次的库（可能已满额），
     复用它会让「满额置灰」看起来像产品 bug，其实是串了别人的状态。
  ② 需要空库 / 指定条数的用例，自己用 `reset_library()`（走 DELETE API 清库、归还颜色）造前置；
     `reset_library()` 之后第一条新建必拿 color_01。
  ③ 前置本身也**断言**出来（`X 前置：…`），别让用例悄悄依赖顺序。
  ④ 页面上的 async 函数（`deleteNick` 之类内部 await 确认对话框的）必须用 `ffire()` 调用：
     Playwright 的 evaluate 会 await 返回的 Promise，直接 evaluate = 死锁。
  ⑤ 满额 16/16 时 `#name-new` / `#name-add` 置灰是**产品的正确行为**，测试要迁就它（自备未满额前置），
     不许为它改产品。

⚠ 只改 web/ 下的东西；本脚本不改 server/ 与 pc-agent/。

用法：
    /vol1/@apphome/hermes-agent/data/venv/bin/python tools/test_nickname_web.py
    … --keep            保留临时目录（排查）
    … --no-shots        不截图（只断言）
    … --shots-dir docs  截图归档到 docs/（**默认写本次临时目录**，不再脏仓库工作区）
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import time
import traceback
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any

from playwright.sync_api import sync_playwright

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
SERVER_DIR = ROOT / "server"
DOCS = ROOT / "docs"
PORT_ON, PORT_OFF = 18899, 18897
PROD_PORT = 18801
ENROLL = "FAMILY-TEST-TOKEN"

# 期望值从**服务端常量**取，不在测试里抄一份（v0.19.1 配色从 16 改成 31 时，
# 抄死的那几处就是被这条教训打脸的：色值/数量写死了，改服务端就假红）。
sys.path.insert(0, str(SERVER_DIR))
import nicknames as pool  # noqa: E402


def dot_light(hex_color: str) -> str:
    """§4.4 规则 1 的浅色圆点：基础色混 35% 黑（镜像 web/static/nickcolor.js）。"""
    s = hex_color.lstrip("#")
    r, g, b = (int(s[i:i + 2], 16) for i in (0, 2, 4))
    f = lambda a: round(a * 0.65)  # noqa: E731
    return "#%02X%02X%02X" % (f(r), f(g), f(b))


results: list[tuple[str, bool, str]] = []


def check(name: str, ok: bool, detail: str = "") -> bool:
    results.append((name, ok, detail))
    print(f"  {'✅' if ok else '❌'} {name}" + (f"   {detail}" if detail else ""))
    return ok


def section(title: str) -> None:
    print(f"\n── {title} " + "─" * max(0, 62 - len(title)))


def eq_hex_vs_rgb(want_hex: str, got_rgb: str) -> bool:
    m = re.match(r"rgba?\((\d+),\s*(\d+),\s*(\d+)", got_rgb or "")
    if not m:
        return False
    got = "#%02X%02X%02X" % (int(m.group(1)), int(m.group(2)), int(m.group(3)))
    return got.upper() == want_hex.upper()


# ══════════════════════════════════════════════════════════════════════
# 测试实例（临时目录 + 临时配置；端口硬拒 18801）
# ══════════════════════════════════════════════════════════════════════
class Srv:
    def __init__(self, tmp: Path, port: int, enabled: bool):
        assert port != PROD_PORT, "拒绝使用生产端口 18801"
        self.port, self.enabled = port, enabled
        self.dir = tmp / ("on" if enabled else "off")
        self.data = self.dir / "data"
        self.data.mkdir(parents=True, exist_ok=True)
        self.cfg = self.dir / "config.yaml"
        self.cfg.write_text(
            f"data_dir: {self.data}\n"
            f"server: {{host: 127.0.0.1, port: {port}, public_url: ''}}\n"
            "web: {password: '', session_hours: 720}\n"
            f"device: {{enroll_token: {ENROLL}, auto_register: true, offline_after_seconds: 45}}\n"
            "message: {popup_auto_close_seconds: 0, max_targets: 20, history_limit: 30}\n"
            f"nickname: {{enabled: {'true' if enabled else 'false'}, color_pool_version: 1}}\n",
            encoding="utf-8")
        self.log_path = self.dir / "server.log"
        self.proc: subprocess.Popen | None = None
        self._f = None

    @property
    def base(self) -> str:
        return f"http://127.0.0.1:{self.port}"

    @property
    def db(self) -> Path:
        return self.data / "family.db"

    def port_is_open(self) -> bool:
        """端口上已经有人在应答？→ 说明有**残留的 / 别人的**实例，绝不复用。

        复用会让用例读到上一次跑剩的库（很可能已经满额 16/16 → #name-new 被产品置灰），
        症状看起来像「产品坏了」，其实是**用例串了别人的状态**（本次隔离问题的元凶）。
        """
        import socket
        with socket.socket() as s:
            s.settimeout(0.4)
            return s.connect_ex(("127.0.0.1", self.port)) == 0

    def start(self, timeout: float = 25.0) -> None:
        if self.port_is_open():
            raise RuntimeError(
                f"端口 {self.port} 已被占用：多半是上次跑崩 / 被打断后残留的测试实例。"
                f"它带着上一次的库（可能已满额），复用它 = 用例之间串状态。先 kill 干净再跑。")
        env = dict(os.environ, FM_CONFIG=str(self.cfg), FM_PORT=str(self.port),
                   TRIM_SERVICE_PORT=str(self.port), PYTHONUNBUFFERED="1")
        self._f = open(self.log_path, "wb")
        self.proc = subprocess.Popen([sys.executable, str(SERVER_DIR / "run.py")],
                                     cwd=str(SERVER_DIR), env=env,
                                     stdout=self._f, stderr=subprocess.STDOUT,
                                     start_new_session=True)
        end = time.time() + timeout
        while time.time() < end:
            if self.proc.poll() is not None:
                raise RuntimeError(f"实例秒退 exit={self.proc.returncode}\n{self.read_log()[-900:]}")
            try:
                st, _ = http(self.base, "GET", "/healthz", timeout=2)
                if st == 200:
                    return
            except Exception:
                pass
            time.sleep(0.25)
        raise RuntimeError(f"实例 {timeout}s 没起来\n{self.read_log()[-900:]}")

    def stop(self) -> bool:
        """停实例，返回**端口是否已释放**（实例是 start_new_session 起的，父进程被打断会变孤儿）。"""
        if self.proc and self.proc.poll() is None:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=8)
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait(timeout=5)
        if self._f:
            self._f.close()
            self._f = None
        end = time.time() + 5
        while time.time() < end and self.port_is_open():
            time.sleep(0.2)
        return not self.port_is_open()

    def read_log(self) -> str:
        try:
            return self.log_path.read_text(errors="replace")
        except Exception:
            return ""


def http(base: str, method: str, path: str, body=None,
         timeout: float = 8.0) -> tuple[int, Any]:
    req = urllib.request.Request(base + path, method=method)
    data = None
    if body is not None:
        data = json.dumps(body, ensure_ascii=False).encode()
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, data=data, timeout=timeout) as r:
            raw = r.read().decode() or "null"
            return r.status, json.loads(raw)
    except urllib.error.HTTPError as e:
        raw = e.read().decode() or "null"
        try:
            return e.code, json.loads(raw)
        except Exception:
            return e.code, {"_raw": raw}


# ══════════════════════════════════════════════════════════════════════
# 用例隔离工具（每个用例自己造前置状态，绝不依赖前一个用例的残留）
# ══════════════════════════════════════════════════════════════════════
def ffire(page, expr: str) -> None:
    """执行页面上的 async 函数，但**不等它的 Promise**。

    Playwright 的 evaluate 会 await 返回的 Promise（已实测确认）。
    像 `deleteNick(n)` 这种内部 `await confirmDialog(...)` 的函数，等它就是死锁：
    对话框要等测试点「删除」，而测试在等 evaluate 返回。
    """
    page.evaluate(f"() => {{ void ({expr}); }}")


def active_names(base: str) -> list[dict]:
    """当前活跃昵称（name / id / color），给前置准备与断言共用。"""
    st, body = http(base, "GET", "/api/nicknames?status=active")
    assert st == 200, (st, body)
    return body["nicknames"]


def reset_library(srv: "Srv", page=None, keep: int = 0, wait_ms: int = 400) -> list[str]:
    """把共享昵称库清到只剩 keep 条（默认全清），让用例自己从已知状态起步。

    归还颜色 → 池子回到起点（`pick_first_available` 取到的第一条必是 color_01）。
    """
    names = [n["display_name"] for n in active_names(srv.base)]
    for n in active_names(srv.base)[keep:]:
        st, b = http(srv.base, "DELETE", f"/api/nicknames/{n['nickname_id']}")
        assert st in (200, 204), (st, b)
    if page is not None:
        page.evaluate("() => loadNicknames()")      # 广播之外的兜底整表同步
        page.wait_for_timeout(wait_ms)
    return names


def ensure_add_enabled(srv: "Srv", page, keep: int = 10) -> bool:
    """保证「添加」入口可用（未满额）——满额是产品的正确行为，但用例不该依赖它。"""
    if not page.is_enabled("#name-new"):
        reset_library(srv, page, keep=keep)
    return page.is_enabled("#name-new")


# ══════════════════════════════════════════════════════════════════════
# 页面工具
# ══════════════════════════════════════════════════════════════════════
def ws_recorder(ctx) -> list[dict]:
    """记录这个上下文收到的全部昵称帧（证明 5 个广播都到了浏览器）。"""
    frames: list[dict] = []

    def on_page(page):
        def on_ws(ws):
            def on_frame(payload):
                if isinstance(payload, str) and payload.startswith("{"):
                    try:
                        d = json.loads(payload)
                    except Exception:
                        return
                    if str(d.get("type", "")).startswith("nickname"):
                        frames.append(d)
            ws.on("framereceived", lambda p: on_frame(getattr(p, "payload", p)))
        page.on("websocket", on_ws)

    ctx.on("page", on_page)
    return frames


def open_page(ctx, base: str):
    page = ctx.new_page()
    page.set_default_timeout(12000)
    errs: list[str] = []
    page.on("pageerror", lambda e: errs.append(str(e)))
    page.goto(base + "/", wait_until="load")
    page.wait_for_function("() => !!window.FMNickResolver", timeout=15000)
    page.wait_for_timeout(350)
    return page, errs


def go_settings(page):
    page.evaluate("() => go('settings')")
    page.wait_for_selector("#page-settings:not([hidden])")
    page.wait_for_timeout(120)


def go_home(page):
    # 发送区在主页（#page-home）；在设置页上 #content 是 hidden 的，fill 会超时
    page.evaluate("() => go('home')")
    page.wait_for_selector("#page-home:not([hidden])")
    page.wait_for_timeout(120)


def row_colors(page) -> list[dict]:
    """设置页每一行：逻辑色 ID（data 属性） + 色点实际渲染色 + 名字。"""
    return page.evaluate("""() => Array.from(document.querySelectorAll('#names-list .name-row')).map(r => {
        const sw = r.querySelector('.name-row__swatch');
        const inp = r.querySelector('.name-row__input');
        return {
          id: r.dataset.nickId,
          colorId: sw.dataset.colorId,
          dot: getComputedStyle(sw).backgroundColor,
          name: (inp && inp.value) || '',
          using: r.classList.contains('name-row--using'),
        };
      })""")


def msg_colors(page) -> list[dict]:
    """消息行：头像底 + 头像字色 + 名字（页面上真正渲染出来的颜色）。"""
    return page.evaluate("""() => Array.from(document.querySelectorAll('#home-recent .chat-row')).map(r => {
        const av = r.querySelector('.chat-avatar');
        const nm = r.querySelector('.chat-name');
        return {
          name: nm ? nm.textContent : '',
          bg: getComputedStyle(av).backgroundColor,
          fg: getComputedStyle(av).color,
          letter: av.textContent,
        };
      })""")


def combo_state(page, host_id: str = 'sender-sel') -> dict:
    return page.evaluate("""(id) => {
        const host = document.getElementById(id);
        const btn = host.querySelector('.combo__btn');
        const sw = btn.querySelector('.combo__swatch');
        /* 共享昵称路径：按钮里有色块（§4.4 规则 4「色块 + 常规字色」）；
           老路径（开关关闭 / 本地哈希昵称）：没有色块，颜色落在 label 的字色上（逐像素回退）。 */
        const label = btn.querySelector('span:not(.combo__swatch)');
        return {
          value: host._value,
          label: btn.textContent.trim(),
          swatch: sw ? getComputedStyle(sw).backgroundColor : getComputedStyle(label).color,
        };
      }""", host_id)


# ══════════════════════════════════════════════════════════════════════
def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--keep", action="store_true")
    ap.add_argument("--no-shots", action="store_true")
    ap.add_argument("--shots-dir", default="",
                    help="截图输出目录。默认写到**本次临时目录**（不再脏仓库工作区）；"
                         "要像以前那样归档到 docs/ 就传 docs")
    args = ap.parse_args()

    tmp = Path(tempfile.mkdtemp(prefix="fm-nick-p3-"))
    shots: list[Path] = []
    shot_dir = Path(args.shots_dir) if args.shots_dir else (tmp / "shots")
    shot_dir.mkdir(parents=True, exist_ok=True)

    def shot(page, name: str) -> None:
        if args.no_shots:
            return
        p = shot_dir / f"nickname-{name}.png"
        page.screenshot(path=str(p), full_page=False)
        shots.append(p)
        print(f"     📷 {p.name}")

    on = Srv(tmp, PORT_ON, True)
    off = Srv(tmp, PORT_OFF, False)
    print(f"═══ 共享昵称 Phase 3（网页端）验收 ═══\n  临时目录：{tmp}")

    try:
        on.start()
        print(f"  测试实例（开关开）：{on.base}   ← 生产 18801 未触碰")
        with sync_playwright() as pw:
            browser = pw.chromium.launch()

            # ── F. 空库 + 新浏览器 = 灰色「默认用户」，且能发消息 ──────────
            section("F 新浏览器：灰色临时昵称「默认用户」（空库不假造任何昵称）")
            reset_library(on)          # 自备前置：先清库，不假设实例天生是干净的
            A = browser.new_context(viewport={"width": 1280, "height": 900})
            framesA = ws_recorder(A)
            pageA, errsA = open_page(A, on.base)

            st, body = http(on.base, "GET", "/api/nicknames?status=active")
            check("空库 GET /api/nicknames 返回空数组（不建任何行）", st == 200 and body.get("nicknames") == [], f"{st} {body}")

            combo = combo_state(pageA)
            check("新浏览器发送区显示灰色「默认用户」", "默认用户" in combo["label"], combo["label"])
            want = pageA.evaluate("() => FMNickColor.dot('gray', 'light')")
            check("该灰点是 §4.4 的灰兜底色（浅色 #5A5A5A）",
                  eq_hex_vs_rgb("#5A5A5A", combo["swatch"]), f"{combo['swatch']} vs {want}")

            # ── F2. 远程解锁入口策略：只有离线才拦，会话状态只做二次确认 ──────
            # 用户实测反馈：PC 锁屏时网页端点不动「远程解锁」。旧策略把
            # windows_state != locked/logon_screen 当成禁用条件，而状态由心跳上报、
            # 可能滞后十几秒（同一台机器还有 headless / 交互式两个实例可能报旧值）
            # ⇒ 用户明明锁着屏也没法验证这条路。现在只拦离线，状态改成"说清事实 +
            # 让用户确认"，PC 侧凭据是否就绪才是真正决定能不能解锁的因素。
            section("F2 远程解锁入口策略（只有离线才拦）")
            pol = pageA.evaluate("""() => {
              const P = window.FM_UNLOCK_POLICY || {};
              const call = (fn, d) => { try { return P[fn](d); } catch (e) { return 'EXC:' + e; } };
              return {
                offline_block:  call('blockReason', { online: false, windows_state: 'locked' }),
                locked_block:   call('blockReason', { online: true,  windows_state: 'locked' }),
                unlocked_block: call('blockReason', { online: true,  windows_state: 'unlocked' }),
                unknown_block:  call('blockReason', { online: true,  windows_state: 'unknown' }),
                locked_warn:    call('warnReason',  { online: true,  windows_state: 'locked' }),
                logon_warn:     call('warnReason',  { online: true,  windows_state: 'logon_screen' }),
                unlocked_warn:  call('warnReason',  { online: true,  windows_state: 'unlocked' }),
                unknown_warn:   call('warnReason',  { online: true,  windows_state: 'unknown' }),
              };
            }""")
            check("离线仍拦（按钮禁用）", "离线" in (pol.get("offline_block") or ""), str(pol))
            check("已锁屏 / 登录界面：不拦、也不弹确认",
                  pol.get("locked_block") == "" and pol.get("locked_warn") == ""
                  and pol.get("logon_warn") == "", str(pol))
            check("显示未锁屏：不拦，但要二次确认并说明原因",
                  pol.get("unlocked_block") == "" and "未锁屏" in (pol.get("unlocked_warn") or ""),
                  str(pol))
            check("状态未知：不拦，但要二次确认",
                  pol.get("unknown_block") == "" and "会话状态" in (pol.get("unknown_warn") or ""),
                  str(pol))

            # 机器码 → 中文说法的映射：漏一个用户就会看到英文码
            reasons = pageA.evaluate("() => (window.FM_UNLOCK_POLICY || {}).reason || {}")
            need = ("no_credential", "expired", "replay", "not_mine", "bad_action",
                    "cp_missing", "cp_error", "timeout", "ok")
            missing = [k for k in need if not reasons.get(k)]
            check("每个解锁 machine code 都有中文说法（含 cp_missing）",
                  not missing, f"缺：{missing} / 现有：{sorted(reasons)}")
            check("cp_missing 的说法点明「缺组件」，不是叫用户重试",
                  "组件" in (reasons.get("cp_missing") or ""), str(reasons.get("cp_missing")))

            # 发一条消息（灰临时）
            go_home(pageA)
            pageA.fill("#content", "我是新浏览器，还没选昵称")
            pageA.click("#btn-send")
            pageA.wait_for_timeout(600)
            mc = msg_colors(pageA)
            check("灰临时也能发消息（消息行出现）", len(mc) >= 1 and mc[-1]["name"] == "默认用户",
                  f"{mc[-1] if mc else None}")
            if mc:
                g_bg = pageA.evaluate("() => FMNickColor.display('avatarBg', 'gray')")
                check("灰临时消息头像底 = gray 的 §4.4 头像底（#717171）",
                      eq_hex_vs_rgb(g_bg, mc[-1]["bg"]), f"{mc[-1]['bg']} vs {g_bg}")
                check("灰临时消息头像字色 = 白字（底↔字 ≥4.5）",
                      eq_hex_vs_rgb("#FFFFFF", mc[-1]["fg"]), mc[-1]["fg"])
            go_settings(pageA)
            check("空库空态文案（不假造昵称）",
                  pageA.inner_text("#names-list").strip() == "还没有昵称，先添加一个吧。",
                  pageA.inner_text("#names-list").strip())
            check("空态时新建入口可用", pageA.is_enabled("#name-add"))
            shot(pageA, "01-empty-gray-temp")

            # ── E. 输入长度上限 32（自备干净前置：清库 → 未满额 → 输入框可用）──
            section("E 昵称长度：前端校验 32（与 NAS 一致）")
            go_settings(pageA)
            reset_library(on, pageA)   # 32 字符用例与额度无关，必须自己清干净，
                                       # 绝不依赖前一个用例跑完剩下的额度状态（满额会让输入框按产品行为置灰）
            check("E 前置：库空 + 新建入口可用（未满额，输入框没被产品置灰）",
                  row_colors(pageA) == [] and pageA.is_enabled("#name-new"),
                  f"rows={len(row_colors(pageA))} enabled={pageA.is_enabled('#name-new')}")
            ml = pageA.get_attribute("#name-new", "maxlength")
            check("输入框 maxlength=32", ml == "32", f"maxlength={ml}")
            pageA.fill("#name-new", "长" * 40)
            v = pageA.input_value("#name-new")
            check("实际输入被截到 32 个字符（逐字比对）", v == "长" * 32 and len(v) == 32, f"len={len(v)}")
            pageA.fill("#name-new", "")

            # ── B. 添加 / 撞名 / 换色 / 删除 ──────────────────────────────
            section("B 昵称列表页（全局管理）")
            reset_library(on, pageA)   # 自备前置：空池才能断言「第一色 = color_01」
            check("B 前置：库空（颜色池从 color_01 起步）", row_colors(pageA) == [], str(row_colors(pageA)))
            pageA.fill("#name-new", "妈妈")
            pageA.click("#name-add")
            pageA.wait_for_timeout(700)
            rows = row_colors(pageA)
            check("添加「妈妈」后列表出现一行", len(rows) == 1 and rows[0]["name"] == "妈妈", str(rows))
            check("颜色来自 NAS 的逻辑色 ID（第一色 color_01）",
                  bool(rows) and rows[0]["colorId"] == "color_01", rows[0]["colorId"] if rows else "")
            want_dot = pageA.evaluate("() => FMNickColor.dot('color_01', 'light')")
            exp_dot = dot_light(pool.LOGICAL_COLORS["color_01"])
            check(f"色点渲染 = §4.4 color_01 浅色圆点（{exp_dot}，由服务端色值现算）",
                  bool(rows) and eq_hex_vs_rgb(exp_dot, rows[0]["dot"]), f"{rows[0]['dot'] if rows else ''}")
            reloaded = False
            check("新建后自动选用它（当前昵称 = 妈妈）",
                  "妈妈" in combo_state(pageA)["label"], combo_state(pageA)["label"])

            # 撞名
            pageA.fill("#name-new", "妈妈")
            pageA.click("#name-add")
            pageA.wait_for_selector("#nick-conflict:not([hidden])", timeout=6000)
            ctext = pageA.inner_text("#nick-conflict")
            check("撞名 → 提示「已存在，直接选用它？」", "已存在，直接选用它？" in ctext, ctext.replace("\n", " | "))
            st, body = http(on.base, "GET", "/api/nicknames?status=active")
            check("撞名不新增行（服务端仍只有 1 条）", len(body.get("nicknames", [])) == 1,
                  f"{[n['display_name'] for n in body.get('nicknames', [])]}")
            shot(pageA, "02-conflict-existing")
            pageA.click(".nick-conflict__actions .btn--primary")   # 点一下 = 选用
            pageA.wait_for_timeout(400)
            check("点「选用它」= 选中该昵称（冲突条消失）",
                  pageA.is_hidden("#nick-conflict") and "妈妈" in combo_state(pageA)["label"],
                  combo_state(pageA)["label"])

            # 发一条以「妈妈」名义的消息（验证回显颜色）
            go_home(pageA)
            pageA.fill("#content", "今晚七点开饭")
            pageA.click("#btn-send")
            pageA.wait_for_timeout(600)
            mc = msg_colors(pageA)
            mom = [m for m in mc if m["name"] == "妈妈"]
            bg = pageA.evaluate("() => FMNickColor.display('avatarBg', 'color_01')")
            fg = pageA.evaluate("() => FMNickColor.display('avatarFg', 'color_01')")
            check("发送后回显颜色正确（头像底 = color_01 的 §4.4 头像底 #5E35B1）",
                  bool(mom) and eq_hex_vs_rgb(bg, mom[-1]["bg"]), f"{mom[-1]['bg'] if mom else None} vs {bg}")
            check("头像字色 = color_01 的 §4.4 字色（白字）",
                  bool(mom) and eq_hex_vs_rgb("#FFFFFF", mom[-1]["fg"]), f"{mom[-1]['fg'] if mom else None}")
            shot(pageA, "03-list-with-message")

            # ── D. 三个浏览器上下文同时在线：改名 / 改色 / 删除 → 免刷新同步 ──
            section("D 三个浏览器同时在线：广播免刷新实时同步")
            go_settings(pageA)      # 改色要点的按钮在设置页
            # 前置（显式造，不靠隐式顺序）：改色要池子里还有空色 → 库里必须不是满额
            if len(row_colors(pageA)) != 1:
                reset_library(on, pageA)
                http(on.base, "POST", "/api/nicknames", {"display_name": "妈妈"})
                pageA.wait_for_timeout(700)
                go_settings(pageA)
            check("D 前置：库里恰好 1 条活跃昵称 + 新建入口可用（额度充足）",
                  len(row_colors(pageA)) == 1 and pageA.is_enabled("#name-new"),
                  f"{len(row_colors(pageA))} rows")
            B = browser.new_context(viewport={"width": 1280, "height": 900})
            C = browser.new_context(viewport={"width": 390, "height": 844})   # 手机宽度
            framesB, framesC = ws_recorder(B), ws_recorder(C)
            pageB, errsB = open_page(B, on.base)
            pageC, errsC = open_page(C, on.base)
            for p in (pageB, pageC):
                go_settings(p)
            check("B 打开即拿到同一份列表（整表同步）",
                  [r["name"] for r in row_colors(pageB)] == ["妈妈"], str(row_colors(pageB)))
            check("C（手机宽度）也是同一份列表",
                  [r["name"] for r in row_colors(pageC)] == ["妈妈"], str(row_colors(pageC)))
            check("B / C 的三端颜色一致（同一逻辑色 ID）",
                  row_colors(pageB)[0]["dot"] == row_colors(pageC)[0]["dot"] == row_colors(pageA)[0]["dot"],
                  f"{row_colors(pageB)[0]['dot']}")

            # A 改色（自备前置：B 手里至少得有一条历史消息，否则「历史消息跟着变色」无从谈起）
            if not msg_colors(pageB):
                go_home(pageA)
                pageA.fill("#content", "改色前先垫一条消息")
                pageA.click("#btn-send")
                pageA.wait_for_timeout(700)
                go_settings(pageA)
            beforeA = row_colors(pageA)[0]["dot"]
            beforeB = row_colors(pageB)[0]["dot"]
            beforeC = row_colors(pageC)[0]["dot"]
            beforeB_msg = msg_colors(pageB)
            t0 = time.time()
            pageA.click('#names-list .name-row .name-row__actions button:has-text("换色")')  # 重新分配颜色
            changed = False
            while time.time() - t0 < 6:
                if row_colors(pageB)[0]["dot"] != beforeB and row_colors(pageC)[0]["dot"] != beforeC:
                    changed = True
                    break
                pageB.wait_for_timeout(40)
            dt = (time.time() - t0) * 1000
            # ★ 发起端 A 的更新走 **HTTP 响应**，B/C 走 **WS 广播**：响应偶尔比广播晚几毫秒，
            #   所以再等一次「三端收敛」（最多 3s），避免这条断言在慢机器上假红。
            t1 = time.time()
            while time.time() - t1 < 3:
                afterA = row_colors(pageA)[0]["dot"]
                afterB = row_colors(pageB)[0]["dot"]
                if afterA == afterB:
                    break
                pageA.wait_for_timeout(50)
            afterA = row_colors(pageA)[0]["dot"]
            afterB = row_colors(pageB)[0]["dot"]
            afterC = row_colors(pageC)[0]["dot"]
            check("A 改色 → B / C **不刷新**即变色", changed,
                  f"A {beforeA}→{afterA} | B {beforeB}→{afterB} | C {beforeC}→{afterC}")
            check("三端变色后仍然一致", afterA == afterB == afterC, f"{afterA} == {afterB} == {afterC}")
            check("1 秒内到达（实测耗时）", dt < 1000, f"{dt:.0f} ms")
            check("A 的新色确实换了一个逻辑色 ID（不是同一个）",
                  row_colors(pageA)[0]["colorId"] != "color_01", row_colors(pageA)[0]["colorId"])
            afterB_msg = msg_colors(pageB)
            check("B 的历史消息头像也跟着变色（同一逻辑色 ID 的显示色，无需刷新）",
                  bool(beforeB_msg) and bool(afterB_msg) and beforeB_msg[-1]["bg"] != afterB_msg[-1]["bg"],
                  f"{beforeB_msg[-1]['bg'] if beforeB_msg else None} → {afterB_msg[-1]['bg'] if afterB_msg else None}")
            shot(pageA, "04-realtime-A-after-recolor")
            shot(pageB, "05-realtime-B-no-reload")
            shot(pageC, "06-realtime-C-phone-no-reload")

            # A 改名 → B/C（旧名自取，不写死；后面「消息快照冻结」用它断言）
            old_name = row_colors(pageA)[0]["name"]
            pageA.fill("#names-list .name-row .name-row__input", "母亲")
            pageA.evaluate("() => document.querySelector('#names-list .name-row__input')"
                           ".dispatchEvent(new Event('change'))")
            t0 = time.time()
            ok_rn = False
            while time.time() - t0 < 6:
                if [r["name"] for r in row_colors(pageB)] == ["母亲"]:
                    ok_rn = True
                    break
                pageB.wait_for_timeout(40)
            check("A 改名 → B 免刷新即见新名（nickname_updated）", ok_rn, f"{(time.time()-t0)*1000:.0f} ms")
            check("改名不改颜色（逻辑色 ID 与色点不变）",
                  row_colors(pageB)[0]["colorId"] == row_colors(pageA)[0]["colorId"],
                  row_colors(pageB)[0]["colorId"])
            check("历史消息仍显示当时的名字（消息快照冻结，不会被改名追着改）",
                  any(m["name"] == old_name for m in msg_colors(pageB))
                  and not any(m["name"] == "母亲" for m in msg_colors(pageB)),
                  f"old={old_name!r} msgs={[m['name'] for m in msg_colors(pageB)]}")

            # ── G. 未知逻辑色 ID → 灰兜底 ─────────────────────────────────
            section("G 未知 / 脏逻辑色 ID（如 color_99）→ 灰兜底，style 里不出现非法值")
            # 自备前置：拿**当前真实那一行**的 id / 名字去伪造脏帧，不假设 id 一定是 1
            dirty_id, dirty_name = int(row_colors(pageA)[0]["id"]), row_colors(pageA)[0]["name"]
            pageA.evaluate("""(a) => handleServerFrame({type:'nickname_color_changed',
                nickname:{nickname_id:a.id, display_name:a.name, color:'color_99', status:'active'}})""",
                {"id": dirty_id, "name": dirty_name})
            pageA.wait_for_timeout(200)
            dirty = row_colors(pageA)[0]
            want_gray = pageA.evaluate("() => FMNickColor.dot('gray', 'light')")
            check("脏 ID 那一行仍然渲染成灰（不崩、不当成有效色）",
                  eq_hex_vs_rgb("#5A5A5A", dirty["dot"]), f"{dirty['dot']}")
            bad_style = pageA.evaluate("""() => {
                const out = [];
                document.querySelectorAll('#names-list *').forEach(el => {
                  const s = el.getAttribute('style') || '';
                  if (s.includes('color_99') || s.includes('color_0')) out.push(s);
                });
                return out;
              }""")
            check("style 里不出现逻辑色 ID 原串 / HEX（R2：绝不把收到的字符串塞进 CSS）",
                  bad_style == [], str(bad_style))
            check("未知 ID 的兜底 = gray 的显示色", eq_hex_vs_rgb(want_gray, dirty["dot"]), want_gray)
            # 恢复：让服务端整表同步把它校正回来
            pageA.evaluate("() => loadNicknames()")
            pageA.wait_for_timeout(400)
            check("整表同步把本地脏缓存校正回来（nickname_list_sync / GET）",
                  row_colors(pageA)[0]["colorId"].startswith("color_"),
                  row_colors(pageA)[0]["colorId"])

            # ── 深色主题：同一逻辑色 ID 换主题变体 ─────────────────────────
            section("A 深色主题下的映射表（同一逻辑色 ID → 深色圆点变体）")
            go_settings(pageA)
            light_row = row_colors(pageA)[0] if row_colors(pageA) else None   # 浅色基准现取，不借上个用例的变量
            pageA.evaluate("() => { localStorage.setItem('fm.mode','dark'); applyMode('dark', true); }")
            pageA.wait_for_timeout(300)
            dark_rows = row_colors(pageA)
            check("换成深色主题后色点按深色变体重画（与浅色不同）",
                  bool(light_row) and bool(dark_rows) and dark_rows[0]["dot"] != light_row["dot"],
                  f"light {light_row['dot'] if light_row else None} → dark {dark_rows[0]['dot'] if dark_rows else None}")
            shot(pageA, "07-dark-theme")

            # ── H. 窄屏不重叠 ─────────────────────────────────────────────
            section("H 手机窄屏（375px）昵称卡片不重叠、不溢出")
            N = browser.new_context(viewport={"width": 375, "height": 812})
            pageN, errsN = open_page(N, on.base)
            go_settings(pageN)
            pageN.wait_for_timeout(200)
            if not row_colors(pageN):        # 自备前置：窄屏用例至少要有一行可量
                http(on.base, "POST", "/api/nicknames", {"display_name": "窄屏测试"})
                pageN.wait_for_timeout(700)
                go_settings(pageN)
            geom = pageN.evaluate("""() => Array.from(document.querySelectorAll('#names-list .name-row')).map(r => {
                const q = (s) => { const e = r.querySelector(s); const b = e.getBoundingClientRect();
                                   return {l: b.left, r: b.right, w: b.width}; };
                const btns = Array.from(r.querySelectorAll('.name-row__actions button'));
                return { row: {l: r.getBoundingClientRect().left, r: r.getBoundingClientRect().right},
                         scrollW: r.scrollWidth, clientW: r.clientWidth,
                         input: q('.name-row__input'), first: q('.name-row__actions .btn'),
                         last: q('.name-row__actions .btn:last-child'), h: r.getBoundingClientRect().height,
                         labels: btns.map((b) => b.textContent),
                         useDisabled: btns.length ? btns[0].disabled : null,
                         using: r.classList.contains('name-row--using') };
              })""")
            ok_narrow = len(geom) >= 1 and all(   # len>=1：空数组会让 all() 假通过
                g["scrollW"] <= g["clientW"] + 1                      # 不溢出
                and g["row"]["r"] <= 376                              # 不出视口
                and g["input"]["r"] <= g["first"]["l"] + 1            # 输入框不与按钮重叠
                and g["first"]["r"] <= g["last"]["l"] + 1
                and g["h"] <= 48                                      # 仍是单行（没被挤成两行）
                for g in geom)
            check("窄屏行内不重叠 / 不溢出 / 不换行", ok_narrow, json.dumps(geom, ensure_ascii=False))
            # ★ v0.18.0 实测反馈：网页端要和 PC 端一样有「选用 / 换色 / 删除」三个**文字**按钮
            check("★ 每行都是三个文字按钮：选用 / 换色 / 删除（与 PC 端同款）",
                  all(g["labels"] == ["选用", "换色", "删除"] for g in geom),
                  json.dumps([g["labels"] for g in geom], ensure_ascii=False))
            check("……正在用的那一行「选用」置灰（不用重复点），其余行可点",
                  all((g["useDisabled"] is True) == g["using"] for g in geom),
                  json.dumps([(g["using"], g["useDisabled"]) for g in geom]))
            card = pageN.evaluate("""() => { const c = document.getElementById('card-nicknames');
                const b = c.getBoundingClientRect(); return {l: b.left, r: b.right, w: b.width}; }""")
            check("昵称卡片不超出 375px 视口", card["l"] >= 0 and card["r"] <= 377, json.dumps(card))
            shot(pageN, "08-narrow-375")

            # ── C. 额度提示（14 / 16）─────────────────────────────────────
            section("C 额度提示：14 条起提示、16 条满额并置灰新建入口")
            go_settings(pageA)
            reset_library(on, pageA)   # 自备前置：从 0 造到 14，不依赖前面剩了几条
            check("C 前置：库已清空（14 / 16 都从白纸开始数）", row_colors(pageA) == [], str(row_colors(pageA)))
            for i in range(14):
                st, b = http(on.base, "POST", "/api/nicknames", {"display_name": f"测试昵称{i+1:02d}"})
                assert st == 201, (st, b)
            pageA.wait_for_timeout(900)
            check("C 造数真实生效（服务端 14 条活跃）", len(active_names(on.base)) == 14,
                  str(len(active_names(on.base))))
            q = pageA.inner_text("#nick-quota").strip()
            check("14 条 →「共享昵称颜色即将用尽（14/16）」（真实计数）",
                  q == "共享昵称颜色即将用尽（14/16）", q)
            check("14/16 时列表真是 14 行（不是文案跟计数脱节）", len(row_colors(pageA)) == 14,
                  str(len(row_colors(pageA))))
            shot(pageA, "09-quota-14")

            for i in range(14, 16):
                st, b = http(on.base, "POST", "/api/nicknames", {"display_name": f"测试昵称{i+1:02d}"})
                assert st == 201, (st, b)
            pageA.wait_for_timeout(900)
            q = pageA.inner_text("#nick-quota").strip()
            check("16 条 → 满额文案（逐字）",
                  q == "已达到共享昵称上限，请删除不再使用的昵称后再添加。", q)
            check("满额时「添加」入口置灰", not pageA.is_enabled("#name-add"))
            st, b = http(on.base, "POST", "/api/nicknames", {"display_name": "第17个"})
            check("绕过 UI 直连 POST 仍是 503 NO_AVAILABLE_COLOR",
                  st == 503 and b.get("detail", {}).get("code") == "NO_AVAILABLE_COLOR", f"{st} {b}")
            shot(pageA, "10-quota-full")

            # ── 删除 → 颜色回池（nickname_removed 广播）───────────────────
            nid = int(row_colors(pageA)[-1]["id"])
            # ⚠ deleteNick 是 async 且内部 await confirmDialog：直接 evaluate 会等它的 Promise（对话框没人点）= 死锁。
            #   必须 fire-and-forget，等对话框真出现再点「删除」。
            ffire(pageA, f"deleteNick(nickById.get({nid}))")
            try:
                pageA.wait_for_selector("#dlg-confirm.is-visible", timeout=5000)
                dlg_ok = True
            except Exception as e:
                dlg_ok = False
                print(f"     ⚠ 确认对话框没出现：{type(e).__name__}")
            check("删除前弹出确认对话框（危险操作不静默执行）", dlg_ok)
            if dlg_ok:
                pageA.click("#confirm-ok")
            pageA.wait_for_timeout(900)
            check("删除后列表少一行（三端同步）",
                  len(row_colors(pageA)) == 15 and len(row_colors(pageB)) == 15,
                  f"A={len(row_colors(pageA))} B={len(row_colors(pageB))}")
            q = pageA.inner_text("#nick-quota").strip()
            check("删掉一条后额度回到 15/16 提示", q == "共享昵称颜色即将用尽（15/16）", q)
            check("满额置灰解除（入口又能用）", pageA.is_enabled("#name-add"))
            check("删除真落库（服务端 15 条活跃）", len(active_names(on.base)) == 15,
                  str(len(active_names(on.base))))

            # ── E. 当前昵称存在本地（fm.lastSender = nickname_id）；fm.names 不再读写 ──
            section("E 当前昵称：本地存的必须是 nickname_id；fm.names 不再读写")
            ls = pageA.evaluate("""() => { const o = {};
                for (let i = 0; i < localStorage.length; i++) { const k = localStorage.key(i); o[k] = localStorage.getItem(k); }
                return o; }""")
            check("localStorage 里只有 fm.* 这几个键，没有 fm.names", "fm.names" not in ls, str(list(ls)))
            # 前置（显式造，绝不靠顺序）：先真的选一条现有昵称 → 刷新 → 再看本地存的是什么。
            # 前面的额度用例把库清空过，选中项随之失效、fm.lastSender 被清掉是**产品的正确行为**，
            # 所以这里不能拿「上一条用例残留的选中项」当断言对象（那是用例串状态，不是产品 bug）。
            first = active_names(on.base)[0]          # 自取：后面断言用它的名字，不写死「母亲」
            nid, pick_name = first["nickname_id"], first["display_name"]
            pageA.evaluate(f"() => selectNick({nid})")   # selectNick 是同步函数，evaluate 不会挂
            pageA.wait_for_timeout(200)
            check("E 前置：已显式选中一条活跃昵称（本地有东西可查）",
                  pageA.evaluate("() => localStorage.getItem('fm.lastSender')") == str(nid), str(nid))
            pageA.reload(wait_until="load")
            pageA.wait_for_function("() => !!window.FMNickResolver", timeout=15000)
            pageA.wait_for_timeout(500)
            last = pageA.evaluate("() => localStorage.getItem('fm.lastSender')")
            check("fm.lastSender 存的是 nickname_id（纯数字，不是旧的名字）",
                  bool(re.fullmatch(r"\d+", last or "")), repr(last))
            check("选择后 fm.lastSender 写入该 id",
                  pageA.evaluate("() => localStorage.getItem('fm.lastSender')") == str(nid),
                  str(pageA.evaluate("() => localStorage.getItem('fm.lastSender')")))
            pageA.reload(wait_until="load")
            pageA.wait_for_function("() => !!window.FMNickResolver", timeout=15000)
            pageA.wait_for_timeout(500)
            check("刷新后仍记得这个选择（id → 显示名）",
                  pick_name in combo_state(pageA)["label"],
                  f"want {pick_name!r} got {combo_state(pageA)['label']!r}")
            check("刷新后 fm.lastSender 仍是 id",
                  re.fullmatch(r"\d+", pageA.evaluate("() => localStorage.getItem('fm.lastSender')") or ""),
                  repr(pageA.evaluate("() => localStorage.getItem('fm.lastSender')")))

            # 一次性迁移：老值（名字）能对上 → 换成 id；对不上 → 未选
            pageA.evaluate("(v) => localStorage.setItem('fm.lastSender', v)", pick_name)
            pageA.reload(wait_until="load")
            pageA.wait_for_function("() => !!window.FMNickResolver", timeout=15000)
            pageA.wait_for_timeout(700)
            check("迁移：老值（名字）能对上 → 自动换成 nickname_id",
                  bool(re.fullmatch(r"\d+", pageA.evaluate("() => localStorage.getItem('fm.lastSender')") or ""))
                  and pick_name in combo_state(pageA)["label"],
                  repr(pageA.evaluate("() => localStorage.getItem('fm.lastSender')")))
            pageA.evaluate("() => localStorage.setItem('fm.lastSender', '查无此人')")
            pageA.reload(wait_until="load")
            pageA.wait_for_function("() => !!window.FMNickResolver", timeout=15000)
            pageA.wait_for_timeout(700)
            check("迁移：对不上 → 回到「未选」（灰临时「默认用户」）",
                  pageA.evaluate("() => localStorage.getItem('fm.lastSender')") == ""
                  and "默认用户" in combo_state(pageA)["label"],
                  repr(pageA.evaluate("() => localStorage.getItem('fm.lastSender')")))

            # ── 广播帧清单：5 个事件都真的到过浏览器 ───────────────────────
            section("D' 5 个广播事件都到过浏览器（帧类型清点）")
            got = {f["type"] for f in framesA + framesB + framesC}
            want5 = {"nickname_created", "nickname_updated", "nickname_removed",
                     "nickname_color_changed", "nickname_list_sync"}
            check("5 个昵称广播事件全部收到", want5 <= got, f"收到 {sorted(got)}")
            # 守卫本意：**昵称 / 消息快照**里的颜色只走逻辑色 ID，客户端按主题现算显示色。
            # ★ v0.19 起 `color_table`（色表本身）**合法携带基础 HEX** —— 色表是数据，
            #   它就是「逻辑色 ID → 基础色值」的权威来源。所以这里只查除它以外的字段。
            def _strip_color_table(frame: dict) -> str:
                f = dict(frame)
                f.pop("color_table", None)
                return json.dumps(f, ensure_ascii=False)

            hexes = [f for f in framesA + framesB + framesC if "#" in _strip_color_table(f)]
            check("广播载荷里没有任何 HEX（昵称与快照的颜色只走逻辑色 ID；color_table 除外）",
                  not hexes, str(hexes[:1]))

            # ── G. ★ v0.19：颜色表以服务端为准（内置表只兜底）+ 广播后免刷新跟着变 ──
            #    ⚠ 必须放在「断网 / 开关关闭」之前：那两段会把 on 实例停掉。
            section("★ G 颜色表以服务端为准（v0.19 P2）")
            reset_library(on, keep=0)          # 自备前置：清库，不假设实例天生干净
            st, body = http(on.base, "POST", "/api/nicknames/colors", {"hex": "#0FA3B1"})
            custom = (body.get("color") or {}).get("color_id")
            ver = body.get("color_pool_version")
            check("（前置）服务端加一个自定义色 #0FA3B1",
                  st == 201 and custom == "color_33", f"{st} {body}")
            st, body = http(on.base, "POST", "/api/nicknames", {"display_name": "服务端的色"})
            nid = (body.get("nickname") or {}).get("nickname_id")
            st2, _ = http(on.base, "POST", f"/api/nicknames/{nid}/color", {"color_id": custom})
            check("（前置）把那个昵称指定成自定义色", st == 201 and st2 == 200, f"{st} {st2}")

            G = browser.new_context(viewport={"width": 1280, "height": 900})
            pageG, errsG = open_page(G, on.base)
            stateG = pageG.evaluate("""() => ({
                has: FMNickColor.hasServerTable(), v: FMNickColor.tableVersion(),
                ids: FMNickColor.tableIds().length, hex: FMNickColor.base('color_33'),
            })""")
            exp_active = len(http(on.base, "GET", "/api/nicknames/colors?status=active")[1]["colors"])
            check(f"★ 页面启动就拉了服务端色表（认得出 / 版本一致 / {exp_active} 个色）",
                  stateG["has"] and stateG["v"] == ver and stateG["ids"] == exp_active, json.dumps(stateG))
            check("★ 自定义色按**服务端 HEX** 渲染（内置表里没有 color_33 —— 走兜底会变灰）",
                  stateG["hex"] == "#0FA3B1", json.dumps(stateG))
            go_settings(pageG)
            grows = [r for r in row_colors(pageG) if r["name"] == "服务端的色"]
            check("……昵称行色点用的就是它（服务端表优先，不是灰兜底）",
                  bool(grows) and grows[0]["colorId"] == "color_33",
                  json.dumps(grows, ensure_ascii=False))
            shot(pageG, "12-color-table-from-server")

            # 广播：页面开着，服务端再加一个色 → 免刷新跟到
            st, body = http(on.base, "POST", "/api/nicknames/colors", {"hex": "#7B1FA2"})
            pageG.wait_for_function(f"() => FMNickColor.tableIds().length >= {exp_active + 1}", timeout=8000)
            afterG = pageG.evaluate("() => ({v: FMNickColor.tableVersion(), hex: FMNickColor.base('color_34')})")
            check(f"★ 服务端加色 → color_table_changed 广播 → 页面**免刷新**重拉（{exp_active + 1} 个色 / 版本跟进）",
                  afterG["hex"] == "#7B1FA2" and afterG["v"] == body.get("color_pool_version"),
                  json.dumps(afterG))
            check("……G 段零控制台报错", not errsG, str(errsG[:3]))
            G.close()

            # ── H. ★ v0.19 P3：色表管理界面（加 / 停用 / 选色）────────────────
            section("★ H 色表管理界面（v0.19 P3：加 / 停用 / 逐昵称选色）")
            reset_library(on, keep=0)
            st_b, before = http(on.base, "GET", "/api/nicknames/colors?status=active")
            n_before = len(before["colors"])
            http(on.base, "POST", "/api/nicknames", {"display_name": "界面甲"})
            http(on.base, "POST", "/api/nicknames", {"display_name": "界面乙"})
            H = browser.new_context(viewport={"width": 1400, "height": 1000})
            pageH, errsH = open_page(H, on.base)
            go_settings(pageH)

            def chips():
                return pageH.evaluate("""() => Array.from(document.querySelectorAll('#color-grid .color-chip')).map(c => ({
                    id: c.dataset.colorId, used: c.classList.contains('color-chip--used'),
                    retired: c.classList.contains('color-chip--retired'),
                    dot: getComputedStyle(c.querySelector('.color-chip__dot')).backgroundColor,
                    delDisabled: (function () { var d = c.querySelector('.color-chip__del'); return d ? d.disabled : null; })(),
                    delTitle: (function () { var d = c.querySelector('.color-chip__del'); return d ? d.title : ''; })(),
                }))""")

            def rowsH():
                return pageH.evaluate("""() => Array.from(document.querySelectorAll('#names-list .name-row')).map(r => ({
                    id: r.dataset.nickId, name: (r.querySelector('.name-row__input') || {}).value || '',
                    swatchTag: (r.querySelector('.name-row__swatch') || {}).tagName || '',
                    colorId: (function () { var s = r.querySelector('.name-row__swatch'); return s ? s.dataset.colorId : ''; })(),
                }))""")

            c0 = chips()
            check(f"★ 色块区把整份色表画出来（{n_before} 格，每格带 color_id、色点是算出来的显示色）",
                  len(c0) == n_before and all(c["id"].startswith("color_") for c in c0)
                  and all(c["dot"] not in ("rgb(138, 138, 138)", "") for c in c0),
                  f"{len(c0)} 格 / 首格 {c0[0]['dot']}")
            used0 = [c for c in c0 if c["used"]]
            check("★ 正在被昵称用着的格子：标「在用」+ 删除按钮置灰 + 写明谁在用",
                  len(used0) == 2 and all(c["delDisabled"] is True and "正在用它" in c["delTitle"]
                                          for c in used0),
                  json.dumps(used0[:2], ensure_ascii=False))

            # —— 加颜色：HEX 一条路
            pageH.fill("#color-new", "#C2185B")
            pageH.click("#color-add-btn")
            pageH.wait_for_function(
                f"() => document.querySelectorAll('#color-grid .color-chip').length === {n_before + 1}",
                timeout=8000)
            c1 = chips()
            check("★ 加颜色（填 HEX → 添加）：多出一格，且是服务端分配的新 ID",
                  len(c1) == n_before + 1 and c1[-1]["id"].startswith("color_") and not c1[-1]["retired"],
                  f"{len(c1)} 格 / 新格 {c1[-1]['id']}")
            check("……计数文案跟着走", f"可用 {n_before + 1}" in pageH.inner_text("#color-count"),
                  pageH.inner_text("#color-count"))
            st_c, srv_c = http(on.base, "GET", "/api/nicknames/colors?status=active")
            check("……服务端真落库（可用色 +1）", st_c == 200 and len(srv_c["colors"]) == n_before + 1,
                  f'{st_c} {len(srv_c["colors"])} 个')
            shot(pageH, "13-color-table-desktop")

            # —— 非法 / 重复：就地报错，不静默
            pageH.fill("#color-new", "不是颜色")
            pageH.click("#color-add-btn")
            pageH.wait_for_timeout(500)
            hint = pageH.evaluate("() => { var e = document.getElementById('color-hint');"
                                  " return { hidden: e.hidden, text: e.textContent }; }")
            check("★ 非法色值 → 输入框下面**就地报错**（不静默，也没多加一格）",
                  hint["hidden"] is False and "格式不对" in hint["text"]
                  and pageH.evaluate("() => document.querySelectorAll('#color-grid .color-chip').length")
                  == n_before + 1, json.dumps(hint, ensure_ascii=False))
            pageH.fill("#color-new", "#c2185b")            # 同一个色，只是小写
            pageH.click("#color-add-btn")
            pageH.wait_for_timeout(500)
            hint2 = pageH.evaluate("() => document.getElementById('color-hint').textContent")
            check("★ 重复颜色（大小写不同也算同一个）→ 提示「已经在表里」",
                  "已经在表里" in (hint2 or ""), str(hint2))

            # —— 逐昵称选色：点行首色点 → 弹窗 → 选一个空闲色
            r0 = rowsH()
            target = next(r for r in r0 if r["name"] == "界面甲")
            check("（前置）昵称行的色点已经是按钮（点它选颜色）", target["swatchTag"] == "BUTTON", target)
            pageH.click(f'#names-list .name-row[data-nick-id="{target["id"]}"] .name-row__swatch')
            pageH.wait_for_selector("#dlg-color.is-visible", timeout=6000)
            picks = pageH.evaluate("""() => Array.from(document.querySelectorAll('#color-picker-grid .color-pick')).map(b => ({
                id: b.dataset.colorId, disabled: b.disabled,
                current: b.classList.contains('color-pick--current') }))""")
            check("★ 点色点打开选色弹窗：列出可用色、当前色标出、别人占着的置灰",
                  len(picks) == n_before + 1 and sum(1 for p in picks if p["current"]) == 1
                  and any(p["disabled"] for p in picks),
                  json.dumps([p for p in picks if p["disabled"]][:2], ensure_ascii=False))
            # 等淡入动画跑完再截（否则截到半透明的弹窗）
            pageH.wait_for_timeout(400)
            shot(pageH, "14-color-picker-dialog")
            free = next(p["id"] for p in picks if not p["disabled"] and not p["current"])
            pageH.click(f'#color-picker-grid .color-pick[data-color-id="{free}"]')
            pageH.wait_for_timeout(800)
            after = next(r for r in rowsH() if r["name"] == "界面甲")
            check("★ 选一个空闲色 → 立刻生效（弹窗关闭 + 行上色点换成它）",
                  after["colorId"] == free
                  and pageH.evaluate("() => document.getElementById('dlg-color').classList.contains('is-open')")
                  is False, f'{target["colorId"]} → {after["colorId"]}')
            st_n, body_n = http(on.base, "GET", "/api/nicknames?status=active")
            got = [n["color"] for n in body_n["nicknames"] if n["display_name"] == "界面甲"]
            check("……服务端真落库（GET /api/nicknames 就是它）", got == [free], str(got))

            # —— 弹窗里的「随机换一个」= 原来的换色
            pageH.click(f'#names-list .name-row[data-nick-id="{target["id"]}"] .name-row__swatch')
            pageH.wait_for_selector("#dlg-color.is-visible", timeout=6000)
            pageH.click("#color-dlg-random")
            pageH.wait_for_timeout(900)
            rnd = next(r for r in rowsH() if r["name"] == "界面甲")
            check("弹窗里「随机换一个」→ 颜色变了（≠ 刚指定的那个）",
                  rnd["colorId"] != free and rnd["colorId"].startswith("color_"),
                  f'{free} → {rnd["colorId"]}')

            # —— 停用：加一个没人用的色 → 停用它
            st_x, x = http(on.base, "POST", "/api/nicknames/colors", {"hex": "#455A64"})
            xid = (x.get("color") or {}).get("color_id")
            pageH.wait_for_function(
                f"() => !!document.querySelector('#color-grid .color-chip[data-color-id=\"{xid}\"]')",
                timeout=8000)
            pageH.click(f'#color-grid .color-chip[data-color-id="{xid}"] .color-chip__del')
            pageH.wait_for_selector("#dlg-confirm.is-visible", timeout=6000)
            pageH.click("#confirm-ok")
            pageH.wait_for_timeout(800)
            ret = [c for c in chips() if c["id"] == xid]
            check("★ 停用一个没人用的颜色 → 该格变「已停用」（虚线 + 没有删除按钮）",
                  bool(ret) and ret[0]["retired"] is True and ret[0]["delDisabled"] is None,
                  json.dumps(ret, ensure_ascii=False))
            # 停用后再开一次弹窗：已停用的色不该出现在可选列表里
            pageH.click('#names-list .name-row .name-row__swatch')
            pageH.wait_for_selector("#dlg-color.is-visible", timeout=6000)
            check(f"……已停用的 {xid} 不进选色弹窗",
                  pageH.evaluate(f"""() => !document.querySelector('#color-picker-grid .color-pick[data-color-id="{xid}"]')"""),
                  xid)
            pageH.click("#color-dlg-cancel")
            pageH.wait_for_timeout(300)

            # —— 窄屏 + 暗色（都要过）
            pageH.set_viewport_size({"width": 375, "height": 820})
            pageH.wait_for_timeout(400)
            geo = pageH.evaluate("""() => { var g = document.getElementById('color-grid');
                var c = document.getElementById('card-nicknames');
                return { gridOverflow: g.scrollWidth - g.clientWidth,
                         cardR: c.getBoundingClientRect().right,
                         chips: document.querySelectorAll('#color-grid .color-chip').length }; }""")
            check("窄屏 375：色块区不横向溢出、卡片不出视口",
                  geo["gridOverflow"] <= 1 and geo["cardR"] <= 377 and geo["chips"] >= n_before,
                  json.dumps(geo))
            pageH.evaluate("() => document.getElementById('color-block').scrollIntoView({block: 'center'})")
            pageH.wait_for_timeout(350)
            shot(pageH, "15-color-table-narrow-375")
            pageH.set_viewport_size({"width": 1400, "height": 1000})
            pageH.evaluate("() => applyMode('dark', true)")
            pageH.wait_for_timeout(400)
            dark_dot = pageH.evaluate("""() => { var d = document.querySelector('#color-grid .color-chip__dot');
                return d ? getComputedStyle(d).backgroundColor : ''; }""")
            check("暗色下色块区照常（色点是深色主题变体）", bool(dark_dot), dark_dot)
            pageH.evaluate("() => document.getElementById('color-block').scrollIntoView({block: 'center'})")
            pageH.wait_for_timeout(350)
            shot(pageH, "16-color-table-dark")
            check("……H 段零控制台报错", not errsH, str(errsH[:3]))
            H.close()

            # ── 断网 / 服务不可达：网页端不崩、不本地生效 ─────────────────
            section("G' 断网（服务不可达）时昵称管理失败并提示，不本地生效")
            go_settings(pageA)
            # 自备前置：这个用例要点「添加」，满额时输入框按产品行为是置灰的（不是产品坏了）
            check("G' 前置：新建入口可用（未满额）", ensure_add_enabled(on, pageA),
                  f"rows={len(row_colors(pageA))}")
            saved = [r["name"] for r in row_colors(pageA)]
            pageA.route("**/api/nicknames**", lambda route: route.abort())
            pageA.fill("#name-new", "断网建的")
            pageA.click("#name-add")
            pageA.wait_for_selector(".toast", timeout=6000)
            toasts = pageA.eval_on_selector_all(".toast", "els => els.map(e => e.textContent)")
            check("断网新建 → 明确提示（必须在线）",
                  any("新建失败" in t for t in toasts), str(toasts))
            check("断网时列表**不本地生效**（行数没变）",
                  [r["name"] for r in row_colors(pageA)] == saved, str(saved[:3]))
            pageA.unroute("**/api/nicknames**")

            for page, errs, who in ((pageA, errsA, "A"), (pageB, errsB, "B"), (pageC, errsC, "C")):
                check(f"{who} 页面无 JS 运行时错误", errs == [], str(errs[:2]))
            browser.close()

        # ── 回退开关：nickname.enabled=false ────────────────────────────
        section("G'' 开关关闭（nickname.enabled=false）：回到本机老路径，页面照常")
        on.stop()
        off.start()
        print(f"  测试实例（开关关）：{off.base}")
        with sync_playwright() as pw:
            browser = pw.chromium.launch()
            ctx = browser.new_context(viewport={"width": 1280, "height": 900})
            page, errs = open_page(ctx, off.base)
            check("/api/config 不含 nickname_enabled（字段缺失 = false）",
                  page.evaluate("() => !!state.config.nickname_enabled") is False)
            check("FMNickResolver 报告「未启用」（前端走老路径）",
                  page.evaluate("() => FMNickResolver.current().enabled") is False)
            combo = combo_state(page)
            check("发送区回到本机名字（默认「我」）", combo["label"] == "我", combo["label"])
            hash_dot = page.evaluate("() => nickColor('我')")
            check("老路径的颜色仍是本地哈希色（逐像素回退）",
                  eq_hex_vs_rgb(hash_dot, combo["swatch"]), f"{combo['swatch']} vs {hash_dot}")
            go_settings(page)
            check("设置页仍是本机昵称列表（老 UI 可用）", row_colors(page)[0]["name"] == "我",
                  str(row_colors(page)))
            go_home(page)      # ⚠ #content 在 #page-home 里，设置页上是 hidden 的：不切回来 fill 必定 12s 超时
            page.fill("#content", "开关关闭时照常发消息")
            page.click("#btn-send")
            page.wait_for_timeout(600)
            check("开关关闭时仍能发消息", len(msg_colors(page)) >= 1, str(msg_colors(page)[-1:]))
            check("开关关闭时页面无 JS 错误", errs == [], str(errs[:2]))
            shot(page, "11-disabled-fallback")

            browser.close()
        off.stop()

        # ── 库里的颜色全是逻辑色 ID（grep 不到 HEX）──────────────────────
        section("★ 落地检查：库里只有逻辑色 ID")
        conn = sqlite3.connect(f"file:{on.db}?mode=ro", uri=True)
        try:
            cols = [r[0] for r in conn.execute("SELECT color FROM nicknames")]
            shapes = {re.sub(r"\d+", "NN", c) for c in cols}
        finally:
            conn.close()
        check("nicknames.color 全是 color_NN 形状（无 HEX / 无 gray）",
              shapes <= {"color_NN"}, str(sorted(shapes)))

    except BaseException as e:                    # 用例自己抛异常（超时 / 崩）也要把汇总行打出来：
        print(traceback.format_exc())             # Ctrl-C 照样走 finally，不留下孤儿实例
        if not isinstance(e, KeyboardInterrupt):
            check(f"★ 用例执行未意外中断（{type(e).__name__}）", False, str(e).strip().splitlines()[0][:160])

    finally:
        released = on.stop() and off.stop()          # 实例是 start_new_session 起的，必须确认端口真放掉了
        if not released:
            print("  ⚠ 测试实例没能在 5s 内释放端口（18899 / 18897），请自查是否还有残留进程")
        if not args.keep:
            shutil.rmtree(tmp, ignore_errors=True)

    bad = [r for r in results if not r[1]]
    print(f"\n═══ 结果：{len(results) - len(bad)}/{len(results)} 通过 ═══")
    if bad:
        print("失败项：")
        for n, _, d in bad:
            print(f"  ❌ {n}   {d}")
    if shots:
        print("截图：")
        for p in shots:
            print(f"  {p}")
    print(f"临时目录：{tmp}")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
