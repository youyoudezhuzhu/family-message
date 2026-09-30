# 通信协议（冻结版）

> 这份文档是**跨平台实现契约**。服务端、网页端、Windows Agent、以及未来的 Android / iOS
> Agent 都必须按这里的定义收发。任何一端要改协议，先改这份文档。
>
> **冻结时间：2026-09-29（v0.17.x）**。本次补齐了 2026-09-23 版之后落地的全部帧
> （共享昵称 10 帧、远程解锁 4 帧、能力/设备信息、小米开关），并首次标注**预留/未启用**项。
> 对账方法见 §13（`tools/check_protocol_doc.py`，**改协议不同步文档会被它拦下**）。

- 服务端基址：`http://<NAS-IP>:18801`（走飞牛网关时为 `http://<NAS-IP>:5666/app/family-message`）
- WebSocket：`ws://<NAS-IP>:18801/ws/...`
- 编码：UTF-8 JSON 文本帧。二进制一律用 base64 字符串放在 JSON 字段里（**不要发二进制帧**）

---

## 1. 两个 WebSocket 端点

| 端点 | 谁连 | 用途 |
|---|---|---|
| `/ws/device/{device_id}?name=&type=&platform=&agent_version=&token=&enroll_token=&windows_state=&capabilities=` | Device Agent | 收消息、回报状态、回传截图、昵称管理、解锁应答 |
| `/ws/web` | 任意浏览器 | 看设备状态、看消息、管理昵称 |

---

## 2. 身份模型

- `/ws/web` 是**匿名**的，不区分浏览器。Chrome / Edge / 手机 Safari 连上来都是同一个
  「Web Sender」。**不要按 Cookie / IP / 浏览器实例区分用户。**
- **设备**靠 `device_id` + `token`（首次用 `enroll_token` 注册）标识。
- **昵称（v0.17.0 起，重要变更）**：昵称是**服务端的全局对象** —— `nicknames` 表存
  `nickname_id` / `display_name` / **逻辑色 ID**（`color_01`…`color_16`，灰临时是 `gray`）。
  - 「**当前用谁的名义**」是**纯本地状态**（PC: 本机配置文件；Web: localStorage），
    **不上传、不广播**，离线也能改。
  - 消息上的名字/颜色是**快照**：`sender_name` + `sender_nickname_id` + `sender_color`。
    **写端已补（2026-09-29）**：服务端在落库时填 —— 发送方带上当时选用的 `nickname_id` →
    `(id, 该昵称当时的逻辑色 ID)`（**颜色由服务端查表填，客户端传的色值一律忽略**）；
    明确带 `null`（灰临时）→ `(NULL, 'gray')`；**字段缺失**（老前端 / 老 exe）→ 两列 `NULL`。
    ⚠ 客户端解析仍必须容忍 `NULL`：`NULL` ≠ 灰，要走「按名字反查昵称表 → 再退回名字哈希色」
    兜底（老消息与老客户端产生的行都是这种）。判「灰临时」**只看 `sender_color == 'gray'`**。
    验收：`python3 tools/test_nickname_snapshot.py`（A–G 七组，含「重创同名不污染历史」）。
  - 老协议（2026-09-23 版）写的「昵称是纯本地概念、服务端不存昵称表」**已作废**。

---

## 3. 设备接入流程

1. Agent 用查询参数发起 `GET /ws/device/{device_id}`：

| 参数 | 必需 | 说明 |
|---|---|---|
| `name` | 否 | 显示名，缺省用 `device_id` |
| `type` | 否 | `pc`（默认）/ `phone` / 其它；网页控制台据此显示图标 |
| `platform` | 否 | 如 `Windows 10.0.26100` / `android` |
| `agent_version` | 否 | 版本号，仅留痕 |
| `token` | 已注册设备必需 | 首次注册后由 `hello` 下发 |
| `enroll_token` | 首次注册必需 | 家庭注册口令；重装后可替代 `token`（回退校验） |
| `windows_state` | 否 | 会话状态（`interactive` / `logon_screen`）；连上就带，网页端不用等第一次心跳 |
| `capabilities` | 否 | 逗号分隔能力表（见 §10）；与心跳里的取值同源 |

