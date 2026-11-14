using System;
using System.Collections.Generic;
using System.Text.Json;
using BiliDesk.Models;

namespace BiliDesk.Services;

/// <summary>
/// 互动消息(回复我的 / @我的 / 收到的赞)的 JSON 解析。
///
/// ★ 为什么单独抽成静态纯函数类:
///   ① 这三个接口的字段**没有一份可靠的完整文档**(reply 有, at/like 只有零散记录),
///      实测形态还会随服务端灰度变化 —— 必须写得宽容, 而宽容的解析最容易写错;
///   ② 抽出来就能单测: 拿一段真实响应喂进去, 断言"缺字段也不该整页空白"。
///   它不碰网络、不碰 WPF, 只吃 JsonElement。
///
/// ★★ 宽容的三条底线(每条都对应一个真实会遇到的形态):
///   · user / users 两种都见过(点赞合并时是 users 数组) —— 取第一个当主显示人;
///   · 时间字段三套名字(reply_time / at_time / like_time), 统一回落到 item 里的时间;
///   · cursor 既可能是对象 {id,time,is_end}, 也可能只有 offset —— 取不到就按"没有下一页"。
/// </summary>
public static class MsgFeedParser
{
    /// <summary>解析一页互动消息</summary>
    /// <param name="kind">决定默认动作文案与时间字段名</param>
    /// <param name="data">接口返回的 data 对象</param>
    public static MsgFeedPage ParsePage(MsgTab kind, JsonElement data)
    {
        var page = new MsgFeedPage();
        if (data.ValueKind != JsonValueKind.Object) return page;

        // ★★ 「收到的赞」是**另一套结构**(2026-11-14 真实账号实测钉下来的):
        //     data = { latest: { items, last_view_at }, total: { cursor, items } }
        //   —— 没有根级 items, 也没有根级 cursor。latest 是"最近新增的一批",
        //   total 是"全部(带分页游标)"。官方前端把两者**合并去重**后一起显示,
        //   只读其中一个会漏掉另一批(表现为"新收到的赞看不到")。
        if (kind == MsgTab.Like)
        {
            var seen = new HashSet<long>();
            AppendLikeItems(data, "latest", page, seen);
            AppendLikeItems(data, "total", page, seen);

            if (data.TryGetProperty("total", out var total) &&
                total.ValueKind == JsonValueKind.Object)
                ReadCursor(total, page);

            return page;
        }

        if (data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in items.EnumerateArray())
            {
                var item = ParseItem(kind, e);
                if (item != null) page.Items.Add(item);
            }
        }

