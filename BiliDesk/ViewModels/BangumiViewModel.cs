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
                OnPropertyChanged(nameof(IsTeleplay));
                OnPropertyChanged(nameof(FollowButtonText));
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

    // ---------------------------------------------------------------- 追番(2026-11-19)

    private bool _isFollowed;

    /// <summary>
    /// 当前登录用户是否已追番。
    /// ★ 数据来自 <see cref="Services.BangumiFollowCache"/>(查「我的追番」列表) ——
    ///   详情接口的 user_status.follow 实测追番成功后仍是 0, 不能用(见那个类的说明)。
    /// ★ 界面用 <c>FollowToggleButtonStyle</c> 的 DataTrigger 直接绑它(见 BangumiPage.xaml),
    ///   所以必须发变更通知 —— 追番成功后按钮要立刻变「已追番」并置灰。
    /// </summary>
    public bool IsFollowed
    {
        get => _isFollowed;
        private set
        {
            if (!SetProperty(ref _isFollowed, value)) return;
            OnPropertyChanged(nameof(FollowButtonText));
        }
    }

    /// <summary>追番按钮上的字: 已追 / 未追。</summary>
    public string FollowButtonText => IsTeleplay
        ? (IsFollowed ? "已追剧" : "+ 追剧")
        : (IsFollowed ? "已追番" : "+ 追番");

    /// <summary>
    /// 这一季是不是追剧类(电影 / 纪录片 / 电视剧 / 综艺)。
    /// ★ 详情还没回来时用列表项自带的 season_type 兜底, 否则按钮会先闪一下"追番"。
    /// </summary>
    public bool IsTeleplay => Detail?.IsTeleplay ?? _seed.IsTeleplay;

    private bool _followBusy;

    /// <summary>追番请求在途(挡住连点两下发两次相反请求)。</summary>
    public bool FollowBusy
    {
        get => _followBusy;
        private set
        {
            if (!SetProperty(ref _followBusy, value)) return;
            OnPropertyChanged(nameof(CanToggleFollow));
        }
    }

    /// <summary>追番入口是否可点(在途时置灰; 不看登录态 —— 未登录点了给 Toast 说明原因)。</summary>
    public bool CanToggleFollow => !_followBusy;

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

            // 追番态单独查一次(不挡选集显示): 详情里的 user_status.follow 不可信,
            // 真正的判据是「我的追番」列表里有没有这一季(见 BangumiFollowCache 的说明)。
            _ = RefreshFollowStateAsync(_seed.SeasonId);
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
        // ★ 把整季分集表一起交给播放器(2026-11-19): 播放器据此
        //   ① 在信息栏给出「选集」入口(在播放器里就能换集),
        //   ② 播完自动接下一集(受「自动连播」开关控制)。
        //   PGC 的分集表只存在于 pgc/view/web/season, 播放器单集加载那条接口拿不到,
        //   所以必须由这里递进去。
        Svc.Player.PlayVideo(ep.Bvid, Title + " - " + ep.DisplayTitle, BuildPlaylist());
    }

    /// <summary>把当前这一季的集表打包给播放器。</summary>
    private BangumiPlaylist BuildPlaylist() => new()
    {
        SeasonId = Detail?.SeasonId ?? _seed.SeasonId,
        Title = Title,
        Episodes = Episodes.ToList()
    };

    /// <summary>直接播最新一集(详情页右上角那个按钮)</summary>
    public void PlayLatest()
    {
        var ep = Current ?? Episodes.LastOrDefault();
        if (ep != null) PlayEpisode(ep);
    }

    /// <summary>
    /// 补查"这一季我追了吗"(后台, 不挡选集显示)。
    /// ★ 期间可能已经换了另一部番(内嵌页被复用) ⇒ 回来先比对 seasonId 再写状态。
    /// </summary>
    private async Task RefreshFollowStateAsync(long seasonId)
    {
        if (seasonId <= 0) return;
        var followed = await Svc.BangumiFollow.IsFollowedAsync(seasonId, IsTeleplay);
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if ((Detail?.SeasonId ?? _seed.SeasonId) != seasonId) return;
            IsFollowed = followed;
        });
    }

    /// <summary>
    /// 追番 / 取消追番(2026-11-19)。
    ///
    /// ★ 只按**接口返回值**翻转本地状态: 不做乐观更新 —— 追番人数与列表都跟着服务端走,
    ///   本地抢先翻转再失败的话, 用户会以为追上了、回头发现列表里没有。
    /// ★ 成功后同时做三件事:
    ///   ① <see cref="Svc.BangumiFollow"/>.SetFollowed: 详情页下次打开直接命中缓存;
    ///   ② <see cref="BangumiFollowSignal.Bump"/>: 回到追番页时「我的追番」会重新拉一次
    ///      (HomePage 在重新可见时按这个版本号补拉, 见 HomeViewModel.RefreshBangumiIfFollowChanged);
    ///   ③ Toast 给一句明确反馈。
    /// ★ 未登录走 Toast 而不是禁用按钮: 用户至少知道"为什么点了没反应"。
    /// </summary>
    public async Task ToggleFollowAsync()
    {
        if (FollowBusy) return;
        if (!Svc.Session.HasLogin) { Svc.Toast.Show(IsTeleplay ? "追剧需要先登录" : "追番需要先登录"); return; }
        if (Detail == null) return;

        var seasonId = Detail.SeasonId;
        var target = !IsFollowed;
        var teleplay = IsTeleplay;
        var verb = teleplay ? "追剧" : "追番";
        FollowBusy = true;
        try
        {
            var (ok, err) = await Svc.Api.SetBangumiFollowAsync(seasonId, target);
            if (!ok)
            {
                Svc.Toast.Show((target ? verb + "失败: " : "取消" + verb + "失败: ") + (err ?? "未知错误"));
                return;
            }

            IsFollowed = target;
            Svc.BangumiFollow.SetFollowed(seasonId, target, teleplay);
            BangumiFollowSignal.Bump();
            Svc.Toast.Show(target ? "已" + verb : "已取消" + verb);
        }
        finally
        {
            FollowBusy = false;
        }
    }
}