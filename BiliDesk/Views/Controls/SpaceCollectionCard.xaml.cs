using System;
using System.Windows.Controls;
using System.Windows.Input;
using BiliDesk.Models;

namespace BiliDesk.Views.Controls;

/// <summary>
/// UP 主主页「合集和系列」卡片。
///
/// ★ 控件自己不导航: 只把点击抛成 <see cref="OpenRequested"/> 事件,
///   由页面决定用哪个 NavigationDispatcher / 带什么 UP 名去打开详情页。
/// </summary>
public partial class SpaceCollectionCard : UserControl
{
    /// <summary>整卡被点击(左键)。</summary>
    public event EventHandler? OpenRequested;

    public SpaceCollectionCard()
    {
        InitializeComponent();
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (DataContext is not SpaceCollectionItem) return;
        OpenRequested?.Invoke(this, EventArgs.Empty);
    }
}
