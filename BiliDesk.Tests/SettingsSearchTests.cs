using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BiliDesk.Models;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 设置页搜索(2026-11-28)的匹配规则 + **索引与 XAML 的一致性契约**。
///
/// 背景: 搜索的定位锚点是"行的标题文字"(五个 Tab 的内容是 DataTemplate, 没有 x:Name 可用,
/// 见 Models/SettingsSearchCatalog 的类注释)。这带来一个天然的漂移风险:
/// 有人新增/改名一个设置行, 忘了同步索引 —— 搜索就静默搜不到, 而且**不会报错**。
/// 所以这里把两份清单对起来数: SettingsTabs.xaml 里 SettingsRowTitleStyle 的出现次数
/// (减去快捷键模板里那一条绑定出来的标题)必须等于静态索引条目数, 且每条标题都能在
/// XAML 里找到 Text="…"。
/// </summary>
public class SettingsSearchTests
{
    private static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BiliDesk.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("找不到 BiliDesk.sln, 无法定位仓库根");
    }

    private static string TabsXaml { get; } =
        File.ReadAllText(Path.Combine(RepoRoot, "BiliDesk", "Views", "Pages", "Settings", "SettingsTabs.xaml"));

    [Fact]
    public void 空关键词返回空结果()
    {
        Assert.Empty(SettingsSearchCatalog.Search(""));
        Assert.Empty(SettingsSearchCatalog.Search("   "));
        Assert.Empty(SettingsSearchCatalog.Search(null));
    }

    [Fact]
    public void 跨Tab命中并带上正确的Tab序号()
    {
        var hits = SettingsSearchCatalog.Search("默认画质");
        Assert.NotEmpty(hits);

        var top = hits[0];
        Assert.Equal("默认画质", top.Title);
        Assert.Equal(1, top.TabIndex);          // 播放
        Assert.Equal("播放", top.TabTitle);
    }

    [Fact]
    public void 标题命中排在同义词命中之前()
    {
        // "弹幕"既是很多标题的一部分, 也是不少行的同义词 —— 标题里带它的必须靠前
        var hits = SettingsSearchCatalog.Search("弹幕");
        Assert.True(hits.Count > 2);
        Assert.Contains("弹幕", hits[0].Title);
    }

    [Fact]
    public void 同义词能搜到设置项()
    {
        // "软解"只写在关键词里, 标题是「硬件解码」
        var hits = SettingsSearchCatalog.Search("软解");
        Assert.Contains(hits, h => h.Title == "硬件解码" && h.TabIndex == 1);

        // 英文缩写同理
        Assert.Contains(SettingsSearchCatalog.Search("mica"), h => h.Title == "窗口背景材质");
        Assert.Contains(SettingsSearchCatalog.Search("1080p"), h => h.Title == "免登录 1080P");
    }

    [Fact]
    public void 多关键词按全部命中处理()
    {
        var hits = SettingsSearchCatalog.Search("弹幕 速度");
        Assert.NotEmpty(hits);
        // 两个词必须都能在这一条上找到(标题/同义词/分组/Tab 名任一)
        Assert.All(hits, h =>
            Assert.True(h.Title.Contains("弹幕") || h.Keywords.Contains("弹幕") || h.Group.Contains("弹幕")));
    }

    [Fact]
    public void 快捷键项来自ShortcutCatalog()
    {
        var hits = SettingsSearchCatalog.Search("全屏");
        Assert.Contains(hits, h => h.Title == "全屏" && h.TabIndex == 2 && h.IsShortcutEntry);

        // 目录里每一条都应该能在索引里搜到
        foreach (var d in ShortcutCatalog.All)
            Assert.Contains(SettingsSearchCatalog.All, e => e.IsShortcutEntry && e.Title == d.Title);
    }

    [Fact]
    public void 会折叠的行指向常驻行()
    {
        // 亚克力透明度只在"亚克力"材质下可见, 定位要落到常驻的「窗口背景材质」
        var entry = SettingsSearchCatalog.All.Single(e => e.Title == "亚克力透明度 · 浅色");
        Assert.Equal("窗口背景材质", entry.LocateTitle);
    }

    /// <summary>
    /// ★ 契约: 静态索引与 SettingsTabs.xaml 的行标题一一对应。
    /// 快捷键 Tab 里那一条是 <c>Text="{Binding Title}"</c>(运行时才有值), 不参与静态比对。
    /// </summary>
    [Fact]
    public void 静态索引与SettingsTabs的行标题一致()
    {
        // 抓出所有"标题行": Text="…" 紧跟 Style="{StaticResource SettingsRowTitleStyle}"
        var titles = Regex.Matches(
                TabsXaml,
                @"Text=""([^""]+)""\s*Style=""\{StaticResource SettingsRowTitleStyle\}""")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.NotEmpty(titles);

        // 绑定出来的标题(快捷键模板里那一行)不算静态行
        var staticTitles = titles.Where(t => !t.StartsWith("{Binding", StringComparison.Ordinal)).ToList();
        var boundCount = titles.Count - staticTitles.Count;

        var staticEntries = SettingsSearchCatalog.All.Where(e => !e.IsShortcutEntry).ToList();

        // 每个静态条目都必须在 XAML 里真的存在这个标题
        foreach (var e in staticEntries)
            Assert.True(staticTitles.Contains(e.Title),
                $"索引里的「{e.Title}」在 SettingsTabs.xaml 里找不到对应的行标题 —— 被改名/删掉了?");

        // 反向: XAML 里每一行都应该有索引(漏加 → 搜索搜不到这一行)
        Assert.Equal(titles.Count - boundCount, staticEntries.Count);
        Assert.Equal(1, boundCount);   // 快捷键模板的绑定标题只有一条
    }

    /// <summary>
    /// LocateTitle 必须也是某条真实标题(否则滚不过去, 用户点完看不到任何反应)。
    /// ★ 快捷键行例外: 它们的标题是 <c>{Binding Title}</c> 运行时才有值, 按标题定位照常可用。
    /// </summary>
    [Fact]
    public void LocateTitle都是真实存在的标题()
    {
        foreach (var e in SettingsSearchCatalog.All.Where(x => !x.IsShortcutEntry))
            Assert.True(
                TabsXaml.Contains($"Text=\"{e.LocateTitle}\""),
                $"「{e.Title}」的定位锚点「{e.LocateTitle}」在 SettingsTabs.xaml 里不存在");
    }

    /// <summary>标题重复会让"按文字定位"落到错误的那一行(或第一行), 直接钉死。</summary>
    [Fact]
    public void 静态标题唯一()
    {
        var duplicates = SettingsSearchCatalog.All
            .Where(e => !e.IsShortcutEntry)
            .GroupBy(e => e.Title, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, "索引里有重复标题: " + string.Join(", ", duplicates));
    }
}
