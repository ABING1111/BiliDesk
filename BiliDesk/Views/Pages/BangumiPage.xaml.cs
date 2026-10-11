using System.Windows;
using System.Windows.Controls;
using BiliDesk.Models;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

/// <summary>
/// 番剧详情 + 选集页。由追番页点海报进来, 在主窗口内容区里内嵌(见 NavigationDispatcher.OpenBangumi)。
///
/// 页面只管"把选集显示出来"和"用户点了哪一集", 取数据与状态全在 <see cref="BangumiViewModel"/>。
/// </summary>
public partial class BangumiPage : UserControl
{
    public BangumiPage(BangumiItem item)
    {
        InitializeComponent();
        Vm = new BangumiViewModel(item);
        DataContext = Vm;
        // 首次进入就拉选集。放在构造函数末尾而不是 Loaded: 本页每次都是新建的
        // (内嵌层关掉就销毁), 不存在"第二次进入要避免重复请求"的问题。
        _ = Vm.InitAsync();
    }

    private BangumiViewModel? Vm { get; }

    private void OnPlayLatestClick(object sender, RoutedEventArgs e) => Vm?.PlayLatest();

    private async void OnRetryClick(object sender, RoutedEventArgs e)
    {
        // 失败时选集是空的, InitAsync 里的"已加载就跳过"不会命中, 这里直接重拉即可。
        await Vm?.InitAsync()!;
    }
}