using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.Views;

public partial class PlayerWindow : FluentWindow
{
    // ------------------------------------------------------------ 直播弹幕(2026-11-13)
    //
    // 与点播弹幕的关系:
    //   · **共用渲染层** —— 泳道/字号/速度/透明度/胶囊/区域占比/类型过滤全部沿用,
    //     这里只负责"消息从哪来"和"什么时候上屏"。
    //   · 点播是"按时间轴游标派发"(_danmaku + DispatchDanmaku);
    //     直播是"到达即显示", 没有时间轴, 因此不碰 _danmaku / _indexInLib。
    //
    // ★ 三条必须遵守的约束:
    //   ① WS 回调在后台线程: 事件里只入队, 绝不再碰 WPF 对象;
    //   ② 直播间弹幕量可以很猛(热门房间几十条/秒): 100ms 批量出队 + 每批限 8 条,
    //      超出交给 AddDanmaku 现有的"同屏 80 条/无泳道即丢弃"兜底;
    //   ③ 不播放/暂停/缓冲时**直接丢队列** —— 直播弹幕没有"补播"的意义,
    //      攒下来的旧消息一旦恢复播放会一次性糊上来。

    private LiveDanmakuService? _liveDanmaku;
    private readonly ConcurrentQueue<LiveDanmakuItem> _liveDanmakuQueue = new();
    private readonly Dictionary<string, (long At, int Count)> _liveDanmakuRecent =
        new(StringComparer.Ordinal);
    private DispatcherTimer? _liveDanmakuPump;

    /// <summary>右侧直播弹幕列表的数据源(只保留最近若干条)</summary>
    private readonly ObservableCollection<LiveChatEntry> _liveChat = new();

    /// <summary>
    /// 右侧聊天列表是否跟着最新消息自动滚动。
    /// ★ 用户往上翻看历史时置 false, 滚回底部自动恢复; 否则新消息会把用户正在看的内容顶走。
    /// ★ 这是"每次进入直播间"的瞬时态, 已登记在 PerMediaStateFields 里。
    /// </summary>
    private bool _liveChatAutoScroll = true;

    /// <summary>聊天列表最多保留多少条(内存/布局保护)</summary>
    private const int LiveChatMaxEntries = 300;

    /// <summary>每 100ms 最多往聊天列表追加几条(热门房间削峰; 超出的消息只丢列表, 不影响滚动弹幕)</summary>
    private const int LiveChatMaxPerTick = 16;

    /// <summary>右侧聊天列表的一行。★ 必须 public: XAML 的 {Binding} 通过反射读属性。</summary>
    public sealed class LiveChatEntry
    {
        public string UserName { get; init; } = "";
        public string Text { get; init; } = "";

        /// <summary>正文颜色(永远非空: 默认弹幕在浅色面板上必须用主题主文字色, 不能用白)</summary>
        public Brush TextBrush { get; init; } = Brushes.White;

        /// <summary>身份徽章文案: "粉丝团5" / "房管"; 空 + Collapsed = 没有徽章</summary>
        public string BadgeText { get; init; } = "";
        public string BadgeToolTip { get; init; } = "";
        public Visibility BadgeVisibility { get; init; } = Visibility.Collapsed;
        public Brush BadgeBrush { get; init; } = Brushes.Transparent;
        public Brush BadgeTextBrush { get; init; } = Brushes.White;
    }

    /// <summary>粉丝勋章颜色的笔刷缓存(热门直播间几千条消息, 颜色只有几十种)。</summary>
    private readonly Dictionary<uint, Brush> _liveBadgeBrushes = new();

    /// <summary>队列上限: 播放尚未开始时最多攒这么多, 再多就丢最旧的。</summary>
    private const int LiveDanmakuMaxQueued = 200;

    /// <summary>每 100ms 最多渲染几条。过高会让 UI 线程被弹幕测量拖住。</summary>
    private const int LiveDanmakuMaxPerTick = 8;

    /// <summary>相同文本的重复判定窗口(毫秒)</summary>
    private const int LiveDanmakuRepeatWindowMs = 10_000;

    /// <summary>同一窗口内相同文本最多保留几条(超过视为复读刷屏)</summary>
    private const int LiveDanmakuRepeatMax = 3;

