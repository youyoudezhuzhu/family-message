# 昵称快照 · 三端端到端验收报告（2026-09-29）

> 对应 Phase 5 收尾后的「下一阶段候选 ①」：网页端 → 快照写入 → **三端上色**一致，
> 含 §3.4.1 的硬规则（重创同名不改历史）。
> 复现命令：`python3 tools/test_snapshot_e2e.py`（只起测试实例 18899；生产 18801 硬拒绝）
> 原始输出：`docs/snapshot-e2e-raw.log`　截图：`docs/snapshot-e2e-0{1..4}-*.png`

## 1. 三端是什么（不是三份 mock）

| 端 | 形态 | 数据来源 |
|---|---|---|
| **Web A** | 真 Chromium + 真 `/ws/web` | 选昵称（本地机制）→ 在真界面里点发送 |
| **Web B** | 第二个真浏览器上下文，**全程不刷新** | 只靠服务端广播收到同一条消息 |
| **PC 壳页** | `web/shell/app.html` + 假桥（等价于 WPF 壳） | 喂**服务端真实发给设备的那一帧**（从一条真设备 WS 上抓的，不是手写 payload） |

## 2. 判据与结果（E2E 全绿）

| # | 判据 | 实测 |
|---|---|---|
| E1 | 落库快照 = (nickname_id, 该昵称的逻辑色 ID) | `{"sender_name":"妈妈","sender_nickname_id":1,"sender_color":"color_01"}` ✅ |
| E2 | Web B **不刷新**就收到 | DOM 出现「妈妈 \| E2E 三端消息」 ✅ |
| E3 | 三端解析出**同一个逻辑色 ID**（各自的官方钩子：Web `FMNickResolver.colorIdForMessage` / PC `FM_PC.nickColorIdForMessage`） | A=`color_01` B=`color_01` PC=`color_01` ✅ |
| E4 | 三端**渲染出来的头像底色一致**（网页端与 PC 共用 `static/chat.js`） | 三端均 `rgb(94, 53, 177)` ✅ |
| E5 | 服务端→设备的那一帧**自己带快照**（否则 PC 只能名字反查） | `sender_nickname_id=1 / sender_color=color_01` ✅ |
| E5b | 帧里 `history[]` 的每个条目也带快照字段（PC 弹窗右侧历史同样按快照上色） | 每条都含两个字段 ✅ |
| E6 | ★ **重创同名**：删「妈妈」→ 重创（新 id=2、换色成 `color_08`）→ 老消息在 Web B（不刷新）与 PC 端**仍是旧色 `color_01`**，且不是新行的色 | B 与 PC 都 = `color_01`（≠`color_08`）✅ |
| E7 | A / B / PC 三处控制台零报错 | 全部 `[]` ✅ |

## 3. 本次 E2E 抓到的**真问题**（已修，正是它该抓的东西）

跑第一遍时 E5/E5b **失败**，暴露出写端只补了一半：

1. **服务端 → 设备的 `message` 帧不带快照**（`_device_payload` 只挑了 8 个字段）→
   PC 端 `pc.js: nickColorIdForMessage()` 的优先级是「快照 id → 快照色 → **名字反查**」，
   没有快照就只能名字反查 —— **重创同名 / 删名之后老消息会挂到新行上**，正是 §3.4.1 明令禁止的。
2. 帧里的 **`history[]` 条目也不带快照**（`group_history` 只挑了 5 个字段）→
   PC 弹窗右侧的历史消息同样只能名字反查。

修法（已在 `b936350` 之后的提交里）：`server/main.py:_device_payload()` 与
`server/services/messages.py:group_history()` 各补两个字段（`sender_nickname_id` / `sender_color`）。
**修完 E5/E5b 转绿**，E6 的重创同名场景才真正成立（PC 端走的是快照，不是名字反查）。

> 结论：这条 E2E 的价值不在「确认能跑」，而在它**实际抓到了**单测抓不到的一环 ——
> 「库里有值」≠「三端拿到的都是这份值」。库层单测（`tools/test_nickname_snapshot.py`）当时全绿，
> 但设备帧少了字段。

## 4. 一处被测出的「断言不可证」并已修正

E6 第一版失败的原因是**测试自身的假设**：删掉「妈妈」后立刻重建，颜色分配是「池里第一个可用」
→ 新行**又拿到同一个 `color_01`**，于是「旧色 ≠ 新色」不成立，断言无法证伪。
现在测试先显式换一次色（`reassign-color` 保证新色 ≠ 旧色），E6 才有判别力。

## 5. 回归（同一天跑的）

| 套件 | 结果 |
|---|---|
| `tools/test_snapshot_e2e.py`（本报告） | E1–E7 **全 PASS**，EXIT=0 |
| `tools/test_nickname_snapshot.py` | A–G **全 PASS** |
| `tools/test_nicknames.py`（NAS 数据层 + API + 广播） | **119/119** |
| `dotnet test`（Core 单测） | **153 全绿** |
| `dotnet build pc-agent/FamilyAgent` | **0 warning / 0 error** |
| `tools/check_protocol_doc.py` | **退出码 0**（协议与服务端逐条对齐） |
| `tools/check_agent_refs.py` | 通过 |

## 6. 仍未覆盖的一格（诚实标注）

- **真 Windows 壳**（WPF + WebView2）跑不了（开发机是 Linux，见 `docs/CORE-REFACTOR-PLAN.md` §8.8）；
  本报告用「同一个 `web/shell/app.html` + 假桥 + **真实设备帧**」等价替代，
  与 `tools/test_pc_sender_nickname.py` 同一套做法。
- Web 端**昵称选择器的 UI 操作**由 `tools/test_nickname_web.py`（Phase 3 套件）覆盖；
  本 E2E 用文档化的本地机制（localStorage `fm.lastSender`）设置当前选用，避免重复覆盖同一段 UI。
