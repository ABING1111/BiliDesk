using BiliDesk.Services;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 诊断报告里"哪些事件日志算本程序的崩溃"的判定(2026-11-28 新增)。
///
/// ★ 为什么要单测这么小的一件事:
///   这条判定错了会**静默**地毁掉整份报告的价值 —— 用户发来的是别的程序的一堆
///   崩溃记录, 而真正的 BiliDesk 崩溃淹没在噪音里。而且它错得很隐蔽(报告看着"有内容")。
///
/// ★ 实测踩过的坑: 只判 "消息里含 BiliDesk" 会把**从 BiliDesk 目录启动的 dotnet.exe**
///   (探针就是那样跑的)也认成 BiliDesk 崩溃 —— 因为崩溃消息里带着源码路径
///   <c>C:\...\BiliDesk-main\...</c>。必须按 Windows 自己写的那一段匹配。
/// </summary>
public class DiagnosticReportTests
{
    [Theory]
    // .NET Runtime(1026) 的格式
    [InlineData("Application: BiliDesk.exe\nCoreCLR Version: 10.0", true)]
    // Application Error(1000) 的格式
    [InlineData("Faulting application name: BiliDesk.exe, version: 1.8.8.0", true)]
    // WER 的格式
    [InlineData("Application: BiliDesk.exe, version 1.8.8", true)]
    public void 认得出本程序的崩溃(string message, bool expected)
        => Assert.Equal(expected, DiagnosticReportService.IsCrashOfThisApp(message, "BiliDesk.exe"));

    [Theory]
    // ★ 关键回归: 路径里含 BiliDesk, 但崩溃的是 dotnet.exe
    [InlineData("Application: dotnet.exe\nStack:\n  at Program.Main() in C:\\Agent\\Bilidesk\\BiliDesk-main\\.probes\\x\\Program.cs:line 1")]
    // 别的程序
    [InlineData("Application: msedgewebview2.exe\nDescription: crash")]
    [InlineData("Faulting application name: chrome.exe")]
    // 空
    [InlineData("")]
    public void 认不出别人(string message)
        => Assert.False(DiagnosticReportService.IsCrashOfThisApp(message, "BiliDesk.exe"));

    [Fact]
    public void 大小写不敏感()
    {
        // Windows 事件里的进程名大小写不总是与磁盘上一致
        Assert.True(DiagnosticReportService.IsCrashOfThisApp(
            "Application: bilidesk.EXE", "BiliDesk.exe"));
    }

    [Fact]
    public void 空的exe名一律不匹配()
    {
        // 防御: 拿不到进程名时不能"全匹配"
        Assert.False(DiagnosticReportService.IsCrashOfThisApp("Application: ", ""));
        Assert.False(DiagnosticReportService.IsCrashOfThisApp("Application: x.exe", ""));
    }

    [Fact]
    public void 报告路径带版本号与时间戳()
    {
        var path = DiagnosticReportService.DefaultReportPath();
        Assert.EndsWith(".zip", path);
        Assert.Contains("v" + BiliDesk.App.AppVersion, path);
        // 落在下载目录(用户找得到), 不是隐藏的数据目录
        Assert.StartsWith(BiliDesk.Services.AppPaths.DownloadDir, path);
    }
}
