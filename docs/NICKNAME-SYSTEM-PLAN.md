# 全局共享昵称系统 · 勘察与分阶段实施方案

> 状态：**已定稿（r6），未改动任何代码**。本轮只改两个文档：本文件（`docs/NICKNAME-SYSTEM-PLAN.md`，勘察 + 方案）
> 与 `docs/NICKNAME-SYSTEM-SUMMARY.md`（≤150 行的用户摘要，同一批内容的精简版）。
> **r6 定稿口径：昵称长度统一 32 / PC 写操作走 `/ws/device` 帧 / 完全拒绝离线改昵称（无 pending 机制）/
> `color` 存逻辑色 ID（显示色由客户端按主题算）/ 活跃上限 16（含额度提示文案）/ 历史冻结是硬规则（含重创同名）。**
> 需求来源：规格《Family Message：全局昵称与昵称颜色系统》（24 节，下称「规格 §N」）
> + **《全局共享昵称系统——最终需求变更说明》（下称「最终说明 §N」，最高依据，覆盖此前所有设计）**。
> 勘察对象：本仓库当前提交 **`616551e`（v0.16.0）**。
> **本文件里所有「现状」结论都带文件 + 行号依据**；凡是代码里读不出来的，一律写「待确认」。
> 本文件所有行号**已在 v0.16.0 上逐条复核通过**（`app.js` / `chat.js` / `shell.js` /
> `style.css` / `pc.css` / `db.py` / `main.py` / `hub.py` / `AgentConfig.cs`）。

---

## ★ 修订 r6（本次 · 定稿）：四条最终拍板 + 两处设计升级

> **本轮只做文档定稿，不改任何代码。** 用户拍板原文（最高依据）→ 落地位置逐条对应：

| 拍板 | 用户原话（要点） | 落地 |
|---|---|---|
| ① 昵称长度 | 「NAS / API / PC / Web 统一最大 32 个字符；前端也统一校验」 | DDL 保持 `1..32`（§3.1）；**四处统一到 32**：服务层 32 不变、Web 输入框 `maxLength` 16→32、PC 写入前 32 校验（§2.C2 / §5.5 / §7 Phase 3-4） |
| ② PC 写操作通道 | 走 `WebSocket /ws/device` 帧，**不新增 HTTP 写接口** | 与 r5 同方案，**确认**（§5.4）。⚠ ③ 生效后，「离线队列 / 重连补发」**不再适用于昵称写操作**（§5.4 的限制注） |
| ③ ★ 离线改名 | 「**完全拒绝离线改昵称**」 | **删掉整套「离线 pending 昵称同步」**：新建 / 改名 / 删除 / 重分配颜色 **四个管理操作全部必须在线**（离线一律拒绝并明示原因）；**只有「选择昵称」可离线**（§0.13 / §5.2 / §7 Phase 4；作废清单 B16） |
| ④ ★★ 颜色 | 「把『逻辑颜色』和『显示颜色』分开：`nickname.color = color_01`，客户端按 `theme + color_01` 得到最终 CSS/ARGB；NAS 不必存两套浅/深色，也不破坏『昵称颜色全局统一』」 | **`nicknames.color` 改存逻辑色 ID**（`color_01`…`color_16`），DDL 的 `CHECK` 枚举 16 个 ID（§3.1）；**所有 API / 广播 / 帧 / 消息快照的 `color` 一律传逻辑 ID**（§3.2 / §5.3 / §5.4）；16 色池变成「逻辑色 → 基础色值」常量表（§4.3）；主题变体由**客户端渲染时算**、不进协议不进库；新增**客户端映射表 §4.4**（PC / Web 两端共用，含灰） |
| ⑤ 活跃上限 16 | 已确认；接近要提示：「共享昵称颜色即将用尽（14/16）」；满：「已达到共享昵称上限，请删除不再使用的昵称后再添加。」 | §3.1 / §4.3 / §5.5 / §7 Phase 3；§10 已定 12 更新 |
| ⑥ 历史冻结 = 硬规则 | 「昵称和颜色修改只影响未来消息，不回溯修改历史消息」；**重新创建同名昵称也不得污染历史** | 硬规则由七条升为**八条**（新增第 8 条）；新增 **§3.4.1「重新创建同名昵称」**：`nickname_id` **绝不能按 `display_name` 去查**；**存量回填定稿改为「不建议回填」**（§3.4 + §3.4.1） |

**新增 / 重写的章节**：§0.3 / §0.4（改写）+ §0.13（新）、§3.1（DDL 改逻辑色）、§3.2（快照存逻辑色 ID）、
§3.2.1（灰 = 逻辑 ID `gray`）、**§3.4（回填改口径）+ §3.4.1（新 · 重创同名）**、
§4.2 / §4.3（逻辑色池 + 上限文案）+ **§4.4（新 · 客户端逻辑色→显示色映射表）**、
§5.1 / §5.2（离线拒绝）/ §5.3 / §5.4（帧 + 通道限制注）/ §5.5（校验）、
§7 Phase 1 / 3 / 4 / 5、§8.2（去掉 pending 状态机）、§9、§10（待拍板清空）、附录 A、附录 B（B10 / B11 / B15 改 + **B16 / B17 / B18 新增**）。

**r6 的作废项（照附录 B 反向检查，别复活）**：离线 pending 昵称同步（`nickname_sync_state = pending`、
离线改名队列、`hello` / 心跳里的 pending 补发）、`nicknames.color` 或任何协议字段存 HEX、
用 `display_name` 反查 / 回填 `nickname_id`。

---

## 修订 r5（上一轮，以《最终需求变更说明》为准）：全局共享昵称 · 三层状态模型

> **一句话原则（最终说明 §18 —— 先读这句）**
> **昵称属于整个系统，而不属于任何客户端；客户端只有「当前使用昵称」这一状态。
> NAS 维护唯一的共享昵称及其颜色；新安装客户端的 `ComputerName` 仅作为灰色本地临时昵称，
> 不进入共享昵称池。**

**最终数据模型（逐字，最高依据）**：

```
Nickname
├── nickname_id      # 系统内部唯一身份
├── display_name     # 显示昵称
├── color            # 昵称颜色
├── status           # active / inactive
├── created_at
└── updated_at
```

**三层状态（本方案的全部结构 —— 后面所有章节都在讲这三层，别混）**

| 层 | 是什么 | 存在哪 | 谁能改 |
|---|---|---|---|
| ① **共享昵称** | 全系统唯一的「妈妈 → 红色」 | NAS 的 `nicknames` 表（唯一权威） | **任何客户端**（全是全局操作，最终说明 §7） |
| ② **当前使用昵称** | 这个客户端「现在用谁的名义发消息」 | **客户端本地**，只存一个 `current_nickname_id`；也可以是「无」= 灰临时 | 只有这个客户端自己 |
| ③ **消息快照** | 这条消息**当时**的 `nickname_id` / `display_name` / `color` | `messages` 行内（入库存死） | 谁都不能改（快照就是快照） |

**八条硬规则（第 1–7 条由最终说明逐条拍板；第 8 条是 r6 升级上来的历史冻结）**

1. **禁止归属字段**（§2）：`nicknames` 表**不许**出现 `owner_type` / `owner_id` / `device_id` / `client_id`。
   一句话：**「这台 PC 的昵称」这个概念根本不存在** —— 只有「系统里的『妈妈』」和「这台 PC 现在正用着『妈妈』」。
2. **同名 = 被拒绝，不是被合并**（§3）：`display_name` 在 `active` 上唯一；创建时若已存在 →
   **明确返回 `NICKNAME_ALREADY_EXISTS`**（不是静默复用），客户端据此**引导用户直接选用那个既有昵称**。
   → 所以**不存在「两个同名昵称颜色不同」**，**「同名共色」这类特殊机制一并删除**（一个活跃名字本来就只有一行）。
3. **选择 ≠ 修改**（§8，必须严格区分；详见 §5.2）：
   **选择昵称** = 客户端本地状态，只存 `current_nickname_id`，**不改 NAS**；
   **修改昵称** = 改**全局共享昵称本身**（`妈妈 → 老妈`），**所有客户端同步看到**。
   **「修改我这台 PC 的昵称」这个概念不存在。**
4. **所有客户端都可管理**（§7）：查看 / 选择 / 添加 / 修改 / 删除 / 重新分配颜色 —— **全是全局操作**，
   不区分「谁创建的」。文档里**不再有**「仅 Web 管理端可操作」「PC 只能改自己」这类限制。
5. **灰色 = 客户端本地临时昵称**（§9）：新安装客户端第一次启动显示
   「当前昵称：`LIVINGROOM-PC` / 颜色：灰色」，**不进共享池**：不创建 `nickname_id`、不占用颜色池、
   不参与全局同步、不需要 NAS 分配颜色、不影响共享昵称数量。**协议细节见 §3.2.1。**
6. **消息自带快照**（§11）：`nickname_id` + `display_name_snapshot` + `color_snapshot` + `content`。
   删了昵称，历史消息照样显示当时的样子。昵称被删后，正在用它的客户端必须处理
   **「当前昵称已失效」**状态（方案见 §3.5 / §10 已定 10）。
7. **广播管实时、整表同步管一致性**（§13 / §14）：事件名 `nickname_created` / `nickname_updated` /
   `nickname_removed` / `nickname_color_changed` / `nickname_list_sync`，载荷统一
   `{"type": "...", "nickname": {...完整对象...}}`；启动 / 重连用 `nickname_list_request` →
   `nickname_list_response` 校正本地缓存。**不要依赖客户端互相之间同步。**
8. ★ **历史冻结是硬规则（r6 升级，用户原话「昵称和颜色修改只影响未来消息，不回溯修改历史消息」）**：
   ⇒ ① `messages` 的三个快照列必须齐 —— `sender_nickname_id` + `display_name_snapshot`（= 既有的
   `sender_name`）+ `color_snapshot`（= `sender_color`）—— 且**读取时绝不 JOIN `nicknames`**；
   ② **即使以后重新创建同名昵称，历史也不得被污染**：旧消息的 `nickname_id` 指向**旧行**（已 `inactive`），
   新昵称是**新的 `nickname_id`** ⇒ ★ **`nickname_id` 绝不能按 `display_name` 去查**
   （否则会把老消息误挂到新行；完整推演见 **§3.4.1**）。

**颜色池**：沿用最终说明 §5 给的 **16 个 Material 色**，并**已按现有 UI 的明暗背景逐色做可读性验证**
（真实数据见 §4）。结论一句话：原色直接用会出现「头像首字 5/16 不达标、浅底圆点 4/16 不达标、
深底圆点 6/16 不达标」，**已给出定稿规则**（自适应字色 + 圆点按主题取变体）→ 逐项 16/16 达标。
**r6 起这 16 个 HEX 只是「基础色值」**：落库 / 协议 / 快照里走的是**逻辑色 ID**（`color_01`…`color_16`），
主题变体由客户端渲染时算（§4.3 / §4.4）。

**修订史（全部作废，只留结论，不再展开）**：r1 勘察 → r2 `owner` 模型 → r3 `owner` + `nickname_colors`
颜色归属表 → r4「昵称中心模型」（砍掉 owner，但仍留着「按名幂等创建 / Web 懒建默认用户 / 同名共色」）
→ **r5（以最终说明为准）**：砍掉「幂等创建」与「同名共色」，改成
**「显式 `NICKNAME_ALREADY_EXISTS` + 灰色本地临时昵称 + 消息快照 + 只有 `nickname_id` 是身份」**
→ **r6（本次 · 定稿）**：**完全拒绝离线改昵称**（删掉整套 pending 同步）+ **逻辑色 / 显示色分离**
（`color` 存逻辑 ID、客户端按主题算显示色）+ 昵称长度统一 32 + 活跃上限 16（含额度提示文案）+
**历史冻结升为硬规则**（含「重创同名」场景，存量回填**定稿为不建议**）。

**本文件的文字约定**：`owner_type` / `owner_id` / `nickname_colors` / `affected_nickname_ids` /
`NOT_YOURS` / 「Web 单例」/「同名共色」/「按名幂等创建」，以及 r6 新增的
**`nickname_sync_state` / 「离线 pending 昵称同步」/ 「离线改名队列」/ 协议或落库里的 HEX 色值**
（`#RRGGBB` 只允许作为「基础色值」出现在 §4 的实测表与 §4.3 / §4.4 的常量表里）这些字样，
**只允许**出现在带「已作废」标注的历史句与**附录 B 的反例清单**里；
**可施工正文（DDL / 创建流程 / API / 帧 / 验收判据）里一律不得出现。**
**附录 B 是「照着反向检查、别复活」的清单。**

**受影响章节（本次已按最终说明重写）**：文件头、§0、§2.C1 / §2.C2 / §2.C3 / §2.C4 / §2.C7、§3（含新增
§3.2.1）、§4、§5、§6、§7、§9、§10、附录 B。**未受影响（保留 r1 的勘察结论与实测数据）**：
§1.1–§1.8、§2.C5、§2.C6、§8。 §2.C5 / §4.1 的对比度数据本次**已用脚本重算并与原文互相验证**（数值一致）。
**r6 另改**：§0.3 / §0.4（改写）+ §0.13（新）、§2.C1 / §2.C2 / §2.C6（R2 / R5）、§3.1 / §3.2 / §3.2.1、
§3.4（改写）+ §3.4.1（新）、§3.5、§4.2 / §4.3（改写）+ §4.4（新）、§5.1–§5.6、§6.3 / §6.4、
§7 Phase 1 / 3 / 4 / 5、§8.2、§9、§10、附录 A / B。

---
## 0. 硬约束（施工时不许破）

1. **本轮只写这两个文档**（`docs/NICKNAME-SYSTEM-PLAN.md` + `docs/NICKNAME-SYSTEM-SUMMARY.md`），
   **不改任何代码**。施工开工时先确认工作区干净
   （勘察期间那批未提交改动已合并为 v0.16.0：`pc-agent/` 平台能力抽象 / 离线重连同步、
   `web/static/style.css`、`web/shell/pc.css` 的气泡宽度修复）。
2. **示例名一律中性**：文档、默认值、占位符、测试数据、日志样例都不用真人名。
   PC 默认昵称示例统一写 `MY-PC` / `我的电脑` / `客厅电脑`；Web 默认昵称写 `默认用户`；
   昵称示例写 `妈妈` / `爸爸`（关系称呼，不是真人姓名）。规格正文里出现的具体人名/机器名
   （如 `ZHENHUI-PC`）**只当规格原文引用**，绝不作为默认值、占位符或落库值，也不许编译进产物。
3. **NAS 是逻辑色与昵称身份的唯一权威**（规格 §1 / §9；**r6 改写**）：`nicknames.color` 只存
   **逻辑色 ID**（`color_01`…`color_16`），由 NAS 分配；客户端**不许自造 ID、不许自分配颜色**。
   **显示色**（基础色值 + 主题变体）由客户端**按 §4.4 的映射表算出来** —— 这是**渲染职责**，
   不是「客户端自算颜色」：「逻辑色 / 显示色分离」正是 r6 拍板的第 ④ 条。
4. **逻辑色池（哪些 ID 存在、哪个被占用）只在 NAS 定义**，不复制到任何客户端（规格 §9）；
   但**「逻辑色 ID → 基础色值」常量表 + 主题变体规则必须在 PC / Web 两端各实现一份**（渲染需要），
   并由 `pool_version` 约束一致性：客户端遇到**不认识的逻辑色 ID** → 走兜底显示（灰 / 哈希），
   **绝不把 ID 原样塞进 CSS、也绝不猜一个 HEX**（§4.4）。
5. 删除昵称是**软删**（`status='inactive'`）并**立即释放颜色**（规格 §8）。
6. **没有 owner 概念**：任何地方都不得再出现 `owner_type` / `owner_id` / 「某个客户端拥有某个昵称」
   的建模。昵称是**全局对象**，客户端只是**使用者**（§0.6）。
7. **客户端身份只存在于连接层 / 设备管理层**：`device_id` / `connection` / `online` / `screenshot` /
   `power_on` 归设备体系；**消息、聊天、昵称、颜色一律不带设备身份**。
   一个设备可以使用某个昵称，多个设备也可以使用同一个昵称。
8. **绝不用浏览器 session / IP / Cookie / WebSocket connection id 参与昵称建模**：
   昵称的地址只有一个 —— `nickname_id`。
9. **服务端不代任何客户端创建昵称**：`GET /api/nicknames`（`status=active`）**空就是空**
   （返回 `{"nicknames": []}`，不建行、不分配颜色）。没有共享昵称可选的客户端用**灰临时昵称**发消息
   （PC 用 `ComputerName`、Web 用固定中性文案「默认用户」，逻辑色 `gray`（基础色值 `#8A8A8A`），`nickname_id = NULL`，
   不进池、不占色 —— 最终说明 §9 / §11，完整协议见 §3.2.1）。r4 的「空表懒建默认用户」**已作废**（附录 B）。
10. **「当前使用哪个昵称」是客户端本地状态，永远不进 NAS**（最终说明 §15）：
    PC → 本地配置里的 `current_nickname_id`；**Web → 这一个浏览器自己的 `localStorage`**
    （`fm.lastSender` 存 `nickname_id`；定稿理由见 §5.2 / §10 已定 3）。**服务端没有、也不许有
    「当前昵称」指针**：`GET` / `PUT /api/web/sender` 这类端点**一律不实现**
    —— 否则 A 浏览器选了「爸爸」会把 B 的界面一起改掉（谁赢没有语义）。
11. **一个活跃 `display_name` 只有一个对象、一个颜色**：这是昵称中心模型的**结构性结果**
    （索引① 直接保证），不需要任何额外机制 —— r3 的「颜色归属表 `nickname_colors`」与
    「同名共色」这类说法**都已作废**（§2.C7 / 附录 B）。
12. 协议改动遵循本项目既有约定：**只加字段，不改已有字段语义**（`docs/PROTOCOL.md:3-4`、
    `MessageFrame.cs:5-7` 的「逐字对应」注释）。
13. ★ **昵称的任何管理操作都必须在在线状态下进行**（r6，用户拍板「**完全拒绝离线改昵称**」）：
    新建 / 改名 / 删除 / 重新分配颜色 —— 这四个写操作**离线一律不可用**，客户端必须**明示原因**
    （如「当前未连接服务器，昵称管理不可用」），**不排队、不本地生效、不生成 pending、不留待补发**。
    **唯一可离线做的是「选择昵称」**（`current_nickname_id` 是客户端本地状态，本来就一个字节都不上服务端）。
    ⇒ r4 / r5 的「离线 pending 昵称同步」（`nickname_sync_state = pending`、离线改名队列、
    重连提交 pending）**整套作废**（附录 B B16）。

## 1. 现状勘察（规格 §24 点名的八项）

### 1.1 当前昵称保存在哪里？

**结论：纯本地、三处各存一份，服务端完全没有昵称存储。**

| 端 | 存哪 | 依据 |
|---|---|---|
| 网页端 | `localStorage` 两个 key：`fm.names`（昵称数组 JSON）与 `fm.lastSender`（上次选中的昵称） | `web/static/app.js:1088` `NAMES_KEY='fm.names'`、`:1089` `LAST_SENDER_KEY='fm.lastSender'`；读写点 `loadNames()` `:1092-1104`（解析失败/为空回默认 `['我']`，见 `:1103`）、`persistNames()` `:1106-1108`、`rememberSender()` `:1110-1112`、`currentSender()` `:1115-1121` |
| 网页端（设置页 UI） | 「发送昵称」卡片：新增 / 改名 / 删除全在本地 | `web/index.html:392-404`（`:399` `name-new` 输入框、`:404` `names-list` 列表）；渲染与增删改 `web/static/app.js:1135-1187`（改名 `:1159-1169`、删除 `:1176-1181`） |
| 网页端（发送区） | 「以谁的名义发」下拉，选项来自上面那份本地数组 | `web/index.html:229`（`sender-sel`）；`web/static/app.js:1124-1133` `renderNameSelectors()`；发送时 `sender_name: currentSender()` `:881` |
| PC 本地页 | `localStorage` key `fm.pc.lastSender`（**只记「上次用哪个」**，不是权威） | `web/shell/pc.js:36` `LS_SENDER='fm.pc.lastSender'`；读取 `:567`、写入 `:269`、`:493` |
| PC 宿主（权威） | `%APPDATA%\FamilyAgent\config.json` 里的 `reply_names` / `reply_name` | `pc-agent/FamilyAgent.Core/Config/AgentConfig.cs:44-50`、`:81` `FilePath`；首次运行从 `MachineName` 派生（见 1.2）；桥接 `pc-agent/FamilyAgent/JsBridge.cs:55`、`:65-66`（`host.hello` 带 `reply_names` / `reply_name`）；唯一写入点 `pc-agent/FamilyAgent/App.xaml.cs:530` 起（点「保存」→ `web.save_config`，`:594-610` 把新名字追加进 `ReplyNames` 并设 `ReplyName`），桥的字段解析 `JsBridge.cs:403-416`、`:513` |
| 服务端 | **无昵称表、无昵称字段** | `server/db.py:30-127` 的 SCHEMA 只有 8 张表：`devices` / `messages` / `message_targets` / `xiaomi_devices` / `xiaomi_auth` / `events` / `unlock_requests` / `unlock_guard`；`server/` 全目录 grep `nick` 只命中 `messages.sender_name`。协议文档也明说：「发送人昵称是**纯本地概念**……**服务端不存昵称表**」`docs/PROTOCOL.md:23-24`；`docs/GROUP-CHAT-MODEL.md:25`「昵称仍纯本地、不上传服务端」、`:65`「昵称纯本地，服务端只存 `sender_name` 字符串」 |

> 已实测确认：库文件 `server/data/family.db` 的 `sqlite_master` 里没有 `nicknames`（或任何昵称相关）对象。
>
> ⚠ **容易混淆的一点**：`devices.name` 是**设备名**，不是昵称。它来自 PC 连接串的 `name=` 参数
> （`server/main.py:733` `name = q.get("name") or device_id`），而 PC 侧 `name=` 取的是 `DeviceName`
> （`pc-agent/FamilyAgent.Core/Transport/ConnectionManager.cs:230` `Add("name", _config.DeviceName)`），
> 首次运行 `DeviceName = Environment.MachineName`（`AgentConfig.cs:116-117`）。
> 也就是说**今天「设备名」和 PC 默认「回复昵称」是同一个来源、同一个字符串**，但它俩是两条独立的存储路径。

### 1.2 当前 PC 默认昵称如何产生？

**结论：`Windows ComputerName` → `AgentConfig.Normalize()` → `DeviceName`，再由 `DeviceName` 兜底成第一个回复昵称。与规格 §3 的期望一致，但它同时被当成设备名用。**

- `pc-agent/FamilyAgent.Core/Config/AgentConfig.cs:114-117`：
  `Normalize()` 里 `if (string.IsNullOrWhiteSpace(DeviceName)) DeviceName = Environment.MachineName;`
- 同文件 `:118-124`：`DeviceId` 默认也由 `MachineName` 派生（小写、只留字母数字和 `-`/`_`，加前缀 `pc_`）。
  ⚠ 这个 `DeviceId` **只是设备体系的主键**（连接 / 在线状态 / 截图 / 开关机都用它）。
  昵称中心模型下它**不再参与任何昵称建模** —— 「谁拥有这个昵称」这个概念已删除（§0.6）。
- 同文件 `:126-132`：`ReplyNames` 为空时 `ReplyNames.Add(DeviceName)`，`ReplyName = ReplyNames[0]`。
  → **第一次安装的默认昵称 = ComputerName**（例：`MY-PC`）。
- 同文件 `:127-135`：`ReplyNames` 会被 `Trim()`，但**没有长度上限**（对比网页端 `input.maxLength = 16`，
  `web/static/app.js:1157`；服务端 HTTP 侧 `max_length=32`，`server/main.py:347`）。
- 「后续不再因 ComputerName 改变而自动覆盖用户昵称」这一点**现状已满足**：`Normalize()` 只在 `DeviceName`
  为空时才写 `MachineName`，用户改名只写 `ReplyName` / `ReplyNames`（`App.xaml.cs:603-608`），不回写 `DeviceName`。

### 1.3 Web 昵称如何保存？

**结论：`localStorage` 的 `fm.names` 数组 + `fm.lastSender`；服务端不参与，也不返回任何昵称。**

- 读写点见 1.1 表格；默认值 `['我']`（`web/static/app.js:1103`）。
- 服务端 `/api/config` 明确「**不含昵称**」：`server/main.py:163-173` 返回
  `auth_required / popup_auto_close_seconds / public_url / xiaomi_enabled / server_time / permissions`；
  协议文档也在 `docs/PROTOCOL.md:197` 标注「前端需要的公开配置（**不含昵称**）」。
- Web 的「身份」在服务端**只剩一个常量标记**：`sender_kind='web'`
  （`server/db.py:51` 默认值、`:131` 迁移、`server/services/messages.py:40` 插入时写死 `'web'`），
  **没有任何 ID、没有用户、没有角色**（`server/permissions.py:8-11`「这个系统目前**没有用户、没有角色**」；
  `/ws/web` 匿名，见 `docs/PROTOCOL.md:21-22`）。

### 1.4 消息中的 nickname 字段结构？

**结论：就是一个字符串 `sender_name`，没有 nickname_id、没有颜色。**

- 库：`server/db.py:45-53` `messages` 表 —— `sender_name TEXT NOT NULL`（`:47`）、
  `sender_kind TEXT NOT NULL DEFAULT 'web'`（`:51`）、`sender_device_id TEXT`（`:52`）。
- 写入：网页端发消息 `server/services/messages.py:34-42`（`sender_kind` 写死 `'web'`，`sender_device_id` 为 `NULL`）；
  PC 回复 `server/services/messages.py:183-197`（`sender_kind='device'`，`sender_device_id=device_id`）。
- 入参校验：`server/main.py:346-352` `MessageBody.sender_name: str = Field(min_length=1, max_length=32)`；
  WS `reply` 帧侧 `server/main.py:879-880`（`.strip()[:32]`，为空则回退设备名 `dev.name` → `device_id`）。
- 出参/帧：`server/main.py:300-316` `_device_payload()` 里的 `sender_name`（推给 PC）；
  `server/services/messages.py:169-175` `group_history()` 每条只有
  `message_id / sender_name / content / created_at / direction`；`public_message()` `:135-146` 原样保留 `sender_name`。
- PC 端 DTO：`pc-agent/FamilyAgent.Core/Protocol/Frames/MessageFrame.cs:18` `SenderName`；
  `pc-agent/FamilyAgent.Core/Protocol/HistoryEntry.cs:8` `SenderName`（`:19` 拼成 `Name · Time` 表头）；
  `pc-agent/FamilyAgent.Core/Messaging/MessageStore.cs:23` `MessageRecord.SenderName`（`:70` 从帧取、`:272` 从落盘 JSON 取）。
- Web 端消费：`web/static/chat.js:44-46` `nameOf(msg)` 取 `msg.sender_name`；
  `:49-53` `isOwn()` = **`sender_name` 是否等于我当前选的昵称**（群聊模型下这就是「靠左还是靠右」的唯一判据）。

### 1.5 当前 WebSocket 消息协议？

**结论：两条端点，`/ws/device` 双向、`/ws/web` 实际只有「ping → pong」上行；所有 Web 写操作走 HTTP。**

依据：`docs/PROTOCOL.md`（协议是契约文档，`docs/PROTOCOL.md:3-4`）+ 实际代码。

- **端点**：`/ws/device/{device_id}?token=&name=&type=&platform=&agent_version=&enroll_token=&windows_state=&capabilities=`
  （`server/main.py:729-741`；PC 侧拼查询串 `ConnectionManager.cs:224-236`）；
  `/ws/web`（`server/main.py:965-986`）。
