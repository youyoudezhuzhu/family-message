#!/usr/bin/env python3
"""协议对账：`server/` 里真实收发的帧 vs `docs/PROTOCOL.md` 的帧表（CORE-REFACTOR-PLAN §6 Phase 5 验收①）。

为什么需要它：昵称系统（v0.17.0）上线时，10 个 `nickname_*` 帧**一个都没写进协议文档**，
而「文档里的帧表格」正是 Android / iOS 新端接入时唯一的事实来源 —— 靠肉眼对账必然再漏。
这个脚本把对账变成退出码，改协议不同步文档就红。

判据（三条，全部可机械复现）：
  ① 服务端**发出**的每个 type（`server/main.py` + `server/hub.py` 里的 `"type": "<name>"`，
     **跳过注释行**）必须出现在 `docs/PROTOCOL.md` 里；
     例外只有 `RESERVED`（代码里被注释掉、文档必须显式标注「预留/未启用」的项）。
  ② 服务端**受理**的设备上行帧（`mtype == "…"` / `mtype in (…)` / `NICKNAME_FRAME_TYPES`）
     必须出现在文档里。
  ③ 反向：文档**帧表首列**列出的 type，必须在服务端有发出点或受理点（白名单 `DOC_ONLY`
     放行少数非帧条目，如客户端心跳 `ping` 与能力名 `unlock`）。

用法：python3 tools/check_protocol_doc.py         # 退出码 0 = 对齐，1 = 有问题
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DOC = ROOT / "docs" / "PROTOCOL.md"
SERVER_FILES = [ROOT / "server" / "main.py", ROOT / "server" / "hub.py"]

#: 代码里存在但**被注释掉**（不再发出）的帧：文档必须有，且必须写明「预留」
RESERVED = {"message_status"}

#: 文档帧表首列出现、但既不是服务端发出也不是设备上行的条目（白名单，各有理由）
DOC_ONLY = {
    "ping",      # 客户端 → 服务端的网页端心跳（服务端只回 pong）
}

RE_TYPE = re.compile(r'"type"\s*:\s*"([a-z_]+)"')
#: `_nickname_list_frame("nickname_list_response")` 这类「type 放在变量里」的构造点
RE_LIST_FRAME = re.compile(r'_nickname_list_frame\(\s*"([a-z_]+)"\s*\)')
RE_MTYPE_EQ = re.compile(r'mtype\s*==\s*"([a-z_]+)"')
RE_MTYPE_IN = re.compile(r'mtype\s+in\s+\(([^)]*)\)')
RE_TUPLE = re.compile(r"NICKNAME_FRAME_TYPES\s*=\s*\(([^)]*)\)")
RE_QUOTED = re.compile(r'"([a-z_]+)"')
RE_BACKTICK = re.compile(r"`([a-z][a-z0-9_]+)`")
#: 帧表的行：首列是 `type`
RE_TABLE_ROW = re.compile(r"^\|\s*`([a-z][a-z0-9_]+)`\s*\|", re.MULTILINE)
RE_SECTION = re.compile(r"^##\s+(.*)$", re.MULTILINE)
#: 只有这几节的表格是**帧表**（别把 §3 的查询参数表、§10 的能力表算进来）
FRAME_SECTIONS = ("服务端 → 设备", "设备 → 服务端", "/ws/web 广播")


def code_lines(path: Path):
    """逐行读，返回 (行号, 去注释后的代码行) —— 行首 # 开头的整行注释直接跳过。"""
    for i, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        if raw.lstrip().startswith("#"):
            continue
        yield i, raw


def emitted_types() -> dict[str, str]:
    found: dict[str, str] = {}
    for path in SERVER_FILES:
        for i, line in code_lines(path):
            for name in RE_TYPE.findall(line) + RE_LIST_FRAME.findall(line):
                found.setdefault(name, f"{path.name}:{i}")
    return found


def frame_sections(doc: str) -> list[tuple[str, str]]:
    """按 `## ` 切段，只返回帧表所在的那几节（(标题, 正文)）。"""
    parts = RE_SECTION.split(doc)
    out: list[tuple[str, str]] = []
    # split 后形如 [前言, 标题1, 正文1, 标题2, 正文2, …]
    for i in range(1, len(parts) - 1, 2):
        title, body = parts[i], parts[i + 1]
        if any(key in title for key in FRAME_SECTIONS):
            out.append((title, body))
    return out


def handled_types() -> dict[str, str]:
    found: dict[str, str] = {}
    for path in SERVER_FILES:
        for i, line in code_lines(path):
            for name in RE_MTYPE_EQ.findall(line):
                found.setdefault(name, f"{path.name}:{i}")
            for group in RE_MTYPE_IN.findall(line):
                for name in RE_QUOTED.findall(group):
                    found.setdefault(name, f"{path.name}:{i}")
    # NICKNAME_FRAME_TYPES 是跨行的元组，单独抓
    text = "\n".join(p.read_text(encoding="utf-8") for p in SERVER_FILES)
    for group in RE_TUPLE.findall(text):
        for name in RE_QUOTED.findall(group):
            found.setdefault(name, "main.py:NICKNAME_FRAME_TYPES")
    return found


def main() -> int:
    doc = DOC.read_text(encoding="utf-8")
    doc_tokens = set(RE_BACKTICK.findall(doc))
    doc_table = set()
    for _title, body in frame_sections(doc):
        doc_table.update(RE_TABLE_ROW.findall(body))
    emitted = emitted_types()
    handled = handled_types()

    problems: list[str] = []

    missing = sorted(set(emitted) - RESERVED - doc_tokens)
    if missing:
        problems.append("服务端在发、但文档里没有的帧：" + "、".join(
            f"`{t}`（{emitted[t]}）" for t in missing))

    missing_up = sorted(set(handled) - doc_tokens)
    if missing_up:
        problems.append("服务端受理、但文档里没有的上行帧：" + "、".join(
            f"`{t}`（{handled[t]}）" for t in missing_up))

    for name in sorted(RESERVED):
        if name not in doc_tokens:
            problems.append(f"预留帧 `{name}` 没写进文档")
            continue
        line = next((ln for ln in doc.splitlines() if f"`{name}`" in ln), "")
        if "预留" not in line and "未启用" not in line:
            problems.append(f"`{name}` 是预留项，文档里必须显式标注「预留/未启用」：{line.strip()[:60]}")

    stray = sorted(doc_table - set(emitted) - set(handled) - DOC_ONLY - RESERVED)
    if stray:
        problems.append("文档帧表里有、但服务端既不发也不受理：" + "、".join(f"`{t}`" for t in stray))

    print(f"服务端发出 {len(emitted)} 种｜设备上行 {len(handled)} 种｜"
          f"文档帧表 {len(doc_table)} 行｜文档出现 token {len(doc_tokens)} 个")
    print("预留项：" + "、".join(f"`{t}`" for t in sorted(RESERVED)))

    if problems:
        print("\n✗ 协议文档与服务端不一致：")
        for p in problems:
            print("   -", p)
        return 1
    print("\n✓ PROTOCOL.md 与服务端逐条对齐")
    return 0


if __name__ == "__main__":
    sys.exit(main())
