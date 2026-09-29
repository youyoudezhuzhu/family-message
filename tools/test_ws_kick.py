#!/usr/bin/env python3
"""复现「同一 device_id 两条连接对踢 → 消息投给没有界面的那一侧」。

背景（服务端侧并不改逻辑，只加日志 + 提供复现基准）
--------------------------------------------------
PC 端有两个实例连**同一个 device_id**：开机自启的 headless（无界面）与
登录后的交互式。服务端 hub.bind_device 的踢人逻辑本身是对的：新连接顶掉
旧连接，关闭码 4000 / reason=replaced by new connection。但旧连接会**立刻
重连回来对踢**，于是：

  · hub.devices[device_id] 永远只保留「最后连上的那一条」
  · _broadcast_message 只往 hub 里那一条推
  · 消息于是投给了「碰巧刚重连成功」的那一方 —— 常常是**没有界面的 headless**；
    服务端看到 send=success，用户界面却什么都没收到
  · 两侧日志里表现为密集的「断开 4000 → 立刻重连」抖动

本脚本自起一个测试实例（默认 18896，data_dir 在 /tmp 下），跑完按**精确 PID**
停掉。18801 是生产实例，脚本直接拒绝使用该端口。

用法：
    python tools/test_ws_kick.py                 # 默认 18896，10 秒风暴、8 条消息
    python tools/test_ws_kick.py --storm 20 --messages 16
    FM_PY=/path/to/python python tools/test_ws_kick.py    # 指定跑实例的解释器
"""
from __future__ import annotations

import argparse
import asyncio
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import time
import uuid
from pathlib import Path
from urllib.parse import urlencode

import httpx
import websockets

ROOT = Path(__file__).resolve().parent.parent
SERVER_DIR = ROOT / "server"
PROD_PORT = 18801          # 生产实例：脚本硬拒绝碰它
ENROLL = "FAMILY-TEST-TOKEN"

PASS: list[str] = []
FAIL: list[str] = []


def check(name: str, ok: bool, detail: str = "") -> None:
    (PASS if ok else FAIL).append(name)
    print(f"  {'✅' if ok else '❌'} {name}{('  — ' + detail) if detail else ''}")


def note(text: str) -> None:
    print(f"  ℹ️  {text}")


# ============================================================
# 测试实例
# ============================================================
def resolve_python() -> str:
    """挑一个能 import fastapi/uvicorn/websockets 的解释器来跑测试实例。"""
    cands = [os.environ.get("FM_PY"), sys.executable,
             str(Path("/vol1/@apphome/hermes-agent/data/venv/bin/python")),
             "/usr/bin/python3"]
    for p in cands:
        if not p or not Path(p).exists():
            continue
        try:
            r = subprocess.run([p, "-c", "import fastapi, uvicorn, websockets"],
                               capture_output=True, timeout=25)
            if r.returncode == 0:
                return p
        except Exception:
            continue
    raise SystemExit("找不到能 import fastapi/uvicorn/websockets 的 python，用 FM_PY= 指定一个")


def port_free(port: int) -> bool:
    """端口是否可用（带 SO_REUSEADDR：跑完一轮会留下大量 TIME_WAIT，
    没有它会把「刚被自己用过的端口」误判成被占用）。"""
    s = socket.socket()
    try:
        s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        s.bind(("127.0.0.1", port))
        return True
    except OSError:
        return False
    finally:
        s.close()


def prod_pids() -> list[int]:
    """生产 instance 的 PID（只用来事后核对「没被碰过」，不用于杀进程）。

    判定：命令行里带 `family-message/server/run.py`，但**不在本仓库路径下**
    （本仓库跑出来的测试实例不算生产）。
    """
    try:
        out = subprocess.run(["ps", "-eo", "pid,cmd"], capture_output=True,
                             text=True, timeout=15).stdout
    except Exception:
        return []
    mine = str(ROOT)
    pids = []
    for line in out.splitlines()[1:]:
        parts = line.split(None, 1)
        if len(parts) != 2:
            continue
        pid, cmd = parts
        if "family-message/server/run.py" in cmd and "grep" not in cmd \
                and mine not in cmd:
            try:
                pids.append(int(pid))
            except ValueError:
                pass
    return sorted(pids)


