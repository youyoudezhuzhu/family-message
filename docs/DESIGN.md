# 家庭消息与设备控制系统 · 设计文档

> 对应需求文档 §28「AI Agent 的工作方式」要求的十项交付物。
> 版本 v0.1 · 2026-09-22

---

## 1. 需求理解

这不是一个聊天软件。它是一个**以 Server 为唯一中心的家庭设备控制总线**。

三句话概括：

1. **Web Sender 是唯一入口，不是用户体系。** 家里任何浏览器打开网页都是同一个发送者，昵称只是发送时贴的标签，不产生账号、不产生会话隔离。
2. **Device 是受信任的被控终端，不是聊天客户端。** Agent 常驻后台，收到消息后主动、立即、全屏抢占屏幕显示，而不是弹个系统通知。
3. **消息与设备管理是两层权限。** 发消息是「通知」，看截图/远程开机是「设备管理」——都属于 Server 对受信设备的指令，不需要设备端点击同意。

关键约束（决定了很多设计选择）：

- 所有通信必须过 Server，设备之间**不直连**（原则 5）
- 第一版只上 FastAPI + SQLite + WebSocket + C# WPF + Docker，**不引入 Redis / MQ / 微服务 / Electron / Home Assistant**（原则 6）
- 数据模型要预留 `Sender → Server → Receiver` 双向能力，尽管第一版只做 Web → Device（§23）

---

## 2. 最终系统架构

```
                    ┌──────────────────────────────┐
                    │        NAS (fnOS)            │
   浏览器 ──HTTP──▶ │  ┌────────────────────────┐  │
   (手机/PC/平板)   │  │  Family Message Server │  │
        ▲           │  │  FastAPI + SQLite      │  │
        │           │  │  + 连接中心 Hub         │  │
        └──WS───────│  └───────┬────────────────┘  │
                    └──────────┼───────────────────┘
                               │ WebSocket 长连接
              ┌────────────────┼────────────────┐
              ▼                ▼                ▼
        pc_001 书房电脑    pc_002 客厅电脑   phone_001(未来)
        Windows Agent     Windows Agent      Android Agent
              │
              │ 设备管理指令（截图 / 开机）
              ▼
        米家智能插座 ──MIoT Cloud──▶ 给 PC 上电
```

三层职责边界：

| 层 | 职责 | 明确不做 |
|---|---|---|
| Server | 唯一真值源：设备注册表、消息与状态、指令路由、米家凭据 | 不渲染 UI、不存明文密码、不做 P2P 中转 |
| Web | 家控台：发消息、看在线、看截图、点开机 | 不做账号体系、不做聊天会话列表 |
| Agent | 长连接常驻、全屏弹窗、截屏、托盘、自启 | 不做消息中转、不直连其他设备 |

---

## 3. 职责划分

### Server
- **设备注册表**：`device_id` / 名称 / 类型 / 状态 / 最后在线 / 设备令牌
- **连接中心（Hub）**：维护 `device_id → WebSocket`，处理心跳、断线、离线判定、重连替换
- **消息引擎**：落库 + 逐设备投递 + 五态 ACK 状态机 + 离线补投
- **指令通道**：截图请求按 `request_id` 配对 Future，实现「一问一答」的同步语义
- **米家模块**：独立于设备体系的 IoT 层，token 生命周期自管理
- **Web 订阅**：向所有浏览器广播设备上下线、消息状态流转

### Web
- 单一入口页，无登录用户概念（可选一个家庭访问口令）
- 发送人下拉（来自服务端 `senders` 配置）+ 接收设备多选 + 内容 + 发送
- 设备卡片：在线圆点、最后在线、`发送消息 / 查看桌面 / 远程开机`
- 消息记录：每条消息对每个设备的**五态进度标签**
- 截图以模态框展示，不新开页面

### PC Agent
- 后台常驻 + 系统托盘，开机自启
- 首次运行弹配置窗（Server URL / 设备名 / Device ID / 注册口令）
- WebSocket 长连接 + 心跳 + 指数退避重连
- 收到消息 → 立即全屏无边框 TopMost 窗口 → 回报 `popup_displayed` → 点「知道了」回报 `read`
- 收到 `screenshot_request` → 截屏 → base64 回传

---

