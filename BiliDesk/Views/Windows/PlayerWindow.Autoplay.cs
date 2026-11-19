using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.Views;

/// <summary>
/// 播放器 · **自动连播**(2026-11-19)。
///
/// 分片职责: 「播完之后该播什么」的全部状态与动作。
///   · 队列: 合集/番剧的下一集(优先) + 打开视频时固定的「相关视频」列表;
///   · 倒计时: 播完后 5 秒换片, 「取消连播」随时能拦下;
///   · 会话: 用户手动换片一律作废重开(旧队列不再成立)。
///
/// ★ 为什么单独开一个分片: 它横跨 Playback(播完)/Season(合集与番剧选集)/Loading(加载与
///   相关视频)三个已有分片, 塞进任何一个都会让那个分片的职责变浑。判定部分是纯函数,
///   放在 <see cref="AutoPlayPolicy"/> 里可以单测。
///
/// ★★★ 与 <see cref="ResetForNewMedia"/> 的关系(最容易写错的一点):
///   本分片的状态**必须跨片保持** —— 一次连播会话本来就要跨好几条视频。所以这些字段
///   登记在 <c>PersistentFields</c> 而不是 <c>PerMediaStateFields</c>, 由 <see cref="EnsureAutoPlaySession"/>
///   在每次加载开头显式决定"继续还是重开"。
/// </summary>
public partial class PlayerWindow : FluentWindow
{
    /// <summary>连播队列里的一项(合集/番剧的一集, 或一条相关视频)。</summary>
    private sealed class AutoPlayEntry
    {
        public AutoPlayEntry(string bvid, string title, string cover)
        {
            Bvid = bvid;
            Title = title;
            Cover = cover ?? "";
        }

        public string Bvid { get; }
        public string Title { get; }
        /// <summary>封面地址 —— 倒计时那张圆角视频卡用它(Cover 附加属性负责解码与缓存)。</summary>
        public string Cover { get; }
    }

    // ---------------------------------------------------------------- 会话状态

    /// <summary>连播会话是否进行中(设置关掉 / 用户手动换片后为 false)。</summary>
    private bool _autoPlaySessionActive;

    /// <summary>会话起点: 打开播放器时那一条视频。判"用户是否手动换了片"的锚点之一。</summary>
    private string _autoPlayOriginBvid = "";

    /// <summary>
    /// 正在自动切过去的目标 bvid。
    /// ★ 它是"会话继续"的第二个合法锚点: 自动换片之后 <c>_currentBvid</c> 等于它,
    ///   所以那次换片不会被 <see cref="EnsureAutoPlaySession"/> 误判成"用户手动换片"。
    /// </summary>
    private string _autoPlayExpectedBvid = "";

    /// <summary>相关视频链是按哪个起点视频建的(防同一会话里重复建)。</summary>
    private string _autoPlayVideoChainBvid = "";

    /// <summary>相关视频链消费到第几条(下一条 = 这个下标)。</summary>
    private int _autoPlayVideoCursor;

    /// <summary>倒计时剩余秒数。</summary>
    private int _autoPlayCountdown;

    /// <summary>正在倒计时的那一项(已"看过"但**尚未提交**; 取消时它仍然留在队列里)。</summary>
    private AutoPlayEntry? _autoPlayPending;

    /// <summary>倒计时表。★ DispatcherTimer 字段由 <c>StopAllTimers</c> 反射自动停, 不必登记。</summary>
    private DispatcherTimer? _autoPlayCountdownTimer;

    /// <summary>合集 / 番剧的"下一集..."链(FIFO: 取走一条就出队)。</summary>
    private readonly List<AutoPlayEntry> _autoPlaySeasonChain = new();

    /// <summary>相关视频链(起点视频打开时固定下来, 只建一次; 按 <c>_autoPlayVideoCursor</c> 消费)。</summary>
    private readonly List<AutoPlayEntry> _autoPlayVideoChain = new();

    // ---------------------------------------------------------------- 会话生命周期

    /// <summary>
    /// 每次 <see cref="LoadVideoAsync"/> 开头判一次: 这次换片是"连播自己接上的"还是"用户手动换的"。
    /// 后者一律作废重开 —— 继续按旧队列播会跳到跟用户操作完全无关的东西上。
    /// </summary>
    private void EnsureAutoPlaySession()
    {
        if (_closing) return;

        if (!Svc.Settings.AutoPlayNext)
        {
            ResetAutoPlaySession();
            return;
        }

        if (_autoPlaySessionActive &&
            AutoPlayPolicy.ContinuesSession(_autoPlayOriginBvid, _autoPlayExpectedBvid, _currentBvid))
            return;

        ResetAutoPlaySession();
        _autoPlaySessionActive = true;
        _autoPlayOriginBvid = _currentBvid;
    }

