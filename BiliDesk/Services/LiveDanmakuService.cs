using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BiliDesk.Helpers;
using BiliDesk.Models;

namespace BiliDesk.Services;

/// <summary>
/// 直播弹幕接收服务(2026-11-13 新增): 连接 B 站弹幕 WebSocket, 把普通聊天弹幕抛给播放器。
///
/// ★ 生命周期: **每个播放器窗口一个实例**。不做全局单例 —— 弹幕连接与"当前在看哪个直播间"
///   强绑定, 单例会在换窗口/换直播间时留下没人认领的连接, 而且拿不到窗口关闭信号。
///
/// ★ 线程模型: 整个服务跑在后台 Task 上, <see cref="Received"/> / <see cref="StateChanged"/>
///   都在**后台线程**触发。订阅方(PlayerWindow)只做两件事: 入队 / Dispatcher 切回 UI,
///   绝不能在事件里直接碰 WPF 对象。
///
/// ★ 失败策略: 所有异常都在内部吞掉并转成状态事件。直播弹幕是**附加功能**,
///   任何情况下都不允许影响视频播放。
/// </summary>
public sealed class LiveDanmakuService : IDisposable
{
    private const int MaxReconnect = 3;

    /// <summary>重连退避(毫秒)。每次重连都要重新取 token —— token 会过期。</summary>
    private static readonly int[] ReconnectBackoffMs = { 2_000, 5_000, 10_000 };

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private const int ConnectTimeoutMs = 8_000;
    private const int MaxMessageBytes = 4 * 1024 * 1024;

    /// <summary>连上后这么快就断开, 基本可以判定"没登录被踢"(而不是网络抖动)。</summary>
    private const long FastDropWindowMs = 3_000;

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _roomId;
    private int _disposed;

    /// <summary>当前连接的房间号(0 = 没有)</summary>
    public long RoomId => Interlocked.Read(ref _roomId);

