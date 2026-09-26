#!/usr/bin/env python3
"""把 PC 端本地界面组装成一个「构建后的 shell/ 目录」。

为什么需要它：PC 端界面在运行时只带 shell/ 这几个文件（app.html 同目录下
必须有 pc.css / pc.js / chat.js / tokens.css），而源文件分散在两处：
    web/shell/*            PC 端自己的页面与样式表现层
    web/static/chat.js     与网页端**同一份**群聊气泡组件（构建时复制）
    web/static/tokens.css  与网页端**同一份** Fluent 2 令牌（构建时复制）
本脚本按 docs/PC-LOCAL-UI.md 的「文件清单」把它们拼到一起，供本地测试与
打包前核对 —— 和 FamilyAgent.csproj 的 Content→shell/ 复制规则等价。

用法：
    python3 tools/pack_shell.py [目标目录]
不传目标目录就用临时目录（tempfile.mkdtemp）。最后一行打印：
    SHELL_DIR=<绝对路径>
"""
from __future__ import annotations

import re
import shutil
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WEB = ROOT / "web"
SHELL_SRC = WEB / "shell"
STATIC_SRC = WEB / "static"

# PC 端界面本体（web/shell/ 下，随 exe 打包）
LOCAL_FILES = ["app.html", "pc.css", "pc.js"]
# 与网页端共用的源文件（构建时复制到 shell/ 下与 app.html 同目录）
SHARED_FILES = ["chat.js", "tokens.css"]
# 保留的兜底页（首屏占位 / 连不上服务端时的诊断页）
KEEP_FILES = ["boot.html", "offline.html"]

# 组出来的 shell/ 必须齐这些，缺一个页面就白屏
REQUIRED = LOCAL_FILES + SHARED_FILES


def _guard_architecture(dest: Path) -> list[str]:
    """架构红线自检（app.html 必须自带界面、不靠 URL 参数定形态、不引外链）。

    返回问题列表；空列表 = 通过。
    """
    problems: list[str] = []
    html = (dest / "app.html").read_text(encoding="utf-8")
    js = (dest / "pc.js").read_text(encoding="utf-8")

    # 1. 不能有绝对外链（NAS 的 index.html 就是靠 <script src="http://…"> 粘上去的）
    for m in re.finditer(r'<(?:link|script)[^>]*?(?:href|src)="([^"]+)"', html):
        url = m.group(1)
        if "://" in url or url.startswith("//"):
            problems.append(f"app.html 引用了绝对地址：{url}")

    # 2. 形态不能由 URL 参数决定（?shell=1&mode= 那套已废）
    for pat in (r"location\.search", r"URLSearchParams"):
        if re.search(pat, html) or re.search(pat, js):
            problems.append(f"出现了 URL 参数逻辑：{pat}")

    # 3. 界面必须真的在本页：三个视图容器都要在 app.html 里
    for vid in ("v-client", "v-popup", "v-settings"):
        if f'id="{vid}"' not in html:
            problems.append(f"app.html 缺视图容器 #{vid}")

    # 4. 群聊气泡必须用共用组件 FMChat（不许自己画气泡）
    if "FMChat" not in js:
        problems.append("pc.js 没有使用 web/static/chat.js 的 FMChat")
    if "chat.js" not in html:
        problems.append("app.html 没有引入 chat.js")
    if "tokens.css" not in html:
        problems.append("app.html 没有引入 tokens.css")
    return problems


def pack(dest: str | Path | None = None, quiet: bool = False) -> Path:
    """把 shell/ 组装到 dest（None = 临时目录），返回目标目录。"""
    target = Path(dest) if dest else Path(tempfile.mkdtemp(prefix="fm-shell-"))
    target.mkdir(parents=True, exist_ok=True)

    for f in LOCAL_FILES + KEEP_FILES:
        src = SHELL_SRC / f
        if not src.is_file():
            raise FileNotFoundError(f"缺文件：{src}")
        shutil.copy2(src, target / f)

    for f in SHARED_FILES:
        src = STATIC_SRC / f
        if not src.is_file():
            raise FileNotFoundError(f"缺共享源文件：{src}")
        shutil.copy2(src, target / f)

    missing = [f for f in REQUIRED if not (target / f).is_file()]
    if missing:
        raise RuntimeError(f"组装结果缺文件：{missing}")

    problems = _guard_architecture(target)

    if not quiet:
        print(f"源：{SHELL_SRC} + {STATIC_SRC}")
        print(f"目标：{target}")
        for f in sorted(target.iterdir()):
            if f.is_file():
                print(f"  {f.name:<14} {f.stat().st_size:>7} B")
        if problems:
            print("架构自检：不通过")
            for p in problems:
                print("  ✗ " + p)
        else:
            print("架构自检：通过（无外链 / 无形态度 URL 参数 / 三视图齐 / 复用 chat.js + tokens.css）")
        print(f"SHELL_DIR={target}")

    if problems:
        raise SystemExit(1)
    return target


def main() -> int:
    dest = sys.argv[1] if len(sys.argv) > 1 else None
    pack(dest)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
