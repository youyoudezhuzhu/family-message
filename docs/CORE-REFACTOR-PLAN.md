# Core 层解耦重构 · 勘察与分阶段方案

> 状态：**方案文档，未动任何代码**。本文件是「把跨平台能力从 WebUI 下沉到 Core」的施工图，
> 不是已完成记录。所有「现状」描述都来自当前工作区代码，并标注了文件名 + 行号/函数名。
>
> 一句话目标：**不要再把 WebUI 当成 Agent 的运行时核心；把它降级为一种客户端 UI 实现。**
>
> 目标形态：
>
> ```
> NAS Server ── WebSocket ─┬─ Windows Client（Native UI：WPF + WebView2 壳，本机页）
>                          ├─ Android Client（Native UI，未来）
>                          └─ Web Client（NAS 托管的网页控制台 WebUI）
> ```
>
> 勘察时间：2026-09-28。勘察基线：`git HEAD = 80f5e82`，**同时**纳入当时未提交的工作区改动
> （`pc-agent/FamilyAgent/AgentClient.cs` 等，见 §2.7-①，两者结论不同，必须分清）。

---

## 0. 结论速览（先看这段）

| 判断 | 依据 |
|---|---|
| 好消息：**协议本身已经是平台中立的** | 纯 WebSocket + JSON + base64，无二进制帧（`docs/PROTOCOL.md:8`），服务端 `server/hub.py:150-169` 只做 `send_json` |
| 好消息：**PC 端界面已经不在 NAS 网页里** | `docs/PC-LOCAL-UI.md:15-21`、`WebHostWindow.xaml.cs:411-492`（`InitWebViewAsync`）加载 exe 自带 `shell/app.html`，不再加载 NAS 的 `index.html` |
| 坏消息：**分层并没有真的发生** | 连接/消息/ACK 逻辑与 WPF + WebView2 仍写在同一批「胖类」里：`App.xaml.cs` 1120 行、`WebHostWindow.xaml.cs` 865 行、`AgentClient.cs` 756 行 |
| 坏消息：**消息的「显示」与 ACK 强绑定** | `popup_displayed` 只有页面回报 `web.ack` 之后才发出：`App.xaml.cs:302-305`、`WebHostWindow.xaml.cs:357-361`、`web/static/shell.js:536` |
| 坏消息：**客户端没有本地持久化** | 只有内存队列：`WebHostWindow.xaml.cs:95`（`_pending`）、`:98`（`_queued`，无上限）。WebView2 加载失败 → 消息永远只堆在内存里，且永不回 ACK |
| 结论：**重构的真正对象是「PC 宿主内部的分层」，不是「把 WebUI 拆掉」** | 见 §2.7-⑤ |

**最重要的一条风险（先说）**：仓库里有一个**尚未结案**的实时消息 bug（`docs/BUG-PC-REALTIME.md`），
根因怀疑正好落在「宿主 → WebView2 页面」这段桥接上。**先定位根因、再动这段代码**，
两者的先后关系见 §9.1。

---

## 1. 本轮不做的事（防跑偏）

1. **不引入任何跨平台 UI 框架**：MAUI / Flutter / React Native / Kotlin Multiplatform / Avalonia 全部不引入。
   本轮只做 Core 解耦，Android 客户端本轮**不写一行代码**。
2. **不删 WebUI**（`web/`、`web/static/`、`web/shell/` 全部保留）。PC-LOCAL-UI 之后它们的分工是：
   `web/` 是 NAS 托管的独立 Web 客户端；`web/shell/` 是 PC 端宿主加载的本机界面。
   WebUI **降级为客户端 UI 实现之一**，不是被删掉。
3. **不动服务端协议语义**：`server/` 本轮不改帧类型、不改字段含义、不加 `since` 参数。
   协议里确实缺的东西一律记入 §9 开放问题，不在这轮顺手加。
4. **不重写 UI**：第一阶段必须是「把连接与消息逻辑从 UI 里抽干净」，WebView2 里的界面照旧
   （`web/shell/app.html` 一个文档三视图：`client` / `popup` / `settings`，`pc.js:117` `setView`）。
5. **不为修 bug 而重构，也不为重构而改 bug 行为**：每个阶段的行为等价性要能被现有工具验证
   （`tools/` 下已有 `diag_realtime.py`、`probe_robustness.py`、`test_bidirectional.py`、`dev_keepalive.py`、`cli_agent.py`）。

---

## 2. 现状勘察（读代码得出，逐条给依据）

### 2.1 仓库结构与规模

| 部分 | 路径 | 规模（行） | 技术栈 |
|---|---|---|---|
| 服务端 | `server/` | 1050 + 264 + 218 + 106 + 55 + 79 + 132 + 229 + 187 + 612 = **2932** | Python / FastAPI / SQLite / WebSocket |
| 网页控制台 | `web/index.html`(594) + `web/static/`(app.js 1687、style.css 917、shell.js 916、tokens.css 207、chat.js 131、shell.css 282) | **4734** | 原生 HTML/CSS/JS |
| PC 本机界面 | `web/shell/`（app.html 267、offline.html 239、boot.html 82、pc.js 673、pc.css 375） | **1636** | 原生 HTML/CSS/JS，构建时复制进 exe 的 `shell/` |
| Windows Agent | `pc-agent/FamilyAgent/`（C# + XAML） | **5060** | .NET 9 / WPF / WebView2 |

构建/发布链路：Windows Agent **只能**在 Windows 上编译，走 GitHub Actions
（`.github/workflows/build-windows-agent.yml`，`runs-on: windows-latest`），本机（NAS，Linux）**没有 dotnet**
（实测 `which dotnet` → 无），发布前先跑静态检查 `tools/check_agent_refs.py`。
→ 这一点直接决定了新阶段的可行性判据（见 §9.8）。

### 2.2 `pc-agent/FamilyAgent/` 逐文件职责与「是否碰 UI」

「碰 UI」= 直接引用 `Dispatcher` / `System.Windows`（WPF）/ `Microsoft.Web.WebView2` / `WinForms` / `Window` 类型。

| 文件 | 行 | 职责 | 碰 UI？ | 依据 |
|---|---|---|---|---|
| `AgentClient.cs` | 756 | 唯一一条 WebSocket 连接循环 + 心跳 + 收发分派 + 发送队列 + 截图回传 + 能力上报 | **基本不碰**（4 处命中全是注释；但**有两个静态依赖**，见下） | 命中行：191、335、359、495（均为注释/`Environment.OSVersion`） |
| `App.xaml.cs` | 1120 | 进程启动编排、托盘（WinForms `NotifyIcon`）、窗口生命周期、**服务端事件 → Dispatcher → UI**、设置/关机/截图/解锁落地 | **重度**（43 处） | 订阅 `Client.*` 事件：:137-144；`Dispatcher.Invoke`：:741、:751、:784、:810、:843、:854；托盘：:984 `SetupTray` |
| `WebHostWindow.xaml.cs` | 865 | WebView2 宿主：窗口形态、三个页面视图、桥接、**消息缓存队列**、自动关闭、页面就绪判定 | **重度**（76 处） | `_queued`：:98；`PushMessage`：:229-238；`OnPageReady`：:336-355；`OnPageAck`：:357-361；`DispatcherTimer`：:74-75、:121、:798 |
| `JsBridge.cs` | 539 | 桥的**格式层**：宿主→页面 `host.*` 帧构造、页面→宿主 `web.*` 分派 | **刻意不碰**（自己声明：:19-20、:516） | 仅通过注入的 `Sender` 委托输出；`HandleFromWeb`：:267-449 |
| `SessionState.cs` | 253 | Windows 会话状态探测（锁屏/登录/注销），变化时通知 | **碰**（8 处） | `Dispatcher.Invoke`：:188-190（回调切 UI 线程）；`Changed` 事件：:43 |
| `ScreenCapture.cs` | 70 | 桌面截图 JPEG + base64 | **碰平台 API** | `using System.Windows.Forms`：:5；`Screen.PrimaryScreen`：:22 |
| `PowerControl.cs` | 51 | 关机/取消（`shutdown.exe`） | 不碰 UI | :25-33 走 `Process.Start` |
| `Presence.cs` | 148 | 「登录前 headless / 登录后交互式」两个实例的连接交接（文件心跳） | 不碰 | 头注释 :8-28；`StartSupervisor`：:95 |
| `SingleInstance.cs` | 258 | 文件式单实例仲裁 + 把已有窗口拉前 + 版本不一致告知 | 不碰 | 头注释 :9-25 |
| `AutoStart.cs` | 443 | 开机自启：Run 项 + 两个计划任务（`FamilyAgent-Boot` ONSTART / `FamilyAgent-Logon` ONLOGON） | 不碰 UI（碰注册表/计划任务） | 头注释 :10-22 |
| `UnlockGuard.cs` | 257 | `unlock_request` 的归属/动作/时效/重放校验（协议层语义，非 UI） | 不碰 | `UnlockReply`：:10-25 |
| `Config.cs` | 146 | 本地配置（`%APPDATA%\FamilyAgent\config.json`） | 不碰 | 字段清单 :18-52；`IsConfigured`：:54 |
| `AgentLog.cs` | 53 | 本地日志（512KB 轮转） | 不碰 | :12-20 |
| `HistoryItem.cs` | 45 | 弹窗历史条目模型 | 不碰 | :6-20 |

**关键发现（分层的真实缺口）**：`AgentClient.cs` 本身几乎不依赖 UI，但它**依赖两个 UI 层的静态成员**：

