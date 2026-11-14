using System.Collections.Generic;
using System.Text.Json;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 消息页的纯逻辑测试: 互动消息解析 + 表情短代码切分。
/// 这两块都是"喂一段接口 JSON / 一段正文, 断言输出"的形状, 不需要 WPF 也不需要网络。
/// </summary>
public class MsgFeedParserTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        // Clone: 让元素脱离 using 的生命周期, 否则测试里拿到的是悬空引用
        return doc.RootElement.Clone();
    }

    // ---------------------------------------------------------------- 回复我的

    [Fact]
    public void Reply_ParsesUserContentAndContext()
    {
        var data = Parse("""
        {
          "cursor": { "is_end": false, "id": 111, "time": 1700000000 },
          "items": [
            {
              "id": 222,
              "user": { "mid": 42, "nickname": "小明", "avatar": "//i0.hdslb.com/a.jpg" },
              "item": {
                "title": "某个视频",
                "source_content": "这是我的回复",
                "uri": "https://www.bilibili.com/video/BV1xx411c7mD",
                "business_id": 1
              },
              "reply_time": 1700000123
            }
          ]
        }
        """);

        var page = MsgFeedParser.ParsePage(MsgTab.Reply, data);

        Assert.Single(page.Items);
        var item = page.Items[0];
        Assert.Equal(222, item.Id);
        Assert.Equal(42, item.UserMid);
        Assert.Equal("小明", item.UserName);
        Assert.Equal("https://i0.hdslb.com/a.jpg", item.UserFace);
        Assert.Equal("这是我的回复", item.Content);
        Assert.Equal("某个视频", item.ContextTitle);
        Assert.Equal("回复了你", item.ActionText);
        Assert.Equal("BV1xx411c7mD", item.Bvid);
        Assert.Equal(1700000123, item.Time);

        Assert.True(page.HasMore);   // cursor.is_end = false ⇒ 还有下一页
        Assert.Equal(111, page.NextId);
        Assert.Equal(1700000000, page.NextTime);
    }

    // ---------------------------------------------------------------- @我的

    [Fact]
    public void At_UsesAtTimeAndAtAction()
    {
        var data = Parse("""
        {
          "cursor": { "is_end": true },
          "items": [
            {
              "id": 1,
              "user": { "mid": 7, "nickname": "阿伟" },
              "item": { "source_content": "@我 来看这个" },
              "at_time": 1700000500
            }
          ]
        }
        """);

        var page = MsgFeedParser.ParsePage(MsgTab.At, data);

        Assert.Single(page.Items);
        Assert.Equal("@了你", page.Items[0].ActionText);
        Assert.Equal(1700000500, page.Items[0].Time);
        Assert.False(page.HasMore); // is_end = true ⇒ 没有下一页
    }

    // ---------------------------------------------------------------- 收到的赞

    [Fact]
    public void Like_MergesMultipleUsersIntoExtraText()
    {
        var data = Parse("""
        {
          "latest": { "items": [
            {
              "id": 3,
              "users": [
                { "mid": 11, "nickname": "甲", "avatar": "//i0.hdslb.com/1.jpg" },
                { "mid": 12, "nickname": "乙" },
                { "mid": 13, "nickname": "丙" }
              ],
              "counts": 3867,
              "is_multi": 1,
              "item": { "item_id": 5, "title": "被赞的评论" },
              "like_time": 1700000900
            }
          ] },
          "total": { "cursor": { "is_end": false, "id": 9, "time": 1700000900 }, "items": [] }
        }
        """);

        var page = MsgFeedParser.ParsePage(MsgTab.Like, data);

        Assert.Single(page.Items);
        var item = page.Items[0];
        Assert.Equal("甲", item.UserName);
        // ★ 人数取 users 数组长度, **不是** counts ——
        //   counts 是被赞内容的总赞数(真实数据里是 3867), 拿它当人数会显示成
        //   "等 3867 人 赞了你", 严重误导。这里特意用 3867 当 counts 来钉住这条规则。
        Assert.Equal("等 3 人", item.ExtraText);
        Assert.Equal("甲 等 3 人 赞了你", item.HeaderText);
        // 点赞通知的 title 是"被赞的内容", 显示在正文行而不是来源标题行
        Assert.Equal("被赞的评论", item.Content);
        Assert.Equal("", item.ContextTitle);
    }

    /// <summary>
    /// ★★ 点赞接口的真实结构(2026-11-14 用真实账号实测):
    ///    data = { latest: { items, last_view_at }, total: { cursor, items } }
    ///    —— 没有根级 items, 也没有根级 cursor。
    ///    只读 items 的话整页永远是空的(用户看到"还没有收到赞"), 而接口明明有几百条。
    /// </summary>
    [Fact]
    public void Like_ReadsLatestAndTotalLayers()
    {
        var data = Parse("""
        {
          "latest": {
            "items": [
              { "id": 100, "users": [ { "mid": 1, "nickname": "新" } ],
                "counts": 1, "like_time": 1700002000,
                "item": { "item_id": 1, "type": "video", "title": "新被赞的视频",
                          "uri": "https://www.bilibili.com/video/BV1aa411c7aa" } }
            ],
            "last_view_at": 1700000000
          },
          "total": {
            "cursor": { "is_end": false, "id": 99, "time": 1699999000 },
            "items": [
              { "id": 200, "users": [ { "mid": 2, "nickname": "旧" } ],
                "counts": 1, "like_time": 1699999000,
                "item": { "item_id": 2, "type": "video", "title": "旧被赞的视频",
                          "uri": "https://www.bilibili.com/video/BV1bb411c7bb" } }
            ]
          }
        }
        """);

        var page = MsgFeedParser.ParsePage(MsgTab.Like, data);

        Assert.Equal(2, page.Items.Count);
        Assert.Contains(page.Items, i => i.Id == 100);
        Assert.Contains(page.Items, i => i.Id == 200);

        // 游标来自 total.cursor
        Assert.True(page.HasMore);
        Assert.Equal(99, page.NextId);
        Assert.Equal(1699999000, page.NextTime);
    }

    [Fact]
    public void Like_DeduplicatesAcrossLayers()
    {
        var data = Parse("""
        {
          "latest": { "items": [ { "id": 7, "users": [ { "nickname": "甲" } ], "like_time": 100 } ] },
          "total":  { "cursor": { "is_end": true },
                      "items": [ { "id": 7, "users": [ { "nickname": "甲" } ], "like_time": 100 } ] }
        }
        """);

        var page = MsgFeedParser.ParsePage(MsgTab.Like, data);
        Assert.Single(page.Items);
    }

    [Fact]
    public void Like_ItemTitleBecomesContentNotContext()
    {
        // 点赞通知里 item.title 是"被赞的那条评论正文", 不是来源标题 ——
        // 当 ContextTitle 显示会变成"某某 赞了你 / 这条评论原文"两行重复又对不上。
        var data = Parse("""
        {
          "latest": { "items": [
            { "id": 1, "users": [ { "nickname": "甲" } ], "counts": 1, "like_time": 100,
              "item": { "item_id": 5, "type": "reply", "title": "这是评论原文",
                        "uri": "https://www.bilibili.com/video/BV1cc411c7cc" } }
          ] },
          "total": { "cursor": { "is_end": true }, "items": [] }
        }
        """);

        var item = MsgFeedParser.ParsePage(MsgTab.Like, data).Items[0];
        Assert.Equal("这是评论原文", item.Content);
        Assert.Equal("", item.ContextTitle);
        Assert.Equal("BV1cc411c7cc", item.Bvid);
    }

    [Fact]
    public void Like_SingleUserHasNoExtraText()
    {
        var data = Parse("""
        {
          "latest": { "items": [
            { "id": 4, "user": { "mid": 11, "nickname": "甲" }, "counts": 1, "like_time": 1700000900 }
          ] },
          "total": { "cursor": { "is_end": true }, "items": [] }
        }
        """);

        var item = MsgFeedParser.ParsePage(MsgTab.Like, data).Items[0];
        Assert.Equal("", item.ExtraText);
        Assert.Equal("甲 赞了你", item.HeaderText);
    }

    // ---------------------------------------------------------------- 宽容性

    [Fact]
    public void EmptyItemsArray_YieldsEmptyPage()
    {
        var page = MsgFeedParser.ParsePage(MsgTab.Reply, Parse("""{ "items": [] }"""));
        Assert.Empty(page.Items);
        Assert.False(page.HasMore);
    }

    [Fact]
    public void NullOrMissingItems_DoesNotThrow()
    {
        var page = MsgFeedParser.ParsePage(MsgTab.Reply, Parse("""{ "items": null }"""));
        Assert.Empty(page.Items);
    }

    [Fact]
    public void ItemWithoutAnyIdentity_IsSkipped()
    {
        // 既没昵称、也没内容、也没标题 ⇒ 渲染出来是个空卡片, 不如丢掉
        var data = Parse("""{ "items": [ { "id": 5 } ] }""");
        Assert.Empty(MsgFeedParser.ParsePage(MsgTab.Reply, data).Items);
    }

    [Fact]
    public void FractionalCounts_DoNotThrow()
    {
        // 服务端偶尔把计数下发成小数; GetInt32 会抛异常, 解析器必须兜住
        var data = Parse("""
        { "latest": { "items": [
            { "id": 1, "user": { "nickname": "甲" }, "counts": 2.0, "like_time": 1 }
          ] },
          "total": { "cursor": { "is_end": true }, "items": [] } }
        """);
        // counts 是小数时不能抛; 但点赞的人数看 users, 单个人 ⇒ 没有"等 N 人"
        var item = MsgFeedParser.ParsePage(MsgTab.Like, data).Items[0];
        Assert.Equal("", item.ExtraText);
    }

    [Fact]
    public void Bvid_FallsBackToParsingUri()
    {
        var data = Parse("""
        {
          "items": [ {
            "id": 1,
            "user": { "nickname": "甲" },
            "item": { "uri": "https://www.bilibili.com/video/BV1GJ411x7h7?spm=1" },
            "reply_time": 1
          } ]
        }
        """);
        Assert.Equal("BV1GJ411x7h7", MsgFeedParser.ParsePage(MsgTab.Reply, data).Items[0].Bvid);
    }
}

