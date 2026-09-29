#!/usr/bin/env python3
"""隐私自检（CI 门禁）：拦住"把真实内网特征 / 口令示例 / 个人称呼提交进仓库"。

为什么要有它：2026-09-29 那次对外发布前清理发现，仓库与**历史**里长期躺着真实内网 IP、
设备注册口令示例值、个人称呼，甚至进过已发布的 Release 资产 —— 清一次靠人记，会被再犯。
这条检查把「不该进仓库的串」变成机器判据，和 check_protocol_doc / check_agent_refs 一样进 CI。

用法：
    python tools/check_privacy.py            # 扫工作区（默认）
    python tools/check_privacy.py --base .   # 指定根目录
退出码：0 = 干净；1 = 有命中（CI 直接红）。
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

# 不扫的目录（第三方/产物/缓存）与二进制后缀
SKIP_DIRS = {".git", "bin", "obj", "node_modules", ".venv", "__pycache__"}
#: 本文件自己：检查项正则在源码里写着（不然没法当判据），扫自己必然命中 —— 跳过。
SELF = "tools/check_privacy.py"
BINARY_EXT = {".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".pdf", ".zip", ".fpk",
              ".tgz", ".gz", ".woff", ".woff2", ".ttf", ".db", ".wal", ".shm", ".so", ".dll", ".exe"}

#: 检查项：名称 → (正则, 说明, 例外)
CHECKS: list[tuple[str, re.Pattern[str], str, re.Pattern[str] | None]] = [
    ("lan-ip-nonstandard",
     re.compile(r"192\.168\.(?!1\.)\d{1,3}\.\d{1,3}"),
     "非通用段的内网 IP（真实内网特征）→ 换成 192.168.1.x",
     None),
    ("vpn-ip",
     re.compile(r"\b172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3}\b"),
     "私有段 172.16-31.* 的真实地址 → 换成通用段或删掉",
     None),
    ("real-name",
     re.compile(r"辉哥|振辉|倚苓"),
     "个人称呼 / 真实姓名（含示例数据）→ 改成「用户」「老大」这类中性词",
     None),
    ("handle",
     re.compile(r"pumpedup"),
     "个人账号/手柄 → 用中性设备名",
     None),
    ("known-token",
     re.compile(r"family-2026"),
     "曾经用过的设备注册口令示例值 → FAMILY-TEST-TOKEN",
     None),
    ("user-uid-path",
     re.compile(r"/vol1/1000\b"),
     "交付文档里的 uid 路径 → /vol1/<uid>",
     None),
    ("yaml-token-value",
     re.compile(r"""enroll_token\s*:\s*["'](?!\s*["'])([^"']{6,})["']"""),
     "配置里写着非占位符的注册口令 → 留空或用 FAMILY-TEST-TOKEN",
     # 允许：明确的测试占位 / 模板变量（shell 里的 {enroll}、<your-token> 这类）
     re.compile(r"FAMILY-TEST-TOKEN|CHANGE-ME|change-me|<[^>]*>|\{[^}]*\}|\$[A-Za-z_]+")),
    ("hardcoded-secret",
     re.compile(r"ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}"),
     "疑似硬编码的 GitHub 令牌 → 立刻撤销并改从环境变量读",
     None),
]


def iter_files(root: Path):
    for p in root.rglob("*"):
        if not p.is_file():
            continue
        if any(part in SKIP_DIRS for part in p.parts):
            continue
        if p.suffix.lower() in BINARY_EXT:
            continue
        if str(p.relative_to(root)).replace("\\", "/") == SELF:
            continue
        yield p


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default=str(Path(__file__).resolve().parent.parent))
    args = ap.parse_args()
    root = Path(args.base).resolve()

    hits: list[tuple[str, str, int, str]] = []
    scanned = 0
    for f in iter_files(root):
        try:
            text = f.read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError):
            continue
        scanned += 1
        for name, pat, why, allow in CHECKS:
            for m in pat.finditer(text):
                seg = m.group(0)
                if allow and allow.search(seg):
                    continue
                if allow and name == "yaml-token-value" and allow.search(m.group(1) or ""):
                    continue
                line = text[:m.start()].count("\n") + 1
                hits.append((str(f.relative_to(root)), name, line, seg))

    print(f"═══ 隐私自检：扫了 {scanned} 个文本文件 ═══")
    if hits:
        print(f"\n✗ 命中 {len(hits)} 处（这些串不该进仓库）：")
        for rel, name, line, seg in hits[:40]:
            print(f"   {rel}:{line}  [{name}]  {seg[:60]}")
        if len(hits) > 40:
            print(f"   …还有 {len(hits) - 40} 处")
        print("\n规则与替换表见 skills/github/repo-publish-privacy-hygiene。")
        return 1
    print("✓ 没有命中：内网特征 / 口令示例 / 个人称呼 / 硬编码令牌")
    return 0


if __name__ == "__main__":
    sys.exit(main())
