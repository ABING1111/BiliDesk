using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.ViewModels;

/// <summary>首页 ViewModel: 推荐 + 热门 + 排行榜 + 直播 四个 tab</summary>
public class HomeViewModel : ObservableObject
{
    // 默认 tab 是「推荐」, 四个可见性标志的初值必须与之一致 ——
    // 否则首帧会先把「热门」的空列表显示出来, 再被 RadioButton 的默认选中切走。
    private bool _isPopularTab;
    private bool _isRecommendTab = true;
    private bool _isRankingTab;
    private bool _isLiveTab;
    private bool _loading;
    private bool _loadingMore;
    private bool _rankingBlocked;
    private string _error = "";
    private int _popularPage = 1;
    private bool _popularHasMore = true;

    public ObservableCollection<VideoItem> PopularItems { get; } = new();
    public ObservableCollection<VideoItem> RecommendItems { get; } = new();
    public ObservableCollection<VideoItem> RankingItems { get; } = new();
    public ObservableCollection<VideoItem> LiveItems { get; } = new();

    public bool IsPopularTab { get => _isPopularTab; set => SetProperty(ref _isPopularTab, value); }
    public bool IsRecommendTab { get => _isRecommendTab; set => SetProperty(ref _isRecommendTab, value); }
    public bool IsRankingTab { get => _isRankingTab; set => SetProperty(ref _isRankingTab, value); }
    public bool IsLiveTab { get => _isLiveTab; set => SetProperty(ref _isLiveTab, value); }
    public bool Loading { get => _loading; private set => SetProperty(ref _loading, value); }
    public bool LoadingMore { get => _loadingMore; private set => SetProperty(ref _loadingMore, value); }
    public bool RankingBlocked { get => _rankingBlocked; private set => SetProperty(ref _rankingBlocked, value); }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }
    public bool PopularHasMore { get => _popularHasMore; private set => SetProperty(ref _popularHasMore, value); }
    public int PopularCount => PopularItems.Count;
    public int RecommendCount => RecommendItems.Count;
    public int RankingCount => RankingItems.Count;
    public int LiveCount => LiveItems.Count;

    /// <summary>直播列表还能不能翻下一页(直播不做"到底了"提示, 但滚动加载要有个闸)</summary>
    public bool LiveHasMore { get => _liveHasMore; private set => SetProperty(ref _liveHasMore, value); }

    private bool _liveHasMore = true;
    private int _livePage = 1;
    private bool _liveLoading;

    public ICommand RefreshCommand { get; }
    public ICommand LoadMoreCommand { get; }
    public ICommand OpenRankingWebCommand { get; }

    public HomeViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        LoadMoreCommand = new AsyncRelayCommand(LoadMoreAsync);
        OpenRankingWebCommand = new RelayCommand(() =>
            Svc.Navigate?.OpenWebViewWindow(
                "https://www.bilibili.com/v/popular/rank/all", "排行榜 - 网页版"));

        // 搜索卡: 回车 / 点历史词条 / 点热搜条目, 三个入口都汇到 RequestSearch 这一个出口
        RunSearchCommand = new RelayCommand(() => RequestSearch(SearchKeyword));
        UseSearchHistoryCommand = new RelayCommand(p => RequestSearch(p as string));
        ClearSearchHistoryCommand = new RelayCommand(() => Svc.SearchHistory.Clear());
        ToggleHistoryExpandCommand = new RelayCommand(() =>
        {
            HistoryExpanded = !HistoryExpanded;
            RebuildHistoryShown();
        });
        UseHotSearchCommand = new RelayCommand(p =>
        {
            if (p is HotSearchItem h) RequestSearch(h.Keyword);
        });

        // 搜索页搜过一次 / 清空历史之后, 这里显示的那一份也要跟着变
        Svc.SearchHistory.Changed += RebuildHistoryShown;
        RebuildHistoryShown();

        // 设置页切了推荐算法(App 官方 / 网页版): 手里这份列表是**上一套模型**给的,
        // 留着不换用户会以为"改了没用" —— 直接清掉重新拉一次。
        // 还没加载过(列表为空)就不动, 免得把一次没必要的请求打出去。
        Svc.Settings.RecommendSourceChanged += () =>
        {
            if (RecommendItems.Count == 0) return;
            _ = LoadRecommendAsync(reset: true);
        };

        RefreshGreeting();
    }

    // ----------------- 问候语 -----------------

    /// <summary>分时段问候文案(早上好 / 中午好 / 下午好 / 晚上好 / 夜深了)</summary>
    public string Greeting { get; private set; } = "";

    /// <summary>搭配问候语的文字表情符号</summary>
    public string GreetingFace { get; private set; } = "";

    private static readonly Random GreetRand = new();

    /// <summary>
    /// 按当前时间刷新问候语。每次重新进入首页时调用一次,
    /// 这样应用长时间挂在后台后再切回来, 文案也会跟着时段变化。
    /// </summary>
    public void RefreshGreeting()
    {
        var hour = DateTime.Now.Hour;

        string text;
        string[] faces;
        if (hour is >= 5 and < 11)          // 05:00 - 10:59
        {
            text = "早上好";
            faces = new[] { "(｡･ω･｡)", "(＾▽＾)", "(・ω・)" };
        }
        else if (hour is >= 11 and < 13)    // 11:00 - 12:59
        {
            text = "中午好";
            faces = new[] { "(￣▽￣)", "(＾ω＾)", "(・ω・)" };
        }
        else if (hour is >= 13 and < 18)    // 13:00 - 17:59
        {
            text = "下午好";
            faces = new[] { "(´・ω・`)", "(・ω・)ノ", "(￣ω￣)" };
        }
        else if (hour is >= 18 and < 23)    // 18:00 - 22:59
        {
            text = "晚上好";
            faces = new[] { "(￣▽￣)ノ", "(・ω・)ﾉ", "(＾▽＾)" };
        }
        else                                // 23:00 - 04:59
        {
            text = "夜深了";
            faces = new[] { "(。-ω-)zzz", "(-ω-)zZ", "(´-ω-`)" };
        }

        Greeting = text;
        GreetingFace = faces[GreetRand.Next(faces.Length)];
        OnPropertyChanged(nameof(Greeting));
        OnPropertyChanged(nameof(GreetingFace));
    }

    /// <summary>进入首页时自动加载一次(默认加载推荐 tab)</summary>
    public async Task InitAsync()
    {
        // 默认 tab 已设为推荐, 加载推荐
        if (RecommendItems.Count == 0) await LoadRecommendAsync(reset: true);
    }

    /// <summary>手动刷新(当前 tab)</summary>
    public async Task RefreshAsync()
    {
        if (IsRecommendTab)
        {
            await LoadRecommendAsync(reset: true);
        }
        else if (IsPopularTab)
        {
            _popularPage = 1;
            _popularHasMore = true;
            await LoadPopularAsync(reset: true);
        }
        else if (IsRankingTab)
        {
            await LoadRankingAsync();
        }
        else if (IsLiveTab)
        {
            _livePage = 1;
            LiveHasMore = true;
            await LoadLiveAsync(reset: true);
        }
    }

    /// <summary>切到热门 tab</summary>
    public async Task SwitchToPopularAsync()
    {
        SetTab(PageTab.Popular);
        Error = "";
        if (PopularItems.Count == 0) await RefreshAsync();
    }

    /// <summary>切到推荐 tab</summary>
    public async Task SwitchToRecommendAsync()
    {
        SetTab(PageTab.Recommend);
        Error = "";
        if (RecommendItems.Count == 0) await LoadRecommendAsync(reset: true);
    }

    /// <summary>切到排行榜 tab</summary>
    public async Task SwitchToRankingAsync()
    {
        SetTab(PageTab.Ranking);
        Error = "";
        if (RankingItems.Count == 0 && !RankingBlocked) await LoadRankingAsync();
    }

    /// <summary>切到直播 tab</summary>
    public async Task SwitchToLiveAsync()
    {
        SetTab(PageTab.Live);
        Error = "";
        // 直播是"实时"内容, 每次切回来都重新拉一页 —— 缓存住一个两小时前的列表没有意义
        _livePage = 1;
        LiveHasMore = true;
        await LoadLiveAsync(reset: true);
    }

    private enum PageTab { Recommend, Popular, Ranking, Live }

    private void SetTab(PageTab tab)
    {
        IsRecommendTab = tab == PageTab.Recommend;
        IsPopularTab = tab == PageTab.Popular;
        IsRankingTab = tab == PageTab.Ranking;
        IsLiveTab = tab == PageTab.Live;
    }

    // ----------------- 直播 -----------------

    private async Task LoadLiveAsync(bool reset = false)
    {
        // 同推荐流: 这个入口会被"切换 tab"和"滚动加载"两条路径打到, 需要防重入
        if (_liveLoading) return;
        _liveLoading = true;
        if (reset) Loading = true;
        else LoadingMore = true;
        Error = "";
        try
        {
            var (ok, err, items) = await Svc.Api.GetLiveListAsync(_livePage);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Loading = false;
                LoadingMore = false;
                if (items != null)
                {
                    if (reset) LiveItems.Clear();
                    var seen = new HashSet<long>(LiveItems.Select(x => x.RoomId));
                    foreach (var it in items)
                        if (it.RoomId > 0 && seen.Add(it.RoomId)) LiveItems.Add(it);
                    // 接口每页固定 20 条, 不满一页就是到底了
                    LiveHasMore = items.Count >= 20;
                    OnPropertyChanged(nameof(LiveCount));
                }
                else
                {
                    Error = err ?? "直播列表加载失败";
                }
            });
        }
        catch (Exception ex)
        {
            Loading = false;
            LoadingMore = false;
            Error = ex.Message;
        }
        finally
        {
            _liveLoading = false;
        }
    }

    // ----------------- 热门 -----------------

    private async Task LoadPopularAsync(bool reset = false)
    {
        Loading = true;
        Error = "";
        try
        {
            var (ok, err, items) = await Svc.Api.GetPopularAsync(_popularPage);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Loading = false;
                if (items != null)
                {
                    if (reset) PopularItems.Clear();
                    foreach (var it in items) PopularItems.Add(it);
                    _popularHasMore = items.Count >= 20;
                    OnPropertyChanged(nameof(PopularHasMore));
                    OnPropertyChanged(nameof(PopularCount));
                }
                else
                {
                    Error = err ?? "加载失败";
                }
            });
        }
        catch (Exception ex)
        {
            Loading = false;
            Error = ex.Message;
        }
    }

    // ----------------- 推荐 -----------------

    /// <summary>
    /// 推荐请求正在进行中。
    ///
    /// 为什么需要这个哨兵: 推荐流有两个入口会在启动时几乎同时触发 ——
    /// HomePage 构造函数里 `RecommendTab.IsChecked = true`(触发 Checked 事件),
    /// 以及 MainWindow 首次显示后的 `Init()`。两者都用"列表为空"判断是否需要加载,
    /// 而第一个请求还没回来时列表当然是空的, 于是会**并发发出两次完全相同的请求**
    /// (多一次网络往返 + 多一轮封面解码, 首屏反而更慢)。
    /// </summary>
    private bool _recommendLoading;

    /// <summary>
    /// 按 Bvid 去重追加。原来是外层遍历 + 内层线性查找的 O(n²) 写法,
    /// 推荐流滚动几十页后每追加一批都要全表扫描, 这里改成 HashSet 的 O(n)。
    /// </summary>
    private static void AppendDistinct(ObservableCollection<VideoItem> target, IEnumerable<VideoItem> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in target) seen.Add(v.Bvid);
        foreach (var v in items)
            if (!string.IsNullOrEmpty(v.Bvid) && seen.Add(v.Bvid)) target.Add(v);
    }

    private async Task LoadRecommendAsync(bool reset = false)
    {
        if (_recommendLoading) return; // 已有推荐请求在跑, 直接复用它的结果
        _recommendLoading = true;
        Loading = true;
        Error = "";
        try
        {
            var (ok, err, items) = await Svc.Api.GetRecommendAsync();
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Loading = false;
                if (items != null)
                {
                    if (reset) RecommendItems.Clear();
                    AppendDistinct(RecommendItems, items);
                    OnPropertyChanged(nameof(RecommendCount));
                }
                else
                {
                    Error = err ?? "加载失败";
                }
            });
        }
        catch (Exception ex)
        {
            Loading = false;
            Error = ex.Message;
        }
        finally
        {
            _recommendLoading = false;
        }
    }

    // ----------------- 加载更多(按当前 tab) -----------------

    public async Task LoadMoreAsync()
    {
        if (LoadingMore || Loading) return;

        // 直播单独走: 它的"还有没有下一页"是 LiveHasMore, 而且重新拉取是整页替换语义
        if (IsLiveTab)
        {
            if (!LiveHasMore) return;
            _livePage++;
            await LoadLiveAsync(reset: false);
            return;
        }

        if (IsPopularTab && !PopularHasMore) return;
        LoadingMore = true;
        try
        {
            if (IsPopularTab)
            {
                _popularPage++;
                var (ok, err, items) = await Svc.Api.GetPopularAsync(_popularPage);
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    LoadingMore = false;
                    if (items != null)
                    {
                        foreach (var it in items) PopularItems.Add(it);
                        _popularHasMore = items.Count >= 20;
                        OnPropertyChanged(nameof(PopularHasMore));
                        OnPropertyChanged(nameof(PopularCount));
                    }
                    else if (!string.IsNullOrEmpty(err)) Error = err;
                });
            }
            else if (IsRecommendTab)
            {
                var (ok, err, items) = await Svc.Api.GetRecommendAsync();
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    LoadingMore = false;
                    if (items != null && items.Count > 0)
                    {
                        AppendDistinct(RecommendItems, items);
                        OnPropertyChanged(nameof(RecommendCount));
                    }
                });
            }
        }
        catch (Exception ex)
        {
            LoadingMore = false;
            Error = ex.Message;
        }
    }

    // ----------------- 排行榜 -----------------

    private async Task LoadRankingAsync()
    {
        Loading = true;
        Error = "";
        RankingBlocked = false;
        try
        {
            var (ok, err, items) = await Svc.Api.GetRankingAsync();
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Loading = false;
                if (items != null)
                {
                    RankingItems.Clear();
                    foreach (var it in items) RankingItems.Add(it);
                    OnPropertyChanged(nameof(RankingCount));
                }
                else if (err != null && err.Contains("-352"))
                {
                    RankingBlocked = true;
                    Error = "排行榜接口被风控拦截, 可改用网页版查看";
                }
                else
                {
                    Error = err ?? "加载失败";
                }
            });
        }
        catch (Exception ex)
        {
            Loading = false;
            Error = ex.Message;
        }
    }

    // ------------------------------------------------------------ 右上角搜索卡
    //
    // 卡片长什么样、平移到哪、怎么展开, 全在 HomePage.xaml.cs 里 —— 这里只提供它要用的**数据**:
    // 关键词、搜索历史(带"展开更多")、热搜榜, 以及"用户选定了要搜什么"这一个出口。
    // 输入框和卡片是同一个元素, 所以这里没有"打开/关闭"这种状态, 那些属于视图。

    private string _searchKeyword = "";

    /// <summary>搜索卡输入框里的词</summary>
    public string SearchKeyword
    {
        get => _searchKeyword;
        set => SetProperty(ref _searchKeyword, value);
    }

    /// <summary>
    /// 用户选定了要搜的词(回车 / 点历史词条 / 点热搜条目)。
    /// 由 HomePage 接管: 收起卡片 + 跳到搜索页搜一次 —— 导航是窗口的事, VM 不碰窗口。
    /// </summary>
    public event Action<string>? SearchRequested;

    private void RequestSearch(string? keyword)
    {
        var kw = (keyword ?? "").Trim();
        if (kw.Length == 0) return;
        SearchRequested?.Invoke(kw);
    }

    public ICommand RunSearchCommand { get; }
    public ICommand UseSearchHistoryCommand { get; }
    public ICommand ClearSearchHistoryCommand { get; }
    public ICommand ToggleHistoryExpandCommand { get; }
    public ICommand UseHotSearchCommand { get; }

    // ---------------- 搜索历史 ----------------

    /// <summary>历史上限 20 条, 先只显示这么多, 其余藏到"展开更多"后面</summary>
    private const int HistoryPreviewCount = 10;

    /// <summary>搜索历史全量(直接引用全局服务里那一份, 增删/清空都会同步过来)</summary>
    public ObservableCollection<string> SearchHistory => Svc.SearchHistory.Items;

    /// <summary>当前**实际显示**的历史词条: 收起时前 10 条, 展开后全部</summary>
    public ObservableCollection<string> SearchHistoryShown { get; } = new();

    private bool _historyExpanded;

    /// <summary>是否已展开到全部历史。HomePage 据它决定卡片要长到多高。</summary>
    public bool HistoryExpanded
    {
        get => _historyExpanded;
        private set => SetProperty(ref _historyExpanded, value);
    }

    /// <summary>一条历史都没有时, 整个「搜索历史」区块(含标题和清空)都不显示</summary>
    public bool HasSearchHistory => Svc.SearchHistory.Items.Count > 0;

    /// <summary>历史条数超过预览数 → 显示"展开更多 / 收起"那一行</summary>
    public bool CanExpandSearchHistory => Svc.SearchHistory.Items.Count > HistoryPreviewCount;

    public string HistoryExpandText => _historyExpanded ? "收起" : "展开更多";

    /// <summary>
    /// 按当前的展开状态重建"实际显示的那一份"。
    ///
    /// 逐项比对而不是无脑 Clear + Add: 清空重填会让所有词条按钮重建一遍, 面板开着的时候
    /// 能看见闪一下(同类坑在收藏夹 chips 上踩过, 见 FavFolder.MediaCount 的注释)。
    /// </summary>
    private void RebuildHistoryShown()
    {
        var src = Svc.SearchHistory.Items;
        var count = _historyExpanded ? src.Count : Math.Min(HistoryPreviewCount, src.Count);

        var same = SearchHistoryShown.Count == count;
        if (same)
        {
            for (var i = 0; i < count; i++)
            {
                if (string.Equals(SearchHistoryShown[i], src[i], StringComparison.Ordinal)) continue;
                same = false;
                break;
            }
        }

        if (!same)
        {
            SearchHistoryShown.Clear();
            for (var i = 0; i < count; i++) SearchHistoryShown.Add(src[i]);
        }

        OnPropertyChanged(nameof(HasSearchHistory));
        OnPropertyChanged(nameof(CanExpandSearchHistory));
        OnPropertyChanged(nameof(HistoryExpandText));
    }

    // ---------------- 热搜 ----------------

    /// <summary>
    /// 热搜左栏(1~5)与右栏(6~10)。
    ///
    /// 接口是按名次 1~10 顺序给的, 界面要的是**两列、左列 1~5**。用两个列表各自绑定一个
    /// ItemsControl, 比"塞进 UniformGrid 再按行倒序插入"直白得多(后者要读者自己推一遍顺序)。
    /// </summary>
    public ObservableCollection<HotSearchItem> HotLeft { get; } = new();
    public ObservableCollection<HotSearchItem> HotRight { get; } = new();

    private bool _hotLoading;
    public bool HotLoading { get => _hotLoading; private set => SetProperty(ref _hotLoading, value); }

    private string _hotError = "";
    public string HotError { get => _hotError; private set => SetProperty(ref _hotError, value); }

    public bool HasHot => HotLeft.Count > 0 || HotRight.Count > 0;

    /// <summary>
    /// 热搜缓存有效期。榜单几分钟才动一次, 每展开一次都重拉纯属浪费; 也不能一直不刷 ——
    /// 十分钟是"看着仍是当下热度"与"请求够少"的折中。
    /// </summary>
    private static readonly TimeSpan HotCacheTtl = TimeSpan.FromMinutes(10);

    private DateTime _hotFetchedAt = DateTime.MinValue;

    /// <summary>
    /// 保证热搜有数据(首次展开、或缓存过期时重新拉)。失败只影响热搜这一块,
    /// 输入框和搜索历史照常可用。
    /// </summary>
    public async Task EnsureHotSearchAsync()
    {
        if (_hotLoading) return;
        if (HasHot && DateTime.UtcNow - _hotFetchedAt < HotCacheTtl) return;

        _hotLoading = true;
        HotLoading = true;
        HotError = "";
        try
        {
            var (ok, err, items) = await Svc.Api.GetHotSearchAsync();
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                HotLoading = false;
                if (items == null || items.Count == 0)
                {
                    // 拿不到就明说: "热搜"标题下面空着, 用户只会以为是卡住了
                    HotError = err != null ? "热搜暂时取不到: " + err : "热搜暂时取不到";
                    return;
                }

                var half = (items.Count + 1) / 2;
                HotLeft.Clear();
                HotRight.Clear();
                for (var i = 0; i < items.Count; i++)
                {
                    if (i < half) HotLeft.Add(items[i]);
                    else HotRight.Add(items[i]);
                }
                _hotFetchedAt = DateTime.UtcNow;
                OnPropertyChanged(nameof(HasHot));
            });
        }
        catch (Exception ex)
        {
            HotLoading = false;
            HotError = "热搜暂时取不到: " + ex.Message;
        }
        finally
        {
            _hotLoading = false;
        }
    }
}