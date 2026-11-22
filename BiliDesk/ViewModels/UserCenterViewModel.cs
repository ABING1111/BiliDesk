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

/// <summary>「我的空间」页的三个 Tab。</summary>
public enum UserCenterTab
{
    /// <summary>动态(综合动态流: 视频 / 图文 / 文字 / 专栏 / 转发)</summary>
    Dynamics = 0,
    /// <summary>关注(TA 关注的 UP 主)</summary>
    Followings = 1,
    /// <summary>粉丝(关注 TA 的人)</summary>
    Followers = 2,
}

/// <summary>
/// 「我的空间」ViewModel(2026-11-18 新增, issue #90)。
///
/// 一个页面三个 Tab: 动态 / 关注 / 粉丝 —— 看的是**同一个 mid** 的三份内容。
/// 「我的」页点三个数字进来、UP 主主页点关注数/粉丝数进来, 都用这一个 VM
/// (区别只是 mid 不同), 所以两处入口的行为完全一致。
///
/// ★ 数据源:
///   动态   = /x/polymer/web-dynamic/v1/feed/space (host_mid, **游标** offset 分页)
///   关注   = /x/relation/followings (页码分页, 每页 50, total 权威)
///   粉丝   = /x/relation/followers (页码分页, 每页 20, total 权威)
///
/// ★★★ 三个必须遵守的坑(都是本仓库里已经踩过的, 别重新踩):
///
///   ① **加载状态一律用 finally 收口**。
///      中途抛异常时若把 Loading 留在 true, 页面上的菊花会永远转,
///      而且 `if (Loading) return` 会把"加载更多"挡死 —— 用户自己没法救回来。
///
///   ② **世代号(_gen)丢弃过期响应**。
///      快速连点 Tab / 点刷新时, 旧请求回来会把新内容盖掉。
///      世代号是最小、最确定的解法(与 UserSpaceViewModel._loadGen 同款)。
///
///   ③ **"到底"判据用接口给的权威值, 不用"本页是否满页"**。
///      · 关注/粉丝: 用接口的 total;
///      · 动态: 用 has_more **且** offset 确实前进 —— 只看 has_more 时,
///        接口返回重复页就会一直认为"还有下一页", 底部转圈永远不消失。
/// </summary>
public class UserCenterViewModel : ObservableObject
{
    // ---------------- Tab 状态 ----------------

    private UserCenterTab _tab = UserCenterTab.Dynamics;

    /// <summary>当前 Tab。</summary>
    public UserCenterTab Tab
    {
        get => _tab;
        private set
        {
            if (!SetProperty(ref _tab, value)) return;
            OnPropertyChanged(nameof(IsDynamicsTab));
            OnPropertyChanged(nameof(IsFollowingsTab));
            OnPropertyChanged(nameof(IsFollowersTab));
            OnPropertyChanged(nameof(TitleText));
            OnPropertyChanged(nameof(SummaryText));
        }
    }

    public bool IsDynamicsTab => Tab == UserCenterTab.Dynamics;
    public bool IsFollowingsTab => Tab == UserCenterTab.Followings;
    public bool IsFollowersTab => Tab == UserCenterTab.Followers;

    // ---------------- 主体信息 ----------------

    private long _mid;
    private string _name = "";
    private string _face = "";
    private bool _isSelf;

    /// <summary>看的是谁的空间。</summary>
    public long Mid { get => _mid; private set => SetProperty(ref _mid, value); }

    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public string Face { get => _face; private set => SetProperty(ref _face, value); }

    /// <summary>页面标题: 看自己叫「我的空间」, 看别人叫「XX 的空间」。</summary>
    public string TitleText => Name.Length > 0 ? Name + " 的空间" : "我的空间";

