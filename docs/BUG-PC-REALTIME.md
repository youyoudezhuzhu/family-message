# PC 端收不到实时消息 —— 问题描述（供分析）

## 一、项目是什么

自托管的「家庭消息 + 设备控制」系统，**仓库是公开的**：
https://github.com/youyoudezhuzhu/family-message （分支 main，最新提交 `eb11c3c` v0.13.3）

三部分：

| 部分 | 技术栈 | 位置 |
|---|---|---|
| 服务端 | Python / FastAPI + SQLite + WebSocket | `server/` |
| 网页控制台（全功能版） | 原生 HTML/CSS/JS，Fluent 2 | `web/index.html`、`web/static/{app.js,style.css,tokens.css}` |
| PC 端 Agent | C# / .NET 9 / WPF **宿主** + WebView2 加载**本地页面** | `pc-agent/FamilyAgent/`（C#）、`web/shell/{app.html,pc.js,pc.css}`（界面） |

**PC 端架构（2026-09-26 刚重构过，理解了它才能定位问题）**：

```
PC 端宿主（C#，WebView2 窗口）
   ├─ 加载 exe 自带的本地页 https://familyagent.local/app.html   ← 不加载 NAS 网页
   ├─ 自己持有一条到 NAS 的 WebSocket（AgentClient.cs）
   └─ 收到服务端事件后，通过「宿主桥」把数据转给页面：
        宿主→页面  postMessage(JSON)     页面用 window.chrome.webview 收
        页面→宿主  window.chrome.webview.postMessage(JSON)
```

**桥的两条推送路径（本问题的核心）**：

| 路径 | 触发时机 | 对应的界面行为 |
|---|---|---|
| `host.message` | 服务端推来一条**新消息**（`AgentClient` 的 `case "message"`） | 实时刷新列表 + 切全屏弹窗 |
| `host.history` | 重开消息窗口时**全量重拉历史** | 列表整体替换 |

## 二、现象（用户原话）

> 「网页版发送消息 PC端不会自动全屏，PC端收到消息后不会显示，需要重新打开消息界面才会显示，
> PC端发送的消息不会在pc端自己这边显示、但在网页上可以正常显示，
> PC端重新打开消息界面以后才会显示自己的消息，
> 总结下来就是PC端的消息不会实时刷新」

> （修过一轮后）「PC端还是无法接收到最新的消息，需要重新打开消息窗口才会显示最新的消息」

**逐条拆解**：

1. 网页端发消息 → PC 端**不自动弹全屏**
2. 网页端发消息 → PC 端界面**不显示这条消息**
3. 但**重新打开一次消息窗口**（托盘 → 打开）→ 上面两条的消息**就都出来了**
4. PC 端自己回复的消息 → PC 端自己界面**看不到**，但**网页端能看到**
5. PC 端重开窗口后 → 自己发的那条也出来了

**关键推论**：现象 3 说明消息**早已在页面里**（重开窗口走的是 `host.history`，
说明数据能到页面）—— 所以怀疑是 **`host.message` 这条实时路径没生效**，
或者**窗口没有被显示到前台**（内容画好了但看不见）。

## 三、已经排除的（都做过实测，不是推断）

**① 服务端广播是好的** —— 起了一个服务端实例 + 一个模拟 PC 设备的 WebSocket 客户端，
从 HTTP API 发消息，模拟设备**确实收到了 `message` 帧**，且内容正确：

```json
{"type":"message","message_id":3,"sender_name":"妈妈","content":"结构测试",
 "message_type":"text","created_at":"…","status":"sent","auto_close_seconds":0,
 "history":[…]}
```

**② 页面渲染是好的** —— 用 Playwright 加载组装好的 `shell/` 目录 + 注入假桥，
注入一帧 `host.message` → 消息**立刻出现在列表里**、靠右（自己的）/靠左（别人的）、
带「已发送」标记。零控制台报错。

**③ 宿主发给页面的帧格式是对的**（静态核对）——
`JsBridge.PostMessage` 发 `{type:"host.message", message:{…}}`，
页面读 `d.message || d`；`JsBridge.PostHistory` 发 `{type:"host.history", messages:[…]}`，
页面读 `d.messages`。两条都对得上。

**④ 事件订阅与派发是对的**（静态核对）——
`App.xaml.cs:139 Client.MessageReceived += OnMessageReceived;`
`AgentClient.cs:370 case "message": MessageReceived?.Invoke(root.Clone());`

## 四、嫌疑集中在这里（唯一无法远程验证的部分）

