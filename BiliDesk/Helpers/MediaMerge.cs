using System;
using System.Collections.Generic;
using BiliDesk.Models;

namespace BiliDesk.Helpers;

/// <summary>
/// 影视页「正在热播」的合并规则(2026-11-28)。
///
/// 需求是"一栏里同时看到电影 / 电视剧 / 纪录片 / 综艺", 但 PGC 索引接口一次只能按
/// 一个 season_type 查。所以先分别拿四类(各自按"最近更新"排序), 再按**轮转**交错合并:
/// 电影第 1 条 → 电视剧第 1 条 → 纪录片第 1 条 → 综艺第 1 条 → 电影第 2 条……
/// 这样第一屏不会全是电影, 也不会因为某一类更新特别多而把其它类挤没。
///
/// ★ 去重按 season_id: 同一部剧可能同时出现在两个分类的返回里(实测综艺/纪录片偶发),
///   不去重会在这一栏里出现两张一模一样的卡。
/// ★ take 是"最多要多少条", 不是"每一类取多少条"。
/// </summary>
public static class MediaMerge
{
    /// <summary>
    /// 把多组条目轮流交错合并、按 season_id 去重, 最多取 <paramref name="take"/> 条。
    /// 空组会被跳过; 某一组先耗尽后, 其余组继续按顺序补, 不会提前停。
    /// </summary>
    public static List<BangumiItem> Interleave(
        IReadOnlyList<IReadOnlyList<BangumiItem>> rows, int take)
    {
        var result = new List<BangumiItem>();
        if (rows == null || rows.Count == 0 || take <= 0) return result;

        var seen = new HashSet<long>();
        var maxCount = 0;
        foreach (var row in rows)
        {
            if (row != null && row.Count > maxCount) maxCount = row.Count;
        }

        for (var i = 0; i < maxCount && result.Count < take; i++)
        {
            foreach (var row in rows)
            {
                if (row == null || i >= row.Count) continue;
                var item = row[i];
                if (item == null || item.SeasonId <= 0) continue;
                if (!seen.Add(item.SeasonId)) continue;
                result.Add(item);
                if (result.Count >= take) break;
            }
        }

        return result;
    }
}
