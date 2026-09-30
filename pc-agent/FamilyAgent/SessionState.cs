using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FamilyAgent.Core.Diagnostics;
using Microsoft.Win32;

namespace FamilyAgent;

/// <summary>
/// Windows 会话状态检测（远程解锁 Phase 1 的「状态上报」来源）。
///
/// 上报的四种状态（协议已冻结，字面量不要改）：
///   <c>logon_screen</c> —— 停在登录界面 / 无人登录：headless 实例（会话 0）属于这种
///   <c>locked</c>       —— 有交互式会话，但输入桌面拿不到 → 锁屏中
///   <c>unlocked</c>     —— 有已登录的交互式桌面，能打开输入桌面
///   <c>unknown</c>      —— 其它检测失败，如实报 unknown，不猜
///
/// 判定只用一次 <c>OpenInputDesktop</c>：成功即 unlocked；失败且
/// ERROR_ACCESS_DENIED(5) 即 locked（锁屏时输入桌面归 Winlogon 的安全桌面，
/// 本进程被拒）。拿到句柄**必须** <c>CloseDesktop</c>，否则每次读取都漏一个句柄。
///
/// 实时性：订阅 <c>SystemEvents.SessionSwitch</c>（锁屏/解锁/登录/注销）做即时刷新，
/// 但**不把事件当作唯一依据** —— <see cref="Current"/> 每次调用都真实检测一遍，
/// 所以即使事件订阅失败或漏事件，心跳报出去的也永远是此刻的真状态。
/// </summary>
public static class SessionState
{
    // ── 协议取值（与 NAS 侧冻结，勿改字面量）──────────────────────────
    public const string LogonScreen = "logon_screen";
    public const string Locked = "locked";
    public const string Unlocked = "unlocked";
    public const string Unknown = "unknown";

    private const uint DesktopSwitchDesktop = 0x0100;   // DESKTOP_SWITCHDESKTOP
    private const int ErrorAccessDenied = 5;            // ERROR_ACCESS_DENIED
    private const uint NoActiveSession = 0xFFFFFFFF;    // WTSGetActiveConsoleSessionId 失败值

    /// <summary>会话事件触发后复核的延迟：切换桌面本身有几百毫秒的过程，
    /// 事件到达时 OpenInputDesktop 可能还读到切换前的桌面。</summary>
    private const int RecheckDelayMs = 1500;

    /// <summary>状态发生变化时的通知。回调已切回 UI 线程（Dispatcher）。</summary>
    public static event Action<string>? Changed;

    private static readonly object Gate = new();
    private static string _lastNotified = Unknown;
    private static bool _started;
    private static int _recheckPending;

    /// <summary>
    /// 当前会话状态。**每次都真实检测**，不返回缓存值 ——
    /// 事件只负责「立刻通知」，状态本身永远以检测为准。
    /// OpenInputDesktop + CloseDesktop 是微秒级开销，15 秒一次的心跳不值得缓存。
    /// </summary>
    public static string Current => Detect();

    /// <summary>启动：订阅会话切换事件 + 做一次初始检测。重复调用安全。</summary>
    public static void Start()
    {
        lock (Gate)
        {
            if (_started)
                return;
            _started = true;
        }

        try
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            AgentLog.Write("会话状态监听已启用（SystemEvents.SessionSwitch）");
        }
        catch (Exception ex)
        {
            // 订阅失败不致命：心跳里每次都会读 Current（真检测），不会一直报旧状态
            AgentLog.Write("订阅 SessionSwitch 失败（心跳仍会实时检测）：" + ex.Message);
        }

