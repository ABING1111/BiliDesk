using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.ViewModels;

/// <summary>
/// 「动态」页 ViewModel。
///
/// 布局是左右两栏(参考 B 站客户端动态页):
///   · 左栏 = 搜索关注的 UP + 「全部动态」入口 + 关注的 UP 主列表
///     (有未读时头像左侧显示红点、头像右下角显示条数角标);
///   · 右栏 = 当前选中对象的内容, 两档排版由 <see cref="UseCardWall"/> 切:
///       - 全部动态: 关注的所有 UP 近期的**综合动态**(视频/图文/文字/专栏/转发, feed/all 游标分页);
///       - 具体某 UP: 该 UP 的投稿视频(空间投稿接口, 页码分页)。
///
/// ★ 2026-11-06 关注 2790+ UP 的性能重做:
///   · 左栏不再一次创建全部行 —— 页面用虚拟化 ListBox, 只生成可见行;
///   · 关注列表每页 50(接口上限), 用接口 total 判断到底, 不再靠空页/offset 猜;
///   · 左栏/右栏加载状态分离, 左栏翻页不再让右栏底部转圈;
///   · 搜索走 `/x/relation/followings/search`, 只返回已关注的人, 不需要把 2790 人全拉下来。
///
/// 未读红点由应用级 <see cref="DynamicNotifyService"/> 维护, 这个 VM 只负责把状态
/// 应用到左栏并排序。
/// </summary>
public class FollowViewModel : ObservableObject
{
    private bool _loading;
    private bool _loadingMore;          // 右栏动态流加载更多
    private bool _loadingMoreUsers;     // 左栏关注列表加载更多(与右栏分开, 否则两边一起转圈)
    private bool _hasMore = true;
    private bool _hasMoreUsers = true;
    private bool _loadMoreError;
    private int _emptyFeedPages;

    /// <summary>
    /// 右栏加载世代。每次 reset(true) 自增; 在途的旧请求回来时对不上就整批丢弃。
    ///
    /// ★ 为什么需要(2026-11-07 "连续下滑 + 刷新"): 翻页请求还在路上时用户点了刷新,
    ///   刷新会清空 FeedItems 并重新拉第一页; 旧翻页随后返回, 会把自己的条目追加到
    ///   新列表上(混入上一轮内容), 还把 Loading/LoadingMore 状态改掉。
    ///   这不会直接崩进程, 但会让卡片墙的"首尾条目"对不上, 触发整墙重建, 连续操作时
    ///   越滚越乱。世代号是最小、最确定的解决办法。
    /// </summary>
    private int _feedGeneration;
    private string _error = "";
    private string _usersLoadError = "";
    private long _selfMid;
    private int _followingPage = 1;      // 左栏 UP 列表的翻页
    private int _totalFollowings;

    public ObservableCollection<FollowUser> Users { get; } = new();

    /// <summary>左栏搜索结果(只含已关注的 UP)。非空时替代普通关注列表显示。</summary>
    public ObservableCollection<FollowUser> FollowingSearchResults { get; } = new();

    /// <summary>右栏内容(综合动态: 视频/图文/文字/专栏/转发)。切选择时整体换血。</summary>
    public ObservableCollection<DynamicItem> FeedItems { get; } = new();

    /// <summary>
    /// 卡片墙显示用的条目。CardWallPanel 不会虚拟化, 这里最多放 300 条,
    /// 防止 2790+ 关注下滑到几百条时一次生成太多卡片容器。
    ///
    /// ★ 与 <see cref="FeedItems"/> 的同步必须是**增量**的(2026-11-07 修连续下滑崩溃):
    ///   以前每 Add 一条 FeedItems 就把整墙 Clear 再重建一遍, 而 CardWallPanel 没有虚拟化,
    ///   一页 20 条 = 20 次 × 最多 300 张卡片容器的创建/销毁。连续翻几页后 UI 线程与
    ///   WPF 渲染层都被这堆废容器淹没, 表现为越滚越卡、最后直接闪退。
    ///   现在只在整批落地后调一次, 且只把**新增的尾巴**追加进来(见 SyncWallItems)。
    /// </summary>
    public ObservableCollection<VideoItem> WallItems { get; } = new();
    private const int WallItemLimit = 300;

    private FollowUser? _selectedUp;

    /// <summary>当前选中的 UP(null = 全部动态)</summary>
    public FollowUser? SelectedUp
    {
        get => _selectedUp;
        private set
        {
            if (_selectedUp == value) return;
            var old = _selectedUp;
            _selectedUp = value;
            // 选中态是左栏列表项的高亮依据: **必须真正赋值**(IsSelected 的 setter 自带通知)。
            // 之前只调 RaiseSelectedChanged() 发通知但不改字段值 —— WPF 收到通知回头一读
            // 还是 false, 触发器永远不亮, 这就是"选中没有粉色"的根因。
            if (old != null) old.IsSelected = false;
            if (value != null) value.IsSelected = true;
            OnPropertyChanged(nameof(IsAllSelected));
            OnPropertyChanged(nameof(SelectionTitle));
        }
    }

    /// <summary>是否选中了「全部动态」(左栏入口的高亮依据)</summary>
    public bool IsAllSelected => SelectedUp == null;

    /// <summary>右栏标题: 说明当前在看谁的内容</summary>
    public string SelectionTitle => SelectedUp == null ? "全部动态" : SelectedUp.Name + " 的视频";

    /// <summary>整页加载状态。★ 变化时必须连带通知 ShowFullLoading —— 它是计算属性
    /// (Loading && FeedItems.Count==0), 不手动 raise 绑定就不会刷新。以前只在
    /// LoadSelectionAsync 里手动 raise 过两处, RefreshAsync 首次置 true 时没人通知,
    /// 遮罩不出现 ⇒ 加载期间页面"半初始化"地裸露约 1 秒(用户看到残缺的入口图案)。</summary>
    public bool Loading
    {
        get => _loading;
        private set
        {
            if (!SetProperty(ref _loading, value)) return;
            OnPropertyChanged(nameof(ShowFullLoading));
        }
    }

    /// <summary>
    /// 整页加载遮罩只在"右栏还没有任何内容"时显示。
    /// 切 UP / 回全部动态时右栏换血, 若还整页盖 spinner 会闪烁 —— 那种情况
    /// 让旧内容留着、右栏自己呈现加载状态更稳。
    /// </summary>
    public bool ShowFullLoading => Loading && FeedItems.Count == 0;

    /// <summary>右栏动态流正在加载下一页(底部转圈只绑这个)。</summary>
    public bool LoadingMore { get => _loadingMore; private set => SetProperty(ref _loadingMore, value); }

