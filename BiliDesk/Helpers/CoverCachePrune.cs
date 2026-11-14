using System;
using System.Collections.Generic;
using System.IO;

namespace BiliDesk.Helpers;

/// <summary>
/// 封面图磁盘缓存(imgcache)的**自动清理策略** —— 纯逻辑、不依赖 WPF, 单测直接调。
///
/// ★ 为什么单独拆一个类, 而不是写进 <see cref="CoverLoader"/>:
///   ① <see cref="CoverLoader"/> 里全是 BitmapImage / UI 类型, 测试工程约定"只测纯逻辑类";
///   ② 清理规则(天数/个数/体积)要和设置页的说明文字**共用同一份常量**,
///      散在两个文件里迟早会出现"界面写 30 天、代码删 14 天"。
///
/// 规则(用户 2026-11-14 选定):
///   · 超过 <see cref="MaxAge"/> 没写入过的 → 删;
///   · 再按**最久没写入**优先删, 直到 ≤ <see cref="MaxFiles"/> 个 且 ≤ <see cref="MaxTotalBytes"/>;
///   · 一次清理删到 80% 水位, 避免"每下载一张就触发一次清理"的抖动;
///   · <see cref="FreshGuard"/> 内刚写入的文件一律不删 —— 它们可能正在被解码。
///
/// ★ 为什么用 LastWriteTimeUtc 而不是 LastAccessTimeUtc:
///   Windows 默认关闭 NTFS 的"最后访问时间"更新, 读一次文件根本不会刷新访问时间,
///   拿它当 LRU 等于永远按"下载时间"排序。既然如此就明确用写入时间, 行为可预期。
///   代价是"很久以前缓存、最近又常看"的图可能被重下一遍 —— 一张 284KB, 可以接受。
///
/// ★ 并发安全性: 删除失败(文件正被解码占用, Windows 不允许删)一律跳过,
///   返回的"释放字节数"只统计**真的删掉了**的部分。调用方不需要加锁。
/// </summary>
public static class CoverCachePrune
{
    // 下面四个阈值是公开的: 设置页要把"自动清理规则"原样写给用户看,
    // 数字散成两处迟早会对不上, 所以只留这一份(与 ShortVideoCache 的做法一致)。

    /// <summary>超过这么久没写入过就删。</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    /// <summary>缓存文件个数上限。</summary>
    public const int MaxFiles = 2000;

    /// <summary>缓存总体积上限。按实测 284KB/张, 300MB 约 1000 张, 够覆盖多屏来回滚动。</summary>
    public const long MaxTotalBytes = 300L * 1024 * 1024;

    /// <summary>
    /// 刚写入多久内的文件不参与清理。
    /// 为什么需要: 下载完成 → 写盘 → 解码之间有个窗口期, 这期间文件虽然"旧不到哪去",
    /// 但若恰好赶上一次清理且超了上限, 删掉它会让这一轮解码失败(封面灰一下)。
    /// 1 小时远大于任何一次正常的"下载 + 解码", 又不会让刚攒下的垃圾长期留下。
    /// </summary>
    public static readonly TimeSpan FreshGuard = TimeSpan.FromHours(1);

    /// <summary>
    /// 清理到上限的百分之多少。留 20% 余量是为了避免"每加一张就淘汰一张"的抖动
    /// (与 <see cref="CoverLoader"/> 内存 LRU 的 80% 水位同理)。
    /// </summary>
    private const int TargetPercent = 80;

    /// <summary>
    /// 清一个目录。返回**实际释放**的字节数; 目录不存在/无权限时返回 0, 不抛异常。
    /// </summary>
    /// <param name="dir">缓存目录(通常是 <c>AppPaths.CacheDir</c>)</param>
    /// <param name="nowUtc">当前 UTC 时间(传入而不是内部取, 单测才能构造"60 天前")</param>
    /// <param name="maxBytes">总体积上限</param>
    /// <param name="maxFiles">文件个数上限</param>
    /// <param name="maxAge">最长保留时间</param>
    /// <param name="freshGuard">刚写入多久内的文件不删</param>
    public static long PruneDirectory(string dir, DateTime nowUtc,
        long maxBytes, int maxFiles, TimeSpan maxAge, TimeSpan freshGuard)
    {
        if (string.IsNullOrWhiteSpace(dir)) return 0;

        DirectoryInfo di;
        try
        {
            di = new DirectoryInfo(dir);
            if (!di.Exists) return 0;
        }
        catch
        {
            return 0;
        }

        long freed = 0;
        long totalBytes = 0;
        var totalCount = 0;
        var freshCount = 0;
        var candidates = new List<FileInfo>();

        try
        {
            foreach (var f in di.EnumerateFiles("*.jpg", SearchOption.TopDirectoryOnly))
            {
                long len;
                DateTime writeUtc;
                try
                {
                    len = f.Length;
                    writeUtc = f.LastWriteTimeUtc;
                }
                catch
                {
                    // 枚举过程中被别的线程删了 —— 跳过, 不要因为一个文件中断整轮清理
                    continue;
                }

                totalCount++;
                totalBytes += len;

                var age = nowUtc - writeUtc;
                // ★ 新文件一律留下: 哪怕总量已经超了, 也不能删"可能正在解码"的那张。
                //   刚写入的文件最多只占一小时内的下载量, 不会把缓存永久顶在上限之上。
                if (age < freshGuard)
                {
                    freshCount++;
                    continue;
                }

                // 0 字节 = 上次写盘被中断留下的残渣; 过期文件同样直接删。
                // 这两种都不进候选, 所以不受 80% 水位影响 —— 该删就删。
                // ★ 只有**真的删掉**才从总量里扣: 被占用的文件还在目录里, 不能当它不存在。
                if (len <= 0 || age > maxAge)
                {
                    if (TryDelete(f.FullName))
                    {
                        freed += len;
                        totalCount--;
                        totalBytes -= len;
                    }
                    continue;
                }

                candidates.Add(f);
            }
        }
        catch
        {
            // 目录读取失败(权限/被占用): 已经删掉的部分照常返回
            return freed;
        }

        if (totalCount <= maxFiles && totalBytes <= maxBytes) return freed;

        // ★ 目标水位按"上限的 80%"算, 但不低于 freshCount:
        //   新文件不能删, 若目标比它们还少, 循环会白跑一遍(最终也删不到目标)。
        var targetCount = Math.Max(maxFiles * TargetPercent / 100, freshCount);
        var targetBytes = maxBytes * TargetPercent / 100;

        candidates.Sort((x, y) =>
        {
            try { return x.LastWriteTimeUtc.CompareTo(y.LastWriteTimeUtc); }
            catch { return 0; }
        });

        foreach (var f in candidates)
        {
            if (totalCount <= targetCount && totalBytes <= targetBytes) break;

            long len;
            try { len = f.Length; } catch { continue; }
            if (!TryDelete(f.FullName)) continue;   // 被占用: 算术上不算删掉
            freed += len;
            totalCount--;
            totalBytes -= len;
        }

        return freed;
    }

    /// <summary>删除成功返回 true(文件本来就不存在也算成功)。</summary>
    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return true;
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
