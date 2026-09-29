# Android 契约核对表（Core 重构 Phase 5 · 第 3 项）

- 生成时间：2026-09-29 ｜ 依据：`docs/CORE-REFACTOR-PLAN.md` §6 Phase 5（第 606–607 行）、§7-G-17
- 结论一句话：**协议与消息/连接状态机原样复用**（Core 就是它的参考实现），
  Android 只需要自己实现 **6 个依赖倒置接口**（下表 A），并把「不支持的能力」如实回 error、**不静默超时**。
- 本页只做契约核对，**不改任何代码**；`Core` 在这轮保持零改动。

---

## A. Android 必须自己实现的接口（Core 用它们切断平台耦合）

| 接口（`FamilyAgent.Core.*`） | 成员签名 | Windows 参照实现 | Android 实现要点 | 不实现 / 失败时的**规定行为** |
|---|---|---|---|---|
| `IPlatformInfo` | `bool IsHeadless { get; }`<br>`string Platform { get; }`<br>`string SessionState { get; }` | `Platform/WindowsPlatformInfo.cs` | `Platform="android"`；`IsHeadless` 恒 `false`（有桌面）；`SessionState` 用 `KeyguardManager.isKeyguardLocked` + 前台/后台：「锁屏」→`logon_screen`、否则 `interactive` | 缺了它心跳里没有 `windows_state`，但**能力表照常上报**（`DeviceCapabilities.Build` 只看 `PlatformCapabilities`）。⚠ 实现必须**每次现算**、不许缓存 |
| `IScreenshotProvider` | `Task<ScreenshotResult> CaptureAsync(CancellationToken)` | `Platform/WindowsScreenshotProvider.cs` | `MediaProjection`（需用户授权一次）；返回 JPEG base64 + width/height | ★ **必须回 `ScreenshotResult.Unsupported("本机不支持截图")`（或 `Failed`）→ Core 发 `screenshot_response{error}`**，绝不静默（§7-G-17） |
| `IPowerProvider` | `int DefaultDelaySeconds { get; }`<br>`Task<PowerResult> ShutdownAsync(int delaySeconds, CancellationToken)` | `Platform/WindowsPowerProvider.cs` | 一般不实现（手机不给 App 关机）；要支持则用 `DevicePolicyManager`/无障碍 | 返回 `PowerResult.Supported=False` → `event{kind=shutdown_failed}`；**不要抛异常**（Core 会兜，但那是降级不是设计） |
| `IUnlockGuard` | `UnlockReply? Evaluate(UnlockRequestFrame request, string? myDeviceId)` | `Platform/WindowsUnlockGuard.cs` | 本阶段**不实现**（`unlock` 能力不上报，见下）；返回 `null` 即「本机不管这条」 | 不实现 → `unlock` 不进能力表（**当前 Windows 也不上报**，见 `DeviceCapabilities` 注释）；收到 `unlock_request` 也不应答 |
| `IMessageStore` | `Put/Get/Contains/All/PendingReplay/Count/Describe/Compact` | `Messaging/JsonlMessageStore.cs`（`%APPDATA%` 下的 JSONL） | Room/SQLite 或应用私有目录的追加式 JSONL；语义等价于「**进程崩溃也不丢**」 | 不实现就没法重放：Phase 4 的「断线 30s 不丢消息」判据靠 `PendingReplay()`，缺失 → 消息只在内存里，重启即丢 |
| `INotificationSink` | `bool Notify(string? senderName, string? content, string reason)` | `Platform/FallbackNotifier.cs` | `NotificationCompat` + 通知渠道（Android 8+ 必须建 channel） | WebView2 不可用时才走它（PC 的回落路径）；Android 若自带 UI 可先不注册，但**建议实现**，用于「界面没起来也必须提醒」 |

另有两个**非接口但必须注入**的依赖（同一类「宿主给、Core 用」的关系）：

| 依赖 | 签名 / 形态 | Android 侧 |
|---|---|---|
| `INicknameStore` | `NicknameState Load(string defaultName)` / `void Save(NicknameState)` | 存 `SharedPreferences` 里的**逻辑色 ID + nickname_id**（**不是**颜色 HEX，见 §4.4 / `NicknameColor`） |
| `INicknameTransport` | `bool Connected { get; }` / `bool Send(string type, object payload)` | 生产用 `ConnectionNicknameTransport(connectionManager)`：**直发当前连接，不进 Outbox、不落盘、不补发**（离线改昵称必须被拒） |
| 昵称表时间来源 | `NicknameService.OnFrame(CoreFrame)` | 由宿主持有（**单消费者**）：把连接层收到的帧转发进来一次 |

---

## B. Android 可直接复用、**不要重写**的部分