    /// <summary>这个空间是不是"我自己"(关注搜索只在看自己时显示)。</summary>
    public bool IsSelf
    {
        get => _isSelf;
        private set
        {
            if (!SetProperty(ref _isSelf, value)) return;
            // 搜索态本身也依赖 IsSelf: 看别人时不能把普通关注列表顶掉。
            OnPropertyChanged(nameof(HasFollowingsSearch));
            OnPropertyChanged(nameof(ShowFollowingsSearchEmpty));
            OnPropertyChanged(nameof(SummaryText));

            if (!value && !string.IsNullOrWhiteSpace(_followingsSearchText))
            {
                // 搜索框在别人的空间是隐藏的; 留着文本会让搜索结果区顶掉普通列表。
                FollowingsSearchText = "";
            }
            else if (value && !string.IsNullOrWhiteSpace(_followingsSearchText))
            {
                // VM 复用时把已有搜索重新跑一次, 避免停在上一次留下的空结果上。
                StartFollowingsSearch();
            }
        }
    }

    // ---------------- 动态 Tab ----------------

    private bool _dynamicsLoading;
    private bool _dynamicsLoadingMore;
    private bool _dynamicsHasMore = true;
    private string _dynamicsError = "";
    private string? _dynamicsOffset;

    /// <summary>动态流(游标分页)。</summary>
    public ObservableCollection<DynamicItem> Dynamics { get; } = new();

    public bool DynamicsLoading
    {
        get => _dynamicsLoading;
        private set
        {
            if (SetProperty(ref _dynamicsLoading, value)) RaiseTabState();
        }
    }

    public bool DynamicsLoadingMore
    {
        get => _dynamicsLoadingMore;
        private set => SetProperty(ref _dynamicsLoadingMore, value);
    }

    public bool DynamicsHasMore
    {
        get => _dynamicsHasMore;
        private set => SetProperty(ref _dynamicsHasMore, value);
    }

    public string DynamicsError
    {
        get => _dynamicsError;
        private set
        {
            if (!SetProperty(ref _dynamicsError, value)) return;
            OnPropertyChanged(nameof(ShowDynamicsError));
            // 空态/遮罩都是计算属性: 错误一变, 它们也得跟着重算
            RaiseTabState();
        }
    }

    /// <summary>只有列表为空时才用整块错误遮罩盖住列表(已有内容时错误只作提示)。</summary>
    public bool ShowDynamicsError => DynamicsError.Length > 0 && Dynamics.Count == 0;

    // ---------------- 关注 / 粉丝 Tab ----------------

    private bool _followingsLoading;
    private bool _followingsLoadingMore;
    private bool _followingsHasMore = true;
    private string _followingsError = "";
    private int _followingsPage = 1;
    private int _followingsTotal;
    // 一批 Add 期间先压住计算属性的通知, 每页只通知一次(50 人全量通知会卡 UI)。
    private bool _followingsBatch;

    /// <summary>TA 关注的 UP 主。</summary>
    public ObservableCollection<FollowUser> Followings { get; } = new();

    public bool FollowingsLoading
    {
        get => _followingsLoading;
        private set
        {
            if (SetProperty(ref _followingsLoading, value)) RaiseTabState();
        }
    }

    public bool FollowingsLoadingMore
    {
        get => _followingsLoadingMore;
        private set => SetProperty(ref _followingsLoadingMore, value);
    }

    public bool FollowingsHasMore
    {
        get => _followingsHasMore;
        private set => SetProperty(ref _followingsHasMore, value);
    }

    public string FollowingsError
    {
        get => _followingsError;
        private set
        {
            if (!SetProperty(ref _followingsError, value)) return;
            OnPropertyChanged(nameof(ShowFollowingsError));
            RaiseTabState();
        }
    }

    public bool ShowFollowingsError => FollowingsError.Length > 0 && Followings.Count == 0;

    // ---------------- 关注搜索 ----------------

    private string _followingsSearchText = "";
    private bool _followingsSearching;
    private string _followingsSearchError = "";
    private CancellationTokenSource? _followingsSearchCts;