    /// <summary>左栏关注列表正在加载下一页(与右栏分开, 否则左栏翻页时右栏也转圈)。</summary>
    public bool LoadingMoreUsers
    {
        get => _loadingMoreUsers;
        private set => SetProperty(ref _loadingMoreUsers, value);
    }

    public bool HasMore
    {
        get => _hasMore;
        private set
        {
            if (!SetProperty(ref _hasMore, value)) return;
            OnPropertyChanged(nameof(ShowEndOfFeed));
        }
    }

    public bool HasMoreUsers { get => _hasMoreUsers; private set => SetProperty(ref _hasMoreUsers, value); }

    /// <summary>
    /// 「已经到底啦」是否该显示(2026-11-06 用户报"这个提示不会消失, 会一直存在")。
    ///
    /// ★ 为什么不能只绑 <c>HasMore</c> 取反:
    ///   HasMore 一旦变成 false 就再也不变了 —— 用户滚上去再看, 那句"已经到底啦"
    ///   还钉在列表底部, 像一条常驻的横幅。用户要的是"到底了"这个**瞬时反馈**:
    ///   滚到底时出现, 往上滚就消失。
    /// ★ 所以额外要一个"用户当前是不是在列表底部"的状态(由滚动事件写入)。
    /// </summary>
    public bool ShowEndOfFeed => !_hasMore && _atFeedBottom;

    /// <summary>用户当前是否停在右栏列表的底部(滚动事件写入)。</summary>
    private bool _atFeedBottom;

    /// <summary>由 FollowPage 的滚动事件调用: 更新"是否在底部", 决定「已经到底啦」的显隐。</summary>
    public void SetAtFeedBottom(bool atBottom)
    {
        if (_atFeedBottom == atBottom) return;
        _atFeedBottom = atBottom;
        OnPropertyChanged(nameof(ShowEndOfFeed));
    }

    /// <summary>右栏翻页失败: 底部显示重试, 滚动不再自动重试(否则会无限转圈)。</summary>
    public bool LoadMoreError { get => _loadMoreError; private set => SetProperty(ref _loadMoreError, value); }

    /// <summary>左栏关注列表翻页失败</summary>
    public string UsersLoadError
    {
        get => _usersLoadError;
        private set
        {
            if (!SetProperty(ref _usersLoadError, value)) return;
            OnPropertyChanged(nameof(HasUsersLoadError));
        }
    }
    public bool HasUsersLoadError => UsersLoadError.Length > 0;

    public string Error { get => _error; private set => SetProperty(ref _error, value); }