2. 服务端校验：设备未注册 → 用 `enroll_token` 注册（对不上 `close(4003)`）；
   已注册 → 校验 `token`，失败回退 `enroll_token`，都对不上 `close(4001)`。
3. 握手成功，服务端下发 `hello`（并顺手把上线事件广播给 `/ws/web`）：

```json
{
  "type": "hello",
  "device_id": "pc_shufang",
  "token": "<以后每次连接都带这个>",
  "server_time": "2026-09-29T11:31:05+08:00",
  "offline_after_seconds": 60
}
```

4. Agent **必须把 `token` 存下来**，后续连接用它替代 `enroll_token`。
5. 服务端会补投离线期间没送达的消息（帧里带 `redelivered: true`）。
6. **Agent 收到 `hello` 后要做的第一件事**：把本地积压未发出的消息补发一次（见 §7），
   并（昵称开启时）发一次 `nickname_list_request` 校正整表。

---

## 4. 服务端 → 设备 的帧

| type | 载荷 | 说明 |
|---|---|---|
| `hello` | `device_id` `token` `server_time` `offline_after_seconds` | 握手成功（§3） |
| `message` | `message_id` `sender_name` `content` `message_type` `created_at` `status` `sender_nickname_id` `sender_color` `auto_close_seconds` `history[]` `redelivered?` | 有条留言要弹（见下）。★ 快照两字段与 `history[]` 里每个条目都带（PC 端 `pc.js` 的解析优先级是「快照 id → 快照色 → 名字反查」，缺了就会在**重创同名**时挂错行，§3.4.1） |
| `heartbeat_ack` | `server_time` | 心跳回执 |
| `screenshot_request` | `request_id` | 要求截屏 |
| `shutdown` | `delay_seconds` | 要求关机（默认 5 秒） |
| `reply_ack` | `client_id` `message_id` `status` `created_at` | 对 `reply` 的回执 |
| `history_response` | `request_id` `device_id` `messages[]` | 历史补齐 |
| `unlock_request` | `request_id` `device_id` `action` `nonce` `expires_at` | 远程解锁：一次性令牌 |
| `unlock_result_ack` | `request_id` `status` `reason` | 解锁应答被拒时的回执（如 `rejected/not_mine`） |
| `nickname_list_response` | `nicknames[]` `pool_version` `color_table[]` | 昵称整表（点对点应答，全量 `active`）；**v0.19** 起 `color_table` 带**权威颜色表** `[{color_id,hex}]`（客户端色表以它为准） |
| `nickname_list_sync` | `nicknames[]` `pool_version` `color_table[]` | 昵称整表（广播，与上同形） |
| `nickname_created` | `nickname{}` | 有昵称被新建 |
| `nickname_updated` | `nickname{}` | 昵称改名 / 被服务端修订 |
| `nickname_color_changed` | `nickname{}` | 「重新分配颜色」的结果 |
| `nickname_removed` | `nickname{}` `released_color` | 软删（`status=inactive`）后广播，颜色回池 |
| `nickname_error` | `request` `code` `message` `existing_nickname_id?` | 昵称操作被拒（`request` 回显发起帧 type） |
| `color_table_changed` | `color_pool_version` | **v0.19**：颜色表被增删（加色 / 停用）→ 客户端**重拉** `GET /api/nicknames/colors` 并重画；认不出的客户端忽略即可（只带版本号，不推全表） |

### `hello` 帧（服务端 → 设备，连上即发）

