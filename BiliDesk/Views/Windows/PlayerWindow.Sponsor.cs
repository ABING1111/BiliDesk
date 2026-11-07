using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace BiliDesk.Views;

public partial class PlayerWindow : FluentWindow
{
    // ------------------------------------------------------------ Toast

    private System.Windows.Threading.DispatcherTimer? _localToastTimer;
    private void OnToast(string msg) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_closing) return;
        LocalToastText.Text = msg;
        LocalToastBorder.Visibility = Visibility.Visible;
        // ★ 淡入前必须先摘掉上一次的动画 + 归位: 连发两条 Toast 时(投币成功又来一条提示),
        //   上一次的淡出动画可能还挂着。不摘的话新淡入的起点读到的是"动画当前值"
        //   (可能已经是 0), 于是这条 Toast 是从 0 淡到 0.95 —— **等于看不见**。
        LocalToastBorder.BeginAnimation(OpacityProperty, null);
        LocalToastBorder.Opacity = 0.95;
        LocalToastBorder.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.95, 1, TimeSpan.FromMilliseconds(160))
            { FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop });
        _localToastTimer?.Stop();
        _localToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        _localToastTimer.Tick += (_, _) =>
        {
            _localToastTimer.Stop();
            var a = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280))
            { FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop };
            a.Completed += (_, _) =>
            {
                // ★ 淡出结束后把动画摘掉并归位: 不摘的话 Opacity 被动画钉在 0,
                //   下一条 Toast 即使 Visible 也完全不显示(整块透明)。
                LocalToastBorder.BeginAnimation(OpacityProperty, null);
                LocalToastBorder.Opacity = 0.95;
                LocalToastBorder.Visibility = Visibility.Collapsed;
            };
            LocalToastBorder.BeginAnimation(OpacityProperty, a);
        };
        _localToastTimer.Start();
    }));

    // ------------------------------------------------------------ 跳过赞助片段(SponsorBlock)

    /// <summary>
    /// 发起片段查询。**必须在"取播放地址"之前调用** —— 它和后面的网络请求/合流下载并行跑,
    /// 等我们真要用它时结果基本已经在手里了。没有在线语境(直播/本地文件)或功能没开时
    /// 一个请求都不发, 直接给空任务。
    /// </summary>
    private Task<IReadOnlyList<SponsorSegment>> FetchSponsorSegmentsAsync()
    {
        CancelSponsorFetch();
        var none = Task.FromResult<IReadOnlyList<SponsorSegment>>(Array.Empty<SponsorSegment>());
        if (!Svc.Settings.SponsorBlockEnabled) return none;
        if (_isLive || _isLocalPlayback) return none;
        if (_currentCid <= 0 || string.IsNullOrEmpty(_currentBvid)) return none;

        _sponsorCts = new CancellationTokenSource();
        return Svc.SponsorBlock.GetRawAsync(_currentBvid, _currentCid, _sponsorCts.Token);
    }

    /// <summary>换片/关窗时掐掉还没回来的查询(否则上一个视频的结果会写进新视频的状态)</summary>
    private void CancelSponsorFetch()
    {
        var cts = _sponsorCts;
        _sponsorCts = null;
        if (cts == null) return;
        try { cts.Cancel(); } catch { /* 已经取消了也无所谓 */ }
        try { cts.Dispose(); } catch { }
    }

    /// <summary>当前视频的身份串(bvid|cid), 用于判断"这份片段数据还是不是当前这个视频的"</summary>
    private string SponsorKey() => _currentBvid + "|" + _currentCid;

    private static async Task<IReadOnlyList<SponsorSegment>> SafeAwait(
        Task<IReadOnlyList<SponsorSegment>> task)
    {
        try { return await task; }
        catch { return Array.Empty<SponsorSegment>(); }
    }

    /// <summary>
    /// 起播前落地片段数据 —— **绝不等待**。
    ///
    /// 历史(2026-10-30 修): 这里原来最多等 800ms, 为的是拿到"开场就是广告"时能做空降。
    /// 但实测服务端 www.bsbsb.top 首包要 **2.6 秒**, 800ms 必然等满且拿不到数据 ——
    /// 等于每条视频白等 0.8 秒, 空降却一次都没成过。
    /// 现在只在"查询已经返回"时做空降; 否则起播优先, 由 500ms 心跳按正常时机补跳
    /// (顶多先看到半秒广告)。
    /// </summary>
    private async Task BeforePlayApplySponsorAsync(Task<IReadOnlyList<SponsorSegment>> task)
    {
        var key = SponsorKey();
        if (task.IsCompleted)
        {
            ApplySponsorRaw(await SafeAwait(task), applyInitialDrop: true);
            return;
        }

        // 还没回来 ⇒ 先起播, 结果回来后再补(见 ApplySponsorLaterAsync)
        _ = ApplySponsorLaterAsync(task, key);
    }

    /// <summary>超时后补落地。只认"还是同一个视频"的那份数据 —— 用户可能已经换片了</summary>
    private async Task ApplySponsorLaterAsync(Task<IReadOnlyList<SponsorSegment>> task, string key)
    {
        var raw = await SafeAwait(task);
        // _ = 显式丢弃: 这里只是把 UI 更新排进队列, 不等它(在 async 方法里不加会报 CS4014)
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing || key != SponsorKey()) return;
            // 这时候早就起播了, 再"空降"会把看着的画面硬拽走 —— 交给心跳按正常时机跳
            ApplySponsorRaw(raw, applyInitialDrop: false);
        }));
    }

    /// <summary>
    /// 落地片段数据。
    /// <paramref name="applyInitialDrop"/> = true 时才考虑"起播空降": 如果有片段直接盖住视频开头
    /// (起点 ≤ 0.6 秒), 起播就落在它的末尾 —— 复用"切清晰度后恢复进度"那条已验证的路径
    /// (_resumeAfterSwitchMs), 而不是先播 0.2 秒再跳走(那样看起来就是画面闪了一下)。
    /// </summary>
    private void ApplySponsorRaw(IReadOnlyList<SponsorSegment> raw, bool applyInitialDrop)
    {
        _sponsorRaw = raw ?? Array.Empty<SponsorSegment>();
        RecalcSponsorActive();
        if (!applyInitialDrop || _sponsorActive.Count == 0) return;

        SponsorSegment? coverStart = null;
        foreach (var s in _sponsorActive)
        {
            if (s.StartSec > 0.6) continue;
            if (coverStart == null || s.EndMs > coverStart.EndMs) coverStart = s;
        }
        if (coverStart == null) return;

        _sponsorHandled.Add(coverStart.Uuid);
        _resumeAfterSwitchMs = coverStart.EndMs;
        _resumeByStartTime = false;
        // 空降也要说一声: 不给提示的话用户只会觉得"这视频怎么从第 14 秒开始",
        // 根本想不到是被跳了、更想不到还能撤回。_lastSkipSeg 一并记上, 撤回逻辑就能复用。
        _lastSkipSeg = coverStart;
        ShowSkipNotice(
            $"已跳过{coverStart.Label}  {VideoItem.FormatSeconds((int)coverStart.EndSec)}");
        WriteLibVLCDebug(
            $"[Sponsor] 空降 {coverStart.Category} {coverStart.StartSec:0.##}~{coverStart.EndSec:0.##}s\n");
    }

    /// <summary>
    /// 按当前设置从**原始数据**里重算"该跳哪些", 并同步刷新进度条上的色块。
    /// 用原始数据算, 所以用户在设置页改类别时不需要再问一次第三方。
    /// </summary>
    private void RecalcSponsorActive()
    {
        _sponsorActive = Svc.Settings.SponsorBlockEnabled
            ? SponsorBlockService.Select(_sponsorRaw, _currentCid, Svc.Settings.SponsorBlockCategories)
            : Array.Empty<SponsorSegment>();
        RenderSponsorMarks();
    }

    /// <summary>
    /// 设置页动了"跳过赞助片段": 关掉要**下一次心跳立刻**停跳, 改类别要立刻重算。
    /// 两者都用手里已有的原始数据完成, 不产生新请求。
    /// </summary>
    private void OnSponsorSettingsChanged() => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_closing) return;
        RecalcSponsorActive();
        if (!Svc.Settings.SponsorBlockEnabled) HideSkipNotice();
    }));

    /// <summary>
    /// 心跳里的跳过判定。五道闸门缺一不可:
    ///   · <c>_pendingSeekMs</c> —— 上一次跳转还没确认时再跳会把目标改来改去, 让看门狗去重发;
    ///   · <c>_isSeeking</c> —— 用户正拖着进度条, 这时跳是跟用户抢;
    ///   · <c>_isLive/_isLocalPlayback</c> —— 没有在线语境, 也没有片段数据;
    ///   · <c>_mp.Length &lt;= 0</c> —— 还没探到时长, Time 不可信;
    ///   · <see cref="SponsorMinRemainMs"/> —— 快到片段尾巴了, 跳过去只前进一点点, 观感像画面抽了一下。
    /// </summary>
    private void MaybeSkipSponsorSegment()
    {
        if (_closing || _mp == null) return;
        if (_sponsorActive.Count == 0) return;
        if (_isLive || _isLocalPlayback) return;
        if (!Svc.Settings.SponsorBlockEnabled) return;
        if (_ended || _isSeeking) return;
        if (_pendingSeekMs >= 0) return;
        if (_mp.Length <= 0) return;

        var t = _mp.Time;
        foreach (var seg in _sponsorActive)
        {
            if (t < seg.StartMs || t >= seg.EndMs) continue;
            if (seg.EndMs - t <= SponsorMinRemainMs) return;   // 只剩个头, 不值当
            if (!_sponsorHandled.Add(seg.Uuid)) return;        // 这条已经处理过(跳过或撤回)
            SkipSponsorSegment(seg, t);
            return;
        }
    }

    /// <summary>
    /// 真正跳过去。走的是**和用户拖进度条完全同一条路**(MarkSeekIssued + 设 Time):
    /// 钉住进度条、启动重发看门狗、seek 静音(避免"画面还没回来声音先到了")全都在那里面,
    /// 自己另写一套必然漏掉其中几样。
    /// </summary>
    private void SkipSponsorSegment(SponsorSegment seg, long fromMs)
    {
        var target = seg.EndMs;
        _lastSkipSeg = seg;
        MarkSeekIssued(target);
        try { _mp.Time = target; } catch { /* 播放器可能刚好失效 */ }
        ShowSkipNotice($"已跳过{seg.Label}  {VideoItem.FormatSeconds((int)((target - fromMs) / 1000.0))}");
        WriteLibVLCDebug(
            $"[Sponsor] skip {seg.Category} {seg.StartSec:0.##}~{seg.EndSec:0.##}s (from {fromMs / 1000.0:0.##}s)\n");
    }

    /// <summary>显示跳过提示(带「撤回」)。常驻 6 秒 —— 短了来不及反应, 长了挡画面。</summary>
    private void ShowSkipNotice(string text)
    {
        if (SkipNoticeBorder == null || SkipNoticeText == null) return;
        SkipNoticeText.Text = text;
        SkipNoticeBorder.BeginAnimation(OpacityProperty, null);
        SkipNoticeBorder.Opacity = 0;
        SkipNoticeBorder.Visibility = Visibility.Visible;
        SkipNoticeBorder.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));

        // 复用同一个定时器实例(只改间隔), 不要每次提示都 new 一个: DispatcherTimer 一旦
        // Start 就会被 Dispatcher 长期持有, 反复建新的等于反复往 Dispatcher 上挂对象。
        if (_skipNoticeTimer == null)
        {
            _skipNoticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _skipNoticeTimer.Tick += (_, _) =>
            {
                _skipNoticeTimer?.Stop();
                HideSkipNotice();
            };
        }
        else
        {
            _skipNoticeTimer.Stop();
        }
        _skipNoticeTimer.Start();
    }

    /// <summary>收起跳过提示(淡出后真正隐藏, 并把基准透明度归零 —— 下次才能再从 0 淡入)</summary>
    private void HideSkipNotice()
    {
        _skipNoticeTimer?.Stop();
        if (SkipNoticeBorder == null || SkipNoticeBorder.Visibility != Visibility.Visible) return;

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) =>
        {
            SkipNoticeBorder.BeginAnimation(OpacityProperty, null);
            SkipNoticeBorder.Opacity = 0;
            SkipNoticeBorder.Visibility = Visibility.Collapsed;
        };
        SkipNoticeBorder.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// 「撤回」: 退回被跳过的那条片段的开头。
    /// 这条片段已经在 _sponsorHandled 里, 所以退回去之后**不会**再被自动跳走 —— 这正是撤回的意义。
    /// </summary>
    private void OnSkipUndoClick(object sender, RoutedEventArgs e)
    {
        var seg = _lastSkipSeg;
        HideSkipNotice();
        if (_closing || _mp == null || seg == null) return;

        var target = seg.StartMs;
        MarkSeekIssued(target);
        try { _mp.Time = target; } catch { }
        WriteLibVLCDebug($"[Sponsor] undo -> {seg.StartSec:0.##}s\n");
    }

    // ------------------------------------------------------------ 进度条上的片段色块

    /// <summary>类别 → 色块笔刷(建一次就冻结复用; 色块每帧都在重画, 别每次都 new 一个)</summary>
    private static readonly Dictionary<string, Brush> SponsorMarkBrushes = new(StringComparer.Ordinal);

    private static Brush SponsorMarkBrush(string category)
    {
        lock (SponsorMarkBrushes)
        {
            if (SponsorMarkBrushes.TryGetValue(category, out var cached)) return cached;

            var brush = new SolidColorBrush(Colors.Gray);
            try
            {
                if (ColorConverter.ConvertFromString(SponsorCategories.MarkColorHex(category)) is Color c)
                    brush = new SolidColorBrush(c);
            }
            catch
            {
                // 颜色表写错不该让播放器崩: 灰块照旧能表达"这一段有东西"
            }
            brush.Freeze();
            SponsorMarkBrushes[category] = brush;
            return brush;
        }
    }

    /// <summary>
    /// 滑块轨道的可用区间: (起点偏移, 可用宽度)。
    ///
    /// 为什么不能直接用 Slider 的宽度: Slider 的 Value 0..Max 对应的是**滑块中心**的行程 ——
    /// 滑块本身有宽度(这里是个 11px 的圆点), 于是中心在 x = 半宽 .. 宽-半宽 之间移动。
    /// 直接按控件宽度做比例, 两端各会偏掉半个滑块(约 3.5px), 长视频里看不出来,
    /// 但短视频(几十秒铺满整条)上就明显了。取模板里的 PART_Track 拿真实尺寸最准。
    /// </summary>
    private (double offset, double usable) TrackMetrics()
    {
        try
        {
            if (ProgressSlider?.Template?.FindName("PART_Track", ProgressSlider) is Track track &&
                track.ActualWidth > 0)
            {
                var thumb = track.Thumb?.ActualWidth ?? 0;
                return (thumb / 2, Math.Max(1, track.ActualWidth - thumb));
            }
        }
        catch
        {
            // 模板还没应用(或以后换了模板) -> 走下面的兜底
        }
        return (0, Math.Max(1, ProgressSlider?.ActualWidth ?? 0));
    }

    /// <summary>
    /// 把"当前生效的片段"画成进度条上的色块。
    ///
    /// 只画 _sponsorActive(已按开关 + 勾选类别过滤) —— 所以关掉开关、改类别时它跟着变,
    /// 与"实际会跳哪些"永远一致, 不会出现"画了色块却不跳"的鬼故事。
    /// 每段长度按毫秒比例映射; 起点/终点都 clamp 到轨道内, 防止服务端给的脏数据(超出时长)画出界。
    /// </summary>
    private void RenderSponsorMarks()
    {
        if (SponsorMarks == null) return;
        SponsorMarks.Children.Clear();
        if (_sponsorActive.Count == 0) return;

        var total = _totalMs > 0 ? _totalMs : (_mp?.Length ?? 0);
        if (total <= 0) return;

        var (offset, usable) = TrackMetrics();
        if (usable <= 1) return;

        // 色块压在 3px 的白色轨道上, 给 5px 才一眼能看见; 竖直居中(圆角端头, 像一段"贴纸")
        const double markHeight = 5;
        var height = SponsorMarks.ActualHeight;
        var top = height > markHeight ? (height - markHeight) / 2 : 6.5;

        foreach (var seg in _sponsorActive)
        {
            var ratio1 = Math.Clamp(seg.StartMs / (double)total, 0, 1);
            var ratio2 = Math.Clamp(seg.EndMs / (double)total, 0, 1);
            var x1 = offset + usable * ratio1;
            var x2 = offset + usable * ratio2;
            if (x1 >= usable + offset) continue;      // 整段都在轨道外(脏数据) -> 不画

            var rect = new System.Windows.Shapes.Rectangle
            {
                // 最短给 2px: 极短的片段(比如 1 秒的一闪而过)按比例算出来不到 1px, 会整个消失
                Width = Math.Max(2, x2 - x1),
                Height = markHeight,
                RadiusX = markHeight / 2,
                RadiusY = markHeight / 2,
                Fill = SponsorMarkBrush(seg.Category)
            };
            Canvas.SetLeft(rect, x1);
            Canvas.SetTop(rect, top);
            SponsorMarks.Children.Add(rect);
        }
    }
}