**宿主侧 `OnMessageReceived` → 页面**。开发机是 Linux，**跑不了 WebView2**，
所以这一段只能读代码 + 靠用户机器上的日志。

```
App.xaml.cs:749   OnMessageReceived(el)
                    → Dispatcher.Invoke
                    → if (IsHeadless) { 回报 delivered; return; }
                    → var host = EnsureHost(); if (host is null) return;
                    → host.ShowPopup();                        // 切全屏形态 + 把窗口拉到前面
                    → host.PushMessage(el, autoClose);

WebHostWindow.xaml.cs:229  PushMessage(message, autoClose)
                    → if (!Bridge.PageReady) { _queued.Add(…); 写日志「页面未就绪，缓存消息 …」; return; }
                    → SendMessage(…) → Bridge.PostMessage(…)

JsBridge.cs:452    Send<T>(payload)
                    → if (Sender is null) return;
                    → if (!PageReady) return;                  // ← 又一个闸门
                    → Sender(json)

WebHostWindow.xaml.cs:336  OnPageReady()      // 页面的 web.ready 到达时
                    → if (_stage != Stage.App) { 写日志「等正式界面」; return; }
                    → Bridge.PageReady = true;
                    → PostHello / PostSession / PostConnection / PostMode
                    → FlushQueued();

WebHostWindow.xaml.cs:695  ShowWindow(alert)   // v0.13.3 刚改
                    → if (alert) 提示音
                    → if (!IsVisible) Show();
                    → if (WindowState == Minimized) WindowState = Normal;
                    → if (Topmost != wantTopmost) Topmost = wantTopmost;
                    → Activate(); Focus();
```

**几个可疑点（供参考，我没有证据）**：

- `PageReady` 是否真的变成了 `true`？如果没被置位，`PushMessage` 会走「缓存」分支
  （日志里会写「壳：页面未就绪，缓存消息 …」），而 `host.history`
  —— 用户重开窗口时能看到 —— 说明历史那条路是通的。两条路用**同一个** `PageReady` 闸门，
  按代码读不应该一个通一个不通，**这里有矛盾没解释通**。
- `_stage` 在页面发 `web.ready` 时是否已经是 `Stage.App`？
- 用户机器上有历史遗留的多份 exe（曾出现「开机的自启拉起的是旧版」导致看到的
  其实是旧程序），但用户已确认本次跑的是 v0.13.3（顶栏显示版本号）。

## 五、用户机器上的日志（关键片段）

```
=== 进程启动 pid=14084 exe=C:\…\FamilyAgent-win-x64(2)\FamilyAgent.exe ===
=== FamilyAgent 启动 device=pc_workstation_7000p server=http://192.168.1.50:18801 agent=cs-0.12.4 ===
页面模式 → client
窗口模式 → Window  1000x760 at (524,172)
托盘打开会话：窗口已显示 visible=True state=Normal size=1000x760
→ history_request（65 字节）
← hello
← history_response
壳：加载正式页面 http://192.168.1.50:18801/?shell=1&mode=client   ← 这是 v0.12.4 的旧日志
```

（注意：上面这段是**旧版本**的日志，那时还会加载 NAS 网址。v0.13.x 改成本地页之后
日志里的行是「壳：加载本机界面 app.html（exe 自带，不加载 NAS 网页）」。
**用户尚未提供 v0.13.3 的日志** —— 这是下一步最想拿到的证据。）

## 六、版本演进（避免把旧 bug 当成新 bug）

| 版本 | 做了什么 |
|---|---|
| v0.13.0 | PC 端界面改成本地页（不再加载 NAS 网页）；`?shell=1&mode=` 那套废掉，形态由桥的 `host.mode` 决定 |
| v0.13.1 | 单实例从 `Global\` Mutex 改成文件式（旧写法会因权限直接崩）；第二实例改为把已有窗口拉到前面 |
| v0.13.2 | 修本地页 URL 多拼一层 `/shell/` 导致的白屏 |
| v0.13.3 | 修 `ShowWindow` 只在首次显示时置顶（`Topmost` 只设过一次）+ 页面加「自己发的消息本地回显」 |

## 七、最想得到的帮助

1. 按上面的代码，`host.message` 这条实时路径有可能在什么情况下**不通**，
   而同一时刻 `host.history` **通**？（两条路共用 `PageReady` 闸门，这是最矛盾的地方）
2. 除了日志，有没有**在 WebView2 里直接观测**宿主→页面桥消息的可靠办法？
3. 现象 3「重开窗口就都出来了」—— 如果页面从未重新加载（本地页不会因为重开窗口而导航），
   那新消息是怎么出现的？除了 `host.history` 还有别的解释吗？