        ReadCursor(data, page);
        return page;
    }

    /// <summary>把点赞响应里 latest / total 那一层的 items 追加进来(按 id 去重)</summary>
    private static void AppendLikeItems(JsonElement data, string layer, MsgFeedPage page,
        HashSet<long> seen)
    {
        if (!data.TryGetProperty(layer, out var box) || box.ValueKind != JsonValueKind.Object) return;
        if (!box.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return;

        foreach (var e in items.EnumerateArray())
        {
            var item = ParseItem(MsgTab.Like, e);
            if (item == null) continue;
            // 两层里会出现同一条(id 相同), 去重后再按时间倒序插进去
            if (item.Id > 0 && !seen.Add(item.Id)) continue;
            page.Items.Add(item);
        }
    }

    /// <summary>解析一条。返回 null 表示这条既没人也没内容, 不值得显示。</summary>
    public static MsgFeedItem? ParseItem(MsgTab kind, JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;

        var item = new MsgFeedItem
        {
            Id = GetLong(e, "id"),
            ActionText = DefaultAction(kind)
        };

        // ---------------------------------------------------------------- 人
        // 单人: user 对象; 点赞合并: users 数组(取第一个, 其余算进"等 N 人")
        var userCount = 0;
        if (e.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
        {
            ReadUser(item, user);
            userCount = 1;
        }
        else if (e.TryGetProperty("users", out var users) && users.ValueKind == JsonValueKind.Array)
        {
            foreach (var u in users.EnumerateArray())
            {
                if (u.ValueKind != JsonValueKind.Object) continue;
                userCount++;
                if (item.UserMid == 0) ReadUser(item, u);
            }
        }

        // 人数: 只有「点赞」通知需要合并计数, 而且**不能用 counts** ——
        // ★ 2026-11-14 真实账号实测: 点赞项的 counts 是"**被赞内容的总赞数**"
        //   (看到过 3867), 不是"这次合并了几个人"。拿它当人数会显示成
        //   "某某 等 3867 人 赞了你", 读起来像 3867 个人都赞了你。
        //   真正能反映合并规模的是 users 数组(官方前端也是画一排头像)。
        //   reply/at 的 counts 文档写明恒为 1, 那里用它没有歧义。
        var people = kind == MsgTab.Like
            ? userCount
            : Math.Max(GetInt(e, "counts"), userCount);
        if (people > 1) item.ExtraText = $"等 {people} 人";

        // ---------------------------------------------------------------- 详情
        if (e.TryGetProperty("item", out var detail) && detail.ValueKind == JsonValueKind.Object)
            ReadDetail(item, detail);

        // 时间: 按类别取对应字段, 再退回 item 里的通用时间
        item.Time = kind switch
        {
            MsgTab.Reply => GetLong(e, "reply_time"),
            MsgTab.At => GetLong(e, "at_time"),
            MsgTab.Like => GetLong(e, "like_time"),
            _ => 0
        };
        if (item.Time <= 0) item.Time = GetLong(e, "time");
        if (item.Time <= 0 && e.TryGetProperty("item", out var d2) && d2.ValueKind == JsonValueKind.Object)
            item.Time = GetLong(d2, "time");

        item.TimeText = FormatTime(item.Time);

        // 既没有昵称、也没有内容、也没有标题 ⇒ 多半是坏数据, 不占一行
        if (item.UserName.Length == 0 && item.Content.Length == 0 && item.ContextTitle.Length == 0)
            return null;

        return item;
    }

    private static void ReadUser(MsgFeedItem item, JsonElement u)
    {
        item.UserMid = GetLong(u, "mid");
        item.UserName = GetStr(u, "nickname");
        item.UserFace = UrlUtil.Normalize(GetStr(u, "avatar"));
    }

    private static void ReadDetail(MsgFeedItem item, JsonElement d)
    {
        // 别人说了什么: source_content 是"直接回复/被@的那条内容", reply_content 是备选
        var content = GetStr(d, "source_content");
        if (content.Length == 0) content = GetStr(d, "reply_content");
        if (content.Length == 0) content = GetStr(d, "target_reply_content");
        item.Content = content;

        // 来源标题(视频名 / 动态摘要)
        item.ContextTitle = GetStr(d, "title");
        if (item.ContextTitle.Length == 0) item.ContextTitle = GetStr(d, "detail_title");

        // 跳转地址: 优先 web 的 uri, 没有再用 native_uri
        var uri = GetStr(d, "uri");
        if (uri.Length == 0) uri = GetStr(d, "native_uri");
        item.ContextUri = uri;

        item.BusinessId = GetInt(d, "business_id");

        // 能直接拿 BV 号最好(进内置播放器); 拿不到再从 uri 里抠
        var bvid = GetStr(d, "bvid");
        if (bvid.Length == 0) bvid = DynamicParser.ExtractBvidFromJumpUrl(uri);
        item.Bvid = bvid;

        // ★ 点赞通知的 item 里, `title` 是**被赞的那条评论/动态的正文**(不是"来源标题"),
        //   而视频名在 desc / 没有。所以这里把 title 当作 Content 显示更贴切:
        //   用户看到的是"某某 赞了你的评论: <评论原文>"。
        //   判断依据: 点赞接口的 business 是 "评论"/"视频", 且 item 里有 item_id/pid 这组
        //   与 reply/at 不同的字段 —— 有 item_id 就按点赞那条规则处理。
        if (d.TryGetProperty("item_id", out _))
        {
            var title = GetStr(d, "title");
            if (title.Length > 0 && item.Content.Length == 0) item.Content = title;
            item.ContextTitle = "";
        }
    }

    /// <summary>读分页游标</summary>
    private static void ReadCursor(JsonElement data, MsgFeedPage page)
    {
        if (!data.TryGetProperty("cursor", out var cursor) || cursor.ValueKind != JsonValueKind.Object)
            return;

        page.NextId = GetLong(cursor, "id");
        page.NextTime = GetLong(cursor, "time");
        page.HasMore = cursor.TryGetProperty("is_end", out var end)
            ? end.ValueKind == JsonValueKind.False
            : page.NextId > 0;
    }

    private static string DefaultAction(MsgTab kind) => kind switch
    {
        MsgTab.Reply => "回复了你",
        MsgTab.At => "@了你",
        MsgTab.Like => "赞了你",
        _ => ""
    };

    /// <summary>相对时间("刚刚"/"27分钟前"/"2小时前"/"昨天 18:45"/"09-20")</summary>
    private static string FormatTime(long ts)
    {
        if (ts <= 0) return "";
        try
        {
            var t = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;
            var span = DateTime.Now - t;
            if (span.TotalMinutes < 1) return "刚刚";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}分钟前";
            if (span.TotalDays < 1) return $"{(int)span.TotalHours}小时前";
            if (span.TotalDays < 2) return $"昨天 {t:HH:mm}";
            return t.Year == DateTime.Now.Year ? t.ToString("MM-dd") : t.ToString("yyyy-MM-dd");
        }
        catch { return ""; }
    }

    private static string GetStr(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) &&
           (v.ValueKind == JsonValueKind.String || v.ValueKind == JsonValueKind.Number)
            ? v.ToString() : "";

    /// <summary>
    /// 取整数。★ 一律走 TryGetInt32/TryGetInt64 再退到浮点截断 ——
    /// 服务端偶尔把计数下发成小数(如 1.0), 直接 GetInt32() 会抛异常把整页打空。
    /// </summary>
    private static int GetInt(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return 0;
        if (v.TryGetInt32(out var i)) return i;
        return (int)v.GetDouble();
    }

    private static long GetLong(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return 0;
        if (v.TryGetInt64(out var l)) return l;
        return (long)v.GetDouble();
    }
}
