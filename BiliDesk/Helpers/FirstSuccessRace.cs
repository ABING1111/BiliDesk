using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BiliDesk.Helpers;

/// <summary>
/// 「并发尝试多条路径, 取最先成功的那一条」(2026-11-20 新增)。
///
/// ★ 为什么需要: 直播弹幕的候选服务器是**依次**试的, 每个还有 8 秒超时 ——
///   首选那台不通时, 用户要对着空白弹幕区等满 8 秒才开始试第二台。并发之后
///   最坏等待回到"一次握手"的量级, 而正常情况(首选就通)完全不受影响。
///
/// ★ 为什么单独抽成纯协调逻辑(不塞进 LiveDanmakuService):
///   这里真正容易出错的是**并发下的收尾** —— 胜出者之外的连接必须被释放,
///   否则每进一次直播间就漏一个 WebSocket(握手成功但没人持有的那种)。
///   把它和网络调用解耦后, 可以直接用假的委托验"谁胜出、谁被释放", 不必真连 B 站。
///
/// ★ 语义:
///   · 所有 <paramref name="attempts"/> 立刻并发启动;
///   · 第一个返回非 null 的结果胜出, 并**立刻取消**其余尝试(它们通常还在握手);
///   · 取消之后仍然握手成功的那些, 用 <paramref name="discard"/> 释放掉;
///   · 全部失败/全部超时返回 null。
/// </summary>
public static class FirstSuccessRace
{
    /// <summary>
    /// 并发跑完所有尝试, 返回第一个非 null 结果。
    /// </summary>
    /// <param name="attempts">各条路径的启动委托; 收到的 token 会在"别人已胜出"或超时时取消。</param>
    /// <param name="discard">
    /// 释放"输了但仍然成功"的结果。必须提供 —— 不释放就是资源泄漏(见类注释)。
    /// </param>
    /// <param name="timeoutMs">整体截止时间(毫秒)。&lt;=0 表示不设整体上限(靠 <paramref name="ct"/> 控制)。</param>
    /// <param name="ct">外部取消(换房间/关窗)。</param>
    public static async Task<T?> RunAsync<T>(
        IReadOnlyList<Func<CancellationToken, Task<T?>>> attempts,
        Action<T>? discard,
        int timeoutMs,
        CancellationToken ct = default) where T : class
    {
        if (attempts == null || attempts.Count == 0) return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs > 0) cts.CancelAfter(timeoutMs);

        var pending = new List<Task<T?>>(attempts.Count);
        foreach (var attempt in attempts)
        {
            try
            {
                pending.Add(attempt(cts.Token));
            }
            catch
            {
                // 同步就抛(参数错误/对象已释放): 这一路直接作废, 不影响其它路
            }
        }

        T? winner = null;

        // ★ 必须把 pending 全部排空再返回: 提前 return 会留下没人 await 的 Task,
        //   它们之后若抛异常就成了"未观察的异常"(在某些同步上下文里会升级成崩溃)。
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);

            T? result = null;
            try { result = await done.ConfigureAwait(false); }
            catch { /* 这一路失败(含被取消): 让其它路继续 */ }

            if (result is null) continue;

            if (winner is null)
            {
                winner = result;
                // 立刻取消其余: 它们多半还在握手, 取消后会在自己的 catch 里释放 socket,
                // 不必干等满 8 秒超时。
                cts.Cancel();
            }
            else
            {
                // 后来者也连上了(两次握手几乎同时完成): 只留第一个, 其余立刻释放。
                discard?.Invoke(result);
            }
        }

        return winner;
    }
}
