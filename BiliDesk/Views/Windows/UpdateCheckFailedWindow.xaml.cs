using System;
using System.Windows;
using BiliDesk.Services;
using BiliDesk.Views;

namespace BiliDesk.Views.Windows;

/// <summary>
/// 「检查更新失败」弹窗。
///
/// 为什么单独一个窗而不是 Toast / MessageBox:
///   · Toast 一闪而过, 用户既看不到原因也没机会马上重试;
///   · MessageBox 是系统旧风格, 与本应用其它窗口观感割裂;
///   · 这里需要承载"重试"的进行中状态, 以及"以后不再自动检查"这个出口。
///
/// ★ 只由自动检查的失败路径打开(手动检查在设置页里用状态行 + Toast, 不再叠一个窗)。
/// </summary>
public partial class UpdateCheckFailedWindow : FluentWindow
{
    private bool _retrying;

    public UpdateCheckFailedWindow(UpdateCheckException error)
    {
        InitializeComponent();
        ShowError(error);
    }

    private void ShowError(UpdateCheckException error)
    {
        HeadlineText.Text = error.Kind switch
        {
            UpdateCheckFailureKind.Network => "无法连接到 GitHub",
            UpdateCheckFailureKind.Timeout => "连接 GitHub 超时",
            UpdateCheckFailureKind.RateLimited => "GitHub 暂时限制了请求",
            UpdateCheckFailureKind.Http => "GitHub 返回了异常",
            UpdateCheckFailureKind.Parse => "更新信息无法解析",
            _ => "检查更新失败"
        };

        MessageText.Text = error.Kind switch
        {
            UpdateCheckFailureKind.Network =>
                "自动检查更新没能连上 GitHub。使用 VPN / 代理、DNS 解析异常或网络不稳定时都可能出现这种情况；"
                + "BiliDesk 的其它功能不受影响。",
            UpdateCheckFailureKind.Timeout =>
                "连接 GitHub 超时。网络较慢，或 VPN / 代理线路不稳定时会出现，可以稍后重试。",
            UpdateCheckFailureKind.RateLimited =>
                "GitHub 暂时限制了请求次数，常见于多人共用的 VPN / 代理出口 IP。"
                + "通常过一段时间会自行恢复，不影响使用 BiliDesk。",
            UpdateCheckFailureKind.Http =>
                "GitHub 返回了异常状态，可能正在维护，或者当前线路受到干扰。"
                + "可以稍后重试，或直接前往发布页查看。",
            UpdateCheckFailureKind.Parse =>
                "已经收到 GitHub 的响应，但内容无法解析。可能是更新接口格式有变化，请前往发布页手动查看。",
            _ => "检查更新时发生了未预期的错误。可以重试，或前往发布页手动查看。"
        };

        // 只暴露有意义的技术细节: 原始异常信息是英文的, 对用户没有帮助。
        DetailText.Text = error.StatusCode is { } code ? $"GitHub API 返回 HTTP {code}" : "";
        DetailText.Visibility = DetailText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e)
    {
        if (_retrying) return;
        _retrying = true;
        BtnRetry.IsEnabled = false;
        RetryStatus.Text = "正在重试…";
        RetryStatus.Visibility = Visibility.Visible;

        try
        {
            var info = await UpdateChecker.CheckAsync();
            if (info == null)
            {
                Svc.Toast.Show("已是最新版本");
                Close();
                return;
            }

            // 先关掉自己再弹新版窗: 本窗的 Owner 是主窗口, 不关的话两个窗会叠在一起。
            Close();

            var owner = SvcWindow.Main ?? Application.Current?.MainWindow;
            var win = new UpdatePromptWindow(info)
            {
                Owner = owner,
                WindowStartupLocation = owner is null
                    ? WindowStartupLocation.CenterScreen
                    : WindowStartupLocation.CenterOwner
            };
            win.Show();
        }
        catch (UpdateCheckException ex)
        {
            ShowError(ex);
            RetryStatus.Text = "重试失败：" + ex.UserMessage;
        }
        catch (Exception ex)
        {
            // catch 不静默是本项目的硬规矩
            App.ReportError(ex);
            RetryStatus.Text = "重试失败，请稍后再试";
        }
        finally
        {
            _retrying = false;
            BtnRetry.IsEnabled = true;
        }
    }

    private void OnOpenGithubClick(object sender, RoutedEventArgs e)
        => UpdateChecker.OpenInBrowser(UpdateChecker.ReleasePageUrl);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        // 用户在网络不通时往往找不到设置里的开关, 这里给一个就近出口。
        // 勾选后写进设置; 下次打开设置页读的是实时值, 不会出现开关状态对不上。
        if (DisableAutoCheck.IsChecked == true && Svc.Settings.AutoCheckUpdate)
        {
            Svc.Settings.SetAutoCheckUpdate(false);
            Svc.Toast.Show("已关闭自动检查更新, 可在设置中重新打开");
        }

        base.OnClosed(e);
    }
}