- **`/ws/device` 上行帧**（`server/main.py:825-959` `handle_device_message`）：
  `heartbeat`（`:828-836`）、`ack`（`:838-852`）、`screenshot_response`（`:854-857`）、
  `reply`（`:859-893`，字段 `sender_name` / `content` / `client_id`）、`history_request`（`:895-905`）、
  `unlock_result`（`:907-944`）、`event`（`:946-947`）、`device_info`（`:949-959`）。
- **`/ws/device` 下行帧**：`hello`（`main.py:775-781`）、`message`（`:291-316`，含 `history` + `auto_close_seconds` + 补投时 `redelivered`）、
  `heartbeat_ack`（`:836`）、`reply_ack`（`:865-870` / `:882-888`）、`history_response`（`:898-905`）、
  `screenshot_request`（`hub.py:199-201`）、`shutdown`（`main.py:503`）、`unlock_request`（`main.py:572`）。
- **`/ws/web` 上行帧**：**只有 `ping`**（`server/main.py:977-980`）。
  → 这是本方案的关键约束：**Web 端没有「上行事件」通道**，昵称操作必须走 HTTP API。
- **`/ws/web` 下行广播类型**（全量 grep `broadcast_web`，`server/main.py`）：
  `ready`（`:976`）、`device_status`（`:782`、`:821`、`:833`、`:957`）、`device_updated`（`:230`）、
  `device_deleted`（`:237`）、`message`（`:372`、`:890`，设备回复额外带 `reply: true`）、
  `message_status`（**已注释掉**，`:847-852`）、`wake`（`:487`）、`shutdown_sent`（`:508`）、
  `unlock_pending`（`:586`）、`unlock_result`（`:933`、`:942`）、`xiaomi`（`:717`）。
  协议文档对应表：`docs/PROTOCOL.md:174-186`。
- **广播原语**：`HUB.broadcast_web()`（`server/hub.py:179-187`，遍历 `web_clients`）
  与 `HUB.send_to_device()`（`server/hub.py:150-169`）；「推给所有已连接设备、排除发起者」的既有写法
  在 `server/main.py:319-340` `_broadcast_message()`（遍历 `HUB.devices.keys()`）——**昵称广播应照抄这个形状**。
- **Auth**：`/ws/web` 带口令时校验 cookie 或 `?token=`（`server/main.py:967-972`）；`/ws/device` 校验设备 token
  或 `enroll_token`（`main.py:744-762`，`services/devices.py:95-101` `verify_token`）。
- **心跳/重连/发送队列**：`docs/PROTOCOL.md:157-168`（「不预判，直接尝试发；失败了才入队，`hello` 之后 + 心跳时补发」）；
  PC 侧实现 `pc-agent/FamilyAgent.Core/Transport/Outbox.cs`（**内存队列**，`:27-32` 只有
  `List<(string Json, string Kind)>`，**不落盘**）、`HeartbeatLoop.cs`、`ConnectionManager.cs:478-486`（`SendOrQueue`）、
  `:498-503`（`reply` 帧构造）。

### 1.6 当前 PC 本地配置文件？

**结论：一个 `config.json`，11 个字段，**没有** nickname_id / color，也**没有任何同步状态字段**。**

- 路径：`%APPDATA%\FamilyAgent\config.json`（`AgentConfig.cs:66-67`、`:81`）；可用 `--config` 覆盖
  （`:69-79`，SYSTEM 身份的登录前实例必须共用用户那份，否则会注册成另一台设备）。
- 字段清单（`AgentConfig.cs:23-58`）：`server_url`、`device_id`、`device_name`、`token`、`enroll_token`、
  `auto_start`、`popup_auto_close_seconds`、`reply_names[]`、`reply_name`、`theme`、`theme_mode`。
- 读写：`Load()` `:89-112`（损坏就回默认，**不报错**）、`Save()` `:145-153`（`WriteIndented` + `UnsafeRelaxedJsonEscaping`）。
- 同目录还有别的本机文件（说明「昵称缓存」也可以有独立文件）：
  `%APPDATA%\FamilyAgent\messages.jsonl`（本地消息库，`MessageStore.cs:126`、`:158-161`）。

### 1.7 当前 NAS SQLite schema？

**结论：8 张表 + 5 个索引，靠 `CREATE TABLE IF NOT EXISTS` + `MIGRATIONS` 列表演进；单连接 + RLock，`execute()` 每次自动 commit。**

- SCHEMA：`server/db.py:30-127`。表：`devices`（`:31-43`）、`messages`（`:45-53`）、`message_targets`（`:55-64`）、
  `xiaomi_devices`（`:66-80`）、`xiaomi_auth`（`:82-90`）、`events`（`:92-98`）、`unlock_requests`（`:103-113`）、
  `unlock_guard`（`:118-122`）。索引：`:115`、`:124-126`。
- 迁移机制：`MIGRATIONS`（`:130-151`，8 条 `(表, 列, DDL)`）+ `POST_MIGRATION_DDL`（`:154-156`），
  执行器 `_migrate()` `:159-166` —— 用 `PRAGMA table_info` 判断列在不在，**天然幂等**，新增列/索引直接追加进列表即可。
- 连接：`get_conn()` `:169-183` —— **全局单连接** + `check_same_thread=False`、`journal_mode=WAL`、
  `synchronous=NORMAL`、`foreign_keys=ON`（`:177-179`）。**注意：没有设 `busy_timeout`。**
- 并发：`threading.RLock`（`:22`）包住 `query` / `execute`（`:190-211`）；`execute()` **每条语句自己 commit**（`:209`）。
- 另外 `server/main.py` 是单进程单事件循环（`server/run.py:76-77` uvicorn `Server(cfg).run(sockets=...)`，没配 workers）。
- 实测：`server/data/family.db` 的 `sqlite_master` 与 `SCHEMA` 一致，含 `sqlite_sequence`。

### 1.8 当前消息历史结构？

**结论：服务端就是 `messages` 表裸行（对外砍掉 `targets`、状态写死 `sent`）；PC 弹窗右侧历史由服务端按 `viewer` 现算。**

- 对外单条视图：`server/services/messages.py:135-146` `public_message()`
  —— 去掉 `targets`，**写死** `status = "sent"`（常量 `STATUS_SENT` `:31`）。
- 群聊最近往来：`services/messages.py:153-176` `group_history(limit=30, viewer_device_id=...)`
  —— 按 `id DESC` 取 N 条再 `reverse()`，逐条给
  `{message_id, sender_name, content, created_at, direction}`（`direction` 是**相对该设备**的 `out`/`in`，`:164-175`）。
  别名 `history_for_device()` `:223-229`；老视图 `conversation()` `:200-220`（带 `targets`）。
- HTTP：`GET /api/messages`（`server/main.py:378-387`，`limit=min(limit,200)`）、
  `GET /api/conversations/{device_id}`（`:390-400`，标「老视图」）、
  `POST /api/messages/{id}/read`（`:403-413`）。
- 推给 PC 的历史：`main.py:309-312` 把 `group_history(limit=CONFIG.message.history_limit)`（默认 30，
  `server/config.py:25`）塞进 `message` 帧；`history_request` 时再给一份（`main.py:902-904`）。
- PC 侧消费：`pc-agent/FamilyAgent.Core/Protocol/HistoryEntry.cs:26-46` `FromJson()` 读
  `message_id / created_at / sender_name / content / direction`（`:31` `sender_name`、`:42` `IsOut`）；
  `MessageStore` 会把**整帧原文**存进 `Raw` 以便重放（`MessageStore.cs:30-32`、`:36-39`）。
- 结论：历史消息里**没有任何颜色信息、也没有稳定身份 ID**，渲染时颜色是客户端现算出来的（见 §2-C1）。

---

## 2. 冲突与风险清单

### 2.C1（**最高优先级**）颜色从「客户端哈希」改成「服务端分配」后，历史消息的颜色怎么办？

**现状（可执行证据）**

- 权威算法：`web/static/app.js:331-344` —— 12 色数组 `NICK_COLORS`（`:331-334`）+ 31 乘法哈希取模
  （`h = (Math.imul(h,31)+charCodeAt(i))>>>0; NICK_COLORS[h % 12]`）。
- 等价副本：`web/static/chat.js:21-34` `NICK_COLORS` + `fallbackNickColor()`；`:37-42` `colorOf()`
  优先转调 `window.nickColor`（app.js 那份是「唯一权威」），拿不到就用副本。
- 第三处转调：`web/static/shell.js:677-680` `nickColorOf()`（壳模式下同样优先用 `window.nickColor`，
  兜底硬编码 `'#90CAF9'`）。
- 文档把它当契约写死了：`docs/DESIGN-TOKENS.md:122-146`「12 色调色板 + 哈希取模……算法必须三处完全一致」；
  `docs/PROTOCOL.md:251`（Android 也照抄）。

**颜色的 4 个消费点（改造要逐个改，漏一个就两端不一致）**

| # | 位置 | 用途 | 依赖的 CSS 变量 |
|---|---|---|---|
| 1 | `web/static/chat.js:63` | 消息行头像底（32px 圆 + 首字） | `--chat-nick`，落点 `web/static/style.css:878`（网页版）、`web/shell/pc.css:181`（PC 本地页） |
| 2 | `web/static/shell.js:604` | 全屏弹窗消息行的**小圆点**（8–10px） | `--msg-nick`，落点 `web/static/style.css:527`、`web/static/shell.css:184` |
| 3 | `web/static/app.js:1125`、`web/static/shell.js:657` | 昵称下拉项的**文本颜色** | 直接 `label.style.color`（`web/static/app.js:384`） |
| 4 | `web/static/app.js:1152` | 设置页昵称列表的色块 | 直接 `sw.style.background` |

**真正的冲突（不是「改一行」的事）—— 最终说明 §11 已给出答案：快照**

1. **历史消息必须自带快照，不能依赖昵称表** —— 最终说明 §11 定死：
   `message = {nickname_id, display_name_snapshot, color_snapshot, content}`
   （**r6**：`color_snapshot` 落库的是**逻辑色 ID**，如 `color_12` / `gray`，见 §3.2 —— 存 ID 才能跨主题正确渲染）。
   于是「改名之后老消息显示什么」不再是问题：**显示当时的名字 + 当时的逻辑色 ID**。
   - 现状：`messages.sender_name`（`db.py:47`）**就是** `display_name_snapshot` 的天然落点
     （写入即固化，`services/messages.py:38-42`）→ **不动它、不改名**；
   - 要加的是 `sender_nickname_id`（稳定身份）与 `sender_color`（**当时的逻辑色 ID**）两列。
     这是本方案**唯一必动的既有表结构**（§3.2）。
2. **改名 / 改色 / 删除昵称，一律不回写历史消息**（快照就是快照）。这顺手解决了 r4 的两难：
   「跟随当前色」会让老消息集体变色（看着像「这个人换色了」），「冻结」又似乎要多一列 ——
   最终说明选了**冻结（快照）**，而且它不是「可选的一列」，而是**历史能正确显示的前提**：
   昵称一旦被删，按 `nickname_id` 现查就什么都查不到了。
3. **老消息（改造前入库的）没有快照**：`sender_nickname_id` / `sender_color` 只能为 `NULL`，
   而 `sender_name` 有值 → 显示时用「名字 + 哈希兜底色」（= 今天的行为，**零回归**）。
   回填只在**能确定**时才做（见 §3.4）。

**改造点清单（Phase 3 的验收就按这张表逐项打勾）**

- `web/static/app.js:336-344` `nickColor()`：保留为**兜底**（`nickname.enabled=false` 时完全不改行为），
  新增 `nickColorById(id, fallbackName)` ← 查 NAS 下发的昵称表字典（字典里存的是**逻辑色 ID**），
  再经 §4.4 的映射表算出显示色；查不到该 id 再退回 `nickColor(fallbackName)`。
- `web/static/chat.js:37-42` `colorOf()`：改成「优先查昵称表（按 `msg.sender_nickname_id`）→ 次选 `window.nickColor(name)` → 末选内置副本」。
  这一处改完，**网页端与 PC 本地页同时生效**（chat.js 是两端共用的，`web/static/chat.js:1-2`）。
- `web/static/shell.js:677-680` `nickColorOf()`：同上，壳模式经 `host.*` 拿到的昵称表优先。
- `web/static/app.js:384`（下拉文本色）：**注意这处今天就不达标**，见 §4。
- `web/shell/pc.css:181-184` / `web/static/style.css:878-879`：头像字色**写死** `color: rgba(0,0,0,0.7)`
  （pc.css 那行自带注释「唯一一处非令牌色……与 style.css 里 .chat-avatar 的值一模一样，改就两端一起改」）。
  ⚠ **这两处必须改**：16 个基础色值里配 0.7 黑字有 **11/16 达不到 4.5**（§4 实测），
  要改成「按底色亮度自适应选白字 / 0.7 黑字」（两个文件一起改，保持 pc.css 注释里那条「同值」约束）。
  **r6 起颜色来源是逻辑色 ID**：显示色（圆点变体 / 头像底 / 字色）**统一由 §4.4 的映射表算**，
  CSS 里写死的 `rgba(0,0,0,0.7)` 换成变量（如 `--chat-nick-fg`）。

### 2.C2 规格 §18 / 最终说明 §11 的「消息自带发送时昵称文本 + 颜色」是否已满足？

**一半满足：文本已经有，缺「稳定 ID」与「颜色快照」两列。**

- 已满足：发送时的昵称文本（`messages.sender_name`，`db.py:47`，写入即固化，
  `services/messages.py:38-42` 网页端 / `:190-195` PC 回复）——**它就是 `display_name_snapshot`**，
  不需要新列、也不需要改名（协议「只加字段」）。规格 §18「不要因为删除 nickname 导致历史消息无法显示」
  在**文本**这一层今天就已经成立（历史不 JOIN 昵称表）。
- 缺的东西（按重要性）：
  1. `sender_nickname_id` —— 稳定身份（改名后仍能把历史与同一昵称对上）。
  2. `sender_color` —— **当时的逻辑色 ID**（最终说明 §11 的 `color_snapshot`；值形如 `color_12` / `gray`）。
     没有它，昵称被删或改色后，历史消息的颜色只能落到哈希兜底 → 同一个人看起来两种颜色（§2.C1）。
  3. PC 侧本地落盘的帧副本里也没有（`MessageStore.cs:36-39` `Raw` 是服务端帧原文）→
     新字段必须走「协议只加字段」的路子，**老帧缺字段要能容忍**（`HistoryEntry.cs:31` 那种
      `TryGetProperty` 写法是正确范式，照抄）。
  4. 长度上限三方不一致：Web 输入框 16（`app.js:1157`）、HTTP 32（`main.py:347`）、PC 无限制
     （`AgentConfig.cs:127-135` 只 `Trim`）→ **r6 已定：NAS / API / PC / Web 统一 32**
     （`nicknames.display_name` 保持 `1..32`；Web 输入框 `maxLength` 16→32；PC 写入前按 32 校验）
     （§3.1 / §5.5 / §10 已定 13）。

### 2.C3 规格 §1/§7 的「统一发送端 / 身份」怎么界定？—— 新模型下这个问题消失了

**现状（事实）**：`/ws/web` 匿名、不区分浏览器（`docs/PROTOCOL.md:21-22`；`server/main.py:965-972`
只校验家庭口令）；服务端只有一个常量 `sender_kind='web'`（`db.py:51`/`:131`、`messages.py:40`）；
没有用户、没有角色（`permissions.py:8-11`）；网页端今天把昵称存在浏览器 `localStorage`
（`app.js:1088-1121`），PC 存在 `AgentConfig` 的 `reply_names` / `reply_name`（`AgentConfig.cs:44-50`）。
→ 也就是说：**今天「谁在用哪个昵称」本来就是纯客户端的本地状态**，服务端一点都不参与。

**r2/r3 走过的弯路（记录在此，避免下一轮再绕回去）**：先按「谁拥有昵称」建模，于是不得不回答
「Web 是不是一个全局单例身份」「同一个 owner 能挂几个 active 昵称」「两个同名 PC 是不是同一个人」，
并为每个问题各写一条 UNIQUE 约束、甚至专门的「颜色归属表」。
**用户指出这些问题根本不该存在** —— 把「拥有」这件事从模型里删掉，问题全部消失（§0.6）。

**新模型（昵称中心）三个概念，互不混淆**

| 概念 | 是什么 | 放在哪 |
|---|---|---|
| **昵称**（`nickname_id` / `display_name` / `color` / `status`） | **全局对象，没有拥有者** | NAS 的 `nicknames` 表（唯一权威，§3.1） |
| **客户端**（PC / Web / 将来的 Android） | 昵称的**使用者** | 连接层 / 设备管理层（`device_id`、`/ws/device`、`/ws/web`） |
| **「这个客户端现在用哪个昵称」** | 客户端的**选用状态**（最终说明 §8 的「选择昵称」） | **客户端本地** —— PC 存本地配置、Web 存 `localStorage`；**永远不上 NAS**（§5.2） |

于是（最终说明 §7 / §8 的口径）：

- **Web 不是「一个身份」，也不需要「单例」**：它就是「一个可以选用任意共享昵称的公共客户端」。
  三个浏览器同时在线 = 三个客户端各自选用昵称；**同名只有一个对象**（所以颜色必然只有一份）。
- **PC / Android 也不「拥有」昵称**：它们只是本地记一个 `current_nickname_id`，表示
  「我发消息时用谁的名义」；还没选就是「灰临时昵称」（§3.2.1）。
- **规格 §1「Web 是统一发送端」→ 等价说法**：**「Web 客户端可以选用共享昵称表里任意 active 昵称来发消息」**
  （与原意一致，只是不再需要把 Web 建模成一个「身份实体」）。
- **规格 §7「一个 owner 只能有一个 active 昵称」→ 该条不再适用**（没有 owner 了）；
  等价且更强的约束变成 **「一个 `display_name` 只能有一个 active 昵称」**（§2.C4 索引①）。
  ⚠ 最终说明 §3 比 r4 更严：同名**不是**「撞上就共用」，而是**创建时被拒**
  （`NICKNAME_ALREADY_EXISTS`），由客户端引导用户**直接选用既有的那个昵称**。
- 多客户端共用同一昵称是**一等公民场景**，不是特例：

  ```
  PC-001 ─┐
  PC-002 ─┼──→ 「妈妈」→ 红色   ← 只有一条 nicknames 行、一个颜色，三个使用者
  Web    ─┘
  ```
  ⚠ 最终说明 §16 明令**禁止**的旧写法（反例，别复活）：`PC-001/妈妈/红色` + `PC-002/妈妈/蓝色`，
  然后再写「颜色共享」逻辑去把它们对齐 —— 最终只有 `妈妈 → 红色` 一份（见附录 B）。
### 2.C4 唯一性怎么落库（新模型：两条 partial unique index 就够）

**结论：`display_name` 在 `active` 上唯一、`color` 在 `active` 上唯一。
两条都能用 SQLite 的「部分唯一索引」（partial unique index）表达，并且**在同一张表上互不冲突**
—— r2/r3 那对「两条规则互相打架」的问题，随 owner 模型一起消失了（见下）。**

```sql
-- ① 一个 display_name 在活跃期只有一行
--    = 「同名只有一个对象」的唯一保证（最终说明 §3 / §2.C7）
CREATE UNIQUE INDEX IF NOT EXISTS ux_nicknames_name_active
    ON nicknames(display_name) WHERE status = 'active';

-- ② 一个 color 在活跃期只属于一行 —— 「不重色」的最后防线（规格 §11 / §12）
CREATE UNIQUE INDEX IF NOT EXISTS ux_nicknames_color_active
    ON nicknames(color) WHERE status = 'active';
```

- 为什么不用普通 `UNIQUE(display_name)`：软删（`inactive`）后要能**再建同名**（规格 §8「释放颜色」），
  历史的 `inactive` 行**不能占位** → 只有部分索引能表达「只在 `active` 上唯一」。
- **为什么这次两条索引不打架了**（值得记一行，这是 r2/r3 翻车的技术根因）：
  那时既要「同一 owner 只能一条 active」，又要「同名共色」，于是**两条同名的行必须共用同一个颜色**，
  而 `UNIQUE(color) WHERE active` 恰好**拒绝**两行共用一色 → 两条规则直接冲突，只能再引入一张颜色归属表。
  现在**一个活跃名字只有一行**、颜色只存一份 → **根本不存在「两行同名要共用一色」这种需求**，
  第二条索引只管「不同名字不同色」，两条各管一件事，彼此无关。
- SQLite 版本要求：部分索引自 **3.8.0**（2013）起支持，`WHERE` 子句**可以引用本表任意列**
  （含 `display_name` / `color`）。本机实测 `sqlite3.sqlite_version = 3.40.1`（Python 3.11.2 自带）
  → 远高于要求，且本库用标准库 `sqlite3`（`server/db.py:10-13`「不引入 ORM」）→ **零新依赖**。
- 部分索引的 `WHERE` **不能**引用其它表 / 不能含子查询 / 不能含非确定性函数 —— 本方案两条都不需要。

**实测记录**（临时库验证，可原样复现）：

| 操作 | 结果 | 靠谁 |
|---|---|---|
| 建「妈妈」→ 再建「妈妈」 | **被拒**：返回 `NICKNAME_ALREADY_EXISTS`，库里始终只有 1 行（最终说明 §3：显式报错，**不静默复用**） | 索引① + 服务层显式判重（§3.3） |
| 两台同名机器各创建一次 `LIVINGROOM-PC` | 第一次建行；第二次**被拒** `NICKNAME_ALREADY_EXISTS` → 客户端提示「已存在，直接选用它？」 | 索引① |
| 给另一个名字分配「妈妈」已占的颜色 | **拒绝**：`UNIQUE constraint failed: nicknames.color` | 索引② |
| 「妈妈」软删 → 再建「妈妈」 | **成功**，拿到新的 `nickname_id` 与颜色（历史的 `inactive` 行不占位） | 索引① 的 `WHERE status='active'` |
| `inactive` 同名行堆积多条 | 无冲突 | 同上 |

**索引表达不了、必须由应用层事务兼顶的部分（逐条 + 怎么测）**

| # | DDL 表达不了 | 为什么做不到 | 应用层怎么兼顶 | 怎么测 |
|---|---|---|---|---|
| 1 | **名字的 trim 规范化** | SQLite 默认 `BINARY` 排序规则，`'妈妈'` 与 `'妈妈 '` 是两个不同值（**已实测两条都能建**） | 服务层写入前 `strip()`，DB 存的就是规范化后的值 | 提交 `' 妈妈 '` → 断言落库 `'妈妈'`；提交 `'妈妈 '` 与 `'妈妈'` → 409 |
| 2 | **Unicode 规范化**（NFC/NFD 等值写法） | SQLite 不做 Unicode 规范化 | 服务层入库前 `unicodedata.normalize('NFC', name)` | 断言 `'é'`(U+00E9) 与 `'e'+U+0301` 归一后冲突 |
| 3 | **大小写等价**（`MY-PC` vs `my-pc`） | 默认 `BINARY` 区分大小写；`COLLATE NOCASE` 只折叠 ASCII，且会把「两个大小写不同的昵称是两条」的意愿也折叠掉 | **有意不做**归一：显示名大小写是有意义的。要改只需把索引① 写成 `display_name COLLATE NOCASE`（一条 SQL） | 若采用 NOCASE：断言 `Mom` 与 `mom` 冲突 |
| 4 | **「颜色池还没占满」** | 索引② 只保证不重色，不保证还有色可取 | `pick_first_available()` 返回 `None` → `503 NO_AVAILABLE_COLOR`，**绝不回退到重色或哈希**（规格 §11） | §6.4 判据 2 |
| 5 | **重复名的错误语义**（要「明确报错」而不是「静默复用」，最终说明 §3） | 完整性约束只管「拒绝 / 允许」，不管「报什么错」 | 服务层先 `SELECT` 判重 → 命中即抛 `NICKNAME_ALREADY_EXISTS`（**不插入**）；并发下索引① 拒绝 `IntegrityError` 后**翻成同一个错误**返回（§3.3 / §6.2） | 同名连建 5 次 → 5 次都是 `NICKNAME_ALREADY_EXISTS`，库里始终 1 行 |

**r3 的「颜色归属表 `nickname_colors`」与 r4 的「同名共色」表述都已作废**（记录一行以免恢复）：
颜色归属表是为了在「同名多行」下保证同名共色而引入的（`ux_ncolors_name_active` + `ux_ncolors_color_active`
+ 一套释放逻辑）；r4 的「同名共色」则是靠「一个活跃名字只有一行」来「结构性」保证。
**r5 的口径**：一个活跃名字只有一行，颜色就在那一行上 → 同名共色**不需要任何机制**，
`nickname_colors` **整张删掉**，也不要在代码/文档里再出现「同名共色」这个说法（最终说明 §16 明令禁止那套写法）。
### 2.C5 现有 12 色板 vs 规格给的 16 色候选

**结论：两套色板的亮度语义相反，不能简单替换；必须先定「昵称色的用途契约」，再定池子。**（数据见 §4）

- 现有 12 色是 MD3 **tone-80 级浅彩**（`#90CAF9`、`#CE93D8`…，`app.js:331-334` / `DESIGN-TOKENS.md:143-146`）。
- 规格给的 16 色是 **tone-40 级中深彩**（`#5E35B1`、`#3949AB`…，规格 §9）
  —— **r6 起它们是「基础色值」，入库 / 协议走逻辑色 ID `color_01`…`color_16`**（§4.3）。
- 实测结论（对比度按 WCAG 2.1 算，浅色表面 `#FFFFFF`、深色表面 `#2B2B2B` 取自
  `web/static/tokens.css:86` / `:156`）：
  - 作为**头像底 + 深字**（今天 `.chat-avatar` 的实际用法，`style.css:878-880`）：
    现有 12 色 **12/12 达标**（≥4.5），规格 16 色只有 **5/16** 达标；
  - 作为**浅色表面上的圆点**（8–10px，非文本判据 ≥3.0）：现有 12 色 **0/12 达标**（1.29–2.39），
    规格 16 色 **12/16 达标**；
  - 作为**深色表面上的圆点**：现有 12 色 **12/12**，规格 16 色 **10/16**；
  - 作为**浅色表面上的文本色**（`app.js:384` 下拉项的实际用法）：现有 12 色 **0/12 达标**，
    规格 16 色 **6/16** 达标。
- 也就是说：**「圆点在浅色主题下看不见」这个问题今天就已经存在**，与昵称系统改造无关；
  但如果直接换成规格的 16 色，就会把「头像里首字看不清」变成新问题（5/16 达标）。
- **定稿见 §4.2 / §4.3 / §4.4**：池子用最终说明 §5 的 16 色，并配「圆点按主题变体 / 字色自适应 /
  头像底变体 / 下拉不用色值当字色」四条规则（逐色实测后给出，两主题可做到 16/16 达标）。
  **r6**：这四条规则**全部在客户端渲染时执行**，契约表见 §4.4（PC / Web 两端共用）。

### 2.C6 其他风险（不阻塞，但要记）

