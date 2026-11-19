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
    private long _mid;
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

    /// <summary>
    /// 上次拉收藏夹时的集合版本(见 <see cref="FavFolderSync"/>)。
    /// 在收藏页/播放器新建或删除收藏夹后版本会自增 —— 本页据此让"已加载过"的短路失效,
    /// 用户切回来就能看到最新卡片, 不必手动点刷新。
    /// </summary>
    private int _foldersVersion = -1;

    /// <summary>
    /// 列表需要重拉: 刚建完夹但服务端列表还没把它带出来时, 本地先补一张卡片,
    /// 同时打上这个标记 —— 下次进页面一定再拉一次权威列表。
    /// </summary>
    private bool _foldersDirty;

    /// <summary>正在删除收藏夹(请求在飞): 防连点, 也防同一张卡被删两次</summary>
    private bool _deletingFolder;

    public ObservableCollection<FavFolder> Folders { get; } = new();

    public bool Loading { get => _loading; private set => SetProperty(ref _loading, value); }
    public bool IsLoggedIn { get => _isLoggedIn; private set => SetProperty(ref _isLoggedIn, value); }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }
    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public string Face { get => _face; private set => SetProperty(ref _face, value); }
    public int Level { get => _level; private set => SetProperty(ref _level, value); }

    /// <summary>
    /// 当前登录用户的 UID。
    ///
    /// ★ 2026-11-18 新增(issue #90): 头像/昵称/三个数字要能点进「个人主页」与「我的空间」,
    ///   而那两处都得知道"我是谁" —— 这个 mid 之前被丢掉了(只取了昵称/头像/等级)。
    /// </summary>
    public long Mid { get => _mid; private set => SetProperty(ref _mid, value); }

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

    /// <summary>
    /// 已经成功拉过一次列表, 且账号下一个收藏夹都没有 —— 空状态要在这里给出「新建收藏夹」。
    /// (不能只凭 FolderError 判断: 网络失败也是非空错误, 那种时候新建按钮帮不上忙。)
    /// </summary>
    public bool NoFolders => _foldersLoaded && Folders.Count == 0;

    public ICommand OpenCacheCommand { get; }
    public ICommand OpenHistoryCommand { get; }
    public ICommand OpenFavoritesCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CreateFolderCommand { get; }
    public ICommand DeleteFolderCommand { get; }
    public ICommand LoginCommand { get; }

    /// <summary>头像 / 昵称 / 等级徽章 → 我的个人主页(复用 UP 主主页页)。</summary>
    public ICommand OpenSelfSpaceCommand { get; }

    /// <summary>「动态」数字 → 我的空间·动态。</summary>
    public ICommand OpenDynamicsCommand { get; }

    /// <summary>「关注」数字 → 我的空间·关注。</summary>
    public ICommand OpenFollowingsCommand { get; }

    /// <summary>「粉丝」数字 → 我的空间·粉丝。</summary>
    public ICommand OpenFollowersCommand { get; }

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
        CreateFolderCommand = new AsyncRelayCommand(CreateFolderAsync);
        // 删除带参数(卡片对应的 FavFolder): 用 RelayCommand 自己转发到异步方法
        DeleteFolderCommand = new RelayCommand(p =>
        {
            if (p is FavFolder f) _ = DeleteFolderAsync(f);
        });
        LoginCommand = new RelayCommand(() => App.MainVm.OpenLoginWindow());

        // ---- 2026-11-18 新增(issue #90): 头像与三个数字变成可点的入口 ----
        //
        // ★ 都走 Svc.Navigate(App 启动时注册的单例): 它内部会判断主窗口在不在,
        //   在就内嵌进内容区(自带返回按钮), 不在就退回独立窗口 ——
        //   与「我的」页已有的快捷入口同一套导航体系。
        OpenSelfSpaceCommand = new RelayCommand(() =>
        {
            if (Mid <= 0) { Svc.Toast.Show("还没拿到账号信息, 请稍后再试"); return; }
            Svc.Navigate?.OpenUserSpace(Mid.ToString());
        });
        OpenDynamicsCommand = new RelayCommand(
            () => OpenUserCenter(UserCenterTab.Dynamics));
        OpenFollowingsCommand = new RelayCommand(
            () => OpenUserCenter(UserCenterTab.Followings));
        OpenFollowersCommand = new RelayCommand(
            () => OpenUserCenter(UserCenterTab.Followers));

        // 登录态变化(扫码成功/退出)时原地刷新 —— 否则用户在「我的」页点「去登录」,
        // 登录窗口关了页面还停在未登录态, 得切出去再切回来才恢复
        Svc.Session.Changed += OnSessionChanged;
    }

    /// <summary>打开「我的空间」的某个 Tab(看的是**我**自己的空间)。</summary>
    private void OpenUserCenter(UserCenterTab tab)
    {
        if (Mid <= 0) { Svc.Toast.Show("还没拿到账号信息, 请稍后再试"); return; }
        Svc.Navigate?.OpenUserCenter(Mid, Name, Face, isSelf: true, tab);
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
            Mid = 0;
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
            // ★ 2026-11-18: 记下自己的 mid —— 头像/昵称/三个数字都要靠它进「个人主页」与「我的空间」。
            //   之前这个值被丢掉了(只取了昵称/头像/等级), 于是那几个入口根本不知道该查谁。
            Mid = user.Mid;
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
        // ★ 版本号变了 = 别处新建过收藏夹, 即使"已经加载过"也要重拉(force 之外的第二个理由)
        if (_foldersLoaded && !force && !_foldersDirty && _foldersVersion == FavFolderSync.Version) return;
        FolderLoading = true;
        FolderError = "";
        try
        {
            var (ok, err, folders) = await Svc.Api.GetFavFoldersFullAsync();

            Folders.Clear();
            OnPropertyChanged(nameof(FavCountText));
            OnPropertyChanged(nameof(NoFolders));
            if (!ok || folders == null)
            {
                // 失败**不置**"已加载"标记: 下次切回本页会自动重试(否则收藏夹会一直空着)。
                // ★ 这里连旧标记一起放下: 列表已经被清空, 再"假装已加载"会让重试被短路,
                //   也会让 NoFolders 误判成"账号里没有收藏夹"而显示新建按钮。
                _foldersLoaded = false;
                FolderError = err ?? "收藏夹加载失败";
                return;
            }

            foreach (var f in folders) Folders.Add(f);
            _foldersLoaded = true;
            _foldersVersion = FavFolderSync.Version;
            _foldersDirty = false;
            OnPropertyChanged(nameof(FavCountText));
            OnPropertyChanged(nameof(NoFolders));
            if (folders.Count == 0) FolderError = "还没有创建收藏夹";
        }
        finally
        {
            // 同 LoadAsync: 标志位必须由 finally 收口, 否则一次异常就让收藏夹永久卡在"加载中"
            FolderLoading = false;
        }
    }

    /// <summary>
    /// 新建收藏夹(2026-11-19): 弹窗/请求与收藏页、播放器收藏窗共用 <see cref="FavFolderActions"/>,
    /// 这里只负责建完刷新卡片列表(封面/隐私/数量都得走服务端真数据, 本地拼不出来)。
    /// </summary>
    private async Task CreateFolderAsync()
    {
        var created = await FavFolderActions.CreateAsync(SvcWindow.Main);
        if (created == null) return;

        await LoadFoldersAsync(force: true);

        // 列表接口偶尔有延迟(或这次刷新失败), 没带出新夹: 先把接口返回的这一颗补成卡片,
        // 并标记"脏"让下次进页面重拉权威列表(那时封面/数量才准)。
        // ★ 同时清掉 FolderError: 卡片区在错误文案非空时是整块收起的, 不清的话
        //   用户明明刚建好夹, 页面上却只有一句"还没有创建收藏夹"。
        if (created.Id > 0 && Folders.All(f => f.Id != created.Id))
        {
            Folders.Add(created);
            _foldersDirty = true;
            FolderError = "";
            OnPropertyChanged(nameof(FavCountText));
            OnPropertyChanged(nameof(NoFolders));
        }
    }

    /// <summary>
    /// 删除收藏夹(2026-11-19): 确认/请求在 <see cref="FavFolderActions.DeleteAsync"/>,
    /// 这里建完刷新卡片列表 —— 封面/顺序以服务端为准。
    /// </summary>
    private async Task DeleteFolderAsync(FavFolder folder)
    {
        if (folder == null || _deletingFolder) return;
        _deletingFolder = true;
        try
        {
            if (!await FavFolderActions.DeleteAsync(folder)) return;
            await LoadFoldersAsync(force: true);
        }
        finally
        {
            _deletingFolder = false;
        }
    }
}
