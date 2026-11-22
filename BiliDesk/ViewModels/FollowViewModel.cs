using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
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
///       - 具体某 UP: 同样是 TA 的**综合动态**(视频/图文/文字/专栏/直播/转发, feed/space 游标分页)。
///
/// ★★★ 2026-11-21 修「左栏时间与动态卡上发布时间不符」(用户截图):
///   右栏原来走 arc/search(该 UP 的**投稿视频**列表), 而左栏的"最近更新"时间来自
///   动态流(feed/nav / feed/all, **任何类型**的动态)。一个 UP 只要最近发的是直播/
///   图文/转发而不是投稿视频, 两边就必然对不上 —— 实测笨尼尼子(mid=1767542):
///   左栏显示"44分钟前"(一条直播动态), 右栏卡片却是 11-14(最新一条投稿视频), 差 7 天。
///   现在右栏改用 feed/space(该 UP 自己的动态流), 与左栏同一口径:
///   列表最上面一条就是 TA 最新的一条动态, 发布时间自然与左栏一致。
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
    ///
    /// ★★ 2026-11-20(用户报"动态页视频卡下滑加载时, 加载的那一拍画面冻结"):
    ///   整页新增仍然是一次性 Add —— 卡片墙不虚拟化, 一页十几张 VideoCard 会在同一拍
    ///   全部实例化(模板/标题换行/入场动画), 压在 Normal 优先级的加载回调里。
    ///   现在由 <see cref="SyncWallItems"/> 登记目标, 泵(<see cref="DrainWallAppends"/>)
    ///   按帧补 2~12 张, 与首页 DeferredFill 的追加分帧同一套思路。
    /// </summary>
    public ObservableCollection<VideoItem> WallItems { get; } = new();
    private const int WallItemLimit = 300;

    /// <summary>卡片墙分帧追加的目标快照(null = 没有待补内容)。</summary>
    private List<VideoItem>? _wallAppendTarget;

    /// <summary>本帧的追加任务是否已经排上(合并同一帧里的多次请求)。</summary>
    private bool _wallAppendScheduled;

    /// <summary>当前每帧追加几张; 按帧间隔自适应(见 <see cref="DrainWallAppends"/>)。</summary>
    private int _wallAppendBatch = WallAppendBatchStart;

    /// <summary>上一次追加帧的时刻(TickCount64), 用来量上一帧实际花了多久。</summary>
    private long _wallAppendLastTick;

    /// <summary>这次追加是否把 LoadingMore 撑到了追加结束(追加完要替调用方收掉)。</summary>
    private bool _wallAppendHoldsLoadingMore;

    private const int WallAppendBatchStart = 4;
    private const int WallAppendBatchMin = 2;
    private const int WallAppendBatchMax = 12;

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
    public string SelectionTitle => SelectedUp == null ? "全部动态" : SelectedUp.Name + " 的动态";

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
    private string? _upSpaceOffset;      // 单 UP: 该 UP 动态流的游标(feed/space)

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
            CancelWallAppends();
            WallItems.Clear();
            // 集合清空了, 缓存的"可见视频总数"也得跟着归零, 否则提示会留在空墙上。
            _wallVisibleVideoCount = 0;
            RefreshWallLimitHint();
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
            _upSpaceOffset = null;
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
                // ★★★ 2026-11-21: 单 UP 视图改用 feed/space(该 UP 自己的动态流),
                //   不再用 arc/search(只含投稿视频)。理由见类头注释。
                var (sOk, sErr, sItems, sNext, sHasMore) =
                    await Svc.Api.GetSpaceDynamicsAsync(forUp.Mid, reset ? null : _upSpaceOffset);
                ok = sOk; err = sErr;
                // ★ 按 pub_ts 倒序排: feed/space 会把该 UP **置顶的那条**放在最前面
                //   (实测笨尼尼子的第一条是 3月28日的置顶图文, 真正最新的是 5 小时前的
                //    直播动态)。不排的话最上面一张卡的时间又和左栏对不上了。
                //   ★★ 只排"这一页"不够 —— 置顶那条会被排到本页末尾, 而下一页的条目
                //     比它新, 于是它插在了中间。所以真正落地时还要按时间**插入**到
                //     FeedItems 的正确位置(见下面的 InsertByPubdateDescending)。
                items = sItems != null
                    ? sItems.OrderByDescending(x => x.Pubdate).ToList()
                    : null;

                // 与「全部动态」同一套判据: has_more + 游标必须真的前进,
                // 否则重复页会让底部永远转圈。
                hasMore = ok && sHasMore && !string.IsNullOrEmpty(sNext) && sNext != _upSpaceOffset;
                if (ok) _upSpaceOffset = sNext;

                // ★ 头像/名字兜底(2026-10-15 用户截图: 卡片上一个头像都没有、名字也空):
                //   feed/space 正常会带 module_author.name/face, 但接口偶有缺字段,
                //   缺了就用左栏这一行上现成的 —— 不必为此再发请求, 更不该让卡片空着。
                if (items != null)
                {
                    foreach (var it in items)
                    {
                        if (string.IsNullOrEmpty(it.AuthorName)) it.AuthorName = forUp.Name;
                        if (string.IsNullOrEmpty(it.AuthorFace)) it.AuthorFace = forUp.Face;
                    }
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
                // ★ 单 UP 视图(2026-11-21): feed/space 的第一条可能是**置顶**的老动态,
                //   只在"页内排序"会把它排到本页末尾, 而下一页的条目其实比它新 ——
                //   所以这里按 pub_ts 插入到正确位置, 保证整条列表始终是时间倒序,
                //   最上面那张卡的时间才和左栏一致。
                //   全部动态(feed/all)本身就是倒序, 走原来的追加即可。
                var insertSorted = forUp != null;
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

                    if (insertSorted) InsertByPubdateDescending(it);
                    else FeedItems.Add(it);
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

                // ★★★ 2026-11-21: 单 UP 视图把「TA 最新一条动态的时间」回写给左栏。
                //   左栏的"最近更新"只由 feed/nav(每 60 秒)与 feed/all 维护, 而那两条流
                //   都**不是全量**: 实测松哥讲化学刚开播时, 直播动态既不在 feed/nav
                //   也不在 feed/all 前几页, 左栏就还停在 3 小时前的投稿上, 右栏却已经是
                //   1 小时前的直播 —— 又变成"两侧不符"。
                //   右栏这次拿到的就是该 UP 自己的动态流(feed/space), 是权威的;
                //   用它把左栏时间抬上去, 两个数字必然一致。
                //   ★ 只增不减: 与 ApplyNotifyState / ReorderUsersByActivity 的"取更大值"同一口径,
                //     免得翻页翻到更早的条目把左栏时间改小。
                if (forUp != null)
                {
                    var newest = FeedItems.Select(x => x.Pubdate).DefaultIfEmpty(0).Max();
                    if (newest > forUp.LatestPubdate) forUp.LatestPubdate = newest;
                }
                if (UseCardWall)
                {
                    var wallPending = SyncWallItems();
                    // ★ 滚动加载的追加还没铺完: 把 LoadingMore 撑到泵结束 ——
                    //   页面的自动翻页闸门看的就是它, 不然用户滑到"暂时只铺了一半"的墙底
                    //   会立刻再发一页, 两批追加叠在一起。
                    if (wallPending && !reset)
                    {
                        _wallAppendHoldsLoadingMore = true;
                        LoadingMore = true;
                    }
                }
                RefreshWallLimitHint();

                if (FeedItems.Count == 0)
                    Error = forUp == null ? "关注的 UP 最近没有动态" : "该 UP 暂时没有动态";

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
    private bool _showWallLimitHint;

    /// <summary>FeedItems 里"视频动态且没被屏蔽"的条数 —— 卡片墙的目标总量, 可能超过 300。
    /// 由 <see cref="SyncWallItems"/> 遍历时数一次, 提示刷新就不用再扫集合。</summary>
    private int _wallVisibleVideoCount;

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
            RefreshWallLimitHint();
        }
    }

    /// <summary>卡片墙超过 300 条时的提示(避免用户以为后面没有内容了)。
    /// ★ 卡片墙只收视频, 所以按 FeedItems 里的视频动态数比较, 不是总条数。
    /// ★ 2026-11-20: 只数**当前没被屏蔽**的 —— 卡片墙自己过滤了, 被屏蔽的不进墙。
    /// ★ 2026-11-22: 改成缓存值。以前是计算属性, 分帧补卡时每帧都发一次通知、两个绑定各求值
    ///   一次, 等于每帧全表扫描 FeedItems; 现在可见总数由 <see cref="SyncWallItems"/> 数好存进
    ///   <c>_wallVisibleVideoCount</c>, 这里只做 O(1) 比较。
    ///   注意不能换成"墙的目标数 &gt; 已落地数": 目标被 WallItemLimit(300) 截断, 超过 300 的场景
    ///   那样会丢提示。</summary>
    public bool ShowWallLimitHint
    {
        get => _showWallLimitHint;
        private set => SetProperty(ref _showWallLimitHint, value);
    }

    private void RefreshWallLimitHint() => ShowWallLimitHint = UseCardWall &&
        _wallVisibleVideoCount > WallItems.Count;

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
    ///
    /// ★★ 2026-11-20: append 不再当场逐条 Add, 而是登记目标后交给
    ///   <see cref="PumpWallAppends"/> 按帧补(见 WallItems 的说明)。
    ///   返回 true 表示还有卡片没落地(调用方据此决定是否把 LoadingMore 撑到泵结束)。
    /// </summary>
    private bool SyncWallItems()
    {
        // DynamicItem.AsVideoItem() 内部缓存, 同一条动态每次返回同一个 VideoItem 引用,
        // 下面的首尾引用比对才稳定。
        var videos = new List<VideoItem>(WallItemLimit);
        var visible = 0;
        foreach (var it in FeedItems)
        {
            if (!it.HasAnyVideo) continue;
            // ★ 2026-11-20: 卡片墙**不再挂 view.Filter** —— 它不虚拟化, view.Refresh 一发 Reset
            //   就会把几百张 VideoCard 在同一个回调里销毁重建(用户报的"屏蔽后卡 1 秒")。
            //   这里按同一套全局屏蔽条件自己过滤, 规则变化时只对差异做增删(见 ReconcileWall)。
            if (!FilterService.Instance.IsVisible(it)) continue;
            // ★ 计数不跟着 300 条截断: 提示要回答的是"还有没有没显示的视频"。
            visible++;
            if (videos.Count >= WallItemLimit) continue;
            var v = it.AsVideoItem();
            if (v != null) videos.Add(v);
        }
        _wallVisibleVideoCount = visible;

        if (videos.Count == 0)
        {
            CancelWallAppends();
            if (WallItems.Count > 0) WallItems.Clear();
            RefreshWallLimitHint();
            return false;
        }

        // 已有内容有变化(屏蔽/取消屏蔽): 先按差集摘掉/插回, 不整墙 Clear。
        if (WallItems.Count > 0)
        {
            var desired = new HashSet<VideoItem>(videos);
            var survivors = 0;
            foreach (var w in WallItems)
                if (desired.Contains(w)) survivors++;

            // 一半以上旧卡都不在新目标里 = 整批换掉(刷新/换 UP), 清空后分帧重铺
            if (survivors * 2 < WallItems.Count)
            {
                CancelWallAppends();
                WallItems.Clear();
            }
            else
            {
                ReconcileWall(videos, desired);
            }
        }

        // 剩下的尾部仍走分帧追加(新翻一页 / 首屏)
        _wallAppendTarget = WallItems.Count < videos.Count ? videos : null;
        PumpWallAppends();
        RefreshWallLimitHint();
        return _wallAppendTarget != null;
    }

    /// <summary>
    /// 屏蔽规则变化后重算卡片墙。只做增量增删, 不重建整墙; 没在用卡片墙时留给下次切换。
    /// </summary>
    public void RefreshWallFilter()
    {
        if (!UseCardWall) return;
        SyncWallItems();
    }

    /// <summary>一次同步补回这么多张以上就改走"清空 + 分帧重铺", 免得又造出一次长冻结。</summary>
    private const int MaxWallInserts = 6;

    /// <summary>
    /// 把卡片墙收敛到新的目标列表：只摘掉被屏蔽的卡、插回恢复可见的卡，其余容器原地不动。
    ///
    /// ★ 前提: 墙与目标有超过一半的卡片是同一批对象(否则外面已经走清空重铺)。
    /// ★ 调用前 <paramref name="desired"/> 已建好; 插入的数量超过 <see cref="MaxWallInserts"/>
    ///   时会放弃增量, 改为清空后交给分帧泵 —— 取消一大批屏蔽时仍会分帧重铺。
    /// </summary>
    private void ReconcileWall(List<VideoItem> videos, HashSet<VideoItem> desired)
    {
        for (var i = WallItems.Count - 1; i >= 0; i--)
            if (!desired.Contains(WallItems[i])) WallItems.RemoveAt(i);

        // 尾部新增(翻页)交给分帧泵, 不要在这里同步实例化一整页
        if (IsPrefix(WallItems, videos)) return;

        if (!OrderCompatible(WallItems, videos) || videos.Count - WallItems.Count > MaxWallInserts)
        {
            CancelWallAppends();
            WallItems.Clear();
            return;
        }

        var j = 0;
        for (var i = 0; i < videos.Count; i++)
        {
            if (j < WallItems.Count && ReferenceEquals(WallItems[j], videos[i]))
            {
                j++;
                continue;
            }
            var v = videos[i];
            BiliDesk.Views.Controls.VideoCard.MarkNoEntranceAnimation(v);
            WallItems.Insert(j, v);
            j++;
        }
    }

    /// <summary>墙是不是目标列表的前缀(最常见: 翻页只是往尾巴追)。</summary>
    private static bool IsPrefix(IList<VideoItem> wall, List<VideoItem> videos)
    {
        if (wall.Count > videos.Count) return false;
        for (var i = 0; i < wall.Count; i++)
            if (!ReferenceEquals(wall[i], videos[i])) return false;
        return true;
    }

    /// <summary>墙上保留的条目在目标列表里是否保持同序(顺序全变 = 换了一批内容)。</summary>
    private static bool OrderCompatible(IList<VideoItem> wall, List<VideoItem> videos)
    {
        var j = 0;
        for (var i = 0; i < wall.Count; i++)
        {
            while (j < videos.Count && !ReferenceEquals(videos[j], wall[i])) j++;
            if (j >= videos.Count) return false;
            j++;
        }
        return true;
    }

    /// <summary>
    /// 排一次卡片墙追加帧(同一帧只排一次)。Background 优先级 = 渲染之后再补几张,
    /// 让滚动/输入始终排在前面。
    /// </summary>
    private void PumpWallAppends()
    {
        if (_wallAppendScheduled || _wallAppendTarget == null) return;
        _wallAppendScheduled = true;
        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(
            DispatcherPriority.Background, new Action(DrainWallAppends));
    }

    /// <summary>
    /// 把待补的视频卡按**这一帧的批量**搬进卡片墙。
    ///
    /// ★★ 为什么要分帧(2026-11-20, 用户报"动态页视频卡下滑加载时, 加载的那一拍画面冻结"):
    ///   卡片墙(CardWallPanel)不虚拟化, 一次 Add 一整页 = 一次布局同步实例化十几张
    ///   VideoCard(模板 + 标题换行测量 + 入场动画), 全部压在加载回调那一拍里。
    ///   改成每帧 2~12 张后, 每次布局只创建几张, 滚动线程保持响应。
    /// ★ 批量按**帧间隔**自适应(与首页 DeferredFill 同一套): 间隔 >40ms 说明这机器/
    ///   这个构建撑不住, 对半砍; <20ms 再慢慢加回来。Debug 构建会自动收敛到 2~4 张/帧。
    /// ★ 追加的卡不播入场淡入(先 MarkNoEntranceAnimation 再 Add): 一页十几张同时起
    ///   360ms 动画, 合成器那一下也是卡顿来源之一; 首屏/切 Tab 那批仍照旧播。
    /// </summary>
    private void DrainWallAppends()
    {
        _wallAppendScheduled = false;
        var target = _wallAppendTarget;
        if (target == null) return;

        var now = Environment.TickCount64;
        if (_wallAppendLastTick != 0)
        {
            var delta = now - _wallAppendLastTick;
            if (delta > 40) _wallAppendBatch = Math.Max(WallAppendBatchMin, _wallAppendBatch / 2);
            else if (delta < 20) _wallAppendBatch = Math.Min(WallAppendBatchMax, _wallAppendBatch + 1);
        }
        _wallAppendLastTick = now;

        var added = 0;
        while (added < _wallAppendBatch && WallItems.Count < target.Count)
        {
            var v = target[WallItems.Count];
            BiliDesk.Views.Controls.VideoCard.MarkNoEntranceAnimation(v);
            WallItems.Add(v);
            added++;
        }

        RefreshWallLimitHint();

        if (WallItems.Count < target.Count)
        {
            PumpWallAppends();   // 还有存货, 下一帧继续
            return;
        }

        _wallAppendTarget = null;
        _wallAppendLastTick = 0;
        EndWallAppendHold();
    }

    /// <summary>停掉分帧追加(刷新/换选择/退出登录): 目标作废, 转圈状态由我们收掉。</summary>
    private void CancelWallAppends()
    {
        _wallAppendTarget = null;
        _wallAppendBatch = WallAppendBatchStart;
        _wallAppendLastTick = 0;
        EndWallAppendHold();
    }

    /// <summary>追加结束时替调用方收掉 LoadingMore(只收我们自己撑起来的那个)。</summary>
    private void EndWallAppendHold()
    {
        if (!_wallAppendHoldsLoadingMore) return;
        _wallAppendHoldsLoadingMore = false;
        LoadingMore = false;
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

        // ★ 占位行数量可能上百, 原来每扫一行都要再遍历 want / 整个 Users —— O(n²)。
        //   两个索引把查找压到 O(1), 列表本身的 Move 次数没变(未读数远小于关注总数)。
        var byMid = new Dictionary<long, FollowUser>();
        foreach (var u in Users)
            if (u.Mid > 0 && !byMid.ContainsKey(u.Mid)) byMid.Add(u.Mid, u);

        var want = new List<(long Mid, string Name, string Face, int NewCount, long Pub)>();
        var wantMids = new HashSet<long>();
        foreach (var s in snapshot)
        {
            if (loadedMids.Contains(s.Mid) || s.NewCount <= 0) continue;
            want.Add(s);
            wantMids.Add(s.Mid);
        }

        // 先删掉不再需要的占位行(已读 / 已被真实数据取代)
        // ★ 正在选中的那一行不删: 用户点开一个"未读占位行"的瞬间它就已经已读了,
        //   立刻删掉会让左栏选中高亮凭空消失, 右栏却还在显示 TA 的视频 —— 像点错了。
        //   它会在用户切到别的 UP / 下一次轮询时自然消失。
        for (var i = Users.Count - 1; i >= 0; i--)
        {
            var u = Users[i];
            if (!u.IsPlaceholder || u == SelectedUp) continue;
            if (wantMids.Contains(u.Mid)) continue;
            Users.RemoveAt(i);
            if (u.Mid > 0) byMid.Remove(u.Mid);
        }

        // 再按快照顺序补/更新(占位行一律在最前面)
        for (var target = 0; target < want.Count; target++)
        {
            var w = want[target];
            byMid.TryGetValue(w.Mid, out var existing);
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
                byMid.Add(w.Mid, existing);
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

    /// <summary>
    /// 按发布时间倒序把一条动态插进 <see cref="FeedItems"/>。
    ///
    /// ★ 2026-11-21 单 UP 视图专用: feed/space 会把该 UP 的**置顶动态**排在第一条,
    ///   而它是很早以前发的。页内排序只能把它挪到本页末尾, 下一页的条目却比它新 ——
    ///   只有按 pub_ts 插入才对得上"列表从上到下 = 时间从新到旧"。
    ///   常见情形(翻页拿到的都是更早的)退化成 Append, 代价是 O(1)。
    /// </summary>
    private void InsertByPubdateDescending(DynamicItem item)
    {
        if (FeedItems.Count == 0 || FeedItems[^1].Pubdate >= item.Pubdate)
        {
            FeedItems.Add(item);
            return;
        }
        for (var i = FeedItems.Count - 1; i >= 0; i--)
        {
            if (FeedItems[i].Pubdate < item.Pubdate) continue;
            FeedItems.Insert(i + 1, item);
            return;
        }
        FeedItems.Insert(0, item);
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