- 既有握手字段：`device_id`、`token`、`server_time`、`offline_after_seconds`。
- **v0.19 新增**：颜色表版本 + 可用的颜色表 `[{color_id, hex}]` —— **设备端 / PC 壳 / 将来 Android
  以此为准**（客户端内置表只做首屏兜底，认不出的逻辑色 ID 走兜底色）。
  色表变更由广播 `color_table_changed` 通知，客户端据此重拉 `GET /api/nicknames/colors`。
  昵称开关关闭时这两个字段**都不存在**（与「开关关闭时现有行为逐字不变」一致）。

### `message` 帧

```json
{
  "type": "message",
  "message_id": 42,
  "sender_name": "妈妈",
  "content": "下来吃饭了",
  "message_type": "text",
  "created_at": "12:31",
  "status": "sent",
  "auto_close_seconds": 0,
  "redelivered": false,
  "history": [
    {"message_id": 40, "sender_name": "爸爸", "content": "买菜了吗",
     "created_at": "2026-09-29T12:28:03+08:00", "direction": "in"},
    {"message_id": 41, "sender_name": "书房电脑", "content": "买了",
     "created_at": "2026-09-29T12:29:11+08:00", "direction": "out"}
  ]
}
```

- `history` 是**群聊空间**里最近 `message.history_limit` 条往来（不只是本设备与 Web 的往来），
  用来铺满对话界面；`direction` 是**相对本设备**的视角（本机发的 = `out`）。
- `auto_close_seconds > 0` 到点自动关；`0` 必须手动关。
- 快照字段（`sender_nickname_id` / `sender_color`）见 §2；已由服务端写入（颜色查表填）。
- **Agent 收到 `message` 后按顺序做**：① 立刻把弹窗显示出来 → ② 发 `ack: popup_displayed`
  → ③ 用户点关闭 → 发 `ack: read`。`device_received` 由服务端自己记，Agent 不发。
- 界面 20 秒内起不来（WebView2 故障等）**必须有终态**：回报 `popup_displayed`
  或走回落提醒（`DisplayFallbackRequired`）→ `popup_displayed`，不能永远挂在未 ACK（§9）。

### `shutdown` 帧

网页端设备卡片上的「关机」发出来的。Agent 收到后：① 记日志 + 给可见提示；
② 执行 `shutdown /s /t <delay_seconds>`；③ 回报 `event`（`kind=shutdown` 或 `shutdown_failed`）。
**不要加 `/f`**；**不要在本机再弹一次确认**（延迟几秒本身就是反应时间，`shutdown /a` 可取消）。
不支持的平台回 `event{kind=shutdown_failed}`，**不要静默**。

### `unlock_request` 帧（一次性令牌）

`nonce` + `expires_at` 过期即废；同一 `request_id` 只能结一次单（服务端 `used_at` 兜底，
PC 侧另有本地重放缓存）。应答用 `unlock_result`（§5），`status="armed"` 是**中间态**：
表示「PC 已收到并校验通过，等真正解锁」，服务端只广播不结单。

---

## 5. 设备 → 服务端 的帧

| type | 载荷 | 说明 |
|---|---|---|
| `heartbeat` | `windows_state?` `capabilities?` | 建议 15 秒一次；会话状态有变化才广播 |
| `ack` | `message_id` `status` | `status ∈ {device_received, popup_displayed, read}`；**只入库，不再对外广播**（群聊模型） |
| `reply` | `sender_name` `content` `client_id` `sender_device_id?` `nickname_id?` | 本机回复。`client_id` 本地随机串，用来对回执；`sender_device_id` 让服务端跳过发起者；`nickname_id` = 本机当时选用的共享昵称（`null` = 灰临时；**字段缺失 = 老 exe**）→ 服务端据此写消息快照（颜色查表填，见 §2） |
| `history_request` | `request_id` `limit` | 主动拉历史（≤200） |
| `screenshot_response` | `request_id` `format` `data_base64` `width` `height` `error` | 二选一：有 `data_base64` 或有 `error`。**别名 `screenshot` 仍被接受（兼容老 exe）** |
| `unlock_result` | `request_id` `status` `reason` | `status ∈ {armed, success, failed…}` |
| `event` | `kind` `detail` | 事件留痕（`GET /api/events`） |
| `device_info` | `platform?` `name?` | 运行中上报平台/改名 |
| `nickname_list_request` | — | 拉整表（连接后 / 点「刷新」） |
| `nickname_create_request` | `display_name` | 新建（颜色由服务端分配；撞名 409 语义） |
| `nickname_rename_request` | `nickname_id` `display_name` | 改名（全局；撞名 `NAME_TAKEN`） |
| `nickname_reassign_color_request` | `nickname_id` | 重新分配颜色（新色 ≠ 旧色、且未被占用） |
| `nickname_delete_request` | `nickname_id` | 软删；颜色立刻回池 |

