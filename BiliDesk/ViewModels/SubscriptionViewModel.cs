using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.ViewModels;

/// <summary>「我的订阅」页的三个 Tab。</summary>
public enum SubscriptionTab
{
    /// <summary>追番(PGC type=1)</summary>
    Bangumi = 0,
    /// <summary>追剧(PGC type=2: 电视剧 / 纪录片 / 电影)</summary>
    Teleplay = 1,
    /// <summary>订阅的 UGC 合集(collected/list 里 type=21 的项)</summary>
    Collections = 2,
}

/// <summary>
/// 「我的订阅」ViewModel(2026-11-27 新增)。
///
/// 三个 Tab 的**页面顺序**: 合集 / 追番 / 追剧(合集排第一, 用户 2026-11-27 要求;
/// 也是初始选中的 Tab)。枚举值本身与顺序无关, 只影响代码里的可读性。
///
/// 数据源:
///   追番 / 追剧 = /x/space/bangumi/follow/list (type 1/2, 带 total, 页码分页)
///   订阅的合集   = /x/v3/fav/folder/collected/list (type=21, 只按 has_more 无限滚动)
///
/// ★★ 两类数据的分页方式**必须分开**:
///   追番/追剧有接口给的 total, 用页码栏(IPagedViewModel, PagerBar);
///   合集列表的 count 混着「收藏的收藏夹」(type=11), 没有“只要合集”的服务端筛选,
///   拿 count 算总页数会把页码栏算错 —— 所以合集 tab 只按 has_more 一页页追加。
///
/// ★ 三个坑(项目里已经踩过, 别重犯):
///   ① 加载状态一律 finally 收口, 否则一次异常就把页永久卡在“加载中”;
///   ② 世代号丢弃过期响应(快速切 tab / 连点页码时旧响应不能盖新内容);
///   ③ “到底”判据用接口权威值: 追番/追剧用 total, 合集用 has_more。
/// </summary>
public class SubscriptionViewModel : ObservableObject, IPagedViewModel
{
    private const int BangumiPageSize = 20;
    private const int CollectionPageSize = 20;

    /// <summary>合集首页全是收藏夹(type=11)时, 最多自动往后翻几页找合集</summary>
    private const int CollectionAutoScanPages = 10;

    private SubscriptionTab _tab = SubscriptionTab.Collections;
    private long _mid;
    private bool _isLoggedIn;

    // ------------------------------------------------------------ Tab

