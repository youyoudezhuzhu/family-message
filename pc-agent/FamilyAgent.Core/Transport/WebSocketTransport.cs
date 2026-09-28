using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FamilyAgent.Core.Transport;

/// <summary>
/// WebSocket 的收 / 发原语与超时、关闭码处理（原 <c>AgentClient</c> 的
/// <c>SendRawAsync</c> :573-602 与 <c>ReceiveLoopAsync</c> :440-483，见 §4 迁移映射表）。
///
/// 这一层只认字节与字符串，不认协议：分帧、关闭码交给它，
/// 「这一帧是什么类型、该派发给谁」在 <c>Protocol.FrameCodec</c> 与
/// <c>ConnectionManager</c> 里。
/// </summary>
public sealed class WebSocketTransport
{
    /// <summary>
    /// 发送超时：发送有可能永久卡住（TCP 缓冲写满、对端不读），
    /// 卡住比抛异常更糟 —— 外面完全看不出来。
    /// </summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Action<string>? _log;

    public WebSocketTransport(Action<string>? log = null) => _log = log;

    /// <summary>
    /// 发一条文本帧。拿不到发送锁（10 秒）视为超时；发送本身失败的异常照原样抛出，
    /// 由 <c>Outbox</c> 决定「入队补发 + 计数 + 强制重连」。
    /// </summary>
    public async Task SendRawAsync(ClientWebSocket ws, string json, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open)
            throw new InvalidOperationException($"连接不可用（{ws.State}）");

        // 加超时：发送有可能永久卡住（TCP 缓冲写满、对端不读），
        // 卡住比抛异常更糟 —— 外面完全看不出来。
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(SendTimeout);

        try
        {
            await _sendLock.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("等待发送锁超时（10 秒）");
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token)
                    .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// 接收循环。返回值 = 服务端给的关闭码（没给关闭帧 / 本地取消时为 null）——
    /// 主循环靠它区分「普通断线」和「被顶替（4000）」，后者不能重连。
    ///
    /// 每收完一条完整文本帧就交给 <paramref name="onText"/>；它抛出的异常（解析失败）
    /// 在这里兜住并记日志，**不会**中断接收循环 —— 与原实现包住 <c>Dispatch</c> 的那层
    /// catch 位置、文案完全一致。
    /// </summary>
    public async Task<WebSocketCloseStatus?> ReceiveLoopAsync(ClientWebSocket ws, Action<string> onText,
                                                             CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            sb.Clear();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // 关闭码就在这条接收结果上：.NET 解析到关闭帧时已经填好
                    // （ManagedWebSocket 里是 _closeStatus → WebSocketReceiveResult
                    // 的 CloseStatus），比事后读 ws.CloseStatus 更贴这一刻的现场。
                    // 4000 属私有区间，枚举里没有名字，拿到的就是数值本身。
                    var code = result.CloseStatus ?? ws.CloseStatus;
                    try
                    {
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", ct)
                                .ConfigureAwait(false);
                    }
                    catch { }
                    return code;
                }
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            try
            {
                onText(sb.ToString());
            }
            catch (Exception ex)
            {
                _log?.Invoke("消息解析失败：" + ex.Message);
            }
        }

        return null;      // 本地取消 / 连接已经不可用，没有服务端关闭码
    }

    /// <summary>掐掉连接，逼主循环重连（发送侧卡死时唯一能自救的手段）。</summary>
    public static void TryAbort(ClientWebSocket ws)
    {
        try { ws.Abort(); } catch { }
    }

    /// <summary>给日志用的连接状态描述（排查时能直接看出卡在哪）。</summary>
    public static string Describe(ClientWebSocket? ws) =>
        ws is null ? "无连接对象" : ws.State.ToString();
}