    /// <summary>彻底作废连播会话(清队列、清倒计时)。</summary>
    private void ResetAutoPlaySession()
    {
        CancelAutoPlayCountdown();
        _autoPlaySessionActive = false;
        _autoPlayOriginBvid = "";
        _autoPlayExpectedBvid = "";
        _autoPlayVideoChainBvid = "";
        _autoPlayVideoCursor = 0;
        _autoPlaySeasonChain.Clear();
        _autoPlayVideoChain.Clear();
    }

    /// <summary>
    /// 按"当前这一集"重建合集链(合集与番剧共用)。
    ///
    /// ★ 每次 <see cref="ApplySeason"/> 都**替换**而不是追加: 换到第 2 集时剩下的就是
    ///   第 3..N 集, 与"第 1 集时记下的链已经出队一项"结果一致, 不会重复播。
    /// ★ 番剧走的是同一条链 —— 播放器把 PGC 分集表合成了一份 SeasonInfo
    ///   (见 <see cref="BuildBangumiSeason"/>), 所以"番剧播完自动下一集"零额外代码。
    /// </summary>
    private void ApplyAutoPlaySeasonChain(SeasonInfo? season)
    {
        _autoPlaySeasonChain.Clear();
        if (!_autoPlaySessionActive) return;
        if (season == null || _currentSeasonEpisode == null) return;

        var eps = season.Episodes;
        var idx = IndexOfEpisode(eps, _currentSeasonEpisode);
        if (idx < 0) return;   // 定位不上当前集 ⇒ 不猜"下一集是哪一集"

        for (var i = idx + 1; i < eps.Count; i++)
        {
            var ep = eps[i];
            if (string.IsNullOrEmpty(ep.Bvid)) continue;
            _autoPlaySeasonChain.Add(new AutoPlayEntry(ep.Bvid, ep.Title, ep.Cover));
        }
    }

