# PC 端本地界面架构（v0.13.0）

> 2026-09-26 辉哥拍板。**这是架构更正，不是加功能。**

## 为什么改

辉哥原话：

> 「我觉得你目前这个模式从根本上就不对，pc端只是链接通讯，基本的ui界面不应该依赖于网页端」

之前 PC 壳加载的是 NAS 的 `index.html`（完整网页控制台），靠 CSS 把控制台藏起来只留客户端。
**这个做法从根上就错**：把"网页端能不能正常渲染"变成了"PC 端能不能用"的前提。
实测就是控制台藏不掉，PC 上一直显示带侧边栏的管理后台。

## 新架构

```
旧：PC 壳 ──加载──> NAS 的 index.html（完整控制台）→ 靠 CSS 藏起来（会失败）
新：PC 壳 ──加载──> exe 自带的本地页面（只有 PC 界面，物理上没有控制台）
              └──WebSocket──> NAS（只走数据与通讯）
```

| | 旧 | 新 |
|---|---|---|
| 界面从哪来 | NAS 的网页 | **exe 自带**（`shell/app.html`） |
| 控制台 | 有，靠 CSS 藏 | **物理上不存在** |
| NAS 挂掉 | 白屏 / 离线页 | **界面照常，只是收不到消息** |
| PC 端职责 | 渲染网页 | **只管通讯 + 自己的界面** |
| 形态怎么定 | URL `?shell=1&mode=xxx` | **桥的 `host.hello.mode` / `host.mode`** |

**`?shell=1&mode=` 这套整个废掉** —— 形态不再依赖 URL，那类"URL 没生效 → 界面不对"的
bug 从根上消失。

## 界面从哪来（三个视图，同一个本地页）

`shell/app.html` 一个页面，三个视图：

| 视图 | 什么时候 | 内容 |
|---|---|---|
| `client` | 启动 / 托盘打开 | 顶栏（家庭消息·本机名·连接状态·设置按钮）+ 群聊记录 + 回复栏 |
| `popup` | 收到消息 | 全屏强提醒（最大字号的消息 + 回复栏 + 知道了） |
| `settings` | 点顶栏设置 | **本机配置**：服务端地址、注册口令、回复昵称、开机自启、主题、版本信息 |

**PC 端没有设备页**（截图/关机/解锁）—— 那些只在 NAS 网页端。
但 PC 的**能力**保留（服务端仍可远程请求截图/关机/解锁），只是 PC 界面上不给入口。

## 文件清单（构建时怎么进包）

```
web/shell/app.html      ← PC 端唯一界面（源文件在网页端仓库里，一处维护）
web/shell/pc.css        ← PC 界面样式（令牌来自 tokens.css）
web/shell/pc.js         ← PC 界面表现层（视图切换 / 设置表单 / 桥消息分流）
web/shell/boot.html     ← 保留：首屏占位
web/shell/offline.html  ← 保留：连不上服务端时的诊断页
web/static/chat.js      ← **与网页端同一份源文件**，构建时复制到 shell/
web/static/tokens.css   ← 同上
```

`FamilyAgent.csproj` 的 Content 规则要扩展成把上面这些 `.html/.css/.js` 都复制到
输出的 `shell/` 下。`shell/` 通过虚拟域名 `https://familyagent.local/shell/` 加载。

**与网页端共享 `chat.js` + `tokens.css` 的源文件** —— 所以群聊气泡两端长得完全一样
（辉哥要的"统一"），但**互不依赖**：网页端改了不会自动改变 PC 端，要重新编译才生效。

## 桥协议（**只加不删**）

已存在的照旧用：
```
页面→宿主  web.ready / web.reply / web.ack / web.close / web.quit
           web.request_screenshot / web.request_action / web.set_server
宿主→页面  host.hello / host.mode / host.message / host.history / host.reply_ack
           host.connection / host.session / host.screenshot / host.action_result
```

### 新增：`host.hello` 补字段（只加）
```jsonc
{ "type": "host.hello",
  "mode": "client|popup|settings",   // 起始视图
  "version": "cs-0.13.0",
  "platform": "Windows 11 …",
  "server": "http://192.168.31.50:18801",
  "theme_mode": "system|light|dark",
  "device_id": "pc_xxx", "device_name": "书房电脑",
  "reply_names": ["…"], "reply_name": "…",
  "enroll_configured": true,        // ★ 新增：注册口令配过没有
  "autostart": true                 // ★ 新增：开机自启开着没有
}
```

