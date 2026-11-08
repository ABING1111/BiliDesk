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
/// UP 主空间 ViewModel。
/// 接口链路有降级策略(acc/info → card → 旧接口), 即使单个接口被风控也能展示。
///
/// ★ 2026-11-08 投稿改为**页码分页**(用户要求, 参考 B 站投稿页):
///   原来靠滚到底自动追加, 用户永远不知道一共有多少页、也没法直接跳到某一页;
///   现在一次只保留当前页, 底部页码栏负责翻页, 翻页后回到顶部。
/// </summary>
public class UserSpaceViewModel : ObservableObject
{
    /// <summary>每页投稿数(与 B 站空间投稿页一致)</summary>
    private const int PageSize = 30;

    private long _mid;
    private string _name = "";
    private string _face = "";
    private string _sign = "";
    private int _level;
    private long _follower;
    private long _following;
    private long _videoCount;
    private long _likeCount;
    private string _vipLabel = "";
    private string _vipColor = "";
    private bool _loading;
    private string _error = "";

    // ---------------- 分页状态 ----------------
    private int _currentPage = 1;
    private int _totalPages;
    private long _totalVideos;
    private bool _loadingPage;
    /// <summary>请求代次: 快速连点页码时只认最后一次的结果, 防止旧响应盖掉新页</summary>
    private int _loadGen;

    public ObservableCollection<VideoItem> Videos { get; } = new();

    /// <summary>底部页码栏的条目(数字页或省略号)</summary>
    public ObservableCollection<PageItem> PageItems { get; } = new();

    /// <summary>请求页面把列表滚回顶部(翻页后调; 由 UserSpacePage 订阅)</summary>
    public event Action? ScrollToTopRequested;

    public long Mid { get => _mid; private set { if (SetProperty(ref _mid, value)) OnPropertyChanged(nameof(UidText)); } }
    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public string Face { get => _face; private set => SetProperty(ref _face, value); }
    public string Sign
    {
        get => _sign;
        private set
        {
            if (!SetProperty(ref _sign, value)) return;
            OnPropertyChanged(nameof(CanExpandSign));
            OnPropertyChanged(nameof(SignDetailText));
        }
    }
    public int Level { get => _level; private set { if (SetProperty(ref _level, value)) OnPropertyChanged(nameof(HasLevel)); } }
    public long Follower { get => _follower; private set { if (SetProperty(ref _follower, value)) OnPropertyChanged(nameof(FollowerText)); } }
    public long Following { get => _following; private set { if (SetProperty(ref _following, value)) OnPropertyChanged(nameof(FollowingText)); } }
    public long VideoCount { get => _videoCount; private set { if (SetProperty(ref _videoCount, value)) OnPropertyChanged(nameof(VideoCountText)); } }
    public long LikeCount { get => _likeCount; private set { if (SetProperty(ref _likeCount, value)) OnPropertyChanged(nameof(LikeText)); } }
    public string VipLabel { get => _vipLabel; private set { if (SetProperty(ref _vipLabel, value)) OnPropertyChanged(nameof(HasVip)); } }
    public string VipColor { get => _vipColor; private set => SetProperty(ref _vipColor, value); }

    public bool Loading
    {
        get => _loading;
        private set => SetProperty(ref _loading, value);
    }

    public string Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    // ---------------- 顶部信息展示 ----------------
    public bool HasLevel => Level > 0;
    public string LevelText => $"LV{Level}";
    public string UidText => $"UID: {Mid}";
    public bool HasVip => !string.IsNullOrEmpty(VipLabel);

    public string FollowerText => VideoItem.FormatCount(Follower);
    public string FollowingText => VideoItem.FormatCount(Following);
    public string VideoCountText => VideoItem.FormatCount(VideoCount);
    public string LikeText => VideoItem.FormatCount(LikeCount);

    /// <summary>投稿区标题: 带上总数, 页码栏的页数才有对照</summary>
    public string PageSummary => TotalVideos > 0
        ? $"投稿 · 共 {VideoItem.FormatCount(TotalVideos)} 个视频"
        : "投稿";