class TestServer:
    """自起的测试实例；只按**精确 PID** 停止。"""

    def __init__(self, port: int, python: str):
        self.port = port
        self.python = python
        self.data_dir = Path("/tmp") / f"fm_wskick_{port}_{uuid.uuid4().hex[:6]}"
        self.log_path = self.data_dir / "server.log"
        self.proc: subprocess.Popen | None = None
        self._logf = None

    @property
    def base(self) -> str:
        return f"http://127.0.0.1:{self.port}"

    @property
    def wsbase(self) -> str:
        return f"ws://127.0.0.1:{self.port}"

    def start(self) -> None:
        self.data_dir.mkdir(parents=True, exist_ok=True)
        env = dict(
            os.environ,
            FM_DATA_DIR=str(self.data_dir),
            FM_PORT=str(self.port),
            TRIM_SERVICE_PORT=str(self.port),   # run.py 优先读它
            FM_ENROLL_TOKEN=ENROLL,
            FM_WEB_PASSWORD="",                 # 测试实例不设口令，HTTP API 免登录
            PYTHONUNBUFFERED="1",
        )
        self._logf = open(self.log_path, "wb")
        self.proc = subprocess.Popen(
            [self.python, str(SERVER_DIR / "run.py")],
            cwd=str(SERVER_DIR), env=env,
            stdout=self._logf, stderr=subprocess.STDOUT, start_new_session=True,
        )
        print(f"  测试实例已启动 pid={self.proc.pid} port={self.port} "
              f"data_dir={self.data_dir}")

    def stop(self) -> None:
        if self.proc is None:
            return
        pid = self.proc.pid
        if self.proc.poll() is None:
            print(f"  停止测试实例 pid={pid}（精确 PID，非关键字 kill）…")
            self.proc.terminate()
            try:
                self.proc.wait(timeout=8)
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait(timeout=5)
        print(f"  测试实例已停止 pid={pid} exit={self.proc.returncode}")
        if self._logf:
            self._logf.close()

    def log_text(self) -> str:
        try:
            return self.log_path.read_text(errors="replace")
        except Exception:
            return ""


# ============================================================
# 假 Agent：一条连接 / 一个带自动重连的实例
# ============================================================
class Conn:
    """一条 WebSocket 连接：后台收帧 + 记录关闭码。"""

    def __init__(self, tag: str, ws, on_message=None):
        self.tag = tag
        self.ws = ws
        self.msgs: list[dict] = []
        self.close_code: int | None = None
        self.close_reason = ""
        self.closed = asyncio.Event()
        self._on_message = on_message
        self._task = asyncio.create_task(self._pump())

    async def _pump(self) -> None:
        while True:
            try:
                raw = await self.ws.recv()
            except websockets.exceptions.ConnectionClosed as e:
                rcvd = getattr(e, "rcvd", None)
                self.close_code = getattr(rcvd, "code", None)
                if self.close_code is None:
                    self.close_code = getattr(e, "code", None)
                self.close_reason = (getattr(rcvd, "reason", None)
                                     or getattr(e, "reason", "") or "")
                break
            except asyncio.CancelledError:
                raise
            except Exception as e:
                self.close_code = -1
                self.close_reason = f"{type(e).__name__}: {e}"
                break
            try:
                frame = json.loads(raw)
            except Exception:
                continue
            self.msgs.append(frame)
            if self._on_message:
                self._on_message(frame)
        if self.close_code is None:
            self.close_code = getattr(self.ws, "close_code", None)
        self.closed.set()

    async def wait_hello(self, timeout: float = 6.0) -> dict:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            for f in self.msgs:
                if f.get("type") == "hello":
                    return f
            if self.closed.is_set():
                return {}
            await asyncio.sleep(0.05)
        return {}

    async def wait_closed(self, timeout: float = 3.0) -> int | None:
        try:
            await asyncio.wait_for(self.closed.wait(), timeout)
        except asyncio.TimeoutError:
            return None
        return self.close_code

    async def close(self) -> None:
        """关掉这条连接。**必须带超时**：对踢现场里服务端可能已经把我们顶掉，
        此刻 websockets 的 close 会等一个永远等不到的 close 应答，把脚本卡住。"""
        try:
            await asyncio.wait_for(self.ws.close(), 2.0)
        except Exception:
            pass


