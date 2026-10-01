using System.Windows;
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
/// </summary>
public partial class UpdatePromptWindow : FluentWindow
{
    private readonly UpdateInfo _info;

    public UpdatePromptWindow(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;

        Headline.Text = $"发现新版本 v{info.Version}";
        Subline.Text = $"当前版本 v{App.AppVersion} · {info.Title}";
        NotesText.Text = string.IsNullOrWhiteSpace(info.Notes)
            ? "(这个版本没有填写发布说明)"
            : info.Notes;
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
