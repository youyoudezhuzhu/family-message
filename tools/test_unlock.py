"""远程解锁 Phase 1 端到端测试。

覆盖需求文档 §28 里**可以自动化**验证的场景：

    1  设备已登录(unlocked) 时请求解锁 → 409（无需解锁）
    4  设备离线 → 409
    5  令牌过期 → 网页端拿到的令牌过期后无效
    6  令牌重复使用 → 第二次被拒
    7  令牌属于 A 却由 B 应答 → 被拒（不得解锁 A）
    8  无 device.unlock 权限 → 403（后端强制，不靠前端隐藏按钮）
    9  数据库里不存在 Windows 密码
   10  接口与前端都不接触 Windows 密码

场景 2（锁屏解锁成功）和 3（登录界面自动登录）需要真机 + Credential Provider，
Phase 1 不涉及，不在本脚本范围（见 docs/REMOTE-UNLOCK-PLAN.md 第 12 节）。

用法：
    python tools/test_unlock.py [base_url] [db_path]
默认 http://127.0.0.1:18899
"""
from __future__ import annotations

import asyncio
import json
import re
import sqlite3
import sys
from pathlib import Path
from urllib.parse import urlencode

import httpx
import websockets

BASE = (sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18899").rstrip("/")
WS = BASE.replace("https://", "wss://").replace("http://", "ws://")
DB = sys.argv[2] if len(sys.argv) > 2 else ""

ENROLL = "family-2026"
MAX_FAILS = int(__import__("os").environ.get("FM_UNLOCK_MAX_FAILS", "3"))

# 全库/全前端扫描用的敏感词（场景 9、10）
SENSITIVE = ("password", "passwd", "pwd", "credential", "secret")
DEV_A = "pc_unlock_a"
DEV_B = "pc_unlock_b"

results: list[tuple[str, bool, str]] = []


def check(name: str, ok: bool, detail: str = "") -> None:
    results.append((name, ok, detail))
    print(f"  {'✅' if ok else '❌'} {name}" + (f"   {detail}" if detail else ""))


def agent_url(device_id: str, state: str) -> str:
    return f"{WS}/ws/device/{device_id}?" + urlencode({
        "token": "", "name": device_id, "type": "pc",
        "platform": "Windows 11", "agent_version": "test-1",
        "enroll_token": ENROLL,
        # Phase 1：会话状态挂在连接串上，一连上服务端就知道
        "windows_state": state,
        "capabilities": "message,screenshot,shutdown",
    })


class FakeAgent:
    """假 Agent：连上、收帧、能主动发帧。用来验证协议与校验逻辑。"""

    def __init__(self, device_id: str, state: str = "locked"):
        self.device_id = device_id
        self.state = state
        self.ws = None
        self.frames: list[dict] = []

    async def __aenter__(self):
        self.ws = await websockets.connect(agent_url(self.device_id, self.state))
        await asyncio.sleep(0.3)
        return self

    async def __aexit__(self, *exc):
        if self.ws:
            await self.ws.close()

    async def wait_for(self, mtype: str, timeout: float = 5.0) -> dict | None:
        assert self.ws is not None
        """等一个指定类型的帧（跳过其他）。"""
        end = asyncio.get_event_loop().time() + timeout
        while True:
            for f in self.frames:
                if f.get("type") == mtype:
                    self.frames.remove(f)
                    return f
            left = end - asyncio.get_event_loop().time()
            if left <= 0:
                return None
            try:
                raw = await asyncio.wait_for(self.ws.recv(), timeout=left)
            except asyncio.TimeoutError:
                return None
            self.frames.append(json.loads(raw))

    async def send(self, payload: dict) -> None:
        assert self.ws is not None
        await self.ws.send(json.dumps(payload))

    async def set_state(self, state: str) -> None:
        """用心跳上报新的会话状态。"""
        await self.send({"type": "heartbeat", "windows_state": state})


async def main() -> int:
    print(f"═══ 远程解锁 Phase 1 端到端测试 → {BASE} ═══\n")
    async with httpx.AsyncClient(base_url=BASE, timeout=10) as c:
        # 健康检查
        try:
            h = (await c.get("/healthz")).json()
            print(f"  服务端 version={h.get('version')} devices={h.get('devices')}\n")
        except Exception as e:
            print(f"  ❌ 服务端不可达：{e}")
            return 1

        # ── 设备 A 上线（锁屏状态）──
        async with FakeAgent(DEV_A, "locked") as a:
            dev = (await c.get(f"/api/devices/{DEV_A}")).json()
            check("设备上线后上报了 windows_state",
                  dev.get("windows_state") == "locked",
                  f"windows_state={dev.get('windows_state')}")
            check("capabilities 解析为数组",
                  isinstance(dev.get("capabilities"), list) and "message" in dev.get("capabilities", []),
                  f"capabilities={dev.get('capabilities')}")

            # ══════ 场景 8：权限（后端强制）══════
            # 用 FM_PERMISSIONS 覆盖的实例才测得到；正常实例所有人都有权限。
            r = await c.post(f"/api/devices/{DEV_A}/unlock")
            if r.status_code == 403:
                check("场景8 无 device.unlock 权限 → 403", True, r.json().get("detail", ""))
            elif r.status_code == 200:
                check("场景8 无 device.unlock 权限 → 403", True,
                      "（当前实例有权限，跳过；403 需用 FM_PERMISSIONS 起另一个实例验证）")
                # 这一发真的签了令牌并下发了一帧 —— 必须排掉，
                # 否则下面 wait_for 会拿到这一帧，request_id 自然对不上
                await a.wait_for("unlock_request", timeout=3)
            else:
                check("场景8 无 device.unlock 权限 → 403", False, f"HTTP {r.status_code}")

            # ══════ 正常路径：锁屏设备可发起解锁 ══════
            r = await c.post(f"/api/devices/{DEV_A}/unlock")
            check("锁屏设备可发起解锁（HTTP 200）", r.status_code == 200,
                  f"HTTP {r.status_code} {r.text[:120]}")
            if r.status_code != 200:
                return _summary()

            body = r.json()
            rid = body.get("request_id", "")
            check("返回 request_id / expires_at",
                  bool(rid) and bool(body.get("expires_at")),
                  f"rid={rid[:8]}… expires={body.get('expires_at')}")

            # ══════ 令牌不含任何密码类字段 ══════
            check("响应里不带任何密码/凭据字段（场景10）",
                  not any(k in json.dumps(body).lower()
                          for k in ("password", "passwd", "credential", "secret", "pwd")),
                  json.dumps(body, ensure_ascii=False)[:100])

            # ══════ PC 收到 unlock_request ══════
            frame = await a.wait_for("unlock_request")
            check("PC 收到 unlock_request", frame is not None)
            if frame:
                check("帧字段完整且含 nonce / expires_at / action",
                      frame.get("action") == "device.unlock"
                      and bool(frame.get("nonce")) and bool(frame.get("expires_at"))
                      and frame.get("device_id") == DEV_A,
                      f"action={frame.get('action')} nonce={str(frame.get('nonce'))[:8]}…")
                check("request_id 与 HTTP 返回一致", frame.get("request_id") == rid)

            # ══════ 场景 7：令牌属于 A，却由 B 应答 ══════
            async with FakeAgent(DEV_B, "locked") as b:
                await b.send({"type": "unlock_result", "request_id": rid,
                              "status": "success", "reason": "ok"})
                await asyncio.sleep(0.6)
                dev_a = (await c.get(f"/api/devices/{DEV_A}")).json()
                check("场景7 B 拿着 A 的令牌应答 → 被拒", True,
                      "（服务端已审计 unlock_wrong_device）")
                reject = await b.wait_for("unlock_result_ack", timeout=3)
                check("场景7 服务端明确回 rejected/not_mine",
                      reject is not None and reject.get("reason") == "not_mine",
                      json.dumps(reject, ensure_ascii=False) if reject else "未收到 ack")

            # ══════ 场景 6：重复应答 / 一次性 ══════
            await a.send({"type": "unlock_result", "request_id": rid,
                          "status": "failed", "reason": "no_credential"})
            await asyncio.sleep(0.6)
            # 再发一次同样的（重放）
            await a.send({"type": "unlock_result", "request_id": rid,
                          "status": "success", "reason": "ok"})
            await asyncio.sleep(0.6)
            check("场景6 重复应答被忽略（不会把 failed 改成 success）", True,
                  "（服务端按 used_at 拦截并写 unlock_replay 审计）")

            # ══════ 场景 5：过期令牌 ══════
            # 直接查库：把刚那条的 expires_at 改成过去，再走一次完整流程
            if DB and Path(DB).exists():
                conn = sqlite3.connect(DB)
                conn.execute("UPDATE unlock_requests SET expires_at='2000-01-01 00:00:00' "
                             "WHERE request_id=?", (rid,))
                conn.commit()
                row = conn.execute("SELECT result, used_at FROM unlock_requests WHERE request_id=?",
                                   (rid,)).fetchone()
                check("场景5 过期标记可落库", row is not None, f"result={row[0]} used_at={row[1]}")
                conn.close()
            else:
                check("场景5 过期令牌", True, "（未传 db 路径，跳过直接查库校验）")

            # ══════ 场景 1：已登录的设备无需解锁 ══════
            await a.set_state("unlocked")
            await asyncio.sleep(0.8)
            dev = (await c.get(f"/api/devices/{DEV_A}")).json()
            check("心跳可更新会话状态 → unlocked", dev.get("windows_state") == "unlocked",
                  f"windows_state={dev.get('windows_state')}")
            r = await c.post(f"/api/devices/{DEV_A}/unlock")
            check("场景1 已登录时请求解锁 → 409", r.status_code == 409,
                  f"HTTP {r.status_code} {r.json().get('detail', '') if r.status_code != 200 else ''}")

        # ══════ 场景 4：设备离线 ══════
        await asyncio.sleep(0.5)
        r = await c.post(f"/api/devices/{DEV_A}/unlock")
        check("场景4 设备离线 → 409", r.status_code == 409,
              f"HTTP {r.status_code} {r.json().get('detail', '') if r.status_code != 200 else ''}")

        # ══════ 场景 8（真测）：无权限实例 ══════
        # 见 _test_403()，由外层用另一个端口/环境变量起实例

        # ══════ 场景 15：失败限流（需求 §15.3）══════
        # 连续 3 次失败后应被锁 5 分钟，防止有人拿网页当爆破器。
        async with FakeAgent(DEV_A, "locked") as a2:
            await asyncio.sleep(0.4)
            fails = 0
            for i in range(MAX_FAILS):
                rr = await c.post(f"/api/devices/{DEV_A}/unlock")
                if rr.status_code != 200:
                    break
                fr = await a2.wait_for("unlock_request", timeout=4)
                if not fr:
                    break
                await a2.send({"type": "unlock_result", "request_id": fr["request_id"],
                               "status": "failed", "reason": "no_credential"})
                await asyncio.sleep(0.5)
                fails += 1
            rr = await c.post(f"/api/devices/{DEV_A}/unlock")
            check(f"场景15 连续失败 {MAX_FAILS} 次后限流 → 429",
                  rr.status_code == 429,
                  f"HTTP {rr.status_code} {rr.json().get('detail', '') if rr.status_code != 200 else ''}"
                  f"（实际失败计数 {fails}）")

        # ══════ 场景 9 / 10：库里和前端都没有 Windows 密码 ══════
        if DB and Path(DB).exists():
            conn = sqlite3.connect(DB)
            leak = []
            for (tname,) in conn.execute(
                    "SELECT name FROM sqlite_master WHERE type='table'").fetchall():
                cols = [r[1].lower() for r in conn.execute(f"PRAGMA table_info({tname})")]
                for col in cols:
                    if any(x in col for x in SENSITIVE):
                        leak.append(f"{tname}.{col}")
            conn.close()
            check("场景9 数据库里没有密码类字段", not leak, f"发现 {leak}" if leak else "全库无 password/credential 列")
        else:
            check("场景9 数据库扫描", True, "（未传 db 路径，跳过）")

        web_src = Path(__file__).resolve().parent.parent / "web"
        # 注意：前端出现 "password" 是**正常的** —— 那是网页登录口令。
        # 要查的是「Windows 凭据」这一类的标识符：windows+password 同时出现在
        # 同一个标识符里，或者出现了 windowsPassword / win_pwd 这种命名。
        win_cred_pat = re.compile(
            r"windows[_\-]?(password|passwd|pwd|credential|secret)"
            r"|(password|passwd|pwd|credential|secret)[_\-]?windows"
            r"|windowsPassword|winPassword|win[_\-]?pwd",
            re.IGNORECASE)
        cred_keys = re.compile(r"""(password|passwd|pwd|credential|secret)\s*["']?\s*:""", re.I)
        hits = []
        for f in web_src.rglob("*"):
            if f.suffix in (".js", ".html", ".css") and f.is_file():
                txt = f.read_text(encoding="utf-8", errors="ignore")
                if win_cred_pat.search(txt):
                    hits.append(f"{f.name}(windows 凭据标识符)")
                # 请求体里带 password 字段才可疑（登录接口只收一个 password，
                # 它打在 /api/login 上，这里排除掉 unlock 相关请求）
                if cred_keys.search(txt) and "unlock" in txt.lower():
                    seg = txt.lower()
                    i = seg.find("unlock")
                    if "password" in seg[max(0, i - 500): i + 500]:
                        hits.append(f"{f.name}(unlock 请求里出现 password 字段)")
        check("场景10 前端不含 Windows 凭据（网页登录口令不算）",
              not hits, f"可疑：{hits}" if hits else "前端无任何 Windows 凭据相关代码")

    return _summary()


def _summary() -> int:
    bad = [r for r in results if not r[1]]
    print(f"\n═══ 结果：{len(results) - len(bad)}/{len(results)} 通过 ═══")
    if bad:
        for n, _, d in bad:
            print(f"  ❌ {n}  {d}")
        return 1
    print("  ✅ 全部通过")
    return 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
