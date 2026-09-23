using System;
using System.Diagnostics;

namespace FamilyAgent;

/// <summary>
/// 电源控制：执行关机 / 取消。
///
/// 这是「设备管理」能力的一部分 —— PC Agent 是受 Server 信任的家庭设备 Agent，
/// 网页端点了关机就执行，不在本机再弹一次确认（与本项目 §13 的权限模型一致）。
/// 但会延迟几秒再关，给坐在电脑前的人一个反应/取消的机会。
/// </summary>
public static class PowerControl
{
    public const int DefaultDelaySeconds = 5;

    /// <summary>计划关机。delaySeconds 到点后由 Windows 执行。</summary>
    public static void Shutdown(int delaySeconds = DefaultDelaySeconds)
    {
        var delay = Math.Clamp(delaySeconds, 0, 600);
        // 不加 /f：让系统正常关闭各程序，避免丢未保存的内容。
        // 不加 /c 注释：中文注释参数在部分代码页下会乱码，且失败模式更多，不值得。
        var psi = new ProcessStartInfo
        {
            FileName = "shutdown.exe",
            Arguments = $"/s /t {delay}",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process.Start(psi);
    }

    /// <summary>取消已计划的关机（保留给将来的「撤销」入口）。</summary>
    public static void CancelShutdown()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/a",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch
        {
            // 没有待取消的关机时 shutdown /a 会返回错误码，忽略即可
        }
    }
}