- `App.IsHeadless`（`AgentClient.cs:381`，在 `BuildCapabilities()` 里读）—— Core 直接读 UI/进程层静态属性；
- `SessionState.Current`（`AgentClient.cs:401`，在 `BuildHeartbeatJson()` 里读）—— Core 直接读 Windows 专用类。

也就是说：「Core 不允许依赖 WPF/WebView2/Dispatcher」这条要求，**卡在最后两行静态引用上**，
其余部分比预期干净。这是本轮最划算的切口。

### 2.3 收一条消息的完整链路（现在的真实控制流）

```
server: _broadcast_message()                 server/main.py:319-340   只推「当前在线的」设备
   └─ _device_payload()                      server/main.py:291-316   带 history / auto_close_seconds
        └─ HUB.send_to_device()              server/hub.py:150-169    send_json + 投递日志
             ↓ ws
core-ish: AgentClient.ReceiveLoopAsync()     AgentClient.cs:440-483   收帧/拼消息/取关闭码
   └─ Dispatch()                             AgentClient.cs:485-571   type → 事件（裸 JsonElement）
        └─ event MessageReceived              AgentClient.cs:55,527
             ↓ 订阅（UI 层）
ui: App.OnMessageReceived()                  App.xaml.cs:749-780     ★ Dispatcher.Invoke 包住整个函数体
   ├─ IsHeadless → Client.Ack(id,"delivered")          App.xaml.cs:762-768（见 §9.4「delivered」不是合法状态）
   └─ host.ShowPopup() + host.PushMessage(el, autoClose)  :777-778
        └─ WebHostWindow.PushMessage()       WebHostWindow.xaml.cs:229-238
             ├─ !Bridge.PageReady → _queued.Add(...)   :231-236   ★ 只进内存，永不 ACK
             └─ SendMessage()                :743-751   _pending.Add(id)
                  └─ Bridge.PostMessage()    JsBridge.cs:85-111  → host.message
                       ↓ WebView2 postMessage
ui(web): pc.js ackLead() / onMessage()       web/shell/pc.js:183-191、199-215 画到屏幕
   └─ web.ack                                pc.js:183（收到帧后向宿主回报）
        └─ JsBridge AckReceived              JsBridge.cs:316；WebHostWindow.OnPageAck :357-361
             └─ host.MessageAcked → Client.Ack(id,"popup_displayed")  App.xaml.cs:302-305
```

**这条链路里有 3 个平台耦合点**（重构要切开的地方）：

1. **渲染前后由 UI 决定**：`popup_displayed` 只有 WebView2 真的画出气泡才会发出（`App.xaml.cs:302-305`）。
   WebView2 坏掉 → 消息**收得到但 ACK 链断**（服务端侧 `message_targets.status` 停在 `device_received`）。
2. **未就绪消息只存在 UI 内存里**：`WebHostWindow._queued`（`:98`，无上限、无持久化、进程退出即丢）。
3. **协议解析泄漏到 UI 层**：Core 把裸 `JsonElement` 抛给 UI（`AgentClient.cs:527/540/545/561`），
   于是 UI 层自己去 `TryGetProperty("message_id"/"auto_close_seconds"/"status"/"client_id")`
   （`App.xaml.cs:753-760`、`:788-793`、`WebHostWindow.xaml.cs:809-815`、`JsBridge.cs:85-111`）。
   → Core 没有「协议边界」，Android 端要么照抄这段解析，要么重写。

### 2.4 `web/shell/` 与 `web/static/`：哪些必须跑在 WebView2 里

| 文件 | 行 | 职责 | 必须跑在 WebView2 里？ |
|---|---|---|---|
| `web/shell/app.html` | 267 | PC 端**唯一**界面文档，三视图；`<script>` 只引 `chat.js` + `pc.js`（:264-265） | ✅ 是（PC 端界面本体） |
| `web/shell/pc.js` | 673 | 视图切换、设置表单、桥消息分流（`host.*` → 界面） | ✅ 是 |
| `web/shell/boot.html` | 82 | 首屏占位 | ✅ 是（`Stage.Boot`） |
| `web/shell/offline.html` | 239 | 「从未配置过」时的诊断/配置页 | ✅ 是（`Stage.Offline`） |
| `web/shell/pc.css` | 375 | PC 界面样式 | ✅ 是 |
| `web/static/chat.js` | 131 | **气泡组件**，与网页端共用同一源文件，构建时复制成 `shell/chat.js` | ⚠️ 是「UI 组件」，两端共用（`FamilyAgent.csproj:66-68`） |
| `web/static/tokens.css` | 207 | 设计令牌，同上复制成 `shell/tokens.css` | ⚠️ 同上（`FamilyAgent.csproj:69-71`） |
| `web/static/shell.js` | 916 | 旧的「网页端壳模式」，文件头自己写明**壳模式下页面不连 `/ws/web`**（:9、:159、:434） | ❌ 已标废弃、当前未被 PC 端使用（`docs/PC-LOCAL-UI.md:130-131` 说「保留一版再清理」） |
| `web/index.html` + `web/static/app.js` | 594 + 1687 | NAS 托管的网页控制台（设备页/截图/关机/解锁/米家） | ❌ 独立 Web 客户端，与 PC 端无关 |

**结论**：`web/shell/*` 是「UI 实现」，`web/static/shell.js` 是**可回收的负债**（§5 Phase 5 处理）。
**它们都不该承载协议逻辑** —— 现状里 `pc.js` 已经只认 `host.*` 帧（`pc.js:547-558` 的 `HANDLERS`），
这点是合格的；真正的协议逻辑在 C# 宿主里。所以「下沉到 Core」要移动的是 **C# 侧**，不是 JS。

### 2.5 服务端现有端点与 WS 帧（照 `server/main.py` / `server/hub.py` 实列）

**HTTP 端点**（`grep -nE '@app\.' server/main.py`）：

| 方法 | 路径 | 行 |
|---|---|---|
| POST | `/api/login` | main.py:148 |
| GET | `/api/config` | :163 |
| GET | `/api/devices` | :179 |
| GET | `/api/devices/{device_id}` | :194 |
| GET | `/api/devices/{device_id}/status` | :204 |
| PATCH | `/api/devices/{device_id}` | :224 |
| DELETE | `/api/devices/{device_id}` | :234 |
| POST | `/api/messages` | :355 |
| GET | `/api/messages` | :378 |
| GET | `/api/conversations/{device_id}` | :390 |
| POST | `/api/messages/{message_id}/read` | :403 |
| POST | `/api/devices/{device_id}/screenshot` | :419 |
| POST | `/api/devices/{device_id}/wake` | :474 |
| POST | `/api/devices/{device_id}/shutdown` | :491 |
| POST | `/api/devices/{device_id}/unlock` | :547 |
| GET/POST/PATCH/DELETE | `/api/xiaomi/*`（11 个） | :594-707 |
| GET | `/api/events` | :721 |
| GET | `/healthz` | :1034 |
| WS | `/ws/device/{device_id}` | :729 |
| WS | `/ws/web` | :965 |

**设备通道 · 服务端 → 设备**（`main.py:775-781` 及 `handle_device_message` 内的回帧）：

| type | 触发位置 | 关键字段 |
|---|---|---|
| `hello` | main.py:775-781 | `device_id` / `token` / `server_time` / `offline_after_seconds` |
| `message` | main.py:291-316（`_device_payload`） | `message_id` / `sender_name` / `content` / `message_type` / `created_at` / `status="sent"` / `auto_close_seconds` / `history[]` / `redelivered?` |
| `heartbeat_ack` | main.py:836 | `server_time` |
| `screenshot_request` | hub.py:199-201 | `request_id` |
| `history_response` | main.py:898-905 | `request_id` / `device_id` / `messages[]` |
| `reply_ack` | main.py:865-870（空）/ :882-888（正常） | `client_id` / `message_id` / `status`（`ok`\|`empty`）/ `created_at` |
| `shutdown` | main.py:503 | `delay_seconds`（写死 5） |
| `unlock_request` | main.py:572 | 远程解锁 Phase 1 |
| `unlock_result_ack` | main.py:921-924 | `request_id` / `status="rejected"` / `reason="not_mine"` |

**设备通道 · 设备 → 服务端**（`handle_device_message`，main.py:825-959）：

| type | 行 | 处理 |
|---|---|---|
| `heartbeat` | :828-836 | `touch()`；`_apply_reported_state()` 变了才广播；回 `heartbeat_ack` |
| `ack` | :838-852 | `msg_svc.advance()`；**广播被注释掉**（:847-852，群聊模型下不对外暴露逐设备状态） |
| `screenshot_response`（别名 `screenshot`） | :854-857 | `HUB.resolve(request_id, ...)` 配对 Future |
| `reply` | :859-893 | 落库 + `reply_ack` + 广播给 Web + 广播给其他设备（排除发起者） |
| `history_request` | :895-905 | 回 `history_response`（`limit` 上限 200） |
| `unlock_result` | :907-944 | 一次性令牌校验（`used_at`）+ 审计 + 广播给 Web |
| `event` | :946-947 | 只落 `db.log_event` |
| `device_info` | :949-959 | 更新 platform/name + `_apply_reported_state`（**当前 C# 客户端没有发送点**，见 §2.7-⑧） |

**Web 通道 `/ws/web` 广播**（`main.py` 中所有 `broadcast_web`）：

`device_status`（:782、:821、:833、:957）、`message`（:372、:890）、`device_updated`（:230）、
`device_deleted`（:237）、`wake`（:487）、`shutdown_sent`（:508）、`unlock_pending`（:586）、
`unlock_result`（:933、:942）、`xiaomi`（:717）；握手 `ready`（:976），心跳 `pong`（:980）。
`message_status`（:850）**已被注释掉**。

