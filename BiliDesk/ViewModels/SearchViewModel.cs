using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.ViewModels;

/// <summary>搜索页 ViewModel</summary>
public class SearchViewModel : ObservableObject
{
    private string _keyword = "";
    private bool _searching;
    private bool _loadingMore;
    private bool _blocked;
    private bool _hasMore;
    private string _error = "";
    private int _page = 1;
    private string? _lastKeyword;

    /// <summary>
    /// 搜索代次。**每次发起新搜索 +1**; 请求回来时代次对不上就整包丢弃。
    ///
    /// 为什么必须有它: 搜索有三个入口(搜索页回车 / 首页搜索卡 / 历史词条), 而它们各自是独立的
    /// 命令实例, 防重入只管得住自己那一个 —— 于是"上一次搜索的响应"完全可能在新搜索清空列表
    /// **之后**才回来, 把旧关键词的结果追加到新结果里(用户看到的就是"结果不准确 / 混着上一次的")。
    /// 分页请求同理: 翻页途中又搜了一次, 那一页的结果必须丢掉, 否则一次点击就往新列表里灌 20 条旧数据。
    /// </summary>
    private int _generation;

    public ObservableCollection<VideoItem> Results { get; } = new();

    /// <summary>番剧结果(2026-10-11 新增)。与 <see cref="Results"/> 互斥, 见 <see cref="ContentKind.Bangumi"/>。</summary>
    public ObservableCollection<BangumiItem> BangumiResults { get; } = new();

    /// <summary>关键词。清空输入框时要顺带决定"要不要显示搜索历史", 所以这里是自定义 setter。</summary>
    public string Keyword
    {
        get => _keyword;
        set
        {
            if (SetProperty(ref _keyword, value)) OnPropertyChanged(nameof(ShowHistoryStrip));
        }
    }

