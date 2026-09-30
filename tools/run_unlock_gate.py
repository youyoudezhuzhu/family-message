#!/usr/bin/env python3
"""临时实例（复用 test_nickname_web 的 Srv 引导）+ 解锁相关套件回归。

跑三件事：
  1. tools/check_unlock_policy.py  —— 入口策略（浏览器里调纯函数）
  2. tools/test_unlock.py          —— 解锁主套件（HTTP + 假设备）
  3. tools/test_nickname_web.py    —— 网页端套件（含新加的 F2 策略断言）

端口固定 18899（生产 18801 硬拒），实例是临时的，跑完就停、临时目录删掉。
"""
from __future__ import annotations

import importlib.util
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

# 仓库根：优先取环境变量，否则按本文件位置推（不写任何个人路径 —— 隐私门禁会拦）
REPO = Path(os.environ.get("FM_REPO") or Path(__file__).resolve().parents[1])
VENV_PY = os.environ.get("FM_VENV_PY", sys.executable)
PORT = 18899

sys.path.insert(0, str(REPO / "tools"))
spec = importlib.util.spec_from_file_location("tnw", REPO / "tools" / "test_nickname_web.py")
tnw = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tnw)

tmp = Path(tempfile.mkdtemp(prefix="fm-gate-", dir="/vol1/@appdata/hermes-agent/tmp"))
srv = tnw.Srv(tmp, PORT, True)
print(f"临时实例目录：{tmp}")
srv.start()
print(f"实例已起：{srv.base}")

results: dict[str, int] = {}
try:
    for label, cmd in [
        ("1/3 入口策略", [VENV_PY, "tools/check_unlock_policy.py", srv.base]),
        ("2/3 解锁主套件", [VENV_PY, "tools/test_unlock.py", srv.base]),
    ]:
        print(f"\n══ {label} ══")
        p = subprocess.run(cmd, cwd=str(REPO), capture_output=True, text=True)
        tail = (p.stdout or "").strip().splitlines()[-14:]
        print("\n".join(tail))
        if p.returncode != 0 and p.stderr:
            print("[stderr] " + p.stderr.strip().splitlines()[-1][:200])
        results[label] = p.returncode
finally:
    srv.stop()
    shutil.rmtree(tmp, ignore_errors=True)

# 网页端套件**自己**起实例（同样用 18899），必须等上面的临时实例把端口放掉再跑
print("\n══ 3/3 网页端套件（自带实例，含 F2 解锁入口策略断言）══")
p = subprocess.run([VENV_PY, "tools/test_nickname_web.py", "--no-shots"],
                   cwd=str(REPO), capture_output=True, text=True)
print("\n".join((p.stdout or "").strip().splitlines()[-14:]))
if p.returncode != 0 and p.stderr:
    print("[stderr] " + p.stderr.strip().splitlines()[-1][:200])
results["3/3 网页端套件"] = p.returncode

print("\n══ 汇总 ══")
for k, v in results.items():
    print(f"  {'✓' if v == 0 else '✗'} {k} (exit={v})")
sys.exit(1 if any(v != 0 for v in results.values()) else 0)