### 2.6 设备注册 / 心跳里的 capabilities 现状

**服务端已经有 capabilities 字段**（需求 §9 的判断成立），链路完整：

| 环节 | 位置 | 实际行为 |
|---|---|---|
| 表结构 | `db.py:146-151`（迁移，非 `SCHEMA`） | `devices.windows_state TEXT NOT NULL DEFAULT 'unknown'`、`devices.capabilities TEXT NOT NULL DEFAULT ''`（**JSON 数组的字符串**） |
| 写入 | `services/devices.py:56-92` `set_session_state()` | `capabilities` 存 `json.dumps([str(c)[:32] for c in capabilities][:16])`；**值没变返回 False**（避免心跳刷屏广播） |
| 读出口 | `main.py:241-259` `_public()` | `capabilities` 字符串 → `json.loads` → **数组**给前端；非法/空 → `[]`；同时补 `online`、`windows_state`（缺省 `"unknown"`） |
| 接收（两种来源） | `main.py:262-288` `_apply_reported_state()` | ① WS 连接串查询参数 `windows_state` / `capabilities`（逗号分隔）；② `heartbeat` / `device_info` 帧里的字段 |
| 连接串解析 | `main.py:740-741`、`772-773` | `q.get("windows_state")`、`q.get("capabilities")` 取到后立刻写库并广播 |
| 心跳帧 | `AgentClient.cs:397-403` `BuildHeartbeatJson()` | `{type:"heartbeat", windows_state: SessionState.Current, capabilities: BuildCapabilities()}` |

**实际取值**（客户端唯一权威定义，`AgentClient.cs:376-388` `BuildCapabilities()`）：

- 恒有：`"message"`、`"screenshot"`；
- 非 headless 追加：`"shutdown"`（`App.IsHeadless` 为真时**没有**这个能力）；
- `"unlock"`：`ReportUnlockCapability = false`（`:370`）**故意不上报** —— 注释写明「凭据存储与 Credential Provider 都还没做，报了就是谎报」。

服务端只做**字符串白名单裁剪**（长度 32、最多 16 个），**不做取值校验**（`devices.py:76-79`），
所以将来 Android 报什么能力名都不会被拦 —— 但也没有任何地方声明过合法能力名的列表（见 §9.6）。

**会话状态取值**（服务端校验，`devices.py:73`）：`unknown | logon_screen | locked | unlocked`。

**能力上报的时延现状（一个真缺口）**：`AgentClient.BuildUri()`（`:169-196`）带的查询参数是
`token / name / type / platform / agent_version / enroll_token`（`:188-193`），
**没有** `windows_state` 和 `capabilities`。服务端为「一连上就报」准备的那条路径（`main.py:740-741`）
**当前无人使用**；能力/状态要等连接后 `App.OnConnectionChanged` 立刻调一次 `ReportSessionState()`
（`App.xaml.cs:735-738` → `AgentClient.cs:412-419`，走心跳帧）才到，理论上 ≤1 个 RTT，但
「查询串」这条更快的路是空转的。

### 2.7 现状与需求/文档**对不上**的地方（逐条，含依据）

① **任务书说的 `_ws = null` bug：HEAD 有，工作区已修**
`git show HEAD:pc-agent/FamilyAgent/AgentClient.cs` 第 149 行是**无条件** `_ws = null`（旧连接退出会抹掉新连接引用）；
但当前**工作区未提交改动**已把它改成 epoch 代次 + `TryRetire()` 身份比对（工作区 `AgentClient.cs:38-39`、`:129-163`、`:250-251`，
`git diff` 显示该文件 +257 行）。→ 后续阶段必须**明确以工作区为基线**，并留意另一条并行任务正在改这个文件。

② **PROTOCOL.md §7 列的 `message_status` 已不存在**：文档 `docs/PROTOCOL.md:183` 仍写「某条消息的 ACK 状态推进了」，
服务端广播代码已注释（`main.py:847-852`）。

③ **PROTOCOL.md §4 的 `ack.status` 集合不完整**：文档 `:121` 写 `status ∈ {device_received, popup_displayed, read}`，
客户端实际还会发 `"delivered"`（`App.xaml.cs:767`，headless 分支）。

④ **`"delivered"` 不是服务端的合法状态**：`messages.py:24` 的 `STATES` 为
`created/server_received/device_received/popup_displayed/read`；`advance()` 用 `RANK.get(status, -1) <= RANK.get(当前, -1)`
（`messages.py:65`）→ `"delivered"` 的 rank 是 **-1**，直接返回、**什么都不记**。
（影响有限：`device_received` 由服务端投递成功时自己记，`main.py:338`、`:790`，但语义上这是一条死值。）

⑤ **需求把「WebUI 当运行时核心」当成现状**，但 CODE 里**PC 端早就不是了**：
`docs/PC-LOCAL-UI.md:10-21` 记录了 v0.13.0 的架构更正，`WebHostWindow.xaml.cs:411-492` 只加载
`https://familyagent.local/app.html`（exe 自带）。**真正还在耦合的是「PC 宿主内部的 Core/UI 分层」**
（见 §2.2、§2.3）。所以本方案的重心是 **C# 宿主拆分**，不是「把 WebUI 从核心位置挪走」。

⑥ **PROTOCOL.md 漏掉了一批已实现的帧**：`unlock_request` / `unlock_result` / `unlock_result_ack` /
`unlock_pending` / `shutdown_sent` / `device_info` / `event` / `xiaomi`，
以及心跳帧里的 `windows_state` + `capabilities`（`AgentClient.cs:397-403`）。文档 §3/§4/§5/§7 都没有。

⑦ **需求 §7 里的 `command` / `command_ack` 一对帧在现状里不存在**：现在的做法是
「一种命令一个帧类型」（`shutdown`、`unlock_request`、`screenshot_request`）。
要引入统一 `command` 信封就等于改服务端协议语义 → 违反本轮不做的事（§1.3），
本方案的落地方式是**协议文档化 + 映射表**（§5），把现有帧对齐到需求清单里的名字。

⑧ **`device_info` 帧服务端支持、客户端没有发送点**：`main.py:949-959` 处理它，但
`grep -rn device_info pc-agent/ web/` 无命中。属「服务端预留、客户端未实现」。

⑨ **需求 §5 的「local persistence」现状为零**：PC 端唯一的持久化是 `config.json`（`Config.cs`）
和日志（`AgentLog.cs`）。消息只有内存队列（`WebHostWindow.xaml.cs:95/98`）。

⑩ **需求 §4 的 `connection_id`(GUID) 现状是两个「只用于日志」的短号**：
客户端每次连接生成 8 位 hex（`AgentClient.cs:214` `Guid.NewGuid().ToString("N")[..8]`），
服务端给每条连接发 `c0007` 之类的短号（`hub.py:41-63`、`conn_id()/conn_count()`）。
**协议里没有任何 `connection_id` 字段**，两侧的号也互不可见（只在各自的日志/`print` 里，
`main.py:768-770`、`hub.py:85-113`）。→ 直接写进协议的代价见 §9.5。

⑪ **`popup_displayed` 的触发依赖 WebView2 页面**：`App.xaml.cs:302-305` 由 `host.MessageAcked` 驱动，
后者来自页面 `web.ack`（`JsBridge.cs:316`、`WebHostWindow.xaml.cs:357-361`、`pc.js:183`、`shell.js:536`）。
需求 §3「WebView2 不得成为接收消息的必要条件」**当前不成立**。

⑫ **`docs/DESIGN.md:75-84` 的 PC Agent 职责描述已过期**：那里写「收到消息 → 立即全屏无边框 TopMost 窗口」，
现状是「WebView2 壳 + 本地页 + 桥」（`PC-LOCAL-UI.md`）。旧文档 §191 的五态状态机对外也已收成单一 `sent`
（`messages.py:27-31`、`:135-146`）。

---

## 3. 目标分层（具体到文件/类名）

### 3.1 分层规则（硬性，作为验收判据）

```
FamilyAgent.Core（net9.0，无 -windows TFM，可 Linux 编译/测试）
  允许依赖：System.Net.WebSockets / System.Text.Json / 本地文件或 SQLite / 注入进来的平台接口
  禁止出现：WebView2、System.Windows.*、System.Windows.Forms、Dispatcher、HTML、JavaScript、DOM
  职责：连接管理 / 认证 / 协议解析 / 消息收发与队列 / ACK / History / Sync / 设备状态 / 能力上报 / 命令派发

FamilyAgent（net9.0-windows，WPF 宿主 = Windows 客户端）
  职责：窗口、托盘、WebView2 宿主、桥、平台能力实现（截图/电源/会话状态）、开机自启、单实例

web/shell/*（HTML/CSS/JS）
  职责：渲染。只认 host.* 帧，不含协议逻辑（现状已满足，pc.js:547-558）
```

### 3.2 目标目录结构