    public bool Searching { get => _searching; private set => SetProperty(ref _searching, value); }
    public bool LoadingMore { get => _loadingMore; private set => SetProperty(ref _loadingMore, value); }
    public bool Blocked { get => _blocked; private set => SetProperty(ref _blocked, value); }
    public bool HasMore { get => _hasMore; private set => SetProperty(ref _hasMore, value); }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }
    // ★ ResultCount 的定义在下面"搜索类别"一节里(它要按类别取不同的集合),
    //   这里**不要**再写一个只看 Results 的版本, 否则会把它遮住。

    // ---------------- 搜索类别(视频 / 直播间 / 番剧) ----------------

    private ContentKind _kind = Svc.Settings.SearchKind;

    /// <summary>
    /// 当前搜的是视频还是直播间。切换时**重新搜一次**(带着同一个关键词) ——
    /// 两类内容来自完全不同的接口, 不重搜就只能看到上一类的残留结果。
    /// </summary>
    public ContentKind Kind
    {
        get => _kind;
        set
        {
            if (!SetProperty(ref _kind, value)) return;
            // ★ 记住用户停在哪个类别(2026-11-12 用户报"番剧依然无法被搜索"):
            //   首页搜索框跳过来时用的是默认类别(视频), 用户每次都得手动切一次「番剧」。
            //   落盘之后, 切过一次就一直是那个类别。
            Svc.Settings.SetSearchKind(value);
            OnPropertyChanged(nameof(IsVideoKind));
            OnPropertyChanged(nameof(IsLiveKind));
            OnPropertyChanged(nameof(IsBangumiKind));
            OnPropertyChanged(nameof(IdleHint));
            OnPropertyChanged(nameof(ResultCount));
            // 换类别 = 换一套结果: 清空旧结果并重搜(没搜过关键词就只是清一下)
            _generation++;          // 让在途请求的结果作废, 免得旧类别的响应灌进新列表
            Results.Clear();
            BangumiResults.Clear(); // ★ 番剧也要清 —— 否则切回视频/直播时海报还挂在那儿
            Error = "";
            Blocked = false;
            HasMore = false;
            if (_lastKeyword != null) _ = SearchAsync();
            else OnPropertyChanged(nameof(IsIdle));
        }
    }

    /// <summary>XAML 里三个 RadioButton 的绑定(IsChecked 只支持 bool, 所以给三个派生属性)</summary>
    public bool IsVideoKind
    {
        get => _kind == ContentKind.Video;
        set { if (value) Kind = ContentKind.Video; }
    }

    public bool IsLiveKind
    {
        get => _kind == ContentKind.Live;
        set { if (value) Kind = ContentKind.Live; }
    }

    /// <summary>番剧类别(2026-10-11 新增)</summary>
    public bool IsBangumiKind
    {
        get => _kind == ContentKind.Bangumi;
        set { if (value) Kind = ContentKind.Bangumi; }
    }

    /// <summary>当前类别是不是"结果走另一个集合"的番剧 —— 页面据此切列表</summary>
    public bool IsBangumiSearch => _kind == ContentKind.Bangumi;

    /// <summary>空状态提示语跟着类别走 —— "搜索你想看的视频"在直播/番剧类别下就说不通了</summary>
    public string IdleHint => _kind switch
    {
        ContentKind.Live => "输入关键词, 搜索你想看的直播间",
        ContentKind.Bangumi => "输入关键词, 搜索你想看的番剧",
        _ => "输入关键词, 搜索你想看的视频"
    };

    /// <summary>番剧结果条数: 跟着类别取对应的集合, 否则番剧类别永远显示 0 条</summary>
    public int ResultCount => IsBangumiSearch ? BangumiResults.Count : Results.Count;

    /// <summary>
    /// 番剧结果去重追加。
    ///
    /// ★ 不能直接用 <c>VideoList.AppendDistinct</c> —— 那个是按 <c>Bvid</c> 去重的,
    ///   而番剧**没有 bvid**(它按 season_id 寻址), 拿 Bvid 去比会把每一条都当成"新的",
    ///   等于完全没去重。所以这里按 season_id 自己写一份。
    /// </summary>
    private static void AppendBangumiDistinct(
        ObservableCollection<BangumiItem> target, System.Collections.Generic.IEnumerable<BangumiItem> items)
    {
        foreach (var it in items)
        {
            var dup = false;
            foreach (var existing in target)
            {
                if (existing.SeasonId == it.SeasonId) { dup = true; break; }
            }
            if (!dup) target.Add(it);
        }
    }

    /// <summary>是否为初始空状态(未搜索过)</summary>
    public bool IsIdle => !Searching && _lastKeyword == null && Results.Count == 0 && BangumiResults.Count == 0;

    // ---------------- 搜索历史 ----------------

    /// <summary>搜索历史, 新的在前。空关键词时在搜索框下面展示成标签。</summary>
    public ObservableCollection<string> History => Svc.SearchHistory.Items;

    /// <summary>
    /// 要不要显示搜索历史条。
    /// 判据是"输入框空着"而不是"有没有搜过" —— 搜过一次之后把框清空, 历史应该还在手边。
    /// </summary>
    public bool ShowHistoryStrip
        => _keyword.Trim().Length == 0 && Svc.SearchHistory.Items.Count > 0;

    /// <summary>
    /// 内容框里有没有搜索历史可显示(决定"搜索历史"那一段是否露出来)。
    ///
    /// ★ 2026-10-03 加: 内容框里现在还有"视频 / 直播间"类别标签, 所以**不能**再靠
    ///   "历史为空就不展开卡片"来省这块地方 —— 历史为空时应当只藏掉历史那一段,
    ///   类别标签照常露出来。见 SearchPage.OpenCard 的说明。
    /// </summary>
    public bool HasHistory => Svc.SearchHistory.Items.Count > 0;

    public ICommand UseHistoryCommand { get; }
    public ICommand RemoveHistoryCommand { get; }
    public ICommand ClearHistoryCommand { get; }

    public ICommand SearchCommand { get; }
    public ICommand LoadMoreCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand OpenWebSearchCommand { get; }

    public SearchViewModel()
    {
        // ★ 初始类别来自设置(上次停在哪个 tab), 所以要把三个投影属性各通知一次 ——
        //   否则 RadioButton 组的 IsChecked 全绑到 false, 界面表现为"三个 tab 一个都没选"。
        //   (XAML 里三个按钮都没写 IsChecked="True", 选中态完全靠这两条绑定。)
        OnPropertyChanged(nameof(IsVideoKind));
        OnPropertyChanged(nameof(IsLiveKind));
        OnPropertyChanged(nameof(IsBangumiKind));

        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !string.IsNullOrWhiteSpace(Keyword));
        LoadMoreCommand = new AsyncRelayCommand(LoadMoreAsync);
        RetryCommand = new AsyncRelayCommand(SearchAsync);
        OpenWebSearchCommand = new RelayCommand(() =>
            Svc.Navigate?.OpenWebViewWindow(
                // 网页版的搜索类型也分视频/直播/番剧, 跟着当前类别走才对得上
                (_kind switch
                 {
                     ContentKind.Live => "https://search.bilibili.com/live?keyword=",
                     ContentKind.Bangumi => "https://search.bilibili.com/bangumi?keyword=",
                     _ => "https://search.bilibili.com/all?keyword="
                 }) + Uri.EscapeDataString(_lastKeyword ?? ""),
                "搜索 - 网页版"));

        // 点历史标签 = 填回关键词再搜一次
        UseHistoryCommand = new RelayCommand(p =>
        {
            if (p is not string kw || kw.Trim().Length == 0) return;
            Keyword = kw; // setter 里会把历史条收起来
            _ = SearchAsync();
        });
        RemoveHistoryCommand = new RelayCommand(p => { if (p is string kw) Svc.SearchHistory.Remove(kw); });
        ClearHistoryCommand = new RelayCommand(() => Svc.SearchHistory.Clear());
        // 增删/清空历史都会改变"要不要显示这一条", 也可能让历史变空
        Svc.SearchHistory.Changed += () =>
        {
            OnPropertyChanged(nameof(ShowHistoryStrip));
            OnPropertyChanged(nameof(HasHistory));
        };
    }

    /// <summary>关键字变化时刷新搜索按钮可用性</summary>
    public void OnKeywordChanged()
        => ((AsyncRelayCommand)SearchCommand).RaiseCanExecuteChanged();

    public async Task SearchAsync()
    {
        var kw = Keyword.Trim();
        if (kw.Length == 0) return;

        // 新的一代: 在这之前发出的搜索/翻页请求回来时都会被丢掉(见 _generation)
        _generation++;
        var gen = _generation;

        Searching = true;
        Error = "";
        Blocked = false;
        _page = 1;
        HasMore = false;
        LoadingMore = false;
        _lastKeyword = kw;
        OnPropertyChanged(nameof(IsIdle));
        // 记进搜索历史(去重后挪到最前)。放在发起请求之前 ——
        // 被风控拦下的搜索也算"我搜过", 用户往往正是要照着重试一次。
        Svc.SearchHistory.Add(kw);

        try
        {
            var data = await Svc.Api.SearchAsync(kw, _page, _kind);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (gen != _generation) return;   // 已经有更新的一次搜索接管了, 这份结果作废
                Searching = false;
                Results.Clear();
                BangumiResults.Clear();
                // ★ 两套集合按类别分流: 番剧结果在 data.BangumiItems, 不在 data.Items。
                //   都往 Results 灌的话, 番剧那一类会是空的(点开又播不了, 本来也不该走播放)。
                if (IsBangumiSearch) AppendBangumiDistinct(BangumiResults, data.BangumiItems);
                else VideoList.AppendDistinct(Results, data.Items);
                HasMore = data.HasMore;
                Blocked = data.Blocked;
                Error = data.Error ?? "";
                OnPropertyChanged(nameof(ResultCount));

                if (data.Blocked)
                {
                    // 被风控: 提示并给出网页搜索入口
                    Svc.Toast.Show("搜索被风控拦截, 试试网页版搜索");
                }
            });
        }
        catch (Exception ex)
        {
            if (gen != _generation) return;
            Searching = false;
            Error = ex.Message;
        }
    }

    /// <summary>
    /// 翻下一页。**由搜索页滚到底部自动调用**(见 SearchPage.OnResultScrollChanged), 手动
    /// 「加载更多」按钮也走它。
    /// </summary>
    public async Task LoadMoreAsync()
    {
        if (LoadingMore || Searching || !HasMore || Blocked || _lastKeyword == null) return;

        var gen = _generation;
        var next = _page + 1;      // ★ 页码**成功之后**才推进: 失败/异常时留在原页, 下次重试的是同一页
        LoadingMore = true;
        try
        {
            var data = await Svc.Api.SearchAsync(_lastKeyword, next, _kind);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (gen != _generation) return;   // 翻页途中又搜了一次 → 这一页属于上一次搜索, 丢掉
                LoadingMore = false;
                if (data.Blocked || !string.IsNullOrEmpty(data.Error))
                {
                    // 失败要说出来: 以前这里只更新 HasMore, 风控/报错完全是静默的
                    Blocked = data.Blocked;
                    Error = data.Error ?? "加载更多失败";
                    return;
                }
                _page = next;
                // ★ 同样按类别分流(见 SearchAsync 里那条注释): 翻页追加的也是各自那个集合。
                if (IsBangumiSearch) AppendBangumiDistinct(BangumiResults, data.BangumiItems);
                else VideoList.AppendDistinct(Results, data.Items);   // ★ 去重: 搜索排序会变, 同一稿件可能又给一遍
                HasMore = data.HasMore;
                OnPropertyChanged(nameof(ResultCount));
            });
        }
        catch (Exception ex)
        {
            if (gen != _generation) return;
            LoadingMore = false;
            Error = ex.Message;
        }
    }
}