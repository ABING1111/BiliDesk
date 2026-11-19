using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Services;
using BiliDesk.ViewModels;
using BiliDesk.Views.Controls;

namespace BiliDesk.Views.Pages;

/// <summary>
/// 「我的空间」页: 三个 Tab(动态 / 关注 / 粉丝), 看的是同一个 mid 的三份内容。
///
/// ★ 2026-11-18 新增(issue #90):「我的」页点动态/关注/粉丝三个数字进来,
///   UP 主主页点关注数/粉丝数也进来 —— 两处入口共用这一个页面, 行为一致。
///
/// ★ 选中态**只在这里写 IsChecked**(与播放页「简介/评论」那条经验一致):
///   分散着改必然出现"两个都亮"或"两个都不亮"。
/// </summary>
public partial class UserCenterPage : UserControl, IRefreshable
{
    /// <summary>滚到底自动加载下一页的阈值(像素)。</summary>
    private const double AutoLoadThreshold = 600;

    /// <summary>
    /// 内容不足一屏时的主动补页次数上限。
    /// ★ 内容装得下一屏时列表根本没有滚动条, 滚动事件不会触发 ⇒ "到底了"没人更新,
    ///   列表会永远停在这一页(用户感受就是"滑不动了")。所以要主动补。
    ///   上限是防"接口一直回空页"时无限自补(与 FollowPage 的同一道闸)。
    /// </summary>
    private const int MaxShortContentFills = 3;

    /// <summary>连续"内容还不够一屏"的补页计数(用户一滚动就清零)。</summary>
    private int _shortContentFills;

    /// <summary>
    /// 是否正在同步 Tab 选中态。
    ///
    /// ★ 为什么需要这个闸: RadioButton 的 Checked 事件会调 SwitchTabAsync,
    ///   而 SwitchTabAsync 又会回写 IsChecked —— 不加这道闸就会**来回弹**。
    ///   (与播放页「简介/评论」那套同样要小心。)
    /// </summary>
    private bool _syncingTab;

    private UserCenterViewModel? Vm => DataContext as UserCenterViewModel;

    public UserCenterPage()
    {
        InitializeComponent();

        // ★ 列表纳入全局屏蔽(与首页/动态/收藏/历史同一套条件)。
        //   FollowUser 不在屏蔽谓词的认识范围内, 会被原样放行 —— 符合语义。
        FilterService.Instance.Changed += OnFilterApplied;

        // ★★★ 必须退订: 这个页面是**每次打开时新建**的(不是 MainWindow 缓存的那种),
        //   而 FilterService 是单例、持有强引用。不退订的话每打开一次「我的空间」
        //   就多挂一个死页面的处理器 —— 打开几十次就把这些页面全钉在内存里。
        //   (FollowPage 那种常驻页不用操心这个, 它是唯一实例。)
        Unloaded += (_, _) => FilterService.Instance.Changed -= OnFilterApplied;

        // VM 的 Tab 变了要同步 RadioButton 的选中态 ——
        // 否则"页面初始停在关注"而按钮亮的是"动态", 两边对不上。
        DataContextChanged += (_, e) => AttachVm(e.NewValue as UserCenterViewModel);

        // ★ 页面第一次上屏时也要同步一次: 构造参数那路已经同步过了,
        //   但 XAML 里直接 new + 赋 DataContext 的用法(探针/测试)走的是这条。
        Loaded += (_, _) => AttachVm(Vm);
    }

