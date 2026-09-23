#!/bin/bash
# 模拟 fpk 的【升级】流程，验证「升级后真的在跑新代码」。
#
# 背景：upgrade_init 原本是空的（exit 0），升级时旧进程没被杀掉，继续用旧代码
# 占着端口服务，新进程绑不上端口就退出 —— 表现为「升级了但功能没变化」。
# 这个脚本把两种场景都跑一遍，防止以后改回去。
#
# 用法：tools/simulate-upgrade.sh [fpk路径]

FPK="${1:-/vol1/1000/workspace/family-message/family-message_0.4.1.fpk}"
SIM=/vol1/@apphome/hermes-agent/data/fpk-sim
PORT=18899
PASS=0
FAIL=0

ok()   { echo "  ✅ $1"; PASS=$((PASS + 1)); }
bad()  { echo "  ❌ $1"; FAIL=$((FAIL + 1)); }
hdr()  { echo; echo "▶ $1"; }

version_now() {
    curl -s -m 6 "http://127.0.0.1:$PORT/healthz" 2>/dev/null \
        | grep -oP '"version":\s*"\K[^"]+' | head -1
}

listeners() {
    ss -lntp 2>/dev/null | grep -E "[:.]${PORT}[[:space:]]" \
        | grep -oE 'pid=[0-9]+' | cut -d= -f2 | sort -u | tr '\n' ' '
}

echo "════════ 升级流程回归测试 ════════"
chmod -R u+rwX "$SIM" 2>/dev/null || true
rm -rf "$SIM"
mkdir -p "$SIM/work" "$SIM/target" "$SIM/var" "$SIM/home" "$SIM/etc"

hdr "解包 + 首次安装"
tar -xzf "$FPK" -C "$SIM/work"
tar -xzf "$SIM/work/app.tgz" -C "$SIM/target"

export TRIM_APPDEST="$SIM/target"
export TRIM_PKGVAR="$SIM/var"
export TRIM_PKGHOME="$SIM/home"
export TRIM_PKGETC="$SIM/etc"
export TRIM_TEMP_LOGFILE="$SIM/install-error.log"
export TRIM_SERVICE_PORT="$PORT"
export PATH="/var/apps/python312/target/bin:$PATH"
export wizard_web_password=""
export wizard_enroll_token="family-2026"

CMD="$SIM/work/cmd"
BASE_VER=$(grep -m1 '^version' "$SIM/work/manifest" | cut -d= -f2 | tr -d ' "')
echo "  基准版本: $BASE_VER"

if ! /bin/bash "$CMD/install_callback" > "$SIM/install_callback.out" 2>&1; then
    echo "  ❌ install_callback 失败"; tail -15 "$SIM/install_callback.out"; exit 1
fi
ok "install_callback 通过"

/bin/bash "$CMD/main" start >/dev/null 2>&1
sleep 2
[ "$(version_now)" = "$BASE_VER" ] \
    && ok "首装后 /healthz 报版本 = $BASE_VER" \
    || bad "首装后版本不对：期望 $BASE_VER，实际 $(version_now)"

# ── 场景 A：正常升级 ───────────────────────────────────────────────
hdr "场景 A：正常升级（upgrade_init 应先停服务，upgrade_callback 再拉起）"
NEW_VER="0.4.2-simtest"
sed -i "s/^version *=.*/version               = $NEW_VER/" "$SIM/work/manifest"

/bin/bash "$CMD/upgrade_init" >/dev/null 2>&1
sleep 1
[ -z "$(version_now)" ] \
    && ok "upgrade_init 后旧的已停（端口无响应）" \
    || bad "upgrade_init 后旧进程还在（版本 $(version_now)）"

/bin/bash "$CMD/upgrade_callback" >/dev/null 2>&1
sleep 2
[ "$(version_now)" = "$NEW_VER" ] \
    && ok "upgrade_callback 后跑的是新版本 $NEW_VER" \
    || bad "升级后版本不对：期望 $NEW_VER，实际 $(version_now)"

# ── 场景 B：孤儿进程（本次真实故障的复现）─────────────────────────
hdr "场景 B：孤儿进程（模拟卸载/升级残留，PID 文件丢失但进程占着端口）"
ORPHAN_VER="$NEW_VER"
ORPHAN_PID="$(listeners)"
rm -f "$SIM/var/family-message.pid"          # 模拟 PID 文件丢失
echo "  当前占用 $PORT 的孤儿 PID: ${ORPHAN_PID:-无}"

NEW_VER2="0.4.3-simtest"
sed -i "s/^version *=.*/version               = $NEW_VER2/" "$SIM/work/manifest"

/bin/bash "$CMD/main" start >/dev/null 2>&1
sleep 2
GOT="$(version_now)"
if [ "$GOT" = "$NEW_VER2" ]; then
    ok "start 自动清理了旧进程（$ORPHAN_VER），跑起新版本 $NEW_VER2"
else
    bad "start 没能替换旧进程：期望 $NEW_VER2，实际 $GOT"
fi
for p in $ORPHAN_PID; do
    if kill -0 "$p" 2>/dev/null; then
        bad "孤儿进程 $p 仍然活着"
    else
        ok "孤儿进程 $p 已被清理"
    fi
done

# ── 场景 C：重复 start 幂等 ────────────────────────────────────────
hdr "场景 C：重复 start 应幂等（不重启、不报错）"
P1="$(listeners)"
/bin/bash "$CMD/main" start >/dev/null 2>&1
sleep 1
P2="$(listeners)"
[ -n "$P2" ] && [ "$(version_now)" = "$NEW_VER2" ] \
    && ok "重复 start 后仍是 $NEW_VER2（PID ${P1:-?} → ${P2:-?}）" \
    || bad "重复 start 出问题：版本 $(version_now)"

hdr "收尾"
/bin/bash "$CMD/main" stop >/dev/null 2>&1
sleep 1
[ -z "$(version_now)" ] && ok "stop 之后端口已释放" || bad "stop 之后端口仍被占用"

echo
echo "════════ 结果：$PASS 项通过，$FAIL 项失败 ════════"
echo "（服务日志尾）"
tail -12 "$SIM/var/family-message.log" 2>/dev/null | sed 's/^/  /'
exit $([ "$FAIL" -eq 0 ] && echo 0 || echo 1)