```
pc-agent/
├─ FamilyAgent.Core/                     ← 新项目（net9.0）
│  ├─ FamilyAgent.Core.csproj
│  ├─ Transport/
│  │   ├─ ConnectionManager.cs           ← 原 AgentClient 的连接部分（唯一连接循环 + 代次 + 状态机）
│  │   ├─ ConnectionState.cs             ← enum Disconnected/Connecting/Connected/Closing
│  │   ├─ WebSocketTransport.cs          ← 原 SendRawAsync(573-602) + ReceiveLoopAsync(440-483) 的分帧
│  │   ├─ HeartbeatLoop.cs               ← 原 HeartbeatLoopAsync(304-357)（往返看门狗 + 积压看门狗）
│  │   ├─ BackoffPolicy.cs               ← 原 delaySeconds 计算(284)
│  │   └─ Outbox.cs                      ← 原 _outbox 全套(610-696)
│  ├─ Protocol/
│  │   ├─ Frames/                        ← 强类型 DTO：HelloFrame / MessageFrame / ReplyAckFrame /
│  │   │                                    HeartbeatAckFrame / HistoryFrame / ScreenshotRequestFrame /
│  │   │                                    ShutdownCommandFrame / UnlockRequestFrame
│  │   ├─ FrameCodec.cs                  ← 原 Dispatch(485-571) 的 type → DTO 映射 + 上行帧构造
│  │   └─ ProtocolVersion.cs             ← 取代硬编码的 AgentVersion(AgentClient.cs:23)
│  ├─ Messaging/
│  │   ├─ MessageManager.cs              ← 生命周期编排（收 → 持久化 → ACK → 通知平台层）
│  │   ├─ DeliveryState.cs               ← enum { Received, Persisted, AckSent, Displayed, Read }
│  │   ├─ MessageStore.cs                ← 本地持久化（接口 + 默认文件/SQLite 实现）
│  │   └─ PendingAckQueue.cs             ← 「已收未 ACK」的补发（替代 UI 层的 _pending/_queued 语义）
│  ├─ Sync/
│  │   └─ SyncService.cs                 ← Connect → Authenticate(hello) → Sync/History → Live
│  ├─ Devices/
│  │   ├─ DeviceCapabilities.cs          ← 能力名常量 + 组合（取代 BuildCapabilities 的散装字符串）
│  │   ├─ CapabilityReporter.cs          ← 连接后立即上报 + 变化时上报（走心跳帧与 device_info 帧）
│  │   └─ SessionStateSnapshot.cs        ← Windows 会话状态的值对象（字符串 + 校验，对齐 devices.py:73）
│  ├─ Commands/
│  │   ├─ CommandRouter.cs               ← 服务端命令 → Core 事件（shutdown/unlock/screenshot）
│  │   └─ Abstractions/
│  │       ├─ IScreenshotProvider.cs     ← 平台实现（Windows: ScreenCapture）
│  │       ├─ IPowerProvider.cs          ← 平台实现（Windows: PowerControl）
│  │       ├─ IUnlockGuard.cs            ← 现状 UnlockGuard 的协议校验（见 §9.12 归属待拍板）
│  │       └─ IPlatformInfo.cs           ← IsHeadless / Platform 字符串 / 能力探测（**切掉 App.IsHeadless 与 SessionState.Current 这两个静态依赖**）
│  ├─ Events/
│  │   └─ CoreEvents.cs                  ← MessageReceived / MessageDeliveryStateChanged /
│  │                                        ConnectionStateChanged / CommandReceived / HistoryReceived
│  └─ Config/
│      └─ AgentConfig.cs                 ← 原 Config.cs（去 UI 相关字段访问；主题只存值不做渲染）
│
├─ FamilyAgent/                          ← 现有 WPF 宿主（Windows 客户端）
│  ├─ App.xaml / App.xaml.cs             ← 只保留：启动编排、托盘、单实例、Presence、AutoStart
│  ├─ ShellWindow.xaml(.cs)              ← 现 WebHostWindow 改名（可后置），仍负责 WebView2 与窗口形态
│  ├─ JsBridge.cs                        ← 保留（UI 适配器：CoreEvents → host.* 帧）
│  └─ Platform/
│      ├─ WindowsPlatformInfo.cs         ← 实现 IPlatformInfo（包装 SessionState / App.IsHeadless）
│      ├─ WindowsScreenshotProvider.cs   ← 现 ScreenCapture.cs 移到此处（+ capability=screenshot）
│      ├─ WindowsPowerProvider.cs        ← 现 PowerControl.cs（+ capability=shutdown）
│      ├─ WindowsUnlockGuard.cs          ← 现 UnlockGuard.cs（+ capability=unlock，暂仍 false）
│      ├─ MessageDisplaySink.cs          ← 把 Core 的 MessageReceived 接到窗口 + 回 NotifyDisplayed
│      └─ FallbackNotifier.cs            ← WebView2 不可用时的原生通知（托盘气泡，见 §9.11）
│
└─ FamilyAgent.Core.Tests/               ← 新项目（net9.0），CI 增加 ubuntu-latest job
   ├─ ConnectionEpochTests.cs            ← 旧连接 dispose 不污染新连接状态
   ├─ OutboxTests.cs                     ← 断线积压 → 重连补发
   ├─ MessageLifecycleTests.cs           ← 无 UI 订阅者时仍持久化 + 发 ACK
   └─ SyncServiceTests.cs                ← history 合并去重
```

### 3.3 Core 对外事件契约（对应需求 §2）

| Core 事件 | 取代现状 | 载荷（强类型，不再是裸 `JsonElement`） |
|---|---|---|
| `ConnectionStateChanged` | `AgentClient.ConnectionChanged(bool,string)`（:54） | `(ConnectionState, connectionId, detail)` |
| `MessageReceived` | `AgentClient.MessageReceived(JsonElement)`（:55） | `MessageFrame`（含 `message_id` / `sender_name` / `content` / `auto_close_seconds` / `history` / `redelivered`） |
| `MessageDeliveryStateChanged` | `AgentClient.ReplyAcked(JsonElement)`（:57） + 本地状态机 | `(messageId, DeliveryState, detail)`，同时覆盖「本机回复的 reply_ack」与「收到的消息的显示/已读」 |
| `CommandReceived` | `ScreenshotRequested`(:56) / `ShutdownRequested`(:59) / `UnlockRequested`(:63) | `(CommandKind, payload, respond)`，`respond` 是 Core 提供的回执回调 |
| `HistoryReceived` | `AgentClient.HistoryReceived(JsonElement)`（:58） | `IReadOnlyList<HistoryEntry>` |

**Core 不知道消息最后显示在哪**：`MessageManager` 只把 `MessageFrame` 交给订阅者；
「有没有画到屏幕上」由平台层回调 `MessageManager.NotifyDisplayed(messageId)` / `NotifyDismissed(messageId)`
（取代 `WebHostWindow.MessageAcked`/`Dismissed` → `App.xaml.cs:302-311`）。

---

## 4. 迁移映射表

