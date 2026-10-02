using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using BiliDesk.Helpers;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

/// <summary>
/// 分区页。入口: 首页左上角 logo 的分区面板(MainWindow.NavigateToRegion)。
///
/// 卡片容器走 DeferredFill 分帧挂载(和首页同一套) —— 分区榜一次给 60~95 条,
/// ItemsSource 一挂就是 90 张 VideoCard 在一次布局里同步生成, 正是首页当年"点一下卡一下"的成因。
///
/// ★★★ 2026-10-02 重构(用户报"分区第一次进入后还是卡一秒"): 与首页同款问题 —— 旧实现
///   `WireFill` 每次都 Dispose+重建 DeferredFill, `OnUnloaded` 还 `ItemsSource=null` 把整墙拆掉,
///   于是每次进分区/换分区/从别的页回来都要**重新生成几十上百张卡**。
///   新做法: DeferredFill **只建一次、永驻**, 换分区时靠 DeferredFill 自己监听到 Items 的
///   Reset(Clear)重建内容; 离开本页**不拆墙**(内存换流畅, 和首页同一取舍)。
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
}
