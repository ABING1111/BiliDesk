using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;

namespace BiliDesk.ViewModels;

/// <summary>
/// 一个互动消息 Tab(回复我的 / @我的 / 收到的赞)的列表状态。
///
/// ★ 为什么每个 Tab 各持一份而不是共用一个 VM:
///   用户在这三个 Tab 之间来回切是很常见的动作, 共用一份就意味着"切回来要重新拉一次,
///   滚动位置和已加载的几页全丢"。三个实例各自缓存自己的列表, 切回来立刻就是原样。
/// </summary>
public class MsgFeedViewModel : ObservableObject
{
    public MsgTab Kind { get; }

    public ObservableCollection<MsgFeedItem> Items { get; } = new();

    private bool _loading;
    private bool _loadingMore;
    private string _error = "";
    private bool _loaded;
    private bool _hasMore;
    private long _nextId;
    private long _nextTime;

    public bool Loading { get => _loading; private set => SetProperty(ref _loading, value); }
    public bool LoadingMore { get => _loadingMore; private set => SetProperty(ref _loadingMore, value); }
    public string Error
    {
        get => _error;
        private set
        {
            if (!SetProperty(ref _error, value)) return;
            OnPropertyChanged(nameof(ShowError));
            OnPropertyChanged(nameof(ShowInlineError));
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>是否已经成功加载过一次(切 Tab 回来不再重复拉)</summary>
    public bool Loaded => _loaded;

    /// <summary>空态提示: 加载完、没错误、也没有条目</summary>
    public bool IsEmpty => _loaded && !_loading && _error.Length == 0 && Items.Count == 0;

    /// <summary>
    /// 整块错误提示要不要显示。
    /// ★ 只有**一条都没加载出来**时才占整块: 翻页失败时列表里明明有内容,
    ///   一块居中的错误提示会盖在卡片上(探针实测过), 那时只该在末尾给一行小字。
    /// </summary>
    public bool ShowError => _error.Length > 0 && Items.Count == 0;

    /// <summary>末尾的"加载更多失败"小字: 有内容 + 有错误</summary>
    public bool ShowInlineError => _error.Length > 0 && Items.Count > 0;

    /// <summary>标题: 用在空态文案里("还没有人回复你"这种)</summary>
    public string EmptyText => Kind switch
    {
        MsgTab.Reply => "还没有人回复你",
        MsgTab.At => "还没有人 @ 你",
        MsgTab.Like => "还没有收到赞",
        _ => "暂无消息"
    };

    public ICommand RefreshCommand { get; }
    public ICommand LoadMoreCommand { get; }

    public MsgFeedViewModel(MsgTab kind)
    {
        Kind = kind;
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        LoadMoreCommand = new RelayCommand(() => _ = LoadMoreAsync());
    }

    /// <summary>强制重新加载第一页</summary>
    public Task RefreshAsync()
    {
        _loaded = false;
        return LoadFirstPageAsync();
    }

    /// <summary>首次进入时加载(已加载过就什么都不做)</summary>
    public Task EnsureLoadedAsync()
        => _loaded ? Task.CompletedTask : LoadFirstPageAsync();

    private async Task LoadFirstPageAsync()
    {
        if (Loading) return;

        Loading = true;
        Error = "";
        try
        {
            var (ok, err, page) = await Svc.Api.GetMsgFeedAsync(Kind);
            if (!ok || page == null)
            {
                Error = err ?? "加载失败";
                return;
            }

            Items.Clear();
            foreach (var item in page.Items) Items.Add(item);

            _hasMore = page.HasMore;
            _nextId = page.NextId;
            _nextTime = page.NextTime;
            _loaded = true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Loading = false;
            RaiseDerived();
        }
    }

    /// <summary>集合/错误变化后推一次派生属性</summary>
    public async Task LoadMoreAsync()
    {
        if (Loading || LoadingMore || !_hasMore || !_loaded) return;

        LoadingMore = true;
        try
        {
            var (ok, err, page) = await Svc.Api.GetMsgFeedAsync(Kind, _nextId, _nextTime);
            if (!ok || page == null)
            {
                // 翻页失败不覆盖已有内容, 只把提示挂出来
                Error = err ?? "加载更多失败";
                return;
            }

            foreach (var item in page.Items) Items.Add(item);
            _hasMore = page.HasMore;
            _nextId = page.NextId;
            _nextTime = page.NextTime;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            LoadingMore = false;
            RaiseDerived();
        }
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowError));
        OnPropertyChanged(nameof(ShowInlineError));
    }
}
