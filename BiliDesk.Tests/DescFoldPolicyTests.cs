using BiliDesk.Helpers;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 播放器简介「要不要折叠」的纯判定(2026-11-16)。
///
/// 背景: 折叠本来是给简介下方的「相关视频」让位置; 用户关掉相关视频后,
/// 旧实现仍然按 108 折起来, 信息栏里平白多出一个「展开简介」按钮 —— 报的就是这个。
/// 这里把三条不变量钉死:
///   · 关掉相关视频 ⇒ 永不折叠、永不显示按钮、折叠态复位;
///   · 相关视频在 + 简介不超 6 行 ⇒ 无按钮, 状态复位;
///   · 相关视频在 + 简介超长 ⇒ 折叠 108 / 展开给实际高度, 且必须显示按钮。
/// </summary>
public class DescFoldPolicyTests
{
    [Fact]
    public void 关闭相关视频_超长简介整段展开_不显示按钮()
    {
        var r = DescFoldPolicy.Decide(relatedVisible: false, expanded: false, needed: 900);

        Assert.False(r.ShowButton);
        Assert.False(r.Expanded);
        Assert.Equal(double.PositiveInfinity, r.MaxHeight);
    }

    [Fact]
    public void 关闭相关视频_已展开态被复位()
    {
        // 重新打开相关视频时要回到"默认折叠", 不能带着上一次的展开态。
        var r = DescFoldPolicy.Decide(relatedVisible: false, expanded: true, needed: 900);

        Assert.False(r.ShowButton);
        Assert.False(r.Expanded);
        Assert.Equal(double.PositiveInfinity, r.MaxHeight);
    }

    [Fact]
    public void 关闭相关视频_短简介同样不折叠()
    {
        // 关掉时调用方不测量, needed 传 0 —— 结果不能因此变成"折到 108"。
        var r = DescFoldPolicy.Decide(relatedVisible: false, expanded: false, needed: 0);

        Assert.False(r.ShowButton);
        Assert.Equal(double.PositiveInfinity, r.MaxHeight);
    }

    [Fact]
    public void 相关视频在_短简介不折叠_状态复位()
    {
        var r = DescFoldPolicy.Decide(relatedVisible: true, expanded: true, needed: 60);

        Assert.False(r.ShowButton);
        Assert.False(r.Expanded);
        Assert.Equal(DescFoldPolicy.CollapsedHeight, r.MaxHeight);
    }

    [Fact]
    public void 相关视频在_超长简介折叠态_显示按钮并压到六行()
    {
        var r = DescFoldPolicy.Decide(relatedVisible: true, expanded: false, needed: 900);

        Assert.True(r.ShowButton);
        Assert.False(r.Expanded);
        Assert.Equal(DescFoldPolicy.CollapsedHeight, r.MaxHeight);
    }

    [Fact]
    public void 相关视频在_超长简介展开态_上限是内容实际高度()
    {
        var r = DescFoldPolicy.Decide(relatedVisible: true, expanded: true, needed: 900);

        Assert.True(r.ShowButton);
        Assert.True(r.Expanded);
        Assert.Equal(900, r.MaxHeight);
    }

    [Fact]
    public void 刚好六行_不显示按钮()
    {
        var r = DescFoldPolicy.Decide(relatedVisible: true, expanded: false,
            needed: DescFoldPolicy.CollapsedHeight);

        Assert.False(r.ShowButton);
    }

    [Fact]
    public void 容差以内_不显示按钮()
    {
        // +1 容差: 亚像素误差不该把"刚好 6 行"判成溢出。
        var r = DescFoldPolicy.Decide(relatedVisible: true, expanded: false,
            needed: DescFoldPolicy.CollapsedHeight + 1);

        Assert.False(r.ShowButton);
    }

    [Fact]
    public void 超过容差_显示按钮()
    {
        var r = DescFoldPolicy.Decide(relatedVisible: true, expanded: false,
            needed: DescFoldPolicy.CollapsedHeight + 2);

        Assert.True(r.ShowButton);
    }

    [Fact]
    public void 展开后内容变短_复位成折叠态()
    {
        // 字号/窗口宽度变化让简介变短: 按钮必须消失, 否则用户会看到一个点了没反应的入口。
        var r = DescFoldPolicy.Decide(relatedVisible: true, expanded: true,
            needed: DescFoldPolicy.CollapsedHeight);

        Assert.False(r.ShowButton);
        Assert.False(r.Expanded);
        Assert.Equal(DescFoldPolicy.CollapsedHeight, r.MaxHeight);
    }
}