**昵称 5 帧的通用规则**：全部**要求在线**（离线本地直接拒绝并提示，不排队、不入 Outbox、
不补发）；写操作成功后服务端**先**给发起者 `nickname_list_response`，**再**广播增量。
`nickname.enabled=false` 时这 5 种帧**静默忽略**（与未知帧同待遇）。

### 截图回传

成功：`{"type":"screenshot_response","request_id":"a1b2c3","format":"jpeg","data_base64":"/9j/4AAQ...","width":2560,"height":1440,"screen_locked":false}`
失败（**必须回 `error`，不要静默丢弃**）：`{"type":"screenshot_response","request_id":"a1b2c3","error":"屏幕已锁定"}`

> 限流：单帧上限 16MB。截图建议 JPEG 质量 80 以内、长边 ≤ 2560。

---

## 6. `/ws/web` 广播

| type | 说明 |
|---|---|
| `ready` | 连接建立：`{server_time}` |
| `pong` | 对客户端 `{"type":"ping"}` 的回执 |
| `device_status` | 设备上下线 / 会话状态变化：`{device_id, status, device}` |
| `device_updated` | 设备信息变更：`{device}` |
| `device_deleted` | 设备被移除：`{device_id}` |
| `message` | 新留言：`{message:{…}, reply?:true}`（`reply=true` 表示这是设备回复） |
| `wake` | 远程开机结果：`{device_id, result}` |
| `shutdown_sent` | 关机指令已下发：`{device_id}` |
| `xiaomi` | 米家开关动作结果：`{id, on}` |
| `unlock_pending` | 解锁请求已下发、等设备应答：`{device_id, request_id, expires_at}` |
| `unlock_result` | 解锁结果：`{device_id, request_id, status, reason}`（`armed` 是中间态） |
| `nickname_list_sync` | 昵称整表广播（有人在拉整表时顺手同步） |
| `nickname_created` | 新昵称：`{nickname}` |
| `nickname_updated` | 改名：`{nickname}` |
| `nickname_color_changed` | 换色：`{nickname}` |
| `nickname_removed` | 删除：`{nickname, released_color}` |
| `color_table_changed` | **v0.19** 颜色表变了：`{color_pool_version}`（客户端重拉色表） |
| `message_status` | ⚠ **预留 / 未启用**：逐设备 ACK 推进的广播在群聊模型下**已被注释掉**（数据照旧入库，只差这一层）。要恢复就放开 `server/main.py` 里那段注释 |

---

## 7. 发送队列（Agent 侧必须实现的可靠性要求）

> ❌ 错误做法：发送前先判断「我连接着吗？没连接就直接报失败」——连接状态判断可能不一致，
> 会把**能发的消息误报成失败**。
>
> ✅ 正确做法：**不预判，直接尝试发；失败了才入队**，然后在两个时机重试补发：
> ① 收到 `hello` 之后；② 心跳循环里（每次心跳顺手检查队列）。

