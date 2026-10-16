using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using BiliDesk.Helpers;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

/// <summary>
/// 分区页。入口: 首页左上角 logo 的分区面板(MainWindow.NavigateToRegion)。
///
/// 卡片容器走 DeferredFill 分帧挂载(和首页同一套) —— 分区流一次 20 条, 但要能一路翻下去,
/// 不靠分帧的话每补一页都会在一次布局里同步生成 20 张卡(首页当年"点一下卡一下"的成因)。
///
/// ★★★ 2026-10-02 重构(用户报"分区第一次进入后还是卡一秒"): 与首页同款问题 —— 旧实现
///   `WireFill` 每次都 Dispose+重建 DeferredFill, `OnUnloaded` 还 `ItemsSource=null` 把整墙拆掉,
///   于是每次进分区/换分区/从别的页回来都要**重新生成几十上百张卡**。
///   新做法: DeferredFill **只建一次、永驻**, 换分区时靠 DeferredFill 自己监听到 Items 的
///   Reset(Clear)重建内容; 离开本页**不拆墙**(内存换流畅, 和首页同一取舍)。
///
/// ★★★ 2026-10-17 增补"滚到底自动补下一页"(用户要求重做分区功能)。
///   旧的分区接口不支持翻页, 本页因此只有"刷新"没有"加载更多"; 换源之后 display_id 是真游标,
///   于是接上和首页/搜索/稍后再看同一套自动加载。闸门见 <see cref="OnListScrollChanged"/>。
/// </summary>
public partial class RegionPage : UserControl
{
    private DeferredFill? _fill;
    private RegionViewModel? _hookedVm;

    public RegionPage() => InitializeComponent();

    /// <summary>切到某个分区。同一个分区且已加载过 → 不重拉。</summary>
    public async Task OpenAsync(string name, int tid)
    {
        if (DataContext is not RegionViewModel vm) return;
        HookVm(vm);

        var sameRegion = vm.RegionName == name && vm.Tid == tid && vm.HasItems;
        if (!sameRegion)
        {
            await vm.OpenAsync(name, tid);   // LoadAsync 结束时 PropertyChanged(Loading=false) 会触发挂载
        }
    }

    private void HookVm(RegionViewModel vm)
    {
        if (ReferenceEquals(_hookedVm, vm)) return;
        if (_hookedVm != null) _hookedVm.PropertyChanged -= OnVmPropertyChanged;
        _hookedVm = vm;
        _hookedVm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 数据到位(或失败清空)后(重新)挂载容器
        if (e.PropertyName == nameof(RegionViewModel.Loading) && sender is RegionViewModel vm && !vm.Loading)
            EnsureFill(vm);
    }

    /// <summary>
    /// 保证卡片墙挂上了分帧填充器。**只建一次**, 之后换分区靠 DeferredFill 监听 Items 的
    /// Reset 自己重建内容 —— 不再每次 Dispose+重建(那正是"进分区卡一下"的元凶)。
    /// </summary>
    private void EnsureFill(RegionViewModel vm)
    {
        if (_fill != null) return;
        _fill = new DeferredFill(CardList, vm.Items, 24);
        _fill.Start();
    }

    // ------------------------------------------------------------ 滚动到底自动补页

    /// <summary>离底部还剩这么多像素就算"到底了", 提前把下一页拉回来(与首页/搜索同值)</summary>
    private const double AutoLoadThreshold = 240;

    /// <summary>
    /// 连续自动补页的次数。与首页/搜索同款的一道闸: 内容不足一屏时"补了还是短"会立刻再来一次,
    /// 没有它就会一直往下拉(用户看到的是"页面自己在不停加载")。
    /// </summary>
    private int _autoFillCount;

    /// <summary>
    /// 列表滚到接近底部(或内容不足一屏)就自动翻下一页 —— 与首页/搜索页同一套写法。
    ///
    /// ★ 用 async void + await 是**故意的**: _autoFillCount 计数跨 await 必须还有效,
    ///   否则几次请求同时在飞时闸门等于没有(首页/搜索那边也是这么写的)。
    /// </summary>
    private async void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not RegionViewModel vm) return;
        if (!vm.HasMore || vm.Loading || vm.LoadingMore) return;
        if (_autoFillCount > 5) return;
        if (sender is not ScrollViewer sv) return;
        // 分帧填充进行中: 每补一批 ScrollableHeight 都在长, 这时判"内容不够一屏"是错的,
        // 会带着空列表去拉下一页; 等填充完(补帧引发的 ScrollChanged 会再来)再判。
        if (_fill is { IsActive: true }) return;
        // 首屏还没出内容: 这一拍的高度是"转圈"撑出来的, 不是内容高度
        if (vm.Items.Count == 0) return;

        var distanceToBottom = sv.ScrollableHeight - sv.VerticalOffset;
        var contentShort = sv.ScrollableHeight < sv.ActualHeight * 0.8;
        if (distanceToBottom > AutoLoadThreshold && !contentShort) return;

        _autoFillCount++;
        try { await vm.LoadMoreAsync(); }
        finally { _autoFillCount--; }
    }
}