| # | 风险 | 依据 / 说明 |
|---|---|---|
| R1 | **改昵称的鉴权面比 r2 大**：现有 `/api/*` 只有家庭口令 `WebAuth`（`server/main.py:114-123`），没有用户/角色（`permissions.py:4`「没有用户、没有角色」，权限常量在 `:23-37`）。新模型下昵称是**全局共享对象**，改一个名字影响**所有使用者**（r2 时代只影响「自己那条」）。 | 本项目定位就是「一家人用一台 NAS + 一个家庭口令」（`permissions.py:4`），因此**不引入账号体系**；但要①在 UI 上明说「这是全局昵称」（规格 §19 的昵称列表页本来就是全局视图），②加 `nickname.manage` 权限常量占位（§5.1）。**现已定为 §10 已定 11**。 |
| R2 | **颜色最终会写进 inline style**（`el.style.setProperty('--chat-nick', color)`）。协议 / 库里只走**逻辑色 ID**（DDL 的 `CHECK` 直接枚举 16 个值），客户端渲染前必须**再查一次映射表**：**未知 ID → 走兜底**，绝不把收到的字符串原样塞进 CSS —— 否则接口/DB 被写脏就等于往页面注入任意 CSS。 | `chat.js:63`、`shell.js:604`、`app.js:1152`。DDL 枚举约束 + 服务层白名单 + 客户端映射表兜底（§3.1 / §4.4 / §5.5）。 |
| R3 | **`DELETE /api/devices/{id}` 会物理删设备**（`services/devices.py:129-132`，顺带删 `message_targets`）。**新模型下昵称与设备完全解耦 → 删设备不再影响任何昵称。** | r2/r3 的「删设备级联软删昵称」**已作废**（R8 / §3.5）。删设备时**不要动 `nicknames`**。 |
| R4 | `/ws/web` 上行只有 `ping`（`main.py:977-980`）→ **昵称广播只有下行**。Web 写操作全走 HTTP，副作用路径单一。 | 见 §5.6。 |
| R5 | PC 侧的发送队列**不落盘**（`Outbox.cs:27-32` 内存 `List`）→ 若要做「离线 pending 的昵称改动」，就只能靠本地配置文件持久化，不能只依赖 Outbox。 | **r6 已选择「完全不离线改昵称」**（§0.13），因此不需要这层持久化：昵称写操作**必须在线**，**不进 `Outbox`**、不补发（§5.2 / §5.4 / §8.2）。 |
| R6 | 服务端**没有设置 `busy_timeout`**（`db.py:177-179` 只有 WAL / synchronous / foreign_keys）→ 跨进程并发写会立刻 `SQLITE_BUSY`，不是等待。 | 必须在 Phase 1 一起补 `PRAGMA busy_timeout`，见 §6.3。 |
| R7 | 三处颜色算法副本（`app.js` / `chat.js` / `shell.js`）+ 一处文档契约（`DESIGN-TOKENS.md:122-146`）+ 一处协议承诺（`PROTOCOL.md:251` Android 存 SharedPreferences）。改颜色来源要**同时**更新这 5 处，否则出现「网页端新色、PC 本地页旧色」。 | 属于「文档漂移」，Phase 3 必须把 `DESIGN-TOKENS.md` §6 改写成新契约。 |
| R8 | **最大的一条：「某个客户端现在用哪个昵称」被误当成昵称表的一部分。** 这正是 r2/r3 反复翻车的根因（先有 owner，再为 owner 补约束与被删掉的表）。 | 该状态是**客户端的选用状态**，不进 `nicknames` 表（§5.2）。**任何「在昵称表里记录谁在用」的设计都应立刻打回。** |
| R9 | **昵称被删（`inactive`）后，仍在选用它的客户端怎么办**（下一条消息该用什么名字）。 | 见 §3.5 / §10 已定 10：客户端收到 `nickname_removed` 后要回退选用（回退到**灰临时昵称**），服务端写入时若发现该 `nickname_id` 已 `inactive` 也应拒绝并提示。 |
### 2.C7 「同名 = 同一个人」怎么保证？（新模型下是免费的）

**用户原话（早期拍板）**：「撞名则认为就是同一个用户，这与我们初始的以昵称区分用户的理论不冲突。」
**最终说明 §3 的落地**：`display_name` 在 `active` 上唯一；**创建时若已存在 → 明确返回
`NICKNAME_ALREADY_EXISTS`**，然后「客户端允许用户直接使用已经存在的那个」。**不自动加后缀**。

```sql
CREATE UNIQUE INDEX ux_nicknames_name_active ON nicknames(display_name) WHERE status='active';
```

- 「同一个活跃 `display_name` 只有一行」⇒ **不存在「两个都叫妈妈、却是两个对象」的状态**。
- ⇒ **也就不存在「两个同名却颜色不同」**，因此**不需要「同名共色」的任何机制**：
  r3 的「颜色归属表」`nickname_colors`、r4 的「同名共色」表述**都已作废**（§2.C4 末尾 / 附录 B）。
- ⇒ 用户想让第二台机器也用「妈妈」的名义：**不是**再去创建一个「妈妈」（会被拒），
  而是让那台机器**「选用」既有的那个「妈妈」**（§5.2：选择 = 客户端本地状态，NAS 一个字节都不动）。

**为什么是「报错」而不是「静默复用」**（这条决定 UI，必须写清）：静默复用会让用户在「新建昵称」里
填了「妈妈」，却拿到一条自己分不清是不是「既有的/别人的」记录，下一步很可能就去改它的颜色 ——
那是**全局改动**。显式报错 + 一句「已存在，直接选用它？」的引导，把**「创建」与「选择」在 UI 上彻底分开**
（最终说明 §8 的同一原则）。

**与规格 §2「不要使用 `display_name` 作为唯一身份」的张力 —— 新模型怎么解**

| | 说的是什么 | 新模型如何满足 |
|---|---|---|
| 规格 §2 | 不要用 `display_name` 当**唯一身份** | 身份是 **`nickname_id`**（主键、永不变）。`display_name` 只是**当前显示名**，可以改（改名不改 id） |
| 最终说明 §3 | 同名 ⇒ 同一个昵称 | 用 `UNIQUE(display_name) WHERE active` 保证**同一时刻同名只有一个对象**，且创建重复名**被显式拒绝** |
| 是否冲突 | **不冲突，但有一个必须写清的边界** | `UNIQUE(display_name)` 保证的是**同一时刻**「一个名字 ↔ 一个对象」；**跨时间**改名后，历史消息靠**快照**（`nickname_id` + 名字 + 颜色）仍然显示当时的样子（§2.C1）。两句话说的不是同一件事。 |

**已知取舍（必须写进 UI 说明，见 §10 已定 9）**

1. **改名到「已被占用的名字」会撞车**：唯一索引会**拒绝**（409 `NAME_TAKEN`），而**不是**「把两个对象合并」。
   → 用户想「让这台 PC 也署名妈妈」时，正确操作是**选用**已有的「妈妈」（§5.2），**而不是**把它改名成「妈妈」。
   UI 必须给出这个引导，否则用户会以为功能坏了。
2. **同一时刻不能有两个「妈妈」**（哪怕真的是两位长辈）→ 用更精确的显示名区分（「外婆」「奶奶」）。
   这是「同名 ⇒ 同一昵称」的必然代价，用户已接受。
3. **颜色随名字走**：改名 = 换了一个名字 → 颜色会变成新名字原有的颜色。这与规格 §3「改名不改变颜色」
   **表面冲突**；等价做法见 §5.2：如果只是「我以后用别的昵称」，那是**客户端选用状态**的切换
   （不影响昵称本身，也就没有颜色变化问题）；**历史消息不受影响**（快照，§2.C1）。
## 3. 数据模型设计

### 3.1 `nicknames` 表（完整 DDL —— 只有这一张新表）

追加进 `server/db.py` 的 `SCHEMA`（`db.py:30-127`，由 `executescript` 一次执行，
`CREATE ... IF NOT EXISTS` 幂等，不影响现有 8 张表）：

```sql
-- ── 共享昵称表（NAS 是唯一权威；最终说明 §1 / §2）────────────────────
-- 设计要点（r6）：
--   · **只有这 6 列**：不许出现 owner_type / owner_id / device_id / client_id（最终说明 §2 明令禁止）；
--   · nickname_id 才是稳定身份，display_name 只是「当前显示名」（规格 §2）；
--   · ★ color 存**逻辑色 ID**（color_01 … color_16），**不存 HEX**：HEX 是「显示色」，
--     由客户端按「逻辑色 ID + 主题」算出来（§4.4）。这样 NAS 不需要为浅色/深色各存一套色，
--     也不破坏「昵称颜色是全局统一的」这个概念（r6 拍板第 ④ 条）；
--   · 「逻辑色 ID → 基础色值」常量表在服务端 Python（server/nicknames.py，§4.3）；
--     DB 只做「枚举 + 唯一」约束 → 改基础色值不需要 DDL 迁移；
--   · 灰（本地临时昵称用）的逻辑色 ID 是 `gray`，**不在下面的 CHECK 枚举里**，
--     也永远不被分配给共享昵称（§3.2.1）；
--   · 删除 = status='inactive'（软删），行永久保留，颜色立即释放（规格 §8）。
CREATE TABLE IF NOT EXISTS nicknames (
    nickname_id  INTEGER PRIMARY KEY AUTOINCREMENT,
    display_name TEXT    NOT NULL CHECK (length(trim(display_name)) BETWEEN 1 AND 32),
    color        TEXT    NOT NULL CHECK (color IN (
                     'color_01','color_02','color_03','color_04','color_05','color_06','color_07','color_08',
                     'color_09','color_10','color_11','color_12','color_13','color_14','color_15','color_16')),
    status       TEXT    NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'inactive')),
    created_at   TEXT    NOT NULL,
    updated_at   TEXT    NOT NULL
);

-- ① 一个 display_name 在活跃期只有一行
--    = 「同名只有一个对象」的唯一保证（最终说明 §3 / §2.C7）
--    inactive 的历史行不占位 → 软删后同名可再建（规格 §8）
CREATE UNIQUE INDEX IF NOT EXISTS ux_nicknames_name_active
    ON nicknames(display_name) WHERE status = 'active';

-- ② 一个 color 在活跃期只属于一行 → 「不重色」的最后防线（规格 §11 / §12）
CREATE UNIQUE INDEX IF NOT EXISTS ux_nicknames_color_active
    ON nicknames(color) WHERE status = 'active';

-- 常用查询：列全表按状态（判重 SELECT 与列表查询都走索引①）
CREATE INDEX IF NOT EXISTS ix_nicknames_status ON nicknames(status, nickname_id);
```

约定说明：

- `nickname_id` 就是**系统内部唯一身份**，一旦分配永不改变（改名不改 id）。
  （rowid，稳定、可广播、可进消息、与 `messages.message_id` 一致）。
- `display_name`：`trim()` 后 `1..32` 字符；**r6 已定：NAS / API / PC / Web 统一 32**
  （Web 输入框 `maxLength` 由 16 放宽到 32；PC 写入前按 32 校验；服务层与 DDL 不变）（§2-C2 / §10 已定 13）。
- `color`：**只接受 16 个逻辑色 ID**（`color_01`…`color_16`；`CHECK ... IN (...)` 直接枚举）。
  **不存 HEX、不存主题变体** —— 显示色一律由客户端算（§4.4）。注意 `CHECK` 是**区分大小写**的
  （SQLite 默认 `BINARY`），服务层统一 `.lower()` 后再写入（`COLOR_01` → `color_01`）。
- `created_at` / `updated_at`：沿用本项目风格 —— **本地时间 ISO 字符串**
  （`db.now_iso()`，`db.py:26-27`，`"%Y-%m-%d %H:%M:%S"`）。
- **禁止的列**（最终说明 §2，硬红线）：本表**不得**出现 `owner_type` / `owner_id` / `device_id` / `client_id`。
  「谁在用这个昵称」是**客户端本地状态**（§5.2），**永远不进这张表**（R8）。
- **本地临时昵称（灰）不在这张表里**：它没有 `nickname_id`，也不占颜色池（见 §3.2.1）；
  它的颜色是服务端常量**逻辑色 ID `gray`**（基础色值 `#8A8A8A`），**不在上面那 16 个枚举里**。
- ★ **活跃昵称上限 = 16**（= 池子大小，索引② 的结构性结果）：满 16 条时新建一律
  `503 NO_AVAILABLE_COLOR`，**绝不重色**（规格 §11）。**UI 必须在接近上限时提前提示**（文案见 §4.3）：
  `active ≥ 14`（剩余 ≤ 2）显示「**共享昵称颜色即将用尽（14/16）**」（括号里用真实计数 `x/16`）；
  已满 16 显示「**已达到共享昵称上限，请删除不再使用的昵称后再添加。**」
  并置灰新建入口；服务端仍然兜底（§5.5）。
- `status` 只有 `active` / `inactive`（软删）。**没有「占用 / 锁定 / 归属」这类状态**
  —— 那是 owner 模型的残留。

### 3.2 与现有表的关联：消息快照怎么落库（最终说明 §11）

**要动 `messages`：加两列 —— `sender_nickname_id`（可空）与 `sender_color`（可空）。
`sender_name` 保持不动，**它就是 `display_name_snapshot`**（协议「只加字段」）。
⚠ **r6：`sender_color` 存的是逻辑色 ID**（`color_01`…`color_16` / `gray`），**不是 HEX** ——
列名沿用 `sender_color`（少一处概念），但语义 = `color_snapshot` 的**逻辑色**。
存 ID 的三个理由：① 客户端可按主题算出正确的显示色（跨主题一致）；② NAS 不必存浅/深两套色；
③ 快照与「昵称表那一行」用的是同一套取值，`gray` 与池内色一眼可分。**

```python
# server/db.py —— MIGRATIONS 追加（放在列表末尾，顺序执行，幂等）
("messages", "sender_nickname_id", "ALTER TABLE messages ADD COLUMN sender_nickname_id INTEGER"),
("messages", "sender_color",       "ALTER TABLE messages ADD COLUMN sender_color TEXT"),  # 值 = 逻辑色 ID（color_01…color_16 / gray）

# POST_MIGRATION_DDL 追加
"CREATE INDEX IF NOT EXISTS idx_messages_sender_nick ON messages(sender_nickname_id)",
```

| 最终说明 §11 的快照字段 | 落在哪 | 说明 |
|---|---|---|
| `nickname_id` | `messages.sender_nickname_id`（新、可空） | 共享昵称才有值；**灰临时昵称 = `NULL`**（§3.2.1） |
| `display_name_snapshot` | `messages.sender_name`（**已有，不动、不改名**） | 发送时的名字文本，写入即固化（`db.py:47` / `services/messages.py:38-42`） |
| `color_snapshot` | `messages.sender_color`（新、可空） | 发送时的**逻辑色 ID**（`color_01`…`color_16`，灰临时 = `gray`）；改造前的老行是 `NULL`（**r6：不回填**，§3.4） |
| `content` | `messages.content`（已有） | 不动 |

- **为什么可空、不加 `NOT NULL` / `DEFAULT`**：`ALTER TABLE ADD COLUMN` 对存量行只能填常量；
  给一个**假的** `nickname_id` 比 `NULL` 更糟。`NULL` = 「这条消息没有共享昵称身份」，语义干净。
- **快照是权威：读取时不 JOIN `nicknames`**（§2.C1）。改名 / 改色 / 删除昵称，历史消息**原样显示**。
  对外形状（最终说明 §11 的结构 + 本项目既有字段）：
  ```json
  {"message_id": 88, "nickname_id": 12, "nickname": "妈妈", "color": "color_12",
   "content": "吃饭了", "created_at": "2026-09-29 12:00:00", "direction": "in"}
  ```
  - `nickname` 沿用项目既有字段名（语义 = `display_name_snapshot`）；`color` = `color_snapshot`。
  - ★ **`color` 是逻辑色 ID**（`"color_12"`），**不是 HEX**：客户端拿到后按 §4.4 的映射表 + 当前主题渲染。
  - 两者都**只读本行的快照列**，不做任何反查 ——
    `sender_nickname_id` **只用于「查那一行（若还在）」，绝不参与按名字匹配**（§3.4.1）。
- **不加外键**到 `nicknames`：历史消息允许指向已软删的昵称（软删只改 `status`，行还在）；
  外键会让删除语义变复杂（`PRAGMA foreign_keys=ON` 已在 `db.py:179` 打开）。
- **不改** `sender_name` 的语义与约束（`db.py:47` 保持 `TEXT NOT NULL`）。
- **不需要** `sender_kind='web'` / `sender_device_id` 来「标识发送人」—— 设备身份属设备体系（§0.7 / R8）。
  这两个既有列**保留不动**，但昵称与颜色**不再依赖**它们。

#### 3.2.1 还没选共享昵称时的消息：灰临时昵称（**跨层设计，必须落地**）

**问题**：新安装的 PC 还没选 / 建共享昵称，它能发消息吗？发出去的消息里带什么？
**答案（用户已拍板）**：**能发；昵称就是它的默认昵称（Windows 用 `ComputerName`），颜色是灰色。**

| 问题 | 结论 |
|---|---|
| 能不能发 | **能**。服务端**不得**因为「昵称不在共享池里」而拒绝这条消息（下面的「服务端要不要放宽校验」逐条核实过） |
| `nickname_id` 填什么 | **`NULL`** —— 不是 `0`、不是负数、不是 `"local"` 之类的魔法值。理由：① `nickname_id` 的语义就是**共享昵称表的主键**，没有共享昵称就是「没有」；② `NULL` 在 SQLite / Python / JSON 三端都是天然的「无」，不需要约定；③ **改造前的老消息也是 `NULL`**（**r6：不回填**，§3.4），两者语义恰好一致（都=「没有共享身份」）→ 显示逻辑只需一套 |
| `display_name_snapshot` | 该客户端的**默认昵称**：Windows 用 `ComputerName`（`AgentConfig.cs:116-117`）；Web 用固定中性文案「默认用户」 |
| `color_snapshot` | **逻辑色 ID `gray`**（基础色值 `#8A8A8A`，见下表） |
| 灰色谁定、服务端认不认 | **服务端常量** `LOCAL_TEMP_COLOR_ID = "gray"`（`server/nicknames.py`）→ 服务端**认**它：「本地临时昵称」在协议 / 库里的**唯一合法颜色就是一个逻辑色 ID `gray`**；客户端只负责渲染 |
| DB 要不要存这个值 | **要** —— `messages.sender_color` 写字符串 `'gray'`（**逻辑 ID，不是 HEX**）；**但绝不写进 `nicknames.color`**：`gray` **不在 `COLOR_POOL` 里、也不在 DDL 的 `CHECK` 枚举里**，`pick_first_available()` 永远取不到它，服务层白名单也**拒绝**把它分配给共享昵称 |
| 历史消息怎么显示 | **灰色 + 默认昵称，照常显示**。建议**不加**文字标记 —— 灰色本身就是「非共享昵称」的视觉标记；唯一的区别是它**不出现在昵称列表页**（不在共享池里） |
| 别的客户端看到它 | **照常显示**（灰色圆点 + 名字），不需要特殊分支、不需要报错 |
| 后来选了共享昵称，旧消息要不要改写 | **不改写**（用户已确认：快照就是快照）。界面会呈现「同一个人的前几条是灰的、后面是红的」—— 这是**忠实的历史**，不是 bug |
| 两台 `ComputerName` 相同的 PC 都用灰色发消息 | **界面上会看起来像同一个人，这是可接受的**（用户已确认）：「同名 ⇒ 同一昵称」这条规则**只约束共享池**，灰色本地临时昵称**不受它约束**。真要区分只能看设备层信息（`sender_device_id`，`db.py:52`），本方案**不在昵称层做这件事** |

**灰的逻辑色 ID 定稿：`gray`**（基础色值 `#8A8A8A`；浅色主题既有令牌 `--fluent-color-text-tertiary`，
`tokens.css:103`；深色主题同名令牌是 `#9E9E9E`，`tokens.css:171`）。

`gray` 是**第 17 个逻辑色 ID**，只属于「本地临时昵称」，与池子是两个命名空间：
`COLOR_POOL` 永远不含它，`nicknames.color` 的 `CHECK` 枚举里也没有它（§3.1）。
实测（WCAG 2.1，背景取自 `tokens.css`）：

| 用途 | 基础色值 `#8A8A8A` 直接当圆点 | 按 §4.4 取主题变体（推荐，无特例） |
|---|---|---|
| 8px 圆点（非文本判据 ≥3.0） | 浅色对 `#FFFFFF` **3.45**、`#FAFAFA` 3.31 ✅ / 深色对 `#2B2B2B` **4.10**、`#272727` 4.33 ✅ | 浅 `mix(#8A8A8A,#000,35%) = #5A5A5A` → **6.90** ✅ / 深 `mix(#8A8A8A,#FFF,30%) = #ADADAD` → **6.31** ✅ |
| 32px 头像底 + 首字（≥4.5） | 白字 3.45 ✗、0.7 黑字 4.21 ✗ | 底 `mix(#8A8A8A,#000,18%) = #717171` + **白字** → **4.88** ✅ |

- **基色为什么还是 `#8A8A8A`（而不是深色主题那个 `#9E9E9E`）**：`#9E9E9E` 在浅色底上只有 **2.68**
  （<3.0，圆点基本看不见）→ 一个基色两个主题都能用，就没有必要为「浅 / 深各一份」再引入第二个 ID。
  ⚠ r5 曾担心「库里存的值与渲染出来的颜色不一致（快照要求跨主题一致）而不得不定一个值」——
  **r6 逻辑色 / 显示色分离后这个顾虑彻底消失**：库里只存 ID，客户端本来就按主题算显示色（§4.4），
  「快照跨主题一致」由「存 ID」天然保证。
- 灰**也走同一条圆点变体规则**（不设特例）：实现上就是 §4.4 表里多一行，而不是在 `chat.js` 里写 `if`。

**服务端要不要放宽校验（现状核实 → 结论：不需要放宽，只需要加可选字段）**

| 入口 | 现状（行号） | 结论 |
|---|---|---|
| `POST /api/messages` | `MessageBody` 只有 `sender_name`（1..32）/ `content` / `targets` / `message_type`（`server/main.py:346-352`），**根本没有 `nickname_id` 字段** | **现在就不会拦**。加一个**可选**字段 `nickname_id`（可空）：给了 → 必须存在且 `active`（否则 404/409）；**省略或 `null` = 灰临时**，服务端把 `sender_color` 写成 `'gray'`。**不再接受任何颜色入参**（r6） |
| 同上·颜色入参 | 客户端现在根本不能传颜色 | **r6 起一律不接受任何颜色字段**（HEX 或逻辑 ID 都不接受）：有 `nickname_id` → 抄该昵称当前的逻辑色 ID；没有 → 写常量 `'gray'`。客户端**一个颜色字节都不上行** → 从根上堵住「往页面注入任意颜色」（R2） |
| WS `reply` 帧 | `sender_name` 取帧值，为空回退设备名（`server/main.py:879-880`） | 新逻辑同 HTTP：可选 `nickname_id`（缺省 = 灰临时 → `sender_color='gray'`）；**不接受颜色入参** |
| 写入 | `services/messages.py:34-42`（网页端，`sender_kind` 写死 `'web'`、`sender_device_id=NULL`）与 `:183-197`（PC 回复，带 `sender_device_id`） | 两条 INSERT 都要加 `sender_nickname_id` / `sender_color` 两列 |

- **要改什么 / 为什么**：改的是「INSERT 的列 + 两个可选入参的校验」，**不是放宽**
  （现状本来就不校验昵称归属，所以也不存在「拦住了灰色消息」这个 bug）。
- **风险（必须测）**：`NULL` 在两种情况下都会出现 ——「灰临时消息」与「改造前的老消息」。
  若把 `NULL` 一律当灰色 → 老消息会集体变灰。
  **判定「是不是灰临时」的唯一依据 = `sender_color = 'gray'`**（显式写进去的逻辑色 ID），
  而 `sender_color IS NULL` 的老行走哈希兜底（§3.4）。这是本设计**最容易写错的一处**，Phase 1 / Phase 5 都要专门测。

### 3.3 首次安装流程 + 创建共享昵称（最终说明 §9 / §10 / §3）

**两句话**：① **新安装客户端不预建任何共享昵称**，只有灰色本地临时昵称（§9）；
② **没有「按名幂等创建」** —— 名字重复**就是被拒**（§3）。

**首次安装（一台新 PC 第一次连上 NAS）**

| 步 | 发生什么 | 存哪 |
|---|---|---|
| 1 | 客户端读 `ComputerName`（`AgentConfig.cs:116-117`） | 本地 |
| 2 | 向 NAS 请求共享昵称**整表**（`nickname_list_request` → `nickname_list_response`，最终说明 §14） | — |
| 3 | 界面立刻显示「当前昵称：`LIVINGROOM-PC` / 颜色：灰色」（**本地临时昵称**） | 本地 |
| 4 | **NAS 侧什么都不做**：不建行、不分配颜色、不占位（最终说明 §9） | — |
| 5 | 用户此后二选一（最终说明 §10）：**方式 A：选用已有的** → 本地存 `current_nickname_id`，从此用它发消息；**方式 B：创建新的** → 见下 | 本地 |
| 6 | 方式 B 若名字已存在 → **`NICKNAME_ALREADY_EXISTS`** → UI 提示「已存在，直接选用它？」；点「选用」即落到方式 A | — |

> 还没选共享昵称时**也能发消息**，走灰临时昵称 → 完整协议见 §3.2.1。

**方式 B：服务端创建（`POST /api/nicknames`）**

```python
# server/services/nicknames.py（Phase 1 落地）
def create(display_name: str) -> dict:
    """创建一个**新的**共享昵称。名字已存在 → 抛 NicknameAlreadyExists（最终说明 §3）。
       ⚠ **不是幂等**：r4 的 ensure()（按名幂等返回既有行）已作废 ——
       幂等会让「创建」与「选择」在 UI 上分不开。"""
    name = normalize(display_name)          # strip() + NFC + 拒控制字符（§5.5）
    with db.tx() as conn:                   # §6.2：一个 tx 完成「查重 + 分配颜色 + 插入」
        # ① 查重：命中就**明确报错**（不静默复用、不碰颜色池）
        row = conn.execute(
            "SELECT nickname_id FROM nicknames WHERE display_name=? AND status='active'",
            (name,)).fetchone()
        if row:
            raise NicknameAlreadyExists(row["nickname_id"])   # → 409 + 既有 id（UI 拿它去「选用」）
        # ② 同一事务里取第一个可用色（返回**逻辑色 ID**，如 'color_07' —— 不是 HEX）
        color = pick_first_available(conn)  # SELECT color FROM nicknames WHERE status='active'
        if color is None:
            raise NoAvailableColor()        # → 503；绝不重色（规格 §11）
        # ③ 插入
        conn.execute(
            "INSERT INTO nicknames (display_name, color, status, created_at, updated_at)"
            " VALUES (?,?, 'active', ?, ?)", (name, color, db.now_iso(), db.now_iso()))
        return {...}                        # 完整对象：nickname_id / display_name / color / status / …
```

```sql
-- 等价的 INSERT（少一次往返；冲突目标必须带 partial index 的 WHERE，已实测）
INSERT INTO nicknames (display_name, color, status, created_at, updated_at)
VALUES (?, ?, 'active', ?, ?)
ON CONFLICT(display_name) WHERE status='active' DO NOTHING;
```

- 注意：`ON CONFLICT(display_name)` **不带** `WHERE status='active'` 会在 SQLite 报
  `ON CONFLICT clause does not match any PRIMARY KEY or UNIQUE constraint`（因为唯一索引是部分的）。
  但**推荐 §6.2 的 `SELECT → INSERT` 版**：`DO NOTHING` 之后还得再查一次才能拿到「既有那个的 `nickname_id`」，
  而最终说明 §3 要求把**那个既有昵称**交给客户端去「选用」。
- 并发下两个请求同时创建同名：一个插入成功，另一个拿到 `IntegrityError` →
  §6.3 的重试里**翻成 `NICKNAME_ALREADY_EXISTS`**（不是重试成功），语义才不会变味。
- **「撞名 ⇒ 同一个昵称」怎么体现**：不是自动落到同一行，而是**报错 + 客户端引导选用** ——
  最终效果一样（系统里只有一行「妈妈」），但用户**知道**自己在用哪一个（最终说明 §3）。

### 3.4 存量消息要不要回填（**r6 定稿：不建议回填**）

**结论先写：不要跑回填。** 让改造前的老消息保持**两列 `NULL`**、前端走「`sender_color` → 按名字哈希兜底色」
的兜底链，观感与今天**逐像素一致**（`app.js:336-344`，零回归、零伪造）。

