using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

/// <summary>
/// 历史记录页: 云端历史(封面 + 标题 + UP 主 + 时长), 版式与首页一致 —— 卡片墙 + 右下角浮动工具列。
/// 本机历史和时间轴都已移除, 所以这里没有模式切换、也没有滚动同步之类的逻辑。
///
/// 2026-09-30 改版: 内容从"一条一行"的虚拟化 ListBox 换成与首页同构的卡片墙。
/// 点击行为也交给卡片自己(点封面/标题播放, 点 UP 主进主页), 页面不再自己处理行点击。
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
            // 过滤条件一变就让搜索按钮更新"已筛选中"的外观。
            // 只在这里订阅、不解绑: 本页实例被 MainWindow 长期缓存, 生命周期等同于应用
            // (与 HomePage 订阅 ThemeChanged / FilterService 是同一套理由), 不存在泄漏。
            vm.PropertyChanged += OnVmPropertyChanged;
            // 立刻同步一次: 页面与 VM 都是常驻缓存的, 上次留下的关键词可能还在
            // (切走再回来若不刷新, 按钮会是常态色, 但列表其实还筛着)。
            RefreshSearchButton();
        };
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HistoryViewModel.SearchKeyword)) RefreshSearchButton();
    }

    /// <summary>右下角浮动工具列里的返回按钮: 历史/收藏/缓存的入口都在「我的」页, 统一回那里</summary>
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

    // ------------------------------------------------------------ 滚动: 自动翻页 + 回到顶部

    /// <summary>下滑超过这个距离才把「回到顶部」亮出来(与首页同值)</summary>
    private const double BackToTopThreshold = 400;

    private bool _backToTopShown;

    /// <summary>滚动到接近底部就自动加载下一页; 顺带维护「回到顶部」的显隐</summary>
    private async void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateBackToTop(sender as ScrollViewer);

        if (e.ExtentHeight <= 0 || e.ViewportHeight <= 0) return;
        if (e.VerticalOffset < e.ExtentHeight - e.ViewportHeight - 240) return;
        if (Vm is { HasMore: true, Loading: false, LoadingMore: false } vm)
            await vm.LoadAsync(reset: false);
    }

    /// <summary>
    /// 按当前滚动位置点亮/熄灭「回到顶部」。
    /// 只切 Opacity 与 IsHitTestVisible, **不用 Visibility** —— 它在工具列最上面,
    /// 用 Collapsed 的话下面几个按钮每过一次阈值就会上下跳一下。
    /// </summary>
    private void UpdateBackToTop(ScrollViewer? sv)
    {
        var show = sv is { VerticalOffset: > BackToTopThreshold };
        if (show == _backToTopShown) return;
        _backToTopShown = show;

        BtnTop.IsHitTestVisible = show;
        BtnTop.BeginAnimation(OpacityProperty,
            new DoubleAnimation(BtnTop.Opacity, show ? 1 : 0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void OnBackToTopClick(object sender, RoutedEventArgs e) => ListScroll?.ScrollToTop();

    // ------------------------------------------------------------ 搜索(入口 = 右下角工具列里的放大镜按钮)

    /// <summary>点搜索按钮: 开合面板; 打开时把光标放进输入框</summary>
    private void OnSearchClick(object sender, RoutedEventArgs e)
    {
        SearchPopup.IsOpen = !SearchPopup.IsOpen;
        if (!SearchPopup.IsOpen) return;

        // 弹出层的内容要等一帧才真正建好, 当场 Focus() 会落空(与"失焦判断要放到
        // BeginInvoke(Input) 之后"是同一类时序问题)。SelectAll: 再次打开时直接改关键词。
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            HistorySearchBox.Focus();
            HistorySearchBox.SelectAll();
        }));
    }

    /// <summary>面板上的「完成」: 只收面板, **不清关键词**(还得继续看筛选后的列表)</summary>
    private void OnCloseSearchClick(object sender, RoutedEventArgs e) => SearchPopup.IsOpen = false;

    /// <summary>有过滤条件时搜索按钮变强调色 —— 否则收起面板之后就看不出来列表是被筛过的</summary>
    private void RefreshSearchButton()
    {
        if (BtnSearch == null) return;
        var keyword = Vm?.SearchKeyword;
        var on = !string.IsNullOrWhiteSpace(keyword);
        BtnSearch.Foreground = on
            ? (Brush)FindResource("AccentTextBrush")
            : (Brush)FindResource("TextPrimaryBrush");
        BtnSearch.ToolTip = on ? $"搜索历史记录(筛选中): {keyword}" : "搜索历史记录";
    }

    /// <summary>
    /// Esc: 清空并**收起面板**(和浏览器里按 Esc 关搜索框一个习惯)。
    /// Enter 只吃掉 —— 过滤是随输入实时生效的, 回车没有额外动作;
    /// 不吃掉的话某些容器会把它当成"默认按钮"去响应。
    /// </summary>
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Vm?.ClearSearch();
            SearchPopup.IsOpen = false;
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
        }
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e)
    {
        Vm?.ClearSearch();
        HistorySearchBox.Focus();   // 清空后把光标留在框里, 方便接着输
    }
}
