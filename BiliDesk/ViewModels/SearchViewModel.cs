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

    public ObservableCollection<VideoItem> Results { get; } = new();

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
    public int ResultCount => Results.Count;

    /// <summary>是否为初始空状态(未搜索过)</summary>
    public bool IsIdle => !Searching && _lastKeyword == null && Results.Count == 0;

    // ---------------- 搜索历史 ----------------

    /// <summary>搜索历史, 新的在前。空关键词时在搜索框下面展示成标签。</summary>
    public ObservableCollection<string> History => Svc.SearchHistory.Items;

    /// <summary>
    /// 要不要显示搜索历史条。
    /// 判据是"输入框空着"而不是"有没有搜过" —— 搜过一次之后把框清空, 历史应该还在手边。
    /// </summary>
    public bool ShowHistoryStrip
        => _keyword.Trim().Length == 0 && Svc.SearchHistory.Items.Count > 0;

    public ICommand UseHistoryCommand { get; }
    public ICommand RemoveHistoryCommand { get; }
    public ICommand ClearHistoryCommand { get; }

    public ICommand SearchCommand { get; }
    public ICommand LoadMoreCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand OpenWebSearchCommand { get; }

    public SearchViewModel()
    {
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !string.IsNullOrWhiteSpace(Keyword));
        LoadMoreCommand = new AsyncRelayCommand(LoadMoreAsync);
        RetryCommand = new AsyncRelayCommand(SearchAsync);
        OpenWebSearchCommand = new RelayCommand(() =>
            Svc.Navigate?.OpenWebViewWindow(
                "https://search.bilibili.com/all?keyword=" + Uri.EscapeDataString(_lastKeyword ?? ""),
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
        Svc.SearchHistory.Changed += () => OnPropertyChanged(nameof(ShowHistoryStrip));
    }

    /// <summary>关键字变化时刷新搜索按钮可用性</summary>
    public void OnKeywordChanged()
        => ((AsyncRelayCommand)SearchCommand).RaiseCanExecuteChanged();

    public async Task SearchAsync()
    {
        var kw = Keyword.Trim();
        if (kw.Length == 0) return;

        Searching = true;
        Error = "";
        Blocked = false;
        _page = 1;
        _lastKeyword = kw;
        OnPropertyChanged(nameof(IsIdle));
        // 记进搜索历史(去重后挪到最前)。放在发起请求之前 ——
        // 被风控拦下的搜索也算"我搜过", 用户往往正是要照着重试一次。
        Svc.SearchHistory.Add(kw);

        try
        {
            var data = await Svc.Api.SearchAsync(kw, _page);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Searching = false;
                Results.Clear();
                foreach (var it in data.Items) Results.Add(it);
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
            Searching = false;
            Error = ex.Message;
        }
    }

    private async Task LoadMoreAsync()
    {
        if (LoadingMore || Searching || !HasMore || Blocked || _lastKeyword == null) return;
        LoadingMore = true;
        try
        {
            _page++;
            var data = await Svc.Api.SearchAsync(_lastKeyword, _page);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                LoadingMore = false;
                foreach (var it in data.Items) Results.Add(it);
                HasMore = data.HasMore;
                OnPropertyChanged(nameof(ResultCount));
            });
        }
        catch (Exception ex)
        {
            LoadingMore = false;
            Error = ex.Message;
        }
    }
}