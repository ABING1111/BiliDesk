using System;
using System.Collections.Generic;
using BiliDesk.Models;

namespace BiliDesk.Helpers;

/// <summary>
/// UP 主主页投稿列表的排序方式(2026-11-28, issue #131)。
///
/// 用户要求"在 UP 主主页能按播放量排序"。B 站空间投稿接口
/// (<c>/x/space/wbi/arc/search</c>) 原生支持 <c>order</c> 参数:
///   · <c>pubdate</c> = 最新发布(接口默认, 也是本应用一直以来的行为)
///   · <c>click</c>   = 最多播放
///   · <c>stow</c>    = 最多收藏
/// 所以主路径**不需要本地排序**, 直接把 order 发上去, 翻页也仍然正确
/// (这一点很关键: 卡片墙是零虚拟化的, 一次只有 30 条, 本地排序只能排当前页)。
///
/// ★ 降级路径不是这样: <c>/x/series/recArchivesByKeywords</c> **没有 order 参数**,
///   而且它只下发 <c>stat.view</c>(没有收藏数)。所以:
///     · 最多播放 → 能按 ViewCount 本地排, 这一页内的顺序是对的;
///     · 最多收藏 → **拿不到收藏数**, 退化成按播放量排(见 <see cref="LocalSort"/> 的说明)。
///   页面会同时显示一句"接口受限时仅对当前页排序"提醒, 不让用户以为整库都排好了。
///
/// ★ 为什么抽成纯函数: 枚举 ↔ 接口字符串的映射错一个字就是"点了没反应"
///   (接口会忽略不认识的 order 值, 静默按 pubdate 返回), 这种错只能靠单测拦住。
/// </summary>
public enum SpaceVideoOrder
{
    /// <summary>最新发布(接口 order=pubdate; 与改动前的行为一致)</summary>
    Latest = 0,

    /// <summary>最多播放(接口 order=click)</summary>
    MostPlayed = 1,

    /// <summary>最多收藏(接口 order=stow)</summary>
    MostFavorited = 2,
}

/// <summary>空间投稿排序的纯逻辑(枚举映射 / 本地兜底排序)。见 <see cref="SpaceVideoOrder"/>。</summary>
public static class SpaceVideoSort
{
    /// <summary>界面上从左到右的排序项(顺序即按钮顺序)。</summary>
    public static readonly SpaceVideoOrder[] All =
    {
        SpaceVideoOrder.Latest,
        SpaceVideoOrder.MostPlayed,
        SpaceVideoOrder.MostFavorited,
    };

    /// <summary>发给 B 站接口的 <c>order</c> 字符串。</summary>
    /// <remarks>
    /// ★ 只认这三个值。拼错时接口**不会报错**, 而是静默按 pubdate 返回 ——
    ///   表现就是"点了最多播放, 顺序没变", 排查时很难想到是字符串写错了。
    /// </remarks>
    public static string ToApiOrder(SpaceVideoOrder order) => order switch
    {
        SpaceVideoOrder.MostPlayed => "click",
        SpaceVideoOrder.MostFavorited => "stow",
        _ => "pubdate",
    };

    /// <summary>界面上的按钮文字。</summary>
    public static string Label(SpaceVideoOrder order) => order switch
    {
        SpaceVideoOrder.MostPlayed => "最多播放",
        SpaceVideoOrder.MostFavorited => "最多收藏",
        _ => "最新发布",
    };

    /// <summary>
    /// 把任意 int 收敛成合法排序(配置/URL 里可能存着越界值)。
    /// 与 <see cref="FastForwardSpeedPolicy.Normalize"/> 同一思路: 认不出就回落默认。
    /// </summary>
    public static SpaceVideoOrder Normalize(int value)
        => Enum.IsDefined(typeof(SpaceVideoOrder), value) ? (SpaceVideoOrder)value : SpaceVideoOrder.Latest;

    /// <summary>
    /// **降级路径专用**: 对已拿到的一页结果按排序方式原地重排。
    ///
    /// ★ 只用于 <c>recArchivesByKeywords</c> 那条降级链路 —— 主路径必须靠服务端 order,
    ///   因为这里只能排到"当前这一页的 30 条", 跨页顺序无从得知。
    /// ★ 最多收藏没有收藏数字段可用(降级接口只给 stat.view), 退化成按播放量降序 ——
    ///   这是"聊胜于无"的兜底, UI 上有对应提示, 不要让用户以为收藏序是准的。
    /// ★ 最新发布按 Pubdate 降序(而不是保持接口原序): 降级接口自己按推荐序返回,
    ///   不重排的话"最新发布"这个按钮点下去看不出任何变化。
    /// </summary>
    public static void LocalSort(List<VideoItem> items, SpaceVideoOrder order)
    {
        if (items == null || items.Count < 2) return;

        switch (order)
        {
            case SpaceVideoOrder.MostPlayed:
            case SpaceVideoOrder.MostFavorited:
                // List.Sort 不是稳定排序, 播放量相同时顺序会抖; 用 bvid 兜底保证可复现。
                items.Sort((a, b) =>
                {
                    var byView = b.ViewCount.CompareTo(a.ViewCount);
                    return byView != 0 ? byView : string.CompareOrdinal(a.Bvid, b.Bvid);
                });
                break;

            case SpaceVideoOrder.Latest:
            default:
                items.Sort((a, b) =>
                {
                    var byTime = b.Pubdate.CompareTo(a.Pubdate);
                    return byTime != 0 ? byTime : string.CompareOrdinal(a.Bvid, b.Bvid);
                });
                break;
        }
    }

    /// <summary>
    /// 降级路径下, 这个排序方式是否**做不到**(需要提示用户)。
    /// 目前只有"最多收藏": 降级接口不下发收藏数。
    /// </summary>
    public static bool IsApproximateInFallback(SpaceVideoOrder order)
        => order == SpaceVideoOrder.MostFavorited;
}