    /// <summary>接口给出的关注总数(2790 这类大账号靠它判断到底, 不看空页)。</summary>
    public int TotalFollowings
    {
        get => _totalFollowings;
        private set
        {
            if (!SetProperty(ref _totalFollowings, value)) return;
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(FollowingsSummary));
        }
    }

    /// <summary>头部显示的关注数: 优先用接口 total, 还没拿到时退回已加载条数。</summary>
    public int Count => TotalFollowings > 0 ? TotalFollowings : Users.Count(u => !u.IsPlaceholder);

    /// <summary>左栏状态行: "已加载 300 / 2790"(占位行不计入已加载数)。</summary>
    public string FollowingsSummary => TotalFollowings > 0
        ? $"已加载 {Users.Count(u => !u.IsPlaceholder)} / {TotalFollowings}"
        : $"已加载 {Users.Count(u => !u.IsPlaceholder)}";

    public bool NotLoggedIn => !Svc.Session.HasLogin;

    // ------------------------------------------------------------ 搜索已关注 UP(2026-11-06)

    private string _userSearchText = "";
    private bool _userSearching;
    private string _userSearchError = "";
    private CancellationTokenSource? _userSearchCts;

    /// <summary>搜索框文本。输入即触发 300ms 防抖, 调 B 站「搜索关注明细」接口。</summary>
    public string UserSearchText
    {
        get => _userSearchText;
        set
        {
            if (!SetProperty(ref _userSearchText, value)) return;
            OnPropertyChanged(nameof(HasUserSearch));
            OnPropertyChanged(nameof(ShowUserSearchEmpty));
            StartUserSearch();
        }
    }

    public bool HasUserSearch => !string.IsNullOrWhiteSpace(UserSearchText);

    public bool UserSearching
    {
        get => _userSearching;
        private set => SetProperty(ref _userSearching, value);
    }

    public string UserSearchError
    {
        get => _userSearchError;
        private set
        {
            if (!SetProperty(ref _userSearchError, value)) return;
            OnPropertyChanged(nameof(HasUserSearchError));
        }
    }
    public bool HasUserSearchError => UserSearchError.Length > 0;

    /// <summary>搜索结果为空时的提示(区分"正在搜"和"没搜到")。</summary>
    public bool ShowUserSearchEmpty =>
        HasUserSearch && !UserSearching && !HasUserSearchError && FollowingSearchResults.Count == 0;

    /// <summary>动态页头部「全部已读」按钮的可见性。</summary>
    public bool HasAnyUnread => Svc.DynamicNotify.HasUnread;

    // ------------------------------------------------------------ 关注直播看板(2026-10-31)

    /// <summary>正在直播的关注主播(点一条直接进 TA 的直播间)。空 = 没人在播(看板整块收起)。</summary>
    public ObservableCollection<VideoItem> LiveNow { get; } = new();

    private bool _liveBoardOpen;

    /// <summary>
    /// 看板展开/收起(UI 状态, 不落盘)。
    /// ★ 默认**收起**(2026-11-03 用户要求"关注的直播默认折叠, 仅点击后再展开"):
    ///   标题行始终显示人数, 用户想看谁在播时再点开 —— 不占动态流的首屏空间。
    ///   收起时列表与空态提示都必须一起藏掉(见 <see cref="ShowLiveEmptyText"/>),
    ///   否则会剩一行"暂时都没有在直播"挂在那里, 看着像没收干净。
    /// </summary>
    public bool LiveBoardOpen
    {
        get => _liveBoardOpen;
        set
        {
            if (SetProperty(ref _liveBoardOpen, value))
                OnPropertyChanged(nameof(ShowLiveEmptyText));
        }
    }

    /// <summary>看板标题: 说明有几人在播(0 人也保留标题行, 看板本身始终常驻)</summary>
    public string LiveBoardTitle => $"关注直播 · {LiveNow.Count}";

    /// <summary>看板加载中(随 RefreshAsync 并行, 不挡动态流)</summary>
    private bool _liveBoardLoading;

    /// <summary>看板请求在途(挡住定时刷新与人工刷新撞车; 不参与 UI 绑定)</summary>
    private bool _liveBoardBusy;

    public bool LiveBoardLoading
    {
        get => _liveBoardLoading;
        private set
        {
            if (SetProperty(ref _liveBoardLoading, value))
                OnPropertyChanged(nameof(ShowLiveEmptyText));
        }
    }

    /// <summary>看板有没有内容可显示(有主播在播)</summary>
    public bool HasLiveNow => LiveNow.Count > 0;

    /// <summary>
    /// 看板空态说明("关注的主播暂时都没有在直播")的可见性:
    /// 看板展开、加载**完成**且没人在播才显示 —— 加载中显示它会把"正在查"误报成"没人在播",
    /// 收起时显示它则像没收干净(2026-11-03 默认收起后必须一起判断)。
    /// (Style 触发器压不过属性绑定, 这种复合条件在 VM 里算好最干净。)
    /// </summary>
    public bool ShowLiveEmptyText => LiveBoardOpen && !LiveBoardLoading && !HasLiveNow;

    /// <summary>
    /// 拉"关注的主播正在直播"列表。与动态流**并行**加载(不占 RefreshAsync 的关键路径),
    /// 失败静默 —— 看板是锦上添花, 不该因为它给整页报错。
    ///
    /// ★ 进入动态页时总会调用一次(见 InitAsync 与 FollowPage 的定时刷新),
    ///   所以必须有**并发闸**: 定时器与服务端响应慢时的人工刷新会撞在一起,
    ///   两路结果先后 Clear+Add 会让列表闪一下、条数也可能对不上。
    ///
    /// ★ showLoading: 后台轮询不要点亮标题行那只 LoadingRing ——
    ///   否则没人在播时, 空态文案每分钟消失半秒, 看着像在抽风。
    /// </summary>
    public async Task RefreshLiveBoardAsync(bool showLoading = true)
    {
        if (!Svc.Session.HasLogin || _liveBoardBusy) return;
        _liveBoardBusy = true;
        if (showLoading) LiveBoardLoading = true;
        try
        {
            var (ok, _, items) = await Svc.Api.GetFollowingLiveAsync();
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                LiveBoardLoading = false;
                if (!ok) return;   // 静默: 风控/网络抖动时看板保持原样
                LiveNow.Clear();
                if (items != null)
                    foreach (var it in items) LiveNow.Add(it);
                OnPropertyChanged(nameof(LiveBoardTitle));
                OnPropertyChanged(nameof(HasLiveNow));
                OnPropertyChanged(nameof(ShowLiveEmptyText));
            });
        }
        catch
        {
            LiveBoardLoading = false;
            // 静默(同上)
        }
        finally
        {
            _liveBoardBusy = false;
        }
    }

    /// <summary>点看板里的一条 → 直接进 TA 的直播间</summary>
    public void OpenLive(VideoItem item)
    {
        if (item == null || item.RoomId <= 0) return;
        Svc.Player.PlayLive(item.RoomId, item.Title);
    }

    public ICommand RefreshCommand { get; }
    public ICommand LoadMoreCommand { get; }
    public ICommand LoadMoreFeedCommand { get; }
    public ICommand LoginCommand { get; }
    public ICommand OpenUpCommand { get; }
    public ICommand ClearUserSearchCommand { get; }
    public ICommand MarkAllReadCommand { get; }

    // 分页游标(两种来源各自一套)。
    // "快速连点导致结果串台"用 LoadSelectionAsync 里的 forUp 快照 + SelectedUp 比对兜住。
    private string? _feedOffset;         // 全部动态: 游标
    private int _upVideoPage = 1;        // 单 UP: 页码

    /// <summary>
    /// 动态流里同一视频会出现多次(转发 + 原投稿 + 多人合作视频各自的动态),
    /// 按跨页累积的 bvid 去重, 否则"全部动态"里能看到两张一模一样的卡片。
    /// reset 时清空。
    /// </summary>
    private readonly HashSet<string> _seenBvid = new(StringComparer.Ordinal);

    /// <summary>
    /// 图文/文字/转发没有 bvid, 按动态 id 去重。视频动态也先过这一道,
    /// 防止同一条动态被接口重复下发。reset 时清空。
    /// </summary>
    private readonly HashSet<string> _seenDynamicId = new(StringComparer.Ordinal);

    public FollowViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        LoadMoreCommand = new AsyncRelayCommand(LoadMoreAsync);
        LoadMoreFeedCommand = new AsyncRelayCommand(LoadMoreFeedAsync);
        ClearUserSearchCommand = new RelayCommand(ClearUserSearch);
        MarkAllReadCommand = new RelayCommand(() =>
        {
            Svc.DynamicNotify.MarkAllRead();
            ApplyNotifyStates();
            OnPropertyChanged(nameof(HasAnyUnread));
        });
        LoginCommand = new RelayCommand(() =>
        {
            var win = new Views.LoginWindow
            {
                Owner = System.Windows.Application.Current.MainWindow,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner
            };
            win.ShowDialog();
        });
        OpenUpCommand = new RelayCommand(p =>
        {
            if (p is FollowUser u)
            {
                Svc.Navigate?.OpenUserSpace(u.Mid.ToString());
            }
        });
        Svc.Session.Changed += OnSessionChanged;
        Svc.DynamicNotify.Updated += OnNotifyUpdated;
        // ★ 不再逐条监听 FeedItems.CollectionChanged:
        //   翻一页会 Add 十几到几十条, 逐条通知会让卡片墙**整墙重建 N 次**
        //   (每次 Clear + 重新 Add 最多 300 条) —— 用户连续下滑加载时 UI 线程被这堆
        //   容器生成/销毁拖住, 内存曲线也一节一节往上跳, 极端情况下直接把进程拖崩。
        //   现在改为在"一批加载真正落地后"调一次 SyncWallItems(增量追加), 见 LoadSelectionAsync。
    }

    private void OnNotifyUpdated()
    {
        // ★ 2026-11-06 用户要求"点带红点的 UP 以后不要瞬间跳位置":
        //   服务的 Updated 事件每 60 秒也会因轮询触发一次, 以前这里顺手重排了整条列表,
        //   于是"点开一个红点 UP"= 它的红点清零 = 当场从顶部掉到已读区。
        //   现在这里**只就地更新红点数字**, 位置一律不动; 要重排就退出动态页再进来,
        //   或点右下角刷新(见 ReshapeUserList 的调用点)。
        ApplyNotifyStates();
    }

    private void OnSessionChanged()
    {
        OnPropertyChanged(nameof(NotLoggedIn));
        if (!Svc.Session.HasLogin)
        {
            // 退出登录: 不要让上一个账号的关注列表/未读状态留在界面上。
            Users.Clear();
            FollowingSearchResults.Clear();
            FeedItems.Clear();
            WallItems.Clear();
            TotalFollowings = 0;
            HasMoreUsers = false;
            SelectedUp = null;
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(FollowingsSummary));
            OnPropertyChanged(nameof(HasAnyUnread));
        }
        _ = RefreshAsync();
    }

    public async Task InitAsync()
    {
        // ★ 页面实例是长期缓存的(MainWindow._pages): 从别的页面切回来时 Loaded 会再响一次,
        //   但 Users.Count 不为 0, 以前这里直接返回 —— 看板/未读红点就永远停在
        //   "第一次进动态页"那一刻。现在: 已加载过就轻量刷新看板与红点,
        //   让"刚刚开播"和"刚刚投稿"都能立刻出现在页面上。
        if (Users.Count == 0)
        {
            await RefreshAsync();
            return;
        }

        await Task.WhenAll(
            RefreshLiveBoardAsync(showLoading: LiveNow.Count == 0),
            RefreshUnreadBadgesAsync());
    }

    /// <summary>从列表移除一个 UP 主(取消关注成功后调用)。移除的若是选中项, 回到全部动态</summary>
    public void RemoveUser(FollowUser u)
    {
        Users.Remove(u);
        FollowingSearchResults.Remove(u);
        Svc.DynamicNotify.Forget(u.Mid);
        if (TotalFollowings > 0) TotalFollowings = Math.Max(0, TotalFollowings - 1);
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(FollowingsSummary));
        OnPropertyChanged(nameof(HasAnyUnread));
        if (SelectedUp?.Mid == u.Mid) SelectedUp = null;
    }

    /// <summary>选中「全部动态」</summary>
    public Task SelectAllAsync()
    {
        // 从搜索结果点回「全部动态」时退出搜索, 否则左栏还停在搜索结果上, 与右栏不一致。
        if (HasUserSearch) ClearUserSearch();
        SelectedUp = null;
        return LoadSelectionAsync(reset: true);
    }

    /// <summary>选中某个 UP</summary>
    public Task SelectUpAsync(FollowUser u)
    {
        if (u == null || u.Mid <= 0) return Task.CompletedTask;
        // 搜索结果可能是一个新的 FollowUser 实例; 已加载的 Users 里有同一个 mid 时
        // 优先用 Users 的实例, 这样左栏的选中高亮/排序仍然围绕同一个对象。
        var target = Users.FirstOrDefault(x => x.Mid == u.Mid) ?? u;
        SelectedUp = target;
        // 点开 TA = 已读: 清掉这个 UP 的红点, 基准推进到最新一条。
        Svc.DynamicNotify.MarkRead(u.Mid);
        ApplyNotifyStates();
        return LoadSelectionAsync(reset: true);
    }

    public async Task RefreshAsync()
    {
        Loading = true;
        Error = "";
        UsersLoadError = "";
        LoadMoreError = false;
        if (!Svc.Session.HasLogin)
        {
            Loading = false;
            Error = "请先登录后查看动态";
            return;
        }
        try
        {
            // 先拿到自己的 mid
            var (_, _, me) = await Svc.Api.GetUserStateAsync();
            if (me == null || me.Mid <= 0)
            {
                Loading = false;
                Error = "未登录";
                return;
            }
            _selfMid = me.Mid;

            var (ok, err, list, total) = await Svc.Api.GetFollowingsAsync(_selfMid, 1, 50);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (list != null)
                {
                    Users.Clear();
                    foreach (var u in list) Users.Add(u);
                    _followingPage = 1;
                    TotalFollowings = total;
                    // 刷新会换掉 Users 的实例: 把选中项重新指向新实例, 否则左栏高亮会消失。
                    if (SelectedUp != null)
                    {
                        var keep = Users.FirstOrDefault(x => x.Mid == SelectedUp.Mid);
                        SelectedUp = keep;
                    }
                    // total 是权威值; 拿不到时退回"本页是否满 50"。
                    // 首页为空也必须停, 否则会一直请求下一页。
                    HasMoreUsers = list.Count > 0 &&
                                   (total > 0
                                       ? Users.Count(u => !u.IsPlaceholder) < total
                                       : list.Count >= 50);
                    OnPropertyChanged(nameof(Count));
                    OnPropertyChanged(nameof(FollowingsSummary));
                    // 进入页面/刷新是**唯一**重排左栏的时机(2026-11-06 用户要求:
                    // 点带红点的 UP 不要当场跳位置, 位置只在重新进入动态页后刷新)。
                    ApplyNotifyStates();
                    RefreshUserPositions();
                }
                else if (Users.Count == 0)
                {
                    Error = err ?? "加载失败";
                }
                Loading = false;
            });
            await LoadSelectionAsync(reset: true);
            // 关注直播看板: 与动态流并行(不 await 到关键路径里), 慢一点没关系
            _ = RefreshLiveBoardAsync();
            // 刷新未读红点: 服务内部有 25 秒新鲜窗口, 刚轮询过就不重复请求。
            _ = RefreshUnreadBadgesAsync();
        }
        catch (Exception ex)
        {
            Loading = false;
            Error = ex.Message;
        }
    }

    /// <summary>
    /// 左栏翻页: 关注列表一页 50 个(接口上限)。
    /// ★ 与右栏的 LoadingMore 完全分开: 左栏翻页时右栏底部不能跟着转圈。
    /// ★ 用接口 total 判断到底; 空页/重复页不会让页码卡住反复重试。
    /// </summary>
    public async Task LoadMoreAsync()
    {
        if (LoadingMoreUsers || !HasMoreUsers || Loading || _selfMid <= 0) return;
        LoadingMoreUsers = true;
        UsersLoadError = "";
        try
        {
            var nextPage = _followingPage + 1;
            var (ok, err, list, total) = await Svc.Api.GetFollowingsAsync(_selfMid, nextPage, 50);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!ok || list == null)
                {
                    // 失败时保留 HasMoreUsers, 底部显示重试; 滚动不再自动重试。
                    UsersLoadError = err ?? "加载关注列表失败";
                    return;
                }

                _followingPage = nextPage;
                if (total > 0) TotalFollowings = total;

                var seen = new HashSet<long>(Users.Where(u => !u.IsPlaceholder).Select(u => u.Mid));
                var added = 0;          // 新增到列表的行数(不含占位行替换)
                var progressed = 0;     // 本页真正被消费的行数(新增 + 占位行补全)
                foreach (var u in list)
                {
                    if (u.Mid <= 0) continue;

                    // 这个 UP 已经在左栏最前面有一条"未读占位行": 用真实数据**就地替换**它,
                    // 而不是再加一行(否则同一个 UP 会出现两次, 用户会以为关注了两个同名账号)。
                    // ★ 这一步必须在 seen 判重**之前**: 占位行的 mid 也在 Users 里,
                    //   先判重的话会把它当成"已存在"直接跳过, 占位行就永远补不成真实数据。
                    var placeholder = Users.FirstOrDefault(x => x.Mid == u.Mid && x.IsPlaceholder);
                    if (placeholder != null)
                    {
                        var idx = Users.IndexOf(placeholder);
                        u.IsSelected = placeholder.IsSelected;
                        u.NewCount = placeholder.NewCount;
                        if (placeholder.LatestPubdate > u.LatestPubdate) u.LatestPubdate = placeholder.LatestPubdate;
                        Users[idx] = u;
                        if (SelectedUp == placeholder) SelectedUp = u;
                        progressed++;
                        continue;
                    }

                    if (!seen.Add(u.Mid)) continue;
                    Users.Add(u);
                    added++;
                    progressed++;
                }

                OnPropertyChanged(nameof(Count));
                OnPropertyChanged(nameof(FollowingsSummary));

                // 到底的判据: 有 total 用 total; 没有 total 时本页不满 50。
                // ★ 空页/全是重复项也必须停, 否则滚到底会无限重试同一页(转圈不消失)。
                // ★ 用 progressed 而不是 added: 一页全是"占位行补全"时列表条数没涨,
                //   但页码确实前进了, 不该因此误判到底。
                HasMoreUsers = list.Count > 0 && progressed > 0 &&
                               (TotalFollowings > 0
                                   ? Users.Count(u => !u.IsPlaceholder) < TotalFollowings
                                   : list.Count >= 50);

                // 新翻出来的 UP 也要立刻应用未读状态(红点数字), 但**不重排** ——
                // 位置只在进入动态页/点刷新时刷新(2026-11-06 用户要求)。
                ApplyNotifyStates();
            });
        }
        catch (Exception ex)
        {
            UsersLoadError = ex.Message;
        }
        finally
        {
            LoadingMoreUsers = false;
        }
    }

    // ------------------------------------------------------------ 右栏内容

    /// <summary>按当前选择加载右栏。reset = 清空重来; false = 追加下一页</summary>
    private async Task LoadSelectionAsync(bool reset)
    {
        if (Loading) return;
        if (reset)
        {
            // 新的世代: 之前还在路上的翻页请求全部作废(见 _feedGeneration 的说明)
            _feedGeneration++;
            Loading = true;
            // 在途的翻页属于旧世代, 它的结果会被丢弃 —— 这里必须把它的"加载中"标记收掉,
            // 否则它永远没机会自己清(旧回调看到世代不符已经 return), 之后再也翻不了页。
            LoadingMore = false;
            FeedItems.Clear();
            // 右栏换血时卡片墙也要立刻空掉(不然加载期间会留着上一个 UP / 上一轮的内容)。
            // 这里是**一次性**清空, 不是逐条事件, 代价只有一次。
            if (UseCardWall) SyncWallItems();
            _seenBvid.Clear();
            _seenDynamicId.Clear();
            _feedOffset = null;
            _emptyFeedPages = 0;
            LoadMoreError = false;
            Error = "";
            // 换内容 ⇒ 不在底部了, 「已经到底啦」先收起来(等用户真滑到底再出现)
            SetAtFeedBottom(false);
            // (ShowFullLoading 的通知已由 Loading 的 setter 连带发出, 不再手动 raise)
        }
        else
        {
            if (LoadingMore || !HasMore || LoadMoreError) return;
            LoadingMore = true;
        }

        var forUp = SelectedUp;         // 快照: 期间用户又点了别人时, 结果作废
        var generation = _feedGeneration;   // 快照: 期间发生过 reset 时, 本次结果作废
        bool ok;
        string? err = null;
        List<DynamicItem>? items = null;
        var hasMore = false;

        try
        {
            if (forUp == null)
            {
                var (rOk, rErr, rItems, nextOffset, rHasMore) =
                    await Svc.Api.GetDynamicFeedAsync(reset ? null : _feedOffset);
                ok = rOk; err = rErr; items = rItems;

                // ★ 2026-11-06: 真正使用接口的 has_more, 同时保留 offset 前进判断。
                //   旧版只看 offset, 接口失败/连续返回重复页时会一直认为"还有下一页",
                //   每次滚动都再请求一次, 底部转圈永远不消失。
                hasMore = ok && rHasMore && !string.IsNullOrEmpty(nextOffset) &&
                          nextOffset != _feedOffset;
                if (ok) _feedOffset = nextOffset;
            }
            else
            {
                if (reset) _upVideoPage = 1;
                var r = await Svc.Api.GetSpaceVideosAsync(forUp.Mid, _upVideoPage, 30);
                ok = r.Item1; err = r.Item2;
                var videoItems = r.Item3;
                // ★★ 2026-11-06: "到底"改用接口给的**总条数**判断, 不再用"本页 &lt; 30"。
                //   旧判据不成立(实测): 降级接口 recArchivesByKeywords 自己按 17/22 这种
                //   零头分页, 952 投稿的 UP 在第 31 页只给 17 条 —— 于是第 31 页就被
                //   误判成到底, 用户滚了半天还没看完就冒出「已经到底啦」。
                //   total 是权威的总投稿数(来自 arc/search 的 page.count);
                //   ★ 降级路径(被风控时)拿不到 total ⇒ 改用"空页才到底": 本页非空就继续翻,
                //     翻到接口真的返回 0 条才是底 —— 这比"条数不足 30"准得多。
                var loaded = FeedItems.Count + (videoItems?.Count ?? 0);
                hasMore = ok && videoItems != null && videoItems.Count > 0 &&
                          (r.Item4 > 0 ? loaded < r.Item4 : true);
                if (ok) _upVideoPage++;

                // ★ 单 UP 视图的**头像与名字要在这里盖章**(2026-10-15 用户截图: 卡片上一个头像
                //   都没有、名字也是空的, 只剩"· 10-14 18:45"):
                //   空间投稿的两个接口**都不下发头像**; 风控降级接口连 UP 名都没有(只有 upMid)。
                //   但这两样我们本来就知道 —— 左侧那一条 UP 上就摆着 —— 抄过去即可,
                //   不必为此再发请求, 更不该让卡片空着。
                if (videoItems != null)
                {
                    foreach (var it in videoItems)
                    {
                        if (string.IsNullOrEmpty(it.Author)) it.Author = forUp.Name;
                        if (string.IsNullOrEmpty(it.AuthorFace)) it.AuthorFace = forUp.Face;
                    }
                    // 单 UP 视图只有视频, 包成 DynamicItem 以复用同一张综合动态卡
                    items = videoItems.Select(DynamicItem.FromVideo).ToList();
                }
            }

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                // ★ 期间发生过 reset(刷新/切选择): 本次结果整批丢弃, **连 Loading 状态都不要碰** ——
                //   现在这两个标记属于新的那次加载, 旧请求无权清掉(否则刷新遮罩会提前消失)。
                if (generation != _feedGeneration) return;
                Loading = false;
                LoadingMore = false;
                if (SelectedUp != forUp) return;   // 选择已变, 丢弃本次结果
                if (items == null)
                {
                    if (reset || FeedItems.Count == 0) Error = err ?? "加载失败";
                    else LoadMoreError = true;
                    return;
                }

                var added = 0;
                foreach (var it in items)
                {
                    // 先按动态 id 去重(图文/文字/转发没有 bvid, 只能靠它)
                    if (!string.IsNullOrEmpty(it.Id) && !_seenDynamicId.Add(it.Id))
                        continue;

                    // 视频再按 bvid 去重: 同一条视频可能被原投稿 + 多人转发重复下发
                    if (it.HasAnyVideo)
                    {
                        var v = it.AsVideoItem();
                        if (v != null && !string.IsNullOrEmpty(v.Bvid) && !_seenBvid.Add(v.Bvid))
                            continue;
                    }

                    FeedItems.Add(it);
                    added++;
                }

                if (reset) _emptyFeedPages = 0;
                if (added == 0) _emptyFeedPages++;
                else _emptyFeedPages = 0;

                // 连续 2 页没有任何新动态 = 到底, 或接口一直回重复页/空页。
                // 不设这道闸时, 滚到底会无限"加载中 → 0 条 → 再加载"。
                if (_emptyFeedPages >= 2) hasMore = false;

                HasMore = hasMore;
                LoadMoreError = false;
                Error = "";
                // (ShowFullLoading 的通知已由 Loading 的 setter 连带发出, 不再手动 raise)
                OnPropertyChanged(nameof(FollowingsSummary));
                if (UseCardWall) SyncWallItems();
                OnPropertyChanged(nameof(ShowWallLimitHint));

                if (FeedItems.Count == 0)
                    Error = forUp == null ? "关注的 UP 最近没有动态" : "该 UP 暂时没有投稿视频";

                // ★ 内容装得下一屏时列表根本没有滚动条, 滚动事件不会触发 ⇒ "是否在底部"没人更新,
                //   「已经到底啦」也就永远不出现。这里主动按"装得下"置为在底部。
                //   (装不下的情况由 FeedScroll_OnScrollChanged 负责。)
                if (!hasMore && FeedItems.Count > 0)
                    SetAtFeedBottom(true);

                // 左栏未读状态由应用级服务维护; 这里只把最新状态应用上(**不重排**,
                // 位置只在进入动态页/点刷新时刷新, 见 RefreshUserPositions)。
                ApplyNotifyStates();
            });
        }
        catch (Exception ex)
        {
            // 旧世代请求的失败同样不参与状态变更(见上)。
            if (generation != _feedGeneration) return;
            Loading = false;
            LoadingMore = false;
            if (reset) Error = ex.Message;
            else LoadMoreError = true;
        }
    }

    // ------------------------------------------------------------ 右栏排版

    private bool _useCardWall = Svc.Settings.FollowCardWall;

    /// <summary>
    /// 右栏排版: true = 卡片墙(多列网格), false = 动态卡(单列: 头像 + 发布时间 + 视频)。
    ///
    /// ★ 初值直接读设置(用户偏好, 重启/切页都保持); 改一次就落盘 —— 与"卡片列数"同一类,
    ///   这不是"每片状态", 不该跟着数据刷新走。
    /// ★ 切换只改这个 bool: 界面上两套 ItemsControl 各自绑 Its Visibility(见 FollowPage),
    ///   所以不需要重建列表, 滚动位置也不会丢。
    /// </summary>
    public bool UseCardWall
    {
        get => _useCardWall;
        set
        {
            if (!SetProperty(ref _useCardWall, value)) return;
            Svc.Settings.FollowCardWall = value;
            Svc.Settings.Save();
            if (value) SyncWallItems();
            OnPropertyChanged(nameof(ShowWallLimitHint));
        }
    }

    /// <summary>卡片墙超过 300 条时的提示(避免用户以为后面没有内容了)。
    /// ★ 卡片墙只收视频, 所以按 FeedItems 里的视频动态数比较, 不是总条数。</summary>
    public bool ShowWallLimitHint => UseCardWall && FeedItems.Count(x => x.HasAnyVideo) > WallItems.Count;

    /// <summary>
    /// 把 FeedItems 里的**视频动态**(前 300 条)同步到卡片墙集合。
    /// 图文/文字/专栏不进卡片墙 —— VideoCard 依赖 16:9 封面与播放数据, 硬塞会破坏网格。
    ///
    /// ★★ 增量追加(2026-11-07): 只有"墙确实接不上"时才整墙清空, 其余情况只补尾巴。
    ///   整墙清空 = CardWallPanel 把最多 300 个卡片容器全部销毁再重建; 连续下滑时
    ///   每翻一页都来一次, 是"越滚越卡 → 崩溃"的直接来源。
    ///
    /// 整墙重建的三种情况:
    ///   ① 墙空了(want &gt; 0) —— 正常从 0 开始补;
    ///   ② 墙比目标长(刷新/换 UP/切回卡片墙后 FeedItems 变短);
    ///   ③ 墙的首尾条目与视频源对不上 —— 说明整批内容被换过(刷新/切选择/屏蔽)。
    ///   其余情况(最常见: 翻页后 FeedItems 只是变长)走 append, 开销 = 新增那几条。
    /// </summary>
    private void SyncWallItems()
    {
        // DynamicItem.AsVideoItem() 内部缓存, 同一条动态每次返回同一个 VideoItem 引用,
        // 下面的首尾引用比对才稳定。
        var videos = new List<VideoItem>(WallItemLimit);
        foreach (var it in FeedItems)
        {
            if (!it.HasAnyVideo) continue;
            var v = it.AsVideoItem();
            if (v != null) videos.Add(v);
            if (videos.Count >= WallItemLimit) break;
        }

        var want = videos.Count;
        var reset = want == 0
                    || WallItems.Count > want
                    || (WallItems.Count > 0 &&
                        (!ReferenceEquals(WallItems[0], videos[0]) ||
                         !ReferenceEquals(WallItems[WallItems.Count - 1], videos[want - 1])));

        if (reset && WallItems.Count > 0) WallItems.Clear();

        // reset 之后 Count 为 0; 未重置时从这里接着补新增的条目。
        for (var i = WallItems.Count; i < want; i++) WallItems.Add(videos[i]);

        OnPropertyChanged(nameof(ShowWallLimitHint));
    }

    // ------------------------------------------------------------ 未读红点(应用级服务)

    /// <summary>
    /// 刷新未读红点。真正的轮询在 <see cref="DynamicNotifyService"/> 里(应用级、每 60 秒),
    /// 这里只请求它"现在刷一次"并把状态应用到左栏, 不碰右栏列表。
    ///
    /// ★ 调用时机 = **进入动态页 / 点刷新**。这也是重排左栏的时机(见 RefreshUserPositions):
    ///   60 秒轮询(OnNotifyUpdated)只更新红点数字, 不动行位置 —— 否则用户刚点开一个
    ///   带红点的 UP, 它当场就从顶部掉下去。
    /// </summary>
    public async Task RefreshUnreadBadgesAsync(bool force = false)
    {
        if (!Svc.Session.HasLogin || Users.Count == 0) return;
        await Svc.DynamicNotify.RefreshAsync(force);
        ApplyNotifyStates();
        RefreshUserPositions();
    }

    /// <summary>把服务里的未读状态应用到已加载的 UP 和搜索结果上。</summary>
    private void ApplyNotifyStates()
    {
        foreach (var u in Users) ApplyNotifyState(u);
        foreach (var u in FollowingSearchResults) ApplyNotifyState(u);
        OnPropertyChanged(nameof(HasAnyUnread));
        OnPropertyChanged(nameof(ShowUserSearchEmpty));
    }

    /// <summary>
    /// 重排左栏 + 补齐未读占位行 —— **只在进入动态页 / 点刷新时调用**(2026-11-06 用户要求)。
    ///
    /// ★ 为什么不能每次轮询都排: 用户报"点一个带红点的 UP, 它当场就跳走了"——
    ///   MarkRead 会触发服务的 Updated 事件, 而那里原本也调重排, 于是刚点的那一行
    ///   立刻从顶部掉到"已读"区。现在事件里只更新红点数字, 位置保持不变,
    ///   要看到新顺序就退出动态页再进来(或点右下角刷新)。
    /// </summary>
    private void RefreshUserPositions()
    {
        SyncUnreadPlaceholders();
        ReorderUsersByActivity(FeedItems);
    }

    /// <summary>
    /// 把"有未读红点、但还没被分页加载出来"的 UP 直接插进**同一个** Users 列表(2026-11-06)。
    ///
    /// ★ 为什么不再单独做一个「有新视频」面板(用户 2026-11-06 要求"左栏只做一个面板"):
    ///   关注 2790 人时, 一个刚更新的 UP 很可能还在第 2 页之后, 单独开一个面板会让左栏
    ///   变成"两个列表", 视觉上很乱。现在把这类 UP 作为**占位行**插到 Users 最前面,
    ///   与正常行同一个列表、同一套模板 —— 点进去就是正常的"只看 TA 的投稿"。
    /// ★ 占位行带 Mid/Name/头像/未读数; 等分页真的加载到这个人时, 用真实数据**就地补全**
    ///   (见 LoadMoreAsync 里的 MergeLoadedUser), 不会出现两行同一个人。
    /// ★ 头像(Face)必须有(2026-11-06 用户报"更新的 UP 头像不显示/显示别人的"):
    ///   占位行不来自关注列表接口, 只能从 portal 的 up_list 拿; 拿不到的话那一格
    ///   在虚拟化回收后会残留**上一个 UP 的头像** —— 这就是"错显示其他 UP 头像"的来源。
    /// </summary>
    private void SyncUnreadPlaceholders()
    {
        var snapshot = Svc.DynamicNotify.GetUnreadSnapshot();
        var loadedMids = new HashSet<long>(Users.Where(u => !u.IsPlaceholder).Select(u => u.Mid));

        var want = new List<(long Mid, string Name, string Face, int NewCount, long Pub)>();
        foreach (var s in snapshot)
        {
            if (loadedMids.Contains(s.Mid) || s.NewCount <= 0) continue;
            want.Add(s);
        }

        // 先删掉不再需要的占位行(已读 / 已被真实数据取代)
        // ★ 正在选中的那一行不删: 用户点开一个"未读占位行"的瞬间它就已经已读了,
        //   立刻删掉会让左栏选中高亮凭空消失, 右栏却还在显示 TA 的视频 —— 像点错了。
        //   它会在用户切到别的 UP / 下一次轮询时自然消失。
        for (var i = Users.Count - 1; i >= 0; i--)
        {
            if (!Users[i].IsPlaceholder) continue;
            if (Users[i] == SelectedUp) continue;
            var keep = false;
            foreach (var w in want)
                if (Users[i].Mid == w.Mid) { keep = true; break; }
            if (!keep) Users.RemoveAt(i);
        }

        // 再按快照顺序补/更新(占位行一律在最前面)
        for (var target = 0; target < want.Count; target++)
        {
            var w = want[target];
            var existing = Users.FirstOrDefault(x => x.Mid == w.Mid);
            if (existing == null)
            {
                existing = new FollowUser
                {
                    Mid = w.Mid,
                    Name = w.Name,
                    Face = w.Face,
                    IsPlaceholder = true,
                    NewCount = w.NewCount,
                    LatestPubdate = w.Pub,
                };
                Users.Insert(Math.Min(target, Users.Count), existing);
            }
            else if (existing.IsPlaceholder)
            {
                if (existing.Name != w.Name && w.Name.Length > 0) existing.Name = w.Name;
                // 头像: 服务端可能这轮才给 face(之前是空的), 有了就补上
                if (w.Face.Length > 0 && existing.Face != w.Face) existing.Face = w.Face;
                if (w.Pub > existing.LatestPubdate) existing.LatestPubdate = w.Pub;
                if (existing.NewCount != w.NewCount) existing.NewCount = w.NewCount;
                var cur = Users.IndexOf(existing);
                if (cur != target) Users.Move(cur, target);
            }
        }

        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(FollowingsSummary));
    }

    private static void ApplyNotifyState(FollowUser u)
    {
        if (u == null || u.Mid <= 0) return;
        if (!Svc.DynamicNotify.TryGetState(u.Mid, out var count, out var latestPub)) return;
        u.NewCount = count;
        // 服务里的时间比动态流里的近似时间更可靠; 只取更大值, 避免旧数据把排序顶回去。
        if (latestPub > u.LatestPubdate) u.LatestPubdate = latestPub;
        // 头像(2026-11-07): 占位行("有未读、还没被分页加载出来"的 UP)只能靠服务给头像,
        // 而它是轮询里陆续到位的 —— 上一轮可能是空串(显示默认人形), 这一轮有了就补上。
        // FollowUser.Face 带变更通知, 一赋值那一格就会自动重新绑定贴图。
        // ★ 只补空, 不覆盖非空值: 已加载行的头像来自关注列表接口, 是权威的。
        if (u.Face.Length == 0 && Svc.DynamicNotify.TryGetFace(u.Mid, out var face))
            u.Face = face;
    }

    // ------------------------------------------------------------ 左栏排序

    /// <summary>
    /// 左栏「关注的 UP」排序规则(2026-11-06 重做):
    ///   ① **有未读红点的全部在最前面** —— 这是用户最想先看到的;
    ///   ② 未读/已读各自内部按最近发布时间从新到旧;
    ///   ③ 时间相同按名字、再按原顺序, 保证刷新不抖动。
    ///
    /// ★ 旧版只按时间排, 会出现"明明有红点却排在没更新的 UP 后面" —— 这次修掉。
    /// ★ 用就地 Move 而不是换 ItemsSource: 保留选中态与滚动位置。
    /// </summary>
    private void ReorderUsersByActivity(IReadOnlyList<DynamicItem> items)
    {
        var newestPub = new Dictionary<long, long>();
        foreach (var it in items)
        {
            if (it.AuthorMid <= 0) continue;
            if (!newestPub.TryGetValue(it.AuthorMid, out var cur) || it.Pubdate > cur)
                newestPub[it.AuthorMid] = it.Pubdate;
        }

        foreach (var u in Users)
        {
            // 动态流里没出现的 UP 保留上一轮已知时间; 出现了就取 max(跨页拼接可能夹更早条目)。
            if (newestPub.TryGetValue(u.Mid, out var ts) && ts > u.LatestPubdate)
                u.LatestPubdate = ts;
        }

        var indexed = Users.Select((u, i) => (u, i)).ToList();
        var ordered = indexed
            .OrderByDescending(x => x.u.HasNew)
            .ThenByDescending(x => x.u.LatestPubdate)
            .ThenBy(x => x.u.Name, StringComparer.Ordinal)
            .ThenBy(x => x.i)
            .Select(x => x.u)
            .ToList();

        // ★ 顺序没变就直接返回: 关注 2790 人时, 每次轮询都重排会触发几千次 Move,
        //   而绝大多数轮询里顺序根本没变 —— 那是纯浪费, 也是"列表卡一下"的来源。
        var same = true;
        for (var i = 0; i < ordered.Count; i++)
            if (!ReferenceEquals(Users[i], ordered[i])) { same = false; break; }
        if (same) return;

        for (var target = 0; target < ordered.Count; target++)
        {
            var want = ordered[target];
            var cur = Users.IndexOf(want);
            if (cur == target) continue;
            Users.Move(cur, target);
        }
    }

    // ------------------------------------------------------------ 搜索已关注 UP

    /// <summary>
    /// 输入防抖 300ms 后调「搜索关注明细」接口。
    /// ★ 不做本地过滤: 2790 个关注全部加载/每次输入刷新 2790 行会直接卡死 UI。
    /// ★ async 方法 + Forget(2026-11-07): 这里已有完整 try/catch, async void 只剩
    ///   "异常逃逸进全局弹框"的形态风险 —— 保险起见收敛掉(用户输入路径触发频率最高)。
    /// </summary>
    private void StartUserSearch() => RunUserSearchAsync().Forget();

    private async Task RunUserSearchAsync()
    {
        _userSearchCts?.Cancel();
        _userSearchCts?.Dispose();
        _userSearchCts = null;

        FollowingSearchResults.Clear();
        UserSearchError = "";
        OnPropertyChanged(nameof(ShowUserSearchEmpty));

        var keyword = UserSearchText.Trim();
        if (keyword.Length == 0)
        {
            UserSearching = false;
            return;
        }
        if (_selfMid <= 0)
        {
            UserSearching = false;
            UserSearchError = "账号信息还没准备好, 请稍后再试";
            return;
        }

        // 防抖期间也显示"搜索中" —— 否则用户输入后 300ms 内一片空白, 像没反应。
        UserSearching = true;
        OnPropertyChanged(nameof(ShowUserSearchEmpty));

        var cts = new CancellationTokenSource();
        _userSearchCts = cts;
        try
        {
            await Task.Delay(300, cts.Token);
            if (cts.IsCancellationRequested) return;

            var (ok, err, list, _) = await Svc.Api.SearchFollowingsAsync(_selfMid, keyword, 1, 50);
            if (cts.IsCancellationRequested) return;

            if (!ok || list == null)
            {
                UserSearchError = err ?? "搜索失败, 请重试";
                return;
            }

            var seen = new HashSet<long>();
            foreach (var u in list)
                if (u.Mid > 0 && seen.Add(u.Mid)) FollowingSearchResults.Add(u);
            ApplyNotifyStates();
        }
        catch (OperationCanceledException)
        {
            // 输入还在继续, 这一次作废。
        }
        catch (Exception ex)
        {
            UserSearchError = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_userSearchCts, cts))
            {
                _userSearchCts = null;
                UserSearching = false;
                OnPropertyChanged(nameof(ShowUserSearchEmpty));
            }
            cts.Dispose();
        }
    }

    private void ClearUserSearch()
    {
        _userSearchCts?.Cancel();
        _userSearchCts = null;
        UserSearchText = "";       // setter 会清空结果并取消搜索
        FollowingSearchResults.Clear();
        UserSearchError = "";
        UserSearching = false;
        OnPropertyChanged(nameof(ShowUserSearchEmpty));
    }

    // ------------------------------------------------------------ 右栏加载更多

    /// <summary>右栏滚动到底 / 底部重试按钮: 追加下一页。</summary>
    public async Task LoadMoreFeedAsync()
    {
        if (Loading || LoadingMore || !HasMore) return;
        if (UseCardWall && WallItems.Count >= WallItemLimit) return;
        // 手动/自动重试前清掉上一次的错误, 让 LoadSelectionAsync 不再被它挡住。
        if (LoadMoreError) LoadMoreError = false;
        await LoadSelectionAsync(reset: false);
    }
}
