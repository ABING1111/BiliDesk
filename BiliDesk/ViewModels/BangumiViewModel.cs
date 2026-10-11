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
/// 番剧详情 + **选集**页的 ViewModel。
///
/// 选集是这一页存在的理由: 番剧一季几十上百集, 播放器只吃单条 bv, 所以必须先把整季的
/// 分集表拉出来摆在用户面前, 让他挑一集再播。
/// </summary>
public class BangumiViewModel : ObservableObject
{
    private readonly BangumiItem _seed;
    private bool _loading;
    private string _error = "";
    private BangumiDetail? _detail;

    /// <summary>选集列表(正片, 按官方顺序)</summary>
    public ObservableCollection<BangumiEpisode> Episodes { get; } = new();

    /// <summary>正在播放 / 高亮的那一集。
    /// ★ setter 是 public 的: 选中态与"是否开播"是**两件事** —— 页面上高亮某集
    ///   不等于立刻播放它(将来记住上次看到第几集就会用到)。私有 setter 会把这两件事
    ///   绑死, 于是"切回来只恢复高亮、不自动开播"这种需求就做不了。
    /// </summary>
    private BangumiEpisode? _current;
    public BangumiEpisode? Current
    {
        get => _current;
        set
        {
            if (!SetProperty(ref _current, value)) return;
            // ★ 高亮挂在**每一集自己**的 IsCurrent 上(见 BangumiEpisode.IsCurrent), 所以换集时
            //   要把上一集摘掉 —— 只设新的不摘旧的, 两集会同时高亮, 看起来像选不中。
            if (_previousCurrent != null && !ReferenceEquals(_previousCurrent, value))
                _previousCurrent.IsCurrent = false;
            _previousCurrent = value;
            if (value != null) value.IsCurrent = true;
            OnPropertyChanged(nameof(HasCurrent));
        }
    }

    /// <summary>上一次高亮的集(换集时要把它的 IsCurrent 摘掉)。与 Current 分开存, 免得被新值顶掉。</summary>
    private BangumiEpisode? _previousCurrent;

    public bool HasCurrent => Current != null;

    public bool Loading { get => _loading; private set => SetProperty(ref _loading, value); }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }

    public BangumiDetail? Detail
    {
        get => _detail;
        private set
        {
            if (SetProperty(ref _detail, value))
            {
                OnPropertyChanged(nameof(HasDetail));
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(Evaluate));
                OnPropertyChanged(nameof(MetaLine));
                OnPropertyChanged(nameof(EpCountText));
            }
        }
    }

    public bool HasDetail => Detail != null;

    // ---- 详情各字段的直接投影(界面上不绑 Detail.XXX, 少一层 null 判断) ----

    public string Title => Detail?.Title is { Length: > 0 } t ? t : _seed.Title;
    public string Subtitle => Detail?.Subtitle ?? _seed.Subtitle;
    public string Cover => Detail?.Cover is { Length: > 0 } c ? c : _seed.Cover;

    public string Evaluate => Detail?.Evaluate ?? "";

    /// <summary>评分 / 地区 / 更新说明那一行; 没有就整行不显示</summary>
    public string MetaLine
    {
        get
        {
            if (Detail == null) return "";
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Detail.ScoreText)) parts.Add(Detail.ScoreText);
            if (!string.IsNullOrEmpty(Detail.Area)) parts.Add(Detail.Area);
            if (!string.IsNullOrEmpty(Detail.Renewal)) parts.Add(Detail.Renewal);
            return string.Join(" · ", parts);
        }
    }

    public string EpCountText => Episodes.Count > 0 ? "选集 · 共 " + Episodes.Count + " 集" : "选集";

    /// <summary>一集都没拉到(新番还没上正片 / 全是预告)。界面据此显示"暂无选集"而不是一片空白。</summary>
    public bool NoEpisodes => Detail != null && Episodes.Count == 0;

    // ---------------------------------------------------------------- 命令

    public ICommand PlayEpisodeCommand { get; }

    public BangumiViewModel(BangumiItem seed)
    {
        _seed = seed;
        PlayEpisodeCommand = new RelayCommand(p =>
        {
            if (p is BangumiEpisode ep) PlayEpisode(ep);
        });
    }

    public async Task InitAsync()
    {
        // 已经拉过就不重复请求: 用户在选集里播一集、看几集, 中途可能切走又切回来。
        if (Episodes.Count > 0) return;

        Loading = true;
        Error = "";
        try
        {
            var (ok, err, detail) = await Svc.Api.GetBangumiDetailAsync(_seed.SeasonId);
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Loading = false;
                if (!ok || detail == null)
                {
                    Error = err ?? "加载选集失败";
                    return;
                }

                Detail = detail;
                Episodes.Clear();
                foreach (var ep in detail.Episodes) Episodes.Add(ep);
                OnPropertyChanged(nameof(NoEpisodes));

                // ★ 默认高亮"更新到的那一集"而不是第 1 集: 追番页来的用户想看的是新的一集。
                //   没有进度信息时退回最后一集(连载中通常是最新一集), 已完结的退回最后一集也合理。
                Current = Episodes.LastOrDefault();
            });
        }
        catch (Exception ex)
        {
            Loading = false;
            Error = ex.Message;
        }
    }

    /// <summary>点某一集: 交给播放器播, 并把它标成当前集(选集列表里高亮)。</summary>
    private void PlayEpisode(BangumiEpisode ep)
    {
        if (string.IsNullOrEmpty(ep.Bvid)) return;
        Current = ep;
        Svc.Player.PlayVideo(ep.Bvid, Title + " - " + ep.DisplayTitle);
    }

    /// <summary>直接播最新一集(详情页右上角那个按钮)</summary>
    public void PlayLatest()
    {
        var ep = Current ?? Episodes.LastOrDefault();
        if (ep != null) PlayEpisode(ep);
    }
}