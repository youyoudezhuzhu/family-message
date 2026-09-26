using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace FamilyAgent;

/// <summary>
/// 单实例仲裁（文件式，**不依赖任何特权**）。
///
/// ★ 为什么不用「Global\ 命名 Mutex」：
///   创建/打开 Global\ 命名内核对象需要 SeCreateGlobalPrivilege，普通用户账号
///   默认没有。日志里已经出现过
///   `UnauthorizedAccessException: Access to the path 'Global\FamilyAgent.SingleInstance' is denied`
///   —— 直接崩在启动（2026-09-24 21:54、2026-09-25 20:35 各两次），
///   比「重复启动」这件事本身严重得多。文件没有这个限制
///   （跟 Presence.cs 的 heartbeat 同一套思路）。
///
/// ★ 为什么第二个实例不再静默退出：
///   用户机器上可能同时放着好几份 exe（旧的在桌面、新的在别的目录）。旧的那份
///   被开机自启拉起来占着，双击新版 exe 时如果悄无声息地退出，屏幕上还是旧界面
///   —— 用户只会得出「你根本没编译」的结论（这已经坑过两次）。
///   现在第二个实例只写一条请求，由**正在跑的那个**把窗口拉到前面；两者版本
///   不一致时，正在跑的那个还要用托盘气泡把话说清楚。
///
/// 文件（与配置文件同目录，跟 Presence 的 heartbeat 放在一处：
/// 两个实例都算得出来同一个位置，跨会话也一样）：
///   instance.lock  {"pid":123,"version":"(版本号)","started":"ISO8601"}
///   show.request   {"version":"(版本号)","at":"ISO8601","pid":456}
/// 版本号一律取 <see cref="AgentClient.ReportedVersion"/>，这里**不写死任何版本字符串**。
///
/// 所有文件读写都 try/catch 包住（磁盘满 / 权限 / 文件被占都可能），失败一律
/// 记一笔日志后当作「没有这回事」，**绝不能把程序带崩**。
/// </summary>
public static class SingleInstance
{
    /// <summary>正在运行的实例：它的 pid + 它自己写下的版本号。</summary>
    public sealed record InstanceInfo(int Pid, string Version);

    private static readonly object Gate = new();

    /// <summary>上次处理过的 show.request 时间戳（去重，避免同一条反复触发）。</summary>
    private static DateTime _lastShowAtUtc = DateTime.MinValue;

    /// <summary>仲裁文件所在目录 = 配置文件所在目录（算不出来就用当前目录）。</summary>
    private static string Dir
    {
        get
        {
            var dir = Path.GetDirectoryName(AgentConfig.FilePath);
            return string.IsNullOrEmpty(dir) ? "." : dir;
        }
    }

    private static string LockPath => Path.Combine(Dir, "instance.lock");

    private static string ShowRequestPath => Path.Combine(Dir, "show.request");

    // ──────────────────── 已在跑的实例：判定 ────────────────────