    /// <summary>订阅/退订 VM 的 Tab 变化事件。</summary>
    private void AttachVm(UserCenterViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm != null) _vm.TabChanged -= OnVmTabChanged;
        _vm = vm;
        if (vm == null) return;
        vm.TabChanged += OnVmTabChanged;
        // 立刻对齐一次(页面可能是复用的, VM 的 Tab 已经变了)
        SetCheckedTab(vm.Tab);
    }

    private UserCenterViewModel? _vm;

    /// <summary>带参数构造: 指定看谁的空间、初始停在哪个 Tab。</summary>
    public UserCenterPage(long mid, string name = "", string face = "", bool isSelf = false,
                          UserCenterTab tab = UserCenterTab.Dynamics)
    {
        InitializeComponent();
        FilterService.Instance.Changed += OnFilterApplied;
        // ★ 同上: 每次打开都新建页面, 必须退订单例上的强引用(见无参构造里的说明)。
        Unloaded += (_, _) => FilterService.Instance.Changed -= OnFilterApplied;

        var vm = new UserCenterViewModel();
        DataContext = vm;

        // ★ VM 的 Tab 变了要同步 RadioButton 的选中态 ——
        //   否则"页面初始停在关注"而按钮亮的是"动态", 两边对不上。
        vm.TabChanged += OnVmTabChanged;

        // ★ 先把 Tab 摆好再 InitAsync: 否则页面一上屏显示的就是"动态"Tab,
        //   而用户点的其实是"关注" —— 要等第一次请求回来才跳过去, 看着像没响应。
        vm.SetInitialTab(tab);

        _ = vm.InitAsync(mid, name, face, isSelf);

        // 初始选中的那个 Tab 要亮起来(其余两个由 GroupName 互斥自动熄灭)。
        SetCheckedTab(tab);
    }

    // ------------------------------------------------------------ Tab 切换

    /// <summary>
    /// 设置当前选中的 Tab(只改 IsChecked, 由 GroupName 互斥带掉另外两个)。
    /// </summary>
    private void SetCheckedTab(UserCenterTab tab)
    {
        _syncingTab = true;
        try
        {
            TabDynamics.IsChecked = tab == UserCenterTab.Dynamics;
            TabFollowings.IsChecked = tab == UserCenterTab.Followings;
            TabFollowers.IsChecked = tab == UserCenterTab.Followers;
        }
        finally
        {
            _syncingTab = false;
        }
    }

    /// <summary>VM 的 Tab 变了 → 同步按钮的选中态。</summary>
    private void OnVmTabChanged(UserCenterTab tab) => SetCheckedTab(tab);

    private void OnTabDynamicsChecked(object sender, RoutedEventArgs e)
    {
        // ★ 同步中不响应: SetCheckedTab 改 IsChecked 会再触发一次 Checked,
        //   不挡住就会"页面回写 → 事件再回写"地弹。
        if (_syncingTab) return;
        _ = SwitchTabAsync(UserCenterTab.Dynamics);
    }

    private void OnTabFollowingsChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingTab) return;
        _ = SwitchTabAsync(UserCenterTab.Followings);
    }

    private void OnTabFollowersChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingTab) return;
        _ = SwitchTabAsync(UserCenterTab.Followers);
    }

    private async Task SwitchTabAsync(UserCenterTab tab)
    {
        if (Vm == null) return;
        // ★ 不需要再判"是不是当前 Tab": RadioButton 点自己时 Checked **不会触发**,
        //   而程序化改选中态的那条路已被 _syncingTab 挡住。走到这里就说明真的要切。
        await Vm.SwitchTabAsync(tab);
        _shortContentFills = 0;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(TryAutoFillShortContent));
    }

    // ------------------------------------------------------------ 动态详情

    private void OnDynamicCardDetailRequested(object sender, DynamicDetailRequestedEventArgs e)
    {
        if (e.Item == null) return;
        SvcWindow.Main?.ShowDynamicDetail(e.Item, e.FocusComments, e.Source);
    }

    // ------------------------------------------------------------ 滚动加载

    private void OnDynamicsScrollChanged(object sender, ScrollChangedEventArgs e)
        => HandleScrollChanged(e, Vm?.IsDynamicsTab == true, () => Vm?.LoadMoreAsync());

    private void OnFollowingsScrollChanged(object sender, ScrollChangedEventArgs e)
        => HandleScrollChanged(e, Vm?.IsFollowingsTab == true, () => Vm?.LoadMoreAsync());

    private void OnFollowersScrollChanged(object sender, ScrollChangedEventArgs e)
        => HandleScrollChanged(e, Vm?.IsFollowersTab == true, () => Vm?.LoadMoreAsync());

    /// <summary>
    /// 三个列表共用的"滚到底加载下一页"。
    ///
    /// ★ 内容明显不足一屏时**主动**补一次: 没有滚动条 ⇒ 永远等不到 ScrollChanged。
    /// ★ 用户自己滚动过就清零补页额度 —— 不然会把手滑出来的翻页机会白吃掉。
    /// </summary>
    private void HandleScrollChanged(ScrollChangedEventArgs e, bool isActiveTab, Func<Task?> loadMore)
    {
        if (!IsLoaded || !isActiveTab) return;

        // 用户滚动过 ⇒ 补页额度重新开始
        if (Math.Abs(e.VerticalChange) > 0.5) _shortContentFills = 0;

        // ★★★ 缩放/分帧填充会连续发 ScrollChanged, 其中 Extent/Viewport 是变的 ——
        //   这一拍的高度不是用户看着的稳定版式, 立即判"离底很近"必然误触发。
        if (CardWall.IsResizing) return;
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0) return;

        var sv = ScrollViewerOf(e);
        if (sv == null) return;

        // 内容还不够一屏: 没有滚动条, 也就不会再有 ScrollChanged —— 主动补
        if (sv.ScrollableHeight <= 0)
        {
            TryAutoFillShortContent();
            return;
        }

        var distanceToBottom = sv.ScrollableHeight - sv.VerticalOffset;
        if (distanceToBottom > AutoLoadThreshold) return;

        _ = loadMore();
    }

    /// <summary>取滚动事件里的 ScrollViewer(ListBox 的 ScrollChanged 是附加事件)。</summary>
    private static ScrollViewer? ScrollViewerOf(ScrollChangedEventArgs e)
        => e.OriginalSource as ScrollViewer;

    /// <summary>
    /// 内容明显不足一屏时的主动补页。
    ///
    /// ★ 只在"用户没有滚动"的时机调用(页面加载完 / 切 Tab 后 / 刷新后):
    ///   这些时候如果没有滚动条, ScrollChanged 不会再来, 不主动补就会永远停在一页上。
    /// </summary>
    private void TryAutoFillShortContent()
    {
        if (!IsLoaded) return;
        if (CardWall.IsResizing) return;
        if (Vm == null) return;
        if (_shortContentFills >= MaxShortContentFills) return;

        var sv = GetVisibleScroll();
        if (sv == null) return;

        // 内容明显够长(超过 0.8 个视口)就不补
        if (sv.ScrollableHeight >= Math.Max(1, sv.ViewportHeight) * 0.8) return;

        _shortContentFills++;
        _ = Vm.LoadMoreAsync();
    }

    /// <summary>取当前可见那个列表的 ScrollViewer。</summary>
    private ScrollViewer? GetVisibleScroll()
    {
        var vm = Vm;
        if (vm == null) return null;
        var list = vm.IsDynamicsTab ? (DependencyObject?)DynamicsList
                 : vm.IsFollowingsTab ? FollowingsList
                 : FollowersList;
        return list == null ? null : FindChild<ScrollViewer>(list);
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            var nested = FindChild<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }

    /// <summary>屏蔽条件落地后, 等布局量完再判断要不要补一页。</summary>
    private void OnFilterApplied()
    {
        if (!IsLoaded) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(async () =>
        {
            if (!IsLoaded || Vm == null) return;
            var sv = GetVisibleScroll();
            if (sv == null) return;
            var distanceToBottom = sv.ScrollableHeight - sv.VerticalOffset;
            var contentShort = sv.ScrollableHeight <= 0;
            if (distanceToBottom > AutoLoadThreshold && !contentShort) return;
            await Vm.LoadMoreAsync();
        }));
    }

    // ------------------------------------------------------------ 刷新

    /// <summary>F5: 刷新当前 Tab。</summary>
    public void Refresh()
    {
        if (Vm == null) return;
        _ = Vm.RefreshAsync();
        _shortContentFills = 0;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(TryAutoFillShortContent));
    }
}
