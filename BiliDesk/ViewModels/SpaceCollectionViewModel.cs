using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.ViewModels;

/// <summary>
/// 「合集 / 系列」详情页 ViewModel。
///
/// 合集与系列共用这一个页面: 列表接口不同(seasons_archives_list / series/archives),
/// 但翻页、页码栏、视频卡片完全一致, 由 <see cref="SpaceCollectionItem.Kind"/> 决定走哪条。
/// </summary>
public class SpaceCollectionViewModel : ObservableObject, IPagedViewModel
{
    private const int PageSize = 30;

    private readonly SpaceCollectionItem _item;
    private readonly string _upName;

    private int _currentPage = 1;
    private int _totalPages;
    private long _totalVideos;
    private bool _loading = true;
    private string _error = "";
    private bool _loadingPage;
    /// <summary>请求代次: 快速翻页时只认最后一次的结果</summary>
    private int _loadGen;

    public SpaceCollectionViewModel(SpaceCollectionItem item, string upName = "")
    {
        _item = item ?? throw new ArgumentNullException(nameof(item));
        _upName = upName ?? "";

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
    }

    public ObservableCollection<VideoItem> Videos { get; } = new();
    public ObservableCollection<PageItem> PageItems { get; } = new();

    /// <summary>翻页后请求页面把列表滚回顶部(由 SpaceCollectionPage 订阅)</summary>
    public event Action? ScrollToTopRequested;

    // ---------------- 顶部信息 ----------------
    public string Title => _item.DisplayTitle;
    public string Cover => _item.Cover;
    public string Intro => _item.Intro;
    public bool HasIntro => !string.IsNullOrWhiteSpace(Intro);
    public string MetaLine => $"{_item.KindText} · {_item.CountText}";

    public string Summary => TotalVideos > 0
        ? $"共 {VideoItem.FormatCount(TotalVideos)} 个视频"
        : "视频列表";

    // ---------------- 加载 / 错误 ----------------
    public bool Loading
    {
        get => _loading;
        private set => SetProperty(ref _loading, value);
    }

    public string Error
    {
        get => _error;
        private set
        {
            if (!SetProperty(ref _error, value)) return;
            OnPropertyChanged(nameof(ShowErrorOverlay));
        }
    }

    /// <summary>只有列表为空时才用整块错误/空态提示盖住列表, 翻页失败不影响已显示的内容。</summary>
    public bool ShowErrorOverlay => Error.Length > 0 && Videos.Count == 0;

    // ---------------- 分页 ----------------
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
            OnPropertyChanged(nameof(Summary));
        }
    }

    public bool CanGoPrev => !_loadingPage && CurrentPage > 1;
    public bool CanGoNext => !_loadingPage && TotalPages > 0 && CurrentPage < TotalPages;
    public bool IsPaging => _loadingPage;
    public bool CanChangePage => !_loadingPage;
    public bool ShowPager => TotalPages > 1 && Videos.Count > 0;

    public ICommand RefreshCommand { get; }
    public ICommand GoToPageCommand { get; }
    public ICommand PrevPageCommand { get; }
    public ICommand NextPageCommand { get; }

    public Task InitAsync() => LoadPageAsync(1);

    private async Task RefreshAsync()
    {
        await LoadPageAsync(Videos.Count > 0 ? CurrentPage : 1);
    }

    /// <summary>
    /// 加载指定页并替换当前列表。代次规则与 UP 主页投稿页相同:
    /// 快速点 1→2→3 时, 旧响应晚回来不能盖掉新页。
    /// </summary>
    private async Task LoadPageAsync(int page)
    {
        if (_item.Id <= 0) return;
        if (page < 1) page = 1;
        if (TotalPages > 0 && page > TotalPages) page = TotalPages;

        var gen = ++_loadGen;
        _loadingPage = true;
        if (Videos.Count == 0) Loading = true;
        Error = "";
        OnPropertyChanged(nameof(CanGoPrev));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(IsPaging));
        OnPropertyChanged(nameof(CanChangePage));
        try
        {
            var (ok, err, items, total) = await Svc.Api.GetCollectionVideosAsync(
                _item.Mid, _item.Kind, _item.Id, page, PageSize);

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (gen != _loadGen) return;

                Videos.Clear();
                if (items != null)
                {
                    foreach (var it in items)
                    {
                        // 系列接口不返回 UP 名; 用进入详情页时带的 UP 名补齐,
                        // 否则卡片第二行会只剩一个日期前的点。
                        if (string.IsNullOrEmpty(it.Author)) it.Author = _upName;
                        Videos.Add(it);
                    }
                }

                if (!ok && Videos.Count == 0)
                {
                    Error = err ?? "加载失败";
                    TotalPages = 0;
                    RecalcPages(page);
                    return;
                }

                CurrentPage = page;
                TotalVideos = total > 0 ? total : Videos.Count;
                if (total > 0)
                    TotalPages = (int)Math.Ceiling(total / (double)PageSize);
                else
                    TotalPages = Videos.Count >= PageSize ? page + 1 : page;

                if (Videos.Count == 0) Error = "这里暂时没有视频";
                RecalcPages(page);
                OnPropertyChanged(nameof(ShowPager));
                OnPropertyChanged(nameof(ShowErrorOverlay));
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
            Loading = false;
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
        if (page == CurrentPage) return;

        // 先把高亮移到目标页, 失败时再回滚
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
}
