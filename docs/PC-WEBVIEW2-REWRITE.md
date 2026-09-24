# PC 端重做方案：WebView2 壳

> 目标：PC 端不再是 WPF 手绘界面，改为 **WebView2 壳 + 加载网页端页面**。
> 好处：UI 与网页端**完全一致**、改样式只改一处、两端**同步更新**。

---

## 1. 为什么这次值得重做

前几轮 PC 端反复出问题的根因是**平台本身**，不是我没写好：

| 踩过的坑 | 性质 |
|---|---|
| 改共享画刷 `.Color` → 撞上 WPF 的 Freeze 机制，界面打不开 | XAML 运行期语义 |
| `{x:Static}` 不做类型转换 → `CornerRadius` 拆箱失败，窗口永远打不开 | 同上 |
| `x:Static` 不支持嵌套类型（MC3050） | 编译期才发现 |
| 硬编码 `CornerRadius="28"` 在换主题时被漏改 → 看着还是 Material | 全局替换靠人眼 |
| `align-self` 让抽屉按内容高度收缩 | CSS 也有坑，但**能截图看到** |

**关键差别**：这些错误**编译全过、只在真机运行时暴露**，而我在 NAS 上既跑不了 WPF 也截不了图 —— 只能盲改，改完等你反馈。

换成 WebView2 后：**HTML/CSS/JS 我可以用 Playwright 截图自检**，同一个坑不会再"盲"第二次。

---

## 2. 架构

```
FamilyAgent.exe  (WPF 宿主，界面只剩一个 WebView2 控件)
├── WebView2      ← 加载网页端页面，这就是"界面"
├── 托盘图标       （NotifyIcon，仍是宿主职责）
├── AgentClient   （WebSocket，宿主持有 —— 断线重连/离线队列/心跳都在这）
├── 截图 / 关机 / 会话状态 / 开机自启 / 解锁校验   （宿主能力，经 JS 桥暴露）
└── 两种显示模式
     · Popup  全屏 / 无边框 / 置顶  → 加载 ?shell=1&mode=popup
     · Window 标准窗口 / 可缩放     → 加载 ?shell=1&mode=console
```

**保留不动**（纯 C#，不依赖 UI 框架）：
`AgentClient.cs` / `AutoStart.cs` / `Presence.cs` / `Config.cs` /
`ScreenCapture.cs` / `PowerControl.cs` / `SessionState.cs` / `UnlockGuard.cs` /
`AgentLog.cs` / `HistoryItem.cs` / `App.xaml.cs`（启动与托盘）

**删除**：`PopupWindow.xaml(.cs)` / `MessageCard.cs` / `MdTheme.cs` / `MdPalette.g.cs`
（那一整坨手绘 UI 和主题层，连同它的坑一起删掉）

**新增**：`WebHostWindow.xaml(.cs)`（WebView2 宿主）+ `JsBridge.cs`（桥）

---

## 3. 页面加载策略

| 场景 | 加载什么 |
|---|---|
| 正常 | 服务端的网页端页面（**因此 UI 与网页端同步更新**） |
| 连不上服务端 / 还没配置 | **本地兜底页**（`shell/offline.html`，随 exe 打包）：填服务端地址 + 注册口令 + 重试 |
| 首屏 | 先显示本地 `shell/boot.html`（窗口不闪白），`host.hello` 到达后再切到正式页面 |

> 服务端地址存在配置里。壳启动 → 读配置 → 探测 `/healthz` → 成功就加载正式页，失败就加载兜底页。

---

## 4. JS 桥协议（**冻结**）

### 宿主 → 页面（`window.chrome.webview.postMessage`）

