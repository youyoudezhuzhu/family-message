"""配置加载：config.yaml + 环境变量覆盖。"""
from __future__ import annotations

import copy
import os
from pathlib import Path

import yaml

BASE_DIR = Path(__file__).resolve().parent
CONFIG_PATH = Path(os.environ.get("FM_CONFIG", str(BASE_DIR / "config.yaml")))

_DEFAULTS: dict = {
    "server": {"host": "0.0.0.0", "port": 18801, "public_url": ""},
    "data_dir": str(BASE_DIR / "data"),
    "senders": ["振辉"],
    "web": {"password": "", "session_hours": 720},
    "device": {
        "enroll_token": "",
        "auto_register": True,
        "offline_after_seconds": 45,
    },
    "message": {"popup_auto_close_seconds": 0, "max_targets": 20},
    "xiaomi": {
        "enabled": False,
        "username": "",
        "password": "",
        "region": "cn",
        "refresh_ahead_seconds": 86400,
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