        Refresh("启动");
    }

    /// <summary>退出时取消订阅，避免进程收尾阶段还有回调打进来。</summary>
    public static void Stop()
    {
        lock (Gate)
        {
            if (!_started)
                return;
            _started = false;
        }

        try { SystemEvents.SessionSwitch -= OnSessionSwitch; }
        catch { /* 退出阶段，取消失败无所谓 */ }
    }

    /// <summary>
    /// 真实检测一次。**保证不抛异常** —— 调用方是心跳发送循环，
    /// 这里抛出去会被当成「心跳发不出去」而强制重连，属于误伤。
    /// </summary>
    public static string Detect()
    {
        try
        {
            return DetectCore();
        }
        catch (Exception ex)
        {
            AgentLog.Write("会话状态检测异常（按 unknown 上报）：" + ex.Message);
            return Unknown;
        }
    }

    /// <summary>
    /// 判定链，顺序不能换：
    ///   ① headless（开机后无人登录，跑在会话 0）→ logon_screen
    ///   ② 本进程不在交互式会话里（会话 0 / 没有活动控制台会话）→ logon_screen
    ///   ③ OpenInputDesktop 成功 → unlocked
    ///   ④ 失败且 ERROR_ACCESS_DENIED(5) → locked
    ///   ⑤ 其它失败 → unknown
    ///
    /// ★ v0.20.2 起先把**原始信号**收进 <see cref="Diag"/> 再判定（<see cref="Diagnose"/>）——
    ///   同一份信号既能判状态、又能原样写进日志/设置页。用户报「状态不对」时，
    ///   看一眼诊断行就知道是哪一步判歪了，不用猜。
    ///   「是不是锁屏」= ③④：有交互会话但拿不到输入桌面（那就是锁屏/安全桌面）；
    ///   是否有**已登录用户**由 `WTSQuerySessionInformation(WTSUserName)` 直接问系统，
    ///   不再靠猜 —— 锁屏与"没人登录"这两种都会拿不到输入桌面，必须靠它区分。
    /// </summary>
    private static string DetectCore() => Diagnose().State;

    /// <summary>一次检测的全部原始信号（状态由它推导，日志/设置页也用它）。</summary>
    public sealed record Diag(
        bool Headless,
        uint SessionId,
        uint ActiveConsoleSessionId,
        bool Interactive,
        string LoggedInUser,
        bool InputDesktop,
        int Win32Error,
        string State)
    {
        public string Describe() =>
            $"headless={Headless} session={SessionId} active={ActiveConsoleSessionId} "
            + $"interactive={Interactive} user={(LoggedInUser.Length == 0 ? "-" : LoggedInUser)} "
            + $"input_desktop={(InputDesktop ? "ok" : "拿不到")} err={Win32Error} → {State}";
    }

    /// <summary>
    /// 真实检测一次并返回全部信号。**保证不抛异常**（调用方是心跳循环，
    /// 这里抛出去会被当成"心跳发不出去"而强制重连，属于误伤）。
    /// </summary>
    public static Diag Diagnose()
    {
        bool headless = false;
        uint sessionId = 0, active = NoActiveSession;
        var interactive = false;
        var user = "";
        var desktopOk = false;
        var err = 0;
        var state = Unknown;

        try
        {
            headless = App.IsHeadless;

            _ = ProcessIdToSessionId((uint)Environment.ProcessId, out sessionId);
            active = WTSGetActiveConsoleSessionId();
            interactive = !headless && sessionId != 0 && active != NoActiveSession;

            // 已登录用户名：锁屏与"没人登录"都会拿不到输入桌面，靠这个区分
            user = interactive ? LoggedInUserOf(sessionId) : "";

            var handle = OpenInputDesktop(0, false, DesktopSwitchDesktop);
            if (handle != IntPtr.Zero)
            {
                CloseDesktop(handle);        // ★ 不关就是句柄泄漏
                desktopOk = true;
            }
            else
            {
                err = Marshal.GetLastWin32Error();
            }

            state = headless || !interactive
                ? LogonScreen
                : desktopOk
                    ? Unlocked
                    : err == ErrorAccessDenied && user.Length > 0
                        ? Locked             // 有人登录 + 输入桌面被安全桌面占着 = 锁屏
                        : err == ErrorAccessDenied
                            ? LogonScreen    // 拿不到桌面且没有登录用户 = 停在登录界面
                            : Unknown;
        }
        catch (Exception ex)
        {
            state = Unknown;
            AgentLog.Write("会话状态检测异常（按 unknown 上报）：" + ex.Message);
        }

        return new Diag(headless, sessionId, active, interactive, user, desktopOk, err, state);
    }

    /// <summary>
    /// 问系统「这个会话里登录的是谁」。空串 = 没有已登录用户（登录界面 / 会话 0）。
    /// 只读、无副作用；失败一律返回空串（按"没人登录"处理，不猜）。
    /// </summary>
    private static string LoggedInUserOf(uint sessionId)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, WtsUserName, out buffer, out var bytes))
                return "";
            if (buffer == IntPtr.Zero || bytes <= 2)
                return "";
            var name = Marshal.PtrToStringUni(buffer) ?? "";
            return name.Trim();
        }
        catch (Exception)
        {
            return "";
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                try { WTSFreeMemory(buffer); } catch { /* 释放失败不致命 */ }
            }
        }
    }

    /// <summary>重新检测；状态变了才写日志并抛 <see cref="Changed"/>。线程安全。</summary>
    public static void Refresh(string why)
    {
        var diag = Diagnose();
        var state = diag.State;
        var changed = false;

        lock (Gate)
        {
            if (!string.Equals(state, _lastNotified, StringComparison.Ordinal))
            {
                _lastNotified = state;
                changed = true;
            }
        }

        if (!changed)
            return;

        // ★ 带上全部原始信号：以后用户说「状态不对」，这行日志就是现场
        AgentLog.Write($"[SESSION] {why} → {diag.Describe()}");
        RaiseChanged(state);
    }

    private static void RaiseChanged(string state)
    {
        var handler = Changed;
        if (handler is null)
            return;

        try
        {
            var app = System.Windows.Application.Current;
            // SystemEvents 的回调在它自己的线程上，不是 UI 线程。订阅方要刷界面，
            // 所以统一切回 Dispatcher 再通知；已经在 UI 线程就直接回调，避免自等。
            if (app is not null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.Invoke(() => handler(state));
                return;
            }
        }
        catch (Exception ex)
        {
            AgentLog.Write("会话状态通知切 Dispatcher 失败（改为直接回调）：" + ex.Message);
        }

        try { handler(state); }
        catch (Exception ex) { AgentLog.Write("会话状态回调异常：" + ex.Message); }
    }

    private static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        var why = "会话事件 " + e.Reason;
        Refresh(why);
        ScheduleRecheck(why);
    }

    /// <summary>
    /// 事件触发那一刻桌面切换可能还没完成（会读到切换前的状态），
    /// 延迟一秒多复核一次，避免上报一个转瞬即逝的错状态。
    /// 同一时间只排一次复核，连续事件不会叠加。
    /// </summary>
    private static void ScheduleRecheck(string why)
    {
        if (Interlocked.CompareExchange(ref _recheckPending, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(RecheckDelayMs).ConfigureAwait(false);
                Refresh(why + " 复核");
            }
            catch (Exception ex)
            {
                AgentLog.Write("会话状态复核失败：" + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _recheckPending, 0);
            }
        });
    }

    // ── Win32 ────────────────────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

    // WTSUserName = 5（wtsapi32.h 的 WTS_INFO_CLASS 枚举值，勿改）
    private const int WtsUserName = 5;

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(
        IntPtr hServer, uint sessionId, int wtsInfoClass, out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);
}