def dev_url(wsbase: str, device_id: str, name: str) -> str:
    return f"{wsbase}/ws/device/{device_id}?" + urlencode({
        "token": "", "name": name, "type": "pc", "platform": "kick-test",
        "agent_version": "kick-test", "enroll_token": ENROLL,
    })


async def connect_one(wsbase: str, device_id: str, name: str) -> Conn:
    ws = await websockets.connect(dev_url(wsbase, device_id, name), open_timeout=6)
    return Conn(name, ws)


class Kicker:
    """带自动重连的假 Agent：被顶替后**立刻**重连（复刻当前 PC 端的对踢行为）。"""

    def __init__(self, tag: str, wsbase: str, device_id: str, name: str):
        self.tag = tag
        self.url = dev_url(wsbase, device_id, name)
        self.received: dict[int, str] = {}     # message_id -> content
        self.frames = 0
        self.kicked = 0                        # 被 4000 顶替的次数
        self.connect_errors = 0
        self.conns: list[Conn] = []
        self.cur: Conn | None = None
        self.running = True
        self._task: asyncio.Task | None = None

    def _on_message(self, frame: dict) -> None:
        if frame.get("type") == "message":
            self.received.setdefault(frame.get("message_id"), frame.get("content"))
            self.frames += 1

    async def _loop(self) -> None:
        while self.running:
            try:
                ws = await websockets.connect(self.url, open_timeout=5)
            except Exception:
                self.connect_errors += 1
                await asyncio.sleep(0.15)
                continue
            c = Conn(self.tag, ws, on_message=self._on_message)
            self.conns.append(c)
            self.cur = c
            if not self.running:      # stop() 刚好发生在这条连接建立之后
                await c.close()
                break
            await c.closed.wait()
            self.cur = None
            if c.close_code == 4000:
                self.kicked += 1
            if not self.running:
                break
            # 不给退避：立刻重连 —— 这正是「对踢」的来源
        await asyncio.sleep(0)

    def start(self) -> None:
        self._task = asyncio.create_task(self._loop())

    async def stop(self) -> None:
        self.running = False
        # 关掉还没关的连接（不只是 cur）：stop 可能刚好落在两次连接的缝隙里。
        # 并发关 + 整体超时，否则几千条连接一条条等会把脚本拖死。
        live = [c for c in self.conns if not c.closed.is_set()]
        if live:
            try:
                await asyncio.wait_for(
                    asyncio.gather(*(c.close() for c in live), return_exceptions=True),
                    3.0,
                )
            except Exception:
                pass
        if self._task:
            try:
                await asyncio.wait_for(self._task, 3)
            except (asyncio.TimeoutError, asyncio.CancelledError):
                self._task.cancel()
                try:
                    await self._task
                except (asyncio.CancelledError, Exception):
                    pass


# ============================================================
# 工具
# ============================================================
async def wait_health(http: httpx.AsyncClient, timeout: float = 25.0) -> dict | None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            r = await http.get("/healthz")
            if r.status_code == 200:
                return r.json()
        except Exception:
            pass
        await asyncio.sleep(0.25)
    return None


async def send_message(http: httpx.AsyncClient, content: str) -> int | None:
    r = await http.post("/api/messages",
                        json={"sender_name": "对踢复现脚本", "content": content})
    r.raise_for_status()
    return (r.json().get("message") or {}).get("id")


async def wait_frame(conn: Conn, message_id: int, timeout: float = 3.0) -> dict | None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        for f in conn.msgs:
            if f.get("type") == "message" and f.get("message_id") == message_id:
                return f
        await asyncio.sleep(0.05)
    return None