| 现状位置（文件:行/成员） | 迁移去向 | 处理方式 |
|---|---|---|
| `AgentClient.cs:87-163` `Start/Stop/Restart/IsCurrent/TryPublish/TryRetire` + `_lifeGate/_epoch` | `Core/Transport/ConnectionManager.cs` | **搬**（保留 epoch 语义，`connection_id` 改为完整 GUID，对外暴露） |
| `AgentClient.cs:205-297` `LoopAsync` | `Core/Transport/ConnectionManager.cs` | **搬 + 拆**（连接编排留在 ConnectionManager，退避抽到 `BackoffPolicy`） |
| `AgentClient.cs:573-602` `SendRawAsync`（发送锁 + 10s 超时） | `Core/Transport/WebSocketTransport.cs` | **搬** |
| `AgentClient.cs:440-483` `ReceiveLoopAsync`（分帧 + 关闭码） | `Core/Transport/WebSocketTransport.cs` | **搬** |
| `AgentClient.cs:485-571` `Dispatch` | `Core/Protocol/FrameCodec.cs` | **改**（`type` → 强类型 DTO，不再抛裸 `JsonElement`） |
| `AgentClient.cs:304-357` `HeartbeatLoopAsync` | `Core/Transport/HeartbeatLoop.cs` | **搬**（看门狗保留；`_lastAckUtc/_lastBeatUtc/_outboxSinceUtc/_sendFailStreak` 收进实例字段，见 §9.7） |
| `AgentClient.cs:610-696` `_outbox/SendOrQueue/EnqueueOutbox/FlushOutbox/SendNowAsync` | `Core/Transport/Outbox.cs` | **搬** |
| `AgentClient.cs:698-699`（旧 `Ack`，群聊下不承载状态） | `Core/Messaging/PendingAckQueue.cs` | **改**（见 §9.13） |
| `AgentClient.cs:703-719` `Reply` / `RequestHistory` | `Core/Messaging/MessageManager.cs` + `Core/Sync/SyncService.cs` | **搬 + 归位** |
| `AgentClient.cs:722-755` `SendScreenshotAsync` | `Core/Commands/CommandRouter.cs`（发帧）+ `WindowsScreenshotProvider`（取图） | **拆** |
| `AgentClient.cs:376-388` `BuildCapabilities` | `Core/Devices/DeviceCapabilities.cs` + `IPlatformInfo` | **改**（去掉 `App.IsHeadless` 静态依赖） |
| `AgentClient.cs:397-403` `BuildHeartbeatJson` | `Core/Transport/HeartbeatLoop.cs` + `Core/Devices/SessionStateSnapshot.cs` | **改**（去掉 `SessionState.Current` 静态依赖，改为注入） |
| `AgentClient.cs:169-196` `BuildUri` | `Core/Transport/ConnectionManager.cs` | **搬 + 补**（补上 `windows_state`/`capabilities`/`connection_id` 查询参数，与服务端 `main.py:740-741` 对齐；`connection_id` 是否上协议见 §9.5） |
| `App.xaml.cs:137-144`（事件订阅） | `Core/Events/CoreEvents.cs` 订阅 | **改**（订阅变成 Core 事件；`Client.Start()` 调用点 :156 变为 `Core.Start()`） |
| `App.xaml.cs:749-780` `OnMessageReceived` | `Platform/MessageDisplaySink.cs` | **搬**（保留 `Dispatcher.Invoke`，但**去掉 ack 与 headless 判断**） |
| `App.xaml.cs:762-768` headless 分支 + `Ack("delivered")` | `Platform/MessageDisplaySink.cs` + `Core/Messaging/MessageManager.cs` | **改**（headless 由 `IPlatformInfo` 决定显示策略，ACK 由 Core 判断状态） |
| `App.xaml.cs:302-311` `MessageAcked`/`Dismissed` → `Ack("popup_displayed"/"read")` | `MessageDisplaySink` → `MessageManager.NotifyDisplayed/NotifyDismissed` | **改**（ACK 决策从 UI 移到 Core） |
| `App.xaml.cs:782-806` `OnReplyAcked` | `Core/Messaging/MessageManager.cs`（发 `MessageDeliveryStateChanged`）+ `JsBridge`（转 `host.reply_ack`） | **拆** |
| `App.xaml.cs:808-817` `OnHistoryReceived` | `Core/Sync/SyncService.cs` → `HistoryReceived` → `JsBridge.PostHistory` | **拆** |
| `App.xaml.cs:822-845` `OnScreenshotRequested` | `Core/Commands/CommandRouter.cs` → `CommandReceived` → `WindowsScreenshotProvider` | **拆** |
| `App.xaml.cs:851-899` `OnShutdownRequested`/`ExecuteShutdown` | `WindowsPowerProvider.cs`（执行）+ `Core/Commands/CommandRouter.cs`（回 `event` 帧） | **拆** |
| `App.xaml.cs:911-922` `OnUnlockRequested` | `WindowsUnlockGuard.cs` 或 `Core/Commands`（§9.12 待拍板） | **搬** |
| `App.xaml.cs:929-944` `OnSessionStateChanged` | `WindowsPlatformInfo.cs` → `CapabilityReporter` | **拆** |
| `App.xaml.cs:1011-1033` `ShowConversation` 里的 `Client.RequestHistory(50)`（:1032） | `Core/Sync/SyncService.cs`（打开界面时请求补齐，UI 只发「我要显示」意图） | **改** |
| `App.xaml.cs:291-333` `CreateHost`（桥事件接线） | 保留在宿主，删掉 `MessageAcked`/`Dismissed` 两段 | **改** |
| `App.xaml.cs:984-1010` `SetupTray`、`:694-718` 设置、`:206-273` 单实例/版本告知、`:154-155` AutoStart、`:164-169` Presence | **不动**（纯 Windows 客户端职责） | — |
| `WebHostWindow.xaml.cs` 全文 | 改名为 `ShellWindow`/`WebHostView`（可选），**保留在 Windows 层** | **不动语义**（除下方两处） |
| `WebHostWindow.xaml.cs:95/98/229-238/743-766` `_pending`/`_queued`/`PushMessage`/`FlushQueued`/`RaiseDismissed` | 队列语义上移到 `Core/Messaging/{MessageStore,PendingAckQueue}`；宿主只保留「投递给页面」 | **改**（这是「WebView2 不是收消息必要条件」的关键改动） |
| `WebHostWindow.xaml.cs:336-361` `OnPageReady`/`OnPageAck` | 保留（页面就绪判定是 UI 事实），但 `OnPageAck` 改为调用 `MessageManager.NotifyDisplayed` | **改** |
| `WebHostWindow.xaml.cs:539-560` `ShowRuntimeHint`/`ShellDir`、`:566-580` `NavigationCompleted`、`:411-476` `InitWebViewAsync` | **不动**（WebView2 特异性，必须留在平台层） | — |
| `JsBridge.cs` 全文 | **不动结构**（只把 `case "message"` 的数据源从裸 `JsonElement` 换成 `MessageFrame`） | **微改** |
| `ScreenCapture.cs` | `Platform/WindowsScreenshotProvider.cs` | **搬 + 包接口** |
| `PowerControl.cs` | `Platform/WindowsPowerProvider.cs` | **搬 + 包接口** |
| `UnlockGuard.cs` | `Platform/WindowsUnlockGuard.cs` 或 `Core/Commands/UnlockGuard.cs` | **搬**（归属待拍板 §9.12） |
| `SessionState.cs` | `Platform/WindowsPlatformInfo.cs`（保留 `Detect()` 与事件，去掉对 `App.Dispatcher` 的硬依赖或注入同步器） | **搬** |
| `Config.cs` | `Core/Config/AgentConfig.cs` | **搬**（`ThemeId`/`ThemeMode` 只存值） |
| `AgentLog.cs` | `Core/Diagnostics/AgentLog.cs` | **搬** |
| `HistoryItem.cs` | `Core/Protocol/HistoryEntry.cs` | **搬 + 改名** |
| `Presence.cs` / `SingleInstance.cs` / `AutoStart.cs` | **不动**（Windows 进程/会话/计划任务职责） | — |
| `web/shell/*`、`web/static/chat.js`、`web/static/tokens.css` | **不动**（UI 实现） | — |
| `web/static/shell.js`（916 行，废弃） | 候选删除（Phase 5，需拍板） | **待确认** |
| `server/` 全部 | **不动** | — |
| `tools/cli_agent.py`（330 行） | **不动**，继续充当协议基准实现（`PROTOCOL.md:244`） | — |

---

## 5. 协议文档化（需求 §7 的落地方式）

**现状**：`docs/PROTOCOL.md`（252 行）**已经存在且质量不错**，但落后于代码（§2.7-②③④⑥）。
本轮不重写，做两件事：**补齐 + 冻结**。

### 5.1 需求清单 → 现状帧对照

| 需求要求的帧 | 现状对应 | 差距 |
|---|---|---|
| `hello` | ✅ 服务端→设备，`main.py:775-781` | 文档已有（PROTOCOL.md:35-45）；建议补 `connection_id`（§9.5） |
| `heartbeat` | ✅ 设备→服务端，`AgentClient.cs:397-403` / `main.py:828-836` | 文档缺 `windows_state` / `capabilities` 两字段 |
| `heartbeat_ack` | ✅ 服务端→设备，`main.py:836` | 文档有 |
| `message` | ✅ 服务端→设备，`main.py:291-316` | 文档有（PROTOCOL.md:76-102）；补 `status="sent"`（群聊单态） |
| `reply` | ✅ 设备→服务端，`main.py:859-893` | 文档有 |
| `reply_ack` | ✅ 服务端→设备，`main.py:865-888` | 文档有；补 `status` 全集（含 `empty`） |
| `ack` | ✅ 设备→服务端，`main.py:838-852` | 文档有；**修正 Status 集合**、说明「服务端不再对外广播逐设备状态」（:847-852 注释） |
| `history_request` | ✅ 设备→服务端，`main.py:895-905` | 文档有 |
| `history_response` | ✅ 服务端→设备，`main.py:898-905` | 文档有 |
| `screenshot_request` | ✅ 服务端→设备，`hub.py:199-201` | 文档有 |
| `screenshot_response` | ✅ 设备→服务端，`main.py:854-857` / `AgentClient.cs:722-755` | 文档有；补「能力不支持时回 `error` 而非静默」（Android/无桌面场景） |
| `command` / `command_ack` | ❌ **不存在**。现状是 `shutdown` / `unlock_request` / `unlock_result` / `unlock_result_ack` 分类型各管各的 | **本轮不改协议**；在文档里给出「命令族映射表」并记为开放项（§9.9） |

### 5.2 建议的补章（Phase 5 交付物）

1. §3/§4 补：`unlock_request`、`unlock_result`、`unlock_result_ack`、`device_info`、`event`、`shutdown_sent`、`unlock_pending`。
2. §3 心跳补字段表：`windows_state ∈ {unknown, logon_screen, locked, unlocked}`、`capabilities: string[]`、
   服务端校验与裁剪规则（`devices.py:73-79`、`main.py:262-288`）。
3. §7 删除 `message_status`，标注「群聊模型下逐设备状态只入库不广播（`main.py:843-852`）」。
4. 新增 §「能力（capabilities）清单」：`message` / `screenshot` / `shutdown` / `unlock`，
   并要求**不支持的平台显式上报 `unsupported` 或干脆不报该能力**（需求 §8 的语义）。
5. 新增 §「客户端实现检查表」（给 Android 用）：单连接、状态机四态、`connection_id`、Outbox、
   ACK 与 UI 解耦、重连后 Sync 补齐、能力上报。

---

## 6. 分阶段方案（每阶段可单独交付、可单独回退）

> 阶段顺序铁律：**Phase 1 必须是「把连接与消息逻辑从 UI 里抽干净」**，不是重写 UI。
> 每阶段结束都要能：① 出一版可发给用户的 exe；② `git revert` 单次提交即回到前一状态。

### Phase 1 — 抽出 `FamilyAgent.Core`：连接、传输、协议解析离开 UI

**改什么**
1. 新建 `pc-agent/FamilyAgent.Core/FamilyAgent.Core.csproj`（**`net9.0`，不带 `-windows`**）+ `FamilyAgent.Core.Tests`。
2. 把 `AgentClient.cs` 整体搬进 Core（`Transport/ConnectionManager.cs` + `Transport/WebSocketTransport.cs` + `Transport/Outbox.cs` + `Transport/HeartbeatLoop.cs` + `Protocol/FrameCodec.cs`），
   **保留 epoch 代次与 `TryRetire` 语义**（工作区 `:38-39/129-163/250-251`），`connection_id` 改为完整 GUID 并对内暴露。
3. 切断两个 UI 静态依赖：`App.IsHeadless`（`:381`）与 `SessionState.Current`（`:401`）→ 注入 `IPlatformInfo`；
   Windows 侧 `Platform/WindowsPlatformInfo.cs` 实现它（包装现有 `SessionState` + `App.IsHeadless`，行为不变）。
