using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FamilyAgent;

/// <summary>
/// 「登录前 / 登录后」两个实例的交接。
///
/// 场景：开机 → 计划任务 FamilyAgent-Boot（SYSTEM 身份，--headless）先把设备
/// 顶上线；之后有人登录 → Run 项 / FamilyAgent-Logon 再起一个交互式实例。
///
/// 两个实例连同一个 device_id 会互相打架（截图请求可能落到那个看不见屏幕的
/// 身上），所以必须交接：
///
///   · 交互式实例 —— 每 10 秒写一次心跳文件
///   · headless 实例 —— 每 10 秒看一次心跳：
///         心跳新鲜 → **让位**（断开连接，让登录后的那个实例独占）
///         心跳过期 → **接管**（重新连上，保证无人登录时设备仍在线）
///
/// 为什么不退出而只是让位：用户注销后心跳会过期，headless 实例还得能重新接管，
/// 否则「登录又注销」之后设备就永远离线了。
///
/// 为什么用文件而不是命名内核对象：跨会话（session 0 ↔ session 1）通信需要
/// `Global\` 命名对象，而创建它要求 SeCreateGlobalPrivilege，普通用户账号默认
/// 没有，会直接失败。文件没有这个限制。
/// </summary>
public static class Presence
{
    private const int HeartbeatSeconds = 10;
    private const int FreshSeconds = 45;

    private static CancellationTokenSource? _cts;

    /// <summary>心跳文件路径：放在配置文件旁边，两个实例都算得出来同一个位置。</summary>
    private static string HeartbeatPath => Path.Combine(
        Path.GetDirectoryName(AgentConfig.FilePath) ?? ".", "interactive.heartbeat");

    // ──────────────────── 交互式实例：发心跳 ────────────────────

    public static void StartHeartbeat()
    {
        Stop();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var dir = Path.GetDirectoryName(HeartbeatPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(HeartbeatPath, DateTime.UtcNow.ToString("O"));
                }
                catch (Exception ex)
                {
                    AgentLog.Write("写心跳失败（不影响消息收发）：" + ex.Message);
                }

                try { await Task.Delay(TimeSpan.FromSeconds(HeartbeatSeconds), ct); }
                catch (OperationCanceledException) { return; }
            }
        }, ct);
    }

    public static void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    /// <summary>交互式实例退出时清掉心跳，让 headless 实例能马上回来接管。</summary>
    public static void ClearHeartbeat()
    {
        try
        {
            if (File.Exists(HeartbeatPath))
                File.Delete(HeartbeatPath);
        }
        catch
        {
            // 删不掉也无所谓，45 秒后自然过期
        }
    }

    // ──────────────────── headless 实例：看心跳、让位/接管 ────────────────────

    /// <summary>
    /// 只在 <c>--headless</c> 下调用。维护「有人登录就让位、没人登录就接管」。
    /// </summary>
    public static void StartSupervisor(AgentClient client)
    {
        _ = Task.Run(async () =>
        {
            var yielded = false;      // 当前是否处于「让位」状态
            AgentLog.Write($"headless 监督已启动（心跳文件 {HeartbeatPath}）");

            while (true)
            {
                try
                {
                    var interactiveAlive = IsInteractiveAlive();

                    if (interactiveAlive && !yielded)
                    {
                        AgentLog.Write("检测到交互式实例在运行 → 让位（断开连接，避免两个实例抢同一个设备）");
                        client.Stop();
                        // 给旧连接一点时间真正收尾，避免和紧接着的重连打架
                        await Task.Delay(TimeSpan.FromSeconds(2));
                        yielded = true;
                    }
                    else if (!interactiveAlive && yielded)
                    {
                        AgentLog.Write("交互式实例已退出 → 接管（重新连上服务端）");
                        client.Start();
                        yielded = false;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(HeartbeatSeconds));
                }
                catch (Exception ex)
                {
                    AgentLog.Write("headless 监督循环异常（继续）：" + ex.Message);
                    try { await Task.Delay(TimeSpan.FromSeconds(HeartbeatSeconds)); } catch { }
                }
            }
        });
    }

    private static bool IsInteractiveAlive()
    {
        try
        {
            var fi = new FileInfo(HeartbeatPath);
            if (!fi.Exists)
                return false;
            return (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalSeconds < FreshSeconds;
        }
        catch
        {
            return false;
        }
    }
}
