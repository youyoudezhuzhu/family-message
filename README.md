# 家庭消息与设备控制系统

部署在家庭 NAS 上的**自托管**消息 + 设备管理系统。表面上像家庭群聊，但它不是聊天软件——
核心模型是 **Web Sender → Server → Device Agents**，Server 是唯一中心。

```
Web Sender（任意浏览器，统一入口，不需要账号）
        ↓
      NAS Server（FastAPI + SQLite + WebSocket）
        ↓
 Device Agents（Windows PC，未来扩手机）
```

## 它解决什么问题

| 场景 | 做法 |
|---|---|
| 叫书房的人下来吃饭 | 网页发一条 → PC **立即全屏弹窗**（不是系统通知），并回报「弹窗已显示 / 已点知道了」 |
| 想知道某台 PC 在不在线 | 网页看在线状态 + 最后在线时间 |
| 想知道 PC 上现在什么画面 | 网页点「查看桌面」→ 直接返回一张当前桌面截图 |
| PC 关机了想远程开 | 网页点「开机」→ 米家智能插座通电 → PC 启动 → Agent 自动上线 |

## 五条设计原则

1. **Web Sender ≠ Device** — 网页端是统一发送入口；PC / 手机是设备
2. **发送昵称 ≠ 用户账号** — 昵称只是发消息时选的显示名，不产生账号、不隔离会话
3. **聊天权限 ≠ 设备管理权限** — 截图、远程开机属于设备管理，是 Server 对受信设备的指令
4. **PC Agent ≠ 普通聊天客户端** — 后台常驻，收到消息主动、立即、全屏抢占屏幕
5. **Server 是中心** — 所有设备通过 Server 通信，设备之间不直连

## 目录结构

```
family-message/
├── server/            FastAPI 服务端（API + WebSocket + 静态前端托管）
│   ├── main.py        路由、WS 协议、网关前缀适配
│   ├── db.py          SQLite Schema 与访问层
│   ├── hub.py         连接中心：设备长连接 / 请求配对 / 离线巡检
│   ├── run.py         fnOS 启动入口（TCP + unix socket 双绑）
│   └── services/      devices / messages / xiaomi
├── web/               家控台前端（原生 JS，零构建）
├── pc-agent/          Windows Agent（C# / .NET 8 / WPF）
├── fpk/               fnOS 打包目录（manifest / cmd 生命周期 / 向导 / 图标）
├── tools/             参考 Agent、图标生成、安装模拟
├── docs/DESIGN.md     设计文档（需求 §28 要求的十项交付物）
└── build-fpk.sh       一键打包 fpk
```

## 快速开始

### 1. 在 NAS 上安装服务端

从 [Releases](../../releases) 下载 `family-message_x.y.z.fpk`，到
**fnOS 应用中心 → 手动安装** 选择该文件。

> 安装向导会问三件事：家庭控制台访问口令（可留空）、设备注册口令、可选发送昵称。
> 安装时会自动创建 Python 运行环境并安装依赖，需要 NAS 能访问 PyPI。

安装完成后打开：`http://<NAS的IP>:18801`

### 2. 在 PC 上装 Agent

从 [Releases](../../releases) 下载 `FamilyAgent-win-x64.zip`，解压得到 `FamilyAgent.exe`，
双击运行，填写：

- **服务端地址**：`http://<NAS的IP>:18801`
- **设备名称**：例如「书房电脑」
- **设备 ID**：保持默认即可（同一台机器固定不变）
- **注册口令**：第 1 步里设置的那个

勾选「Windows 启动时自动运行」，点「保存并连接」。之后它会常驻系统托盘，开机自动上线。

### 3. 用起来

网页上选发送人 → 选设备 → 输入内容 → 发送。
PC 会立刻全屏弹出这条消息，点「知道了」关闭；网页上的状态会依次变成
「服务器已接收 → PC 已收到 → 弹窗已显示 → 已点击知道了」。

## 消息状态机

```
created → server_received → device_received → popup_displayed → read
 入库       准备投递           已送达 Agent        已全屏显示        用户点知道了
```

状态单调前进。设备离线时消息不会丢——保留在 `server_received`，**设备下次上线自动补投**。

## 双向对话（PC ⇄ 网页）

PC 端不是单向喇叭：收到消息的弹窗里可以直接回复，回复会落库并实时出现在网页控制台。

```
网页 ──发送──▶ Server ──▶ PC 弹窗（左侧消息堆叠）
网页 ◀──广播── Server ◀──回复── PC 弹窗（右侧回复框）
```

**PC 弹窗布局**

- 左栏：**聊天式对话区**——网页发来的靠左、本机回复的靠右，各带头像与圆角气泡，
  最新的带描边高亮。重开窗口时用服务端历史铺满，所以能看到上下文。
  新消息插入播「淡入 + 从下方 40px 滑入 + 微放大」，340ms 缓出。
  连续来消息是**接着往下追加**，不会关闭再弹（窗口只创建一次、反复复用）。
