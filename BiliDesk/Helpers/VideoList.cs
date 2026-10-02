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
    /// 去重追加。返回真正加进去的条数。
    ///
    /// 去重键分两种(2026-10-03 加直播搜索时暴露出来的):
    ///   · 视频 → `bvid`
    ///   · 直播间 → `roomId`(直播间**没有 bvid**)
    /// ★★ 别退回"只看 bvid": 那样直播条目会被整批丢掉, 而调用方拿到的 added 是 0、
    ///    界面上一片空白, 但 HasMore 却是 true —— 正是"看起来成功但内容不对"。
    ///    实测就是这么踩到的(`%TEMP%\bd-probe-kindsui` 的搜索页直播类别结果数为 0)。
    /// ★ 两种都没有的条目(接口给的推广位等)才跳过 —— 那种条目点开也播不了。
    ///
    /// 实现是 HashSet 的 O(n): 每追加一批都重建一次索引, 但比起"每条都在列表里线性找一遍"
    /// (列表滚到几百条时就是几万次比较)划算得多 —— 这是推荐流滚动几十页之后的主要开销之一。
    /// </summary>
    public static int AppendDistinct(ObservableCollection<VideoItem> target, IEnumerable<VideoItem> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in target)
        {
            var k = KeyOf(v);
            if (k != null) seen.Add(k);
        }

        var added = 0;
        foreach (var v in items)
        {
            var key = KeyOf(v);
            if (key == null || !seen.Add(key)) continue;
            target.Add(v);
            added++;
        }
        return added;
    }

    /// <summary>去重键: 视频用 bvid, 直播用房间号; 两者都没有则返回 null(表示"这条不该进列表")</summary>
    private static string? KeyOf(VideoItem v)
    {
        if (!string.IsNullOrEmpty(v.Bvid)) return "b:" + v.Bvid;
        if (v.RoomId > 0) return "l:" + v.RoomId;
        return null;
    }
}
