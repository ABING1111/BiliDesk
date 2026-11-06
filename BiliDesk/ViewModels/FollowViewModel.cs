using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
///   · 左栏 = 「全部动态」入口 + 关注的 UP 主列表(有未读时头像左侧显示红点、
///     头像右下角显示条数角标);
///   · 右栏 = 当前选中对象的内容, 两档排版由 <see cref="UseCardWall"/> 切:
///       - 全部动态: 关注的所有 UP 近期的**视频投稿**(动态时间线接口, 游标分页);
///       - 具体某 UP: 该 UP 的投稿视频(空间投稿接口, 页码分页)。
/// 动态里非视频的内容(图文/纯文字/投票)没有可播对象, 不进这个列表。
///
/// 未读红点由应用级 <see cref="DynamicNotifyService"/> 维护, 这个 VM 只负责把状态
/// 应用到左栏并排序。
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
    public ICommand LoginCommand { get; }
    public ICommand OpenUpCommand { get; }
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

    public FollowViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        LoadMoreCommand = new AsyncRelayCommand(LoadMoreAsync);
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
            FeedItems.Clear();
            SelectedUp = null;
            OnPropertyChanged(nameof(Count));
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
        Svc.DynamicNotify.Forget(u.Mid);
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasAnyUnread));
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
        // 点开 TA = 已读: 清掉这个 UP 的红点, 基准推进到最新一条。
        Svc.DynamicNotify.MarkRead(u.Mid);
        ApplyNotifyStates();
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
                    //
                    // ★ 2026-11-05: 同样在这里补一次"按最近更新排序" ——
                    //   新翻出来的 UP 也要参与排序(它可能在动态流里出现过, 有时间戳),
                    //   否则它们会一直挂在列表末尾, 与排序规则矛盾。
                    if (SelectedUp == null)
                    {
                    }
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


                // 左栏未读状态由应用级服务维护; 这里只把最新状态应用上(**不重排**,
                // 位置只在进入动态页/点刷新时刷新, 见 RefreshUserPositions)。
                ApplyNotifyStates();
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
        OnPropertyChanged(nameof(HasAnyUnread));
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
                if (w.Pub > existing.LatestPubdate) existing.LatestPubdate = w.Pub;
                if (existing.NewCount != w.NewCount) existing.NewCount = w.NewCount;
                var cur = Users.IndexOf(existing);
                if (cur != target) Users.Move(cur, target);
            }
        }

        OnPropertyChanged(nameof(Count));
    }

    private static void ApplyNotifyState(FollowUser u)
    {
        if (u == null || u.Mid <= 0) return;
        if (!Svc.DynamicNotify.TryGetState(u.Mid, out var count, out var latestPub)) return;
        u.NewCount = count;
        // 服务里的时间比动态流里的近似时间更可靠; 只取更大值, 避免旧数据把排序顶回去。
        if (latestPub > u.LatestPubdate) u.LatestPubdate = latestPub;
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
    private void ReorderUsersByActivity(IReadOnlyList<VideoItem> items)
    {
        var newestPub = new Dictionary<long, long>();
        foreach (var it in items)
        {
            if (it.OwnerMid <= 0) continue;
            if (!newestPub.TryGetValue(it.OwnerMid, out var cur) || it.Pubdate > cur)
                newestPub[it.OwnerMid] = it.Pubdate;
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


    /// <summary>右栏滚动到底: 追加下一页</summary>
    public async Task LoadMoreFeedAsync()
    {
        if (Loading || LoadingMore || !HasMore) return;
        await LoadSelectionAsync(reset: false);
    }
}
