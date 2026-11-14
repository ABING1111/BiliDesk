using System;
using System.IO;
using System.Linq;
using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 封面图磁盘缓存的自动清理规则(2026-11-14 新增)。
///
/// ★ 为什么可以单测: 规则本体是 <see cref="CoverCachePrune.PruneDirectory"/> 的纯逻辑,
///   不碰 BitmapImage / UI; 阈值和时间都由参数注入, 不依赖真实用户目录。
/// ★ 这里钉住四条最容易改错的语义:
///   ① 超龄删、近期留;
///   ② 超个数/超体积时**从最旧的开始删**, 且删到 80% 水位(不是删一张就停);
///   ③ 刚写入的文件(**1 小时内)一张都不能删** —— 它们可能正在被解码;
///   ④ 只碰 .jpg, 不误删目录里别的东西。
/// </summary>
public class CoverCachePruneTests
{
    private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan NoFreshGuard = TimeSpan.Zero;
    private static readonly TimeSpan LongAge = TimeSpan.FromDays(3650);

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bd-cacheprune-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void MakeFile(string dir, string name, int bytes, DateTime writeUtc)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        // 内容长度就是体积; 写入时间用 Set 显式钉住, 避免依赖创建时刻
        File.SetLastWriteTimeUtc(path, writeUtc);
    }

    private static long Prune(string dir,
        long maxBytes = 1024L * 1024 * 1024,
        int maxFiles = 2000,
        TimeSpan? maxAge = null,
        TimeSpan? freshGuard = null)
        => CoverCachePrune.PruneDirectory(dir, Now, maxBytes, maxFiles,
            maxAge ?? LongAge, freshGuard ?? NoFreshGuard);

    [Fact]
    public void 超龄文件删除_近期文件保留_并返回释放的字节数()
    {
        var dir = NewTempDir();
        try
        {
            MakeFile(dir, "old.jpg", 500, Now.AddDays(-60));
            MakeFile(dir, "keep.jpg", 300, Now.AddDays(-5));
            MakeFile(dir, "fresh.jpg", 200, Now.AddMinutes(-10));

            var freed = Prune(dir, maxAge: TimeSpan.FromDays(30), freshGuard: TimeSpan.FromHours(1));

            Assert.Equal(500, freed);
            Assert.False(File.Exists(Path.Combine(dir, "old.jpg")));
            Assert.True(File.Exists(Path.Combine(dir, "keep.jpg")));
            Assert.True(File.Exists(Path.Combine(dir, "fresh.jpg")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 超个数时从最旧的开始删_并删到80percent水位()
    {
        var dir = NewTempDir();
        try
        {
            // 20 个文件, 0 号最新、19 号最旧; 上限 10 个 ⇒ 目标水位 8 个
            for (var i = 0; i < 20; i++)
                MakeFile(dir, $"f{i}.jpg", 100, Now.AddDays(-i));

            var freed = Prune(dir, maxFiles: 10);

            var left = Directory.GetFiles(dir, "*.jpg").Select(Path.GetFileName).ToHashSet();
            Assert.Equal(8, left.Count);
            // 留下的必须是最新的 8 个(0~7), 旧的 8~19 全部删掉
            for (var i = 0; i < 8; i++) Assert.Contains($"f{i}.jpg", left);
            for (var i = 8; i < 20; i++) Assert.DoesNotContain($"f{i}.jpg", left);
            Assert.Equal(12 * 100, freed);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 超体积时从最旧的开始删_直到回到水位以下()
    {
        var dir = NewTempDir();
        try
        {
            // 10 × 200B = 2000B; 上限 1000B ⇒ 目标水位 800B ⇒ 删掉最旧的 6 个
            for (var i = 0; i < 10; i++)
                MakeFile(dir, $"f{i}.jpg", 200, Now.AddDays(-i));

            var freed = Prune(dir, maxBytes: 1000, maxFiles: 1000);

            var left = Directory.GetFiles(dir, "*.jpg").Select(Path.GetFileName).ToHashSet();
            Assert.Equal(4, left.Count);
            Assert.Equal(4 * 200L, Directory.GetFiles(dir, "*.jpg").Sum(f => new FileInfo(f).Length));
            for (var i = 0; i < 4; i++) Assert.Contains($"f{i}.jpg", left);
            Assert.Equal(6 * 200, freed);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 一小时内新写入的文件即使超限也不删_避免删掉正在解码的那张()
    {
        var dir = NewTempDir();
        try
        {
            // 上限故意设成 1 个 / 1 字节, 三个文件全在 FreshGuard 内
            for (var i = 0; i < 3; i++)
                MakeFile(dir, $"fresh{i}.jpg", 100, Now.AddMinutes(-10 - i));

            var freed = Prune(dir, maxBytes: 1, maxFiles: 1,
                maxAge: TimeSpan.FromDays(30), freshGuard: TimeSpan.FromHours(1));

            Assert.Equal(0, freed);
            Assert.Equal(3, Directory.GetFiles(dir, "*.jpg").Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 零字节残留会被清掉_非jpg文件不归这里管()
    {
        var dir = NewTempDir();
        try
        {
            MakeFile(dir, "broken.jpg", 0, Now.AddDays(-2));
            MakeFile(dir, "old.png", 1000, Now.AddDays(-60));
            MakeFile(dir, "fresh.jpg", 100, Now.AddMinutes(-10));

            var freed = Prune(dir, maxAge: TimeSpan.FromDays(30), freshGuard: TimeSpan.FromHours(1));

            Assert.Equal(0, freed);   // 0 字节文件释放不了空间
            Assert.False(File.Exists(Path.Combine(dir, "broken.jpg")));
            // 只匹配 *.jpg: 同一个目录里的 png 不是封面缓存, 绝不能顺手删掉
            Assert.True(File.Exists(Path.Combine(dir, "old.png")));
            Assert.True(File.Exists(Path.Combine(dir, "fresh.jpg")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 目录不存在时返回0且不抛异常()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bd-cacheprune-missing-" + Guid.NewGuid().ToString("N"));

        var freed = CoverCachePrune.PruneDirectory(dir, Now,
            1024, 10, TimeSpan.FromDays(30), TimeSpan.FromHours(1));

        Assert.Equal(0, freed);
    }
}
