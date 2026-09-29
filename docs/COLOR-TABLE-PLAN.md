# 昵称颜色表配置 · 勘察与分阶段方案（**P1–P4 已全部完成并随 v0.19.1 发布**）

> 需求原文（v0.18.0 实测反馈第 4 条）：
> **「网页版开放昵称颜色表配置吧，可以自行添加删减颜色（HEX RGB 格式）、人为选择昵称对应颜色」**
>
> 本文只做**勘察 + 方案 + 决策点**，按规矩**先不动代码**（`docs/CORE-REFACTOR-PLAN.md` 的项目纪律）。
> 相关既有规格：`docs/NICKNAME-SYSTEM-PLAN.md` §3.4.1（历史冻结）、§4.3/§4.4（逻辑色 ID 与显示色）、
> `docs/PROTOCOL.md` §2（快照）、§6（HTTP 端点）。

---

## 1. 现状勘察（都是实测出来的事实，附文件位置）

| # | 事实 | 位置 |
|---|---|---|
| 1 | 色表是**逻辑色 ID → 基础 HEX** 的 16 色常量表；入库 / 协议 / 快照里**只有 ID** | `server/nicknames.py:44-55`（`LOGICAL_COLORS` / `COLOR_POOL`） |
| 2 | 第 17 个 ID 是 `gray`（本地临时昵称），**不在池里**、不进 DDL 枚举、不可被分配 | `server/nicknames.py:57-62`、`is_valid_shared_color()` |
| 3 | 池子大小 = 活跃上限（`MAX_ACTIVE_NICKNAMES = len(COLOR_POOL)` = **16**） | `server/nicknames.py:75` |
| 4 | 表有版本号 `COLOR_POOL_VERSION = 1`，已通过 `/api/config` 下发给客户端（网页端存成 `nickPoolVersion`，落后就重拉映射表） | `server/nicknames.py:71`、`web/static/app.js:1176` |
| 5 | 库里有唯一性/白名单约束：`nicknames.color` 的 DDL `CHECK` 枚举 + active 行颜色唯一 | `server/db.py`（建表/索引） |
| 6 | 消息快照只存 **ID**（`sender_nickname_id` / `sender_color`），渲染时才查表 → **ID 一旦改值，历史消息颜色会跟着变**（违反 §3.4.1） | `server/services/messages.py:snapshot_of()`、`web/static/app.js:1328-1339` |
| 7 | 两端各有一份**内置**色表：网页 `nickcolor.js` 的 `BASE`（16 色）；PC `pc.js:54` 同一份 16 色；未知 ID → `FALLBACK_ID` 兜底 | `web/static/nickcolor.js:27-31`、`web/shell/pc.js:54`、`FMNick.isKnown()` |
| 8 | ★ **PC 端界面是 exe 自带的**（`NavigateLocal("app.html")`，日志逐字：「壳：加载本机界面 app.html（exe 自带，不加载 NAS 网页）」）→ 色表改动**不会**自动到已安装的 PC 客户端 | `pc-agent/FamilyAgent/WebHostWindow.xaml.cs:664`、`tools/pack_shell.py`（打包 `app.html/pc.js/pc.css/chat.js/tokens.css`） |
| 9 | 换色目前是「在够远的候选里**随机**」（v0.18.1 修），没有「人为指定某个色」的入口 | `server/services/nicknames.py:reassign_color()` |

**结论**：现状是「**色表写死在客户端**」+「服务端只发 ID」。要「自行增删颜色」，必须先解决 **第 8 条**：
色表得由**服务端下发**，否则网页端加了颜色，PC 端（和将来的 Android）渲染不出来。

---

## 2. 设计红线（这几条不能破）

1. **ID 只增不减、永不复用、永不改值** —— 历史消息快照只存 ID，改值/复用会让老消息颜色漂移（§3.4.1）。
   「删颜色」= 标记 `retired`（不再参与分配），**不是** 删行。
