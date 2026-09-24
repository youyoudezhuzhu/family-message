# Windows 远程解锁改造方案

> 状态：**待辉哥确认**，确认前不动代码。
> 对应需求：`Transfer Dock_Text_20260924111600.txt`

---

## 0. 先说三个与你需求文档对不上的事实

| 需求文档写的 | 仓库实际 | 说明 |
|---|---|---|
| `C# / .NET 8 WPF` | **.NET 9 WPF** | v0.9.0 已按你的选择升到 .NET 9（Fluent 内置主题）。方案按 .NET 9 写 |
| 「PC Agent 依赖 Windows 用户登录后启动，登录界面时不在线」 | **这个已经解决了** | v0.10.1 已交付 `ONSTART` 计划任务（SYSTEM 身份 `--headless`），**开机后不登录，网页端已能看到在线** ✓ |
| 「必须使用 Windows Service」 | 当前是**计划任务**，不是 Service | 登录前启动这件事两者都能做到。Service 的额外好处是崩溃自动重启、`services.msc` 可管、session 0 常驻进程更标准。**建议迁移，但它是增强、不是前置条件** |

**所以第一阶段的一部分已经在你手里了。** 下面的方案聚焦你真正缺的那部分：**远程解锁**。

---

## 1. 现有架构（逐项核实过，不是推测）

### 1.1 NAS 服务端

`server/` — FastAPI + SQLite + WebSocket，单进程。

| 文件 | 职责 |
|---|---|
| `main.py` (807 行) | 全部 HTTP 路由 + WebSocket 端点 |
| `hub.py` | 在线设备与 WS 连接的双向绑定 |
| `db.py` | SQLite 建表 + 迁移 |
| `services/devices.py` | 设备注册、token 校验 |
| `services/messages.py` | 消息与投递 |
| `services/xiaomi.py` | 米家 OAuth2 + MIoT 规格 |

### 1.2 认证模型（现状 —— 这是改造的起点）

**Web 身份**（`main.py:121`）

```python
@app.post("/api/login")          # body: {password}
  → 与 CONFIG["web"]["password"] 做 hmac.compare_digest
  → set_cookie(SESSION_COOKIE, _session_token(), httponly, samesite=lax, max_age=session_hours*3600)
```

- **单口令、无用户、无角色、无权限表** —— `WebAuth` 依赖只回答「登录了没」
- 所有 `/api/*` 都挂 `dependencies=[WebAuth]`

> ⚠️ **这是本方案最大的改造点**：需求 §13 要 `device.unlock` 权限，但现在系统里**根本没有"权限"这个概念**，只有一个布尔「已登录」。

**设备身份**（`main.py:569`）

```
WS /ws/device/{device_id}?token=&name=&type=&platform=&agent_version=&enroll_token=
  未注册 → enroll(device_id, name, ..., enroll_token)   # 校验共享的注册口令
  已注册 → verify_token(device_id, token)               # 或者给注册口令也行（方便重装）
```

- 设备凭的是一个**服务端签发的共享 token**，放在 WS 查询串里
- **没有设备密钥对**，没有挑战-应答，没有签名

> 需求 §19 要的 `private key 只在 PC / public key 在 NAS` 目前**完全不存在**，属于新增。

### 1.3 数据表（`db.py`）

```
devices(id, device_id, name, type, platform, status, last_seen, token,
        ip, agent_version, created_at)
messages(...)          message_targets(...)
xiaomi_devices(...)    xiaomi_auth(...)
events(...)            ← 已有审计表，可直接复用
```

`devices.status` 只有 `online` / `offline`。**没有 Windows 会话状态字段**。

### 1.4 WebSocket 协议（现有全部帧）

| 方向 | type | 用途 |
|---|---|---|
| NAS→PC | `hello` | 握手，回传设备配置 |
| NAS→PC | `message` | 投递留言 |
| NAS→PC | `screenshot_request` | 请求截图 |
| NAS→PC | `wake` / `shutdown` | 米家唤醒 / 关机 |
| NAS→PC | `history_response` `device_status` `heartbeat_ack` `message_status` `reply_ack` `pong` `device_updated` `device_deleted` | 应答与广播 |
| PC→NAS | `heartbeat` `ack` `reply` `history_request` `pong` | 上报 |

