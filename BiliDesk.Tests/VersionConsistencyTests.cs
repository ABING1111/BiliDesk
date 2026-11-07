using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 版本号"单一来源"契约测试。
///
/// 背景: App.AppVersion 的注释里写明有 4 个同步点 ——
///   BiliDesk.csproj 的 &lt;Version&gt;、Publish\installer.iss 的 MyAppVersion、
///   发布.ps1 的默认 $Version。
/// 漏改任何一处, 出现的就是"关于页 1.6.4、安装包文件名还是 1.6.3"这种不一致。
/// 人工记忆不可靠, 用测试钉死: 四处不一致时这里直接红。
/// </summary>
public class VersionConsistencyTests
{
    /// <summary>仓库根目录(测试 DLL 在 BiliDesk.Tests/bin/... 下, 向上找 sln)。</summary>
    private static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BiliDesk.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("找不到 BiliDesk.sln, 无法定位仓库根");
    }

    private static string ReadAllText(params string[] parts) =>
        File.ReadAllText(Path.Combine(parts));

    [Fact]
    public void AppVersion_与csproj一致()
    {
        var csproj = ReadAllText(RepoRoot, "BiliDesk", "BiliDesk.csproj");
        var m = Regex.Match(csproj, @"<Version>([\d\.]+)</Version>");
        Assert.True(m.Success, "BiliDesk.csproj 里没有 <Version> 节点");
        Assert.Equal(BiliDesk.App.AppVersion, m.Groups[1].Value);
    }

    [Fact]
    public void AppVersion_与installer一致()
    {
        var iss = ReadAllText(RepoRoot, "Publish", "installer.iss");
        var m = Regex.Match(iss, @"#define\s+MyAppVersion\s+""([\d\.]+)""");
        Assert.True(m.Success, "installer.iss 里没有 MyAppVersion 定义");
        Assert.Equal(BiliDesk.App.AppVersion, m.Groups[1].Value);
    }

    [Fact]
    public void AppVersion_与发布脚本一致()
    {
        var ps1 = ReadAllText(RepoRoot, "发布.ps1");
        // ★ 允许 param() 里还有别的参数(如 -FixIntegrity) —— 只要求 $Version 的默认值存在。
        var m = Regex.Match(ps1, @"param\([^)]*\$Version\s*=\s*""([\d\.]+)""[^)]*\)");
        Assert.True(m.Success, "发布.ps1 里没有默认 $Version 参数");
        Assert.Equal(BiliDesk.App.AppVersion, m.Groups[1].Value);
    }

    [Theory]
    [InlineData(@"^\d+\.\d+\.\d+$")]
    public void AppVersion_格式合法(string pattern)
    {
        // 三段式数字: 安装器/zip 文件名都按这个形状拼, 带 -beta 之类后缀会破约定
        Assert.Matches(pattern, BiliDesk.App.AppVersion);
    }
}