截图响应**不要入队**（有时效性，过期就是废图）；`reply` / `ack` / `history_request` 都要入队。
**昵称的 5 个管理帧和 `unlock_result` 不入队**（在线语义：离线就是拒绝/丢弃）。

---

## 8. HTTP API

所有 `/api/*` 都要求 `WebAuth` 依赖（家庭访问口令，走 cookie / header）。

| 方法 | 路径 | 说明 |
|---|---|---|
| POST | `/api/login` | 提交家庭访问口令 |
| GET | `/api/config` | 前端需要的公开配置（**不含昵称**） |
| GET | `/api/devices` | 设备列表（含在线状态、最后在线） |
| GET | `/api/devices/{id}` | 单个设备 |
| GET | `/api/devices/{id}/status` | 设备状态 |
| DELETE | `/api/devices/{id}` | 移除设备 |
| POST | `/api/messages` | 发送留言 `{sender_name, content, nickname_id?}`；`nickname_id` = 本机当时选用的共享昵称（`null` = 灰临时，**不带** = 老前端 → 两列 `NULL`），服务端据此写消息快照（§2） |
| GET | `/api/messages` | 消息列表（对外单一状态 `sent`） |
| GET | `/api/conversations/{device_id}` | 某设备的对话明细（**双向**，靠 `sender_kind` 判方向） |
| POST | `/api/messages/{id}/read` | 标记已读 |
| POST | `/api/devices/{id}/screenshot` | 请求截图，返回图片（带鉴权，不是公开 URL） |
| POST | `/api/devices/{id}/wake` | 执行该 PC 绑定的米家开关动作（默认「开」） |
| POST | `/api/devices/{id}/shutdown` | 让 PC Agent 关机（离线返回 409） |
| POST | `/api/devices/{id}/unlock` | 远程解锁（返回 `request_id` / `expires_at` / `advisory`；**离线**才 409） |
| GET | `/api/nicknames` | 昵称整表（`?status=active`；空库返回 `{"nicknames": []}`，**不建任何行**） |
| POST | `/api/nicknames` | 新建 `{display_name}`，201；撞名 **409 `NICKNAME_ALREADY_EXISTS` + `existing_nickname_id`** |
| PATCH | `/api/nicknames/{nickname_id}` | 改名 `{display_name}`；撞名 **409 `NAME_TAKEN`**；不存在 404 |
| POST | `/api/nicknames/{nickname_id}/reassign-color` | 重新分配颜色（新色 ≠ 旧色；池满 503 `NO_AVAILABLE_COLOR`） |
| DELETE | `/api/nicknames/{nickname_id}` | 软删（`status=inactive`），颜色回池；已删 409 `NICKNAME_INACTIVE` |
| GET | `/api/nicknames/colors` | **v0.19** 颜色表：`{color_pool_version, colors:[{color_id, hex, sort, status, used_by[]}]}`；`?status=active\|retired\|all`（默认 `all`） |
| POST | `/api/nicknames/colors` | **v0.19** 加颜色 `{hex}` 或 `{rgb:[r,g,b]}`（收 `#RRGGBB`/`RRGGBB`/`#RGB`/`rgb(…)`/`r,g,b`），201 `{color, color_pool_version}`；同色值 409 `COLOR_ALREADY_EXISTS`；非法 422 `INVALID_COLOR_HEX`；池满 503 `COLOR_POOL_FULL` |
| DELETE | `/api/nicknames/colors/{color_id}` | **v0.19** 停用颜色（行保留、`status=retired`，**ID 不复用**）；在用 409 `COLOR_IN_USE`；已停用 409 `COLOR_RETIRED`；不存在 404；形状错 422 `INVALID_COLOR_ID` |
| POST | `/api/nicknames/{nickname_id}/color` | **v0.19** 人为指定颜色 `{color_id}`（与随机换色并列）；被别人占用 409 `COLOR_IN_USE`；已停用 409 `COLOR_RETIRED`；同色幂等 200 |
| GET | `/api/xiaomi/devices` | 米家开关绑定列表 |
| POST | `/api/xiaomi/devices` | 新建绑定 |
| PATCH | `/api/xiaomi/devices/{id}` | 改绑定（动作 / 关联 PC / siid / piid） |
| DELETE | `/api/xiaomi/devices/{id}` | 删除绑定 |
| GET | `/api/xiaomi/auth-url` | 米家 OAuth2 授权地址（第一步） |
| POST | `/api/xiaomi/exchange` | 用回调里的 code 换 token（第二步） |
| POST | `/api/xiaomi/logout` | 清除本地 token |
| POST | `/api/xiaomi/discover` | 拉取账号下的米家设备列表 |
| POST | `/api/xiaomi/devices/{id}/power` | 直接开关某个米家设备 |
| GET | `/api/xiaomi/devices/{id}/state` | 读某个米家设备的开关状态 |
| GET | `/api/xiaomi/status` | 米家授权状态 |
| GET | `/api/events` | 事件留痕（排查用） |
| GET | `/healthz` | 健康检查（无需鉴权） |