### 1.5 Hub 的一个关键约束（会影响架构选型）

```python
async def bind_device(self, device_id, ws):
    old = self.devices.get(device_id)
    self.devices[device_id] = ws
    if old is not None and old is not ws:
        await old.close(code=4000, reason="replaced by new connection")
```

**同一个 `device_id` 只允许一条 WS 连接**，新连接会把旧的踢掉。

> 这条直接决定了：**Service 和 UI 不能各自连 NAS**。必须由 **Service 单独连 NAS**，UI 通过本地 IPC 挂在 Service 上 —— 正好就是需求 §18 的设计。

### 1.6 PC Agent（`pc-agent/FamilyAgent/`，单进程 WPF）

| 文件 | 职责 |
|---|---|
| `App.xaml.cs` (594) | 启动流程、`--headless` / `--config`、事件分发 |
| `AgentClient.cs` (512) | WS 客户端、重连、离线队列 |
| `AutoStart.cs` (443) | Run 项 + `FamilyAgent-Logon` / `FamilyAgent-Boot` 计划任务 |
| `Presence.cs` (148) | 登录前/后两个实例的心跳交接 |
| `PopupWindow.xaml(.cs)` | 全屏消息弹窗 + 设置页 |
| `MessageCard.cs` / `MdTheme.cs` / `MdPalette.g.cs` | 消息卡片与主题（Fluent） |
| `ScreenCapture.cs` / `PowerControl.cs` | 截图 / 关机 |
| `Config.cs` | `%APPDATA%\FamilyAgent\config.json` |

**配置文件现状**：明文 JSON，含 `token` / `enroll_token`。

> ⚠️ 需求 §2 明令禁止「明文配置文件」。**新增的 Windows 密码绝不进这个文件**；顺带建议把 `token` 也迁到 DPAPI。

---

## 2. 目标架构

```
       浏览器（手机/PC）
            │  Web 登录密码 → Session Cookie
            ▼
   ┌──────────────────────────┐
   │  NAS Server (FastAPI)    │
   │  · 权限检查 device.unlock │
   │  · 生成一次性 Unlock Token │
   │  · 只存 device public_key │
   └────────────┬─────────────┘
                │ WSS /ws/device/{device_id}
                ▼
   ┌──────────────────────────────────────┐
   │  FamilyAgent.Service  (Windows Service) │
   │  · 开机即启，不依赖登录                  │
   │  · WS / 心跳 / 设备认证 / 状态上报        │
   │  · 截图代理 · 关机 · 解锁授权             │
   │  · 唯一连 NAS 的进程                     │
   └───────┬──────────────────────┬─────────┘
           │ Named Pipe (本地 IPC)  │ 一次性授权文件
           ▼                      ▼   (ACL: 仅 SYSTEM)
   ┌──────────────┐      ┌────────────────────────┐
   │ FamilyAgent.UI│      │ FamilyAgent.Credential │
   │ WPF / 托盘     │      │ Provider (原生 C++ COM) │
   │ 全屏弹窗       │      │ 运行在 LogonUI 进程内    │
   │ 设置           │      │ CPUS_LOGON / UNLOCK     │
   └──────────────┘      └───────────┬────────────┘
                                     │ DPAPI 解密（机器范围）
                                     ▼
                           Windows 凭据（只在本机）
```

**核心不变式**：Windows 密码**从 PC 产生、存在 PC、在 PC 使用**。NAS 只在中间传一个**不含密码**的一次性令牌。

---

## 3. 需要修改 / 新增的模块

### 3.1 新增

| 模块 | 语言 | 说明 |
|---|---|---|
| `FamilyAgent.Service` | C# .NET 9 | Windows Service 宿主，**唯一**连 NAS 的进程 |
| `FamilyAgent.Shared` | C# | 协议模型、IPC 契约、常量（Service / UI 共用） |
| `FamilyAgent.CredentialProvider` | **原生 C++ / COM** | 见 §8，**不能用 C#** |
| `server/permissions.py` | Python | 权限模型 + `device.unlock` 后端校验 |
| `server/services/unlock.py` | Python | 一次性令牌签发、验证、限流、审计 |
| `tools/test_unlock.py` | Python | 10 个测试场景的自动化部分 |