    /// <summary>
    /// 关注列表搜索框文本(issue #110)。
    /// ★ 服务端搜索: 关注可能有几千人, 本地过滤只能搜到已加载的那几页。
    /// </summary>
    public string FollowingsSearchText
    {
        get => _followingsSearchText;
        set
        {
            if (!SetProperty(ref _followingsSearchText, value)) return;
            OnPropertyChanged(nameof(HasFollowingsSearch));
            OnPropertyChanged(nameof(ShowFollowingsSearchEmpty));
            OnPropertyChanged(nameof(SummaryText));
            StartFollowingsSearch();
        }
    }

    /// <summary>只有本人的关注列表才有搜索态(搜索框也只在这种情况下显示)。</summary>
    public bool HasFollowingsSearch => IsSelf && !string.IsNullOrWhiteSpace(FollowingsSearchText);

    public bool FollowingsSearching
    {
        get => _followingsSearching;
        private set
        {
            if (!SetProperty(ref _followingsSearching, value)) return;
            OnPropertyChanged(nameof(ShowFollowingsSearchEmpty));
            OnPropertyChanged(nameof(SummaryText));
        }
    }

    public string FollowingsSearchError
    {
        get => _followingsSearchError;
        private set
        {
            if (!SetProperty(ref _followingsSearchError, value)) return;
            OnPropertyChanged(nameof(HasFollowingsSearchError));
            OnPropertyChanged(nameof(ShowFollowingsSearchEmpty));
            OnPropertyChanged(nameof(SummaryText));
        }
    }

    public bool HasFollowingsSearchError => FollowingsSearchError.Length > 0;

    public bool ShowFollowingsSearchEmpty =>
        HasFollowingsSearch && !FollowingsSearching && !HasFollowingsSearchError
        && FollowingSearchResults.Count == 0;

    /// <summary>关注列表的搜索结果(只含已关注的 UP)。</summary>
    public ObservableCollection<FollowUser> FollowingSearchResults { get; } = new();

    private bool _followersLoading;
    private bool _followersLoadingMore;
    private bool _followersHasMore = true;
    private string _followersError = "";
    private int _followersPage = 1;
    private int _followersTotal;

    /// <summary>关注 TA 的人。</summary>
    public ObservableCollection<FollowUser> Followers { get; } = new();

    public bool FollowersLoading
    {
        get => _followersLoading;
        private set
        {
            if (SetProperty(ref _followersLoading, value)) RaiseTabState();
        }
    }

    public bool FollowersLoadingMore
    {
        get => _followersLoadingMore;
        private set => SetProperty(ref _followersLoadingMore, value);
    }

    public bool FollowersHasMore
    {
        get => _followersHasMore;
        private set => SetProperty(ref _followersHasMore, value);
    }

    public string FollowersError
    {
        get => _followersError;
        private set
        {
            if (!SetProperty(ref _followersError, value)) return;
            OnPropertyChanged(nameof(ShowFollowersError));
            RaiseTabState();
        }
    }

    public bool ShowFollowersError => FollowersError.Length > 0 && Followers.Count == 0;

    // ---------------- 概要 ----------------

    /// <summary>
    /// Tab 下方那行说明文字。
    /// ★ 关注/粉丝的"已加载 N / 总数"是**权威判据**的展示 —— 用户能一眼看出还有没有更多。
    /// </summary>
    public string SummaryText => Tab switch
    {
        UserCenterTab.Followings when HasFollowingsSearch => FollowingsSearchSummary,
        UserCenterTab.Followings => FollowingsSummary,
        UserCenterTab.Followers => FollowersSummary,
        _ => DynamicsSummary,
    };

    /// <summary>动态 Tab 的说明文字(已加载多少条)。</summary>
    public string DynamicsSummary => Dynamics.Count > 0
        ? $"动态 · 已加载 {Dynamics.Count} 条"
        : "动态";

    /// <summary>关注列表的"已加载 N / 总数"。</summary>
    public string FollowingsSummary => _followingsTotal > 0
        ? $"关注 · 已加载 {Followings.Count} / {_followingsTotal}"
        : $"关注 · 已加载 {Followings.Count}";