| 能力 | Core 里的类 | 为什么平台无关 |
|---|---|---|
| 协议帧解析 / 构造 | `Protocol/FrameCodec.cs`（`Parse` / `NicknameFrames`） | 纯 JSON，零平台依赖；枚举 `FrameTypes` 与 `docs/PROTOCOL.md` 对齐（Phase 5 段 A 会把文档补全） |
| 连接 / 退避重连 / 心跳 / Outbox | `Transport/{ConnectionManager,WebSocketTransport,HeartbeatLoop,BackoffPolicy,Outbox,ConnectionEpoch}.cs` | 只依赖 `ClientWebSocket`；Android 用 OkHttp 时**照抄状态机语义**（单 active 连接、epoch 换代、旧连接 `finally` 不改新状态） |
| 消息生命周期 / ACK / 重放 | `Messaging/{MessageManager,MessageStore,PendingAckQueue,DeliveryState}.cs` | `device_received → popup_displayed → read` 的状态机与 `max()` 语义；**UI 不订阅也照样入库 + 发 ack** |
| 命令派发 | `Commands/CommandRouter.cs` | 只认上面 4 个能力接口；返回「不支持」也是**成功应答**（`UnsupportedScreenshotReason` / `UnsupportedShutdownReason`） |
| 断线补齐 | `Sync/{SyncService,HistoryBackfill,ISyncConnection}.cs` | 握手序列 `Connect → Authenticate → Sync/History 补齐 → Live`，靠 `ISyncConnection` 抽象，Android 用自己的 WebSocket 实现它即可 |
| 昵称整表 / 上色 | `Nicknames/{NicknameService,NicknameColor}.cs` | `NicknameColor` 是**纯函数**（逻辑色 ID → 各主题显示色），Android 可逐行照抄；R7 要求与 Web/PC 渲染一致 |

---

## C. 协议层注意事项（**对 `PROTOCOL.md` §10 的修正**）

`docs/PROTOCOL.md` §10（第 240–252 行）是 Android 初版，其中两条**已经过期**，Phase 5 段 A 会同步修文档：

1. ~~「昵称：存 `SharedPreferences`，和服务端无关」~~ → **错**。v0.17.0 起昵称是**服务端全局对象**
   （`nickname_id` / 逻辑色 ID / 广播），Android 必须：连接后发 `nickname_list_request` 拉整表、
   监听 `nickname_created/_updated/_color_changed/_removed`、选区动作**纯本地**（`web`/PC 同口径）。
   协议细节见段 A 补全后的帧表（当前文档完全没有 `nickname_*` 帧）。
2. 「心跳 15 秒 + `ForegroundService`」→ **仍然有效**（Doze/后台限制）；`heartbeat` 与
   `heartbeat_ack` 的字段以修正后的帧表为准（含 `windows_state` / `capabilities` 两个查询参数）。
3. ★ **v0.19：颜色表是数据，不许写死 16 色**。Android 必须：
   · 连接后从 `hello.color_table`（`[{color_id, hex}]`）或 `GET /api/nicknames/colors` 取**权威色表**，
     内置那份只做首屏兜底；
   · 监听 `color_table_changed` → **重拉**色表（帧里只有版本号，不推全表）→ 按新表重画；
   · 认不出的逻辑色 ID 走兜底色（**绝不**把 ID 原样塞进样式，R2 不变）；
   · 快照里的 `sender_color` 仍是逻辑色 ID —— 着色一律「先查表、查不到兜底」。
   核心口径与 `Nicknames/NicknameColor.cs` 一致（`ColorTable` / `NicknameColorEntry` 已在 Core 里）。

其他仍然有效的既有结论（§10 第 1/2/4/5/7 条）：协议零依赖（WebSocket + JSON + base64）、
`tools/cli_agent.py` 是协议的**参考实现**、连接时 `type=phone` / `platform=android`、
截图授权失败要回 `error` 帧、发送队列必须自己实现（移动网络切换频繁）。

---

## D. 验收（本页怎么算过）

| 判据 | 怎么测 |
|---|---|
| 表 A 的 6 个接口与 Core 代码里的签名**逐字一致** | `grep -n 'interface I' pc-agent/FamilyAgent.Core/Commands/Abstractions/*.cs` 与上表逐条比对（本页生成时已比对过一遍） |
| 「不支持」有终态、不超时 | Core 单测已覆盖（`CommandRouter` 用 `PlatformCapabilities.None` 时回 `error` 帧）；Android 侧照此写 |
| 与协议初版交叉核对无冲突 | 本页 §C 的两条修正已登记，段 A 会把 `PROTOCOL.md` §10 一并改掉，避免两处口径打架 |
| Core 零改动 | `git diff --stat pc-agent/FamilyAgent.Core/` 在 Windows 侧 Phase 5 期间应为空 |