### 3.2 修改

| 模块 | 改什么 |
|---|---|
| `server/db.py` | `devices` 加列；新增 `unlock_requests`；迁移 |
| `server/main.py` | 新增 `POST /api/devices/{id}/unlock`；`GET /api/devices/{id}` 补字段；WS 增加 `unlock_request` / `unlock_result` / `session_state` |
| `server/hub.py` | 增加「按设备下发帧并等待应答」的请求-应答封装（带超时） |
| `server/services/devices.py` | 记录并校验 `public_key`、`windows_state`、`capabilities` |
| `pc-agent/FamilyAgent/*` | **拆成 Service + UI 两个进程**；现有弹窗/托盘/设置逻辑整体搬进 UI |
| `web/static/app.js` + `index.html` | 设备详情加 Windows 状态与「远程解锁」按钮 |
| `.github/workflows/` | **加 C++ 构建**（MSBuild/CMake）+ 原生 DLL 签名 |

---

## 4. 数据库变更

```sql
-- devices 增列
ALTER TABLE devices ADD COLUMN windows_state TEXT NOT NULL DEFAULT 'unknown';
    -- unknown | offline | online | logon_screen | locked | unlocked
ALTER TABLE devices ADD COLUMN public_key   TEXT NOT NULL DEFAULT '';
    -- 设备公钥（PEM/SPKI）。私钥永不上行
ALTER TABLE devices ADD COLUMN capabilities TEXT NOT NULL DEFAULT '';
    -- JSON 数组：["message","screenshot","shutdown","unlock"]

-- 新增：一次性解锁请求
CREATE TABLE IF NOT EXISTS unlock_requests (
    request_id TEXT PRIMARY KEY,
    device_id  TEXT NOT NULL,
    nonce      TEXT NOT NULL,
    action     TEXT NOT NULL DEFAULT 'device.unlock',
    created_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    used_at    TEXT,
    result     TEXT,              -- success | expired | denied | replay | timeout | error
    reason     TEXT
);
CREATE INDEX IF NOT EXISTS idx_unlock_dev ON unlock_requests(device_id, created_at);

-- 新增：解锁限流计数（或直接查 unlock_requests 聚合，二选一）
CREATE TABLE IF NOT EXISTS unlock_guard (
    device_id   TEXT PRIMARY KEY,
    fail_count  INTEGER NOT NULL DEFAULT 0,
    locked_until TEXT
);
```

**审计**：直接写已有的 `events` 表，字段 `(time, device_id, request_id, action, result, reason)`，满足需求 §15.4。

> 迁移照现有 `db.py` 的模式（`PRAGMA table_info` 检查列是否存在再 ALTER），不破坏老库。

---

## 5. API 变更

### 5.1 新增

```
POST /api/devices/{device_id}/unlock      （需权限 device.unlock）
  → 校验：会话有效 + 有权限 + 设备存在 + 在线 + windows_state ∈ {logon_screen, locked} + 未限流
  → 生成 request_id / nonce，expires_at = now + 30s
  → 落库 unlock_requests
  → WS 下发 unlock_request 给该设备
  → 返回 {"ok": true, "request_id": "...", "expires_at": "..."}
  失败 → 403（无权限）/ 404（设备不存在）/ 409（离线或状态不允许）/ 429（限流）

POST /api/devices/{device_id}/unlock/{request_id}/cancel   （可选，撤销未使用的请求）
```

**结果不经 HTTP 返回** —— PC 的应答走 WS 回来，由 `device_status` / `unlock_result` 广播给网页端（现有的 `device_status` 广播机制直接复用）。

### 5.2 修改

```
GET /api/devices/{device_id}
  → 增加 windows_state、capabilities、last_unlock（可选）

GET /api/devices
  → 同上的批量字段
```

---

## 6. WebSocket 协议变更

### 6.1 NAS → PC

```jsonc
// 新增
{ "type": "unlock_request",
  "request_id": "uuid",
  "device_id": "pc_xxx",
  "action": "device.unlock",
  "nonce": "random",
  "expires_at": "2026-09-24T23:10:30+08:00" }
```