    /// <summary>搜索态的概要: 搜索结果是独立的一批, 不能继续显示普通列表的已加载数。</summary>
    public string FollowingsSearchSummary => FollowingsSearching
        ? "关注 · 正在搜索…"
        : HasFollowingsSearchError
            ? "关注 · 搜索失败"
            : $"关注 · 搜索到 {FollowingSearchResults.Count} 个";

    /// <summary>粉丝列表的"已加载 N / 总数"。</summary>
    public string FollowersSummary => _followersTotal > 0
        ? $"粉丝 · 已加载 {Followers.Count} / {_followersTotal}"
        : $"粉丝 · 已加载 {Followers.Count}";

    /// <summary>
    /// 各 Tab 自己的空态标记。
    ///
    /// ★ 为什么是"每个 Tab 一个"而不是一个总的 TabEmpty: 三个 Tab 的内容区各自绑自己的
    ///   那一句(见 UserCenterPage.xaml), 用总的计算属性会让**当前 Tab 之外**的那两个提示
    ///   也跟着亮/灭 —— 看起来就是"页面上一口气挂着三句空态文案"。各算各的最省心。
    /// ★★ 它们全是**计算属性**, 依赖 Loading / Count / Error —— 这三样任何一个变了
    ///   都得由 <see cref="RaiseTabState"/> 通知, 否则界面会停在旧值。
    /// </summary>
    public bool DynamicsEmpty =>
        !DynamicsLoading && Dynamics.Count == 0 && DynamicsError.Length == 0;
    public bool FollowingsEmpty =>
        !FollowingsLoading && Followings.Count == 0 && FollowingsError.Length == 0;
    public bool FollowersEmpty =>
        !FollowersLoading && Followers.Count == 0 && FollowersError.Length == 0;

    /// <summary>
    /// Tab 级别的状态变化后, 把**页面真正绑的那些**计算属性一并通知出去。
    ///
    /// ★ 为什么必须手动 raise: 空态与错误遮罩都是计算属性, WPF 不会自动重算 ——
    ///   只通知源属性, 界面上的遮罩/空态就永远停在旧值。
    ///
    /// ★★★ 2026-11-19 用户实机撞到的 bug, 别重犯: 这里原来只通知了
    ///   TabLoading / TabErrorOverlay / TabEmpty 三个**聚合**属性 —— 而页面绑的是
    ///   每个 Tab 自己的 FollowingsEmpty / FollowersEmpty / DynamicsEmpty, 那三个聚合
    ///   属性根本没人绑。于是"切到粉丝 Tab, 列表已经有 20 条, 上面还挂着还没有粉丝",
    ///   而直接进粉丝页却不复现(那一路首次求值时 Loading 已经是 true, 蒙对了)。
    ///   更坏的是单测断言的是 TabEmpty —— 一个没人绑的属性, 测试全绿而界面是错的。
    ///   ⇒ 通知列表必须与 XAML 里的绑定逐一对应。
    /// </summary>
    private void RaiseTabState()
    {
        OnPropertyChanged(nameof(DynamicsEmpty));
        OnPropertyChanged(nameof(FollowingsEmpty));
        OnPropertyChanged(nameof(FollowersEmpty));

        // 同理: 错误遮罩也判"列表是否为空", 集合一变就得重算
        OnPropertyChanged(nameof(ShowDynamicsError));
        OnPropertyChanged(nameof(ShowFollowingsError));
        OnPropertyChanged(nameof(ShowFollowersError));
    }

    // ---------------- 命令 ----------------

    /// <summary>重试当前 Tab(错误遮罩里的「重试」按钮)。</summary>
    public ICommand RetryCommand { get; }

    /// <summary>刷新当前 Tab(F5)。</summary>
    public ICommand RefreshCommand { get; }

    /// <summary>
    /// 请求代次。每次 reset(换 Tab / 刷新)自增; 在途的旧请求回来时对不上就整批丢弃。
    /// </summary>
    private int _gen;

    /// <summary>各 Tab 是否已经拉过一次(切 Tab 时懒加载: 第一次进才请求)。</summary>
    private bool _dynamicsLoaded;
    private bool _followingsLoaded;
    private bool _followersLoaded;