# ============================================================
# 阶段 1：确定性验证「第二条顶掉第一条」
# ============================================================
async def phase1(wsbase: str, device_id: str, http: httpx.AsyncClient) -> None:
    print("\n── 阶段 1：确定性验证「同一 device_id 的第二条连接顶掉第一条」")

    a = await connect_one(wsbase, device_id, "对踢测试-headless")
    hello_a = await a.wait_hello()
    check("第 1 条（headless，无界面）注册成功", bool(hello_a.get("device_id")),
          f"device_id={hello_a.get('device_id')}")

    b = await connect_one(wsbase, device_id, "对踢测试-交互式")
    hello_b = await b.wait_hello()
    check("第 2 条（交互式）用**同一 device_id** 注册成功",
          bool(hello_b.get("device_id")), f"device_id={hello_b.get('device_id')}")

    code_a = await a.wait_closed(4.0)
    check("第 1 条被服务端主动关闭", code_a is not None, f"close_code={code_a}")
    check("关闭码 = 4000（replaced by new connection）", code_a == 4000,
          f"code={code_a} reason={a.close_reason!r}")

    hz = (await http.get("/healthz")).json()
    check("hub 里只留最后一条（/healthz online=1）", hz.get("online") == 1,
          f"online={hz.get('online')} web_clients={hz.get('web_clients')}")

    # 此刻 hub 持有的是第 2 条 —— 发一条消息看它落在谁身上
    mid = await send_message(http, "阶段1：这条应该落到第 2 条")
    if mid is None:
        check("阶段 1 消息发送成功", False, "HTTP API 没返回 message_id")
        await b.close()
        return
    got = await wait_frame(b, mid, 4.0)
    check("消息落到第 2 条（hub 持有的就是它）", got is not None,
          f"message_id={mid}")
    check("第 1 条（已被顶掉）收不到这条消息",
          not any(f.get("message_id") == mid for f in a.msgs))

    # 顺带观察：被顶掉的旧连接在 finally 里会把设备状态刷成 offline
    await asyncio.sleep(1.0)
    try:
        st = (await http.get(f"/api/devices/{device_id}")).json()
        note(f"观察：第 1 条被顶掉后设备的 DB 状态 status={st.get('status')!r}"
             f"（hub 在线={hz.get('online')}）—— "
             f"旧连接的 finally 会 set_offline，与「新连接在线」相矛盾")
    except Exception as e:
        note(f"观察失败：{type(e).__name__}: {e}")

    await b.close()


# ============================================================
# 阶段 2：两个「都会自动重连」的实例对踢
# ============================================================
async def phase2(wsbase: str, device_id: str, http: httpx.AsyncClient,
                 n_msgs: int, storm: float) -> dict:
    print(f"\n── 阶段 2：两个都会自动重连的实例互相对踢，"
          f"{storm:.0f} 秒内从 HTTP API 发 {n_msgs} 条消息")

    a = Kicker("headless", wsbase, device_id, "对踢测试-headless")
    b = Kicker("interactive", wsbase, device_id, "对踢测试-交互式")
    a.start()
    b.start()
    await asyncio.sleep(1.5)          # 让两条都连上，进入对踢循环

    sent: list[int] = []
    gap = storm / max(n_msgs, 1)
    for i in range(n_msgs):
        mid = await send_message(http, f"对踢测试-{i + 1}")
        if mid is not None:
            sent.append(mid)
        await asyncio.sleep(gap)
    await asyncio.sleep(1.0)          # 等在途帧落地
    await a.stop()
    await b.stop()

    only_a = [m for m in sent if m in a.received and m not in b.received]
    only_b = [m for m in sent if m in b.received and m not in a.received]
    both = [m for m in sent if m in a.received and m in b.received]
    none_got = [m for m in sent if m not in a.received and m not in b.received]

    print(f"\n  一轮下来：")
    print(f"    headless  被顶替 {a.kicked} 次，重连 {len(a.conns) - 1} 次，"
          f"收到 {len(a.received)}/{len(sent)} 条消息")
    print(f"    交互式    被顶替 {b.kicked} 次，重连 {len(b.conns) - 1} 次，"
          f"收到 {len(b.received)}/{len(sent)} 条消息")
    print(f"\n    {'message_id':>10} | {'headless':^9} | {'交互式':^9}  ← 每条消息落到了谁那里")
    print(f"    {'-' * 10:>10}-+-{'-' * 9:^9}-+-{'-' * 9:^9}")
    for m in sent:
        print(f"    {m:>10} | {('✓' if m in a.received else '✗'):^9} | "
              f"{('✓' if m in b.received else '✗'):^9}")

    print()
    check("对踢确实发生（两侧合计被顶替 > 0）", (a.kicked + b.kicked) > 0,
          f"headless {a.kicked} 次 / 交互式 {b.kicked} 次")
    check("复现成功：存在**只被一个客户端收到**的消息",
          bool(only_a or only_b),
          f"仅 headless {len(only_a)} 条 {only_a}；仅交互式 {len(only_b)} 条 {only_b}")
    check("（同一 device_id 的）消息从未两边同时收到 —— hub 只持有一条连接",
          not both, f"两边都收到 {len(both)} 条 {both}")
    check("没有消息彻底丢失（没连接时走离线补投）", not none_got,
          f"两边都没收到 {len(none_got)} 条 {none_got}")

    return {"sent": sent, "only_a": only_a, "only_b": only_b, "both": both,
            "none": none_got, "a_kicked": a.kicked, "b_kicked": b.kicked}


