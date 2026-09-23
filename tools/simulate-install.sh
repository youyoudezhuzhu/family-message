#!/bin/bash
# 在没有 App Center 的情况下，模拟一遍 fpk 的安装与启动流程。
# 目的：提前验证 install_init / install_callback / cmd/main 的正确性。
set -e

FPK="${1:-/vol1/1000/workspace/family-message/family-message_0.1.0.fpk}"
SIM=/vol1/@apphome/hermes-agent/data/fpk-sim

echo "▶ 清理旧模拟环境"
chmod -R u+rwX "$SIM" 2>/dev/null || true
rm -rf "$SIM"
mkdir -p "$SIM/work" "$SIM/target" "$SIM/var" "$SIM/home" "$SIM/etc"

echo "▶ 解包 fpk"
tar -xzf "$FPK" -C "$SIM/work"
tar -xzf "$SIM/work/app.tgz" -C "$SIM/target"

export TRIM_APPDEST="$SIM/target"
export TRIM_PKGVAR="$SIM/var"
export TRIM_PKGHOME="$SIM/home"
export TRIM_PKGETC="$SIM/etc"
export TRIM_TEMP_LOGFILE="$SIM/install-error.log"
export PATH="/var/apps/python312/target/bin:$PATH"
export wizard_web_password=""
export wizard_enroll_token="family-2026"

CMD="$SIM/work/cmd"

echo "▶ install_init"
/bin/bash "$CMD/install_init" && echo "  exit=$?"

echo "▶ install_callback（会建 venv + pip 装依赖，稍慢）"
if ! /bin/bash "$CMD/install_callback" > "$SIM/install_callback.out" 2>&1; then
  echo "  ❌ install_callback 失败，最后 20 行："
  tail -20 "$SIM/install_callback.out"
  [ -f "$TRIM_TEMP_LOGFILE" ] && { echo "  --- TRIM_TEMP_LOGFILE ---"; cat "$TRIM_TEMP_LOGFILE"; }
  exit 1
fi
echo "  ✅ install_callback 通过"

echo "▶ 生成的 venv"
ls "$TRIM_PKGHOME/venv/bin/python" && "$TRIM_PKGHOME/venv/bin/python" -c "import fastapi,uvicorn,yaml,httpx,websockets;print('  依赖 OK')"

echo "▶ 生成的 config.yaml"
cat "$TRIM_PKGHOME/config.yaml"

echo "▶ cmd/main status（未启动时应 exit 3）"
set +e
/bin/bash "$CMD/main" status; echo "  exit=$? (期望 3)"
set -e

echo "▶ cmd/main start"
/bin/bash "$CMD/main" start
echo "  exit=$?"

echo "▶ cmd/main status（应 exit 0）"
set +e
/bin/bash "$CMD/main" status; echo "  exit=$? (期望 0)"
set -e

sleep 3
echo "▶ 健康检查（端口 ${TRIM_SERVICE_PORT:-18801}）"
curl -s -m 8 "http://127.0.0.1:${TRIM_SERVICE_PORT:-18801}/healthz"; echo
echo "▶ 首页"
curl -s -m 8 -o /dev/null -w "  index=%{http_code}\n" "http://127.0.0.1:${TRIM_SERVICE_PORT:-18801}/"
echo "▶ unix socket"
curl -s -m 8 --unix-socket "$TRIM_APPDEST/family-message.sock" -o /dev/null -w "  sock=%{http_code}\n" http://localhost/healthz || true

echo "▶ 服务日志尾"
tail -15 "$TRIM_PKGVAR/family-message.log" 2>/dev/null || echo "  (无日志)"