### 新增：配置读写
```jsonc
// 页面→宿主
{ "type": "web.save_config",
  "server_url": "http://…",     // 可选，只传要改的
  "enroll_token": "…",
  "reply_name": "爸爸",
  "autostart": true,
  "theme_mode": "dark" }

// 宿主→页面
{ "type": "host.config_saved", "ok": true, "detail": "服务端地址已保存，正在重连" }
```
`web.set_server` 保留（等价于只传 `server_url` 的 `web.save_config`）。

### 新增：设置视图要读的运行时信息
```jsonc
// 宿主→页面（进设置视图时发一次，或配置变化时主动推）
{ "type": "host.runtime",
  "version": "cs-0.13.0", "runtime": "153.0.4234.48",
  "device_id": "pc_xxx", "device_name": "书房电脑",
  "server": "http://…", "enroll_configured": true, "autostart": true,
  "theme_mode": "system",
  "log_path": "C:\\Users\\…\\AppData\\Roaming\\FamilyAgent\\app.log",
  "config_path": "C:\\Users\\…\\AppData\\Roaming\\FamilyAgent\\config.json" }
```

## 宿主改动要点

1. **永远加载本地页**：`GoApp()` 取代 `GoServer()` —— 导航到 `app.html`，不再导航到 NAS 网址
2. **形态只走桥**：`ShowPopup/ShowClient/ShowSettings` 只改 `Bridge.Mode` + 发 `host.mode`，不重新导航
3. `GoOffline()` 保留（服务端不可达时给诊断页）—— 但**只在从未配置过**时用；
   已配置过的话直接进 app.html，界面照常，顶栏显示"未连接"
4. 顶栏的**设置按钮**发 `web.open_settings` → 宿主切 settings 形态
5. 界面上**显示版本号 + 当前形态**（辉哥要求，防再出"跑的是哪版"的困惑）

## 不动的东西

- 服务端（`server/`）**一行不改** —— 网页端继续是全功能版本
- WebSocket 协议、设备注册、心跳、离线补投、截图/关机/解锁链路
- `web/static/shell.js` / `shell.css`：**标记为废弃但保留**（网页端的壳模式不再被使用，
  先不删，等 PC 端稳定运行一版后再清理）


---

## 附：v0.13.0 白屏事故（必记）

**症状**：PC 端打开后**纯白，什么都没有**，日志里也没有任何线索。

**真因**：`NavigateLocal()` 拼 URL 时多了一层 `/shell/`。

```csharp
// 映射的根目录就是 ShellDir()（<exe>\shell）
core.SetVirtualHostNameToFolderMapping(LocalHostName, ShellDir(), ...);

core.Navigate($"{LocalOrigin}/shell/{file}");   // ✗ 解析成 <exe>\shell\shell\app.html → 404
core.Navigate($"{LocalOrigin}/{file}");          // ✅
```

这行是**早期**写的 —— 那时映射根目录是 exe 目录，`/shell/xxx` 是对的；
后来根目录改成 `ShellDir()` 却没同步改这行。

**两条永久性防线**（已加进代码）：

1. **`NavigationCompleted` 里判断失败必须落日志**，并带上 `HttpStatusCode` ——
   404 会直接显示成 `✗ 页面加载失败：...（HTTP 404）url=...`。
   白屏那种「什么都没说」的状态，比 bug 本身更难查。
2. 本地页的 URL 一律 `{LocalOrigin}/{file}`，**不带任何目录前缀**。

## 附：跨平台演进方向（辉哥 2026-09-26 定）

目标：将来能跑到 **Linux / 安卓**，宿主框架换成跨平台方案（如 Avalonia）。

**这正好是本地页面方案的价值所在**：界面是纯 HTML/CSS/JS，在 Linux 的 WebKitGTK、
安卓的 WebView 里都能原样跑；换宿主只需重写「宿主层」，界面整块留下。

要为此保持的分层（后续重构方向，不是现在就要做）：

```
Core/          通讯、配置、会话状态、截图、电源 —— 零 UI 依赖，可直接移植
UI/            宿主窗口 + WebView 承载 + 桥 —— 换平台时重写这一层
web/shell/     界面本体（HTML/CSS/JS）—— 跨平台原样复用
```

新增功能时注意：**能放 Core 就别放 UI**，能做成桥消息就别写进宿主的 C#。
