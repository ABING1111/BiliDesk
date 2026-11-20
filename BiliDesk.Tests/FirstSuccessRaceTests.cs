using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 「并发试多条路径, 取最先成功的一条」(2026-11-20)。
///
/// ★ 这里锁住的是**并发收尾**的契约, 它才是这段代码真正的坑:
///   胜出者之外那些"也连上了"的结果必须被释放, 否则每进一次直播间漏一个 WebSocket。
///   用假委托 + 计数验, 比真连 B 站可靠得多。
/// </summary>
public class FirstSuccessRaceTests
{
    private sealed class Conn
    {
        public readonly string Name;
        public bool Disposed;
        public Conn(string name) => Name = name;
    }

    /// <summary>造一个"立刻成功"的尝试</summary>
    private static Func<CancellationToken, Task<Conn?>> Win(string name, List<Conn> created)
        => _ =>
        {
            var c = new Conn(name);
            created.Add(c);
            return Task.FromResult<Conn?>(c);
        };

    /// <summary>造一个"永远失败"的尝试</summary>
    private static Func<CancellationToken, Task<Conn?>> Fail()
        => _ => Task.FromResult<Conn?>(null);

    /// <summary>造一个"等取消才结束"的尝试(模拟还在握手中)</summary>
    private static Func<CancellationToken, Task<Conn?>> Pending()
        => async token =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
                return null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        };

    private static Task<Conn?> Run(List<Func<CancellationToken, Task<Conn?>>> attempts,
        List<Conn>? discarded = null, int timeoutMs = 0)
        => FirstSuccessRace.RunAsync<Conn>(
            attempts,
            discard: c => { c.Disposed = true; discarded?.Add(c); },
            timeoutMs: timeoutMs);

    [Fact]
    public async Task 第一个成功者胜出()
    {
        var created = new List<Conn>();
        var result = await Run(new List<Func<CancellationToken, Task<Conn?>>>
        {
            Win("a", created), Fail(), Fail(),
        });

        Assert.NotNull(result);
        Assert.Equal("a", result!.Name);
    }

    [Fact]
    public async Task 前面的失败不会拖住后面的成功()
    {
        // 这正是"依次试"要治的病: 首选不通时不能挡住第二台
        var created = new List<Conn>();
        var result = await Run(new List<Func<CancellationToken, Task<Conn?>>>
        {
            Fail(), Fail(), Win("c", created),
        });

        Assert.Equal("c", result?.Name);
    }

    [Fact]
    public async Task 全部失败返回null()
    {
        var result = await Run(new List<Func<CancellationToken, Task<Conn?>>>
        {
            Fail(), Fail(),
        });
        Assert.Null(result);
    }

    [Fact]
    public async Task 空列表返回null()
    {
        var result = await Run(new List<Func<CancellationToken, Task<Conn?>>>());
        Assert.Null(result);
    }

    [Fact]
    public async Task 胜出后取消其余在途尝试()
    {
        var created = new List<Conn>();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var result = await Run(new List<Func<CancellationToken, Task<Conn?>>>
        {
            Pending(), Win("b", created),
        });

        Assert.Equal("b", result?.Name);
        // 若没取消, Pending 会永远挂着 —— 这里能返回就说明取消生效了
        Assert.True(sw.ElapsedMilliseconds < 3_000, $"应当立刻返回, 实际 {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task 两个几乎同时成功_只留一个_另一个被释放()
    {
        // ★ 这是最容易漏的一条: 两次握手都在取消信号到达前完成了。
        //   不释放的话, 那个多余的 WebSocket 就永远悬着了。
        var created = new List<Conn>();
        var discarded = new List<Conn>();

        var result = await Run(new List<Func<CancellationToken, Task<Conn?>>>
        {
            Win("a", created), Win("b", created),
        }, discarded);

        Assert.NotNull(result);
        Assert.Single(discarded);
        Assert.True(discarded[0].Disposed, "输掉但仍连上的那个必须被释放");
        Assert.NotEqual(result!.Name, discarded[0].Name);
    }

    [Fact]
    public async Task 整体超时返回null而不是挂住()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await Run(new List<Func<CancellationToken, Task<Conn?>>>
        {
            Pending(), Pending(),
        }, timeoutMs: 150);

        Assert.Null(result);
        Assert.True(sw.ElapsedMilliseconds < 3_000, $"超时必须生效, 实际 {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task 外部取消能立刻打断()
    {
        using var cts = new CancellationTokenSource();
        var task = FirstSuccessRace.RunAsync<Conn>(
            new List<Func<CancellationToken, Task<Conn?>>> { Pending(), Pending() },
            discard: null,
            timeoutMs: 0,
            ct: cts.Token);

        cts.CancelAfter(50);
        var result = await task;
        Assert.Null(result);
    }

    [Fact]
    public async Task 尝试同步抛异常不影响其它路径()
    {
        var created = new List<Conn>();
        var result = await Run(new List<Func<CancellationToken, Task<Conn?>>>
        {
            _ => throw new InvalidOperationException("boom"),
            Win("b", created),
        });

        Assert.Equal("b", result?.Name);
    }
}