    private static readonly TimeSpan LiveDanmakuPumpInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 开始接收某个直播间的弹幕。
    ///
    /// ★ 幂等: 同一个房间且连接任务还活着时直接返回 —— 直播 EOF 后点"重播"会重走
    ///   LoadLiveAsync, 那时不应该把弹幕连接拆了重建(会丢消息, 也会多打两次接口)。
    /// </summary>
    private void StartLiveDanmaku(long roomId)
    {
        if (_closing || roomId <= 0) return;

        var running = _liveDanmaku;
        if (running != null && running.RoomId == roomId && running.IsRunning) return;

        StopLiveDanmaku();

        var service = new LiveDanmakuService();
        service.Received += OnLiveDanmakuReceived;
        service.StateChanged += OnLiveDanmakuStateChanged;
        _liveDanmaku = service;

        StartLiveDanmakuPump();
        service.Start(roomId);
    }

    /// <summary>停止接收并释放连接(换片/关窗/直播起播失败时调用)</summary>
    private void StopLiveDanmaku()
    {
        _liveDanmakuPump?.Stop();
        _liveInfoTimer?.Stop();

        var service = _liveDanmaku;
        _liveDanmaku = null;
        if (service != null)
        {
            service.Received -= OnLiveDanmakuReceived;
            service.StateChanged -= OnLiveDanmakuStateChanged;
            // Dispose 只发取消信号、不等待: 关窗/换片不能被一次网络收尾卡住。
            service.Dispose();
        }

        ClearLiveDanmakuBuffers();
    }

    /// <summary>
    /// 清掉"还没上屏"与"上屏后还在飞"的直播弹幕。
    /// ★ 直播重连/重建 Media 会把媒体时间归零, 而在飞弹幕的出生时间是旧时间轴上的值 ——
    ///   不清掉的话它们会卡在右边缘一动不动, 直到新时钟追上旧值(可能几分钟)。
    /// </summary>
    private void ResetLiveDanmakuLayer()
    {
        ClearLiveDanmakuBuffers();
        DanmakuCanvas?.Children.Clear();
        ResetLanes();
    }

    private void ClearLiveDanmakuBuffers()
    {
        _liveDanmakuQueue.Clear();
        _liveDanmakuRecent.Clear();
    }

    private void StartLiveDanmakuPump()
    {
        if (_liveDanmakuPump == null)
        {
            _liveDanmakuPump = new DispatcherTimer { Interval = LiveDanmakuPumpInterval };
            _liveDanmakuPump.Tick += OnLiveDanmakuPumpTick;
        }
        _liveDanmakuPump.Start();
    }

    /// <summary>WS 线程回调: 只入队, 不碰 UI。</summary>
    private void OnLiveDanmakuReceived(LiveDanmakuItem item)
    {
        if (_closing) return;
        _liveDanmakuQueue.Enqueue(item);
        while (_liveDanmakuQueue.Count > LiveDanmakuMaxQueued)
            _liveDanmakuQueue.TryDequeue(out _);
    }

