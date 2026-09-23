"""端到端验证：米家绑定规则 + 网页端「关机」指令下发。

重点验证：
  1. 绑定「米家设备 + 动作 → PC」后，/api/devices 里那台 PC 带上 xiaomi 信息
     （网页端据此决定是否显示「开机」按钮）
  2. 「开机」按钮会去找绑定并执行对应动作（没授权时给出明确错误，而不是静默）
  3. 「关机」按钮能把 shutdown 帧真的推到 PC Agent 的 WebSocket 上
"""
import asyncio
import json
import sys
import urllib.error
import urllib.request
from uuid import uuid4

import websockets

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18899"
WS = BASE.replace("http://", "ws://").replace("https://", "wss://")
DEV = "pc_shutdown_probe"
PASS = FAIL = 0


def ok(m):
    global PASS
    PASS += 1
    print(f"  ✅ {m}")


def bad(m):
    global FAIL
    FAIL += 1
    print(f"  ❌ {m}")


def req(method, path, body=None):
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(BASE + path, data=data, method=method,
                               headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(r, timeout=15) as resp:
            return resp.status, json.loads(resp.read() or b"{}")
    except urllib.error.HTTPError as e:
        try:
            return e.code, json.loads(e.read() or b"{}")
        except Exception:
            return e.code, {}


async def main():
    print("① 绑定一条规则：米家插座（动作=开）→ 这台 PC")
    st, row = req("POST", "/api/xiaomi/devices", {
        "name": "书房插座", "miot_device_id": "fake_did_0001",
        "urn": "cuco.plug.v3", "power_action": "on",
        "target_device_id": DEV, "power_siid": 2, "power_piid": 1,
    })
    if st == 200 and row.get("power_action") == "on":
        ok(f"绑定成功 id={row['id']} 动作={row['power_action']}")
    else:
        bad(f"绑定失败 {st} {row}")

    print("② 把动作改成「关」，验证可选动作生效")
    st, row2 = req("PATCH", f"/api/xiaomi/devices/{row['id']}", {"power_action": "off"})
    if st == 200 and row2.get("power_action") == "off":
        ok("动作已改为 off")
    else:
        bad(f"改动作失败 {st} {row2}")
    req("PATCH", f"/api/xiaomi/devices/{row['id']}", {"power_action": "on"})

    print("③ 设备接入后，/api/devices 里应带上 xiaomi 绑定信息")
    url = (f"{WS}/ws/device/{DEV}?name=%E6%9C%BA%E6%88%BFPC&type=pc"
           f"&enroll_token=family-2026")
    async with websockets.connect(url) as ws:
        await asyncio.wait_for(ws.recv(), 10)          # hello

        st, devs = req("GET", "/api/devices")
        me = next((d for d in devs if d["device_id"] == DEV), None)
        if me and me.get("xiaomi") and me["xiaomi"]["name"] == "书房插座":
            ok(f"设备卡片拿到绑定：{me['xiaomi']['name']} / 动作={me['xiaomi']['power_action']}")
        else:
            bad(f"/api/devices 没带上 xiaomi：{me}")

        print("④ 在线时点「开机」→ 应直接说已在线，不该去动插座")
        st, r = req("POST", f"/api/devices/{DEV}/wake")
        if st == 200 and r.get("already_online"):
            ok("在线时返回 already_online（不会误操作米家）")
        else:
            bad(f"在线点开机结果不对 {st} {r}")

        print("⑤ 点「关机」→ shutdown 帧必须真的推到设备 WebSocket 上")
        st, r = req("POST", f"/api/devices/{DEV}/shutdown")
        if st != 200:
            bad(f"关机接口返回 {st} {r}")
        else:
            ok(f"关机接口 200：{r.get('message')}")
            try:
                frame = json.loads(await asyncio.wait_for(ws.recv(), 8))
            except asyncio.TimeoutError:
                frame = None
            if frame and frame.get("type") == "shutdown":
                ok(f"设备收到指令帧：{frame}")
            else:
                bad(f"设备没收到 shutdown 帧，收到的是 {frame}")

    print("⑥ 设备离线后点关机 → 应拒绝（409），不能假装成功")
    await asyncio.sleep(1.5)
    st, r = req("POST", f"/api/devices/{DEV}/shutdown")
    if st == 409:
        ok(f"离线时拒绝关机：{r.get('detail')}")
    else:
        bad(f"离线关机应 409，实际 {st} {r}")

    print("⑦ 离线后点「开机」→ 应真的去调米家（未授权必须给出明确 401/502）")
    st, r = req("POST", f"/api/devices/{DEV}/wake")
    if st in (401, 502):
        ok(f"离线开机去调米家并明确报错 HTTP {st}：{r.get('detail', '')[:44]}")
    else:
        bad(f"离线点开机结果不对 {st} {r}")

    print("⑧ 删除绑定 → 设备卡片上的 xiaomi 应消失")
    req("DELETE", f"/api/xiaomi/devices/{row['id']}")
    st, devs = req("GET", "/api/devices")
    me = next((d for d in devs if d["device_id"] == DEV), None)
    if me is not None and not me.get("xiaomi"):
        ok("删除绑定后设备卡片不再带 xiaomi（「开机」按钮会消失）")
    else:
        bad(f"删除绑定后仍有 xiaomi：{me}")

    print(f"\n结果：{PASS} 项通过，{FAIL} 项失败")
    return 0 if FAIL == 0 else 1


sys.exit(asyncio.run(main()))
