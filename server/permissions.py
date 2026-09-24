"""权限模型（最小可用版）。

── 现状与取舍 ────────────────────────────────────────────────
这个系统目前**没有用户、没有角色**：Web 只有一个访问口令，`require_web`
只回答「登录了没」。需求文档 §13 要求 `device.unlock` 权限、且**后端必须强制校验**
（不能只靠前端隐藏按钮）。

所以这里先做两件事，且**不改变现有体验**：
  1. 建立权限常量与判定接口，为将来多用户 / 受限角色留位置
  2. 后端在关键端点上真正检查（403），而不是只信前端

当前策略：**一旦通过 Web 口令登录，即拥有全部权限**。以后要加"客人只能发消息、
不能解锁/关机"这类角色时，只改 `for_session()` 一处，所有端点自动生效。
"""
from __future__ import annotations

import os

# ── 权限清单 ──────────────────────────────────────────────────
# 新增权限时同步：
#   · web/static/app.js 里按钮的可用性判断
#   · /api/config 返回的 permissions（前端据此决定 UI）
MESSAGE_SEND = "message.send"
DEVICE_VIEW = "device.view"
DEVICE_SCREENSHOT = "device.screenshot"
DEVICE_WAKE = "device.wake"
DEVICE_SHUTDOWN = "device.shutdown"
DEVICE_UNLOCK = "device.unlock"          # 最高风险的设备操作之一

ALL_PERMISSIONS = [
    MESSAGE_SEND,
    DEVICE_VIEW,
    DEVICE_SCREENSHOT,
    DEVICE_WAKE,
    DEVICE_SHUTDOWN,
    DEVICE_UNLOCK,
]


def for_session() -> list[str]:
    """当前会话拥有的权限。

    还没有用户系统的阶段：登录即全部权限（保持现有行为不变）。

    `FM_PERMISSIONS` 环境变量可以覆盖（逗号分隔）—— **仅供测试**用来验证
    「没有 device.unlock 时必须返回 403」这条后端强制校验不会被绕过。
    """
    override = os.environ.get("FM_PERMISSIONS")
    if override is not None:
        return [x.strip() for x in override.split(",") if x.strip()]
    return list(ALL_PERMISSIONS)


def has(perms: list[str] | tuple[str, ...], perm: str) -> bool:
    return perm in perms
