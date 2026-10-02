using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using BiliDesk.Models;

namespace BiliDesk.Helpers;

/// <summary>
/// 视频列表的公共小工具。
///
/// 为什么要有它: "按 bvid 去重追加"这件事在好几处分页列表里都要做(推荐流、搜索结果…),
/// 而**搜索结果尤其需要** —— B 站的搜索排序每次请求都可能变(同一个稿件在前一页出现过,
/// 翻下一页又给你一遍), 不去重就会在列表里出现两张一模一样的卡片。
/// </summary>
public static class VideoList
{
    /// <summary>
    /// 按 Bvid 去重追加。返回真正加进去的条数。
    ///
    /// 实现是 HashSet 的 O(n): 每追加一批都重建一次索引, 但比起"每条都在列表里线性找一遍"
    /// (列表滚到几百条时就是几万次比较)划算得多 —— 这是推荐流滚动几十页之后的主要开销之一。
    /// </summary>
    public static int AppendDistinct(ObservableCollection<VideoItem> target, IEnumerable<VideoItem> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in target) seen.Add(v.Bvid);

        var added = 0;
        foreach (var v in items)
        {
            if (string.IsNullOrEmpty(v.Bvid) || !seen.Add(v.Bvid)) continue;
            target.Add(v);
            added++;
        }
        return added;
    }
}