4. `FrameCodec` 输出**强类型 DTO**，`CoreEvents` 取代裸 `JsonElement` 事件；宿主侧订阅点（`App.xaml.cs:137-144`）改为订阅 Core 事件，回调体内逻辑**逐字保留**（含 `Dispatcher.Invoke`）。
5. CI（`.github/workflows/build-windows-agent.yml`）新增 `ubuntu-latest` job：`dotnet test pc-agent/FamilyAgent.Core.Tests`。

**验收标准**
- 行为等价：连接/断线重连/心跳/收消息/回复/截图/关机/解锁与重构前一致（回归清单：`tools/probe_robustness.py`、`tools/test_bidirectional.py`、`tools/diag_realtime.py`、`tools/test_screenshot.py`、`tools/test_unlock.py`）。
- 依赖判据（可机械检查）：
  判据分两层（第一版写法把注释和字符串也算进去了，会误报 —— 已收严）：

  ```bash
  # 第 1 层（决定性）：Core 的 using 与 csproj 引用里不许出现 UI 类型
  grep -rhn '^using' pc-agent/FamilyAgent.Core/ --include='*.cs' | sed 's/.*using //' | sort -u
  #   → 只允许 System.* 与 FamilyAgent.Core.*
  grep -nE 'PackageReference|UseWPF|UseWindowsForms' pc-agent/FamilyAgent.Core/FamilyAgent.Core.csproj
  #   → 输出为空（Core 连一个 PackageReference 都不该有）

  # 第 2 层（补充）：代码行里不许出现 UI 类型 —— 但要**排除注释行**
  grep -rnE "WebView2|System\.Windows|Dispatcher|System\.Windows\.Forms" \
       pc-agent/FamilyAgent.Core/ --include='*.cs' | grep -v '/bin/\|/obj/' | grep -vE ':\s*///?'
  #   → 输出为空
  #   （注释和日志文案里出现这些词是允许的，甚至是有价值的 —— 比如
  #     「WebView2 不可用时回落到原生通知」这种说明。判的是依赖，不是字面量。）
  ```
- 单测（在 Linux CI 上跑）：`ConnectionEpochTests`（旧连接 dispose 在新连接 publish 之后调用时，`IsConnected` 仍为 true、不会触发假的 `ConnectionStateChanged(false)`）。
- 编译判据：`dotnet build pc-agent/FamilyAgent.Core/FamilyAgent.Core.csproj` 可在 `ubuntu-latest` 通过。

**风险**：文件/命名空间移动会让 `tools/check_agent_refs.py` 与 XAML `x:Static` 引用失效（CI 已内置该检查，属于可控）；`net9.0` 库被 `net9.0-windows` 项目引用需确认 `<ProjectReference>` 与 `TreatWarningsAsErrors` 设置；**本机（NAS）没有 dotnet，本地无法编译验证，必须依赖 CI 往返**。

**依赖**：无（但见 §9.1 关于与实时消息 bug 的先后顺序）。
**回退**：单次提交 revert（Core 新增、宿主改引用，均为纯移动，不改行为）。

---

### Phase 2 — MessageManager：消息生命周期与 ACK 彻底离开 UI

**改什么**
1. `Core/Messaging/MessageManager.cs` + `MessageStore.cs` + `PendingAckQueue.cs` + `DeliveryState.cs`：
   收到消息 → 归一 `message_id` → **本地持久化** → 发出 `MessageDeliveryStateChanged(Received/Persisted)` → 触发 `MessageReceived` 交给平台层 → 收到 `NotifyDisplayed` 后记 `Displayed` 并发 `ack:popup_displayed`；收到 `NotifyDismissed` 记 `Read` 并发 `ack:read`。
2. 把「ACK 决策」从 `App.xaml.cs:302-311`（`MessageAcked`/`Dismissed` 直接发 ack）移进 `MessageManager`。
3. UI 层的消息缓存（`WebHostWindow.xaml.cs:98` `_queued`、`:95` `_pending`）**降级为纯投递缓冲**：
   页面未就绪时消息已在 Core 落盘；页面就绪后由 Core 重放（`MessageStore` 里 status < `Displayed` 的消息）。
4. `App.xaml.cs:762-768` headless 分支改为：由 `IPlatformInfo.IsHeadless` 决定显示策略，
   ACK 状态由 `MessageManager` 决定（**不再发非法的 `"delivered"`**，见 §9.4）。
5. WebView2 不可用（`WebHostWindow` 处于 `Stage.Hint`，`:539-560`）时，由 `Platform/FallbackNotifier.cs`
   用原生通知提醒（托盘 `NotifyIcon.ShowBalloonTip` 已有现成用法：`App.xaml.cs:869-871`），并如实回调 `NotifyDisplayed` 或记 `Displayed=false`。