---

## 9. 消息状态机

```
created → server_received → device_received → popup_displayed → read
```

- `created` / `server_received`：服务端收到就算，无需 Agent 参与
- `device_received`：投递成功，服务端自己记（Agent 不发）
- `popup_displayed`：**Agent 回报**（「真的弹出来了」的唯一证据；回落提醒也要报它）
- `read`：用户点了「关闭窗口」

状态只前进不后退（`max()` 语义）。**对外（HTTP / 广播）只有单一 `status = "sent"`** ——
逐设备的四态仅存在于服务端内部（`message_targets`）与客户端本地记账。

---

## 10. 能力清单（`capabilities`）

| 能力 | 含义 | Windows 现状 | Android 现在 |
|---|---|---|---|
| `message` | 能收消息并显示 | ✅ | ✅ |
| `screenshot` | 能截图并回 `screenshot_response` | ✅（`WindowsScreenshotProvider`） | 需 `MediaProjection`；授权失败回 `error` |
| `shutdown` | 能执行 `shutdown` 指令 | ✅（`WindowsPowerProvider`）；**会话 0（headless）不上报** | ❌ 不上报 |
| `unlock` | 能参与远程解锁 | ⚠️ **视凭据而定**：本机存好解锁凭据且校验通过才上报（Phase 2 起） | ❌ 不上报 |

- 取值来自**单一常量表**（`DeviceCapabilities`），`hello` 查询参数与心跳帧同源；
- 会话 0（开机无人登录）时**不上报 `shutdown`**，也不上报解锁（没有桌面可弹窗）；
- `unlock` 是**条件能力**（Phase 2 起）：只有本机**存好解锁凭据且校验通过**时才上报
  （`IUnlockGuard.Ready`）——"有实现"与"能用"是两件事，凭据没配就上报等于给网页端一个
  点了必然失败的按钮。用户在 PC 设置页存/清凭据后，下次心跳/重连即生效。

  ⚠ **网页端的解锁入口不受这个能力位限制**：能力位只描述"PC 自报能不能做"，
  入口是否出现由 `device.unlock` 权限决定。用户要的正是"锁屏时点一下，看 PC 怎么答" ——
  所以凭据没配时照样能点，PC 回 `failed/no_credential`，界面把原因说清楚
  （`unlock_result.reason` 有中文映射）。
  收到解锁请求后本机**按三种情况如实回**（不谎报）：
  · 凭据没就绪 → `failed/no_credential`；
  · 凭据就绪但没装 Credential Provider（Phase 3 组件）→ `failed/cp_missing`
    —— 复用 `armed` 会让网页端一直等到超时，用户看到的是"没反应"，所以单列一个码；
  · 两者都有 → `armed/ok`（由 CP 完成解锁）；
  · 协议不过（过期/重放/不属于我/动作不对）→ 原样回对应码。
  **真正把锁解开要等 Phase 3**。