    public UserCenterViewModel()
    {
        // ★ 集合变了要通知那几个**计算属性**(摘要行 / 空态 / 错误遮罩)。
        //   WPF 不会自动重算它们 —— 不手动 raise 的话, 列表从空变成有内容时
        //   "空态提示"会一直挂在上面(与 FollowViewModel.ShowFullLoading 同款问题)。
        Dynamics.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(DynamicsSummary));
            OnPropertyChanged(nameof(SummaryText));
            RaiseTabState();
        };
        Followings.CollectionChanged += (_, _) =>
        {
            if (_followingsBatch) return;
            OnPropertyChanged(nameof(FollowingsSummary));
            OnPropertyChanged(nameof(SummaryText));
            RaiseTabState();
        };
        FollowingSearchResults.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowFollowingsSearchEmpty));
            OnPropertyChanged(nameof(SummaryText));
        };
        Followers.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(FollowersSummary));
            OnPropertyChanged(nameof(SummaryText));
            RaiseTabState();
        };

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync());
        RetryCommand = new AsyncRelayCommand(() => RefreshAsync());
    }

    /// <summary>
    /// 初始化: 设定看的是谁, 然后按当前 Tab 加载。
    ///
    /// ★ <paramref name="isSelf"/> 由调用方判定(调用方本来就知道自己的 mid),
    ///   不在这里多打一次 nav 请求 —— 那一次往返不划算。
    /// </summary>
    public async Task InitAsync(long mid, string name = "", string face = "", bool isSelf = false)
    {
        Mid = mid;
        Name = name;
        Face = face;
        IsSelf = isSelf;
        OnPropertyChanged(nameof(TitleText));
        await SwitchTabAsync(Tab);
    }

    /// <summary>页面侧 Tab 选中态变化时通知(由 UserCenterPage 写 RadioButton.IsChecked 后调)。</summary>
    public event Action<UserCenterTab>? TabChanged;

    /// <summary>
    /// 在 InitAsync **之前**把初始 Tab 定好。
    ///
    /// ★ 为什么需要单独一个入口: 页面一上屏显示的就是"动态"Tab,
    ///   而用户点的其实是"关注" —— 要等第一次请求回来才跳过去, 看着像没响应。
    ///   所以调用方(页面构造时)先把 Tab 摆好, 再 init。
    /// </summary>
    public void SetInitialTab(UserCenterTab tab)
    {
        if (_tab == tab) return;
        _tab = tab;
        OnPropertyChanged(nameof(Tab));
        OnPropertyChanged(nameof(IsDynamicsTab));
        OnPropertyChanged(nameof(IsFollowingsTab));
        OnPropertyChanged(nameof(IsFollowersTab));
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(SummaryText));
        TabChanged?.Invoke(tab);
    }

    /// <summary>切 Tab。第一次进某个 Tab 才拉数据, 之后切回来用已加载的内容(可手动刷新)。</summary>
    public async Task SwitchTabAsync(UserCenterTab tab)
    {
        var changed = Tab != tab;
        Tab = tab;
        if (changed) TabChanged?.Invoke(tab);
        OnPropertyChanged(nameof(SummaryText));
        // 换 Tab 后"当前该显示哪一句空态/错误遮罩"全变了, 六个计算属性一起重算
        RaiseTabState();

        switch (tab)
        {
            case UserCenterTab.Followings:
                if (_followingsLoaded) return;
                _followingsLoaded = true;
                await LoadFollowingsAsync(reset: true);
                // ★ 失败时**不保留**"已加载"标记 —— 用户切走再切回来会自动重试。
                //   (与 MineViewModel._foldersLoaded 同一套语义: 置了标记就再也不拉了,
                //    收藏夹/关注列表会一直空着, 用户只能靠刷新按钮救。)
                if (FollowingsError.Length > 0 && Followings.Count == 0) _followingsLoaded = false;
                break;
            case UserCenterTab.Followers:
                if (_followersLoaded) return;
                _followersLoaded = true;
                await LoadFollowersAsync(reset: true);
                if (FollowersError.Length > 0 && Followers.Count == 0) _followersLoaded = false;
                break;
            default:
                if (_dynamicsLoaded) return;
                _dynamicsLoaded = true;
                await LoadDynamicsAsync(reset: true);
                if (DynamicsError.Length > 0 && Dynamics.Count == 0) _dynamicsLoaded = false;
                break;
        }
    }

    /// <summary>F5 / 刷新按钮: 重拉当前 Tab。</summary>
    public async Task RefreshAsync()
    {
        switch (Tab)
        {
            case UserCenterTab.Followings:
                // 搜索结果是用户当前看到的内容: 刷新它, 而不是背后那张隐藏的普通列表。
                if (HasFollowingsSearch)
                {
                    StartFollowingsSearch(debounce: false);
                    return;
                }
                _followingsLoaded = true;
                await LoadFollowingsAsync(reset: true);
                break;
            case UserCenterTab.Followers:
                _followersLoaded = true;
                await LoadFollowersAsync(reset: true);
                break;
            default:
                _dynamicsLoaded = true;
                await LoadDynamicsAsync(reset: true);
                break;
        }
    }

    /// <summary>滚到底 / 底部重试: 加载当前 Tab 的下一页。</summary>
    public async Task LoadMoreAsync()
    {
        switch (Tab)
        {
            case UserCenterTab.Followings:
                // 搜索结果是一整批返回的; 搜索时不要再翻普通关注列表。
                if (HasFollowingsSearch) return;
                if (FollowingsLoading || FollowingsLoadingMore || !FollowingsHasMore) return;
                await LoadFollowingsAsync(reset: false);
                break;
            case UserCenterTab.Followers:
                if (FollowersLoading || FollowersLoadingMore || !FollowersHasMore) return;
                await LoadFollowersAsync(reset: false);
                break;
            default:
                if (DynamicsLoading || DynamicsLoadingMore || !DynamicsHasMore) return;
                await LoadDynamicsAsync(reset: false);
                break;
        }
    }

    // ------------------------------------------------------------ 动态

    /// <summary>
    /// 拉一页动态(游标分页)。
    ///
    /// ★ "到底"判据: <c>has_more</c> **且** offset 确实前进。
    ///   只看 has_more 时, 接口返回重复页就会一直认为"还有下一页" —— 底部转圈永远不消失
    ///   (与 UserSpaceViewModel.LoadImagesPageAsync 同款判断)。
    /// </summary>
    private async Task LoadDynamicsAsync(bool reset)
    {
        if (Mid <= 0) return;
        if (reset)
        {
            _gen++;
            _dynamicsOffset = null;
            _dynamicsHasMore = true;
            DynamicsError = "";
            Dynamics.Clear();
            DynamicsLoading = true;
        }
        else
        {
            if (DynamicsLoadingMore || !DynamicsHasMore) return;
            DynamicsLoadingMore = true;
        }

        var gen = _gen;
        try
        {
            var (ok, err, items, next, hasMore) = await Svc.Api.GetSpaceDynamicsAsync(Mid, _dynamicsOffset);
            if (gen != _gen) return;   // 过期结果: 整批丢弃, 连 Loading 状态都不碰

            if (!ok)
            {
                DynamicsError = err ?? "动态加载失败";
                return;
            }

            if (items != null)
            {
                // 按动态 id 去重(图文/文字/转发没有 bvid, 只能靠它)
                var seen = new HashSet<string>(Dynamics.Select(x => x.Id));
                foreach (var it in items)
                {
                    if (!string.IsNullOrEmpty(it.Id) && !seen.Add(it.Id)) continue;
                    Dynamics.Add(it);
                }
            }

            var advanced = !string.IsNullOrEmpty(next) && next != _dynamicsOffset;
            _dynamicsOffset = next;
            _dynamicsHasMore = hasMore && advanced;

            OnPropertyChanged(nameof(DynamicsSummary));
            OnPropertyChanged(nameof(SummaryText));
        }
        catch (Exception ex)
        {
            if (gen == _gen) DynamicsError = ex.Message;
        }
        finally
        {
            // ★ 必须走 finally: 见类注释的坑 ①
            if (gen == _gen)
            {
                DynamicsLoading = false;
                DynamicsLoadingMore = false;
            }
        }
    }

    // ------------------------------------------------------------ 关注

    /// <summary>
    /// 拉一页关注列表(页码分页, 每页 50)。
    ///
    /// ★ "到底"判据用接口给的 **total**: 关注 2790+ 的账号不能靠"本页是否满 50"猜
    ///   (降级路径会按 17/22 这种零头分页, 早早就误判成到底)。
    /// </summary>
    private async Task LoadFollowingsAsync(bool reset)
    {
        if (Mid <= 0) return;
        if (reset)
        {
            _gen++;
            _followingsPage = 1;
            _followingsTotal = 0;
            _followingsHasMore = true;
            FollowingsError = "";
            Followings.Clear();
            FollowingsLoading = true;
        }
        else
        {
            if (FollowingsLoadingMore || !FollowingsHasMore) return;
            FollowingsLoadingMore = true;
        }

        var gen = _gen;
        try
        {
            var page = _followingsPage;
            var (ok, err, list, total) = await Svc.Api.GetFollowingsAsync(Mid, page, 50);
            if (gen != _gen) return;

            if (!ok || list == null)
            {
                FollowingsError = err ?? "关注列表加载失败";
                return;
            }

            if (total > 0) _followingsTotal = total;   // 摘要行要显示总数, 必须在批量通知之前落定
            AppendFollowingsPage(list);
            _followingsPage = page + 1;

            // ★ 到底判据: 有 total 用 total; 没有 total 时退回"本页是否满页"。
            //   空页也必须停 —— 否则滚到底会无限重试同一页(转圈不消失)。
            FollowingsHasMore = list.Count > 0 &&
                               (_followingsTotal > 0
                                   ? Followings.Count < _followingsTotal
                                   : list.Count >= 50);
        }
        catch (Exception ex)
        {
            if (gen == _gen) FollowingsError = ex.Message;
        }
        finally
        {
            if (gen == _gen)
            {
                FollowingsLoading = false;
                FollowingsLoadingMore = false;
            }
        }
    }

    /// <summary>
    /// 批量追加一页关注列表。
    ///
    /// ★ 一页 50 条, 每条 CollectionChanged 都要重算 FollowingsSummary / SummaryText / 空态与
    ///   错误遮罩(见构造函数里的订阅) —— 几百次属性通知全压在 UI 线程上, 就是"进关注页卡一下"。
    ///   这里批量期间压住逐条通知, 结束后统一补一次。
    /// ★ 调用前必须先把 <c>_followingsTotal</c> 更新好: 摘要行显示的是"已加载 N / 总数"。
    /// </summary>
    private void AppendFollowingsPage(IReadOnlyList<FollowUser> page)
    {
        _followingsBatch = true;
        try
        {
            foreach (var u in page)
            {
                if (u.Mid <= 0) continue;
                Followings.Add(u);
            }
        }
        finally
        {
            _followingsBatch = false;
        }

        OnPropertyChanged(nameof(FollowingsSummary));
        OnPropertyChanged(nameof(SummaryText));
        // 批量期间 CollectionChanged 里的 RaiseTabState 被压掉了, 这里补一次:
        // 否则"还没有关注任何人"的空态(以及错误遮罩)会一直挂在已有内容的列表上。
        RaiseTabState();
    }

    // ------------------------------------------------------------ 关注搜索

    private void StartFollowingsSearch(bool debounce = true)
        => RunFollowingsSearchAsync(debounce).Forget();

    /// <summary>
    /// 300ms 防抖后调「搜索关注明细」接口(与动态页左栏同一套做法)。
    /// ★ 不做本地过滤: 关注可能有几千人, 本地过滤只能搜到已加载的几十/几百人。
    /// </summary>
    private async Task RunFollowingsSearchAsync(bool debounce = true)
    {
        _followingsSearchCts?.Cancel();
        _followingsSearchCts?.Dispose();
        _followingsSearchCts = null;

        FollowingSearchResults.Clear();
        FollowingsSearchError = "";

        // 属性是 public, 绑定之外也可能被代码写 null; 这里兜一下。
        var keyword = (FollowingsSearchText ?? "").Trim();
        if (keyword.Length == 0)
        {
            FollowingsSearching = false;
            return;
        }
        // 只在自己的关注列表提供搜索: 别人的关注列表可能被隐私设置挡住。
        if (!IsSelf)
        {
            FollowingsSearching = false;
            return;
        }
        if (Mid <= 0)
        {
            FollowingsSearching = false;
            FollowingsSearchError = "账号信息还没准备好, 请稍后再试";
            return;
        }

        FollowingsSearching = true;
        var cts = new CancellationTokenSource();
        _followingsSearchCts = cts;
        try
        {
            // 手动刷新/错误重试不需要再等防抖, 否则用户会觉得按钮没反应。
            if (debounce) await Task.Delay(300, cts.Token);
            if (cts.IsCancellationRequested) return;

            var (ok, err, list, _) = await Svc.Api.SearchFollowingsAsync(Mid, keyword, 1, 50);
            if (cts.IsCancellationRequested) return;

            if (!ok || list == null)
            {
                FollowingsSearchError = err ?? "搜索失败, 请重试";
                return;
            }

            var seen = new HashSet<long>();
            foreach (var u in list)
                if (u.Mid > 0 && seen.Add(u.Mid)) FollowingSearchResults.Add(u);
        }
        catch (OperationCanceledException)
        {
            // 输入还在继续, 这一次作废。
        }
        catch (Exception ex)
        {
            // 旧请求被取消后抛出的异常不能覆盖当前这次搜索的状态。
            if (!cts.IsCancellationRequested) FollowingsSearchError = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_followingsSearchCts, cts))
            {
                _followingsSearchCts = null;
                FollowingsSearching = false;
            }
            cts.Dispose();
        }
    }

    // ------------------------------------------------------------ 粉丝

    /// <summary>
    /// 拉一页粉丝列表(页码分页, 每页 20 —— 服务端硬上限, 见 ApiClient.FollowerPageSize)。
    ///
    /// ★ 注意: 该接口对**非本人**只返回前 100 位粉丝(B 站的服务端限制)。
    ///   看自己的粉丝时不受限; 看别人的 UP 主页时列表会停在 100 —— 这是服务端的规矩,
    ///   不是翻页出了 bug。
    /// </summary>
    private async Task LoadFollowersAsync(bool reset)
    {
        if (Mid <= 0) return;
        if (reset)
        {
            _gen++;
            _followersPage = 1;
            _followersTotal = 0;
            _followersHasMore = true;
            FollowersError = "";
            Followers.Clear();
            FollowersLoading = true;
        }
        else
        {
            if (FollowersLoadingMore || !FollowersHasMore) return;
            FollowersLoadingMore = true;
        }

        var gen = _gen;
        try
        {
            var page = _followersPage;
            var (ok, err, list, total) = await Svc.Api.GetFollowersAsync(Mid, page);
            if (gen != _gen) return;

            if (!ok || list == null)
            {
                FollowersError = err ?? "粉丝列表加载失败";
                return;
            }

            foreach (var u in list)
            {
                if (u.Mid <= 0) continue;
                Followers.Add(u);
            }

            if (total > 0) _followersTotal = total;
            _followersPage = page + 1;

            FollowersHasMore = list.Count > 0 &&
                              (_followersTotal > 0
                                  ? Followers.Count < _followersTotal
                                  : list.Count >= ApiClient.FollowerPageSize);

            OnPropertyChanged(nameof(FollowersSummary));
            OnPropertyChanged(nameof(SummaryText));
        }
        catch (Exception ex)
        {
            if (gen == _gen) FollowersError = ex.Message;
        }
        finally
        {
            if (gen == _gen)
            {
                FollowersLoading = false;
                FollowersLoadingMore = false;
            }
        }
    }
}