### 6.2 PC → NAS

```jsonc
// 新增：解锁结果
{ "type": "unlock_result", "request_id": "uuid",
  "status": "armed" | "success" | "failed",
  "reason": "expired|replay|not_mine|bad_action|no_credential|cp_error|timeout" }

// 新增字段：会话状态（挂在 heartbeat / hello 上，不新增帧类型）
{ "type": "heartbeat", "windows_state": "locked", "capabilities": ["message","screenshot","shutdown","unlock"] }
```

### 6.3 设备认证增强（需求 §19）

现有 token 机制**保留不动**（向后兼容），叠加挑战-应答：

```
PC 连接 → NAS 下发 {"type":"challenge","nonce":...}
PC 用私钥签名 → {"type":"auth","nonce":..., "signature":...}
NAS 用库里的 public_key 验签 → 通过才 bind_device
```

- 算法：**ECDSA P-256**（.NET `ECDsa` 原生支持，签名 64 字节，比 RSA 短）
- 上下文串：`"family-message-device-auth-v1|" + device_id + "|" + nonce`
- 老设备（无 public_key）走原 token 路径，不强制升级

---

## 7. Windows Service 方案

**目标**：开机即启、不依赖登录、崩溃自动重启、`services.msc` 可管。

```csharp
// FamilyAgent.Service — net9.0-windows
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "FamilyAgent");
builder.Services.AddHostedService<AgentWorker>();   // WS / 心跳 / IPC / 解锁授权
```

- 安装：`sc create FamilyAgent binPath= "...\FamilyAgent.Service.exe" start= auto` + `sc description`
- 账号：**LocalSystem**（读写机器范围 DPAPI、写授权文件、控制 Credential Provider 都需要）
- 恢复策略：`sc failure FamilyAgent reset= 86400 actions= restart/5000/restart/10000/restart/30000`
- 事件日志：写 Windows 事件日志 + 自己的 `%ProgramData%\FamilyAgent\service.log`

**与现状的关系**：现在 `FamilyAgent-Boot` 计划任务已经在做「开机以 SYSTEM 跑 headless」。迁移到 Service 后，这个计划任务**保留**作为兜底（Service 装不上/被禁用时不至于完全失去登录前在线能力），但两者同时跑会因为 `bind_device` 互踢 —— 所以 Service 装成功后计划任务自动停用。

---

## 8. Credential Provider 技术方案（**最需要谨慎的部分**）

### 8.1 为什么必须是原生 C++

Credential Provider 是**被 LogonUI.exe（运行在 Winlogon 安全桌面、SYSTEM 身份）进程内加载的 COM 组件**。

- LogonUI 里**没有 CLR**，C# 写的 in-proc COM 组件无法可靠加载
- 即使用 `ComVisible` + `regasm`，也会因为 .NET 运行时初始化失败而**让登录界面挂掉**
- 所以：**C++ / Win32 原生实现，不做"全 C#"的妥协**（正合需求 §26）

### 8.2 接口清单

| 接口 | 用途 |
|---|---|
| `ICredentialProvider` | `SetUsageScenario`（`CPUS_LOGON` / `CPUS_UNLOCK_WORKSTATION`）、`GetCredentialCount`、`GetCredentialAt` |
| `ICredentialProviderCredential` | `SetSelected` / `SetDeselected`、`GetSerialization`（**提交凭据的关键**） |
| `ICredentialProviderCredential2` | 关联到具体用户（登录场景必需） |
| `ICredentialProviderSetUserArray` | 拿到用户列表 |
| `ICredentialProviderFilter`（可选） | 只在需要时出现 |

### 8.3 关键设计：怎么做到"自动提交"

```
Credential Provider（在 LogonUI 里）
  1. SetUsageScenario(CPUS_LOGON | CPUS_UNLOCK_WORKSTATION) → 记住场景，否则返回 E_INVALIDARG
  2. GetCredentialCount()
       └─ 检查「一次性授权文件」是否存在且未过期
             有 → 报告 1 个可用凭据
             无 → 报告 0 个（**关键：把登录界面完全交还给系统原生 Provider**）
  3. GetSerialization()
       └─ DPAPI 解密 Windows 密码
       └─ 组装 KERB_INTERACTIVE_UNLOCK_LOGON
       └─ 返回 CPGSR_RETURN_CREDENTIAL_FINISHED（而不是 NO_CREDENTIAL）
       └─ **立即删除授权文件**（一次性语义在这里兜底）
```

