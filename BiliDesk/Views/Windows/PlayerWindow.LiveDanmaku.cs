using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    /// <summary>
    /// 发送弹幕的在途闸(2026-11-28)。
    /// ★ 防止 Enter 连按 / 发送按钮连点把同一条弹幕发多次。
    /// ★ 属于"这一路片源"的瞬时态: 换片/换直播间必须复位, 已登记在 PerMediaStateFields 里。
    /// </summary>
    private bool _sendingLiveDanmaku;

    /// <summary>聊天列表最多保留多少条(内存/布局保护)</summary>
    private const int LiveChatMaxEntries = 300;

    /// <summary>
    /// 每个聊天批次最多往列表里追加几条(热门房间削峰; 超出的消息只丢列表, 不影响滚动弹幕)。
    /// ★ 2026-11-17: 配合"每 3 个 tick(≈100ms)一批"—— 批次**大小与频率都与原来一致**
    ///   (原实现也是每 100ms 最多 16 条), 所以列表密度不变。
    ///   真正的优化在别处: 裁剪改成一次裁一批(见 TrimLiveChatAndScroll),
    ///   徽章笔刷改成缓存(见 LiveAdminBadgeBrushes), 这两项才是 UI 线程的主要开销。
    /// </summary>
    private const int LiveChatMaxPerBatch = 16;

    /// <summary>多少个 33ms 的 tick 攒成一次聊天列表批量追加(3 × 33ms ≈ 100ms)。</summary>
    private const int LiveChatTicksPerBatch = 3;

    /// <summary>距离下一个聊天批次的 tick 计数(见 LiveChatTicksPerBatch)。</summary>
    private int _liveChatTickCounter = 1;

    /// <summary>裁剪聊天列表时一次至少裁掉多少条(见 TrimLiveChatAndScroll)。</summary>
    private const int LiveChatTrimChunk = 20;

    /// <summary>
    /// 聊天"待显示缓冲"的上限。
    /// ★ 它只在两个批次点之间攒消息(约 100ms), 正常量远小于这个值;
    ///   设成 64 是给热门房间的突发峰值留余量, 同时保证内存不会因为堆积而失控。
    /// </summary>
    private const int LiveChatPendingMax = 64;

    /// <summary>攒着还没灌进右侧列表的聊天消息(见 FlushLiveChatPending)。</summary>
    private readonly List<LiveDanmakuItem> _liveChatPending = new();

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

    /// <summary>
    /// 每个 tick 最多渲染几条。
    /// ★ 2026-11-17: 从 8 降到 3 —— tick 也从 100ms 缩到 33ms(见 LiveDanmakuPumpInterval),
    ///   单位时间的上限反而略降, 但**上屏更细**。原来 100ms 一口气放 8 条, 这 8 条是同一帧
    ///   贴到右边缘的, 观感是"一坨一坨地涌"; 现在每 33ms 只放 3 条, 连续感明显更好。
    ///   上限仍是 3/33ms ≈ 90 条/秒, 远高于热门房间的常见速率, 不会因为削峰把弹幕丢掉。
    /// </summary>
    private const int LiveDanmakuMaxPerTick = 3;

    /// <summary>
    /// 同一批里相邻两条弹幕的出生时间错开多少秒。
    /// ★ 一个 tick 里上屏的几条如果 SpawnClock 完全相同, 它们会永远并排贴在一起飞 ——
    ///   哪怕只错开 60ms, 视觉上就是"依次进场"。媒体时钟下这点偏移会被校准吸收,
    ///   直播时钟是纯墙钟, 错开多少就是多少, 所以这个值必须小且固定。
    /// </summary>
    private const double LiveDanmakuSpawnStaggerSeconds = 0.06;

    /// <summary>相同文本的重复判定窗口(毫秒)</summary>
    private const int LiveDanmakuRepeatWindowMs = 10_000;

    /// <summary>同一窗口内相同文本最多保留几条(超过视为复读刷屏)</summary>
    private const int LiveDanmakuRepeatMax = 3;

    /// <summary>
    /// 出队节拍。
    /// ★ 2026-11-17: 100ms → 33ms。100ms 的节拍本身就会让弹幕"一涌一涌"(人眼对 100ms
    ///   的批次非常敏感), 而 33ms 与 30fps 同拍, 上屏时机自然得多。
    /// ★ 用 Normal 优先级而不是默认值: 默认 Background 会被布局/输入排在后面,
    ///   热门房间聊天列表正在重排时, 弹幕的上屏时机会被推迟上百毫秒 —— 那也是一顿。
    /// </summary>
    private static readonly TimeSpan LiveDanmakuPumpInterval = TimeSpan.FromMilliseconds(33);

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

        // ★ 直播时钟模式必须一起退出(2026-11-17): 否则切回点播后 CalibrateDanmakuClock
        //   会继续被"直播模式"忽略, 弹幕时钟再也不跟媒体时间对齐 —— 拖进度条后弹幕不纠偏。
        _dmClockLive = false;

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
        _liveChatPending.Clear();
        _liveChatTickCounter = 1;
    }

    private void StartLiveDanmakuPump()
    {
        if (_liveDanmakuPump == null)
        {
            // ★ Normal 优先级: 默认(Background)会被布局/输入排在后面, 热门房间聊天列表
            //   正在重排时弹幕上屏会被推迟上百毫秒 —— 那也是一顿(见 LiveDanmakuPumpInterval)。
            _liveDanmakuPump = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = LiveDanmakuPumpInterval
            };
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
    /// 每 33ms 把队列里的弹幕放上屏 / 追加到右侧聊天列表。
    ///
    /// ★ 两个落点的活跃条件不同, 必须分开判断:
    ///   · 右侧聊天列表: **只要还在直播就追加**, 与播放状态、弹幕开关无关 ——
    ///     用户暂停或关掉滚动弹幕时, 聊天记录仍然应该继续走(参考图里就是常驻一块聊天区)。
    ///   · 视频上的滚动弹幕: 只在真正播放且弹幕时钟已起来时渲染。
    ///     缓冲/暂停时到达的消息不再补播, 否则恢复播放会一次性糊上来。
    ///
    /// ★ 2026-11-17 两处节奏调整(修"弹幕一涌一涌 / 位移卡顿"):
    ///   ① tick 从 100ms 缩到 33ms、每 tick 只放 3 条, 同一批的出生时间再错开 60ms ——
    ///      原来 100ms 一口气放 8 条, 那 8 条是同一帧贴到右边缘的, 看着就是"一坨";
    ///   ② 聊天列表改成**每 3 个 tick(≈100ms)批量追加一次**(批次大小与原实现相同):
    ///      气泡模板很重(Border+Grid+3 个 Run), 33ms 就往里塞一批会把 UI 线程切碎,
    ///      反而把弹幕的位移拖卡。
    /// </summary>
    private void OnLiveDanmakuPumpTick(object? sender, EventArgs e)
    {
        if (_closing || !_isLive)
        {
            _liveDanmakuQueue.Clear();
            return;
        }

        // 队列空就立刻返回: 下面每 tick 都要读一次 _mp.IsPlaying(原生调用),
        // 33ms 一拍空转也白搭。
        // ★ 但攒着的聊天消息还没灌进去时不能返回 —— 否则最后一批会卡在缓冲里,
        //   直到下一条弹幕到达才补上(表现为"聊天列表忽然不动了")。
        if (_liveDanmakuQueue.IsEmpty && _liveChatPending.Count == 0) return;

        var canRender = _mp != null && _mp.IsPlaying && _dmClockRunning && _danmakuOn;
        var shown = 0;
        var chatDue = --_liveChatTickCounter <= 0;
        if (chatDue) _liveChatTickCounter = LiveChatTicksPerBatch;

        // 本 tick 的基准时间只取一次: 一批弹幕共用同一个基准, 再按 LiveDanmakuSpawnStaggerSeconds
        // 逐条错开 —— 每条各读一次时钟会让同批弹幕的时间差变成"CPU 执行速度", 不可控。
        var baseClock = canRender ? DanmakuClock() : 0;

        while (_liveDanmakuQueue.TryDequeue(out var item))
        {
            // ① 右侧聊天列表: **先攒进待显示缓冲**, 到批次点再一次性灌进 ObservableCollection。
            //    ★ 不能"本 tick 不是批次点就丢掉" —— 队列每 tick 都会被清空, 那样 2/3 的
            //      聊天消息会凭空消失(弹幕列表是给人看记录的, 不能这么削峰)。
            //    ★ 缓冲本身有上限: 热门房间一秒几百条时, 保住最新的那些就够了。
            if (_liveChatPending.Count < LiveChatPendingMax)
                _liveChatPending.Add(item);

            // ② 视频上的滚动弹幕
            if (!canRender || shown >= LiveDanmakuMaxPerTick) continue;
            if (!ShouldShowLiveDanmaku(item)) continue;
            // 同批共用 baseClock, 再逐条错开: 不错开的话这几条会永远并排贴在一起飞。
            AddDanmaku(item.Text, item.Mode, baseClock, item.Color,
                spawnClock: baseClock + shown * LiveDanmakuSpawnStaggerSeconds);
            shown++;
        }

        if (chatDue) FlushLiveChatPending();
    }

    /// <summary>
    /// 把攒下的聊天消息批量灌进右侧列表。
    /// ★ 一批最多 <see cref="LiveChatMaxPerBatch"/> 条; 超出的直接丢最旧的 ——
    ///   这是**列表显示**的削峰, 与滚动弹幕完全无关。
    /// </summary>
    private void FlushLiveChatPending()
    {
        if (_liveChatPending.Count == 0) return;

        var take = Math.Min(_liveChatPending.Count, LiveChatMaxPerBatch);
        // 超量时丢掉最旧的那部分(保留最新的, 与"聊天列表看最新"的语义一致)
        var skip = _liveChatPending.Count - take;
        for (var i = 0; i < take; i++) AppendLiveChat(_liveChatPending[skip + i]);
        _liveChatPending.Clear();
        TrimLiveChatAndScroll();
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
            // ★ 走缓存: TryFindResource 会走一遍资源查找链, 热门房间每秒几十条房管消息,
            //   每条查两次纯属浪费(2026-11-17)。笔刷本身由 ThemeService 就地改色, 引用可长期持有。
            var (adminBg, adminFg) = LiveAdminBadgeBrushes();
            badgeBrush = adminBg;
            badgeTextBrush = adminFg;
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

    /// <summary>
    /// 房管徽章的两支笔刷(背景 / 文字), 首次取到后缓存。
    /// ★ 缓存的是**资源引用本身**: ThemeService 换肤时是就地改色, 引用不变, 所以缓存不会失效。
    /// </summary>
    private (Brush Background, Brush Foreground) LiveAdminBadgeBrushes()
    {
        if (_liveAdminBadgeBg != null && _liveAdminBadgeFg != null)
            return (_liveAdminBadgeBg, _liveAdminBadgeFg);

        _liveAdminBadgeBg = TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;
        _liveAdminBadgeFg = TryFindResource("TextOnAccentBrush") as Brush ?? Brushes.White;
        return (_liveAdminBadgeBg, _liveAdminBadgeFg);
    }

    private Brush? _liveAdminBadgeBg;
    private Brush? _liveAdminBadgeFg;

    /// <summary>粉丝勋章背景色。0/非法值用默认青色(B站勋章常见色系)。</summary>
    private Brush LiveBadgeBrush(uint color)    {
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

    /// <summary>
    /// 裁掉过旧的消息, 并在用户没有往上翻时贴到底部。
    ///
    /// ★ 2026-11-17: 改成"超了就一次裁掉一批", 不再逐条 RemoveAt(0)。
    ///   ObservableCollection.RemoveAt(0) 每次都要让 ListBox 把**整个**集合往下挪一位,
    ///   热门房间每 100ms 都在触发, 逐条裁比批量裁多出几十倍的布局通知。
    /// </summary>
    private void TrimLiveChatAndScroll()
    {
        var overflow = _liveChat.Count - LiveChatMaxEntries;
        if (overflow > 0)
        {
            // 一次裁到上限以下 20 条, 留出余量: 免得之后每个批次都要裁一次。
            var trim = Math.Max(overflow, LiveChatTrimChunk);
            trim = Math.Min(trim, _liveChat.Count);
            for (var i = 0; i < trim; i++) _liveChat.RemoveAt(0);
        }

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

        // 发送区的可用性(登录态 / 是否直播)跟着换片一起刷新。
        UpdateLiveDanmakuComposer();
    }

    // ------------------------------------------------------------ 发送弹幕(2026-11-28)

    /// <summary>
    /// 发送区(输入框 / 占位提示 / 发送按钮)的可用状态。
    ///
    /// ★ 与评论输入框同一套前提: 已登录 + 当前是直播。
    ///   未登录时直接禁用输入, 比让用户敲完一大段再点发送报错要好。
    /// ★ 登录态变化会走 Svc.Session.Changed → UpdateButtonState → 这里, 不需要额外订阅。
    /// </summary>
    private void UpdateLiveDanmakuComposer()
    {
        if (_closing || LiveDanmakuDraft == null) return;

        var canSend = _isLive && Svc.Session.HasLogin;
        LiveDanmakuDraft.IsEnabled = canSend;
        LiveDanmakuDraft.ToolTip = canSend ? null : "登录后才能发送弹幕";

        if (LiveDanmakuPlaceholder != null)
        {
            LiveDanmakuPlaceholder.Text = canSend ? "发个弹幕吧…" : "登录后才能发送弹幕";
            LiveDanmakuPlaceholder.Visibility = LiveDanmakuDraft.Text.Length == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        UpdateSendLiveDanmakuButton();
    }

    /// <summary>发送按钮: 有内容、已登录、且没有请求在途时才可点。</summary>
    private void UpdateSendLiveDanmakuButton()
    {
        if (BtnSendLiveDanmaku == null || LiveDanmakuDraft == null) return;

        BtnSendLiveDanmaku.IsEnabled = LiveDanmakuDraft.IsEnabled
                                       && !_sendingLiveDanmaku
                                       && !string.IsNullOrWhiteSpace(LiveDanmakuDraft.Text);
    }

    private void OnLiveDanmakuDraftChanged(object sender, TextChangedEventArgs e)
    {
        // TextBox 没有 Placeholder: 有内容时把叠在上面的提示藏掉。
        if (LiveDanmakuPlaceholder != null)
            LiveDanmakuPlaceholder.Visibility = LiveDanmakuDraft.Text.Length == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

        UpdateSendLiveDanmakuButton();
    }

    /// <summary>
    /// Enter 发送。
    /// ★ 用 Preview 阶段拦: 直播弹幕是单行, 不需要 Shift+Enter 换行;
    ///   把 Enter 直接吃掉可以避免 TextBox 自己插入换行/发出提示音。
    /// </summary>
    private void OnLiveDanmakuDraftPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = SendLiveDanmakuAsync();
    }

    private void OnSendLiveDanmakuClick(object sender, RoutedEventArgs e)
        => _ = SendLiveDanmakuAsync();

    /// <summary>
    /// 发送一条直播文本弹幕。
    ///
    /// ★ 成功只清空输入, 不本地插假消息: 服务端会把这条弹幕原样回推 WS
    ///   (带真实昵称/勋章/房管身份), 右侧聊天列表与滚动弹幕都会自然显示。
    ///   本地再插一条只会在回显到达时变成两条。
    /// ★ 失败**不清空**输入: 网络抖一下就把用户写的字抹掉是最让人恼火的行为。
    /// ★ 请求返回时直播间可能已经换了: 用 _liveRoomId(片源标识, 不随 room_info 回填变化)
    ///   做代次校验, 避免旧请求的 Toast/状态复位跑到新直播间里。
    /// </summary>
    private async Task SendLiveDanmakuAsync()
    {
        if (_closing || _sendingLiveDanmaku) return;
        if (!_isLive || _liveRoomId <= 0) return;
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("请先登录后再发送弹幕"); return; }

        var text = LiveDanmakuSendPolicy.NormalizeText(LiveDanmakuDraft?.Text);
        if (text.Length == 0) return;

        var liveRoom = _liveRoomId;
        // 优先用真实长号: room_info 回来之前退回卡片上的房间号(绝大多数情况下也是长号)。
        var sendRoomId = CurrentLiveSendRoomId();

        _sendingLiveDanmaku = true;
        UpdateSendLiveDanmakuButton();
        try
        {
            var (ok, err) = await Svc.Api.SendLiveDanmakuAsync(sendRoomId, text);
            if (_closing || !_isLive || liveRoom != _liveRoomId) return;

            if (!ok)
            {
                Svc.Toast.Show(err ?? "弹幕发送失败");
                return;
            }

            LiveDanmakuDraft?.Clear();
            // 发完把聊天列表贴回底部: 用户可能正翻着历史, 自己的回显不该被埋在上面。
            _liveChatAutoScroll = true;
            // 连续发弹幕是常见操作, 把焦点留在输入框(不要丢给发送按钮)。
            LiveDanmakuDraft?.Focus();
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            if (!_closing) Svc.Toast.Show("弹幕发送失败");
        }
        finally
        {
            // 只有还停在同一路直播时才复位; 换片路径已经把它清成 false 了。
            if (!_closing && _isLive && liveRoom == _liveRoomId)
            {
                _sendingLiveDanmaku = false;
                UpdateSendLiveDanmakuButton();
            }
        }
    }

    /// <summary>
    /// 发送时使用的直播间号。
    /// ★ 直播发送接口按真实长号工作; room_info 回来后 <see cref="LiveRoomInfo.RoomId"/>
    ///   就是真实号, 比卡片上的展示号可靠。拿不到时才回退 _liveRoomId。
    /// </summary>
    private long CurrentLiveSendRoomId()
    {
        var real = _liveRoomInfo?.RoomId ?? 0;
        return real > 0 ? real : _liveRoomId;
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
