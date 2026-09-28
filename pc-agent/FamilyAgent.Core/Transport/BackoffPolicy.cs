using System;

namespace FamilyAgent.Core.Transport;

/// <summary>
/// 重连退避：2 秒起、指数翻倍、30 秒封顶（原 <c>LoopAsync</c> 里的
/// <c>var delaySeconds = 2;</c> / <c>delaySeconds = Math.Min(delaySeconds * 2, 30)</c>）。
///
/// 唯一的语义要点：连接**成功**时退避回到起点（<see cref="Reset"/>），
/// 所以「偶尔抖一下」不会把重连间隔一路推到 30 秒。
/// </summary>
public sealed class BackoffPolicy
{
    /// <summary>起点（秒）。</summary>
    public const int InitialSeconds = 2;

    /// <summary>封顶（秒）。</summary>
    public const int MaxSeconds = 30;

    private int _seconds = InitialSeconds;

    /// <summary>下一次断线要等多少秒。</summary>
    public int CurrentSeconds => _seconds;

    /// <summary>连接成功 → 回到起点。</summary>
    public void Reset() => _seconds = InitialSeconds;

    /// <summary>等完这一轮 → 翻倍封顶。</summary>
    public void Next() => _seconds = Math.Min(_seconds * 2, MaxSeconds);
}