**回填本来想做什么**（按 `sender_name` 匹配 `active` 同名，写入那个 `nickname_id` + 该昵称**当前**的逻辑色 ID）：

```sql
-- ⚠ 仅供说明「它做了什么」，r6 不推荐执行（理由见下 + §3.4.1）
UPDATE messages SET
       sender_nickname_id = (SELECT n.nickname_id FROM nicknames n
                              WHERE n.display_name = messages.sender_name AND n.status = 'active'),
       sender_color       = (SELECT n.color FROM nicknames n
                              WHERE n.display_name = messages.sender_name AND n.status = 'active')
 WHERE sender_color IS NULL
   AND EXISTS (SELECT 1 FROM nicknames n
                WHERE n.display_name = messages.sender_name AND n.status = 'active');
```

**三条否决理由（逐条可验证）**

1. **它本质上是一次「名字 → 身份」的猜测**：改造前根本没有昵称表，那些消息**从来没有共享身份**
   （`sender_name` 只是客户端本地的字符串）。回填等于**给历史编造一个共享身份**，
   与硬规则第 8 条「绝不回溯修改历史」的精神直接冲突：冻结的意义就是「当时是什么，就永远是什么」。
2. **回填进去的逻辑色 ID 是「回填那一刻的颜色」**，不是发送当时的颜色（当时没有快照）→
   造出一批**颜色错误但看起来权威**的快照，比 `NULL`（明确表示「未知」）更糟。
3. ★ **与「重新创建同名昵称」叠起来会出错（本轮最需要说清的一处，详见 §3.4.1）**：
   `IS NULL` 只保证「**已填过的行**不会被改写」，**不保证「每次跑都只匹配当时在场的那一行」**——
   回填**跑得越晚或越多次**，越可能把老消息挂到**后来重新创建的、完全不相干的那个同名新昵称**上。
   删掉「妈妈」再重创「妈妈」之后，表里会有两行同名（旧 `inactive` + 新 `active`），
   而回填只看「名字相同 + `status='active'`」→ 老消息被挂到**新对象**，还拿到**新对象的颜色**。

**其他口径（保持 r5 已实测的结论）**

- 实测（说明回填能跑通）：`MY-PC` 命中、`妈妈` 命中、**`早就删掉的名字` 保持 `NULL`**。
- **匹配不到的怎么办**：**保持 `NULL`（唯一推荐）**；不要为了「让它们有颜色」而把一堆历史名字批量登记成
  `inactive` 昵称（表会变脏、且那些名字从来不是共享昵称）。
- ⚠ **绝不要把「`sender_color IS NULL`」当成「灰临时昵称」**：老消息也是 `NULL`。
  判定灰临时消息的唯一依据是 `sender_color = 'gray'`（§3.2.1 的风险条目）。
- 如果真的有人坚持要跑（例如明确要求「老消息也按共享昵称上色」），必须**同时**满足三条：
  ① 只在昵称表**还是第一批数据**时一次性执行（此时不存在「同名已软删 + 重创」的历史）；
  ② 接受「颜色是当时的现色」；③ 跑完**禁止任何**按 `display_name` 反查 `nickname_id` 的读取路径（§3.4.1）。
  **三条都很难保证 → 所以定稿是不跑**；也**不做** `tools/backfill_nickname_snapshot.py`。

#### 3.4.1 ★ 重新创建同名昵称（r6 新增 —— 历史冻结硬规则的必答题）

**场景**：`妈妈`（`nickname_id=12`，`color_12`）被删除（`status='inactive'`，行永久保留）；
后来用户**又新建**了一个「妈妈」（`nickname_id=20`，可能拿到 `color_05`）。此时表里
**两行同名**（12 已删 / 20 活跃），历史消息里既有 `sender_nickname_id=12` 的老消息，
也会有 `sender_nickname_id=20` 的新消息。

**结论（两条，都是硬规则）**

1. **历史照旧显示**：`nickname_id=12` 的消息读**自己行内**的快照（`sender_name='妈妈'` +
   `sender_color='color_12'`）→ **原样渲染，与重创的那条没有任何关系**。
   重创同名**不会**、也**不许**改变任何历史消息的显示（甚至不必知道旧行是否还活着）。
2. ★ **`nickname_id` 绝不能按 `display_name` 去查**（本节的核心禁令）：
   任何「按名字反查昵称行」的读取路径都会把老消息误挂到**新行**（20）上 ——
   在新对象的颜色 / 名字与原对象不同时，历史会「集体变色 / 集体改名」，正是硬规则第 8 条要禁止的事。
   ⇒ **读取历史时只读本行快照三列**；`sender_nickname_id` 只允许用于「取那一行（若还在）」（例如给 UI
   标一个「该昵称已删除」），**永不参与名字匹配**。

**为什么这条禁令是「硬」的**：`display_name` 在**同一时刻**唯一（索引①），但**跨时间**同名可以有多个
对象（每次软删 + 重创都是一个全新 `nickname_id`）。所以「名字」在时间轴上**不是身份**，
`nickname_id`（以及历史里的快照）才是（§2.C7 的边界行、规格 §2）。

**由此一并否决的两件事**

- **回填**（§3.4）：回填就是一次「按名字反查」的**批量写**，理由见 §3.4 第 3 条。
- **给 `messages` 加外键 + 读取时 JOIN `nicknames`**：外键解决不了「名字歧义」，JOIN 更是直接把禁令
  踩在脚下；本方案**不加外键**（§3.2），读取**不 JOIN**。

**怎么测**（Phase 1 / Phase 5 都要有一条）：建「妈妈」→ 发一条带 `nickname_id` 的消息 → 删「妈妈」→
**重创**「妈妈」→ 断言：① 老消息的 `sender_nickname_id` 仍是旧值、显示仍是旧名字 + 旧逻辑色 ID；
② 新消息指向新 `nickname_id`；③ 全局 grep 不到任何「按 `display_name` 查 `nicknames` 的读取路径」。

### 3.5 昵称的生命周期（**与设备无关**）

| 事件 | 处理 |
|---|---|
| 客户端首次连接 / 首次访问 | **什么都不做**（最终说明 §9）：只显示灰临时昵称（PC 用 `ComputerName`，Web 用「默认用户」）。r4 的「PC 注册建行 / Web 懒建默认用户」**已作废** |
| **选择昵称**（我以后用谁的名义发） | **只改客户端本地状态**（`current_nickname_id`）：**不动 `nicknames` 表、不广播、无任何服务端副作用**（最终说明 §8） |
| **创建共享昵称** | `POST /api/nicknames`：名字已存在 → **409 `NICKNAME_ALREADY_EXISTS`**；否则建行 + 分配**逻辑色 ID**（§3.3）。**必须在线**（r6：离线一律拒绝并明示原因，§5.2） |
| 第二个 / 第三个客户端开始使用同一个昵称 | **什么都不做** —— 没有「占用 / 归属」概念，这正是核心场景（§2.C3） |
| **修改昵称**（`妈妈 → 老妈`） | `PATCH /api/nicknames/{nickname_id}`：目标名字未被占用 → 只动 `display_name`（颜色不变）；**已被占用 → 409 `NAME_TAKEN`**；**广播给所有客户端**；**历史消息不变**（快照，§3.2）。**必须在线**（r6：完全拒绝离线改名，§5.2） |
| 颜色变更 | `POST /api/nicknames/{nickname_id}/reassign-color`：**换一个未被任何 active 占用的逻辑色 ID**（新 ID ≠ 旧 ID；池满 → `503 NO_AVAILABLE_COLOR`）；广播 `nickname_color_changed`（载荷里是逻辑色 ID）；**历史消息不变**（快照）。**必须在线**（离线时客户端拒绝执行并提示） |
| 删除昵称 | `DELETE /api/nicknames/{nickname_id}` → `status='inactive'` + `updated_at`；索引② 的 `WHERE active` 让**逻辑色 ID 立即回到可用池**（规格 §8）；广播 `nickname_removed`（带 `released_color` = 逻辑色 ID）。**必须在线** |
| **昵称被删后，仍在选用它的客户端** | **必须处理「当前昵称已失效」**（最终说明 §11）：客户端收到 `nickname_removed` 且 `nickname_id` == 自己的 `current_nickname_id` → 立刻**清空本地选用**并回退到**灰临时昵称**（§3.2.1），UI 给一句「你用的昵称已被删除，已切回本地临时昵称」；服务端在写入时若收到已 `inactive` 的 `nickname_id` → 返回 `409 NICKNAME_INACTIVE`（见 §10 已定 10） |
| ★ **重新创建同名昵称**（删了「妈妈」又建「妈妈」） | 是**新的一行 / 新的 `nickname_id`**，旧的 `inactive` 行不占位（索引①）。**历史消息不受任何影响**，仍然显示旧名字 + 旧逻辑色 ID；**绝不允许按名字反查** `nickname_id`（§3.4.1） |
| **删除设备**（`services/devices.py:129-132`） | **什么都不做**（昵称与设备解耦，R3）。r2 的「级联软删昵称」已作废 |
| 历史 `inactive` 昵称攒多了 | 不清理（软删 + 审计价值）；`GET /api/nicknames?status=inactive` 可查 |
## 4. 颜色池定稿建议（对应 §2-C5）

### 4.1 实测对比度数据（WCAG 2.1；浅 `#FFFFFF` / 深 `#2B2B2B` 取自 `tokens.css:86`/`:156`）

现有 12 色（`app.js:331-334`）：

| 色值 | 头像底+深字(0.7 黑) | 浅底圆点 | 深底圆点 | 浅底文本 | 深底文本 |
|---|---|---|---|---|---|
| `#90CAF9` | 6.41 ✅ | 1.75 ❌ | 8.09 ✅ | 1.75 ❌ | 8.09 ✅ |
| `#CE93D8` | 5.35 ✅ | 2.39 ❌ | 5.93 ✅ | 2.39 ❌ | 5.93 ✅ |
| `#80CBC4` | 6.21 ✅ | 1.87 ❌ | 7.59 ✅ | 1.87 ❌ | 7.59 ✅ |
| `#A5D6A7` | 6.67 ✅ | 1.64 ❌ | 8.61 ✅ | 1.64 ❌ | 8.61 ✅ |
| `#FFE082` | 7.56 ✅ | 1.29 ❌ | 10.95 ✅ | 1.29 ❌ | 10.95 ✅ |
| `#FFCC80` | 7.06 ✅ | 1.48 ❌ | 9.57 ✅ | 1.48 ❌ | 9.57 ✅ |
| `#EF9A9A` | 5.72 ✅ | 2.15 ❌ | 6.58 ✅ | 2.15 ❌ | 6.58 ✅ |
| `#F48FB1` | 5.60 ✅ | 2.23 ❌ | 6.34 ✅ | 2.23 ❌ | 6.34 ✅ |
| `#9FA8DA` | 5.48 ✅ | 2.31 ❌ | 6.13 ✅ | 2.31 ❌ | 6.13 ✅ |
| `#80DEEA` | 6.87 ✅ | 1.55 ❌ | 9.14 ✅ | 1.55 ❌ | 9.14 ✅ |
| `#C5E1A5` | 7.13 ✅ | 1.43 ❌ | 9.90 ✅ | 1.43 ❌ | 9.90 ✅ |
| `#FFAB91` | 6.29 ✅ | 1.83 ❌ | 7.74 ✅ | 1.83 ❌ | 7.74 ✅ |
| **达标数** | **12/12** | **0/12** | **12/12** | **0/12** | **12/12** |

规格 16 色（规格 §9 的候选）：

| 色值 | 头像底+深字 | 浅底圆点 | 深底圆点 | 浅底文本 | 深底文本 |
|---|---|---|---|---|---|
| `#5E35B1` | 2.23 ❌ | 8.02 ✅ | 1.77 ❌ | 8.02 ✅ | 1.77 ❌ |
| `#3949AB` | 2.29 ❌ | 7.73 ✅ | 1.83 ❌ | 7.73 ✅ | 1.83 ❌ |
| `#1E88E5` | 4.03 ❌ | 3.68 ✅ | 3.85 ✅ | 3.68 ❌ | 3.85 ❌ |
| `#039BE5` | 4.57 ✅ | 3.08 ✅ | 4.60 ✅ | 3.08 ❌ | 4.60 ✅ |
| `#00897B` | 3.62 ❌ | 4.32 ✅ | 3.28 ✅ | 4.32 ❌ | 3.28 ❌ |
| `#43A047` | 4.34 ❌ | 3.30 ✅ | 4.29 ✅ | 3.30 ❌ | 4.29 ❌ |
| `#7CB342` | 5.19 ✅ | 2.50 ❌ | 5.65 ✅ | 2.50 ❌ | 5.65 ✅ |
| `#C0CA33` | 6.33 ✅ | 1.79 ❌ | 7.91 ✅ | 1.79 ❌ | 7.91 ✅ |
| `#F9A825` | 6.05 ✅ | 1.97 ❌ | 7.18 ✅ | 1.97 ❌ | 7.18 ✅ |
| `#FB8C00` | 5.43 ✅ | 2.37 ❌ | 5.97 ✅ | 2.37 ❌ | 5.97 ✅ |
| `#F4511E` | 4.26 ❌ | 3.48 ✅ | 4.07 ✅ | 3.48 ❌ | 4.07 ✅ |
| `#E53935` | 3.71 ❌ | 4.23 ✅ | 3.35 ✅ | 4.23 ❌ | 3.35 ✅ |
| `#D81B60` | 3.32 ❌ | 4.95 ✅ | 2.86 ❌ | 4.95 ✅ | 2.86 ❌ |
| `#8E24AA` | 2.49 ❌ | 7.04 ✅ | 2.01 ❌ | 7.04 ✅ | 2.01 ❌ |
| `#6D4C41` | 2.31 ❌ | 7.61 ✅ | 1.86 ❌ | 7.61 ✅ | 1.86 ❌ |
| `#546E7A` | 3.02 ❌ | 5.40 ✅ | 2.62 ❌ | 5.40 ✅ | 2.62 ❌ |
| **达标数** | **5/16** | **12/16** | **10/16** | **6/16** | **5/16** |

（判据：正文/小字 ≥4.5；非文本图形 ≥3.0。`✅/❌` 按该列对应判据。）

### 4.2 定稿：就用这 16 个基础色值，配四条渲染规则（逐色实测后的结论）

**池子 = 最终说明 §5 给的这 16 个 Material 色**（不再保留 r4 的「扩 12 色到 16」方案）。
**r6：池子按逻辑色 ID 管理**（`color_01`…`color_16`，§4.3），下面这些 HEX 只是每个 ID 的**基础色值**；
客户端按主题把它们变成**显示色**（§4.4，这一步**不进协议、不进库**）。
但**基础色值直接用会出问题**，下面是实测后的具体结论（不是抄需求）：

**① 圆点（8px，非文本判据 ≥3.0）：基础色值直接用会漏 4 个浅底 / 6 个深底**

| 主题 | 背景（`tokens.css`） | 基础色值直接达标 | 不达标的色 |
|---|---|---|---|
| 浅色 | `#FFFFFF`(:86) / `#FAFAFA`(:87) / `#F0F0F0`(:88) | **12/16** | `#7CB342` 2.50、`#C0CA33` 1.79、`#F9A825` 1.97、`#FB8C00` 2.37（高亮黄绿/橙） |
| 深色 | `#2B2B2B`(:156) / `#272727`(:157) / `#333333`(:158) | **10/16** | `#5E35B1` 1.77、`#3949AB` 1.83、`#D81B60` 2.86、`#8E24AA` 2.01、`#6D4C41` 1.86、`#546E7A` 2.62（深紫/棕） |

→ **规则 1（圆点按主题取变体）**：浅色模式 `dot = mix(color, #000000, 35%)`；深色模式 `dot = mix(color, #FFFFFF, 30%)`。
   实测：**两个主题都 16/16 达标**。**身份色是逻辑色 ID**（DB 里、协议里、`color_snapshot` 里存的都是
   `color_0x`，**不是 HEX**），HEX 与变体**只在客户端渲染时算**，**不进协议、不进库**（§4.4）。

**② 头像首字（32px，≥4.5）：写死 0.7 黑字会漏 11 个**

- 现状 `style.css:878-879` / `pc.css:181-184` **写死** `color: rgba(0,0,0,0.7)`；
- 16 个基础色值里配 0.7 黑字达标只有 **5/16**（`#039BE5` 4.57、`#7CB342` 5.19、`#C0CA33` 6.33、`#F9A825` 6.05、`#FB8C00` 5.43）；
- 配白字达标有 **6/16**（`#5E35B1` 8.02、`#3949AB` 7.73、`#D81B60` 4.95、`#8E24AA` 7.04、`#6D4C41` 7.61、`#546E7A` 5.40）；
- **剩下 5 个两头都不达标**（都卡在 3.3–4.3）：`#1E88E5`（0.7黑 4.03 / 白 3.68）、`#00897B`（3.62 / 4.32）、
  `#43A047`（4.34 / 3.30）、`#F4511E`（4.26 / 3.48）、`#E53935`（3.71 / 4.23）。

→ **规则 2（字色自适应）**：`对比度(白字, color) ≥ 4.5` → 用白字；否则 `对比度(0.7黑字, color) ≥ 4.5` → 用 0.7 黑字。

→ **规则 3（两头都不达标 → 头像底取变体）**：头像底改用 `mix(color, #000000, 18%)` + **白字**。实测全部 ≥4.5：

| 基础色值 | 头像底变体（18% 黑） | 白字对比 |
|---|---|---|
| `#1E88E5` | `#1970BC` | 5.14 ✓ |
| `#00897B` | `#007065` | 5.99 ✓ |
| `#43A047` | `#37833A` | 4.70 ✓ |
| `#F4511E` | `#C84219` | 4.94 ✓ |
| `#E53935` | `#BC2F2B` | 5.87 ✓ |
| `#8A8A8A`（灰） | `#717171` | 4.88 ✓ |

- 规则 2 / 3 必须落成**一个共用函数**（如 `nickColorFor(kind, color)`，`kind ∈ {dot, avatarBg, text}`），
  由 `chat.js` / `shell.js` 共用，CSS 两处把写死的 `rgba(0,0,0,0.7)` 换成 `var(--chat-nick-fg)`
  —— 否则会出现「网页端对、PC 本地页错」（R7）。

**③ 昵称下拉项的文本色（`app.js:384`）：用色值当字色，10/16 不达标**

→ **规则 4**：不用色值当字色；改成「色块 + 常规字色 `--fluent-color-text`」。这处**不需要变体**，换实现即可。

### 4.3 逻辑色池的落法（与规则 1–4 配套；r6：池子按逻辑色 ID 定义）

```python
# server/nicknames.py（新增）—— **逻辑色池的唯一定义点**（规格 §9「颜色池集中定义在 NAS 服务端」）

# ① 「逻辑色 ID → 基础色值」常量表：书写顺序即分配优先级（规格 §10）
#    入库 / 协议 / 广播 / 消息快照里出现的**只有左边的 ID**；
#    右边的 HEX 是客户端算显示色的起点（§4.4），**不进协议、不进库**。
LOGICAL_COLORS: dict[str, str] = {
    "color_01": "#5E35B1", "color_02": "#3949AB", "color_03": "#1E88E5", "color_04": "#039BE5",
    "color_05": "#00897B", "color_06": "#43A047", "color_07": "#7CB342", "color_08": "#C0CA33",
    "color_09": "#F9A825", "color_10": "#FB8C00", "color_11": "#F4511E", "color_12": "#E53935",
    "color_13": "#D81B60", "color_14": "#8E24AA", "color_15": "#6D4C41", "color_16": "#546E7A",
}
COLOR_POOL: tuple[str, ...] = tuple(LOGICAL_COLORS)     # 分配顺序 = 上面的书写顺序
LOCAL_TEMP_COLOR_ID = "gray"      # 本地临时昵称的逻辑色（基础色值 #8A8A8A）—— **不在池里**（§3.2.1）
COLOR_POOL_VERSION  = 1           # 池子语义版本：换 ID / 改基础色值时必须 +1，
                                  # 写进 /api/config 与 nickname_list_response（客户端据此判断映射表是否落后）
MAX_ACTIVE_NICKNAMES = len(COLOR_POOL)   # = 16：活跃昵称上限（§3.1）

def pick_first_available(conn) -> str | None: ...   # 查 active 占用 → 返回第一个没被占的**逻辑色 ID**
def is_valid_shared_color(cid: str) -> bool: ...     # 服务层白名单：必须 ∈ COLOR_POOL（"gray" → False）
```

- **占用查询**：`SELECT color FROM nicknames WHERE status='active'` —— **一张表就够**
  （r3 颜色归属层已作废，§2.C4 末尾）。
- **不入库成表**：池子是常量，不是业务数据。放 DB 就要为「改池子」写迁移，还会与
  `ux_nicknames_color_active` 的既有数据打架。放 Python 常量 → 改池子 = 改一行、可单测、可回退。
- ★ **入库的是 ID、不是 HEX**：`nicknames.color` 的 `CHECK` 直接枚举这 16 个 ID（§3.1）→
  **「颜色一定取自池内」由 DDL 保证**（旧版 `GLOB` 只管 HEX 格式，r6 起约束更硬）。
- **灰不进池**：`LOCAL_TEMP_COLOR_ID = "gray"` 与 `COLOR_POOL` 是两个命名空间 ——
  `pick_first_available()` 永远不返回 `"gray"`，`is_valid_shared_color("gray")` 返回 `False`（§3.2.1）。
- 「按固定顺序取第一个可用」= 可预测、可测试（规格 §10），判据见 §7 Phase 1。
- ★ **活跃上限 = 16**（逻辑色在 active 昵称之间唯一 ⇒ 同一时刻最多 16 条活跃昵称）：
  满 16 条时新增返回 `503 NO_AVAILABLE_COLOR`，**绝不回退到重色 / 哈希**（规格 §11）。
- ★ **额度提示文案（用户给定，逐字使用；前端按 `active` 计数 `x` 提示）**：
  - `x ≥ 14`（剩余 ≤ 2）：**「共享昵称颜色即将用尽（14/16）」**（括号内用真实计数，如 `15/16`）；
  - `x = 16`（满）：**「已达到共享昵称上限，请删除不再使用的昵称后再添加。」**，并把新建入口置灰；
    此后再提交也只会拿到 503（服务端仍然兜底，见 §5.5）。
  - 计数来源 = `GET /api/nicknames?status=active` 的列表长度，**不需要新增端点**；文案在
    「新建昵称」入口 + 昵称列表页各出现一次（PC 设置页与 Web 设置页同文案）。

### 4.4 ★ 客户端渲染契约：逻辑色 ID → 显示色（PC / Web 两端共用，r6 新增）

**这是 r6 拍板第 ④ 条的落地**：NAS 只发**逻辑色 ID**，客户端按 `theme + ID` 算出最终 CSS / ARGB 颜色。
**两端必须实现同一张表**（PC 本地页 / 全屏弹窗 与 网页版共用同一份算法：`chat.js` / `shell.js` / `app.js`），
任何一端偏差都会立刻表现成「网页端一个色、PC 弹窗另一个色」（R7 的成因）。

**四条规则（与 §4.2 一一对应）**

1. **`dot`**（8–10px 圆点）：浅色主题 `mix(base, #000000, 35%)`；深色主题 `mix(base, #FFFFFF, 30%)`。
2. **`avatarBg` / `avatarFg`**：**优先原色底** —— `对比(白字, 底) ≥ 4.5` → 白字；
   否则 `对比(0.7 黑字, 底) ≥ 4.5` → 0.7 黑字（`0.7 黑字` 指 `rgba(0,0,0,0.7)`
   叠在该底色上的实色，即 `round(底色 × 0.3)`）。
3. **两头都不达标**（5 个色：`color_03` / `color_05` / `color_06` / `color_11` / `color_12`）→
   底取 `mix(base, #000000, 18%)` + **白字**。
4. **文本 / 下拉项不用色值当字色**：改成「色块 + 常规字色 `--fluent-color-text`」（`app.js:384`）。

**映射表**（`mix(a,b,p)` = 按权重 `p` 线性混合，逐通道取整；对比度 = WCAG 2.1，
浅色表面 `#FFFFFF`、深色表面 `#2B2B2B`，取自 `tokens.css:86` / `:156`）

| 逻辑色 ID | 基础色值 | 浅色圆点（35% 黑） | 对比 | 深色圆点（30% 白） | 对比 | 头像底 | 头像字色 | 底↔字对比 |
|---|---|---|---|---|---|---|---|---|
| `color_01` | `#5E35B1` | `#3D2273` | 12.54 | `#8E72C8` | 3.63 | 原色 | 白字 | 8.02 |
| `color_02` | `#3949AB` | `#252F6F` | 12.25 | `#7480C4` | 3.79 | 原色 | 白字 | 7.73 |
| `color_03` | `#1E88E5` | `#145895` | 7.34 | `#62ACED` | 5.83 | `#1970BC`（18% 黑） | 白字 | 5.14 |
| `color_04` | `#039BE5` | `#026595` | 6.37 | `#4FB9ED` | 6.40 | 原色 | 0.7 黑字 | 4.62 |
| `color_05` | `#00897B` | `#005950` | 8.25 | `#4CACA3` | 5.22 | `#007065`（18% 黑） | 白字 | 5.99 |
| `color_06` | `#43A047` | `#2C682E` | 6.72 | `#7BBC7E` | 6.30 | `#37833A`（18% 黑） | 白字 | 4.70 |
| `color_07` | `#7CB342` | `#51742B` | 5.41 | `#A3CA7B` | 7.61 | 原色 | 0.7 黑字 | 5.19 |
| `color_08` | `#C0CA33` | `#7D8321` | 4.09 | `#D3DA70` | 9.46 | 原色 | 0.7 黑字 | 6.33 |
| `color_09` | `#F9A825` | `#A26D18` | 4.43 | `#FBC266` | 8.77 | 原色 | 0.7 黑字 | 6.05 |
| `color_10` | `#FB8C00` | `#A35B00` | 5.18 | `#FCAE4C` | 7.62 | 原色 | 0.7 黑字 | 5.43 |
| `color_11` | `#F4511E` | `#9F3514` | 7.02 | `#F78562` | 5.72 | `#C84219`（18% 黑） | 白字 | 4.94 |
| `color_12` | `#E53935` | `#952522` | 8.19 | `#ED7472` | 4.95 | `#BC2F2B`（18% 黑） | 白字 | 5.87 |
| `color_13` | `#D81B60` | `#8C123E` | 9.25 | `#E45F90` | 4.27 | 原色 | 白字 | 4.95 |
| `color_14` | `#8E24AA` | `#5C176E` | 11.65 | `#B066C4` | 3.74 | 原色 | 白字 | 7.04 |
| `color_15` | `#6D4C41` | `#47312A` | 12.06 | `#99827A` | 3.93 | 原色 | 白字 | 7.61 |
| `color_16` | `#546E7A` | `#37484F` | 9.54 | `#879AA2` | 4.84 | 原色 | 白字 | 5.40 |
| **`gray`**（本地临时，不在池） | `#8A8A8A` | `#5A5A5A` | 6.90 | `#ADADAD` | 6.31 | `#717171`（18% 黑） | 白字 | 4.88 |

- **判据**：两个圆点列（非文本图形）≥ **3.0** → **16/16 + 灰 全部达标**；
  头像「底↔字」列（小字）≥ **4.5** → **16/16 + 灰 全部达标**。
- **`gray` 也走同一条规则**（不设特例）；即使直接拿基色 `#8A8A8A` 当圆点也已达标（3.45 / 4.10，§3.2.1）。
- **两端实现要求**：写成一个**共用函数**（建议 `nickDisplay(kind, colorId, theme)`，
  `kind ∈ {dot, avatarBg, avatarFg}`），由 `chat.js` / `shell.js` / `app.js` 共用（§2.C1 的四个消费点）；
  CSS 里写死的 `rgba(0,0,0,0.7)` 换成变量（`style.css:878-879` 与 `pc.css:181-184` 两处**同值**约束一起改）。
