# 发布清单（family-message）

> 一页纸。每次发版照着走，**别凭记忆**。上次实测：v0.17.1（2026-09-29 之前）。

## 0. 前置（不满足就别往下走）

1. `git status --porcelain` 里**没有 ` M` 行**（未提交改动一律不进包：`build-fpk.sh` 是从**工作区**拷 `server/` + `web/` 的）。
   - ⚠ 已知坑：并行 agent 未提交的 `web/static/*`、`server/*` 会被打进 fpk，但**不会**进 tag 指向的 Windows 包 → 两边内容不一致。
2. 远端已同步：`git fetch origin && git rev-list --left-right --count origin/main...HEAD` → `0 0`。

## 1. 全绿门禁（一条条跑，任何一条红就停）

> 这些门禁**已经进 CI**（`.github/workflows/checks.yml`：`core-and-docs` + `ui-e2e` 两个 job，
> 在每次 push / tag / PR 上跑）。下面手工再跑一遍是**发布前的最终确认** —— CI 跑的是同一个 commit，
> 但它不会告诉你「本地工作区里有没有没提交的东西」。

```bash
export PATH=/vol1/<uid>/workspace/dotnet9:$PATH
python3 tools/check_protocol_doc.py            # 协议与服务端逐条对齐（退出码 0）
python3 tools/check_agent_refs.py pc-agent/FamilyAgent
dotnet test pc-agent/FamilyAgent.Core.Tests/FamilyAgent.Core.Tests.csproj
dotnet build pc-agent/FamilyAgent/FamilyAgent.csproj          # 0 warning / 0 error
python3 tools/test_nickname_snapshot.py        # 快照写端 A–G（起 18899）
python3 tools/test_snapshot_e2e.py             # 三端 E2E E1–E7（起 18899）
V=/vol1/@apphome/hermes-agent/data/venv/bin/python
$V tools/test_nicknames.py                     # NAS 数据层 + API + 广播（起 18899，硬拒 18801）
$V tools/test_nickname_web.py                  # 网页端 Phase 3（起 18899/18897）
$V tools/test_pc_sender_nickname.py            # PC 端昵称交互三形态
```
> ⚠ `test_nickname_web.py` 会把 `docs/nickname-*.png` 截图重写；跑完 `git checkout -- docs/nickname-*.png` 还原，别把测试产物混进发布提交。
> ⚠ 生产 18801 全程只读探活；上面所有脚本都硬拒 18801 作为实例端口。

## 2. 版本号（**7 处**，一处不改就会出现「网页还在拉 0.17.1 的缓存」这类问题）

| 文件 | 内容 |
|---|---|
| `fpk/manifest` | `version = x.y.z` |
| `pc-agent/FamilyAgent/FamilyAgent.csproj` | `<Version>x.y.z</Version>` |
| `pc-agent/FamilyAgent.Core/Protocol/ProtocolVersion.cs` | `AgentVersion = "cs-x.y.z"` |
| `web/index.html` | 7 处 `?v=x.y.z`（tokens.css / style.css / shell.css / nickcolor.js / chat.js / shell.js / app.js） |
| `web/shell/app.html` | 4 处 `?v=x.y.z`（tokens.css / pc.css / chat.js / pc.js） |

```bash
grep -rn '0\.17\.1' . 2>/dev/null | grep -vE '/(bin|obj)/|\.fpk|/\.git/|release/'
# ↑ 只应剩下「测试假数据 / 文档叙述历史版本」的命中，其余都要改
```

## 3. 提交与打标签

```bash
git add -A && git commit -m 'chore(release): vX.Y.Z'      # 版本号 + 描述（fpk/manifest 的 desc 也在这提交里核对）
git tag -a vX.Y.Z -m 'vX.Y.Z：<一句话>'
git push origin main && git push origin vX.Y.Z            # ★ 推 tag 就是「发布」：Actions 会建 Release
```

`.github/workflows/build-windows-agent.yml`：`on: push: tags: ['v*']`（另有 `workflow_dispatch` 手动触发）→
windows-latest 编译 → 上传 artifact → `softprops/action-gh-release` 附到 Release。

