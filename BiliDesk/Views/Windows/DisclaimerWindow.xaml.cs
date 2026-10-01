using System.ComponentModel;
using System.Windows;
using BiliDesk.Views;

namespace BiliDesk.Views.Windows;

/// <summary>
/// "非官方软件 / 免责声明"窗口。两种用法共用这一份 XAML 与同一段正文:
///
///   1. **首次启动的确认**(默认): 二选一且没有第三条路 —— 同意才继续, 不同意就退出程序。
///      所以除了两个按钮, Alt+F4 / 标题栏的 × / Esc 这些"绕过按钮"的关闭路径
///      也一律按"不同意"处理, 否则窗口一关主界面照样起来, 用户等于没做选择。
///   2. **只读查看**(viewer: true, 从设置页「关于」进入): 只留一个「关闭」。
///
/// 两种模式不各写一个窗口, 是为了保证"用户同意的"和"事后能查到的"永远是同一段文本。
/// </summary>
public partial class DisclaimerWindow : FluentWindow
{
    /// <summary>用户是否点了「同意并继续」</summary>
    public bool Agreed { get; private set; }

    private readonly bool _viewer;
    private bool _decided;

    public DisclaimerWindow() : this(false) { }

    public DisclaimerWindow(bool viewer)
    {
        InitializeComponent();
        _viewer = viewer;
        Closing += OnClosing;

        if (viewer)
        {
            Title = "免责声明 · BiliDesk";
            FooterHint.Text = "本声明随时可在这里重新查看。";
            BtnDecline.Content = "关闭";
            BtnAccept.Visibility = Visibility.Collapsed;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 没点过按钮就关闭 = 不同意(只读模式不参与这个判断)
        if (!_viewer && !_decided) Agreed = false;
    }

    private void OnAcceptClick(object sender, RoutedEventArgs e)
    {
        Agreed = true;
        _decided = true;
        Close();
    }

    private void OnDeclineClick(object sender, RoutedEventArgs e)
    {
        if (!_viewer) Agreed = false;
        _decided = true;
        Close();
    }
}
