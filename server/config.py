"""配置加载：config.yaml + 环境变量覆盖。"""
from __future__ import annotations

import copy
import os
from pathlib import Path
from typing import Optional

import yaml

BASE_DIR = Path(__file__).resolve().parent
CONFIG_PATH = Path(os.environ.get("FM_CONFIG", str(BASE_DIR / "config.yaml")))

_DEFAULTS: dict = {
    "server": {"host": "0.0.0.0", "port": 18801, "public_url": ""},
    "data_dir": str(BASE_DIR / "data"),
    "web": {"password": "", "session_hours": 720},
    "device": {
        "enroll_token": "",
        "auto_register": True,
        "offline_after_seconds": 45,
    },
    "message": {"popup_auto_close_seconds": 0, "max_targets": 20, "history_limit": 30},
    "xiaomi": {
        "enabled": False,
        "username": "",
        "password": "",
        "region": "cn",
        "refresh_ahead_seconds": 86400,
        # 官方 OAuth2：redirect_url 必须是小米那边为 client_id 注册过的地址，
        # 默认沿用官方 HA 集成的注册地址；oauth_device_id 首次使用时自动生成并固定。
        "redirect_url": "http://homeassistant.local:8123",
        "oauth_device_id": "",
    },
}


def _merge(base: dict, override: dict) -> dict:
    out = copy.deepcopy(base)
    for k, v in (override or {}).items():
        if isinstance(v, dict) and isinstance(out.get(k), dict):
            out[k] = _merge(out[k], v)
        else:
            out[k] = v
    return out


def load_config() -> dict:
    raw = {}
    if CONFIG_PATH.exists():
        raw = yaml.safe_load(CONFIG_PATH.read_text(encoding="utf-8")) or {}
    cfg = _merge(_DEFAULTS, raw)

    # 环境变量覆盖（Docker 部署时用）
    if os.environ.get("FM_DATA_DIR"):
        cfg["data_dir"] = os.environ["FM_DATA_DIR"]
    if os.environ.get("FM_PORT"):
        cfg["server"]["port"] = int(os.environ["FM_PORT"])
    if os.environ.get("FM_WEB_PASSWORD") is not None:
        cfg["web"]["password"] = os.environ["FM_WEB_PASSWORD"]
    if os.environ.get("FM_ENROLL_TOKEN") is not None:
        cfg["device"]["enroll_token"] = os.environ["FM_ENROLL_TOKEN"]
    if os.environ.get("FM_XIAOMI_USERNAME"):
        cfg["xiaomi"]["username"] = os.environ["FM_XIAOMI_USERNAME"]
    if os.environ.get("FM_XIAOMI_PASSWORD"):
        cfg["xiaomi"]["password"] = os.environ["FM_XIAOMI_PASSWORD"]

    Path(cfg["data_dir"]).mkdir(parents=True, exist_ok=True)
    return cfg


CONFIG = load_config()


def save_config(cfg: Optional[dict] = None) -> None:
    """把内存里的配置写回 config.yaml。

    只写「用户可能改过」的那几段，不把全部默认值固化进文件 ——
    否则以后改默认值对已安装的用户就不生效了。
    """
    # ── 安全阀：绝不写代码目录里的那份 config.yaml ──────────────────
    # 线上部署时 cmd/main 会把 FM_CONFIG 指到 $TRIM_PKGHOME/config.yaml；
    # 代码目录里的那份是「安装模板」，带完整注释、随升级分发。
    # 一旦被运行时写回：注释会被 yaml.safe_dump 抹掉，本机生成的
    # oauth_device_id 之类也会被带进仓库（所有人共用一个 id）。
    try:
        if CONFIG_PATH.resolve().parent == BASE_DIR.resolve():
            print(f"[config] 拒绝写入代码目录里的模板配置，已跳过：{CONFIG_PATH}", flush=True)
            return
    except OSError:
        pass

    cfg = cfg or CONFIG
    out: dict = {}
    if CONFIG_PATH.exists():
        try:
            out = yaml.safe_load(CONFIG_PATH.read_text(encoding="utf-8")) or {}
        except Exception:
            out = {}

    for section in ("server", "web", "device", "message", "xiaomi"):
        if section in cfg:
            out[section] = dict(cfg[section])

    # data_dir 由部署决定，不写回，避免把开发机路径固化到别人的机器上
    CONFIG_PATH.parent.mkdir(parents=True, exist_ok=True)
    CONFIG_PATH.write_text(
        yaml.safe_dump(out, allow_unicode=True, sort_keys=False), encoding="utf-8"
    )
