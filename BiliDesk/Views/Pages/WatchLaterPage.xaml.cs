using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

/// <summary>
/// 「稍后再看」页(2026-09-28 新增): 卡片墙 + 编辑模式(批量移出/清空)。
/// 页面实例被 MainWindow 缓存, 每次切进来 Loaded 都重拉一次 —— 清单会被别的端改,
/// 而且本页只有一次请求, 重拉没有成本压力。
/// </summary>
public partial class WatchLaterPage : UserControl
{
    private WatchLaterViewModel? Vm => DataContext as WatchLaterViewModel;

    public WatchLaterPage()
    {
        InitializeComponent();
        // 屏蔽筛掉一批后补下一页(否则停在半屏空白上)
        FilterService.Instance.Changed += OnFilterApplied;
        // 页面常驻 PageLayer: 切回来时不一定再发 ScrollChanged, 按当前偏移补一次按钮状态
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) BtnTopRefresh.UpdateForOffset(ListScroll);
        };
        Loaded += async (_, _) =>
        {
            if (Vm != null)
            {
                // 稍后再看也纳入全局屏蔽(以前这里怎么屏蔽都不生效)
                FilterService.Instance.Attach(Vm.Items, isActive: () => IsVisible);
                await Vm.LoadAsync();
            }
        };
    }

    /// <summary>
    /// 连续自动补页的闸门(见 <see cref="AutoFillGuard"/>)。
    ///
    /// ★★★ 2026-11-28: 原来这里是 `_autoFillCount++ / finally { _autoFillCount--; }` ——
    ///   await 一结束就减回去, 计数器只等于"在飞请求数", **永远到不了上限 5**,
    ///   连续补页没有刹车。改成只增不减, 由用户滚动 / 刷新来重置。
    ///
    /// ★ 补页进行中必须暂停判定: 每补一批内容都在长, 这时"内容不够一屏"是假象 ——
    ///   不挡住的话几次 ScrollChanged 就把剩下所有页一口气全铺出来, 分页等于白做
    ///   (与首页分帧填充时踩的是同一个坑)。这道判断在 <see cref="MaybeAutoLoad"/> 里。
    /// </summary>
    private readonly AutoFillGuard _autoFillGuard = new();

    /// <summary>自动加载的公共闸门: 缩放/布局变化时不立即翻页(见 <see cref="AutoLoadThrottle"/>)。</summary>
    private readonly AutoLoadThrottle _autoLoadThrottle = new();

    private void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        // 工具列顶部 = 刷新, 下滑 = 回到顶部
        BtnTopRefresh.UpdateForOffset(sv);
        // ★ 先把布局变化那一类挡掉: 卡片墙补锚点的程序化滚动也带 VerticalChange,
        //   不能拿它给连续补页"充值"(见 AutoFillGuard)。
        if (_autoLoadThrottle.ShouldSkipLayoutChange(e, () => MaybeAutoLoad(sv))) return;
        if (Math.Abs(e.VerticalChange) > 0.5) _autoFillGuard.Reset();
        MaybeAutoLoad(sv);
    }

    /// <summary>内容接近底部 / 不足一屏就补下一页。滚动与"屏蔽后补内容"共用这一份判断。</summary>
    private void MaybeAutoLoad(ScrollViewer? sv)
    {
        if (sv == null) return;
        if (Vm is not { HasMore: true } vm) return;
        if (vm.IsFilling) return;
        // ★ 连续补页上限(见 AutoFillGuard): 补了还是不满一屏时不能无限自补。
        if (!_autoFillGuard.CanFill) return;

        var nearBottom = sv.ScrollableHeight - sv.VerticalOffset < 400;
        var contentShort = sv.ScrollableHeight < sv.ActualHeight * 0.8;
        if (!nearBottom && !contentShort) return;

        if (!_autoLoadThrottle.TryEnter()) return;

        // ★ 只累加、不在"加载完"时回退 —— 回退就等于没有上限。
        _autoFillGuard.OnFilled();
        vm.ShowNextPage();
    }

    /// <summary>屏蔽条件落地后, 等布局量完再补内容</summary>
    private void OnFilterApplied()
    {
        if (!IsLoaded || !IsVisible) return;
        // ★ 屏蔽刚筛掉一批内容: 这是"内容变短"的新前提, 连续补页额度重开一次。
        _autoFillGuard.Reset();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (IsLoaded && IsVisible) MaybeAutoLoad(ListScroll);
        }));
    }

    /// <summary>工具列二合一按钮: 顶部 = 刷新稍后再看, 下滑 = 回到顶部。</summary>
    private void OnTopRefreshClick(object sender, RoutedEventArgs e)
    {
        if (BtnTopRefresh.ShowsBackToTop)
        {
            ListScroll.ScrollToTop();
            return;
        }

        // ★ 刷新 = 整批换内容: 连续补页额度重开。
        _autoFillGuard.Reset();
        if (Vm?.ReloadCommand.CanExecute(null) == true) Vm.ReloadCommand.Execute(null);
    }

    /// <summary>
    /// 返回"上一级"(入口在「我的」页): 目标与鼠标侧键 / Esc 一致 —— 有来源页回来源页,
    /// 没有才回「我的」, 见 MainViewModel.ResolveBackTarget。
    /// </summary>
    private void OnBackToMineClick(object sender, RoutedEventArgs e)
        => App.MainVm.Navigate(App.MainVm.ResolveBackTarget(PageKey.WatchLater));
}