    // ---------------- 简介展开 ----------------
    private bool _signExpanded;

    /// <summary>简介是否展开(默认两行, 点「详情」展开全部)</summary>
    public bool IsSignExpanded
    {
        get => _signExpanded;
        private set
        {
            if (!SetProperty(ref _signExpanded, value)) return;
            OnPropertyChanged(nameof(SignDetailText));
        }
    }

    /// <summary>简介是否长到需要「详情」(短签名不显示这个入口)</summary>
    public bool CanExpandSign => Sign.Length > 40;

    public string SignDetailText => IsSignExpanded ? "收起" : "详情";

    // ---------------- 分页公开属性 ----------------
    public int CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (!SetProperty(ref _currentPage, value)) return;
            OnPropertyChanged(nameof(CanGoPrev));
            OnPropertyChanged(nameof(CanGoNext));
        }
    }

    public int TotalPages
    {
        get => _totalPages;
        private set
        {
            if (!SetProperty(ref _totalPages, value)) return;
            OnPropertyChanged(nameof(ShowPager));
            OnPropertyChanged(nameof(CanGoPrev));
            OnPropertyChanged(nameof(CanGoNext));
        }
    }

    public long TotalVideos
    {
        get => _totalVideos;
        private set
        {
            if (!SetProperty(ref _totalVideos, value)) return;
            OnPropertyChanged(nameof(PageSummary));
        }
    }

    public bool CanGoPrev => !_loadingPage && CurrentPage > 1;
    public bool CanGoNext => !_loadingPage && TotalPages > 0 && CurrentPage < TotalPages;

    /// <summary>正在翻页(页码栏显示小转圈, 并暂时禁点, 防止连点)</summary>
    public bool IsPaging => _loadingPage;

    /// <summary>页码数字是否可点(翻页在途时禁用)</summary>
    public bool CanChangePage => !_loadingPage;

    /// <summary>只有一页时页码栏没有意义, 直接收起</summary>
    public bool ShowPager => TotalPages > 1 && Videos.Count > 0;

    // ----------------- 关注状态 -----------------
    // 与播放器里同一套逻辑(PlayerWindow.RefreshFollowStateAsync):
    // 先查与该 UP 的关系, 已关注则按钮显示「已关注」、点击发送取消关注。
    // 之前这里恒发 follow=true, 已关注的用户点按钮只会得到
    // "已经关注用户, 无法重复关注"(用户 2026-10-03 报的"无法取消关注")。

    private bool _isFollowed;
    /// <summary>是否已关注该 UP 主(进入页面时查询一次, 成功操作后本地翻转)</summary>
    public bool IsFollowed
    {
        get => _isFollowed;
        private set
        {
            if (SetProperty(ref _isFollowed, value))
            {
                OnPropertyChanged(nameof(FollowLabel));
                OnPropertyChanged(nameof(IsNotFollowed));
            }
        }
    }

    /// <summary>未关注 = 显示强调色「+ 关注」; 已关注 = 切成灰色底「已关注」</summary>
    public bool IsNotFollowed => !IsFollowed;

    public string FollowLabel => IsFollowed ? "已关注" : "+ 关注";

    public ICommand ToggleFollowCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand GoToPageCommand { get; }
    public ICommand PrevPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand ToggleSignCommand { get; }

    public UserSpaceViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        PrevPageCommand = new AsyncRelayCommand(() => GoToPageAsync(CurrentPage - 1));
        NextPageCommand = new AsyncRelayCommand(() => GoToPageAsync(CurrentPage + 1));
        GoToPageCommand = new RelayCommand(p =>
        {
            var page = p switch
            {
                int i => i,
                string s when int.TryParse(s, out var n) => n,
                _ => 0
            };
            if (page > 0) _ = GoToPageAsync(page);
        });
        ToggleFollowCommand = new AsyncRelayCommand(ToggleFollowAsync);
        ToggleSignCommand = new RelayCommand(() => IsSignExpanded = !IsSignExpanded);
    }

    public async Task InitAsync(long mid)
    {
        Mid = mid;
        if (string.IsNullOrEmpty(Name)) await RefreshAsync();
    }

    /// <summary>
    /// 是否正在初始化/刷新。
    ///
    /// 这个页面有两条初始化路径: 构造函数里直接 InitAsync 一次, Loaded 里又 InitAsync 一次。
    /// 构造那次还没等到接口返回(Name 仍为空), Loaded 就又满足 `Name 为空` 的条件,
    /// 于是并发发出**两套**"资料 + 投稿"请求; 而 _page 被自增两次 ——
    /// 第二个请求直接落到第 2 页, 用户第一次打开 UP 主页会看不到最新投稿。
    /// 用哨兵挡住重复的那次。
    /// </summary>
    private bool _initializing;

    public async Task RefreshAsync()
    {
        if (_mid <= 0) return;
        if (_initializing) return;
        _initializing = true;
        // 只有首次(无内容)才显示居中大菊花, 已有内容时静默刷新
        var firstLoad = Videos.Count == 0 && string.IsNullOrEmpty(Name);
        if (firstLoad) Loading = true;
        Error = "";
        try
        {
            // 信息与列表并行加载, 互不阻塞; 关注状态顺带查一次(未登录静默跳过)
            var infoTask = Svc.Api.GetSpaceInfoAsync(_mid);
            var videosTask = LoadPageAsync(1);
            _ = RefreshFollowStateAsync();
            var (infoOk, _, info) = await infoTask;
            await videosTask;

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (infoOk && info != null)
                {
                    Name = info.Name;
                    Face = info.Face;
                    Sign = string.IsNullOrEmpty(info.Sign) ? "这个 UP 主很懒, 什么都没写~" : info.Sign;
                    Level = info.Level;
                    Follower = info.Follower;
                    Following = info.Following;
                    VideoCount = info.VideoCount;
                    LikeCount = info.LikeCount;
                    VipLabel = info.VipLabel;
                    VipColor = info.VipColor;
                    OnPropertyChanged(nameof(Face));
                    // ★ 降级路径(arc/search 被风控)拿不到 page.count, 用名片里的投稿数补总页数;
                    //   不补的话页码栏要么不出现, 要么只能显示"下一页"却不知道共几页。
                    //   ★★ 只补 TotalVideos 是不够的: TotalPages 是在 LoadPageAsync 里按
                    //     "本页是否满 30 条"猜的(降级接口会按 17/22 这种零头分页), 不重算
                    //     会一直停在 1~2 页 —— 页数够多的 UP 就永远看不到页码栏。
                    if (TotalVideos <= 0 && VideoCount > 0)
                    {
                        TotalVideos = VideoCount;
                        TotalPages = Math.Max(1, (int)Math.Ceiling(VideoCount / (double)PageSize));
                        if (CurrentPage > TotalPages) CurrentPage = TotalPages;
                        RecalcPages(CurrentPage);
                        OnPropertyChanged(nameof(ShowPager));
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            _initializing = false;
            Loading = false;
        }
    }

    /// <summary>
    /// 加载指定页并替换当前列表。
    /// ★ 用代次(_loadGen)丢弃过期响应: 用户快速点 1→2→3 时, 如果 1 的响应最后才回来,
    ///   列表会被错误地盖回第 1 页的内容, 而页码栏显示的却是 3。
    /// </summary>
    private async Task LoadPageAsync(int page)
    {
        if (_mid <= 0) return;
        if (page < 1) page = 1;
        if (TotalPages > 0 && page > TotalPages) page = TotalPages;

        var gen = ++_loadGen;
        _loadingPage = true;
        OnPropertyChanged(nameof(CanGoPrev));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(IsPaging));
        OnPropertyChanged(nameof(CanChangePage));
        try
        {
            var (ok, err, items, total) = await Svc.Api.GetSpaceVideosAsync(_mid, page, PageSize);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (gen != _loadGen) return;
                if (items == null)
                {
                    Error = err ?? "加载失败";
                    return;
                }

                Videos.Clear();
                foreach (var it in items) Videos.Add(it);
                CurrentPage = page;

                // 总条数: 优先接口 page.count; 降级路径(0)用名片里的投稿数。
                var known = total > 0 ? total : VideoCount;
                TotalVideos = known;
                if (known > 0)
                {
                    TotalPages = (int)Math.Ceiling(known / (double)PageSize);
                }
                else
                {
                    // 两条接口都拿不到总数: 至少用"本页是否满页"推断还有没有下一页,
                    // 不要显示一个假的末页号。
                    TotalPages = items.Count >= PageSize ? page + 1 : page;
                }

                if (Videos.Count == 0) Error = err ?? "该 UP 主暂无投稿视频";
                else Error = "";

                RecalcPages(page);
                OnPropertyChanged(nameof(ShowPager));
                if (page > 1) ScrollToTopRequested?.Invoke();
            });
        }
        catch (Exception ex)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (gen == _loadGen) Error = ex.Message;
            });
        }
        finally
        {
            _loadingPage = false;
            OnPropertyChanged(nameof(CanGoPrev));
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(IsPaging));
            OnPropertyChanged(nameof(CanChangePage));
        }
    }

    private async Task GoToPageAsync(int page)
    {
        if (_loadingPage) return;
        if (page < 1 || (TotalPages > 0 && page > TotalPages)) return;
        if (page == CurrentPage && !_initializing) return;

        // ★ 先把高亮移到目标页, 让用户**立刻**看到"点到了"(2026-11-08 用户反馈
        //   "不知道点了有没有")。请求失败时再回滚到原页, 不会停在错误的高亮上。
        var previous = CurrentPage;
        CurrentPage = page;
        RecalcPages(page);

        await LoadPageAsync(page);

        if (Error.Length > 0 && CurrentPage == page && page != previous)
        {
            CurrentPage = previous;
            RecalcPages(previous);
        }
    }

    /// <summary>按当前总页数重建页码序列(0 表示省略号)</summary>
    private void RecalcPages(int current)
    {
        PageItems.Clear();
        if (TotalPages <= 1) return;
        foreach (var p in PageBar.Build(current, TotalPages))
        {
            PageItems.Add(p == PageBar.Ellipsis
                ? new PageItem { IsEllipsis = true, Display = "…" }
                : new PageItem { Page = p, Display = p.ToString(), IsCurrent = p == current });
        }
    }

    private async Task FollowAsync()
    {
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("关注需要先登录"); return; }
        if (_mid <= 0) return;
        var (ok, err) = await Svc.Api.FollowUpAsync(_mid, true);
        Svc.Toast.Show(ok ? "已关注" : "关注失败: " + (err ?? "未知错误"));
    }

    /// <summary>查询与该 UP 的关注关系(进页面时调一次)</summary>
    public async Task RefreshFollowStateAsync()
    {
        if (_mid <= 0 || !Svc.Session.HasLogin) return;
        var (ok, _, followed) = await Svc.Api.GetRelationAsync(_mid);
        if (ok) IsFollowed = followed;
    }

    /// <summary>关注 / 取消关注(按当前状态切换)。按钮文案随 IsFollowed 变。</summary>
    private async Task ToggleFollowAsync()
    {
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("关注需要先登录"); return; }
        if (_mid <= 0) return;
        var (ok, err) = await Svc.Api.FollowUpAsync(_mid, !IsFollowed);
        if (ok)
        {
            IsFollowed = !IsFollowed;
            Svc.Toast.Show(IsFollowed ? "已关注" : "已取消关注");
        }
        else
        {
            Svc.Toast.Show("操作失败: " + (err ?? "未知错误"));
        }
    }
}

/// <summary>页码栏的一项: 数字页或省略号。</summary>
public sealed class PageItem : ObservableObject
{
    public int Page { get; init; }
    public string Display { get; init; } = "";

    /// <summary>true = 省略号(不可点)</summary>
    public bool IsEllipsis { get; init; }

    private bool _isCurrent;
    /// <summary>当前页(强调色实底)</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }
}
