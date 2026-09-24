using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace FamilyAgent;

/// <summary>
/// Windows 开机自启。
///
/// ── 为什么需要两套机制 ─────────────────────────────────────────────
/// 「电脑只开了机、没人登录」时，`HKCU\...\Run` 是**不会**生效的：
/// 那个键由资源管理器在**用户登录**的那一刻才执行，而登录前根本不存在
/// 交互式桌面（Vista 起会话 0 与交互式桌面隔离）。
///
/// 所以：
///   · Run 项（不需要管理员）—— 常规场景：用户登录后自动启动
///   · 计划任务 FamilyAgent-Boot（ONSTART，SYSTEM 身份）—— 覆盖
///     「只开机、未登录」：以 <c>--headless</c> 启动，先把设备顶上线
///   · 计划任务 FamilyAgent-Logon（ONLOGON，最高权限）—— 比 Run 项更可靠，
///     不会被组策略对 Run 项的限制拦掉
///
/// ── 登录前能做到什么、做不到什么 ──────────────────────────────────
/// 做不到：显示全屏消息弹窗。会话 0 没有桌面，这是操作系统约束，
///         不是配置问题（绕过它需要把代码注入 winlogon 桌面，脆弱且危险）。
/// 能做到：进程已经在跑、已经连上服务端、设备在网页端显示在线，
///         可远程关机、可观察状态；等有人登录，弹窗能力自动接回来。
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FamilyAgent";
    private const string LogonTask = "FamilyAgent-Logon";
    private const string BootTask = "FamilyAgent-Boot";

    /// <summary>
    /// 自启的落地情况。UI 用它告诉用户「登录前」那一半到底有没有生效 ——
    /// 不能只回一个 bool，否则用户没法知道开机自启是不是"只成功了一半"。
    /// </summary>
    public sealed record Status(bool RunKey, bool LogonTask, bool BootTask)
    {
        public bool Any => RunKey || LogonTask || BootTask;

        /// <summary>登录前（仅开机、无人登录）也能启动 —— 只有 ONSTART 计划任务做得到。</summary>
        public bool PreLogin => BootTask;

        public string Describe()
        {
            if (!Any)
                return "未启用";
            var parts = new List<string>();
            if (RunKey) parts.Add("登录时(Run 项)");
            if (LogonTask) parts.Add("登录时(计划任务)");
            if (BootTask) parts.Add("开机即启动(登录前)");
            return string.Join(" + ", parts);
        }
    }

    // ────────────────────────────── 查询 ──────────────────────────────

    public static bool IsEnabled() => Query().Any;

    public static Status Query()
    {
        var run = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            run = key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch
        {
            // 读不到就是没启用，不外抛
        }

        return new Status(run, TaskExists(LogonTask), TaskExists(BootTask));
    }

    private static bool TaskExists(string name)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{name}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null)
                return false;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // ────────────────────────────── 应用 ──────────────────────────────

    /// <summary>启用/停用自启，返回**实际**落地的情况（而不是我们"打算"做什么）。</summary>
    public static Status Apply(bool enabled)
    {
        if (!enabled)
        {
            RemoveRunKey();
            DeleteTask(LogonTask);
            DeleteTask(BootTask);       // SYSTEM 任务需要管理员，失败就再提权试一次
            return Query();
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            AgentLog.Write("自启失败：拿不到自身 exe 路径（Environment.ProcessPath 为空）");
            return Query();
        }

        WriteRunKey(exe);
        RegisterLogonTask(exe);
        RegisterBootTask(exe);
        return Query();
    }

    // ── Run 项 ──

    private static void WriteRunKey(string exe)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            key?.SetValue(ValueName, $"\"{exe}\" --tray");
        }
        catch (Exception ex)
        {
            AgentLog.Write("写 Run 项失败：" + ex.Message);
        }
    }

    private static void RemoveRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            key?.DeleteValue(ValueName, false);
        }
        catch (Exception ex)
        {
            AgentLog.Write("删 Run 项失败：" + ex.Message);
        }
    }

    // ── 登录时：计划任务（比 Run 项可靠，且能拿最高权限）──

    private static void RegisterLogonTask(string exe)
    {
        string sid;
        try
        {
            sid = WindowsIdentity.GetCurrent().User?.Value ?? "";
        }
        catch
        {
            sid = "";
        }
        if (string.IsNullOrEmpty(sid))
        {
            AgentLog.Write("建登录计划任务失败：拿不到当前用户 SID");
            return;
        }

        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>家庭消息 Agent —— 用户登录时启动</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{Escape(sid)}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{Escape(sid)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{Escape(exe)}</Command>
                  <Arguments>--tray</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        if (!RegisterFromXml(LogonTask, xml, elevated: false))
            AgentLog.Write("建登录计划任务失败（Run 项仍然生效，不影响常规开机自启）");
    }

    // ── 开机时（登录前）：SYSTEM 身份跑 headless ──

    private static void RegisterBootTask(string exe)
    {
        // headless 实例必须读**用户**那份配置，否则 SYSTEM 会用自己的
        // %APPDATA%，注册成另一个设备。所以显式把路径传过去。
        var cfgPath = AgentConfig.FilePath;

        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>家庭消息 Agent —— 开机即启动（登录前），让设备在无人登录时也保持在线</Description>
              </RegistrationInfo>
              <Triggers>
                <BootTrigger>
                  <Enabled>true</Enabled>
                  <Delay>PT8S</Delay>
                </BootTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>S-1-5-18</UserId>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{Escape(exe)}</Command>
                  <Arguments>--headless --config "{Escape(cfgPath)}"</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        // 建 SYSTEM 任务需要管理员。先按当前权限试，不行就弹一次 UAC 提权重试 ——
        // 只在这里提权，程序本身不以管理员身份运行（否则每次启动都弹 UAC）。
        if (RegisterFromXml(BootTask, xml, elevated: false))
        {
            AgentLog.Write("开机自启（登录前）已启用");
            return;
        }

        AgentLog.Write("建开机计划任务需要管理员权限，尝试提权…");
        if (RegisterFromXml(BootTask, xml, elevated: true))
            AgentLog.Write("开机自启（登录前）已启用（经提权）");
        else
            AgentLog.Write("开机自启（登录前）未启用：提权也没成功。"
                         + "登录时仍会自动启动，但「只开机未登录」的场景需要管理员权限才能覆盖。");
    }

    // ── schtasks 封装 ──

    /// <summary>
    /// 用 XML 建任务（而不是 /TR 拼命令行）—— 参数里有空格和引号时，
    /// /TR 的转义规则很容易写错，XML 让 Windows 自己解析。
    /// </summary>
    private static bool RegisterFromXml(string name, string xml, bool elevated)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"familyagent-{name}.xml");
        try
        {
            // schtasks /XML 读 UTF-16（带 BOM）最稳，且 XML 声明也写的 UTF-16
            File.WriteAllText(tmp, xml, new UnicodeEncoding(false, true));

            var psi = new ProcessStartInfo("schtasks.exe", $"/Create /F /TN \"{name}\" /XML \"{tmp}\"")
            {
                UseShellExecute = elevated,
                CreateNoWindow = !elevated,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (elevated)
                psi.Verb = "runas";

            using var p = Process.Start(psi);
            if (p is null)
                return false;
            p.WaitForExit(60000);
            if (p.ExitCode != 0)
                AgentLog.Write($"schtasks 建任务 {name} 返回 {p.ExitCode}");
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            AgentLog.Write($"建任务 {name} 异常：{ex.Message}");
            return false;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private static void DeleteTask(string name)
    {
        if (!TaskExists(name))
            return;
        // SYSTEM 任务删除同样要管理员，失败再提权一次
        if (RunSchtasks($"/Delete /F /TN \"{name}\"", elevated: false))
            return;
        RunSchtasks($"/Delete /F /TN \"{name}\"", elevated: true);
    }

    private static bool RunSchtasks(string args, bool elevated)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = elevated,
                CreateNoWindow = !elevated,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (elevated)
                psi.Verb = "runas";

            using var p = Process.Start(psi);
            if (p is null)
                return false;
            p.WaitForExit(60000);
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            AgentLog.Write($"schtasks {args} 异常：{ex.Message}");
            return false;
        }
    }

    private static string Escape(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}
