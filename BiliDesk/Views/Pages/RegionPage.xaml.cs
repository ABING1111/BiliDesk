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
/// ★ 离开本页时把 ItemsSource 摘掉(容器与封面位图全是等 GC 的垃圾), 回来再重新挂 ——
///   与首页"非当前 tab 摘 ItemsSource"同一套内存治理。
/// </summary>
public partial class RegionPage : UserControl
{
    private DeferredFill? _fill;
    private RegionViewModel? _hookedVm;

    public RegionPage() => InitializeComponent();

    /// <summary>切到某个分区。同一个分区且已加载过 → 不重拉, 只把容器重新挂上。</summary>
    public async Task OpenAsync(string name, int tid)
    {
        if (DataContext is not RegionViewModel vm) return;
        HookVm(vm);

        var sameRegion = vm.RegionName == name && vm.Tid == tid && vm.HasItems;
        if (!sameRegion)
        {
            await vm.OpenAsync(name, tid);   // LoadAsync 结束时 PropertyChanged(Loading=false) 会触发重新挂载
        }
        else
        {
            WireFill(vm);
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
        // 数据到位(或失败清空)后重新挂载容器
        if (e.PropertyName == nameof(RegionViewModel.Loading) && sender is RegionViewModel vm && !vm.Loading)
            WireFill(vm);
    }

    private void WireFill(RegionViewModel vm)
    {
        _fill?.Dispose();
        _fill = new DeferredFill(CardList, vm.Items, 24);
        _fill.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 摘掉容器: 这批卡片与封面位图在用户去别的页面后全是垃圾
        _fill?.Dispose();
        _fill = null;
        CardList.ItemsSource = null;
    }
}
