using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.ViewModels;

/// <summary>
/// 「我的」页 ViewModel(2026-09-26 新增)。
///
/// 收拢个人资料(头像/昵称/等级/硬币/经验)、三项统计(动态/关注/粉丝)、
/// 快捷入口(离线缓存/观看记录/收藏/稍后再看)和收藏夹卡片。
/// 我的订阅**暂不做**(用户拍板), 参考图里那个位置先空着不加;
/// 稍后再看已于 2026-09-28 补上(见 WatchLaterPage / WatchLaterViewModel)。
///
/// 数据源:
///   资料   = nav(已登录才有硬币/经验)
///   关注/粉丝 = relation/stat(匿名可用, 实测与登录态无关)
///   动态数  = dynamic_svr/num(匿名被风控, 失败显示 "--", 见 ApiClient 注释)
///   收藏夹  = created/list(带封面/隐私的那个接口)
/// </summary>
public class MineViewModel : ObservableObject
{
    private bool _loading;
    private bool _isLoggedIn;
    private string _error = "";
    private string _name = "";
    private string _face = "";
    private int _level;
    private string _coinsText = "--";
    private string _expText = "";
    private double _expProgress;
    private string _dynamicText = "--";
    private string _followingText = "--";
    private string _followerText = "--";
    private bool _folderLoading;
    private string _folderError = "";

    /// <summary>
    /// 收藏夹是否已经拉过一次。
    ///
    /// ★ 为什么要有它: 页面实例被 MainWindow 缓存, 每次切回本页都会重新触发 `Loaded` → `LoadAsync`。
    /// 资料/统计那几个数字重拉无妨(便宜、还会变), 但**收藏夹卡片要重新拉 `created/list` 并重建
    /// 整面卡墙 + 重新解码封面** —— 用户看到的就是"每次进『我的』页收藏夹都闪一下重新加载"。
    /// 所以收藏夹只在第一次进入时拉, 之后交给右上角那个刷新按钮。
    /// </summary>
    private bool _foldersLoaded;

    public ObservableCollection<FavFolder> Folders { get; } = new();

