"""fnOS / 内网启动入口。

同时监听两个地址，共用一个 ASGI app：
  - TCP  0.0.0.0:18801                       → 局域网：网页控制台 + PC Agent 长连接（主入口）
  - unix $TRIM_APPDEST/family-message.sock   → 飞牛统一网关（可选，桌面入口/远程访问）

说明：uvicorn 这一版的 Config 能收列表但 Server 不认，所以这里自己建 socket
再交给 Server.run(sockets=[...])，最后走的是 uvicorn 官方的多监听路径（单 lifespan）。
"""
from __future__ import annotations

import os
import socket
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))

import uvicorn  # noqa: E402

from config import CONFIG  # noqa: E402
from main import app  # noqa: E402

BACKLOG = 2048


def _tcp_listener(host: str, port: int) -> socket.socket:
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    sock.bind((host, port))
    sock.listen(BACKLOG)
    sock.set_inheritable(True)
    return sock


def _unix_listener(path: str) -> socket.socket:
    p = Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    if p.exists():
        p.unlink()
    sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    sock.bind(path)
    os.chmod(path, 0o666)
    sock.listen(BACKLOG)
    sock.set_inheritable(True)
    return sock


def main() -> None:
    host = CONFIG["server"]["host"]
    port = int(os.environ.get("TRIM_SERVICE_PORT") or CONFIG["server"]["port"])
    uds = (os.environ.get("GATEWAY_SOCKET") or "").strip()

    listeners = [_tcp_listener(host, port)]
    targets = [f"tcp://{host}:{port}"]

    if uds:
        try:
            listeners.append(_unix_listener(uds))
            targets.append(f"unix://{uds}")
        except OSError as e:
            print(f"[family-message] unix socket 绑定失败（不影响内网访问）: {e}", flush=True)

    print(
        f"[family-message] 监听 {', '.join(targets)} "
        f"data_dir={CONFIG['data_dir']} prefix={os.environ.get('GATEWAY_PREFIX', '') or '(无)'}",
        flush=True,
    )
    for t in targets:
        print(f"[family-message]   → {t}", flush=True)

    cfg = uvicorn.Config(app, log_level="info", timeout_keep_alive=30)
    uvicorn.Server(cfg).run(sockets=listeners)


if __name__ == "__main__":
    main()