2. **活跃昵称之间颜色唯一**（沿用现有唯一约束，靠事务 + 唯一索引兜并发）。
3. **池子大小 ≠ 活跃上限**：解耦。池子可变（新常量 `MAX_COLOR_POOL`），活跃昵称上限保持 16。
4. **未知 ID 必须降级不崩**：老 exe / 未升级客户端拿到不认识的 ID → 走兜底色（现状已有，保持）。
5. **色值只由服务端查表填**，客户端传的 HEX 一律忽略（沿用 v0.18.0 的防伪造口径）。

---

## 3. 方案（推荐 A：色表入库 + 服务端下发）

### 3.1 数据模型（新增一张表，内置 16 色做初始数据）

```sql
CREATE TABLE nickname_palette (
  color_id   TEXT PRIMARY KEY,          -- 'color_01' … 允许增加到 'color_17' 以上（只增）
  hex        TEXT NOT NULL,             -- 归一化 '#RRGGBB'
  sort       INTEGER NOT NULL,          -- 池子顺序（= 新建优先顺序）
  status     TEXT NOT NULL DEFAULT 'active',   -- active | retired
  created_at TEXT NOT NULL
);
```
> 表名说明：**不叫 `nickname_colors`** —— 那是 r3 废弃的「归属层」的名字，
> `tools/test_nicknames.py` 里有守卫断言它不许存在；色表与「归属」是两件事。
- 迁移：把现有 16 色写进去（`color_pool_version` 保持 **1**，语义不变）；`gray` 依旧不入表。
- `nicknames.color` 的 DDL `CHECK` 枚举要**放宽**（否则自定义色进不去）→ 白名单改由 `is_valid_shared_color()` 查表判定。

### 3.2 服务端接口（新增；**必须同步进 `docs/PROTOCOL.md` 的 HTTP 表**）

| 方法 | 路径 | 语义 |
|---|---|---|
| GET | `/api/nicknames/colors` | `{color_pool_version, colors:[{color_id, hex, sort, status, used_by:[昵称id…]}]}` |
| POST | `/api/nicknames/colors` | `{hex}` 或 `{rgb:[r,g,b]}` → 201 `{color_id:'color_17', hex}`；hex 重复 → 409 |
| DELETE | `/api/nicknames/colors/{color_id}` | 仅当没有任何 active 昵称在用它 → `status='retired'`，`color_pool_version += 1`；在用 → 409 `COLOR_IN_USE`（**不是** 404） |
| POST | `/api/nicknames/{id}/color` | `{color_id}` 人为指定（仍要唯一 + 合法；广播 `nickname_color_changed`，客户端零改动） |
| WS 广播 | `color_table_changed {color_pool_version}` | 新帧：客户端据此重拉色表并重画 |

### 3.3 客户端渲染（以服务端表为准，内置表只做首屏兜底）

- `host.hello` 增加 `color_table`（`[{color_id, hex}]`）+ `color_pool_version`；网页端启动时多拉一次 `/api/nicknames/colors`。
- 两端把「查内置表」改成「查**服务端表**，查不到再查内置表，再查不到才 FALLBACK」。
- 收到 `color_table_changed` → 重拉 + 重画（头像/圆点/昵称行/设置页色标）。
- **老 exe 兼容**：未知 ID 走 FALLBACK（现状行为不变）—— 但要让 PC 端**看得见新颜色**，需要升级一次 exe（见 §5 决策点 ④）。

### 3.4 网页端 UI（`web/static/app.js` + `style.css`）

1. 昵称卡里新增「颜色表」区：色块网格 + 「在用 x/16」标记 + 每格「停用」（在用中的不可停用，给明确文案）+ 底部一行「加颜色：[HEX 或 RGB 输入] [添加]」，输入非法就就地报错（`#RRGGBB` / `rgb(12,34,56)` 两种都收）。
2. 昵称行现在的三按钮（选用 / 换色 / 删除）**保持**；「换色」旁加「选色…」→ 浮层里列出色表（带色块），点一个 = `POST /api/nicknames/{id}/color`；浮层里保留「随机」按钮 = 现有 `reassign-color`。
3. 沿用 Fluent 2 令牌；窄屏（≤480px）与暗色必须都过（现有 375px / 暗色用例会覆盖）。