- 客户端收到**不支持**的命令必须回终态错误（`event{kind=shutdown_failed}` /
  `screenshot_response{error}`），**不允许超时静默**。

---

## 11. 客户端实现检查表（新端接入照着勾）

| # | 事项 | 判据 |
|---|---|---|
| 1 | 握手四步 | `Connect → Authenticate → Sync/History 补齐 → Live`，日志可读 |
| 2 | 单一连接 | 任意时刻只有一条 active 连接；旧连接的 `finally` 不影响新连接状态 |
| 3 | `hello` 后补发队列 | 队列里的 `reply`/`ack`/`history_request` 全部重发一次 |
| 4 | 心跳 15 秒 | 断线按指数退避（2→4→8→16→30 秒封顶） |
| 5 | 消息落盘 | 先写 `MessageStore` 再交给 UI；UI 崩了/不订阅也要入库 + 发 `ack` |
| 6 | 弹窗终态 | 20 秒内没弹出来 → 回落提醒 + `popup_displayed`，不许永远未 ACK |
| 7 | 截图/关机不支持 | 回 `error` / `shutdown_failed`，不超时 |
| 8 | 逐设备状态不对外 | 对外只报 `sent`；本地四态不上协议 |
| 9 | 昵称快照容错 | `sender_nickname_id` / `sender_color` 为 `null` → 名字反查 → 哈希色兜底；`sender_color='gray'` 才是灰临时 |
| 10 | 昵称离线策略 | 只允许「选择昵称」离线；新建/改名/换色/删除离线一律拒绝并提示 |
| 11 | 昵称写操作结果 | 回执 ≠ 成功；等整表/增量（成功）或 `nickname_error`（失败），并有超时兜底 |
| 12 | 昵称上色一致 | 逻辑色 ID → 显示色的映射与 `NicknameColor` / `nickcolor.js` 逐行一致（浅/深主题各一遍） |

---

## 12. 移植到 Android 的注意事项

1. **协议零依赖**：只用到 WebSocket + JSON + base64。Android 用 OkHttp WebSocket 就够。
2. **`tools/cli_agent.py` 是协议参考实现**——任何平台的对接都以它为准。
3. **心跳**：Android 受 Doze 影响，建议 15 秒心跳 + `ForegroundService`。
4. **设备类型**：连接时 `type=phone`、`platform=android`。
5. **截图**：`MediaProjection` 需用户授权一次；授权失败**要回 `error` 帧**，不要静默超时。
6. **昵称**：~~存 `SharedPreferences`，和服务端无关~~ → **已作废**。昵称是服务端全局对象
   （§2）；`SharedPreferences` 只存**本机当前选用**（`nickname_id` + 逻辑色 ID 快照）。
7. **发送队列**：见 §7，同样要实现（移动网络切换频繁，不实现会丢消息）。
8. 接口级别的「必须自己实现哪些类」见 **`docs/ANDROID-CONTRACT.md`**（Phase 5 产出）。

---

## 13. 对账与预留项（协议冻结的机器判据）

```bash
python3 tools/check_protocol_doc.py      # 退出码 0 = 服务端帧与本文档逐条对齐
```

- 脚本读 `server/main.py` + `server/hub.py` 里**非注释**的 `"type": "<name>"`（服务端发出）
  与设备帧分派（`mtype == …` / `NICKNAME_FRAME_TYPES`），要求两边都在本文档的帧表里出现。
- **预留项白名单**（必须出现在文档里，且必须被显式标注为「预留/未启用」）：
  - `message_status`：群聊模型下**已注释**，不再对外广播（数据照旧入库）。
- 反向（文档列了但服务端没有）：只允许 `ping`（客户端 → 服务端，网页端心跳）。
  其它情况脚本会报错 —— 说明有人改了文档没改代码，或反之。
