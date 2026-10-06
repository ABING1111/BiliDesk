using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

/// <summary>
/// 历史记录页: 云端历史(封面 + 标题 + UP 主 + 时长), 版式与首页一致 —— 卡片墙 + 右上角操作组。
/// 本机历史和时间轴都已移除, 所以这里没有模式切换、也没有滚动同步之类的逻辑。
///
/// 2026-09-30 改版: 内容从"一条一行"的虚拟化 ListBox 换成与首页同构的卡片墙。
/// 点击行为也交给卡片自己(点封面/标题播放, 点 UP 主进主页), 页面不再自己处理行点击。
///
/// 2026-10-06 改版: 撤掉右下角浮动工具列, 搜索/刷新/编辑/返回「我的」挪到标题行右上角,
/// 与收藏页、稍后再看页同一套写法。搜索面板改从按钮**下方**弹出(原先从工具列往左弹)。
/// 「回到顶部」按钮随工具列一起去掉 —— 右上角那排是操作而不是导航, 不塞它。
/// </summary>
public partial class HistoryPage : UserControl
{
    private HistoryViewModel? Vm => DataContext as HistoryViewModel;

    /// <summary>首次显示时才拉云端数据(页面是懒加载挂进可视树的)</summary>
    private bool _loadedOnce;

    public HistoryPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is not HistoryViewModel vm) return;
            // 历史列表也纳入全局屏蔽(命中关键词的记录会被隐藏), 见 FilterService
            FilterService.Instance.Attach(vm.Entries);
            // ★ 这里原来还订阅 vm.PropertyChanged 去刷搜索按钮的"已筛选中"外观,
            //   2026-10-06 搜索功能整体移除后已无必要, 连同 OnVmPropertyChanged 一起删除。
        };
    }

    /// <summary>右上角操作组里的返回按钮: 历史/收藏/缓存的入口都在「我的」页, 统一回那里</summary>
    private void OnBackToMineClick(object sender, RoutedEventArgs e)
        => App.MainVm.Navigate(PageKey.Mine);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;

        if (!_loadedOnce)
        {
            _loadedOnce = true;
            await Vm.LoadAsync(reset: true);
            return;
        }

        // 再次进来: 上次没拿到内容(未登录 / 失败)就补拉一次。
        // 判断条件写成"已经拿到内容才跳过", 这样登录之后回到这页不会只剩一条旧错误。
        if (Vm.Entries.Count == 0) await Vm.LoadAsync(reset: true);
    }

    // ------------------------------------------------------------ 滚动: 自动翻页

    /// <summary>滚动到接近底部就自动加载下一页</summary>
    private async void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeight <= 0 || e.ViewportHeight <= 0) return;
        if (e.VerticalOffset < e.ExtentHeight - e.ViewportHeight - 240) return;
        if (Vm is { HasMore: true, Loading: false, LoadingMore: false } vm)
            await vm.LoadAsync(reset: false);
    }

}
