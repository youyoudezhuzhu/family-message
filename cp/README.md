# FamilyAgentCp —— 家庭消息远程解锁 Credential Provider（Phase 3）

这个目录是**真正把锁解开**的那一环。设计、安全边界、回滚、验证清单见
[`docs/REMOTE-UNLOCK-PLAN.md`](../docs/REMOTE-UNLOCK-PLAN.md) §18。

## 它是什么

一个原生 C++ DLL，由 Windows 的 **LogonUI.exe 以 SYSTEM 身份**加载到登录界面（安全桌面）里。
- **平时完全隐形**：没有待解锁请求时返回 **0 个凭据**，锁屏界面跟你没装过一模一样；
- **只有被点名时才动**：PC 端 Agent 收到服务端的 `unlock_request`、并且本机装了它、凭据也配好了，
  就会写一张**一次性** `unlock-arm.json`（带服务端签发的 nonce、120 秒有效）；
  CP 的工作线程看到它 → 通知 LogonUI 重枚举 → 出现一个凭据并**自动提交** → 会话真正解锁。

## 编译（NAS 上没有 MSVC，所以只在 CI 编）

- CI：`.github/workflows/build-cp.yml`（推 `cp/**` 就编，产物存 artifact）
- Windows 打包时也会编一遍并塞进 `FamilyAgent-win-x64.zip` 的 `cp\` 目录
- 本地想编：在 **x64 Native Tools 命令提示符**里执行 `build.bat`

```
cp\build.bat          →  cp\out\FamilyAgentCp.dll
```

## 装 / 卸 / 自检（用 Agent 本体，都要管理员）

```
FamilyAgent.exe --cp-selftest      ← 先跑这个！只验"读凭据 → DPAPI 解密 → 打包"，不碰登录界面
FamilyAgent.exe --install-cp       ← 拷到 %ProgramFiles%\FamilyAgent\ + 写两处注册表
FamilyAgent.exe --uninstall-cp     ← 删注册表两项 + 删 DLL（一条命令回到没装过）
```

结果同时写：`%ProgramData%\FamilyAgent\cp-cmd-last.txt`、`cp.log`，交互式下还会弹框。

## 排障

| 现象 | 看哪儿 |
|---|---|
| 网页端点解锁，PC 回 `cp_missing` | 还没 `--install-cp`（或注册表被清） |
| 回 `cp_error` | arm 凭证写不下去（权限/磁盘），日志里有原话 |
| 回 `armed` 但要等很久才 `success` | CP 在轮询（500ms）+ LogonUI 重枚举，正常几百毫秒内 |
| `armed` 之后 `failed/timeout` | CP 没消费（没装 / 没加载 / 凭据解不开）→ 看 `cp.log` |
| 登录界面异常 | **回滚**：`FamilyAgent.exe --uninstall-cp`；进不去系统就用安全模式/安装盘删 §18.5 里那两个注册表键 |

## 硬规矩（改代码前先读）

1. **fail-closed**：每个 COM 方法都 `__try/__except`，任何异常/超时都退回"0 个凭据"——
   绝不让 LogonUI 卡住或抛（那会导致登录界面异常，是最坏后果）。
2. **不落明文**：口令只在内存里，用完 `SecureZeroMemory`；审计日志**不含**口令。
3. **一次性**：arm 凭证消费即删，失败也删（不反复试，免得把账户打到锁定）。
4. `__try` 不能和需要析构的 C++ 对象同帧（C2712）—— 干活儿的函数分开写。
5. 与 C# 侧共享的常量（GUID / 路径 / 熵 / arm 字段名）改一处必须改两处，
   Core 的单测 `UnlockArmingTests` 钉着 arm 文件格式这一条。