## 4. 构建 fpk 并附到同一个 Release

⚠ 两个坑（v0.18.0 实测各踩一次）：

1. **别用默认暂存根**：`/vol1/@apphome/hermes-agent/data/fpk-build` 是全局共享的，**并发的第二次构建**
   会把它封成 0000（fnpack 的副作用）→ 另一头 `mkdir: Permission denied` 直接失败。
   每次用独立的 `FM_BUILD_ROOT`（如 `/vol1/<uid>/workspace/.fm-build-$VERSION`），事后
   `chmod -R u+rwX <它>` 再 `rm -rf`。
2. **从 tag 的干净工作树构建**（不要从正在干活的工作区构建）：`build-fpk.sh` 是拷**工作区**的
   `server/` + `web/` 的，工作区里有人并发改文件时，包里出的就不是 tag 的内容了。

```bash
V=vX.Y.Z
git worktree add --detach /vol1/<uid>/workspace/.fm-rel-$V $V          # tag 的干净检出
cd /vol1/<uid>/workspace/.fm-rel-$V
FM_VENV_PY=/vol1/@apphome/hermes-agent/data/venv/bin/python \
FM_BUILD_ROOT=/vol1/<uid>/workspace/.fm-build-$V \
  ./build-fpk.sh                                                      # 产出 ./family-message_X.Y.Z.fpk
tar -xOzf family-message_X.Y.Z.fpk manifest | head -3                 # 抽检：version + desc 对不对
cd - && cp /vol1/<uid>/workspace/.fm-rel-$V/family-message_X.Y.Z.fpk .
chmod -R u+rwX /vol1/<uid>/workspace/.fm-build-$V 2>/dev/null; rm -rf /vol1/<uid>/workspace/.fm-build-$V
rm -f /vol1/<uid>/workspace/.fm-rel-$V/family-message_X.Y.Z.fpk
git worktree remove --force /vol1/<uid>/workspace/.fm-rel-$V
```

上传（**必须 `--data-binary`，不要 `-F`**；release id 从 API 取）：

```bash
TOKEN=$(git config --get remote.origin.url | sed -E 's#https://[^:]+:([^@]+)@.*#\1#')
RID=$(curl -s -H "Authorization: Bearer $TOKEN" \
      https://api.github.com/repos/youyoudezhuzhu/family-message/releases/tags/vX.Y.Z | python3 -c 'import json,sys;print(json.load(sys.stdin)["id"])')
curl -s -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/octet-stream" \
     --data-binary @family-message_X.Y.Z.fpk \
     "https://uploads.github.com/repos/youyoudezhuzhu/family-message/releases/$RID/assets?name=family-message_X.Y.Z.fpk"
```
> 本机**没有 gh CLI**，所以走 REST API；token 就在 `origin` 的 URL 里（`x-access-token:...`），别外发。

## 5. 验收（发布完成判据）

```bash
curl -s https://api.github.com/repos/youyoudezhuzhu/family-message/releases/latest \
  | python3 -c 'import json,sys;d=json.load(sys.stdin);print(d["tag_name"]);[print(" -",a["name"],a["size"],a["state"]) for a in d["assets"]]'
```
- 应看到 **两个资产**：`FamilyAgent-win-x64.zip`（Actions 产出）+ `family-message_X.Y.Z.fpk`，且都 `uploaded`。
- tag 指向的 commit = `git rev-parse vX.Y.Z^{commit}`，与本地一致。

## 6. 交付给用户

把两个产物复制到工作区（他习惯从这里取）：

```bash
cp family-message_X.Y.Z.fpk /vol1/<uid>/workspace/
cp <下载的> FamilyAgent-win-x64.zip /vol1/<uid>/workspace/
```
Windows 包从 Release 资产下载（Actions 的 artifact 会过期，Release 资产不会）。

## 7. 回退

- 发错了：`git push --delete origin vX.Y.Z` + 删 Release（或标记为 pre-release）；fpk 资产一并删。
- **别**用 `--force` 重推同一个 tag 覆盖（已下载的人对不上内容）；改版本号重发。