### 8.4 一次性授权的传递（Service → Credential Provider）

**不用网络、不是 IPC**，用**文件 + ACL**：

```
Service 收到合法 unlock_request
  → 写 %ProgramData%\FamilyAgent\unlock.arm
       内容：{request_id, nonce, expires_at}（不含密码）
       ACL：仅 SYSTEM 可读写，其他人无权限
  → 触发 LogonUI 重新枚举凭据（WTSLogoffSession/ SendMessage 到 LogonUI，或等下一次枚举）

Credential Provider 每次 GetCredentialCount 都读这个文件
  → 存在且未过期 → 报告凭据
  → GetSerialization 后删除文件
```

**为什么这样最安全**：
- Credential Provider **完全不联网**，只读本地文件 → 攻击面最小
- 授权文件**不含密码**，只是个「现在允许自动登录一次」的开关
- 密码只在 GetSerialization 那一瞬间由 DPAPI 解出，用完即弃

### 8.5 凭据存储（**这里有个必须说清的固有事实**）

| 存储方式 | 谁能读 | 结论 |
|---|---|---|
| DPAPI **用户范围** | 仅该用户（且要加载其 profile） | ❌ **LogonUI 在登录前读不到**（此时用户 profile 还没加载） |
| DPAPI **机器范围** | SYSTEM 及任何本机管理员 | ✅ 可用，但**本机管理员能解出明文** |
| Credential Manager（`CRED_PERSIST_LOCAL_MACHINE`） | 同上 | ✅ 等价 |

> ⚠️ **必须诚实说明**：远程解锁的密码**必须能被 SYSTEM 读到**（因为 LogonUI/SYSTEM 要用它去登录）。
> 这是**固有的**，不是我们设计缺陷 —— 任何"无人值守自动登录"方案（包括微软自家的 Sysinternals Autologon，它把密码放进 LSA 加密区）都有这条性质。
>
> 我们能做的是**收窄到最小**：
> 1. 存储 **DPAPI 机器范围**
> 2. 密文文件 ACL 只给 **SYSTEM**，连普通管理员账号也不给读（但管理员可以提权绕过 —— 这是 Windows 权限模型的边界，无法根本消除）
> 3. 密码**绝不**出现在配置文件、日志、WS 帧、数据库、浏览器里
> 4. 建议开启 **BitLocker**（防离线拷贝磁盘提取）

### 8.6 账户类型支持范围（**建议第一版明确限定**）

| 账户类型 | 第一版 | 说明 |
|---|---|---|
| 本地账户 `COMPUTERNAME\user` / `.\user` | ✅ 支持 | 域字段填计算机名 |
| Microsoft 账户（MSA） | ⚠️ 尽力支持 | 域字段用 `MicrosoftAccount`；用户名是邮箱。**需要真机验证** |
| Entra ID / Azure AD | ❌ 不做 | 需要 PRT/CloudAP 参与，Credential Provider 无法单独完成 |
| 域账户（AD） | ❌ 不做 | 需要域可达 + 域策略允许 |

**UI 里要写清楚第一版只支持本地账户**，不要为了"全都支持"去做不安全的绕过（正合需求 §23）。

### 8.7 防锁死设计（**这是我最担心的部分**）

Credential Provider 在**登录链路**上，写坏了人可能**再也登不进去**。

**必做的四道保险**：

1. **Fail-safe**：任何异常/超时/未知状态 → `GetCredentialCount` 报告 **0 个凭据**，让 Windows 用原生 Provider 继续。**永不阻塞、永不抛异常穿过 COM 边界**
2. **性能硬约束**：所有入口点必须在**毫秒级**返回；读文件失败立即降级返回 0
3. **先虚拟机验证**：**必须在 VM 里跑通全部 10 个场景**，才允许装到真机
4. **一键卸载**：附带 `uninstall-cp.bat`（删注册表 + 注销 DLL），且**装机前先确认本地管理员密码可用**