- **未知逻辑色 ID**（比 `pool_version` 新的 ID、脏数据）→ **兜底**：按 `gray` 渲染（或该条走哈希兜底），
  **绝不把收到的字符串直接塞进 `style`**（R2）。
- **对比度复核**：本表由脚本按 WCAG 2.1 重算，与 §4.1 的 16 色原表**判定完全一致**
  （唯一差异：`color_04` 的 0.7 黑字 4.62 vs §4.1 的 4.57，来自 α 合成的取整方式，不影响任何判定）。

## 5. API 设计

### 5.1 通用约定

- 全部挂在现有 FastAPI app（`server/main.py`），**新增文件 `server/services/nicknames.py`**（业务）
  + `server/nicknames.py`（逻辑色池与分配算法）。
- 鉴权沿用 `dependencies=[WebAuth]`（`main.py:114-123`）；权限常量建议新增
  `NICKNAME_MANAGE = "nickname.manage"`（`server/permissions.py:24-38` 的既有风格），
  并加进 `/api/config` 返回的 `permissions`（`main.py:172`），前端据此隐藏按钮。
- **没有 `owner_id` / `owner_type` 参数，也没有 `NOT_YOURS` 这类「不是你的」错误**（§0.6 / R8）：
  昵称是**全局对象**，能改它的唯一凭据是家庭口令（+ 将来的 `nickname.manage` 权限）。
- 错误返回：沿用 `HTTPException(status, detail)` 既有风格（`main.py:198`、`:659`）。
  建议 `detail = {"code": "NAME_TAKEN", "message": "…"}`，并把 `web/static/app.js:64-68` 改成
  `err.code = detail.code`（Phase 3 一起改，2 行）。
- 时间字段一律 `db.now_iso()`（`db.py:26-27`）；`nickname_id` 一律整数（§10 已定 1）。
- ★ **凡出现 `color` 的地方（HTTP 响应 / 广播 / 设备帧 / 消息快照）一律传逻辑色 ID 字符串**
  （`color_01`…`color_16`；本地临时 = `gray`）—— **协议里不出现 HEX**（r6 / §4.4）。
- ★ **「改名」与「换个昵称用」是两件完全不同的事**，UI 与 API 都必须分开表达：

  | 用户想做的事 | 调什么 | 影响面 |
  |---|---|---|
  | **改昵称本身的名字**（「妈妈」→「老妈」） | `PATCH /api/nicknames/{nickname_id}` | **全局**：所有使用它的客户端都跟着改；`nickname_id` 不变 → 历史消息仍指向同一身份 |
  | **我（这个客户端）以后用别的昵称** | 只改客户端本地选用状态（Web：浏览器 `localStorage` 的 `fm.lastSender` 存 `nickname_id`；PC：本地配置的 `current_nickname_id`）—— **没有服务端端点** | **只影响这个客户端**；`nicknames` 表一个字节都不动 |

  → 需求 §4「PC 离线修改自己的昵称」**已作废**（r6 拍板：**完全拒绝离线改昵称**）——
    理由与替代做法见 §5.2 末。
### 5.2 选择 ≠ 修改（最终说明 §8 —— 单独一节，因为这是最容易弄混的地方）

| 动作 | 是什么 | 存哪 | NAS 被改了吗 | 别人能看到吗 |
|---|---|---|---|---|
| **选择昵称**（「我以后用谁的名义发」） | **客户端本地状态**，只存一个 `current_nickname_id`（也可以是「无」= 灰临时） | **客户端本地**：PC → 本地配置（`%APPDATA%\FamilyAgent\`，由今天的 `reply_names` / `reply_name` 演化，`AgentConfig.cs:44-50`）；Web → 浏览器 `localStorage`（由 `fm.lastSender` 演化，`app.js:1089` / `:1119`） | **没有**（一个字节都不改） | **不能**（不广播） |
| **修改昵称**（「妈妈 → 老妈」） | 改**全局共享昵称本身** | NAS 的 `nicknames` 表 | **改了** —— 所有使用者一起变 | **能**（广播 `nickname_updated`） |

- **「修改我这台 PC 的昵称」这个概念不存在**（最终说明 §8）：PC 没有「自己的昵称」，
  它只是「当前用着某个共享昵称」。所以设置页不该有「本机昵称」输入框 ——
  只有「我当前用：妈妈」+「管理共享昵称（**全局**）」两块，后者的文案要写明**会影响所有使用者**。
- **选择昵称不需要任何接口**：客户端本地写一个 id 即可。
  因此 r4 设计、**r5 明确不做**的 `GET` / `PUT /api/web/sender` 两个端点**取消**（Web 不再往 NAS 存「当前选中谁」），
  §2.C3 表格 / §5.3 一并按此收敛。
- ★ **能不能离线做**（需求 §4 / 规格 §17；**r6 定稿：完全拒绝离线改昵称**）：
  - **可离线**：**只有「选择昵称」**（切 `current_nickname_id`）—— 它是纯客户端本地状态，
    本来一个字节都不上服务端，天然可以离线改（§5.2 上表）。
  - **不可离线（四个管理写操作全部拒绝）**：**新建 / 改名 / 删除 / 重新分配颜色** ——
    离线时 UI 必须**禁用入口并明示原因**（文案例：「当前未连接服务器，昵称管理不可用」），
    **不本地生效、不入队、不生成 pending、不在重连时补发**；断线后即使用户在别处改了本地文件也不补发。
  - **为什么定成这样（三条）**：① 昵称是**全局共享对象**，离线改名多出一个「重连时名字已被别人占用」的
    二义状态（旧 P7 就是为它写的），**直接不允许就根本没有这个状态**；② 客户端没有颜色池的权威，
    离线分配颜色本来就是错的（规格 §5）；③ 少一套状态机（`synced` / `pending`），
    也少一处「配置损坏回默认把 pending 悄悄吞掉」的坑（§8.2）。
  - ⇒ **r4 / r5 的「离线 pending 昵称同步」整套作废**：`nickname_sync_state = pending`、离线改名队列、
    `hello` / 心跳里的 pending 补发**全部删除**（附录 B B16）。**重连补发只保留消息收发的既有机制，
    与昵称无关**（§5.4 的限制注）。
- **在线时改名撞名** → **409 `NAME_TAKEN`**（不静默覆盖、不自动加后缀），UI 引导「换个名字 / 直接选用既有的」
  （规格 §17 的语义收敛到这一步）。
- **昵称被删（`inactive`）时**：客户端必须处理「**当前昵称已失效**」—— 清空本地 `current_nickname_id`
  并回退到**灰临时昵称**（§3.5 / §10 已定 10）。
### 5.3 端点清单（**全部是全局操作** —— 最终说明 §7）

| # | 方法 | 路径 | 请求体 / query | 成功响应 | 错误 |
|---|---|---|---|---|---|
| 1 | GET | `/api/nicknames` | query `status=active\|inactive\|all`（默认 `active`） | `200 {nicknames:[ {...}, ... ]}`（按 `nickname_id` 升序）。**空表就返回空数组，不创建任何东西**（§0.9） | 422（非法 `status`） |
| 2 | POST | `/api/nicknames` | `{display_name}` | `201 {nickname:{...}}`（颜色由服务端分配） | 422 `INVALID_DISPLAY_NAME`、**409 `NICKNAME_ALREADY_EXISTS`**（响应带 `existing_nickname_id`）、503 `NO_AVAILABLE_COLOR` |
| 3 | PATCH | `/api/nicknames/{nickname_id}` | `{display_name}` | `200 {nickname:{...}}`（`nickname_id` 不变、颜色不变） | 404 `NICKNAME_NOT_FOUND`、409 `NICKNAME_INACTIVE`、422、409 `NAME_TAKEN` |
| 4 | DELETE | `/api/nicknames/{nickname_id}` | — | `200 {ok:true, nickname_id, released_color}` | 404、409 `NICKNAME_INACTIVE` |
| 5 | POST | `/api/nicknames/{nickname_id}/reassign-color` | — | `200 {nickname:{nickname_id, display_name, color}}`（新色**必与旧色不同、且未被任何 active 占用**） | 404、409 `NICKNAME_INACTIVE`、**503 `NO_AVAILABLE_COLOR`** |

- **谁能调**：**任何客户端**（PC / Web / 将来的 Android），一律 `WebAuth` +（可选）`nickname.manage` 常量。
  **没有「谁创建的才能改」这种东西**（最终说明 §7）。
- **没有带 owner / 设备维度的路径**：不需要 `/api/nicknames/web` 这类子资源，也不需要「管理端专用路径」
  —— 所有路径本来就是全局的（r2 的 `/api/nicknames/web` 已作废，见附录 B）。
- **「选择昵称」没有任何端点**（§5.2）：那是客户端本地状态，不进 NAS。
- ★ **响应 / 请求里的 `color` 都是逻辑色 ID**（`color_01`…`color_16`；灰 = `gray`），**不是 HEX**（§4.4）；
  `POST /api/nicknames/{id}/reassign-color` 的语义 = **换一个未被任何 active 占用的逻辑色 ID**
  （新 ID ≠ 旧 ID；池满 → `503 NO_AVAILABLE_COLOR`，`detail.message` 直接用满额文案，§4.3）。
- **`POST` 的语义**（最终说明 §3）：**新建成功 = `201`**；**名字已存在 = `409 NICKNAME_ALREADY_EXISTS`**
  （响应里带 `existing_nickname_id`，UI 直接拿它做「选用」）。
  ⚠ 别和 `409 NAME_TAKEN` 混：**创建**撞名 = `NICKNAME_ALREADY_EXISTS`（引导「选用既有的」）；
  **改名**撞名 = `NAME_TAKEN`（引导「换个名字」）—— 两者的 UI 引导不同。
- **`GET /api/nicknames` 不创建任何东西**（r4 的「空表懒建默认用户」已作废，§0.9）：
  空就是空，客户端这时用灰临时昵称发消息（§3.2.1）。
- 列表页（规格 §19）直接消费第 1 行；`status=inactive` 供排查 / 审计（不做专门 UI，§9）。
### 5.4 PC 端端点：**建议走 WS 帧，不新增 HTTP 依赖**

理由（R1 / R5）：`/ws/device` 连接已经用设备 token 鉴权（`main.py:744-762`），**传输通道现成**
（`ConnectionManager.cs:478-486`、`Outbox.cs:27-32`、`PROTOCOL.md:157-168`），
而给 Core 新加一个 HTTP 客户端（`FamilyAgent.Core.csproj` 现在是**零 PackageReference**）是多余的 ——
这就是用户 r6 拍板第 ② 条：**走设备帧，不新增 HTTP 写接口**。

> ★ **r6 重要限制：通道自带的「离线队列 / 重连补发」能力不适用于昵称写操作。**
> 拍板第 ③ 条（**完全拒绝离线改昵称**）优先于第 ② 条里对通道能力的描述：
> 昵称帧**只在线发送** —— 离线时客户端**根本不构造请求**；发送走**直接 `Send`**
> （**不进 `Outbox`**、不落盘、不补发），失败就回 UI 提示（§5.2 / §7 Phase 4）。
> 「沿用离线队列 / 重连补发」这句话只对**消息收发**成立。

> **注意**：走设备连接**只是传输通道的选择**，不代表昵称「属于」这台设备（§0.6 / R8）。
> 帧里带的 `device_id` 只用于鉴权与路由，**不进入昵称模型**。

上行帧（设备 → NAS）：

| type | 字段 | 语义 | 响应 |
|---|---|---|---|
| `nickname_list_request` | — | 启动 / 重连后拉**整表**校正本地缓存（最终说明 §14） | `nickname_list_response` |
| `nickname_create_request` | `display_name` | **创建新的共享昵称**（方式 B，§3.3）；名字已存在 → `NICKNAME_ALREADY_EXISTS`；**必须在线** | `nickname_list_response` / `nickname_error` |
| `nickname_rename_request` | `nickname_id`, `display_name` | **改名**（**全局**操作，影响所有使用者）；**必须在线**（r6：离线不发送、不排队、不补发，§5.2） | 同上 |
| `nickname_reassign_color_request` | `nickname_id` | **重新分配颜色**（= 换一个未被占用的逻辑色 ID；规格 §11）；**必须在线**，离线时 PC 拒绝执行并提示 | 同上 |
| `nickname_delete_request` | `nickname_id` | 删除（软删）；**必须在线** | 同上 |

> ⚠「**选择昵称**」**不在这里**：它是客户端本地状态（§5.2），**一个帧都不发**（也因此它是唯一能离线做的动作）。
> ⚠ 表里**五个上行帧全是在线操作**（r6）：离线时客户端一律不发送（§5.2）。

下行帧（NAS → 设备）：

| type | 字段 | 说明 |
|---|---|---|
| `nickname_list_response` | `nicknames: [ {...} ]`（`status=active` 全量，`color` 是逻辑色 ID）、`pool_version`（= §4.3 的 `COLOR_POOL_VERSION`，客户端据此判断自己的映射表是否落后） | 最终说明 §14「整表同步管一致性」。**必须全量**：PC 弹窗右侧要显示别人的消息（`main.py:309-312`），没有全量就只能哈希。**不再有「本机那一条」** —— 「本机现在用哪个」由 PC 本地配置决定（§5.2） |
| `nickname_error` | `code`, `message`, `request`（回显发起帧的 type）, `existing_nickname_id?` | 如 `NO_AVAILABLE_COLOR` / `NICKNAME_NOT_FOUND` / `NAME_TAKEN` / `NICKNAME_ALREADY_EXISTS` / `NICKNAME_INACTIVE` |

**消息帧也一并新增快照字段**（`nickname_id` / `nickname` / `color`，§3.2）：下行 `message` 帧与
`history_response` 都带上，老帧缺字段要能容忍（`HistoryEntry.cs:31` 的 `TryGetProperty` 范式）。

广播帧（NAS → 所有在线客户端；Web 走 `HUB.broadcast_web`，PC 走遍历 `HUB.devices` 的 `send_to_device`）
—— **载荷统一 `{"type": ..., "nickname": {...完整对象...}}`（最终说明 §13）**：

```json
{"type":"nickname_created","nickname":{"nickname_id":21,"display_name":"妈妈","color":"color_12",
  "status":"active","created_at":"2026-09-29 12:00:00","updated_at":"2026-09-29 12:00:00"}}
{"type":"nickname_updated","nickname":{"nickname_id":21,"display_name":"老妈","color":"color_12",
  "status":"active","updated_at":"2026-09-29 12:05:00"}}
{"type":"nickname_color_changed","nickname":{"nickname_id":21,"display_name":"老妈","color":"color_14",
  "status":"active","updated_at":"2026-09-29 12:06:00"}}
{"type":"nickname_removed","nickname":{"nickname_id":21,"display_name":"老妈",
  "status":"inactive","updated_at":"2026-09-29 12:07:00"},"released_color":"color_14"}
{"type":"nickname_list_sync","nicknames":[{"nickname_id":21,"display_name":"老妈","color":"color_14"},
  {"nickname_id":22,"display_name":"爸爸","color":"color_02"}],"pool_version":1}
