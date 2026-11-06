using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 搜索结果里的一个用户 / UP 主(2026-11-06 新增, issue #47)。
/// 左键单击 → 在主窗口内容区里打开 TA 的主页。
/// </summary>
public partial class UserCard : UserControl
{
    /// <summary>
    /// 本次左键手势是否从这一行上按下。
    ///
    /// ★ 与 VideoCard._pressInside 同款(issue #50): 打开动作挂在 MouseLeftButtonUp 上,
    ///   而 Up 是按抬起坐标重新命中测试的 —— 窗口最大化/列表重排后, 同一个屏幕坐标可能
    ///   落到另一行上。没有配对 Down 的 Up 一律丢弃, 避免误打开别人的主页。
    /// </summary>
    private bool _pressInside;

    public UserCard()
    {
        InitializeComponent();
        // 容器复用/换数据时清掉手势标记, 避免上一行残留的"已按下"状态误开下一个用户。
        DataContextChanged += (_, _) => _pressInside = false;
    }

    private void OnCardPress(object sender, MouseButtonEventArgs e) => _pressInside = true;

    private void OnCardLeave(object sender, MouseEventArgs e) => _pressInside = false;

    private void OnOpen(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (!_pressInside) return;
        _pressInside = false;

        if (DataContext is not SearchUserItem u || u.Mid <= 0) return;

        // 与搜索卡片/动态页左栏同一条路: 内嵌进主窗口, 自带返回按钮
        new NavigationDispatcher(Window.GetWindow(this)).OpenUserSpace(u.Mid.ToString());
    }
}
