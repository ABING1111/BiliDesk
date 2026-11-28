using System;
using System.Net;
using System.Xml;
using BiliDesk.Services;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 自动更新的纯逻辑部分: Atom 备用源解析 + HTTP 失败分类。
/// 网络请求本身不进单测(会变成"今天 GitHub 通不通"的抽奖), 只测"拿到内容之后怎么判断"。
/// </summary>
public class UpdateCheckerTests
{
    /// <summary>比当前版本大的三段式版本号 —— 以后升版本也不会把测试写死。</summary>
    private static string NewerTag()
    {
        var current = Version.Parse(BiliDesk.App.AppVersion);
        return $"v{current.Major + 1}.0.0";
    }

    private static string AtomFeed(string idTag, string title) => """
<?xml version="1.0" encoding="UTF-8"?>
<feed xmlns="http://www.w3.org/2005/Atom">
  <title>Release notes from BiliDesk</title>
  <entry>
    <id>tag:github.com,2008:Repository/123456/__TAG__</id>
    <updated>2026-11-20T10:00:00Z</updated>
    <link rel="alternate" type="text/html" href="https://github.com/ABING1111/BiliDesk/releases/tag/__TAG__" />
    <title>__TITLE__</title>
  </entry>
</feed>
""".Replace("__TAG__", idTag).Replace("__TITLE__", title);

    [Fact]
    public void Atom_版本号优先取id()
    {
        var tag = NewerTag();
        var info = UpdateChecker.ParseAtomFeed(AtomFeed(tag, "BiliDesk " + tag));

        Assert.NotNull(info);
        Assert.Equal(Version.Parse(tag[1..]), info!.Version);
        Assert.Equal($"https://github.com/ABING1111/BiliDesk/releases/tag/{tag}", info.ReleaseUrl);
        Assert.NotNull(info.PublishedAt);
    }

    [Fact]
    public void Atom_镜像源会重写Release链接()
    {
        var tag = NewerTag();
        var info = UpdateChecker.ParseAtomFeed(AtomFeed(tag, "BiliDesk " + tag), "https://gh-proxy.com/");

        Assert.NotNull(info);
        Assert.Equal($"https://gh-proxy.com/https://github.com/ABING1111/BiliDesk/releases/tag/{tag}", info!.ReleaseUrl);
    }

    [Fact]
    public void Atom_id解析不出时回落到title()
    {
        var tag = NewerTag();
        var info = UpdateChecker.ParseAtomFeed(AtomFeed("not-a-version", tag));

        Assert.NotNull(info);
        Assert.Equal(Version.Parse(tag[1..]), info!.Version);
    }

    [Fact]
    public void Atom_没有entry时返回null()
    {
        const string xml = """
<feed xmlns="http://www.w3.org/2005/Atom">
  <title>Release notes from BiliDesk</title>
</feed>
""";
        Assert.Null(UpdateChecker.ParseAtomFeed(xml));
    }

    [Fact]
    public void Atom_版本不比当前新时返回null()
    {
        Assert.Null(UpdateChecker.ParseAtomFeed(AtomFeed("v0.0.1", "v0.0.1")));
    }

    [Fact]
    public void Atom_坏XML抛XmlException()
    {
        // 代理返回 HTML 登录页/错误页时走这里, 由 CheckViaAtomAsync 转成 Parse 失败
        Assert.Throws<XmlException>(() => UpdateChecker.ParseAtomFeed("<feed>"));
    }

    [Theory]
    [InlineData(null, "https://api.github.com/repos/a/b/releases/latest", "https://api.github.com/repos/a/b/releases/latest")]
    [InlineData("", "https://api.github.com/x", "https://api.github.com/x")]
    [InlineData("   ", "https://api.github.com/x", "https://api.github.com/x")]
    [InlineData("https://gh-proxy.com/", "https://api.github.com/x", "https://gh-proxy.com/https://api.github.com/x")]
    [InlineData("https://gh-proxy.com", "https://api.github.com/x", "https://gh-proxy.com/https://api.github.com/x")]
    [InlineData("https://kkgithub.com/{url}", "https://github.com/x", "https://kkgithub.com/https://github.com/x")]
    [InlineData("not-a-url", "https://api.github.com/x", "https://api.github.com/x")]
    public void 镜像源_支持前缀与模板(string? mirror, string url, string expected)
    {
        Assert.Equal(expected, UpdateChecker.MirrorUrl(mirror, url));
    }

    [Theory]
    [InlineData(404, false, null)]
    [InlineData(403, false, UpdateCheckFailureKind.Http)]
    [InlineData(403, true, UpdateCheckFailureKind.RateLimited)]
    [InlineData(429, false, UpdateCheckFailureKind.RateLimited)]
    [InlineData(500, false, UpdateCheckFailureKind.Http)]
    public void HTTP状态码分类(int code, bool rateLimitExhausted, UpdateCheckFailureKind? expected)
    {
        Assert.Equal(expected, UpdateChecker.ClassifyHttpStatus((HttpStatusCode)code, rateLimitExhausted));
    }
}
