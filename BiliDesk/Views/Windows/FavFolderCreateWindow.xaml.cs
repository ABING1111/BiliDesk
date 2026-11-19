using System.Windows;
using System.Windows.Controls;
using BiliDesk.Helpers;

namespace BiliDesk.Views.Windows;

/// <summary>
/// 新建收藏夹的输入窗(2026-11-19)。
/// 只负责"名称 + 私密开关"的收集与前置校验, 请求由 <see cref="FavFolderActions.CreateAsync"/> 发。
/// 用法: <c>if (dlg.ShowDialog() == true) { dlg.FolderTitle; dlg.IsPrivate; }</c>
/// </summary>
public partial class FavFolderCreateWindow : FluentWindow
{
    /// <summary>Trim 之后的名称(仅 ShowDialog() 返回 true 时有意义)</summary>
    public string FolderTitle { get; private set; } = "";

    /// <summary>是否建成私密收藏夹</summary>
    public bool IsPrivate { get; private set; }

    public FavFolderCreateWindow()
    {
        InitializeComponent();
        // 打开即可输入: 用户点「新建收藏夹」接下来的动作一定是敲名字
        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>
    /// 输入变化: 刷新计数与提示, 并据此开关「创建」按钮。
    /// ★ 计数/禁用都走同一个 <see cref="FavFolderName.TryNormalize"/>, 不在两处各写一套判断 ——
    ///   否则很容易出现"计数说 20/20 合法, 按钮却还是灰的"。
    /// </summary>
    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        var raw = NameBox.Text ?? "";
        CountText.Text = $"{raw.Trim().Length}/{FavFolderName.MaxLength}";

        var ok = FavFolderName.TryNormalize(raw, out _, out var error);
        BtnOk.IsEnabled = ok;
        HintText.Text = ok ? "给收藏夹起个名字" : (error ?? "");
        HintText.Foreground = ok
            ? (System.Windows.Media.Brush)FindResource("TextTertiaryBrush")
            : (System.Windows.Media.Brush)FindResource("DangerTextBrush");
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        // 再校验一次(按钮理论上已经禁用, 但 Enter 触发 IsDefault 的路径也该兜住)
        if (!FavFolderName.TryNormalize(NameBox.Text, out var title, out var error))
        {
            HintText.Text = error ?? "";
            HintText.Foreground = (System.Windows.Media.Brush)FindResource("DangerTextBrush");
            return;
        }

        FolderTitle = title;
        IsPrivate = PrivateCheck.IsChecked == true;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