**注册位置**（装机脚本）：
```
HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{自己的GUID}
  → (Default) = "FamilyAgent Remote Unlock"
HKLM\SOFTWARE\Classes\CLSID\{自己的GUID}\InprocServer32
  → (Default) = DLL 全路径
  → ThreadingModel = Apartment
```
**必须同时保留系统原生 Provider**（不做 Filter 全部拦掉），这样密码/PIN/Windows Hello 都还能用 ✓ 需求 §6。

---

## 9. 解锁完整时序（端到端）

```
① PC 开机
   → FamilyAgent.Service 启动（开机即启，不等登录）
   → WS 连 NAS + 设备认证
   → 上报 online=true, windows_state=logon_screen, capabilities=[...]
   → 网页端：设备「在线 · Windows 登录界面」

② 用户在网页点「远程解锁」（无需二次密码，已有 Web Session）
   → POST /api/devices/{id}/unlock
   → 后端：会话✓ 权限✓ 设备✓ 在线✓ 状态✓ 未限流✓
   → 生成 request_id/nonce，30 秒有效，落库
   → WS 下发 unlock_request
   → 网页端：「正在发送解锁请求…」

③ PC Service 收到
   → 校验 device_id 属于自己的？✓
   → action == "device.unlock"？✓
   → 未过期？✓
   → request_id 本地重放缓存里没有？✓（有则拒绝）
   → 写一次性授权文件（ACL: SYSTEM）
   → 回 unlock_result{status:"armed"}

④ Credential Provider 被 LogonUI 枚举
   → 读到有效授权文件 → 报告 1 个凭据
   → GetSerialization → DPAPI 解密密码 → 组装 KERB_INTERACTIVE_UNLOCK_LOGON
   → 删除授权文件 → 返回 CREDENTIAL_FINISHED
   → Windows 完成登录/解锁

⑤ Service 观察到 windows_state → unlocked
   → WS 上报 → NAS 写审计 + 广播 device_status
   → 网页端：「解锁成功」

任何一步失败 → unlock_result{status:"failed", reason} → 审计 + 网页端明确提示
```

---

## 10. 权限系统（需求 §13）

现状是「只有登录与否」，需要补最小可用的权限模型：

```python
# server/permissions.py
PERMS = ["message.send","device.view","device.screenshot",
         "device.wake","device.shutdown","device.unlock"]

# 第一版：单口令登录 = 家庭成员 = 全部权限（保持现有体验不变）
# 但**接口先建好**，后端强制检查，为将来多用户/受限角色留位置
def require(perm):
    def dep(session=Depends(WebAuth)):
        if perm not in perms_of(session): raise HTTPException(403, "无权限")
        return session
    return dep
```

- `POST /api/devices/{id}/unlock` 挂 `require("device.unlock")`
- **后端强制**，前端隐藏按钮只是体验优化（需求 §13 明确要求两端都有）
- Web 前端按 `GET /api/config` 返回的权限列表决定按钮可用性

---

## 11. 限流与审计（需求 §15）

| 项 | 设计 |
|---|---|
| 一次性 | `unlock_requests.used_at` 落库 + PC 侧本地重放缓存，**双保险** |
| 过期 | 30 秒（可配置），NAS + PC 各校验一次 |
| 限流 | 同一设备连续失败 ≥3 次 → `unlock_guard.locked_until` 锁 5 分钟；成功即清零 |
| 审计 | 写 `events` 表：时间 / device_id / request_id / action / result / reason |

---

## 12. 测试方案（需求 §28 的 10 个场景）

| # | 场景 | 预期 | 可自动化？ |
|---|---|---|---|
| 1 | PC 已登录，Web→解锁 | 409，提示"当前无需解锁" | ✅ 服务端可自动测 |
| 2 | PC 锁屏，Web→解锁 | 解锁成功，状态→unlocked | ❌ 需真机 |
| 3 | PC 停在登录界面，Web→解锁 | 自动登录成功 | ❌ 需真机 |
| 4 | PC 离线，Web→解锁 | 409"设备离线" | ✅ |
| 5 | Token 过期 | PC 拒绝，审计记 expired | ✅ 可用假 Agent |
| 6 | Token 重复使用 | 第二次拒绝 | ✅ |
| 7 | Token 属于 PC-A 发给 PC-B | PC-B 拒绝 | ✅ |
| 8 | 无 device.unlock 权限直接调 API | 403 | ✅ |
| 9 | 数据库泄露 | 全库搜不到 Windows 密码 | ✅ 脚本扫描 |
| 10 | 浏览器被查看 | 前端/localStorage 无密码 | ✅ 脚本扫描 |

