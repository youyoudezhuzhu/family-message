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

**PC 弹窗布局（Material Design 3）**

- **聊天式对话区占满整宽**——网页发来的靠左、本机回复的靠右，各带头像与圆角气泡。
  重开窗口时用服务端历史铺满，所以能看到上下文。
  新消息插入播「淡入 + 从下方 40px 滑入 + 微放大」，340ms 缓出。
  连续来消息是**接着往下追加**，不会关闭再弹（窗口只创建一次、反复复用）。
- 底部：昵称下拉（本机维护）+ 回复框（Enter 直接发送）+「关闭窗口」一次清空未读并回报已读。
- 右上角「设置」进配置页：配色方案、连接参数、开机自启、回复昵称。

> 早期版本的「右侧弹幕式历史流」已移除 —— 和网页端消息记录重复。

**配色与昵称色**

- 8 套配色（靛蓝/紫罗/青碧/松绿/琥珀/珊瑚/品红/天青），网页端和 PC 端都能改，**只存本地**。
- 消息颜色**按昵称分配**：同一昵称在任何一端、任何时候都是同一个颜色，
  与「从网页发来」还是「从 PC 回复」无关。取色算法三端一致，见 `docs/DESIGN-TOKENS.md` §3。

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

## 远程开机 / 关机

### 开机：米家智能插座（官方 OAuth2）

米家的接入**严格对照小米官方的 [ha_xiaomi_home](https://github.com/XiaoMi/ha_xiaomi_home)**
（`miot_cloud.py`），不引入 Home Assistant，只保留最小实现：

1. **OAuth2 授权码流程**（不保存账号密码）：网页「设置 → 米家」里点「获取授权链接」，
   浏览器里同意后页面会跳到 `homeassistant.local`（打不开是正常的），
   把地址栏整段粘回网页即可换到 `access_token` / `refresh_token` / `expires_in`
2. token 在剩余寿命的 **70%** 处提前续期（官方 `TOKEN_EXPIRES_TS_RATIO`），
   只有 refresh_token 也失效时才需要重新授权
3. 调用走 `https://ha.api.io.mi.com/app/v2/...`，`Authorization: Bearer<token>` 头

绑定模型是「**米家设备 + 动作(开/关) → 某台 PC**」。绑好之后，那台 PC 的卡片上才会出现
「开机」按钮，点击即执行该动作。

> ⚠️ 两点如实说明：
> 1. `redirect_uri` 必须是小米那边为该 `client_id` **注册过**的地址，我们复用的是官方集成的
>    注册地址，所以需要「粘贴 code」这一步；如果将来自己注册 OAuth 应用，改 `xiaomi.redirect_url` 即可。
> 2. 官方实现的鉴权头是 `Bearer<token>`（**中间没有空格**），这里照抄，不要顺手「修正」。

> 不同型号插座的 `siid/piid` 可能不同，绑定时可改（默认 2/1）；接入真实设备后请实测确认。

### 关机：由 PC 端 Agent 执行

设备卡片上的「关机」按钮会下发 `{"type":"shutdown","delay_seconds":5}`：

- PC 端执行 `shutdown /s /t 5`（**不加 `/f`**，让系统正常关闭程序，避免丢未保存内容）
- 延迟几秒是留给坐在电脑前的人反应时间，`shutdown /a` 可取消
- 设备离线时服务端直接返回 409，不会假装成功
- 不需要在 PC 上再点一次确认 —— PC Agent 是受 Server 信任的家庭设备 Agent（设计原则 §13）

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

## 故障排查

### 升级之后功能没变化？

**先看网页控制台左上角的版本号**（和 `curl http://<NAS>:18801/healthz` 的 `version` 字段）。

如果它没变成新版本，说明**旧进程还在跑旧代码**。fnOS 升级只替换文件，不会自动停旧进程；
旧进程把旧代码留在内存里、还占着端口，新进程绑不上端口就退出，而 PID 文件还在，
所以「状态」看起来一切正常。

本项目的 `upgrade_init` 会在升级前停服务，`cmd/main start` 也会在启动前清理占用端口的
陈旧进程，正常情况下不会再出现。真遇到了就手动停一次再启动：

```bash
# 看谁占着端口
ss -lntp | grep 18801
```

`tools/simulate-upgrade.sh` 会把这套流程跑一遍回归。

### PC 端发的消息网页上看不到 / 截图取不到

两者都是「PC → 服务器」方向，属于同一类问题。Agent 现在有发送侧看门狗：
心跳 45 秒收不到回执、或消息积压超过 12 秒发不出去，就自动重连并补发，
**不需要再手动点「保存并连接」**。

排查时看 `%APPDATA%\FamilyAgent\agent.log`（设置页有「打开日志」按钮），
搜这些标记：

- `⚠ 45 秒没收到心跳回执` / `⚠ 有 N 条消息积压` —— 发送链路断过，已自动恢复
- `→ reply` / `→ screenshot_response` —— 确实发出去了
- `✗ reply 发送失败` —— 发送失败原因

服务端对应的留痕在 `/api/events`（`handler_error` / `bad_frame`）。

## 文档

- [设计文档 docs/DESIGN.md](docs/DESIGN.md) — 架构、数据库 Schema、API、WS 协议、米家模块设计
- [通信协议 docs/PROTOCOL.md](docs/PROTOCOL.md) — 跨平台实现契约（写 Android/macOS Agent 看这份）
- [视觉规范 docs/DESIGN-TOKENS.md](docs/DESIGN-TOKENS.md) — 三端共用的调色板/组件/动效
- [server/](server/) — 服务端源码
- [pc-agent/](pc-agent/) — Windows Agent 源码

## 路线图

- [x] **Phase 1** 服务端 + 网页发送 + 全屏弹窗 + 在线状态 + ACK
- [x] **Phase 2** 设备管理：桌面截图、设备列表、远程开机
- [x] **Phase 3** 双向对话：PC 端弹窗回复、消息堆叠、历史弹幕流
- [x] **Phase 4** 米家官方 OAuth2 接入：授权、设备发现、开关绑定、远程开机
- [x] **Phase 4.5** 远程关机（服务端下发指令，PC Agent 执行）；两端彩色 emoji
- [x] **Phase 6** 完整 Material Design 3 设计系统（两段共用令牌；Light/Dark/System 三态；MD3 组件层与图标系统）
- [ ] **Phase 5** 消息历史分页、图片/文件、广播组、Android/Linux/macOS Agent

## 许可

MIT
