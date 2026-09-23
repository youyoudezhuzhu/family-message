# 通信协议

> 这份文档是**跨平台实现契约**。服务端、网页端、Windows Agent、以及未来的 Android / iOS
> Agent 都必须按这里的定义收发。任何一端要改协议，先改这份文档。

- 服务端基址：`http://<NAS-IP>:18801`（走飞牛网关时为 `http://<NAS-IP>:5666/app/family-message`）
- WebSocket：`ws://<NAS-IP>:18801/ws/...`
- 编码：UTF-8 JSON 文本帧。二进制一律用 base64 字符串放在 JSON 字段里（**不要发二进制帧**）

---

## 1. 两个 WebSocket 端点

| 端点 | 谁连 | 用途 |
|---|---|---|
| `/ws/device/{device_id}?name=&type=&platform=&agent_version=&token=&enroll_token=` | Device Agent | 收消息、回报状态、回传截图 |
| `/ws/web` | 任意浏览器 | 看设备状态、看消息、看 ACK 进度 |

**身份模型**（这是本项目的核心设计，别按传统 IM 理解）：

- `/ws/web` 是**匿名**的，不区分浏览器。Chrome / Edge / 手机 Safari 连上来都是同一个
  「Web Sender」。**不要按 Cookie / IP / 浏览器实例区分用户。**
- 发送人昵称是**纯本地概念**：网页存 localStorage，PC Agent 存本地配置，
  发消息时把昵称当普通字符串带上。**服务端不存昵称表。**
- 设备靠 `device_id` + `token`（首次用 `enroll_token` 注册）标识。

---

## 2. 设备接入流程

1. Agent 用查询参数发起 `GET /ws/device/{device_id}`，带上设备信息。
2. 服务端校验：
   - **设备未注册过** → 用 `enroll_token` 注册；对不上则 `close(4003)`。
   - **已注册** → 校验 `token`；token 对不上时回退到校验 `enroll_token`（方便重装 Agent 后重新接入）。都对不上则 `close(4001)`。
3. 握手成功，服务端下发 `hello`：

```json
{
  "type": "hello",
  "device_id": "pc_shufang",
  "token": "<以后每次连接都带这个>",
  "server_time": "2026-09-23T12:31:05+08:00",
  "offline_after_seconds": 60
}
```

4. Agent **必须把 `token` 存下来**，后续连接用它替代 `enroll_token`。
5. 服务端会补投离线期间没送达的消息（`redelivered: true`）。

**Agent 收到 `hello` 后要做的第一件事**：把本地积压未发出的消息补发一次（见 §6）。

---

## 3. 服务端 → 设备 的帧

| type | 说明 |
|---|---|
| `hello` | 握手成功。含 `token` / `server_time` / `offline_after_seconds` |
| `message` | 有条留言要弹。见下 |
| `heartbeat_ack` | 心跳回执 |
| `screenshot_request` | 要求截屏。含 `request_id` |

### `message` 帧

```json
{
  "type": "message",
  "message_id": 42,
  "sender_name": "妈妈",
  "content": "下来吃饭了",
  "message_type": "text",
  "created_at": "12:31",
  "auto_close_seconds": 0,
  "redelivered": false,
  "history": [
    {"message_id": 40, "sender_name": "爸爸", "content": "买菜了吗",
     "created_at": "2026-09-23T12:28:03+08:00", "direction": "in"},
    {"message_id": 41, "sender_name": "书房电脑", "content": "买了",
     "created_at": "2026-09-23T12:29:11+08:00", "direction": "out"}
  ]
}
```

- `history` 是最近 `message.history_limit` 条对话，**用来铺满对话界面**，
  让 Agent 一打开就有上下文。Agent 自己决定怎么渲染
  （Windows 端是左边聊天气泡，靠 `direction` 决定左右）。
- **`direction`**：`"in"` = 这台设备收到的，`"out"` = 这台设备自己发出去的。
  注意这是**相对该设备**的视角，不是绝对方向。
- `auto_close_seconds > 0` 时，弹窗到点自动关；`0` 表示必须手动关。

### Agent 收到 `message` 后必须按顺序做

```
① 立刻把弹窗显示出来
② 发 ack: popup_displayed          ← 让网页端知道「真的弹出来了」
③ 用户点关闭 → 发 ack: read
```

**注意**：`device_received` 由服务端自己记（投递成功即记），Agent 不需要发。

---

## 4. 设备 → 服务端 的帧

| type | 字段 | 说明 |
|---|---|---|
| `heartbeat` | — | 建议 15 秒一次。服务端据此刷新 `last_seen` |
| `ack` | `message_id`, `status` | `status ∈ {device_received, popup_displayed, read}` |
| `reply` | `sender_name`, `content`, `client_id` | 本机发的回复。`client_id` 是本地生成的随机串，用来对回执 |
| `screenshot_response` | `request_id`, `format`, `data_base64`, `width`, `height`, `error` | 二选一：有 `data_base64` 或有 `error` |
| `history_request` | `request_id`, `limit` | 主动拉历史（托盘打开对话窗口时用） |