    public bool Loading { get => _loading; private set => SetProperty(ref _loading, value); }
    public bool IsLoggedIn { get => _isLoggedIn; private set => SetProperty(ref _isLoggedIn, value); }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }
    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public string Face { get => _face; private set => SetProperty(ref _face, value); }
    public int Level { get => _level; private set => SetProperty(ref _level, value); }

    public string LevelText => Level > 0 ? "LV" + Level : "";

    public string CoinsText { get => _coinsText; private set => SetProperty(ref _coinsText, value); }

    /// <summary>"经验 25181/28800" 那一行的文本; 没拿到经验时为空串(整行隐藏)</summary>
    public string ExpText { get => _expText; private set => SetProperty(ref _expText, value); }

    /// <summary>经验进度条 0~1(Lv6 时 next_exp=0, 直接拉满)</summary>
    public double ExpProgress
    {
        get => _expProgress;
        private set
        {
            if (SetProperty(ref _expProgress, value))
                OnPropertyChanged(nameof(ExpBarWidth));
        }
    }

    /// <summary>
    /// 经验进度条的实心段宽度(px)。直接给换算好的值而不是绑 0~1:
    /// XAML 里没有现成的乘法转换器, 与其在转换器上多一个文件, 不如让 VM 算完。
    /// </summary>
    public double ExpBarWidth => 120 * _expProgress;

    public string DynamicText { get => _dynamicText; private set => SetProperty(ref _dynamicText, value); }
    public string FollowingText { get => _followingText; private set => SetProperty(ref _followingText, value); }
    public string FollowerText { get => _followerText; private set => SetProperty(ref _followerText, value); }

    public bool FolderLoading { get => _folderLoading; private set => SetProperty(ref _folderLoading, value); }
    public string FolderError { get => _folderError; private set => SetProperty(ref _folderError, value); }

    /// <summary>「我的收藏 N」里的 N = 收藏夹个数(和参考图一致)</summary>
    public string FavCountText => Folders.Count > 0 ? Folders.Count.ToString() : "";

    public ICommand OpenCacheCommand { get; }
    public ICommand OpenHistoryCommand { get; }
    public ICommand OpenFavoritesCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand LoginCommand { get; }

    public MineViewModel()
    {
        // 快捷入口: 直接走主导航(页面实例都在 MainWindow._pages 里缓存着)
        OpenCacheCommand = new RelayCommand(() => App.MainVm.Navigate(PageKey.Cache));
        OpenHistoryCommand = new RelayCommand(() => App.MainVm.Navigate(PageKey.History));
        OpenFavoritesCommand = new RelayCommand(() => App.MainVm.Navigate(PageKey.Favorites));
        // 收藏夹卡片 → 收藏页并选中那个夹(经 MainWindow 落地)
        OpenFolderCommand = new RelayCommand(p =>
        {
            if (p is FavFolder f) SvcWindow.Main?.OpenFavFolder(f);
        });
        RefreshCommand = new RelayCommand(() => _ = LoadAsync());
        LoginCommand = new RelayCommand(() => App.MainVm.OpenLoginWindow());
        // 登录态变化(扫码成功/退出)时原地刷新 —— 否则用户在「我的」页点「去登录」,
        // 登录窗口关了页面还停在未登录态, 得切出去再切回来才恢复
        Svc.Session.Changed += OnSessionChanged;
    }

    private void OnSessionChanged() => _ = LoadAsync();

    /// <summary>
    /// 拉全部数据。每个子项独立容错: 某一项挂了不影响其它项显示
    /// (动态数那项本来就是"能拿则拿"的语义)。
    /// </summary>
    public async Task LoadAsync()
    {
        if (Loading) return;

        if (!Svc.Session.HasLogin)
        {
            // 未登录: 清空旧资料, 界面退到"去登录"态
            IsLoggedIn = false;
            Name = "";
            Face = "";
            Level = 0;
            CoinsText = "--";
            ExpText = "";
            DynamicText = FollowingText = FollowerText = "--";
            Folders.Clear();
            FolderError = "";
            _foldersLoaded = false;   // 退出登录: 收藏夹属于上一个账号, 下次登录要重新拉
            OnPropertyChanged(nameof(FavCountText));
            return;
        }

        Loading = true;
        Error = "";
        IsLoggedIn = true;
        try
        {
            var (stateOk, stateErr, user) = await Svc.Api.GetUserStateAsync();
            if (!stateOk || user == null)
            {
                // 会话过期: nav 拿不到就是拿不到, 不清 Cookie(网页端可能还有效)
                Error = stateOk ? "登录状态已过期, 请重新登录" : stateErr ?? "获取登录信息失败";
                return;
            }

            Name = user.Name;
            Face = user.Face;
            Level = user.Level;
            CoinsText = user.Coins.ToString("0.#");
            if (user.ExperienceNext > 0)
            {
                ExpText = $"{user.ExperienceCurrent}/{user.ExperienceNext}";
                ExpProgress = Math.Min(1.0, (double)user.ExperienceCurrent / user.ExperienceNext);
            }
            else
            {
                // Lv6: next_exp=0, 进度直接拉满且不显示分母
                ExpText = user.ExperienceCurrent > 0 ? $"{user.ExperienceCurrent}" : "";
                ExpProgress = user.ExperienceCurrent > 0 ? 1.0 : 0;
            }

            // 关注/粉丝/动态数: nav/stat 一次给齐(登录 Cookie, api.bilibili.com 域,
            // 之前用的 dynamic_svr/num 在应用内也被风控拦, 动态数永远是 "--")。
            // nav/stat 挂了就退回匿名的 relation/stat 拿关注/粉丝, 动态数显示 "--"。
            var (statOk, _, following, follower, dynCount) = await Svc.Api.GetUserNavStatAsync();
            if (statOk)
            {
                FollowingText = following.ToString();
                FollowerText = follower.ToString();
                DynamicText = dynCount.ToString();
            }
            else
            {
                var (_, _, fol, fan) = await Svc.Api.GetRelationStatAsync(user.Mid);
                FollowingText = statOk || fol > 0 ? fol.ToString() : "--";
                FollowerText = statOk || fan > 0 ? fan.ToString() : "--";
                DynamicText = "--";
            }
        }
        finally
        {
            // ★ 必须走 finally: 中途抛异常时若把 Loading 留在 true, 页面上那圈菊花会一直转,
            // 而且「刷新」被开头的 `if (Loading) return` 挡死 —— 用户自己没法把它救回来。
            Loading = false;
        }

        await LoadFoldersAsync();
    }

    /// <summary>收藏夹卡片独立加载/刷新(和资料部分解耦, 刷新按钮只重拉这一段)。
    /// <paramref name="force"/> = true 时无视"已经加载过"的短路(刷新按钮走这条)。</summary>
    public async Task LoadFoldersAsync(bool force = false)
    {
        if (FolderLoading) return;
        if (_foldersLoaded && !force) return;
        FolderLoading = true;
        FolderError = "";
        try
        {
            var (ok, err, folders) = await Svc.Api.GetFavFoldersFullAsync();

            Folders.Clear();
            OnPropertyChanged(nameof(FavCountText));
            if (!ok || folders == null)
            {
                // 失败**不置**"已加载"标记: 下次切回本页会自动重试(否则收藏夹会一直空着)
                FolderError = err ?? "收藏夹加载失败";
                return;
            }

            foreach (var f in folders) Folders.Add(f);
            _foldersLoaded = true;
            OnPropertyChanged(nameof(FavCountText));
            if (folders.Count == 0) FolderError = "还没有创建收藏夹";
        }
        finally
        {
            // 同 LoadAsync: 标志位必须由 finally 收口, 否则一次异常就让收藏夹永久卡在"加载中"
            FolderLoading = false;
        }
    }
}