---

## 4. 分期（每期可独立交付、可回退）

> **执行进度**
> - **★ v0.19.1 追加（用户定稿，2026-09-29）**：
>   ① **默认配色 16 → 31 色**：前 15 个 Material 400 系、后 16 个 800/900 系（用户清单里 `#26A69A`
>      重复出现两次，而 `hex` 有 UNIQUE 约束、色表也不该有重复色 → 按去重 31 个建表）。
>      `nicknames.color` 存的是**逻辑色 ID**，改配色**不需要动任何昵称**；老库靠 `_seed_color_table()`
>      的 **upsert** 把已有槽位改指到新色，有改动 → `color_pool_version +1` → 客户端免刷新重拉。
>   ② **池上限 32 → 64**（默认 31 个内置色，留 32 就只剩 1 个名额）；上限改由服务端下发
>      （`GET /api/nicknames/colors` 的 `max_color_pool`），网页端不再写死。
>   ③ **客户端兜底表同步 + 防漂移断言**：`nickcolor.js` 的 `BASE` 与 `pc.js` 的 `NICK_BASE` 按服务端常量
>      重新生成（31 条），并加断言「两张客户端表必须与服务端 `LOGICAL_COLORS` 逐条一致」。
> - **P1 ✅ 已完成（2026-09-29）**：`nickname_palette` 表 + 迁移（真实库副本实测通过）、颜色表服务
>   （增 / 停用 / 指定 / 校验 / 池上限 32）、4 个 HTTP 端点 + `color_table_changed` 广播、
>   `create`·`reassign` 改用动态色表、活跃上限 16 与池子大小解耦。
>   测试：`tools/test_nicknames.py` **154/154**（含 27 条色表断言：ID 不复用、在用色不可停用、
>   指定颜色、广播三端都收到、版本号逐次 +1）。
> - **P2 ✅ 已完成（2026-09-29）**：色表由服务端下发、三端渲染以它为准 ——
>   ① 设备 `hello` 帧带 `color_table` + `color_pool_version`；② 整表帧（response / sync）也带 `color_table`；
>   ③ 网页端 `nickcolor.js` / PC 壳 `pc.js` 均「服务端表优先、内置表兜底、**空表不覆盖**」；
>   ④ 网页端收 `color_table_changed` 免刷新重拉；PC 端由宿主收该帧→重拉整表→推 `host.nickname`；
>   ⑤ `ANDROID-CONTRACT.md` 写明「不许写死 16 色」。测试：网页端 **85/85**、PC 套件（T7 七条）、
>   NAS 套件 **155/155**、Core 单测 153。
> - **P3 ✅ 已完成（2026-09-29）**：网页端色表管理 + 逐昵称选色 ——
>   ① 昵称卡里新增「颜色表」区（色块网格 / 计数「可用 x / 上限 32 · 在用 y/16」/ 每格写谁在用、
>   在用中的不能停用）；② 底部「加颜色」（HEX 与 RGB 都收，非法与重复**就地报错**，不静默）；
>   ③ 点昵称行的**色点**（现在是按钮）打开「选颜色」弹窗：当前色描边、别人占用的置灰、
>   点一个即生效，弹窗里保留「随机换一个」；④ 停用走确认框，停在用的色有明确文案。
>   测试：网页端 **102/102**（新增 H 段 17 条，含窄屏 375 与暗色；并修掉一条真 flake ——
>   发起端走 HTTP 响应、其余端走广播，改成「等三端收敛」）。截图：`13-color-table-desktop` /
>   `14-color-picker-dialog` / `15-color-table-narrow-375` / `16-color-table-dark`。
> - **P4 ✅ 已发布（2026-09-29）**：`v0.19.0` —— Release 两资产（`family-message_0.19.0.fpk` 224,602 B +
>   `FamilyAgent-win-x64.zip` 65,958,457 B）；tag `v0.19.0` → `7654d6b`；CR 在 tag 上两条 workflow 都 success
>   （`Checks` = 本仓门禁、`Build Windows Agent` = 出 exe）。交付物已复制到 `/vol1/<uid>/workspace/`。

