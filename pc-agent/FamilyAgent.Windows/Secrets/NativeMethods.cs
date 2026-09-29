using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FamilyAgent.Windows.Secrets;

/// <summary>
/// DPAPI（`crypt32.dll`）的原生封装。**只在这个文件里出现 P/Invoke**，
/// 上层走 <see cref="DpapiSecretProtector"/>，测试可以对它做「真机验证」。
///
/// 为什么不用 `System.Security.Cryptography.ProtectedData` 这个包：
/// 同一个东西，但它是一层托管包装；直接 P/Invoke 可以不引 NuGet 包，
/// 云编译时少一次离线还原失败的可能（附带的熵 / 作用域语义完全一致）。
/// </summary>
internal static class NativeMethods
{
    /// <summary>不弹任何 UI（Service 里没有桌面可以弹）。</summary>
    internal const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    /// <summary>密文绑定到**这台机器**（本机任何账户/服务都能解）而不是当前用户。</summary>
    internal const int CRYPTPROTECT_LOCAL_MACHINE = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    internal struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr LocalFree(IntPtr hMem);

    // ── 下面两个给 LogonUser 用 ───────────────────────────────────────

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LogonUser(
        string lpszUsername,
        string? lpszDomain,
        string lpszPassword,
        int dwLogonType,
        int dwLogonProvider,
        out IntPtr phToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    /// <summary>把托管字节数组封成 DATA_BLOB（用完必须 <see cref="FreeBlob"/>）。</summary>
    internal static DATA_BLOB MakeBlob(byte[] data)
    {
        var blob = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, blob.pbData, data.Length);
        return blob;
    }

    /// <summary>把 DATA_BLOB 拷回托管数组。</summary>
    internal static byte[] ReadBlob(DATA_BLOB blob)
    {
        var outp = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, outp, 0, blob.cbData);
        return outp;
    }

    /// <summary>释放 <see cref="MakeBlob"/> 或 DPAPI 输出的非托管内存。</summary>
    internal static void FreeBlob(ref DATA_BLOB blob)
    {
        if (blob.pbData != IntPtr.Zero)
        {
            LocalFree(blob.pbData);
            blob.pbData = IntPtr.Zero;
            blob.cbData = 0;
        }
    }

    /// <summary>取最近一次 Win32 错误，拼成人话（审计/日志里用）。</summary>
    internal static string LastError()
    {
        var code = Marshal.GetLastWin32Error();
        return $"Win32 错误 {code}（{(code == 0 ? "无" : new System.ComponentModel.Win32Exception(code).Message)}）";
    }

    /// <summary>把 debug 用的描述串转成 UTF-16 字节（DPAPI 的 szDataDescr 用）。</summary>
    internal static string Describe(string s) => Encoding.Unicode.GetString(Encoding.Unicode.GetBytes(s));
}
