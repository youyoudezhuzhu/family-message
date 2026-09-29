using System;
using System.Collections.Generic;
using System.Text.Json;
using FamilyAgent.Core.Config;
using FamilyAgent.Core.Diagnostics;
using FamilyAgent.Core.Nicknames;
using FamilyAgent.Core.Protocol;

namespace FamilyAgent;

/// <summary>
/// 壳 ↔ 页面的双向桥。协议**已冻结**，见 docs/PC-LOCAL-UI.md。
///
/// 约定（改代码前先读一遍）：
/// - 一律走 JSON，字段名固定；只加字段不删旧字段（前后兼容）
/// - 页面**不能**直接调用宿主能力，必须发帧；宿主收到后自己校验状态与权限
/// - 页面发来的一切都要过 <c>try/catch</c> + 类型校验，**一个坏帧不能让宿主崩**
/// - 页面是 exe 自带的本地页（<c>shell/app.html</c>），三个视图
///   <c>client</c> / <c>popup</c> / <c>settings</c>；**形态只走桥**，
///   宿主不再靠 URI 参数或重新导航来决定界面
/// - 壳模式下页面不连 <c>/ws/web</c>，消息由宿主推给它（避免双份消息）
///
/// 本类刻意不引用 WPF / WebView2 类型：宿主用 <see cref="Sender"/> 注入「怎么把 JSON
/// 送进页面」，其余全是纯 C#。这样桥能单独看懂、也不受 WebView2 初始化时序影响。
/// </summary>
public sealed class JsBridge
{
    // ── 宿主 → 页面 ──────────────────────────────────────────────

    /// <summary>
    /// 宿主把一条 JSON 投给页面的方式（WebHostWindow 注入 WebView2 的
    /// PostWebMessageAsJson）。为 null 时所有推送静默丢弃 —— 页面还没起来。
    /// </summary>
    public Action<string>? Sender { get; set; }

    /// <summary>
    /// 页面当前视图（"client" / "popup" / "settings"），由宿主在切换形态时更新。
    ///
    /// ⚠ 这是**页面视图**，不是窗口形态（窗口形态在 WebHostWindow._mode）。
    /// 两者各管各的：窗口可以是普通窗口而视图是 settings，反之亦然。
    /// </summary>
    public string Mode { get; set; } = "client";

    /// <summary>
    /// 页面是否已经就绪（收到过正式页面的 <c>web.ready</c>）。
    ///
    /// ⚠ 由 WebHostWindow 置位/复位，不要在别处改：本地 boot / offline 页也会发
    /// web.ready，但**那个文档马上会被正式页面取代**，把它当成就绪会导致消息被推
    /// 进一个随即销毁的文档里丢掉。宿主只在「阶段 = 正式页面」时才置 true。
    /// </summary>
    public bool PageReady { get; set; }

    public void PostHello()
    {
        var cfg = App.Config;
        var names = cfg is null ? new List<string>() : new List<string>(cfg.ReplyNames);
        Send(new
        {
            type = "host.hello",
            mode = Mode,
            version = ProtocolVersion.AgentVersion,
            platform = "Windows " + Environment.OSVersion.Version,
            server = cfg is null ? "" : cfg.ServerUrl,
            theme_mode = cfg is null || string.IsNullOrWhiteSpace(cfg.ThemeMode) ? "system" : cfg.ThemeMode,
            // 昵称列表是纯本地设置，页面照着 PC 这份来（协议「只加字段」，这是新增字段）
            reply_names = names,
            reply_name = cfg is null ? "" : cfg.ReplyName,
            // 页面要拿它去 GET /api/conversations/{device_id} 拉这台机器的往来记录 ——
            // 客户端视图只关心「我这台机器」的会话，不是整个家庭的消息
            device_id = cfg is null ? "" : cfg.DeviceId,
            device_name = cfg is null ? "" : cfg.DeviceName,
            // ★ 新增（docs/PC-LOCAL-UI.md）：设置视图要拿它决定表单的初始值 ——
            //   注册口令配过没有、开机自启开着没有（口令本身**不**过桥，这是本机机密）
            enroll_configured = cfg is not null && !string.IsNullOrWhiteSpace(cfg.EnrollToken),
            autostart = cfg is not null && cfg.AutoStart,
        });
    }

