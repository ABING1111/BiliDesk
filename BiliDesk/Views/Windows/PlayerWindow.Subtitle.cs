using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.Views;

public partial class PlayerWindow : FluentWindow
{
    // ------------------------------------------------------------ 字幕全链路(2026-11-15)
    //
    // 数据: /x/player/wbi/v2 拿轨道列表 → 下载字幕 JSON → 内存时间轴。
    // 渲染: WPF 浮层(SubitleLayer), 时间用**弹幕时钟**(已按倍速外推、暂停冻结)。
    // 记忆: SubtitleEnabled(开关) / SubtitleLan(语言) / SubtitleFontPercent(字号)。
    //
    // ★ 为什么不用 LibVLC 原生字幕: 要把 JSON 转成临时 SRT 文件再挂给 Media,
    //   文件生命周期/样式控制都是额外负担; WPF 层还能任意调字号与延迟。

    /// <summary>本次播放可选的字幕轨(CC + AI, 已按接口顺序去重)。属于"这一片"。</summary>
    private readonly List<SubtitleTrack> _subtitleTracks = new();

    /// <summary>当前轨道的时间轴(毫秒, 按 FromMs 有序 —— 二分查找的前提)。属于"这一片"。</summary>
    private readonly List<SubtitleCue> _subtitleCues = new();

    /// <summary>字幕当前是否处于"显示中"状态(用户显式开的, 与"有轨道"是两回事)。</summary>
    private bool _subtitleOn;

    /// <summary>轨道列表正在加载(按钮显示加载态用)。</summary>
    private bool _subtitleLoadingTracks;

    /// <summary>字幕本体正在下载(弹层提示行用)。</summary>
    private bool _subtitleLoadingCues;

    /// <summary>"有字幕但未登录拿不到"(need_login_subtitle)。提示文案用它区分"真没有"。</summary>
    private bool _subtitleNeedsLogin;

    /// <summary>填充弹层列表时抑制 SelectionChanged(与 _fillingQualityMenu 同款闸)。</summary>
    private bool _fillingSubtitleMenu;

    /// <summary>最近一次加载失败的提示(弹层显示; 空串 = 没有错误)。</summary>
    private string _subtitleError = "";

    /// <summary>当前选中的语言(lan 代码; 空串 = 没选)。换片自动选轨的偏好之一。</summary>
    private string _subtitleLan = "";

    /// <summary>字幕延迟(毫秒, 正 = 字幕延后)。本片内有效, 不持久化 —— 延迟几乎总是"这一片"的特性。</summary>
    private long _subtitleDelayMs;

    /// <summary>字幕加载代次: 换片 +1, 旧请求回来时据此丢弃(与 _loadGen 同款防串)。</summary>
    private int _subtitleGen;

    /// <summary>字幕请求的取消源: 换片/关窗立刻掐, 别让上一个视频的下载继续跑。</summary>
    private CancellationTokenSource? _subtitleCts;

    /// <summary>
    /// 字幕刷新心跳(100ms)。★ 为什么不用 TimeChanged 逐条驱动: 那个事件在 seek 等待期
    /// 可能不来(看门狗路径), 而字幕必须"哪怕时间没变也要能藏/显" —— 独立心跳最省心。
    /// 定时器由反射式 StopAllTimers 自动停(见 Lifetime.cs 的说明), 不用手动清理。
    /// </summary>
    private DispatcherTimer? _subtitleTimer;

    /// <summary>上一次写到 SubtitleText 的文本。相同就不碰控件, 免得每 100ms 重排一次。</summary>
    private string _subtitleLastText = "";

    /// <summary>延迟步进(毫秒)。0.2s 一档: AI 字幕的偏差常在 ±1~3s, 0.1 太细 0.5 太粗。</summary>
    private const long SubtitleDelayStepMs = 200;

    /// <summary>延迟的上下限。±10s 之外基本就是"选错了轨", 该换轨而不是调延迟。</summary>
    private const long SubtitleDelayMaxMs = 10_000;

    // ------------------------------------------------------------ 入口与加载

    /// <summary>控制栏字幕按钮点击: 走统一的弹层开关(点一下展开, 再点一下收起)。</summary>
    private void OnSubtitleClick(object sender, RoutedEventArgs e)
    {
        FillSubtitleMenu();
        TogglePlayerPopup(SubtitlePopup);
    }

