#!/bin/bash
# 组装源码 → 暂存目录 → fnpack build → 产出 .fpk
#
# 注意：fnpack 打包时会把「被它处理的目录」权限封成 0000（fnOS 常见坑），
# 所以绝不在仓库目录里直接 build，一律在暂存目录操作。
#
# 用法：./build-fpk.sh
set -e

ROOT="$(cd "$(dirname "$0")" && pwd)"
FPK_SRC="$ROOT/fpk"
VENV_PY="${FM_VENV_PY:-/vol1/@apphome/hermes-agent/data/family-venv/bin/python}"
STAGE_ROOT="${FM_BUILD_ROOT:-/vol1/@apphome/hermes-agent/data/fpk-build}"
STAGE="$STAGE_ROOT/family-message"

export TMPDIR="${FM_TMPDIR:-$STAGE_ROOT/tmp}"
mkdir -p "$TMPDIR"

echo "▶ 准备暂存目录: $STAGE"
# fnpack 会把暂存目录权限封成 0000，先修权限才删得掉（fnOS ACL 坑）
chmod -R u+rwX "$STAGE_ROOT" 2>/dev/null || true
rm -rf "$STAGE"
mkdir -p "$STAGE"
if [ -n "$(ls -A "$STAGE" 2>/dev/null)" ]; then
  echo "❌ 暂存目录未清空，残留旧文件会打进包：$STAGE"
  exit 1
fi
cp -r "$FPK_SRC/." "$STAGE/"

echo "▶ 复制服务端源码 → app/server"
rm -rf "$STAGE/app/server"
mkdir -p "$STAGE/app/server"
cp -r "$ROOT/server/." "$STAGE/app/server/"

echo "▶ 复制前端 → app/www"
rm -rf "$STAGE/app/www"
mkdir -p "$STAGE/app/www"
cp -r "$ROOT/web/." "$STAGE/app/www/"

echo "▶ 生成图标"
"$VENV_PY" "$ROOT/tools/make_icons.py" "$STAGE" >/dev/null

echo "▶ 清理缓存 / 开发残留 / 赋可执行位"
# 先修权限：fnOS 上 cp -r 会连 ACL 一起复制，源目录若是 0000 复制出来也删不掉
chmod -R u+rwX "$STAGE" 2>/dev/null || true
find "$STAGE" -name '__pycache__' -type d -print0 2>/dev/null | xargs -0 -r rm -rf
find "$STAGE" -name '*.pyc' -delete 2>/dev/null || true
rm -rf "$STAGE/app/server/__pycache__" "$STAGE/app/server/services/__pycache__"
# 开发用的 config.yaml 不进包：真实配置由 install_callback 生成到 $TRIM_PKGHOME
rm -f "$STAGE/app/server/config.yaml"
rm -rf "$STAGE/family-message.fpk"
chmod -R u+rwX "$STAGE"
chmod +x "$STAGE"/cmd/* 2>/dev/null || true
echo "  残留 pycache: $(find "$STAGE" -name '__pycache__' 2>/dev/null | wc -l) 个"

VERSION="$(grep -E '^version' "$STAGE/manifest" | awk -F= '{gsub(/[ \t]/,"",$2); print $2}')"
echo "▶ fnpack build (version $VERSION)"
( cd "$STAGE" && fnpack build )

if [ -f "$STAGE/family-message.fpk" ]; then
  OUT="$ROOT/family-message_${VERSION}.fpk"
  cp "$STAGE/family-message.fpk" "$OUT"
  chmod u+rw "$OUT"
  echo "✅ 打包完成: $OUT"
  ls -l "$OUT"
  echo "▶ 内容清单"
  tar -tzf "$OUT" | head -20
else
  echo "❌ 未生成 fpk，请检查 fnpack 输出"
  exit 1
fi
