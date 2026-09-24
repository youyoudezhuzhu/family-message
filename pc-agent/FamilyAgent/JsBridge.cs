using System;
using System.Collections.Generic;
using System.Text.Json;

namespace FamilyAgent;

/// <summary>
/// 壳 ↔ 页面的双向桥。协议**已冻结**，见 docs/PC-WEBVIEW2-REWRITE.md §4。
///
/// 约定（改代码前先读一遍）：
/// - 一律走 JSON，字段名固定；只加字段不删旧字段（前后兼容）
/// - 页面**不能**直接调用宿主能力，必须发帧；宿主收到后自己校验状态与权限
/// - 页面发来的一切都要过 <c>try/catch</c> + 类型校验，**一个坏帧不能让宿主崩**
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

    /// <summary>当前窗口形态（"popup" / "console"），由宿主在切换形态时更新。</summary>
    public string Mode { get; set; } = "popup";

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
            version = AgentClient.ReportedVersion,
            platform = "Windows " + Environment.OSVersion.Version,
            server = cfg is null ? "" : cfg.ServerUrl,
            theme_mode = cfg is null || string.IsNullOrWhiteSpace(cfg.ThemeMode) ? "system" : cfg.ThemeMode,
            // 昵称列表是纯本地设置，页面照着 PC 这份来（协议「只加字段」，这是新增字段）
            reply_names = names,
            reply_name = cfg is null ? "" : cfg.ReplyName,
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
    /// 本机是否具备「真正解锁」的能力。
    ///
    /// ⚠ Phase 1 与 AgentClient.ReportUnlockCapability 一致：凭据存储和
    /// Credential Provider 都还没做，这里必须是 false —— 报了就是谎报，页面会给出
    /// 一个点了必然失败的按钮。Phase 2 落地后两处一起改成 true。
    /// </summary>
    private static bool CanUnlock => false;

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
    /// 服务端发来的历史对话转给页面渲染。协议 §4 里没有这条 —— 属于**新增帧**
    /// （只加不删），页面不认识就忽略；壳模式下页面不连 /ws/web，有它才能省掉一次往返。
    /// </summary>
    public void PostHistory(JsonElement messages)
    {
        if (messages.ValueKind != JsonValueKind.Array)
            return;
        Send(new { type = "host.history", messages });
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

    /// <summary>页面要求切形态（"popup" / "console"）。</summary>
    public event Action<string>? ModeRequested;

    /// <summary>兜底页保存服务端配置：(url, enroll_token)。</summary>
    public event Action<string, string>? ServerChanged;

    /// <summary>页面要求退出程序。</summary>
    public event Action? QuitRequested;

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
                    var mode = GetString(root, "mode");
                    if (mode != "popup" && mode != "console")
                    {
                        AgentLog.Write($"✗ 桥：web.switch_mode 的 mode 不认识（{mode}，已忽略）");
                        return;
                    }
                    ModeRequested?.Invoke(mode);
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

    private static long GetLong(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
            return 0;
        try { return el.GetInt64(); }
        catch { return 0; }   // 小数这个接口会抛，别让它带崩调用方
    }
}
