using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace FamilyAgent.Windows.Unlock;

/// <summary>
/// 安装 / 卸载 / 自检 Phase 3 的 Credential Provider（CP）。
///
/// 为什么要有"自检"：CP 跑在**登录界面**里，写坏的后果是登录界面异常（最坏情况）。
/// 所以安装后先跑一次 <see cref="SelfTest"/> —— 它只做"读凭据 → DPAPI 解密 → 打包成
/// LSA blob"这条链，**完全不碰登录界面**；这一步过了再去锁屏试。
///
/// 需要管理员（写 HKLM + 写 %ProgramFiles%）；非管理员时 <see cref="TryRelaunchElevated"/>
/// 用 <c>runas</c> 重新拉起自己（与 AutoStart 建计划任务时同款做法）。
/// </summary>
public static class CredentialProviderInstaller
{
    /// <summary>锁屏上那行/那个 tile 的名字。</summary>
    public const string ProviderName = "家庭消息远程解锁";

    private static readonly string[] RegistrationKeys =
    {
        // CP 必须同时注册在"COM 组件"与"Windows 认识的凭据提供程序"两处
        $@"SOFTWARE\Classes\CLSID\{CredentialProviderProbe.ProviderGuid}",
        $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{CredentialProviderProbe.ProviderGuid}",
    };

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FamilyAgent");

    public static string InstalledDllPath => Path.Combine(InstallDir, "FamilyAgentCp.dll");

    /// <summary>自检用的 DLL：优先用装好的那份，没有就用 exe 旁边的（便于"装之前先试"）。</summary>
    public static string? FindDll()
    {
        if (File.Exists(InstalledDllPath))
            return InstalledDllPath;

        foreach (var rel in new[] { Path.Combine("cp", "FamilyAgentCp.dll"), "FamilyAgentCp.dll" })
        {
            var p = Path.Combine(AppContext.BaseDirectory, rel);
            if (File.Exists(p))
                return p;
        }
        return null;
    }

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>用 runas 把自己重新拉起（带上同一个参数）。返回是否成功发起。</summary>
    public static bool TryRelaunchElevated(string arg)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "",
                Arguments = arg,
                UseShellExecute = true,
                Verb = "runas",          // 会弹一次 UAC；用户拒绝会抛
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception)
        {
            return false;                // 用户点了"否"或没有可用提权方式
        }
    }

    /// <summary>装：拷 DLL 到 %ProgramFiles%\FamilyAgent\ + 写两处注册表。</summary>
    public static (bool Ok, string Detail) Install()
    {
        var src = FindDll();
        if (src is null)
            return (false, "找不到 FamilyAgentCp.dll（应该在 exe 同目录的 cp\\ 下）");

        try
        {
            Directory.CreateDirectory(InstallDir);
            File.Copy(src, InstalledDllPath, overwrite: true);

            using var clsid = Registry.LocalMachine.CreateSubKey(
                $@"SOFTWARE\Classes\CLSID\{CredentialProviderProbe.ProviderGuid}");
            clsid.SetValue(null, ProviderName);
            using var inproc = clsid.CreateSubKey("InprocServer32");
            inproc.SetValue(null, InstalledDllPath);
            inproc.SetValue("ThreadingModel", "Apartment");

            using var cp = Registry.LocalMachine.CreateSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{CredentialProviderProbe.ProviderGuid}");
            cp.SetValue(null, ProviderName);

            return (true, $"已安装：{InstalledDllPath}\r\n注册项：\r\n  " + string.Join("\r\n  ", RegistrationKeys)
                        + $"\r\n\r\n下次锁屏时 LogonUI 会加载它（平时不可见；只有收到远程解锁请求才出现）。");
        }
        catch (Exception ex)
        {
            return (false, "安装失败：" + ex.Message);
        }
    }

    /// <summary>卸：删两处注册表 + 删 DLL（+ 空目录）。一条命令回到"没装过"。</summary>
    public static (bool Ok, string Detail) Uninstall()
    {
        var problems = new System.Collections.Generic.List<string>();

        foreach (var key in RegistrationKeys)
        {
            try { Registry.LocalMachine.DeleteSubKeyTree(key, throwOnMissingSubKey: false); }
            catch (Exception ex) { problems.Add($"{key}: {ex.Message}"); }
        }

        try
        {
            if (File.Exists(InstalledDllPath))
                File.Delete(InstalledDllPath);
        }
        catch (Exception ex) { problems.Add("删 DLL: " + ex.Message); }

        try
        {
            // 目录里没别的东西才删（不硬来）
            if (Directory.Exists(InstallDir) && Directory.GetFileSystemEntries(InstallDir).Length == 0)
                Directory.Delete(InstallDir);
        }
        catch (Exception) { /* 删不掉目录不影响功能 */ }

        return problems.Count == 0
            ? (true, "已卸载：注册表两项已删、DLL 已删。锁屏界面回到没装过的样子。")
            : (false, "卸载有残留（多为被占用/权限）：\r\n  " + string.Join("\r\n  ", problems));
    }

    private delegate int CpSelfTestFn(IntPtr outBuf, uint cchOut);

    /// <summary>
    /// 自检：加载 CP 的 DLL，调它导出的 <c>FamilyAgentCpSelfTest</c> ——
    /// 那条链做的是"读 arm 凭证 → 读凭据 → DPAPI 解密 → 打包 KERB blob"，
    /// **不碰登录界面**。装之前/之后都能跑（未安装时用 exe 旁边那份）。
    /// </summary>
    public static (bool Ok, string Detail) SelfTest()
    {
        var dll = FindDll();
        if (dll is null)
            return (false, "找不到 FamilyAgentCp.dll（应该在 exe 同目录的 cp\\ 下）");

        var handle = IntPtr.Zero;
        var buf = IntPtr.Zero;
        try
        {
            handle = NativeLibrary.Load(dll);
            if (!NativeLibrary.TryGetExport(handle, "FamilyAgentCpSelfTest", out var fn))
                return (false, "DLL 里没有 FamilyAgentCpSelfTest 导出（是不是装错了文件？）");

            buf = Marshal.AllocHGlobal(4096);
            Marshal.Copy(new byte[4096], 0, buf, 4096);

            var call = Marshal.GetDelegateForFunctionPointer<CpSelfTestFn>(fn);
            var hr = call(buf, 2048);
            var text = Marshal.PtrToStringUni(buf) ?? "";

            var head = $"DLL：{dll}\r\n"
                     + $"是否登记为凭据提供程序：{(CredentialProviderProbe.IsInstalled() ? "是" : "否（还没装，属正常）")}\r\n"
                     + $"HRESULT：0x{hr:X8}\r\n";
            return (hr == 0, head + text);
        }
        catch (Exception ex)
        {
            return (false, "自检异常：" + ex.Message);
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            if (handle != IntPtr.Zero) NativeLibrary.Free(handle);
        }
    }
}
