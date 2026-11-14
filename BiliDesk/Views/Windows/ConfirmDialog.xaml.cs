using System.Windows;
using BiliDesk.Services;
using BiliDesk.Views;

namespace BiliDesk.Views.Windows;

/// <summary>
/// 通用确认框: 替代系统 MessageBox, 与其它 Fluent 窗口共用主题与按钮样式。
///
/// ★ 为什么要它(用户反馈): MessageBox 是系统旧风格窗口, 与应用内界面明显不一致
///   (清空稍后再看 / 退出登录 等确认框都是那种观感)。
/// ★ 用法: ConfirmDialog.Show("确定要…吗?", "清空稍后再看", "清空");
///   返回 true = 用户点了「确定」。
/// </summary>
public partial class ConfirmDialog : FluentWindow
{
    public ConfirmDialog(string message, string title, string okText, string cancelText)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        BtnOk.Content = okText;
        BtnCancel.Content = cancelText;
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>弹出确认框; owner 默认取主窗口。</summary>
    public static bool Show(string message, string title = "BiliDesk",
        string okText = "确定", string cancelText = "取消")
    {
        var dlg = new ConfirmDialog(message, title, okText, cancelText)
        {
            Owner = SvcWindow.Main ?? Application.Current?.MainWindow
        };
        return dlg.ShowDialog() == true;
    }
}