```

- 五个事件的职责（最终说明 §13 / §14）：前四个是**增量实时事件**（客户端据此重画，§5.6.3）；
  `nickname_list_sync` 是**整表下发**（新客户端连上、或客户端主动 `nickname_list_request` 时用它对齐整表）。
  **广播管实时、整表同步管一致性 —— 客户端之间不互相同步。**
- ★ **载荷里的 `color` 与 `released_color` 都是逻辑色 ID**（`color_0x` / `gray`），**不是 HEX**（r6 / §4.4）。
- `nickname_removed` 额外带 `released_color`（规格 §8 的「释放」要看得见，前端可据此提示）。
- **载荷里没有任何 owner / 设备字段**：连 `device_id` 都没有（§0.7 / R8）。
- **广播范围**：所有 `/ws/web` 订阅者 + **所有已连接设备**（不只是发起者）。
  因为别人的弹窗里也有这个昵称的历史消息 —— 名字/颜色得跟着变（规格 §13「所有在线客户端收到后更新本地缓存」）。
  实现参照 `main.py:319-340` `_broadcast_message()`。→ 完整回答见 **§5.6**。
- **不要**给「客户端换了选用昵称」发广播：那是客户端本地状态（§5.2），广播它会造成
  「别人也在换昵称」的错觉。
- **协议文档必须同步更新**：`docs/PROTOCOL.md` §7（`:172-186` 加下行帧）+ 新增「昵称协议」一节，
  并把 `:23-24`、`:197`、`:251` 三处「昵称是纯本地概念 / 服务端不存昵称表」的**过时论断改掉**。
### 5.5 参数校验（服务端，逐个明确）

| 字段 | 规则 | 违规返回 |
|---|---|---|
| `display_name` | **r6 定稿：全端统一 32** —— `trim()` 后 `1..32` 字符（NAS 服务层 / DDL / HTTP / WS 帧 / PC / Web 输入框同一上限）；**禁止控制字符**（`\n` / `\r` / `\t` 一律拒）；入库前 `unicodedata.normalize('NFC', …)`；**同一时刻不得与已有 `active` 昵称重名**（DDL 索引① 强制） | 422 `INVALID_DISPLAY_NAME`；**创建**撞名 → 409 `NICKNAME_ALREADY_EXISTS`；**改名**撞名 → 409 `NAME_TAKEN` |
| `nickname_id`（昵称接口） | 必须存在且 `status='active'`（`PATCH` / `DELETE` / `reassign-color` 均适用）；已 `inactive` → `NICKNAME_INACTIVE` | 404 `NICKNAME_NOT_FOUND` / 409 `NICKNAME_INACTIVE` |
| `color`（昵称接口） | 共享昵称的颜色**只能由服务端从 `COLOR_POOL` 取一个未被占用的逻辑色 ID**；请求体里**不接受** color（HEX 或 ID 都不接受） | 一律忽略（不报错，但绝不采纳） |
| `nickname_id`（消息） | **可空**：`null` / 省略 = **灰临时昵称**（§3.2.1）；给了就必须是 active 共享昵称 | 404 / 409 `NICKNAME_INACTIVE` |
| `color`（消息） | **r6 起彻底不接受**：消息颜色**只能**由服务端决定 —— 有 `nickname_id` → 抄该昵称当前的**逻辑色 ID**；没有 → 写常量 `'gray'`。客户端上传任何颜色字段（HEX 或 ID）一律忽略（R2：不许把颜色写进 inline style） | 忽略 + 用服务端值 |
| **活跃昵称上限** | `active` 昵称数 = 16（= `len(COLOR_POOL)`）时新建 → `NoAvailableColor`；`GET` 列表长度即计数（§4.3） | `503 NO_AVAILABLE_COLOR`，`detail.message` 用满额文案「已达到共享昵称上限，请删除不再使用的昵称后再添加。」 |
| `status`（query） | ∈ `{active, inactive, all}` | 422 |
| ~~`owner_id` / `owner_type` / `device_id` / `client_id`~~ | **最终说明 §2 明令禁止**：`nicknames` 表与协议里都不许出现。请求体里出现 → 一律忽略；**DDL 里出现就是 bug** | — |
### 5.6 广播实时性：三个浏览器同时在线时，颜色怎么同步

**问题**：`电脑浏览器 A` 把「妈妈」从绿色重新分配成紫色，`B` 与`手机浏览器`能不能立刻变紫？

#### 5.6.1 现在 `server/hub.py` 的广播发给了谁（读码结论）

| 事实 | 依据 |
|---|---|
| Web 订阅者就是一个**集合**：`self.web_clients: set[WebSocket]` | `server/hub.py:28` |
| `broadcast_web()` **遍历整个集合**逐个 `send_json`，写失败的从集合里摘掉 | `server/hub.py:179-187`（`:181` 遍历、`:183` 发送、`:186-187` 清理死连接） |
| 谁会被加进这个集合：每一个连上 `/ws/web` 的浏览器各算一个成员 | `server/main.py:965-986`（`:974` `HUB.add_web(websocket)`、`:986` `remove_web`） |
| `/ws/web` 只校验家庭口令，**不区分浏览器**（没有 session/身份/连接语义） | `server/main.py:967-972` |

→ **一句话：`broadcast_web()` 发给了「所有已连接的 `/ws/web` 订阅者」，不区分是哪个浏览器。
所以三端都能收到这件事，传输层今天就已经满足；缺的不是通道，是「没有这个广播的发送方」
（`main.py` 现有 9 种 `broadcast_web` 类型里没有任何 `nickname_*`，清单见 §1.5）。**

对照：发给设备走 `HUB.send_to_device()`（`hub.py:150-169`），「全体设备、排除发起者」的既有写法是
`main.py:319-340` `_broadcast_message()`（遍历 `HUB.devices.keys()`）。

#### 5.6.2 要做到「A 改色 → B/手机立刻变紫」，需要改三处

1. **服务端：新增广播发起点**。改色动作（`reassign-color` HTTP、或设备侧的
   `nickname_reassign_color_request` 帧）在**事务提交之后**调用
   `_broadcast_nickname(payload)`，内部同时 `HUB.broadcast_web(...)` + 遍历 `HUB.devices` 逐个
   `send_to_device(...)`（形状照 `main.py:319-340`）。放在提交之后是关键：广播里带的颜色必须是已落库的值。
2. **浏览器：必须在 `/ws/web` 上**。网页版由 `app.js:1047-1062` `connectWS()` 建连，`ws.onmessage`
   统一转给 `handleServerFrame()`（`app.js:1058-1061`）。
   ⚠ **壳模式例外**：`app.js:1051` 在壳里直接 `return` **不连 `/ws/web`**（宿主持有长连接），
   帧由桥喂进来（`shell.js:9`、`:159`、`shell.js:434-447` `onHostMessage`）。
   → 壳模式下宿主必须把 `nickname_*` 帧**经桥转发给页面**，否则 PC 本地页永远收不到颜色变化（Phase 4 要做）。
3. **前端：在帧分发链里加分支**。`handleServerFrame()`（`app.js:1065-1085`）是一个
   `if / else if` 类型链，需要新增**五个**分支：`nickname_created` / `nickname_updated` /
   `nickname_color_changed` / `nickname_removed`（增量事件）+ `nickname_list_sync`（整表下发，
   客户端据此对齐整张本地缓存，见 5.6.3）。**`nickname_*` 广播类型只允许这五个**（文件头 r5 第 7 条规则）。

#### 5.6.3 网页端现在昵称是 localStorage —— 读写点与改造方式

现状（全部在 `web/static/app.js`）：

| 用途 | 位置 |
|---|---|
| key 定义：`NAMES_KEY='fm.names'`、`LAST_SENDER_KEY='fm.lastSender'` | `:1088`、`:1089` |
| **读**：`loadNames()`（解析失败/为空 → 默认 `['我']`） | `:1092-1104`（默认值 `:1103`） |
| **读**：`currentSender()` 里的 `localStorage.getItem(LAST_SENDER_KEY)` | `:1119` |
| **写**：`persistNames()` | `:1106-1108` |
| **写**：`rememberSender()` | `:1110-1112` |
| 消费：`renderNameSelectors()` —— 下拉项颜色走**哈希** `nickColor(n)` | `:1124-1133`（`:1125`） |
| 消费：`renderNamesList()` —— 色块 `nickColor(name)`、`maxLength=16`、改名、删除 | `:1135-1187`（`:1152`、`:1157`、`:1159-1169`、`:1176-1181`） |
| PC 本地页另有 `fm.pc.lastSender`（只记「上次用哪个」） | `web/shell/pc.js:36`（读 `:567`、写 `:269`/`:493`） |

收到 `nickname_color_changed` 后**应该怎么做**（Phase 3 的施工顺序）：

1. 用帧里的 `{nickname_id, display_name, color}`（**`color` 是逻辑色 ID**，渲染时经 §4.4 算显示色）
   更新内存里的两张表：
   `Map<nickname_id → {display_name, color}>`（主索引，供 `sender_nickname_id` 查）
   **和** `Map<display_name → {nickname_id, color}>`（反查索引 —— 历史消息只有 `sender_name`，没有 id，
   兜底链见 §2-C1）。
2. 重画 §2-C1 的 4 个颜色消费点：消息行头像底（`chat.js:63`）、弹窗小圆点（`shell.js:604`）、
   昵称下拉的文本色（`app.js:1125` / `shell.js:657`）、设置页色块（`app.js:1152`）。
   重画要有**单一入口**（例如 `applyNicknames()`），否则又会出现「网页端新色、PC 本地页旧色」（R7 的成因）。
3. 若被改的正是「我当前选中的发送人」→ 发送区下拉同步更新（重跑 `renderNameSelectors()`，`app.js:1124-1133`）。
4. **绝不把新颜色写回 localStorage**：Phase 3 之后 localStorage 只留「本机选了谁」这一件事 ——
   `fm.lastSender` 存的是**这个浏览器选中的 `nickname_id`**，这就是 Web 端「当前昵称」的**唯一落点**
   （硬约束 §0.10；PC 侧的对应物是本地配置里的 `current_nickname_id`）。
   **颜色一律实时来自 NAS**（硬约束 §0.3）；**没有服务端指针** —— A 浏览器选了别人，
   不会改到 B 的界面（§5.2 / §10 已定 3）。
   `fm.names`（本地名字数组）停止读写（一次性迁移见 §7 Phase 3）。
5. 时延判据：Phase 3 验收里「1 秒内变紫」的路径 = HTTP 写 → 服务端事务提交 → `broadcast_web` →
   B 的 `ws.onmessage` → `handleServerFrame` → 重画。同局域网内是**毫秒级**，
   不需要轮询、不需要前端定时拉取。
6. **`/ws/web` 上行只有 `ping`**（`main.py:977-980`）→ 改颜色只能走 HTTP，广播只有下行。
   三个浏览器同时改色时也不会互相打架：颜色根本不由浏览器决定，服务端是单进程 + 单写事务（§6.1）。

#### 5.6.4 多个客户端共用同一昵称时，广播的影响面 —— **r5：该问题已不存在（旧补丁整段作废）**

**昵称中心模型下「一个名字 = 一行 = 一个颜色」，所以旧版「同名多行必须一起变色」那套补丁全部作废。**

- 旧版（r2/r3）里「Web 单例的『妈妈』」与「某台 PC 的『妈妈』」是**两行**，为此不得不：另建
  `nickname_colors` 归属表、在同一个事务里刷所有同名行的 `updated_at`、并在广播里塞
  `affected_nickname_ids: [...]`。**这些在 r5 全部消失** —— 它们本来就是「同一个名字却有多个对象」
  这个错误建模的补丁（对照 §2.C4 末尾、§2.C7、附录 B）。
- 逐条对应：

  | 旧版（r3）的补丁 | r5 的等价物 |
  |---|---|
  | `nickname_colors` 归属表 + 刷同名行 | **不存在**：颜色就是 `nicknames.color` 那一列（§3.1） |
  | 广播带 `affected_nickname_ids: [...]` | **不存在**：一条广播本来只对应一行，带 `nickname_id` 就够（§5.4 的载荷已按此定稿） |
  | 「同名多行一起变紫」 | **结构性成立**：多个客户端用的是同一行，改色即全端 + 全历史一起变（§2.C7） |
  | 「删掉最后一条同名行才释放颜色」 | **不存在**：删这一行就释放（`released_color`，§3.5 / §6.2 的 `remove()`） |

- 唯一需要客户端做的仍是**两级索引**（`nickname_id → {display_name, color}` 主索引 +
  `display_name → {nickname_id, color}` 反查索引，见 §5.6.3 第 1 条）——
  它的作用**不是**「合并同名行」，而是给只有 `sender_name` 的历史消息兜底（§2-C1）。
- **创建撞名不产生广播**：`POST /api/nicknames` 撞上一个已有的活跃名字 →
  **409 `NICKNAME_ALREADY_EXISTS`**（响应带 `existing_nickname_id`）：**不新增行、不分配颜色、
  不发 `nickname_created`**；客户端拿这个 id 去做的是**「选用」**（纯本地动作，也不广播，§5.2）。
  r4 的「`ensure()` 幂等命中 + 返回 `existing:true`」**已作废**（附录 B）—— UI 上「创建」与「选择」
  分得开，靠的就是这条 409。
- **`nickname_removed` 的语义**：软删那一行 → 颜色立即回池（索引② 的 `WHERE status='active'`），
  广播里带 `released_color`（规格 §8 的「释放」要看得见）。
- 五个广播事件（`nickname_created` / `nickname_updated` / `nickname_removed` /
  `nickname_color_changed` / `nickname_list_sync`）里，**只有 `nickname_list_sync` 用来下发整表**；
  其余四个都是「一行变了」的增量事件，载荷统一带完整 `nickname` 对象（§5.4）。

---

## 6. 颜色分配的事务安全

### 6.1 现状的并发事实（决定了方案怎么写）

| 事实 | 依据 |
|---|---|
| 单进程、单事件循环（uvicorn 无 workers） | `server/run.py:76-77` |
| 全局**单条** SQLite 连接（`check_same_thread=False`） | `server/db.py:169-183`（`:175`） |
| 所有读写被同一把 `threading.RLock` 串起来 | `db.py:22`、`:190-211` |
| `execute()` **每条语句自己 commit** | `db.py:203-211`（`:209`）→ **「读可用色」与「写新行」是两次独立提交，不是一个事务** |
| WAL 模式、`synchronous=NORMAL`、`foreign_keys=ON` | `db.py:177-179` |
| **没有 `busy_timeout`** | 同上（缺）→ 跨进程会立刻 `SQLITE_BUSY` |

结论：**同进程内不会真并发**，但「检查 + 分配 + 写入」在 `execute()` 粒度上**不是原子的**，
而且一旦将来上多 worker / 多进程（或运维脚本 `tools/*.py` 直连同一个 `family.db`），
只靠 `_lock` 就彻底失效。所以规格 §12 的要求必须**同时**靠「显式事务」和「数据库约束」两层。

### 6.2 事务边界（推荐写法）

在 `server/db.py` 增加一个上下文管理器（**不改现有 `query`/`execute` 的语义**）：

```python
import contextlib

@contextlib.contextmanager
def tx():
    """写事务：BEGIN IMMEDIATE … COMMIT / ROLLBACK。

    为什么必须持 _lock：本进程的读也走同一条连接（db.query），
    不把整段独占住，别的线程的 SELECT 会看到本事务未提交的中间态。
    为什么用 IMMEDIATE：一上来就抢写锁，避免「读到了可用色、写的时候锁不上」的经典竞态。
    """
    with _lock:
        conn = get_conn()
        conn.execute("BEGIN IMMEDIATE")
        try:
            yield conn
            conn.commit()
        except BaseException:
            conn.rollback()
            raise
```

昵称的「新增」在**一个** `tx()` 里完成（规格 §6「在同一个数据库事务中完成」、§12「具有原子性」）：

```python
# server/services/nicknames.py（伪代码，Phase 1 落地；完整版见 §3.3）
def create(display_name: str) -> dict:
    """创建一个**新的**共享昵称。名字已被 active 昵称占用 → 抛 NicknameAlreadyExists(既有 id)。
       ⚠ **不是幂等**：r4 的 ensure()（按名幂等返回既有行）已作废 —— 幂等会让「创建」与「选择」
       在 UI 上分不开（最终说明 §3）。"""
    name = normalize(display_name)       # trim + NFC + 拒控制字符（§5.5）
    with db.tx() as conn:
        # ① 显式判重：命中就**明确报错**（不静默复用；**立刻返回、不碰颜色池** ——
        #    否则池子将满时会把「名字已存在」误报成 503）
        row = conn.execute(
            "SELECT nickname_id FROM nicknames WHERE display_name=? AND status='active'",
            (name,)).fetchone()
        if row:
            raise NicknameAlreadyExists(existing_id=row["nickname_id"])   # → 409 + 既有 id
        # ② 新名字 → 同一事务里查占用色，按池子顺序取第一个可用
        used = {r[0] for r in conn.execute(
            "SELECT color FROM nicknames WHERE status='active'")}   # 全是逻辑色 ID
        color = next((c for c in COLOR_POOL if c not in used), None)  # 第一个可用的逻辑色 ID
        if color is None:
            raise NoAvailableColor()     # → HTTP 503 / WS nickname_error（规格 §11，绝不重色）
        # ③ 插入（本表**有** color 列；写锁已在手，读到什么就是什么）
        conn.execute(
            "INSERT INTO nicknames (display_name, color, status, created_at, updated_at)"
            " VALUES (?,?, 'active', ?, ?)", (name, color, db.now_iso(), db.now_iso()))
        return {...}                     # nickname_id / display_name / color / status / …
```

**对比 r3 / r4**：整段 `owner_type` 分叉、`nickname_colors` 归属查询与「同名复用颜色」都删了（r3）；
r4 的「按名幂等创建」也删了（r5）—— 同一个名字**不再是一个可以静默命中的分支，而是一条 409**
（`NICKNAME_ALREADY_EXISTS` + `existing_nickname_id`），客户端据此把用户**引导到「选用」**。
**索引① 仍是最后防线**：同一时刻一个活跃名字只有一行，所以「同名 = 同一个对象」照样成立
（§2.C4 / §2.C7）。

**软删（颜色立即回池）**（同 **一个** `tx()`；r3 在这里最难写对，r5 只有三步）：

```python
def remove(nickname_id: int) -> dict:
    with db.tx() as conn:
        row = conn.execute("SELECT * FROM nicknames WHERE nickname_id=? AND status='active'",
                           (nickname_id,)).fetchone()
        if not row:
            raise NotFound()                     # → HTTP 404 NICKNAME_NOT_FOUND
        conn.execute("UPDATE nicknames SET status='inactive', updated_at=? WHERE nickname_id=?",
                     (db.now_iso(), nickname_id))
        return {"nickname_id": nickname_id, "released_color": row["color"]}
```

- **没有**「该名字下还有别的 active 行吗」的判断，也**没有** `UPDATE nickname_colors ...`
  —— 那两步都是在维护「同名多行」，r5 下不存在（§5.6.4 / 附录 B）。
  一行 = 一个颜色 = 一次释放；颜色回池完全由索引② 的 `WHERE status='active'` 保证（§3.1 / §3.5）。

**「空表懒建默认用户」已作废（反例见附录 B）**

r4 曾设计 `ensure_default()`：`GET /api/nicknames?status=active` 拿到 0 条时由服务端建一条
「默认用户」。**r5 删除它**，三条理由：

1. 最终说明 §9：**新客户端不预建任何共享昵称**。第一次启动显示的是**客户端本地**的灰色临时昵称
   （Web 用固定中性文案「默认用户」+ 逻辑色 `gray`，`nickname_id = NULL`，见 §3.2.1）——
   它**不是** `nicknames` 表里的一行。
2. 表里凭空多一条「用户从没创建过、也不认识」的对象，昵称列表页会显示一个假昵称。
3. 一旦建行就必须分配颜色 → **白占一个色位**（`COLOR_POOL` 只有 16 个），
   而且并发首访还得多写一层「只建一条」的幂等推理。这两笔成本现在都不用付。

→ 因此 `GET /api/nicknames` 是**纯读**：空表就返回 `{"nicknames": []}`（§5.3 第 1 行 / §0.9），
页面显示灰临时「默认用户」并给出「创建一个昵称 / 选用已有昵称」的引导。

### 6.3 约束作为最后防线（与并发模型无关）

即使事务写错、即使将来多进程、即使有人用 `sqlite3` 直接改库，以下 DDL 都成立
（§3.1，全表只有**两条 partial unique index**）：

- **同一个活跃 `display_name` 不可能有两行** → 同一时刻「同名只有一个对象」；重复创建**不是新建成功**，而是 `NICKNAME_ALREADY_EXISTS`〔索引① `ux_nicknames_name_active`〕
- **同一个活跃 `color` 不可能属于两行** → 「不重色」（规格 §11 / §12）〔索引② `ux_nicknames_color_active`〕
- `inactive` 行**不占位**：软删后同名可再建、颜色可被新名字拿到（两条索引都带 `WHERE status='active'`）

r3 的另外三条（`ux_nicknames_device_owner_active`、`ux_nicknames_web_name_active`、表级 CHECK
`ck_nicknames_web_singleton`）与**整张颜色归属表 `nickname_colors` 及其两条索引**
（`ux_ncolors_name_active` / `ux_ncolors_color_active`）**全部作废**（§2.C4 末尾 / §2.C7 / 附录 B）。

★ **r6：「颜色一定取自池内」现在由 DDL 直接保证** —— `nicknames.color` 的 `CHECK` **枚举 16 个逻辑色 ID**
（§3.1），不再像旧版那样只校验 HEX 格式。服务层白名单（`is_valid_shared_color`）仍保留，用于
**入参校验与错误提示**（R2 / §4.3）。
唯一仍必须由 §6.2 的 `tx()` 兜住的是**「按顺序取第一个可用」**与**池满时的 `503`**
（DDL 管得了「色值合法 / 不重复」，管不了「还有没有色可取」）。

（r4 曾把「至少有一条 active 昵称」列为第二件，靠 `ensure_default()` 兜底。**r5 取消了这条需求**：
没有共享昵称时客户端用灰临时昵称发消息（§3.2.1），所以「最小基数」不再是约束，
`ensure_default()` 也一并删除 —— 见 §6.2 与附录 B。）

配套必须有**重试**（否则并发下会向上抛 `IntegrityError` 变成 500）：

```python
for _ in range(len(COLOR_POOL) + 2):
    try:
        return create_once(display_name)     # 内部一个 tx()（§6.2）
    except sqlite3.IntegrityError as e:
        msg = str(e)
        if "nicknames.display_name" in msg:  # 索引①：名字被并发抢先建了
            # **绝不重试** —— 重试就会变成「静默复用」。翻成明确错误（最终说明 §3），
            # 并带上既有那一行的 id，UI 拿它去「选用」：
            raise NicknameAlreadyExists(existing_id=_active_id_of(display_name))
        if "nicknames.color" in msg:         # 索引②：颜色被并发抢走 → 重来一轮（下一轮看到新占用）
            continue
        raise                                # 别的完整性错误与并发无关，直接抛
raise NoAvailableColor()
```

**同时必须补 `busy_timeout`**（R6）：在 `db.py:177-179` 那一串 `PRAGMA` 后加
`_conn.execute("PRAGMA busy_timeout=5000")`，否则跨进程并发下 `BEGIN IMMEDIATE` 会直接
`database is locked` 而不是等 5 秒。

### 6.4 并发场景逐个说明（为什么不会重色 / 不会重复实体）

| 场景 | 会发生什么 | 为什么正确 |
|---|---|---|
| **两个客户端同时新增不同名**（客户端 A 建「客厅电脑」+ 客户端 B 建「我的电脑」） | 两个 `tx()` 串行（`BEGIN IMMEDIATE` 抢写锁；同进程内还被 `_lock` 串住）。第一个拿到池子里第 1 个可用色，第二个看到它已占用 → 拿到第 2 个 | 单写者 + 「查占用 → 取第一个可用」在同一事务内；跨进程还有写锁 + `busy_timeout` + 索引② 兜底 |
| ★ **两个客户端同时新增同一个名字**（两台同名 PC、或 PC 与 Web 都叫「妈妈」） | 第一个建行（201）；第二个要么在 ① 的 `SELECT` 命中、要么被索引① 拒绝后**翻成同一个错误** → **409 `NICKNAME_ALREADY_EXISTS` + `existing_nickname_id`**（客户端提示「已存在，直接选用它？」）；不报 500、不加后缀、不占第二个色位 | 索引① 强制「一个活跃名字一行」；服务层**显式判重**（最终说明 §3：明确报错，**不静默复用**） |
| **同一客户端重发**（PC 重连重发创建帧、Web 重复点「新增」） | 第二次起**都是 409 `NICKNAME_ALREADY_EXISTS`**（响应带同一个 `existing_nickname_id`）→ UI 直接引导「选用」；**不新增行、不变色、不发广播**（§5.6.4） | 服务层判重与插入在**同一** `tx()` 内；「幂等」只存在于客户端层（本地选用状态） |
| 池子被占满（≥ `len(COLOR_POOL)` 条 active）新增 | `pick_first_available()` 返回 `None` → `NoAvailableColor` → HTTP **503 `NO_AVAILABLE_COLOR`** / WS `nickname_error` | 规格 §11「不要强行重复颜色」；**绝不回退到随机或哈希** |
| 两个客户端同时 `reassign-color` **同一个**昵称 | 串行；第二个看到第一个的新色已占用（且自己的旧色已被释放回池）→ 拿到另一个色 | 同 §6.2 的事务边界 |
| 两个客户端同时 `reassign-color` **两个不同**昵称 | 串行，各拿一个色，互不重色 | 同上 |
| 删除 + 新增同时（删「爸爸」释放 `color_05`，同时新增「奶奶」） | 事务顺序决定谁先；无论顺序，结果都满足「active 无重色」 | 部分唯一索引 + 写锁 |
| 同一个昵称 reassign 到自己当前的色 | 服务层显式排除自身：`used - {self.color}`（规格 §11 第 3 步），`pick_first_available` 会跳过旧色 | 服务层规则 + 单测「新色 ≠ 旧色」 |
| 改名 + 新增同时（A 把 `MY-PC` 改成「奶奶」，B 同时新增「奶奶」） | 串行；后到的那次拿到 409 `NAME_TAKEN`（**不是**自动合并、**不是**加后缀） | 索引① 强制；改名走 `PATCH`，语义是「拒绝」而非「并入」（§2.C7 取舍 1） |

**可测判据（Phase 1 就要能跑）**：
`tools/test_nicknames.py`（新增，风格照 `tools/test_unlock.py` / `tools/test_ws_kick.py`）：
1. 顺序建 `len(POOL)` 个**不同名字** → 颜色集合大小 == `len(POOL)`，且顺序 == 池子顺序；
2. 第 `len(POOL)+1` 个 → 抛 `NoAvailableColor`；
3. 删除第 3 个 → 再新增一个新名字 → 拿到的正是它释放的那个色（且是「池子里第一个可用」）；
4. `threading` 起 8 线程同时建 8 个不同名字 → 颜色集合大小 == 8（靠索引重试也不能重色）；
5. **同一个名字**连调 `create()` 5 次 → 第 1 次 201，后 4 次全是 409 `NICKNAME_ALREADY_EXISTS` 且带同一个 `existing_nickname_id`；库里**始终 1 行 active**、颜色不变；
6. reassign → 新色 ≠ 旧色 且未被占用；reassign 到池子只剩自己一个可用色时仍成功（排除自身规则生效）。

---

## 7. 分阶段实施方案（每个阶段都能单独交付）

阶段划分沿用规格 §23 的 Phase 1-5，但补上「改什么 / 验收判据 / 风险 / 回退 / 依赖」，
并加一个**全局开关**作为跨阶段回退手段：

```yaml
# server/config.yaml（新增段；默认值落在 server/config.py 的 _DEFAULTS，:20-35 的既有风格）
nickname:
  enabled: false        # Phase 1 起为 true（只影响 API 是否注册/是否下发），Phase 3-4 期间前端也读它
  color_pool_version: 1
```

`/api/config`（`main.py:163-173`）加一行 `nickname_enabled` + `color_pool_version` →
前端在 `enabled=false` 时完全走今天的哈希路径（`app.js:336-344`），一行代码都不改行为。

---

### Phase 1 —— NAS 数据层（可独立交付，无对外行为变化）

**改什么**
- `server/db.py`：SCHEMA 加**一张表** `nicknames`（**6 列**，其中 `color` 是**逻辑色 ID 枚举**
  `color_01`…`color_16`；**没有** owner 列）+ **2 条 partial unique index** + 1 个普通索引（§3.1）；
  `MIGRATIONS` 加 **两列** ——
  `messages.sender_nickname_id` + `messages.sender_color`（§3.2；两列都可空，存量回填见 §3.4）；
  `POST_MIGRATION_DDL` 加 `idx_messages_sender_nick`；
  `get_conn()` 加 `PRAGMA busy_timeout=5000`；新增 `tx()`。
- `server/nicknames.py`（新）：`LOGICAL_COLORS`（逻辑色 ID → 基础色值）、`COLOR_POOL`、
  `LOCAL_TEMP_COLOR_ID = "gray"`、`COLOR_POOL_VERSION`、`MAX_ACTIVE_NICKNAMES`、
  `pick_first_available()`（返回逻辑色 ID）、`is_valid_shared_color()`（§4.3）。
- `server/services/nicknames.py`（新）：`create / rename / remove / reassign_color / list / get`，
  全部走 `db.tx()` + 重试（§6）。**没有 `ensure()` / `ensure_default()`** —— 按名幂等创建与
  「空表懒建默认用户」都已作废（附录 B）。
  **三个最容易写错的点**（§6.2 已给伪代码）：① `create()` 是**显式判重**：命中已有 active 同名 → 抛
  `NicknameAlreadyExists(existing_id)` → 409（**绝不**返回既有行 —— 那不是「创建」）；
  ② 判重命中时**立刻返回、绝不碰颜色池**（否则池子将满时会把「名字已存在」误报成 503）；
  ③ `remove()` 只需 `UPDATE status` —— **没有**「同名还有别的 active 行吗」的判断，
  也**没有**颜色归属表的连动（那两步 r5 下不存在）。
- `tools/test_nicknames.py`（新）：§6.4 的 6 条判据 **+ §2.C4 实测表的判据**
  （同名再建 → 409 且不新增行 / 同名连建 5 次全 409、库里始终 1 行 / 同名软删后能再建 /
  `inactive` 不占位 / 不同名同色被索引②拒 / `status` 只接受 `active|inactive` /
  灰 `gray` 拒绝分配给共享昵称（`is_valid_shared_color('gray') == False`）；
  第 17 条 active 新建 → `NoAvailableColor`（**活跃上限 16**，§4.3））。
- `server/config.py`：加 `nickname` 段默认值。

**验收（可测）**
1. 启动服务后 `PRAGMA table_info(nicknames)` **恰好 6 列**：`nickname_id / display_name / color /
   status / created_at / updated_at`，**没有** `owner_type` / `owner_id`；
   `sqlite_master` 里 `name like 'ux_nicknames%'` **恰好 2 条**，且两条的 `sql` 里都带 `WHERE`；
   `sqlite_master` 里**不存在** `nickname_colors` / `ux_ncolors%`。
1b. ★ **颜色列只装逻辑色 ID（r6）**：`sqlite_master` 里 `nicknames` 的建表 SQL 含
   `CHECK (color IN (`，且恰好列出 `color_01`…`color_16` **16 个值**；插 `'#E53935'`（HEX）→
   **`IntegrityError`**；插 `'gray'` → **同样被拒**（灰不在枚举里，§3.2.1）；
   `grep -c 'GLOB' server/db.py` 在 `nicknames` 建表段落为 0（旧版是 `GLOB` 校验格式，r6 换成枚举）。
1c. **同名被拒的单元测**（§2.C4 实测表）：同名连建 3 次 → 第 1 次 201，后 2 次都是
   409 `NICKNAME_ALREADY_EXISTS` 且带同一个 `existing_nickname_id`；库里始终 1 行 active、颜色不变；
   同名软删后再建 → 成功且拿到新 `nickname_id`（历史 `inactive` 行不占位）；
   给两个不同名字分配同一个颜色 → `IntegrityError`（索引②）。
2. `python3 tools/test_nicknames.py` 6 条全绿。
3. 老库升级：拿一份迁移前的 `family.db` 副本启动 → 不报错、既有 8 张表数据行数不变、
   `messages` 多出 **2 列**（`sender_nickname_id` + `sender_color`）且都为 `NULL`。
4. `grep -rn 'owner_type\|owner_id\|nickname_colors' server/` 结果为 **0**（本阶段不许把 owner 概念带进代码）。
5. `grep -n nickColor web/static/*.js` 结果与改造前**完全一致**（本阶段零前端改动）。

**风险 / 回退 / 依赖**
- 风险：迁移写错列（`PRAGMA table_info` 判据已有，安全）；`busy_timeout` 引入新的等待语义（超时仍会报错，行为更可预期）。
- 回退：`DROP TABLE nicknames; DROP INDEX ux_nicknames_name_active, ux_nicknames_color_active, ix_nicknames_status`
  （不动既有表），前端本来就没接；把 `MIGRATIONS` 那一行注释掉即回到今天
  （多余的空列无害，SQLite 不支持删列，但不影响任何查询）。
- 依赖：无。**可以现在开工。**

---

### Phase 2 —— NAS API + 广播（可独立交付：接口可被 curl 验证）

**改什么**
- `server/main.py`：注册 §5.3 的路径 —— `GET` / `POST /api/nicknames`（**`GET` 是纯读**：空表就返回
  `{"nicknames": []}`，不建行、不分配颜色）+ 按 `{nickname_id}` 的 `PATCH` / `DELETE` /
  `reassign-color` 管理路径；**不实现** `GET` / `PUT /api/web/sender` —— Web 的「当前昵称」是浏览器
  本地状态（§0.10 / §5.2 / §10 已定 3）。
  一律 `WebAuth`（+ `nickname.manage` 常量占位）；**不接受**请求体里的 `owner_id` / `owner_type` / `color`
  —— 出现即忽略，绝不采纳（§5.5）。
- `reassign-color` 的语义（§5.3 第 5 行）：改的就是这一行的 `color`；**不需要**任何「同名连带」
  （一个名字只有一行，§5.6.4）。
- `server/main.py:825-959` `handle_device_message`：加 §5.4 的**五个上行分支** ——
  `nickname_list_request` / `nickname_create_request` / `nickname_rename_request` /
  `nickname_reassign_color_request` / `nickname_delete_request`（照现有 `elif mtype == ...` 风格）。
  **帧名一律以 §5.4 为准** —— r2/r3/r4 的 `nickname_register` / `nickname_update` /
  `nickname_ensure` / `nickname_sync_request` **都不作数**，别照旧名写。
- 广播：新增 `_broadcast_nickname(payload)`，同时 `HUB.broadcast_web` + 遍历 `HUB.devices` 下发
  （形状照 `main.py:319-340`）；**类型只允许 §5.4 的五个**；**创建撞名（409）时不广播、不写库**
  （否则列表页会凭空多一条，§5.6.4）。
- `server/services/devices.py:129-132`：**保持不动** —— 删设备**不许**碰 `nicknames`（R3 / §3.5；
  r2 的「级联软删昵称」已作废）。
- `docs/PROTOCOL.md`：更新 §7 下行帧表 + 新增「昵称协议」一节；**修掉** `:23-24`、`:197`、`:251` 三处过时论断。

**验收（可测）**
1. `curl` 逐个过：空库 `GET /api/nicknames?status=active` → **200 + `{"nicknames": []}`**，
   **库里 0 行**（不懒建、不分配颜色；再 GET 仍是空）→ `POST` 建「妈妈」「爸爸」→ 两条都 `201`、
   颜色 = **`color_01` / `color_02`**（逻辑色 ID，不是 HEX）→ 再 `POST` 一次「妈妈」→ **409 `NICKNAME_ALREADY_EXISTS` +
   `existing_nickname_id`**（**不是 200、不是 `existing:true`**）→ `PATCH` 改名 →
   `reassign-color` → `DELETE` → 列表（`status=inactive` 能查到历史行）。
   ⚠ **两个语义别搞混**：**创建**撞名 = 409 `NICKNAME_ALREADY_EXISTS`（引导「直接选用既有的」）；
   **改名**撞名 = **409 `NAME_TAKEN`**（引导「换个名字」）（§5.3 第 2 / 3 行、§2.C7 取舍 1）。
2. 用 `tools/cli_agent.py`（协议参考实现，`PROTOCOL.md:244`）当 PC 侧：发 `nickname_list_request` →
   收到 `nickname_list_response`（全量 `nicknames` + `pool_version`）；发 `nickname_create_request`
   （新名字）→ 收到 `nickname_list_response` + 另一条连接上收到 `nickname_created`；
   同名再发一次 → 收到 `nickname_error`（`code=NICKNAME_ALREADY_EXISTS` + `existing_nickname_id`）；
   发 `nickname_rename_request` → 收到同步 + 另一条连接上收到 `nickname_updated`。
3. **广播范围**：**三个** `/ws/web` 连接（模拟三浏览器）+ 一个设备连接，任一侧改颜色 →
   四方都收到 `nickname_color_changed`（设备侧是 `send_to_device`，不是只推后台）。
   Web 侧能覆盖是 `hub.py:179-187` 遍历 `web_clients` 集合的既有行为（§5.6.1），
   本阶段要验的是「新增的 `_broadcast_nickname` 确实调了它」。
4. 池子占满（16 条 active）→ 新建与 `POST .../reassign-color` 都返回 503 且 body 里
   `code=NO_AVAILABLE_COLOR`、`message` 为满额文案（§4.3）；`GET /api/nicknames` 的 `active` 集合
   颜色仍两两不同（不重色）。
5. **没有「越权」这个概念**：用 A 设备拿到的 `nickname_id`，从另一个设备连接发 `nickname_rename_request`
   → **成功**（昵称是全局对象，任何持家庭口令 / 设备 token 的客户端都能改），并正常广播；
   **不存在** `NOT_YOURS` 这类「不是你的」错误（§5.1 / §0.6）。
   注：`reply` 帧防冒充的既有手法（`main.py:872-876` `reply_sender_mismatch`）是**设备 / 消息**层的事，
   与昵称无关，本方案不动它。
6. **灰临时昵称消息**：省略 `nickname_id` 发一条（HTTP `POST /api/messages` 或 `reply` 帧）→
   落库 `sender_nickname_id IS NULL` 且 `sender_color = 'gray'`（**逻辑色 ID**）；而改造前的老消息是
   **两列都 NULL** —— 判定「灰临时」**只看 `sender_color = 'gray'`**，绝不把 `NULL` 当成灰（§3.2.1 风险条目）。

**风险 / 回退 / 依赖**
- 风险：新增 `elif` 分支写错会让整条设备连接被关（`main.py:803-813` 已有兜底吞异常 + 落痕，但仍要看日志）。
- 回退：删掉路由与 `elif` 分支即可；`nickname.enabled=false` 时连广播都不发（复用同一开关）。
- 依赖：Phase 1。

---

### Phase 3 —— Web 管理端（可独立交付：网页端用完整体验）

**改什么**
- 新增「昵称」页/卡片（规格 §19：列表显示 昵称 / 颜色 / 操作，含「重新分配」）：
  位置建议 `web/index.html` 的「设置」区旁（现有「发送昵称」卡片 `:392-404` **整体替换**为 NAS 版），
  逻辑写在 `web/static/app.js`（现有 `renderNamesList()` `:1135-1187` 的骨架可复用，数据源换成 API）。
- 消息上色：`app.js` 加昵称字典（**存逻辑色 ID**）+ `nickColorById()`；`chat.js:37-42` `colorOf()` 改成三级查找
  （§2-C1）；`shell.js:677-680` 同步改。★ 取出逻辑色 ID 后**必须经 §4.4 的映射表**得到显示色
  （不能在 `chat.js` / `app.js` / `shell.js` 里各处再写一套 HEX 表）。
- `app.js:64-68` `api()`：错误码支持（`err.code`）。
- **昵称渲染按逻辑色走**：昵称字典里存的是**逻辑色 ID**；渲染前经 §4.4 的映射表算出显示色
  （圆点按主题变体 / 头像底与字色自适应）；`web/static/app.js:1157` 的 `maxLength`
  **16 → 32**（§5.5 统一 32）。
- **额度提示**：新建入口 / 列表页按 `GET /api/nicknames?status=active` 的长度 `x` 显示
  `x ≥ 14` → 「共享昵称颜色即将用尽（x/16）」；`x = 16` → 「已达到共享昵称上限，请删除不再使用的昵称后再添加。」
  并把新建入口置灰（文案逐字见 §4.3）。
- **前端接广播**：`handleServerFrame()`（`app.js:1065-1085`）加 `nickname_created` /
  `nickname_updated` / `nickname_color_changed` / `nickname_removed` 四个**增量**分支 **+**
  `nickname_list_sync`（整表，直接替换本地缓存），统一走一个 `applyNicknames()` 重画入口
  （§5.6.2 / 5.6.3）。
- `localStorage` 一次性迁移：`fm.names`（本地名字数组）**停止读写**（昵称真相源 = NAS）；
  `fm.lastSender` **改存 `nickname_id`** —— 它就是**这个浏览器**的「当前昵称」（§0.10）：
  只在这个浏览器 / 浏览器 profile 里生效、不上传、**不影响别的浏览器**（§5.2 / §10 已定 3）。
  ⚠ **绝不能**把 `fm.names` / `fm.lastSender` 当成昵称的来源（浏览器本地、每台机器不同）：
  两个浏览器首次访问各自显示灰临时「默认用户」，**不创建任何共享昵称**（§3.2.1 / §0.9）。
- **空状态（这是正常路径）**：库为空时 `GET` 就是空数组（**服务端不懒建**，§0.9）——
  列表显示引导文案（可复用 `app.js:1138-1144` 已有的「还没有昵称，先添加一个吧。」）
  +「新建一个」入口；发送区显示灰临时「默认用户」，**发消息照常可用**（灰临时消息，§3.2.1）。
- **多端共用同一昵称的展示**：r5 下「两台机器都用『妈妈』的名义」**只有一行**（§2.C7）——
  列表不折叠、也没有「点进去改哪一条」的歧义；改色操作只需提示
  「这个名字所有在用的机器都会一起变」（§5.6.4 / §10 已定 9）。
- `docs/DESIGN-TOKENS.md:122-146`：改写成新契约 —— **颜色 = 来自服务端的逻辑色 ID**，
  客户端按 §4.4「逻辑色 → 显示色」映射表 + 主题渲染；池子版本号（`pool_version`）；未知 ID 兜底链。

**验收（可测）**
1. 三个浏览器（两个不同 profile + 手机浏览器）打开 → 看到**同一份**昵称列表
   （妈妈 / 爸爸 / 奶奶，取自 `/api/nicknames`），每条颜色三端一致，**且不是哈希色**
   （颜色 = NAS 下发的逻辑色 ID 经 §4.4 渲染的结果，切浅/深主题时两端的变体一起变）。
2. 在 A 新建「妈妈」→ B 与手机的列表 **1 秒内**出现（`nickname_created` 广播）。
3. 在 A 改名（妈妈 → 母亲）→ B 与手机 **1 秒内**显示新名，颜色不变（`nickname_updated`）。
4. 在 A 把「妈妈」重分配成紫色 → **B 与手机立刻变紫**、另一台 PC 的弹窗同步变紫
   （`nickname_color_changed`，路径见 §5.6.3 第 5 条）。
5. **多端共用一昵称**：让另一台 PC 也用「妈妈」的名义 —— 它是**选用**已有的「妈妈」
   （本地动作，NAS 一个字节都不变）；若它去**创建**「妈妈」→ 409 `NICKNAME_ALREADY_EXISTS`
   + 引导「直接选用它？」。在 A 改「妈妈」的色 → 三端 + 那台 PC 一起变（本来就同一行、同一个颜色）。
6. 在 A 删除「妈妈」→ 三端列表都消失；其历史消息**文本仍在**（`messages.sender_name` 未动）；
   行变 `inactive`、颜色**立即回池**（再建新名字能拿到它）。
7. 全新部署（空库）打开网页 → **不创建任何昵称**：列表为空 + 引导文案，发送区显示灰临时
   「默认用户」（灰色圆点）；发一条消息能成功落库，且该行 `sender_color = 'gray'`（§3.2.1）。
8. `grep -rn 'fm.names' web/` 结果为 0（不再有第二真相源）；
   `localStorage` 只剩 `fm.lastSender` / `fm.pc.lastSender` 两处「选了谁」。
9. `nickname.enabled=false` 时，网页端表现与今天**逐像素一致**（回退验证）。
10. `grep -rn 'nickColor' web/static/` 只剩兜底调用点（`chat.js` 的 `colorOf` 兜底 + `app.js` 的 `nickColor` 定义），
   没有第三个 `NICK_COLORS` 数组被当成权威。
11. ★ **逻辑色落地**：库里 `nicknames.color` 全是 `color_0x`（`sqlite3` 查出来 grep 不到 `#`）；
   网页端浅 / 深主题各截一次图，圆点与头像首字逐行对得上 §4.4 表；把某个昵称的 `color` 在库里改成
   **未知 ID**（如 `color_99`）→ 页面**不崩、走灰兜底**，且 `style` 里不出现非法值。
12. ★ **额度提示**：造 14 条 active → 新建入口出现「共享昵称颜色即将用尽（14/16）」；
   造满 16 条 → 入口置灰 + 「已达到共享昵称上限，请删除不再使用的昵称后再添加。」，
   绕过 UI 直接 `POST` 仍得到 503 `NO_AVAILABLE_COLOR`。
13. ★ **离线拒绝（Web 侧无此状态，属 PC 侧，见 Phase 4）**：Web 端断网时新建 / 改名 / 删除 /
   重新分配颜色**全部失败并提示**，刷新后列表回到 NAS 的真相（不残留本地假状态、无 pending）。

**风险 / 回退 / 依赖**
- 风险：颜色语义的 CSS 契约（`style.css:878-879`、`pc.css:181-184`）在 v0.16.0 里刚被
  「气泡宽度修复」碰过，**施工前先确认没有并行的同类改动**，避免同一批 CSS 冲突。
- 回退：`nickname.enabled=false`（前端回到哈希路径）；本轮不删 `nickColor()` 定义。
- 依赖：Phase 2。**没有别的阻塞**（v0.16.0 已把那批前端改动落地）。

---

### Phase 4 —— Windows PC（可独立交付：PC 端完整体验；需 §8 的 Core 决策先定）

**改什么**
- **首次运行：不注册任何共享昵称**（最终说明 §9）：`hello` 之后只拉整表（`nickname_list_request`），
  界面显示**灰临时昵称**「当前昵称：`LIVINGROOM-PC`（= `ComputerName`，`AgentConfig.cs:116-117`）/
  颜色：灰色」，本地**不写** `nickname_id`（= 空）。此后用户二选一（§3.3）：
  **方式 A 选用** → 只把 `current_nickname_id` 写进本地配置，**一个帧都不发**；
  **方式 B 创建** → `nickname_create_request`（`display_name` 默认填 `DeviceName`）。
  **方式 B 撞名 → 409 `NICKNAME_ALREADY_EXISTS` + `existing_nickname_id`** → UI 提示
  「已存在，直接选用它？」，点「选用」即落到方式 A。
- **本地缓存**（建议独立 `nickname.json`，见 §8.2）：`nickname_id`（**可空 = 还没选**，即灰临时）、
  `nickname_display_name`、`nickname_color`（**逻辑色 ID**）—— 共 **3** 个字段。
  ★ **没有 `nickname_sync_state`**（r6：不存在 pending 概念，§0.13）。
  灰临时态的 `nickname_color` 恒为 `gray`（从服务端整表同步里拿不到，用客户端本地常量；
  **不来自池、不进池**，§3.2.1）。
- ★ **离线时（r6 硬规则：四个管理操作全部不可用）**（规格 §4 / §5 / §17 的新口径）：
  无连接时设置页的**新建 / 改名 / 删除 / 重新分配颜色**四个入口**一律禁用并明示原因**
  （文案例：「当前未连接服务器，昵称管理不可用」），**不本地生效、不排队、不落 pending、不补发**；
  即使连接中途断开（已在输入框里但发不出去）→ **直接报错回滚 UI**，绝不假装成功。
  **只有「选择昵称」（切 `current_nickname_id`）离线可用**。
- **重连后不补发任何昵称操作**（r6）：`hello` 之后只做两件事 —— ① 发 `nickname_list_request` 拉整表校正；
  ② 若本机 `current_nickname_id` 指向的昵称已 `inactive` → 清空选用 + 回退灰临时 + 提示（§3.5）。
  **没有待补发的改名、没有 `synced/pending` 状态机**（r6 删除了整套离线 pending 昵称同步，附录 B B16）。
- **UI**（规格 §20）：本机设置页显示「当前昵称 + 颜色圆点 + 重新分配按钮」（圆点颜色 = 逻辑色 ID 经
  §4.4 渲染；灰临时 = `gray`），
  数据经桥（建议新增 `host.nickname` 帧；`JsBridge.PostHello` `:52-76` 已带 `reply_names`，
  新帧照它写）。
- 广播消费：收到 `nickname_updated` / `nickname_color_changed` / `nickname_removed` → 更新本地缓存
  （存**逻辑色 ID**）+ 重画（弹窗历史与客户端窗口都用同一份缓存，显示色经 §4.4 算）。
- **壳模式必须把帧转发给页面**：壳里页面不连 `/ws/web`（`app.js:1051` 提前 return；
  `shell.js:9`、`:159` 的注释），实时帧一律由宿主经桥推给页面（`shell.js:434-447` `onHostMessage`）。
  → 宿主收到 `nickname_*` 后要新增一个桥帧（如 `host.nickname`，照 `JsBridge.PostHello` `:52-76` /
  `PostSession` `:126-134` 写），否则 PC 本地页收不到别人的颜色变化（§5.6.2 第 2 点）。
  **注意**：PC 只是**使用者**，不「拥有」昵称（§0.6）—— 宿主只需维护
  「本机当前选用哪个 `nickname_id`」+ 一份全局昵称字典（供弹窗上色），
  **没有**「本机那条昵称行」这个概念（r3 的「一个 owner 一条 active」已作废）。

**验收（可测）**
1. 全新机器（清掉 `%APPDATA%` 下的 `config.json`）首次运行 → **NAS 一行都不多**
   （`SELECT count(*) FROM nicknames` 与之前相同），界面显示灰临时昵称（`LIVINGROOM-PC` + 逻辑色 `gray`
   `gray`）；点「选用」既有的「妈妈」→ 本地写下 `current_nickname_id`，**NAS 仍无任何变化**（§5.2）；
   点「新建」建一个不重名的 → 201 + 其余端收到 `nickname_created`；建一个重名的 →
   409 `NICKNAME_ALREADY_EXISTS` + UI 引导「直接选用它？」。
2. ★ **断网时四个管理入口全部不可用**（r6 判据）：新建 / 改名 / 删除 / 重新分配颜色都点了 → 都提示
   「未连接服务器」，且本地 `nickname.json` **逐字节不变**（断言：没有新名字、没有新逻辑色 ID、
   **文件里不存在 `sync_state` / `pending` 任何痕迹**）；断网期间只有「选择昵称」能改。
3. ★ **重连不补发**：断网时点过改名（被拒）→ 恢复连接并等 30 秒 → NAS 的 `display_name` **不变**、
   没有任何补发请求（服务端日志无 `nickname_rename_request`）；此时再手动改名 → 正常成功 + 广播。
4. 在线重新分配颜色 → NAS 换了一个**未被占用的逻辑色 ID**（≠ 旧 ID）；另一台 PC 与网页端 1 秒内同步。
5. 同名两台 PC（`ComputerName` 相同）→ 两台都还没选共享昵称时都发**灰临时消息**：
   界面看起来像同一个人（用户已确认可接受，§3.2.1），而 NAS 里**没有任何昵称行**；
   其中一台**创建**了「LIVINGROOM-PC」这个共享昵称后，另一台想同名只能**选用**它
   （自己也建一个 → 409）。UI 无需区分、不需要 `×2` 折叠（§10 已定 9）。
6. `dotnet test pc-agent/FamilyAgent.Core.Tests` 全绿（当前 72 个 `[Fact]`，见 §8）。

**风险 / 回退 / 依赖**
- 风险：① 与 Core 重构的未提交改动（`Sync/`、`Commands/`、`Transport/*` 一批）撞车；
  ② ★ **昵称操作绝不能被 `Outbox` 的补发机制「顺手」重放**：`Outbox` 不落盘（R5）且只在**发送失败**时入队，
     若昵称帧走了 `SendOrQueue`，一次「断网时被拒的改名」可能在重连后被静默补发 → **违反 r6 的离线拒绝**。
     因此昵称帧必须走**直接 `Send`**（离线时根本不构造请求），并在单测里断言「离线期间不会产生待发帧」。
- 回退：PC 端不注册昵称（配置里没有 `nickname_id` 就完全按今天的行为跑，发消息只带 `sender_name` 字符串）——
  服务端本来就能容忍没有昵称的 `reply` 帧（`main.py:879-880` 有设备名兜底）。
- 依赖：Phase 2（协议）+ §8 的 Core 决策 + 未提交的 Core 改动先落地。

---

### Phase 5 —— 统一测试与收尾

**改什么**：把下面的清单做成一份可重复执行的脚本/清单（建议 `tools/test_nicknames_e2e.md` 或扩 `tools/` 下的探针脚本）；
**r6：不写存量回填脚本**（§3.4 定稿为「不建议回填」）。
跑完后补文档（`README.md`、`docs/DESIGN.md`、`docs/GROUP-CHAT-MODEL.md:25/:65` 里的「昵称纯本地」也要改），
并让 `docs/NICKNAME-SYSTEM-SUMMARY.md`（用户摘要）与本文档的结论保持一致。

**验收清单**（规格 §23 Phase 5 的 9 项 → 逐项判据）：

| 场景 | 判据 |
|---|---|
| 新增 | 新名字拿到池子里第一个可用的**逻辑色 ID**；`GET /api/nicknames` 里颜色两两不同 |
| **创建撞名被拒 · 不新增行** | 再建同名 / 另一台同名机器再创建 → **409 `NICKNAME_ALREADY_EXISTS` + `existing_nickname_id`**；库里不新增行、`nickname_id` 与颜色都不变 |
| **撞名不发广播** | 重复 `POST /api/nicknames`（同名，返回 409）→ 三端列表**不**多出条目、**无** `nickname_created` 广播（§5.6.4） |
| **改名撞名被拒** | 改名到一个已被占用的名字 → 409 `NAME_TAKEN`（索引① 强制）；改成没被占用的名字 → 只动 `display_name`、颜色不变 |
| **空库不建任何东西** | 清空 `nicknames` → `GET /api/nicknames?status=active` → `200 {"nicknames": []}`，库里**仍 0 行**（不建「默认用户」）；此时网页端用灰临时昵称发消息（§3.2.1） |
| **多端共用一昵称** | 两台 PC + Web 都用「妈妈」的名义 → **同一个 `nickname_id`、同一个颜色**；另两端是**选用**（本地动作），不是各建一行；三端历史消息颜色一致 |
| **颜色不重复** | 给另一个名字分配「妈妈」已占的**逻辑色 ID** → 被索引②拒绝（走 `NO_AVAILABLE_COLOR` 路径） |
| ★ **逻辑色 / 显示色分离** | 库里 / 协议里 / 广播里 / 快照里的 `color` **全是 `color_0x` 或 `gray`**（grep 不到 HEX）；浅 / 深主题下同一 ID 渲染出不同显示色，PC 与 Web 两端**逐行一致**（§4.4）；未知 ID → 灰兜底不崩 |
| ★ **额度提示** | 14 条 active → 「共享昵称颜色即将用尽（14/16）」；16 条 → 置灰 + 「已达到共享昵称上限，请删除不再使用的昵称后再添加。」；绕过 UI 直接请求 → 503 `NO_AVAILABLE_COLOR` |
| ★ **昵称长度统一 32** | 32 字符的名字在 Web 输入框 / PC 设置页 / HTTP / WS 帧 / DDL 五处**结果一致**（都能建、都能改）；33 字符 → 422 `INVALID_DISPLAY_NAME`（前端也拦，§5.5） |
| **颜色回池** | 删掉「妈妈」→ 行变 `inactive`、颜色立刻可被新名字拿到（`released_color` 出现在响应与广播里） |
| **三浏览器同步** | A/B/手机三个 `/ws/web` 同时在线，A 改「妈妈」的颜色 → B/手机 1 秒内变紫（§5.6） |
| **无 owner 可伪造** | 请求体里塞 `owner_id=abc123` / `owner_type=pc` → **一律忽略**，库里那一行不受影响；`nicknames` 表里也不存在这类列（§5.5 / §6.3） |
| 删除 | 行变 `inactive`、颜色立刻可被新昵称拿到、历史消息文本仍在 |
| 重新分配 | 新**逻辑色 ID** ≠ 旧 ID、不与任何 active 重复；**离线时 PC 与 Web 都拒绝执行并提示**（§5.2） |
| 并发 | 8 线程同时建 8 个**不同名字** → 颜色集合 = 8（§6.4 判据 4） |
| ★ 断线（r6 新口径） | 断网期间 PC 的**新建 / 改名 / 删除 / 重新分配颜色全部被拒并提示**，本地 `nickname.json` 不变、**不产生任何待发帧**；只有「选择昵称」能改 |
| ★ 重连 | 重连后 PC **不补发任何昵称操作**（改名在被拒时就已结束）；`hello` 后只发 `nickname_list_request` 拉整表校正；若当前选用已 `inactive` → 清空 + 回退灰临时 + 提示 |
| ★ 重创同名（§3.4.1） | 建「妈妈」→ 发一条带 `nickname_id` 的消息 → 删「妈妈」→ **重创「妈妈」** → 老消息的 `sender_nickname_id` 与显示**都不变**（旧名字 + 旧逻辑色 ID），新消息指新行；grep 确认**没有任何按 `display_name` 查 `nicknames` 的读取路径** |
| 首次安装 | 新机器 → 显示灰临时昵称（`LIVINGROOM-PC` + 逻辑色 `gray`）→ **NAS 不新增行**；选用既有 / 新建（撞名 409）二选一（§3.3） |
| 灰临时昵称消息 | 省略 `nickname_id` 发消息 → `sender_nickname_id IS NULL` 且 `sender_color = 'gray'`；别的客户端照常显示（灰色圆点 + 名字） |
| 老消息不被当成灰 | 改造前的历史行（两列都 `NULL`）→ 前端走哈希兜底，**不显示成灰色**（判定只看 `sender_color = 'gray'`） |
| ★ 存量回填（r6 结论） | **不跑**：老行保持两列 `NULL` → 前端哈希兜底，观感与今天一致（§3.4）。**不要**创建 `tools/backfill_nickname_snapshot.py`；若有人坚持跑，必须先读 §3.4 第 3 条 + §3.4.1（会把老消息误挂到重创的同名新行） |
| 多个 PC | 3 台 PC 名字各不相同时，昵称/颜色各自独立；一个改色不影响别人 |
| PC + Web 同时操作 | 同时改不同昵称 → 都不重色；同时改**同一个**昵称 → 串行，最终一致（无中间脏态） |
| 兼容 | 没有 `nickname_id` 的老客户端（老 exe / 老浏览器缓存）仍能发消息（`sender_name` 字符串路径不变） |

**回退**：`nickname.enabled=false` + PC 端不写昵称配置 → 全链路回到今天。
**数据回退**：`nicknames` 表可整体 `DROP`；`messages.sender_nickname_id` / `messages.sender_color` 置回 `NULL`。

---

## 8. 与正在进行的 Core 重构的关系

### 8.1 现状（事实）

- `pc-agent/FamilyAgent.Core`：`net9.0`（不带 `-windows`），**零 `PackageReference`**
  （`FamilyAgent.Core.csproj` 全文只有 `PropertyGroup`），目标就是「能在 Linux 编译/测试」。
- `pc-agent/FamilyAgent.Core.Tests`：`net9.0` + xunit；当前 `[Fact]`/`[Theory]` 共
  **72 个**（`ConnectionEpochTests` 4 + `CoreContractTests` 6 + `FrameCodecTests` 14 +
  `MessageLifecycleTests` 14 + `PlatformCapabilityTests` 22 + `SyncServiceTests` 12）。
- CI 现状：`.github/workflows/` **只有** `build-windows-agent.yml`（Windows 编译 + 打包 + Release），
  **还没有** Linux 跑 Core 单测的 job（`docs/CORE-REFACTOR-PLAN.md:370` 提到要加 ubuntu job，属计划未落地）。
  **本机装了 .NET 9 SDK**，只是不在默认 PATH（`which dotnet` 为空会误导）——
  用 `export PATH=/vol1/<uid>/workspace/dotnet9:$PATH` 后 `dotnet --version` = `9.0.318`，
  可直接 `dotnet build` / `dotnet test` 本地验证（实测 Core 单测 72 个全绿，约 0.3 秒/轮）。
  **Core 相关改动必须本地编译验证，不要靠 CI 猜。**
- Core 里已有可复用的机制：`Transport/Outbox.cs`（内存队列，`:27-32`）、
  `Transport/ConnectionManager.cs:478-503`（`SendOrQueue` / `Ack` / `Reply` 的帧构造）、
  `Sync/SyncService.cs` + `Sync/HistoryBackfill.cs`（重连补齐）、
  `Messaging/MessageStore.cs`（`JsonlMessageStore` → `%APPDATA%\FamilyAgent\messages.jsonl`，`:126`/`:158-161`）。

### 8.2 建议：昵称同步放 **Core**，宿主只做「显示 + 转发」

| 放哪 | 判断 |
|---|---|
| **Core（建议）** | 昵称相关的判据（**在线 / 离线判定（离线一律拒绝管理操作）**、整表缓存的合并与校正、当前选用失效回退、错误码映射、"同一次操作不重复生效"）全是**纯逻辑**，正是 Core 存在的理由（可 Linux 单测）；它复用 `ConnectionManager` 的连接状态与 `hello` 之后的整表拉取时机，不用在宿主里再造一套 |
| 宿主（`FamilyAgent`） | 只保留：把状态接到界面（`JsBridge` 加 `host.nickname` 帧）、把用户动作转成 Core 调用、写设置页 UI。**不承载状态机** |

落地建议（具体到文件）：

| 新东西 | 位置 | 说明 |
|---|---|---|
| `NicknameState`（`nickname_id / display_name / color`，**`color` 是逻辑色 ID**；**没有 `sync_state`** —— r6 无 pending） | `Core/Nicknames/NicknameState.cs` | 不可变记录 + `With(...)`，照 `MessageStore.cs:23-88` 的 `MessageRecord` 风格 |
| `INicknameStore` + `JsonNicknameStore` / `MemoryNicknameStore` | `Core/Nicknames/` | 落 `%APPDATA%\FamilyAgent\nickname.json`（独立文件，**不塞进 `AgentConfig`**，见下） |
| `NicknameService` | `Core/Nicknames/NicknameService.cs` | 与 NAS 交互：**在线时**发 `nickname_list_request / nickname_create_request / nickname_rename_request / nickname_reassign_color_request / nickname_delete_request`，消费 `nickname_list_response` / `nickname_list_sync` / 四个增量广播帧；★ **离线时四个管理操作直接抛「不可用」错误（不排队、不落 pending）** —— 这就是 r6 删掉状态机之后剩下的那点逻辑（可单测） |
| 帧 DTO | `Core/Protocol/Frames/NicknameFrames.cs` | 照 `MessageFrame.cs` 的强类型风格（不要裸 `JsonElement`，`CORE-REFACTOR-PLAN.md:129` 点过这个坑） |
| 宿主桥帧 | `pc-agent/FamilyAgent/JsBridge.cs` | 新增 `host.nickname` 推送（照 `PostHello` `:52-76` / `PostSession` `:126-134`） |

**为什么不放 `AgentConfig`**：
`AgentConfig` 的自我定位是「连接要用哪些参数 + 设置界面读写哪些字段」（`AgentConfig.cs:13-16`），
它已经被 `CoreContractTests`（6 个测试）盯着，且设置页表单与保存路径（`App.xaml.cs:530-610`）耦合；
而昵称缓存是**运行时状态**（整表 + 本机选用），不是连接参数 —— 放进去会让「配置损坏回默认」这条路径
（`Load()` `:89-112` 的 catch 吞异常）把它悄悄清掉。**建议独立文件**，`AgentConfig` 一个字段都不加。
（r6 顺带一个好处：**没有 `sync_state` 之后，这类「被回默认吞掉」的风险本身就小了一圈**。）

**两条硬约束（必须遵守）**：
1. **不要给 Core 加 HTTP 客户端**：方案（§5.4）是全部走 WS 帧（**r6 已定，见 §10 已定 14**），
   BCL 一个 `HttpClient` 都不用。昵称帧用**直接 `Send`**（不进 `Outbox`），离线时不构造请求；
   为便于单测，`NicknameService` 仍应依赖一个可替换的传输接口（`INicknameTransport`）。
2. **不要动 `AgentClient.cs` 那套已搬迁的旧路径**：Core 重构把老 `AgentClient` 的内容按
   `CORE-REFACTOR-PLAN.md §4` 的映射表搬进 `Transport/` `Messaging/` `Sync/` `Commands/`，
   昵称代码应该是**新增文件**，不是再往 `ConnectionManager` 里塞业务。

### 8.3 排期关系

- 昵称 Phase 4 **依赖**：v0.16.0（`616551e`）已把 Core 那批未提交改动（`Sync/`、`Commands/`、
  `Devices/`、`Platform/` 与 `ConnectionManager` 等）落地，**这一依赖现已满足**；
  昵称 Phase 4 直接基于 v0.16.0 开新分支即可。
- **可以并行推进**：昵称 Phase 1-3 完全不碰 `pc-agent/`（服务端 + 网页端），
  这三段可以现在就做，与 Core 重构的后续阶段（Phase 5 收尾）互不干扰。

---

## 9. 本轮不做的事（防跑偏）

1. **不改任何代码**。本文件（+ 用户摘要 `docs/NICKNAME-SYSTEM-SUMMARY.md`）是勘察 + 方案，施工按 §7 的 Phase 逐个提 PR。
2. **不碰别人的在途改动**：v0.16.0（`616551e`）已把那批 `pc-agent/**` 与两个 CSS 的改动提交；
   施工时如果工作区又出现别处的未提交改动（`git status`），**不回退、不格式化、不"顺手"改**，
   只在自己新增的文件里动手。
3. **不做 Android**（规格 §22 只要求「保留扩展能力」）：r5 下 Android 就是**又一个客户端**（§2.C3）——
   只要它能连 `/ws/device`、能调 §5.3 的 HTTP API，就能**选用**任意 active 昵称；
   **数据模型与协议无需为它改一个字**，也**不写一行 Android 代码**。
4. **不做用户体系 / 登录 / 角色**。`permissions.py:4` 明确「没有用户、没有角色」，
   本方案只加 `nickname.manage` 权限常量（为将来角色留位），不引入账号。
5. **不重写颜色算法本身**：哈希算法保留为**兜底**（`app.js:336-344`），不删。删除它会让老消息与
   `nickname.enabled=false` 的回退路径一起坏掉。**r6**：池内颜色的**身份**一律是 NAS 的逻辑色 ID
   （哈希只服务老消息与回退路径）；显示色由 §4.4 的映射表算。
6. **不改 `devices.name` 与 `AgentConfig.DeviceName` 的关系**：设备名继续是设备名，
   昵称是昵称；两者首次来源相同（ComputerName）但**不合并存储**。
7. ★ **不做「显示色入库」**（r6 强化）：圆点 / 头像底 / 字色的变体**只在客户端渲染时**算（§4.4），
   **不进协议、不进 DB**；**DB 与协议里只有逻辑色 ID**（`color_01`…`color_16` / `gray`），
   连基础色值的 HEX 都不落库（HEX 只是 §4.3 的常量表 + §4.4 的渲染起点）。
8. **不做周期性昵称收集**（规格 §21 明令禁止）：同步只在启动/重连/显式请求时发生。
9. **不做昵称的历史审计页**：`status='inactive'` 的行能查（§5.3 第 6 个端点），但不做专门的审计 UI。
10. **不动既有消息状态机**：`message_targets` 的逐设备状态（`services/messages.py:24-25`）与本方案无关。
11. **不做服务端「当前昵称」指针 / 不做跨浏览器同步**：不实现 `GET` / `PUT /api/web/sender` 之类端点
    （§0.10 / §5.2 / §10 已定 3）。
12. **不做「空表自动建默认昵称」**：`GET /api/nicknames` 空就是空（§0.9；r4 的 `ensure_default()` 见附录 B）。
13. **不做「同名自动加后缀」/ 自动合并**：撞名一律显式 409 —— 创建 `NICKNAME_ALREADY_EXISTS`、
    改名 `NAME_TAKEN`（最终说明 §3 / §10 已定 2）。
14. ★ **不做存量回填**（r6 定稿）：老消息保持两列 `NULL`、前端走哈希兜底；**绝不**按名字猜身份，
   也**绝不**因为「正好存在一个同名活跃行」就把老消息挂上去（重创同名场景，§3.4 / §3.4.1）。
15. ★ **不做离线昵称管理**（r6）：不写 pending、不排队、不落盘待发、不在重连时补发 ——
   离线时四个管理入口**直接拒绝并提示**（§0.13 / §5.2）。
16. ★ **不在协议 / 库里放 HEX 色值**（r6）：凡 `color` 字段一律逻辑色 ID（§4.4）。
17. ★ **不做「浅色 / 深色各一套逻辑色 ID」**（r6）：主题变体是**渲染结果**，不是新 ID；
    灰 `gray` 也一样（§3.2.1 / §4.4）。

---

## 10. 已定问题清单（r6：待拍板项已清空）

**已定（r5 以《最终需求变更说明》为准，r6 追加 13–20 —— 施工时按此实现，不再讨论）**

1. **昵称中心数据模型**：`nicknames` 只有 `nickname_id / display_name / color / status / created_at /
   updated_at` 六个字段，**没有 `owner_type` / `owner_id`**（用户原话：「系统根本不关心『哪个客户端拥有这个昵称』」）。
   PC / Web /（将来的）Android 都只是**使用者**，同一个昵称可以同时被多个客户端使用（§0.6 / §2.C3）。
2. **同名 = 同一个昵称，但创建重复名会被显式拒绝**：`POST /api/nicknames` 撞名 →
   **409 `NICKNAME_ALREADY_EXISTS`**（响应带 `existing_nickname_id`），客户端引导用户**直接选用既有的那个**；
   **不自动加后缀、不静默复用**（最终说明 §3；r4 的 `ensure()` 幂等已作废，附录 B B6）。
3. **Web 的「当前使用哪个昵称」= 浏览器本地（本文件定稿；原 P6，也是最终说明 §15 的 `current_nickname_id`）**：
   存在 `localStorage` 的 `fm.lastSender`（值 = `nickname_id`）；**服务端不存、不设全局指针**，
   `GET` / `PUT /api/web/sender` **不实现**。理由：①最终说明 §15 把 `current_nickname_id` 划归客户端，
   Web 只是一种客户端；②Web 虽是公共端，但「一个浏览器 profile」天然就是一份页面状态 ——
   服务端存一份会让 A 浏览器选了「爸爸」把 B 的界面也改掉（谁赢没有语义）；
   ③与今天的 `fm.lastSender` 行为一致，零回归、少 2 个端点。
   **代价（必须写进 UI 说明）**：同一个人换个浏览器/清掉浏览器数据要再选一次（回到「未选」= 灰临时「默认用户」）。
   **PC 侧同构**：本地配置存 `current_nickname_id`（§5.2 / §0.10）。
4. **无共享昵称时 = 灰临时昵称**：PC 用 `ComputerName`、Web 用固定中性文案「默认用户」，逻辑色 `gray`（基色 `#8A8A8A`），
   `nickname_id = NULL`；**不进池、不占色、不参与同步**（最终说明 §9 / §11，§3.2.1）。
   服务端**不代任何客户端创建昵称**（`GET /api/nicknames` 空就是空；r4 的懒建已作废，附录 B B7）。
5. **消息快照三字段全部落库** → `messages` 加**两列**：`sender_nickname_id` + `sender_color`
   （`sender_name` 就是 `display_name_snapshot`，不动、不改名）（最终说明 §11 / §3.2）。原 P1、P2 按此收口。
6. **颜色池 = 最终说明 §5 的 16 色 + §4.2 四条渲染规则**（圆点按主题取变体 / 字色自适应 / 头像底变体 /
   下拉不用色值当字色）（§4.2）。原 P4（12 色 vs 16 色）**已定**，不再讨论。
   ★ **r6 追加**：池子按**逻辑色 ID** 定义（`color_01`…`color_16`），**库 / 协议 / 广播 / 快照一律存 ID**；
   HEX 只是「基础色值」，显示色由客户端按 `theme + ID` 算（§4.3 / §4.4）。
7. **设备是设备，昵称是昵称**：删设备**不许**动 `nicknames`（R3 / §3.5）；客户端身份只活在连接层 / 设备管理层（§0.7）。
8. **r2/r3/r4 的旧设计全部作废**：owner 模型、`nickname_colors` 颜色归属表、`NOT_YOURS`、
   `/api/nicknames/web` 这类带 owner 的路径、`ensure()` 按名幂等创建、Web 懒建默认用户、
   `affected_nickname_ids` 广播字段 —— 逐条见**附录 B**（照着重查、别复活）。
9. **改名撞名 = 409 `NAME_TAKEN` + UI 引导**（原 P9）：明示「这个名字已被占用：要么换个名字，
   要么直接选用既有的那个昵称」—— 昵称中心模型的必然推论（改名 ≠ 合并，§2.C7 取舍 1）。
10. **昵称被删后，仍在选用它的客户端回退灰临时**（原 P8，最终说明 §11）：客户端收到 `nickname_removed`
    且命中自己的 `current_nickname_id` → 清空选用 + 回退灰临时 + 一句提示；
    服务端对已 `inactive` 的 `nickname_id` 写入 → **409 `NICKNAME_INACTIVE`**（§3.5 / §5.3）。
11. **鉴权**（原 P11）：一律家庭口令 `WebAuth` + 新增 `nickname.manage` 权限常量占位
    （`permissions.py:23-37` 的既有风格，为将来角色留位）；**UI 必须明说「昵称是全局对象」**
    （改一个名字影响所有使用者，R1）。
12. ★ **活跃昵称上限 = 16 + 额度提示文案**（原 P12；r6 定稿）：上限 = 池子大小（16 条 active），
    **不设额外数量上限**；池满时新增 → `503 NO_AVAILABLE_COLOR`，**绝不重色**（规格 §11）。
    **提示文案（用户给定，逐字用）**：`active ≥ 14` → 「**共享昵称颜色即将用尽（14/16）**」（括号用真实计数）；
    满 16 → 「**已达到共享昵称上限，请删除不再使用的昵称后再添加。**」并置灰新建入口（§4.3 / §5.5）。

**已定（r6 拍板，逐条对应上面 1–12 之后的新编号）**

13. **昵称长度统一 32**（原 P3，r6 拍板）：**NAS / API / PC / Web 统一最大 32 个字符，前端也统一校验** ——
    DDL `CHECK (length(trim(display_name)) BETWEEN 1 AND 32)`（§3.1）、服务层 32（`main.py:347` 不变）、
    WS 帧 32、Web 输入框 `maxLength` 16→32（`app.js:1157`）、PC 写入前按 32 校验
    （`AgentConfig.cs:127-135` 现在只 `Trim`，要补长度）（§2.C2 / §5.5）。
14. **PC 端昵称写操作走 `/ws/device` 帧**（原 P5，r6 拍板）：**不新增 HTTP 写接口**，复用设备 token 鉴权与连接，
    Core 仍保持**零 `PackageReference`**。
    ⚠ **但昵称帧不走 `Outbox`、不补发**（见第 15 条）：在线时**直接 `Send`**，离线时根本不构造请求（§5.4 / §8.2）。
15. ★ **完全拒绝离线改昵称**（原 P7，r6 拍板，**本轮最大的一处简化**）：**新建 / 改名 / 删除 / 重新分配颜色
    四个管理操作必须在在线时做**，离线时不可用并**明示原因**；**只有「选择昵称」`current_nickname_id`
    可以离线做**（它本来就不上服务端）。
    ⇒ **整套 pending 机制删除**：`nickname_sync_state = pending`、离线改名队列、重连提交 pending、
    以及 r5 §4 / §17 里「PC 离线修改昵称」的全部推演（含「重连时目标名字已被占用」这个状态）
    **一并作废**（附录 B B16）。
16. ★★ **逻辑色 / 显示色分离**（r6 拍板，**最大的一处设计升级**）：`nickname.color = color_01` 这类**逻辑色 ID**；
    客户端按 `theme + color_01` 得到最终 CSS / ARGB。⇒ **DDL 的 `CHECK` 改为枚举 16 个 ID**（§3.1）、
    **所有 API / 广播 / 帧 / 消息快照的 `color` 都传逻辑 ID**（§3.2 / §5.3 / §5.4）、
    16 色池 = 「逻辑色 → 基础色值」常量表（§4.3）、主题变体（浅 `mix(#000,35%)` / 深 `mix(#FFF,30%)`）
    **不进协议、不进库**、客户端映射表见 **§4.4**（PC / Web 两端共用，含灰 `gray`）、
    重新分配颜色 = **换一个未被占用的逻辑色 ID**（池满仍 `503 NO_AVAILABLE_COLOR`）。
17. ★ **历史冻结 = 硬规则 + 重创同名**（r6 拍板，升格为硬规则第 8 条）：昵称与颜色的修改**只影响未来消息**；
    三快照列齐全、**读取绝不 JOIN**；**重新创建同名昵称不得污染历史** ⇒
    **`nickname_id` 绝不按 `display_name` 去查**（§3.4.1）。
18. **存量回填：不做**（r6 定稿，收紧了 r5 的「可选回填 / 想保持原样就别跑」口径）：
    否决理由三条，决定性的是「与重创同名叠加会把老消息误挂到新行」（§3.4）。
19. **昵称改动记入 `events` 表（审计）**（原 P10，r6 拍板）：**记录** —— 成本≈一行 `db.log_event`
    （`db.py:214-218`，与设备 / 解锁同一套）；「谁什么时候把颜色改了」是排查昵称问题的第一现场。
20. **灰临时昵称的逻辑色 ID = `gray`**（r6 拍板细节）：基色 `#8A8A8A`；不进池、不在 DDL 枚举里、
    判灰只看 `sender_color = 'gray'`（§3.2.1）。

**定稿对照（原「待拍板」4 条的去向 —— r6 已全部拍完，本节没有待拍板项）**

| 历史编号 | 问题 | 定稿结论 |
|---|---|---|
| 原 P3 | `display_name` 长度上限 | **统一 32** → 已定第 13 条 |
| 原 P5 | PC 端昵称写操作走哪条通道 | **`/ws/device` 帧，不新增 HTTP 写接口** → 已定第 14 条 |
| 原 P7 | PC 离线改名、重连时名字被占用怎么办 | ★ **问题消失**：**完全拒绝离线改昵称**（用户拍板）—— 离线根本发不出改名，也就不存在「重连撞名」；原推演作废（附录 B B16），替代规则见已定第 15 条 |
| 原 P10 | 昵称改动要不要审计留痕 | **记录**（`events` 表）→ 已定第 19 条 |

**已作废 / 已并入的旧项（r2/r3/r4 遗留，施工时不要再讨论）**

| 旧项 | 现在的结论 |
|---|---|
| 旧 P1 `messages` 加不加 `sender_nickname_id` | **已定第 5 条**：加（还要加 `sender_color`），快照按最终说明 §11 落库 |
| 旧 P2 历史消息颜色跟随当前色 vs 冻结发送时色 | **已定第 5 条**：冻结（`sender_color` 快照）—— 最终说明 §11 明令 |
| 旧 P4 颜色池（12 色 vs 16 色） | **已定第 6 条**：16 色 + 四条渲染规则（鉴权面那半 → 已定第 11 条） |
| 旧 P6 Web「当前选中昵称」放哪 | **已定第 3 条**：浏览器本地，删掉那两个端点 |
| 旧 P6 删设备时级联软删昵称 | **已定第 7 条**：什么都不做 |
| 旧 P8「网页端不会没有昵称」 | **已定第 4 条**：网页端可以没有共享昵称，用灰临时「默认用户」 |
| 旧 P9 同名两台 PC 的 UI 区分 | **问题消失**：同名只有一行（§2.C7） |
| 旧 P11 把 web 单例常量钉进表级 CHECK | **作废**：没有单例、没有 `owner_id` 列，CHECK 无对象（附录 B B3） |
| 旧 P12 默认发送人要不要跨浏览器同步 | **已定第 3 条**：不做跨浏览器同步（服务端无指针） |
| 旧 P13 颜色池硬约束的数量上限 | **已定第 12 条**：不设上限，池满 503 |
| 旧 P14 改名撞名提示 | **已定第 9 条**：409 + 引导选用 |
| 旧 P15 同名多行要不要折叠显示 | **问题消失**：同名只有一行（§5.6.4 / §2.C7） |
| **r5 的「离线 pending 昵称同步」**（`nickname_sync_state = pending` / 离线改名队列 / 重连补发） | ★ **作废**（r6 用户拍板「完全拒绝离线改昵称」）：四个管理操作必须在线，只有「选择昵称」可离线（§0.13 / §5.2 / §10 已定 15；反例 B16） |
| **r5 的「`nicknames.color` 存 HEX」**（DDL `GLOB` 校验 `#RRGGBB`、协议传 HEX） | ★ **作废**（r6 拍板）：`color` 只存**逻辑色 ID**，DDL 改枚举 `CHECK`，协议 / 快照一律传 ID（§3.1 / §4.4 / §10 已定 16；反例 B17） |
| **r5 的「可选存量回填」**（想保持原样就别跑） | ★ **收紧为「不做」**（r6 定稿）：与重创同名叠加会误挂（§3.4 / §3.4.1 / §10 已定 18） |

---

## 附录 A · 证据索引（一行一条，方便复查）

```
server/db.py:22,30-127,130-166,169-183,190-211,214-218      schema / 迁移 / 单连接+RLock / execute 自动 commit
server/main.py:114-123,163-173,230,237,291-316,319-340      WebAuth / /api/config / 广播原语
server/main.py:346-352,355-375,378-400,403-413              消息 HTTP 面
server/main.py:729-822,825-959,965-986                      WS 两端点 / 设备帧分发 / Web 上行只有 ping
server/hub.py:28,150-169,179-187                             web_clients 集合 / send_to_device / broadcast_web（遍历全部 Web 订阅者）
server/services/messages.py:34-54,135-176,183-197           消息写读 / public_message / group_history / reply
server/services/devices.py:21-53,95-101,129-132              enroll / verify_token / 物理删设备
server/permissions.py:8-11,24-38                            无用户无角色 / 权限常量
server/config.py:20-35,25                                    _DEFAULTS / history_limit
server/run.py:76-77                                          单进程 uvicorn
docs/PROTOCOL.md:3-4,12-26,56-62,157-168,172-186,190-221,240-252
docs/GROUP-CHAT-MODEL.md:25,65                              昵称纯本地（要被本方案改掉）
docs/DESIGN-TOKENS.md:76-78(配色本地),122-146                12 色 + 哈希契约
web/static/app.js:57-70,331-344,384,869-895                 API 封装 / 12 色哈希 / 下拉文本色
web/static/app.js:1047-1085                                   connectWS + handleServerFrame（壳里 :1051 不连 /ws/web）
web/static/app.js:1088-1121,1124-1187                        fm.names / fm.lastSender 读写点 + 多昵称 UI
web/static/chat.js:1-2,21-42,44-53,56-63                     共用气泡组件 / 副本算法 / isOwn
web/static/shell.js:9,159,434-447,371-411,596-604,657-707     壳不连 /ws/web（走桥）/ 弹窗渲染 / nickColorOf
web/static/style.css:518-535,865-885                         圆点与头像的昵称色落点
web/shell/pc.css:172-190                                     头像「唯一一处非令牌色」
web/index.html:229,392-404                                   发送区下拉 / 本地昵称设置卡片
web/shell/pc.js:30,36,240-268,427-493,560-567                PC 本地页昵称 / 设置保存
pc-agent/FamilyAgent.Core/Config/AgentConfig.cs:23-58,66-81,114-153
pc-agent/FamilyAgent.Core/Transport/ConnectionManager.cs:208-236,478-503
pc-agent/FamilyAgent.Core/Transport/Outbox.cs:27-32         内存队列（不落盘）
pc-agent/FamilyAgent.Core/Messaging/MessageStore.cs:23-88,126,133-161,399
pc-agent/FamilyAgent.Core/Protocol/Frames/MessageFrame.cs:5-22
pc-agent/FamilyAgent.Core/Protocol/HistoryEntry.cs:8,19,26-46
pc-agent/FamilyAgent.Core/Sync/{SyncService,HistoryBackfill,ISyncConnection,SyncPhase}.cs
pc-agent/FamilyAgent/JsBridge.cs:52-76,126-134,403-416,513   桥的 host.* 帧与 web.save_config
pc-agent/FamilyAgent/App.xaml.cs:530-610                     本机设置唯一写入点（回复昵称）
docs/CORE-REFACTOR-PLAN.md:303-370,397-420                   Core 分层与迁移映射表 / 单测工程
.github/workflows/                                            只有 build-windows-agent.yml（无 Linux 测 Core 的 job）
```

**复查用命令**

```bash
cd /vol1/<uid>/workspace/family-message
grep -rn 'localStorage' web/static/*.js web/shell/*.js            # 昵称/主题的本地存储点
grep -rn 'nickColor\|NICK_COLORS' web/                            # 颜色算法与消费点
grep -rn 'reply_names\|reply_name' pc-agent/ web/                 # PC 昵称字段全链路
grep -n 'broadcast_web' server/main.py                            # 现有广播类型全集
grep -n 'web_clients\|async def ws_web' server/hub.py server/main.py  # Web 广播的收件人集合与端点
grep -n 'owner_type\|owner_id\|nickname_colors\|NOT_YOURS' docs/NICKNAME-SYSTEM-PLAN.md  # 只应命中「已作废 / 走过弯路」标注处
grep -n 'sync_state\|pending 昵称\|离线改名队列' docs/NICKNAME-SYSTEM-PLAN.md  # 只应命中「已作废 / 反例」标注处（B16）
grep -n "color_0[1-9]\|color_1[0-6]\|'gray'\|\"gray\"" docs/NICKNAME-SYSTEM-PLAN.md | head -40  # 逻辑色 ID 的正确用法
grep -n 'HEX' docs/NICKNAME-SYSTEM-PLAN.md   # 只应出现在「不存 HEX / 只作为基础色值」的说明处

# 昵称表的两条 partial unique index（建表后跑；应打印 2 行，两条 sql 里都带 WHERE）
python3 -c "import sqlite3;c=sqlite3.connect('server/data/family.db');\
print([r[0] for r in c.execute(\"select sql from sqlite_master \
where name like 'ux_nicknames%'\")])"
python3 -c "import sqlite3;c=sqlite3.connect('server/data/family.db');\
print([r[0] for r in c.execute(\"select name from sqlite_master where type='table'\")])"

# 昵称表的颜色列：应看到 CHECK (color IN ('color_01', … ,'color_16'))，不该看到 GLOB / HEX
python3 -c "import sqlite3;c=sqlite3.connect('server/data/family.db');\
print(c.execute(\"select sql from sqlite_master where name='nicknames'\").fetchone())"
```

---

## 附录 B · 反例清单（照着反向检查，别复活）

**下面这些写法一律禁止**：任何一处（文档 / DDL / API / 帧 / 前端 / 测试）出现，就按「设计错误」打回。
`grep` 自查命令见附录 A 末尾。

| # | 反例（禁止的写法） | 为什么禁止 | 现在的正确做法 |
|---|---|---|---|
| B1 | `PC-001 / 妈妈 / 红` **和** `PC-002 / 妈妈 / 蓝` 两条并存，再写一套「颜色对齐 / 同名共色」逻辑把它们刷成同色 | 这就是「一个名字多个对象」；对齐逻辑永远补不完（最终说明 §16 明令禁止） | 一个活跃 `display_name` **只有一行**（索引①）；第二台机器想同名 → **选用同一行**（§2.C7 / §5.2） |
| B2 | `nickname_colors` 颜色归属表（连同 `ux_ncolors_name_active` / `ux_ncolors_color_active`） | 它是为「同名多行还要共色」发明的；颜色本来就是昵称自己的属性 | 颜色就在 `nicknames.color` 一列（§3.1 / §4.3）；整张表删除 |
| B3 | `nicknames` 表出现 `owner_type` / `owner_id` / `device_id` / `client_id`，或表级 CHECK `ck_nicknames_web_singleton` | 最终说明 §2 禁止归属字段：「某个客户端拥有某个昵称」这个概念不存在 | 6 列模型（§3.1）；「谁在用」是**客户端本地状态**（§5.2 / R8） |
| B4 | 「Web 是一个身份 / Web 单例」的建模，以及 `/api/nicknames/web` 这类带 owner 的子资源 | 路径维度本身就是 owner 概念；Web 只是「能选用任意共享昵称的公共客户端」 | 所有端点都是全局的（§5.3），没有 owner / 设备维度 |
| B5 | `NOT_YOURS`（「这不是你的昵称」）错误码，或「谁创建的才能改」 | 昵称是全局对象；鉴别方式是家庭口令（+ `nickname.manage`），不是归属 | 任何客户端都能改，改完广播给所有客户端（§5.1 / §5.3） |
| B6 | `ensure()` / 「按名幂等创建」：撞名时静默返回既有行（HTTP 200 + `existing:true`） | 幂等把「创建」与「选择」在 UI 上混成一件事，用户分不清自己拿的是哪一条（最终说明 §3） | 撞名 → **409 `NICKNAME_ALREADY_EXISTS` + `existing_nickname_id`**，UI 引导「直接选用它？」（§3.3 / §6.2） |
| B7 | Web 懒建默认用户（`ensure_default()`：空表时服务端建一条「默认用户」） | 表里多一条用户从没创建的对象 + 白占一个色位 + 多一层并发幂等推理（§6.2） | `GET /api/nicknames` **纯读**，空就是空；网页端用**灰临时**「默认用户」发消息（§0.9 / §3.2.1） |
| B8 | 广播里带 `affected_nickname_ids: [...]`，或「同名多行一起变色」的事务补丁 | 那是「一个名字多个对象」的补丁；现在一条广播就对应一行 | 五个事件之一 + `{"nickname": {...}}` 载荷（§5.4 / §5.6.4） |
| B9 | 服务端保存「当前选中的昵称」（`GET` / `PUT /api/web/sender`，或任何全局指针 / 会话字段） | 多浏览器会互相覆盖（谁赢没有语义）；最终说明 §15 把它划给客户端 | 每个浏览器各存 `fm.lastSender`（= `nickname_id`）；PC 存本地配置（§0.10 / §10 已定 3） |
| B10 | 把灰色本地临时昵称写进 `nicknames` 表（给它 `nickname_id`），或把 `gray` 放进 `COLOR_POOL` | 灰临时昵称不进共享池：不占色、不参与同步、不影响共享昵称数量（最终说明 §9） | 灰是**客户端本地**默认显示名 + 服务端常量 `LOCAL_TEMP_COLOR_ID = "gray"`；`pick_first_available()` 永远取不到它，DDL 枚举里也没有它（§3.2.1 / §4.3） |
| B11 | 用 `sender_color IS NULL` 判定「灰临时昵称」 | `NULL` 是**二义**的：灰临时消息与改造前的老消息都是 `NULL` → 老消息会集体变灰 | 判定只看 `sender_color = 'gray'`（**逻辑色 ID**）；老行（`NULL`）走哈希兜底（§3.2.1 / §3.4） |
| B12 | 删设备时级联软删「它的」昵称；或把 `AgentConfig.DeviceName` 当成本机昵称的权威存储 | 昵称与设备解耦；`DeviceName` 只是**灰临时显示名**的来源之一，设备名仍归设备体系 | 删设备不动 `nicknames`（§3.5 / R3）；本机「用谁的名义」只由 `current_nickname_id` 决定 |
| B13 | 撞名自动加后缀（`LIVINGROOM-PC-2`），或自动合并到别人那一条 | 会造出用户没要过的名字，还掩盖「其实是同一台机器」的语义（最终说明 §3） | 显式 409：创建 → `NICKNAME_ALREADY_EXISTS`（引导选用）；改名 → `NAME_TAKEN`（引导换名）（§5.3 / §10 已定 9） |
| B14 | 拿 `display_name` 当稳定身份（历史消息靠名字反查昵称行） | 改名后历史会串色 / 串人；规格 §2 明确反对 | 身份是 `nickname_id`；历史靠**快照**（`sender_nickname_id` + `sender_name` + `sender_color`），读取时**不 JOIN**（§3.2 / §2.C1） |
| B15 | 客户端自己算**颜色身份**（哈希 / 本地色表当权威）当权威，或把颜色写回 `localStorage` | 颜色的**分配**是 NAS 的权威（硬约束 §0.3 / §0.4） | 身份/分配来自 NAS 的**逻辑色 ID**；**显示色由客户端按 §4.4 算**（这是规定动作，不是「自算颜色」）；哈希只留作**老消息兜底**（§9 第 5 条） |
| B16 | ★ 离线改昵称：`nickname_sync_state = pending`、离线改名队列、`hello`/心跳里补发 pending、`NAME_TAKEN` 的「重连冲突」状态 | r6 用户拍板「**完全拒绝离线改昵称**」：离线改名会引入「重连时名字被占用」的二义状态，且客户端没有颜色池权威 | 新建 / 改名 / 删除 / 重分配颜色**必须在线**；离线时禁用入口 + 明示原因，**不排队、不落 pending、不补发**；只有「选择昵称」可离线（§0.13 / §5.2 / §7 Phase 4） |
| B17 | ★ 把 HEX 存进 `nicknames.color` 或 `messages.sender_color`，或在协议 / 广播里传 `#RRGGBB` | 会导致 NAS 得为浅 / 深主题各存一套色，且客户端无法按主题正确渲染（r6 拍板：逻辑色 / 显示色分离） | `color` 一律是逻辑色 ID（`color_01`…`color_16` / `gray`）；DDL 枚举约束；显示色由客户端按 §4.4 算（§3.1 / §3.2 / §4.4） |
| B18 | ★ 用 `display_name` 反查 / 回填 `nickname_id`（`JOIN nicknames ON display_name = sender_name`、存量回填脚本） | 「重创同名」场景下老消息会被挂到**新行**（删掉「妈妈」再建「妈妈」= 新 `nickname_id`），历史被污染 —— 违反硬规则第 8 条（§3.4.1） | 历史只读本行快照三列；`sender_nickname_id` 只用于「取那一行」，**永不参与名字匹配**；**不做回填**（§3.2 / §3.4 / §3.4.1） |

**一句话自检**：凡出现「某个客户端**拥有**某个昵称」「两个同名昵称**颜色不同**」「服务端**替你**创建昵称」
「昵称改动**只影响一个人**」「**离线**也能改昵称」「历史颜色 / 身份靠**名字**反查」这六种说法的设计，一律打回。