    public void PostConnection(bool connected, string detail) =>
        Send(new { type = "host.connection", connected, detail = detail ?? "" });

    /// <summary>
    /// 推一条消息给页面。
    ///
    /// 服务端帧的字段**原样透传**（history / auto_close_seconds / message_type /
    /// redelivered …），另外按协议 §4 补上 id / device_id / status 三个字段名 ——
    /// message_id 与 id 同时给，页面用哪个都能对上，web.ack 回哪个都能匹配。
    /// </summary>
    public void PostMessage(JsonElement message, string? deviceId)
    {
        var payload = new Dictionary<string, JsonElement>();
        if (message.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in message.EnumerateObject())
                payload[prop.Name] = prop.Value;
        }

        long id = 0;
        if (payload.TryGetValue("message_id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
        {
            try { id = idEl.GetInt64(); } catch { id = 0; }
        }

        payload["id"] = JsonSerializer.SerializeToElement(id);
        if (!payload.ContainsKey("message_id"))
            payload["message_id"] = JsonSerializer.SerializeToElement(id);
        payload["device_id"] = JsonSerializer.SerializeToElement(deviceId ?? "");
        payload["status"] = JsonSerializer.SerializeToElement("device_received");

        Send(new { type = "host.message", message = payload });
    }

    /// <summary>
    /// 回复的结果回给页面。status：<c>ok</c> / <c>empty</c> / <c>error</c>，
    /// 外加一个 <c>queued</c>（没连上服务器但已入队，等重连自动补发，不算失败）。
    /// </summary>
    public void PostReplyAck(string? clientId, string status, long messageId, string detail) =>
        Send(new
        {
            type = "host.reply_ack",
            client_id = clientId ?? "",
            status = status ?? "error",
            message_id = messageId,
            detail = detail ?? "",
        });

    public void PostSession() =>
        Send(new
        {
            type = "host.session",
            windows_state = SessionState.Current,
            can_unlock = CanUnlock,
            can_screenshot = !App.IsHeadless,
            can_shutdown = !App.IsHeadless,
        });

    /// <summary>
    /// 设置页的凭据操作：<c>status</c> 只读状态；<c>save</c> 先校验再落盘；
    /// <c>test</c> 只校验不落盘；<c>clear</c> 删凭据。结果一律回 <c>host.unlock_credential</c>。
    ///
    /// ⚠ 口令只在这一个方法栈里存在：不写日志、不进审计、不回显（回给页面的只有状态事实）。
    /// </summary>
    private void HandleUnlockCredential(string action, JsonElement root)
    {
        var creds = Platform.UnlockCredentials.Current;
        var user = GetString(root, "user") ?? "";
        var secret = GetString(root, "secret") ?? "";

        var ok = true;
        var detail = "";
        switch (action)
        {
            case "status":
                break;
            case "save":
                (ok, detail) = creds.Save(user, secret);
                break;
            case "test":
                (ok, detail) = creds.Test(user, secret);
                break;
            case "clear":
                ok = creds.Clear();
                detail = ok ? "已清除凭据。" : "本机本来就没有凭据。";
                break;
            default:
                ok = false;
                detail = "未知的凭据操作：" + action;
                break;
        }

        PostUnlockCredential(action, ok, detail);
    }

    /// <summary>把凭据状态推给页面（**只有事实：配了没 / 谁 / 什么时候验的 / 冷却没**）。</summary>
    private void PostUnlockCredential(string action, bool ok, string detail)
    {
        var s = Platform.UnlockCredentials.Current.Status();
        Send(new
        {
            type = "host.unlock_credential",
            action = action ?? "",
            ok,
            detail = detail ?? "",
            configured = s.Configured,
            user = s.User ?? "",
            verified_at = s.VerifiedAt ?? "",
            locked_out = s.LockedOut,
            failures_left = s.FailuresLeft,
            can_unlock = CanUnlock,
        });
    }

    /// <summary>
    /// 本机是否具备「真正解锁」的能力（= 凭据已配好且未被失败冷却锁住）。
    ///
    /// ⚠ 与 Core 的 <c>DeviceCapabilities</c> **必须同源**（都读 <c>UnlockCredentials.Ready</c>）：
    ///   一个说能、一个说不能，页面上就会出现"点了必然失败"的按钮。
    ///   Phase 2 起不再是写死的 false —— 用户在设置页存好凭据后即可为 true
    ///   （真正把锁解开仍要等 Phase 3 的 Credential Provider）。
    /// </summary>
    private static bool CanUnlock => Platform.UnlockCredentials.Current.Ready;

    /// <summary><paramref name="dataUrl"/> 为空 = 截图失败，改回 <c>ok:false</c> + 中文原因。</summary>
    public void PostScreenshot(string? requestId, string? dataUrl, string? error)
    {
        if (string.IsNullOrEmpty(dataUrl))
        {
            Send(new
            {
                type = "host.screenshot",
                request_id = requestId ?? "",
                ok = false,
                error = string.IsNullOrEmpty(error) ? "截图失败" : error,
            });
            return;
        }

        Send(new
        {
            type = "host.screenshot",
            request_id = requestId ?? "",
            ok = true,
            data_url = dataUrl,
        });
    }

    public void PostActionResult(string action, bool ok, string detail) =>
        Send(new { type = "host.action_result", action = action ?? "", ok, detail = detail ?? "" });

    public void PostMode() => Send(new { type = "host.mode", mode = Mode });

    /// <summary>
    /// 设置视图要的运行时信息（docs/PC-LOCAL-UI.md「新增：设置视图要读的运行时信息」）。
    ///
    /// 进设置视图时发一次，配置变化后宿主主动再推一次。页面上「版本 / 当前形态 /
    /// 设备号 / 日志与配置路径」这几个只读项就是拿它填的 ——
    /// 需求：界面上要能直接看到版本号，省得再出现「跑的是哪一版」的困惑。
    /// </summary>
    public void PostRuntime(string? runtimeVersion)
    {
        var cfg = App.Config;
        Send(new
        {
            type = "host.runtime",
            mode = Mode,          // 当前形态（client / popup / settings），界面上要显示它
            version = ProtocolVersion.AgentVersion,
            runtime = runtimeVersion ?? "",   // WebView2 Runtime 版本
            platform = "Windows" + " " + Environment.OSVersion.Version,   // 页面「平台」一行读它
            device_id = cfg is null ? "" : cfg.DeviceId,
            device_name = cfg is null ? "" : cfg.DeviceName,
            server = cfg is null ? "" : cfg.ServerUrl,
            enroll_configured = cfg is not null && !string.IsNullOrWhiteSpace(cfg.EnrollToken),
            autostart = cfg is not null && cfg.AutoStart,
            theme_mode = cfg is null || string.IsNullOrWhiteSpace(cfg.ThemeMode) ? "system" : cfg.ThemeMode,
            log_path = AgentLog.FilePath,
            config_path = AgentConfig.FilePath,
        });
    }

    /// <summary>
    /// 配置保存回执。<paramref name="ok"/> 为 false 时 <paramref name="detail"/> 必须说清原因
    /// （例如「开机自启没能注册：建 SYSTEM 计划任务需要管理员权限」），页面照原话提示用户。
    /// </summary>
    public void PostConfigSaved(bool ok, string detail) =>
        Send(new { type = "host.config_saved", ok, detail = detail ?? "" });

    /// <summary>
    /// 服务端发来的历史对话转给页面渲染。协议 §4 里没有这条 —— 属于**新增帧**
    /// （只加不删），页面不认识就忽略；壳模式下页面不连 /ws/web，有它才能省掉一次往返。
    /// </summary>
    public void PostHistory(JsonElement messages)
    {
        if (messages.ValueKind != JsonValueKind.Array)
            return;
        Send(new { type = "host.history", messages });
    }

    /// <summary>
    /// ★ 推一份**共享昵称的完整状态**给页面（docs/NICKNAME-SYSTEM-PLAN.md §7 Phase 4「新增桥帧」）。
    ///
    /// 壳模式下页面**不连** <c>/ws/web</c>，昵称的整表 / 广播一律由宿主经桥送过来 ——
    /// 没有这条帧，PC 本地页就收不到别人的颜色变化（§5.6.2 第 2 点）。
    ///
    /// 一趟给全（页面免刷新重画只需要这一帧）：
    /// <list type="bullet">
    ///   <item><c>available</c>：服务端**有没有应答过**昵称帧（据此决定设置页走昵称模式还是
    ///     走今天的本地昵称回退路径 —— 服务端 <c>nickname.enabled=false</c> 时它一直是 false，
    ///     页面行为与改造前逐字一致）。</item>
    ///   <item><c>online</c> / <c>can_manage</c>：能否做那四个管理操作（离线时页面禁用入口
    ///     并明示 <c>offline_reason</c>，§5.2）。</item>
    ///   <item><c>current</c>：本机当前选用（灰临时 = <c>nickname_id</c> 为 null + <c>color</c>
    ///     为 <c>gray</c>）。**PC 不「拥有」昵称**，这只是「我当前用谁的名义」。</item>
    ///   <item><c>nicknames</c>：**全量**活跃昵称（弹窗右侧要给别人的消息上色，没有全量就只能退回名字哈希）。</item>
    /// </list>
    ///
    /// ⚠ 载荷里**只有逻辑色 ID**，一个 HEX 都没有（§4.4）：显示色由页面自己按主题算。
    /// </summary>
    public void PostNickname(NicknameService nicknames)
    {
        if (nicknames is null)
            return;

        var rows = new List<object>();
        foreach (var n in nicknames.Table)
        {
            rows.Add(new
            {
                nickname_id = n.NicknameId,
                display_name = n.DisplayName,
                color = n.Color,              // 逻辑色 ID（color_01…color_16）
                status = n.Status,
                created_at = n.CreatedAt,
                updated_at = n.UpdatedAt,
            });
        }

        var colorRows = new List<object>();
        foreach (var c in nicknames.ColorTable)
            colorRows.Add(new { color_id = c.ColorId, hex = c.Hex });   // ★ v0.19 权威色表

        var current = nicknames.Current;
        Send(new
        {
            type = "host.nickname",
            // 服务端是否应答过（开关关闭 → 一直是 false → 页面走回退路径，现有行为不变）
            available = nicknames.Available,
            online = nicknames.Online,
            can_manage = nicknames.CanManage,
            offline_reason = NicknameService.OfflineMessage,
            default_name = nicknames.DefaultName,             // 本机名（灰临时昵称用它）
            local_temp_color = NicknameColor.LocalTempColorId, // "gray"
            max_active = NicknameService.MaxActive,            // 16（额度提示用）
            pool_version = nicknames.PoolVersion,
            color_table = colorRows,
            current = new
            {
                nickname_id = current.NicknameId,              // null = 灰临时（还没选）
                display_name = current.DisplayName,
                color = current.Color,                         // 逻辑色 ID；灰临时 = "gray"
                is_local_temp = current.IsLocalTemp,
            },
            nicknames = rows,
            // 「你用的昵称已被删除，已切回本地临时昵称」这类一次性提示；读过就没了
            notice = nicknames.TakeNotice(),
        });
    }

    /// <summary>
    /// 一次昵称操作的**结果**回给页面（页面据此提示「已发出，等待服务端应答…」/「离线不可用」）。
    ///
    /// ⚠ 这里回的是「**请求被接受了吗**」，不是「改成功了吗」：
    ///   真正的成功判据是服务端回的那一帧（<see cref="PostNickname"/> 里整表变了），
    ///   或者 <see cref="PostNicknameError"/> 里的错误码。宿主**绝不乐观地宣布成功**。
    /// </summary>
    public void PostNicknameResult(string action, bool accepted, bool offline, string detail,
                                   long? existingNicknameId = null)
    {
        Send(new
        {
            type = "host.nickname_result",
            action = action ?? "",
            accepted,
            offline,
            detail = detail ?? "",
            existing_nickname_id = existingNicknameId,
        });
    }

    /// <summary>
    /// 服务端回了一个 <c>nickname_error</c>（撞名 / 池满 / 找不到…）。
    ///
    /// <paramref name="existingNicknameId"/> 只在**创建撞名**
    /// （<c>NICKNAME_ALREADY_EXISTS</c>）时出现 —— 页面拿它做「已存在，直接选用它？」（§3.3 方式 A）。
    /// </summary>
    public void PostNicknameError(string request, string code, string message, long? existingNicknameId)
    {
        Send(new
        {
            type = "host.nickname_error",
            request = request ?? "",
            code = code ?? "",
            message = message ?? "",
            existing_nickname_id = existingNicknameId,
        });
    }

    // ── 页面 → 宿主 ──────────────────────────────────────────────

    /// <summary>页面已就绪（宿主这时候才发 hello / session / connection）.</summary>
    public event Action? ReadyReceived;

    /// <summary>消息真的画到屏幕上了 → 宿主才回报 <c>popup_displayed</c>（参数 message_id）。</summary>
    public event Action<long>? AckReceived;

    /// <summary>页面要发回复：(昵称, 内容, client_id)。</summary>
    public event Action<string, string, string>? ReplyReceived;

    /// <summary>页面点「知道了」→ 宿主隐藏窗口（并把未读算已读）。</summary>
    public event Action? CloseRequested;

    /// <summary>页面要截图（参数 request_id）。</summary>
    public event Action<string>? ScreenshotRequested;

    /// <summary>页面要执行动作：(action, device_id)。</summary>
    public event Action<string, string>? ActionRequested;

    /// <summary>页面要求切形态（"popup" / "client" / "settings"）。</summary>
    public event Action<string>? ModeRequested;

    /// <summary>
    /// 页面要求打开设置视图（顶栏齿轮发 <c>web.open_settings</c>）——
    /// 与托盘菜单的「设置…」是同一条路：宿主切形态、不再重新导航。
    /// </summary>
    public event Action? SettingsRequested;

    /// <summary>
    /// 页面要保存本机设置（<c>web.save_config</c>）。只会带上真正要改的字段，
    /// 落盘 / 重连 / 自启由宿主来做（页面不碰文件与注册表）。
    /// </summary>
    public event Action<ConfigPatch>? ConfigSaveRequested;

    /// <summary>兜底页保存服务端配置：(url, enroll_token)。</summary>
    public event Action<string, string>? ServerChanged;

    /// <summary>页面要求退出程序。</summary>
    public event Action? QuitRequested;

    /// <summary>
    /// 页面发来的一条**昵称请求**（docs/NICKNAME-SYSTEM-PLAN.md §7 Phase 4 的页面 → 宿主方向）。
    ///
    /// 六种 <see cref="NicknameRequest.Kind"/>：
    /// <c>select</c>（选用 / 切回灰临时，**唯一能离线的**）/ <c>create</c> / <c>rename</c> /
    /// <c>reassign_color</c> / <c>delete</c> / <c>refresh</c>。
    /// <c>create</c> 撞名（409）时宿主回 <c>host.nickname_error</c>，
    /// 页面据 <c>existing_nickname_id</c> 引导「已存在，直接选用它？」。
    /// </summary>
    public event Action<NicknameRequest>? NicknameRequested;

    /// <summary>
    /// 处理一帧来自页面的消息。**永远不抛异常**：坏帧只写日志。
    ///
    /// <paramref name="raw"/> 是 WebView2 的 <c>WebMessageAsJson</c>：页面
    /// <c>postMessage(obj)</c> 时是对象 JSON，<c>postMessage("字符串")</c> 时是带引号的
    /// JSON 字符串字面量 —— 后者要解开一层（所以有 depth）。
    /// </summary>
    public void HandleFromWeb(string? raw) => HandleFromWeb(raw, 0);

    private void HandleFromWeb(string? raw, int depth)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return;
        if (depth > 2)
        {
            AgentLog.Write("✗ 桥：页面消息套了太多层字符串（已忽略）");
            return;
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(raw!);
            root = doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            AgentLog.Write("✗ 桥：页面发来的不是合法 JSON（已忽略）：" + ex.Message);
            return;
        }

        if (root.ValueKind == JsonValueKind.String)
        {
            HandleFromWeb(root.GetString(), depth + 1);
            return;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            AgentLog.Write("✗ 桥：页面消息不是 JSON 对象（已忽略）");
            return;
        }

        var type = GetString(root, "type");
        if (string.IsNullOrEmpty(type))
        {
            AgentLog.Write("✗ 桥：页面消息缺 type（已忽略）");
            return;
        }

        try
        {
            switch (type)
            {
                case "web.ready":
                    ReadyReceived?.Invoke();
                    break;

                case "web.ack":
                {
                    var id = GetLong(root, "message_id");
                    if (id <= 0)
                    {
                        AgentLog.Write("✗ 桥：web.ack 缺有效 message_id（已忽略）");
                        return;
                    }
                    AckReceived?.Invoke(id);
                    break;
                }

                case "web.reply":
                {
                    var content = GetString(root, "content");
                    if (string.IsNullOrEmpty(content))
                    {
                        AgentLog.Write("✗ 桥：web.reply 内容为空（已忽略）");
                        return;
                    }
                    var clientId = GetString(root, "client_id");
                    if (string.IsNullOrWhiteSpace(clientId))
                        clientId = Guid.NewGuid().ToString("N")[..12];
                    ReplyReceived?.Invoke(GetString(root, "sender_name"), content, clientId);
                    break;
                }

                case "web.close":
                    CloseRequested?.Invoke();
                    break;

                case "web.request_screenshot":
                {
                    var rid = GetString(root, "request_id");
                    if (string.IsNullOrWhiteSpace(rid))
                    {
                        AgentLog.Write("✗ 桥：web.request_screenshot 缺 request_id（已忽略）");
                        return;
                    }
                    ScreenshotRequested?.Invoke(rid);
                    break;
                }

                case "web.request_action":
                {
                    var action = GetString(root, "action");
                    if (string.IsNullOrWhiteSpace(action))
                    {
                        AgentLog.Write("✗ 桥：web.request_action 缺 action（已忽略）");
                        return;
                    }
                    ActionRequested?.Invoke(action, GetString(root, "device_id"));
                    break;
                }

                case "web.switch_mode":
                {
                    var mode = (GetString(root, "mode") ?? "").Trim().ToLowerInvariant();
                    // 本地页的三个视图：client（消息界面）/ popup（全屏强提醒）/
                    // settings（本机设置）。PC 端没有"控制台"视图可切了 ——
                    // 完整网页控制台由托盘的「打开控制台」用系统浏览器打开。
                    if (mode != "popup" && mode != "client" && mode != "settings")
                    {
                        AgentLog.Write($"✗ 桥：web.switch_mode 的 mode 不认识（{mode}，已忽略）");
                        return;
                    }
                    ModeRequested?.Invoke(mode);
                    break;
                }

                case "web.open_settings":
                {
                    // 顶栏齿轮 → 打开设置；页面点「返回」时补一个 open:false。
                    //
                    // ⚠ 「返回」也必须过桥：视图是宿主说了算的（形态只走桥），
                    //   宿主还停在 settings 的话，回发一次 host.mode 就把页面又推回设置页
                    //   —— 用户会发现「返回按钮点了没用」。
                    if (GetBool(root, "open") ?? true)
                        SettingsRequested?.Invoke();
                    else
                        ModeRequested?.Invoke("client");   // 关设置 = 回到消息界面
                    break;
                }

                case "web.save_config":
                {
                    // 只处理**传了的**字段：没传 = 不改（不是改成空）
                    var patch = new ConfigPatch
                    {
                        ServerUrl = GetStringOrNull(root, "server_url"),
                        EnrollToken = GetStringOrNull(root, "enroll_token"),
                        ReplyName = GetStringOrNull(root, "reply_name"),
                        AutoStart = GetBool(root, "autostart"),
                        ThemeMode = GetStringOrNull(root, "theme_mode"),
                    };
                    if (patch.IsEmpty)
                    {
                        AgentLog.Write("✗ 桥：web.save_config 一个字段都没带（已忽略）");
                        return;
                    }
                    ConfigSaveRequested?.Invoke(patch);
                    break;
                }

                case "web.set_server":
                {
                    var url = GetString(root, "url");
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        AgentLog.Write("✗ 桥：web.set_server 缺 url（已忽略）");
                        return;
                    }
                    ServerChanged?.Invoke(url, GetString(root, "enroll_token"));
                    break;
                }

                case "web.quit":
                    QuitRequested?.Invoke();
                    break;

                // ── 远程解锁凭据（Phase 2）：状态 / 保存 / 测试 / 清除 ──
                case "web.unlock_status":
                case "web.unlock_save":
                case "web.unlock_test":
                case "web.unlock_clear":
                {
                    HandleUnlockCredential(type.Substring("web.unlock_".Length), root);
                    break;
                }

                // ── 共享昵称（§7 Phase 4）。**形态只走桥**，昵称也走桥 ──
                case "web.nickname_select":
                case "web.nickname_create":
                case "web.nickname_rename":
                case "web.nickname_reassign_color":
                case "web.nickname_delete":
                case "web.nickname_refresh":
                {
                    var req = new NicknameRequest
                    {
                        Kind = type.Substring("web.nickname_".Length),
                        // 「传了 null」是**有意义**的输入（= 回灰临时），
                        // 所以要和「根本没传这个字段」分开（后者是坏帧）：
                        // 这里只判**字段在不在**；值是不是 null 交给 Validate() 解释。
                        // （之前把显式 null 也当成「没传」，结果「切回灰临时」会被当坏帧丢掉。）
                        HasNicknameId = root.TryGetProperty("nickname_id", out _),
                        NicknameId = GetLong(root, "nickname_id"),
                        DisplayName = GetString(root, "display_name"),
                    };

                    var problem = req.Validate();
                    if (problem is not null)
                    {
                        AgentLog.Write($"✗ 桥：{type} 不合法（{problem}，已忽略）");
                        return;
                    }

                    NicknameRequested?.Invoke(req);
                    break;
                }

                default:
                    AgentLog.Write($"✗ 桥：未知帧类型 {type}（已忽略）");
                    break;
            }
        }
        catch (Exception ex)
        {
            // 兜底：处理器里出的任何问题都不该把宿主带崩
            AgentLog.Write($"✗ 桥：处理 {type} 出错（已忽略，宿主继续运行）：{ex}");
        }
    }

    // ── 内部 ────────────────────────────────────────────────────

    private void Send<T>(T payload)
    {
        var sender = Sender;
        if (sender is null)
            return;

        // 页面没就绪就别推：正式页面加载完成后宿主会全量重推一次
        // （hello / connection / session / mode + 缓存的消息）
        if (!PageReady)
            return;

        try
        {
            sender(JsonSerializer.Serialize(payload));
        }
        catch (Exception ex)
        {
            AgentLog.Write("✗ 桥：投递失败：" + ex.Message);
        }
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? ""
            : "";

    /// <summary>
    /// 取可选字符串字段：**没传**返回 null（= 不改这个字段），传了空串返回 ""（= 用户清空了）。
    /// 这就是 web.save_config「只处理传了的字段」的落点，别用 <see cref="GetString"/>
    /// 代替 —— 它把「没传」和「传了空」压成同一种结果。
    /// </summary>
    private static string? GetStringOrNull(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    /// <summary>取可选 bool 字段：没传或类型不对都返回 null（= 不改）。</summary>
    private static bool? GetBool(JsonElement root, string name)
    {
        // 写成语句体而不是三元表达式：三元 + is 模式优先级看着容易多想，
        // 这里要的语义就一句话 —— 没传/类型不对 = null，否则就是那个 bool。
        if (!root.TryGetProperty(name, out var el))
            return null;
        if (el.ValueKind == JsonValueKind.True)
            return true;
        if (el.ValueKind == JsonValueKind.False)
            return false;
        return null;
    }

    private static long GetLong(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
            return 0;
        try { return el.GetInt64(); }
        catch { return 0; }   // 小数这个接口会抛，别让它带崩调用方
    }
}

/// <summary>
/// 页面在 <c>web.save_config</c> 里要求改的本机设置。
///
/// **每个字段都是「可空 = 不改」**：null 表示页面没带这一项，宿主必须保持原值
/// （不能当成"清空"）。空串是有意义的输入（用户把输入框删空了），
/// 由宿主决定怎么解释 —— 判空、拒绝、保留原值都行，但别静默当没传。
///
/// 本类刻意不引用 WPF / WebView2：桥照旧是纯 C#，能单独看懂。
/// </summary>
public sealed class ConfigPatch
{
    /// <summary>服务端地址（http/ws 都行，宿主会规范化）。</summary>
    public string? ServerUrl { get; set; }

    /// <summary>注册口令。**不会**回传给页面（host.hello 只报配没配过）。</summary>
    public string? EnrollToken { get; set; }

    /// <summary>回复昵称（自由输入，不在列表里由宿主加进去）。</summary>
    public string? ReplyName { get; set; }

    /// <summary>开机自启。</summary>
    public bool? AutoStart { get; set; }

    /// <summary>明暗模式：system / light / dark。</summary>
    public string? ThemeMode { get; set; }

    /// <summary>一个字段都没带 —— 桥直接忽略，宿主不必处理。</summary>
    public bool IsEmpty =>
        ServerUrl is null && EnrollToken is null && ReplyName is null
        && AutoStart is null && ThemeMode is null;
}

/// <summary>
/// 页面发来的一次**昵称请求**（<c>web.nickname_*</c>，docs/NICKNAME-SYSTEM-PLAN.md §7 Phase 4）。
///
/// 六个 <see cref="Kind"/> 对应页面那六个帧（桥已把 <c>web.nickname_</c> 前缀剥掉）：
/// <list type="bullet">
///   <item><c>select</c>：选用某个共享昵称 / 切回灰临时（**唯一能离线的**）。</item>
///   <item><c>refresh</c>：主动拉一次整表（启动 / 重连 / 点「刷新」）。</item>
///   <item><c>create</c>：新建共享昵称（撞名 → 服务端 409，UI 引导「直接选用」）。</item>
///   <item><c>rename</c>：改**共享昵称本身**的名字（全局生效）。</item>
///   <item><c>reassign_color</c>：换一个未被占用的逻辑色 ID（全局生效）。</item>
///   <item><c>delete</c>：删除共享昵称（软删，颜色立即回池）。</item>
/// </list>
///
/// ★ <see cref="HasNicknameId"/> 与 <see cref="NicknameId"/> 是**两回事**：
///   「<c>select</c> 时显式传 <c>null</c>」是**有意义**的输入（= 切回灰临时），
///   必须和「这一帧根本没带 <c>nickname_id</c> 字段」区分开 —— 后者是坏帧，直接忽略。
///   桥在构造时已经把这个区分算好放在 <see cref="HasNicknameId"/> 里。
///
/// ⚠ 本类**不引用 WPF / WebView2**，和 <see cref="JsBridge"/> 一样是纯 C#（桥能单独看懂）。
///   这里只做**帧形状**校验；名字的 1–32 字符 / 控制字符等业务校验由
///   <c>NicknameService.ValidateName</c> 负责（服务端才是权威，§5.5）。
/// </summary>
public sealed class NicknameRequest
{
    /// <summary>操作种类：<c>select</c> / <c>refresh</c> / <c>create</c> / <c>rename</c> /
    /// <c>reassign_color</c> / <c>delete</c>（由帧名 <c>web.nickname_*</c> 派生）。</summary>
    public string Kind { get; set; } = "";

    /// <summary>
    /// 页面**有没有带** <c>nickname_id</c> 这个字段（带了 null 也算带了）。
    /// 见类注释：<c>select</c> 靠它区分「切回灰临时」与「坏帧」。
    /// </summary>
    public bool HasNicknameId { get; set; }

    /// <summary>目标共享昵称 id（<see cref="HasNicknameId"/> 为 false 时无意义，恒 0）。</summary>
    public long NicknameId { get; set; }

    /// <summary>要创建 / 改成的名字（<c>create</c> / <c>rename</c> 用；其余种类忽略）。</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>这个 <paramref name="kind"/> 认不认识（六个之一）。</summary>
    public static bool IsKnownKind(string? kind) =>
        kind is "select" or "refresh" or "create" or "rename" or "reassign_color" or "delete";

    /// <summary>
    /// 校验这一帧**形状**是否成立。返回中文错误文案；<c>null</c> = 通过。
    ///
    /// 规则（§7 Phase 4 的帧定义）：
    /// <list type="bullet">
    ///   <item><c>select</c>：必须带 <c>nickname_id</c> 字段（值可以是 null = 回灰临时）。</item>
    ///   <item><c>refresh</c>：不看任何字段。</item>
    ///   <item><c>create</c> / <c>rename</c>：<c>display_name</c> 不能为空（去空白后）。</item>
    ///   <item><c>reassign_color</c> / <c>delete</c>：<c>nickname_id &gt; 0</c>。</item>
    /// </list>
    /// </summary>
    public string? Validate()
    {
        if (!IsKnownKind(Kind))
            return $"不认识的昵称操作「{Kind}」";

        switch (Kind)
        {
            case "select":
                // 显式传 null = 切回灰临时（有意义）；连字段都没有 = 坏帧。
                return HasNicknameId ? null : "select 必须带 nickname_id 字段（切回灰临时请显式传 null）";

            case "refresh":
                return null;

            case "create":
            case "rename":
                return string.IsNullOrWhiteSpace(DisplayName) ? "昵称不能为空" : null;

            case "reassign_color":
            case "delete":
                return NicknameId > 0 ? null : "缺少有效的 nickname_id";

            default:
                return $"不认识的昵称操作「{Kind}」";
        }
    }
}
