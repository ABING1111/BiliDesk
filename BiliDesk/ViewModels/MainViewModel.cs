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
    WatchLater = 10
}

/// <summary>主窗口 ViewModel: 导航 + 登录状态</summary>
public class MainViewModel : ObservableObject
{
    private PageKey _current = PageKey.Home;
    private bool _isLoggedIn;
    private string _userName = "";
    private string _userFace = "";

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

    public bool IsLoggedIn { get => _isLoggedIn; private set => SetProperty(ref _isLoggedIn, value); }
    public string UserName { get => _userName; private set => SetProperty(ref _userName, value); }
    public string UserFace { get => _userFace; private set => SetProperty(ref _userFace, value); }

    public event Action? PageChanged;
    public event Action? UserChanged;

    public ICommand NavigateCommand { get; }

    public MainViewModel()
    {
        NavigateCommand = new RelayCommand(
            p => { if (p is PageKey k) Navigate(k); });
        SessionManager.Instance.Changed += OnSessionChanged;
        _ = RefreshUserAsync();
    }

    private void OnSessionChanged()
    {
        IsLoggedIn = SessionManager.Instance.HasLogin;
        if (IsLoggedIn) _ = RefreshUserAsync();
        else
        {
            UserName = "";
            UserFace = "";
            UserChanged?.Invoke();
        }
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

    /// <summary>刷新登录用户信息(nav 接口)</summary>
    public async System.Threading.Tasks.Task RefreshUserAsync()
    {
        try
        {
            var has = SessionManager.Instance.HasLogin;
            IsLoggedIn = has;
            if (!has)
            {
                UserName = "";
                UserFace = "";
                UserChanged?.Invoke();
                return;
            }

            var (_, _, user) = await Svc.Api.GetUserStateAsync();
            if (user is { IsLogin: true })
            {
                UserName = user.Name;
                UserFace = user.Face;
                UserChanged?.Invoke();
            }
            else
            {
                // 会话可能已过期, 但不清除(网页端可能仍有效), 仅界面提示
                IsLoggedIn = false;
                UserName = "登录状态已过期";
                UserChanged?.Invoke();
            }
        }
        catch
        {
            // 用户信息刷新失败不影响使用
        }
    }

    /// <summary>打开登录窗口</summary>
    public void OpenLoginWindow()
    {
        var owner = Application.Current.MainWindow;
        var win = new LoginWindow { Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        win.ShowDialog();
    }
}