**必须自动化的是 1、4、5、6、7、8、9、10**（用 `tools/test_unlock.py` + 假 Agent，不需要 Windows）。
**2、3 必须真机，且建议先在 VM 里跑。**

**Credential Provider 专项验证（装机前）**：
- VM 快照 → 装 CP → 确认原生密码/PIN/Hello 仍可用
- 无授权文件时 CP 报告 0 凭据（登录界面和没装一样）
- 授权文件过期后 CP 报告 0 凭据
- 拔掉 CP 的 DLL（模拟损坏）→ 系统仍能正常登录
- `uninstall-cp.bat` 能干净卸载

---

## 13. 分阶段实施建议

> **强烈建议分 3 阶段，把最危险的 Credential Provider 放最后。** 前面两阶段能独立交付价值、且不碰登录链路。

### Phase 1 —— 状态可见 + 一次性令牌管道（**不碰登录链路，零风险**）

- `devices` 加 `windows_state` / `capabilities`
- PC 上报 `windows_state`（`logon_screen` / `locked` / `unlocked`）
  - 检测方式：`OpenInputDesktop()` 失败 = 已锁屏；`WTSQuerySessionInformation(WTSSessionInfoEx)` 判断会话状态
- 权限模型 + `device.unlock` 后端校验
- `POST /api/devices/{id}/unlock` + 一次性令牌 + 审计 + 限流
- PC 收到 `unlock_request` 后**只校验并回复**（不真解锁），把整条链路跑通
- Web 端加 Windows 状态显示 + 「远程解锁」按钮（此阶段点了会提示"未启用本地凭据"）

**交付价值**：网页端能区分「离线 / 在线已登录 / 在线锁屏 / 在登录界面」，解锁链路的正确性、令牌一次性、限流、审计全部可验证。

### Phase 2 —— Service 化 + 凭据存储

- 拆出 `FamilyAgent.Service`（Windows Service）+ `FamilyAgent.Shared`
- UI 通过 Named Pipe 挂在 Service 上；**只有 Service 连 NAS**
- 设置页加「Windows 远程解锁凭据」配置（用户名/密码 → DPAPI 机器范围 + ACL）
- 一键「测试凭据」（用 `LogonUser` 验证，不真登录）

**交付价值**：凭据安全存储落定，Service 架构就位。**仍不碰登录链路。**

### Phase 3 —— Credential Provider（**最后做，先 VM**）

- C++ COM Credential Provider + 注册/卸载脚本
- Actions 加 C++ 构建
- VM 全场景验证 → 才上真机

---

## 14. 需要你拍板的问题

1. **是否接受分 3 阶段**？（我强烈建议；一次性上 CP 风险太大）
2. **账户类型第一版限定为「本地账户」**，可以吗？（MSA 尽力支持，Entra/域不做）
3. **Windows Service 是否必须**？现在计划任务已能"登录前在线"，Service 是增强（自动重启 + 标准管理）。做 Service 会让 Phase 2 明显变重
4. **设备密钥对（ECDSA）**是否这一轮就做？它是安全增强，但会让 WS 握手协议变更，影响现有已装设备（需要向后兼容）
5. **Credential Provider 的签名**：自签名证书可以吗？（未签名的 CP DLL 在部分环境会被 SmartScreen/策略拦；签名需要证书）

---

## 15. 我这边无法验证的部分（提前说明）

- 所有 **Windows 端行为**：NAS 上没有 .NET 也没有 Windows，CP 更是只能在 Windows 上跑
- Credential Provider 的**实际加载行为**、`GetSerialization` 是否被 LogonUI 接受
- MSA 账户的自动登录
- Service 的安装/权限/恢复策略

👉 所以 Phase 3 我建议**先在 VM 里验证**，我可以把 VM 步骤写成清单给你。