/// <summary>表情短代码切分(私信气泡与输入框共用同一套规则)</summary>
public class MsgContentParserTests
{
    private static Dictionary<string, MsgEmote> Emotes(params string[] codes)
    {
        var d = new Dictionary<string, MsgEmote>();
        foreach (var c in codes)
            d[c] = new MsgEmote { Text = c, Url = "https://i0.hdslb.com/" + c.Trim('[', ']') + ".png" };
        return d;
    }

    [Fact]
    public void NoEmotes_ReturnsSingleTextSegment()
    {
        var segs = MsgContentParser.Split("你好呀", null);
        Assert.Single(segs);
        Assert.Equal("你好呀", segs[0].Text);
        Assert.False(segs[0].IsEmote);
    }

    [Fact]
    public void EmptyText_ReturnsNoSegments()
    {
        Assert.Empty(MsgContentParser.Split("", Emotes("[doge]")));
        Assert.Empty(MsgContentParser.Split(null, Emotes("[doge]")));
    }

    [Fact]
    public void KnownShortcode_BecomesEmoteSegment()
    {
        var segs = MsgContentParser.Split("看这个[doge]哈哈", Emotes("[doge]"));

        Assert.Equal(3, segs.Count);
        Assert.Equal("看这个", segs[0].Text);
        Assert.True(segs[1].IsEmote);
        Assert.Equal("[doge]", segs[1].Text);
        Assert.Equal("哈哈", segs[2].Text);
    }

    [Fact]
    public void UnknownShortcode_StaysAsPlainText()
    {
        // 表里没有的 [xxx] 必须原样保留 —— 用户可能真的在写方括号
        var segs = MsgContentParser.Split("数组[0]和[未知]", Emotes("[doge]"));

        Assert.Single(segs);
        Assert.Equal("数组[0]和[未知]", segs[0].Text);
    }

    [Fact]
    public void ConsecutiveEmotes_ProduceNoEmptyTextSegments()
    {
        var segs = MsgContentParser.Split("[doge][doge]", Emotes("[doge]", "[tv_doge]"));

        Assert.Equal(2, segs.Count);
        Assert.True(segs[0].IsEmote);
        Assert.True(segs[1].IsEmote);
        Assert.All(segs, s => Assert.NotEqual("", s.Text));
    }

    [Fact]
    public void UnclosedBracket_DoesNotSwallowText()
    {
        var segs = MsgContentParser.Split("看[doge", Emotes("[doge]"));
        Assert.Single(segs);
        Assert.Equal("看[doge", segs[0].Text);
    }

    [Fact]
    public void Preview_StripsNewlines()
    {
        Assert.Equal("第一行 第二行", MsgContentParser.ToPreview("第一行\r\n第二行"));
    }
}