async def check_drain(srv: TestServer, device_id: str, wait: float = 3.0) -> None:
    """两个客户端都停了之后，连接台账应排空到 0（确认这套登记不会漏账）。

    ★ 2026-09-29：原来只等 `wait` 秒就读**最后一行** —— 但对踢压测会造出近 4000 条连接，
    服务端的关闭清理是排队的，3 秒不够（实测停在 24）。所以改成**轮询到 0（最多 20s）**
    并把下降过程打出来：能归零 = 清理滞后（不是漏账），一直不归零才是真漏账。
    """
    print(f"\n── 阶段 2b：两侧都断开后，连接台账应排空（轮询最多 20s）")

    def last_count() -> tuple[int, str]:
        lines = [l for l in srv.log_text().splitlines()
                 if l.startswith("[WS]") and f"device={device_id}" in l]
        last = lines[-1] if lines else "(无 [WS] 行)"
        m = re.search(r"connections=(\d+)", last)
        return (int(m.group(1)) if m else -1), last

    trace: list[int] = []
    n, last = last_count()
    deadline = time.time() + 20.0
    await asyncio.sleep(wait)
    while True:
        n, last = last_count()
        trace.append(n)
        if n == 0 or time.time() > deadline:
            break
        await asyncio.sleep(1.0)
    print(f"  最后一行：{last}")
    print(f"  台账下降过程（每秒采样）：{trace}")
    check("连接台账排空（connections=0，无残留登记）", n == 0, f"connections={n} 采样={trace}")


