using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
///   · 左栏 = 「全部动态」入口 + 关注的 UP 主列表(有未读时头像左侧显示红点);
///   · 右栏 = 当前选中对象的内容, 两档排版由 <see cref="UseCardWall"/> 切:
///       - 全部动态: 关注的所有 UP 近期的**视频投稿**(动态时间线接口, 游标分页);
///       - 具体某 UP: 该 UP 的投稿视频(空间投稿接口, 页码分页)。
/// 动态里非视频的内容(图文/纯文字/投票)没有可播对象, 不进这个列表。
///
/// 未读红点的判断在 <see cref="UpdateUnreadBadges"/> —— 本地比对, 不额外请求接口。
/// </summary>
public class FollowViewModel : ObservableObject
{
    private bool _loading;
    private bool _loadingMore;
    private bool _hasMore = true;
    private string _error = "";
    private long _selfMid;
    private int _followingPage = 1;      // 左栏 UP 列表的翻页

    public ObservableCollection<FollowUser> Users { get; } = new();

    /// <summary>右栏内容(视频卡片)。切选择时整体换血。</summary>
    public ObservableCollection<VideoItem> FeedItems { get; } = new();

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

    public bool Loading { get => _loading; private set => SetProperty(ref _loading, value); }

    /// <summary>
    /// 整页加载遮罩只在"右栏还没有任何内容"时显示。
    /// 切 UP / 回全部动态时右栏换血, 若还整页盖 spinner 会闪烁 —— 那种情况
    /// 让旧内容留着、右栏自己呈现加载状态更稳。
    /// </summary>
    public bool ShowFullLoading => Loading && FeedItems.Count == 0;
    public bool LoadingMore { get => _loadingMore; private set => SetProperty(ref _loadingMore, value); }
    public bool HasMore { get => _hasMore; private set => SetProperty(ref _hasMore, value); }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }
    public int Count => Users.Count;
    public bool NotLoggedIn => !Svc.Session.HasLogin;

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
    public ICommand LoginCommand { get; }
    public ICommand OpenUpCommand { get; }

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

    public FollowViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        LoadMoreCommand = new AsyncRelayCommand(LoadMoreAsync);
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
    }

    private void OnSessionChanged()
    {
        OnPropertyChanged(nameof(NotLoggedIn));
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
        OnPropertyChanged(nameof(Count));
        if (SelectedUp?.Mid == u.Mid) SelectedUp = null;
    }

    /// <summary>选中「全部动态」</summary>
    public Task SelectAllAsync()
    {
        SelectedUp = null;
        return LoadSelectionAsync(reset: true);
    }

    /// <summary>选中某个 UP</summary>
    public Task SelectUpAsync(FollowUser u)
    {
        if (u == null || u.Mid <= 0) return Task.CompletedTask;
        SelectedUp = u;
        return LoadSelectionAsync(reset: true);
    }

    public async Task RefreshAsync()
    {
        Loading = true;
        Error = "";
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

            var (ok, err, list) = await Svc.Api.GetFollowingsAsync(_selfMid, 1, 30);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (list != null)
                {
                    Users.Clear();
                    foreach (var u in list) Users.Add(u);
                    _followingPage = 1;
                    OnPropertyChanged(nameof(Count));
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
        }
        catch (Exception ex)
        {
            Loading = false;
            Error = ex.Message;
        }
    }

    /// <summary>左栏翻页: 关注列表一页 30 个。只补列表, 不动右栏。</summary>
    public async Task LoadMoreAsync()
    {
        if (LoadingMore || Loading || _selfMid <= 0) return;
        LoadingMore = true;
        try
        {
            var (ok, err, list) = await Svc.Api.GetFollowingsAsync(_selfMid, _followingPage + 1, 30);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                LoadingMore = false;
                if (list != null && list.Count > 0)
                {
                    _followingPage++;
                    var seen = new HashSet<long>();
                    foreach (var u in Users) seen.Add(u.Mid);
                    foreach (var u in list)
                        if (u.Mid > 0 && seen.Add(u.Mid)) Users.Add(u);
                    OnPropertyChanged(nameof(Count));

                    // ★ 左栏翻出新 UP 后要立刻按已缓存的动态流补算一次红点。
                    //   以前只 Add 用户、不跑 UpdateUnreadBadges —— 关注了 30+ 人的账号
                    //   往下滚出来的 UP 永远 NewCount=0, 明明有新投稿也不亮红点。
                    //   只在「全部动态」下算: 单 UP 视图的 FeedItems 只有该 UP 的投稿,
                    //   拿来给别的 UP 判新旧会把基准写错。
                    if (SelectedUp == null) UpdateUnreadBadges();
                }
            });
        }
        catch (Exception ex)
        {
            LoadingMore = false;
            Error = ex.Message;
        }
    }

    // ------------------------------------------------------------ 右栏内容

    /// <summary>按当前选择加载右栏。reset = 清空重来; false = 追加下一页</summary>
    private async Task LoadSelectionAsync(bool reset)
    {
        if (Loading) return;
        if (reset)
        {
            Loading = true;
            FeedItems.Clear();
            _seenBvid.Clear();
            Error = "";
            OnPropertyChanged(nameof(ShowFullLoading));
        }
        else
        {
            if (LoadingMore || !HasMore) return;
            LoadingMore = true;
        }

        var forUp = SelectedUp;         // 快照: 期间用户又点了别人时, 结果作废
        bool ok;
        string? err = null;
        List<VideoItem>? items = null;
        var hasMore = false;

        try
        {
            if (forUp == null)
            {
                if (reset) _feedOffset = null;
                var (rOk, rErr, rItems, nextOffset, rHasMore) =
                    await Svc.Api.GetDynamicFeedAsync(reset ? null : _feedOffset);
                ok = rOk; err = rErr; items = rItems;
                // 翻页依据: "服务端给了新 offset 且与当前不同", 原样回传 = 到底了。
                // ★ 为什么不用接口的 has_more(2026-10-17 修正): 原来这里的理由是"has_more 实测不可靠,
                //   第一页标 false 却还有下一页" —— 那其实是**解析 bug**造成的: 接口给的是
                //   bool(true/false), 而解析按 Number 读(恒为 false)。现在解析已认 bool,
                //   has_more 是准的(探针 6 页全是 true)。
                //   仍然保留 offset 这套判据: 它不依赖任何接口字段的语义, 且顺便挡住
                //   "offset 原样回传却还标着 has_more=true"那类自相矛盾的响应。
                hasMore = ok && !string.IsNullOrEmpty(nextOffset) && nextOffset != _feedOffset;
                if (ok) _feedOffset = nextOffset;
            }
            else
            {
                if (reset) _upVideoPage = 1;
                var r = await Svc.Api.GetSpaceVideosAsync(forUp.Mid, _upVideoPage, 30);
                ok = r.Item1; err = r.Item2; items = r.Item3;
                hasMore = items != null && items.Count >= 30;
                if (ok) _upVideoPage++;

                // ★ 单 UP 视图的**头像与名字要在这里盖章**(2026-10-15 用户截图: 卡片上一个头像
                //   都没有、名字也是空的, 只剩"· 10-14 18:45"):
                //   空间投稿的两个接口**都不下发头像**; 风控降级接口连 UP 名都没有(只有 upMid)。
                //   但这两样我们本来就知道 —— 左侧那一条 UP 上就摆着 —— 抄过去即可,
                //   不必为此再发请求, 更不该让卡片空着。
                if (items != null)
                {
                    foreach (var it in items)
                    {
                        if (string.IsNullOrEmpty(it.Author)) it.Author = forUp.Name;
                        if (string.IsNullOrEmpty(it.AuthorFace)) it.AuthorFace = forUp.Face;
                    }
                }
            }

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Loading = false;
                LoadingMore = false;
                if (SelectedUp != forUp) return;   // 选择已变, 丢弃本次结果
                if (items == null)
                {
                    if (FeedItems.Count == 0) Error = err ?? "加载失败";
                    return;
                }
                var added = 0;
                foreach (var it in items)
                    if (!string.IsNullOrEmpty(it.Bvid) && _seenBvid.Add(it.Bvid))
                    {
                        FeedItems.Add(it);
                        added++;
                    }
                HasMore = hasMore;
                OnPropertyChanged(nameof(ShowFullLoading));
                if (FeedItems.Count == 0)
                    Error = forUp == null ? "关注的 UP 最近没有视频投稿" : "该 UP 暂时没有投稿视频";
                // 未读红点: 看"全部动态"时刷新各 UP 的红点; 点开某个 UP 就清掉 TA 的红点。
                // ★ 放在这里(而不是刷新入口)是因为**只有拿到这一页条目才能判断**:
                //   红点靠"该 UP 最新一条"与本地基准比对, 没有列表就无从比。
                if (forUp == null) UpdateUnreadBadges();
                else ClearUnreadBadge(forUp);
            });
        }
        catch (Exception ex)
        {
            Loading = false;
            LoadingMore = false;
            Error = ex.Message;
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
        }
    }

    // ------------------------------------------------------------ 左栏未读红点

    /// <summary>红点轻量刷新在途(定时器与页面切回可能同时触发)</summary>
    private bool _unreadRefreshBusy;

    /// <summary>
    /// 只刷新左栏未读红点, **不动右栏列表**。
    ///
    /// 用于两个场景:
    ///   · 从别的页面切回动态页(页面实例被缓存, FeedItems 还是上次的);
    ///   · FollowPage 的定时轮询。
    ///
    /// ★ 为什么不直接复用 LoadSelectionAsync: 那会清空 FeedItems 并弹整页 loading,
    ///   用户只是切回来瞄一眼红点, 不该被强制回到列表顶部; 这里单独拉第一页动态就够了。
    /// </summary>
    public async Task RefreshUnreadBadgesAsync()
    {
        if (!Svc.Session.HasLogin || _unreadRefreshBusy || Users.Count == 0) return;
        _unreadRefreshBusy = true;
        try
        {
            var (ok, _, items, _, _) = await Svc.Api.GetDynamicFeedAsync(null);
            if (!ok || items == null) return;   // 静默: 红点刷新失败不影响页面
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => UpdateUnreadBadges(items));
        }
        catch
        {
            // 静默(同上)
        }
        finally
        {
            _unreadRefreshBusy = false;
        }
    }

    /// <summary>
    /// 「全部动态」加载完 → 按这一页的条目刷新左栏各 UP 的未读红点。
    ///
    /// 规则(基准存在 <see cref="DynamicSeenStore"/>, 本地判断、不请求接口):
    ///   · 没有基准的 UP: 只把 TA 当前最新一条记成基准, **不显示红点**(见该类的说明);
    ///   · 基准与最新一条相同: 红点消失;
    ///   · 不同: NewCount = 这一页里排在基准那条**之前**的该 UP 投稿数
    ///     (基准那条已经翻出本页时, 本页所有该 UP 的条目都算新的)。
    ///     界面只拿它判断红点亮不亮, 具体数字放在红点的悬浮提示里。
    /// ★ 本页没有出现的 UP **不动**: 动态接口只给近期条目, 一个 UP 最近没投稿就无从判断,
    ///   这时把红点清掉是凭空断言"没更新"。宁可留着旧红点, 也不给错的信息。
    /// </summary>
    private void UpdateUnreadBadges() => UpdateUnreadBadges(FeedItems);

    /// <summary>用指定的动态条目集合刷新红点(定时刷新时传临时列表, 不碰 FeedItems)</summary>
    private void UpdateUnreadBadges(IReadOnlyList<VideoItem> items)
    {
        // 每个 UP 在这一页里最新的一条(动态流是按时间倒序拼的, 先遇到的就是最新)
        var newest = new Dictionary<long, string>();
        foreach (var it in items)
        {
            if (it.OwnerMid <= 0 || string.IsNullOrEmpty(it.Bvid)) continue;
            if (!newest.ContainsKey(it.OwnerMid)) newest[it.OwnerMid] = it.Bvid;
        }

        foreach (var u in Users)
        {
            if (!newest.TryGetValue(u.Mid, out var top)) continue;

            if (!DynamicSeenStore.TryGet(u.Mid, out var seen))
            {
                DynamicSeenStore.Set(u.Mid, top);
                u.NewCount = 0;
                continue;
            }
            if (seen == top)
            {
                u.NewCount = 0;
                continue;
            }

            var n = 0;
            foreach (var it in items)
            {
                if (it.OwnerMid != u.Mid) continue;
                if (it.Bvid == seen) break;   // 追到上次看过的那条为止
                n++;
            }
            u.NewCount = n;
        }
        DynamicSeenStore.Flush();
    }

    /// <summary>点开某个 UP(右栏只看 TA 的投稿) = 已读: 清红点, 并把基准推到 TA 当前最新一条</summary>
    private void ClearUnreadBadge(FollowUser u)
    {
        u.NewCount = 0;
        foreach (var it in FeedItems)
        {
            if (it.OwnerMid != u.Mid || string.IsNullOrEmpty(it.Bvid)) continue;
            DynamicSeenStore.Set(u.Mid, it.Bvid);
            break;   // FeedItems 是时间倒序, 第一条就是最新
        }
        DynamicSeenStore.Flush();
    }

    /// <summary>右栏滚动到底: 追加下一页</summary>
    public async Task LoadMoreFeedAsync()
    {
        if (Loading || LoadingMore || !HasMore) return;
        await LoadSelectionAsync(reset: false);
    }
}