## 4. 数据库 Schema

SQLite，WAL 模式，六张表。**Device（通信终端）与 XiaomiDevice（外部 IoT）严格分表**（§19）。

```sql
devices                -- 本系统的通信终端（PC / 未来的手机）
  id, device_id UNIQUE, name, type, platform, status('online'|'offline'),
  last_seen, token, ip, agent_version, created_at

messages               -- 消息主体（与接收方解耦，为广播/双向预留）
  id, sender_name, content, message_type, created_at

message_targets        -- 消息 × 设备 的投递与状态（一消息多目标）
  id, message_id, device_id,
  status('created'|'server_received'|'device_received'|'popup_displayed'|'read'),
  received_at, displayed_at, read_at
  UNIQUE(message_id, device_id)

xiaomi_devices         -- 米家 IoT 设备，与 devices 完全是两类实体
  id, name, urn, miot_device_id, device_type,
  power_capability, target_device_id, enabled

xiaomi_auth            -- 米家凭证（单行表 id=1）
  access_token, refresh_token, expires_at, ssecurity, user_id, updated_at

events                 -- 审计/排障日志（上线、投递、截图、开机…）
  id, device_id, kind, detail, created_at
```

设计说明：

- `messages` 与 `message_targets` 分离 → 一条消息可以发给 N 台设备，各自独立走状态机（§7）
- `messages` 只有 `sender_name` 没有 `sender_id` → 正向贯彻「昵称≠账号」（原则 2）
- 未来双向通信只需给 `messages` 加 `sender_kind`（`web`|`device`）+ `sender_device_id`，**不需要改表结构方向**（§23）
- `xiaomi_devices.target_device_id` 把「米家插座」和「它给哪台 PC 供电」绑起来，但两张表不混

---

## 5. API 设计

### Web → Server

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/config` | 发送人列表、是否需口令、服务端信息 |
| POST | `/api/login` | 家庭访问口令（配置了才需要） |
| GET | `/api/devices` | 设备列表（含实时在线状态，**不含 token**） |
| GET | `/api/devices/{id}` | 单设备详情 + 绑定的米家插座 |
| GET | `/api/devices/{id}/status` | 轻量状态查询 |
| PATCH | `/api/devices/{id}` | 重命名 |
| DELETE | `/api/devices/{id}` | 移除设备 |
| POST | `/api/messages` | 发消息：`{sender_name, content, targets[]}` |
| GET | `/api/messages` | 消息历史（可 `?device_id=` 过滤） |
| POST | `/api/messages/{id}/read` | Web 侧标记已读 |
| **POST** | **`/api/devices/{id}/screenshot`** | **设备管理：取一张桌面截图** |
| **POST** | **`/api/devices/{id}/wake`** | **设备管理：米家上电开机** |
| GET | `/api/xiaomi/devices`、`/api/xiaomi/status` | 米家设备与登录态 |
| GET | `/api/events` | 审计事件 |
| GET | `/healthz` | 健康检查 |

### WebSocket

| 路径 | 用途 |
|---|---|
| `/ws/device/{device_id}` | Device Agent 长连接（注册/心跳/收消息/回 ACK/回截图） |
| `/ws/web` | 浏览器订阅实时事件 |

**为什么不用统一 `/ws`**（§20 留给我决定）：两条通道的**信任级别不同**。设备通道携带设备身份并执行设备管理指令（截图/开机），Web 通道只读订阅。拆开后鉴权、限流、审计都各自独立，任何一个被攻破不会横向污染另一个。

---

## 6. WebSocket 消息协议

### 设备通道

```
连接  ws(s)://host/ws/device/{device_id}?token=&name=&type=&platform=&enroll_token=

Server → Agent
  {"type":"hello","device_id","token","server_time","offline_after_seconds"}
  {"type":"heartbeat_ack","server_time"}
  {"type":"message","message_id","sender_name","content","created_at","auto_close_seconds"}
  {"type":"screenshot_request","request_id"}

Agent → Server
  {"type":"heartbeat"}
  {"type":"ack","message_id","status":"device_received|popup_displayed|read"}
  {"type":"screenshot_response","request_id","format":"jpeg","data_base64","width","height","screen_locked"}
  {"type":"event","kind","detail"}          # 可选诊断
  {"type":"device_info","name","platform"}  # 可选自报
