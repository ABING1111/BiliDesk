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
/// 历史记录页: 云端历史(封面 + 标题 + UP 主 + 时长), 版式与首页一致 —— 卡片墙 + 浮动工具列。
/// 本机历史和时间轴都已移除, 所以这里没有模式切换、也没有滚动同步之类的逻辑。
///
/// 2026-09-30 改版: 内容从"一条一行"的虚拟化 ListBox 换成与首页同构的卡片墙。
/// 点击行为也交给卡片自己(点封面/标题播放, 点 UP 主进主页), 页面不再自己处理行点击。
///
/// 2026-10-06 改版: 搜索功能整体移除, 刷新/编辑/返回「我的」挪到标题行右上角。
///
/// 2026-11-14 改版: 整页一个 ScrollViewer, 标题随内容滚走、「视频 / 直播间」胶囊吸顶
/// (见 <see cref="StickyPin"/>); 右上角那排操作(含编辑态)又搬回**右下角竖列**,
/// 与首页/分区页同一套 FloatingIconButtonStyle 观感。
/// </summary>
public partial class HistoryPage : UserControl
{
    private HistoryViewModel? Vm => DataContext as HistoryViewModel;

    /// <summary>首次显示时才拉云端数据(页面是懒加载挂进可视树的)</summary>
    private bool _loadedOnce;

    /// <summary>
    /// 类别胶囊的吸顶(2026-11-14): 与设置页顶部 Tab 同一套效果, 算法在 <see cref="StickyPin"/>。
    /// 构造时就要建好 —— 它从胶囊的 XAML Margin 里取水平边距与初始自然顶边。
    /// </summary>
    private readonly StickyPin _sticky;

    public HistoryPage()
    {
        InitializeComponent();
        _sticky = new StickyPin(PageScroll, TabSlot, StickyTabBar, StickyTabShift);
        Loaded += OnLoaded;
        // 页面常驻 PageLayer、只切 Visibility: 切回来时 Loaded 不会再发,
        // 而折叠期间量到的是过期版式 —— 变可见时补量一次。
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) _sticky.SyncDeferred(); };
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is not HistoryViewModel vm) return;
            // 历史列表也纳入全局屏蔽(命中关键词的记录会被隐藏), 见 FilterService
            FilterService.Instance.Attach(vm.Entries);
            // ★ 这里原来还订阅 vm.PropertyChanged 去刷搜索按钮的"已筛选中"外观,
            //   2026-10-06 搜索功能整体移除后已无必要, 连同 OnVmPropertyChanged 一起删除。
        };
    }

    /// <summary>右下角工具列的返回按钮: 历史/收藏/缓存的入口都在「我的」页, 统一回那里</summary>
    private void OnBackToMineClick(object sender, RoutedEventArgs e)
        => App.MainVm.Navigate(PageKey.Mine);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        _sticky.SyncDeferred();

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

    // ------------------------------------------------------------ 滚动: 吸顶 + 自动翻页

    /// <summary>自动加载的公共闸门: 缩放/布局变化时不立即翻页(见 <see cref="AutoLoadThrottle"/>)。</summary>
    private readonly AutoLoadThrottle _autoLoadThrottle = new();

    /// <summary>滚动: 先算吸顶位移, 再判"接近底部自动加载下一页"</summary>
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        // ★ 吸顶要在**任何**滚动事件上都算(包括布局变化那几拍), 否则缩放窗口时胶囊会脱钉。
        //   它只改 RenderTransform, 不参与下面的翻页闸门。
        _sticky.Update();
        // ★ 缩放/布局变化会连续发 ScrollChanged, 这一拍的高度不是稳定版式 —— 不能立即翻页。
        //   交给 AutoLoadThrottle 在布局稳定后补一次判断(短列表也能在稳定后补上下一页)。
        if (_autoLoadThrottle.ShouldSkipLayoutChange(e, () => _ = MaybeLoadMoreAsync(sv))) return;
        _ = MaybeLoadMoreAsync(sv);
    }

    private async Task MaybeLoadMoreAsync(ScrollViewer sv)
    {
        if (sv.ExtentHeight <= 0 || sv.ViewportHeight <= 0) return;
        if (sv.VerticalOffset < sv.ExtentHeight - sv.ViewportHeight - 240) return;
        if (!_autoLoadThrottle.TryEnter()) return;
        if (Vm is { HasMore: true, Loading: false, LoadingMore: false } vm)
            await vm.LoadAsync(reset: false);
    }

}