**验收标准（对应需求 §3 的可测判据）**
- **测试 A（WebView2 失效仍可工作）**：在一台机器上把 `<exe目录>\shell\` 改名（或卸载 WebView2 Runtime）→ 从网页发一条消息 →
  `agent.log` 必须出现：`[WS] RX … type=message message_id=N`、**消息落盘日志**、**`→ ack:popup_displayed`（或 headless 等价状态）**；
  且系统通知必须出现。当前实现在此场景下 `PushMessage` 只会写「页面未就绪，缓存消息」（`WebHostWindow.xaml.cs:234`）而永不 ACK → 本阶段通过即为达标。
- **测试 B（没有 UI 订阅者也不丢）**：单测注入一个抛异常的订阅者，断言 `MessageStore` 已写入且 ack 仍发出。
- **测试 C（重放）**：进程重启后，此前未 `Displayed` 的消息在页面就绪时被重新投递且不重复（以 `message_id` 去重）。
- **回归**：正常路径的用户可见行为不变（同样的气泡、同样的自动关闭 —— `auto_close_seconds` 仍生效，`WebHostWindow.xaml.cs:785-807`）。

**风险**：引入本地存储带来迁移/清理策略（建议最小实现：单文件 SQLite 或 JSONL + 按条数/天数裁剪）；
服务端 `message_id` 是 INTEGER 自增（`db.py:39-54`、`messages.py:37-54`），本地表以它为主键去重即可，但**不要假设它全局单调**（换库/重置会重号，见 §9.10）。
**依赖**：Phase 1。
**回退**：单次提交 revert；`MessageStore` 是新增，不删数据即可回滚到内存队列语义。

---

### Phase 3 — 平台能力抽象 + capabilities 上报打通

**改什么**
1. `Core/Devices/DeviceCapabilities.cs`（能力名常量与组合）+ `CapabilityReporter.cs`；
   `IPlatformInfo` 提供能力来源（取代 `AgentClient.cs:376-388` 里对 `App.IsHeadless` 的直读）。
2. 平台能力接口：`IScreenshotProvider`（Windows 实现 = 现 `ScreenCapture.cs`）、`IPowerProvider`（现 `PowerControl.cs`）；
   Android 未来各自实现；**不支持就回 `capability = unsupported`**（需求 §8）。
3. `screenshot_request` 在 Core 内转成 `CommandReceived(ScreenshotRequest)`；平台层返回图片或错误；
   Core 负责发 `screenshot_response`（成功/`error` 两条路径照 `AgentClient.cs:722-755` 与 `PROTOCOL.md:126-140`）。
4. 上报时机修正：`BuildUri()` 补上 `windows_state` / `capabilities` 查询参数（对齐服务端 `main.py:740-741`、`:772-773`），
   使能力在握手时即到，而不是等心跳（当前 `AgentClient.cs:188-193` 未带）。

**验收标准**
- 连接建立后立即（不等 15s 心跳）`GET /api/devices` 的 `capabilities` 已是数组且含 `message`/`screenshot`（依据 `main.py:248-255`）。
- headless 实例的 `capabilities` **不含** `shutdown`、且 `windows_state` 正确反映 `logon_screen`（`devices.py:73`、`AgentClient.cs:381-382`）。
- 用 Fake `IScreenshotProvider`（返回 unsupported）替换 Windows 实现 → 网页端点「查看桌面」得到明确 error，而不是 `main.py:420-468` 的超时。
- `unlock` 仍**不**上报（`ReportUnlockCapability=false`，`AgentClient.cs:370`）—— 本阶段不得打开。

**风险**：能力名一旦上协议就是事实契约，需在 `PROTOCOL.md` 固化（否则 Android 报出拼错的能力名 → 静默失效，因为服务端不做取值校验，`devices.py:76-79`）。
**依赖**：Phase 1（与 Phase 2 无强依赖，可并行）。
**回退**：接口是新增抽象，Windows 侧回退到直接调用 `ScreenCapture`；查询串参数可单行回滚。

---

### Phase 4 — Sync / History 与离线重连补齐（消息不因断线永久丢失）

**改什么**
1. `Core/Sync/SyncService.cs`：`Connect → Authenticate(hello) → Sync/History（补齐未确认消息）→ Live`，
   用 `history_request`（`main.py:895-905`）拉最近 N 条并与本地 `MessageStore` 合并去重。
2. **补齐服务端的一个真实缺口**：设备发起的群聊消息**没有逐设备投递记账**
   （`messages.py:183-197` `create_reply` 不写 `message_targets`），而 `_broadcast_message`（`main.py:319-340`）
   只推当前在线设备 → **离线设备错过之后永久丢失**（网页端发的消息则有 `pending_for_device` 补投，`messages.py:119-128`、`main.py:787-790`）。
   本轮在**客户端侧**用「重连后 history 补齐 + 按 message_id 去重」兜住（不改服务端）。
3. 本地状态四态落地：`已发送(Persisted)` / `已投递(Sent)` / `已 ACK(AckSent)` / `已显示(Displayed)`
   （对外仍只有单一 `sent`，与 `messages.py:27-31/135-146` 的群聊模型一致）。

**验收标准**
- 断网 30 秒，期间网页发 3 条、另一台 PC 回 2 条 → 本机重连后**必须看到 5 条**，顺序正确、无重复（当前第 4、5 条会丢）。
- `agent.log` 能看到 Sync 阶段的行为（进入/补齐条数/去重条数）。
- 连续 3 次「断开—重连」循环后，本地库与 `GET /api/messages` 的条数一致（无重复插入）。
- 单测 `SyncServiceTests`：给定本地库 + `history_response` 重叠数据，断言合并结果无重复且顺序正确。

**风险**：`history_request` 只支持 `limit`（上限 200，`main.py:896-905`），**没有 `since`/`after_id`**；
长时间离线（超过 200 条）仍会漏 → 需要服务端加参数才能根治（属协议扩展，本轮不做，见 §9.2）。
**依赖**：Phase 1（建议在 Phase 2 之后做，因为要用到 `MessageStore`）。
**回退**：Sync 是新增服务；回退后行为回到「只靠服务端补投」（仅网页端消息可靠）。

---

### Phase 5 — 客户端分层收尾 + 协议文档冻结 + Android 前置核对

**改什么**
1. 文档：按 §5.2 补齐/修正 `docs/PROTOCOL.md`，并新增「能力清单」与「客户端实现检查表」；
   把 `docs/DESIGN.md:75-84`、`:191` 的过期段落标注为历史（§2.7-⑫）。
2. 清理候选：~~`web/static/shell.js`（916 行，废弃）~~、`webHostWindow` 里遗留的 `_pending` 语义、`AgentClient.cs` 旧名残留。
   - ⚠ **2026-09-29 勘察更正**：`web/static/shell.js` **不是废弃件** —— 它是**浏览器预览壳**的运行件
     （`web/index.html` 仍在 `<script src="static/shell.js?v=0.17.1">` 引用它，文件头自带
     「浏览器模式一行都不变 / 壳模式不连 /ws/web / 页面不直接调宿主能力」三条硬规矩）。
     删它必须同时改 `web/index.html`，属于 Web 侧重构 → **本轮决定：不删**，
     与「浏览器预览模式去留」一起放到下轮决策。
   - `_pending`：核实为**页面 ACK 等待集**（加：交给页面时；减：`web.ack`；清：关窗按已读回报），
     **不是遗留字段** → 已在 `WebHostWindow.xaml.cs` 就地定死语义，保留。
   - `AgentClient.cs`：v0.16 重构时已删除；Core 里 20+ 处注释引用是**迁移溯源**（带行号出处），
     批量删会丢掉「这段逻辑从哪来」的线索 → 口径统一收敛到 `pc-agent/FamilyAgent.Core/README.md`。
3. 产出「Android 契约核对表」一页：`Core` 里哪些接口 Android 必须自己实现（`IPlatformInfo` / `IScreenshotProvider` /
   `IPowerProvider` / `MessageStore` / 会话状态）、哪些直接复用协议（`PROTOCOL.md:240-252` 已有初版）。
   → 已产出：**`docs/ANDROID-CONTRACT.md`**（该页同时修正了初版里两条过期结论：昵称已不是本地概念、心跳结论保留）。

**执行记录（2026-09-29，Phase 5 落地）**

| 项 | 结果 | 证据 |
|---|---|---|
| 1 协议文档 | ✅ `docs/PROTOCOL.md` 冻结版（服务端 26 帧 + 设备上行 14 帧 + §10 能力清单 + §11 客户端检查表 + `message_status` 标预留）；`docs/DESIGN.md` 两处标历史 | `python3 tools/check_protocol_doc.py` → 退出码 0 |
| 2 清理 | ✅ shell.js **保留**（见上）；`_pending` 语义定死；`AgentClient` 口径收敛（README） | `dotnet build` 0 warning；`python3 tools/check_agent_refs.py` 通过 |
| 3 Android 契约 | ✅ `docs/ANDROID-CONTRACT.md` | 表 A 6 个接口签名逐条核对 Core 源码 |
| ★ 勘察新发现 | ⚠ **`messages.sender_nickname_id` / `sender_color` 没有写端**（列 / 索引 / 两端读端都在，写端缺失 → 恒 `NULL`）→ 已在协议里标为预留并要求客户端容忍 `null`；**补写端另立一项**（需放开 `server/`） | `grep -rn 'sender_nickname_id' server/ pc-agent/` 只命中 `db.py` 的建表/索引 |

**验收标准**
- 机械判据：`grep -rn '"type":' server/main.py server/hub.py` 的输出与 `PROTOCOL.md` 的帧表格**逐条对齐**（允许「已注释」项被显式标注为不广播）。
- 依赖判据同 Phase 1（Core 目录零 UI 引用），且 `Core` 里不出现 `Windows`/`%APPDATA%` 等平台字样（除注入的接口）。
- `tools/check_agent_refs.py` 通过；`dotnet test` 在 CI 的 ubuntu job 上全绿。

**风险**：删 `shell.js` 前要确认没有老版网页引用（`grep -rn "shell.js" web/`）；文档冻结后 Android 侧再改协议的沟通成本升高。
**依赖**：Phase 1-4。
**回退**：文档改动可单提交回滚；删除操作单独提交（便于 revert）。

---

### （Phase 6，本方案不含）Android 原生客户端骨架

只有 Phase 1-5 完成后（Core 契约与协议冻结）才开启。**本轮不写任何 Android 代码**，
也不为此引入任何跨平台框架（§1.1）。

---

## 7. 验收标准（汇总，均可测）

### A. 分层与依赖（机械判据）
1. `grep -rnE "WebView2|System\.Windows|Dispatcher|System\.Windows\.Forms" pc-agent/FamilyAgent.Core/` → 空输出。
2. `pc-agent/FamilyAgent.Core/FamilyAgent.Core.csproj` 的 `<TargetFramework>` 为 `net9.0`（**不是** `net9.0-windows`），
   且可在 `ubuntu-latest` 上 `dotnet test` 通过。
3. Core 的公开事件只有 §3.3 那五类；Core 内部不存在「把消息直接交给某个 UI」的路径
   （即 `Core` 里没有等价于 `WebHostWindow.PushMessage` 的调用点）。

### B. WebView2 不是必要条件（需求 §3）
4. 让 WebView2 **完全加载失败**（`shell\` 目录改名，或机器未装 Runtime → 走 `WebHostWindow.xaml.cs:435-436` 的 `ShowRuntimeHint`）：
   Core 仍能 **连接**（`agent.log` 有 `[WS] CONNECTED connection=<guid>`）、**收消息**（`[WS] RX type=message`）、
   **ACK**（发出 `ack` 帧，服务端 `db.message_targets` 推进）、**提醒**（原生通知出现）。
5. 该场景下消息**不丢**：恢复 WebView2 后重新打开界面，消息仍能显示（来自 `MessageStore`，不是内存队列）。

### C. 连接健壮性（需求 §4）
6. 单一 active WebSocket：任意时刻 `DescribeConnection()` 语义下只有一条连接；`Start()` 不会起第二条循环。
7. 状态机取值限定 `Disconnected/Connecting/Connected/Closing`；每次连接有唯一 `connection_id`。
8. **旧连接的 `finally`/`dispose` 不改变新连接状态**：单测模拟「新连接已 publish → 旧连接 finally 执行」，断言
   ① 新连接的引用未被清空；② 未发出假的 `ConnectionStateChanged(false)`（工作区 `TryRetire` 已实现该语义，重构不得退化）。

### D. 消息不因瞬间断线永久丢失（需求 §6）
9. 断网 ≤ 30 秒期间的网页消息 + 其他设备消息，重连后全部可见（§6 Phase 4 判据）。
10. 握手序列固定为 `Connect → Authenticate → Sync/History 补齐 → Live`，且 `agent.log` 可读出这四步。
11. 对外状态与群聊模型一致（仍只有 `sent`，`messages.py:135-146`），逐设备四态只存在于本地与服务端内部。

### E. ACK 与 UI 解耦（需求 §5）
12. 单测：UI 订阅者抛异常/完全不订阅时，消息仍入库、ack 仍发出。
13. `popup_displayed` **只在真的显示后**发出（不得提前），但**显示失败/无 UI 时必须有终态**（成功或 `displayed_timeout`），不能永远挂在「未 ACK」。
14. 客户端不再发送服务端未定义的 ack 状态（当前 `"delivered"` 必须被替换为 `device_received`/`popup_displayed` 语义，见 §9.4）。

### F. 协议一致性（需求 §7）
15. `PROTOCOL.md` 里列的每个 device→server / server→device 帧，都能在 `server/main.py`+`server/hub.py` 找到对应处理或广播点；反之亦然（差异需显式标注为「已注释/预留」）。
16. 每个帧在文档里明确 `type` / `message_id` / `device_id` / `timestamp` / `payload` 五要素（缺哪个就写「无」并给理由）。

### G. 平台能力（需求 §8/§9）
17. `screenshot_request` 在 Core 内只产生平台无关事件；Windows 用 `WindowsScreenshotProvider`，
    不支持的平台回 `screenshot_response{error}` 而非超时。
18. `capabilities` 在连接建立后立即被服务端看到（§6 Phase 3 判据），取值来自单一常量表。

---

## 8. 风险与开放问题

### 8.1 （最高优先级）正在排查的实时消息 bug 与本次重构的先后关系

- 现状：`docs/BUG-PC-REALTIME.md` 未结案，**唯一无法远程验证的一段正是**「宿主 `OnMessageReceived` → 页面」
  （该文档 §4 明说：开发机是 Linux 跑不了 WebView2；`PageReady` 闸门自相矛盾 ——
  `host.message` 走同一条 `PageReady` 闸门却不通，而 `host.history` 通）。
- **建议顺序**：**先定位根因，再动 Phase 2**；Phase 1（纯搬运 + 接口注入，不改行为）可以并行做，
  因为它不触碰桥与 ACK 路径。
- 理由：Phase 2 会把「显示」与「ACK」的决策从 `WebHostWindow` 搬到 Core，
  如果此时 bug 根因还没定位（例如根因是 `Dispatcher.Invoke` 在窗口未创建时的行为，
  或 `Bridge.PageReady` 从未被置位），重构后的现象会**更难归因**（新旧两层都会成为嫌疑）。
- 界面上可以并行做的**最小调查**（不改架构）：
  ① 在 `App.xaml.cs:749`、`WebHostWindow.xaml.cs:229/336/743`、`JsBridge.cs:450` 各加一行带毫秒时间戳与
  `PageReady`/`_stage` 快照的日志；② 用 `tools/dev_keepalive.py` 保活、`tools/diag_realtime.py` 复现；
  ③ 取用户机器 v0.13.4（含未提交改动）的 `agent.log`。
- **不得**把重构当调试手段：Phase 1 的验收就是「行为完全等价」，等价性一旦被破坏，bug 归因基础就没了。

### 8.2 服务端不支持「设备发起的群聊消息」离线补投（真实缺口）
`messages.py:183-197`（`create_reply` 不写 `message_targets`）+ `main.py:319-340`（只推在线设备）
⇒ 离线设备会永久丢失其他设备在断线期间说的话。当前只能靠客户端 `history_request` 兜（Phase 4）。
根治需要在服务端加 `since`/`after_id` 参数或补 target 记账 —— **属协议语义变更，本轮不做**，需单独决策。

### 8.3 客户端零持久化
消息只存在内存（`WebHostWindow.xaml.cs:95/98`）。任何「不丢消息」的目标都必须先有 `MessageStore`（Phase 2）。

### 8.4 `"delivered"` 不是合法服务端状态
`App.xaml.cs:767` 发的 `"delivered"` 在 `messages.py:65` 的 rank 比较中直接 no-op（rank = -1）。
**待确认**：headless 场景应报什么状态？选项：(a) 保持 `device_received`（服务端已记，客户端不发）；
(b) 引入新状态 `stored`（需改服务端 STATES，属协议变更）；(c) 发 `popup_displayed` 但带 `reason=headless`（语义谎报，不推荐）。

### 8.5 `connection_id` 是否进协议（需求 §4 要求 GUID）
现状两侧都只有日志用短号，且**互不可见**（`AgentClient.cs:214`、`hub.py:41-63`）。
**待确认**：只在客户端本地贯穿（零协议改动，推荐），还是加进 `hello` 与上行帧（便于服务端日志与客户端日志对账，但改协议）。
→ 若选后者，需服务端 `main.py:775-781` 与 `hub.py:58-63` 同步改，**超出本轮范围**。

### 8.6 能力名没有单一权威列表
服务端不校验取值（`devices.py:76-79`），客户端拼错能力名不会报错只会静默失效。
→ Phase 3 必须把 `message/screenshot/shutdown/unlock` 写进 `PROTOCOL.md`，并让两侧共用同一份常量语义。

### 8.7 心跳看门狗的跨代共享字段（工作区版本同样存在）
`_lastAckUtc` / `_lastBeatUtc` / `_outboxSinceUtc` / `_sendFailStreak`（`AgentClient.cs:49-52`）
不受 epoch 保护：`HeartbeatLoopAsync` 开头无条件写（`:306-307`）。两代循环短暂重叠时可能互相干扰看门狗判断。
→ Phase 1 搬到 `HeartbeatLoop` 实例字段后自然消解；搬之前**不要**改语义。

### 8.8 本机没有 dotnet，CI 只有 Windows job
实测 `dotnet` 不存在；CI `build-windows-agent.yml` 只跑 `windows-latest`。
→ Phase 1 必须新增 `ubuntu-latest` job 跑 `FamilyAgent.Core.Tests`，否则「Core 可移植」这个结论无法被机器验证。

### 8.9 双实例互踢（Presence）与「谁拥有连接」
`Presence.cs` 用文件心跳做 headless/交互式交接（`:8-28`、`:95`），服务端用 4000 关闭码踢旧连接（`hub.py:77-99`）。
Core 抽出来后，「连接所有权」由谁决定会变成新的边界问题：
**建议 Core 只提供 `Start/Stop/Restart`，让位策略留在平台层（Presence）**，Core 不得内置单实例语义。

### 8.10 `message_id` 的类型与离线本地 id
服务端是 INTEGER 自增（`db.py:39-54`），客户端用 `long`（`App.xaml.cs:754`、`WebHostWindow.xaml.cs:815`）。
需求 §5 说「稳定的 message_id」：如果将来要支持「离线时本地先建消息、联网再对账」，
就需要客户端临时 id + 服务端回填映射 —— 现状没有这个机制。
**待确认**：本轮是否需要离线发消息？（若需要，Phase 2 的模型要预留 `local_id ↔ server_id` 映射。）

### 8.11 WebView2 不可用时的「弹窗」语义（需求 §3 的用词）
需求 §3 写「Core 仍能连接、收消息、ACK、**弹窗**」。严格说「弹窗」是 UI 行为，
Core 只能做到「产生一个需要提醒的事件」。**待确认**窗口期语义：
(a) 平台层回落原生通知（托盘气泡，`App.xaml.cs:869-871` 已有用法）并 ACK；
(b) 如实回报 `unsupported`，不 ACK `popup_displayed`（则该消息在服务端永远停在 `device_received`）。
**建议 (a)**：对用户的可靠性更重要，且不谎报「弹窗已显示」可以靠状态名区分（例如本地记 `NotifiedFallback`）。

### 8.12 `UnlockGuard` 的归属
`UnlockGuard.cs` 做的是**协议层校验**（归属/动作/时效/重放，`:102-128`），几乎不碰 Windows API，
但 `ReasonNoCredential`（`:128`）暗示未来会依赖 Windows 凭据存储/凭据提供程序（`AgentClient.cs:363-370` 的注释同义）。
**待确认**：协议校验放 Core（平台无关），凭据执行放平台层 —— 建议按此拆，但需要拍板。

### 8.13 `web/static/shell.js`（916 行）的删除时机
`PC-LOCAL-UI.md:130-131` 说「标记废弃但保留，等 PC 端稳定一版后再清理」。
**待确认**：Phase 5 删除，还是再等一版？

### 8.14 待确认清单（汇总）
| # | 待确认事项 | 需谁拍板 |
|---|---|---|
| 1 | 实时 bug 是否必须先定位再进 Phase 2（§8.1） | 用户 |
| 2 | `connection_id` 是否进协议（§8.5） | 用户 |
| 3 | headless 的 ack 状态语义（§8.4） | 用户 |
| 4 | WebView2 不可用时的提醒回落与 ACK 语义（§8.11） | 用户 |
| 5 | 本轮是否需要「离线发消息」（§8.10） | 用户 |
| 6 | `UnlockGuard` 归属（§8.12） | 用户 |
| 7 | 服务端是否加 `since`/`after_id` 或补 target 记账（§8.2） —— 属协议变更 | 用户 |
| 8 | `shell.js` 删除时机（§8.13） | 用户 |
| 9 | `AgentConfig` 里 `theme/theme_mode` 是否算「平台无关」（现状注释说由页面决定，`Config.cs:129`） | 实现时定 |

---

## 9. 附录：勘察可复现命令

```bash
cd /vol1/<uid>/workspace/family-message