    /// <summary>是否有一条连接任务在跑(不代表已经鉴权成功)</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _loop is { IsCompleted: false }; }
    }

    /// <summary>收到一条普通聊天弹幕(后台线程触发)</summary>
    public event Action<LiveDanmakuItem>? Received;

    /// <summary>连接状态变化(后台线程触发)</summary>
    public event Action<LiveDanmakuConnectionState, string?>? StateChanged;

    /// <summary>
    /// 开始接收某个直播间的弹幕。重复调用会先取消上一条连接。
    /// 立即返回 —— 真正的连接/重连全在后台跑。
    /// </summary>
    public void Start(long roomId)
    {
        if (roomId <= 0 || Volatile.Read(ref _disposed) != 0) return;

        lock (_gate)
        {
            // 旧连接的取消交给它自己的收尾(finally 里释放 CTS), 这里只发信号。
            _cts?.Cancel();
            _roomId = roomId;
            var cts = new CancellationTokenSource();
            _cts = cts;
            _loop = Task.Run(() => RunAsync(roomId, cts));
        }
    }

    /// <summary>
    /// 停止接收并等待后台循环退出。关窗路径上是 fire-and-forget 调用, 所以内部吞掉所有异常。
    /// </summary>
    public async Task StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            _cts?.Cancel();
            loop = _loop;
            _roomId = 0;
        }

        if (loop == null) return;
        try { await loop.ConfigureAwait(false); }
        catch { /* 收尾异常一律忽略 */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = null;
            _loop = null;
            _roomId = 0;
        }
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------ 主循环(连接 + 重连)

    private async Task RunAsync(long roomId, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var needLoginReported = false;
        try
        {
            for (var attempt = 0; !ct.IsCancellationRequested; attempt++)
            {
                StateChanged?.Invoke(LiveDanmakuConnectionState.Connecting, null);

                // 每次(重)连都重新取房间/密钥: token 有时效, 复用上一次的只会在几十分钟后失败。
                var (ok, err, endpoint) = await Svc.Api.GetLiveDanmakuEndpointAsync(roomId)
                    .ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;
                if (!ok || endpoint == null)
                {
                    // 房间不存在/主播没开播这类硬失败: 重连没有意义, 如实结束。
                    StateChanged?.Invoke(LiveDanmakuConnectionState.Failed, err);
                    break;
                }

                var result = await RunOnceAsync(endpoint, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;

                if (result == RunResult.NeedLogin)
                {
                    // 只提示一次: 重连也照样会被踢, 反复弹提示只是噪音。
                    if (!needLoginReported)
                    {
                        needLoginReported = true;
                        StateChanged?.Invoke(LiveDanmakuConnectionState.NeedLogin, "登录后才能接收直播弹幕");
                    }
                    break;
                }

                if (attempt >= MaxReconnect)
                {
                    StateChanged?.Invoke(LiveDanmakuConnectionState.Disconnected, "直播弹幕连接已断开");
                    break;
                }

                // 退避后重连。取消要能立刻打断等待, 所以用带 token 的 Delay。
                var backoff = ReconnectBackoffMs[Math.Min(attempt, ReconnectBackoffMs.Length - 1)];
                try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            // 只有"还是当前这条"时才把字段清空 —— 否则会把新连接的任务句柄误清掉
            lock (_gate)
            {
                if (ReferenceEquals(_cts, cts))
                {
                    _cts = null;
                    _loop = null;
                }
            }
            cts.Dispose();
        }
    }

    private enum RunResult
    {
        /// <summary>连接正常结束(服务端关闭/网络断开), 可以重连</summary>
        Closed,
        /// <summary>需要登录(未登录被服务端拒绝/踢掉), 不重连</summary>
        NeedLogin,
    }

    // ------------------------------------------------------------ 单次连接

    private async Task<RunResult> RunOnceAsync(LiveDanmakuEndpoint endpoint, CancellationToken ct)
    {
        ClientWebSocket? socket = null;
        var connectedAt = 0L;
        var authenticated = false;
        var gotAnyDanmaku = false;
        try
        {
            socket = await ConnectAsync(endpoint, ct).ConfigureAwait(false);
            if (socket == null) return RunResult.Closed;

            // ---- 鉴权 ----
            var uid = CurrentUid();
            var buvid = Svc.Session.Current.Buvid3 ?? "";
            var auth = LiveDanmakuProtocol.BuildAuthPacket(
                endpoint.RealRoomId, uid, endpoint.Token, buvid);
            await socket.SendAsync(auth.AsMemory(), WebSocketMessageType.Binary, true, ct)
                .ConfigureAwait(false);

            connectedAt = Environment.TickCount64;

            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var heartbeat = HeartbeatLoopAsync(socket, heartbeatCts.Token);
            try
            {
                var buffer = new byte[16 * 1024];
                using var message = new MemoryStream();

                while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    message.SetLength(0);
                    var discard = false;
                    ValueWebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                            return Finish(authenticated);
                        if (result.MessageType != WebSocketMessageType.Binary) continue;

                        if (!discard)
                        {
                            if (message.Length + result.Count > MaxMessageBytes)
                            {
                                // 单帧过大: 丢掉这一整条消息(后面的分片继续读完, 保持协议同步)
                                discard = true;
                                message.SetLength(0);
                            }
                            else
                            {
                                message.Write(buffer, 0, result.Count);
                            }
                        }
                    } while (!result.EndOfMessage);

                    if (discard || message.Length == 0) continue;

                    foreach (var packet in LiveDanmakuProtocol.Decode(message.ToArray()))
                    {
                        switch (packet.Operation)
                        {
                            case LiveDanmakuProtocol.OpAuthReply:
                            {
                                var code = LiveDanmakuProtocol.ParseAuthCode(packet.Body);
                                if (code != 0)
                                {
                                    // 登录态无效: 未登录时这是"需要登录", 登录了则是 token 过期, 交给重连
                                    return Svc.Session.HasLogin ? RunResult.Closed : RunResult.NeedLogin;
                                }
                                authenticated = true;
                                StateChanged?.Invoke(LiveDanmakuConnectionState.Connected, null);
                                break;
                            }
                            case LiveDanmakuProtocol.OpMessage:
                            {
                                var json = Encoding.UTF8.GetString(packet.Body);
                                if (LiveDanmakuProtocol.TryParseDanmaku(json, out var item))
                                {
                                    // ★ 只有"真收到聊天弹幕"才算连接可用: 房间状态/榜单
                                    //   这类 op=5 消息在游客态也会推, 拿它们当判据会漏掉"没登录"。
                                    gotAnyDanmaku = true;
                                    Received?.Invoke(item);
                                }
                                break;
                            }
                        }
                    }
                }

                return Finish(authenticated);
            }
            finally
            {
                heartbeatCts.Cancel();
                try { await heartbeat.ConfigureAwait(false); } catch { /* 取消/已断开 */ }
            }
        }
        catch (OperationCanceledException)
        {
            return RunResult.Closed;
        }
        catch
        {
            return RunResult.Closed;
        }
        finally
        {
            socket?.Dispose();
        }

        // 局部函数: 统一"这一轮怎么结束"的判定(连上不到 3 秒就断 = 大概率没登录被踢)
        RunResult Finish(bool isAuthenticated)
        {
            if (!isAuthenticated) return Svc.Session.HasLogin ? RunResult.Closed : RunResult.NeedLogin;

            // 鉴权成功但立刻被断开、而且一条弹幕都没收到 —— 游客态最典型的表现。
            // 已登录时同样可能发生(服务端负载/房间关闭), 这时按普通断线处理, 交给重连。
            if (!gotAnyDanmaku && Environment.TickCount64 - connectedAt < FastDropWindowMs && !Svc.Session.HasLogin)
                return RunResult.NeedLogin;

            return RunResult.Closed;
        }
    }

    /// <summary>依次尝试 host_list(第一个连不上就换下一个)。全部失败返回 null。</summary>
    private static async Task<ClientWebSocket?> ConnectAsync(LiveDanmakuEndpoint endpoint, CancellationToken ct)
    {
        var hosts = endpoint.Hosts.Count > 0
            ? endpoint.Hosts
            : new List<LiveDanmakuHost>
            {
                new("live.chat.bilibili.com", 2245),
                new("broadcastlv.chat.bilibili.com", 2245),
            };

        foreach (var host in hosts)
        {
            var socket = new ClientWebSocket();
            try
            {
                socket.Options.SetRequestHeader("User-Agent", HttpDefaults.UserAgent);
                socket.Options.SetRequestHeader("Origin", "https://live.bilibili.com");

                // ★ Cookie 必须带: getDanmuInfo 拿到的 token 与请求时的登录态绑定,
                //   不带 Cookie 的连接会在鉴权阶段被踢(未登录时至少也要有 buvid)。
                var cookie = Svc.Session.BuildCookieHeader();
                if (cookie.Length > 0)
                {
                    try { socket.Options.SetRequestHeader("Cookie", cookie); }
                    catch { /* 个别平台不允许直接设 Cookie 头, 退化为无 Cookie 连接 */ }
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ConnectTimeoutMs);
                await socket.ConnectAsync(new Uri($"wss://{host.Host}:{host.Port}/sub"), timeout.Token)
                    .ConfigureAwait(false);
                return socket;
            }
            catch
            {
                socket.Dispose();
                if (ct.IsCancellationRequested) return null;
                // 换下一个 host 继续试
            }
        }
        return null;
    }

    private static async Task HeartbeatLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var packet = LiveDanmakuProtocol.BuildHeartbeatPacket();
        try
        {
            // 先立即发一次: 服务端对"连上后迟迟没有心跳"的连接会主动断开。
            await socket.SendAsync(packet.AsMemory(), WebSocketMessageType.Binary, true, ct)
                .ConfigureAwait(false);
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                await Task.Delay(HeartbeatInterval, ct).ConfigureAwait(false);
                await socket.SendAsync(packet.AsMemory(), WebSocketMessageType.Binary, true, ct)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // 连接已断/已取消: 心跳任务安静退出, 由接收循环负责收尾
        }
    }

    private static long CurrentUid()
    {
        var id = Svc.Session.Current.DedeUserID;
        return long.TryParse(id, out var uid) ? uid : 0;
    }
}