    private void OnLiveDanmakuStateChanged(LiveDanmakuConnectionState state, string? message)
    {
        if (state != LiveDanmakuConnectionState.NeedLogin) return;
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closing || !_isLive) return;
                // 只在直播里提示一次: 服务端拒绝鉴权/连上即踢是最典型的"没登录"。
                Svc.Toast.Show("登录后才能接收直播弹幕");
                if (DanmakuCountText != null) DanmakuCountText.Text = "需登录";
            }));
        }
        catch
        {
            // 窗口正在销毁, Dispatcher 已不可用
        }
    }

    /// <summary>
    /// 每 100ms 把队列里的弹幕放上屏 / 追加到右侧聊天列表。
    ///
    /// ★ 两个落点的活跃条件不同, 必须分开判断:
    ///   · 右侧聊天列表: **只要还在直播就追加**, 与播放状态、弹幕开关无关 ——
    ///     用户暂停或关掉滚动弹幕时, 聊天记录仍然应该继续走(参考图里就是常驻一块聊天区)。
    ///   · 视频上的滚动弹幕: 只在真正播放且弹幕时钟已起来时渲染。
    ///     缓冲/暂停时到达的消息不再补播, 否则恢复播放会一次性糊上来。
    /// </summary>
    private void OnLiveDanmakuPumpTick(object? sender, EventArgs e)
    {
        if (_closing || !_isLive)
        {
            _liveDanmakuQueue.Clear();
            return;
        }

        var canRender = _mp != null && _mp.IsPlaying && _dmClockRunning && _danmakuOn;
        var shown = 0;
        var chatAdded = 0;

        while (_liveDanmakuQueue.TryDequeue(out var item))
        {
            // ① 右侧聊天列表(削峰: 超过上限的本轮消息只丢弃列表显示)
            if (chatAdded < LiveChatMaxPerTick)
            {
                AppendLiveChat(item);
                chatAdded++;
            }

            // ② 视频上的滚动弹幕
            if (!canRender || shown >= LiveDanmakuMaxPerTick) continue;
            if (!ShouldShowLiveDanmaku(item)) continue;
            AddDanmaku(item.Text, item.Mode, DanmakuClock(), item.Color);
            shown++;
        }

        if (chatAdded > 0) TrimLiveChatAndScroll();
    }

    /// <summary>
    /// 把一条弹幕追加到右侧聊天列表。
    /// ★ 正文颜色规则: 默认/白色 → 主题主文字色(浅色面板上白字看不见);
    ///   真正的彩色弹幕 → 它的原色。关掉「彩色弹幕」时同样回落主题色。
    /// </summary>
    private void AppendLiveChat(LiveDanmakuItem item)
    {
        // 身份徽章: 优先"房管", 其次是粉丝勋章。两者都没有就不显示徽章。
        var badgeText = "";
        var badgeTip = "";
        var badgeVisibility = Visibility.Collapsed;
        Brush badgeBrush = Brushes.Transparent;
        Brush badgeTextBrush = Brushes.White;

        if (item.IsAdmin)
        {
            badgeText = "房管";
            badgeTip = "房管";
            badgeBrush = TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;
            badgeTextBrush = TryFindResource("TextOnAccentBrush") as Brush ?? Brushes.White;
            badgeVisibility = Visibility.Visible;
        }
        else if (item.MedalLevel > 0)
        {
            badgeText = "粉丝团" + item.MedalLevel;
            badgeTip = string.IsNullOrEmpty(item.MedalName) ? "粉丝勋章" : item.MedalName;
            badgeBrush = LiveBadgeBrush(item.MedalColor);
            badgeTextBrush = LiveBadgeTextBrush(item.MedalColor);
            badgeVisibility = Visibility.Visible;
        }

        _liveChat.Add(new LiveChatEntry
        {
            UserName = string.IsNullOrWhiteSpace(item.UserName) ? "观众" : item.UserName,
            Text = item.Text,
            TextBrush = ChatTextBrush(item.Color),
            BadgeText = badgeText,
            BadgeToolTip = badgeTip,
            BadgeVisibility = badgeVisibility,
            BadgeBrush = badgeBrush,
            BadgeTextBrush = badgeTextBrush
        });
    }

    /// <summary>粉丝勋章背景色。0/非法值用默认青色(B站勋章常见色系)。</summary>
    private Brush LiveBadgeBrush(uint color)
    {
        if (color == 0 || color > 0xFFFFFF) color = 0x2E7D8F;
        if (_liveBadgeBrushes.TryGetValue(color, out var cached)) return cached;

        var brush = new SolidColorBrush(Color.FromRgb(
            (byte)((color >> 16) & 0xFF),
            (byte)((color >> 8) & 0xFF),
            (byte)(color & 0xFF)));
        brush.Freeze();
        _liveBadgeBrushes[color] = brush;
        return brush;
    }

    /// <summary>按背景亮度选黑/白字, 避免浅色勋章上白字看不见。</summary>
    private static Brush LiveBadgeTextBrush(uint color)
    {
        var r = (color >> 16) & 0xFF;
        var g = (color >> 8) & 0xFF;
        var b = color & 0xFF;
        var luminance = 0.299 * r + 0.587 * g + 0.114 * b;
        return luminance > 160 ? Brushes.Black : Brushes.White;
    }

    private Brush ChatTextBrush(uint color)
    {
        if (_danmakuColorful && color != 0xFFFFFF && color != 0) return DanmakuBrush(color);

        // 白色/关闭彩色: 用主题主文字色。★ 不能返回 null —— TextBlock.Foreground 绑到 null
        // 会把正文变成默认黑/透明, 深色主题下整块聊天看不见。
        // (ThemeService 的笔刷是就地改色的, 这里拿到的引用在换肤后依然有效。)
        return TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.White;
    }

    /// <summary>裁掉过旧的消息, 并在用户没有往上翻时贴到底部。</summary>
    private void TrimLiveChatAndScroll()
    {
        while (_liveChat.Count > LiveChatMaxEntries) _liveChat.RemoveAt(0);

        if (LiveChatEmptyHint != null && LiveChatEmptyHint.Visibility == Visibility.Visible)
            LiveChatEmptyHint.Visibility = Visibility.Collapsed;

        if (!_liveChatAutoScroll || LiveChatList == null || !LiveChatList.IsVisible ||
            _liveChat.Count == 0)
            return;
        LiveChatList.ScrollIntoView(_liveChat[_liveChat.Count - 1]);
    }

    /// <summary>
    /// 右侧聊天列表滚动: 贴底时保持自动跟随; 用户往上翻时暂停跟随, 翻回底部自动恢复。
    /// </summary>
    private void OnLiveChatScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.ScrollViewer sv) return;

        // 内容高度变化(新消息)时, 只有原本就在跟随时才继续贴底
        if (e.ExtentHeightChange != 0)
        {
            if (_liveChatAutoScroll) sv.ScrollToEnd();
            return;
        }

        if (e.VerticalChange != 0)
            _liveChatAutoScroll = sv.VerticalOffset >= sv.ScrollableHeight - 4;
    }

    /// <summary>
    /// 直播/点播切换右侧面板: 直播显示聊天列表, 点播显示原来的信息面板。
    /// ★ 只改 InfoPanel **内部**的两块内容; InfoPanel 本身的显隐归 ApplyInfoPanelState
    ///   (全屏/铺满窗口统一收栏), 这里不碰 —— 否则两套开关会互相打架。
    /// </summary>
    private void UpdateLiveLayout()
    {
        if (LiveChatPanel == null || InfoContentHost == null) return;

        var live = _isLive;
        LiveChatPanel.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        InfoContentHost.Visibility = live ? Visibility.Collapsed : Visibility.Visible;

        // 每次换片源都从空列表开始: 上一条直播/视频的聊天记录不该带到下一条。
        _liveChat.Clear();
        _liveChatAutoScroll = true;
        ResetLiveInfoPanel();

        if (LiveChatList != null) LiveChatList.ItemsSource = _liveChat;
        if (LiveChatHintText != null) LiveChatHintText.Text = "实时";
        if (LiveChatEmptyHint != null)
            LiveChatEmptyHint.Visibility = _liveChat.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 合集面板是 VOD 的浮层, 直播里没有合集; 不收起会盖在聊天列表上。
        if (live) CloseSeasonPanel();
    }

    /// <summary>
    /// 直播弹幕的过滤: 与点播共用"类型过滤 + 智能屏蔽"两套设置, 但重复判定是**按时间窗**
    /// 而不是点播那种"整段列表最多 4 条" —— 直播是流式的, 没有"整段"可比。
    /// </summary>
    private bool ShouldShowLiveDanmaku(LiveDanmakuItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Text)) return false;

        var s = Svc.Settings;

        // 类型过滤: 四项全开时直接跳过(与 ApplyDanmakuFilter 的早退判据保持一致)
        var typeFilter = !(s.DanmakuFilterScroll && s.DanmakuFilterFixed
                           && s.DanmakuFilterColorful && s.DanmakuFilterAdvanced);
        if (typeFilter)
        {
            var probe = new RawDanmaku(0, item.Text, item.Mode, item.Color);
            if (!IsTypeAllowed(probe, s.DanmakuFilterScroll, s.DanmakuFilterFixed,
                    s.DanmakuFilterColorful, s.DanmakuFilterAdvanced))
                return false;
        }

        if (!s.DanmakuSmartFilter) return true;

        // 超长弹幕会占满一整条泳道; 直播里这种消息还特别多, 智能屏蔽下直接丢。
        if (item.Text.Length > 30) return false;
        return !IsLiveDanmakuRepeat(item.Text);
    }

    /// <summary>
    /// 相同文本在 <see cref="LiveDanmakuRepeatWindowMs"/> 内是否已经出现够多次。
    /// ★ 用墙钟而不是弹幕时钟: 暂停/缓冲时墙钟仍在走, 去重窗口不会因此被拉长。
    /// </summary>
    private bool IsLiveDanmakuRepeat(string text)
    {
        var now = Environment.TickCount64;

        // 定期清理: 热门房间几分钟就能攒出几万个不同文本
        if (_liveDanmakuRecent.Count > 512)
        {
            var expired = new List<string>();
            foreach (var pair in _liveDanmakuRecent)
                if (now - pair.Value.At >= LiveDanmakuRepeatWindowMs)
                    expired.Add(pair.Key);
            foreach (var key in expired) _liveDanmakuRecent.Remove(key);
            if (_liveDanmakuRecent.Count > 1024) _liveDanmakuRecent.Clear();
        }

        if (_liveDanmakuRecent.TryGetValue(text, out var entry) &&
            now - entry.At < LiveDanmakuRepeatWindowMs)
        {
            if (entry.Count >= LiveDanmakuRepeatMax) return true;
            // 保留窗口起点(不是刷新 At): 否则"每 9 秒发一次"的复读机永远命中不了上限。
            _liveDanmakuRecent[text] = (entry.At, entry.Count + 1);
            return false;
        }

        _liveDanmakuRecent[text] = (now, 1);
        return false;
    }
}
