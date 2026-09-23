using System;
using System.IO;

namespace FamilyAgent;

/// <summary>
/// Agent 本地日志。放在 %APPDATA%\FamilyAgent\agent.log。
///
/// 存在的意义：v0.2.0 之前所有发送失败都被静默吞掉，出了问题完全无从下手。
/// 现在每条收发（只记类型，不记内容/图片）和每个异常都会留痕。
/// </summary>
public static class AgentLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FamilyAgent");

    public static string FilePath => Path.Combine(Dir, "agent.log");

    /// <summary>启动时调用：日志太大就轮转一份，避免无限增长。</summary>
    public static void Rotate()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > MaxBytes)
                File.Move(FilePath, FilePath + ".1", overwrite: true);
        }
        catch
        {
            // 日志问题不能影响主流程
        }
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch
            {
                // 磁盘写不进去也不能崩
            }
        }
    }
}
