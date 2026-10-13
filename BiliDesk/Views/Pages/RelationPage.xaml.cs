using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

/// <summary>
/// 「关注 / 粉丝」页。
/// 数据与交互都在 <see cref="RelationViewModel"/>, 这个 code-behind 只做事件转发、
/// 滚动加载、列宽测量与键盘 —— 与 FollowPage 同一套分工。
/// </summary>
public partial class RelationPage : UserControl
{
    private const double AutoLoadThreshold = 240;

    /// <summary>
    /// 自动补页的"闸门": 一旦放行, 必须等这一次 <c>LoadMoreAsync</c> 跑完才允许再放行。
    ///
    /// ★ 为什么需要它: <see cref="OnListScrollChanged"/> 每次滚动都会响,
    ///   而补完一页之后 ScrollViewer 高度变了, **ScrollChanged 会自己再响一次** ——
    ///   于是"滚到底"这一下会连着打出好几页请求。VM 里的 <c>LoadingMore</c> 只能挡住
    ///   **同时**在飞的那一次, 挡不住"上一页刚落地、闸门还没来得及重新关上"的空档。
    /// </summary>
    private bool _autoLoadArmed = true;

    public RelationPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // ★ 列数靠**卡片墙容器的实际宽度**算(见 RelationViewModel.RecalcColumns), 不再用
        //   helpers:Responsive 的窗口档位: 粉丝模式没有左栏、关注模式有 164 的分组栏,
        //   同一个档位下两者能差 200 宽, 套档位就会出现"右边空一大块"或者"挤成两列"。
        WallHost.SizeChanged += (_, e) =>
        {
            if (Vm != null) Vm.WallWidth = e.NewSize.Width;
        };
    }

    private RelationViewModel? Vm => DataContext as RelationViewModel;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 重置闸门: 页面重新上屏时 ScrollViewer 会回到顶部并再次触发 ScrollChanged
        _autoLoadArmed = true;

        // ★ 输入法组合事件挂在**两个搜索框自己身上**, 不是 InputManager 的全局静态事件。
        //   全局静态事件会被整个应用里任何一个输入框触发, 而我们要的只是"这个搜索框在拼词";
        //   更麻烦的是 MainWindow 的页面实例是长期缓存的(见 MainWindow.NavigateToRelation),
        //   静态事件持有实例 = 页面永远不被回收。这里在元素上挂, 元素回收时处理器跟着走。
        AttachImeHandlers(SearchBoxWideHost);
        AttachImeHandlers(SearchBoxNarrowHost);

        // 兜底: 只在**从没初始化过**时按默认的「关注」加载一次。
        // ★ 千万别写成无条件的 InitAsync(Following): Loaded 在每次重新上屏时都会再触发,
        //   而"用户这次点的是关注还是粉丝"只存在于 MainWindow.NavigateToRelation 的参数里,
        //   Loaded 拿不到 —— 无条件刷成 Following 会把刚设好的 Fans 模式当场改掉。
        if (Vm != null && !Vm.Inited) await Vm.InitAsync(RelationMode.Following);

        // 首帧后把真实宽度给 VM(布局刚算完, 这时 ActualWidth 才是最终值)
        if (Vm != null && WallHost.ActualWidth > 0) Vm.WallWidth = WallHost.ActualWidth;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _autoLoadArmed = true;

    /// <summary>
    /// 输入法组合事件。
    ///
    /// ★ 为什么必须管: 中文用户打一个词要经过若干中间态, 每个中间态都会触发 TextChanged。
    ///   一变就搜的话, 搜三个字母会发三次请求; 而 B 站接口不接受关键字, 每次搜索都可能
    ///   触发一次续翻请求 ⇒ 一次打字可能打出去六七个 API 请求, 必然撞风控。
    ///   现在: 组合期间(<see cref="RelationViewModel.IsImeComposing"/>)直接不启动搜索,
    ///   拼完之后再走 300ms 防抖。
    /// ★ PreviewTextInput 一响就是"开始拼了"; TextInputUpdate 里
    ///   <c>IsCompositionComplete</c> 为真才是"拼完了"。没输入法时这两个都不触发。
    /// </summary>
    /// <param name="host">
    /// 搜索框所在的 Grid(宽窄两份)。组合事件要挂在**里面那个 TextBox** 上 ——
    /// 挂在 Grid 上收不到, 因为输入法事件只在可接收文本的元素上冒泡。
    /// </param>
    private void AttachImeHandlers(FrameworkElement? host)
    {
        if (host == null) return;
        var box = host.FindName("SearchBoxWide") as TextBox ?? host.FindName("SearchBoxNarrow") as TextBox;
        if (box == null) return;
        // ★ PreviewTextInput 带 TextComposition ⇒ "正在拼词";
        //   TextInput(提交之后才响)⇒ "拼完了"。
        //   刻意**不**用 TextInputUpdate + IsCompositionComplete:
        //   TextCompositionEventArgs 上根本没有那个成员(TextComposition 只有 Text/
        //   ControlText/CompositionText 等), 写了编译不过。
        TextCompositionManager.AddPreviewTextInputHandler(box, (_, _) => Vm?.BeginImeComposition());
        TextCompositionManager.AddTextInputHandler(box, (_, _) => Vm?.EndImeComposition());
        // ★ 按 Esc 取消拼词时 TextInput 不会响, _imeComposing 会**卡在 true**,
        //   于是之后再也搜不了(每次都要等一个永远不来的"拼完了")。必须在这里解开。
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Vm?.CancelImeComposition();
        };
    }

    // ------------------------------------------------------------ 返回与键盘

    /// <summary>回「我的」页(右下角悬浮按钮 / Esc)</summary>
    private void OnBackToMineClick(object sender, RoutedEventArgs e)
        => App.MainVm.Navigate(PageKey.Mine);

    /// <summary>
    /// 键盘: Esc 回「我的」, F5 刷新。
    ///
    /// ★ Esc 的顺序(2026-10-13 改): **先清搜索、再返回**。
    ///   列表可能几百条, 没有键盘出口的话想走只能去够那个悬浮按钮; F5 与浏览器手感对齐。
    /// ★ 输入法正在拼词时按 Esc 的语义是"取消这次拼词", 不是"离开页面" ——
    ///   不区分的话, 用户想取消拼音输入却直接被踢回「我的」页, 是很明显的"这程序没脑子"。
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (Vm?.IsImeComposing == true) { e.Handled = true; return; }
            if (!string.IsNullOrEmpty(Vm?.SearchText))
            {
                Vm!.ClearSearchCommand.Execute(null);
                e.Handled = true;
                return;
            }
            OnBackToMineClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5)
        {
            if (Vm != null)
            {
                _ = Vm.RefreshAsync(reloadGroups: true);
                _autoLoadArmed = true;
            }
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    // ------------------------------------------------------------ 滚动加载

    /// <summary>滚到接近底部自动补下一页(见 RelationViewModel.LoadMoreAsync)</summary>
    private async void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Vm == null) return;
        var sv = (ScrollViewer)sender;

        // 用户往回滚 ⇒ 重新武装, 这样下次再滚到底还能继续补页
        if (e.VerticalOffset < sv.ViewportHeight * 0.5) _autoLoadArmed = true;

        if (!_autoLoadArmed) return;
        if (sv.ScrollableHeight - sv.VerticalOffset > AutoLoadThreshold) return;

        _autoLoadArmed = false;
        await Vm.LoadMoreAsync();
        // 补完一页(可能没补到)后重新武装 —— 没有新的数据就不会再触发第二次,
        // 连续滚到底时下一屏仍然能补下一页。
        _autoLoadArmed = true;
    }

    /// <summary>
    /// 右键菜单即将弹出: 按模式显隐「取消关注」。
    ///
    /// ★ 为什么必须在代码里做而不能在 XAML 上绑 Visibility(2026-10-13 用最小复现程序实测):
    ///   ContextMenu 不在逻辑树/可视化树上 —— 实测 <c>LogicalTreeHelper.GetParent(menu)</c> 与
    ///   <c>VisualTreeHelper.GetParent(menu)</c> **都是 null**, 于是
    ///   <c>RelativeSource AncestorType=UserControl</c> 这条绑定的状态是 <c>Unattached</c>
    ///   (而且 HasError=false, **不报错**, 静悄悄地不生效)。
    ///   表现就是 MenuItem.Visibility 一直停在默认的 Visible: 粉丝模式下也会看到
    ///   「取消关注」, 点下去被 RelationViewModel.UnfollowAsync 的 CanUnfollow 守卫挡回来。
    /// ★ 这里也不能靠 x:Name 直接拿那一项: DataTemplate 里的 ContextMenu 上的 x:Name
    ///   **不生成** code-behind 字段(编译报 CS0103)。所以用 Tag 标记 + 遍历 Items。
    /// </summary>
    private void OnCardMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm || Vm == null) return;
        var vis = Vm.CanUnfollow ? Visibility.Visible : Visibility.Collapsed;
        foreach (var it in cm.Items)
            if (it is MenuItem { Tag: "Unfollow" } mi)
                mi.Visibility = vis;
    }

    // ------------------------------------------------------------ 卡片点击 / 右键菜单

    /// <summary>
    /// 点卡片 = 打开该 UP 的主页(与动态页左栏同款, 走内嵌页)。
    ///
    /// ★ 2026-10-13 加: 键盘也能打开。卡片是 Focusable 的(为了 Tab 能走到),
    ///   那就必须给 Enter 一个交代 —— 否则键盘用户 Tab 到卡片上按回车毫无反应,
    ///   比 Focusable=False 更糟(后者至少不给人"能点"的错觉)。
    /// </summary>
    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (sender is not FrameworkElement { DataContext: FollowUser u }) return;
        Svc.Navigate?.OpenUserSpace(u.Mid.ToString());
    }

    /// <summary>卡片获得键盘焦点时按回车 = 打开主页</summary>
    private void OnCardKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Space) return;
        if (sender is not FrameworkElement { DataContext: FollowUser u }) return;
        e.Handled = true;
        Svc.Navigate?.OpenUserSpace(u.Mid.ToString());
    }

    private void OnVisitClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.DataContext is FollowUser u)
            Svc.Navigate?.OpenUserSpace(u.Mid.ToString());
    }

    /// <summary>取消关注(仅关注模式可见, 见 OnCardMenuOpened 的显隐)</summary>
    private async void OnUnfollowClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement fe || fe.DataContext is not FollowUser u) return;
        if (Vm == null) return;
        await Vm.UnfollowAsync(u);
    }

    // ------------------------------------------------------------ 左栏关注分组

    /// <summary>
    /// 点左栏一个分组 → 换分组并重新加载那一组。
    ///
    /// ★ 2026-10-13 加: ↑/↓ 在分组之间移动。分组是 RadioButton, 方向键本来也只能 Tab
    ///   的下一站, 但中间隔着 30 个标签, 纯键盘要按 30 次 Tab 才能换个分组。
    /// </summary>
    private void OnGroupClick(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        if (sender is FrameworkElement { DataContext: FollowGroup g })
            Vm.SelectedGroup = g;
    }

    /// <summary>左栏分组列表的方向键漫游(↑/↓ 换选中分组)</summary>
    private void OnGroupListKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm == null || e.Key != Key.Down && e.Key != Key.Up) return;
        if (Vm.Groups.Count == 0) return;

        var idx = Vm.SelectedGroup is { IsAll: true } ? -1 : Vm.Groups.IndexOf(Vm.SelectedGroup!);
        var next = e.Key == Key.Down ? idx + 1 : idx - 1;
        // ↑ 从「全部」往下走的是第一项; ↓ 从最后一项往上走是「全部」
        if (next < 0) next = Vm.Groups.Count - 1;
        if (next >= Vm.Groups.Count) next = -1;
        Vm.SelectedGroup = next < 0 ? Vm.AllGroup : Vm.Groups[next];

        // 焦点要跟着选中项走, 否则键盘焦点还留在老按钮上, 视觉上像是"没反应"
        if (sender is ItemsControl list && next >= 0
            && list.ItemContainerGenerator.ContainerFromIndex(next) is FrameworkElement fe)
            fe.Focus();

        e.Handled = true;
    }

    /// <summary>右键头像 → 预览大图</summary>
    private void OnViewAvatar(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is FollowUser u)
            MediaActions.ShowImage(u.Face, u.Name + " 的头像", Window.GetWindow(this));
    }

    /// <summary>右键头像 → 下载到本地</summary>
    private async void OnDownloadAvatar(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is FollowUser u)
            await MediaActions.SaveImageAsync(u.Face, u.Name + "_头像");
    }
}
