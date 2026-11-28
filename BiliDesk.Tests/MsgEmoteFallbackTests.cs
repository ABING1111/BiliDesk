using System.Collections.Generic;
using System.Linq;
using BiliDesk.Helpers;
using BiliDesk.Models;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 表情表的"服务端 e_infos + 本地面板"兜底合并(2026-11-28)。
///
/// ★ 为什么值得单测: 用户报的是"私信里发个表情 → 切到别的页面 → 回来表情变成文字"。
///   根因是服务端 `data.e_infos` **经常漏掉"我自己发的"表情**(实测拉 50 个会话:
///   对方发的表情基本都在, 我发的经常整条 e_infos 都是 null)。修复靠"服务端表优先、
///   本地面板补缺", 而这个先后顺序与"不认识的方括号不能乱认"两条都极易写反 ——
///   写反的表现分别是"大会员表情显示成通用表情"和"用户写的 [0] 被画成图"。
/// </summary>
public class MsgEmoteFallbackTests
{
    private static Dictionary<string, MsgEmote> Table(params (string Code, string Url)[] items)
    {
        var d = new Dictionary<string, MsgEmote>();
        foreach (var (code, url) in items)
            d[code] = new MsgEmote { Text = code, Url = url };
        return d;
    }

    // ---------------------------------------------------------------- 多表合并

    [Fact]
    public void SplitMany_服务端表优先()
    {
        var server = Table(("[doge]", "server.png"));
        var panel = Table(("[doge]", "panel.png"), ("[tv_doge]", "panel2.png"));

        var segs = MsgContentParser.SplitMany("看[doge]", new[] { server, panel });

        Assert.Equal(2, segs.Count);
        Assert.True(segs[1].IsEmote);
        // 服务端给的那张表排第一 ⇒ 用它给的地址
        Assert.Equal("server.png", segs[1].Emote!.Url);
    }

    [Fact]
    public void SplitMany_服务端缺的用面板补()
    {
        // 这就是 bug 现场: e_infos 里没有 [嘘声](我发的), 面板里有
        var server = Table(("[吃瓜]", "server.png"));
        var panel = Table(("[嘘声]", "panel.png"));

        var segs = MsgContentParser.SplitMany("你好[嘘声]", new[] { server, panel });

        Assert.Equal(2, segs.Count);
        Assert.True(segs[1].IsEmote);
        Assert.Equal("panel.png", segs[1].Emote!.Url);
    }

    [Fact]
    public void SplitMany_两张表都没有_原样当文字()
    {
        var server = Table(("[吃瓜]", "a.png"));
        var panel = Table(("[嘘声]", "b.png"));

        var segs = MsgContentParser.SplitMany("数组[0]和[未知]", new[] { server, panel });

        Assert.Single(segs);
        Assert.False(segs[0].IsEmote);
        Assert.Equal("数组[0]和[未知]", segs[0].Text);
    }

    [Fact]
    public void SplitMany_全是空表_整段文字()
    {
        var segs = MsgContentParser.SplitMany("你好[doge]",
            new[] { new Dictionary<string, MsgEmote>(), new Dictionary<string, MsgEmote>() });

        Assert.Single(segs);
        Assert.Equal("你好[doge]", segs[0].Text);
    }

    [Fact]
    public void SplitMany_表为null_整段文字()
    {
        var segs = MsgContentParser.SplitMany("你好[doge]", null);
        Assert.Single(segs);
        Assert.False(segs[0].IsEmote);
    }

    [Fact]
    public void SplitMany_中间夹表情_前后文字都在()
    {
        var panel = Table(("[嘘声]", "p.png"));
        var segs = MsgContentParser.SplitMany("前[嘘声]后", new[] { panel });

        Assert.Equal(3, segs.Count);
        Assert.Equal("前", segs[0].Text);
        Assert.True(segs[1].IsEmote);
        Assert.Equal("后", segs[2].Text);
    }

    [Fact]
    public void SplitMany_连续表情_不产生空文字段()
    {
        var panel = Table(("[嘘声]", "p.png"), ("[冷]", "q.png"));
        var segs = MsgContentParser.SplitMany("[嘘声][冷]", new[] { panel });

        Assert.Equal(2, segs.Count);
        Assert.All(segs, s => Assert.True(s.IsEmote));
        Assert.All(segs, s => Assert.NotEqual("", s.Text));
    }

    // ---------------------------------------------------------------- 短代码扫描

    [Fact]
    public void Shortcodes_只认方括号形状_不查表()
    {
        var codes = MsgContentParser.Shortcodes("你好[嘘声]和[待办]").ToList();

        Assert.Equal(new[] { "[嘘声]", "[待办]" }, codes);
    }

    [Fact]
    public void Shortcodes_未闭合的方括号不算()
    {
        Assert.Empty(MsgContentParser.Shortcodes("看[doge"));
    }

    [Fact]
    public void Shortcodes_超长的方括号不算()
    {
        // 超过 MaxShortcodeLength 的一定不是表情(省掉无谓的查表)
        var tooLong = "[" + new string('x', MsgContentParser.MaxShortcodeLength) + "]";
        Assert.Empty(MsgContentParser.Shortcodes(tooLong));
    }

    [Fact]
    public void Shortcodes_纯文字_没有结果()
    {
        Assert.Empty(MsgContentParser.Shortcodes("就是一句普通的话"));
        Assert.Empty(MsgContentParser.Shortcodes(""));
        Assert.Empty(MsgContentParser.Shortcodes(null));
    }

    [Fact]
    public void Shortcodes_连续短代码都能扫出来()
    {
        var codes = MsgContentParser.Shortcodes("[嘘声][冷][喜欢]").ToList();
        Assert.Equal(3, codes.Count);
        Assert.Equal("[嘘声]", codes[0]);
        Assert.Equal("[喜欢]", codes[2]);
    }

    // ---------------------------------------------------------------- 表情尺寸(共用一份)

    [Fact]
    public void EmoteSide_小表情比文字高一点()
    {
        // 12.5 * 1.55 = 19.375 ⇒ 圆整 19
        Assert.Equal(19, MsgContentParser.EmoteSide(12.5, 1));
    }

    [Fact]
    public void EmoteSide_大表情明显更大()
    {
        // 12.5 * 2.4 = 30
        Assert.Equal(30, MsgContentParser.EmoteSide(12.5, 2));
    }

    [Fact]
    public void EmoteSide_字号非法时按12_5兜底()
    {
        // 字号没继承到(为 0)时不能算出 0 尺寸的图
        Assert.Equal(MsgContentParser.EmoteSide(12.5, 1), MsgContentParser.EmoteSide(0, 1));
        Assert.Equal(MsgContentParser.EmoteSide(12.5, 1), MsgContentParser.EmoteSide(-3, 1));
    }

    [Fact]
    public void EmoteSide_尺寸随字号线性变化()
    {
        var at12 = MsgContentParser.EmoteSide(12, 1);
        var at24 = MsgContentParser.EmoteSide(24, 1);
        Assert.True(at24 > at12);
        Assert.Equal(19, at12);   // 12 * 1.55 = 18.6 ⇒ 19
        Assert.Equal(37, at24);   // 24 * 1.55 = 37.2 ⇒ 37
    }
}
