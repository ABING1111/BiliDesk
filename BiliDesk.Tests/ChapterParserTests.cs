using System.Text.Json;
using BiliDesk.Models;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 视频章节解析与查找的纯逻辑(2026-11-21)。
/// ★ 样本结构依据 2026-11 实测的 /x/player/wbi/v2 响应(data.view_points[])。
/// </summary>
public class ChapterParserTests
{
    private static JsonElement Data(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ------------------------------------------------------------ Parse

    private const string RealJson = """
        {
          "view_points": [
            {
              "type": 2,
              "from": 18734,
              "to": 20496,
              "content": "9.7空间第二类曲线积分",
              "imgUrl": "http://i0.hdslb.com/bfs/vchapter/43746133156_18734.jpg"
            },
            {
              "type": 2,
              "from": 0,
              "to": 19,
              "content": "开头",
              "imgUrl": "//i0.hdslb.com/bfs/vchapter/43746133156_0.jpg"
            },
            {
              "type": 2,
              "from": 19,
              "to": 209,
              "content": "",
              "imgUrl": ""
            }
          ]
        }
        """;

    [Fact]
    public void 解析章节_秒转毫秒_按起点排序_图片补https()
    {
        var chapters = ChapterParser.Parse(Data(RealJson));

        Assert.Equal(3, chapters.Count);
        // 服务端返回顺序是 "9.7 在第一个", 排序后必须按时间轴走
        Assert.Equal("开头", chapters[0].Content);
        Assert.Equal(0, chapters[0].FromMs);
        Assert.Equal(19_000, chapters[0].ToMs);
        Assert.Equal("https://i0.hdslb.com/bfs/vchapter/43746133156_0.jpg", chapters[0].ImageUrl);

        // 空章名按**时间顺序**补成「第 2 章」
        Assert.Equal("第 2 章", chapters[1].Content);
        Assert.Equal(19_000, chapters[1].FromMs);

        Assert.Equal("9.7空间第二类曲线积分", chapters[2].Content);
        Assert.Equal(18_734_000, chapters[2].FromMs);
        Assert.Equal(20_496_000, chapters[2].ToMs);
        // http 升 https: 播放器里其它封面都走 https, 没理由为一张缩略图留明文请求
        Assert.Equal("https://i0.hdslb.com/bfs/vchapter/43746133156_18734.jpg", chapters[2].ImageUrl);
    }

    [Fact]
    public void 解析章节_坏数据丢弃_负起点夹到0()
    {
        const string json = """
            {
              "view_points": [
                { "from": -5, "to": 10, "content": "负数起点" },
                { "from": 10, "to": 10, "content": "零时长" },
                { "from": 20, "to": 5, "content": "倒挂" }
              ]
            }
            """;

        var chapters = ChapterParser.Parse(Data(json));

        Assert.Single(chapters);
        Assert.Equal(0, chapters[0].FromMs);       // -5 夹到 0
        Assert.Equal(10_000, chapters[0].ToMs);
    }

    [Fact]
    public void 解析章节_同起点去重_保留第一条()
    {
        const string json = """
            {
              "view_points": [
                { "from": 0, "to": 60, "content": "A" },
                { "from": 0, "to": 90, "content": "B" }
              ]
            }
            """;

        var chapters = ChapterParser.Parse(Data(json));

        Assert.Single(chapters);
        Assert.Equal("A", chapters[0].Content);
        Assert.Equal(60_000, chapters[0].ToMs);
    }

    [Fact]
    public void 解析章节_没有view_points字段_返回空()
    {
        Assert.Empty(ChapterParser.Parse(Data("""{ "subtitle": {} }""")));
        Assert.Empty(ChapterParser.Parse(Data("""{ "view_points": [] }""")));
    }

    [Fact]
    public void 解析章节_非对象元素跳过_其余照常()
    {
        // 服务端偶发 null / 非对象元素: 只能丢这一条, 不能让整批(以及同一响应里的字幕)失败。
        const string json = """
            {
              "view_points": [
                null,
                42,
                "坏元素",
                { "from": 0, "to": 60, "content": "开头" },
                { "from": 60, "to": 120, "content": "正常" }
              ]
            }
            """;

        var chapters = ChapterParser.Parse(Data(json));

        Assert.Equal(2, chapters.Count);
        Assert.Equal("开头", chapters[0].Content);
        Assert.Equal("正常", chapters[1].Content);
    }

    // ------------------------------------------------------------ IsUsable

    [Fact]
    public void 章节可用性_至少两章()
    {
        var one = ChapterParser.Parse(Data("""
            { "view_points": [ { "from": 0, "to": 100, "content": "整片" } ] }
            """));
        Assert.False(ChapterParser.IsUsable(one));   // 单章 = 整片一段, 不算章节功能

        var two = ChapterParser.Parse(Data("""
            { "view_points": [
                { "from": 0, "to": 100, "content": "上" },
                { "from": 100, "to": 200, "content": "下" } ] }
            """));
        Assert.True(ChapterParser.IsUsable(two));
    }

    // ------------------------------------------------------------ FindIndex

    private static VideoChapter Ch(long fromSec, long toSec) => new()
    {
        Content = $"{fromSec}-{toSec}",
        FromMs = fromSec * 1000,
        ToMs = toSec * 1000
    };

    [Fact]
    public void 找当前章_命中_空隙算前一章_早于首章返回负一()
    {
        var chapters = new[] { Ch(0, 19), Ch(19, 209), Ch(209, 2251) };

        Assert.Equal(0, ChapterParser.FindIndex(chapters, 0));
        Assert.Equal(0, ChapterParser.FindIndex(chapters, 18_999));
        Assert.Equal(1, ChapterParser.FindIndex(chapters, 19_000));   // 边界属于后一章
        Assert.Equal(1, ChapterParser.FindIndex(chapters, 200_000));
        Assert.Equal(2, ChapterParser.FindIndex(chapters, 2_250_999));
        // 超过最后一章的 to: 仍算最后一章(结尾常有一小段没标)
        Assert.Equal(2, ChapterParser.FindIndex(chapters, 9_999_000));
        // 早于第一章: 没有"当前章"(界面显示笼统的「章节」)
        Assert.Equal(-1, ChapterParser.FindIndex(new[] { Ch(100, 200) }, 50_000));
        Assert.Equal(-1, ChapterParser.FindIndex(System.Array.Empty<VideoChapter>(), 0));
    }

    // ------------------------------------------------------------ FormatOffset

    [Fact]
    public void 章节时间格式_不足一小时用分秒_满一小时带小时()
    {
        Assert.Equal("00:00", ChapterParser.FormatOffset(0));
        Assert.Equal("00:59", ChapterParser.FormatOffset(59_000));
        Assert.Equal("01:00", ChapterParser.FormatOffset(60_000));
        Assert.Equal("37:31", ChapterParser.FormatOffset(2_251_000));
        Assert.Equal("1:02:03", ChapterParser.FormatOffset(3_723_000));
        Assert.Equal("5:45:58", ChapterParser.FormatOffset(20_758_000));
        Assert.Equal("00:00", ChapterParser.FormatOffset(-1));   // 负数夹到 0, 不显示 "-00:01"
    }

    [Fact]
    public void 章节行_透传模型字段()
    {
        var chapter = Ch(19, 209);
        var row = new ChapterRow(chapter, 1);

        Assert.Equal(1, row.Index);
        Assert.Equal("19-209", row.Content);
        Assert.Equal("00:19", row.TimeText);
        Assert.Same(chapter, row.Chapter);
        Assert.Equal(190_000, chapter.DurationMs);
    }
}