```

### Web 通道

```
Server → Browser
  {"type":"ready","server_time"}
  {"type":"device_status","device_id","status","device?"}
  {"type":"device_updated"|"device_deleted"}
  {"type":"message","message":{...含 targets...}}
  {"type":"message_status","message_id","device_id","status","target"}
  {"type":"wake","device_id","result"}

Browser → Server
  {"type":"ping"}  →  {"type":"pong"}
```

### 五态状态机

```
created → server_received → device_received → popup_displayed → read
  入库        准备投递           已送达Agent         已全屏显示         用户点了「知道了」
```

单调前进，服务端拒绝回退（`messages.advance()` 里做 rank 比较）。离线设备的消息保持在 `server_received`，**设备下次上线时自动补投**。

---

## 7. PC Agent 生命周期

```
安装/首次运行
   └─▶ 配置窗口（Server URL / 设备名 / Device ID / 注册口令）
        └─▶ 写入 %APPDATA%\FamilyAgent\config.json
             └─▶ 连接 Server ──▶ 服务端注册并下发设备 token（本地保存）
                  └─▶ 进入托盘常驻
                       ├─ 心跳 15s
                       ├─ 收 message ──▶ 全屏 TopMost 弹窗 ──▶ ack
                       ├─ 收 screenshot_request ──▶ 截屏回传
                       └─ 断线 ──▶ 指数退避重连（2s→30s 封顶）
```

- 开机自启：写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
- 全屏弹窗：无边框 + `Topmost=true` + 覆盖任务栏工作区，多显示器时铺满主屏
- 弹窗行为：默认必须点「知道了」才关（`auto_close_seconds=0` 时），点完回报 `read`
- 保底：Agent 未连上时消息不丢，服务端保留待投递队列

---

## 8. 米家模块设计

参考 `XiaoMi/ha_xiaomi_home` 的认证与 MIoT 调用方式，**只借思路不引入 Home Assistant**。

```
首次：账号密码 → serviceLoginAuth2(sid=xiaomiio)
        → 拿 ssecurity / userId / passToken
        → 用 location 换 serviceToken
        → 全部落库 xiaomi_auth（密码不落库）

日常：ensure_auth() 检查 expires_at
        ├─ 剩余 > 刷新阈值 → 直接用
        ├─ 剩余 < 刷新阈值 → refresh_token 换发（oauth2/token）
        └─ 刷新失败 → 退回账号密码登录

调用：签名请求
        _nonce = base64(random8 + timestamp4)
        signed_nonce = base64(sha256(nonce + ssecurity))
        signature = base64(hmac_sha256(signed_nonce, path + "&" + data))
        GET https://api.io.mi.com/app{path}?data&_nonce&signature
             Cookie: userId, serviceToken

401 → 立即 refresh 一次后重试；仍失败才要求用户重新登录（§15）
```

已实现的 MIoT 调用：`/home/device_list`（设备发现）、`/miotspec/prop/set`（插座开关，默认 `siid=2, piid=1`）。

远程开机闭环：`网页[开机] → Server → MIoT → 插座通电 → PC 启动 → Agent 上线 → 网页转 🟢`

**注意**：`siid/piid` 不同型号插座可能不同，接入时用真实设备验证后再写死到 `xiaomi_devices.power_capability`，不凭空假设。

---

## 9. Docker 部署方式

fnOS 上的约束（已实测）：本地无 `docker build` 能力（hermes-agent 不在 docker 组，`dockermgr` API 也没有 build 端点），但 **NAS 能直连公共 registry**。

因此采用：

```
GitHub Actions 构建镜像 → push 到 ghcr.io → NAS 用 trim-cli docker image pull
                                              → container create --port 18801:18801
                                              → 挂载 /data（SQLite + 截图）