# ============================================================
# 阶段 3：把服务端日志里的证据摘出来
# ============================================================
def phase3(srv: TestServer, device_id: str) -> None:
    print("\n── 阶段 3：服务端日志里的证据（[WS][BIND] / [WS][MESSAGE]）")
    text = srv.log_text()
    lines = [l for l in text.splitlines() if l.startswith("[WS]")]

    bind_replaced = [l for l in lines if "[WS][BIND]" in l and "replaced=c" in l]
    binds = [l for l in lines if "[WS][BIND]" in l]
    msg_ok = [l for l in lines if "[WS][MESSAGE]" in l and "send=success" in l]
    msg_queued = [l for l in lines if "[WS][MESSAGE]" in l and "send=queued" in l]
    msg_failed = [l for l in lines if "[WS][MESSAGE]" in l and "send=failed" in l]
    conns = [l for l in lines if "[WS][CONN]" in l]
    unbinds = [l for l in lines if "[WS][UNBIND]" in l]

    print(f"  [WS][CONN]    建立连接            {len(conns)} 行")
    print(f"  [WS][BIND]    绑定/顶替           {len(binds)} 行"
          f"（其中 replaced=… 顶替 {len(bind_replaced)} 次）")
    print(f"  [WS][UNBIND]  解绑                {len(unbinds)} 行")
    print(f"  [WS][MESSAGE] send=success        {len(msg_ok)} 行")
    print(f"  [WS][MESSAGE] send=queued         {len(msg_queued)} 行")
    print(f"  [WS][MESSAGE] send=failed         {len(msg_failed)} 行")

    check("服务端也统计到了对踢（[WS][BIND] replaced=… 行数 > 0）",
          len(bind_replaced) > 0, f"{len(bind_replaced)} 次")

    def sample(title: str, rows: list[str], n: int = 3) -> None:
        print(f"\n  ── {title}（取前 {min(n, len(rows))} 条）")
        for l in rows[:n]:
            print(f"    {l}")
        if not rows:
            print("    （无）")

    sample("对踢现场：BIND 顶替", bind_replaced)
    sample("投递成功：MESSAGE send=success", msg_ok)
    if msg_queued:
        sample("当时没有在线连接：MESSAGE send=queued", msg_queued)
    if msg_failed:
        sample("写入失败：MESSAGE send=failed", msg_failed)
    sample("连接台账：CONN / UNBIND", conns + unbinds, 6)


# ============================================================
async def run(args) -> int:
    if args.port == PROD_PORT:
        raise SystemExit(f"端口 {PROD_PORT} 是生产实例，脚本拒绝使用")
    if not port_free(args.port):
        raise SystemExit(f"端口 {args.port} 已被占用，换个 --port")

    python = resolve_python()
    prod_before = prod_pids()
    srv = TestServer(args.port, python)
    srv.start()

    print(f"\n解释器 {python}")
    print(f"设备   {args.device}")
    if prod_before:
        print(f"生产实例 PID（本次绝不触碰）：{prod_before}")

    ok = 0
    try:
        async with httpx.AsyncClient(base_url=srv.base, timeout=15) as http:
            hz = await wait_health(http)
            if hz is None:
                print("❌ 测试实例没起来，日志尾部：")
                print("\n".join(srv.log_text().splitlines()[-20:]))
                return 1
            print(f"健康检查 {hz}")

            await phase1(srv.wsbase, args.device, http)
            stats = await phase2(srv.wsbase, args.device, http,
                                 args.messages, args.storm)
            await check_drain(srv, args.device)
            phase3(srv, args.device)

            # 清理测试设备（数据目录本就是 /tmp 下的临时目录）
            try:
                await http.delete(f"/api/devices/{args.device}")
            except Exception:
                pass

        print(f"\n通过 {len(PASS)} / 失败 {len(FAIL)}")
        for f in FAIL:
            print("  失败:", f)

        prod_after = prod_pids()
        if prod_before and prod_before == prod_after:
            print(f"✅ 生产实例未被触碰（pid {prod_after} 原样存活）")
        elif prod_before:
            print(f"⚠️  生产实例 PID 集合变化：{prod_before} → {prod_after}")

        print(f"服务端日志：{srv.log_path}")
        ok = 1 if FAIL else 0
        return ok
    finally:
        if not args.keep:
            srv.stop()
        else:
            print(f"  --keep：测试实例仍在运行 pid={srv.proc.pid}，"
                  f"请自行按精确 PID 停止")


def main() -> int:
    ap = argparse.ArgumentParser(description="复现同一 device_id 两条连接对踢")
    ap.add_argument("--port", type=int,
                    default=int(os.environ.get("FM_KICK_PORT", "18896")))
    ap.add_argument("--device", default=f"pc_kick_{uuid.uuid4().hex[:6]}")
    ap.add_argument("--messages", type=int, default=8, help="风暴期间从 HTTP API 发的消息数")
    ap.add_argument("--storm", type=float, default=10.0, help="对踢风暴持续秒数")
    ap.add_argument("--keep", action="store_true", help="跑完不停止测试实例")
    return asyncio.run(run(ap.parse_args()))


if __name__ == "__main__":
    sys.exit(main())