    private static int IndexOfEpisode(IReadOnlyList<SeasonEpisode> eps, SeasonEpisode target)
    {
        for (var i = 0; i < eps.Count; i++)
        {
            if (ReferenceEquals(eps[i], target)) return i;
            if (string.Equals(eps[i].Bvid, target.Bvid, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    /// <summary>
    /// 把「起点视频的相关视频列表」固定成连播队列。
    ///
    /// ★ 只认会话**起点**那一条: 自动换到第 2 条之后, 它自己的相关列表与原队列无关,
    ///   拿它覆盖会让"固定列表顺序播放"退化成"推荐的推荐"(越播越偏)。
    /// ★ 每次都先判 <c>_autoPlayVideoChainBvid</c>: 同一个起点只建一次。
    /// </summary>
    private void ApplyAutoPlayVideoChain(string bvid, IReadOnlyList<VideoItem> items)
    {
        if (!_autoPlaySessionActive) return;
        if (!string.Equals(_autoPlayOriginBvid, bvid, StringComparison.Ordinal)) return;
        if (string.Equals(_autoPlayVideoChainBvid, bvid, StringComparison.Ordinal)) return;

        _autoPlayVideoChainBvid = bvid;
        _autoPlayVideoChain.Clear();
        _autoPlayVideoCursor = 0;
        foreach (var it in items)
        {
            if (string.IsNullOrEmpty(it.Bvid)) continue;
            if (string.Equals(it.Bvid, bvid, StringComparison.Ordinal)) continue;
            _autoPlayVideoChain.Add(new AutoPlayEntry(it.Bvid, it.Title, it.Cover));
        }
    }

    /// <summary>设置页动了「自动连播」开关(见 SettingsStore.AutoPlayNextChanged)。</summary>
    private void OnAutoPlayNextSettingChanged()
    {
        if (_closing) return;

        if (!Svc.Settings.AutoPlayNext)
        {
            // 关掉: 立刻收手 —— 不能让它在用户关掉开关之后还换片。
            CancelAutoPlayCountdown();
            if (_ended) ShowEndNormalState();
            ResetAutoPlaySession();
            return;
        }

        // 打开: 当前这条视频还没进会话(之前开关是关的) ⇒ 以它为新会话起点,
        // 并把合集链补上(ApplySeason 早就跑过了, 那时会话还没开)。
        EnsureAutoPlaySession();
        ApplyAutoPlaySeasonChain(_season);

        // 相关视频可能根本没取(之前「显示相关视频」关着) ⇒ 补取一次当队列。
        if (!_isLive && !_isLocalPlayback && !string.IsNullOrEmpty(_currentBvid))
            _ = LoadRelatedAsync(_currentBvid);
    }

    // ---------------------------------------------------------------- 取下一条

    /// <summary>看一眼下一条是什么(不消费)。合集链优先, 其次相关视频链。</summary>
    private bool PeekNextAutoPlay(out AutoPlayEntry? entry)
    {
        if (_autoPlaySeasonChain.Count > 0)
        {
            entry = _autoPlaySeasonChain[0];
            return true;
        }
        if (_autoPlayVideoCursor >= 0 && _autoPlayVideoCursor < _autoPlayVideoChain.Count)
        {
            entry = _autoPlayVideoChain[_autoPlayVideoCursor];
            return true;
        }
        entry = null;
        return false;
    }

    /// <summary>
    /// 真正消费掉这一条。
    /// ★ 只在"确实切过去了"时调用 —— 用户点了「取消连播」那一条必须还留在队列里,
    ///   否则重播之后再播完就凭空少了一条。
    /// </summary>
    private void CommitAutoPlay(AutoPlayEntry entry)
    {
        if (_autoPlaySeasonChain.Count > 0 &&
            string.Equals(_autoPlaySeasonChain[0].Bvid, entry.Bvid, StringComparison.Ordinal))
        {
            _autoPlaySeasonChain.RemoveAt(0);
            return;
        }
        if (_autoPlayVideoCursor >= 0 && _autoPlayVideoCursor < _autoPlayVideoChain.Count &&
            string.Equals(_autoPlayVideoChain[_autoPlayVideoCursor].Bvid, entry.Bvid, StringComparison.Ordinal))
        {
            _autoPlayVideoCursor++;
        }
    }

    // ---------------------------------------------------------------- 倒计时

    /// <summary>
    /// 播完之后: 有下一条就起 5 秒倒计时, 否则保持原来的「已播完 + 重播」。
    /// 由 <see cref="OnEndReached"/> 的"真播完"分支末尾调用。
    /// </summary>
    private void TryStartAutoPlayCountdown()
    {
        if (_closing) return;

        if (!Svc.Settings.AutoPlayNext || _isLive || _isLocalPlayback || !_autoPlaySessionActive)
        {
            ShowEndNormalState();
            return;
        }
        if (!PeekNextAutoPlay(out var entry) || entry == null)
        {
            ShowEndNormalState();
            return;
        }
        StartAutoPlayCountdown(entry);
    }

    private void StartAutoPlayCountdown(AutoPlayEntry entry)
    {
        if (_closing) return;

        CancelAutoPlayCountdown();
        _autoPlayPending = entry;
        _autoPlayCountdown = AutoPlayPolicy.CountdownSeconds;

        if (EndNormalPanel != null) EndNormalPanel.Visibility = Visibility.Collapsed;
        if (EndAutoPanel != null) EndAutoPanel.Visibility = Visibility.Visible;
        ApplyAutoPlayCover(entry.Cover);
        if (EndAutoTitleText != null)
            EndAutoTitleText.Text = string.IsNullOrWhiteSpace(entry.Title) ? entry.Bvid : entry.Title;
        UpdateAutoPlayCountdownText();

        _autoPlayCountdownTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _autoPlayCountdownTimer.Tick -= OnAutoPlayCountdownTick;
        _autoPlayCountdownTimer.Tick += OnAutoPlayCountdownTick;
        _autoPlayCountdownTimer.Start();

        WriteLibVLCDebug($"[AutoPlay] 倒计时开始 → {entry.Bvid} {entry.Title}\n");
    }

    private void UpdateAutoPlayCountdownText()
    {
        if (EndAutoCountdownText == null) return;
        EndAutoCountdownText.Text = $"{_autoPlayCountdown} 秒后自动播放";
    }

    /// <summary>
    /// 给倒计时那张圆角视频卡贴封面。
    /// ★ 空封面**整张卡收掉**: 留一个灰底圆角块比不显示更像"加载坏了"。
    /// ★ 走 <see cref="Cover"/> 附加属性(而不是自己 LoadAsync + ImageBrush):
    ///   解码宽度按元素实时宽度算、缓存与离屏回收都由它统一管 ——
    ///   这里是一次性的浮层, 没必要另起一套。
    /// </summary>
    private void ApplyAutoPlayCover(string? cover)
    {
        if (EndAutoCover == null) return;
        var has = !string.IsNullOrWhiteSpace(cover);
        EndAutoCover.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        Cover.SetSource(EndAutoCover, has ? cover : null);
    }

    /// <summary>收掉倒计时(停表 + 清 pending), 不动 UI 的两块面板 —— 那块由调用方决定。</summary>
    private void CancelAutoPlayCountdown()
    {
        _autoPlayCountdownTimer?.Stop();
        _autoPlayPending = null;
        _autoPlayCountdown = 0;
    }

    /// <summary>恢复「已播完 + 重播」的普通态。</summary>
    private void ShowEndNormalState()
    {
        if (EndAutoPanel != null) EndAutoPanel.Visibility = Visibility.Collapsed;
        if (EndNormalPanel != null) EndNormalPanel.Visibility = Visibility.Visible;
    }

    private void OnAutoPlayCountdownTick(object? sender, EventArgs e)
    {
        if (_closing)
        {
            CancelAutoPlayCountdown();
            return;
        }

        // 倒计时期间用户可能: 关掉设置 / 点重播 / 拖进度条 / 换了片 ——
        // 这几件事都会让 `_ended` 变回 false(或开关变 false), 一律立刻收手。
        if (!Svc.Settings.AutoPlayNext || !_ended)
        {
            CancelAutoPlayCountdown();
            ShowEndNormalState();
            return;
        }

        var entry = _autoPlayPending;
        if (entry == null)
        {
            CancelAutoPlayCountdown();
            return;
        }

        _autoPlayCountdown = AutoPlayPolicy.NextCountdown(_autoPlayCountdown);
        if (AutoPlayPolicy.ShouldAdvance(_autoPlayCountdown))
        {
            _ = SwitchToAutoPlayEntryAsync(entry);
            return;
        }
        UpdateAutoPlayCountdownText();
    }

    /// <summary>「取消连播」: 收掉倒计时, 回到「已播完」—— 这一条仍然留在队列里。</summary>
    private void OnAutoPlayCancelClick(object sender, RoutedEventArgs e)
    {
        CancelAutoPlayCountdown();
        ShowEndNormalState();
        WriteLibVLCDebug("[AutoPlay] 用户取消连播(该条仍留在队列)\n");
    }

    /// <summary>「立即播放」: 跳过剩余倒计时。</summary>
    private void OnAutoPlayPlayNowClick(object sender, RoutedEventArgs e)
    {
        var entry = _autoPlayPending;
        if (entry == null) return;
        _ = SwitchToAutoPlayEntryAsync(entry);
    }

    // ---------------------------------------------------------------- 换片

    /// <summary>
    /// 真正切到下一条: 停流 → 换 bvid → 重建每片状态 → 加载。
    ///
    /// ★ 复用 <c>_switchingSeasonEpisode</c> 闸门: 它与"点合集/选集里的一集"是同一件事
    ///   (换 bvid), 共用一个闸门才能挡住"倒计时刚响、用户又点了一集"导致两条
    ///   LoadVideoAsync 叠跑(后回来的覆盖前一个 ⇒ 点了 A 却播了 B)。
    /// ★ `_mp.Stop()` 走 <see cref="StopPlayerAsync"/>: 它会阻塞约 1 秒(等解码/vout 线程退出),
    ///   放 UI 线程上就是"倒计时结束那一刻窗口定格一下"。
    /// ★ 不调 <c>Svc.Player.PlayVideo</c>: 那条路在窗口不可复用时会**新建一个播放器窗口**,
    ///   而连播必须留在本窗口(与 SwitchToSeasonEpisodeAsync 同一个理由)。
    /// </summary>
    private async Task SwitchToAutoPlayEntryAsync(AutoPlayEntry entry)
    {
        if (_closing || entry == null || string.IsNullOrEmpty(entry.Bvid)) return;
        if (_switchingSeasonEpisode) return;

        _switchingSeasonEpisode = true;
        try
        {
            // 倒计时文字先换成"正在播放…", 免得停流那 ~1 秒里界面上还钉着"1 秒后自动播放"。
            if (EndAutoCountdownText != null) EndAutoCountdownText.Text = "正在播放下一条…";

            CommitAutoPlay(entry);
            _autoPlayExpectedBvid = entry.Bvid;
            CancelAutoPlayCountdown();

            WriteLibVLCDebug($"[AutoPlay] 切换 → {entry.Bvid} {entry.Title}\n");

            await StopPlayerAsync();
            if (_closing) return;

            _currentBvid = entry.Bvid;
            _isLocalPlayback = false;
            _isLive = false;
            _liveRoomId = 0;
            ResetForNewMedia();
            await LoadVideoAsync();
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
        }
        finally
        {
            _switchingSeasonEpisode = false;
        }
    }
}