```

- 单容器：一个 uvicorn 进程同时提供 REST + WebSocket + 静态前端（§17「保持一个主服务容器」）
- 数据卷：`/vol1/@apphome/hermes-agent/data/family-data` → 容器 `/data`
- 配置：`config.yaml` 只读挂载，敏感项走环境变量（`FM_WEB_PASSWORD` 等）

---

## 10. Phase 1 开发计划与完成情况

| # | 任务 | 状态 |
|---|---|---|
| 1 | FastAPI 服务端骨架 + SQLite Schema | ✅ 已实现并运行 |
| 2 | 设备注册 / 心跳 / 在线状态 / 离线巡检 | ✅ 已验证 |
| 3 | WebSocket 设备通道 + 五态 ACK 状态机 | ✅ 端到端跑通 |
| 4 | Web 家控台（发送人 / 多设备 / 消息记录） | ✅ 已实现 |
| 5 | 截图请求链路（Web→Server→Agent→回传） | ✅ 已验证（1280×720 JPEG） |
| 6 | Python 参考 Agent（协议基准 + 联调工具） | ✅ 已验证 |
| 7 | Windows C#/.NET WPF Agent | ⏳ 待做（Phase 1 的一部分） |
| 8 | Docker 化 + NAS 部署 | ⏳ 待做 |
| 9 | 米家 MIoT 模块 | ✅ 代码完成，待真实账号联调（Phase 3） |

**Phase 1 验证证据**（2026-09-22 13:16）：

```
妈妈 → 测试书房电脑「下来吃饭了」
  delivered=['pc_test01']  offline=[]
  pc_test01  read  received=13:16:42 displayed=13:16:42 read=13:16:42

截图请求 → ok=True  1280x720  36193 bytes  → /shots/pc_test01_2026-09-22_131647_48d7b5.jpg
```

---

## 后续 Phase 路线

- **Phase 2** 设备管理完善：重命名、删除、米家插座绑定 UI、截图历史
- **Phase 3（已完成）** 双向对话：PC 端弹窗回复、消息堆叠、历史弹幕流
- **Phase 4** 米家：真实账号联调、设备发现、插座开关验证、开机闭环
- **Phase 5** 消息完善：历史分页、图片/文件、广播组；多平台 Agent（Android / Linux / macOS）

---

## 附：双向对话的实现（v0.2.0 增补）

### 数据模型

`messages` 增加两列，不改表方向：

| 列 | 说明 |
|---|---|
| `sender_kind` | `web` = 网页发出；`device` = 设备回复 |
| `sender_device_id` | 回复时记录来源设备 |

老库启动时自动 `ALTER TABLE` 补列（`_migrate()`），索引建在迁移之后
（放前面会让 `CREATE TABLE IF NOT EXISTS` 之后的 `CREATE INDEX` 在旧库上直接报错）。

设备回复**不写 `message_targets`**——它的接收方是「Web Sender」这个统一入口，
不是某台设备。对话串由 `conversation()` 双向查询拼出：

```sql
WHERE (sender_kind='device' AND sender_device_id=?)
   OR (sender_kind='web' AND EXISTS (SELECT 1 FROM message_targets t
                                     WHERE t.message_id=m.id AND t.device_id=?))
```

### 协议扩展

```
设备 → 服务器   {"type":"reply","content","client_id"}
服务器 → 设备   {"type":"reply_ack","client_id","message_id","status"}
服务器 → 设备   {"type":"message", ..., "history":[{"message_id","sender_name",
                 "content","created_at","direction":"in|out"}]}
设备 → 服务器   {"type":"history_request","request_id","limit"}
服务器 → 设备   {"type":"history_response","request_id","messages":[...]}
```

消息推送**直接内嵌最近 N 条历史**（`message.history_limit`），弹窗右栏一次渲染完成，
不需要额外往返。`direction` 已按设备视角翻转：`in` = 发给这台设备的，`out` = 它自己发的。

### PC 弹窗

- 窗口**只创建一次、反复复用**。v0.1.0 每来一条消息都 `ForceClose()` + `new PopupWindow()`，
  连发时会反复销毁重建 → 闪屏。现在新消息只是往堆叠区追加。
- 消息堆叠：字号按距最新的级数递减（`0.80^distance`，下限 0.45），透明度同步递减。
- 入场动画：淡入 + 下方 46px 滑入 + 0.97→1.0 微放大，360ms `CubicEase.EaseOut`。
- 右栏历史改成弹幕流（窄条、无卡片、顶部透明度蒙版渐隐）。
- 「知道了」= 对堆叠内所有未读消息各回报一次 `read`，然后清空堆叠。