    /// <summary>
    /// 拉取字幕轨列表(换片后由 LoadVideoAsync 并行调起, **不 await** —— 绝不阻塞起播)。
    /// ★ gen 用调用方的加载代次而不是自己的: 字幕必须跟着"这次换片"走,
    ///   用户连点两个视频时, 慢的那个字幕响应不能盖到新视频上。
    /// </summary>
    private async Task LoadSubtitleTracksAsync(int gen)
    {
        CancelSubtitleFetch();
        _subtitleTracks.Clear();
        _subtitleCues.Clear();
        _subtitleError = "";
        _subtitleNeedsLogin = false;
        _subtitleLoadingTracks = true;
        _subtitleOn = false;
        HideSubtitleLayer();
        UpdateSubtitleButton();
        UpdateSubtitleHint();

        try
        {
            var (_, _, tracks, needLogin) = await Svc.Api.GetSubtitleTracksAsync(
                _currentBvid, _currentCid, _subtitleCts?.Token ?? CancellationToken.None);
            if (_closing || gen != _loadGen) return;
            _subtitleLoadingTracks = false;
            _subtitleNeedsLogin = needLogin;
            _subtitleTracks.AddRange(tracks);

            if (_subtitleTracks.Count == 0)
            {
                // 没轨道: 自动显示开着也无处可用, 弹层给一句说得清的原因
                UpdateSubtitleButton();
                UpdateSubtitleHint();
                return;
            }

            // 自动显示开着 → 选轨起显; 否则只填弹层等用户点
            if (Svc.Settings.SubtitleEnabled)
            {
                var preferred = SubtitleParser.PickPreferred(_subtitleTracks, _subtitleLan);
                if (preferred != null) await SelectSubtitleTrackAsync(preferred, gen);
                return;
            }
            UpdateSubtitleButton();
            UpdateSubtitleHint();
        }
        catch (Exception ex)
        {
            if (_closing || gen != _loadGen) return;
            _subtitleLoadingTracks = false;
            _subtitleError = ex.Message;
            UpdateSubtitleButton();
            UpdateSubtitleHint();
        }
    }

    /// <summary>
    /// 选择一条轨道并下载时间轴。
    /// ★ 成功后把 SubtitleEnabled/SubtitleLan 一起落盘: 用户"点开字幕"这个动作本身就是
    ///   偏好表达 —— 下一个有字幕的视频应该自动起显(这正是"开启后记忆"的语义)。
    /// </summary>
    private async Task SelectSubtitleTrackAsync(SubtitleTrack track, int gen)
    {
        CancelSubtitleFetch();
        _subtitleLoadingCues = true;
        _subtitleError = "";
        UpdateSubtitleHint();
        UpdateSubtitleButton();

        try
        {
            var (ok, err, cues) = await Svc.Api.GetSubtitleCuesAsync(
                track.Url, _subtitleCts?.Token ?? CancellationToken.None);
            if (_closing || gen != _loadGen) return;
            _subtitleLoadingCues = false;

            if (!ok || cues.Count == 0)
            {
                _subtitleError = err ?? "字幕内容为空";
                _subtitleOn = false;
                HideSubtitleLayer();
                UpdateSubtitleHint();
                UpdateSubtitleButton();
                return;
            }
            _subtitleCues.Clear();
            _subtitleCues.AddRange(cues);
            _subtitleLan = track.Lan;
            _subtitleOn = true;
            _subtitleLastText = "\0";   // 强制下一跳写一次文本(隐藏态刚被展示)
            StartSubtitleTimer();
            RefreshSubtitleNow();
            UpdateSubtitleHint();
            UpdateSubtitleButton();
            // 偏好落盘: 开关 + 语言一起记(这里 gen 已校验, 换片不会带着旧偏好跑)
            Svc.Settings.UpdateSubtitleSettings(true, track.Lan);
        }
        catch (Exception ex)
        {
            if (_closing || gen != _loadGen) return;
            _subtitleLoadingCues = false;
            _subtitleError = ex.Message;
            _subtitleOn = false;
            HideSubtitleLayer();
            UpdateSubtitleHint();
            UpdateSubtitleButton();
        }
    }

