# FamilyAgent.Core —— 平台无关内核

**这一层不认识 UI，也不认识任何具体平台。** 它能在 Linux 上编译、跑单测（CI 的 ubuntu job 就是靠它）。

```
pc-agent/FamilyAgent.Core/          net9.0，零 PackageReference（连 WebView2 都不引）
pc-agent/FamilyAgent.Core.Tests/    xunit；`dotnet test` 在 ubuntu 上全绿
pc-agent/FamilyAgent/               net9.0-windows：WPF 窗口 + WebView2 壳，只做「显示 + 转发 + 平台实现」
pc-agent/FamilyAgent/Platform/      Windows 的平台实现（WindowsPlatformInfo / Screenshot / Power / Unlock / FallbackNotifier）
```

## 分层规则（机械可查，别破坏）

1. **Core 里不允许出现 UI 或平台字样**：`WebView2` / `System.Windows` / `Dispatcher` / `System.Windows.Forms`
   只能出现在**注释**里（解释「为什么本类不认识它们」），不能出现在代码里。
   > 注：`docs/CORE-REFACTOR-PLAN.md` §7-A-1 给的判据是纯文本 `grep`，会被这类注释命中 ——
   > 判读时要**先剥注释**（Phase 5 勘察已指出该判据需要这样修正）。
2. **平台能力一律走接口注入**：`IPlatformInfo` / `IScreenshotProvider` / `IPowerProvider` /
   `IUnlockGuard` / `INotificationSink`（都在 `Commands/Abstractions/`）。Core 只认接口，
   Windows 侧在 `FamilyAgent/Platform/` 各给一份实现；Android 见 `docs/ANDROID-CONTRACT.md`。
3. **持久化也走接口**：`IMessageStore`（消息）+ `INicknameStore`（本机当前选用的昵称）。
   默认实现 `JsonlMessageStore` / `JsonNicknameStore` 落在 `%APPDATA%`（Windows 侧），
   数据目录由宿主决定。
4. **对外事件只有五类**（`Events/CoreEvents.cs` + 各类 `event`）：连接状态、消息到达/回填、
   投递状态、命令到达、截图处理结果。Core **没有**「把消息交给某个窗口」的路径。

## 关于注释里的 `AgentClient`

`pc-agent/FamilyAgent/AgentClient.cs` 是 **v0.16 重构前的单体类，已经删除**。
Core 里 20 多处注释引用它（形如「原 `AgentClient.cs:381` 的直接读」「原 `AgentClient.Dispatch` 的映射部分」），
那是**迁移溯源**，故意保留：

- 读法是「这段逻辑从哪里搬来的」，不是「有个叫 AgentClient 的类还能编译」；
- 行号对应 **v0.16 之前** 的文件，现在 grep 不到是正常的；
- 完整映射见 `docs/CORE-REFACTOR-PLAN.md` §4「迁移映射表」。

⚠ 别把这些注释当「死代码引用」批量删掉 —— 删了就没有「为什么这么写」的线索了；
要收敛口径请**只改本文件**，或先把映射补进 §4。

## 相关文档

- `docs/CORE-REFACTOR-PLAN.md` —— 分层重构总方案（Phase 1–5）与机械验收标准
- `docs/PROTOCOL.md` —— 协议冻结版（帧表 + 能力清单 + 客户端检查表）
- `docs/ANDROID-CONTRACT.md` —— Android 必须自己实现哪些接口
- `docs/PC-LOCAL-UI.md` / `docs/PC-WEBVIEW2-REWRITE.md` —— PC 壳与页面
