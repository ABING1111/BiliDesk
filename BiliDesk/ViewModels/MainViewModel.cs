using System;
using System.Windows;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;
using BiliDesk.Views;

namespace BiliDesk.ViewModels;

public enum PageKey
{
    Home = 0,
    Follow = 1,
    Search = 2,
    History = 3,
    Favorites = 4,
    Settings = 5,
    /// <summary>离线缓存(下载目录里的视频文件)</summary>
    Cache = 6,
    /// <summary>B 站消息(私信)</summary>
    Messages = 7,
    /// <summary>分区页(点左上角 logo 的分区面板里的一项切过来; 带参数, 不在导航栏上)</summary>
    Region = 8,
    /// <summary>我的页(2026-09-26 新增: 资料+快捷入口+收藏夹; 收藏/历史从侧栏收进这里)</summary>
    Mine = 9,
    /// <summary>稍后再看(2026-09-28 新增: 入口是「我的」页的快捷入口, 不在导航栏上)</summary>
    WatchLater = 10,
    /// <summary>我的订阅(2026-11-27 新增: 追番/追剧/订阅的合集; 入口是「我的」页)</summary>
    Subscription = 11
}

/// <summary>主窗口 ViewModel: 导航(登录态/用户信息的展示由各页面自己的 VM 负责)</summary>
public class MainViewModel : ObservableObject
{
    private PageKey _current = PageKey.Home;

    public PageKey Current
    {
        get => _current;
        set
        {
            // WPF RadioButton 在 GroupName 下总是按 TwoWay 绑定 IsChecked, 源属性必须有 public setter
            if (SetProperty(ref _current, value))
                PageChanged?.Invoke();
        }
    }

    public event Action? PageChanged;

    /// <summary>
    /// 子页面的「来源页」(2026-11-28 用户要求"侧键返回上一级")。
    ///
    /// ★ 键 = 子页面自己, 值 = **进入它时**停着的那一页。
    ///   侧键 / Esc / 页面上的返回按钮都用它来决定退回哪一页 —— 用户回到的是自己真的来过的
    ///   地方, 而不是各页写死的目标(那些目标只对"现在的入口"成立)。
    /// ★ 只记 <see cref="PageBackPolicy.IsChildPage"/> 认的那 6 个页面: 首页/动态/我的/设置
    ///   没有"上一级"概念, 不进这张表, 侧键落在它们上面仍然什么都不做。
    /// </summary>
    private readonly Dictionary<PageKey, PageKey> _origins = new();

    /// <summary>某个页面的来源页; 没记录过返回 null(由调用方回落到默认上级)。</summary>
    public PageKey? OriginOf(PageKey key)
        => _origins.TryGetValue(key, out var parent) ? parent : null;

    /// <summary>
    /// "返回上一级"的目标页。没有上一级的页面返回它自己, 调用方据此判断"没得退"。
    /// ★ 页面上那颗返回按钮与主窗口的侧键 / Esc 都走这一条, 保证三处目标永远一致。
    /// </summary>
    public PageKey ResolveBackTarget(PageKey key) => PageBackPolicy.Resolve(key, OriginOf(key));

    public ICommand NavigateCommand { get; }

    public MainViewModel()
    {
        NavigateCommand = new RelayCommand(
            p => { if (p is PageKey k) Navigate(k); });
    }

    public void Navigate(PageKey key)
    {
        if (Current == key)
        {
            // 点的是当前页也要通知一次: 主窗口靠这个信号关掉内嵌页(UP 主主页那种覆盖层)。
            // 页面内容没变时 ShowPage 会自己短路, 所以不会有闪烁。
            PageChanged?.Invoke();
            return;
        }

        // ★ 记来源(2026-11-28): 是"真切页"才记 —— 点当前页那条分支在上面已经 return,
        //   否则"点一下当前导航项"就会把来源冲成自己, 侧键立刻退不回去。
        if (PageBackPolicy.IsChildPage(key)) _origins[key] = Current;

        Current = key; // setter 内部已 fire PageChanged
    }

    /// <summary>
    /// 导航栏上的页面(离线缓存 / 消息 / 搜索不在其中)。
    ///
    /// **搜索页不在导航栏上**(2026-09-26 用户要求"弃用侧边栏搜索"): 它是搜索结果页,
    /// 唯一入口是首页右上角那个搜索卡(选词后由 MainWindow.NavigateToSearch 切过去)。
    ///
    /// **收藏/历史也不在导航栏上了**(2026-09-26 改版): 侧栏改为 首页/动态/我的/设置,
    /// 这两个入口收进「我的」页。
    /// </summary>
    public static readonly PageKey[] NavPages =
    {
        PageKey.Home, PageKey.Follow, PageKey.Mine, PageKey.Settings
    };

    /// <summary>打开登录窗口</summary>
    public void OpenLoginWindow()
    {
        var owner = Application.Current.MainWindow;
        var win = new LoginWindow { Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        win.ShowDialog();
    }
}