    /// <summary>快捷键 C 的开关语义: 显示中 → 关; 没显示 → 用当前偏好(上次语言/自动选轨)起显。</summary>
    private void ToggleSubtitleByKey()
    {
        if (_closing) return;
        if (_subtitleOn)
        {
            CloseSubtitle();
            return;
        }
        if (_subtitleTracks.Count == 0)
        {
            // 没轨道时给一句说得清的提示(直播/本地/未加载/真没有), 别让按键像坏了一样
            if (!_isLive && !_isLocalPlayback && _currentCid > 0 && !_subtitleLoadingTracks)
                _ = LoadSubtitleTracksAsync(_loadGen);
            Svc.Toast.Show(_subtitleLoadingTracks ? "正在获取字幕轨…"
                : _subtitleNeedsLogin ? "该视频有 AI 字幕, 登录后可查看"
                : _isLive || _isLocalPlayback ? "直播与本地文件没有字幕" : "该视频暂无字幕");
            return;
        }
        var preferred = SubtitleParser.PickPreferred(_subtitleTracks,
            _subtitleOn ? _subtitleLan : Svc.Settings.SubtitleLan);
        if (preferred == null) return;
        _ = SelectSubtitleTrackAsync(preferred, _loadGen);
    }

    /// <summary>关闭字幕(弹层第一项 / 快捷键 C 在开启态下)。只关显示, 轨道数据留着可再开。</summary>
    private void CloseSubtitle()
    {
        if (!_subtitleOn && SubtitleLayer != null && SubtitleLayer.Visibility != Visibility.Visible) return;
        _subtitleOn = false;
        _subtitleLastText = "";
        HideSubtitleLayer();
        StopSubtitleTimer();
        UpdateSubtitleButton();
        UpdateSubtitleHint();
        // 开关落盘 + 广播(设置页同步); 语言保持 —— 用户只是关显示, 不是换偏好
        Svc.Settings.UpdateSubtitleSettings(false);
    }

    /// <summary>换片/关窗时掐掉还没回来的字幕请求。</summary>
    private void CancelSubtitleFetch()
    {
        _subtitleGen++;
        var cts = _subtitleCts;
        _subtitleCts = null;
        if (cts == null) return;
        try { cts.Cancel(); } catch { /* 已取消也无妨 */ }
        try { cts.Dispose(); } catch { }
    }

    // ------------------------------------------------------------ 弹层

    /// <summary>填充字幕弹层: 第一项「关闭字幕」, 其余为各轨道; 高亮当前状态。</summary>
    private void FillSubtitleMenu()
    {
        if (SubtitleList == null) return;
        _fillingSubtitleMenu = true;
        try
        {
            SubtitleList.Items.Clear();
            SubtitleList.Items.Add(new ListBoxItem { Tag = "", Content = "关闭字幕" });
            foreach (var t in _subtitleTracks)
                SubtitleList.Items.Add(new ListBoxItem { Tag = t.Lan, Content = t.DisplayName });

            // 高亮: 显示中 → 当前语言; 没显示 → 「关闭字幕」
            var want = _subtitleOn ? _subtitleLan : "";
            for (var i = 0; i < SubtitleList.Items.Count; i++)
            {
                if (SubtitleList.Items[i] is ListBoxItem li && li.Tag is string lan &&
                    string.Equals(lan, want, StringComparison.OrdinalIgnoreCase))
                {
                    SubtitleList.SelectedIndex = i;
                    break;
                }
            }

            if (SubtitleFontSlider != null && !_subtitleFontSliderSyncing)
                SubtitleFontSlider.Value = Svc.Settings.SubtitleFontPercent;
            UpdateSubtitleDelayText();
        }
        finally
        {
            _fillingSubtitleMenu = false;
        }
    }