    /// <summary>
    /// 读 instance.lock：pid 还活着就返回它（版本用它自己写下的那个）；
    /// 没锁 / pid 已死 / 文件坏 → null。
    ///
    /// ★ 判活只看 pid：查不到进程就当作**已经死了**，不能因为「查不出来」
    ///   就把自己也挡在门外 —— 否则一次权限异常会让程序再也起不来。
    ///
    /// ★ pid 跟自己一样也当作没有：那是上次留下的陈锁恰好撞上了复用的 pid，
    ///   不能把自己拦住（本进程还没 Claim 过锁）。
    /// </summary>
    public static InstanceInfo? Running()
    {
        try
        {
            if (!File.Exists(LockPath))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(LockPath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var pid = ReadInt(root, "pid");
            if (pid <= 0 || pid == Environment.ProcessId || !IsAlive(pid))
                return null;

            return new InstanceInfo(pid, ReadString(root, "version"));
        }
        catch (Exception ex)
        {
            AgentLog.Write("读 instance.lock 失败（当作没有其他实例）：" + ex.Message);
            return null;
        }
    }

    /// <summary>抢占锁：写下自己的 pid + 版本号（供下一个实例判定「谁在跑、跑的是哪一版」）。</summary>
    public static void Claim()
    {
        var json = JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId,
            version = AgentClient.ReportedVersion,
            started = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        });
        WriteText(LockPath, json, "写 instance.lock 失败");
    }

    /// <summary>
    /// 退出时清锁。**只管自己那份**：pid 不是自己就留着
    /// （headless 实例从来没 Claim 过，不能顺手把交互式实例的锁删掉）。
    /// 读不出来 / 删不掉也无所谓 —— 下次启动靠 pid 判活绕过。
    /// </summary>
    public static void Release()
    {
        try
        {
            if (!File.Exists(LockPath))
                return;

            using var doc = JsonDocument.Parse(File.ReadAllText(LockPath));
            if (ReadInt(doc.RootElement, "pid") != Environment.ProcessId)
                return;

            File.Delete(LockPath);
        }
        catch (Exception ex)
        {
            AgentLog.Write("清 instance.lock 失败（不影响退出，下次靠 pid 判活绕过）：" + ex.Message);
        }
    }

    // ──────────────────── 第二个实例：请求拉窗口 ────────────────────

    /// <summary>
    /// 第二个实例：写下 show.request，请**已经在跑的那个**把窗口拉到前面。
    ///
    /// 为什么用文件而不是发消息：两个进程之间没有任何通道，而且跨会话
    /// （登录前的 headless 跑在会话 0）连窗口消息都发不过去。文件最省事，
    /// 对方下 1 秒的轮询就能看到，本进程可以直接退场。
    /// </summary>
    public static void RequestShow(string myVersion)
    {
        var json = JsonSerializer.Serialize(new
        {
            version = myVersion ?? "",
            at = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            pid = Environment.ProcessId,
        });
        WriteText(ShowRequestPath, json, "写 show.request 失败（窗口不会被拉到前面）");
        AgentLog.Write($"已请求正在运行的实例把窗口拉到前面"
                     + $"（本进程 pid={Environment.ProcessId} {myVersion}）");
    }

    /// <summary>
    /// 已在跑的实例轮询用：有**新**请求（at 比上次处理的新）就返回对方版本，否则 null。
    ///
    /// 内部记住上次处理过的 at，同一条请求不会触发两次；处理完顺手把请求文件删掉
    /// （内容没变才删，免得把这中间又写进来的新请求一起删了），这样下次重启也不会
    /// 把一条陈年老请求重放一遍。
    /// </summary>
    public static string? TakeShowRequest()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(ShowRequestPath))
                    return null;

                var json = File.ReadAllText(ShowRequestPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return null;

                var at = ReadString(root, "at");
                if (!DateTime.TryParse(at, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var when))
                    return null;                    // 时间戳读不出来：当作坏文件，忽略

                var whenUtc = when.ToUniversalTime();
                if (whenUtc <= _lastShowAtUtc)
                    return null;                    // 这条已经处理过了
                _lastShowAtUtc = whenUtc;

                try
                {
                    if (File.ReadAllText(ShowRequestPath) == json)
                        File.Delete(ShowRequestPath);
                }
                catch (Exception ex)
                {
                    AgentLog.Write("清 show.request 失败（下次靠时间戳去重）：" + ex.Message);
                }

                return ReadString(root, "version");
            }
            catch (Exception ex)
            {
                AgentLog.Write("读 show.request 失败（当作没有请求）：" + ex.Message);
                return null;
            }
        }
    }

    // ──────────────────── 小工具 ────────────────────

    /// <summary>进程还在不在。拿不到进程对象就当作已死（见 <see cref="Running"/> 的说明）。</summary>
    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>宽容地取一个整数字段：字段缺失 / 类型不对 / 不是数字都算 0。</summary>
    private static int ReadInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el))
            return 0;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n))
            return n;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s))
            return s;
        return 0;
    }

    /// <summary>宽容地取一个字符串字段：字段缺失 / 类型不对都算空串。</summary>
    private static string ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el))
            return "";
        return el.ValueKind == JsonValueKind.String ? (el.GetString() ?? "") : "";
    }

    /// <summary>写文件（目录不存在就建）。失败只记日志，不影响主流程。</summary>
    private static void WriteText(string path, string content, string what)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, content);
        }
        catch (Exception ex)
        {
            AgentLog.Write($"{what}（不影响运行）：{ex.Message}");
        }
    }
}
