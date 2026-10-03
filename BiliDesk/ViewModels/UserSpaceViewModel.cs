using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.ViewModels;

/// <summary>
/// UP 主空间 ViewModel。
/// 接口链路有降级策略(acc/info → card → 旧接口), 即使单个接口被风控也能展示
/// </summary>
public class UserSpaceViewModel : ObservableObject
{
    private long _mid;
    private string _name = "";
    private string _face = "";
    private string _sign = "";
    private int _level;
    private long _follower;
    private long _following;
    private long _videoCount;
    private bool _loading;
    private string _error = "";
    private int _page = 1;
    private bool _hasMore = true;
    private bool _loadingMore;

    public ObservableCollection<VideoItem> Videos { get; } = new();

    public long Mid { get => _mid; private set => SetProperty(ref _mid, value); }
    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public string Face { get => _face; private set => SetProperty(ref _face, value); }
    public string Sign { get => _sign; private set => SetProperty(ref _sign, value); }
    public int Level { get => _level; private set => SetProperty(ref _level, value); }
    public long Follower { get => _follower; private set => SetProperty(ref _follower, value); }
    public long Following { get => _following; private set => SetProperty(ref _following, value); }
    public long VideoCount { get => _videoCount; private set => SetProperty(ref _videoCount, value); }
    public bool Loading { get => _loading; private set => SetProperty(ref _loading, value); }
    public bool LoadingMore { get => _loadingMore; private set => SetProperty(ref _loadingMore, value); }
    public bool HasMore { get => _hasMore; private set => SetProperty(ref _hasMore, value); }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }

    public string FollowerText => VideoItem.FormatCount(Follower);
    public string FollowingText => VideoItem.FormatCount(Following);
    public string VideoCountText => VideoItem.FormatCount(VideoCount);

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

    /// <summary>未关注 = 显示强调色「+ 关注」; 已关注 = 切成淡色「已关注」</summary>
    public bool IsNotFollowed => !IsFollowed;

    public string FollowLabel => IsFollowed ? "已关注" : "+ 关注";

    public ICommand ToggleFollowCommand { get; }

    public ICommand RefreshCommand { get; }
    public ICommand LoadMoreCommand { get; }
    public ICommand FollowCommand { get; }

    public UserSpaceViewModel()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        LoadMoreCommand = new AsyncRelayCommand(LoadMoreAsync);
        FollowCommand = new AsyncRelayCommand(FollowAsync);
        ToggleFollowCommand = new AsyncRelayCommand(ToggleFollowAsync);
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
            var videosTask = LoadVideosAsync(reset: true);
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
                    OnPropertyChanged(nameof(FollowerText));
                    OnPropertyChanged(nameof(FollowingText));
                    OnPropertyChanged(nameof(VideoCountText));
                    OnPropertyChanged(nameof(Face));
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

    public async Task LoadVideosAsync(bool reset)
    {
        if (_mid <= 0) return;
        // ★ 刷新必须把页码复位。只清列表不复位的话, 点「刷新」会从上次翻到的页码继续往后取 ——
        // 最新一页(第 1 页)被跳过, 连点几次甚至会请求到空页, 看起来像"刷新把投稿刷没了"。
        if (reset) _page = 1;

        var (ok, err, items) = await Svc.Api.GetSpaceVideosAsync(_mid, _page, 30);
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Loading = false;
            if (items != null)
            {
                if (reset) Videos.Clear();
                foreach (var it in items) Videos.Add(it);
                _hasMore = items.Count >= 30;
                _page++;
                if (Videos.Count == 0) Error = err ?? "该 UP 主暂无投稿视频";
            }
            else
            {
                Error = err ?? "加载失败";
            }
        });
    }

    public async Task LoadMoreAsync()
    {
        if (LoadingMore || Loading || !_hasMore) return;
        LoadingMore = true;
        try
        {
            await LoadVideosAsync(reset: false);
        }
        finally
        {
            LoadingMore = false;
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