    /// <summary>弹层选中项变化: 空 Tag = 关闭; 其余按 lan 找轨道加载。
    /// ★ 加载失败后再点同一条轨道会**重试**(选同轨且未显示时不再被短路) ——
    ///   否则用户遇到一次网络抖动就永远点不动了。</summary>
    private void OnSubtitleSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingSubtitleMenu || SubtitlePopup == null) return;
        if (SubtitleList.SelectedItem is not ListBoxItem li) return;
        SubtitlePopup.IsOpen = false;
        if (li.Tag is not string lan) return;
        if (lan.Length == 0)
        {
            CloseSubtitle();
            return;
        }
        // 同轨且已在显示: 无事可做; 同轨但没显示(失败过): 继续走重试
        if (string.Equals(lan, _subtitleLan, StringComparison.OrdinalIgnoreCase) && _subtitleOn) return;
        var track = _subtitleTracks.Find(t => string.Equals(t.Lan, lan, StringComparison.OrdinalIgnoreCase));
        if (track == null) return;
        _ = SelectSubtitleTrackAsync(track, _loadGen);
    }

    private void UpdateSubtitleHint()
    {
        if (SubtitleHintText == null) return;
        string text;
        if (_subtitleLoadingTracks) text = "正在获取字幕轨…";
        else if (_subtitleLoadingCues) text = "字幕加载中…";
        else if (_subtitleError.Length > 0) text = "加载失败: " + _subtitleError + "(可重试: 再点一次该轨道)";
        else if (_subtitleTracks.Count == 0 && _isLive) text = "直播没有字幕";
        else if (_subtitleTracks.Count == 0 && _isLocalPlayback) text = "本地文件没有字幕";
        else if (_subtitleTracks.Count == 0 && _subtitleNeedsLogin) text = "该视频有 AI 字幕, 登录后可查看";
        else if (_subtitleTracks.Count == 0) text = "该视频暂无字幕";
        else text = "";
        SubtitleHintText.Text = text;
    }

    /// <summary>同步字幕按钮的视觉态: 置灰(无轨) / 关(压暗+斜线) / 开(纯白)。</summary>
    private void UpdateSubtitleButton()
    {
        if (BtnSubtitle == null || SubtitleBtnText == null) return;
        var usable = !_isLive && !_isLocalPlayback && !_subtitleLoadingTracks;
        // 直播/本地/还没拉到轨道: 整颗置灰 —— 留一个按了没反应的按钮不如没有(与弹幕按钮同款)
        BtnSubtitle.IsEnabled = usable;
        BtnSubtitle.Opacity = usable ? 1.0 : 0.4;
        var brush = new System.Windows.Media.SolidColorBrush(_subtitleOn
            ? System.Windows.Media.Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)
            : System.Windows.Media.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        SubtitleBtnText.Foreground = brush;
        if (SubtitleOffSlash != null)
        {
            SubtitleOffSlash.Stroke = brush;
            SubtitleOffSlash.Visibility = _subtitleOn ? Visibility.Collapsed : Visibility.Visible;
        }
        BtnSubtitle.ToolTip = _subtitleOn
            ? "字幕: " + _subtitleLan + " (C)"
            : "字幕 (C)";
    }

    // ------------------------------------------------------------ 延迟与字号

    private void OnSubtitleDelayPlusClick(object sender, RoutedEventArgs e) => ShiftSubtitleDelay(+1);
    private void OnSubtitleDelayMinusClick(object sender, RoutedEventArgs e) => ShiftSubtitleDelay(-1);

    /// <summary>延迟步进。±10s 夹取 —— 出了这范围该换轨而不是继续调。</summary>
    private void ShiftSubtitleDelay(int sign)
    {
        _subtitleDelayMs = Math.Clamp(
            _subtitleDelayMs + sign * SubtitleDelayStepMs,
            -SubtitleDelayMaxMs, SubtitleDelayMaxMs);
        UpdateSubtitleDelayText();
        // 立刻按新延迟刷新一次: 0.2s 的挪动当场就能看到对没对上
        RefreshSubtitleNow();
    }

    private void UpdateSubtitleDelayText()
    {
        if (SubtitleDelayValue == null) return;
        var s = _subtitleDelayMs / 1000.0;
        SubtitleDelayValue.Text = s == 0 ? "0.0s"
            : (s > 0 ? "+" : "−") + Math.Abs(s).ToString("0.0#") + "s";
    }

    /// <summary>字号滑杆回灌闸: FillSubtitleMenu 里赋初值时不触发落盘(与 _syncingDanmakuLook 同款)。</summary>
    private bool _subtitleFontSliderSyncing;

    private void OnSubtitleFontChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (_subtitleFontSliderSyncing) return;
        var percent = (int)Math.Round(e.NewValue);
        if (Svc.Settings.SubtitleFontPercent == percent) return;
        Svc.Settings.SetSubtitleFontPercent(percent);
        if (SubtitleFontValue != null) SubtitleFontValue.Text = percent + "%";
        ApplySubtitleStyle();
    }

    /// <summary>设置页(或其它窗口)改了字幕设置: 同步字号与开关态。</summary>
    private void OnSubtitleSettingsChanged()
    {
        if (_closing) return;
        if (SubtitleFontSlider != null)
        {
            _subtitleFontSliderSyncing = true;
            try { SubtitleFontSlider.Value = Svc.Settings.SubtitleFontPercent; }
            finally { _subtitleFontSliderSyncing = false; }
        }
        if (SubtitleFontValue != null) SubtitleFontValue.Text = Svc.Settings.SubtitleFontPercent + "%";
        ApplySubtitleStyle();
        UpdateSubtitleButton();
    }

    // ------------------------------------------------------------ 渲染

    /// <summary>按视频区尺寸与设置算字幕字号/最大宽, 并跟随全局界面字体。</summary>
    private void ApplySubtitleStyle()
    {
        if (SubtitleLayer == null || SubtitleText == null || VideoAreaRoot == null) return;
        // 基准字号随视频区高度走: 全屏(~1080)≈40px、常规(760 窗)≈22px, 与视频同涨缩。
        var basePx = Math.Clamp(VideoAreaRoot.ActualHeight * 0.042, 16, 40);
        SubtitleText.FontSize = basePx * Svc.Settings.SubtitleFontPercent / 100.0;
        SubtitleText.LineHeight = SubtitleText.FontSize * 1.35;
        // 最大宽限在 78%: 字幕超出画面边缘必然难读, 换行比溢出好
        SubtitleText.MaxWidth = Math.Max(240, VideoAreaRoot.ActualWidth * 0.78);
        // 界面字体(2026-11-14 新增): 字幕层在独立浮层可视树里, 不跟窗口继承走(同弹幕层)
        SubtitleText.FontFamily = UiFont.Current;
    }

    /// <summary>显示字幕层并写入文本。空文本 = 直接隐藏。</summary>
    private void ShowSubtitleText(string text)
    {
        if (SubtitleLayer == null || SubtitleText == null) return;
        if (string.IsNullOrEmpty(text))
        {
            HideSubtitleLayer();
            return;
        }
        if (SubtitleText.Text != text) SubtitleText.Text = text;
        if (SubtitleLayer.Visibility != Visibility.Visible) SubtitleLayer.Visibility = Visibility.Visible;
    }

    private void HideSubtitleLayer()
    {
        if (SubtitleLayer == null) return;
        if (SubtitleLayer.Visibility != Visibility.Collapsed)
            SubtitleLayer.Visibility = Visibility.Collapsed;
    }

    private void StartSubtitleTimer()
    {
        _subtitleTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _subtitleTimer.Tick -= OnSubtitleTimerTick;
        _subtitleTimer.Tick += OnSubtitleTimerTick;
        _subtitleTimer.Start();
    }

    private void StopSubtitleTimer()
    {
        if (_subtitleTimer == null) return;
        _subtitleTimer.Stop();
        _subtitleTimer.Tick -= OnSubtitleTimerTick;
        _subtitleTimer = null;
    }

    private void OnSubtitleTimerTick(object? sender, EventArgs e) => RefreshSubtitleNow();

    /// <summary>
    /// 按当前媒体时间刷新字幕文本。
    /// ★ 时间来源是**弹幕时钟** DanmakuClock(): 它由 TimeChanged 校准、按倍速外推、
    ///   暂停时冻结 —— 字幕与弹幕、画面天然同源, 倍速/暂停/seek 都不需要单独处理。
    /// ★ 媒体时钟在换片后从 0 重来, 而时间轴属于"这一片", 二者由 _subtitleOn 与
    ///   RefreshForNewMedia 的一致性保证: 换片必然清时间轴 + 隐藏层。
    /// </summary>
    private void RefreshSubtitleNow()
    {
        if (_closing) return;
        // 聆听态/已播完: 字幕层被 ListenOverlay / EndOverlay 盖着, 但心跳若照常刷,
        // 会把 Visibility 拉回 Visible —— 退出聆听态/重播时就会闪出旧文本。
        // 这里统一按状态藏, 让"恢复"只由这两个标志翻转驱动。
        if (_isListenMode || _ended)
        {
            HideSubtitleLayer();
            return;
        }
        if (!_subtitleOn || _subtitleCues.Count == 0)
        {
            HideSubtitleLayer();
            return;
        }
        var ms = (long)(DanmakuClock() * 1000) + _subtitleDelayMs;
        var text = SubtitleParser.VisibleText(_subtitleCues, ms);
        // 文本没变就不碰控件: 100ms 心跳 × 每次都赋值 = 每 100ms 一次重排, 纯浪费
        if (text == _subtitleLastText) return;
        _subtitleLastText = text;
        ShowSubtitleText(text);
    }

    /// <summary>换片时清掉字幕的全部"这一片"状态(由 ResetForNewMedia 调用)。</summary>
    private void ResetSubtitleForNewMedia()
    {
        CancelSubtitleFetch();
        _subtitleTracks.Clear();
        _subtitleCues.Clear();
        _subtitleOn = false;
        _subtitleLoadingTracks = false;
        _subtitleLoadingCues = false;
        _subtitleNeedsLogin = false;
        _subtitleError = "";
        _subtitleDelayMs = 0;
        _subtitleLastText = "";
        StopSubtitleTimer();
        HideSubtitleLayer();
        UpdateSubtitleButton();
        if (SubtitleHintText != null) SubtitleHintText.Text = "";
    }
}
