#!/usr/bin/env python3
"""消息昵称快照的**写端**验收（NAS 侧）—— `messages.sender_nickname_id` / `sender_color`。

背景（2026-09-29 勘察发现的缺口）：两列 + 索引 + 两端读端都在，**写端缺失** → 恒 `NULL`，
于是 `docs/NICKNAME-SYSTEM-PLAN.md` §7 Phase 5 的两条验收（「重创同名不污染历史」、
「灰临时消息 `sender_color='gray'`」）根本跑不出来。本脚本钉住补写端之后的语义：

| # | 场景 | 期望 |
|---|---|---|
| A | 带 `nickname_id` 发消息 | `sender_nickname_id` = 该 id，`sender_color` = **服务端查表**填的逻辑色 ID |
| B | 不传 `nickname_id`（老前端 / 老缓存） | 两列 **NULL**（走名字反查 → 哈希兜底，观感与改造前一致） |
| C | 明确传 `nickname_id: null`（灰临时） | `(NULL, 'gray')` —— 判灰只看 `sender_color == 'gray'` |
| D | 传伪造的 `color` | **忽略**（颜色永远由服务端查表决定） |
| E | ★ 重创同名 | 删「妈妈」→ 老消息的快照**一个字都不变**；重创的「妈妈」是新 id，老消息不挂到它上面 |
| F | 用已删昵称的 id 发消息 | 按灰临时记 `(NULL, 'gray')`（不写一个已失效的 id） |
| G | 设备帧 `reply` 带 `nickname_id` | 同样写入快照（PC 回复路径） |

⚠ 只起**测试实例**（默认 18899）；生产 18801 是硬拒绝端口，只在启动前探活一次。
用法：`python3 tools/test_nickname_snapshot.py`　退出码 0 = 全过。
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SERVER_DIR = ROOT / "server"
PROD_PORT = 18801

FAILS: list[str] = []


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


def main() -> int:
    port = int(os.environ.get("FM_TEST_PORT", "18899"))
    assert port != PROD_PORT, "拒绝在生产端口上跑测试"

    tmp = Path(tempfile.mkdtemp(prefix="fm-snap-"))
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
    env = {**os.environ, "FM_CONFIG": str(cfg)}
    proc = subprocess.Popen([sys.executable, str(SERVER_DIR / "run.py")], cwd=str(SERVER_DIR),
                            env=env, stdout=log, stderr=subprocess.STDOUT)
    base = f"http://127.0.0.1:{port}"
    try:
        for _ in range(60):
            if proc.poll() is not None:
                print("✗ 测试实例起不来，日志：", (tmp / "server.log").read_text()[-2000:])
                return 1
            try:
                if http("GET", f"{base}/healthz")[0] == 200:
                    break
            except OSError:
                time.sleep(0.5)
        else:
            print("✗ 等 healthz 超时")
            return 1
        print(f"═══ 消息昵称快照 · 写端验收 ═══\n  实例：{base}（临时库 {tmp / 'data' / 'family.db'}）")

        def send(content, **extra):
            body = {"sender_name": "妈妈", "content": content}
            body.update(extra)
            return http("POST", f"{base}/api/messages", body)

        def all_msgs():
            _, data = http("GET", f"{base}/api/messages")
            rows = data.get("messages") if isinstance(data, dict) else data
            return rows or []

        def last_msg():
            """最新一条。⚠ `GET /api/messages` 是**按 id 倒序**返回的（最新在前），取 [0]。"""
            rows = all_msgs()
            return rows[0] if rows else {}

        def new_nick(name):
            code, resp = http("POST", f"{base}/api/nicknames", {"display_name": name})
            assert code == 201, f"建昵称失败 {code} {resp}"
            return resp["nickname"]

        # 建两个共享昵称：妈妈 / 爸爸
        mom = new_nick("妈妈")
        dad = new_nick("爸爸")
        mom_id, mom_color = mom["nickname_id"], mom["color"]
        print(f"  昵称：妈妈 id={mom_id} color={mom_color}｜爸爸 id={dad['nickname_id']} color={dad['color']}")

        print("\n== A. 带 nickname_id：快照 = (id, 服务端查表色) ==")
        send("A 带昵称", nickname_id=mom_id)
        m = last_msg()
        check("A1 sender_nickname_id 写入", m.get("sender_nickname_id") == mom_id, m.get("sender_nickname_id"))
        check("A2 sender_color = 查表色（不是 gray）",
              m.get("sender_color") == mom_color and m.get("sender_color") != "gray", m.get("sender_color"))

        print("\n== B. 不传字段（老前端）→ 两列 NULL ==")
        send("B 老前端")
        m = last_msg()
        check("B1 两列都是 None", m.get("sender_nickname_id") is None and m.get("sender_color") is None,
              [m.get("sender_nickname_id"), m.get("sender_color")])

        print("\n== C. 明确传 null（灰临时）→ (NULL, 'gray') ==")
        send("C 灰临时", nickname_id=None)
        m = last_msg()
        check("C1 灰临时快照 = (NULL, 'gray')",
              m.get("sender_nickname_id") is None and m.get("sender_color") == "gray",
              [m.get("sender_nickname_id"), m.get("sender_color")])

        print("\n== D. 伪造 color 被忽略 ==")
        send("D 伪造色", nickname_id=mom_id, color="color_09", sender_color="color_09")
        m = last_msg()
        check("D1 仍是妈妈的真实颜色", m.get("sender_color") == mom_color, m.get("sender_color"))

        print("\n== E. ★ 重创同名不污染历史 ==")
        old_id = mom_id
        http("DELETE", f"{base}/api/nicknames/{mom_id}")
        before = [x for x in all_msgs() if x.get("content") == "A 带昵称"][-1]
        mom2 = new_nick("妈妈")
        send("E 重创后的新妈妈", nickname_id=mom2["nickname_id"])
        after = last_msg()
        check("E1 重创同名拿到新 id", mom2["nickname_id"] != old_id, [old_id, mom2["nickname_id"]])
        check("E2 老消息快照一个字都没变（仍指旧 id + 旧色）",
              before.get("sender_nickname_id") == old_id and before.get("sender_color") == mom_color,
              [before.get("sender_nickname_id"), before.get("sender_color")])
        check("E3 新消息挂在新行上（id 与颜色都是新的）",
              after.get("sender_nickname_id") == mom2["nickname_id"]
              and after.get("sender_color") == mom2["color"]
              and after.get("sender_nickname_id") != old_id,
              [after.get("sender_nickname_id"), after.get("sender_color")])

        print("\n== F. 用已删昵称的 id 发消息 → 按灰临时记 ==")
        send("F 已删 id", nickname_id=old_id)
        m = last_msg()
        check("F1 (NULL, 'gray')（不写失效 id）",
              m.get("sender_nickname_id") is None and m.get("sender_color") == "gray",
              [m.get("sender_nickname_id"), m.get("sender_color")])

        print("\n== G. 设备帧 reply 带 nickname_id（PC 回复路径）==")
        try:
            import websockets.sync.client as wsc
            dev_id = "pc_snap_test"
            with wsc.connect(f"ws://127.0.0.1:{port}/ws/device/{dev_id}"
                             "?name=%E4%B9%A6%E6%88%BF%E7%94%B5%E8%84%91&type=pc"
                             "&platform=test&enroll_token=FAMILY-TEST-TOKEN") as ws:
                hello = json.loads(ws.recv())          # hello
                ws.send(json.dumps({"type": "reply", "sender_name": "妈妈", "content": "G 设备回复",
                                    "client_id": "c1", "nickname_id": mom2["nickname_id"]}))
                while True:
                    frame = json.loads(ws.recv())
                    if frame.get("type") == "reply_ack":
                        break
            m = last_msg()
            check("G1 设备回复也写快照（id = 重创后的妈妈）",
                  m.get("sender_nickname_id") == mom2["nickname_id"], m.get("sender_nickname_id"))
            check("G2 颜色由服务端填（= 重创后的颜色）",
                  m.get("sender_color") == mom2["color"], m.get("sender_color"))
            check("G3 hello 正常（设备已登记）", hello.get("type") == "hello", hello.get("type"))
        except ImportError:
            print("    [SKIP] 本解释器没有 websockets，跳过设备帧路径")
        except Exception as e:                       # noqa: BLE001
            check("G 设备帧路径", False, repr(e))
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            proc.kill()
        log.close()

    print()
    if FAILS:
        print(f"✗ 失败 {len(FAILS)} 项：" + "；".join(FAILS))
        return 1
    print("✓ 快照写端全部通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