    public SubscriptionTab Tab
    {
        get => _tab;
        private set
        {
            if (!SetProperty(ref _tab, value)) return;
            OnPropertyChanged(nameof(IsBangumiTab));
            OnPropertyChanged(nameof(IsTeleplayTab));
            OnPropertyChanged(nameof(IsCollectionsTab));
            OnPropertyChanged(nameof(CurrentPage));
            OnPropertyChanged(nameof(TotalPages));
            OnPropertyChanged(nameof(ShowPager));
            OnPropertyChanged(nameof(IsPaging));
            OnPropertyChanged(nameof(CanGoPrev));
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(CanChangePage));
            OnPropertyChanged(nameof(SummaryText));
            RecalcPageItems();
            TabChanged?.Invoke(value);
        }
    }

    public bool IsBangumiTab => Tab == SubscriptionTab.Bangumi;
    public bool IsTeleplayTab => Tab == SubscriptionTab.Teleplay;
    public bool IsCollectionsTab => Tab == SubscriptionTab.Collections;

    /// <summary>页面侧的 Tab 选中态变化通知(由 code-behind 写 IsChecked 后调)。</summary>
    public event Action<SubscriptionTab>? TabChanged;

    /// <summary>翻页 / 重载后请页面把列表滚回顶部。</summary>
    public event Action? ScrollToTopRequested;

    public bool IsLoggedIn
    {
        get => _isLoggedIn;
        private set
        {
            if (SetProperty(ref _isLoggedIn, value))
                OnPropertyChanged(nameof(LoginHint));
        }
    }

    public string LoginHint => "登录 B 站账号即可查看「我的订阅」";

    // ------------------------------------------------------------ 追番 / 追剧数据

    public ObservableCollection<BangumiItem> BangumiList { get; } = new();
    public ObservableCollection<BangumiItem> TeleplayList { get; } = new();
    public ObservableCollection<SpaceCollectionItem> Collections { get; } = new();

    private int _bangumiPage = 1, _bangumiTotal;
    private bool _bangumiBusy, _bangumiLoaded;
    private string _bangumiError = "";
    private int _bangumiGen;

    private int _teleplayPage = 1, _teleplayTotal;
    private bool _teleplayBusy, _teleplayLoaded;
    private string _teleplayError = "";
    private int _teleplayGen;

    private int _collectionPage;
    private bool _collectionBusy, _collectionLoadingMore, _collectionLoaded, _collectionHasMore = true;
    private string _collectionError = "";
    private int _collectionGen;

    public bool BangumiLoading => _bangumiBusy && BangumiList.Count == 0;
    public string BangumiError => _bangumiError;
    public bool ShowBangumiError => _bangumiError.Length > 0 && BangumiList.Count == 0;
    public bool BangumiEmpty => _bangumiLoaded && !_bangumiBusy
                                && BangumiList.Count == 0 && _bangumiError.Length == 0;

    public bool TeleplayLoading => _teleplayBusy && TeleplayList.Count == 0;
    public string TeleplayError => _teleplayError;
    public bool ShowTeleplayError => _teleplayError.Length > 0 && TeleplayList.Count == 0;
    public bool TeleplayEmpty => _teleplayLoaded && !_teleplayBusy
                                 && TeleplayList.Count == 0 && _teleplayError.Length == 0;

    public bool CollectionsLoading => _collectionBusy && !_collectionLoadingMore && Collections.Count == 0;
    public bool CollectionsLoadingMore => _collectionLoadingMore;
    public string CollectionError => _collectionError;
    public bool ShowCollectionError => _collectionError.Length > 0 && Collections.Count == 0;
    public bool CollectionsEmpty => _collectionLoaded && !_collectionBusy
                                    && Collections.Count == 0 && _collectionError.Length == 0;

    public string BangumiTabText => _bangumiTotal > 0 ? $"追番 {_bangumiTotal}" : "追番";
    public string TeleplayTabText => _teleplayTotal > 0 ? $"追剧 {_teleplayTotal}" : "追剧";
    public string CollectionsTabText => _collectionLoaded ? $"合集 {Collections.Count}" : "合集";

    /// <summary>Tab 行下面那行说明(当前 tab 的条数 / 加载进度)。</summary>
    public string SummaryText => Tab switch
    {
        SubscriptionTab.Bangumi => _bangumiTotal > 0 ? $"追番 · 共 {_bangumiTotal} 个" : "追番",
        SubscriptionTab.Teleplay => _teleplayTotal > 0 ? $"追剧 · 共 {_teleplayTotal} 个" : "追剧",
        _ => _collectionHasMore
            ? $"合集 · 已加载 {Collections.Count} 个"
            : $"合集 · 共 {Collections.Count} 个",
    };

    // ------------------------------------------------------------ 页码栏(IPagedViewModel)

    public ObservableCollection<PageItem> PageItems { get; } = new();

    /// <summary>当前 Tab 的当前页(合集 tab 没有页码, 恒为 1)。</summary>
    public int CurrentPage => Tab switch
    {
        SubscriptionTab.Bangumi => _bangumiPage,
        SubscriptionTab.Teleplay => _teleplayPage,
        _ => 1,
    };

    public int TotalPages => Tab switch
    {
        SubscriptionTab.Bangumi => PagerMath.TotalPages(_bangumiTotal, BangumiPageSize),
        SubscriptionTab.Teleplay => PagerMath.TotalPages(_teleplayTotal, BangumiPageSize),
        _ => 0,   // 合集 tab 只有 has_more 的滚动加载, 没有页码
    };

    public bool ShowPager => TotalPages > 1 && CurrentListCount > 0;

    public bool CanGoPrev => !IsPaging && CurrentPage > 1;
    public bool CanGoNext => !IsPaging && TotalPages > 0 && CurrentPage < TotalPages;
    public bool IsPaging => Tab switch
    {
        SubscriptionTab.Bangumi => _bangumiBusy && BangumiList.Count > 0,
        SubscriptionTab.Teleplay => _teleplayBusy && TeleplayList.Count > 0,
        _ => false,
    };
    public bool CanChangePage => !IsPaging;

    private int CurrentListCount => Tab switch
    {
        SubscriptionTab.Bangumi => BangumiList.Count,
        SubscriptionTab.Teleplay => TeleplayList.Count,
        _ => Collections.Count,
    };

    public ICommand GoToPageCommand { get; }
    public ICommand PrevPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand LoginCommand { get; }

    public SubscriptionViewModel()
    {
        GoToPageCommand = new RelayCommand(p =>
        {
            var page = p switch
            {
                int i => i,
                string s when int.TryParse(s, out var n) => n,
                _ => 0,
            };
            if (page > 0) _ = GoToPageAsync(page);
        });
        PrevPageCommand = new AsyncRelayCommand(() => GoToPageAsync(CurrentPage - 1));
        NextPageCommand = new AsyncRelayCommand(() => GoToPageAsync(CurrentPage + 1));
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        RetryCommand = new AsyncRelayCommand(RefreshAsync);
        LoginCommand = new RelayCommand(() => App.MainVm.OpenLoginWindow());

        // 集合变了要重算"空态 / Tab 条数 / 摘要"(WPF 不会自动重算计算属性)
        BangumiList.CollectionChanged += (_, _) => RaiseBangumiState();
        TeleplayList.CollectionChanged += (_, _) => RaiseTeleplayState();
        Collections.CollectionChanged += (_, _) => RaiseCollectionState();

        // 登录态变化(扫码成功/退出)时原地恢复 —— 否则登录窗口关了, 本页还停在"未登录"
        Svc.Session.Changed += OnSessionChanged;
    }

    private void OnSessionChanged()
    {
        if (!Svc.Session.HasLogin)
        {
            ResetAll();
            IsLoggedIn = false;
            return;
        }
        _ = InitAsync();
    }

    // ------------------------------------------------------------ 初始化 / 切 Tab

    /// <summary>
    /// 首次进入: 未登录只给提示; 已登录时三个 tab 各拉第一页
    /// (追番/追剧的 total、合集的第一批都在这一次里拿到, tab 条数立刻可见)。
    /// </summary>
    public async Task InitAsync()
    {
        IsLoggedIn = Svc.Session.HasLogin;
        if (!IsLoggedIn)
        {
            ResetAll();
            return;
        }

        _mid = GetMid();
        if (_mid <= 0)
        {
            _bangumiError = _teleplayError = _collectionError = "没法拿到账号信息, 请重新登录";
            RaiseBangumiState();
            RaiseTeleplayState();
            RaiseCollectionState();
            return;
        }

        await LoadCollectionsAsync(reset: true);
        await LoadBangumiAsync(1);
        await LoadTeleplayAsync(1);
    }

    /// <summary>切 Tab。没加载过(或首次失败)的 tab 进来时补拉第一页。</summary>
    public async Task SwitchTabAsync(SubscriptionTab tab)
    {
        Tab = tab;
        switch (tab)
        {
            case SubscriptionTab.Bangumi when !_bangumiLoaded && _bangumiError.Length == 0:
                await LoadBangumiAsync(1);
                break;
            case SubscriptionTab.Teleplay when !_teleplayLoaded && _teleplayError.Length == 0:
                await LoadTeleplayAsync(1);
                break;
            case SubscriptionTab.Collections when !_collectionLoaded && _collectionError.Length == 0:
                await LoadCollectionsAsync(reset: true);
                break;
        }
    }

    /// <summary>F5 / 刷新按钮: 重拉当前 tab(追番/追剧保留当前页, 合集从第一页重建)。</summary>
    public async Task RefreshAsync()
    {
        switch (Tab)
        {
            case SubscriptionTab.Bangumi:
                await LoadBangumiAsync(Math.Max(1, _bangumiPage));
                break;
            case SubscriptionTab.Teleplay:
                await LoadTeleplayAsync(Math.Max(1, _teleplayPage));
                break;
            default:
                await LoadCollectionsAsync(reset: true);
                break;
        }
    }

    /// <summary>合集 tab 滚到底: 追加下一页(type=21 的合集可能被 type=11 的收藏夹挤到后面几页)。</summary>
    public async Task LoadMoreCollectionsAsync()
    {
        if (Tab != SubscriptionTab.Collections || _collectionBusy || !_collectionHasMore) return;
        await LoadCollectionsAsync(reset: false);
    }

    private async Task GoToPageAsync(int page)
    {
        if (IsPaging || page < 1) return;
        if (TotalPages > 0 && page > TotalPages) return;
        if (page == CurrentPage) return;

        switch (Tab)
        {
            case SubscriptionTab.Bangumi:
                await LoadBangumiAsync(page);
                break;
            case SubscriptionTab.Teleplay:
                await LoadTeleplayAsync(page);
                break;
        }
    }

    // ------------------------------------------------------------ 追番 / 追剧

    private async Task LoadBangumiAsync(int page) => await LoadPgcAsync(type: 1, page);

    private async Task LoadTeleplayAsync(int page) => await LoadPgcAsync(type: 2, page);

    /// <summary>追番(type=1)与追剧(type=2)共用一套: 只有接口 type 与落点集合不同。</summary>
    private async Task LoadPgcAsync(int type, int page)
    {
        var isBangumi = type == 1;
        if (isBangumi ? _bangumiBusy : _teleplayBusy) return;
        if (_mid <= 0) return;
        if (page < 1) page = 1;

        var total = isBangumi ? _bangumiTotal : _teleplayTotal;
        var totalPages = PagerMath.TotalPages(total, BangumiPageSize);
        if (totalPages > 0 && page > totalPages) page = totalPages;

        var gen = isBangumi ? ++_bangumiGen : ++_teleplayGen;
        if (isBangumi) _bangumiBusy = true; else _teleplayBusy = true;
        if (isBangumi) _bangumiError = ""; else _teleplayError = "";
        RaiseBangumiState();
        RaiseTeleplayState();

        try
        {
            var (ok, err, items, fetchedTotal) =
                await Svc.Api.GetMyBangumiPageAsync(_mid, type, page, BangumiPageSize);
            if (gen != (isBangumi ? _bangumiGen : _teleplayGen)) return;

            if (!ok || items == null)
            {
                if (isBangumi) _bangumiError = err ?? "加载追番失败";
                else _teleplayError = err ?? "加载追剧失败";
                return;
            }

            var target = isBangumi ? BangumiList : TeleplayList;
            target.Clear();
            foreach (var it in items) target.Add(it);

            if (isBangumi)
            {
                _bangumiPage = page;
                _bangumiTotal = fetchedTotal;
                _bangumiLoaded = true;
            }
            else
            {
                _teleplayPage = page;
                _teleplayTotal = fetchedTotal;
                _teleplayLoaded = true;
            }

            if (page > 1) ScrollToTopRequested?.Invoke();
        }
        catch (Exception ex)
        {
            if (gen == (isBangumi ? _bangumiGen : _teleplayGen))
            {
                if (isBangumi) _bangumiError = ex.Message;
                else _teleplayError = ex.Message;
            }
        }
        finally
        {
            if (gen == (isBangumi ? _bangumiGen : _teleplayGen))
            {
                if (isBangumi) _bangumiBusy = false; else _teleplayBusy = false;
                // 失败后允许下次切回来重试(与 UserCenterPage 的“失败不置已加载”同一语义)
                if (isBangumi && _bangumiError.Length > 0 && BangumiList.Count == 0) _bangumiLoaded = false;
                if (!isBangumi && _teleplayError.Length > 0 && TeleplayList.Count == 0) _teleplayLoaded = false;
                RaiseBangumiState();
                RaiseTeleplayState();
            }
        }
    }

    // ------------------------------------------------------------ 订阅的合集

    private async Task LoadCollectionsAsync(bool reset)
    {
        if (_collectionBusy || _mid <= 0) return;
        if (!reset && !_collectionHasMore) return;

        var gen = ++_collectionGen;
        _collectionBusy = true;
        _collectionLoadingMore = !reset;
        _collectionError = "";
        if (reset)
        {
            Collections.Clear();
            _collectionPage = 0;
            _collectionHasMore = true;
            _collectionLoaded = false;
        }
        RaiseCollectionState();

        try
        {
            var page = reset ? 1 : _collectionPage + 1;
            var added = 0;
            var scanned = 0;

            while (true)
            {
                var (ok, err, items, hasMore) =
                    await Svc.Api.GetSubscribedCollectionsAsync(_mid, page, CollectionPageSize);
                if (gen != _collectionGen) return;

                if (!ok)
                {
                    // 已有内容时错误只作提示, 不盖掉列表; 没有内容才走整块错误态
                    _collectionError = err ?? "加载订阅的合集失败";
                    return;
                }

                if (items != null)
                {
                    foreach (var it in items)
                    {
                        Collections.Add(it);
                        added++;
                    }
                }

                _collectionPage = page;
                _collectionHasMore = hasMore;
                _collectionLoaded = true;

                // 首页整页都是收藏夹(type=11)时继续往后扫, 否则用户会看到空白且没有可滚动的内容
                var scanMore = reset && added == 0 && hasMore && ++scanned < CollectionAutoScanPages;
                if (!scanMore) break;
                page++;
            }
        }
        catch (Exception ex)
        {
            if (gen == _collectionGen) _collectionError = ex.Message;
        }
        finally
        {
            if (gen == _collectionGen)
            {
                _collectionBusy = false;
                _collectionLoadingMore = false;
                RaiseCollectionState();
            }
        }
    }

    // ------------------------------------------------------------ 状态通知

    private void RaiseBangumiState()
    {
        OnPropertyChanged(nameof(BangumiLoading));
        OnPropertyChanged(nameof(BangumiError));
        OnPropertyChanged(nameof(ShowBangumiError));
        OnPropertyChanged(nameof(BangumiEmpty));
        OnPropertyChanged(nameof(BangumiTabText));
        OnPropertyChanged(nameof(SummaryText));
        RaisePagerState();
    }

    private void RaiseTeleplayState()
    {
        OnPropertyChanged(nameof(TeleplayLoading));
        OnPropertyChanged(nameof(TeleplayError));
        OnPropertyChanged(nameof(ShowTeleplayError));
        OnPropertyChanged(nameof(TeleplayEmpty));
        OnPropertyChanged(nameof(TeleplayTabText));
        OnPropertyChanged(nameof(SummaryText));
        RaisePagerState();
    }

    private void RaiseCollectionState()
    {
        OnPropertyChanged(nameof(CollectionsLoading));
        OnPropertyChanged(nameof(CollectionsLoadingMore));
        OnPropertyChanged(nameof(CollectionError));
        OnPropertyChanged(nameof(ShowCollectionError));
        OnPropertyChanged(nameof(CollectionsEmpty));
        OnPropertyChanged(nameof(CollectionsTabText));
        OnPropertyChanged(nameof(SummaryText));
        RaisePagerState();
    }

    private void RaisePagerState()
    {
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(ShowPager));
        OnPropertyChanged(nameof(IsPaging));
        OnPropertyChanged(nameof(CanGoPrev));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanChangePage));
        RecalcPageItems();
    }

    /// <summary>按当前 tab 的总页数重建页码序列(0 = 省略号)。</summary>
    private void RecalcPageItems()
    {
        PageItems.Clear();
        if (TotalPages <= 1) return;
        foreach (var p in PageBar.Build(CurrentPage, TotalPages))
        {
            PageItems.Add(p == PageBar.Ellipsis
                ? new PageItem { IsEllipsis = true, Display = "…" }
                : new PageItem { Page = p, Display = p.ToString(), IsCurrent = p == CurrentPage });
        }
    }

    private void ResetAll()
    {
        _bangumiGen++;
        _teleplayGen++;
        _collectionGen++;

        BangumiList.Clear();
        TeleplayList.Clear();
        Collections.Clear();

        _bangumiPage = 1; _bangumiTotal = 0; _bangumiBusy = false; _bangumiLoaded = false; _bangumiError = "";
        _teleplayPage = 1; _teleplayTotal = 0; _teleplayBusy = false; _teleplayLoaded = false; _teleplayError = "";
        _collectionPage = 0; _collectionBusy = false; _collectionLoadingMore = false;
        _collectionLoaded = false; _collectionHasMore = true; _collectionError = "";

        RaiseBangumiState();
        RaiseTeleplayState();
        RaiseCollectionState();
    }

    private static long GetMid()
        => long.TryParse(Svc.Session.Current.DedeUserID, out var mid) ? mid : 0;
}