| 期 | 内容 | 交付物 | 不动的东西 |
|---|---|---|---|
| **P1** | 服务端：色表入库 + 迁移 + ①~④ 接口 + 校验 + 单测（`tools/test_nicknames.py` 扩一节） | 服务端可跑，客户端零变化 | 任何客户端 |
| **P2** | 协议与渲染：`PROTOCOL.md` / `ANDROID-CONTRACT.md` 更新、`hello.color_table`、两端「服务端表优先」+ 兜底 + `color_table_changed` 重画 | 三端渲染同一份表 | 网页 UI 里还没有配置入口 |
| **P3** | 网页 UI：色表管理 + 逐昵称「选色…」（§3.4） | 需求第 4 条**可见可用** | 服务端不再改 |
| **P4** | 回归 + 发布：四套测试扩展、快照兼容回归、版本号 bump、**PC exe 升级（v0.19.0）** | Release | — |

**顺序理由**：先让「表」动起来（P1），再做「谁渲染」（P2），最后才有配置界面（P3）——
反过来做会得到一个能加颜色但没人看得见的界面。

---

## 5. 需要拍板的 4 个点（定了我再开工）

| # | 决策点 | 选项 | 我的建议 |
|---|---|---|---|
| ① | 颜色池上限 | (a) 32 (b) 64 (c) 不设上限 | **(a) 32**：够用、UI 一屏放得下、防呆（池满时给 503 与文案） |
| ② | 谁能改色表 | (a) 网页端任何人都能改（现状无鉴权）(b) 只在设置页（家长用）(c) 加管理口令 | **(b)**，与现有「设置页才有的操作」口径一致；口令另立需求 |
| ③ | 停用的色 ID 能不能「复活」（换个 hex 继续用同一个 ID） | (a) 不能（ID 永久绑定那个 hex）(b) 能 | **(a) 不能**：否则历史消息颜色会漂移（硬红线 §2.1） |
| ④ | 容忍「PC 端要升一次 exe 才看得见新色」吗 | (a) 容忍（P4 顺带发 v0.19.0）(b) 不容忍（改成 PC 端只认内置 16 色，新色只在网页端生效） | **(a)**：PC 界面本来就是 exe 自带（勘察第 8 条），一次升级换来长期可用 |

---

## 6. 影响面清单（P1 开工前再逐条过一遍）

- 服务端：`server/nicknames.py`（表操作 + 校验 + 上限）、`server/services/nicknames.py`（事务/错误语义）、`server/db.py`（建表 + 迁移 + 放宽 CHECK）、`server/main.py`（4 个路由 + 广播 + `hello` 带表）、`server/services/messages.py`（**不改**：快照仍是 ID）
- 客户端：`web/static/app.js`（渲染 + UI）、`web/static/style.css`、`web/static/nickcolor.js`（改成「服务端表优先」）、`web/shell/pc.js`（同上 + `hello.color_table`）
- 文档：`docs/PROTOCOL.md`（HTTP 表 + `color_table_changed` + `hello` 字段）、`docs/ANDROID-CONTRACT.md`（Android 必须从协议取表）、`docs/NICKNAME-SYSTEM-PLAN.md`（§4.3 补「色表可变」）、`docs/DESIGN-TOKENS.md`（不涉及）
- 测试：`tools/test_nicknames.py`（色表 CRUD / 校验 / 上限 / 停用占用中 → 409）、`tools/test_nickname_web.py`（UI + 窄屏 + 暗色）、`tools/test_pc_sender_nickname.py`（PC 端按服务端表渲染）、`tools/test_snapshot_e2e.py`（**历史不动**回归）
- 风险：池子膨胀导致 UI 溢出（P3 用网格 + 折叠）；并发加同色（唯一索引兜）；老 exe 看到新 ID（FALLBACK，不崩）