### 截图回传

成功：
```json
{"type": "screenshot_response", "request_id": "a1b2c3", "format": "jpeg",
 "data_base64": "/9j/4AAQ...", "width": 2560, "height": 1440, "screen_locked": false}
```

失败（**必须回 `error`，不要静默丢弃**）：
```json
{"type": "screenshot_response", "request_id": "a1b2c3", "error": "屏幕已锁定"}
```

> 限流：服务端单帧上限 16MB。截图建议 JPEG 质量 80 以内、长边 ≤ 2560。
> 实测 4K 噪点图 base64 约 6.5MB，往返 0.33s，在预算内。

---

## 5. 服务端 → 设备 的回执

| type | 说明 |
|---|---|
| `reply_ack` | 对 `reply` 的回执：`{client_id, status}`。`status = "ok"` 成功；`"empty"` 内容为空；其他字符串是错误原因 |
| `history_response` | `{request_id, messages: [...]}` |
| `heartbeat_ack` | 心跳回执 |

**客户端必须在 10~15 秒内没收到 `reply_ack` 时给出可见反馈**，
不要让界面永远停在「发送中…」。

---

## 6. 发送队列（Agent 侧必须实现的可靠性要求）

这是踩过坑的地方，移植时不要省：

> ❌ 错误做法：发送前先判断「我连接着吗？没连接就直接报失败」。
> 连接状态的判断可能和实际不一致，会导致**能发的消息被误报成失败**。
>
> ✅ 正确做法：**不预判，直接尝试发；失败了才入队**，然后在下面两个时机重试补发：
> 1. 收到 `hello` 之后
> 2. 心跳循环里（每次心跳顺手检查一下队列）

截图响应**不要入队**（有时效性，过期就是废图）；`reply` / `ack` / `history_request` 都要入队。

---

## 7. Web 端 WebSocket（`/ws/web`）

连接后服务端发 `ready`，之后广播：

| type | 说明 |
|---|---|
| `ready` | `{server_time}` |
| `device_status` | 设备上下线 |
| `device_updated` | 设备改名等 |
| `device_deleted` | 设备被移除 |
| `message` | 新留言。带 `reply: true` 表示这是设备回复 |
| `message_status` | 某条消息的 ACK 状态推进了 |
| `wake` | 远程开机结果 |

心跳：客户端发 `{"type": "ping"}`，服务端回 `pong`。

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
| POST | `/api/messages` | 发送留言 `{sender_name, content, device_ids}` |
| GET | `/api/messages` | 消息列表（含每条对各设备的状态） |
| GET | `/api/conversations/{device_id}` | 某设备的对话明细（**双向**）。每条是原始 message 行，靠 `sender_kind`（`web`/`device`）判断方向，并带 `targets` |
| POST | `/api/messages/{id}/read` | 标记已读 |
| POST | `/api/devices/{id}/screenshot` | 请求截图，返回图片（**带鉴权，不是公开 URL**） |
| POST | `/api/devices/{id}/wake` | 通过米家插座开机 |
| GET | `/api/xiaomi/devices` | 米家设备列表 |
| GET | `/api/xiaomi/status` | 米家登录状态 |
| GET | `/api/events` | 事件留痕（排查用） |
| GET | `/healthz` | 健康检查（无需鉴权） |

---

## 9. 消息状态机

```
created → server_received → device_received → popup_displayed → read
```

- `created` / `server_received`：服务端收到就算，无需 Agent 参与
- `device_received`：投递成功，服务端自己记
- `popup_displayed`：**Agent 回报**（这条是「真的弹出来了」的唯一证据）
- `read`：用户点了「关闭窗口」

状态只前进不后退（`max()` 语义）。

---

## 10. 移植到 Android 的注意事项

1. **协议零依赖**：只用到 WebSocket + JSON + base64。Android 用 OkHttp WebSocket 就够，
   不需要任何本项目特有的库。
2. **`tools/cli_agent.py` 是协议参考实现**——只有 200 多行 Python，任何平台的对接都以它为准。
3. **心跳**：Android 受 Doze 影响，建议 15 秒心跳 + `ForegroundService`，
   否则息屏后连接会被系统掐掉。
4. **设备类型**：连接时 `type=phone`、`platform=android`，服务端会照常登记，
   网页控制台会显示成手机图标。
5. **截图**：Android 用 `MediaProjection` 需要用户授权一次（这是系统限制，绕不过），
   授权后可以长期截屏。授权失败时**要回 `error` 帧**，不要静默超时。
6. **昵称**：存 `SharedPreferences`，和服务端无关。
7. **发送队列**：见 §6，同样要实现（移动网络切换频繁，不实现会丢消息）。