# 规模
wc -l pc-agent/FamilyAgent/*.cs pc-agent/FamilyAgent/*.xaml pc-agent/FamilyAgent/*.csproj \
      web/shell/* web/static/* web/index.html server/*.py server/services/*.py

# 「哪些 C# 文件碰 UI」
grep -lnE 'Dispatcher' pc-agent/FamilyAgent/*.cs
grep -lnE 'WinForms|WebView2|System\.Windows' pc-agent/FamilyAgent/*.cs

# 服务端实际帧与端点（协议文档的对照源）
grep -nE '@app\.' server/main.py
grep -rn '"type":' server/main.py server/hub.py
grep -nE 'if mtype == |elif mtype in |elif mtype ==' server/main.py

# capabilities / 会话状态
sed -n '241,290p' server/main.py          # _public() / _apply_reported_state()
sed -n '56,92p'  server/services/devices.py
sed -n '146,152p' server/db.py            # devices.windows_state / capabilities 迁移
sed -n '376,403p' pc-agent/FamilyAgent/AgentClient.cs   # BuildCapabilities / BuildHeartbeatJson

# 收消息链路的关键行
sed -n '749,780p' pc-agent/FamilyAgent/App.xaml.cs
sed -n '229,238p;336,361p;743,766p' pc-agent/FamilyAgent/WebHostWindow.xaml.cs
sed -n '316,340p' pc-agent/FamilyAgent/JsBridge.cs

# HEAD 与工作区的差异（§2.7-①）
git show HEAD:pc-agent/FamilyAgent/AgentClient.cs | grep -n '_ws = null'
git diff pc-agent/FamilyAgent/AgentClient.cs | head -60
```