```jsonc
{"type":"host.hello","mode":"popup|console","version":"cs-0.12.0",
 "platform":"Windows ...","server":"http://192.168.31.50:18801","theme_mode":"system"}

{"type":"host.connection","connected":true,"detail":"已连接"}

// 消息推送：壳自己持有 WebSocket，页面不连 /ws/web
{"type":"host.message","message":{"id":123,"sender_name":"妈妈","content":"…",
 "created_at":"2026-09-24 18:32:00","device_id":"pc_xxx","status":"device_received"}}

{"type":"host.reply_ack","client_id":"…","status":"ok|empty|error","message_id":123,"detail":"…"}

{"type":"host.session","windows_state":"locked","can_unlock":true,"can_screenshot":true,"can_shutdown":true}

{"type":"host.screenshot","request_id":"…","ok":true,"data_url":"data:image/jpeg;base64,…"}
{"type":"host.screenshot","request_id":"…","ok":false,"error":"尚无人登录，当前没有可截取的桌面"}

{"type":"host.action_result","action":"shutdown|unlock|…","ok":true,"detail":"…"}

{"type":"host.mode","mode":"popup|console"}      // 宿主切换形态时通知页面
```

### 页面 → 宿主

```jsonc
{"type":"web.ready"}                              // 页面已就绪，宿主可以发 hello 了
{"type":"web.ack","message_id":123}               // 消息已在屏幕上显示 → 宿主回报 popup_displayed
{"type":"web.reply","client_id":"…","sender_name":"…","content":"…"}
{"type":"web.close"}                              // 点「知道了」→ 宿主隐藏窗口（并 ack 未读）
{"type":"web.request_screenshot","request_id":"…"}
{"type":"web.request_action","action":"shutdown|unlock","device_id":"…"}
{"type":"web.switch_mode","mode":"popup|console"} // 弹窗里点「打开设置」→ 宿主切窗口形态
{"type":"web.set_server","url":"…","enroll_token":"…"}   // 兜底页保存配置
{"type":"web.quit"}                               // 退出程序
```

**约定**：
- 桥消息**一律走 JSON**，字段名固定，新增字段不删旧字段（前后兼容）
- 页面**不能**直接调用宿主能力，必须走桥；宿主收到后**自己校验权限与状态**
- 壳模式下页面**不连 `/ws/web`**（避免和宿主双份消息）；HTTP API 照常用

---

## 5. 依赖与打包

- NuGet：`Microsoft.Web.WebView2`
- **WebView2 Runtime 依赖**：
  - Windows 11 自带 ✓
  - Windows 10 可能需要装（微软官方 Evergreen Runtime）
  - 壳启动时**自检**：`CoreWebView2Environment.GetAvailableBrowserVersionString()` 失败 →
    显示一个本地提示页，给出官方下载链接，并提供「打开安装页」按钮
- 保持 .NET 9 + 自包含单文件（`IncludeNativeLibrariesForSelfExtract` 已开）
- GitHub Actions 云编译流程不变（多一个 NuGet 还原）

---

## 6. 分步实施

| 步骤 | 内容 | 验收 |
|---|---|---|
| **A** | 网页端支持**壳模式**：检测 `window.chrome.webview`、弹窗页、控制台页走桥 | Playwright 注入假桥 + 截图 |
| **B** | 宿主 `WebHostWindow` + `JsBridge`，删掉手绘 UI | 云端编译通过 + 真机跑通 |
| **C** | 本地兜底页（未配置/连不上）| 断网实测 |
| **D** | 回归：消息收发/截图/关机/解锁/开机自启/会话状态 | 对照现有测试清单 |

**A 可以先做且完全可验证**（我能截图）；B 依赖 A 的协议但可以并行写。

---

## 7. 我无法验证的部分（提前说清）

- WebView2 在真机上的加载、DPI、全屏置顶行为
- WebView2 Runtime 是否已装（**需要你确认那台 PC 是 Windows 11 还是 10**）
- 托盘图标与 WebView2 的交互
- 单文件发布后 WebView2 native loader 能否正常解压

> 但相比 WPF：**界面本身我能截图验证了**，这已经是最大的改善。
