using System;
using System.Collections.Generic;
using System.Diagnostics;
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

/// <summary>
/// 原生视频播放器窗口 (LibVLC 内核): 不再使用 WebView2, 视频流由 B 站 playurl 接口获取
/// 周边 UI 仍然是 WPF 原生(标题/UP 主卡片/点赞/收藏/评论/弹幕层), 视觉与应用主题一致
/// </summary>
public partial class PlayerWindow : FluentWindow
{
    private readonly LibVLC _libVLC;
    private readonly MediaPlayer _mp;
    private readonly DispatcherTimer _progressTimer;
    private string _currentBvid = "";
    /// <summary>当前播的是离线缓存里的本地文件(没有 aid/cid, 也没有在线互动)</summary>
    private bool _isLocalPlayback;

    /// <summary>
    /// 当前播的是**直播间**。和本地文件一样没有 aid/cid, 所以互动/评论/进度上报都不做;
    /// 单独留一个标志只是为了把提示文案和进度条形态区分开(直播没有总时长, 进度条要禁用)。
    /// </summary>
    private bool _isLive;
    private long _liveRoomId;
    private long _currentCid;
    private long _ownerMid;
    private string _ownerName = "";
    /// <summary>UP 主头像地址(供右键"看大图/保存"使用, 显示时只用其缩放后的位图)</summary>
    private string _ownerFace = "";
    private long _aid;
    private long _totalMs;
    private bool _isSeeking;

    /// <summary>
    /// 跳转后的"宽限期"截止时刻(`Environment.TickCount64`)。
    ///
    /// 为什么需要: LibVLC 的 `Position`/`Time` **不会**在设置之后立刻反映新位置 ——
    /// 它得先重建解复用器、把目标位置附近的片段拉回来才能报出新时间。而进度定时器每 500ms
    /// 就会把 `_mp.Position` 回写到进度条上, 于是刚点完进度条就被"打回"到旧位置的刻度上,
    /// 用户看到的是"点了没反应", 过一会儿又突然跳过去。短视频(十几秒)尤其明显 ——
    /// 6 秒的视频里 0.5 秒的心跳间隔相当于整段时长的 8%。
    /// 宽限期内不参与回写, 并在这段时间里直接把滑块钉在跳转目标上。
    /// </summary>
    private long _seekGraceUntil;

    /// <summary>已经播到结尾(此时不 Stop, 而是盖一层"重播"遮罩, 见 OnEndReached)</summary>
    private bool _ended;

    /// <summary>
    /// 跳转后的宽限期时长: 这段时间内不把上报的位置回写到进度条。
    ///
    /// 原设计前提是"跳转刚发出时 Position 还是旧值, 回写会把用户刚点的位置顶掉"。
    /// 但 2026-09-25 实测表明前提不成立 —— LibVLC 的 Time/Position 在 seek 发出的 **100ms 内
    /// 就变成目标值**(那只是"已受理"回执, 画面还没动)。所以这个宽限期现在的实际作用只剩
    /// "兜底": 等待期本来就已经由看门狗(watchdog 分支提前 return)挡住回写了, 这里多一层保险,
    /// 防"看门狗因暂停态提前放行"那一瞬间滑块被回写摆动。
    /// 保留它是因为成本为零; 但**别再引用"Position 还是旧值"这个已被证伪的说法**。
    /// </summary>
    private const int SeekGraceMs = 500;

    // --- 跳转看门狗 ---
    // LibVLC 的 seek 是异步的: 网络流上偶尔会"丢"一次跳转指令(解复用器重建失败/片段拉取慢),
    // 表现为"拖了进度条要等好几秒甚至永远不动"。看门狗在跳转发出后盯着 _mp.Time,
    // 迟迟没到目标就重发一次(最多 SeekRetryMax 次), 等待太久就给出缓冲提示。
    private long _pendingSeekMs = -1;     // 待确认的跳转目标(毫秒), -1 = 没有
    private long _seekDeadline;           // 本次跳转的确认截止时刻(TickCount64)
    private int _seekRetries;             // 已重发的次数

    /// <summary>
    /// seek 静音: 跳转后视频画面要等缓冲(几百 ms~几秒), 但音频解复用快, 会**先出声** ——
    /// 用户听到的是"画面还黑着/冻着, 声音已经播到那边去了"。所以 seek 一发出就静音,
    /// 画面确认到位(或放弃/换片/播完)再恢复。-1 = 当前没有处于 seek 静音。
    /// </summary>
    private int _seekMuteSavedVolume = -1;
    private const int SeekRetryMax = 2;
    private const long SeekConfirmWindowMs = 900;  // Time 距目标多少毫秒内算"到位"

    /// <summary>
    /// 判定"位置确实离开了跳转前那一点"的容差(毫秒)。
    ///
    /// 1500ms 的依据: 它只需要把"停在原处"(seek 被丢弃)与"落到别处"(分片边界)分开,
    /// 而 LibVLC 一旦真的受理了 seek, 位置会直接跳到几秒~几分钟以外 —— 实测 60s 的跳转
    /// 落在 55.2s、150s 的落在 145.7s, 都远超这个容差; 反过来"丢弃"时长视频里位置一动不动。
    /// 取太小会把 DASH 的"落点偏早"误判成丢失, 取太大则反过来漏掉真正的丢失。
    /// </summary>
    private const long SeekLandingToleranceMs = 1500;

    /// <summary>
    /// 上一次心跳里"位置同时远离跳转起点与目标"的那个值(-1 = 上一次不满足)。
    /// 用来给"落地但偏早"做**连续两次采样**确认, 见 UpdateProgressUi 里的说明。
    /// </summary>
    private long _seekElsewhereSeenMs = -1;

    /// <summary>
    /// 上一次观察到的播放位置。**不是**用来判断"接近目标"的, 而是用来判断"位置有没有真的在走" ——
    /// 见 UpdateProgressUi 里那段"为什么不能只看 Time 接近目标"的说明。
    /// </summary>
    private long _seekObservedMs = -1;

    /// <summary>
    /// 本次跳转**发出之前**的位置(取自 TimeChanged 的最后一次真实上报)。
    ///
    /// 用途只有一个: 把"seek 被 LibVLC 丢了"和"seek 生效了、但落点明显偏早"分开 ——
    ///   · 位置还停在 <c>_seekFromMs</c> 附近  ⇒ 丢了, 该重发(见 UpdateProgressUi 的看门狗);
    ///   · 位置已经离开 <c>_seekFromMs</c> 且也不在目标附近 ⇒ 其实是落到了目标之前的分片边界
    ///     (远端 DASH 双流的固有行为), 该**收手**, 不能重发。
    ///
    /// ★ 为什么必须分开(2026-09-28 探针实测, 见下方 SeekLandingToleranceMs 的说明):
    ///   把这种情况当成"丢失"重发, 每次重发都会让 LibVLC 重建整个 input(重新建连 + 重新预缓冲),
    ///   把本来已经在正常前进的播放又打回去 —— 实测一次 5 秒的跳转因此被拖到 7.5 秒以上。
    /// </summary>
    private long _seekFromMs = -1;

    /// <summary>
    /// 跳转后多久还没到位就弹加载动画。
    ///
    /// 900ms 是实测出来的: 正常跳转"画面冻住"的时间实测为 470ms(本地/合流单文件)~
    /// 800ms(远端 DASH 双流), 阈值必须高于它, 否则每次正常跳转都会闪一下提示。
    /// 超过 900ms 还没动, 那就真的是在缓冲了, 这时候给反馈才有意义。
    /// </summary>
    private const long SeekBufferingDelayMs = 900;

    /// <summary>
    /// 跳转等待期的进度心跳间隔。
    /// 到位判定和缓冲提示都只在心跳里跑, 平时的 500ms 粒度太粗 —— 等满一跳才提示,
    /// 用户已经先看到了"冻住"。等待期改成 100ms: 提示能在阈值附近立刻出现, 到位判定也更细。
    /// </summary>
    private static readonly TimeSpan ProgressTickSeeking = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ProgressTickIdle = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 缓冲提示文案。"跳转卡住"和"播放中途卡住"共用同一条 —— 对用户来说都是"在缓冲",
    /// 而且收起时要用它判断"这张遮罩是不是我弹的"(别误收"正在切换清晰度"那种)。
    /// </summary>
    private const string BufferingText = "正在缓冲…";

    // --- 提前结束的自救(DASH 音轨先 EOF) ---
    // DASH 的音轨是 input-slave 挂载的独立流, 它先于视频到 EOF 时, LibVLC 3 会结束
    // 整个 input —— 表现为"最后几秒没声音、画面冻住、进度条提前满格"。这不是真的播完:
    // 记下最后位置, 重建 Media 从断点续播。次数 + 推进距离双保险, 防"音频确实比视频短"
    // 的片源陷入无限续播循环。
    //
    // ★ 尾部截断(2026-10-01, 用户报"结尾不断缓冲 + 进度条回退重播几秒前内容"的修复):
    //   离结尾太近时**绝不自救**。原因链: ① 续播要重建 input, 必然重新预缓冲(黑屏);
    //   ② 续播的 seek 在远端 DASH 双流上落点固定偏早 4.5~5s(见 notes-player-internals.md),
    //     所以每次自救都是"回退 5 秒重播已看过的内容"; ③ 播完回退的那几秒音轨又 EOF,
    //     再触发一次自救 —— 推进距离检查被"回退"本身满足, 于是缓冲+回退反复最多 3 次。
    //   ★★ 阈值取 10s(2026-10-01 第二次校准): 第一版 6.5s 实测不够 —— 用户实测一条
    //     08:39 的视频, 音轨 EOF 点在剩 7s 处(512s < 519-6.5=512.5s, 差 0.5s 自救照常
    //     触发), 表现 = 黑屏缓冲几秒 → 重建后续播点音轨已物理缺失 → 立刻再 EOF → 直接
    //     重播遮罩, 纯白等一趟。B 站 DASH 的音轨缺口(音频分片对齐导致)常见 0~10s,
    //     阈值必须盖过缺口的**上限**而不是"落点偏早量" —— 反正缺口之内自救也救不回
    //     任何东西(音轨在那里本来就没有数据), 直接按播完处理。
    private long _lastKnownMs;            // TimeChanged 报告的最后位置(EOF 后 _mp.Time 会归零, 不能用)
    private int _eofResumeCount;
    private long _lastEofResumeMs;
    private const int EofResumeMax = 3;
    private const long EofResumeMinAdvanceMs = 2000;
    /// <summary>
    /// 尾部静默区宽度(毫秒)。距结尾不足这个距离时, 三件事都不做:
    ///   ① EOF 不自救(重建 input 只会黑屏缓冲一趟, 音轨缺口内没有可救的数据);
    ///   ② 卡顿检测不报"正在缓冲"(两路流长度差导致的 Time 停滞是收尾常态);
    ///   ③ 进度显示改用"卡住起点 + 墙钟×倍速"外推(让进度条平滑走完最后一程)。
    /// 取值必须 ≥ B 站 DASH 音轨缺口的实际上限(实测 ~7s, 常见 0~10s)。
    /// </summary>
    private const long TailQuietZoneMs = 10000;

    /// <summary>
    /// 视频区手势层的常态底色(#01000000 —— 近透明但可命中鼠标, 见 VideoAreaRoot 的 XAML 注释)。
    /// EOF 时手势层会切黑盖住露出白底的 vout HWND(见 OnEndReached), 起播时用它还原。
    /// </summary>
    private static readonly Brush VideoAreaBackBrush = CreateVideoAreaBackBrush();

    // --- 全屏黑幕(见 ArmFullscreenCover 的说明) ---
    /// <summary>黑幕已压下、等待首个新帧撤掉</summary>
    private bool _fullscreenCoverArmed;
    private DispatcherTimer? _fullscreenCoverTimer;

    /// <summary>黑幕兜底超时(毫秒): 一直没等到新帧就自己撤掉, 免得挡住画面</summary>
    private const int FullscreenCoverTimeoutMs = 600;

