using System.Text.Json;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.Tests;

/// <summary>
/// 综合动态解析器测试: 视频 / 图文 / 纯文字 / 专栏 / 转发 / 扁平兜底 / 统计 / 缩略图。
/// 样本按 feed/all 与 feed/nav 的真实字段结构精简而来。
/// </summary>
public class DynamicParserTests
{
    private static DynamicItem? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return DynamicParser.ParseItem(doc.RootElement.Clone());
    }

    [Fact]
    public void Modules_Video_ParsesArchiveAndStat()
    {
        var item = Parse("""
        {
          "id_str": "966887968322093078",
          "type": "DYNAMIC_TYPE_AV",
          "visible": true,
          "modules": {
            "module_author": {
              "mid": 3546730050096047,
              "name": "青岛LUCIFER",
              "face": "https://i2.hdslb.com/bfs/face/a.jpg",
              "pub_action": "投稿了视频",
              "pub_time": "刚刚",
              "pub_ts": 1723959548
            },
            "module_dynamic": {
              "desc": null,
              "major": {
                "type": "MAJOR_TYPE_ARCHIVE",
                "archive": {
                  "aid": "112981396619958",
                  "bvid": "BV1oeWNebEv2",
                  "title": "老板娘今天是可爱鬼呀！",
                  "cover": "http://i2.hdslb.com/bfs/archive/x.jpg",
                  "duration_text": "00:16",
                  "pubdate": 1723800000,
                  "jump_url": "//www.bilibili.com/video/BV1oeWNebEv2/",
                  "stat": { "play": "1.4万", "danmaku": "217" }
                }
              }
            },
            "module_stat": {
              "comment": { "count": 142 },
              "forward": { "count": 6 },
              "like": { "count": 1959, "status": true }
            }
          }
        }
        """);

        Assert.NotNull(item);
        Assert.Equal(DynamicKind.Video, item!.Kind);
        Assert.Equal("BV1oeWNebEv2", item.Bvid);
        Assert.Equal(112981396619958, item.Aid);
        Assert.Equal("00:16", item.Duration);
        Assert.Equal(1723800000, item.VideoPubdate);
        Assert.Equal(14000, item.ViewCount);
        Assert.Equal(217, item.DanmakuCount);
        Assert.Equal("投稿了视频", item.ActionLabel);
        Assert.True(item.HasStat);
        Assert.Equal(142, item.CommentCount);
        Assert.Equal(6, item.ForwardCount);
        Assert.Equal(1959, item.LikeCount);
        Assert.True(item.IsLiked);
        Assert.True(item.HasPlayableVideo);
        Assert.Equal("https://i2.hdslb.com/bfs/archive/x.jpg", item.Cover);
        // 视频动态没有 basic 时, 评论区应回退到视频 aid + type=1
        Assert.Equal(112981396619958, item.CommentOid);
        Assert.Equal(1, item.CommentType);
    }

    [Fact]
    public void Modules_Video_无archivePubdate时卡片墙用动态时间()
    {
        // 真实 feed/all 的 archive 不带 pubdate(2026-11-23 实测), 这里按真实字段形状精简。
        var item = Parse("""
        {
          "id_str": "1273865850963623953",
          "type": "DYNAMIC_TYPE_AV",
          "modules": {
            "module_author": {
              "mid": 11298139,
              "name": "青岛LUCIFER",
              "pub_time": "2小时前",
              "pub_ts": 1723959548,
              "pub_action": "投稿了视频"
            },
            "module_dynamic": {
              "major": {
                "type": "MAJOR_TYPE_ARCHIVE",
                "archive": {
                  "bvid": "BV1oeWNebEv2",
                  "title": "老板娘今天是可爱鬼呀！",
                  "cover": "http://i2.hdslb.com/bfs/archive/x.jpg",
                  "duration_text": "00:16",
                  "jump_url": "//www.bilibili.com/video/BV1oeWNebEv2/"
                }
              }
            }
          }
        }
        """);

        Assert.NotNull(item);
        Assert.Equal(0, item!.VideoPubdate);
        Assert.Equal(1723959548, item.Pubdate);
        Assert.Equal(1723959548, item.AsVideoItem()!.Pubdate);
    }

    [Fact]
    public void AsVideoItem_视频发布时间优先于动态时间()
    {
        var direct = new DynamicItem
        {
            Kind = DynamicKind.Video,
            Bvid = "BV1Direct",
            Title = "direct",
            Pubdate = 2000,
            VideoPubdate = 1000,
        };
        var directVideo = direct.AsVideoItem();
        Assert.NotNull(directVideo);
        Assert.Equal(1000, directVideo!.Pubdate);
        Assert.Same(directVideo, direct.AsVideoItem());   // 缓存不能丢, ReconcileWall 靠引用比对
    }

    [Fact]
    public void AsVideoItem_无archive发布时间时用本条动态时间()
    {
        // ★ 2026-11-23 真机: feed/all 的 archive 不返回 pubdate, VideoPubdate 恒为 0。
        //   原投稿的动态 pub_ts 与视频发布时间只差几秒, 所以直接用本条动态时间。
        var direct = new DynamicItem
        {
            Kind = DynamicKind.Video,
            Bvid = "BV1Direct",
            Title = "direct",
            Pubdate = 2000,
        };
        var v = direct.AsVideoItem();
        Assert.NotNull(v);
        Assert.Equal(2000, v!.Pubdate);
    }

    [Fact]
    public void AsVideoItem_转发取原动态时间而不是转发时间()
    {
        // ★ 2026-11-23 真机: 外层 pub_ts 是转发时间(3000), 原动态 pub_ts(900) 才是视频发布时间。
        //   旧写法优先用外层 Pubdate, 于是几个月前的视频被转发一下就会跳到卡片墙最前面。
        var orig = new DynamicItem
        {
            Kind = DynamicKind.Video,
            Bvid = "BV1Orig",
            Title = "orig",
            Pubdate = 900,
        };
        var forward = new DynamicItem
        {
            Kind = DynamicKind.Forward,
            Orig = orig,
            Pubdate = 3000,
        };
        var forwardVideo = forward.AsVideoItem();
        Assert.NotNull(forwardVideo);
        Assert.Equal(900, forwardVideo!.Pubdate);
    }

    [Fact]
    public void AsVideoItem_转发优先用原文archive发布时间()
    {
        // 接口日后若在 archive 里补了 pubdate, 它的精度仍高于原动态 pub_ts。
        var orig = new DynamicItem
        {
            Kind = DynamicKind.Video,
            Bvid = "BV1Orig",
            Title = "orig",
            Pubdate = 900,
            VideoPubdate = 800,
        };
        var forward = new DynamicItem { Orig = orig, Pubdate = 3000 };
        var forwardVideo = forward.AsVideoItem();
        Assert.NotNull(forwardVideo);
        Assert.Equal(800, forwardVideo!.Pubdate);
    }

    [Fact]
    public void Modules_Draw_ParsesImagesAndText()
    {
        var item = Parse("""
        {
          "id_str": "966831498138222598",
          "type": "DYNAMIC_TYPE_DRAW",
          "modules": {
            "module_author": { "mid": 233114659, "name": "碧蓝航线", "pub_time": "3小时前" },
            "module_dynamic": {
              "desc": { "text": "这欧卡画质也是被DLSS5给救了😂" },
              "major": {
                "type": "MAJOR_TYPE_DRAW",
                "draw": {
                  "items": [
                    { "src": "http://i0.hdslb.com/bfs/new_dyn/a.jpg", "width": 1440, "height": 1080 },
                    { "src": "http://i0.hdslb.com/bfs/new_dyn/b.jpg", "width": 1440, "height": 1080 }
                  ]
                }
              }
            },
            "module_stat": { "comment": { "count": 50 }, "forward": { "count": 2 }, "like": { "count": 877, "status": false } }
          }
        }
        """);

        Assert.NotNull(item);
        Assert.Equal(DynamicKind.ImageText, item!.Kind);
        Assert.Equal("这欧卡画质也是被DLSS5给救了😂", item.ContentText);
        Assert.Equal(2, item.Images.Count);
        Assert.Equal(1440, item.Images[0].Width);
        Assert.Contains("@480w.jpg", item.Images[0].ThumbUrl);
        Assert.Contains("@720w.jpg", item.Images[0].DetailUrl);
        Assert.Equal(50, item.CommentCount);
        Assert.Equal(877, item.LikeCount);
        Assert.False(item.IsLiked);
        Assert.False(item.HasPlayableVideo);
        // 没有 basic 时图文动态回退到动态 id + type=17
        Assert.Equal(966831498138222598, item.CommentOid);
        Assert.Equal(17, item.CommentType);
    }

    [Fact]
    public void Modules_Opus_ParsesPicsAndSummary()
    {
        var item = Parse("""
        {
          "id_str": "1",
          "type": "DYNAMIC_TYPE_DRAW",
          "modules": {
            "module_author": { "mid": 1, "name": "up", "pub_ts": 1724152653, "pub_time": "2024年08月20日 19:17" },
            "module_dynamic": {
              "desc": null,
              "major": {
                "type": "MAJOR_TYPE_OPUS",
                "opus": {
                  "title": null,
                  "summary": { "text": "图文正文" },
                  "pics": [ { "url": "https://i0.hdslb.com/bfs/new_dyn/c.jpg", "width": 800, "height": 600 } ]
                }
              }
            }
          }
        }
        """);

        Assert.NotNull(item);
        Assert.Equal(DynamicKind.ImageText, item!.Kind);
        Assert.Equal("图文正文", item.ContentText);
        Assert.Single(item.Images);
        Assert.Equal("https://i0.hdslb.com/bfs/new_dyn/c.jpg", item.Images[0].Url);
        Assert.Equal(1724152653, item.Pubdate);
        Assert.False(item.PubDateApprox);
    }

    [Fact]
    public void Modules_Word_ParsesPureText()
    {
        var item = Parse("""
        {
          "id_str": "2",
          "type": "DYNAMIC_TYPE_WORD",
          "modules": {
            "module_author": { "mid": 1, "name": "up", "pub_time": "昨天 20:46" },
            "module_dynamic": { "desc": { "text": "纯文字动态" }, "major": null }
          }
        }
        """);

        Assert.NotNull(item);
        Assert.Equal(DynamicKind.Text, item!.Kind);
        Assert.True(item.HasText);
        Assert.False(item.HasImages);
        Assert.False(item.HasAnyVideo);
        Assert.Equal("发布了动态", item.ActionLabel);
    }

    [Fact]
    public void Modules_Forward_WrapsOrig()
    {
        var item = Parse("""
        {
          "id_str": "3",
          "type": "DYNAMIC_TYPE_FORWARD",
          "modules": {
            "module_author": { "mid": 10, "name": "转发者", "pub_time": "1分钟前" },
            "module_dynamic": { "desc": { "text": "转发评论" }, "major": null },
            "module_stat": { "comment": { "count": 1 }, "forward": { "count": 0 }, "like": { "count": 2 } }
          },
          "orig": {
            "id_str": "4",
            "type": "DYNAMIC_TYPE_AV",
            "modules": {
              "module_author": { "mid": 20, "name": "原作者", "pub_time": "2小时前" },
              "module_dynamic": {
                "major": {
                  "type": "MAJOR_TYPE_ARCHIVE",
                  "archive": { "bvid": "BV1orig", "title": "原视频", "cover": "https://i0.hdslb.com/bfs/archive/o.jpg", "duration_text": "01:00", "pubdate": 1723700000 }
                }
              }
            }
          }
        }
        """);

        Assert.NotNull(item);
        Assert.Equal(DynamicKind.Forward, item!.Kind);
        Assert.True(item.IsForward);
        Assert.Equal("转发评论", item.ContentText);
        Assert.NotNull(item.Orig);
        Assert.True(item.Orig!.HasPlayableVideo);
        Assert.Equal("BV1orig", item.VideoBvid);
        Assert.Equal(1723700000, item.Orig!.VideoPubdate);
        Assert.True(item.HasAnyVideo);
        Assert.Equal("转发了动态", item.ActionLabel);
        // 转发动态的评论区挂在这条转发自己身上, 不是原视频
        Assert.Equal(3, item.CommentOid);
        Assert.Equal(17, item.CommentType);
    }

    [Fact]
    public void Modules_Article_ParsesTitleAndCovers()
    {
        var item = Parse("""
        {
          "id_str": "5",
          "type": "DYNAMIC_TYPE_ARTICLE",
          "modules": {
            "module_author": { "mid": 1, "name": "up", "pub_time": "09-20" },
            "module_dynamic": {
              "major": {
                "type": "MAJOR_TYPE_ARTICLE",
                "article": {
                  "title": "专栏标题",
                  "desc": "专栏摘要",
                  "covers": [ "https://i0.hdslb.com/bfs/article/c1.jpg" ],
                  "jump_url": "//www.bilibili.com/read/cv1"
                }
              }
            }
          }
        }
        """);

        Assert.NotNull(item);
        Assert.Equal(DynamicKind.Article, item!.Kind);
        Assert.Equal("专栏标题", item.Title);
        Assert.True(item.ShowArticleTitle);
        Assert.Single(item.Images);
        Assert.Equal("投稿了专栏", item.ActionLabel);
    }

    [Fact]
    public void Flat_NavVideo_StillParses()
    {
        var item = Parse("""
        {
          "id_str": "6",
          "type": "8",
          "rid": "123",
          "title": "扁平视频",
          "cover": "http://i0.hdslb.com/bfs/archive/f.jpg",
          "jump_url": "//www.bilibili.com/video/BV1flat",
          "pub_time": "2小时前",
          "author": { "mid": "1255510247", "name": "up", "face": "http://i0.hdslb.com/bfs/face/f.jpg" }
        }
        """);

        Assert.NotNull(item);
        Assert.Equal(DynamicKind.Video, item!.Kind);
        Assert.Equal("BV1flat", item.Bvid);
        Assert.Equal(123, item.Aid);
        Assert.Equal(1255510247, item.AuthorMid);
        Assert.True(item.PubDateApprox);
        Assert.Equal("2小时前", item.PubText);
    }

    [Fact]
    public void Flat_NonVideo_ReturnsNull()
    {
        var item = Parse("""
        { "id_str": "7", "type": "2", "title": "图文", "cover": "x", "author": { "mid": 1 } }
        """);
        Assert.Null(item);
    }

    [Fact]
    public void VisibleFalse_ReturnsNull()
    {
        var item = Parse("""
        { "id_str": "8", "type": "DYNAMIC_TYPE_AV", "visible": false, "modules": {} }
        """);
        Assert.Null(item);
    }

    [Theory]
    [InlineData("1.4万", 14000)]
    [InlineData("12.3亿", 1230000000)]
    [InlineData("217", 217)]
    [InlineData("", 0)]
    public void ParseCountText_Works(string input, long expected)
        => Assert.Equal(expected, DynamicParser.ParseCountText(input));

    [Fact]
    public void MakeThumb_OnlyLimitsWidthOnHdslb()
    {
        Assert.Equal("https://i0.hdslb.com/bfs/a.jpg@480w.jpg",
            DynamicParser.MakeThumb("https://i0.hdslb.com/bfs/a.jpg", 480));
        // 已有 @ 样式不再叠加
        Assert.Equal("https://i0.hdslb.com/bfs/a.jpg@100w.jpg",
            DynamicParser.MakeThumb("https://i0.hdslb.com/bfs/a.jpg@100w.jpg", 480));
        // 非 B 站图床不动
        Assert.Equal("https://example.com/a.jpg",
            DynamicParser.MakeThumb("https://example.com/a.jpg", 480));
    }

    [Fact]
    public void DynamicItem_FromVideo_And_AsVideoItem_RoundTrip()
    {
        var v = new VideoItem
        {
            Bvid = "BV1x",
            Title = "t",
            Cover = "https://i0.hdslb.com/bfs/archive/c.jpg",
            Author = "up",
            OwnerMid = 42,
            Duration = "01:02",
            ViewCount = 100,
        };
        var d = DynamicItem.FromVideo(v);
        Assert.Equal(DynamicKind.Video, d.Kind);
        Assert.True(d.HasPlayableVideo);
        Assert.Equal("投稿了视频", d.ActionLabel);

        var back = d.AsVideoItem();
        Assert.NotNull(back);
        Assert.Equal("BV1x", back!.Bvid);
        Assert.Equal(42, back.OwnerMid);
        // 同一条动态多次转换返回同一引用(卡片墙增量同步依赖它)
        Assert.Same(back, d.AsVideoItem());
    }

    [Fact]
    public void Modules_Basic_SetsCommentTarget()
    {
        var item = Parse("""
        {
          "id_str": "967717348014293017",
          "type": "DYNAMIC_TYPE_DRAW",
          "basic": { "comment_id_str": "326122895", "comment_type": 11 },
          "modules": {
            "module_author": { "mid": 1, "name": "up", "pub_time": "刚刚" },
            "module_dynamic": {
              "desc": { "text": "图文" },
              "major": { "type": "MAJOR_TYPE_DRAW", "draw": { "items": [ { "src": "https://i0.hdslb.com/bfs/new_dyn/x.jpg" } ] } }
            }
          }
        }
        """);
        Assert.NotNull(item);
        Assert.Equal(326122895, item!.CommentOid);
        Assert.Equal(11, item.CommentType);
    }

    [Fact]
    public void ParseRepostItem_ReadsUserAndDesc()
    {
        using var doc = JsonDocument.Parse("""
        {
          "id_str": "1092279955170000920",
          "pub_time": "1分钟前",
          "desc": { "text": "恭喜恭喜" },
          "user": { "mid": 58964126, "name": "see_ka", "face": "https://i2.hdslb.com/bfs/face/x.jpg" }
        }
        """);
        var r = DynamicParser.ParseRepostItem(doc.RootElement.Clone());
        Assert.NotNull(r);
        Assert.Equal("see_ka", r!.Name);
        Assert.Equal(58964126, r.Mid);
        Assert.Equal("恭喜恭喜", r.Text);
        Assert.Equal("1分钟前", r.PubTime);
    }

    // ------------------------------------------------------------ 关注时间线过滤(issue #99)

    /// <summary>构造一条最小可解析的视频动态, 只关心 module_author 里的 following。</summary>
    private static DynamicItem? ParseWithFollowing(string followingJson)
    {
        var author = "\"mid\": 42, \"name\": \"up\", \"pub_time\": \"刚刚\"";
        if (followingJson.Length > 0) author += ", \"following\": " + followingJson;
        return Parse($$"""
        {
          "id_str": "968000000000000001",
          "type": "DYNAMIC_TYPE_AV",
          "modules": {
            "module_author": { {{author}} },
            "module_dynamic": {
              "major": {
                "type": "MAJOR_TYPE_ARCHIVE",
                "archive": {
                  "aid": 1,
                  "bvid": "BV1xx411c7mD",
                  "title": "未关注 UP 的视频",
                  "jump_url": "//www.bilibili.com/video/BV1xx411c7mD/"
                }
              }
            }
          }
        }
        """);
    }

    [Fact]
    public void Following_FollowedAuthorIsKept()
    {
        var item = ParseWithFollowing("true");
        Assert.NotNull(item);
        Assert.True(item!.AuthorFollowing);
        Assert.True(DynamicParser.IsFromFollowedAuthor(item));
    }

    [Fact]
    public void Following_RecommendedAuthorIsFiltered()
    {
        // feed/all 插播的推荐动态: 作者没关注(following=false) → 关注时间线里不展示
        var item = ParseWithFollowing("false");
        Assert.NotNull(item);
        Assert.False(item!.AuthorFollowing);
        Assert.False(DynamicParser.IsFromFollowedAuthor(item));
    }

    [Fact]
    public void Following_NullOrMissingIsKept()
    {
        // 自己的动态 following 为 null; 旧形态/空间动态可能没有该字段 —— 都不能当成"未关注"滤掉
        var own = ParseWithFollowing("null");
        Assert.NotNull(own);
        Assert.Null(own!.AuthorFollowing);
        Assert.True(DynamicParser.IsFromFollowedAuthor(own));

        var missing = ParseWithFollowing("");
        Assert.NotNull(missing);
        Assert.Null(missing!.AuthorFollowing);
        Assert.True(DynamicParser.IsFromFollowedAuthor(missing));
    }

    [Fact]
    public void Timeline_FiltersRecommendationsOnly()
    {
        // 一页里: 已关注 UP / 推荐(未关注) / 自己的动态 —— 只丢掉推荐那条
        using var doc = JsonDocument.Parse("""
        [
          {
            "id_str": "1", "type": "DYNAMIC_TYPE_AV",
            "modules": {
              "module_author": { "mid": 1, "name": "已关注", "following": true },
              "module_dynamic": { "major": { "type": "MAJOR_TYPE_ARCHIVE",
                "archive": { "bvid": "BV1aa", "title": "a", "jump_url": "//www.bilibili.com/video/BV1aa/" } } }
            }
          },
          {
            "id_str": "2", "type": "DYNAMIC_TYPE_AV",
            "modules": {
              "module_author": { "mid": 2, "name": "推荐UP", "following": false },
              "module_dynamic": { "major": { "type": "MAJOR_TYPE_ARCHIVE",
                "archive": { "bvid": "BV1bb", "title": "b", "jump_url": "//www.bilibili.com/video/BV1bb/" } } }
            }
          },
          {
            "id_str": "3", "type": "DYNAMIC_TYPE_AV",
            "modules": {
              "module_author": { "mid": 3, "name": "我自己", "following": null },
              "module_dynamic": { "major": { "type": "MAJOR_TYPE_ARCHIVE",
                "archive": { "bvid": "BV1cc", "title": "c", "jump_url": "//www.bilibili.com/video/BV1cc/" } } }
            }
          }
        ]
        """);

        var kept = new List<DynamicItem>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var item = DynamicParser.ParseTimelineItem(e.Clone());
            if (item != null) kept.Add(item);
        }

        Assert.Equal(2, kept.Count);
        Assert.DoesNotContain(kept, x => x.AuthorName == "推荐UP");
        Assert.Contains(kept, x => x.AuthorName == "已关注");
        Assert.Contains(kept, x => x.AuthorName == "我自己");
    }
}