- 右栏：**弹幕式对话流**——窄条、无边框无卡片无滚动条，文字往上流，
  顶部用透明度蒙版渐隐。家里发来的青色、自己回复的金色。
- 底部：昵称下拉（本机维护）+ 回复框（Enter 直接发送）+「关闭窗口」一次清空未读并回报已读。

**发送昵称完全本地化**

昵称不是账号，也不存在服务端：网页端存浏览器 `localStorage`（顶栏「管理昵称」随时增删改），
PC 端存 Agent 配置（设置窗口可增删改，弹窗下拉可选）。发消息时把名字作为普通字符串带上去，
服务端只负责存 `sender_name`。所以 FPK 安装向导只问访问口令和设备注册口令，不问昵称。

**协议**

```
设备 → 服务器   {"type":"reply","content":"...","client_id":"..."}
服务器 → 设备   {"type":"reply_ack","client_id","message_id","status"}
服务器 → 设备   {"type":"message", ..., "history":[{...}]}   # 弹窗右侧直接渲染，省一次往返
设备 → 服务器   {"type":"history_request","request_id","limit"}
服务器 → 设备   {"type":"history_response","request_id","messages":[...]}
```

消息表用 `sender_kind`（`web` / `device`）+ `sender_device_id` 区分方向，
对话串由双向查询拼出；老库启动时自动 `ALTER TABLE` 补列。这也正是设计文档里
预留的 `Sender → Server → Receiver` 模型——加手机 Agent 时不用改表结构。

## 远程开机（米家）

在服务端 `config.yaml` 里打开 `xiaomi.enabled` 并填账号，程序会：

1. 登录一次拿到 `ssecurity` / `userId` / `passToken`，落库到 `xiaomi_auth`（密码不落库）
2. 每次调用走签名请求（HMAC-SHA256 + nonce），凭证快过期时用 refresh_token 自动续期
3. 只有续期也失败时，才需要重新登录

然后把米家插座和 PC 绑定（`xiaomi_devices.target_device_id`），网页上的「远程开机」就会
`Server → MIoT → 插座通电 → PC 启动 → Agent 上线 → 网页转 🟢`。

> 不同型号插座的 `siid/piid` 可能不同，接入时用真实设备验证后再写死，不要凭空假设。

## 技术栈与取舍

第一版刻意保持简单：**FastAPI + SQLite + WebSocket + C# WPF**，单容器 / 单进程。
不引入 Redis、消息队列、PostgreSQL、微服务、Electron、Home Assistant。

- 前端用原生 JS（无构建步骤）——改完刷新即生效，NAS 上不需要 Node 工具链
- SQLite 用 WAL 模式 + 全局连接 + 互斥锁，家庭级并发完全够用
- 服务端同时监听 TCP 端口（给 PC Agent）和 unix socket（给飞牛统一网关），共用一个 ASGI app

## 本地开发

```bash
# 服务端
uv venv /path/to/venv --python 3.12
uv pip install --python /path/to/venv/bin/python -r server/requirements.txt
cd server && /path/to/venv/bin/python -m uvicorn main:app --host 0.0.0.0 --port 18801

# 参考 Agent（Linux/macOS/Windows 都能跑，也是协议基准实现）
python tools/cli_agent.py --server ws://127.0.0.1:18801 \
  --device-id pc_dev001 --device-name "开发测试机" --enroll-token family-2026
```

## 打包 fpk

```bash
./build-fpk.sh                       # 产出 family-message_x.y.z.fpk
./tools/simulate-install.sh          # 不装应用中心，沙箱验证安装生命周期
```

> 注意：`fnpack` 会把被处理的目录权限封成 `0000`（fnOS 的 ACL 行为），所以脚本一律在
> 独立暂存目录打包，绝不直接对仓库目录操作。

Windows Agent 由 GitHub Actions 云编译（`.github/workflows/build-windows-agent.yml`），
不需要本地装 .NET SDK + Windows SDK。

## 文档

- [设计文档 docs/DESIGN.md](docs/DESIGN.md) — 架构、数据库 Schema、API、WS 协议、米家模块设计
- [server/](server/) — 服务端源码
- [pc-agent/](pc-agent/) — Windows Agent 源码

## 路线图

- [x] **Phase 1** 服务端 + 网页发送 + 全屏弹窗 + 在线状态 + ACK
- [x] **Phase 2** 设备管理：桌面截图、设备列表、远程开机
- [x] **Phase 3** 双向对话：PC 端弹窗回复、消息堆叠、历史弹幕流
- [ ] **Phase 4** 米家真实账号联调、设备发现、插座绑定 UI
- [ ] **Phase 5** 消息历史分页、图片/文件、广播组、Android/Linux/macOS Agent

## 许可

MIT