    private static Brush CreateVideoAreaBackBrush()
    {
        var b = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x01, 0, 0, 0));
        b.Freeze();
        return b;
    }

    private bool _danmakuOn = true;

    /// <summary>是否显示彩色弹幕(全局设置)。关掉之后所有弹幕统一白色, 只看内容不受颜色干扰。</summary>
    private bool _danmakuColorful = true;

    // 弹幕条目类型见 Helpers/RawDanmaku(时间秒 / 文本 / 模式 / 颜色)
    private readonly List<RawDanmaku> _danmaku = new();

    /// <summary>
    /// 未经过滤的原始弹幕(按时间有序)。
    /// 保留它是为了让"设置页改了屏蔽词/智能屏蔽"能在不重新请求接口的前提下立刻重算 _danmaku ——
    /// 否则用户想试一个屏蔽词就得重新拉一遍弹幕, 体验很差(而且长视频要连拉几十个分段)。
    /// </summary>
    private readonly List<RawDanmaku> _danmakuRaw = new();
    private int _indexInLib;
    private string? _lastPlayUrl;

    /// <summary>
    /// 上一次用的音轨地址(DASH 才有, 单文件源为 null)。
    /// 留着它只为一件事: 「重播」。DASH 的音视频是两个文件, 重播必须原样重建这个组合,
    /// 否则重播会变成"只有画面没有声音"。
    /// </summary>
    private string? _lastAudioUrl;

    // --- 关闭流程状态 ---
    // _closing:     关闭流程一开始就置位, 用于拦住所有 LibVLC 原生回调与尚未跑完的异步流程。
    //               LibVLC 的事件都在它自己的内部线程上触发, 不受 WPF 关闭顺序保护,
    //               若不拦住, 窗口销毁后回调仍会访问已释放的原生对象(0xc0000005 访问违例)。
    // _teardownDone: 原生资源拆解完成标记, 让第二次 Close() 能正常放行。
    private volatile bool _closing;
    private bool _teardownDone;
    // 是否已在 VlcCore 登记本窗口的 MediaPlayer(构造函数中途抛异常时可能没登记, 避免计数漂移)
    private bool _playerRegistered;

    // --- 清晰度 ---
    private readonly List<(int qn, string label)> _qualities = new();

    /// <summary>
    /// 本次播放**请求**的清晰度(qn)。
    ///
    /// ★ 2026-10-04 起它的初值不再是写死的 80, 而是用户设置里的「默认画质」
    ///   (见 SettingsStore.PreferredQualityQn)。每个片源开始时由
    ///   <see cref="ResetForNewMedia"/> 重新快照一次 —— 所以用户在设置页改完再点开
    ///   下一个视频就生效, 不需要重启。
    /// ★ 起播完成后这里会被换成**实际拿到**的档位(见 FillQualityMenu): 该档不存在或
    ///   没有权限时 B 站会降级返回别的档, 之后"切清晰度"的基准应该是实际在播的那一档。
    /// </summary>
    private int _currentQn = QualityPreference.DefaultQn;
    private bool _switchingQuality;
    private bool _fillingQualityMenu;
    // 切换清晰度后要恢复到的进度(>0 生效, 起播后清零)
    private long _resumeAfterSwitchMs;

    // --- 弹幕显示区域占比(默认 25%, 即弹幕只占画面上方 1/4) ---
    private double _danmakuAreaRatio = 0.25;

    // --- 点赞状态(用于点赞/取消点赞切换) ---
    private bool _isLiked;
    // --- 关注状态(用于关注/取消关注切换) ---
    private bool _isFollowed;
    // --- 进度条 UI 回写保护(避免定时器刷新触发 ValueChanged 造成 seek 循环) ---
    private bool _updatingSlider;

    /// <summary>收藏动作进行中, 防止连点导致 add/del 交叉</summary>
    private bool _favBusy;

    /// <summary>当前视频是否已在「稍后再看」里(用于按钮的点亮态)</summary>
    private bool _isInWatchLater;

    /// <summary>稍后再看的加入/移出进行中, 防止连点把 add/del 交叉发出去</summary>
    private bool _watchLaterBusy;
    /// <summary>操作栏计数(2026-09-26 起操作栏是"图标 + 数量", 不再是"点赞/投币/收藏"三个字)</summary>
    private long _likeCount;
    private long _coinCount;
    private long _favCount;
    private bool _isFavorited;
    // 当前视频标题(收藏夹选择窗的副标题用; 窗口 Title 带 " - BiliDesk" 后缀不能直接用)
    private string _videoTitle = "";

    // --- 视频区手势: 单击播放暂停 / 双击全屏 / 长按 2x 快进 ---
    private DispatcherTimer? _longPressTimer;     // 检测长按 >= 500ms
    private DispatcherTimer? _singleClickTimer;   // 单击延迟执行(给双击留判定窗口)
    private bool _longPressFired;                  // 当前是否处于长按快进态
    // 双击抑制标记: 第二次点击(Down, ClickCount>=2)到达时置位, 让随后的 Up 不再启动单击定时器。
    // 没有这个标记时: Down 阶段虽然停掉了单击定时器, 但 Up 阶段又会新建一个,
    // 于是双击会先全屏、再在 ~200ms 后补一次播放/暂停 —— 就是"双击既暂停又全屏"的根因。
    private bool _suppressSingleClick;
    private const float FastForwardRate = 2.0f;    // 长按快进的倍速

    // --- SponsorBlock(跳过社区标注的赞助 / 片头片尾片段) ---
    // 原始数据(未过滤, 含所有类别)与"按当前设置挑出来的"分开存:
    // 用户在设置页改类别时, 用原始数据就能立刻重算, 不必再问一次第三方服务。
    private IReadOnlyList<SponsorSegment> _sponsorRaw = Array.Empty<SponsorSegment>();
    private IReadOnlyList<SponsorSegment> _sponsorActive = Array.Empty<SponsorSegment>();

    /// <summary>
    /// 已经处理过的片段 UUID(自动跳过过的, 以及被用户「撤回」的)。
    /// 处理过就不再自动跳 —— 否则用户一点撤回就又被跳走, 撤回等于无效。
    /// </summary>
    private readonly HashSet<string> _sponsorHandled = new(StringComparer.Ordinal);

    /// <summary>最近一次自动跳过的片段(「撤回」要用它算回到哪一秒)</summary>
    private SponsorSegment? _lastSkipSeg;
    private DispatcherTimer? _skipNoticeTimer;

    /// <summary>
    /// 本次取片段请求的取消源: 换片 / 关窗要能立刻掐掉, 别让上一个视频的查询继续跑。
    /// </summary>
    private CancellationTokenSource? _sponsorCts;

    /// <summary>
    /// 起播时为"取片段"预留的等待上限。
    ///
    /// 片段查询与"取播放地址 / 合流下载"是**并行**跑的, 绝大多数情况下这一步早就回来了;
    /// 这里再等 800ms 是为了拿到"开场就是广告"时能做空降(见 ApplySponsorRaw)。
    /// 等不到就照常起播 —— 心跳会在 0.5 秒内补上跳过, 顶多闪一下, 绝不为它推迟起播。
    /// </summary>
    private const int SponsorWaitMs = 800;

    /// <summary>片段剩余不足这么多毫秒时不跳: 跳到片段末尾只前进一点点, 反而像画面抽了一下</summary>
    private const long SponsorMinRemainMs = 500;

    public PlayerWindow() : this("") { }
    public PlayerWindow(string bvid)
    {
        InitializeComponent();
        Closed += OnClosed;
        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
        // 鼠标侧键返回: 挂在窗口上, 也挂在视频区那层浮动覆盖窗口上(见 HookOverlayKeyboard)
        PreviewMouseDown += OnPreviewMouseDown;
        Svc.Toast.Shown += OnToast;
        Svc.Session.Changed += UpdateButtonState;

        // 这里**不再**关掉这个窗口的 DWM 过渡动画。
        // ★★★ 2026-10-02 按用户要求"全屏动画改用 Windows 系统自带的": 以前为了让"秒切"干净,
        //   在 SourceInitialized 里调 DwmInterop.DisableWindowTransitions 把系统那段窗口缩放动画
        //   关掉了 —— 那正是"全屏没有动画、硬切一下"的原因。现在反过来, 让 DWM 自己播这段过渡。
        //   ★ 别再把 DisableWindowTransitions 调回来: 它一开, 系统动画就被掐掉, 全屏又变成硬切。
        //   (主窗口的最大化/还原同理 —— 见 FluentWindow.OnStateChanged。)

        // 清晰度/弹幕区域弹层的定位目标**必须代码直连**: XAML 的 ElementName 绑定在
        // VideoView 的 ForegroundWindow 浮层里解析失败(内容被移进另一个可视树后名字
        // 查找断链), PlacementTarget 为空 → 弹层跑到了浮层左上角(真机截图实证)。
        // 同时右缘对齐: Placement=Top 默认左缘对齐, 这两个弹层在控制栏右段, 左对齐偏丑;
        // Opened 时量出宽度差做负偏移, 弹层右缘与按钮右缘平齐。
        QualityPopup.PlacementTarget = BtnQuality;
        DanmakuAreaPopup.PlacementTarget = BtnDanmakuArea;
        // 右缘对齐(弹层右缘 = 按钮右缘)。注意必须等弹层布局完成后再量宽:
        // Opened 那一刻 ActualWidth 还是 0, 偏移会算成正数把弹层推出窗口右缘(真机实证)。
        // Loaded 优先级在 Popup 自身布局跑完之后, 那时 ActualWidth 才可信。
        QualityPopup.Opened += (_, _) => Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                RightAlignPopup(QualityPopup, BtnQuality)));
        DanmakuAreaPopup.Opened += (_, _) => Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                RightAlignPopup(DanmakuAreaPopup, BtnDanmakuArea)));
        // 主题无需在此订阅: FluentWindow 基类已监听 ThemeChanged 并重建窗体 chrome,
        // 播放器自身的 DynamicResource 会随之自动刷新。之前这里多订阅了一次且从不退订,
        // 导致每个关闭过的播放器窗口都无法被 GC 回收。
        // 弹幕设置(全局): 初始化运行时状态, 并订阅设置页的变更
        _danmakuOn = Svc.Settings.DanmakuEnabled;
        _danmakuColorful = Svc.Settings.DanmakuColorful;
        _danmakuAreaRatio = Svc.Settings.DanmakuAreaPercent / 100.0;
        Svc.Settings.DanmakuSettingsChanged += OnDanmakuSettingsChanged;
        // 跳过赞助片段: 开关/类别一变就重算(关掉要立刻停, 改类别要立刻生效)
        Svc.Settings.SponsorBlockChanged += OnSponsorSettingsChanged;
        // 进度条色块是按像素摆的, 控件一改宽度就得重画(窗口缩放、切全屏、控制栏重排都会触发)。
        // 重画成本只有几个 Rectangle, 不必做节流。
        SponsorMarks.SizeChanged += (_, _) => RenderSponsorMarks();
        if (DanmakuAreaSlider != null) DanmakuAreaSlider.Value = Svc.Settings.DanmakuAreaPercent;
        UpdateDanmakuButton();

        // LibVLC 走进程级共享单例(见 VlcCore): 初始化全进程只做一次, 且永不在这里释放。
        // 反复 new/Dispose 原生库会引发 Windows 级访问违例弹窗:
        //   "Exception Processing Message 0xc0000005 - Unexpected parameters"
        // 窗口只负责挂载属于自己的 MediaPlayer, 开关窗口不再触碰原生库生命周期。
        try
        {
            _libVLC = VlcCore.Get();
        }
        catch (Exception ex)
        {
            WriteLibVLCDebug(
                $"\n\n--- VlcCore.Get() failed ---\n{ex}\n");
            throw;
        }

        // 诊断: 把环境状态写到我们自己的 debug 文件, 万一 libvlc_new 失败也能看清
        WriteLibVLCDebug(
            $"AppContext.BaseDirectory = {AppContext.BaseDirectory}\n" +
            $"LIBVLC_PATH = {Environment.GetEnvironmentVariable("LIBVLC_PATH")}\n" +
            $"VLC_PLUGIN_PATH = {Environment.GetEnvironmentVariable("VLC_PLUGIN_PATH")}\n" +
            $"libvlc dir = {VlcCore.LibVlcDirectory} exists = {Directory.Exists(VlcCore.LibVlcDirectory)}\n" +
            $"plugins dir = {VlcCore.PluginsDirectory} exists = {Directory.Exists(VlcCore.PluginsDirectory)}\n"
        );

        _mp = new MediaPlayer(_libVLC);
        // 登记窗口级 MediaPlayer: 进程退出时 VlcCore 据此判断能否安全释放共享原生库
        _playerRegistered = true;
        VlcCore.RegisterPlayer();
        VideoView.MediaPlayer = _mp;

        _mp.TimeChanged += OnTimeChanged;
        _mp.LengthChanged += OnLengthChanged;
        _mp.EndReached += OnEndReached;
        // Playing 事件触发时隐藏加载遮罩; 若刚切完清晰度则恢复进度
        // 注意: LibVLC 事件都在其内部线程触发, 必须用 BeginInvoke 异步派发到 UI 线程。
        // 若用同步 Invoke, UI 线程可能正阻塞在 _mp 属性调用等 LibVLC 内部锁, 双方互相等待 => 死锁
        // 另外每个派发体都要先判 _closing: 窗口已开始关闭时回调可能仍在队列里, 必须丢弃。
        _mp.Playing += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing) return;
            // aout(音频输出)就绪后把应用侧记住的音量补写回去:
            // 无 aout 时 libvlc_audio_set_volume 是静默丢弃的(反编译 3.8.0 确认 setter 不看返回值),
            // 长视频远端 DASH 在起播/缓冲/换清晰度期间常出现"用户滚轮调了音量但 aout 还没建好"的窗口,
            // 不补写的话 aout 就绪后音量会回到原生侧的旧值 —— 表现就是"滚轮调音量没反应"。
            // 放在函数最前: 后面 _resumeAfterSwitchMs 的 MarkSeekIssued 可能立刻进入 seek 静音,
            // 顺序反了会把静音状态顶掉(声音先于画面出来)。
            try { _mp.Volume = _volumeCache; } catch { }
            LogAudioStateOnce();
            HideLoading();
            // 把 EOF 时切黑的手势层还原成近透明(见 OnEndReached 里切黑的原因):
            // 新 input 的 vout 已经起来, 后面要能看见画面
            VideoAreaRoot.Background = VideoAreaBackBrush;
            // 同样要撤掉可能还压着的"全屏黑幕" —— 否则它会盖住刚起来的画面
            _fullscreenCoverArmed = false;
            _fullscreenCoverTimer?.Stop();
            UpdatePlayButton();
            StartControlsHideCountdown();   // 播放中才开始倒计时自动隐藏
            if (_resumeAfterSwitchMs > 0)
            {
                var resume = _resumeAfterSwitchMs;
                _resumeAfterSwitchMs = 0;
                try { _mp.Time = resume; } catch { }
                // 走一次 MarkSeekIssued: 钉住进度条/时间显示, 并启动 seek 看门狗,
                // 否则恢复进度那一下也可能被回写顶回 0(表现是"切完清晰度从头播")
                MarkSeekIssued(resume);
            }
        }));
        // 暂停/停止时同步按钮图标(否则起播后图标停留在初始状态, 看起来像"反了")
        _mp.Paused += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing) return;
            UpdatePlayButton();
            ShowControls();   // 暂停时控制栏常显(没有"遮挡内容"的问题, 且用户多半要操作)
        }));
        _mp.EncounteredError += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing) return;
            HideLoading();
            FailOverlay.Visibility = Visibility.Visible;
            var msg = "视频播放出错, 请检查网络或尝试其他清晰度";
            FailText.Text = msg;
            // 诊断: 把出错时正在播的 URL 也写进 debug 文件
            WriteLibVLCDebug(
                $"\n[EncounteredError] bvid={_currentBvid} cid={_currentCid} lastUrl={_lastPlayUrl}\n");
        }));

        _currentBvid = bvid;
        // ★ 这条构造函数路径(直接带 bvid new 出窗口)**不走 ResetForNewMedia**
        //   —— 而 _currentQn 的字段初值是"内置默认档"而不是用户设置。
        //   漏了这一句的结果是: 带 bvid 直接打开的第一个视频永远按内置默认档起播,
        //   用户在设置页选的「默认画质」要到第二条视频才生效(极难定位的那种"设置时灵时不灵")。
        ApplyPreferredQuality();
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _progressTimer.Tick += (_, _) =>
        {
            UpdateProgressUi();
            // 跳过判定挂在同一个心跳上, 不另开定时器: 100ms/500ms 两种间隔它都会跟着走。
            // 必须在 UpdateProgressUi 之后 —— 后者在"跳转等待期"会提前 return, 而这里的
            // 判据(_pendingSeekMs)本来就要求"上一次跳转已经确认", 顺序正好对得上。
            MaybeSkipSponsorSegment();
        };
        _progressTimer.Start();

        // 默认音量
        _volumeCache = 80;
        _mp.Volume = 80;

        // 换片重置覆盖度自检: 新增了状态字段却忘了归类, 会在 errors.txt 里被点出来(仅 DEBUG)
        VerifyResetCoverage();
    }

    /// <summary>
    /// 是否正在关闭(窗口已隐藏, 但原生资源拆解尚未完成)。
    /// PlayerService 必须据此判断"不能复用" —— 否则复用到的窗口会在拆解结束时被 Close(),
    /// 表现为"点了视频又立刻自动退出"。
    /// </summary>
    public bool IsClosing => _closing;

    /// <summary>切换视频(用于 PlayerService 复用窗口场景): 停止当前播放, 加载新 bvid</summary>
    public void PlayVideo(string bvid)
    {
        if (_closing || string.IsNullOrEmpty(bvid)) return;
        if (bvid == _currentBvid && _mp.IsPlaying) return;
        try { _mp.Stop(); } catch { }
        _currentBvid = bvid;
        _isLocalPlayback = false;
        _isLive = false;
        ResetForNewMedia();
        _ = LoadVideoAsync();
    }

    /// <summary>
    /// 打开一个直播间。
    /// 与点播的区别: 直播没有总时长/进度、没有弹幕回放、也没有三连与历史进度上报,
    /// 所以互动按钮全部禁用(复用 _isLocalPlayback 那套判断), 进度条置灰。
    /// </summary>
    public void PlayLive(long roomId, string? roomTitle = null)
    {
        if (_closing || roomId <= 0) return;
        if (_isLive && roomId == _liveRoomId && _mp.IsPlaying) return;
        try { _mp.Stop(); } catch { }
        _currentBvid = "";
        _isLocalPlayback = true;   // 没有在线上下文: 不打接口、不互动、不上报
        _isLive = true;
        _liveRoomId = roomId;
        ResetForNewMedia();
        if (!string.IsNullOrEmpty(roomTitle)) VideoTitleText.Text = roomTitle;
        _ = LoadLiveAsync(roomId);
    }

    /// <summary>连接直播间: 取直播流地址 → 起播</summary>
    private async Task LoadLiveAsync(long roomId)
    {
        ShowLoading("正在连接直播间…");
        FailOverlay.Visibility = Visibility.Collapsed;

        try
        {
            if (!string.IsNullOrEmpty(VideoTitleText.Text))
            {
                Title = VideoTitleText.Text + " - BiliDesk";
                TitleText.Text = VideoTitleText.Text;
            }
            ViewCountText.Text = "--";
            DanmakuCountText.Text = "--";
            PubDateText.Text = "直播中";
            UpNameText.Text = "直播间 " + roomId;
            UpDescText.Text = "B 站直播";
            DescText.Text = "这是一个直播间。直播没有进度条与历史记录, 互动按钮在直播中不可用。";
            UpAvatarBox.Background = (Brush)FindResource("AccentSoftFillBrush");
            UpAvatarFallback.Visibility = Visibility.Visible;

            // 直播没有总时长: 进度条禁用 + 时间位写"直播中"。
            // 注意 UpdateProgressUi 里有 `Length > 0` 的判断, 直播的 Length 一直是 0,
            // 所以它本来就不会去覆盖这里显示的文案。
            ProgressSlider.IsEnabled = false;
            TimeText.Text = "直播中";
            _qualities.Clear();
            FillQualityMenu(0);
            // ★ 直播没有"可选清晰度"这一说(清晰度由服务端/主播推流决定), 所以按钮上不显示
            //   用户设置的「默认画质」—— 那会在直播里写成一个用户根本控制不了、甚至拿不到的档位
            //   (2026-10-04: 新增「默认画质」后 FillQualityMenu(0) 会回落到偏好标签, 这里盖掉)。
            if (QualityText != null) QualityText.Text = "直播";
            if (CommentHint != null) CommentHint.Text = "直播没有评论区";
            UpdateButtonState();

            var (ok, err, url) = await Svc.Api.GetLivePlayUrlAsync(roomId);
            if (_closing) return;
            if (!ok || string.IsNullOrEmpty(url))
            {
                ShowFail(err ?? "无法获取直播流地址");
                return;
            }
            _lastPlayUrl = url;
            _lastAudioUrl = null;   // 直播是单流, 没有独立的音轨

            // 直播流地址同样要带 Referer / UA —— 直播 CDN 比点播校验得更严:
            // 不带 Referer 时常见表现就是"能拿到地址但播不出来"(403/直接 EOF)。
            // 之前这里只加了 :no-video-title-show, 于是直播一直播不动。
            var liveOptions = new List<string>
            {
                ":no-video-title-show",
                ":http-referrer=https://live.bilibili.com/" + roomId,
                ":http-user-agent=" + PlayerUserAgent,
                // 直播是持续流: 加大缓存, 别让 LibVLC 因为"看起来卡住"就早早判定结束
                ":network-caching=2000",
                ":live-caching=2000"
            };

            var media = new Media(_libVLC, new Uri(url), liveOptions.ToArray());
            if (!PlayMedia(media))
                ShowFail("直播流播放失败, 可能已经下播");
        }
        catch (Exception ex)
        {
            ShowFail("打开直播间失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 播放本地文件(离线缓存页用)。
    /// 与在线播放的区别: 不查视频信息/播放地址、不加载弹幕、不上报历史、不查点赞收藏状态 ——
    /// 本地文件没有这些上下文。所以这里直接构造一个 FromPath 的 Media 丢给播放器。
    /// </summary>
    public void PlayLocalFile(string path)
    {
        if (_closing || string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try { _mp.Stop(); } catch { }
        _currentBvid = "";
        _isLocalPlayback = true;
        _isLive = false;
        ResetForNewMedia();
        LoadLocalFile(path);
    }

    /// <summary>
    /// 把"本片源要请求的清晰度"快照成设置里的「默认画质」。
    ///
    /// ★ 为什么单独一个方法: 有**三**条起播路径需要它, 各写一遍必然漏一条 ——
    ///   构造函数(带 bvid 直接开)、ResetForNewMedia(复用窗口换片)、以及将来新增的入口。
    ///   实际漏过一次的原形就摆在眼前: 只放 ResetForNewMedia 时, "带 bvid 开新窗口"这条
    ///   路径拿到的是字段初值而不是用户设置。
    ///
    /// ★ 为什么不沿用上一个视频手动切到的档位: 那个选项叫**默认**画质 —— 手动切清晰度只对
    ///   当前这一条有效, 下一条仍然从用户选定的默认档起播(B 站客户端同款语义)。
    ///
    /// ★ 为什么走 <see cref="QualityPreference.RequestQn"/> 而不是直接赋值:
    ///   设置页把「自动(最高可用)」存成哨兵值 0, 而发给接口的必须是"最高可用档"(127)。
    ///   同时 RequestQn 内部会 Normalize —— 配置被手改坏 / 存着已下线的档位时,
    ///   不会把一个谁也不认识的 qn 发进取流请求。
    /// </summary>
    private void ApplyPreferredQuality()
    {
        _currentQn = QualityPreference.RequestQn(Svc.Settings.PreferredQualityQn);
        // ★ 顺手把控件栏那个清晰度按钮的文案摆成"**本次要请求的**档位"(2026-10-04)。
        //
        // 为什么必须在这里写一次: XAML 里 `QualityText` 的初值是写死的 "1080P"。
        // 用户把默认画质设成 720P / 4K 之后, 从"点开视频"到"取流成功调用 FillQualityMenu"
        // 之间——以及**取流直接失败**那条路(ShowFail 后 return, 根本不会调 FillQualityMenu)
        // ——按钮上都会挂着一个 "1080P", 而那既不是用户选的档、也不是正在播的档。
        // 之前写死 qn=80 时它与请求一致所以看不出来, 有了这个设置项就变成实打实的错报。
        // 本地文件/直播那两条路随后会各自用 "本地"/"直播" 覆盖掉它, 不受这里影响。
        if (QualityText != null)
            QualityText.Text = QualityPreference.ShortLabelOf(Svc.Settings.PreferredQualityQn);
    }

    /// <summary>
    /// 换片源前把"上一条视频的痕迹"清干净。
    /// 尤其是 _commentsLoaded / CommentList.ItemsSource —— 原来没清, 于是同一个播放器窗口
    /// 换视频之后评论 tab 里一直挂着上一条视频的评论(而且不会再重新加载)。
    /// </summary>
    private void ResetForNewMedia()
    {
        _indexInLib = 0;
        // 换片: 音频链路诊断重新记一次(每个片源一条, 见 LogAudioStateOnce)
        _audioStateLogged = false;
        // ★ 每个片源开始时都把"要请求的清晰度"快照成设置里的「默认画质」(2026-10-04)。
        //   放在这里而不是只放构造函数里, 是为了让"设置页改完 → 点开下一个视频"立刻生效,
        //   不必重启。
        ApplyPreferredQuality();
        _danmaku.Clear();
        _danmakuRaw.Clear();
        // SponsorBlock: 上一片的片段数据与"已处理"记录全部作废。
        // 掐掉那个查询同样重要 —— 否则上一个视频的响应回来了会写进新视频的状态。
        CancelSponsorFetch();
        _sponsorRaw = Array.Empty<SponsorSegment>();
        _sponsorActive = Array.Empty<SponsorSegment>();
        _sponsorHandled.Clear();
        _lastSkipSeg = null;
        HideSkipNotice();
        // 进度条上的旧色块也要立刻清掉: 本地文件/直播这两条路根本不会去取片段,
        // 不主动刷新的话上一条视频的色块会一直挂在进度条上。
        RenderSponsorMarks();
        _totalMs = 0;
        _currentCid = 0;
        _aid = 0;
        // 换片源: 上一片的合流任务立刻作废(否则会白下几十兆, 也可能把结果写进新片的键)
        CancelRemuxPrefetch();
        _ownerMid = 0;
        _commentsLoaded = false;
        // 换片时把评论详情也收掉(tab 文案与返回链接一并还原),
        // 否则新片子一进来右侧就挂着上一条视频的评论详情页
        if (CommentDetailPanel != null) CloseCommentDetail();
        if (CommentList != null) CommentList.ItemsSource = null;
        if (CommentHint != null) CommentHint.Text = "加载评论中…";
        // 上一条视频没写完的评论草稿不能带到下一条去(用户点开新视频发现输入框里
        // 还留着给上一个视频写了一半的话, 会以为是自己点错了)
        CommentDraft?.Clear();
        _postingComment = false;
        // 进度上报的节流状态也要重置, 让新视频的第一条云端历史能立刻落地
        Svc.HistorySync.Reset();
        // 清掉屏幕上残留的弹幕(防泄漏)并重置泳道占用
        DanmakuCanvas.Children.Clear();
        ResetLanes();
        TimeText.Text = "00:00 / 00:00";
        ProgressSlider.Value = 0;
        // 新片子当然还没播完: 清掉结尾状态与"重播"遮罩, 否则切下一条视频时会挂着上一段的遮罩
        _ended = false;
        _seekGraceUntil = 0;
        // 清掉跳转看门狗与"提前结束自救"的上一片痕迹
        _pendingSeekMs = -1;
        _seekObservedMs = -1;
        RestoreVolumeAfterSeek();   // 上一片若停在 seek 静音中, 别把静音带进新片
        // 心跳间隔也要还原: 上一片可能停在了"跳转等待期"的 100ms 上没来得及还原
        if (_progressTimer != null) _progressTimer.Interval = ProgressTickIdle;
        _lastKnownMs = 0;
        _eofResumeCount = 0;
        _lastEofResumeMs = 0;
        // 卡顿检测状态同样要清: 换片后上一片"时间停在哪"的参照已经没意义, 留着会立刻误判成卡顿
        _stallLastMs = -1;
        _stallTicks = 0;
        // 进度显示的两个基线一并清: 不清的话"尾部只进不退"钳制会把新片钉在上一片的旧进度上
        _anchorMs = -1;
        _displayedMs = -1;
        if (EndOverlay != null) EndOverlay.Visibility = Visibility.Collapsed;
        // 进度条默认可用; 直播那条路会再把它置灰
        if (ProgressSlider != null) ProgressSlider.IsEnabled = true;
        // 弹幕按钮的可用性取决于 _isLive / _isLocalPlayback, 调用方都已在这之前设好这两个标志
        UpdateDanmakuButton();
        // 「稍后再看」的点亮态属于"上一条视频", 一起清掉。放这里而不是 UpdateUiFromDetail:
        // 直播 / 本地离线缓存那两条路不走详情流程, 不清的话会挂着上一条视频的强调色。
        SetInWatchLater(false);
        // 合集: 换片就要把"上一个合集的列表"整个丢掉, 否则普通视频那一瞬间会挂着旧合集的条目
        // (点开面板那一帧还看得见)。★ 面板的**展开态**(_seasonOpen)不在这里清 —— 它登记在
        // PersistentFields 里: 用户连着看几集是常态, 每换一集都把面板收起来很烦。
        _currentSeasonEpisode = null;
        _season = null;
        _hasSeason = false;
        // ★ 这里**不**清 _switchingSeasonEpisode: 它的复位点是 SwitchToSeasonEpisodeAsync 的
        //   finally(与 _switchingQuality 同款)。本方法正是被那次换片调用的 —— 在这里清掉它,
        //   整个 await LoadVideoAsync() 期间闸门就是开的, 连点两集照样能叠起来跑。
        ClearSeasonUi(collapsePanel: false);
        // 换片时**不关**面板(collapsePanel: false): 用户开着面板点下一集是常态, 关掉会在
        // 整个加载过程里闪一下。列表内容已清空, 这里就地显示"加载中", 等 ApplySeason 灌数据。
        // 真到了没有合集的新片, ApplySeason 会把面板整个收起来。
        if (_seasonOpen)
        {
            if (SeasonHint != null) SeasonHint.Text = "正在获取合集…";
            if (SeasonLoadingRing != null) SeasonLoadingRing.Visibility = Visibility.Visible;
        }
        UpdateSeasonButtonState();
    }

    /// <summary>
    /// 把合集列表的内容清干净(入口按钮、标题、副标题、提示、加载圈、选中项、**条目本身**)。
    ///
    /// 单独抽出来是因为它有多个调用点 —— 换片时、详情加载完发现没有合集时。
    /// 两处各写一遍必然漏(尤其"底部加载圈没停"这种, 表现是加载完了圈还在转)。
    /// </summary>
    /// <param name="collapsePanel">
    /// 是否**顺便把面板收起来**。换片时必须传 false: 用户开着面板点下一集是常态,
    /// 换片期间把面板收掉会在整个加载过程里闪一下, 体验上像是"点一下面板没了"。
    /// 只有在"确定这个视频没有合集"时才收面板。
    /// </param>
    private void ClearSeasonUi(bool collapsePanel)
    {
        if (collapsePanel && SeasonPanel != null) SeasonPanel.Visibility = Visibility.Collapsed;
        if (SeasonTitleText != null) SeasonTitleText.Text = "";
        if (SeasonMetaText != null) SeasonMetaText.Text = "";
        if (SeasonHint != null) SeasonHint.Text = "";
        if (SeasonLoadingRing != null) SeasonLoadingRing.Visibility = Visibility.Collapsed;
        // ★★ 必须把 ItemsSource 也清掉, 不能只清 SelectedIndex(2026-10-02 探针实测出来的):
        //   换片时不关面板(见上面 collapsePanel 的说明), 于是从"点了下一集"到
        //   LoadVideoAsync 真正灌回新数据之间有几秒的窗口期 —— 那段时间面板是**开着**的。
        //   只清选中项的话, 列表里挂着的是**上一个合集**的条目, 而底部还写着"正在获取合集…",
        //   自相矛盾。更糟的是此时点一条旧条目会走到 OnSeasonEpisodeSelected:
        //   它的判据是 ep.Bvid != _currentBvid, 而 _currentBvid 已经是新视频了 ⇒ 恒真
        //   ⇒ 会拿旧合集的条目去换片, 播出一个跟用户点的东西无关的视频。
        //   (ApplySeason 保证重新灌数据, 所以这里清成 null 是安全的。)
        if (SeasonList != null)
        {
            SeasonList.ItemsSource = null;
            SeasonList.SelectedIndex = -1;
        }
    }

    // ------------------------------------------------------------ 换片重置的自检

    /// <summary>
    /// 「每片状态」字段清单 = <see cref="ResetForNewMedia"/> 必须清掉的那些。
    ///
    /// 为什么需要这份清单 + 自检: 换片重置那 60 多行是**手工维护**的, 而播放器是"换片就清"
    /// 的结构 —— 漏清一个字段不报错、不告警, 只会让新片悄悄继承上一片的残留(三连状态、
    /// 清晰度、seek 看门狗的锚点……), 表现为"换个视频还带着上一个的状态", 极难定位。
    ///
    /// 与 <see cref="StopAllTimers"/> 的区别: 定时器可以**自动**停(全停即可), 但重置**不能自动** ——
    /// 每个字段该清成什么值不一样, 有的还要连带清集合/退订/刷 UI, 反射猜不出来。
    /// 所以这里退一步: 清单仍然手写, 但用自检保证"新增字段时不会忘了登记"。
    /// </summary>
    private static readonly string[] PerMediaStateFields =
    {
        // 三连 / 关注 / 稍后再看等"上一条视频"的态
        nameof(_isLiked), nameof(_isFollowed), nameof(_isFavorited), nameof(_favBusy),
        nameof(_isInWatchLater), nameof(_watchLaterBusy), nameof(_likeCount),
        nameof(_coinCount), nameof(_favCount),
        // 播放源标识
        nameof(_isLocalPlayback), nameof(_isLive), nameof(_liveRoomId), nameof(_currentCid),
        nameof(_aid), nameof(_ownerMid), nameof(_ownerName), nameof(_ownerFace),
        nameof(_videoTitle), nameof(_currentQn), nameof(_currentBvid),
        nameof(_lastPlayUrl), nameof(_lastAudioUrl),   // 出错诊断日志要打"当时正在播的地址"
        // 进度 / 时长 / EOF 自救
        nameof(_totalMs), nameof(_lastKnownMs), nameof(_eofResumeCount), nameof(_lastEofResumeMs),
        nameof(_resumeAfterSwitchMs), nameof(_ended), nameof(_anchorMs), nameof(_anchorWall),
        nameof(_displayedMs),
        // 跳转看门狗
        nameof(_pendingSeekMs), nameof(_seekDeadline), nameof(_seekRetries),
        nameof(_seekObservedMs), nameof(_seekElsewhereSeenMs), nameof(_seekFromMs),
        nameof(_seekGraceUntil), nameof(_seekMuteSavedVolume), nameof(_isSeeking),
        // 卡顿检测
        nameof(_stallLastMs), nameof(_stallTicks),
        // 弹幕
        nameof(_indexInLib), nameof(_audioStateLogged), nameof(_lastLaneCalcWidth),
        nameof(_lastLaneCalcHeight),
        // 异步代次(旧请求回来时必须已作废)
        nameof(_remuxGen), nameof(_loadingGen), nameof(_kaomojiIndex),
        // 界面交互态
        nameof(_switchingQuality), nameof(_fillingQualityMenu), nameof(_updatingSlider),
        nameof(_commentsLoaded), nameof(_postingComment), nameof(_longPressFired),
        nameof(_suppressSingleClick), nameof(_controlsHidden), nameof(_isFullscreen),
        // 合集: 「这个视频有没有合集」属于"这一片"的属性, 换片必须重算。
        // 不清的话换了普通视频入口还挂着, 点开是个空面板。
        // (_currentSeasonEpisode / _season 是引用类型, 自检只审值类型与 string, 不在清单里;
        //  但它们在 ResetForNewMedia 里同样被清了 —— 清单只负责"别漏", 不负责穷举。)
        nameof(_hasSeason), nameof(_switchingSeasonEpisode),
    };

    /// <summary>
    /// 明确**跨片保持**的值类型字段 —— 不该进每片清单, 也不该被自检点名。
    /// 分类错了自检会报出来, 改这里即可, 别去动 <see cref="PerMediaStateFields"/> 消警告。
    /// </summary>
    private static readonly string[] PersistentFields =
    {
        nameof(_volumeCache),        // 音量是用户偏好, 换片不该重置
        nameof(_wheelAccum),         // 滚轮音量累计, 瞬时量
        nameof(_danmakuOn),          // 弹幕总开关是设置项
        nameof(_danmakuColorful),    // 彩色弹幕是设置项
        nameof(_danmakuAreaRatio),   // 弹幕显示区域是设置项
        nameof(_laneHeight),         // 泳道高随字号走, 跟着设置走
        nameof(_teardownDone),       // 拆解只做一次
        nameof(_playerRegistered),   // 注册计数, 生命周期是窗口而非片源
        nameof(_seasonOpen),         // 合集面板的展开态: 用户开着面板连看几集是常态, 换片收起它很烦
        nameof(_isFillWindow),       // 「铺满窗口」是窗口级的显示模式(同 _isFullscreen), 换片不该把它弹回去
    };

    /// <summary>
    /// 换片重置覆盖度自检: 找出既没登记为"每片状态"、也没登记为"跨片保持"的字段。
    ///
    /// 新增 <c>private bool/int/long/double/string</c> 字段却忘了在上面两张表里登记时,
    /// 它会出现在 errors.txt 里 —— 这是"漏清字段"唯一能被自动发现的入口。
    ///
    /// 只在 DEBUG 下编译进调用点(<see cref="ConditionalAttribute"/>), Release 零开销。
    /// 定时器字段不在审计范围内(它们由 <see cref="StopAllTimers"/> 自动处理)。
    /// </summary>
    [Conditional("DEBUG")]
    private static void VerifyResetCoverage()
    {
        var known = new HashSet<string>(
            PerMediaStateFields.Concat(PersistentFields), StringComparer.Ordinal);

        var unlisted = StateFieldNames()
            .Where(n => !known.Contains(n))
            .ToList();

        if (unlisted.Count == 0) return;

        App.ReportError(new InvalidOperationException(
            "[PlayerWindow] 以下字段未登记到 PerMediaStateFields(每片需清)或 PersistentFields(跨片保持): "
            + string.Join(", ", unlisted)
            + " —— 换片时它会保留上一条视频的残留。请归类后补进对应清单。"));
    }

    /// <summary>取本类里所有"简单状态"字段名(值类型 + string, 不含 UI 元素/集合/定时器/服务引用)</summary>
    private static IEnumerable<string> StateFieldNames()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        return typeof(PlayerWindow).GetFields(flags)
            .Where(f => !typeof(DispatcherTimer).IsAssignableFrom(f.FieldType))
            .Where(f => f.FieldType.IsPrimitive || f.FieldType == typeof(string)
                        || f.FieldType.IsEnum
                        || f.FieldType == typeof(decimal) || f.FieldType == typeof(DateTime))
            .Select(f => f.Name);
    }

    /// <summary>
    /// 打开本地缓存文件并起播。
    ///
    /// 全程同步 —— 以前它是 <c>async Task</c> 却没有任何 await, 靠一句
    /// <c>await Task.CompletedTask;</c> 收尾。那种写法有两个坏处: ① 让"这里其实不异步"这件事
    /// 看不出来; ② 异常会进 Task 里, 而调用方是 <c>_ = LoadLocalAsync(...)</c>(丢弃返回值),
    /// 等于无人观察。方法内已有 try/catch 兜底, 所以这里直接做成同步方法最诚实。
    /// </summary>
    private void LoadLocalFile(string path)
    {
        ShowLoading("正在打开本地文件…");
        FailOverlay.Visibility = Visibility.Collapsed;
        try
        {
            var name = Path.GetFileNameWithoutExtension(path);
            TitleText.Text = name;
            Title = name + " - BiliDesk";
            VideoTitleText.Text = name;
            ViewCountText.Text = "--";
            DanmakuCountText.Text = "--";
            PubDateText.Text = "--";
            UpNameText.Text = "本地文件";
            UpDescText.Text = "离线缓存";
            DescText.Text = "这是离线缓存里的本地文件, 没有在线视频的简介与评论。";
            UpAvatarBox.Background = (Brush)FindResource("AccentSoftFillBrush");
            UpAvatarFallback.Visibility = Visibility.Visible;
            UpdateButtonState();

            // 本地文件没有清晰度可选, 清空菜单避免显示上一条视频的清晰度
            _qualities.Clear();
            FillQualityMenu(0);
            if (QualityText != null) QualityText.Text = "本地";

            var media = new Media(_libVLC, path, FromType.FromPath);
            if (_closing) { media.Dispose(); return; }
            _lastPlayUrl = path;
            PlayMedia(media);
        }
        catch (Exception ex)
        {
            if (!_closing) ShowFail("打开本地文件失败: " + ex.Message);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (TabIntro != null && IntroPanel != null) TabIntro.IsChecked = true;
        UpdateButtonState();
        // 窗口是复用的: 按当前状态(铺满/常规)同步一次信息栏与窗口最小尺寸
        ApplyInfoPanelState();
        // 「铺满窗口」按钮的字形初值: XAML 里写的是"进入前"那个字形(E9A6), 这里按实际状态
        // 再同步一次 —— 窗口是复用的, 复用到一个"上次已铺满"的窗口时不能显示成未铺满。
        UpdateFillWindowButton();
        // 覆盖层窗口(VideoView 的 ForegroundWindow)这时才建出来 —— 把键盘处理也挂上去,
        // 否则点过一次视频/控制栏之后快捷键就全哑了(见 HookOverlayKeyboard 的说明)
        HookOverlayKeyboard();
        await LoadVideoAsync();
    }

    // ------------------------------------------------------------ 加载流程

    private async Task LoadVideoAsync()
    {
        if (string.IsNullOrEmpty(_currentBvid)) return;
        ShowLoading("正在获取视频信息…");
        FailOverlay.Visibility = Visibility.Collapsed;
        try
        {
            var (detailOk, detailErr, detail) = await Svc.Api.GetVideoAsync(_currentBvid);
            // 加载期间用户可能已经关掉窗口, 此时不能再碰任何 UI/原生对象
            if (_closing) return;
            if (detail == null)
            {
                // 现在 err 会带真实原因(接口码/异常), 不用再写死一句猜测的话
                ShowFail(detailErr ?? "获取视频信息失败, 可能视频已删除或不可用");
                return;
            }
            _currentCid = detail.Cid;
            _aid = detail.Aid;
            _ownerMid = detail.OwnerMid;
            _ownerName = detail.Owner;
            UpdateUiFromDetail(detail);
            // 合集数据在详情里就已经解析好了(见 ApiClient.GetVideoAsync), 这里零额外请求。
            // 放在 UpdateUiFromDetail 之后: 它要用刚设好的 _currentCid/_aid 来定位"当前是第几集"。
            ApplySeason(detail.Season);

            if (_currentCid <= 0)
            {
                ShowFail("无法获取视频 cid, 播放中止");
                return;
            }

            // ★ 片段查询**立刻发起, 与下面"取播放地址 / 合流下载"并行** ——
            // 那两步通常要几百毫秒到几十秒, 等它们跑完这边基本已经回来了,
            // 所以"跳过赞助片段"不会给起播增加可感知的等待(见 SponsorWaitMs)。
            var sponsorTask = FetchSponsorSegmentsAsync();

            SetLoadingText("正在获取播放地址…");
            // 默认请求 1080P(qn=80), B 站会按登录态/大会员返回实际可用的清晰度
            var (playOk, playErr, main, backups, durMs, qualities, actualQn, audioUrl, remux) =
                await Svc.Api.GetPlayUrlAsync(_currentBvid, _currentCid, _currentQn);
            if (_closing) return;
            // 以前这里不判成败, 直接拿 main 往下走 —— 取不到地址时 main 是 null, 一路走到
            // BuildMedia(null) 才炸, 用户看到的是一句语焉不详的"打开失败"。
            // 现在把 API 层给的原因直接透出去(见 ApiClient.GetPlayUrlAsync 的说明)。
            if (!playOk && string.IsNullOrEmpty(main))
            {
                ShowFail(playErr ?? "获取播放地址失败, 请稍后重试");
                return;
            }
            _totalMs = durMs;
            UpdateTotalDurationText();
            _qualities.Clear();
            _qualities.AddRange(qualities);
            FillQualityMenu(actualQn);

            // ---- 短视频: 先合流成本地文件再播(它同时给到 1080P / 快跳转 / 完整结尾) ----
            // 顺序: 已缓存 -> 直接播本地; 没缓存 -> 就地下完再播(带进度); 下不动 -> 回落远端 DASH
            var local = ShortVideoCache.TryGet(_currentBvid, actualQn);
            if (local == null && ShortVideoCache.WorthCaching(remux))
                local = await PrepareShortVideoAsync(remux!, actualQn);
            if (_closing) return;

            string? url;
            string? playAudio;
            if (local != null)
            {
                url = local;
                playAudio = null;      // 本地合流是单文件源, 带 input-slave 等于给同一文件再挂一条音轨
            }
            else
            {
                url = main;
                if (string.IsNullOrEmpty(url) && backups.Count > 0) url = backups[0];
                playAudio = audioUrl;
            }
            if (string.IsNullOrEmpty(url))
            {
                ShowFail("无法获取播放地址(可能为大会员/付费视频), 请登录后重试");
                return;
            }

            SetLoadingText("正在缓冲视频…");

            // 记录要播放的 URL(诊断 + 重播时重建 Media 用)
            _lastPlayUrl = url;
            _lastAudioUrl = playAudio;
            WriteLibVLCDebug(
                $"\n[LoadVideo] bvid={_currentBvid} cid={_currentCid} qn={actualQn} url={url} audio={playAudio}\n");

            // 片段数据: 到这里"取播放地址 / 合流下载"已经花掉了大部分时间, 这一步几乎必然已就绪。
            // 万一还没回来, 也最多再等 SponsorWaitMs —— 等不到就先起播, 结果回来后再补上。
            await BeforePlayApplySponsorAsync(sponsorTask);
            if (_closing) return;

            // B站 CDN 对 Referer 和 User-Agent 有校验, 必须通过 Media 选项告诉 LibVLC;
            // DASH 流音视频分离, 用 input-slave 把音轨挂到视频流上一起播放
            PlayMedia(BuildMedia(url, playAudio));
            // 加载弹幕 + 查询点赞/关注/收藏状态(用于按钮状态切换)
            _ = LoadDanmakuAsync(_currentCid);
            _ = RefreshLikeStateAsync();
            _ = RefreshFollowStateAsync();
            _ = RefreshFavStateAsync();
            _ = RefreshWatchLaterStateAsync();
        }
        catch (Exception ex)
        {
            if (!_closing) ShowFail("加载失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 播一个 Media, 并**立刻释放我们自己那一份引用**。
    ///
    /// ★ 为什么必须释放: `MediaPlayer.Play(media)` 内部会 `set_media` —— 播放器**自己**已经
    ///   retain 了一份, 所以 Play 之后我们这份就可以放掉, 播放不受任何影响
    ///   (LibVLCSharp 官方示例就是 `using (var media = ...) mp.Play(media)`)。
    ///
    /// 反过来, 如果我们不释放, 这个 `libvlc_media_t` 就**永远**留着 —— 它握着解复用解析出来的
    /// 东西(本地 MP4 的样本表、网络流的 track / metadata), 而且全是**原生内存**,
    /// .NET 的 GC 一点都管不到。表现就是: 每看一个视频涨一块、关掉也不回落
    /// (2026-09-27 用户报的内存问题里就有这一份)。
    ///
    /// 之前 6 个播放点全都是 `_mp.Play(new Media(...))` 直接把 Media 丢掉, 一个都没释放。
    /// </summary>
    private bool PlayMedia(Media media)
    {
        try
        {
            return _mp.Play(media);
        }
        finally
        {
            try { media.Dispose(); } catch { /* 播放器持有它自己那份引用, 这里失败不影响播放 */ }
        }
    }

    /// <summary>
    /// 喂给 LibVLC 的 UA。B 站 CDN 会校验, 用浏览器 UA 最稳;
    /// 点播/直播/重播都必须带上同一份, 所以提出来共用。
    /// </summary>
    private const string PlayerUserAgent = HttpDefaults.UserAgent;

    /// <summary>
    /// 构造带 CDN 头的 Media。url 既可能是在线地址(DASH 视频流 / durl 单流), 也可能是
    /// 本地文件(离线缓存、或短视频的本地合流缓存)。
    /// DASH 流音视频分离, 用 input-slave 挂音轨; 本地合流是**单文件**, audioUrl 传 null。
    ///
    /// 本地路径必须走 FromPath: `new Uri("C:\...\x.mp4")` 会把盘符当成协议名, 抛 UriFormatException。
    /// </summary>
    private Media BuildMedia(string url, string? audioUrl)
    {
        // B站 CDN 校验 Referer 和 User-Agent
        var options = new List<string>
        {
            ":http-referrer=https://www.bilibili.com/",
            ":http-user-agent=" + PlayerUserAgent
        };
        if (!string.IsNullOrEmpty(audioUrl))
            options.Add(":input-slave=" + audioUrl);

        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var local = new Media(_libVLC, url, FromType.FromPath);
            foreach (var o in options) local.AddOption(o);
            return local;
        }
        return new Media(_libVLC, new Uri(url), options.ToArray());
    }

    // ------------------------------------------------------------ 短视频本地合流(边播边下)

    /// <summary>后台合流任务。换片/切清晰度/关窗都要取消它 —— 否则会白下几十兆, 还可能把旧片写进新键</summary>
    private CancellationTokenSource? _remuxCts;

    /// <summary>
    /// 合流任务的"代次"。换片/切清晰度都要作废上一次的在飞任务 ——
    /// 直接比字段(比如"cid 还一样吗")容易漏(切清晰度时 cid 是不变的), 用单调递增的代次最省心。
    /// </summary>
    private int _remuxGen;

    /// <summary>
    /// 短视频: 下载两路并合流成本地文件, 期间在加载遮罩上显示进度。
    /// 返回合流好的本地路径; 失败 / 超时 / 被取消返回 null(调用方回落到远端 DASH)。
    ///
    /// 为什么是"下完再播"(用户 2026-09-25 选定):
    ///   前一版是"边播边下、就绪后换源"。换源那一下真机上暴露了两个问题 —— 跳转后画面花屏、
    ///   音轨不同步(根因见 DashRemuxer 里 mfhd 序列号那条)。下完再播从头到尾都是**同一个输入**,
    ///   没有换源这一步, 问题面小得多。代价是首次打开要等下载(实测 18MB / 2.5s), 之后就吃缓存。
    ///
    /// 进度是"两路已下字节 / 按码率估算的总字节": 估算值来自接口的 bandwidth, 与实际有偏差,
    /// 所以到 100% 之后还可能要等一下合流(那步是纯内存拷贝, 很快)。
    /// </summary>
    private async Task<string?> PrepareShortVideoAsync(DashStreams spec, int qn)
    {
        CancelRemuxPrefetch();
        var cts = new CancellationTokenSource();
        _remuxCts = cts;
        var gen = _remuxGen;      // CancelRemuxPrefetch 里已经自增过, 这就是"本次任务"的代次
        var bvid = _currentBvid;  // 别在后台线程上读字段: 先取到本地变量

        SetLoadingText("正在准备高清播放…");
        try
        {
            // Progress<T> 在创建它的线程(UI 线程)上回调, 所以这里可以直接碰控件
            var progress = new Progress<double>(p =>
            {
                if (_closing || gen != _remuxGen) return;
                SetLoadingText(p > 0
                    ? $"正在下载高清视频 {p * 100:0}%"
                    : "正在准备高清播放…");
            });

            var path = await ShortVideoCache.PrepareAsync(bvid, qn, spec, cts.Token, progress);
            // 期间用户可能已经换片/切清晰度/关窗: 代次变了就丢掉这次结果
            if (_closing || gen != _remuxGen) return null;
            if (path == null) SetLoadingText("正在缓冲视频…");   // 合流没成 -> 走远端, 文案还原
            return path;
        }
        finally
        {
            if (ReferenceEquals(_remuxCts, cts))
            {
                _remuxCts = null;
                cts.Dispose();
            }
        }
    }

    /// <summary>
    /// 取消正在进行的合流任务(换片 / 切清晰度 / 关窗都调它)。
    /// 用单调递增的代次作废在飞任务: 只比"cid 还是不是同一个"会漏 —— 切清晰度时 cid 不变。
    /// </summary>
    private void CancelRemuxPrefetch()
    {
        _remuxGen++;
        try { _remuxCts?.Cancel(); } catch { /* 已经结束 */ }
        _remuxCts?.Dispose();
        _remuxCts = null;
    }

    private void UpdateUiFromDetail(VideoDetail detail)
    {
        TitleText.Text = detail.Title;
        Title = detail.Title + " - BiliDesk";
        _videoTitle = detail.Title;
        VideoTitleText.Text = detail.Title;
        ViewCountText.Text = VideoItem.FormatCount(detail.ViewCount);
        DanmakuCountText.Text = VideoItem.FormatCount(detail.DanmakuCount);
        PubDateText.Text = detail.Pubdate > 0
            ? DateTimeOffset.FromUnixTimeSeconds(detail.Pubdate).LocalDateTime.ToString("yyyy-MM-dd")
            : "";
        UpNameText.Text = string.IsNullOrEmpty(detail.Owner) ? "UP 主" : detail.Owner;
        UpDescText.Text = "UP 主 mid: " + detail.OwnerMid;
        DescText.Text = string.IsNullOrEmpty(detail.Desc) ? "这个 UP 主很懒, 什么都没写~" : detail.Desc;

        UpdateButtonState();
        // 切换视频时重置各操作按钮状态(随后的异步查询会刷新为真实值)
        _isLiked = false;
        _isFollowed = false;
        // 计数直接来自详情(view 接口的 stat), 比旧的"点赞/投币/收藏"三个字信息量大
        _likeCount = detail.LikeCount;
        _coinCount = detail.CoinCount;
        _favCount = detail.FavoriteCount;
        UpdateActionCounts();
        SetFavorited(false);
        // 「稍后再看」的点亮态不在这里复位 —— 它统一在 ResetForNewMedia 里清,
        // 因为直播/本地文件那两条路根本不会走到这里。
        UpdateFollowButton();
        // UP 主头像优先用 owner.face(之前误用了视频封面)
        _ownerFace = detail.OwnerFace ?? "";
        _ = LoadUpAvatarAsync(_ownerFace);

        // 本机观看历史已移除, 这里不再写本地记录。
        // 观看记录仍然会上报到云端 —— 见下方的 Svc.HistorySync。ReportAsync。
    }

    private async Task LoadUpAvatarAsync(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        // 头像只有 40px 显示, 按小尺寸解码省内存
        var img = await CoverLoader.LoadAsync(url, 80);
        if (img == null) return;
        await Dispatcher.InvokeAsync(() =>
        {
            var brush = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            brush.Freeze();
            UpAvatarBox.Background = brush;
            UpAvatarFallback.Visibility = Visibility.Collapsed;
        });
    }

    private void ShowFail(string msg)
    {
        HideLoading();
        FailOverlay.Visibility = Visibility.Visible;
        FailText.Text = msg;
    }

    // ------------------------------------------------------------ 加载遮罩(含颜文字彩蛋)

    // 颜文字轮换的间隔。700ms 一换: 比一次典型的网络请求慢半拍, 像是"陪伴"而不是"闪烁"。
    private const int KaomojiIntervalMs = 700;
    private static readonly string[] KaomojiList =
    {
        "(´･ω･`)", "(･ω･)つ", "ヾ(≧▽≦*)o", "(๑•̀ㅂ•́)و✧", "(っ•̀ω•́)っ", "┌(・。・)┘♪"
    };
    private DispatcherTimer? _kaomojiTimer;
    private int _kaomojiIndex;

    /// <summary>
    /// 遮罩的淡入/淡出代次。
    /// 用途: 淡出动画跑完时, 如果这期间又被 ShowLoading 重新叫起(seek 卡顿反复触发就会),
    /// 不能再把遮罩藏掉 —— 用代次比对即可判断"我这一层还是不是当前的"。
    /// </summary>
    private int _loadingGen;

    /// <summary>遮罩淡入时长</summary>
    private const int LoadingFadeInMs = 130;

    /// <summary>遮罩淡出时长。比淡入稍长: 收起时慢一点, 画面过渡更自然。</summary>
    private const int LoadingFadeOutMs = 170;

    /// <summary>显示加载遮罩(顺带启动颜文字轮换)。加载遮罩统一从这里走, 不要直接碰 Visibility</summary>
    private void ShowLoading(string message)
    {
        if (_closing || LoadingOverlay == null) return;
        _loadingGen++;
        LoadingText.Text = message;

        if (LoadingOverlay.Visibility != Visibility.Visible)
        {
            // 首次显示: 基准值设 0, 再淡到 1 —— 别"啪"地糊一层黑上来
            LoadingOverlay.Opacity = 0;
            LoadingOverlay.Visibility = Visibility.Visible;
        }

        // 取"当前透明度"(挂了动画时拿到的是动画值)。已经在 1 上就不重播,
        // 否则每次换文案("获取视频信息"→"获取播放地址")都会闪一下。
        var from = LoadingOverlay.Opacity;
        if (from < 1)
        {
            LoadingOverlay.BeginAnimation(OpacityProperty,
                new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(LoadingFadeInMs))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }

        StartKaomoji();
    }

    /// <summary>遮罩已经可见、只是换个文案(比如"获取视频信息"→"获取播放地址")</summary>
    private void SetLoadingText(string message)
    {
        if (_closing) return;
        LoadingText.Text = message;
        StartKaomoji();
    }

    /// <summary>收起加载遮罩(淡出)。所有收起路径都必须走这里</summary>
    private void HideLoading()
    {
        _kaomojiTimer?.Stop();
        _kaomojiTimer = null;

        if (_closing || LoadingOverlay == null) return;
        if (LoadingOverlay.Visibility != Visibility.Visible) return;

        var gen = ++_loadingGen;
        var from = LoadingOverlay.Opacity;
        if (from <= 0.001)
        {
            FinishHideLoading();
            return;
        }

        var fade = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(LoadingFadeOutMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, _) =>
        {
            // 淡出期间又被 ShowLoading 接管(卡顿反复触发时会遇到) -> 不能藏
            if (_closing || gen != _loadingGen) return;
            FinishHideLoading();
        };
        LoadingOverlay.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>真正把遮罩藏起来(同时把基准透明度归零, 下次显示才能从 0 淡入)</summary>
    private void FinishHideLoading()
    {
        LoadingOverlay.BeginAnimation(OpacityProperty, null);
        LoadingOverlay.Opacity = 0;
        LoadingOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>颜文字轮播。定时器常驻到遮罩收起为止, 避免每次 ShowLoading 都新建</summary>
    private void StartKaomoji()
    {
        if (_kaomojiTimer != null) return;
        LoadingKaomoji.Text = KaomojiList[_kaomojiIndex % KaomojiList.Length];
        _kaomojiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(KaomojiIntervalMs) };
        _kaomojiTimer.Tick += (_, _) =>
        {
            if (_closing || LoadingOverlay.Visibility != Visibility.Visible)
            {
                HideLoading();
                return;
            }
            _kaomojiIndex++;
            LoadingKaomoji.Text = KaomojiList[_kaomojiIndex % KaomojiList.Length];
        };
        _kaomojiTimer.Start();
    }

    // ------------------------------------------------------------ 播放控制

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (_mp == null) return;

        // 已经播完了: 按播放键就是"重播"。LibVLC 到 EOF 之后直接 Play() 有时不会回到开头,
        // 显式把时间归零最稳。
        if (_ended)
        {
            RestartFromBeginning();
            return;
        }

        if (_mp.IsPlaying) _mp.Pause();
        else _mp.Play();
        UpdatePlayButton();

        // 暂停是一次"自然的段落结束", 补报一次让云端记录的进度与用户当下的位置一致
        // (否则用户看到一半暂停去网页端, 只能续播到最多 15 秒前的旧位置)
        if (!_mp.IsPlaying) SyncProgressNow();
    }

    /// <summary>
    /// 从头重播(播完之后点播放键 / 点遮罩上的「重播」)。
    ///
    /// 这里**不能**只写 `_mp.Time = 0; _mp.Play();` —— 那正是"点了重播没反应"的根因:
    /// LibVLC 到 EOF 之后, 那条 input 已经结束了, 对它发 seek 和 play 都会被丢掉
    /// (状态已经是 Ended, 不再处理控制指令)。所以必须**重新丢一条 Media 进去**,
    /// 让 LibVLC 起一个全新的 input —— 和"点另一个视频"走的是同一条路, 那条路一直是好的。
    ///
    /// 三个来源分别重建: 直播重新拉流、本地文件用 FromPath、在线视频重建音视频组合
    /// (DASH 的音轨是 input-slave 挂上去的, 不带上它重播就只有画面没有声音)。
    /// </summary>
    private void RestartFromBeginning()
    {
        if (_closing || _mp == null) return;
        try
        {
            _ended = false;
            EndOverlay.Visibility = Visibility.Collapsed;
            // 重播是同一片源的全新 input: 自救计数清零, 之后遇到提前 EOF 仍能续播
            _eofResumeCount = 0;
            _lastEofResumeMs = 0;

            if (_isLive)
            {
                // 直播没有"从头"的概念: 重新连一次拿一条新的流地址
                _ = LoadLiveAsync(_liveRoomId);
                return;
            }

            if (_isLocalPlayback)
            {
                var path = _lastPlayUrl;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                ShowLoading("正在重新播放…");
                var local = new Media(_libVLC, path, FromType.FromPath);
                PlayMedia(local);
            }
            else
            {
                var url = _lastPlayUrl;
                if (string.IsNullOrEmpty(url)) return;
                // 给个加载提示: 重播要重新拉一次流, 几秒内不会有画面 ——
                // 没有提示的话用户会以为"点了没反应"(这正是之前那版的观感)。
                // 遮罩由 Playing 事件负责收起。
                ShowLoading("正在重新播放…");
                PlayMedia(BuildMedia(url, _lastAudioUrl));
            }

            MarkSeekIssued(0);
            UpdatePlayButton();
        }
        catch { /* 播放器可能已失效 */ }
    }

    private void OnReplayClick(object sender, RoutedEventArgs e) => RestartFromBeginning();

    /// <summary>强制上报一次当前进度(暂停 / 关闭窗口时用), 绕过节流间隔判断</summary>
    private void SyncProgressNow()
    {
        if (_closing || _mp == null) return;
        if (_aid <= 0 || _currentCid <= 0) return;
        var progressSec = _mp.Time / 1000;
        _ = Svc.HistorySync.ReportNowAsync(_aid, _currentCid, progressSec);
    }

    private void UpdatePlayButton()
    {
        if (_closing || BtnPlay == null) return;
        // 标准语义: 正在播放时显示"暂停"图标(双竖条), 停止/暂停时显示"播放"图标(三角)
        // 用矢量 Path 而非字体图标, 彻底避免字体缺失时显示为方块的问题
        BtnPlay.Content = _mp.IsPlaying ? CreatePauseIcon() : CreatePlayIcon();
    }

    private static System.Windows.Shapes.Path CreatePlayIcon()
    {
        var geo = System.Windows.Media.Geometry.Parse("M 7,4 L 18,11 L 7,18 Z");
        geo.Freeze();
        return new System.Windows.Shapes.Path
        {
            Data = geo,
            Fill = Brushes.White,
            Width = 15,
            Height = 15,
            Stretch = System.Windows.Media.Stretch.Uniform
        };
    }

    private static System.Windows.Shapes.Path CreatePauseIcon()
    {
        var geo = System.Windows.Media.Geometry.Parse("M 5,4 L 9,4 L 9,18 L 5,18 Z M 12,4 L 16,4 L 16,18 L 12,18 Z");
        geo.Freeze();
        return new System.Windows.Shapes.Path
        {
            Data = geo,
            Fill = Brushes.White,
            Width = 15,
            Height = 15,
            Stretch = System.Windows.Media.Stretch.Uniform
        };
    }

    private void UpdateProgressUi()
    {
        if (_closing || _mp == null) return;

        // 宽限期(SeekGraceMs): 这段时间内不回写进度条。等待期已由下面的看门狗分支挡住,
        // 这里只作为"看门狗提前放行"时的兜底 —— 见 SeekGraceMs 的说明。
        var inSeekGrace = Environment.TickCount64 < _seekGraceUntil;

        // ---- 跳转看门狗: 目标迟迟没到位就重发 seek, 等待久了给缓冲提示 ----
        if (_pendingSeekMs >= 0 && !_isSeeking && !_ended && _mp.Length > 0)
        {
            var now = Environment.TickCount64;

            // ⚠ 这里踩过一个很隐蔽的坑(2026-09-25 实测):
            // LibVLC 的 Time **在 seek 指令发出的那一刻就跳成目标值**(实测 100ms 内),
            // 而此时的画面还冻在旧位置 —— 真正恢复画面要 0.4s(单文件)到 1.4s(DASH 双流)。
            // 所以"Time 接近目标"根本不能单独作为到位依据: 看门狗会在第一个心跳就判成功,
            // 后果有两个 ——
            //   1. 重发 seek 的分支永远不会执行(丢掉的 seek 没人救);
            //   2. "正在缓冲"提示永远不会弹(因为立刻就 return 了), 用户只能对着冻住的画面干等。
            // 正确的判据是"位置落在目标附近 **并且相对上一次观察真的动过**", 那才代表画面回来了。
            var time = _mp.Time;
            var settled = IsSeekSettled(time, _pendingSeekMs, _seekObservedMs, !_mp.IsPlaying);

            // ★ "落地但偏早"—— 远端 DASH 双流跳转的常态, 不是故障。
            // 判据必须**同时**远离"跳转前的位置"和"目标位置":
            //   · Time 刚设完读回来的那个值 == 目标(受理回执), 所以"远离目标"这条能把回执排除掉;
            //   · 真的被丢掉时位置会停在 _seekFromMs 附近, 所以"远离起点"这条能把"丢失"排除掉。
            // 剩下的就只能是"落到了目标之前的分片边界"(实测 60s → 55.2s, 150s → 145.7s)。
            // 这时**绝不能重发**: 重发会让 LibVLC 重建整个 input, 把正在恢复的播放又打回去
            // (实测把 5s 的跳转拖到 7.5s, 而且重发两次也追不回目标, 纯亏)。
            //
            // 但**必须连续两次心跳都成立**才认账: input 重启的那一瞬间位置可能报出一个过渡值,
            // 单次采样就认会让看门狗提前收工(顺带把 seek 静音也提前解除 —— 那正是"画面还没回来
            // 声音先到了"的老问题)。两次心跳间隔 100ms(等待期的粒度), 认账的延迟可以忽略。
            var farFromBoth = _seekFromMs >= 0 &&
                              Math.Abs(time - _seekFromMs) > SeekLandingToleranceMs &&
                              Math.Abs(time - _pendingSeekMs) > SeekConfirmWindowMs;
            var landedElsewhere = farFromBoth && _seekElsewhereSeenMs >= 0 && time != _seekElsewhereSeenMs;
            _seekElsewhereSeenMs = farFromBoth ? time : -1;

            _seekObservedMs = time;

            if (settled || landedElsewhere)
            {
                ClearSeekWatchdog();
            }
            else if (now >= _seekDeadline)
            {
                if (_seekRetries < SeekRetryMax)
                {
                    // seek 指令被 LibVLC 丢了: 重发一次(重发后位置会被重新 ack, 观察值也要清)
                    _seekRetries++;
                    _seekDeadline = now + 2500;
                    _seekObservedMs = -1;
                    try { _mp.Time = _pendingSeekMs; } catch { }
                }
                else
                {
                    ClearSeekWatchdog();
                }
            }
            else if (now - (_seekDeadline - 2500) > SeekBufferingDelayMs &&
                     LoadingOverlay != null && LoadingText != null &&
                     LoadingOverlay.Visibility == Visibility.Collapsed &&
                     !_isLive && !_isLocalPlayback)
            {
                // 跳转后迟迟不到位: 与其让用户对着冻住的老画面干等, 不如把"视频开头那套"
                // 加载动画(转圈 + 颜文字)搬出来说清楚"在缓冲"。
                ShowLoading(BufferingText);
            }

            // 等待期间不回写进度条(目标位置已由 MarkSeekIssued 钉住), 时间文字同理
            return;
        }

        // ---- 播放中途卡顿(网络缓冲)检测: 与"跳转"无关, 纯播放中卡住 ----
        UpdateStallDetector(inSeekGrace);

        if (_mp.Length > 0 && !inSeekGrace)
        {
            // ★ 进度条与时间文字必须同源(2026-10-01, 用户截图 24:43/24:49 实锤"进度条卡在
            //   最右端但时间还剩 6 秒"): 以前进度条用 `_mp.Position`, 时间文字用 `_mp.Time`。
            //   尾部解复用把较短的那路流(DASH 的音轨)读完时, Position 会先飘到 ~1.0 而
            //   Time 还在走 —— 两源一错位, 进度条就"提前满格卡住"。统一从 Time 派生后
            //   两者的行为永远一致(播完钉满格的规则不变)。
            // Slider 0-1000, 按比例(_updatingSlider 防止回写触发 ValueChanged 造成 seek 循环)
            var ms = _ended ? _mp.Length : GetDisplayedTimeMs(_mp.Length);
            var pos = ms / (double)_mp.Length; // 0.0~1.0
            if (_ended) pos = 1;
            if (!_isSeeking)
            {
                _updatingSlider = true;
                ProgressSlider.Value = pos * 1000;
                _updatingSlider = false;
            }
            TimeText.Text = $"{FormatClock(TimeSpan.FromMilliseconds(ms))} / " +
                            $"{FormatClock(TimeSpan.FromMilliseconds(_mp.Length))}";
        }
    }

    // --- 尾部进度外推(输入停滞、解码缓冲还在播的那几秒) ---
    // DASH 双流在结尾会出现"Time 停滞几秒"的窗口: 输入线程在等另一路流/最后分片,
    // 而解码缓冲里的内容还在正常播。此时若如实显示, 进度条和时间会冻在最后 5~6 秒
    // (用户报的"进度条最后五秒卡住不动")。尾部(剩余 < TailQuietZoneMs)改用
    // "锚点 + 墙钟 × 倍速"外推显示; 走出尾部或 EOF(钉满格)时自动归位。
    // 中间卡住仍如实显示, 那是真缓冲, 骗用户没有意义。
    //
    // ★★ 外推的触发判据是"真实读数没追上显示值", **不是**"Time 一格不动"(2026-10-01
    //   第二次返工的教训): 第一版只认 Time 完全冻结, 但实测尾部停滞时时钟常常**每跳
    //   挪几十毫秒**—— 每次都算"在走", 外推永远不启动, 进度条就以 ~6% 的速度龟速爬,
    //   看起来照样是卡死。锚点方案对"完全冻结"和"龟速蠕动"一视同仁。
    /// <summary>外推锚点: 最后一次确认"真实读数追上显示"的位置(-1 = 尚未建立)。</summary>
    private long _anchorMs = -1;
    /// <summary>锚点对应的墙钟(毫秒, Environment.TickCount64)。</summary>
    private long _anchorWall;
    /// <summary>
    /// 上一次**显示**的进度。尾部只进不退的钳制基准(见 GetDisplayedTimeMs);
    /// seek / 换片时归零, 否则"从结尾往回跳转"会被钳在旧位置上。
    /// </summary>
    private long _displayedMs = -1;

    /// <summary>取"应该显示的播放位置": 正常时就是 _mp.Time; 尾部按"锚点 + 墙钟×倍速"外推。</summary>
    private long GetDisplayedTimeMs(long lengthMs)
    {
        var t = _mp.Time;
        var wall = Environment.TickCount64;

        // 非尾部 / 暂停: 如实显示, 锚点对齐当下(暂停时不积累外推量, 恢复后从暂停点继续)
        if (!_mp.IsPlaying || lengthMs - t >= TailQuietZoneMs)
        {
            _anchorMs = t;
            _anchorWall = wall;
            _displayedMs = t;
            return t;
        }

        long shown;
        if (t > _displayedMs)
        {
            // 真实读数追上/超过显示值: 以真实为准, 抬锚
            _anchorMs = t;
            _anchorWall = wall;
            shown = t;
        }
        else
        {
            // 真实读数落后(停滞/龟速/时钟回跳): 从锚点按墙钟外推, 封顶在片尾
            if (_anchorMs < 0) { _anchorMs = t; _anchorWall = wall; }
            var rate = _mp.Rate;
            if (rate <= 0) rate = 1.0f;
            shown = Math.Min(_anchorMs + (long)((wall - _anchorWall) * rate), lengthMs);
        }

        // ★ 只进不退: 时钟重锚/回跳不让显示跟着倒。真正允许倒退的路径
        //   (用户拖进度条、点重播、换片)都会先归零 _displayedMs / _anchorMs。
        if (_displayedMs >= 0 && shown < _displayedMs)
            shown = _displayedMs;
        _displayedMs = shown;
        return shown;
    }

    /// <summary>
    /// 播放器时间标签的时钟格式: 不足一小时用 mm:ss, **满一小时用 h:mm:ss**。
    /// ★ 之前恒用 mm:ss, 一个多小时的视频总时长"1:13:25"会被截成"01:13"(用户截图实锤:
    ///   播到 46:52 时右侧显示 01:13) —— TimeSpan 的 "mm" 格式符**不会进位到小时**,
    ///   它只是"分钟位的两位数字"。小时位要显式给, B 站的样式是"1:13:25"(小时不补零)。
    /// </summary>
    private static string FormatClock(TimeSpan t)
        => t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";

    /// <summary>
    /// 判断一次跳转是不是**真的**到位了(画面回来了), 而不是只收到了 LibVLC 的"已受理"回执。
    ///
    /// 为什么需要这么拧巴的判据(2026-09-25 真机实测得出来):
    ///   LibVLC 的 Time 在 seek 指令发出的那一刻就跳成目标值 —— 实测发出后 100ms 内返回
    ///   60000ms(正好等于目标), 而画面要到 380ms(单文件源)~1410ms(DASH 双流)后才动。
    ///   换句话说"Time 接近目标"只是一张回执, 完全没有画面信息。
    ///   老代码只看这个, 于是看门狗每个跳转都在第一跳就判成功, 连带两个后果:
    ///     ① 重发 seek 的分支永远走不到 —— 被丢掉的 seek 没人救;
    ///     ② "正在缓冲"提示永远弹不出来 —— 用户只能对着冻住的画面干等(这正是"拖进度条卡"
    ///        最难受的那部分: 不是慢, 是没有任何反馈)。
    ///
    /// 所以判据改成"接近目标 **并且** 相对上一次观察真的动过"。
    /// 唯一的例外是暂停态: 暂停时位置本来就不前进, 而 VLC 已经按目标解出了那一帧,
    /// 这时"接近目标"就是完整的到位信号(不加这条, 暂停中拖进度条会白弹一个缓冲提示)。
    /// </summary>
    /// <param name="timeMs">本次观察到的播放位置(毫秒)</param>
    /// <param name="targetMs">跳转目标(毫秒)</param>
    /// <param name="observedMs">上一次观察到的位置, -1 = 还没观察过</param>
    /// <param name="paused">播放器当前是否处于暂停/停止态</param>
    private static bool IsSeekSettled(long timeMs, long targetMs, long observedMs, bool paused)
    {
        if (Math.Abs(timeMs - targetMs) > SeekConfirmWindowMs) return false;
        if (paused) return true;
        return observedMs >= 0 && timeMs != observedMs;
    }

    /// <summary>跳转确认到位(或放弃重试)后收尾: 关掉"正在缓冲"提示, 清掉待确认目标</summary>
    private void ClearSeekWatchdog()
    {
        _pendingSeekMs = -1;
        _seekObservedMs = -1;
        _seekFromMs = -1;
        _seekElsewhereSeenMs = -1;
        if (_progressTimer != null) _progressTimer.Interval = ProgressTickIdle;
        RestoreVolumeAfterSeek();
        HideBufferingIfShown();
    }

    // ------------------------------------------------------------ seek 静音

    /// <summary>跳转发出: 静音(记住原音量)。连续拖动时已静音则只更新心跳, 不覆盖原值。</summary>
    private void MuteForSeek()
    {
        if (_closing || _mp == null || _seekMuteSavedVolume >= 0) return;
        try
        {
            // 读权威值而不是原生 _mp.Volume: aout 未就绪时原生读回 -1, 会被 "v<=0" 误判成
            // "本来就静音" 而跳过, 恢复点也就不会把音量补写回 aout(见 _volumeCache 的说明)
            var v = _volumeCache;
            if (v <= 0) return;   // 本来就静音, 无事可做
            _seekMuteSavedVolume = v;
            _mp.Volume = 0;
        }
        catch { /* 播放器已失效就算了 */ }
    }

    /// <summary>画面确认到位(或放弃/换片/播完): 恢复跳转前的音量。</summary>
    private void RestoreVolumeAfterSeek()
    {
        if (_seekMuteSavedVolume < 0) return;
        var v = _seekMuteSavedVolume;
        _seekMuteSavedVolume = -1;
        _volumeCache = v;   // 静音期间滚轮/滑块一直在改权威值, 恢复时对齐一次, 防止两条账本漂移
        try { if (_mp != null) _mp.Volume = v; } catch { }
    }

    /// <summary>当前遮罩是不是我们自己弹的"正在缓冲"(据此判断能不能收掉)</summary>
    private bool IsShowingBuffering()
        => LoadingOverlay != null && LoadingText != null &&
           LoadingOverlay.Visibility == Visibility.Visible &&
           LoadingText.Text == BufferingText;

    /// <summary>
    /// 收起"正在缓冲"遮罩 —— **只收这一种**。
    /// 不能直接 HideLoading(): 此刻遮罩上可能写着"正在切换清晰度…""正在获取播放地址…",
    /// 那些有自己的生命周期, 被这里顺手收掉就会露出后面的黑屏。
    /// </summary>
    private void HideBufferingIfShown()
    {
        if (_closing) return;
        if (IsShowingBuffering()) HideLoading();
    }

    // --- 播放中途卡顿检测 ---
    // 在每 500ms 一次的进度刷新里比较 _mp.Time 有没有推进: 连续几个心跳原地不动就是卡住了。
    //
    // 为什么不用 LibVLC 的 Buffering 事件: 它在正常播放时也会频繁给到 100, 在部分片源上
    // 又长期给不满 100 —— 拿它当"该不该提示"的依据很容易把提示卡在屏上收不掉。
    // 用"播放位置有没有推进"这个客观事实判断: 最多漏报(极少数片源 Time 会假性前进),
    // 不会误报, 而且位置一动就自动恢复。
    //
    // 这个检测同时覆盖了"快进后卡住"和"看着看着网络卡住"两种情况 —— 用户感知都是同一件事。
    private long _stallLastMs = -1;
    private int _stallTicks;

    /// <summary>连续几次心跳没推进才算卡顿(2 * 500ms ≈ 1 秒, 正常的缓冲抖动不会被判成卡顿)</summary>
    private const int StallTicksToReport = 2;

    /// <summary>判定"没推进"的阈值(毫秒): 500ms 的心跳间隔下正常播放至少推进几百毫秒</summary>
    private const long StallIdleToleranceMs = 60;

    private void UpdateStallDetector(bool inSeekGrace)
    {
        // 直播/本地文件没有"缓冲卡顿"这回事; 播完了也不算
        if (_isLive || _isLocalPlayback || _ended || _mp == null || _mp.Length <= 0)
        {
            _stallTicks = 0;
            return;
        }

        // 暂停时位置本来就不动; 拖动/跳转宽限期内同理 —— 都不算卡顿
        if (!_mp.IsPlaying || _isSeeking || inSeekGrace || _pendingSeekMs >= 0)
        {
            _stallTicks = 0;
            return;
        }

        // ★ 结尾豁免(2026-10-01, 用户实测"长视频最后五秒弹'正在缓冲'"): 剩余不足
        // TailQuietZoneMs 时不再报缓冲。DASH 音视频是两路独立流, 长度略有差异,
        // 短的那路数据先耗尽时 Time 会停几秒等另一路 —— 这是"快播完了"的正常形态,
        // 不是网络卡顿(三分钟以下的视频走本地合流单文件没有双流, 所以从来不出这个提示)。
        // 就算真是网络慢, 结尾几秒也没有"等它缓冲好"的价值, EndReached 马上会来收场。
        var now = _mp.Time;
        var remainingMs = _mp.Length - now;
        if (remainingMs < TailQuietZoneMs)
        {
            _stallTicks = 0;
            _stallLastMs = now;
            HideBufferingIfShown();
            return;
        }

        if (_stallLastMs >= 0 && Math.Abs(now - _stallLastMs) < StallIdleToleranceMs)
        {
            _stallTicks++;
            if (_stallTicks >= StallTicksToReport && LoadingOverlay != null &&
                LoadingOverlay.Visibility != Visibility.Visible)
            {
                // 遮罩上已经有别的内容(切清晰度等)就不抢, 免得把对方的文案顶掉
                ShowLoading(BufferingText);
            }
            return;
        }

        // 位置推进了 -> 卡顿结束。只收我们自己弹的那张遮罩
        _stallLastMs = now;
        if (_stallTicks >= StallTicksToReport) HideBufferingIfShown();
        _stallTicks = 0;
    }

    /// <summary>把一次跳转记下来: 打上宽限期, 并立刻把进度条/时间钉到目标位置</summary>
    private void MarkSeekIssued(long targetMs)
    {
        _seekGraceUntil = Environment.TickCount64 + SeekGraceMs;
        MuteForSeek();
        // 启动/重置跳转看门狗: 2.5 秒内位置还没回到目标附近就重发 seek
        _pendingSeekMs = targetMs;
        // 记下"跳转前的位置"给看门狗判"是不是真的丢了"(见 _seekFromMs 的说明)。
        // ★ 必须在这里取: 调用方通常已经先设过 `_mp.Time = target`, 那个属性读回来的是受理回执
        //   (= 目标值), 拿它当起点会让"落地偏早"永远判不出来。
        _seekFromMs = _lastKnownMs > 0 ? _lastKnownMs : -1;
        _seekRetries = 0;
        _seekDeadline = Environment.TickCount64 + 2500;
        // 观察值清空: 第一个心跳拿到的会是"目标值本身"(那是 LibVLC 的 ack, 不是画面), 不能当作推进
        _seekObservedMs = -1;
        _seekElsewhereSeenMs = -1;
        _ended = false;
        // 进度外推的锚点与显示钳制基线作废: 不作废的话, 跳转落地后可能外推出凭空进度,
        // 往回拖进度条也无法倒着显示
        _anchorMs = -1;
        _displayedMs = -1;
        EndOverlay.Visibility = Visibility.Collapsed;
        // 等待期把心跳调密, 好让缓冲提示与到位判定都及时(见 ProgressTickSeeking 的说明)
        if (_progressTimer != null) _progressTimer.Interval = ProgressTickSeeking;

        if (_totalMs <= 0) return;
        _updatingSlider = true;
        ProgressSlider.Value = Math.Clamp(targetMs * 1000.0 / _totalMs, 0, 1000);
        _updatingSlider = false;
        TimeText.Text = $"{FormatClock(TimeSpan.FromMilliseconds(targetMs))} / " +
                        $"{FormatClock(TimeSpan.FromMilliseconds(_totalMs))}";
    }

    /// <summary>进度条值变化: 点击轨道任意位置直接跳转(IsMoveToPointEnabled + 本处理器)。
    /// 拖动过程由 _isSeeking 屏蔽, 松手才生效</summary>
    private void OnProgressValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_closing || _mp == null || _isSeeking || _updatingSlider) return;
        if (_mp.Length <= 0) return;

        // 用 Time(毫秒) 而不是 Position(0~1 的浮点): 位置量本身就是毫秒,
        // 少一层换算, 短视频(6 秒时 0.001 只代表 6ms)上也不会被"变化太小"的过滤挡掉。
        var targetMs = (long)Math.Round(e.NewValue / 1000.0 * _mp.Length);
        if (Math.Abs(_mp.Time - targetMs) < 250) return; // 已经在目标附近, 不必发跳转
        _mp.Time = targetMs;
        MarkSeekIssued(targetMs);
    }

    private void OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing) return;
            _totalMs = e.Length;
            UpdateTotalDurationText();
        }));

    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing) return;
            // 记下最后已知位置: EOF 之后 _mp.Time 会归零(那条 input 已结束),
            // "提前结束自救"只能靠这里留底
            _lastKnownMs = e.Time;
            // ★ 全屏黑幕: 能收到时间推进 = 视频正在出帧, 这一刻撤掉最准。
            //   不能只靠 Playing 事件 —— 进全屏时视频本来就在播, Playing 不会再来一次。
            ReleaseFullscreenCover();
            // ★ 这里**不**调 UpdateProgressUi —— 进度条与跳转看门狗的唯一心跳是 _progressTimer。
            //   曾经这里也在调, 于是进度 UI 有两个时钟: 等待期定时器 100ms 一跳, 而 TimeChanged
            //   会在两跳之间插进来。这不只是重复干活 —— 看门狗判"落地但偏早"要求
            //   **连续两次心跳**都成立(见 UpdateProgressUi), 那道闸的用意是挡掉 input 重启
            //   那一瞬的过渡位置; 双时钟会把"两次采样"压到 1ms 内, 过渡值照样骗过它,
            //   后果是 seek 静音提前解除、缓冲提示提前消失(画面没回来声音先到)。
            //   定时器全程在跑(构造里 Start, 等待期 100ms / 平时 500ms), 少这一个调用不丢刷新。
            DispatchDanmaku();
            MaybeSyncProgress();
        }));

    /// <summary>
    /// 播放中周期上报进度到云端历史。
    /// 节流逻辑全部在 HistorySyncService 里, 这里只是"到点就报"。
    /// 两个额外的守卫:
    ///   - 只在真正处于播放态时上报(暂停/拖动时不报, 避免上报错误的中间进度);
    ///   - 拖动进度条期间(_isSeeking)跳过, 松手后下一次心跳自然会带上新位置。
    /// 整个调用是 fire-and-forget: 上报失败绝不能影响播放。
    /// </summary>
    private void MaybeSyncProgress()
    {
        if (_closing || _mp == null) return;
        if (!_mp.IsPlaying || _isSeeking) return;
        if (_aid <= 0 || _currentCid <= 0) return;

        var progressSec = _mp.Time / 1000;
        // ShouldReport 内部已含"未登录不上报"的语义(ReportHistoryAsync 会静默跳过),
        // 这里先做一次本地判断, 避免无意义地起 Task。
        if (!Svc.HistorySync.ShouldReport(_aid, _currentCid, progressSec)) return;

        _ = Svc.HistorySync.ReportAsync(_aid, _currentCid, progressSec);
    }

    private void UpdateTotalDurationText()
    {
        if (_closing || _totalMs <= 0) return;
        TimeText.Text = $"00:00 / {FormatClock(TimeSpan.FromMilliseconds(_totalMs))}";
    }

    /// <summary>
    /// 播放结束。LibVLC 官方警告: 绝不能在其事件回调线程里直接调 Stop()(必死锁),
    /// 必须异步派发到 UI 线程执行, 且 Stop 包 try/catch(状态竞争时可能抛异常)
    /// </summary>
    /// <summary>
    /// 播完了。**刻意不调用 `_mp.Stop()`**:
    /// Stop 会把视频输出整个拆掉, 那个 HWND 就成一块没有内容的空白面 —— 浅色界面下看着
    /// 就是"整块全白"(用户报的就是这个)。停在那里不动、上面盖一层遮罩, 既挡住空白,
    /// 又能顺手给出"重播"入口; 下次播放走 RestartFromBeginning 把时间归零。
    ///
    /// LibVLC 官方警告: 绝不能在其事件回调线程里直接操作播放器(必死锁), 所以这里依旧
    /// 异步派发到 UI 线程。
    /// </summary>
    private void OnEndReached(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_closing) return;

        // ---- 提前结束的自救 ----
        // DASH 音轨(input-slave)先于视频到 EOF 时, LibVLC 会结束整个 input:
        // 表现就是"最后几秒没声音 + 进度条提前满格"。这不是真的播完 ——
        // 从最后已知位置重建 Media 续播(次数与推进距离双保险, 防止音频本来就短的
        // 片源无限循环续播)。
        //
        // 两个收紧(短视频"播完又跳回几秒重播"的根因都在这):
        //   1. 长度基准用 **_mp.Length**(LibVLC 实际解封装出的时长), 而不是接口的
        //      timelength —— B 站接口给的 timelength 经常比流的真实时长长几秒,
        //      按 _totalMs 判断会把"正常播完"误判成"提前结束", 于是重建 Media
        //      从"几秒前"续播 —— 用户看到的就是"明明结束了又自己倒回去放"。
        //   2. 单文件源(_lastAudioUrl == null, durl/合流)没有 input-slave,
        //      根本不存在"音轨先 EOF"这个问题, 直接跳过自救 —— 否则任何风吹草动的
        //      提前 EOF 都会在单文件源上触发一次无谓的回跳续播。
        var effectiveTotalMs = _mp.Length > 0 ? _mp.Length : _totalMs;
        if (!_isLive && !_isLocalPlayback && !_ended && effectiveTotalMs > 0 &&
            !string.IsNullOrEmpty(_lastAudioUrl) &&
            // 尾部静默区见 TailQuietZoneMs 的说明: 音轨缺口之内自救救不回任何数据,
            // 只会"黑屏缓冲一趟 → 立刻再 EOF → 重播遮罩"(2026-10-01 用户两轮实测)
            _lastKnownMs > 0 && _lastKnownMs < effectiveTotalMs - TailQuietZoneMs &&
            _eofResumeCount < EofResumeMax &&
            _lastKnownMs - _lastEofResumeMs >= EofResumeMinAdvanceMs &&
            !string.IsNullOrEmpty(_lastPlayUrl))
        {
            _eofResumeCount++;
            _lastEofResumeMs = _lastKnownMs;
            var resume = _lastKnownMs;
            ShowLoading("正在继续播放…");
            // 起播后由 Playing 事件恢复到断点(见 _resumeAfterSwitchMs 那套机制)
            _resumeAfterSwitchMs = resume;
            try { PlayMedia(BuildMedia(_lastPlayUrl, _lastAudioUrl)); } catch { }
            return;
        }

        _ended = true;
        RestoreVolumeAfterSeek();   // 播完若还挂在 seek 静音里, 立刻恢复(重播/下一条别哑)
        // ★ 把手势层切成不透明黑(2026-10-01, 用户报"浅色全屏全白/深色一层阴影遮罩"):
        //   EOF 后 vout 被拆掉, 视频 HWND 露出**默认白底** —— 它在所有 WPF 元素之上
        //   (airspace), RootGrid 切黑盖不住它。表现就是: 浅色主题全屏一块纯白;
        //   深色主题上半透明的"重播"遮罩(#CC000000)盖在白底上 = "一层阴影遮罩"。
        //   手势层在覆盖窗口(VideoView.Content)里, 位于 HWND **之上**, 切黑才真的盖得住。
        //   下次起播由 Playing 事件还原成近透明(见 VideoAreaBackBrush)。
        VideoAreaRoot.Background = Brushes.Black;
        // 播完了: 跳转看门狗必须收掉 —— 它在外面的条件是 !_ended, 留着 _pendingSeekMs >= 0
        // 会让卡顿检测被永久跳过(那个判断里也有 _pendingSeekMs >= 0), 之后重播的卡顿就不再提示了。
        _pendingSeekMs = -1;
        _seekObservedMs = -1;
        if (_progressTimer != null) _progressTimer.Interval = ProgressTickIdle;
        EndOverlay.Visibility = Visibility.Visible;
        // 进度条钉在满格 + 时间显示成总时长, 否则 Position 可能报 0, 看起来像"回到开头了"
        UpdateProgressUi();
        UpdatePlayButton();

        // 播到结尾也算一次"自然的段落结束", 把进度补报到云端(下次能从结尾续上)。
        // 注意不能用 _mp.Time —— EOF 之后它已经归零, 会把云端进度错误地报成 0;
        // 用 TimeChanged 留底的 _lastKnownMs 才是真实看完的位置。
        if (_aid > 0 && _currentCid > 0)
            _ = Svc.HistorySync.ReportNowAsync(_aid, _currentCid, _lastKnownMs / 1000);
    }));

    private void OnProgressDragStart(object sender, DragStartedEventArgs e) => _isSeeking = true;

    private void OnProgressDragEnd(object sender, DragCompletedEventArgs e)
    {
        _isSeeking = false;
        if (_mp.Length > 0)
        {
            var targetMs = (long)Math.Round(ProgressSlider.Value / 1000.0 * _mp.Length);
            _mp.Time = targetMs;
            MarkSeekIssued(targetMs);
        }
        // 拖动结束后立刻补报一次: 用户主动跳转是"我在看这个位置"的强信号,
        // 如果只等 15 秒周期上报, 此时关掉窗口会把位置记在拖动前的老地方。
        SyncProgressNow();
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mp == null) return;
        var v = (int)VolumeSlider.Value;
        // 滑块是用户拖动时的入口, 程序化赋值(SetVolume)也会走到这 —— 同值回写, 无副作用。
        // 必须同步权威值: 否则拖完滑块再滚轮, 基数还是旧缓存(见 _volumeCache 的说明)。
        _volumeCache = v;
        // seek 静音期间(与 SetVolume 同一套约定): 新值记为恢复目标, 声音保持静音直到画面就绪。
        // 这里不能直接写 _mp.Volume —— 否则拖一下滑块就绕过静音, 声音又跑到画面前面去了。
        if (_seekMuteSavedVolume >= 0)
        {
            _seekMuteSavedVolume = v;
            try { _mp.Volume = 0; } catch { }
            return;
        }
        try { _mp.Volume = v; } catch { }
    }

    // ------------------------------------------------------------ 音量(滚轮 / 快捷键)

    /// <summary>一次音量调节的步进。与方向键上下保持一致, 滚轮手感不至于一格跳太多。</summary>
    private const int VolumeStep = 5;

    /// <summary>音量提示浮层的停留时长</summary>
    private static readonly TimeSpan VolumeOsdHold = TimeSpan.FromMilliseconds(1100);

    private DispatcherTimer? _volumeOsdTimer;

    /// <summary>
    /// 滚轮调音量: 上滑 +5, 下滑 -5。
    ///
    /// 为什么用 PreviewMouseWheel 而不是 MouseWheel:
    /// 滚轮是**隧道**事件, Preview 那条从根往叶走, 挂了就能在底部控制栏的 Slider、
    /// 清晰度 Popup 的 ListBox 之前拿到事件; 否则鼠标压在控制栏上滚轮就没反应。
    /// 只挂视频区(和覆盖它的那层浮动窗口, 见 HookOverlayKeyboard) ——
    /// 鼠标在右侧信息面板/评论区时, 滚轮该滚那个列表。
    ///
    /// ⚠ **必须同时挂到 VideoView 的 ForegroundWindow**: 视频画面是被那层独立的顶层窗口
    /// (airspace)盖住的, 鼠标压在画面上时滚轮事件根本进不了主窗口的 WPF 树 ——
    /// 用户报的"滚轮调音量不灵敏"就是这个: 只有压在控制栏那一小条上才有效。
    /// 与键盘失灵、鼠标侧键失灵是同一个坑。
    ///
    /// 步进用**累计**而不是"每个事件至少一步": 标准鼠标一格 ±120, 而触控板/精密滚轮会给
    /// 一串 ±1~±40 的小值 —— 后者按"每事件至少一步"算, 轻轻一划就跳十几个百分点,
    /// 反过来显得"太灵/乱跳"。累计到一整格才走一步, 两种设备的手感就都对了。
    /// </summary>
    private void OnVideoAreaMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_mp == null || _closing || e.Delta == 0) return;

        // 反向滚动时先清掉上次攒的余量, 否则要先"抵消"掉才动
        if (Math.Sign(e.Delta) != Math.Sign(_wheelAccum)) _wheelAccum = 0;
        _wheelAccum += e.Delta;

        var notches = _wheelAccum / 120;
        if (notches != 0)
        {
            _wheelAccum -= notches * 120;
            // 基数用 _volumeCache 而不是 _mp.Volume: 见 _volumeCache 的说明(aout 未就绪时原生读回是 -1/旧值)
            SetVolume(_volumeCache + notches * VolumeStep);
        }
        // 不够一格也要吃掉: 否则事件继续冒泡给别的可滚动控件(视频区里其实没有, 但别留隐患)
        e.Handled = true;
    }

    /// <summary>滚轮累计的余量(±120 为一格)。见 OnVideoAreaMouseWheel 的说明</summary>
    private int _wheelAccum;

    /// <summary>
    /// 应用侧的音量权威值(0~100)。
    ///
    /// 为什么不直接读 _mp.Volume(反编译 LibVLCSharp 3.8.0 确认):
    /// getter 是纯原生调用 libvlc_audio_get_volume, **没有 aout(音频输出)时返回 -1**;
    /// setter 在无 aout 时也是静默丢弃。aout 的生命周期跟媒体流走 ——
    /// 长视频走远端 DASH 双流, 起播/缓冲/拖动/换清晰度期间 aout 可能尚未就绪或刚被重建,
    /// 这时拿 _mp.Volume 当滚轮基数, 基数就是 -1/旧值, 步进全乱(用户报的"长视频滚轮调音量不灵敏")。
    /// 短视频是本地合流文件, aout 几乎秒建, 所以从来感觉不到 —— 时长相关性由此而来。
    /// 滚轮/键盘/滑块一律以本字段为基数, 原生侧只在 SetVolume 里单向写、在 Playing(aout 就绪)时补写。
    /// </summary>
    private int _volumeCache = 80;

    /// <summary>
    /// 设置音量: 同步 LibVLC 与滑块, 并弹出音量提示。
    /// 统一入口是必要的 —— 之前 ↑↓/静音键各自改一遍 _mp.Volume 和 VolumeSlider.Value,
    /// 再加一条滚轮路径就会变成三份容易走偏的代码。
    /// </summary>
    private void SetVolume(int volume, bool showHint = true)
    {
        if (_closing || _mp == null) return;
        var v = Math.Max(0, Math.Min(100, volume));
        // 权威值先落账: 之后所有路径(滚轮/键盘)都以它为基数, 不依赖原生读回(见 _volumeCache 的说明)
        _volumeCache = v;
        // seek 静音期间用户调音量: 把新值记为"恢复目标", 声音仍保持静音直到画面就绪 ——
        // 若直接写 _mp.Volume 会立刻出声(又变成画面没到声音先到), 而且恢复时还会覆盖用户的值。
        if (_seekMuteSavedVolume >= 0)
        {
            _seekMuteSavedVolume = v;
            try { _mp.Volume = 0; } catch { }
            if (VolumeSlider != null) VolumeSlider.Value = v;
            if (showHint) ShowVolumeHint(v);
            return;
        }
        try { _mp.Volume = v; } catch { return; }
        if (VolumeSlider != null) VolumeSlider.Value = v;
        if (showHint) ShowVolumeHint(v);
    }

    /// <summary>
    /// 浮出音量提示。调音量时画面之外没有任何反馈(音量条在底部偶尔还是自动隐藏的),
    /// 所以给一个"音量 65%"的短提示; 连续调节时只重置计时器, 不重播淡入动画,
    /// 否则每滚一格都闪一下。
    /// </summary>
    private void ShowVolumeHint(int volume)
    {
        if (_closing || VolumeOsd == null) return;

        VolumeOsdText.Text = volume + "%";
        // 0 用静音图标, 其余用喇叭: 一眼就能看出"是没声还是声音小"
        VolumeOsdIcon.Text = volume == 0 ? "\uE74F" : "\uE767";

        // 每次都从"当前透明度"补到 1。
        // 不要在"已经 Visible 就跳过"上做判断 —— 淡出动画还没跑完时(计时器已被置空)
        // 用户再滚一下, 浮层仍是 Visible 于是不重播, 结果它继续淡到 0 卡住,
        // 用户反而看不到任何音量反馈。从当前值补到 1 在这两种情况下都正确:
        // 已经在 1 上就是原地不动(连滚不闪), 淡出中途则接着淡回来。
        VolumeOsd.Visibility = Visibility.Visible;
        VolumeOsd.BeginAnimation(OpacityProperty,
            new DoubleAnimation(Math.Min(1.0, VolumeOsd.Opacity), 1, TimeSpan.FromMilliseconds(120)));

        _volumeOsdTimer?.Stop();
        _volumeOsdTimer = new DispatcherTimer { Interval = VolumeOsdHold };
        _volumeOsdTimer.Tick += (_, _) =>
        {
            _volumeOsdTimer?.Stop();
            _volumeOsdTimer = null;
            if (_closing) return;
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(240));
            fade.Completed += (_, _) =>
            {
                // 淡出期间用户又调了一次 -> ShowVolumeHint 已经重开计时器并重播淡入,
                // 这时绝对不能把浮层藏掉(表现就是"滚轮调音量提示闪一下就不见了")
                if (_closing || _volumeOsdTimer != null) return;
                VolumeOsd.Visibility = Visibility.Collapsed;
            };
            VolumeOsd.BeginAnimation(OpacityProperty, fade);
        };
        _volumeOsdTimer.Start();
    }

    /// <summary>隐藏音量提示并停掉它的计时器(关闭流程用)</summary>
    private void StopVolumeOsd()
    {
        _volumeOsdTimer?.Stop();
        _volumeOsdTimer = null;
    }

    private void OnSpeedClick(object sender, RoutedEventArgs e)
    {
        if (_mp == null) return;
        var cur = _mp.Rate;
        var next = cur switch
        {
            1.0f => 1.5f,
            1.5f => 2.0f,
            2.0f => 0.5f,
            _ => 1.0f
        };
        _mp.SetRate(next);
        if (SpeedText != null) SpeedText.Text = next.ToString("0.#") + "x";
    }

    /// <summary>把弹层的右缘与目标按钮的右缘对齐(放在按钮正上方)。</summary>
    private void RightAlignPopup(System.Windows.Controls.Primitives.Popup popup,
        System.Windows.FrameworkElement target)
    {
        var w = popup.ActualWidth;
        if (w <= 0 && popup.Child is FrameworkElement fe) w = fe.ActualWidth;
        if (w <= 0) w = target.ActualWidth;   // 最后的兜底: 至少不会把弹层推出窗外
        popup.HorizontalOffset = target.ActualWidth - w;
    }

    // ------------------------------------------------------------ 清晰度

    /// <summary>填充清晰度菜单并高亮当前项(以 B 站实际返回的清晰度为准)</summary>
    private void FillQualityMenu(int actualQn)
    {
        if (QualityList == null) return;
        _fillingQualityMenu = true;
        try
        {
            QualityList.Items.Clear();
            foreach (var (qn, label) in _qualities)
                QualityList.Items.Add(new ListBoxItem { Tag = qn, Content = label });
            if (actualQn > 0) _currentQn = actualQn;
            // ★★★ 按钮上的文字必须按"**接口实际给的**档位"或"用户的偏好"来取,
            //   **绝不能**对 _currentQn 直接调 QnToLabel(2026-10-04 修):
            //   「默认画质 = 自动」时 _currentQn 是发给接口的请求上界 127, 而 QnToLabel(127)
            //   是"杜比视界"(那是真实流里的 8K 档)。于是本地文件/直播/起播失败(actualQn == 0)
            //   这些"没有实际档位可显示"的路径上, 按钮会写成一个用户根本没有的档位。
            //   这里只显示**确实在播的那一档**: 有实际档位就说它, 没有(本地/直播)就说用户的偏好,
            //   两者都不会把"请求用的内部值"当成画质名报给用户。
            var label2 = actualQn > 0
                ? ApiClient.QnToLabel(actualQn)
                : QualityPreference.LabelOf(Svc.Settings.PreferredQualityQn);
            if (QualityText != null) QualityText.Text = label2;
            // 高亮与实际清晰度匹配的项
            for (var i = 0; i < QualityList.Items.Count; i++)
            {
                if (QualityList.Items[i] is ListBoxItem li && li.Tag is int q && q == actualQn)
                {
                    QualityList.SelectedIndex = i;
                    break;
                }
            }
        }
        finally
        {
            _fillingQualityMenu = false;
        }
    }

    private void OnQualityClick(object sender, RoutedEventArgs e)
    {
        QualityPopup.IsOpen = true;
    }

    private void OnQualitySelected(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingQualityMenu || QualityPopup == null) return;
        if (QualityList.SelectedItem is not ListBoxItem li || li.Tag is not int qn) return;
        QualityPopup.IsOpen = false;
        if (qn == _currentQn || _switchingQuality) return;
        _ = SwitchQualityAsync(qn);
    }

    /// <summary>切换清晰度: 记住播放进度, 换流后自动恢复</summary>
    private async Task SwitchQualityAsync(int qn)
    {
        if (_closing || _switchingQuality || _currentCid <= 0) return;
        _switchingQuality = true;
        try
        {
            var resumeMs = _mp.Time;
            ShowLoading("正在切换清晰度…");

            // 换清晰度 = 换成另一路流: 上一档的合流立刻作废(缓存键里带 qn, 不会互相污染)
            CancelRemuxPrefetch();

            var (_, playErr, main, backups, _, qualities, actualQn, audioUrl, remux) =
                await Svc.Api.GetPlayUrlAsync(_currentBvid, _currentCid, qn);
            if (_closing) return;

            // 和首次加载同一套: 这一档已缓存就播本地, 没缓存就下完再播(单文件源: 跳转快、结尾完整)
            var local = ShortVideoCache.TryGet(_currentBvid, actualQn);
            if (local == null && ShortVideoCache.WorthCaching(remux))
                local = await PrepareShortVideoAsync(remux!, actualQn);
            if (_closing) return;

            var url = local ?? main;
            if (string.IsNullOrEmpty(url) && backups.Count > 0) url = backups[0];
            if (string.IsNullOrEmpty(url))
            {
                // 以前这里只有一句写死的"该清晰度暂时不可用" —— 而真正的原因(大会员/付费专属 /
                // 网络失败 / 该档无源)在 API 层就被丢掉了, 用户只能反复换档试。
                Svc.Toast.Show(string.IsNullOrEmpty(playErr) ? "该清晰度暂时不可用" : playErr);
                HideLoading();
                return;
            }
            _qualities.Clear();
            _qualities.AddRange(qualities);
            FillQualityMenu(actualQn > 0 ? actualQn : qn);

            // 起播后(Playing 事件)自动恢复到切换前进度
            _resumeAfterSwitchMs = resumeMs > 2000 ? resumeMs : 0;
            // 新流重新计时"提前结束自救"
            _eofResumeCount = 0;
            _lastEofResumeMs = 0;
            // 记住换过去的这一路: 之后点「重播」要回到同一个源, 而不是换回上一个清晰度
            _lastPlayUrl = url;
            // 本地合流是单文件源: 不能带 input-slave, 否则等于给同一个文件再挂一条音轨
            _lastAudioUrl = local != null ? null : audioUrl;
            // 必须先 Stop: LibVLC 3 里已有 input 在播时 play() 会被**直接忽略**,
            // 不 Stop 的话新 Media 根本不会起播 —— 这就是"切换清晰度没反应"
            try { _mp.Stop(); } catch { }
            PlayMedia(BuildMedia(url!, _lastAudioUrl));
            UpdatePlayButton();
        }
        catch (Exception ex)
        {
            if (_closing) return;
            Svc.Toast.Show("切换清晰度失败: " + ex.Message);
            HideLoading();
        }
        finally
        {
            _switchingQuality = false;
        }
    }

    // ------------------------------------------------------------ 弹幕显示区域

    private void OnDanmakuAreaClick(object sender, RoutedEventArgs e)
    {
        DanmakuAreaPopup.IsOpen = true;
    }

    /// <summary>调整弹幕显示区域占比(弹幕只在画面上方 ratio 比例内滚动)</summary>
    private void OnDanmakuAreaChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var pct = (int)e.NewValue;
        _danmakuAreaRatio = pct / 100.0;
        if (DanmakuAreaText != null) DanmakuAreaText.Text = pct + "%";
        if (DanmakuAreaValue != null) DanmakuAreaValue.Text = pct + "%";
        // 持久化到全局设置(设置页与播放器双向同步)
        Svc.Settings.UpdateDanmakuSettings(Svc.Settings.DanmakuEnabled, pct, Svc.Settings.DanmakuSmartFilter);
    }

    // ------------------------------------------------------------ 视频区边缘缩放

    /// <summary>缩放带宽度(DIP), 与 XAML 里 WindowChrome 的 ResizeBorderThickness=6 一致</summary>
    private const double ResizeEdgeDip = 6;

    private const int WmNcLButtonDown = 0x00A1;

    // Win32 的 HT*(10~17), 给 DefWindowProc 判定缩放方向
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    /// <summary>
    /// 视频区按下左键时先走这里: 指针在缩放带里就发起系统缩放并返回 true,
    /// 调用方要吞掉这次按下 —— 它是"拖边缘", 不能顺带当成视频区手势(播放/暂停等)。
    ///
    /// 视频区把窗口的左/右/下边缘盖住了, WindowChrome 的缩放边框够不着, 这里手动补上"拖边缘改大小"。
    ///
    /// ★ 根因(2026-10-05 修"铺满窗口时窗口边缘无法拖动缩放"):
    ///   窗口是 WindowStyle=None + WindowChrome(ResizeBorderThickness=6), 那 6px 缩放带的
    ///   命中测试发生在 **PlayerWindow** 的窗口过程上。而视频区被 LibVLC 的两层 HWND 盖着:
    ///   ① VideoView 的原生视频子窗口; ② VideoView 的 ForegroundWindow(弹幕/控制栏所在的
    ///   透明浮动顶层窗口, 见 DetachVideoOverlay 的说明)。鼠标压在这两层上时消息根本到不了
    ///   PlayerWindow, 缩放带形同虚设 —— 平时只有右侧信息栏那一边是纯 WPF 还能拖, 「铺满窗口」
    ///   把信息栏收掉后右边缘也被视频区盖住, 于是"整个窗口边缘都拖不动"。
    ///
    /// ★ 修法: 视频区左键按下时, 指针落在哪条缩放带就替系统发起哪个方向的缩放 ——
    ///   ReleaseCapture() 之后**直接调 DefWindowProc**(WM_NCLBUTTONDOWN + 对应 HT*)。
    ///   ★★ 必须"直接调 DefWindowProc", 不要改成 SendMessage 把消息发回主窗口:
    ///   实测(真窗口探针 .probes/bd-probe-edgeresize, 2026-10-05)合成的 WM_NCLBUTTONDOWN
    ///   会被 WPF/WindowChrome 的消息链吃掉, 根本走不到 DefWindowProc, 表现是"点了毫无反应";
    ///   直接调则与原生拖边缘完全同一条路(ENTER → SIZING… → EXITSIZE, FluentWindow 里
    ///   CardWall.NotifyModalResize 照常收到)。实测拖 120px: 窗口宽 1000 → 1120。
    /// </summary>
    private bool TryBeginEdgeResize()
    {
        if (_closing) return false;
        // 全屏/最大化时没有"改窗口大小"这回事, 交给原有手势
        if (_isFullscreen || WindowState != WindowState.Normal) return false;

        var ht = HitTestResizeEdge();
        if (ht == 0) return false;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return false;

        // 手势状态全部作废: 缩放循环会把松开事件吃掉, 挂起的长按/单击不能再触发
        _singleClickTimer?.Stop();
        _longPressTimer?.Stop();
        _suppressSingleClick = true;

        // 先放掉 WPF 持有的鼠标捕获, 否则模态缩放循环拿不到鼠标
        ReleaseCapture();
        // 直接把"按在缩放带上"交给 DefWindowProc: 它进入系统模态缩放循环, 松开鼠标才返回
        DefWindowProc(hwnd, WmNcLButtonDown, (IntPtr)ht, IntPtr.Zero);
        return true;
    }

    /// <summary>
    /// 指针现在落在窗口四边/四角的哪条缩放带上, 返回 HT*(0 = 不在缩放带)。
    /// 用 Win32 的屏幕物理像素(GetCursorPos + GetWindowRect)而不是 WPF 坐标:
    /// 要判定的是**窗口**的边缘, 而事件来自另一层浮动窗口, 坐标原点/DPI 换算都不好拿。
    /// </summary>
    private int HitTestResizeEdge()
    {
        if (!GetCursorPos(out var pt)) return 0;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return 0;

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var border = Math.Max(2, (int)Math.Ceiling(ResizeEdgeDip * scale));

        var left = pt.X - r.Left < border;
        var right = r.Right - pt.X <= border;
        var top = pt.Y - r.Top < border;
        var bottom = r.Bottom - pt.Y <= border;

        if (top && left) return HtTopLeft;
        if (top && right) return HtTopRight;
        if (bottom && left) return HtBottomLeft;
        if (bottom && right) return HtBottomRight;
        if (left) return HtLeft;
        if (right) return HtRight;
        if (top) return HtTop;
        if (bottom) return HtBottom;
        return 0;
    }

    /// <summary>
    /// 鼠标在视频区移动时, 落在缩放带上就把指针换成对应的方向箭头 ——
    /// 原生缩放带会自动换指针, 但这里事件在浮动窗口里, 不给反馈就没人知道边缘能拖。
    /// (不在缩放带要换回普通箭头: 从边缘移回来时得变回去。)
    /// </summary>
    private void UpdateVideoAreaResizeCursor()
    {
        if (_closing || VideoAreaRoot == null) return;
        if (_isFullscreen || WindowState != WindowState.Normal) return;

        VideoAreaRoot.Cursor = HitTestResizeEdge() switch
        {
            HtLeft or HtRight => Cursors.SizeWE,
            HtTop or HtBottom => Cursors.SizeNS,
            HtTopLeft or HtBottomRight => Cursors.SizeNWSE,
            HtTopRight or HtBottomLeft => Cursors.SizeNESW,
            _ => Cursors.Arrow,
        };
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint pt);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    // ------------------------------------------------------------ 视频区手势

    /// <summary>
    /// 视频区左键按下。
    /// 手势优先级: 双击(全屏) > 长按(2x 快进) > 单击(播放/暂停)。
    ///
    /// WPF 的 ClickCount 在 Down 阶段就已经是 2, 但 Up 阶段同样是 2,
    /// 所以"双击"必须在 Down 里判定并置抑制标记, 让后面两次 Up 都不再排队单击动作。
    /// </summary>
    private void OnVideoAreaMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        // 落在窗口边缘缩放带里的按下是"拖边缘改大小", 不算视频区手势(见 TryBeginEdgeResize)
        if (TryBeginEdgeResize()) { e.Handled = true; return; }
        if (_mp == null) return;
        // 点视频区会把焦点交给那层浮动覆盖窗口/原生视频窗口, 顺手把键盘焦点收回 WPF 树
        RestoreKeyboardFocus();
        // 点击控制栏(播放/清晰度/弹幕区域等)不作为视频区手势, 否则会误触发"单击暂停"
        if (ControlBar != null && ControlBar.IsMouseOver) return;
        // 跳过提示条上的点击(「撤回」按钮)同样不算视频区手势 ——
        // 它是覆盖层里除了控制栏之外唯一会吃掉左键的自绘元素, 不挡住就会"点撤回顺带切一次播放/暂停"。
        if (SkipNoticeBorder != null && SkipNoticeBorder.IsMouseOver) return;

        // 双击: 立即全屏, 取消所有挂起的单击/长按动作, 并抑制后续 Up 的单击排队
        if (e.ClickCount >= 2)
        {
            _suppressSingleClick = true;
            _singleClickTimer?.Stop();
            _longPressTimer?.Stop();
            _longPressFired = false;
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        // 单击: 启动长按计时器, 单击的"播放/暂停"在 MouseUp 时延迟触发
        _suppressSingleClick = false;
        _longPressFired = false;
        _longPressTimer?.Stop();
        _longPressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _longPressTimer.Tick += (_, _) =>
        {
            _longPressTimer!.Stop();
            _longPressFired = true;
            // 进入 2x 快进
            try { _mp.SetRate(FastForwardRate); } catch { }
            if (SpeedText != null) SpeedText.Text = FastForwardRate.ToString("0.#") + "x";
            // 长按触发了, 不再触发单击
            _singleClickTimer?.Stop();
        };
        _longPressTimer.Start();
    }

    /// <summary>视频区左键松开: 若是长按 → 恢复 1x; 否则延迟触发播放/暂停(给双击留窗口)</summary>
    private void OnVideoAreaMouseLeftUp(object sender, MouseButtonEventArgs e)
    {
        // 与 Down 对称: 控制栏上的点击不触发单击暂停
        if (ControlBar != null && ControlBar.IsMouseOver) return;
        // 与 Down 对称: 跳过提示条上的「撤回」点击也不触发单击暂停
        if (SkipNoticeBorder != null && SkipNoticeBorder.IsMouseOver) return;

        _longPressTimer?.Stop();

        // 双击已被 Down 处理过(全屏), 这里直接吞掉, 绝不能再排一次播放/暂停
        if (_suppressSingleClick)
        {
            _suppressSingleClick = false;
            e.Handled = true;
            return;
        }

        // 长按结束: 恢复 1x
        if (_longPressFired)
        {
            _longPressFired = false;
            try { _mp.SetRate(1.0f); } catch { }
            if (SpeedText != null) SpeedText.Text = "1.0x";
            e.Handled = true;
            return;
        }

        // 单击延迟触发: 等一个双击判定窗口再执行播放/暂停。
        // 若这期间来了第二次点击, Down 里的 ClickCount>=2 分支会置 _suppressSingleClick
        // 并停掉本定时器, 因此双击不会同时触发暂停。
        _singleClickTimer?.Stop();
        _singleClickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _singleClickTimer.Tick += (_, _) =>
        {
            _singleClickTimer!.Stop();
            if (_suppressSingleClick) return;
            OnPlayPauseClick(this, new RoutedEventArgs());
        };
        _singleClickTimer.Start();
    }

    /// <summary>鼠标离开视频区: 兜底, 防止按住时鼠标移出导致状态卡住</summary>
    private void OnVideoAreaMouseLeave(object sender, MouseEventArgs e)
    {
        _longPressTimer?.Stop();
        _singleClickTimer?.Stop();
        _suppressSingleClick = false;
        if (_longPressFired)
        {
            _longPressFired = false;
            try { _mp.SetRate(1.0f); } catch { }
            if (SpeedText != null) SpeedText.Text = "1.0x";
        }
        // 离开视频区: 播放中直接藏(倒计时都不用等), 暂停时保持常显
        if (ShouldHideControls()) HideControls();
    }

    // ------------------------------------------------------------ 控制栏自动隐藏

    /// <summary>控制栏从有鼠标活动到自动隐藏的等待时长</summary>
    private static readonly TimeSpan ControlsHideDelay = TimeSpan.FromMilliseconds(2400);
    private DispatcherTimer? _controlsHideTimer;
    private bool _controlsHidden;

    /// <summary>
    /// 视频区有鼠标移动 → 立即显示控制栏并重置倒计时。
    /// ControlBar 就挂在这个视频区 Grid 里(同一个 ForegroundWindow 浮层), 所以鼠标压在
    /// 控制栏上时 MouseMove 也会一路冒泡到这里 —— 不会被自己藏掉。
    /// </summary>
    private void OnVideoAreaMouseMove(object sender, MouseEventArgs e)
    {
        if (_controlsHidden) ShowControls();
        if (ShouldHideControls()) StartControlsHideCountdown();
        else _controlsHideTimer?.Stop();
        // 靠近窗口边缘时把指针换成缩放箭头(见 UpdateVideoAreaResizeCursor)
        UpdateVideoAreaResizeCursor();
    }

    /// <summary>现在是否处于"应该隐藏控制栏"的状态: 只在**播放中**自动隐藏。</summary>
    private bool ShouldHideControls()
    {
        if (_closing || _mp == null || !_mp.IsPlaying) return false;
        if (QualityPopup != null && QualityPopup.IsOpen) return false;
        if (DanmakuAreaPopup != null && DanmakuAreaPopup.IsOpen) return false;
        if (_isSeeking) return false;                       // 拖进度条时别藏
        if (ControlBar.IsMouseOver) return false;           // 鼠标停在控制栏上(即使不动)也别藏
        if (LoadingOverlay != null && LoadingOverlay.Visibility == Visibility.Visible) return false;
        return true;
    }

    private void StartControlsHideCountdown()
    {
        if (!ShouldHideControls()) { _controlsHideTimer?.Stop(); return; }
        if (_controlsHideTimer == null)
        {
            _controlsHideTimer = new DispatcherTimer { Interval = ControlsHideDelay };
            _controlsHideTimer.Tick += (_, _) =>
            {
                _controlsHideTimer.Stop();
                // Tick 时状态可能已变(暂停/弹层开了): 不满足就再等下一轮鼠标活动
                if (ShouldHideControls()) HideControls();
            };
        }
        _controlsHideTimer.Stop();
        _controlsHideTimer.Start();
    }

    private void HideControls()
    {
        if (_controlsHidden || _closing) return;
        _controlsHidden = true;
        _controlsHideTimer?.Stop();
        ControlBar.IsHitTestVisible = false;   // 隐形时不能再吃视频区的点击/滚轮
        VideoAreaRoot.Cursor = System.Windows.Input.Cursors.None;
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0,
            TimeSpan.FromMilliseconds(180))
        { FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop };
        ControlBar.BeginAnimation(OpacityProperty, fade);
        ControlBar.Opacity = 0;
    }

    private void ShowControls()
    {
        _controlsHideTimer?.Stop();
        if (!_controlsHidden)
        {
            // 没藏着也要确保可见(动画/其它路径可能把透明度留成中间值)
            if (ControlBar.Opacity < 1) { ControlBar.BeginAnimation(OpacityProperty, null); ControlBar.Opacity = 1; }
            ControlBar.IsHitTestVisible = true;
            VideoAreaRoot.Cursor = System.Windows.Input.Cursors.Arrow;
            return;
        }
        _controlsHidden = false;
        VideoAreaRoot.Cursor = System.Windows.Input.Cursors.Arrow;
        ControlBar.BeginAnimation(OpacityProperty, null);
        ControlBar.Opacity = 1;
        ControlBar.IsHitTestVisible = true;
    }

    // ------------------------------------------------------------ 缓存当前视频

    private void OnCacheClick(object sender, RoutedEventArgs e)
    {
        if (_closing || string.IsNullOrEmpty(_currentBvid)) return;
        // 与卡片右键"下载视频"同一条链路: 弹独立的下载进度对话框
        _ = VideoDownloader.DownloadAsync(_currentBvid, VideoTitleText.Text, this);
    }

    // ------------------------------------------------------------ 弹幕

    // --- 弹幕轨道状态(消除重叠的核心) ---
    //
    // 旧实现每条弹幕都 Rand.Next 取一个纵坐标, 弹幕一多必然层层叠在一起。
    // 现在把显示区域切成固定高度的"泳道"(lane), 每条滚动弹幕占据一条泳道;
    // 分配时选择"最早可用"的泳道, 并校验与同泳道前一条弹幕的间距,
    // 保证同一泳道里后一条不会追上(追尾)前一条。
    private double _laneHeight = 28;               // 单条泳道高度(随字号联动)
    private double[]? _laneFreeAt;                 // 每条泳道的"可用时间点"(秒, 播放时间轴)
    private double[]? _laneLastWidth;              // 每条泳道最后一条弹幕的宽度
    private double _lastLaneCalcWidth;             // 上次计算泳道时的画布宽度(尺寸变化时重算)
    private double _lastLaneCalcHeight;

    // 滚动弹幕在屏幕上横穿的时长(秒)。B 站惯用 8 秒, 弹幕密集时略短可提升吞吐。
    private const double DanmakuScrollSeconds = 8.0;
    // 屏幕上的滚动弹幕上限(内存/渲染保护)
    private const int MaxOnScreenDanmaku = 80;
    // 顶部/底部固定弹幕的停留时长
    private const double DanmakuFixedSeconds = 4.0;

    // 顶部/底部固定弹幕的占用时间表(避免固定在同一个位置互相压住)
    private readonly List<(double until, int slot)> _topSlots = new();
    private readonly List<(double until, int slot)> _bottomSlots = new();

    /// <summary>
    /// 判定"弹幕游标"与播放位置之间有多大的时间错位才算失配(秒)。
    ///
    /// 取 1.5: 正常播放时游标最多落后一个心跳(TimeChanged 通常 0.25~0.5s 一跳),
    /// 远小于这个阈值, 所以绝不会误判成 seek; 而真实 seek 的错位是"几秒到几分钟"量级。
    /// </summary>
    private const double DanmakuCursorToleranceSec = 1.5;

    /// <summary>
    /// 游标是否已与播放位置失配 —— 失配了就该重新二分定位。
    ///
    /// 游标 <c>_indexInLib</c> 的不变式是"指向第一条还没发出去的弹幕", 正常播放时它总是
    /// 满足 last &lt;= current &lt;= next。失配只有两种, 都发生在位置突变之后:
    ///   · <c>next</c> 在很远的地方 ⇒ 用户**往回**跳了;
    ///   · <c>last</c> 在很远的地方 ⇒ 用户**往前**跳了。
    ///
    /// ★ 后半条以前是漏的(旧代码只判了 next), 而它恰好是会出事的那个: 往前跳之后,
    ///   游标还指着跳转前那一条, 它连同后面**整段**弹幕都满足 time &lt;= current,
    ///   DispatchDanmaku 的 while 循环会在一帧里全部发出来 —— 几百条同屏 = 糊屏 + 卡顿。
    ///   同一个坑还有第二个触发口: 弹幕是异步加载的, 等它回来时播放已经过去一段时间,
    ///   而 _indexInLib 刚被置 0, 于是从头到当前时刻的弹幕一次性全发。
    ///
    /// 纯函数(不碰实例状态), 探针/单测可以直接喂几组三元组断言, 不必起播放器。
    /// </summary>
    private static bool IsDanmakuCursorStale(double currentSeconds, double lastSentTime, double nextTime)
        => lastSentTime > currentSeconds + DanmakuCursorToleranceSec
        || nextTime < currentSeconds - DanmakuCursorToleranceSec;

    private async Task LoadDanmakuAsync(long cid)
    {
        if (cid <= 0) return;
        try
        {
            // 传视频总时长, 让接口只拉真正需要的分段(6 分钟一段), 长视频不再白拉几十个请求。
            // 条数上限 6000: 覆盖绝大多数视频的完整弹幕量, 同时不至于让内存/排序压力过大。
            var durationSec = _totalMs > 0 ? (int)(_totalMs / 1000) : 0;
            var list = await Svc.Api.GetDanmakuAsync(cid, durationSec, 6000);
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closing) return;
                // 原始列表按时间排好序存下来, 供后续"改屏蔽词立即重算"使用
                _danmakuRaw.Clear();
                _danmakuRaw.AddRange(list.OrderBy(d => d.Time));

                _danmaku.Clear();
                _danmaku.AddRange(ApplyDanmakuFilter(_danmakuRaw));
                _indexInLib = 0;
                ResetLanes();
            });
        }
        catch { /* 静默 */ }
    }

    /// <summary>
    /// 用当前的屏蔽设置重新过滤一遍已加载的弹幕(设置页改动时调用)。
    /// 不重新请求接口, 只重算内存里的列表; 会尽量保持当前播放位置附近的索引,
    /// 避免重算后弹幕"从头再刷一遍"或者整段卡住。
    /// </summary>
    private void RefilterDanmaku()
    {
        if (_closing || _danmakuRaw.Count == 0) return;

        var currentSeconds = _mp != null ? _mp.Time / 1000.0 : 0;

        _danmaku.Clear();
        _danmaku.AddRange(ApplyDanmakuFilter(_danmakuRaw));

        // 重算索引: 让游标落在"当前时间之后的第一条", 这样接下来的弹幕会正常滚出来,
        // 而不是把已经播过的部分再补一遍。
        _indexInLib = LowerBound(_danmaku, currentSeconds);
        ResetLanes();
        DanmakuCanvas.Children.Clear();
    }

    /// <summary>
    /// 弹幕过滤。
    /// 智能屏蔽开启时: 过滤超长弹幕 + 相同文本的刷屏(只留前 N 条) + 纯符号/空白;
    /// 关闭时只做最基本的安全过滤(去空白), 其余全部保留。
    /// 关键词屏蔽**独立生效**: 无论智能屏蔽是否开启, 用户手写的黑名单都会被应用
    /// (用户明确写了"不想看到"的词, 优先级高于"要不要自动净化"这个总开关)。
    /// </summary>
    private static IEnumerable<RawDanmaku> ApplyDanmakuFilter(IEnumerable<RawDanmaku> list)
    {
        var s = Svc.Settings;
        var smart = s.DanmakuSmartFilter;
        var rules = DanmakuKeywordFilter.Build(s.DanmakuBlockKeywords);

        // 类型过滤(滚动/固定/彩色/高级): 四项全开 = 不过滤。
        // ★ 用"是否全开"而不是"是否有任一项关闭"来判断能不能跳过 —— 后者写反过一次,
        //   表现是默认状态下弹幕全没了。
        var typeFilter = !(s.DanmakuFilterScroll && s.DanmakuFilterFixed
                           && s.DanmakuFilterColorful && s.DanmakuFilterAdvanced);

        // 三件事都没开就原样返回, 省掉一次完整遍历
        if (!smart && rules == null && !typeFilter) return list;
        return FilterCore(list, smart, rules, typeFilter,
            s.DanmakuFilterScroll, s.DanmakuFilterFixed,
            s.DanmakuFilterColorful, s.DanmakuFilterAdvanced);

        static IEnumerable<RawDanmaku> FilterCore(
            IEnumerable<RawDanmaku> src,
            bool smart,
            DanmakuKeywordFilter? rules,
            bool typeFilter,
            bool allowScroll,
            bool allowFixed,
            bool allowColorful,
            bool allowAdvanced)
        {
            var seen = new Dictionary<string, int>();
            foreach (var d in src)
            {
                if (string.IsNullOrWhiteSpace(d.Text)) continue;

                // 类型过滤: 用户没勾的类型直接丢弃(与智能屏蔽/关键词都解耦)
                if (typeFilter && !IsTypeAllowed(d, allowScroll, allowFixed, allowColorful, allowAdvanced))
                    continue;

                // 关键词/正则黑名单: 命中即丢弃(与智能屏蔽解耦)
                if (rules != null && rules.IsBlocked(d.Text)) continue;

                if (smart)
                {
                    // 超长弹幕(>30 字)会占满一整条泳道, 智能屏蔽下直接丢弃
                    if (d.Text.Length > 30) continue;
                    // 相同文本刷屏: 最多保留 4 条, 再多就是复读机
                    seen.TryGetValue(d.Text, out var n);
                    if (n >= 4) continue;
                    seen[d.Text] = n + 1;
                }
                yield return d;
            }
        }
    }

    /// <summary>
    /// 这条弹幕是否在用户勾选的类型里。
    ///
    /// 类型划分(与 B 站客户端的分类口径一致):
    ///   · 滚动 = mode 1/2/3/6(6 在解析时已被归一成 1)
    ///   · 固定 = mode 4(底部)/5(顶部)
    ///   · 高级 = mode 7(高级)/8(代码)/9(BAS) —— 这类会自己画图形/动画, 和普通弹幕不同源
    ///   · 彩色 = 颜色非白(0xFFFFFF)。它与"彩色弹幕"显示开关是两件事:
    ///     那个决定"彩色要不要按原色画", 这个决定"彩色要不要出现"。
    ///
    /// ★ 四个维度是**或**的关系: 只要命中的类型里有任意一项被允许就放行。
    ///   一条弹幕可能同时是"滚动 + 彩色", 关掉滚动但留着彩色时它应该还在。
    /// </summary>
    private static bool IsTypeAllowed(RawDanmaku d,
        bool allowScroll, bool allowFixed, bool allowColorful, bool allowAdvanced)
    {
        var colorful = d.Color != 0xFFFFFF && d.Color != 0;

        // 滚动类(1/2/3)与高级类(7/8/9)之外的 mode 都算固定(4/5), 未知 mode 也归固定 ——
        // 宁可多显示一条, 也别让接口新增的 mode 变成"凭空消失的弹幕"
        switch (d.Mode)
        {
            case 1: case 2: case 3: return allowScroll || (colorful && allowColorful);
            case 7: case 8: case 9: return allowAdvanced || (colorful && allowColorful);
            default: return allowFixed || (colorful && allowColorful);
        }
    }

    private void DispatchDanmaku()
    {
        if (!_danmakuOn || _closing || _mp == null || _mp.Length <= 0) return;
        var currentSeconds = _mp.Time / 1000.0;

        // 游标与播放位置必须同步, 否则下面的 while 会在一帧里把跨越的整段弹幕全发出来(刷屏)。
        // 往回跳、往前跳、以及"弹幕加载完成时播放已经过去一段时间"这三种失配都在这里收敛,
        // 判据见 IsDanmakuCursorStale 的说明。边界值用 ±∞ 兜住"还没有上一条 / 已经没有下一条"。
        var lastSentTime = _indexInLib > 0 ? _danmaku[_indexInLib - 1].Time : double.NegativeInfinity;
        var nextTime = _indexInLib < _danmaku.Count ? _danmaku[_indexInLib].Time : double.PositiveInfinity;
        if (IsDanmakuCursorStale(currentSeconds, lastSentTime, nextTime))
        {
            _indexInLib = LowerBound(_danmaku, currentSeconds);
            ResetLanes();
        }

        while (_indexInLib < _danmaku.Count && _danmaku[_indexInLib].Time <= currentSeconds)
        {
            var item = _danmaku[_indexInLib];
            AddDanmaku(item.Text, item.Mode, item.Time, item.Color);
            _indexInLib++;
        }
    }

    /// <summary>二分查找第一条 time >= 目标时间的弹幕下标</summary>
    private static int LowerBound(List<RawDanmaku> list, double target)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (list[mid].Time < target) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>画布尺寸变化或跳转后重置泳道占用状态</summary>
    private void ResetLanes()
    {
        _topSlots.Clear();
        _bottomSlots.Clear();
        _laneFreeAt = null;      // 下次渲染时按新尺寸重建
        _laneLastWidth = null;   // 与 _laneFreeAt 同生共死, 避免留下长度不匹配的残影数据
        _lastLaneCalcWidth = 0;
        _lastLaneCalcHeight = 0;
    }

    /// <summary>
    /// 按当前画布尺寸重建泳道数组。
    /// 泳道数 = 显示区域高度 / 单条弹幕高度。字号固定 18px, 加上上下 padding
    /// 约 28px 一条, 所以一条 1080P 视频的 25% 区域大约能容纳 8~10 条泳道。
    /// </summary>
    private void EnsureLanes()
    {
        var w = DanmakuCanvas.ActualWidth;
        var h = DanmakuCanvas.ActualHeight;
        if (h <= 0) return;

        // 尺寸没变则复用现有泳道, 保留占用状态(避免每帧重建导致弹幕重叠)
        if (_laneFreeAt != null && Math.Abs(w - _lastLaneCalcWidth) < 1 &&
            Math.Abs(h - _lastLaneCalcHeight) < 1)
            return;

        var areaHeight = h * _danmakuAreaRatio;
        var laneCount = Math.Max(1, (int)(areaHeight / _laneHeight));

        _laneFreeAt = new double[laneCount];
        _laneLastWidth = new double[laneCount];
        _lastLaneCalcWidth = w;
        _lastLaneCalcHeight = h;

        // 新泳道全部立即可用
        for (var i = 0; i < laneCount; i++) _laneFreeAt[i] = 0;
    }

    /// <summary>
    /// 计算一条弹幕应该落在哪条泳道。
    ///
    /// 判定逻辑(参考主流弹幕实现):
    ///   1. 优先选"当前时间点上, 上一条弹幕已经完全离开画面右侧"的泳道;
    ///   2. 否则选"上一条弹幕虽然还在屏幕上, 但按速度算不会被后一条追上"的泳道;
    ///   3. 都不满足则返回 -1, 表示这条弹幕暂时没有位置 —— 直接丢弃(不叠在一起)。
    ///
    /// 追尾校验: 前一条弹幕在屏幕上占据 [x1, x1+w1], 后一条从右边缘 x=W 出发。
    /// 两者速度相同(都在 DanmakuScrollSeconds 内走完 W+wi), 所以只要保证
    /// 后一条出发时前一条的右边缘已经离开足够距离, 就永远不会追上。
    /// </summary>
    private int AllocateRollingLane(double width, double currentSeconds)
    {
        EnsureLanes();
        if (_laneFreeAt == null || _laneFreeAt.Length == 0) return -1;

        var canvasWidth = DanmakuCanvas.ActualWidth;
        if (canvasWidth <= 0) return -1;

        // 弹幕从左(或右)走到完全离开所需时间 = DanmakuScrollSeconds
        // 速度(像素/秒) = (canvasWidth + width) / DanmakuScrollSeconds
        var speed = (canvasWidth + width) / DanmakuScrollSeconds;

        var bestLane = -1;
        var bestFreeAt = double.MaxValue;

        for (var i = 0; i < _laneFreeAt.Length; i++)
        {
            var freeAt = _laneFreeAt[i];

            if (freeAt <= currentSeconds)
            {
                // 该泳道在"当前时刻"已经是空的 —— 立刻用它, 后面不用再看了。
                // 注意: 这里**不能**加 "freeAt >= bestFreeAt 就 continue" 这类提前剪枝:
                // 空泳道之间无从比较优劣(都是空的), 剪枝只会让本该复用的泳道被判成不可用,
                // 结果是弹幕被无谓丢弃(表现为"弹幕变稀了")。
                bestLane = i;
                bestFreeAt = freeAt;
                break;
            }

            // 泳道仍在占用中: 算一下前一条弹幕此刻的左边缘是否已经滚出足够距离。
            // 只有"当前候选中 freeAt 最早(最先腾空)"的泳道才值得继续评估,
            // 这样一趟循环就能挑出"最该复用的那条"。
            if (freeAt >= bestFreeAt) continue;

            // remaining: 前一条还要占住右侧入口多久
            var remaining = freeAt - currentSeconds;
            // 前一条弹幕此刻的左边缘位置(近似: 按自身速度反推)
            var prevWidth = _laneLastWidth != null && i < _laneLastWidth.Length ? _laneLastWidth[i] : width;
            var prevSpeed = (canvasWidth + prevWidth) / DanmakuScrollSeconds;
            var prevElapsed = DanmakuScrollSeconds - remaining;
            var prevLeft = canvasWidth - prevElapsed * prevSpeed;
            // 后一条从 canvasWidth 出发; 只要前一条的右边缘(prevLeft + prevWidth) 与
            // 后一条的左边缘(canvasWidth) 之间留有安全间距, 就不会视觉重叠
            if (prevLeft + prevWidth + 12 <= canvasWidth)
            {
                bestLane = i;
                bestFreeAt = freeAt;
            }
        }

        return bestLane;
    }

    /// <summary>分配顶部/底部固定弹幕的位置(轮转, 避免连续多条压在同一行)</summary>
    private int AllocateFixedSlot(List<(double until, int slot)> table, double currentSeconds, int slotCount)
    {
        // 清掉已过期的占用
        table.RemoveAll(x => x.until <= currentSeconds);

        for (var s = 0; s < slotCount; s++)
        {
            if (table.All(x => x.slot != s))
            {
                table.Add((currentSeconds + DanmakuFixedSeconds, s));
                return s;
            }
        }
        return -1; // 顶/底位置全满, 丢弃
    }

    /// <summary>
    /// 渲染一条弹幕。
    /// mode: 1/2/3 滚动(从右往左), 4 底部固定, 5 顶部固定。
    /// 其余少见模式(BAS/代码弹幕)统一按滚动处理, 避免出现空白占位。
    ///
    /// 性能上两条铁律(违反任何一条, 高弹幕量视频就会掉帧):
    ///   1. **不挂 Effect**: 每个带 DropShadowEffect 的元素都要独立做一次位图合成,
    ///      几十条弹幕同屏就是几十次全像素处理 —— 描边感交给半透明黑底衬(下面那个
    ///      Border)来给, 效果相近但零额外开销;
    ///   2. **动画只动 RenderTransform**: 动 Canvas.Left 每帧都要走一遍 Canvas 布局,
    ///      动 TranslateTransform 只发生在合成线程的变换矩阵上, 完全不碰布局。
    /// </summary>
    private void AddDanmaku(string text, int mode, double time, uint color = 0xFFFFFF)
    {
        if (DanmakuCanvas == null || string.IsNullOrEmpty(text)) return;
        if (DanmakuCanvas.ActualWidth <= 0 || DanmakuCanvas.ActualHeight <= 0) return;
        // 屏幕弹幕上限, 防止高弹幕量视频把内存和渲染拖垮
        if (DanmakuCanvas.Children.Count >= MaxOnScreenDanmaku) return;

        var currentSeconds = _mp?.Time / 1000.0 ?? time;

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1, 6, 1),
            // RenderTransform 在这里预置好, 滚动弹幕直接拿它做位移动画
            RenderTransform = new TranslateTransform(0, 0),
            Child = new TextBlock
            {
                Text = text,
                Foreground = DanmakuBrush(color),
                FontSize = 18
            }
        };

        DanmakuCanvas.Children.Add(border);
        border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Max(1, border.DesiredSize.Width);

        if (mode == 5 || mode == 4)
        {
            RenderFixed(border, mode, currentSeconds);
        }
        else
        {
            RenderRolling(border, width, currentSeconds);
        }
    }

    // ------------------------------------------------------------ 彩色弹幕

    /// <summary>白色笔刷只建一次(绝大多数弹幕都是白的, 不必每条都 new)</summary>
    private static readonly Brush DanmakuWhiteBrush = MakeFrozen(Colors.White);

    /// <summary>
    /// 颜色 → 笔刷的缓存。
    ///
    /// 为什么必须缓存: 一个热门视频几千条弹幕, 每条都 new SolidColorBrush 会产生几千个
    /// 笔刷对象(还都要走一遍 Freeze), 而实际用到的颜色通常只有几十种 ——
    /// 命中缓存的成本接近零, 也顺带避免了大量小对象给 GC 添麻烦。
    /// 用普通 Dictionary 即可: 只在 UI 线程读写, 且颜色种类天然有限。
    /// </summary>
    private readonly Dictionary<uint, Brush> _danmakuBrushes = new();

    /// <summary>
    /// 取弹幕文字色。
    /// 0xRRGGBB 是接口原样给的十进制颜色(16777215 = 白) —— 白就是默认色,
    /// 直接复用共享笔刷; 非白即"彩色弹幕", 按值上色。
    /// 关掉"彩色弹幕"设置时一律白: 有些视频满屏五颜六色反而看不清内容。
    /// </summary>
    private Brush DanmakuBrush(uint color)
    {
        if (!_danmakuColorful || color == 0xFFFFFF || color == 0) return DanmakuWhiteBrush;

        if (_danmakuBrushes.TryGetValue(color, out var cached)) return cached;

        var brush = MakeFrozen(Color.FromRgb(
            (byte)((color >> 16) & 0xFF),
            (byte)((color >> 8) & 0xFF),
            (byte)(color & 0xFF)));
        _danmakuBrushes[color] = brush;
        return brush;
    }

    private static Brush MakeFrozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>滚动弹幕: 分泳道 + 横向滚动(动 TranslateTransform, 不动布局)</summary>
    private void RenderRolling(Border border, double width, double currentSeconds)
    {
        var lane = AllocateRollingLane(width, currentSeconds);
        if (lane < 0)
        {
            // 没有可用泳道 —— 宁可丢弃也不叠在一起(这正是"大量重叠"的根治点)
            DanmakuCanvas.Children.Remove(border);
            return;
        }

        // 记录该泳道的占用信息, 供后续追尾校验使用
        _laneFreeAt![lane] = currentSeconds + DanmakuScrollSeconds;
        if (_laneLastWidth != null) _laneLastWidth[lane] = width;

        Canvas.SetTop(border, 6 + lane * _laneHeight);

        var canvasWidth = DanmakuCanvas.ActualWidth;
        // 从画面右侧外进场, 走到完全离开左侧为止; 位移全部落在 TranslateTransform 上。
        // 动画 From 必须写 canvasWidth —— BeginAnimation 接管属性后, 动画当前值(即 From)
        // 会立刻覆盖上面预置的初始位置; 写成 From=0 弹幕第一帧就跳到左边缘再往左滑出
        // (表现成"弹幕靠左出现直接消失")。To=-width 恰好是弹幕右边缘完全离开左侧的位置。
        border.RenderTransform = new TranslateTransform(canvasWidth, 0);
        var translate = (TranslateTransform)border.RenderTransform;
        var anim = new DoubleAnimation(canvasWidth, -width, TimeSpan.FromSeconds(DanmakuScrollSeconds))
        {
            // 匀速: 弹幕必须线性运动, 缓动会看起来忽快忽慢
            EasingFunction = null
        };
        anim.Completed += (_, _) => DanmakuCanvas.Children.Remove(border);
        translate.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    /// <summary>顶部/底部固定弹幕: 居中停留数秒后淡出</summary>
    private void RenderFixed(Border border, int mode, double currentSeconds)
    {
        var canvasWidth = DanmakuCanvas.ActualWidth;
        var canvasHeight = DanmakuCanvas.ActualHeight * _danmakuAreaRatio;

        var slotCount = Math.Max(1, (int)(canvasHeight / _laneHeight));
        var table = mode == 5 ? _topSlots : _bottomSlots;
        var slot = AllocateFixedSlot(table, currentSeconds, slotCount);
        if (slot < 0)
        {
            DanmakuCanvas.Children.Remove(border);
            return;
        }

        border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = border.DesiredSize.Width;
        Canvas.SetLeft(border, Math.Max(0, (canvasWidth - width) / 2));

        // 顶部从上往下排, 底部从下往上排
        var top = mode == 5
            ? 6 + slot * _laneHeight
            : Math.Max(6, canvasHeight - 6 - (slot + 1) * _laneHeight);
        Canvas.SetTop(border, top);

        // 停留 DanmakuFixedSeconds 后淡出并移除
        var hold = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(DanmakuFixedSeconds))
        {
            BeginTime = TimeSpan.FromSeconds(Math.Max(0, DanmakuFixedSeconds - 0.6))
        };
        hold.Completed += (_, _) => DanmakuCanvas.Children.Remove(border);
        border.BeginAnimation(OpacityProperty, hold);
    }

    /// <summary>切换弹幕开关(控制栏按钮 / 快捷键 D), 并同步到全局设置</summary>
    private void ToggleDanmaku()
    {
        _danmakuOn = !_danmakuOn;
        Svc.Settings.UpdateDanmakuSettings(_danmakuOn, Svc.Settings.DanmakuAreaPercent, Svc.Settings.DanmakuSmartFilter);
        Svc.Toast.Show(_danmakuOn ? "弹幕已开启" : "弹幕已关闭");
        if (!_danmakuOn)
        {
            DanmakuCanvas.Children.Clear();
            ResetLanes();
        }
        UpdateDanmakuButton();
    }

    /// <summary>控制栏上的弹幕开关按钮</summary>
    private void OnDanmakuToggleClick(object sender, RoutedEventArgs e) => ToggleDanmaku();

    /// <summary>
    /// 刷新弹幕按钮的文案与可用状态。
    ///
    /// 直播和本地文件没有弹幕可放(前者不加载弹幕, 后者没有弹幕文件), 按钮置灰 ——
    /// 留一个按了没反应的按钮比没有按钮更糟。
    /// 注意这里给 TextBlock 设的是**局部值** Foreground: 全局那个隐式 TextBlock 样式会压过继承值,
    /// 所以"关"态的灰字必须显式写在元素上(见 MEMORY 里那条隐式样式的坑)。
    /// </summary>
    private void UpdateDanmakuButton()
    {
        if (DanmakuBtnText == null || BtnDanmaku == null) return;

        var usable = !_isLive && !_isLocalPlayback;
        BtnDanmaku.IsEnabled = usable;
        BtnDanmaku.Opacity = usable ? 1.0 : 0.4;

        DanmakuBtnText.Text = _danmakuOn ? "弹幕开" : "弹幕关";
        // 局部值直接给刷子, 不引资源: 控制栏压在画面上, 颜色要跟主题无关(永远是白系)
        DanmakuBtnText.Foreground = new SolidColorBrush(_danmakuOn
            ? Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)   // 开: 接近纯白
            : Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)); // 关: 压暗, 一眼看出状态
    }

    /// <summary>全局弹幕设置变化(来自设置页): 同步播放器运行时状态</summary>
    private void OnDanmakuSettingsChanged()
    {
        if (_closing) return;
        _danmakuOn = Svc.Settings.DanmakuEnabled;
        // 彩色弹幕开关: 它不影响"哪些弹幕该显示", 只影响颜色 —— 但已上屏的那些是带着
        // 旧颜色的, 所以下面照样要走一遍 RefilterDanmaku 把画布清掉重画。
        _danmakuColorful = Svc.Settings.DanmakuColorful;
        UpdateDanmakuButton();
        _danmakuAreaRatio = Svc.Settings.DanmakuAreaPercent / 100.0;
        if (DanmakuAreaSlider != null) DanmakuAreaSlider.Value = Svc.Settings.DanmakuAreaPercent;
        if (DanmakuAreaText != null) DanmakuAreaText.Text = Svc.Settings.DanmakuAreaPercent + "%";
        if (DanmakuAreaValue != null) DanmakuAreaValue.Text = Svc.Settings.DanmakuAreaPercent + "%";
        // 区域占比变了 => 可用泳道数变了, 必须重建泳道, 否则弹幕会溢出到区域外
        ResetLanes();
        if (!_danmakuOn)
        {
            DanmakuCanvas.Children.Clear();
            return;
        }
        // 智能屏蔽 / 关键词屏蔽都可能刚被改过: 用新规则重算一遍已加载的弹幕,
        // 让用户在设置页边打字边看到播放器里的即时效果。
        RefilterDanmaku();
    }

    // ------------------------------------------------------------ 键盘

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 只在焦点位于"可编辑"输入框时让按键走默认路径; 只读展示框不拦截空格
        if (Keyboard.FocusedElement is TextBox { IsReadOnly: false }) return;
        switch (e.Key)
        {
            case Key.Space: OnPlayPauseClick(this, new RoutedEventArgs()); e.Handled = true; return;
            case Key.Left: SeekRelative(-5); e.Handled = true; return;
            case Key.Right: SeekRelative(5); e.Handled = true; return;
            // 音量: ↑↓ 走统一入口(顺带弹音量提示), M 在静音与 80 之间切换
            // 基数用 _volumeCache: aout 未就绪时 _mp.Volume 原生读回是 -1/旧值(见 _volumeCache 的说明)
            case Key.Up: SetVolume(_volumeCache + VolumeStep); e.Handled = true; return;
            case Key.Down: SetVolume(_volumeCache - VolumeStep); e.Handled = true; return;
            case Key.M: SetVolume(_volumeCache == 0 ? 80 : 0); e.Handled = true; return;
            case Key.F: if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.None) ToggleFullscreen(); e.Handled = true; return;
            case Key.L: OnLikeClick(this, new RoutedEventArgs()); e.Handled = true; return;
            case Key.U: OnUpPageClick(this, new RoutedEventArgs()); e.Handled = true; return;
            case Key.D: ToggleDanmaku(); e.Handled = true; return;
            case Key.Escape: HandleBackRequest(); e.Handled = true; return;
        }
    }

    /// <summary>
    /// "返回上一层"的统一处理(键盘 Esc / 鼠标侧键共用)。
    ///
    /// 三层语义, **由内到外**逐级退, 与所有播放器一致:
    ///   全屏中     → 先退全屏(而不是直接关窗口, 否则用户按一下就"连界面一起没了");
    ///   已铺满窗口 → 再退铺满窗口(信息栏回来);
    ///   都没有     → 关闭播放器回到主界面。
    /// ★ 铺满窗口必须夹在中间: 它是最"内层"的一个显示模式(窗口尺寸都没变), 用户按 Esc 的
    ///   预期是"先把我刚展开的这个视图收回去", 而不是"直接关掉播放器"。
    ///   少了这一级, 用户在铺满窗口状态下按 Esc 会直接关窗 —— 与真全屏下按 Esc 的行为不一致。
    /// 两个入口必须走同一个方法, 不然很容易出现"Esc 是对的、侧键直接关窗"这种不一致。
    /// </summary>
    private void HandleBackRequest()
    {
        if (_isFullscreen)
        {
            _isFullscreen = false;
            ApplyFullscreen(false);
            return;
        }
        if (_isFillWindow)
        {
            _isFillWindow = false;
            ApplyInfoPanelState();
            UpdateFillWindowButton();
            return;
        }
        Close();
    }

    /// <summary>
    /// 鼠标侧键(XButton1/XButton2) = 返回。
    ///
    /// 为什么两个侧键都当返回: 本应用没有"前进"这种概念, 而各家的鼠标把拇指键映射成
    /// 哪个 XButton 并不统一(有的只给一个键, 有的是前/后两个), 只认 XButton1 的话
    /// 相当一部分鼠标按了没反应。既然没有前进语义可用, 统一当返回最实用。
    ///
    /// 为什么用 PreviewMouseDown: 底部的按钮/滑块会吃掉普通 MouseDown, 而隧道事件从
    /// 根往下走, 挂 Preview 就一定先经过我们这里。
    /// </summary>
    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_closing) return;
        if (e.ChangedButton != MouseButton.XButton1 && e.ChangedButton != MouseButton.XButton2) return;
        e.Handled = true;
        HandleBackRequest();
    }

    private void SeekRelative(double seconds)
    {
        if (_mp.Length <= 0) return;
        var newMs = Math.Max(0, Math.Min(_mp.Length, _mp.Time + seconds * 1000));
        _mp.Time = (long)newMs;
        MarkSeekIssued((long)newMs);
    }

    // ------------------------------------------------------------ 全屏

    /// <summary>
    /// 全屏切换。**几何变化本身依然是同步的**(按下的那一刻就把布局与窗口状态改完),
    /// 但"看得见的动画"这件事**交给 Windows/DWM 自己播** —— 见下。
    ///
    /// ★★★ 2026-10-02 按用户要求"把软件全屏动画改成 Windows 系统自带的动画, 不再自己绘制":
    ///   两条一起来的改动(缺一条都还是"自己画的"):
    ///     ① **不再关掉系统动画** —— 原来在 SourceInitialized 里调
    ///        `DwmInterop.DisableWindowTransitions`(DWMWA_TRANSITIONS_FORCEDISABLED),
    ///        把 DWM 那段窗口缩放过渡掐掉了, 所以全屏是"硬切"。该调用与其 API 已整体删除。
    ///     ② 自绘的那套过渡早已不在: 四段(渐暗压黑 60ms → 改布局 → 持黑 150ms → 揭幕 130ms,
    ///        合计 340ms, 2026-09-30 删)与 FluentWindow 的整窗 Scale+淡入(2026-10-02 删)
    ///        都不存在了。现在这个窗口一切换就是"改布局 + 最大化", 剩下的动效由 DWM 负责。
    ///
    /// ★ 那层**全屏黑幕(见 ArmFullscreenCover)必须保留** —— 它挡的是 LibVLC 重新协商 vout
    ///   时的白底空窗, 不是动画: 交给系统播之后窗口尺寸照样在变, 空窗照样出现,
    ///   去掉它浅色主题下就会重新闪白(2026-10-01 修过的老问题)。
    ///
    /// 窗口现在**始终**是 WindowStyle=None + WindowChrome 的自绘标题栏, 所以全屏不再靠
    /// 切换 WindowStyle 实现(之前那套 `None + Maximized` 会让自绘标题栏和系统框打架),
    /// 而是: 最大化 + 把顶栏那一行收成 0 高 + 藏起右侧信息栏。
    /// </summary>
    private void ToggleFullscreen()
    {
        if (_closing) return;

        _isFullscreen = !_isFullscreen;
        ApplyFullscreen(_isFullscreen);
    }

    /// <summary>全屏的开关(布局细节与顺序见 ApplyFullscreenLayout)</summary>
    private void ApplyFullscreen(bool on)
    {
        if (_closing) return;
        ApplyFullscreenLayout(on);
    }

    /// <summary>
    /// 改布局与窗口状态。**全屏唯一改动布局的地方**。
    ///
    /// 顺序仍然有讲究: 进全屏时**先把窗口底色改成黑**, 再收顶栏/信息栏、最后最大化 ——
    /// 底色先就位, 后面几步暴露出来的任何区域才是黑的。退出时反过来: 先把底色还给主题,
    /// 再改几何, 这样窗口缩回去的过程中露出来的边角是对的主题色而不是一块全屏黑。
    /// </summary>
    private void ApplyFullscreenLayout(bool on)
    {
        if (on)
        {
            // ★ 第一步必须是它: 底色先黑, 后面收缩/放大暴露出来的区域才不会是主题色。
            //   ★★ 要切的是**两层**—— 窗口 Background 只是最底下那层, 上面还压着根网格
            //   RootGrid 的 AppBackgroundBrush(浅色主题 = 白), 它不被一起切掉,
            //   全屏期间露出来的任何缝隙就是一块白(2026-10-01 用户报"浅色全屏全白")。
            Background = Brushes.Black;
            RootGrid.Background = Brushes.Black;
            // ★★★ 上面那两层**都盖不住视频区**(见 ArmFullscreenCover 的说明):
            //   VideoView 是 HwndHost(airspace), 画在所有 WPF 元素之上, 而"视频区那层
            //   黑底 Grid"是它的**父容器** —— 父容器画不到子 HWND 上面, 以前那句
            //   "视频区那层 Grid 本身是黑底, 无需处理"是错的。
            //   真正能盖住 vout HWND 的只有覆盖窗口里的手势层 VideoAreaRoot,
            //   所以全屏切换期间必须把它压黑(见 ArmFullscreenCover)。
            ArmFullscreenCover();

            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            TopBar.Visibility = Visibility.Collapsed;
            TopRow.Height = new GridLength(0);
            // ★ 信息栏的收起**必须走这一个入口**(收起原因见 ApplyInfoPanelState 的说明):
            //   全屏与"铺满窗口"两个开关共用它, 谁都不许自己改 InfoPanel/RightColumn。
            ApplyInfoPanelState();

            // ★ 全屏必须**盖住任务栏**。
            // 无边框窗口的最大化尺寸由 WM_GETMINMAXINFO 决定, 而 FluentWindow 里那条钩子
            // 是给主窗口的"普通最大化"定的规矩: 只占工作区(任务栏照常露在外面)。
            // 播放器全屏要的是整块显示器, 所以这里把开关翻过来 —— 见 MaximizeCoversTaskbar。
            MaximizeCoversTaskbar = true;
            // ★★★ 尺寸铺满还不够: Windows 外壳只对"无标题栏样式 + 铺满显示器"的窗口做
            //   全屏判定(自动藏任务栏)。SourceInitialized 里为了系统动画补回的 WS_CAPTION
            //   必须在这里摘掉, 否则任务栏照样浮在全屏画面上(2026-10-03 用户报的 bug)。
            //   退全屏时由下面的对称分支补回来, 系统动画不受影响。
            DwmInterop.SuppressCaptionForFullscreen(this);
            // ★ 开关改完必须逼窗口**重新走一次最大化**: WM_GETMINMAXINFO 只在窗口开始
            //   最大化/移动/缩放时发一次, 已经处于 Maximized 的窗口再改开关不会有任何效果
            //   (表现就是"全屏进去了, 任务栏还在")。先退成 Normal 再最大化即可。
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            // 先改外观再改几何: 窗口在缩回去的过程中露出来的边角就是正确的主题色,
            // 而不是刚才那块"全屏黑"。(FluentWindow 按深浅主题挑 WindowSolid*)
            MaximizeCoversTaskbar = false;
            ApplyChrome();
            // ★★ 不要在这里补 WS_CAPTION: 它由下面对"窗口状态还原之后"那次
            //   SyncCaptionForAcrylic 统一补(那时 WindowState 已回到 Normal, 才补得回来) ——
            //   在这里补会因为窗口还是 Maximized 而被跳过, 退全屏后就没动画了。
            //   (重复按钮的克制现在靠去掉 WS_SYSMENU, 不再靠摘 WS_CAPTION。)

            ReleaseFullscreenCover();
            // 根网格底色还给主题(与 ApplyChrome 同理: 缩回过程露出的边角要主题色)
            RootGrid.SetResourceReference(BackgroundProperty, "AppBackgroundBrush");

            TopBar.Visibility = Visibility.Visible;
            TopRow.Height = new GridLength(44);
            // 与进全屏同一个入口: 退出全屏后信息栏是否回来, 还要看"铺满窗口"是不是还开着
            // (两个开关可以叠加: 先铺满窗口再按 F 进全屏, 退出全屏时应该回到"仍铺满"状态)。
            ApplyInfoPanelState();
            // 退全屏要把窗口从最大化还原 —— 全屏就是靠"最大化 + 收顶栏"实现的。
            // ★ 这一句与 ApplyInfoPanelState 的顺序无关, 但**不能删**: 少了它退全屏后窗口
            //   会一直贴在整块显示器上(铺满窗口那个模式本身不改窗口状态)。
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            // ★ 窗口状态**还原之后**要再同步一次 WS_CAPTION(2026-10-05): 上面那次 ApplyChrome 跑在
            //   "还是 Maximized"的时刻, 而"没开亚克力时补回 WS_CAPTION"只在 Normal 下做 ——
            //   不在还原后补这一下, 退全屏会永久丢掉系统最小化/最大化过渡动画。
            //   (开亚克力时这一句是空操作: 那条路要的是"保持摘掉"。)
            DwmInterop.SyncCaptionForAcrylic(this);

            // 信息栏直接到位, 不再淡入 —— 全屏切换整体没有任何过渡动画了。
            // 顺手把可能的残留动画摘掉并归位, 免得哪天有人给它加动画时被这里顶掉。
            if (InfoPanel != null)
            {
                InfoPanel.BeginAnimation(OpacityProperty, null);
                InfoPanel.Opacity = 1;
            }
        }
    }

    /// <summary>
    /// 全屏黑幕: 切换期间把手势层压黑, 盖住 vout HWND 露出的白底。
    ///
    /// ★ 为什么必须压手势层, 而不是切窗口/RootGrid 底色(2026-10-01 修长期未愈的白闪):
    ///   `VideoView` 是 **HwndHost**(airspace), 它画在**所有 WPF 元素之上**。视频区那层
    ///   `Background="Black"` 的 Grid 是 VideoView 的**父容器** —— 父容器画不到子 HWND 上面。
    ///   所以历史上"底色先切黑就够"的前提其实不成立: 浅色主题下窗口尺寸剧变时,
    ///   LibVLC 要重新协商 vout, 那一瞬视频 HWND 没有新帧可显示, 露出它的**默认白底**,
    ///   而覆盖窗口里唯一能压住它的就是 `VideoAreaRoot`。深色主题下窗口底色本来就是
    ///   #202020, 同一处露底不刺眼, 所以"只有浅色模式出问题" —— 与用户描述一致。
    ///
    /// ★ 为什么"部分视频": 命��� `ShortVideoCache` 的短视频是**已下完的本地单文件**,
    ///   任意时刻都有已解出的帧; 远端 DASH 长视频要 flush/重协商 vout, 空窗明显得多。
    ///   这与 2026-10-01 那条日志记的判别式完全一致。
    ///
    /// ★ 这不是"过渡动画": 不淡入、不用 _fsShadeTimer(那个已被删除且别复活),
    ///   只是切换期间多压一层不透明黑, 首个新帧到达即撤掉。
    ///   ★★ 2026-10-02 用户要求"全屏动画改用 Windows 自带的"之后**它仍然必须留着**:
    ///   它挡的是 LibVLC vout 重协商时的**白底空窗**, 与"动画由谁播"完全无关 ——
    ///   改成系统动画后窗口尺寸变化依旧发生, 空窗依旧存在, 去掉它浅色主题就会闪白。
    /// </summary>
    private void ArmFullscreenCover()
    {
        if (_closing || VideoAreaRoot == null) return;
        VideoAreaRoot.Background = Brushes.Black;
        _fullscreenCoverArmed = true;
        _fullscreenCoverTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(FullscreenCoverTimeoutMs)
        };
        _fullscreenCoverTimer.Tick -= OnFullscreenCoverTimeout;
        _fullscreenCoverTimer.Tick += OnFullscreenCoverTimeout;
        _fullscreenCoverTimer.Start();
    }

    /// <summary>撤掉全屏黑幕(视频已经在出帧了, 再挡着就看不见画面了)</summary>
    private void ReleaseFullscreenCover()
    {
        _fullscreenCoverTimer?.Stop();
        if (_closing || !_fullscreenCoverArmed || VideoAreaRoot == null) return;
        _fullscreenCoverArmed = false;
        VideoAreaRoot.Background = VideoAreaBackBrush;
    }

    /// <summary>
    /// 兜底: 一直没等到新帧(暂停中/静止帧/极短片)就把黑幕撤掉,
    /// 否则画面会被自己挡住。超时取 600ms —— 正常情况下远早于此就已撤掉。
    /// </summary>
    private void OnFullscreenCoverTimeout(object? sender, EventArgs e) => ReleaseFullscreenCover();

    /// <summary>当前是否处于全屏(顶栏已收起、右侧信息栏已收成 0 宽)</summary>
    private bool _isFullscreen;

    /// <summary>
    /// "铺满窗口"是否开着(2026-10-04 新增, 顶栏右上角那个按钮)。
    ///
    /// 与 <see cref="_isFullscreen"/> 是**两个独立的开关**, 要叠加使用:
    ///   · 它只做一件事 —— 把右侧信息栏整个收掉, 让视频铺满窗口宽度; **窗口尺寸/状态一概不动**;
    ///   · 真全屏是"收顶栏 + 最大化到整块显示器 + 盖任务栏", 它不碰。
    /// 两者都收信息栏, 所以"信息栏收没收"由 <see cref="ApplyInfoPanelState"/> 取**或**之后统一决定 ——
    /// 那正是两个开关能叠加的原因(先铺满窗口再按 F 进全屏、退出全屏仍保持铺满)。
    /// </summary>
    private bool _isFillWindow;

    /// <summary>
    /// 右上角「铺满窗口」按钮的点击入口。
    /// Esc 的处理见 <see cref="HandleBackRequest"/> —— 它按"由内到外"退: 全屏 → 铺满窗口 → 关窗。
    /// </summary>
    private void OnFillWindowClick(object sender, RoutedEventArgs e) => ToggleFillWindow();

    private void ToggleFillWindow()
    {
        if (_closing) return;
        _isFillWindow = !_isFillWindow;
        ApplyInfoPanelState();
        UpdateFillWindowButton();
    }

    /// <summary>常规模式(信息栏展开)的窗口最小尺寸 —— 与 XAML 里的 MinWidth/MinHeight 保持一致</summary>
    private const double NormalWindowMinWidth = 1100;
    private const double NormalWindowMinHeight = 600;

    /// <summary>视频列在常规模式下的最小宽度(与 XAML 的 MinWidth 一致); 信息栏收起后放开到 0</summary>
    private const double VideoColumnNormalMinWidth = 480;

    /// <summary>
    /// 铺满窗口/全屏(信息栏收起)时允许缩到的最小尺寸 —— 此时窗口里只剩视频,
    /// 没有"布局放不下"的问题, 可以缩到很小。
    /// </summary>
    private const double FillWindowMinWidth = 320;
    private const double FillWindowMinHeight = 200;

    /// <summary>
    /// 按两个开关的当前值把右侧信息栏设成"该收"或"该展"。**唯一改 InfoPanel / RightColumn 的地方。**
    ///
    /// ★ 为什么信息栏必须**整个 Collapsed**, 不能只把列宽设成 0:
    ///   宽度为 0 的面板仍然参与 measure/arrange/render —— 里面挂着几百张评论卡
    ///   (无对象池, 见 notes-player-internals), 每切一次都要把这棵评论树重新量一遍,
    ///   这就是用户说的"卡顿一下"(退出时还要再付一次)。
    ///
    /// ★ 为什么必须收敛到一个入口: 收信息栏的现在有两个开关(全屏 / 铺满窗口), 各自改一遍
    ///   会立刻出现"退出全屏把铺满窗口的状态也顶掉"这种互相打架 ——
    ///   两个开关叠加时, 谁先谁后都不能让信息栏闪一下或者卡在错误状态。
    /// </summary>
    private void ApplyInfoPanelState()
    {
        if (_closing) return;
        var hide = _isFullscreen || _isFillWindow;
        if (InfoPanel != null)
            InfoPanel.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        // ★ 列宽必须跟着一起给回去: 只把 Visibility 改回 Visible 而列宽还停在 0,
        //   面板会量成 0 宽(用户看到的是"信息栏没回来")。
        if (RightColumn != null)
            RightColumn.Width = new GridLength(hide ? 0 : 400);

        // ★ 最小尺寸必须跟着状态走(2026-10-05 修"铺满窗口后缩到某个程度就被卡住"):
        //   常规的 1100×600 是按"视频列 480 + 信息栏 400"的布局定的下限; 信息栏收起后
        //   整个窗口只剩视频, 再拿它当底线没有道理。视频列的 MinWidth 同理 —— 不跟着放开,
        //   窗口缩到 480 以下时列宽比窗口还宽, 画面会被裁偏。
        //   (FluentWindow 的 WM_GETMINMAXINFO 在每次拖动时重读 MinWidth/MinHeight, 改属性即可。)
        if (VideoColumn != null)
            VideoColumn.MinWidth = hide ? 0 : VideoColumnNormalMinWidth;
        MinWidth = hide ? FillWindowMinWidth : NormalWindowMinWidth;
        MinHeight = hide ? FillWindowMinHeight : NormalWindowMinHeight;
    }

    /// <summary>
    /// 同步「铺满窗口」按钮的字形与提示(进入前 = 虚线框"填充这块区域", 已铺满 = 向内的双箭头"还原")。
    ///
    /// 为什么图标要跟着状态变: 同一个位置点两下是"进入/退出", 字形不变的话用户不知道
    /// 现在是不是已经铺满了, 也不知道再点一下会发生什么。
    /// </summary>
    private void UpdateFillWindowButton()
    {
        if (BtnFillWindow == null) return;
        // E9A6 = 虚线框(填充区域) / E73F = 向内的双箭头(还原)。两者在本字体里形状差异明显。
        if (FillWindowGlyph != null) FillWindowGlyph.Text = _isFillWindow ? "\uE73F" : "\uE9A6";
        BtnFillWindow.ToolTip = _isFillWindow ? "还原 (退出铺满窗口)" : "铺满窗口";
    }

    private void OnFullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();

    // ------------------------------------------------------------ 收藏状态(右侧按钮 + 顶栏图标)

    /// <summary>收藏状态只有这一个入口 —— 分散着改很容易出现两边显示不一致</summary>
    private void SetFavorited(bool favorited)
    {
        _isFavorited = favorited;
        UpdateActionCounts();
    }
    // "关闭"按钮已随顶栏改版删除(2026-09-26): 关播放器统一走 Esc/鼠标侧键的 HandleBackRequest。

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        FailOverlay.Visibility = Visibility.Collapsed;
        // 直播失败要重连直播, 不能落回点播那条路(那时 _currentBvid 是空的)
        if (_isLive) _ = LoadLiveAsync(_liveRoomId);
        else _ = LoadVideoAsync();
    }

    // ------------------------------------------------------------ 按钮事件

    private void UpdateButtonState()
    {
        if (_closing) return;
        // 本地文件没有点赞/投币/收藏/关注这些在线操作, 一律禁用而不是让它点了报错
        var canInteract = Svc.Session.HasLogin && !_isLocalPlayback;
        if (BtnLike != null) BtnLike.IsEnabled = canInteract;
        if (BtnCoin != null) BtnCoin.IsEnabled = canInteract;
        if (BtnFavorite != null) BtnFavorite.IsEnabled = canInteract;
        if (BtnWatchLater != null) BtnWatchLater.IsEnabled = canInteract;
        if (BtnFollow != null) BtnFollow.IsEnabled = canInteract && _ownerMid > 0;
        // 评论输入框的可用性跟着同一套前提走(已登录 + 在线点播)
        UpdateCommentComposer();
    }

    /// <summary>查询当前视频是否已点赞, 用于按钮状态切换</summary>
    private async Task RefreshLikeStateAsync()
    {
        if (_aid <= 0 || !Svc.Session.HasLogin) return;
        var (ok, _, liked) = await Svc.Api.HasLikedAsync(_aid);
        // 查不到就保持原状(别把按钮图标刷成"未赞"), 但把原因记一笔便于排查
        if (!ok) WriteLibVLCDebug($"\n[HasLiked] 查询失败 aid={_aid}\n");
        else _isLiked = liked;
        if (!_closing) UpdateActionCounts();
    }

    private async void OnLikeClick(object sender, RoutedEventArgs e)
    {
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("点赞需要先登录"); return; }
        if (string.IsNullOrEmpty(_currentBvid)) return;
        // 按当前状态切换: 已赞 -> 取消, 未赞 -> 点赞
        var (ok, err) = await Svc.Api.LikeVideoAsync(_currentBvid, !_isLiked);
        if (ok)
        {
            _isLiked = !_isLiked;
            _likeCount = Math.Max(0, _likeCount + (_isLiked ? 1 : -1));
            UpdateActionCounts();
            Svc.Toast.Show(_isLiked ? "点赞成功" : "已取消点赞");
        }
        else
        {
            Svc.Toast.Show("操作失败: " + (err ?? "未知错误"));
        }
    }

    private async void OnCoinClick(object sender, RoutedEventArgs e)
    {
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("投币需要先登录"); return; }
        if (string.IsNullOrEmpty(_currentBvid) || _aid <= 0) return;
        var (ok, err) = await Svc.Api.CoinVideoAsync(_aid, 1, _currentBvid);
        if (ok)
        {
            // 同一个视频最多投 2 枚, 本地不精确记状态(下次进来以详情接口为准), 只把数字 +1
            _coinCount += 1;
            UpdateActionCounts();
            Svc.Toast.Show("投币成功");
        }
        else
        {
            Svc.Toast.Show("投币失败: " + (err ?? "未知错误"));
        }
    }

    /// <summary>
    /// 操作栏的"图标 + 数量"刷新。
    /// 点过赞/收藏过的状态用**强调色**标出来(图标和数字一起变粉), 比"已点赞"三个字更接近 B 站的观感。
    /// ★ 数字 TextBlock 的 Foreground 必须**显式绑**到按钮的 Foreground: 全局那个隐式 TextBlock 样式
    ///   带 Foreground setter, 优先级高于继承, 不绑的话变粉只发生在图标上(和卡片标题同一个坑)。
    /// </summary>
    private void UpdateActionCounts()
    {
        if (_closing) return;
        LikeLabel.Text = VideoItem.FormatCount(_likeCount);
        CoinLabel.Text = VideoItem.FormatCount(_coinCount);
        FavLabel.Text = VideoItem.FormatCount(_favCount);
        BtnLike.Foreground = _isLiked ? (Brush)FindResource("AccentTextBrush") : (Brush)FindResource("TextSecondaryBrush");
        BtnFavorite.Foreground = _isFavorited ? (Brush)FindResource("AccentTextBrush") : (Brush)FindResource("TextSecondaryBrush");
        if (BtnWatchLater != null)
            BtnWatchLater.Foreground = _isInWatchLater ? (Brush)FindResource("AccentTextBrush") : (Brush)FindResource("TextSecondaryBrush");
    }

    /// <summary>
    /// 查询该视频当前的收藏状态。
    /// 用 /x/v3/fav/folder/created/list?rid= : 一次请求同时得到"有没有收藏"与
    /// "收藏在哪些夹里", 后面点按钮时就不用再查一遍。
    /// </summary>
    private async Task RefreshFavStateAsync()
    {
        if (_aid <= 0 || !Svc.Session.HasLogin || _closing) return;
        var (ok, _, folders) = await Svc.Api.GetVideoFavFoldersAsync(_aid);
        if (!ok || folders == null || _closing) return;
        // 只要在任意一个收藏夹里, 就算"已收藏"
        var favorited = folders.Any(f => f.HasVideo);
        SetFavorited(favorited);
    }

    // ------------------------------------------------------------ 稍后再看

    /// <summary>稍后再看的点亮态只有这一个入口(和收藏一样, 别在多处直接改字段)</summary>
    private void SetInWatchLater(bool inList)
    {
        _isInWatchLater = inList;
        if (BtnWatchLater != null)
            BtnWatchLater.ToolTip = inList ? "已在稍后再看（点击移出）" : "加入稍后再看";
        UpdateActionCounts();
    }

    /// <summary>
    /// 查询该视频是否已在「稍后再看」里。
    /// 服务端没有单条查询接口, 走的是"拉列表 + 本地缓存 aid 集合"(见 ApiClient.IsInWatchLaterAsync):
    /// 60 秒内复用, 所以连续点开视频不会每次都拉一遍两百多条。
    /// 返回 null(未登录/查询失败)时**不点亮也不熄灭**, 保持未知态 —— 谎报状态比不显示更糟。
    /// </summary>
    private async Task RefreshWatchLaterStateAsync()
    {
        if (_aid <= 0 || _closing) return;
        var (inOk, _, inList) = await Svc.Api.IsInWatchLaterAsync(_aid);
        // 没查到就保持现状 —— 早先 `inList == null` 的守卫就在这里, 语义不变
        if (_closing || !inOk) return;
        SetInWatchLater(inList);
    }

    /// <summary>
    /// 稍后再看按钮: 加入 / 移出(再次点击)。
    /// 只按**接口返回值**翻转本地状态, 不做乐观更新、也绝不"提交完再拉一次列表确认" ——
    /// 服务端列表本身有约 2 秒延迟, 回查只会读到旧数据。
    /// </summary>
    private async void OnWatchLaterClick(object sender, RoutedEventArgs e)
    {
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("稍后再看需要先登录"); return; }
        if (_isLive || _isLocalPlayback) { Svc.Toast.Show("直播和本地文件不支持稍后再看"); return; }
        if (_aid <= 0 || _watchLaterBusy) return;

        var target = !_isInWatchLater;   // 当前不在 -> 加入; 已在 -> 移出
        _watchLaterBusy = true;
        try
        {
            var (ok, err) = target
                ? await Svc.Api.AddWatchLaterAsync(_aid)
                : await Svc.Api.RemoveWatchLaterAsync(_aid);
            if (_closing) return;
            if (!ok)
            {
                Svc.Toast.Show((target ? "加入失败: " : "移出失败: ") + (err ?? "未知错误"));
                return;
            }
            SetInWatchLater(target);
            Svc.Toast.Show(target ? "已加入稍后再看" : "已从稍后再看移出");
        }
        finally
        {
            _watchLaterBusy = false;
        }
    }

    /// <summary>
    /// 收藏按钮: 弹出收藏夹勾选窗(预勾选视频当前所在的夹),
    /// 确定后把勾选差集(新增/移出)一次提交给 deal 接口 —— 与网页端"选择收藏夹"行为一致。
    /// "取消收藏"也走同一个窗(把勾全去掉), 不再单独走一键直存默认夹的旧路径。
    /// </summary>
    private async void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("收藏需要先登录"); return; }
        if (_aid <= 0 || _favBusy) return;

        _favBusy = true;
        try
        {
            var (ok, err, folders) = await Svc.Api.GetVideoFavFoldersAsync(_aid);
            if (_closing) return;
            if (!ok || folders == null)
            {
                Svc.Toast.Show("收藏状态获取失败: " + (err ?? "未知错误"));
                return;
            }
            if (folders.Count == 0)
            {
                Svc.Toast.Show("账号下还没有收藏夹, 请先去 B 站创建一个");
                return;
            }

            // 模态弹窗(Owner 指向播放器): 播放器若正在关窗, 不再开新窗
            if (_closing) return;
            var picker = new FavFolderPickerWindow(folders, _videoTitle) { Owner = this };
            picker.ShowDialog();
            if (_closing || !picker.Confirmed) return;

            var addIds = picker.AddedIds;
            var delIds = picker.RemovedIds;
            if (addIds.Count == 0 && delIds.Count == 0) return;   // 勾选没变化, 不发请求

            var (ok2, err2) = await Svc.Api.DealFavoriteAsync(_aid,
                addIds.Count > 0 ? addIds : null,
                delIds.Count > 0 ? delIds : null);
            if (_closing) return;
            if (!ok2) { Svc.Toast.Show("收藏失败: " + (err2 ?? "未知错误")); return; }

            // 高亮与提示跟最终勾选走(至少在一个夹里就算"已收藏")
            var final = picker.FinalCheckedIds;
            SetFavorited(final.Count > 0);
            Svc.Toast.Show(final.Count > 0 ? $"已收藏到 {final.Count} 个收藏夹" : "已取消收藏");
        }
        finally
        {
            _favBusy = false;
        }
    }

    /// <summary>查询当前 UP 主关注状态, 用于关注按钮切换显示</summary>
    private async Task RefreshFollowStateAsync()
    {
        if (_ownerMid <= 0) return;
        var (relOk, _, followed) = await Svc.Api.GetRelationAsync(_ownerMid);
        if (!relOk || _closing) return;
        _isFollowed = followed;
        UpdateFollowButton();
    }

    private void UpdateFollowButton()
    {
        if (FollowLabel == null) return;
        FollowLabel.Text = _isFollowed ? "已关注" : "+ 关注";
    }

    private async void OnFollowClick(object sender, RoutedEventArgs e)
    {
        if (!Svc.Session.HasLogin || _ownerMid <= 0) { Svc.Toast.Show("关注需要先登录"); return; }
        // 按当前状态切换: 已关注 -> 取消关注, 未关注 -> 关注
        var (ok, err) = await Svc.Api.FollowUpAsync(_ownerMid, !_isFollowed);
        if (ok)
        {
            _isFollowed = !_isFollowed;
            UpdateFollowButton();
            Svc.Toast.Show(_isFollowed ? "已关注" : "已取消关注");
        }
        else
        {
            Svc.Toast.Show("操作失败: " + (err ?? "未知错误"));
        }
    }

    private void OnUpPageClick(object sender, RoutedEventArgs e)
    {
        if (_ownerMid <= 0) return;
        // 打开原生 UP 主主页(新窗口)
        var dispatcher = new NavigationDispatcher(this);
        dispatcher.OpenUserSpace(_ownerMid.ToString());
    }

    /// <summary>
    /// 左键点 UP 头像/名字 → UP 主主页。
    /// 原"UP 主页"文字入口已删(2026-09-26 用户要求), 这就是 UP 入口的唯一落点;
    /// 头像的右键菜单(看大图/下载)走 ContextMenu, 与左键互不干扰。
    /// </summary>
    private void OnUpAvatarClick(object sender, MouseButtonEventArgs e) => OnUpPageClick(sender, e);

    /// <summary>右键 UP 主头像 → 查看大图</summary>
    private void OnViewUpAvatar(object sender, RoutedEventArgs e)
        => MediaActions.ShowImage(_ownerFace,
            string.IsNullOrEmpty(_ownerName) ? "UP 主头像" : _ownerName + " 的头像", this);

    /// <summary>右键 UP 主头像 → 保存到本地</summary>
    private async void OnDownloadUpAvatar(object sender, RoutedEventArgs e)
        => await MediaActions.SaveImageAsync(_ownerFace,
            string.IsNullOrEmpty(_ownerName) ? "头像" : _ownerName + "_头像");

    private void OnCopyLinkClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentBvid)) return;
        try
        {
            Clipboard.SetText("https://www.bilibili.com/video/" + _currentBvid);
            Svc.Toast.Show("链接已复制");
        }
        catch
        {
            // 剪贴板被别的进程占着时 SetText 会抛(系统级竞争, 重试也可能再失败)。
            // 静默吞掉即可: 复制失败不该弹框打断播放, 而且没有"已复制"的提示用户自己会再点一次。
        }
    }

    /// <summary>
    /// "网页版"入口已随第二行小字链接一起删除(2026-09-26 用户要求)。
    /// 别处要"跳系统浏览器"时, 用 Process.Start(url) { UseShellExecute = true } 即可。
    /// </summary>

    // ------------------------------------------------------------ Tab

    private void TabIntro_Checked(object sender, RoutedEventArgs e)
    {
        IntroPanel.Visibility = Visibility.Visible;
        CommentsPanel.Visibility = Visibility.Collapsed;
        CloseCommentDetail();
    }

    private async void TabComments_Checked(object sender, RoutedEventArgs e)
    {
        IntroPanel.Visibility = Visibility.Collapsed;
        CommentsPanel.Visibility = Visibility.Visible;
        // 切到评论页时顺手校准一次输入框状态: 用户可能是先登录/换号再回来发的
        UpdateCommentComposer();
        await LoadCommentsAsync();
    }

    private bool _commentsLoaded;
    private async Task LoadCommentsAsync(bool force = false)
    {
        if (_closing) return;
        // force: 刚发表完评论要重新拉一次(rpid/楼层/审核状态本地编不出来)。
        // 但"看过就不再拉"这个短路必须保留 —— 切 Tab 会反复调到这里, 不短路就是白刷接口。
        if (force) CloseCommentDetail();
        if (_commentsLoaded && !force) { CommentHint.Text = "已加载"; return; }
        if (_aid <= 0)
        {
            CommentHint.Text = _isLocalPlayback ? "本地文件没有评论" : "未获取到视频 aid";
            return;
        }
        CommentLoadingRing.Visibility = Visibility.Visible;
        CommentHint.Text = force ? "正在刷新评论…" : "加载评论中…";
        try
        {
            var (cmtOk, cmtErr, list) = await Svc.Api.GetCommentsAsync(_aid, 30);
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closing) return;
                CommentList.ItemsSource = list;
                _commentsLoaded = true;
                CommentLoadingRing.Visibility = Visibility.Collapsed;
                // ★ 原来"接口失败"和"这视频真的一条评论都没有"都显示"暂无评论" ——
                //   网络失败被伪装成了正常状态。现在失败时说清原因。
                CommentHint.Text = !cmtOk ? (cmtErr ?? "评论加载失败")
                    : list.Count == 0 ? "暂无评论" : $"共 {list.Count} 条评论";
            });
        }
        catch
        {
            if (_closing) return;
            CommentLoadingRing.Visibility = Visibility.Collapsed;
            CommentHint.Text = "评论加载失败";
        }
    }

    /// <summary>
    /// 展开某条评论下的全部回复(楼中楼)。
    /// 首页接口只内联前 3 条, 这里按需把剩下的补齐 —— 补完 HasMoreReplies 会变 false,
    /// 按钮自己就消失了, 不需要额外的状态管理。
    /// </summary>
    // ------------------------------------------------------------ 评论详情(点楼中楼方框进入)

    private CommentItem? _detailThreadRoot;   // 楼中楼的线程根(拉取回复时当 root 参数)
    private CommentItem? _detailFocus;        // 详情卡片上展示的那一条
    private CommentItem? _replyTarget;        // 详情页输入框当前要回复谁; null = 发表普通评论

    /// <summary>
    /// 点评论卡片里的**楼中楼方框** → 进详情页看这条评论的全部回复。
    /// 方框长在顶层评论下面, 所以线程根就是这条评论自己。
    /// (上一版是"查看详情"文字链接 + 楼中楼条目各自可点, 都已并入方框这一个入口。)
    /// </summary>
    private void OnReplyBoxClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_closing || (sender as FrameworkElement)?.DataContext is not CommentItem item) return;
        ShowCommentDetail(threadRoot: item, focus: item);
    }

    private void ShowCommentDetail(CommentItem threadRoot, CommentItem focus)
    {
        _detailThreadRoot = threadRoot;
        _detailFocus = focus;
        // Host 的 DataContext = 线程根: "相关回复共 N 条"/回复列表/加载更多都从它取
        CommentDetailHost.DataContext = threadRoot;
        CommentDetailCard.Content = focus;
        // tab 文案随状态走: 进详情后"评论"变"评论详情", 返回链接就摆在它右边(用户指定)
        TabComments.Content = "评论详情";
        BtnBackToComments.Visibility = Visibility.Visible;
        // 评论区只有一个输入框(列表页顶部那个), 进详情页它自动切成"回复 @这层楼"
        // (用户规则: 在详情页评论 = 给这层楼评论); 点提示条上的"取消"才改回发新楼层。
        // **不动用户已经写了一半的草稿** —— 只是想看看回复, 不该把评论内容清掉。
        // focus: false —— 一进门就抢焦点容易误触输入法。
        SetReplyTarget(threadRoot, focus: false);
        CommentList.Visibility = Visibility.Collapsed;
        CommentDetailPanel.Visibility = Visibility.Visible;
        // "楼中楼自动展开所有评论": 进来就把这条线程剩下的回复补全, 不再要用户点"展开更多"
        if (threadRoot.HasMoreReplies) _ = AutoCompleteThreadRepliesAsync(threadRoot);
        // 滚动交给外层右面板的 ScrollViewer: 让详情面板滚进视野
        CommentDetailPanel.BringIntoView();
    }

    /// <summary>
    /// 把一条线程的回复**自动补全**(用户要求"楼中楼自动展开所有评论")。
    ///
    /// 上限 5 页(约 100 条): 爆款楼的回复能上四位数, 无上限地拉会把接口和界面一起拖住;
    /// 拉完后若还有剩, 详情页底部那个"展开更多"按钮仍在, 用户可以继续点。
    /// 死循环保护: 服务端若重复返回同一页(去重后一条没新增)立即收手。
    /// </summary>
    private async Task AutoCompleteThreadRepliesAsync(CommentItem root)
    {
        try
        {
            var guard = 0;
            while (!_closing && root.HasMoreReplies && guard++ < 5)
            {
                var (_, _, more) = await Svc.Api.GetReplyRepliesAsync(_aid, root.Rpid, pn: root.RepliesNextPage, ps: 20);
                if (_closing) return;
                if (more.Count == 0)
                {
                    // 服务端取不全: 用实际条数收口, 否则按钮会一直挂着
                    root.ReplyCount = root.Replies.Count;
                    break;
                }
                root.RepliesNextPage++;
                var seen = new HashSet<long>(root.Replies.Select(x => x.Rpid));
                var added = 0;
                foreach (var r in more)
                    if (r.Rpid > 0 && seen.Add(r.Rpid)) { root.Replies.Add(r); added++; }
                if (added == 0) break;
            }
            root.NotifyRepliesChanged();
        }
        catch (Exception ex)
        {
            // 自动补全是"锦上添花": 失败不能影响已经渲染出来的回复
            App.ReportError(ex);
        }
    }

    private void OnCommentDetailBack(object sender, RoutedEventArgs e) => CloseCommentDetail();

    /// <summary>关掉详情视图(返回列表 / 切 tab / 换视频时都要调, 防止残留旧内容)</summary>
    private void CloseCommentDetail()
    {
        if (CommentDetailPanel.Visibility != Visibility.Visible) return;
        CommentDetailPanel.Visibility = Visibility.Collapsed;
        CommentDetailCard.Content = null;
        CommentDetailHost.DataContext = null;
        _detailThreadRoot = _detailFocus = null;
        ClearReplyTarget();
        // tab 文案与返回链接一起还原
        TabComments.Content = "评论";
        BtnBackToComments.Visibility = Visibility.Collapsed;
        CommentList.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------ 视频合集

    /// <summary>
    /// 当前正在播的那一集(不在任何合集里时为 null)。
    /// 用来给面板里的条目打"就是这条"的标, 也是判断"要不要为切集重置进度"的依据。
    /// </summary>
    private SeasonEpisode? _currentSeasonEpisode;

    /// <summary>
    /// 当前视频有没有合集(有才显示「合集」入口)。
    /// 单独一个 bool 而不是判 <c>_currentSeasonEpisode != null</c>: 入口是否出现只取决于
    /// "这个视频属于合集吗", 而 _currentSeasonEpisode 还可能因为定位不上(合集里有一条
    /// bvid/cid/aid 都对不上的脏数据)而暂时为 null —— 那种情况下面板仍该能打开。
    /// </summary>
    private bool _hasSeason;

    /// <summary>当前视频的合集(没有则 null)。面板的数据源, 也是"换片后要不要重灌列表"的依据</summary>
    private SeasonInfo? _season;

    /// <summary>
    /// "正在为切集换片"的闸门。合集列表是 ListBox, 连点两集会连续触发两次 SelectionChanged ——
    /// 不挡住就是两个 LoadVideoAsync 并发, 后回来的那个覆盖前一个, 表现是"点了 A 却播了 B"。
    /// </summary>
    private bool _switchingSeasonEpisode;

    /// <summary>
    /// 合集面板是否展开。
    ///
    /// ★ 登记在 <see cref="PersistentFields"/> 里(跨片保持): 用户开着面板连看几集是常态;
    ///   每换一集都把面板收起来, 想接着点下一集还得再展开一次, 很烦。
    ///   代价是换到普通视频时面板会空着 —— 那种情况由 ClearSeasonUi 把面板**整个收起**,
    ///   不留一个空壳面板(见 LoadVideoAsync 里没有合集的分支)。
    /// </summary>
    private bool _seasonOpen;

    /// <summary>点 tab 行右侧的「合集」: 开关面板(不请求接口 —— 数据在详情加载时就已经拿到了)</summary>
    private void OnSeasonToggleClick(object sender, RoutedEventArgs e)
    {
        if (_closing || !_hasSeason || SeasonPanel == null) return;
        if (_seasonOpen) CloseSeasonPanel();
        else OpenSeasonPanel();
    }

    private void OnSeasonCloseClick(object sender, RoutedEventArgs e) => CloseSeasonPanel();

    private void OpenSeasonPanel()
    {
        if (_closing || SeasonPanel == null) return;
        _seasonOpen = true;
        SeasonPanel.Visibility = Visibility.Visible;
        UpdateSeasonButtonState();
        ScrollSeasonToCurrent();
    }

    /// <summary>
    /// 把合集列表滚到"正在播的那一集"。100 多集的合集里不滚, 用户得自己找半天。
    ///
    /// ★ 必须派发到 Loaded 优先级之后: 面板刚由 Collapsed 变 Visible 时布局还没跑完,
    ///   此时 ScrollIntoView 会因为没有可视区域高度而无效(静默不滚, 不报错)。
    /// </summary>
    private void ScrollSeasonToCurrent()
    {
        if (SeasonList?.SelectedItem == null) return;
        var target = SeasonList.SelectedItem;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_closing || !_seasonOpen) return;
            SeasonList?.ScrollIntoView(target);
        }));
    }

    private void CloseSeasonPanel()
    {
        _seasonOpen = false;
        if (SeasonPanel != null) SeasonPanel.Visibility = Visibility.Collapsed;
        UpdateSeasonButtonState();
    }

    /// <summary>
    /// 刷新「合集」按钮的可见性与高亮态。
    /// 没有合集的视频**隐藏**按钮(而不是禁用): 右侧信息栏只有 400px, 一个常年灰着的按钮
    /// 既占位又像是坏了; 官方在没有合集时也不给入口。
    ///
    /// ★ 2026-10-03 起高亮改由 `Tag="on"/"off"` 驱动(样式里的 DataTrigger), 不再直接写
    ///   `Foreground`: 与 简介/评论 统一风格后, 点亮要连带那根 2px 下划线一起 ——
    ///   只设 Foreground 的话下划线不会亮, 看着像个半吊子的选中态。
    ///   文字颜色仍由 XAML 里那个 TextBlock 绑 Button.Foreground 拿到(全局隐式 TextBlock
    ///   样式带 Foreground setter, 优先级高于继承, 光设 Button.Foreground 是改不动它的)。
    /// </summary>
    private void UpdateSeasonButtonState()
    {
        if (BtnSeason == null) return;
        BtnSeason.Visibility = _hasSeason ? Visibility.Visible : Visibility.Collapsed;
        BtnSeason.Tag = _seasonOpen ? "on" : "off";
    }

    /// <summary>详情加载完之后灌合集数据</summary>
    private void ApplySeason(SeasonInfo? season)
    {
        if (_closing) return;

        if (season == null || season.Episodes.Count == 0)
        {
            // 没有合集: 入口隐藏、面板整个收起 —— 不留一个空壳面板(上一个视频的面板可能正开着)
            _season = null;
            _currentSeasonEpisode = null;
            _hasSeason = false;
            _seasonOpen = false;
            ClearSeasonUi(collapsePanel: true);
            UpdateSeasonButtonState();
            return;
        }

        _season = season;
        _hasSeason = true;
        _currentSeasonEpisode = FindCurrentEpisode(season);

        ClearSeasonUi(collapsePanel: false);
        // 列表数据源 = 展平后的全部集。顺序即官方顺序(见 ApiClient.ParseSeason)
        if (SeasonList != null)
        {
            SeasonList.ItemsSource = season.Episodes;
            // 选中 = 高亮。★ 必须走 SelectedItem, 而不是"给每条算一个 IsPlaying 字段":
            // 那些条目是普通 POCO, 没有 INPC, 改了字段界面不会刷新。
            // 赋值会触发 OnSeasonEpisodeSelected —— 那一次的目标就是当前集, 会被自己短路掉。
            SeasonList.SelectedItem = _currentSeasonEpisode;
        }
        SeasonTitleText.Text = season.Title;
        // 副标题带上分组数(仅多分组时) —— 让用户知道面板是按分组排的
        SeasonMetaText.Text = season.Sections.Count > 1
            ? $"共 {season.Episodes.Count} 集 · {season.Sections.Count} 个分组"
            : $"共 {season.Episodes.Count} 集";
        // 定位不上当前集时给一句明确说明: 好过列表里一个高亮都没有, 用户会以为坏了
        SeasonHint.Text = _currentSeasonEpisode == null ? "没能在合集里定位到当前这一集" : "";
        SeasonLoadingRing.Visibility = Visibility.Collapsed;
        UpdateSeasonButtonState();
        // 面板本来开着(用户连看几集)就保持开着 —— 换片不该把面板收起来
        if (_seasonOpen) SeasonPanel.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 定位"当前播的是合集里的哪一集"。
    /// 优先级: cid 精确匹配 &gt; bvid 匹配 &gt; aid 匹配。
    ///
    /// 先按 cid 是因为**同一个 bvid 可能有多个分 P**, 合集里存的是"这一集的默认分 P",
    /// 用户从搜索等入口点进来的可能是它的第 2 个分 P —— 那时 bvid 相同但实际内容不同,
    /// 用 cid 才能把"第几集"标对。取不到 cid 才退回 bvid, 再退回 aid。
    /// </summary>
    private SeasonEpisode? FindCurrentEpisode(SeasonInfo season)
    {
        if (_currentCid > 0)
        {
            var byCid = season.Episodes.FirstOrDefault(e => e.Cid == _currentCid);
            if (byCid != null) return byCid;
        }
        if (!string.IsNullOrEmpty(_currentBvid))
        {
            var byBv = season.Episodes.FirstOrDefault(e => e.Bvid == _currentBvid);
            if (byBv != null) return byBv;
        }
        if (_aid > 0)
        {
            var byAid = season.Episodes.FirstOrDefault(e => e.Aid == _aid);
            if (byAid != null) return byAid;
        }
        return null;
    }

    /// <summary>
    /// 点了合集里的某一集 → **在本窗口**换片(不新开窗口)。
    ///
    /// ★ 这里刻意**不调 Svc.Player.PlayVideo**: 那条路会走 PlayerService 的"复用闸门",
    ///   而闸门在窗口不可复用的场景下会**新建一个 PlayerWindow** —— 用户点下一集却弹出一个
    ///   新窗口, 正是任务里明确禁止的。换片的动作直接落在本窗口上, 与「切换清晰度」同款:
    ///   Stop 掉当前流 → ResetForNewMedia 清掉上一片的全部残留 → 重新走 LoadVideoAsync。
    ///
    /// ★ 不在这里手工挪 SelectedItem: ResetForNewMedia 会把列表清空, 换片完成后
    ///   ApplySeason 会重新定位并设好选中项 —— 那才是唯一的高亮来源。手工补一次反而会在
    ///   "换片失败"时留下一个高亮但没在播的条目。
    /// </summary>
    private void OnSeasonEpisodeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_closing || SeasonList == null) return;
        if (SeasonList.SelectedItem is not SeasonEpisode ep) return;
        // 选中的就是当前这一集: 不做任何换片动作。
        // (ApplySeason 里给 SelectedItem 赋值也会走到这里, 必须能自己短路掉)
        if (ep.Bvid == _currentBvid) return;
        _ = SwitchToSeasonEpisodeAsync(ep);
    }

    private async Task SwitchToSeasonEpisodeAsync(SeasonEpisode ep)
    {
        if (_closing || string.IsNullOrEmpty(ep.Bvid)) return;
        // 防连点: 上一次换片还没走完就再点一集, 会把两个 LoadVideoAsync 叠起来跑,
        // 后一个的结果覆盖前一个 —— 表现是"点了 A 结果播了 B"。用换片闸门直接挡掉。
        if (_switchingSeasonEpisode) return;
        _switchingSeasonEpisode = true;
        try
        {
            try { _mp.Stop(); } catch { }
            _currentBvid = ep.Bvid;
            // ★ 关键: 合集里的集是**在线稿件**, 不是本地文件/直播 —— 从离线缓存/直播那类入口
            //   切过来时这两个标志还是 true, 不清掉新片会被当成"没有在线上下文",
            //   弹幕 / 三连 / 历史 / 评论全都不会加载。
            _isLocalPlayback = false;
            _isLive = false;
            _liveRoomId = 0;
            ResetForNewMedia();
            await LoadVideoAsync();
        }
        finally
        {
            _switchingSeasonEpisode = false;
        }
    }

    // ------------------------------------------------------------ 头像跳转 / 踩 / 回复 / 详情页发表

    /// <summary>
    /// 点评论头像 → 进 TA 的个人主页。
    /// 走的是和"播放器里点 UP 主头像"完全相同的一条路(NavigationDispatcher):
    /// 在主窗口内容区盖一层带返回按钮的主页, 而不是另开独立窗口。
    /// </summary>
    private void OnCommentAvatarClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_closing || (sender as FrameworkElement)?.DataContext is not CommentItem item) return;
        if (item.Mid <= 0)
        {
            // 极少数情况接口没给 mid(已注销账号等), 给个明确反馈而不是"点了没反应"
            Svc.Toast.Show("没拿到 TA 的 UID, 打不开主页");
            return;
        }
        new NavigationDispatcher(this).OpenUserSpace(item.Mid.ToString());
    }

    /// <summary>
    /// 点"回复" → 打开详情页并把输入框切到"回复 @某人"模式。
    ///
    /// 线程根要分清两种情况: 在详情页里点某条回复 → 根是详情页的线程根;
    /// 在列表里点顶层评论 → 根就是它自己(发出去的是给楼主的第一层回复)。
    /// </summary>
    private void OnCommentReplyClick(object sender, RoutedEventArgs e)
    {
        if (_closing || (sender as FrameworkElement)?.DataContext is not CommentItem item) return;
        if (!Svc.Session.HasLogin)
        {
            Svc.Toast.Show("请先登录后再回复");
            return;
        }
        var root = _detailThreadRoot != null && _detailThreadRoot.Replies.Contains(item)
            ? _detailThreadRoot
            : item;
        ShowCommentDetail(threadRoot: root, focus: root);
        SetReplyTarget(item);
    }

    /// <summary>
    /// 把详情页输入框切到"回复 @XXX"模式(顶部冒出提示条, 发布时带 root/parent)。
    /// focus: 是否顺手把光标放进输入框 —— 点"回复"按钮时该给(用户接着就要打字),
    /// 进详情页时不给(一进门就抢焦点容易误触输入法)。
    /// </summary>
    private void SetReplyTarget(CommentItem target, bool focus = true)
    {
        _replyTarget = target;
        if (CommentDetailReplyHint != null) CommentDetailReplyHint.Visibility = Visibility.Visible;
        if (CommentDetailReplyTarget != null) CommentDetailReplyTarget.Text = "回复 @" + target.UserName;
        if (focus) CommentDraft?.Focus();
        // 提示文案("Enter 发表" → "Enter 发送回复")与按钮可用性都跟着回复目标变
        UpdateCommentComposer();
        UpdatePostCommentButton();
    }

    private void ClearReplyTarget()
    {
        _replyTarget = null;
        if (CommentDetailReplyHint != null) CommentDetailReplyHint.Visibility = Visibility.Collapsed;
        if (CommentDetailReplyTarget != null) CommentDetailReplyTarget.Text = "";
        // 退出回复态 → 提示文案要退回"Enter 发表"
        UpdateCommentComposer();
    }

    /// <summary>退出"回复 @某人"模式, 退回发表普通评论</summary>
    private void OnCommentReplyCancel(object sender, RoutedEventArgs e)
    {
        ClearReplyTarget();
        CommentDraft?.Focus();
        UpdatePostCommentButton();
    }


    /// <summary>重新拉一条线程的第一页回复(发表成功后调用)。失败就保持原样, 不清空已有内容。</summary>
    private async Task ReloadThreadRepliesAsync(CommentItem root)
    {
        try
        {
            var (_, _, list) = await Svc.Api.GetReplyRepliesAsync(_aid, root.Rpid, pn: 1, ps: 20);
            if (_closing || list.Count == 0) return;
            // 重拉只覆盖第一页: 页码复位, "展开更多"重新可用(已加载的第 2 页会被第一页取代)
            root.RepliesNextPage = 2;
            var seen = new HashSet<long>();
            var merged = new List<CommentItem>();
            foreach (var r in list)
                if (r.Rpid > 0 && seen.Add(r.Rpid)) merged.Add(r);
            root.Replies.Clear();
            foreach (var r in merged) root.Replies.Add(r);
            if (root.ReplyCount < root.Replies.Count) root.ReplyCount = root.Replies.Count;
            root.NotifyRepliesChanged();
        }
        catch (Exception ex)
        {
            // 重拉失败不影响"已发布成功"这个结论, 记日志即可
            App.ReportError(ex);
        }
    }

    /// <summary>
    /// 点"查看对话" → 定位到被回复的那条(滚进视野 + 闪两下)。
    /// 目标还没加载出来(楼中楼只内联了前几条)时给明确提示, 而不是静默无反应。
    /// </summary>
    private void OnViewConversationClick(object sender, RoutedEventArgs e)
    {
        if (_closing || (sender as FrameworkElement)?.DataContext is not CommentItem reply) return;
        var root = _detailThreadRoot;
        if (root == null || reply.ParentRpid <= 0) return;

        var parent = root.Replies.FirstOrDefault(r => r.Rpid == reply.ParentRpid);
        if (parent == null)
        {
            Svc.Toast.Show("被回复的那条还没加载, 先点下方\u201c展开更多回复\u201d");
            return;
        }
        if (DetailRepliesList?.ItemContainerGenerator.ContainerFromItem(parent) is not FrameworkElement fe) return;
        fe.BringIntoView();
        // 闪一下: 告诉用户"就是这一条", 不然滚过去了也对不上号
        var flash = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(140))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(2)
        };
        fe.BeginAnimation(UIElement.OpacityProperty, flash);
    }

    private async void OnLoadMoreRepliesClick(object sender, RoutedEventArgs e)
    {
        if (_closing || _aid <= 0) return;
        if ((sender as FrameworkElement)?.DataContext is not CommentItem item) return;
        if (item.Rpid <= 0 || item.IsLoadingReplies) return;

        item.IsLoadingReplies = true;
        try
        {
            var (_, _, more) = await Svc.Api.GetReplyRepliesAsync(_aid, item.Rpid, pn: item.RepliesNextPage, ps: 20);
            item.RepliesNextPage++;
            if (_closing) return;

            // 内联的那几条也在返回结果里, 用 rpid 去重再追加
            var seen = new HashSet<long>(item.Replies.Select(x => x.Rpid));
            foreach (var r in more)
                if (r.Rpid > 0 && seen.Add(r.Rpid)) item.Replies.Add(r);

            // 服务端也有取不全的情况(风控/已删除), 用实际条数收口,
            // 否则按钮会永远停在"展开 N 条回复"上点不动
            if (more.Count == 0 || item.Replies.Count >= item.ReplyCount)
                item.ReplyCount = item.Replies.Count;
            item.NotifyRepliesChanged();
        }
        finally
        {
            item.IsLoadingReplies = false;
        }
    }

    // ------------------------------------------------------------ 评论点赞

    /// <summary>
    /// 给一条评论点赞 / 取消点赞。
    ///
    /// 先**乐观更新**界面(数字立刻动), 请求失败再回滚 —— 点赞是高频小动作,
    /// 等一次网络往返再变数字会有明显的"点了没反应"感。
    /// LikeBusy 防连点: 连点会让 赞 / 取消 两个请求交叉, 服务端的最终状态就不确定了。
    /// </summary>
    private async void OnCommentLikeClick(object sender, RoutedEventArgs e)
    {
        if (_closing || _aid <= 0) return;
        if ((sender as FrameworkElement)?.DataContext is not CommentItem item) return;
        if (item.Rpid <= 0 || item.LikeBusy) return;
        if (!Svc.Session.HasLogin)
        {
            Svc.Toast.Show("请先登录后再点赞");
            return;
        }

        var target = !item.IsLiked;
        var oldCount = item.LikeCount;
        // 服务端行为: 点赞成功会自动消掉踩 → 本地状态也要跟着互斥
        var wasDisliked = item.IsDisliked;

        item.LikeBusy = true;
        item.IsLiked = target;
        item.LikeCount = Math.Max(0, oldCount + (target ? 1 : -1));
        if (target) item.IsDisliked = false;
        try
        {
            var (ok, err) = await Svc.Api.CommentLikeAsync(_aid, item.Rpid, target);
            if (_closing) return;
            if (!ok)
            {
                // 回滚
                item.IsLiked = !target;
                item.LikeCount = oldCount;
                item.IsDisliked = wasDisliked;
                Svc.Toast.Show(err ?? "点赞失败");
            }
        }
        finally
        {
            item.LikeBusy = false;
        }
    }

    /// <summary>
    /// 点踩 / 取消点踩。与点赞对称(乐观更新 + 失败回滚), 区别是踩**没有计数** ——
    /// 接口不下发 dislike 总数, 所以只有图标的状态变化。
    ///
    /// ★ 踩走的是**另一个接口**(`/x/v2/reply/hate`), 不是点赞接口的 action=2 ——
    ///   2026-09-27 修: 之前发 action=2 给 /reply/action, 服务端回 "12011 不合法的赞或踩",
    ///   表现就是"点踩点了报错"。
    ///   两者在服务端互相覆盖(点踩成功会自动消掉赞), 所以本地状态也必须互斥。
    /// </summary>
    private async void OnCommentDislikeClick(object sender, RoutedEventArgs e)
    {
        if (_closing || _aid <= 0) return;
        if ((sender as FrameworkElement)?.DataContext is not CommentItem item) return;
        if (item.Rpid <= 0 || item.LikeBusy) return;
        if (!Svc.Session.HasLogin)
        {
            Svc.Toast.Show("请先登录后再点踩");
            return;
        }

        var target = !item.IsDisliked;       // true = 现在要点踩
        var wasLiked = item.IsLiked;
        var oldCount = item.LikeCount;

        item.LikeBusy = true;
        item.IsDisliked = target;
        // 点踩会顶掉赞(服务端行为), 本地那个数字也要跟着减回去
        if (target && wasLiked)
        {
            item.IsLiked = false;
            item.LikeCount = Math.Max(0, oldCount - 1);
        }
        try
        {
            var (ok, err) = await Svc.Api.CommentDislikeAsync(_aid, item.Rpid, target);
            if (_closing) return;
            if (!ok)
            {
                item.IsDisliked = !target;
                if (target && wasLiked)
                {
                    item.IsLiked = true;
                    item.LikeCount = oldCount;
                }
                Svc.Toast.Show(err ?? "点踩失败");
            }
        }
        finally
        {
            item.LikeBusy = false;
        }
    }

    // ------------------------------------------------------------ 发表评论

    private bool _postingComment;

    /// <summary>
    /// 输入框的可用状态与提示。三个前提缺一个就不能发: 已登录 / 有 aid / 不是本地文件播放。
    /// 直接把它们做进"输入框能不能打字"里, 比让用户敲完一大段再点按钮报错要好得多。
    /// </summary>
    private void UpdateCommentComposer()
    {
        if (_closing || CommentDraft == null) return;

        var canCompose = Svc.Session.HasLogin && _aid > 0 && !_isLocalPlayback && !_isLive;
        CommentDraft.IsEnabled = canCompose;
        // 提示文案要跟着"当前是发表还是回复"走, 否则回复态下还写着"Enter 发表"会让人困惑
        CommentPostHint.Text = _isLive
            ? "直播没有评论区"
            : !Svc.Session.HasLogin
                ? "登录后才能发表评论"
                : _aid <= 0
                    ? "没拿到视频 aid, 暂时不能评论"
                    : _replyTarget != null
                        ? "Enter 发送回复 · Shift+Enter 换行 · 点提示条上的“取消”改发评论"
                        : "Enter 发表 · Shift+Enter 换行";
        UpdatePostCommentButton();
    }

    private void UpdatePostCommentButton()
    {
        if (_closing || PostCommentButton == null || CommentDraft == null) return;
        PostCommentButton.IsEnabled = CommentDraft.IsEnabled && !_postingComment
                                      && !string.IsNullOrWhiteSpace(CommentDraft.Text);
    }

    private void OnCommentDraftChanged(object sender, TextChangedEventArgs e)
        => UpdatePostCommentButton();

    /// <summary>
    /// Enter 发表, Shift+Enter 换行。用 Preview 阶段拦: 输入框开着 AcceptsReturn,
    /// 换行是在 TextBox 自己的 KeyDown 里插进去的, 冒泡到我们的处理器时已经晚了。
    /// </summary>
    private void OnCommentDraftPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;   // 换行, 放它过去
        e.Handled = true;
        _ = PostCommentAsync();
    }

    private void OnPostCommentClick(object sender, RoutedEventArgs e) => _ = PostCommentAsync();

    /// <summary>
    /// 发表。**同一个输入框两种语义**(用户规则: 在列表页评论 = 自己开新楼层; 在详情页评论 = 回复这层楼):
    /// 有回复目标(_replyTarget) 就带 root/parent 发成楼中楼回复, 否则发普通评论。
    ///
    /// 失败**不清空输入框** —— 网络抖一下就把用户写的字抹掉, 是最让人恼火的那种"智能"。
    ///
    /// 成功后重新拉一次列表, 而不是本地插一条假的: 服务端返回的 rpid / 楼层 / 是否要审核
    /// 都不是本地能猜的, 拼一条出来看着即时, 一旦用户去点赞就会发现那条根本不存在。
    /// </summary>
    private async Task PostCommentAsync()
    {
        if (_closing || _postingComment) return;
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("发表评论需要先登录"); return; }
        if (_aid <= 0) { Svc.Toast.Show("没拿到视频 aid, 暂时不能评论"); return; }

        var text = CommentDraft.Text?.Trim() ?? "";
        if (text.Length == 0) return;

        var target = _replyTarget;                     // null = 发表新楼层
        var root = _detailThreadRoot;
        // 回复必须**同时**带 root 与 parent: 少了 root 服务端会把回复挂到根楼层上(回错位置)
        var rootRpid = root != null && target != null ? root.Rpid : 0;
        var parentRpid = target?.Rpid ?? 0;
        var isReply = rootRpid > 0 && parentRpid > 0;

        _postingComment = true;
        PostCommentButton.Content = isReply ? "回复中…" : "发表中…";
        UpdatePostCommentButton();
        try
        {
            var (ok, err) = await Svc.Api.PostCommentAsync(_aid, text, rootRpid, parentRpid);
            if (_closing) return;
            if (!ok)
            {
                Svc.Toast.Show(err ?? (isReply ? "回复发送失败" : "评论发表失败"));
                return;
            }

            CommentDraft.Clear();
            ClearReplyTarget();     // 发完退回"发表"模式, 免得下一条手滑又回给同一个人
            Svc.Toast.Show(isReply ? "回复已发布" : "评论已发表");

            if (isReply && root != null)
                await ReloadThreadRepliesAsync(root);   // 回复: 只重拉这条线程
            else
                await LoadCommentsAsync(force: true);   // 评论: 重拉整页列表

            if (!_closing && CommentHint != null)
            {
                // 说实话: 列表是"按热度"排的、回复还要过审排队 ——
                // 不说这句, 用户会以为内容没发出去(或发了但被吞了)。
                CommentHint.Text = isReply
                    ? "回复已发布 · 服务端要过审排队, 可能不会立刻出现在回复列表里"
                    : "评论已发表 · 列表按热度排序, 刚发的可能不在前几条";
            }
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            if (!_closing) Svc.Toast.Show(isReply ? "回复发送失败" : "评论发表失败");
        }
        finally
        {
            _postingComment = false;
            if (!_closing)
            {
                PostCommentButton.Content = "发表";
                UpdatePostCommentButton();
            }
        }
    }

    // ------------------------------------------------------------ Toast

    private System.Windows.Threading.DispatcherTimer? _localToastTimer;
    private void OnToast(string msg) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_closing) return;
        LocalToastText.Text = msg;
        LocalToastBorder.Visibility = Visibility.Visible;
        LocalToastBorder.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.95, 1, TimeSpan.FromMilliseconds(160)));
        _localToastTimer?.Stop();
        _localToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        _localToastTimer.Tick += (_, _) =>
        {
            _localToastTimer.Stop();
            var a = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280));
            a.Completed += (_, _) => LocalToastBorder.Visibility = Visibility.Collapsed;
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
    /// 起播前落地片段数据。最多等 <see cref="SponsorWaitMs"/> ——
    /// 等得到就能做"开场就是广告"的空降; 等不到就先起播, 由后台补上(心跳会兜住跳过)。
    /// **绝不为了它把起播卡住。**
    /// </summary>
    private async Task BeforePlayApplySponsorAsync(Task<IReadOnlyList<SponsorSegment>> task)
    {
        if (task.IsCompleted)
        {
            ApplySponsorRaw(await SafeAwait(task), applyInitialDrop: true);
            return;
        }

        var key = SponsorKey();
        var done = await Task.WhenAny(task, Task.Delay(SponsorWaitMs));
        if (_closing) return;

        if (ReferenceEquals(done, task))
            ApplySponsorRaw(await SafeAwait(task), applyInitialDrop: true);
        else
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

    // ------------------------------------------------------------ 关闭

    /// <summary>
    /// 把视频覆盖层从屏幕上摘下来。
    ///
    /// 背景 —— LibVLCSharp.WPF 的 airspace 方案: VideoView 会把它的 Content
    /// (也就是弹幕层 + 底部播放控制栏 + 各种遮罩) **搬运到一个独立的透明顶层窗口**
    /// (ForegroundWindow)里, 并把它设成 PlayerWindow 的 owned window 来跟随移动。
    ///
    /// 坑在于: Windows 只在 owner **最小化**时才连带隐藏被它拥有的窗口, 单纯 Hide() 不会。
    /// 我们的"秒关"走的正是 Hide(), 于是那层不透明内容(视觉上就是底部控制栏那一条,
    /// 因为弹幕层/视频区是透明的)会作为"残影"留在屏幕上, 直到约 1 秒后后台拆解完成
    /// 才被 VideoView.Dispose() 关掉。
    ///
    /// 这里做两件事, 任何一件单独都足以让残影消失, 一起做是保险:
    ///   1. 清空 VideoView.Content -> ForegroundWindow 里的内容被移除, 该窗口变成全透明
    ///      (空间上仍占着原位, 所以第 2 步还是有意义的 —— 否则它会吞掉那块区域的鼠标输入);
    ///   2. 反射把那个浮动窗口 Hide() 掉。属性是库的 private 成员, 拿不到就跳过,
    ///      此时第 1 步已经保证了"看不见"。
    /// </summary>
    private void DetachVideoOverlay()
    {
        try { VideoView.Content = null; } catch { }

        try
        {
            var prop = typeof(VideoView).GetProperty("ForegroundWindow",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (prop?.GetValue(VideoView) is Window fw) fw.Hide();
        }
        catch { /* 拿不到就算了, 内容已清空, 顶多剩下一个全透明的空窗口 */ }
    }

    /// <summary>
    /// 把键盘与鼠标侧键处理也挂到 VideoView 的那个浮动覆盖层窗口上。
    ///
    /// 为什么必须这么做(用户报的"点一下视频后键盘就失灵"):
    /// 弹幕层 + 播放控制栏是渲染在 **ForegroundWindow** 里的(见上面 airspace 的说明),
    /// 那是一个独立的顶层窗口。鼠标点视频区/控制栏会把焦点交给它, 于是按键全部由它接收,
    /// PlayerWindow 的 PreviewKeyDown 一次都不会触发 —— 快捷键(空格/方向键/F/D)全哑。
    /// 点一下右侧信息栏焦点才回到 PlayerWindow, 键盘"就恢复正常了", 正是这个原因。
    ///
    /// 鼠标侧键是同一个道理: 指针在视频区上时, 事件属于那个浮动窗口, 根本不会路由到
    /// PlayerWindow —— 所以两个事件都要在这儿补一份。
    ///
    /// 与其在每次点击后抢回焦点(更容易和 Slider 拖动打架), 不如两个窗口共用一套处理:
    /// 谁拿到焦点都一样。属性是库的 private 成员, 拿不到就静默跳过。
    /// </summary>
    private void HookOverlayKeyboard()
    {
        if (_closing) return;
        try
        {
            var prop = typeof(VideoView).GetProperty("ForegroundWindow",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (prop?.GetValue(VideoView) is not Window fw) return;
            if (ReferenceEquals(fw, _overlayKeyboardWindow)) return;
            _overlayKeyboardWindow = fw;
            fw.PreviewKeyDown += OnPreviewKeyDown;
            fw.PreviewMouseDown += OnPreviewMouseDown;
            // 滚轮也要挂上来: 鼠标压在视频画面上时事件属于这层浮动窗口(见 OnVideoAreaMouseWheel)
            fw.PreviewMouseWheel += OnVideoAreaMouseWheel;
            // 这个窗口不该出现在任务栏里(它是跟随主窗口的装饰层)
            fw.ShowInTaskbar = false;
        }
        catch { /* 拿不到就算了, 键盘至少在主窗口里是可用的 */ }
    }

    /// <summary>
    /// 把键盘焦点从原生子窗口抢回 WPF 树。
    /// 只有"WPF 侧完全没有焦点元素"时才动手 —— 那说明焦点落在 LibVLC 的原生视频窗口上了;
    /// 平时不能抢, 否则在评论框里打字时点一下视频区就丢焦点。
    /// </summary>
    private void RestoreKeyboardFocus()
    {
        if (_closing) return;
        try
        {
            if (Keyboard.FocusedElement == null) Keyboard.Focus(this);
        }
        catch { /* 忽略 */ }
    }

    /// <summary>已经挂过键盘处理的覆盖层窗口(避免重复订阅)</summary>
    private Window? _overlayKeyboardWindow;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 为什么要"先隐藏 -> 后台拆解 -> 再真正 Close":
        // LibVLC 的 Stop/Dispose 会阻塞等待解复用/解码线程退出(实测约 1 秒),
        // 原先在 UI 线程同步调用, 表现就是"点了关闭要卡一下窗口才消失"。
        // 现在第一次关闭请求只做两件极快的事(置标志 + 隐藏窗口), 用户看到的是瞬间关闭;
        // 慢的原生拆解丢到后台线程, 完成后回来真正 Close()。
        if (!_teardownDone)
        {
            e.Cancel = true;
            // 必须在 Stop() 之前抓取进度: 一旦拆解开始, _mp.Time 会归零/_mp 会被 Dispose,
            // 再取就只能拿到 0, 云端历史会丢位置。这里同步取好数值交给 fire-and-forget 任务。
            var finalAid = _aid;
            var finalCid = _currentCid;
            var finalProgress = 0L;
            try { if (_mp != null && _mp.Length > 0) finalProgress = _mp.Time / 1000; } catch { /* 播放器已失效 */ }
            if (finalAid > 0 && finalCid > 0)
                _ = Svc.HistorySync.ReportNowAsync(finalAid, finalCid, finalProgress);

            _closing = true;

            // ★ 立刻停掉声音 —— 这是"窗口关了但声音还响半秒"的修法。
            //
            // 为什么会响: 真正的 Stop() 会阻塞约 1 秒, 只能丢到后台线程去做(见 TeardownAndCloseAsync),
            // 而 Hide() 之后窗口已经看不见了、音频却还在播 —— 用户听到的就是"关掉后余音半秒"。
            //
            // Pause 是非阻塞的, 在 UI 线程当场生效, 于是声音在 Hide() 之前就断了。
            // 位置必须在 `_closing = true` 之后、DetachVideoOverlay 之前 —— 与上面读进度是同一个
            // "拆解尚未开始"的安全窗口, 再晚就有碰已释放原生指针的风险。
            // ★ 这里**不要**再叠一句 `_mp.Mute = true`: 实测(2026-09-30 探针)在 LibVLCSharp 3.8 +
            //   本机 libvlc 上 `MediaPlayer.Mute` 是不可靠读数 —— 全新进程里第一个播放器读回就是
            //   True, 显式置 false 也改不动; 往它上面写只会留下一个查不出真假的静音隐患。
            //   停声靠 Pause 就够了。
            try { _mp?.SetPause(true); } catch { /* 已经停了/正在拆解 */ }

            _progressTimer?.Stop();
            _kaomojiTimer?.Stop();
            // 手势定时器必须停掉: 它们可能在窗口已经关闭之后才 Tick, 而 Tick 里会去碰
            // 正在被 Dispose 的 _mp。访问已释放的原生指针不是托管异常, try/catch 拦不住,
            // 表现为进程级访问违例(0xc0000005)。反复"打开-关闭"时这几率会被放大。
            _longPressTimer?.Stop();
            _singleClickTimer?.Stop();
            _localToastTimer?.Stop();
            _controlsHideTimer?.Stop();
            StopVolumeOsd();

            // 先把覆盖层摘掉再隐藏窗口, 否则底部控制栏会以残影形式留在屏幕上
            DetachVideoOverlay();

            // 从全屏态直接关闭时, 先摘掉"无边框 + 最大化"这个组合。
            // 该状态下窗口句柄的清理容易不干净(参见 Esc 分支里的同款处理),
            // 反复"全屏打开 -> 直接关闭"会累积成残留窗口/句柄, 也是原生访问违例的温床。
            if (WindowStyle == WindowStyle.None)
            {
                try
                {
                    // 进全屏时 WS_CAPTION 被摘掉了(盖任务栏用, 见 ApplyFullscreenLayout):
                    // 这里要切成 SingleBorderWindow, 必须先把样式位补回来, 否则系统标题栏缺失。
                    DwmInterop.RestoreCaption(this);
                    WindowStyle = WindowStyle.SingleBorderWindow;
                    WindowState = WindowState.Normal;
                }
                catch { /* 已经处于不可切换状态, 忽略 */ }
            }

            // 立刻隐藏, 感知上就是"秒关"; 拆解过程在后台继续
            Hide();

            _ = TeardownAndCloseAsync();
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// 异步拆解原生资源后真正关闭窗口。
    /// WPF 控件(VideoView)必须在 UI 线程释放, 只有会阻塞的 LibVLC Stop/Dispose 放到后台线程。
    /// </summary>
    private async Task TeardownAndCloseAsync()
    {
        var mp = _mp;

        // 顺序很关键: **先 Stop, 再解绑 Hwnd**。
        //
        // 反过来写(VideoView.MediaPlayer = null 会走到 libvlc_media_player_set_hwnd(NULL))
        // 有两个问题:
        //   1. 视频还在播的情况下把 Hwnd 清成 NULL, LibVLC 会另建一个属于它自己的顶层输出窗口,
        //      关闭瞬间就会闪出一个"多出来的视频窗口";
        //   2. "边播边换 HWND, 紧接着 Stop" 正是原生层最容易踩访问违例(0xc0000005)的组合 ——
        //      而这恰好对应用户反馈的"反复打开关闭后偶发 Unexpected parameters"。
        // Stop 会阻塞等待解码线程退出(约 1 秒), 所以先丢到后台线程。
        if (mp != null)
        {
            await Task.Run(() =>
            {
                try { mp.Stop(); } catch { /* 可能已经停了 */ }
            });
        }

        // 回到 UI 线程解绑(VideoView 是 DependencyObject, 只能在 UI 线程操作)
        try { VideoView.MediaPlayer = null; } catch { }
        try { VideoView.Content = null; } catch { }
        // 释放 VideoView 自己持有的原生子窗口 + ForegroundWindow
        try { VideoView.Dispose(); } catch { }

        // MediaPlayer 的 Dispose 同样可能有内部等待, 也放后台
        if (mp != null)
        {
            await Task.Run(() =>
            {
                try { mp.Dispose(); } catch { }
            });
        }

        _teardownDone = true;
        try { Close(); } catch { /* 应用可能已在退出 */ }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closing = true;
        // 后台合流任务要停: 窗口都没了, 下完也没地方切(而且它还在往磁盘写)
        CancelRemuxPrefetch();
        // ★ 全部定时器在这里统一停 —— 漏掉任何一个都会让整个窗口漏掉(见 StopAllTimers 的说明)
        StopAllTimers();
        StopVolumeOsd();
        // 正常路径下 _mp / VideoView 已在 TeardownAndCloseAsync 里释放过了,
        // 这里只兜底那些没走完正常关闭流程的情况(例如构造中途抛异常),
        // 避免对同一个 MediaPlayer 重复 Dispose。
        if (!_teardownDone)
        {
            try { _mp?.Stop(); } catch { }
            try { _mp?.Dispose(); } catch { }
            try { VideoView?.Dispose(); } catch { }
        }
        // 注意: 绝不释放 _libVLC —— 它是进程级共享实例(见 VlcCore),
        // 由 App.OnExit 统一释放。窗口级别的反复创建/销毁不会再触碰原生库生命周期。
        if (_playerRegistered)
        {
            _playerRegistered = false;
            VlcCore.UnregisterPlayer();
        }
        Svc.Toast.Shown -= OnToast;
        Svc.Session.Changed -= UpdateButtonState;
        Svc.Settings.DanmakuSettingsChanged -= OnDanmakuSettingsChanged;
        // 跳过赞助片段: 退订 + 掐掉还没回来的查询
        Svc.Settings.SponsorBlockChanged -= OnSponsorSettingsChanged;
        CancelSponsorFetch();
        // 主动松掉"占地大"的那几处引用, 再请求一次回收 + 把空出来的物理页交还系统。
        // 顺序不能反: 先松引用, 紧接着那次回收才有东西可收 —— 对用户就是"关掉视频内存数字马上掉"。
        ReleaseHeavyContent();
        MemoryTrim.RequestTrimAndRelease();
        // 把焦点/激活权交还主窗口, 避免关闭后主界面不响应输入。
        // 但如果此刻已经有新的播放器窗口接管(_window 已指向别人), 就绝不能抢焦点 ——
        // 否则新窗口会被挤到后台, 看起来就像"点了视频又立刻退出了"。
        if (Svc.Player.IsCurrent(this))
        {
            try { (System.Windows.Application.Current?.MainWindow)?.Activate(); } catch { }
        }
    }

    /// <summary>
    /// 停掉本窗口拥有的**全部** DispatcherTimer。
    ///
    /// ★ 这不是"顺手收拾一下", 而是防内存泄漏的关键一步, 每一个都不能漏:
    ///   DispatcherTimer 一旦 Start 就被 Dispatcher 长期持有, 而它的 Tick 委托几乎都捕获了 this ——
    ///   只要**有一个还在跑**, 整个已关闭的 PlayerWindow(连同它手里的弹幕列表、评论树、位图、
    ///   整棵可视树)就永远回收不掉。而播放器是"看完就关、下次再看新建一个"的, 于是
    ///   **每看一个视频就永久泄漏一个窗口**。
    ///
    /// 2026-09-27 实测: `_controlsHideTimer` 就是漏掉的那一个 —— 它只在 HideControls/ShowControls
    /// 里停, 而"刚动过鼠标(倒计时重启)就把窗口关掉"正好落在它还在跑的时刻。用户报的
    /// "看视频内存涨很多、关掉也不回落" 就是它攒出来的。
    ///
    /// ★ 2026-10-01: 清单本身已改成**反射收集**(见 TimerFields), 所以"加定时器必须回来补一笔"
    /// 这条规矩**作废了** —— 新加的定时器会被自动停掉。原先靠"探针里一条断言守这份白名单"
    /// 来兜底, 现在兜底换成了机制本身: 漏写不再可能, 探针那条断言也就不再是必需项。
    /// </summary>
    private void StopAllTimers()
    {
        foreach (var f in TimerFields)
        {
            // 防御: 构造函数可能在某个定时器初始化之前就抛了(LibVLC 加载失败时), 所以取不到就跳过
            if (f.GetValue(this) is DispatcherTimer t) t.Stop();
        }
    }

    /// <summary>
    /// 本类(以及它的基类链上, 直到 <see cref="Window"/>)声明的**全部** DispatcherTimer 字段。
    ///
    /// ★ 2026-10-01: 这里原来是一份手打的 8 行白名单, 漏一个就是泄漏一个窗口 ——
    ///   2026-09-27 实测漏掉的就是 `_controlsHideTimer`, 用户报"看视频内存涨很多、关掉也不回落"。
    ///   改用反射收集之后, **加定时器不必再记得回来改这里**, 这类 bug 从根上没了。
    ///
    /// 为什么按**字段类型**收集而不是按名字/按白名单:
    ///   · 按名字 ⇒ `_localToastTimer` 声明时写的是全限定名
    ///     `System.Windows.Threading.DispatcherTimer?`(不是短名), 白名单迟早漏掉它;
    ///   · 显式列全类型 ⇒ 谁新增都跑不掉, 漏不掉就是漏不掉。
    ///
    /// 遍历基类链是防"以后有人把某个定时器挪到 FluentWindow 上"; 只取本类的话挪过去就会失效。
    /// 类型化只扫一次(静态初始化), 运行期开销可忽略。
    /// </summary>
    private static readonly FieldInfo[] TimerFields = CollectTimerFields();

    private static FieldInfo[] CollectTimerFields()
    {
        var found = new List<FieldInfo>();
        for (var t = typeof(PlayerWindow); t != null && t != typeof(Window); t = t.BaseType)
        {
            found.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(f => typeof(DispatcherTimer).IsAssignableFrom(f.FieldType)));
        }
        return found.ToArray();
    }

    /// <summary>
    /// 关窗时主动松掉几处"占地大"的引用。
    ///
    /// 窗口整体被回收当然也能释放它们, 但那要等下一次 GC 真的跑到那个代;
    /// 这里先断开, 紧接着的那次 RequestTrimAndRelease 才有东西可收。
    /// 顺序上它是"先松手再回收"的前半截, 少了它回收基本收不到什么。
    /// </summary>
    private void ReleaseHeavyContent()
    {
        try { DanmakuCanvas?.Children.Clear(); } catch { }
        try { _danmaku.Clear(); } catch { }
        try { _danmakuRaw.Clear(); } catch { }
        _sponsorRaw = Array.Empty<SponsorSegment>();
        _sponsorActive = Array.Empty<SponsorSegment>();
        _sponsorHandled.Clear();
        if (CommentList != null) CommentList.ItemsSource = null;
        // 封面/头像的 ImageBrush 也是"挂着位图"的引用, 一并放掉
        if (UpAvatarBox != null) UpAvatarBox.Background = null;
    }

    /// <summary>音频链路诊断是否已经记过(每个片源只记一次)</summary>
    private bool _audioStateLogged;

    /// <summary>
    /// 每个片源只在**第一次** Playing 时记一条音频链路状态。
    ///
    /// 为什么需要它: "画面正常但没有声音"这类问题离线复现不了 —— 是没挂上音轨、还是音量被
    /// 压到 0、还是走了单文件源, 只有起播那一刻的原生状态能回答。Playing 之后 aout 才建立,
    /// 所以这个时机是唯一能读到有意义数值的地方(起播前原生音量读回 -1)。
    /// 记一次就够: Playing 在缓冲/seek 后会反复触发, 每次都写会把日志冲掉。
    /// </summary>
    private void LogAudioStateOnce()
    {
        if (_audioStateLogged) return;
        _audioStateLogged = true;
        try
        {
            WriteLibVLCDebug(
                $"[Audio] bvid={_currentBvid} cid={_currentCid} qn={_currentQn} " +
                $"音轨数={_mp.AudioTrackCount} 原生音量={_mp.Volume} 应用音量={_volumeCache} " +
                $"独立音轨={(string.IsNullOrEmpty(_lastAudioUrl) ? "无(单文件源)" : "有")}\n");
        }
        catch { /* 诊断信息拿不到就算了, 绝不影响播放 */ }
    }

    /// <summary>
    /// LibVLC 诊断日志(选流/初始化环境/播放失败时的上下文)。
    ///
    /// 两个发布前修掉的问题:
    ///   1. 旧版写到 <c>AppContext.BaseDirectory</c> —— 装进 Program Files 后该目录**不可写**,
    ///      日志会永远静默丢失; 现在写到数据目录(与 errors.txt 同一处)。
    ///   2. 旧版无限追加(开发机上已积到 600KB+); 现在超过 256KB 就整份重来 ——
    ///      这份日志只在"排查最近一次播放问题"时有价值, 老内容没必要留。
    /// </summary>
    private static void WriteLibVLCDebug(string content)
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDir, "vlc-debug.txt");
            var f = new FileInfo(path);
            if (f.Exists && f.Length > 256 * 1024) f.Delete();
            File.AppendAllText(path, content);
        }
        catch { /* 静默 */ }
    }
}
