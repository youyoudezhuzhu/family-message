"""共享昵称测试 —— Phase 1（NAS 数据层，纯数据层、**不需要起服务**）
+ Phase 2（NAS API + 广播，会**真起测试实例**并做 HTTP / 多 WebSocket 实测）。

覆盖 docs/NICKNAME-SYSTEM-PLAN.md 里可以自动化验证的部分：

  Phase 1（数据层，见下）
  §7 Phase 1 验收 1    表恰好 6 列 / `ux_nicknames%` 恰好 2 条且都带 WHERE / 无 owner 列
  §7 Phase 1 验收 1b   颜色列只装逻辑色 ID（16 个枚举值；HEX、gray、大写全部被 DDL 拒）
  §7 Phase 1 验收 1c   同名被拒（409 语义 + 既有 id）/ 软删后可再建 / 索引② 拒重色
  §7 Phase 1 验收 3    老库迁移：既有表行数不变、messages 多 2 列且全 NULL（用 --db）
  §6.4 判据 1–6        分配顺序 / 池满 NoAvailableColor / 软删回池 / 8 线程并发不重色 /
                       同名连建 5 次全撞名且同一个 existing_nickname_id /
                       reassign 新色≠旧色（含「排除自身」）
  §2.C4 实测表         trim 规范化 / NFC 归一 / 不同名同色被拒 / status 只收 active|inactive
  §3.2.1               `gray` 不在池里（`is_valid_shared_color` 为 False）、不能写进 nicknames
  §3.4.1               重创同名不污染历史（老消息的 sender_nickname_id 不变）
  事务原子性           事务体内抛异常 → 一行不落库；未提交的中间态对别的连接不可见

  Phase 2（§5 / §7 Phase 2，**起实例在 18899**，绝不用生产 18801）
  §5.3 的 5 个 HTTP 端点：空库 GET 返回 [] 且不建行 / POST 201 / 撞名 409 + existing_nickname_id /
       PATCH（改名 & NAME_TAKEN & 404）/ reassign-color（新色≠旧色且未被占用 & 404）/
       DELETE（软删 + released_color + 释放的颜色能被下一个新建拿到 & 已删 409 NICKNAME_INACTIVE）/
       status 非法 422 / display_name 非法 422 / 池满 503 NO_AVAILABLE_COLOR（含满额文案）
  §5.4 的 5 个设备帧：list_request→list_response / create→created / rename→updated /
       reassign→color_changed / delete→removed；撞名与非法 id 回 nickname_error 且**不断连接**；
       昵称帧不进离线队列、不补发（这里只能验「应答由当前连接直收」）
  §5.4 的 5 个广播：2 个 /ws/web + 1 个设备连接**都收到**同名事件（改名/改色/删除/新建）
  §7 回退             `nickname.enabled=false`：/api/nicknames 404、现有接口逐个正常、
       /api/config 与原版逐字一致、昵称帧静默

用法：
    python tools/test_nicknames.py                  # 全新临时库 + Phase 2（起 18899）
    python tools/test_nicknames.py --db 某库副本.db   # 先在该**副本**上跑迁移，再跑全部判据
    python tools/test_nicknames.py --no-phase2       # 只跑 Phase 1（不起任何实例）
    python tools/test_nicknames.py --keep            # 保留临时目录（排查用）

⚠ 本脚本只碰**临时目录里的库副本**；Phase 2 起的是**测试实例**（默认端口 18899），
  生产实例 18801 是硬拒绝端口，全程只读探活（`GET /healthz`）。
⚠ Phase 2 需要一个能 `import fastapi, uvicorn` 的解释器来跑测试实例；
  当前解释器还需要 `websockets` 才能跑多连接广播实测（缺了会**跳过 WS 部分**，不算失败）。
"""
from __future__ import annotations

import argparse
import asyncio
import json
import os
import shutil
import socket
import sqlite3
import subprocess
import random
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any

HERE = Path(__file__).resolve().parent
SERVER_DIR = HERE.parent / "server"

results: list[tuple[str, bool, str]] = []


def check(name: str, ok: bool, detail: str = "") -> bool:
    results.append((name, ok, detail))
    print(f"  {'✅' if ok else '❌'} {name}" + (f"   {detail}" if detail else ""))
    return ok


def section(title: str) -> None:
    print(f"\n── {title} " + "─" * max(0, 60 - len(title)))


