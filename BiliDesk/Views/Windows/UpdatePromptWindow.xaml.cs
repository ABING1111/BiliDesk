using System;
using System.Windows;
using BiliDesk.Helpers;
using BiliDesk.Services;
using BiliDesk.Views;

namespace BiliDesk.Views.Windows;

/// <summary>
/// 「发现新版本」弹窗。自动检查(启动后)与手动检查(设置页「检查更新」按钮)共用这一个窗口。
///
/// 按钮语义:
///   · 跳过此版本 —— 把版本号记进设置, **自动**检查对这个版本不再弹窗(手动检查仍会弹, 见下);
///   · 以后再说 —— 只关窗口, 下次自动检查还会提醒;
///   · 前往 GitHub 下载 —— 跳系统浏览器打开这个 Release 页, 程序内不下载不安装。
///
/// ★ 2026-11-21: 正文改为渲染后的 Markdown(见 MarkdownView)。发布说明里
///   开头重复的 "## 1.8.1 更新内容" 与末尾的下载/SHA256 段由 <see cref="ReleaseNotes.Clean"/> 裁掉 ——
///   弹窗里只需要"这版改了什么", 安装包名与校验和属于 GitHub 发布页的事。
/// </summary>
public partial class UpdatePromptWindow : FluentWindow
{
    private readonly UpdateInfo _info;

    public UpdatePromptWindow(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;

        Title = $"发现新版本 v{info.Version}";
        NewVersionText.Text = $"v{info.Version}";
        VersionLine.Text = BuildVersionLine(info);

        var notes = ReleaseNotes.Clean(info.Notes, info.Version.ToString());
        NotesView.Markdown = notes;
        EmptyNotes.Visibility = notes.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>「当前版本 v1.8.0 · 发布于 2026-11-20」, 缺哪段就少哪段(不写"未知日期")。</summary>
    private static string BuildVersionLine(UpdateInfo info)
    {
        var line = $"当前版本 v{App.AppVersion}";
        if (info.PublishedAt is { } published)
            line += " · 发布于 " + published.ToLocalTime().ToString("yyyy-MM-dd");
        return line;
    }

    /// <summary>手动检查也走这个窗口 —— 但手动检查时用户是主动来的, 「跳过此版本」没意义, 会由调用方隐藏</summary>
    public bool AllowSkip
    {
        get => BtnSkip.Visibility == Visibility.Visible;
        set => BtnSkip.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSkipClick(object sender, RoutedEventArgs e)
    {
        Svc.Settings.SetSkippedVersion(_info.Version.ToString());
        Close();
    }

    private void OnLaterClick(object sender, RoutedEventArgs e) => Close();

    private void OnGithubClick(object sender, RoutedEventArgs e)
        => UpdateChecker.OpenInBrowser(_info.ReleaseUrl);
}
