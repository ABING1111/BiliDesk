using System;
using System.Linq;
using System.Text.Json;
using BiliDesk.Models;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 字幕 JSON 的解析纯逻辑(2026-11-15)。
/// ★ 样本结构依据 2026-11 实测的 /x/player/wbi/v2 响应与 aisubtitle.hdslb.com 的字幕本体。
/// </summary>
public class SubtitleParserTests
{
    private static JsonElement Data(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ------------------------------------------------------------ ParseTracks

    private const string PlayerDataJson = """
        {
          "need_login_subtitle": false,
          "subtitle": {
            "allow_submit": false,
            "lan": "",
            "lan_doc": "",
            "subtitles": [
              {
                "id": 11,
                "id_str": "11",
                "lan": "zh-Hans",
                "lan_doc": "中文(简体)",
                "subtitle_url": "//aisubtitle.hdslb.com/bfs/cc/11.json",
                "type": 0
              },
              {
                "id": 22,
                "id_str": "22",
                "lan": "ai-zh",
                "lan_doc": "中文(自动生成)",
                "subtitle_url_v2": "//aisubtitle.hdslb.com/bfs/ai_subtitle/prod/22.json",
                "type": 1
              },
              {
                "id": 33,
                "id_str": "33",
                "lan": "en-US",
                "lan_doc": "英语(机器翻译)",
                "subtitle_url": "/",
                "type": 1
              }
            ]
          }
        }
        """;

    [Fact]
    public void 解析轨道_v1优先_补https_过滤空地址()
    {
        var tracks = SubtitleParser.ParseTracks(Data(PlayerDataJson), out var needLogin);
        Assert.False(needLogin);
        Assert.Equal(2, tracks.Count);   // 空 URL 的第三条被丢弃

        var zh = tracks.Single(t => t.Lan == "zh-Hans");
        Assert.Equal("https://aisubtitle.hdslb.com/bfs/cc/11.json", zh.Url);
        Assert.False(zh.IsAi);

        var ai = tracks.Single(t => t.Lan == "ai-zh");
        // ★ v1 优先(2026-11-15): v2 是混淆地址(控制字符 path), 请求必然 400。
        //   样本里 ai 轨的 v1 是 /bfs/ai_subtitle/prod/22.json, 必须选中它。
        Assert.Equal("https://aisubtitle.hdslb.com/bfs/ai_subtitle/prod/22.json", ai.Url);
        Assert.True(ai.IsAi);
        // 显示名: AI 轨追加标注; 服务端给的 lan_doc 已含"自动生成"则不重复
        Assert.Contains("AI", ai.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void 解析轨道_v2乱码地址被拒_回落v1()
    {
        const string json = """
            {"subtitle": {"subtitles": [
              {"id_str": "1", "lan": "ai-zh",
               "subtitle_url_v2": "//subtitle.bilibili.com/%01%1B%5C=_%04%12%12%049f",
               "subtitle_url": "//aisubtitle.hdslb.com/bfs/ai_subtitle/prod/ok.json?auth_key=1"}
            ]}}
            """;
        var tracks = SubtitleParser.ParseTracks(Data(json), out _);
        var track = Assert.Single(tracks);
        Assert.Equal("https://aisubtitle.hdslb.com/bfs/ai_subtitle/prod/ok.json?auth_key=1", track.Url);
    }

    [Fact]
    public void 解析轨道_只有v2明文地址时_仍可用()
    {
        const string json = """
            {"subtitle": {"subtitles": [
              {"id_str": "1", "lan": "zh", "subtitle_url_v2": "//aisubtitle.hdslb.com/bfs/cc/9.json"}
            ]}}
            """;
        var tracks = SubtitleParser.ParseTracks(Data(json), out _);
        var track = Assert.Single(tracks);
        Assert.Equal("https://aisubtitle.hdslb.com/bfs/cc/9.json", track.Url);
    }

    [Fact]
    public void 需要登录标记_能被解析()
    {
        const string json = """{"need_login_subtitle": true, "subtitle": {"subtitles": []}}""";
        var tracks = SubtitleParser.ParseTracks(Data(json), out var needLogin);
        Assert.True(needLogin);
        Assert.Empty(tracks);
    }

    [Fact]
    public void 空轨道_返回空列表不抛()
    {
        const string json = """{"subtitle": {"subtitles": []}}""";
        Assert.Empty(SubtitleParser.ParseTracks(Data(json), out _));
    }

    [Fact]
    public void 无subtitle节点_返回空列表不抛()
    {
        const string json = """{"aid": 170001}""";
        Assert.Empty(SubtitleParser.ParseTracks(Data(json), out _));
    }

    [Fact]
    public void 重复轨道_去重()
    {
        const string json = """
            {"subtitle": {"subtitles": [
              {"id_str": "1", "lan": "zh", "subtitle_url": "//a/1.json"},
              {"id_str": "1", "lan": "zh", "subtitle_url": "//a/1.json"},
              {"id_str": "2", "lan": "en", "subtitle_url": "//a/2.json"}
            ]}}
            """;
        var tracks = SubtitleParser.ParseTracks(Data(json), out _);
        Assert.Equal(2, tracks.Count);
    }

    // ------------------------------------------------------------ ParseBody

    private const string BodyJson = """
        {
          "font_size": 0.4,
          "type": "AIsubtitle",
          "lang": "zh",
          "body": [
            { "from": 2.0, "to": 1.0, "content": "坏数据: 结束早于开始" },
            { "from": 1.0, "to": 2.5, "content": "  你好  " },
            { "from": 3.0, "to": 4.0, "content": "" },
            { "from": 2.6, "to": 4.8, "content": "世界" }
          ]
        }
        """;

    [Fact]
    public void 解析本体_秒转毫秒_排序_滤空()
    {
        var cues = SubtitleParser.ParseBody(Data(BodyJson));
        Assert.Equal(2, cues.Count);   // 坏时间轴 + 空文本被丢弃

        Assert.Equal(1000, cues[0].FromMs);
        Assert.Equal(2500, cues[0].ToMs);
        Assert.Equal("你好", cues[0].Content);

        // 必须按开始时间有序(二分查找的前提)
        Assert.True(cues[0].FromMs <= cues[1].FromMs);
        Assert.Equal("世界", cues[1].Content);
    }

    [Fact]
    public void 无body节点_返回空列表不抛()
    {
        Assert.Empty(SubtitleParser.ParseBody(Data("{}")));
    }

    // ------------------------------------------------------------ host 兜底(2026-11-15 用户实测)

    [Fact]
    public void 换域兜底_subtitle点bilibili域_换成hdslb域_路径保留()
    {
        var url = "https://subtitle.bilibili.com/bfs/subtitle/abc123.json?xx=1";
        var method = typeof(Services.ApiClient).GetMethod("ReplaceSubtitleHost",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var result = (string)method!.Invoke(null, new object[] { url })!;
        Assert.Equal("https://aisubtitle.hdslb.com/bfs/subtitle/abc123.json?xx=1", result);
    }

    [Fact]
    public void 换域兜底_其它host_原样返回()
    {
        var url = "https://aisubtitle.hdslb.com/bfs/ai_subtitle/prod/x.json";
        var method = typeof(Services.ApiClient).GetMethod("ReplaceSubtitleHost",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(url, (string)method!.Invoke(null, new object[] { url })!);
    }

    [Fact]
    public void DNS失败判定_认识中英文_不认HTTP状态()
    {
        var method = typeof(Services.ApiClient).GetMethod("IsDnsFailure",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        Assert.True((bool)method!.Invoke(null, new object?[] { "字幕服务器连接失败: 不知道这样的主机。" })!);
        Assert.True((bool)method!.Invoke(null, new object?[] { "No such host is known" })!);
        Assert.False((bool)method!.Invoke(null, new object?[] { "字幕下载失败(HTTP 404)" })!);
        Assert.False((bool)method!.Invoke(null, new object?[] { null })!);
    }

    // ------------------------------------------------------------ PickPreferred

    [Fact]
    public void 选轨_人工中文优先于AI中文()
    {
        var tracks = new[]
        {
            new SubtitleTrack { Lan = "ai-zh", IsAi = true, Url = "a" },
            new SubtitleTrack { Lan = "zh-Hans", Url = "b" },
            new SubtitleTrack { Lan = "en", Url = "c" }
        };
        Assert.Equal("zh-Hans", SubtitleParser.PickPreferred(tracks, null)!.Lan);
    }

    [Fact]
    public void 选轨_没有人工时取AI中文()
    {
        var tracks = new[]
        {
            new SubtitleTrack { Lan = "en", Url = "c" },
            new SubtitleTrack { Lan = "ai-zh", IsAi = true, Url = "a" }
        };
        Assert.Equal("ai-zh", SubtitleParser.PickPreferred(tracks, null)!.Lan);
    }

    [Fact]
    public void 选轨_上次语言优先于盲取第一条()
    {
        var tracks = new[]
        {
            new SubtitleTrack { Lan = "ai-zh", IsAi = true, Url = "a" },
            new SubtitleTrack { Lan = "en-US", Url = "b" }
        };
        Assert.Equal("en-US", SubtitleParser.PickPreferred(tracks, "en-US")!.Lan);
    }

    [Fact]
    public void 选轨_什么都没有_取第一条()
    {
        var tracks = new[]
        {
            new SubtitleTrack { Lan = "ja", Url = "a" },
            new SubtitleTrack { Lan = "ko", Url = "b" }
        };
        Assert.Equal("ja", SubtitleParser.PickPreferred(tracks, "zh-Hans")!.Lan);
    }

    [Fact]
    public void 选轨_空列表_返回null()
    {
        Assert.Null(SubtitleParser.PickPreferred(Array.Empty<SubtitleTrack>(), null));
    }

    // ------------------------------------------------------------ FindVisible / VisibleText

    private static List<SubtitleCue> SampleCues() => new()
    {
        new() { FromMs = 1000, ToMs = 2000, Content = "A" },
        new() { FromMs = 2000, ToMs = 3000, Content = "B" },
        new() { FromMs = 5000, ToMs = 6000, Content = "C" }
    };

    [Fact]
    public void 时间轴_起点包含_终点不包含()
    {
        var cues = SampleCues();
        Assert.Equal("A", SubtitleParser.VisibleText(cues, 1000));   // 起点那一刻已经显示
        Assert.Equal("A", SubtitleParser.VisibleText(cues, 1999));
        Assert.Equal("B", SubtitleParser.VisibleText(cues, 2000));   // 上一条结束的这一刻切下一条
        Assert.Equal("", SubtitleParser.VisibleText(cues, 3000));    // 结束即隐藏
        Assert.Equal("", SubtitleParser.VisibleText(cues, 4000));
    }

    [Fact]
    public void 时间轴_向前向后跳都能找到()
    {
        var cues = SampleCues();
        Assert.Equal("C", SubtitleParser.VisibleText(cues, 5500));   // 向后
        Assert.Equal("A", SubtitleParser.VisibleText(cues, 1500));   // 再跳回前
        Assert.Equal("B", SubtitleParser.VisibleText(cues, 2500));
    }

    [Fact]
    public void 时间轴_重叠cue换行拼接()
    {
        var cues = new List<SubtitleCue>
        {
            new() { FromMs = 1000, ToMs = 4000, Content = "上" },
            new() { FromMs = 2000, ToMs = 3000, Content = "下" }
        };
        Assert.Equal("上", SubtitleParser.VisibleText(cues, 1500));
        Assert.Equal("上\n下", SubtitleParser.VisibleText(cues, 2500));
        Assert.Equal("上", SubtitleParser.VisibleText(cues, 3500));
    }

    [Fact]
    public void 时间轴_空列表返回空()
    {
        Assert.Equal("", SubtitleParser.VisibleText(new List<SubtitleCue>(), 1234));
    }

    [Fact]
    public void 时间轴_超范围时间不抛()
    {
        var cues = SampleCues();
        Assert.Equal("", SubtitleParser.VisibleText(cues, -500));
        Assert.Equal("", SubtitleParser.VisibleText(cues, long.MaxValue - 1));
    }
}
