#!/usr/bin/env python3
"""验「远程解锁」入口的策略：只有离线才拦，会话状态只做二次确认。

背景（用户实测反馈）：PC 锁屏时网页端点不动「远程解锁」，因为原来的策略是
`windows_state != locked/logon_screen → 按钮禁用`，而状态由心跳上报、可能滞后，
同一台机器还有 headless/交互式两个实例可能报出旧值 ⇒ 用户明明锁着屏也没法验证。

现在的契约（前端 + 服务端同一套）：
  · 离线 → 拦（按钮禁用 + 后端 409）
  · unlocked / unknown → **不拦**，点下去先弹确认（解释清楚"系统显示未锁屏"）
  · locked / logon_screen → 直接下发

这里直接调页面里暴露的纯函数 `window.FM_UNLOCK_POLICY`（app.js 里就是那两段判定），
不依赖设备数据，跑起来快且没有副作用。

用法：python tools/check_unlock_policy.py [base_url]
"""
from __future__ import annotations

import sys

from playwright.sync_api import sync_playwright

BASE = (sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:18899").rstrip("/")

fails: list[str] = []


def check(ok: bool, label: str, extra: str = "") -> None:
    print(f"  {'✓' if ok else '✗'} {label}" + (f"   ← {extra}" if extra else ""))
    if not ok:
        fails.append(label)


def main() -> int:
    with sync_playwright() as pw:
        b = pw.chromium.launch()
        ctx = b.new_context(viewport={"width": 1280, "height": 900}, locale="zh-CN")
        pg = ctx.new_page()
        errs: list[str] = []
        pg.on("pageerror", lambda e: errs.append(str(e)[:200]))
        pg.goto(BASE, wait_until="domcontentloaded")
        pg.wait_for_timeout(600)

        has = pg.evaluate("() => !!(window.FM_UNLOCK_POLICY && window.FM_UNLOCK_POLICY.blockReason)")
        check(has is True, "页面暴露了 FM_UNLOCK_POLICY（app.js 已加载）", str(has))
        if not has:
            print("\n页面没加载到 app.js，后面的判定没法测")
            ctx.close()
            b.close()
            return 1

        cases = pg.evaluate("""() => {
          const P = window.FM_UNLOCK_POLICY;
          const call = (fn, d) => { try { return P[fn](d); } catch (e) { return 'EXC:' + e; } };
          return {
            offline_block:   call('blockReason', { online: false, windows_state: 'locked' }),
            locked_block:    call('blockReason', { online: true,  windows_state: 'locked' }),
            unlocked_block:  call('blockReason', { online: true,  windows_state: 'unlocked' }),
            unknown_block:   call('blockReason', { online: true,  windows_state: 'unknown' }),
            locked_warn:     call('warnReason',  { online: true,  windows_state: 'locked' }),
            logon_warn:      call('warnReason',  { online: true,  windows_state: 'logon_screen' }),
            unlocked_warn:   call('warnReason',  { online: true,  windows_state: 'unlocked' }),
            unknown_warn:    call('warnReason',  { online: true,  windows_state: 'unknown' }),
          };
        }""")

        print("\n═══ 远程解锁入口策略 ═══")
        for k, v in cases.items():
            print(f"    {k:16s} = {v!r}")

        print("\n判定：")
        check("离线" in (cases["offline_block"] or ""), "离线 → 拦（按钮禁用）", cases["offline_block"])
        check(cases["locked_block"] == "", "已锁屏 → 不拦", repr(cases["locked_block"]))
        check(cases["unlocked_block"] == "", "**显示未锁屏 → 不拦**（改为二次确认）",
              repr(cases["unlocked_block"]))
        check(cases["unknown_block"] == "", "状态未知 → 不拦（改为二次确认）",
              repr(cases["unknown_block"]))
        check(cases["locked_warn"] == "", "已锁屏 → 不弹确认（正是该解锁的时候）",
              repr(cases["locked_warn"]))
        check(cases["logon_warn"] == "", "登录界面 → 不弹确认", repr(cases["logon_warn"]))
        check("未锁屏" in (cases["unlocked_warn"] or ""), "显示未锁屏 → 弹确认并说明原因",
              cases["unlocked_warn"])
        check("会话状态" in (cases["unknown_warn"] or ""), "状态未知 → 弹确认并说明原因",
              cases["unknown_warn"])
        check(errs == [], "页面无 JS 异常", str(errs[:2]))

        ctx.close()
        b.close()

    print(f"\n{'全部通过' if not fails else '失败项：' + '；'.join(fails)}")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