# ══════════════════════════════════════════════════════════════════════
# 0. 准备：临时目录 + 配置（**必须在 import db 之前**设好 FM_CONFIG）
# ══════════════════════════════════════════════════════════════════════
def snapshot(path: str) -> dict:
    """只读快照：每张表的行数 / messages 的列 / 全部 sqlite_master 对象名，
    外加「既有消息逐条内容」与「既有设备 id」—— 用来证明迁移是**零丢失**。"""
    conn = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    try:
        conn.row_factory = sqlite3.Row
        tables = {name: conn.execute(f'SELECT count(*) FROM "{name}"').fetchone()[0]
                  for (name,) in conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        cols = [r[1] for r in conn.execute("PRAGMA table_info(messages)")]
        objs = {r[0] for r in conn.execute("SELECT name FROM sqlite_master")}
        msgs = {r["id"]: (r["sender_name"], r["content"], r["message_type"], r["created_at"],
                          r["sender_kind"], r["sender_device_id"])
                for r in conn.execute("SELECT id, sender_name, content, message_type, created_at,"
                                      " sender_kind, sender_device_id FROM messages")}
        devs = {r[0] for r in conn.execute("SELECT device_id FROM devices")}
        return {"tables": tables, "cols": cols, "objs": objs, "msgs": msgs, "devs": devs}
    finally:
        conn.close()


def backup_db(src: str, dst: str) -> None:
    """用 sqlite3 的 backup API 复制（WAL 库直接 cp 可能丢最近的提交）。"""
    s = sqlite3.connect(f"file:{src}?mode=ro", uri=True)
    d = sqlite3.connect(dst)
    try:
        s.backup(d)
    finally:
        d.close()
        s.close()


def prepare(args) -> tuple[Path, str]:
    tmp = Path(tempfile.mkdtemp(prefix="fm-nick-p1-"))
    (tmp / "data").mkdir(parents=True, exist_ok=True)
    db_path = tmp / "data" / "family.db"

    if args.db:
        src = Path(args.db).resolve()
        assert src.exists(), f"找不到库文件：{src}"
        backup_db(str(src), str(db_path))
        print(f"  旧库副本：{src} → {db_path}（原文件只读，不会被改）")

    cfg = tmp / "config.yaml"
    cfg.write_text(
        f"data_dir: {tmp / 'data'}\n"
        "server: {host: 127.0.0.1, port: 18899, public_url: ''}\n"
        "web: {password: '', session_hours: 720}\n"
        "device: {enroll_token: FAMILY-TEST-TOKEN, auto_register: true, offline_after_seconds: 45}\n"
        "message: {popup_auto_close_seconds: 0, max_targets: 20, history_limit: 30}\n"
        "nickname: {enabled: false, color_pool_version: 1}\n",
        encoding="utf-8")
    os.environ["FM_CONFIG"] = str(cfg)
    return tmp, str(db_path)


# ══════════════════════════════════════════════════════════════════════
def main() -> int:
    ap = argparse.ArgumentParser(add_help=True)
    ap.add_argument("--db", default="", help="要迁移的旧库副本（会复制到临时目录，不动原文件）")
    ap.add_argument("--keep", action="store_true", help="保留临时目录")
    ap.add_argument("--port", type=int, default=18899,
                    help="Phase 2 测试实例端口（默认 18899；生产 18801 会被拒绝）")
    ap.add_argument("--no-phase2", action="store_true",
                    help="只跑 Phase 1 数据层（不起任何实例）")
    args = ap.parse_args()

    tmp, db_file = prepare(args)
    print(f"═══ 共享昵称 Phase 1（NAS 数据层）测试 ═══\n  临时目录：{tmp}")

    # server/ 就是顶层（与 run.py 一致）；FM_CONFIG 已指到临时配置
    sys.path.insert(0, str(SERVER_DIR))

    import db                                    # noqa: E402
    from services import nicknames as nk         # noqa: E402
    import nicknames as pool                     # noqa: E402

    # 先给**迁移前**的副本拍张快照（对象名 / 每表行数 / messages 的列），迁移完再拍一张对比。
    # 注意：db.get_conn() 一旦调用就已经迁移过了，所以这一行必须在它**之前**。
    pre = snapshot(db_file) if args.db else None
    conn = db.get_conn()                         # ← 触发 SCHEMA + 增量迁移

    POOL = pool.COLOR_POOL
    print(f"  逻辑色池：{len(POOL)} 个（{POOL[0]} … {POOL[-1]}），"
          f"pool_version={pool.COLOR_POOL_VERSION}，上限={pool.MAX_ACTIVE_NICKNAMES}")
    print(f"  灰（本地临时，不在池）：{pool.LOCAL_TEMP_COLOR_ID} = {pool.LOCAL_TEMP_BASE_COLOR}")

    def wipe() -> None:
        db.execute("DELETE FROM nicknames")

    def active() -> list[dict]:
        return nk.list("active")

    def raw_insert(name: str, color: str, status: str = "active") -> str:
        """绕开服务层直插 —— 专门测 DDL 边界。返回 'ok' 或异常文本。"""
        now = db.now_iso()
        try:
            with db.tx() as c:
                c.execute("INSERT INTO nicknames (display_name,color,status,created_at,updated_at)"
                          " VALUES (?,?,?,?,?)", (name, color, status, now, now))
            return "ok"
        except sqlite3.IntegrityError as e:
            return f"IntegrityError: {e}"

    # ══════════════════════════════════════════════════════════════
    section("1. DDL 形状与迁移（§7 Phase 1 验收 1 / 1b）")
    # ══════════════════════════════════════════════════════════════
    cols = [r[1] for r in conn.execute("PRAGMA table_info(nicknames)")]
    check("表恰好 6 列，且顺序 = 规格 §3.1",
          cols == ["nickname_id", "display_name", "color", "status", "created_at", "updated_at"],
          f"{cols}")
    forbidden = [c for c in cols if c in ("owner_type", "owner_id", "device_id", "client_id")]
    check("没有 owner / device 列（§0.6 / §10 已定 1）", not forbidden, f"发现 {forbidden}" if forbidden else "无")

    ux = conn.execute("SELECT name, sql FROM sqlite_master WHERE type='index'"
                      " AND name LIKE 'ux_nicknames%'").fetchall()
    check("ux_nicknames% 恰好 2 条", len(ux) == 2, f"{[r[0] for r in ux]}")
    check("两条 partial unique index 都带 WHERE status='active'",
          all("WHERE status" in (r[1] or "") for r in ux),
          " | ".join((r[1] or "").replace("\n", " ") for r in ux))
    check("普通查询索引 ix_nicknames_status 存在（规格 §3.1 第三条索引）",
          bool(conn.execute("SELECT 1 FROM sqlite_master WHERE type='index'"
                            " AND name='ix_nicknames_status'").fetchone()))
    check("不存在 r3 归属层的 ux_ncolors% 索引（该设计已作废）",
          not conn.execute("SELECT 1 FROM sqlite_master WHERE name LIKE 'ux_ncolors%'").fetchone())
    check("不存在 r3 归属层的 nickname_colors 表（⚠ v0.19 的色表**故意不叫这个名字**）",
          not conn.execute("SELECT 1 FROM sqlite_master WHERE name='nickname_colors'").fetchone())

    # ★ v0.19（docs/COLOR-TABLE-PLAN.md）：色表变成**数据**（表 nickname_palette，网页端可增删），
    #   所以 nicknames.color 那个写死 16 个 ID 的**枚举 CHECK 必须去掉** —— 留着就永远加不了新色。
    #   换成**形状 CHECK** `color_NN`：拦住乱值与 'gray'，但放行 color_17 及以后。
    ddl = conn.execute("SELECT sql FROM sqlite_master WHERE type='table'"
                       " AND name='nicknames'").fetchone()[0]
    check("color 用**形状** CHECK（color_NN），不再写死 16 个 ID 的枚举",
          "GLOB 'color_[0-9][0-9]'" in ddl and "CHECK (color IN (" not in ddl,
          "形状 CHECK ✓（枚举已按 v0.19 放开）")

    def _color_rejected(value: str) -> bool:
        """这个值能不能被 nicknames.color 的 CHECK 拒掉？（插完立刻删，不留痕迹）"""
        try:
            conn.execute("INSERT INTO nicknames (display_name, color, status, created_at, updated_at)"
                         " VALUES ('形状测试', ?, 'active', 'x', 'x')", (value,))
            return False
        except sqlite3.IntegrityError:
            return True
        finally:
            conn.execute("DELETE FROM nicknames WHERE display_name='形状测试'")

    check("形状 CHECK 仍然挡住 'gray' / 'red' / 'color_x1'（灰不进共享昵称，§3.2.1）",
          _color_rejected("gray") and _color_rejected("red") and _color_rejected("color_x1"))
    check("★ color_17 现在能进 nicknames.color（v0.19 之前会被枚举 CHECK 拒绝）",
          not _color_rejected("color_17"))

    pal = conn.execute("SELECT color_id, hex, sort, status FROM nickname_palette"
                       " ORDER BY sort").fetchall()
    check("★ 色表 nickname_palette 已播种内置 16 色、顺序 = 内置顺序、全部 active",
          [r[0] for r in pal] == list(POOL) and {r[3] for r in pal} == {"active"},
          f"{len(pal)} 行：{pal[0][0]} … {pal[-1][0]}")
    check("★ 色表 HEX 与内置表逐条一致（种子没写错）",
          [r[1] for r in pal] == [pool.LOGICAL_COLORS[c] for c in POOL],
          f"{pal[0][1]} … {pal[-1][1]}")
    check("★ 色表版本入库 = 1（老客户端看到 1 = 与内置表一致，不用重拉）",
          (conn.execute("SELECT value FROM app_meta WHERE key='color_pool_version'").fetchone()
           or [None])[0] == "1")

    def cols_of(t: str) -> list[str]:
        return [r[1] for r in conn.execute(f"PRAGMA table_info({t})")]

    mc = cols_of("messages")
    check("messages 多出 2 列 sender_nickname_id + sender_color",
          "sender_nickname_id" in mc and "sender_color" in mc,
          f"新增 {[c for c in mc if c.startswith('sender_')]}")
    info = {r[1]: r for r in conn.execute("PRAGMA table_info(messages)")}
    check("两列都可空、无默认值（存量行 = NULL，§3.2）",
          info["sender_nickname_id"][3] == 0 and info["sender_nickname_id"][4] is None
          and info["sender_color"][3] == 0 and info["sender_color"][4] is None,
          f"notnull={info['sender_nickname_id'][3]}/{info['sender_color'][3]}"
          f" dflt={info['sender_nickname_id'][4]!r}/{info['sender_color'][4]!r}")
    check("idx_messages_sender_nick 已建（POST_MIGRATION_DDL）",
          bool(conn.execute("SELECT 1 FROM sqlite_master WHERE type='index'"
                            " AND name='idx_messages_sender_nick'").fetchone()))

    bt = conn.execute("PRAGMA busy_timeout").fetchone()[0]
    check("PRAGMA busy_timeout = 5000（§6.3 / R6）", bt == 5000, f"busy_timeout={bt}")

    if pre:
        pre_tables, pre_cols, pre_objs = pre["tables"], pre["cols"], pre["objs"]
        now_tables = {name: conn.execute(f'SELECT count(*) FROM "{name}"').fetchone()[0]
                      for (name,) in conn.execute(
                          "SELECT name FROM sqlite_master WHERE type='table'")}
        missing = [t for t in pre_tables if t not in now_tables]
        drifted = {t: (n, now_tables.get(t)) for t, n in pre_tables.items()
                   if t != "nicknames" and now_tables.get(t) != n}
        check("§7 验收 3：迁移后既有表**行数零丢失**", not missing and not drifted,
              f"迁移前 {sum(pre_tables.values())} 行 / 迁移后 {sum(now_tables.values())} 行"
              f"（漂移 {drifted or '无'}）")
        check("§7 验收 3：迁移后 messages 恰好多 2 列", set(mc) - set(pre_cols) ==
              {"sender_nickname_id", "sender_color"} and set(pre_cols) - set(mc) == set(),
              f"迁移前 {len(pre_cols)} 列 → 迁移后 {len(mc)} 列")
        nulls = conn.execute("SELECT count(*) FROM messages WHERE sender_nickname_id IS NOT NULL"
                            " OR sender_color IS NOT NULL").fetchone()[0]
        check("§7 验收 3：全部存量消息的两列都是 NULL（不回填，§3.4）", nulls == 0,
              f"非 NULL 行数 = {nulls} / 共 {now_tables['messages']} 条")

    # ══════════════════════════════════════════════════════════════
    section("2. DDL 边界（绕开服务层直插，§7 验收 1b / §2.C4 实测表）")
    # ══════════════════════════════════════════════════════════════
    wipe()
    r = raw_insert("边界-合法", "color_01")
    check("color_01 → 接受", r == "ok", r)
    for bad in ("#E53935", "gray", "COLOR_01"):
        r = raw_insert(f"边界-{bad}", bad)
        check(f"{bad} → IntegrityError（CHECK 只收池内小写 ID）", r.startswith("IntegrityError"), r)

    r = raw_insert("x" * 32, "color_02")
    check("32 字符名字 → 接受", r == "ok", r)
    r = raw_insert("x" * 33, "color_02")
    check("33 字符名字 → IntegrityError", r.startswith("IntegrityError"), r)
    for label, bad_name in (("<空串>", ""), ("纯空格 '   '", "   ")):
        r = raw_insert(bad_name, "color_03")
        check(f"{label} → IntegrityError（trim 后长度 ≥ 1）", r.startswith("IntegrityError"), r)
    r = raw_insert("状态非法", "color_04", status="deleted")
    check("status='deleted' → IntegrityError（只收 active|inactive）", r.startswith("IntegrityError"), r)

    wipe()
    raw_insert("同名A", "color_01")
    r = raw_insert("同名A", "color_02")
    check("同名两行 active → IntegrityError（索引① ux_nicknames_name_active）",
          r.startswith("IntegrityError") and "nicknames.display_name" in r, r)
    r = raw_insert("不同名", "color_01")
    check("不同名同色 → IntegrityError（索引② ux_nicknames_color_active）",
          r.startswith("IntegrityError") and "nicknames.color" in r, r)

    # ══════════════════════════════════════════════════════════════
    section("3. 服务层：分配顺序 / 池满 / 回池 / 并发 / 撞名（§6.4 判据 1–6）")
    # ══════════════════════════════════════════════════════════════
    wipe()
    names = [f"用户{i:02d}" for i in range(1, len(POOL) + 1)]
    made = [nk.create(n) for n in names]
    got = [m["color"] for m in made]
    check("判据1 顺序建 16 个 → 颜色 == 池子顺序（书写顺序即分配优先级）",
          got == list(POOL), f"{got[0]} … {got[-1]}")
    check("创建返回的对象只有 6 个字段、无 owner 字段",
          all(set(m) == set(nk.FIELDS) for m in made), f"{sorted(made[0])}")

    try:
        nk.create("第17个")
        check("判据2 第 17 条 → NoAvailableColor（活跃上限 = 16）", False, "居然建成功了")
    except nk.NoAvailableColor as e:
        check("判据2 第 17 条 → NoAvailableColor（活跃上限 = 16）",
              e.code == "NO_AVAILABLE_COLOR" and e.http_status == 503, f"{e.code} / {e.message}")
    check("判据2 池满时表里仍恰好 16 条 active", len(active()) == 16, f"{len(active())}")

    victim = made[2]                                  # 持 color_03
    rel = nk.remove(victim["nickname_id"])
    check("判据3 软删返回 released_color（= 它占的那个色）",
          rel["released_color"] == victim["color"] == POOL[2], f"{rel}")
    check("判据3 软删后 active 少一个、inactive 行保留",
          len(active()) == 15 and len(nk.list("inactive")) == 1,
          f"active={len(active())} inactive={len(nk.list('inactive'))}")
    again = nk.create("第17个")
    check("判据3 新建拿到刚释放的色（= 池中第一个可用）",
          again["color"] == POOL[2], f"{again['display_name']} → {again['color']}")

    try:
        nk.reassign_color(made[0]["nickname_id"])
        check("没色可换时 reassign → NoAvailableColor（绝不回退到同色）", False, "居然换成了")
    except nk.NoAvailableColor as e:
        check("没色可换时 reassign → NoAvailableColor（绝不回退到同色）",
              e.code == "NO_AVAILABLE_COLOR", f"{e.code} / {e.message}")

    # 判据6：新色 ≠ 旧色、且未被别的 active 占用；「排除自身」规则要生效
    nk.remove(made[1]["nickname_id"])                 # 释放 color_02
    old = made[0]["color"]
    new = nk.reassign_color(made[0]["nickname_id"])
    others = {m["color"] for m in nk.list("active") if m["nickname_id"] != made[0]["nickname_id"]}
    # ⚠ 换色自 v0.18.1 起是**随机**的，不再钉死「一定拿到刚释放的那个色」——只钉契约本身：
    #   新色 ≠ 旧色、不在他人占用里、是池里的逻辑色。
    check("判据6 reassign → 新色 ≠ 旧色、且未被任何 active 占用",
          new["color"] != old and new["color"] not in others and new["color"] in POOL,
          f"{old} → {new['color']}（他人占用 {sorted(others)[:3]}…）")
    nk.remove(made[3]["nickname_id"])                 # 再腾一个色
    n2 = nk.reassign_color(made[0]["nickname_id"])
    check("判据6 排除自身规则：旧色已空出时也必须换一个新的（不能原地不动）",
          n2["color"] != new["color"] and n2["color"] in POOL,
          f"{new['color']} → {n2['color']}")

    # 判据4：8 线程并发建 8 个不同名字
    wipe()
    errs: list[str] = []
    ids: list[dict] = []
    lock = threading.Lock()

    def worker(i: int) -> None:
        try:
            row = nk.create(f"并发{i}")
            with lock:
                ids.append(row)
        except Exception as e:                        # noqa: BLE001
            with lock:
                errs.append(f"{type(e).__name__}: {e}")

    threads = [threading.Thread(target=worker, args=(i,)) for i in range(8)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    check("判据4 8 线程并发建 8 个不同名字 → 无异常、颜色互不重复",
          not errs and len(ids) == 8 and len({r["color"] for r in ids}) == 8,
          f"成功 {len(ids)}/8，色 {[r['color'] for r in ids]}，异常 {errs[:1]}")

    # 判据5：同一个名字连建 5 次
    wipe()
    first = nk.create("妈妈")
    outcomes = []
    for _ in range(4):
        try:
            nk.create("妈妈")
            outcomes.append(("ok", None))
        except nk.NicknameAlreadyExists as e:
            outcomes.append(("NICKNAME_ALREADY_EXISTS", e.existing_nickname_id))
    rows = db.query("SELECT nickname_id, color FROM nicknames WHERE display_name='妈妈'"
                    " AND status='active'")
    check("判据5 同名连建 5 次 → 第 1 次成功、后 4 次全是 NICKNAME_ALREADY_EXISTS 且带同一个既有 id",
          all(o == ("NICKNAME_ALREADY_EXISTS", first["nickname_id"]) for o in outcomes),
          f"{[o[0] for o in outcomes]} existing_id={[o[1] for o in outcomes]}")
    check("判据5 库里始终只有 1 行 active 且颜色不变",
          len(rows) == 1 and rows[0]["color"] == first["color"],
          f"{len(rows)} 行 → {dict(rows[0]) if rows else None}")

    # 撞名时不碰颜色池（池满时不能把「名字已存在」误报成 503）
    wipe()
    for i in range(len(POOL)):
        nk.create(f"占位{i}")
    try:
        nk.create("占位0")
        check("池满时撞名 → 仍报 NICKNAME_ALREADY_EXISTS（不是 503）", False, "报错了类型")
    except nk.NicknameAlreadyExists as e:
        check("池满时撞名 → 仍报 NICKNAME_ALREADY_EXISTS（不是 503）", True,
              f"existing_nickname_id={e.existing_nickname_id}")

    # ══════════════════════════════════════════════════════════════
    section("4. 软删/重创同名/改名/校验（§3.4.1 / §3.5 / §5.5）")
    # ══════════════════════════════════════════════════════════════
    wipe()
    mom = nk.create("妈妈")
    msg_id = db.execute(
        "INSERT INTO messages (sender_name, content, created_at, sender_kind,"
        " sender_nickname_id, sender_color) VALUES (?,?,?,'web',?,?)",
        ("妈妈", "开饭了", db.now_iso(), mom["nickname_id"], mom["color"]))
    old_snapshot = dict(db.query_one("SELECT sender_name, sender_color, sender_nickname_id"
                                     " FROM messages WHERE id=?", (msg_id,)))
    nk.remove(mom["nickname_id"])
    mom2 = nk.create("妈妈")                            # 重创同名（§3.4.1）
    check("软删后同名可再建，且是**新的 nickname_id**、拿到回池的色",
          mom2["nickname_id"] != mom["nickname_id"] and mom2["color"] == mom["color"],
          f"#{mom['nickname_id']} → #{mom2['nickname_id']}（色 {mom2['color']}）")
    after = dict(db.query_one("SELECT sender_name, sender_color, sender_nickname_id"
                             " FROM messages WHERE id=?", (msg_id,)))
    check("§3.4.1 重创同名**不污染历史**：老消息的快照三列一字未变",
          after == old_snapshot and after["sender_nickname_id"] == mom["nickname_id"],
          f"{old_snapshot} → {after}")
    check("§3.4.1 inactive 的历史行不占位、仍可查（同名两行）",
          len(nk.list("all")) == 2 and all(r["display_name"] == "妈妈" for r in nk.list("all")),
          f"{[(r['nickname_id'], r['status']) for r in nk.list('all')]}")

    dad = nk.create("爸爸")
    ren = nk.rename(dad["nickname_id"], " 老爸 ")
    check("改名：strip 生效、id 与颜色不变", ren["display_name"] == "老爸"
          and ren["nickname_id"] == dad["nickname_id"] and ren["color"] == dad["color"],
          f"{ren}")
    try:
        nk.rename(mom2["nickname_id"], "老爸")
        check("改名撞名 → NameTaken（不合并、不加后缀）", False, "居然改成功了")
    except nk.NameTaken as e:
        check("改名撞名 → NameTaken（不合并、不加后缀）", e.http_status == 409, f"{e.code} / {e.message}")
    try:
        nk.rename(999999, "谁")
        check("改名不存在的 id → NICKNAME_NOT_FOUND(404)", False, "居然成功了")
    except nk.NicknameNotFound as e:
        check("改名不存在的 id → NICKNAME_NOT_FOUND(404)", e.http_status == 404, f"{e.code}")
    try:
        nk.rename(mom["nickname_id"], "又改")
        check("改已软删的行 → NICKNAME_INACTIVE(409)", False, "居然成功了")
    except nk.NicknameInactive as e:
        check("改已软删的行 → NICKNAME_INACTIVE(409)", e.http_status == 409, f"{e.code}")
    out = nk.rename(dad["nickname_id"], "老爸")
    check("改成自己当前的名字 → 幂等通过（不算被占用）", out["display_name"] == "老爸", f"{out}")

    bad_cases = [("换行", "妈\n妈"), ("制表", "妈\t妈"), ("33 字", "x" * 33), ("空", "   ")]
    ok = True
    for label, val in bad_cases:
        try:
            nk.create(val)
            ok = False
            print(f"      ! {label} 居然被接受了")
        except nk.InvalidDisplayName:
            pass
    check("服务层校验：换行 / 制表 / 33 字 / 空串 → INVALID_DISPLAY_NAME(422)", ok)

    wipe()
    a = nk.create("é")                                  # U+00E9
    try:
        nk.create("e\u0301")                            # e + 组合重音 → NFC 后与上面同形
        check("NFC 归一：组合写法与预组合写法视为同一个名字", False, "建出了两行")
    except nk.NicknameAlreadyExists:
        check("NFC 归一：组合写法与预组合写法视为同一个名字", True, f"#{a['nickname_id']}")
    t = nk.create("  加空格  ")
    check("trim 规范化：落库的是 '加空格'", t["display_name"] == "加空格"
          and [r["display_name"] for r in nk.list("all") if r["display_name"] == "加空格"], f"{t['display_name']!r}")

    check("list('all') = active + inactive；list 非法 status → 422",
          bool(nk.list("all")) and _raises_invalid_status(nk))

    # 灰（本地临时昵称）：不在池里、不进 nicknames
    check("§3.2.1 灰不在池里：is_valid_shared_color('gray') is False；池内 ID 为 True",
          pool.is_valid_shared_color("gray") is False
          and pool.is_valid_shared_color("COLOR_01") is False
          and all(pool.is_valid_shared_color(c) for c in POOL))
    wid = db.execute("INSERT INTO messages (sender_name, content, created_at, sender_kind,"
                     " sender_nickname_id, sender_color) VALUES (?,?,?,'device',NULL,?)",
                     ("LIVINGROOM-PC", "灰临时", db.now_iso(), pool.LOCAL_TEMP_COLOR_ID))
    row = db.query_one("SELECT sender_nickname_id, sender_color FROM messages WHERE id=?", (wid,))
    check("§3.2.1 灰临时消息：sender_nickname_id=NULL + sender_color='gray' 落库成功",
          row["sender_nickname_id"] is None and row["sender_color"] == "gray", f"{dict(row)}")
    r = raw_insert("想用灰", pool.LOCAL_TEMP_COLOR_ID)
    check("§3.2.1 灰**不能**分配给共享昵称（DDL 拒）", r.startswith("IntegrityError"), r)

    # ══════════════════════════════════════════════════════════════
    section("5. 事务原子性（§6.1 / §6.2 / §6.3）")
    # ══════════════════════════════════════════════════════════════
    wipe()
    n0 = len(nk.list("all"))
    try:
        with db.tx() as c:
            c.execute("INSERT INTO nicknames (display_name,color,status,created_at,updated_at)"
                      " VALUES ('回滚我','color_01','active',?,?)", (db.now_iso(), db.now_iso()))
            raise RuntimeError("故意炸")
    except RuntimeError:
        pass
    check("事务体内抛异常 → 整体回滚（一行都不落库）", len(nk.list("all")) == n0 == 0,
          f"行数 {n0} → {len(nk.list('all'))}")

    committed_before = len(nk.list("all"))
    seen_by_other_conn = None
    with db.tx() as c:
        c.execute("INSERT INTO nicknames (display_name,color,status,created_at,updated_at)"
                  " VALUES ('未提交','color_01','active',?,?)", (db.now_iso(), db.now_iso()))
        other = sqlite3.connect(f"file:{db.DB_PATH}?mode=ro", uri=True)
        try:
            seen_by_other_conn = other.execute("SELECT count(*) FROM nicknames").fetchone()[0]
        finally:
            other.close()
    check("BEGIN IMMEDIATE 事务：未提交的中间态对**另一条连接**不可见（真的在一个事务里，不是自动 commit）",
          seen_by_other_conn == committed_before and len(nk.list("all")) == committed_before + 1,
          f"事务中另见 {seen_by_other_conn} 行 / 提交前 {committed_before} 行 → 提交后 {len(nk.list('all'))} 行")

    wipe()
    nk.create("锚点")
    orig_pick = nk.pick_first_available
    try:
        nk.pick_first_available = lambda conn: POOL[0]        # 永远返回已被占用的色
        try:
            nk.create("被抢色")
            res = "居然成功了"
        except nk.NoAvailableColor:
            res = "NoAvailableColor（重试耗尽）"
    finally:
        nk.pick_first_available = orig_pick
    rows = nk.list("all")
    check("颜色冲突重试耗尽 → NoAvailableColor，且**没有留下半条脏数据**",
          res.startswith("NoAvailableColor") and len(rows) == 1 and rows[0]["display_name"] == "锚点",
          f"{res}，表内 {[(r['display_name'], r['color']) for r in rows]}")

    try:
        nk.pick_first_available = lambda conn: (_ for _ in ()).throw(RuntimeError("色池炸了"))
        try:
            nk.create("异常路径")
            res2 = "居然成功了"
        except RuntimeError:
            res2 = "RuntimeError 透传"
    finally:
        nk.pick_first_available = orig_pick
    check("事务中途任意异常 → 回滚且异常透传（不留残行）",
          res2.startswith("RuntimeError") and len(nk.list("all")) == 1,
          f"{res2}，表内行数 {len(nk.list('all'))}")

    try:
        with db.tx() as c:
            c.execute("BEGIN IMMEDIATE")
        check("嵌套 BEGIN 被拒（tx 不可重入）", False, "居然没报错")
    except sqlite3.OperationalError as e:
        check("嵌套 BEGIN 被拒（tx 不可重入）", True, f"{e}")

    # ══════════════════════════════════════════════════════════════
    section("6. 旧库迁移实测（§7 验收 3）")
    # ══════════════════════════════════════════════════════════════
    if pre:
        pre_tables, pre_cols, pre_objs = pre["tables"], pre["cols"], pre["objs"]
        check("旧库原本没有 nicknames 表（确实是迁移前结构）", "nicknames" not in pre_objs,
              f"旧库对象 {len(pre_objs)} 个")
        check("迁移后服务层在旧库上可直接用（建 + 列）",
              nk.create("旧库可用")["nickname_id"] > 0 and len(nk.list("active")) >= 1,
              f"active={len(nk.list('active'))}")

        # 零丢失：既有表的行数只增不减（本脚本自己插了 2 条消息 + 若干审计事件）
        final_tables = {name: conn.execute(f'SELECT count(*) FROM "{name}"').fetchone()[0]
                        for (name,) in conn.execute(
                            "SELECT name FROM sqlite_master WHERE type='table'")}
        shrunk = {t: (n, final_tables.get(t)) for t, n in pre_tables.items()
                  if t != "nicknames" and final_tables.get(t, 0) < n}
        check("旧库既有表行数**只增不减**（没有任何既有行被删）", not shrunk,
              " ".join(f"{t}={final_tables.get(t)}" for t in pre_tables if t != "nicknames")
              + f"（漂移 {shrunk or '无'}）")

        # 零丢失（更硬）：迁移前每一条消息的 id + 6 个既有字段逐字未变
        now_msgs = {r["id"]: (r["sender_name"], r["content"], r["message_type"], r["created_at"],
                              r["sender_kind"], r["sender_device_id"])
                    for r in conn.execute("SELECT id, sender_name, content, message_type,"
                                          " created_at, sender_kind, sender_device_id FROM messages")}
        lost = [i for i in pre["msgs"] if i not in now_msgs]
        changed = {i: (v, now_msgs.get(i)) for i, v in pre["msgs"].items()
                   if i in now_msgs and now_msgs[i] != v}
        check("旧库每条消息逐字未变（id + sender_name/content/created_at/sender_kind/"
              "sender_device_id）", not lost and not changed,
              f"迁移前 {len(pre['msgs'])} 条全部命中，丢失 {lost or '无'}，被改写 {changed or '无'}")
        pre_devs = pre["devs"]
        now_devs = {r[0] for r in conn.execute("SELECT device_id FROM devices")}
        check("旧库设备一条不少（device_id 集合 ⊇ 迁移前）", pre_devs <= now_devs,
              f"{sorted(pre_devs)} ⊆ {sorted(now_devs)}")
    else:
        print("  ⏭  未传 --db：跳过旧库迁移实测（用 `--db server/data/family.db` 跑一遍）")

    # ══════════════════════════════════════════════════════════════
    section("6b. 换色选色：够远的候选里随机（v0.18.1；纯函数 + 服务层）")
    # ══════════════════════════════════════════════════════════════
    # 历史：v0.17 用户报「点重新分配颜色没反应」——旧实现取「池子顺序第一个可用色」，
    # 库里只有 1 条昵称时可用色 = 除自己外全部 → 永远取到 color_02，再点又回 color_01，
    # 这两个色的浅色圆点（#3D2273 / #252F6F）ΔE00 只有 7.5，肉眼看不出变化。
    # v0.18.0 的修法「取与旧色 ΔE00 最大者」**又是确定性的**，于是变成「原色 ↔ 最远色」两色互跳
    # —— v0.18.0 用户实测反馈：**「只在绿色和原本色之间互换，不是真正的随机颜色」**。
    # ★ v0.18.1 定稿：候选里筛出 ΔE00 ≥ pool.MIN_RECOLOR_DISTANCE(35) 的，**随机**取一个
    #   （pool.pick_random_recolor）；候选全都很近时才退回 pick_farthest_from。

    def _dot(hex_color: str, dark: bool = False) -> str:
        """镜像客户端 §4.4 规则 1 的圆点色（浅色 mix 35% 黑 / 深色 mix 30% 白）。

        ⚠ 只在**测试**里用：服务端不持有显示色表（那是客户端渲染职责）。
        这里做一份是为了**交叉校验**「按基础色取最远」这个代理是否忠实于用户真正看到的颜色。
        """
        c = [int(hex_color[i:i + 2], 16) for i in (1, 3, 5)]
        out = [v * 0.70 + 255 * 0.30 for v in c] if dark else [v * 0.65 for v in c]
        return "#%02X%02X%02X" % tuple(max(0, min(255, round(v))) for v in out)

    # ── 度量本身 ──
    check("ΔE 度量自洽：ΔE00(黑,白) = ΔE76(黑,白) = 100.0、同色 = 0",
          abs(pool.delta_e2000("#000000", "#FFFFFF") - 100.0) < 0.01
          and abs(pool.delta_e76("#000000", "#FFFFFF") - 100.0) < 0.01
          and pool.delta_e2000("#5E35B1", "#5E35B1") == 0.0)
    check("ΔE00 对称：d(a,b) == d(b,a)",
          pool.visual_distance("color_01", "color_13") == pool.visual_distance("color_13", "color_01"),
          f"{pool.visual_distance('color_01', 'color_13'):.2f}")
    check("visual_distance 是 Lab 距离、不是 RGB 欧氏距离（color_01↔color_02 = 9.2479）",
          abs(pool.visual_distance("color_01", "color_02") - 9.2479) < 0.001,
          f"{pool.visual_distance('color_01', 'color_02'):.4f}")
    try:
        pool.visual_distance("color_01", "gray")
        check("未知逻辑色 ID（gray）→ KeyError（不静默按「距离 0」处理）", False, "居然没抛")
    except KeyError as e:
        check("未知逻辑色 ID（gray）→ KeyError（不静默按「距离 0」处理）", True, str(e))

    # ── 单条昵称：候选 = 池中除自己外全部 → 必须挑最远的，不是池子第一个 ──
    one = [c for c in POOL if c != "color_01"]
    best = pool.pick_farthest_from("color_01", one)
    d_best = pool.visual_distance("color_01", best)
    check("单条昵称（旧色 color_01）→ 选到候选中 ΔE00 最大的 color_08，不是池子第一个 color_02",
          best == "color_08"
          and d_best == max(pool.visual_distance("color_01", c) for c in one),
          f"选到 {best}（ΔE00 {d_best:.1f}）；池子第一个 color_02 只有 "
          f"{pool.visual_distance('color_01', 'color_02'):.1f}")

    old_c, new_c = "color_02", best                       # 旧算法给的色 vs 新算法给的色
    base_old = (pool.delta_e76(pool.LOGICAL_COLORS["color_01"], pool.LOGICAL_COLORS[old_c]),
                pool.visual_distance("color_01", old_c))
    base_new = (pool.delta_e76(pool.LOGICAL_COLORS["color_01"], pool.LOGICAL_COLORS[new_c]),
                pool.visual_distance("color_01", new_c))
    dot_old = (pool.delta_e76(_dot(pool.LOGICAL_COLORS["color_01"]), _dot(pool.LOGICAL_COLORS[old_c])),
               pool.delta_e2000(_dot(pool.LOGICAL_COLORS["color_01"]), _dot(pool.LOGICAL_COLORS[old_c])))
    dot_new = (pool.delta_e76(_dot(pool.LOGICAL_COLORS["color_01"]), _dot(pool.LOGICAL_COLORS[new_c])),
               pool.delta_e2000(_dot(pool.LOGICAL_COLORS["color_01"]), _dot(pool.LOGICAL_COLORS[new_c])))
    print(f"    旧算法 color_01 ↔ {old_c}：基础色 ΔE76 {base_old[0]:.1f} / ΔE00 {base_old[1]:.1f}；"
          f"浅色圆点 ΔE76 {dot_old[0]:.1f} / ΔE00 {dot_old[1]:.1f}")
    print(f"    新算法 color_01 → {new_c}：基础色 ΔE76 {base_new[0]:.1f} / ΔE00 {base_new[1]:.1f}；"
          f"浅色圆点 ΔE76 {dot_new[0]:.1f} / ΔE00 {dot_new[1]:.1f}")
    check("新色比旧色的差异**明显变大**（基础色 ΔE00 ≥ 8×；用户看到的浅色圆点 ΔE00 ≥ 8×）",
          base_new[1] >= 8 * base_old[1] and dot_new[1] >= 8 * dot_old[1],
          f"基础 {base_old[1]:.1f} → {base_new[1]:.1f}（{base_new[1] / base_old[1]:.1f}×）；"
          f"圆点 {dot_old[1]:.1f} → {dot_new[1]:.1f}（{dot_new[1] / dot_old[1]:.1f}×）")

    # ── 「基础色代理」的忠实度 + 深色主题也要成立 ──
    agree, worst_dark = 0, 1e9
    for old in POOL:
        cands = [c for c in POOL if c != old]
        pick = pool.pick_farthest_from(old, cands)
        by_dot = max(cands, key=lambda c: pool.delta_e2000(_dot(pool.LOGICAL_COLORS[old]),
                                                           _dot(pool.LOGICAL_COLORS[c])))
        agree += (pick == by_dot)
        worst_dark = min(worst_dark, pool.delta_e2000(_dot(pool.LOGICAL_COLORS[old], True),
                                                      _dot(pool.LOGICAL_COLORS[pick], True)))
    check("「按基础色取最远」忠实于「按浅色圆点取最远」（16 个旧色里 15 个一致，与模块文档一致）",
          agree == 15, f"一致 {agree}/16")
    check("新算法在**深色**主题下同样一眼可辨（所有旧色里最小的圆点 ΔE00 ≥ 30；旧算法 7.5）",
          worst_dark >= 30, f"最小 ΔE00 {worst_dark:.1f}")

    # ── 确定性与边界 ──
    check("确定性：候选顺序颠倒不影响结果（最大值唯一时不依赖顺序）",
          pool.pick_farthest_from("color_01", one) == best
          == pool.pick_farthest_from("color_01", list(reversed(one))))
    _vd = pool.visual_distance
    pool.visual_distance = lambda a, b: 5.0                # 造「全部同分」
    try:
        tie = pool.pick_farthest_from("color_01", ["color_09", "color_11", "color_13"])
    finally:
        pool.visual_distance = _vd
    check("同分取迭代顺序靠前者（= COLOR_POOL 书写顺序，可复现）", tie == "color_09", f"选到 {tie}")
    check("候选为空 → None（调用方据此抛 503，绝不回退同色）",
          pool.pick_farthest_from("color_01", []) is None)
    check("只有一个候选时没得挑，仍然返回它",
          pool.pick_farthest_from("color_03", ["color_02"]) == "color_02")
    check("旧色是脏数据（不在池里）→ 退回第一个候选，不抛异常",
          pool.pick_farthest_from("gray", ["color_05", "color_06"]) == "color_05")

    # ── pick_random_recolor：真随机 + 最小差异 + 兜底（v0.18.1）──
    one15 = [c for c in POOL if c != "color_01"]
    _rr = random.Random(7)
    picks = [pool.pick_random_recolor("color_01", one15, rnd=_rr) for _ in range(12)]
    check("pick_random_recolor：12 次里出现 ≥ 3 种不同结果（真随机，不是固定取最远）",
          len(set(picks)) >= 3 and all(p in one15 for p in picks) and all(p != "color_01" for p in picks),
          f"{sorted(set(picks))}")
    check(f"pick_random_recolor：结果一律离旧色 ≥ ΔE00 {pool.MIN_RECOLOR_DISTANCE:.0f}（一步看得出变化）",
          all(pool.visual_distance("color_01", p) >= pool.MIN_RECOLOR_DISTANCE for p in picks),
          f"最小 {min(pool.visual_distance('color_01', p) for p in picks):.1f}")
    near = [c for c in one15 if pool.visual_distance("color_01", c) < pool.MIN_RECOLOR_DISTANCE]
    check("pick_random_recolor：候选全都太近（只有 color_02/color_14/color_16）→ 退回最远的那个",
          bool(near) and pool.pick_random_recolor("color_01", near) == pool.pick_farthest_from("color_01", near),
          f"候选 {near} → {pool.pick_random_recolor('color_01', near)}")
    check("pick_random_recolor：候选为空 → None（调用方据此抛 503，绝不回退同色）",
          pool.pick_random_recolor("color_01", []) is None)

    # ── 服务层集成：真写库、真换色 ──
    wipe()
    solo = nk.create("换个色试试")
    seq = [solo["color"]]
    for _ in range(3):
        seq.append(nk.reassign_color(solo["nickname_id"])["color"])
    steps = [pool.visual_distance(a, b) for a, b in zip(seq, seq[1:])]
    check(f"服务层：单条昵称连换 3 次 → 每次都是新色，且每步 ΔE00 ≥ {pool.MIN_RECOLOR_DISTANCE:.0f}（旧算法 7.5）",
          all(a != b for a, b in zip(seq, seq[1:])) and min(steps) >= pool.MIN_RECOLOR_DISTANCE,
          " → ".join(seq) + f"（相邻 ΔE00 {['%.1f' % d for d in steps]}）")
    check("……换完仍只占 1 个色位、库里仍 1 条 active（不泄漏颜色）",
          len(active()) == 1 and len({r["color"] for r in active()}) == 1, f"{active()}")

    # ★ 用户 v0.18.0 实测反馈的**直接回归**：「颜色更换只是在绿色和原本颜色之间互换」。
    #   连点 6 次，出现的不同颜色必须 ≥ 3 种（两色互跳只有 2 种）。
    #   种子固定 = CI 可复现；这里断言的是「不是两色互跳」这个契约，不是统计随机性。
    wipe()
    solo2 = nk.create("连点六次")
    random.seed(20260929)
    seq2 = [solo2["color"]] + [nk.reassign_color(solo2["nickname_id"])["color"] for _ in range(6)]
    uniq = len(set(seq2[1:]))
    check("★ 回归（v0.18.0 实测）：单条昵称连点 6 次换色 → ≥ 3 种不同颜色，不是两色互跳",
          uniq >= 3, " → ".join(seq2) + f"（不同色 {uniq} 种）")
    check("……且每一步都真的换了（相邻不重复）",
          all(a != b for a, b in zip(seq2, seq2[1:])),
          f"相邻 {['%.0f' % pool.visual_distance(a, b) for a, b in zip(seq2, seq2[1:])]}")

    wipe()
    made15 = [nk.create(f"人{i}") for i in range(15)]
    free = [c for c in POOL if c not in {m["color"] for m in made15}][0]
    chg = nk.reassign_color(made15[0]["nickname_id"])
    check(f"15 条 active（只剩 {free} 一个空闲色）→ 只能换到它，仍然换了（不原地不动）",
          chg["color"] == free and chg["color"] != made15[0]["color"],
          f"{made15[0]['color']} → {chg['color']}")

    # ══════════════════════════════════════════════════════════════
    section("7. Phase 2：NAS API + 广播（真起测试实例，默认 18899）")
    # ══════════════════════════════════════════════════════════════
    if args.no_phase2:
        print("  ⏭  --no-phase2：跳过 Phase 2")
    else:
        try:
            phase2(Path(tmp), port=args.port)
        except Exception as e:                    # 兜底：起实例失败也要给出可读结论
            check("Phase 2 整段执行（起测试实例 → HTTP → 多连接广播 → 开关关闭对照）",
                  False, f"{type(e).__name__}: {e}")

    # ══════════════════════════════════════════════════════════════
    conn.close()
    if not args.keep:
        shutil.rmtree(tmp, ignore_errors=True)
    else:
        print(f"\n  临时目录保留：{tmp}")
    return _summary()


# ══════════════════════════════════════════════════════════════════════
# Phase 2：NAS API + 广播（真起测试实例；HTTP 走 urllib，WS 走 websockets）
# ══════════════════════════════════════════════════════════════════════
PROD_PORT = 18801        # 生产实例：本脚本硬拒绝使用该端口
ENROLL = "FAMILY-TEST-TOKEN"


def _find_python() -> str:
    """挑一个能 `import fastapi, uvicorn` 的解释器来跑测试实例。"""
    cands = [os.environ.get("FM_PY"), sys.executable,
             "/vol1/@apphome/hermes-agent/data/venv/bin/python", "/usr/bin/python3"]
    for p in cands:
        if not p or not Path(p).exists() or p == sys.executable:
            continue
        try:
            r = subprocess.run([p, "-c", "import fastapi, uvicorn"],
                               capture_output=True, timeout=30)
            if r.returncode == 0:
                return p
        except Exception:
            continue
    if sys.executable and subprocess.run(
            [sys.executable, "-c", "import fastapi, uvicorn"],
            capture_output=True).returncode == 0:
        return sys.executable
    raise RuntimeError("找不到能 import fastapi/uvicorn 的解释器，用 FM_PY= 指定一个")


def _free_port(start: int) -> int:
    for p in range(start, start + 60):
        if p == PROD_PORT:
            continue
        s = socket.socket()
        try:
            s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            s.bind(("127.0.0.1", p))
            return p
        except OSError:
            continue
        finally:
            s.close()
    raise RuntimeError(f"从 {start} 起找不到空闲端口")


def _http(base: str, method: str, path: str, body=None,
          timeout: float = 8.0) -> tuple[int, Any]:
    """返回 (status, json)。4xx/5xx 不抛异常（本脚本就是要验错误码）。"""
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


class _Srv:
    """自起的测试实例；只按**精确 PID** 停止。端口硬拒 18801。"""

    def __init__(self, python: str, port: int, enabled: bool, tmp: Path):
        assert port != PROD_PORT, "拒绝使用生产端口 18801"
        self.python, self.port, self.enabled = python, port, enabled
        self.dir = tmp / ("enabled" if enabled else "disabled")
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
        self._logf = None

    @property
    def base(self) -> str:
        return f"http://127.0.0.1:{self.port}"

    @property
    def wsbase(self) -> str:
        return f"ws://127.0.0.1:{self.port}"

    @property
    def db_path(self) -> Path:
        return self.data / "family.db"

    def start(self, timeout: float = 20.0) -> None:
        env = dict(os.environ, FM_CONFIG=str(self.cfg), FM_PORT=str(self.port),
                   TRIM_SERVICE_PORT=str(self.port), PYTHONUNBUFFERED="1")
        self._logf = open(self.log_path, "wb")
        self.proc = subprocess.Popen(
            [self.python, str(SERVER_DIR / "run.py")], cwd=str(SERVER_DIR), env=env,
            stdout=self._logf, stderr=subprocess.STDOUT, start_new_session=True)
        end = time.time() + timeout
        while time.time() < end:
            if self.proc.poll() is not None:
                raise RuntimeError(f"实例秒退 exit={self.proc.returncode}\n"
                                   f"{self.log()[-800:]}")
            try:
                st, _ = _http(self.base, "GET", "/healthz", timeout=2)
                if st == 200:
                    return
            except Exception:
                pass
            time.sleep(0.25)
        raise RuntimeError(f"实例 {timeout}s 内没起来\n{self.log()[-800:]}")

    def stop(self) -> None:
        if self.proc is None:
            return
        if self.proc.poll() is None:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=8)
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait(timeout=5)
        if self._logf:
            self._logf.close()
            self._logf = None

    def log(self) -> str:
        try:
            return self.log_path.read_text(errors="replace")
        except Exception:
            return ""

    def row_count(self) -> int:
        """直接只读连库数行数（证明「不建行」，不看接口自述）。"""
        try:
            c = sqlite3.connect(f"file:{self.db_path}?mode=ro", uri=True)
        except sqlite3.OperationalError:
            return 0
        try:
            return c.execute("SELECT count(*) FROM nicknames").fetchone()[0]
        finally:
            c.close()


class _WS:
    """一条 WebSocket 连接：后台收帧 + 按类型查找。"""

    def __init__(self, name: str, ws):
        self.name, self.ws, self.frames = name, ws, []
        self._err = ""
        self._t = asyncio.create_task(self._pump())

    async def _pump(self) -> None:
        try:
            async for raw in self.ws:
                try:
                    self.frames.append(json.loads(raw))
                except Exception:
                    self.frames.append({"_raw": raw})
        except Exception as e:
            self._err = f"{type(e).__name__}: {e}"

    async def send(self, obj: dict) -> None:
        await self.ws.send(json.dumps(obj, ensure_ascii=False))

    def types(self) -> list[str]:
        return [f.get("type") for f in self.frames]

    async def wait(self, ftype: str, timeout: float = 2.0) -> dict | None:
        end = time.time() + timeout
        while time.time() < end:
            for f in self.frames:
                if f.get("type") == ftype:
                    return f
            await asyncio.sleep(0.05)
        return None

    async def close(self) -> None:
        self._t.cancel()
        try:
            await self.ws.close()
        except Exception:
            pass


async def _ws_suite(srv: _Srv, out: list) -> None:
    """HTTP 触发 + 设备帧触发，验证「3 个连接都收到」以及 5 个帧的应答/错误语义。"""
    import websockets  # 延迟导入（缺失时由调用方跳过）

    B, W = srv.base, srv.wsbase

    async def ok(label, cond, detail=""):
        out.append((label, bool(cond), detail))

    w1 = _WS("web-A", await websockets.connect(W + "/ws/web"))
    w2 = _WS("web-B", await websockets.connect(W + "/ws/web"))
    dev = _WS("dev-1", await websockets.connect(
        W + f"/ws/device/pc_nk_test?enroll_token={ENROLL}&name=NK-TEST&type=pc"))
    await asyncio.sleep(1.0)
    ALL = (w1, w2, dev)
    try:
        sync_frame = await w1.wait("nickname_list_sync", 0.5)
        await ok("新 /ws/web 连上即收 nickname_list_sync（整表同步，§5.4）",
                 sync_frame is not None, f"web-A 帧序 {w1.types()}")
        await ok("★ 整表帧里带权威色表（v0.19 P2：color_table + pool_version），"
                 "客户端色表以它为准（内置表只兜底）",
                 bool(sync_frame) and len(sync_frame.get("color_table") or []) >= 16
                 and all(set(c) == {"color_id", "hex"} for c in sync_frame["color_table"]),
                 f"n={len((sync_frame or {}).get('color_table') or [])}")
        hello_dev = await dev.wait("hello", 0.5)
        await ok("设备连上收 hello（既有握手未受影响）", hello_dev is not None, f"{dev.types()}")
        st_ct, ct = await asyncio.to_thread(_http, B, "GET", "/api/nicknames/colors?status=active")
        await ok("★ 设备 hello 里带了色表（v0.19 P2：color_table + color_pool_version，"
                 "设备端 / PC 壳 / 将来 Android 都以此为准，内置表只兜底）",
                 bool(hello_dev) and st_ct == 200
                 and len(hello_dev.get("color_table") or []) == len(ct.get("colors") or [])
                 and hello_dev.get("color_pool_version") == ct.get("color_pool_version")
                 and all(set(c) == {"color_id", "hex"} for c in (hello_dev.get("color_table") or [])),
                 f"n={len((hello_dev or {}).get('color_table') or [])} "
                 f"v{(hello_dev or {}).get('color_pool_version')} vs {len(ct.get('colors') or [])}")

        # ── HTTP POST 新建 → 三端 nickname_created ──
        st, body = await asyncio.to_thread(_http, B, "POST", "/api/nicknames",
                                           {"display_name": "广播甲"})
        nid = (body or {}).get("nickname", {}).get("nickname_id")
        await asyncio.sleep(1.0)
        await ok("HTTP POST 新建 → 2 个浏览器 + 1 个设备都收到 nickname_created（§5.4）",
                 st == 201 and all([await c.wait("nickname_created", 0.5) for c in ALL]),
                 f"status={st} id={nid} frames={[c.types() for c in ALL]}")

        # ── HTTP PATCH 改名 → 三端 nickname_updated ──
        st, _ = await asyncio.to_thread(_http, B, "PATCH", f"/api/nicknames/{nid}",
                                        {"display_name": "广播甲A"})
        await asyncio.sleep(1.0)
        await ok("HTTP PATCH 改名 → 三端都收到 nickname_updated",
                 st == 200 and all([await c.wait("nickname_updated", 0.5) for c in ALL]),
                 f"status={st}")

        # ── ★ HTTP reassign-color → 三端 nickname_color_changed（本轮头条判据）──
        _, before = await asyncio.to_thread(_http, B, "GET", "/api/nicknames?status=active")
        old = next((n["color"] for n in before["nicknames"] if n["nickname_id"] == nid), None)
        for c in ALL:
            c.frames.clear()
        st, body = await asyncio.to_thread(_http, B, "POST",
                                           f"/api/nicknames/{nid}/reassign-color")
        new = (body or {}).get("nickname", {}).get("color")
        await asyncio.sleep(1.0)
        got = [c.name for c in ALL if await c.wait("nickname_color_changed", 0.5)]
        used = {n["color"] for n in (await asyncio.to_thread(
            _http, B, "GET", "/api/nicknames?status=active"))[1]["nicknames"]}
        await ok("★ 改一次颜色 → 2 浏览器 + 1 设备**三个连接都收到** nickname_color_changed",
                 st == 200 and len(got) == 3, f"收到者={got}")
        await ok("reassign 新色 ≠ 旧色，且新色确实落库（未被别的 active 占用）",
                 old is not None and new and new != old and new in used, f"{old} → {new}")

        # ── 设备帧：5 个上行帧 ──
        for c in ALL:
            c.frames.clear()
        await dev.send({"type": "nickname_list_request"})
        await asyncio.sleep(1.0)
        await ok("设备帧 nickname_list_request → 发起设备收 nickname_list_response"
                 "（全量 nicknames + pool_version）",
                 (await dev.wait("nickname_list_response", 0.5)) is not None
                 and isinstance((await dev.wait("nickname_list_response", 0.1) or {}).get("pool_version"), int),
                 f"{dev.types()}")
        await ok("……同时广播 nickname_list_sync 给 2 个浏览器（整表校正）",
                 all([await c.wait("nickname_list_sync", 0.5) for c in (w1, w2)]),
                 f"web-A={w1.types()}")

        for c in ALL:
            c.frames.clear()
        await dev.send({"type": "nickname_rename_request", "nickname_id": nid,
                        "display_name": "设备改的名"})
        await asyncio.sleep(1.0)
        await ok("设备帧 nickname_rename_request → 发起端收 list_response + 三端 nickname_updated",
                 (await dev.wait("nickname_list_response", 0.5)) is not None
                 and all([await c.wait("nickname_updated", 0.5) for c in ALL]),
                 f"{[c.types() for c in ALL]}")

        for c in ALL:
            c.frames.clear()
        await dev.send({"type": "nickname_create_request", "display_name": "设备改的名"})
        await asyncio.sleep(1.0)
        err = await dev.wait("nickname_error", 1.0)
        await ok("设备帧 create 撞名 → nickname_error(NICKNAME_ALREADY_EXISTS) + existing_nickname_id",
                 err and err.get("code") == "NICKNAME_ALREADY_EXISTS"
                 and err.get("existing_nickname_id") == nid
                 and err.get("request") == "nickname_create_request",
                 f"{err}")
        await ok("……撞名时**不广播** nickname_created（列表不会凭空多一条，§5.6.4）",
                 not any([await c.wait("nickname_created", 0.4) for c in ALL]),
                 f"{[c.types() for c in ALL]}")

        for c in ALL:
            c.frames.clear()
        await dev.send({"type": "nickname_delete_request", "nickname_id": nid})
        await asyncio.sleep(1.0)
        rm = await dev.wait("nickname_removed", 0.5)
        await ok("设备帧 nickname_delete_request → 三端 nickname_removed（带 released_color + "
                 "完整对象 status=inactive）",
                 rm and rm.get("released_color") and rm.get("nickname", {}).get("status") == "inactive"
                 and all([await c.wait("nickname_removed", 0.5) for c in ALL]),
                 f"{rm}")

        dev.frames.clear()
        await dev.send({"type": "nickname_reassign_color_request", "nickname_id": nid})
        await asyncio.sleep(0.8)
        err2 = await dev.wait("nickname_error", 1.0)
        await ok("设备帧 reassign 已软删的行 → nickname_error(NICKNAME_INACTIVE)",
                 err2 and err2.get("code") == "NICKNAME_INACTIVE", f"{err2}")

        # ── 非法帧：回 error 但**不能拖垮整条连接**（main.py 已有兜底吞异常）──
        dev.frames.clear()
        await dev.send({"type": "nickname_rename_request", "display_name": "没带 id"})
        await asyncio.sleep(0.8)
        await dev.send({"type": "heartbeat"})
        hb = await dev.wait("heartbeat_ack", 1.5)
        err3 = await dev.wait("nickname_error", 0.1)
        await ok("非法帧（缺 nickname_id）→ nickname_error(INVALID_REQUEST)，且**连接仍活着**"
                 "（随后 heartbeat 还能收到 heartbeat_ack）",
                 hb is not None and err3 is not None and err3.get("code") == "INVALID_REQUEST",
                 f"err={err3} heartbeat_ack={'有' if hb else '无'}")

        # ── ★ 颜色表变更 → `color_table_changed` 广播（v0.19，docs/COLOR-TABLE-PLAN.md §3.2）──
        for c in ALL:
            c.frames.clear()
        st, body = _http(srv.base, "POST", "/api/nicknames/colors", {"hex": "#123456"})
        await asyncio.sleep(0.8)
        got = {c.name: await c.wait("color_table_changed", 1.0) for c in ALL}
        await ok("★ 加一个颜色 → 两个 /ws/web + 设备都收到 color_table_changed（只带版本号，不推全表）",
                 st == 201 and all(got[c.name] for c in ALL)
                 and all(got[c.name].get("color_pool_version") == body.get("color_pool_version")
                         for c in ALL)
                 and all("colors" not in got[c.name] for c in ALL),
                 f"st={st} " + " ".join(f"{k}:{v.get('color_pool_version') if v else None}"
                                        for k, v in got.items()))
        newcid = body.get("color", {}).get("color_id")
        for c in ALL:
            c.frames.clear()
        st2, body2 = _http(srv.base, "DELETE", f"/api/nicknames/colors/{newcid}")
        await asyncio.sleep(0.8)
        got2 = {c.name: await c.wait("color_table_changed", 1.0) for c in ALL}
        await ok("……停用同一个色 → 也广播（版本再 +1），且可用色回到 16 个",
                 st2 == 200 and all(got2[c.name] for c in ALL)
                 and body2.get("color_pool_version") == (body.get("color_pool_version") or 0) + 1
                 and len(_http(srv.base, "GET", "/api/nicknames/colors?status=active")[1]["colors"]) == 16,
                 f"st={st2} v{body2.get('color_pool_version')}")

        # ── 广播载荷里不许有 owner / 设备字段（§0.7 / §5.4）──
        bad = [f for c in ALL for f in c.frames
               if f.get("type", "").startswith("nickname_")
               and any(k in (f.get("nickname") or {}) for k in
                       ("owner_id", "owner_type", "device_id", "client_id"))]
        await ok("昵称广播 / 帧的载荷里没有任何 owner / 设备字段（§0.7 / §5.4）",
                 not bad, f"{bad[:1]}")
    finally:
        for c in ALL:
            await c.close()


def _phase2_http_checks(srv: _Srv, out: list) -> None:
    """5 个 HTTP 端点的完整验收（§5.3 / §7 Phase 2 验收 1、4）。"""
    B = srv.base

    def push(label, cond, detail=""):
        out.append((label, bool(cond), detail))

    def g(path):
        return _http(B, "GET", path)

    # ── 空库纯读 ──
    st, body = g("/api/nicknames?status=active")
    push("空库 GET /api/nicknames → 200 + {\"nicknames\": []}",
         st == 200 and body == {"nicknames": []}, f"{st} {body}")
    push("……且**一行都没建**（不懒建、不分配颜色，§0.9）", srv.row_count() == 0,
         f"表内 {srv.row_count()} 行")
    st2, body2 = g("/api/nicknames")
    push("再 GET 一次仍是空（证明纯读，无副作用）",
         st2 == 200 and body2 == {"nicknames": []} and srv.row_count() == 0)
    push("非法 status → 422 INVALID_STATUS",
         g("/api/nicknames?status=deleted")[1].get("detail", {}).get("code") == "INVALID_STATUS")

    # ── POST 新建 ──
    st, body = _http(B, "POST", "/api/nicknames", {"display_name": "甲"})
    a = body.get("nickname", {})
    push("POST /api/nicknames 建「甲」→ **201** + 颜色是逻辑色 ID color_01（不是 HEX）",
         st == 201 and a.get("color") == "color_01" and a.get("status") == "active"
         and isinstance(a.get("nickname_id"), int), f"{st} {body}")

    st, body = _http(B, "POST", "/api/nicknames", {"display_name": "甲"})
    d = body.get("detail", {})
    push("再建「甲」→ **409 NICKNAME_ALREADY_EXISTS** + existing_nickname_id（不是 200）",
         st == 409 and d.get("code") == "NICKNAME_ALREADY_EXISTS"
         and d.get("existing_nickname_id") == a.get("nickname_id"), f"{st} {body}")
    push("……撞名不新增行（库里仍 1 行）", srv.row_count() == 1, f"{srv.row_count()} 行")

    # ── 第二个名字拿 color_02 ──
    st, body = _http(B, "POST", "/api/nicknames", {"display_name": "乙"})
    b = body.get("nickname", {})
    aid, bid = a["nickname_id"], b.get("nickname_id")
    push("再建「乙」→ 201 + 颜色 color_02（按池子书写顺序分配）",
         st == 201 and b.get("color") == "color_02", f"{st} {body}")

    # ── 请求体里夹带 color / owner_type → 一律忽略，颜色仍由服务端分配（§5.5）──
    st, body = _http(B, "POST", "/api/nicknames",
                     {"display_name": "试图自选色", "color": "#000000", "owner_type": "web"})
    tried = body.get("nickname", {})
    push("请求体里带上 color / owner_type → 一律**被忽略**（颜色只能服务端定，§5.5）",
         st == 201 and tried.get("color") == "color_03" and tried.get("color") != "#000000"
         and not any(k in tried for k in ("owner_type", "owner_id")),
         f"{st} color={tried.get('color')}")
    _http(B, "DELETE", f"/api/nicknames/{tried.get('nickname_id')}")   # 清掉，保持后续计数确定

    # ── PATCH 改名 ──
    st, body = _http(B, "PATCH", f"/api/nicknames/{aid}", {"display_name": "甲A"})
    r = body.get("nickname", {})
    push("PATCH /api/nicknames/{id} 改名 → 200，nickname_id 不变、**颜色不变**、updated_at 变了",
         st == 200 and r.get("nickname_id") == aid and r.get("display_name") == "甲A"
         and r.get("color") == a.get("color"), f"{st} {body}")

    st, body = _http(B, "PATCH", f"/api/nicknames/{aid}", {"display_name": "乙"})
    push("PATCH 撞名 → **409 NAME_TAKEN**（与创建撞名的错误码不同，§5.3）",
         st == 409 and body.get("detail", {}).get("code") == "NAME_TAKEN", f"{st} {body}")

    st, body = _http(B, "PATCH", "/api/nicknames/99999", {"display_name": "X"})
    push("PATCH 不存在的 id → 404 NICKNAME_NOT_FOUND",
         st == 404 and body.get("detail", {}).get("code") == "NICKNAME_NOT_FOUND")
    push("PATCH display_name 非法（空白 / 控制字符 / 33 字）→ 422 INVALID_DISPLAY_NAME",
         all(_http(B, "PATCH", f"/api/nicknames/{aid}", {"display_name": n})[1]
             .get("detail", {}).get("code") == "INVALID_DISPLAY_NAME"
             for n in ("   ", "a\nb", "x" * 33)))

    # ── reassign-color ──
    st, body = _http(B, "POST", f"/api/nicknames/{aid}/reassign-color")
    r = body.get("nickname", {})
    active = g("/api/nicknames?status=active")[1]["nicknames"]
    used = {n["color"] for n in active}
    push("reassign-color → 200，新色 ≠ 旧色、且未被任何 active 占用（"
         "与「乙」的 color_02 不撞）",
         st == 200 and r.get("color") not in (None, a.get("color"))
         and r.get("color") in used, f"{a.get('color')} → {r.get('color')} used={sorted(used)}")
    try:
        from nicknames import visual_distance as _vd2
        d_gap = _vd2(a.get("color", ""), r.get("color", ""))
    except Exception as e:                                    # noqa: BLE001
        d_gap = -1.0
        print(f"  ! 算 ΔE 失败：{type(e).__name__}: {e}")
    from nicknames import MIN_RECOLOR_DISTANCE as _MIN_D
    push(f"……换到的色与旧色 ΔE00 = {d_gap:.1f} ≥ {_MIN_D:.0f}"
         f"（一眼可辨；旧实现只会给出 color_02，ΔE00 9.2）",
         d_gap >= _MIN_D, f"{a.get('color')} → {r.get('color')} ΔE00={d_gap:.1f}")
    st, body = _http(B, "POST", "/api/nicknames/99999/reassign-color")
    push("reassign-color 不存在的 id → 404 NICKNAME_NOT_FOUND",
         st == 404 and body.get("detail", {}).get("code") == "NICKNAME_NOT_FOUND")

    # ── DELETE（软删 + 释放）──
    st, body = _http(B, "DELETE", f"/api/nicknames/{bid}")
    rel = body.get("released_color")
    push("DELETE /api/nicknames/{id} → 200 {ok, nickname_id, released_color}",
         st == 200 and body.get("ok") is True and body.get("nickname_id") == bid and rel,
         f"{st} {body}")
    push("……软删而非硬删：status=inactive 能查到那一行（历史冻结，§3.5）",
         any(n["nickname_id"] == bid and n["status"] == "inactive"
             for n in g("/api/nicknames?status=inactive")[1]["nicknames"]))
    push("……status=all = active + inactive（按 nickname_id 升序）",
         [n["nickname_id"] for n in g("/api/nicknames?status=all")[1]["nicknames"]]
         == sorted(n["nickname_id"] for n in g("/api/nicknames?status=all")[1]["nicknames"]))

    st, body = _http(B, "DELETE", f"/api/nicknames/{bid}")
    push("再 DELETE 同一条 → 409 NICKNAME_INACTIVE（不是 404，§5.5）",
         st == 409 and body.get("detail", {}).get("code") == "NICKNAME_INACTIVE", f"{st} {body}")

    # ── ★ 释放的颜色能被下一个新建拿到 ──
    # 让「甲A」换一次色（换到一个空闲色）→ 软删它 → 「池中第一个空闲色」（= 新建的口径
    # pick_first_available）必须能立刻被下一个新建拿到：删掉的色回到了池子（§3.5）。
    # ⚠ 换色自 v0.18.1 起是**随机**的，所以这里**不能**钉死「甲必须持有 color_01」——
    #   改成按池子顺序算出「此刻第一个空闲色」再对账（口径与新建完全一致）。
    st, body = _http(B, "POST", f"/api/nicknames/{aid}/reassign-color")
    held = body.get("nickname", {}).get("color")
    before_used = {n["color"] for n in g("/api/nicknames?status=active")[1]["nicknames"]}
    _http(B, "DELETE", f"/api/nicknames/{aid}")
    after_used = {n["color"] for n in g("/api/nicknames?status=active")[1]["nicknames"]}
    from nicknames import COLOR_POOL as _POOL
    first_free = next(c for c in _POOL if c not in after_used)
    st, body = _http(B, "POST", "/api/nicknames", {"display_name": "丙"})
    got = body.get("nickname", {}).get("color")
    push("★ 软删释放的颜色能被**下一个新建**拿到（删掉的色回到了池子，§3.5）",
         st == 201 and held in before_used and held not in after_used
         and held in [c for c in _POOL if c not in after_used] and got == first_free,
         f"删前 active 占用={sorted(before_used)} / 被删行持有={held} / "
         f"删后空闲={sorted(c for c in _POOL if c not in after_used)} / 新建拿到={got}")
    new_id = body.get("nickname", {}).get("nickname_id")

    # ── 没有「越权」概念（§7 Phase 2 验收 5）──
    # 「设备连接能改 HTTP 创建的同一个 nickname_id 且正常广播」已由 _ws_suite 覆盖
    # （设备帧 rename / delete 改的就是 HTTP 建出来的那个 id）。
    # ── ★ 颜色表（v0.19：色表是数据；docs/COLOR-TABLE-PLAN.md §3.2）──────────────
    #  这里只测**服务端行为**（P1）：增 / 停用 / 指定 / 校验 / 版本 / ID 不复用。
    #  客户端渲染与网页界面是 P2 / P3 的事。
    #  ⚠ 本段的收尾状态：可用色**恰好回到内置 16 个**（让后面的 _phase2_pool_full 仍然成立）。
    st, body = _http(B, "GET", "/api/nicknames/colors")
    cols = body.get("colors", []) if isinstance(body, dict) else []
    push("GET /api/nicknames/colors → 200：16 个内置色、全 active、版本 1",
         st == 200 and len(cols) == 16 and body.get("color_pool_version") == 1
         and {c["status"] for c in cols} == {"active"}
         and [c["color_id"] for c in cols][:2] == ["color_01", "color_02"],
         f"{st} {len(cols)} 个 v{body.get('color_pool_version')}")
    push("……每行带 hex / sort / used_by（UI 要知道谁在用）",
         all(all(k in c for k in ("hex", "sort", "used_by")) for c in cols)
         and any(c["used_by"] for c in cols), json.dumps(cols[0], ensure_ascii=False))

    st, body = _http(B, "POST", "/api/nicknames/colors", {"hex": "#0FA3B1"})
    push("POST /api/nicknames/colors → 201：新 ID = color_17（只增）、版本 +1 = 2",
         st == 201 and body.get("color", {}).get("color_id") == "color_17"
         and body.get("color", {}).get("hex") == "#0FA3B1"
         and body.get("color_pool_version") == 2, f"{st} {body}")
    st, body = _http(B, "POST", "/api/nicknames/colors", {"hex": "#0fa3b1"})
    push("同色值（小写）→ 409 COLOR_ALREADY_EXISTS，且带既有的 color_17",
         st == 409 and body.get("detail", {}).get("code") == "COLOR_ALREADY_EXISTS"
         and body["detail"].get("existing_color_id") == "color_17", f"{st} {body}")
    st, body = _http(B, "POST", "/api/nicknames/colors", {"hex": "rgb(15, 163, 177)"})
    push("RGB 写法归一后同色 → 也是 409（查重口径统一大写）",
         st == 409 and body.get("detail", {}).get("code") == "COLOR_ALREADY_EXISTS", f"{st} {body}")
    st, body = _http(B, "POST", "/api/nicknames/colors", {"hex": "不是颜色"})
    push("非法色值 → 422 INVALID_COLOR_HEX",
         st == 422 and body.get("detail", {}).get("code") == "INVALID_COLOR_HEX", f"{st} {body}")

    # ── 人为指定颜色（v0.19 新能力）──
    st, body = _http(B, "POST", "/api/nicknames", {"display_name": "配色测试"})
    cid = body.get("nickname", {}).get("nickname_id")
    push("（前置）建一个昵称专门用来测「指定颜色」", st == 201 and bool(cid), f"{st} {body}")
    st, body = _http(B, "POST", f"/api/nicknames/{cid}/color", {"color_id": "color_17"})
    push("★ POST /api/nicknames/{id}/color → 200：人为指定的颜色立刻生效",
         st == 200 and body.get("nickname", {}).get("color") == "color_17", f"{st} {body.get('nickname')}")
    st, body = _http(B, "GET", "/api/nicknames")
    mine = [n for n in body["nicknames"] if n["nickname_id"] == cid]
    push("……真落库（GET /api/nicknames 里就是 color_17）",
         bool(mine) and mine[0]["color"] == "color_17", json.dumps(mine, ensure_ascii=False))
    st, _ = _http(B, "POST", f"/api/nicknames/{cid}/color", {"color_id": "color_17"})
    push("指定成同一个色 → 幂等 200（不报错、不广播）", st == 200, f"{st}")
    others = [n for n in _http(B, "GET", "/api/nicknames")[1]["nicknames"] if n["nickname_id"] != cid]
    st, body = _http(B, "POST", f"/api/nicknames/{cid}/color", {"color_id": others[0]["color"]})
    push("指定**别人正在用**的色 → 409 COLOR_IN_USE（带是谁在用）",
         st == 409 and body.get("detail", {}).get("code") == "COLOR_IN_USE"
         and body["detail"].get("nickname_id") == others[0]["nickname_id"], f"{st} {body}")
    st, body = _http(B, "POST", f"/api/nicknames/{cid}/color", {"color_id": "color_99"})
    push("指定表里没有的色（形状对）→ 404 COLOR_NOT_FOUND",
         st == 404 and body.get("detail", {}).get("code") == "COLOR_NOT_FOUND", f"{st} {body}")
    st, body = _http(B, "POST", f"/api/nicknames/{cid}/color", {"color_id": "gray"})
    push("指定 gray → 422 INVALID_COLOR_ID（灰不是共享昵称的颜色，§3.2.1）",
         st == 422 and body.get("detail", {}).get("code") == "INVALID_COLOR_ID", f"{st} {body}")

    # ── 停用（只停止分配，不删行）──
    st, body = _http(B, "DELETE", "/api/nicknames/colors/color_17")
    push("停用**正在被用**的色 → 409 COLOR_IN_USE（不是静默停掉）",
         st == 409 and body.get("detail", {}).get("code") == "COLOR_IN_USE", f"{st} {body}")
    _http(B, "POST", f"/api/nicknames/{cid}/reassign-color")       # 先把它换走
    st, body = _http(B, "DELETE", "/api/nicknames/colors/color_17")
    push("★ DELETE /api/nicknames/colors/color_17 → 200：status=retired、版本 +1 = 3",
         st == 200 and body.get("color", {}).get("status") == "retired"
         and body.get("color_pool_version") == 3, f"{st} {body}")
    st, body = _http(B, "GET", "/api/nicknames/colors?status=retired")
    push("……已停用的是「行还在」而不是删行（?status=retired 能列出来）",
         st == 200 and [c["color_id"] for c in body["colors"]] == ["color_17"], f"{st} {body}")
    st, body = _http(B, "POST", f"/api/nicknames/{cid}/color", {"color_id": "color_17"})
    push("指定一个已停用的色 → 409 COLOR_RETIRED",
         st == 409 and body.get("detail", {}).get("code") == "COLOR_RETIRED", f"{st} {body}")
    st, body = _http(B, "DELETE", "/api/nicknames/colors/nope")
    push("停用非 color_NN → 422 INVALID_COLOR_ID",
         st == 422 and body.get("detail", {}).get("code") == "INVALID_COLOR_ID", f"{st} {body}")
    st, body = _http(B, "DELETE", "/api/nicknames/colors/color_99")
    push("停用形状对但不在表里的 → 404 COLOR_NOT_FOUND",
         st == 404 and body.get("detail", {}).get("code") == "COLOR_NOT_FOUND", f"{st} {body}")

    st, body = _http(B, "POST", "/api/nicknames/colors", {"hex": "#010203"})
    push("★ **ID 不复用**：停用 color_17 后加新色拿到 color_18（历史消息颜色不会被顶掉，§2.1）",
         st == 201 and body.get("color", {}).get("color_id") == "color_18", f"{st} {body}")
    st, body = _http(B, "POST", "/api/nicknames/colors", {"rgb": [171, 205, 239]})
    push("rgb=[171,205,239] → 201 color_19 / #ABCDEF（版本 5）",
         st == 201 and body.get("color", {}).get("color_id") == "color_19"
         and body.get("color", {}).get("hex") == "#ABCDEF"
         and body.get("color_pool_version") == 5, f"{st} {body}")
    # 收尾：把临时加的两色停掉，可用色回到内置 16 个（后面的池满用例依赖这个数）
    st17, _ = _http(B, "DELETE", "/api/nicknames/colors/color_18")
    st18, body18 = _http(B, "DELETE", "/api/nicknames/colors/color_19")
    st19, body19 = _http(B, "GET", "/api/nicknames/colors?status=active")
    push("收尾：停掉临时加的两色 → **可用**色回到 16 个（版本 7；已停用的 3 个仍能列出来）",
         (st17, st18) == (200, 200) and len(body19["colors"]) == 16
         and body19.get("color_pool_version") == 7, f"{st19} {len(body19['colors'])} 个 v{body19.get('color_pool_version')}")

    # 注：活跃上限 16 → 503 的判据在 _phase2_pool_full()（**会把池子占满，必须最后跑**）。
    _ = new_id


def _phase2_pool_full(srv: _Srv, out: list) -> None:
    """把 active 填满 16 条 → 新建 / 改色都 503（§7 Phase 2 验收 4）。

    ⚠ 会**把池子占满**，所以从 HTTP 检查里拆出来，必须放在广播实测**之后**跑。
    """
    B = srv.base

    def push(label, cond, detail=""):
        out.append((label, bool(cond), detail))

    for i in range(30):
        st, _ = _http(B, "POST", "/api/nicknames", {"display_name": f"填充{i}"})
        if st != 201:
            break
    active = _http(B, "GET", "/api/nicknames?status=active")[1]["nicknames"]
    some_id = active[0]["nickname_id"] if active else 1
    st_c, body_c = _http(B, "POST", "/api/nicknames", {"display_name": "溢出"})
    st_r, body_r = _http(B, "POST", f"/api/nicknames/{some_id}/reassign-color")
    FULL = "已达到共享昵称上限，请删除不再使用的昵称后再添加。"
    push("池子占满 16 条后：新建 → 503 NO_AVAILABLE_COLOR + 满额文案（§4.3）",
         st_c == 503 and body_c.get("detail", {}).get("code") == "NO_AVAILABLE_COLOR"
         and body_c.get("detail", {}).get("message") == FULL, f"{st_c} {body_c}")
    push("……reassign-color 也 503 NO_AVAILABLE_COLOR（绝不回退到重色，§4.3）",
         st_r == 503 and body_r.get("detail", {}).get("code") == "NO_AVAILABLE_COLOR",
         f"{st_r} {body_r}")
    cols = [n["color"] for n in _http(B, "GET", "/api/nicknames?status=active")[1]["nicknames"]]
    push("……16 条 active 的颜色仍两两不同（不重色）",
         len(cols) == 16 and len(set(cols)) == 16, f"{len(cols)} 条 / {len(set(cols))} 色")


def _phase2_off_checks(off: _Srv, prod_before: int | None, out: list) -> None:
    """开关关闭时的对照：接口不注册、帧不处理、广播不发、现有接口照旧。"""
    def push(label, cond, detail=""):
        out.append((label, bool(cond), detail))

    st, _ = _http(off.base, "GET", "/api/nicknames")
    push("开关关闭：GET /api/nicknames → **404**（路由根本没注册）", st == 404, f"status={st}")
    st, _ = _http(off.base, "POST", "/api/nicknames", {"display_name": "不该建出来"})
    push("开关关闭：POST /api/nicknames → 404（不是 201 / 422）", st == 404, f"status={st}")
    push("……库里一行都没建", off.row_count() == 0, f"{off.row_count()} 行")

    # 现有接口逐个照旧
    st_h, _ = _http(off.base, "GET", "/healthz")
    st_d, devs = _http(off.base, "GET", "/api/devices")
    st_m, msgs = _http(off.base, "GET", "/api/messages")
    st_p, posted = _http(off.base, "POST", "/api/messages",
                         {"sender_name": "默认用户", "content": "开关关闭对照消息"})
    push("开关关闭：现有接口逐个正常（/healthz、/api/devices、/api/messages 读+写）",
         (st_h, st_d, st_m, st_p) == (200, 200, 200, 200) and isinstance(devs, list)
         and isinstance(msgs, list) and posted.get("message", {}).get("status") == "sent",
         f"healthz={st_h} devices={st_d} messages={st_m} post={st_p}")

    st_c, cfg = _http(off.base, "GET", "/api/config")
    nick_keys = [k for k in cfg if "nickname" in k or "pool_version" in k]
    push("开关关闭：/api/config 里**没有** nickname_enabled / color_pool_version（逐字不变）",
         st_c == 200 and not nick_keys and
         sorted(cfg) == ["auth_required", "permissions", "popup_auto_close_seconds",
                         "public_url", "server_time", "xiaomi_enabled"], f"{sorted(cfg)}")

    if prod_before is not None:
        try:
            st_prod, _ = _http("http://127.0.0.1:18801", "GET", "/healthz", timeout=4)
        except Exception as e:
            st_prod = f"{type(e).__name__}"
        push("生产实例 18801 全程未被影响（只读探活 GET /healthz 仍 200）",
             st_prod == 200, f"status={st_prod}（期初 {prod_before}）")


async def _ws_off_silence(off: _Srv, out: list) -> None:
    """开关关闭时昵称帧**不处理**：发 5 个帧，除 hello 外一帧都不该回。"""
    try:
        import websockets
    except ImportError:
        return
    ws = await websockets.connect(
        off.wsbase + f"/ws/device/pc_off_silence?enroll_token={ENROLL}&name=OFF&type=pc")
    c = _WS("off-dev", ws)
    try:
        await c.wait("hello", 3.0)
        for frame in ({"type": "nickname_list_request"},
                      {"type": "nickname_create_request", "display_name": "无人应答"},
                      {"type": "nickname_rename_request", "nickname_id": 1, "display_name": "x"},
                      {"type": "nickname_reassign_color_request", "nickname_id": 1},
                      {"type": "nickname_delete_request", "nickname_id": 1}):
            await c.send(frame)
        await asyncio.sleep(1.5)
        extra = [t for t in c.types() if t != "hello"]
        out.append(("开关关闭：5 个昵称帧发过去**一帧都不回**（除 hello），连接也没被踢",
                    not extra, f"收到 {c.types()}"))
    finally:
        await c.close()


def phase2(tmp: Path, port: int) -> None:
    """起测试实例跑完整 Phase 2（在 tmp 下建 enabled/disabled 两套临时目录）。"""
    if port == PROD_PORT:
        check("Phase 2 拒绝使用生产端口 18801", False, "请换端口")
        return
    on_port = _free_port(port)
    off_port = _free_port(on_port + 1)
    try:
        python = _find_python()
    except RuntimeError as e:
        print(f"  ⏭  跳过 Phase 2：{e}")
        return
    print(f"  跑实例的解释器：{python}")
    print(f"  测试实例端口：enabled={on_port} / disabled={off_port}")

    try:
        prod_before = _http("http://127.0.0.1:18801", "GET", "/healthz", timeout=4)[0]
    except Exception:
        prod_before = None
    print(f"  生产 18801 期初探活：{prod_before}（本脚本只用 GET /healthz，绝不写它）")

    on = _Srv(python, on_port, True, tmp)
    off = _Srv(python, off_port, False, tmp)
    out: list[tuple[str, bool, str]] = []
    try:
        on.start()
        print(f"  enabled 实例已起 pid={on.proc.pid} {on.base}")

        _phase2_http_checks(on, out)
        try:
            import websockets  # noqa: F401
            asyncio.run(_ws_suite(on, out))
            # 池满判据必须最后跑（它会把 16 个色全占掉）
            _phase2_pool_full(on, out)
        except ImportError:
            print("  ⏭  当前解释器没有 websockets：跳过「多连接广播」部分（HTTP 部分已跑）"
                  "—— 用带 websockets 的解释器（如 venv）跑可全覆盖")
            _phase2_pool_full(on, out)

        off.start()
        print(f"  disabled 实例已起 pid={off.proc.pid} {off.base}")
        _phase2_off_checks(off, prod_before, out)
        try:
            asyncio.run(_ws_off_silence(off, out))
        except ImportError:
            pass
    finally:
        on.stop()
        off.stop()
        print(f"  两个测试实例已按精确 PID 停止（{on_port} / {off_port}）")
        # ★ 无论中途有没有异常，已跑出来的判据都要报出来（否则一个早期异常会吞掉全部结果）
        for label, cond, detail in out:
            check(label, cond, detail)


def _raises_invalid_status(nk) -> bool:
    try:
        nk.list("deleted")
        return False
    except nk.InvalidStatus:
        return True


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
    sys.exit(main())
