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
    /// <summary>
    /// 播放器**只让右侧信息栏参与**全局软件缩放(2026-10-06 用户决定): 视频画面与顶栏都保持原尺寸。
    ///
    /// ★ 为什么不能缩整窗: 本窗口的**视频区是原生 HWND**(HwndHost), 而 LibVLCSharp.WPF 还会把
    ///   `VideoView.Content`(控制栏 + 弹幕层)**搬进一棵独立的顶层浮层可视树**。整窗加 LayoutTransform
    ///   时那棵浮层树跟不上, 实测会与视频区错位 195×186px(还正好差一个系统 DPI 的 1/1.25):
    ///   `.probes/bd-probe-uiscale-player`。视频 HWND 本身其实是**跟得上**变换的(实测 0px 偏差),
    ///   出问题的只有浮层 —— 但浮层里有控制栏, 错位不可接受。
    /// ★ 所以缩放的落点是 <see cref="InfoPanel"/>: 右侧那 400px 一列(标题/UP 主卡/操作栏/
    ///   简介与评论 Tab/评论列表, 以及盖在它上面的合集面板与 Toast)全在它里面。
    ///   宽度仍是父容器给的 400(固定), LayoutTransform 只把内部按 1/倍率 排版再放大 ⇒
    ///   字与控件变大、可见内容变少, 视频区尺寸一点不受影响。
    /// </summary>
    protected override FrameworkElement? ScaleRoot => InfoPanel;

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
    /// "落地但偏早"允许的**最大**偏早幅度(毫秒)。
    ///
    /// 真实偏早是秒级(实测 60s→55.2s = 4.8s、150s→145.7s = 4.3s), 取 30s 已经很宽松。
    /// 超过它就不是"落到前一个分片边界", 而是 seek 根本没落地(位置跑到片子别处去了)。
    /// ★ 2026-10-17 加这条的原因: 原来的判据没有上界, 把"落到开头 5s"(目标 329s)当成
    ///   "落地偏早"认账, 于是不重试 ⇒ 用户看到"拉过去又跳回去"。见 UpdateProgressUi。
    /// </summary>
    private const long SeekLandingMaxEarlyMs = 30_000;

    /// <summary>
    /// "落地但偏早"允许的**最小**偏早幅度(毫秒)。
    /// 与 <see cref="SeekConfirmWindowMs"/> 同量级: 比这更近就算"到位", 不必再区分。
    /// </summary>
    private const long SeekLandingMinEarlyMs = 3_000;

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
    /// 视频区手势层的常态底色(#01000000 —— 近透明但可命中鼠标)。
    ///
    /// ★★ 2026-10-13 的回归教训: 我曾为了"盖住 letterbox 白底"把它改成**不透明黑**,
    ///   结果用户报"视频播放时全黑"。层级搞反了 —— 这一层在 VideoView.Content
    ///   (浮层窗口)里, 位于 vout HWND **之上**, 不透明会盖住**视频画面**而不是白底。
    ///   想盖 vout 白底要用独立的遮罩层(EndOverlay / LoadingOverlay / 全屏黑幕那几层,
    ///   平时 Collapsed), 手势层必须保持近透明。
    ///
    /// EOF 时手势层会切黑盖住露出白底的 vout HWND(见 OnEndReached), 起播时用它还原。
    /// </summary>
    private static readonly Brush VideoAreaBackBrush = CreateVideoAreaBackBrush();

    // --- 全屏黑幕(见 ArmFullscreenCover 的说明) ---
    /// <summary>黑幕已压下、等待首个新帧撤掉</summary>
    private bool _fullscreenCoverArmed;
    private DispatcherTimer? _fullscreenCoverTimer;

    /// <summary>黑幕兜底超时(毫秒): 一直没等到新帧就自己撤掉, 免得挡住画面</summary>
    private const int FullscreenCoverTimeoutMs = 600;

    /// <summary>
    /// "拖动改大小期间的按住闸": 为真时 <see cref="ReleaseFullscreenCover"/> 拒绝撤罩。
    /// 用来保证遮罩从 WM_ENTERSIZEMOVE 一直压到 WM_EXITSIZEMOVE ——
    /// 拖动期间 LibVLC 会反复重协商 vout, 中途放行必然又露出白底(见 ArmResizeCover)。</summary>
    private bool _resizeHold;

    private static Brush CreateVideoAreaBackBrush()
    {
        // 近透明黑(#01000000): 见 VideoAreaBackBrush 的说明 —— 绝不能改成不透明,
        // 那会盖住视频画面(2026-10-13 实测回归: 用户看到整个画面全黑)。
        // 之所以不是完全透明: 背景全透明时鼠标事件会穿透, 手势就收不到了。
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

    /// <summary>当前视频是否已投过币(用于投币按钮的点亮态)</summary>
    private bool _isCoined;

    /// <summary>稍后再看的加入/移出进行中, 防止连点把 add/del 交叉发出去</summary>
    private bool _watchLaterBusy;
    /// <summary>操作栏计数(2026-09-26 起操作栏是"图标 + 数量", 不再是"点赞/投币/收藏"三个字)</summary>
    private long _likeCount;
    private long _coinCount;
    private long _favCount;
    private bool _isFavorited;
    // 当前视频标题(收藏夹选择窗的副标题用; 窗口 Title 带 " - BiliDesk" 后缀不能直接用)
    private string _videoTitle = "";

    /// <summary>
    /// 当前视频封面地址。「听视频」聆听态拿它当画面锚点。
    /// 跟 _videoTitle 一样属于"这一片"的属性, 换片必须清。
    /// </summary>
    private string _coverUrl = "";

    // --- 视频区手势: 单击播放暂停 / 双击全屏 / 长按快进 ---
    private DispatcherTimer? _longPressTimer;     // 检测长按 >= 500ms
    private DispatcherTimer? _singleClickTimer;   // 单击延迟执行(给双击留判定窗口)
    private bool _longPressFired;                  // 当前是否处于长按快进态
    // 双击抑制标记: 第二次点击(Down, ClickCount>=2)到达时置位, 让随后的 Up 不再启动单击定时器。
    // 没有这个标记时: Down 阶段虽然停掉了单击定时器, 但 Up 阶段又会新建一个,
    // 于是双击会先全屏、再在 ~200ms 后补一次播放/暂停 —— 就是"双击既暂停又全屏"的根因。
    private bool _suppressSingleClick;

    // --- 长按快进: **改倍速(SetRate)** ---
    //
    // ★★★ 2026-10-16 定稿: 长按快进回到 SetRate。曾经改过一次 seek 式, 已撤回。
    //
    // 为什么撤掉 seek 式(用户实机反馈"画面和音频均卡顿"):
    //   周期性向前 seek 每 400ms 就要重定位解复用器、重建 DASH 音视频两路、重解码关键帧,
    //   **画面和音频各被反复打断** —— 这是结构性的, 调参数救不了(实测平均 1.9x 也救不了手感)。
    //   而"倍速按钮 2x"是顺滑的(用户对照验证过): SetRate 只改 scaletempo 的 scale,
    //   不重建滤镜链、不打散解码 —— 顺滑度是 SetRate 天生就有的。
    //
    // SetRate 的代价: libvlc 在速率生效时会丢一次音频输出缓冲, 原生日志打
    //   `playback way too late: flushing buffers`。**实测(探针 bd-probe-rateguard2)这个代价比想象中小**:
    //     松手后真正的停顿只有 **0.25~0.5s**(吞吐比 0.79~1.12), 不是曾经以为的 2 秒。
    //     之前听到的"2 秒"其实是: 松手瞬间 OS 缓冲里还排着已解码音频,
    //     它仍在**以 2 倍速**播完(实测按住期间吞吐达正常 1.3x 的 3.4 倍)⇒ 听感像"还在快进",
    //     等它播空才是那一小段静音。
    //
    // ★ 三个"缓解"手段实测**全都更糟**, 别再试:
    //     · 松手前先压音量再改 rate(prewarm)  → 停顿 2.25s、吞吐比 0.01
    //     · 按住期间全程静音(muteheld)        → 停顿 2.00s、吞吐比 0.27
    //     · 松手不改 rate 只补一次 seek(norestore) → 停顿 1.50s
    //   即: 什么都不做(base, 停顿 0.25~0.5s)就是最优解。
    //
    // ★ 长按期间不改 SpeedText: 它是控制栏那个**播放速率**指示。长按的反馈交给 ShowSpeedHint 浮层。
    /// <summary>长按快进的倍速</summary>
    private const float FastForwardRate = 2.0f;

    /// <summary>当前是否处于"长按快进"中(即速率已被切成 <see cref="FastForwardRate"/>)。</summary>
    private bool _isFastForwarding;

    /// <summary>
    /// 长按快进开始: 切到 <see cref="FastForwardRate"/>。
    ///
    /// ★ 松手时要恢复到的速率由 <c>_rateBeforeFastForward</c> 记住 —— 不能写死 1.0f:
    ///   用户可能先用倍速按钮设了 1.5x, 写死会让松手后悄悄变成 1x。
    ///   (旧版就有这个 bug, 2026-10-16 一并对齐。)
    /// </summary>
    private float _rateBeforeFastForward = 1.0f;

    /// <summary>开始长按快进(改倍速)。见上方那段说明为什么用 SetRate 而不是 seek。</summary>
    private void StartFastForward()
    {
        if (_closing || _mp == null) return;
        // 直播没有"加速往前放"这回事(本来就一直在最新处); 播完了也没有
        if (_isLive || _ended) return;

        try { _rateBeforeFastForward = _mp.Rate; }
        catch { _rateBeforeFastForward = 1.0f; }
        if (_rateBeforeFastForward <= 0) _rateBeforeFastForward = 1.0f;
        // 已经在快进速率上(理论上到不了): 别把"该恢复的速率"记成 2x
        if (Math.Abs(_rateBeforeFastForward - FastForwardRate) < 0.01f) _rateBeforeFastForward = 1.0f;

        _isFastForwarding = true;
        // ★ 顺序: 先让弹幕时钟按新倍速落定当前值, 再真正 SetRate(同 OnSpeedClick 那条说明)
        SyncDanmakuClockRate(FastForwardRate);
        try { _mp.SetRate(FastForwardRate); } catch { StopFastForward(); }
    }

    /// <summary>停止长按快进: 恢复按下前的速率。</summary>
    private void StopFastForward()
    {
        if (!_isFastForwarding) return;
        _isFastForwarding = false;
        // ★ 关窗路径不恢复速率: 那里 _closing 已置位, 而且这个窗口的 MediaPlayer
        //   马上就要被 Dispose —— 再去碰它是"访问已释放原生指针"的来源(0xc0000005),
        //   恢复了也没有意义(播放器跟窗口一起没了)。
        if (_closing || _mp == null) return;
        var back = _rateBeforeFastForward > 0 ? _rateBeforeFastForward : 1.0f;
        // 同 StartFastForward: 先落定弹幕时钟, 再改速率
        SyncDanmakuClockRate(back);
        try { _mp.SetRate(back); } catch { /* 播放器已失效 */ }
    }

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
        // ★ 两个弹层的"开着"状态本身就是判据(见 AnyPlayerPopupOpen), 不需要订阅 Closed。
        //   2026-10-13 第一版在这里订阅过 Closed 记时间窗, 实测证明 Closed 晚于视频区的
        //   Up(70ms), 时间窗一次都没生效过 —— 已删除。
        // 主题无需在此订阅: FluentWindow 基类已监听 ThemeChanged 并重建窗体 chrome,
        // 播放器自身的 DynamicResource 会随之自动刷新。之前这里多订阅了一次且从不退订,
        // 导致每个关闭过的播放器窗口都无法被 GC 回收。
        // 弹幕设置(全局): 初始化运行时状态, 并订阅设置页的变更
        _danmakuOn = Svc.Settings.DanmakuEnabled;
        _danmakuColorful = Svc.Settings.DanmakuColorful;
        _danmakuAreaRatio = Svc.Settings.DanmakuAreaPercent / 100.0;
        Svc.Settings.DanmakuSettingsChanged += OnDanmakuSettingsChanged;
        // 外观三项(不透明度/字号/速度)在此**首次**从设置里读出来。
        // 构造函数里字段还是编译期默认值(80% / 100% / 适中), 不走这一步的话,
        // 那些把旧值存进配置的用户(比如不透明度存的是 40%)开播放器会先按默认值起播,
        // 直到下一次改动才纠正 —— 表现为"设置里明明是 40%, 播放器却先亮一下再变暗"。
        ApplyDanmakuLook();
        UpdateDanmakuLookTexts();

        // 相关视频卡片点一下就换片(2026-10-12)。命令在 XAML 的 DataTemplate 里绑,
        // 所以必须是窗口的**属性**(DataContext 就是 this), RelativeSource 才找得到。
        PlayRelatedCommand = new Helpers.RelayCommand(PlayRelated);

        // 简介折叠要在**宽度确定之后**才判得出"要不要显示展开按钮", 而那一刻还没布局完
        // (Loaded 时 ActualWidth 可能仍是 0)。SizeChanged 才是可靠信号。
        DescText.SizeChanged += (_, _) => UpdateDescFold();

        // 跳过赞助片段: 开关/类别一变就重算(关掉要立刻停, 改类别要立刻生效)
        Svc.Settings.SponsorBlockChanged += OnSponsorSettingsChanged;        // 进度条色块是按像素摆的, 控件一改宽度就得重画(窗口缩放、切全屏、控制栏重排都会触发)。
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
            // 新 input 的 vout 已经起来, 后面要能看见画面。
            // ★★ 必须走 <see cref="ReleaseFullscreenCover"/> 而不是直接赋值(2026-10-13 修):
            //   原来这里是 `VideoAreaRoot.Background = VideoAreaBackBrush;` + 顺手把
            //   `_fullscreenCoverArmed = false;` —— 两条都**绕过了 _resizeHold 闸门**。
            //   后果: 拖边缘 resize 期间 LibVLC 反复重协商 vout, 每出一帧就触发一次 Playing
            //   ⇒ 遮罩被撤掉、露出黑底, 下一轮重协商又压黑 —— 用户看到的就是
            //   "视频区边缘黑/亮交替抽动"(同一道闸本来就是为了这个才加的, 见 ArmResizeCover)。
            //   顺带还解释了"拖动时偶尔能看到一块白": 撤罩那一瞬 vout 还没帧, 白底露出来。
            ReleaseFullscreenCover();
            // ★ 视频没填满时的白色边带(2026-10-17): 起播后要把那块原生窗口底色刷黑。
            //   ★★ 不能只调一次就完事: 实测 `Playing` 触发时 libvlc 的 `Static` 子窗口
            //      **还没建出来**(那一刻拿到的句柄是 0, 修复静默跳过), vout 建好之后才有。
            //      所以这里起一个**有限次重试**的定时器, 命中即停。
            //   ★ 用定时器而不是单个 BeginInvoke: vout 建立耗时随片源/网速变化(实测 200ms~1s),
            //     排一拍常常还是太早。重试上限 10 次 × 120ms ≈ 1.2s, 足够覆盖;
            //     命中后立刻停, 不常驻。
            StartVideoBackdropFix();
            UpdatePlayButton();
            StartControlsHideCountdown();   // 播放中才开始倒计时自动隐藏
            // 聆听态: 起播后把文案里的秒数对齐到真实位置(切流前后 _mp.Time 会有几十毫秒偏差),
            // 并确保声波律动已经在跑(切清晰度/换片都会重建 Media, 但定时器是窗口级的, 不会断)。
            if (_isListenMode)
            {
                UpdateListenLabel();
                StartListenWave();
            }
            if (_resumeAfterSwitchMs > 0)
            {
                var resume = _resumeAfterSwitchMs;
                _resumeAfterSwitchMs = 0;
                try { _mp.Time = resume; } catch { }
                // 走一次 MarkSeekIssued: 钉住进度条/时间显示, 并启动 seek 看门狗,
                // 否则恢复进度那一下也可能被回写顶回 0(表现是"切完清晰度从头播")
                MarkSeekIssued(resume);
                // ★ 重建后 libvlc 会"接受目标→又退回低值"来回震, 单次设 Time 不够,
                //   这里起一个短间隔补跳把它压住(见 KickRebuildSeek 的说明)。
                KickRebuildSeek(resume);
            }
        }));
        // 暂停/停止时同步按钮图标(否则起播后图标停留在初始状态, 看起来像"反了")
        _mp.Paused += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing) return;
            // ★ 弹幕时钟跟着视频冻结/解冻(用户要求"视频暂停时弹幕不动")。
            //   ★ LibVLCSharp 的 `Paused` 事件签名是 **EventHandler<EventArgs>**(参数里没有
            //     "新的暂停态"), 而 `MediaPlayer` 也**没有** `IsPaused` 属性 —— 只有 `IsPlaying`。
            //     播完(EndReached)时 IsPlaying 同样为 false, 顺带把弹幕也停了, 正合需要。
            //     这里的一次原生调用没问题 —— 暂停是低频事件, 与逐帧路径(必须零原生调用)
            //     不在一条路上。
            try { SetDanmakuClockRunning(_mp.IsPlaying); } catch { /* 原生对象可能已失效 */ }
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

        // ★★★ vout 建立/重建的那一刻就把原生窗口底色处理掉(2026-10-17, 修"最大化/还原后白块")。
        //   为什么必须要这个事件、不能只靠定时器: libvlc 的 `Static` 子窗口是**新建**出来的,
        //   它第一次上屏时就已经是白的了 —— 定时器最快也要 120ms 之后才轮到, 那 120ms
        //   在浅色主题下就是用户看到的那一下"白块"。何况最大化/还原/换清晰度都会重建它。
        //   ⇒ 挂在这里: vout 一出现就立刻刷黑 + 起一轮短时守护。
        // ★★ 这里**故意同步执行、不走 Dispatcher**(与别的事件处理器不同):
        //   `Vout` 在 libvlc 的 vout 线程上触发, 而"改类背景刷"是纯 Win32 调用,
        //   不碰任何 WPF 对象 —— 走 Dispatcher 会把动作推迟到下一轮消息循环, 就晚了。
        //   原生回调里抛异常会直接终结进程, 所以整段包在 try 里。
        // ★ 参数 `Count` = 当前 vout 个数; 为 0 表示 vout 被销毁, 不必处理。
        _mp.Vout += (_, e) =>
        {
            if (_closing || e.Count <= 0) return;
            try
            {
                var hwnd = FindVideoStaticWindow();
                if (hwnd != IntPtr.Zero) VideoBackdropInterop.PaintClassBackgroundBlack(hwnd);
            }
            catch { /* 原生回调里绝不能抛 */ }

            // UI 线程上再起一轮短时守护(兜住"vout 稍后才建好"与"稍后又被重绘"）
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_closing) return;
                    StartVideoBackdropFix();
                }));
            }
            catch { /* 窗口正在销毁 */ }
        };

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

        // 音量: 从设置里读"上一次调到的音量"(2026-11-12 音量继承)。
        // ★ 这里必须是**读设置**, 不能写死 80 —— 写死的表现是"每个视频都要重调一遍音量",
        //   而用户调过的偏好应该跟着走到下一个视频。
        // ★ 也**不能**只依赖"复用同一个窗口时不重置": PlayerService 关窗重开时
        //   构造路径同样会跑到这里, 那时读的仍是设置, 所以两条路都对。
        _volumeCache = Svc.Settings.PlayerVolume;
        try { _mp.Volume = _volumeCache; } catch { }
        // ★★★ 滑块的 Value 也必须一起给(2026-11-12 用户报"进入下一个视频时音量条仍是 80%"):
        //   XAML 里写死 `Value="80"`, 而上面只改了 _volumeCache 与原生音量, **没碰滑块** ——
        //   于是声音确实是继承的那个值, 滑块却停在 80, 用户看到的就是"没继承"。
        //   ★ 必须放在 _mp 就绪之后: 滑块此时已在可视树里, 赋 Value 会触发 OnVolumeChanged,
        //     那条路会再写一次 _volumeCache/原生音量(同值回写, 无副作用), 正好对齐。
        if (VolumeSlider != null) VolumeSlider.Value = _volumeCache;

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

    // ------------------------------------------------------------ 简介折叠 + 相关视频(2026-10-12)

    /// <summary>点相关视频卡片: 换成那个视频</summary>
    public RelayCommand PlayRelatedCommand { get; }

    /// <summary>简介是否处于"展开"态。默认 false = 折叠(短简介看不出来, 长简介默认收起)。</summary>
    private bool _descExpanded;

    /// <summary>相关视频的加载代次: 换片后作废上一片的在飞请求</summary>
    private int _relatedGen;

    /// <summary>简介折叠后的最大高度(与 XAML 里 DescCollapsedHeight 同一个数)</summary>
    private const double DescCollapsedHeight = 108;

    /// <summary>
    /// 「展开简介」按钮。
    ///
    /// ★★ 判据必须是"文本**不限高**时需要多高", 所以 Measure 之前**必须先把 MaxHeight 摘掉**:
    ///   WPF 的 Measure 会遵守 MaxHeight —— 折叠态下 MaxHeight 就是 108,
    ///   于是 DesiredSize.Height 拿回来恒等于 108, 与阈值比较永远不成立
    ///   ⇒ 按钮**永远不出现**(实测: 40 行简介 + 展开按钮 Collapsed + DesiredHeight=108)。
    ///   这个坑很隐蔽: 逻辑读起来完全合理, 只有真跑一次界面才暴露。
    /// ★ 所以下面那三步的顺序不能换: 备份 → 摘掉 → 量 → 装回。
    /// ★ 另一个容易写错的替代品是读 ActualHeight: 折叠态下它也被 MaxHeight 夹住,
    ///   同样永远判不出"超没超"。必须用无上限的那次测量。
    /// ★ 宽度未知(窗口还没布局)时直接隐藏按钮: 那时的 0 宽度会算出 0 高度,
    ///   判成"不溢出"就对了, 但纯属巧合, 所以这里显式处理而不是让它自然走完。
    /// </summary>
    private void UpdateDescFold()
    {
        if (DescText == null || BtnDescFold == null) return;

        var width = DescText.ActualWidth;
        if (width <= 1)
        {
            // 还没量到宽度: 先不显示。等布局跑完由 SizeChanged 再调一次。
            BtnDescFold.Visibility = Visibility.Collapsed;
            return;
        }

        // ★ 临时摘掉 MaxHeight 再量 —— 见方法说明。try/finally 不能省:
        //   Measure 抛异常时若没装回去, 简介就会永远失去高度上限(撑满整屏)。
        var savedMax = DescText.MaxHeight;
        var needed = 0d;
        try
        {
            DescText.MaxHeight = double.PositiveInfinity;
            DescText.Measure(new Size(width, double.PositiveInfinity));
            needed = DescText.DesiredSize.Height;
        }
        finally
        {
            DescText.MaxHeight = _descExpanded
                ? (needed > DescCollapsedHeight ? needed : DescCollapsedHeight)
                : DescCollapsedHeight;
        }

        // +1 容差: 亚像素误差会让"刚好 6 行"被误判成没超, 按钮该出时不出。
        var overflows = needed > DescCollapsedHeight + 1;
        BtnDescFold.Visibility = overflows ? Visibility.Visible : Visibility.Collapsed;
        // 短简介没有"展开"可言, 状态复位, 否则下次换片会带着一个无意义的展开态
        if (!overflows) _descExpanded = false;

        ApplyDescFoldText();
    }

    /// <summary>
    /// 「展开/收起」那行文字的**唯一**写法点(文案 + 符号一起换方向)。
    ///
    /// ★ 为什么单独抽出来: 符号(⌄/⌃)与文案必须在**同一次**调用里同步更新,
    ///   而它们分属两个不同的对象(TextBlock 与 Run)。留在 UpdateDescFold 里就地写没问题,
    ///   但一旦以后新增第二个入口(比如键盘快捷键/右键菜单)忘了改符号方向,
    ///   就会出现"点了展开, 文字说收起、箭头还朝下"这种自相矛盾的按钮。
    /// </summary>
    private void ApplyDescFoldText()
    {
        if (BtnDescFoldLabel != null) BtnDescFoldLabel.Text = _descExpanded ? "收起简介" : "展开简介";
        // ⌄ = 往下展开, ⌃ = 往上收起
        if (BtnDescFoldGlyph != null) BtnDescFoldGlyph.Text = _descExpanded ? "⌃" : "⌄";
        if (BtnDescFold != null) BtnDescFold.ToolTip = _descExpanded ? "收起简介" : "展开完整简介";
    }

    /// <summary>展开/收起简介</summary>
    private void OnDescFoldClick(object sender, RoutedEventArgs e)
    {
        _descExpanded = !_descExpanded;
        UpdateDescFold();
        // 展开后要把焦点挪开: 按钮在浅色主题下有焦点框, 停在上挺难看;
        // 而且按空格(播放/暂停)会打在按钮上而不是视频。
        DescText.Focus();
    }

    /// <summary>
    /// 载入当前视频的相关视频。
    ///
    /// ★★ 2026-10-13 用户报"相关视频只有 8 个, 添加更多视频, 往下滑刷新":
    ///   接口一次就返 40 条且翻页无效(实测 ps/pn 都不影响, 见 GetRelatedAsync 的说明),
    ///   所以这里把**全量 40 条**存在 <see cref="_relatedAll"/>, 界面只挂前
    ///   <see cref="RelatedPageSize"/> 条; 滚到附近时由 <see cref="ShowMoreRelated"/>
    ///   再补一批。列表用 ObservableCollection, 追加即刷新, 不重排已有卡片。
    /// </summary>
    private async Task LoadRelatedAsync(string bvid)
    {
        if (_closing || string.IsNullOrEmpty(bvid)) return;

        // 直播/本地文件没有"相关视频"这回事(见 LoadLiveAsync 里对 DescText 的处理)
        if (RelatedPanel == null || RelatedList == null) return;

        var gen = ++_relatedGen;

        // 先清空再转圈: 不清的话上一次的卡片会一直挂在界面上, 而那批推荐与当前视频无关。
        _relatedAll = new List<VideoItem>();
        _relatedShown = 0;
        RelatedList.ItemsSource = null;
        RelatedPanel.Visibility = Visibility.Visible;
        RelatedLoadingRing.Visibility = Visibility.Visible;
        RelatedHint.Text = "正在加载…";

        var (ok, err, items) = await Svc.Api.GetRelatedAsync(bvid);
        // 代次比对: 换片后这批结果就不属于当前视频了, 丢掉(与 ClearDanmaku 那套同款闸门)
        if (_closing || gen != _relatedGen) return;

        RelatedLoadingRing.Visibility = Visibility.Collapsed;
        RelatedHint.Text = "";

        if (!ok || items == null || items.Count == 0)
        {
            // ★ 失败/为空时把整块藏起来, 而不是留一个空标题 + 一句报错:
            //   "相关视频"下面是空的比不显示更糟(看着像功能坏了)。
            //   真的想给用户一个交代, 那该走 Toast, 而这属于后台补充信息, 不该打扰。
            RelatedPanel.Visibility = Visibility.Collapsed;
            // 只写 errors.txt 不弹窗: 相关视频是后台补充信息, 失败不该打断看片。
            // 包一层 Exception 是因为 App.ReportError 只收 Exception(它的唯一用途就是落盘),
            // 这里不去动那个公共签名 —— 加一个 string 重载会让人以为"失败可以不记录"。
            App.ReportError(new InvalidOperationException($"相关视频加载失败({bvid}): {err ?? "未知错误"}"));
            return;
        }

        _relatedAll = items;
        // 先挂第一批(与旧行为一致: 打开播放器先看到 8 条, 不一下铺满 40 条)
        _relatedShown = Math.Min(RelatedPageSize, items.Count);
        RelatedList.ItemsSource = items.GetRange(0, _relatedShown);
    }

    /// <summary>
    /// 信息栏滚动: 滚到相关视频附近时把下一批补上(用户要求"往下滑刷新出更多相关视频")。
    ///
    /// ★ 判据是"相关视频列表的**底部**离视口底部还有不到一屏", 不是"滚到底":
    ///   等用户真的滚到最底才补, 会先出现"列表空了半屏"的空白期, 看着像没有了。
    /// ★ 只在往下滚的方向补: 往上滚回来时不该再 append(已经补过的就在那儿)。
    ///   <see cref="ShowMoreRelated"/> 本身幂等, 这里加方向判断是省掉无谓的 ItemsSource 重挂 ——
    ///   每次重挂都会让 ItemsControl 重新生成全部容器, 一屏卡片会闪一下。
    /// </summary>
    private void OnInfoScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_closing || _relatedShown >= _relatedAll.Count) return;
        // 换片后 _relatedAll 是空的, 上面的闸门已经挡住; 这里再确认面板是显示的,
        // 免得在"简介 tab"下也补(相关视频属于简介 tab, 评论 tab 下整块是隐藏的)。
        if (RelatedPanel == null || RelatedPanel.Visibility != Visibility.Visible) return;
        // 往上滚不补
        if (e.VerticalChange < 0) return;

        if (sender is not ScrollViewer sv) return;
        // 相关视频列表在可视区里的位置: 它的底部距 ScrollViewer 内容底部还有多远
        var targetBottom = RelatedList.TransformToVisual(sv)
            .Transform(new Point(0, RelatedList.ActualHeight)).Y;
        // 距"还要再滚一屏才看得见"的阈值内就补下一批
        if (targetBottom - (sv.VerticalOffset + sv.ViewportHeight) < sv.ViewportHeight)
            ShowMoreRelated();
    }

    /// <summary>相关视频每批展示几条(滚动到附近时按这个量追加)</summary>
    private const int RelatedPageSize = 8;

    /// <summary>当前视频的相关视频**全量**(接口一次给 40 条; 界面只挂前 <see cref="_relatedShown"/> 条)</summary>
    private List<VideoItem> _relatedAll = new();

    /// <summary>当前已经挂到界面上的条数; 换片时归零</summary>
    private int _relatedShown;

    /// <summary>
    /// 再展示一批相关视频(信息栏滚到相关视频附近时调)。
    ///
    /// ★ 幂等 + 到顶即停: 已经全展示完时什么都不做, 不会重复 append。
    /// ★ 换片后 <see cref="_relatedAll"/> 是空的, 这里直接返回 —— 不会把上一片的
    ///   推荐补进新片的列表(那是"点下去跳到不相干视频"的老 bug)。
    /// </summary>
    private void ShowMoreRelated()
    {
        if (_closing || _relatedShown >= _relatedAll.Count) return;
        var next = Math.Min(_relatedShown + RelatedPageSize, _relatedAll.Count);
        var shown = _relatedAll.GetRange(0, next);
        _relatedShown = next;
        RelatedList.ItemsSource = shown;
    }

    /// <summary>相关视频卡片的点击: 切到那个 bvid</summary>
    private void PlayRelated(object? param)
    {
        // ★ 必须真的转成 string 再用: CommandParameter 从 XAML 过来是 object,
        //   直接拿它和 _currentBvid 比是**引用比较** —— 编译器会警告 CS0252,
        //   而运行时几乎永远不相等, 表现为"点了相关视频没反应"。
        var bvid = param as string;
        if (_closing || string.IsNullOrEmpty(bvid)) return;
        // 点自己没意义(接口那边已经滤掉了, 这里再挡一道是防"同一片快速点两次")
        if (bvid == _currentBvid) return;
        PlayVideo(bvid);
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
            // 直播没有"相关视频"(接口只认视频稿件 bvid), 直接把整块藏掉,
            // 否则会停在上一片残留的推荐列表上 —— 那是**别的视频**的推荐。
            ClearRelatedPanel();
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
    /// 清空并隐藏「相关视频」整块。
    ///
    /// ★ 必须在每条**没有在线上下文的**播放路径上调用(直播 / 本地文件 / 换片起点):
    ///   否则上一批推荐会一直挂在界面上, 而那批是**别的视频**的推荐 ——
    ///   点下去跳到不相干的视频, 却没有任何迹象说明它过期了。
    /// ★ 顺带把代次 +1: 让还在飞的请求回来时立刻作废, 不会把旧结果又灌进来。
    /// </summary>
    private void ClearRelatedPanel()
    {
        _relatedGen++;
        // 换片/切直播: 缓存也一起丢。不清的话 OnInfoScrollChanged 里的闸门
        // (_relatedShown >= _relatedAll.Count) 会看到上一片还有"没展示完"的条数,
        // 于是把上一片的推荐补进新片的列表 —— 那是"点下去跳到不相干视频"的老 bug。
        _relatedAll = new List<VideoItem>();
        _relatedShown = 0;
        if (RelatedList != null) RelatedList.ItemsSource = null;
        if (RelatedPanel != null) RelatedPanel.Visibility = Visibility.Collapsed;
        if (RelatedLoadingRing != null) RelatedLoadingRing.Visibility = Visibility.Collapsed;
        if (RelatedHint != null) RelatedHint.Text = "";
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
        // 本地文件没有 bvid, 自然也没有相关视频
        ClearRelatedPanel();
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
        // ★ 分段跳必须立刻停: 它带着"上一条视频的时间轴目标", 不停的话新片会继续
        //   一段段往前冲(2026-10-17 加分段跳时同步加的)。字段本身另有登记(PerMediaStateFields)。
        StopSegmentedSeek();
        _indexInLib = 0;
        // 换片: 音频链路诊断重新记一次(每个片源一条, 见 LogAudioStateOnce)
        _audioStateLogged = false;
        // 「听视频」的音轨地址属于上一条视频 —— 不清的话换成新片时会拿旧音轨去播。
        // ★ _isListenMode 本身**不清**(登记在 PersistentFields): 用户切到纯音频连听几集是常态;
        //   新片的音轨地址在 LoadVideoAsync 里重新快照(见 ApplyListenAudioUrl)。
        _listenAudioUrl = null;
        _listenResumeMs = 0;
        _listenWaveTick = 0;
        _coverUrl = "";
        // ★ 每个片源开始时都把"要请求的清晰度"快照成设置里的「默认画质」(2026-10-04)。
        //   放在这里而不是只放构造函数里, 是为了让"设置页改完 → 点开下一个视频"立刻生效,
        //   不必重启。
        ApplyPreferredQuality();
        _danmaku.Clear();
        _danmakuRaw.Clear();
        // 相关视频同属"上一条视频"的界面内容: 换片瞬间就必须清掉, 不能等新片的
        // 异步请求回来 —— 那中间几百毫秒里, 界面上挂着的还是**上一条**的推荐,
        // 用户一点就跳到一个跟当前标题完全对不上的视频。
        ClearRelatedPanel();
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
        // 下载状态属于"上一条视频"(见 UpdateActionCounts 里那个绿色终态)
        _cacheDownloaded = false;
        // 评论总数属于"上一条视频" —— 不清的话换了片子那句"共 N 条评论"还挂着旧数字,
        // 而排序入口的可见性正是跟着它走的。排序**方式**(_commentSort)不清: 那是用户习惯。
        _commentTotal = 0;
        UpdateCommentSortUi(visible: false);
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
        // ★ 长按快进也要收: 若停在长按态换片, MediaPlayer 上的速率会**跟着留到新片子**
        //   (速率是 MediaPlayer 的属性, 不随 Media 重置 —— 实测)。所以这里必须先恢复。
        //   顺带把 _longPressFired 清掉 —— 否则新片的第一次 Up 会走进"结束快进"分支。
        StopFastForward();
        _longPressFired = false;
        // 复位"该恢复到几倍速"的记录: 它只描述**本片**那次长按, 不该带到下一片
        // (下一片起播速率由 _mp.Rate 自己决定, 这里只是清掉上一个手势的账)。
        _rateBeforeFastForward = 1.0f;
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
        // 「听视频」的可用性同样取决于那两个标志(直播/本地文件没有独立音轨)。
        // ★ 必须在 _isListenMode 已经确定之后调: 它内部会在"不支持聆听"时顺手退出聆听态,
        //   否则聆听遮罩会盖在一个根本没有音轨的本地文件上。
        UpdateListenButtonState();
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

        // 分P: 与合集同款处理 —— 换片先把上一片的分P 列表整个丢掉, 入口按钮也收起来,
        // 等新详情到了由 ApplyPages 重新决定要不要露出来。
        // 弹层强制关掉: 分P 弹层是 Popup(独立 HWND), 不关的话它会**浮在下一条视频上面**,
        // 里面还是上一条的分P 列表 —— 而合集面板是窗口内的 Grid, 收起就跟着一起没了。
        _pages = new List<VideoPage>();
        _hasPages = false;
        ClosePagesPopup();
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
        nameof(_isInWatchLater), nameof(_watchLaterBusy), nameof(_isCoined),
        nameof(_likeCount), nameof(_coinCount), nameof(_favCount), nameof(_cacheDownloaded),
        // 播放源标识
        nameof(_isLocalPlayback), nameof(_isLive), nameof(_liveRoomId), nameof(_currentCid),
        nameof(_aid), nameof(_ownerMid), nameof(_ownerName), nameof(_ownerFace),
        nameof(_videoTitle), nameof(_currentQn), nameof(_currentBvid),
        nameof(_coverUrl),           // 封面属于"这一片"(聆听态的画面锚点)
        // 「听视频」的音轨地址与恢复进度: 换片后要重新取, 留着上一条的会播错东西
        nameof(_listenAudioUrl), nameof(_listenResumeMs), nameof(_listenWaveTick),
        nameof(_lastPlayUrl), nameof(_lastAudioUrl),   // 出错诊断日志要打"当时正在播的地址"
        // 进度 / 时长 / EOF 自救
        nameof(_totalMs), nameof(_lastKnownMs), nameof(_eofResumeCount), nameof(_lastEofResumeMs),
        nameof(_resumeAfterSwitchMs), nameof(_ended), nameof(_anchorMs), nameof(_anchorWall),
        nameof(_displayedMs),
        // 跳转看门狗
        nameof(_pendingSeekMs), nameof(_seekDeadline), nameof(_seekRetries),
        nameof(_seekObservedMs), nameof(_seekElsewhereSeenMs), nameof(_seekFromMs),
        nameof(_seekGraceUntil), nameof(_seekMuteSavedVolume), nameof(_isSeeking),
        // 分段跳(2026-10-17): 它属于"这一片"的跳转过程 —— 换片时上一次的分段目标必须作废,
        // 否则新片会继续按旧目标一段段往前冲(目标还是上一条视频的时间轴)。
        nameof(_segSeekTarget), nameof(_segSeekRunning),
        nameof(_segSeekStepIssuedMs), nameof(_segSeekStepFromMs), nameof(_segSeekStepDeadline),
        // 重建后补跳的状态: 同样是"这一次跳转"的过程量
        nameof(_rebuildSeekTarget), nameof(_rebuildSeekTries),
        nameof(_rebuildSeekStable), nameof(_rebuildRebuilds),
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
        // 长按快进的状态与 _longPressFired 同类(都是"这一次手势"的瞬时态): 换片必须复位。
        // ★ _rateBeforeFastForward 也登记在这里 —— 它决定"松手恢复到几倍速",
        //   不跟着换片复位的话, 新片子的第一次长按会恢复到上一片记下的速率。
        nameof(_isFastForwarding), nameof(_rateBeforeFastForward),
        nameof(_commentTotal),
        nameof(_suppressSingleClick), nameof(_controlsHidden), nameof(_isFullscreen),
        // 合集: 「这个视频有没有合集」属于"这一片"的属性, 换片必须重算。
        // 不清的话换了普通视频入口还挂着, 点开是个空面板。
        // (_currentSeasonEpisode / _season 是引用类型, 自检只审值类型与 string, 不在清单里;
        //  但它们在 ResetForNewMedia 里同样被清了 —— 清单只负责"别漏", 不负责穷举。)
        nameof(_hasSeason), nameof(_switchingSeasonEpisode),
        // 分P 与合集是一对: 同样是"每片重算 + 换片闸门"。别只加一个漏一个。
        nameof(_hasPages), nameof(_switchingPage),
    };

    /// <summary>
    /// 明确**跨片保持**的值类型字段 —— 不该进每片清单, 也不该被自检点名。
    /// 分类错了自检会报出来, 改这里即可, 别去动 <see cref="PerMediaStateFields"/> 消警告。
    ///
    /// ★ 这张表装两种东西, 别混淆:
    ///   ① **用户偏好/设置**(音量、弹幕开关、排序方式…)—— 换片必须保持, 清了是 bug;
    ///   ② **窗口级的瞬态与订阅标记**(关闭标记、黑幕闸、Rendering 订阅…)—
    ///      它们活不到换片那一刻, 或者由事件自动收敛, 所以"不需要清"。
    ///   两种都不该被自检点名, 但只有 ① 是"必须保持"的语义。
    /// </summary>
    private static readonly string[] PersistentFields =
    {
        nameof(_volumeCache),        // 音量是用户偏好, 换片不该重置
        nameof(_wheelAccum),         // 滚轮音量累计, 瞬时量
        nameof(_danmakuOn),          // 弹幕总开关是设置项
        nameof(_danmakuColorful),    // 彩色弹幕是设置项
        nameof(_danmakuAreaRatio),   // 弹幕显示区域是设置项
        nameof(_laneHeight),         // 泳道高随字号走, 跟着设置走
        // ★ 外观三项(2026-10-12)同理是**用户偏好**: 换片时必须保持, 清了就等于"换个视频
        //   弹幕又变回默认大小/速度"—— 那是用户马上会报上来的 bug。
        //   与 _danmakuAreaRatio 同一类, 三项+泳道高都是设置驱动的派生量。
        nameof(_danmakuFontSize), nameof(_danmakuOpacity), nameof(_danmakuSpeedLevel),
        nameof(_danmakuScrollSeconds), nameof(_danmakuFixedSeconds),
        nameof(_teardownDone),       // 拆解只做一次
        nameof(_playerRegistered),   // 注册计数, 生命周期是窗口而非片源
        nameof(_seasonOpen),         // 合集面板的展开态: 用户开着面板连看几集是常态, 换片收起它很烦
        nameof(_isFillWindow),       // 「铺满窗口」是窗口级的显示模式(同 _isFullscreen), 换片不该把它弹回去
        nameof(_commentSort),        // 评论排序是用户看评论的习惯(同 _seasonOpen): 选过一次就一直是那个顺序
        nameof(_isListenMode),       // 「听视频」是窗口级的播放模式(同 _isFullscreen): 换片不该把它弹回视频
        // hover 预览锁是"上一次操作的瞬时反馈"(750ms 自己过期, 与片源无关), 不属于任何一片
        nameof(_actionHoverLockUntil),
        // ---- 2026-10-12 补登记: 以下 8 个都是**窗口级**的瞬态/订阅标记, 换片不该清 ----
        //
        // ★★ 为什么它们不属于"每片状态": 自检只问"这个字段换片时该不该清", 而答案可以是
        //   "不该"—— 登记在这里就是把这个答案固化下来, 免得后人看到 errors.txt 又去
        //   ResetForNewMedia 里补一堆实际不需要的重置代码(那反而会引入新 bug)。
        //
        // 关闭流程标记(见字段声明处的说明): 窗口生命周期, 与片源无关
        nameof(_closing),
        // 全屏黑幕/拖动按住闸: 都是**百毫秒级**的窗口显示瞬态 ——
        //   黑幕压下来等首个新帧就撤(FullscreenCoverTimeoutMs = 600ms 兜底),
        //   不可能活到用户换下一个视频。留着 false 的初值即可, 清了没有任何可观察差异。
        nameof(_fullscreenCoverArmed),
        nameof(_resizeHold),
        // 手势"这一下是否由 Down 起步"的标记(左键/右键各一份): 它们描述的是**当前这一次点击**,
        //   窗口关闭/换片时鼠标多半早已抬起, 留着初值 false 即可;
        //   真按住不放时换片本来就是不可能的操作, 清了也没有可观察差异。
        nameof(_videoGestureActive), nameof(_rightGestureActive),
        // CompositionTarget.Rendering / 弹幕时钟的订阅只挂一次, 属窗口级(同 _playerRegistered)
        nameof(_modalSizeWatchAttached),
        nameof(_danmakuClockAttached),
        // 弹幕时钟的三个量: 靠 _mp.TimeChanged(约 4Hz)自动校准 —— 换片后新片一开播,
        //   下一次 TimeChanged 就把 _dmClockMedia 推到新片时间, 260ms 内自然收敛。
        //   ★ 特意**不**在 ResetForNewMedia 里清: 那会与 CalibrateDanmakuClock 抢写,
        //     而后者才是权威来源(2026-11-11 为修"暂停回跳"刚把权威收敛到它)。
        nameof(_dmClockMedia), nameof(_dmClockStamp), nameof(_dmClockRunning),
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

    /// <summary>
    /// XAML 编译器自动生成、但**不带任何特性标记**的字段名 —— 必须显式排除。
    ///
    /// ★★ 为什么不能靠特性过滤(2026-10-12 实测):
    ///   `PlayerWindow.g.cs` 生成的 `private bool _contentLoaded;` 是 `InitializeComponent()`
    ///   的防重入标记。它与手写字段同属一个部分类, 所以 `DeclaredOnly` 拦不住;
    ///   类型正好是 bool, 于是被自检当成"漏登记的状态字段"—— errors.txt 里因此刷了 **397 条**
    ///   (同一句话每打开一个视频报一次, 日志涨到 797KB)。
    ///   而它**既没有 `CompilerGeneratedAttribute` 也没有 `GeneratedCodeAttribute`**
    ///   (只有相邻的 `InitializeComponent` 方法带 GeneratedCode, 字段本身光秃秃),
    ///   类上同样没有标记 ⇒ 反射层面没有任何判据能区分它与手写字段。
    ///   (试过按 CompilerGenerated 过滤, 探针实测生成字段数为 0, 那条路走不通。)
    /// ⇒ 只能按名字排除。这个名字由 XAML 编译器固定生成, 全项目唯一, 不会与业务字段撞。
    /// </summary>
    private static readonly HashSet<string> XamlGeneratedFieldNames = new(StringComparer.Ordinal)
    {
        "_contentLoaded",
    };

    /// <summary>取本类里所有"简单状态"字段名(值类型 + string, 不含 UI 元素/集合/定时器/服务引用)</summary>
    private static IEnumerable<string> StateFieldNames()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        return typeof(PlayerWindow).GetFields(flags)
            // ★★ XAML 编译器生成的字段必须排除(2026-10-12, errors.txt 里刷了 381 次的那条),
            //   原因与为什么只能按名字排除, 见 XamlGeneratedFieldNames 的注释。
            .Where(f => !XamlGeneratedFieldNames.Contains(f.Name))
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
        // ★ 这条路径(带 bvid 直接 new 出窗口)**不走 ResetForNewMedia**(与 ApplyPreferredQuality
        //   当年踩的是同一个坑), 所以「听视频」按钮的可用性也要在这里同步一次。
        //   位置在 LoadVideoAsync **之前**: UpdateListenButtonState 依赖 _isLive/_isLocalPlayback,
        //   而这两个标志由调用方在构造后、Show 前就已设好。
        UpdateListenButtonState();
        // 聆听态是跨窗口复用的窗口级模式: 复用一个"上次停在聆听态"的窗口时,
        // 顶栏按钮与遮罩都要还原成聆听态(否则按钮说"听视频"、实际播的是音轨)
        if (_isListenMode) ApplyListenMode(true);
        // 覆盖层窗口(VideoView 的 ForegroundWindow)这时才建出来 —— 把键盘处理也挂上去,
        // 否则点过一次视频/控制栏之后快捷键就全哑了(见 HookOverlayKeyboard 的说明)
        HookOverlayKeyboard();
        await LoadVideoAsync();
    }

    // ------------------------------------------------------------ 加载流程

    /// <summary>
    /// 拉详情 → 取流 → 播放。
    ///
    /// ★ <paramref name="cid"/>: 指定要播的**分P**。0 = 走默认(pages[0])。
    ///   分P 是同一个 bvid 切成几段, 换段只换 cid(见 SwitchToPageAsync),
    ///   所以这里用参数传, 不去改 _currentBvid —— 改了会把弹幕/历史/三连的归属整个搞错。
    /// </summary>
    private async Task LoadVideoAsync(long cid = 0)
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
            // ★ cid 优先级: 调用方指定的分P > pages[0]。
            //   合集换片那条路不传 cid(它是"换 bvid", 落回 pages[0] 与原行为完全一致),
            //   分P 换段那条路传具体 cid。
            _currentCid = cid > 0 ? cid : detail.Cid;
            _aid = detail.Aid;
            _ownerMid = detail.OwnerMid;
            _ownerName = detail.Owner;
            UpdateUiFromDetail(detail);
            // 合集数据在详情里就已经解析好了(见 ApiClient.GetVideoAsync), 这里零额外请求。
            // 放在 UpdateUiFromDetail 之后: 它要用刚设好的 _currentCid/_aid 来定位"当前是第几集"。
            ApplySeason(detail.Season);
            // 分P 同理, 零额外请求。放在 ApplySeason 之后: 两者抢的是同一块信息栏, 后写的赢。
            ApplyPages(detail.Pages);

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
            //
            // ★ 只在播**第一个 P** 时才碰本地缓存(2026-10-11 加分P时补的):
            //   ShortVideoCache 的键只有 bvid, 不含 cid。多P 的短视频(每段都小于 150MB 上限,
            //   比如一个视频切成几段 vlog)切到 P2 时会命中 P1 落盘的文件 —— 表现为
            //   "换了集还是上一段的内容"。长视频(纪录片那种 90 分钟)本来就超预算走不到这里,
            //   所以这个坑平时碰不到, 但短视频多P 一做分P 就会踩。
            //   与其给缓存 API 加 cid 参数(要动文件命名/清理/落盘三处), 不如认准"只缓存首 P"。
            var isFirstPage = cid <= 0 || cid == detail.Cid;
            var local = isFirstPage ? ShortVideoCache.TryGet(_currentBvid, actualQn) : null;
            if (local == null && isFirstPage && ShortVideoCache.WorthCaching(remux))
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
            // ★★★ 「听视频」存的必须是 **DASH 音轨地址(audioUrl)**, 不能存 playAudio。
            //   两者的区别正是"三分钟以下视频点听视频说没有音轨"的根因:
            //     短视频走**本地合流文件**那条路时 playAudio 是 null(单文件源不能挂 input-slave),
            //     但接口给的 DASH 音轨地址依然是好的、依然能单独播。
            //     以前这里写成 `= playAudio` ⇒ 短视频的 _listenAudioUrl 恒为 null ⇒
            //     点「听视频」直接报"这条视频没有可用的音轨"(2026-10-06 用户实测)。
            //   存 audioUrl 之后: 聆听态播纯音轨(省流量), 退出时用 _lastPlayUrl(本地文件)
            //   原样拼回 —— 两条路各用各的地址, 互不干扰。
            _listenAudioUrl = audioUrl;
            WriteLibVLCDebug(
                $"\n[LoadVideo] bvid={_currentBvid} cid={_currentCid} qn={actualQn} url={url} audio={playAudio}\n");

            // 片段数据: 到这里"取播放地址 / 合流下载"已经花掉了大部分时间, 这一步几乎必然已就绪。
            // 万一还没回来, 也最多再等 SponsorWaitMs —— 等不到就先起播, 结果回来后再补上。
            await BeforePlayApplySponsorAsync(sponsorTask);
            if (_closing) return;

            // B站 CDN 对 Referer 和 User-Agent 有校验, 必须通过 Media 选项告诉 LibVLC;
            // DASH 流音视频分离, 用 input-slave 把音轨挂到视频流上一起播放。
            // ★ 「听视频」开着时(user 开着聆听态点了下一条视频)要直接起播**音轨单流** ——
            //   否则会先起视频流再被 EnterListenMode 切一次, 白拉一段视频数据、还多闪一次画面。
            if (_isListenMode && !string.IsNullOrEmpty(_listenAudioUrl))
            {
                _listenResumeMs = 0;
                ShowLoading("正在切换到听视频…");
                PlayMedia(BuildMedia(_listenAudioUrl!, null));
            }
            else
            {
                // ★ 聆听态开着但这一片**取不到独立音轨**(本地合流是单文件源): 退回整路播放,
                //   并把聆听态收掉 —— 否则遮罩会说"正在聆听"而实际在播视频, 自相矛盾。
                if (_isListenMode)
                {
                    _isListenMode = false;
                    ApplyListenMode(false);
                }
                PlayMedia(BuildMedia(url, playAudio));
            }
            UpdateListenButtonState();
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

    /// <summary>
    /// 当前分P 挂在标题后面的后缀, 如 <c>" - P12 S05E08 收获"</c>。
    /// 不是多P 视频、或当前 cid 在 pages 里定位不上, 都返回空串(标题一个字不动)。
    /// </summary>
    private string CurrentPageSuffix(VideoDetail detail)
    {
        if (detail.Pages.Count <= 1 || _currentCid <= 0) return "";
        foreach (var p in detail.Pages)
            if (p.Cid == _currentCid)
                return $" - P{p.Page} {p.DisplayTitle}";
        return "";
    }

    private void UpdateUiFromDetail(VideoDetail detail)
    {
        // ★ 多P 时标题后面挂上"当前这一段"(2026-10-11):
        //   40 段的纪录片每段标题都一样("克拉克son's农场 1-5季"), 不标出来用户根本分不清
        //   自己在第几段, 切窗口(Alt-Tab)回来也是一脸茫然。官方网页版同样在标题里带 part。
        //   单P 视频一个字符都不多写。
        var pageSuffix = CurrentPageSuffix(detail);
        var displayTitle = detail.Title + pageSuffix;

        TitleText.Text = displayTitle;
        Title = displayTitle + " - BiliDesk";
        // ★ _videoTitle 保持**不含分P**: 它是要往历史/上报里写的视频名, 那是稿件的标题,
        //   不是"我现在看到第几段"。混进去会让同一稿件在历史里出现几十条重名记录。
        _videoTitle = detail.Title;
        VideoTitleText.Text = displayTitle;
        ViewCountText.Text = VideoItem.FormatCount(detail.ViewCount);
        DanmakuCountText.Text = VideoItem.FormatCount(detail.DanmakuCount);
        PubDateText.Text = detail.Pubdate > 0
            ? DateTimeOffset.FromUnixTimeSeconds(detail.Pubdate).LocalDateTime.ToString("yyyy-MM-dd")
            : "";
        UpNameText.Text = string.IsNullOrEmpty(detail.Owner) ? "UP 主" : detail.Owner;
        UpDescText.Text = "UP 主 mid: " + detail.OwnerMid;
        DescText.Text = string.IsNullOrEmpty(detail.Desc) ? "这个 UP 主很懒, 什么都没写~" : detail.Desc;
        // ★ 换片 ⇒ 折叠态必须复位: 否则在 A 片点开简介, 换到 B 片时它是展开的,
        //   而 B 片简介很短 —— 展开按钮又不显示, 用户会以为"按钮坏了"。
        _descExpanded = false;
        // 宽度可能还没变(信息栏固定 400px), SizeChanged 这次不会触发, 得主动判一次
        UpdateDescFold();

        // 相关视频: 与详情一起取, 走后台, 不挡起播(见 LoadRelatedAsync 的代次闸门)
        _ = LoadRelatedAsync(detail.Bvid);

        UpdateButtonState();
        // 切换视频时重置各操作按钮状态(随后的异步查询会刷新为真实值)
        _isLiked = false;
        _isFollowed = false;
        // ★ 投币态**不能**取自详情(那个字段根本不存在, 见 GetCoinCountAsync 的说明),
        //   这里先复位, 真实值由随后的 RefreshCoinStateAsync 异步补上。
        _isCoined = false;
        // 计数直接来自详情(view 接口的 stat), 比旧的"点赞/投币/收藏"三个字信息量大
        _likeCount = detail.LikeCount;
        _coinCount = detail.CoinCount;
        _favCount = detail.FavoriteCount;
        UpdateActionCounts();
        SetFavorited(false);
        // 与点赞同款: 详情带不来"本人是否投过币", 换片后单独查一次, 否则投币键恒灰
        _ = RefreshCoinStateAsync();
        // 「稍后再看」的点亮态不在这里复位 —— 它统一在 ResetForNewMedia 里清,
        // 因为直播/本地文件那两条路根本不会走到这里。
        UpdateFollowButton();
        // UP 主头像优先用 owner.face(之前误用了视频封面)
        _ownerFace = detail.OwnerFace ?? "";
        _ = LoadUpAvatarAsync(_ownerFace);
        // 视频封面: 「听视频」聆听态要拿它当视觉锚点(一眼知道在听什么)。
        // 顺手记下来, 不必等用户点了聆听再去请求详情接口。
        _coverUrl = detail.Pic ?? "";
        if (_isListenMode)
        {
            if (ListenOverlayTitle != null) ListenOverlayTitle.Text = detail.Title;
            LoadListenCover(_coverUrl);
        }

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
        // 记住"这是用户自己按的": seek 后的"确保在播放"要尊重它, 不许把暂停顶回去
        _pausedByUser = !_mp.IsPlaying;
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
                // ★ 聆听态下重播仍然只拉音轨: 重播是"再听一遍", 不该突然开始下视频。
                if (_isListenMode && !string.IsNullOrEmpty(_listenAudioUrl))
                    PlayMedia(BuildMedia(_listenAudioUrl!, null));
                else
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

            // ★★ 2026-10-17 大修: 原来的判据只要求"远离起点 + 远离目标", 没有**上界**,
            //   结果把"seek 彻底失效、位置回到片子开头"误判成"落地但偏早", 于是收手不重试 ——
            //   这正是用户报的"拉过去又跳回去、画面卡住"(探针 bd-probe-seekstick 实测):
            //     拖到 329s → 落在 5.1s, 拖到 724s → 落在 11.1s, 拖到 988s → 落在 200s。
            //   而 `_seekFromMs` 当时是 3.4s / 8.9s / 198s, 用 `> 1500ms` 判"远离起点"轻而易举
            //   就成立 ⇒ 立刻被当成"落地偏早"认账。**判据缺的是"离目标不能太远"这个上界。**
            //
            //   真实"落地偏早"(DASH 分片边界)的偏差是**秒级**: 实测 60s→55.2s(4.8s)、
            //   150s→145.7s(4.3s); 而这次失效的偏差是 324s / 713s / 788s —— 差两个数量级。
            //   所以判据取"偏早不超过目标的 1/8 且不超过 30s": 正常偏早(几秒)仍被认账,
            //   跳到开头/跳到很远的地方则一律判为"没落地", 交给看门狗重发。
            //
            // ★ 为什么上界要用**相对目标的比例**而不是固定值: 目标可能在 5 分钟处也可能在
            //   20 分钟处, 固定 30s 对短跳转太松(5s 目标偏 25s = 完全跑偏却仍认账)。
            var earlyBy = _pendingSeekMs - time;                 // >0 表示落在目标之前
            var earlyUpper = Math.Min(SeekLandingMaxEarlyMs,
                                      Math.Max(SeekLandingMinEarlyMs, _pendingSeekMs / 8));
            var farFromBoth = _seekFromMs >= 0 &&
                              Math.Abs(time - _seekFromMs) > SeekLandingToleranceMs &&
                              Math.Abs(time - _pendingSeekMs) > SeekConfirmWindowMs;
            // ★ 只接受"落在目标之前、且偏早幅度在合理范围内"; 落在目标**之后**超窗也不算
            //   (那同样是跑偏, 该重试)
            var withinEarlyBand = earlyBy > SeekConfirmWindowMs && earlyBy <= earlyUpper;
            var landedElsewhere = farFromBoth && withinEarlyBand &&
                                  _seekElsewhereSeenMs >= 0 && time != _seekElsewhereSeenMs;
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

    // ------------------------------------------------------------ 倍速切换与"松手后静音"
    //
    // ★★★ 2026-10-16 定稿(实测见 .probes/bd-probe-rateguard2, 前面两次结论都有偏差, 以本条为准):
    //
    //  现象: 长按 2x 松手后, 会听到一小段"没有声音"。
    //
    //  根因: **libvlc 在 SetRate 生效时会丢弃整个音频输出缓冲**, 原生日志:
    //          playback way too late (~529-655ms): flushing buffers
    //        官方 flush 定义 = "discard all pending buffers and stop playback as soon as
    //        possible" ⇒ 待播音频被整体丢掉, 要重填才恢复出声。
    //        ★ 这是 **VLC 自身行为**, 不是本项目的代码 bug(探针裸调 libvlc 也复现)。
    //
    //  ★★ 但**这个代价比之前估计的小得多**: 真正的停顿只有 **0.25~0.5s**
    //     (base 变体两次实测: 停顿 0.25s / 0.50s, 松手后吞吐比 1.12 / 0.79)。
    //     曾经记成"约 2 秒"是**误读**: 松手瞬间 OS 缓冲里还排着已解码音频, 它仍以
    //     **2 倍速**播完(实测按住期间吞吐 = 正常 1x 的 3.4 倍)⇒ 听感像"还在快进",
    //     等它播空才轮到那一小段静音。**静音本身只有零点几秒。**
    //
    //  ★★★ 三个"缓解"手段全都**实测更糟**, 已被否掉(别再想):
    //    · 松手前先压音量再改 rate  → 停顿拉长到 2.25s, 吞吐比 0.01
    //    · 按住期间全程静音          → 停顿 2.00s, 吞吐比 0.27
    //    · 松手不改 rate 只补一次 seek → 停顿 1.50s
    //    ⇒ 什么都不做(base)就是最优。**不要去"治"那 0.3 秒。**
    //
    //  ★ 早期逐一实测过、确认**无效**的参数(别再重复): `--network-caching` 1000/3000/5000/6000、
    //    `--no-audio-time-stretch`(libvlc 级与 media 级; 该选项不生效, scaletempo 照旧加载)、
    //    `--aout=mmdevice / directsound / waveout / afile / amem`、`--clock-jitter=0`、
    //    `--clock-synchro=0`、Pause/Play 包住 SetRate、分两步换速率(flush 翻倍)、
    //    SetRate 后补 seek(flush 暴增)、Stop + 重建 Media(更差)、先压音量再 SetRate(同前)。
    //  ★ 也扫过 libvlccore/libvlc 的选项表: **没有**能调 aout 缓冲深度的开关
    //    (--audio-buffer / --aout-buffer / --mmdevice-buffer 等全部不存在)。
    //
    //  ⇒ 结论: 长按快进就用 SetRate。顺滑度是它天生就有的(与倍速按钮同款),
    //    代价只有松手后零点几秒静音 —— 换 seek 式反而会把顺滑度整个丢掉(实测用户反馈"画面和音频均卡顿")。


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
        // ★ 2026-10-16: 这里**不再**豁免长按快进。那条豁免是 seek 式方案加的(那时我们自己
        //   每 400ms 发一次 seek, 位置会一阵阵停住)。改回 SetRate 后位置照常推进
        //   (只是快 2 倍)—— 与"倍速按钮 2x"完全同款, 那个从来没豁免过。
        //   留着反而会掩盖长按期间真实的卡顿。
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

    /// <summary>
    /// 安全读一次播放位置, 并把 libvlc 的**无效值**归一成 -1。
    ///
    /// ★ 必须这么做(2026-10-17 修 seek 时实测): 分段跳 / Media 重建途中,
    ///   `_mp.Time` 会报出 -1 或超大值(实测读到 18446744073709551 —— ulong 溢出)。
    ///   直接当位置用会算出荒谬的下一段, 判据也会被污染。
    /// </summary>
    private long SafeMpTime()
    {
        try
        {
            var t = _mp.Time;
            return (t < 0 || t > 24L * 3600 * 1000) ? -1 : t;
        }
        catch { return -1; }
    }

    /// <summary>统一的 seek 发起入口 —— **所有跳转都必须走这里**(拖进度条 / 方向键 / 快进快退)。
    ///
    /// ★★★ 为什么需要它(2026-10-17 用户报"拉进度条失效, 拉过去又跳回去、画面卡住"):
    ///   真根因是 **libvlc 3.0.20 对 frag-MP4 的 seek 有单次跳跃距离上限**。
    ///   探针 bd-probe-reseek 实测(视频 BV1WqpS66E7w, 22 分钟):
    ///     <code>
    ///       单次跳  60s → 偏差   499ms  ✅
    ///       单次跳 120s → 偏差  -751ms  ✅
    ///       单次跳 180s → 偏差 -2750ms  ✅
    ///       单次跳 300s → 偏差 -297249ms  ❌ 位置掉回原处
    ///       直接跳到 25%(329s) → 偏差 -323943ms ❌
    ///     </code>
    ///   原生日志对应 `cannot seek (to offset 30717145)` + `INPUT_CONTROL_SET_TIME ... failed`。
    ///   ⇒ 用户拖得越远越必然失败, 表现就是"拉过去又跳回去"。
    ///
    /// ★ 修法(两条都实测可用, 见探针 G/H/I 段):
    ///   ① **往前**跳: 拆成每段 ≤150s 的连续跳转 —— 实测 8 段 150s 全部准确,
    ///      从 1.4s 一路跳到 1181s(90%)落点偏差 4s 内;
    ///   ② **往回**跳: 分段无效(实测第一段就原地不动), 改用**重建 Media** 后直接跳 ——
    ///      实测落到目标偏差 416ms。重建就是"换片"那条路, 一直好用。
    ///   ★ 短距离(≤150s)仍然一步到位, 不给普通跳转引入任何额外延迟。
    /// </summary>
    private void SeekTo(long targetMs)
    {
        if (_closing || _mp == null) return;
        var length = _mp.Length;
        if (length <= 0) return;
        targetMs = Math.Clamp(targetMs, 0, Math.Max(0, length - 500));

        var cur = _lastKnownMs > 0 ? _lastKnownMs : 0;
        var delta = targetMs - cur;

        // ★ 这是**用户主动发起**的一次新跳转: 重建重试预算复位, 否则一次失败后
        //   后续所有往回跳都失去了"再重建一次"的机会。
        _rebuildRebuilds = 0;
        _ensurePlayProbeMs = -1;   // "确保播放"的推进基线也重新建立

        // 短跳(含往前/往回 150s 内): 一步到位, 保持原有手感
        if (Math.Abs(delta) <= SeekSingleMaxMs)
        {
            try { _mp.Time = targetMs; } catch { }
            MarkSeekIssued(targetMs);
            return;
        }

        // 往回且距离不小: 分段无效, 只能重建
        if (delta < 0)
        {
            RebuildMediaAndSeek(targetMs);
            return;
        }

        // 往前长跳: 分段逼近
        StartSegmentedSeek(targetMs);
    }

    /// <summary>单次 seek 能可靠完成的最大距离(毫秒)。超过它 libvlc 会把位置丢回原处。</summary>
    private const long SeekSingleMaxMs = 150_000;

    // --- 分段跳状态 ---
    private readonly System.Windows.Threading.DispatcherTimer _segSeekTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(700)
    };
    private long _segSeekTarget = -1;
    private bool _segSeekRunning;
    /// <summary>当前这一段的目标(-1 = 还没发过), 用来判"本段是否已落地"</summary>
    private long _segSeekStepIssuedMs = -1;
    /// <summary>当前这一段的起点, 用来判"位置有没有真的动过"</summary>
    private long _segSeekStepFromMs = -1;
    /// <summary>本段的等待截止(TickCount64), 超时就不等了直接发下一段</summary>
    private long _segSeekStepDeadline;

    /// <summary>
    /// 分段跳: 每段往前挪最多 <see cref="SeekSingleMaxMs"/>, 直到到达目标。
    ///
    /// ★★ 间隔必须"等本段确认落地", 不能固定 700ms(2026-10-17 实测踩到):
    ///   固定间隔时 libvlc 还没落地就发下一段, 位置会**来回震荡**
    ///   (实测序列 150→150→454→306→458→606→459→607, 最终冲到 -1), 比不修还糟。
    ///   现在每段发完就等"位置确实到了本段落点附近"再发下一段(超时 2.5s 兜底)。
    /// </summary>
    private void StartSegmentedSeek(long targetMs)
    {
        _segSeekTarget = targetMs;
        _segSeekRunning = true;
        _segSeekStepIssuedMs = -1;
        _segSeekStepFromMs = -1;
        _segSeekStepDeadline = 0;
        MarkSeekIssued(targetMs);          // 进度条立刻钉到目标, 不要让它一格一格爬
        _segSeekTimer.Tick -= OnSegmentedSeekTick;
        _segSeekTimer.Tick += OnSegmentedSeekTick;
        _segSeekTimer.Start();
        StepSegmentedSeek();
    }

    private void OnSegmentedSeekTick(object? sender, EventArgs e) => StepSegmentedSeek();

    private void StepSegmentedSeek()
    {
        if (_closing || _mp == null || !_segSeekRunning || _segSeekTarget < 0)
        {
            StopSegmentedSeek();
            return;
        }

        var now = Environment.TickCount64;
        // 一段还没落地: 等它(超时 2.5s 就强行发下一段, 免得卡死)
        if (_segSeekStepIssuedMs >= 0 && now < _segSeekStepDeadline)
        {
            var observed = _lastKnownMs;
            // ★ 判"本段已落地"不能要求精确到 8s 内 —— 远端 DASH 落地偏差实测 0~4s,
            //   而更关键的是:**位置已经离开上一段起点**就说明 input 已经动过了。
            //   之前用 |observed - 段目标| > 8000 判"还没到", 结果每段都要等满 2.5s 超时
            //   (实测 9 秒才前进 3 段, 用户会觉得"拖了之后半天不动")。
            if (Math.Abs(observed - _segSeekStepIssuedMs) > 20_000 &&
                Math.Abs(observed - _segSeekStepFromMs) < 2_000) return;   // 完全没动, 继续等
        }

        // ★ 用**实时**的 _mp.Time 而不是 _lastKnownMs(2026-10-17 实测踩到):
        //   _lastKnownMs 靠 TimeChanged 事件更新(约 4Hz, 且有延迟), 分段跳读到它
        //   会滞后 1~2 段 ⇒ 算出的"剩余距离"偏大 ⇒ **冲过目标**
        //   (实测冲到 898400ms, 超目标 173 秒)。实时读一次就没这个问题。
        var cur = SafeMpTime();
        if (cur <= 0) cur = _lastKnownMs > 0 ? _lastKnownMs : 0;
        var remain = _segSeekTarget - cur;

        if (remain <= 5_000)
        {
            StopSegmentedSeek();
            MarkSeekIssued(_segSeekTarget);
            return;
        }

        // ★ 分段步进必须**双重钳制**: 既不超过单段上限, 也不越过目标。
        var step = Math.Min(remain, SeekSingleMaxMs);
        var next = cur + step;
        try { _mp.Time = next; } catch { }

        _segSeekStepFromMs = cur;
        _segSeekStepIssuedMs = next;
        _segSeekStepDeadline = now + 2_500;

        // 每一段都重新计时看门狗 —— 否则总时长会超过 2.5s 被误判成"丢了"并重发
        _pendingSeekMs = next;
        _seekDeadline = now + 2_500;
        _seekObservedMs = -1;
        _seekFromMs = cur;
        _seekElsewhereSeenMs = -1;
    }

    private void StopSegmentedSeek()
    {
        _segSeekRunning = false;
        _segSeekTarget = -1;
        _segSeekStepIssuedMs = -1;
        _segSeekStepFromMs = -1;
        _segSeekTimer.Stop();
    }

    /// <summary>
    /// 重建 Media 后跳到目标 —— 用于**往回**的长跳(libvlc 往回长跳实测原地不动)。
    ///
    /// ★ 这就是"换片/切清晰度"那条路: 丢一条全新的 Media 进去, 起播后立刻设目标。
    ///   实测落点偏差 416ms(见探针 I 段)。
    /// </summary>
    private void RebuildMediaAndSeek(long targetMs)
    {
        if (_closing) return;
        if (_isLocalPlayback)
        {
            var path = _lastPlayUrl;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            ShowLoading("正在跳转…");
            _resumeAfterSwitchMs = targetMs;
            PlayMedia(new Media(_libVLC, path, FromType.FromPath));
            return;
        }

        var url = _lastPlayUrl;
        if (string.IsNullOrEmpty(url)) return;
        ShowLoading("正在跳转…");
        // ★ 重建前把"最后已知位置"清掉: 它记的是**旧 input** 的位置(比如 72 万 ms),
        //   留着的话补跳会拿它当"当前位置"算出荒谬的下一段(2026-10-17 实测踩到)。
        _lastKnownMs = 0;
        // 起播后由 Playing 事件读到这个值并跳到目标(见 _resumeAfterSwitchMs 那套机制)
        _resumeAfterSwitchMs = targetMs;
        _rebuildSeekTarget = targetMs;
        PlayMedia(BuildMedia(url, _isListenMode ? null : _lastAudioUrl));
    }

    /// <summary>
    /// 重建 Media 后要落到的目标(-1 = 没有)。
    ///
    /// ★★ 为什么要单独记一份并**反复补跳**(2026-10-17 实测):
    ///   起播后设一次 Time 不够 —— libvlc 会先接受目标、随后又退回低值, 来回震
    ///   (实测序列 197605 → 1402 → 197605 → 5404 → 197605 → 8660 → 10404)。
    ///   而看门狗把这种"离开目标又回来"误当成"落地偏早"就收手了, 于是最终停在 10 秒。
    ///   ⇒ 这里在起播后按短间隔再补几次, 每次都重新钉住目标, 直到位置真的稳住。
    /// </summary>
    private long _rebuildSeekTarget = -1;
    private int _rebuildSeekTries;

    /// <summary>重建后补跳: 起播后最多补 <see cref="RebuildSeekMaxTries"/> 次, 每次间隔 400ms</summary>
    private const int RebuildSeekMaxTries = 20;

    private void KickRebuildSeek(long targetMs)
    {
        _rebuildSeekTarget = targetMs;
        _rebuildSeekTries = 0;
        _rebuildSeekStable = 0;
        var myTarget = targetMs;   // 闭包捕获: 目标变了老 timer 就退休

        // ★★ 起播后先等一段再动手(2026-10-17 实测):
        //   重建后 input 还在初始化, 这时发 seek 会被丢
        //   (原生日志 `prefetch stream error: cannot seek`, 位置随后退回开头)。
        var delay = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1200)
        };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            if (_closing || _mp == null || _rebuildSeekTarget != myTarget) return;
            StartRebuildStepTimer(myTarget);
        };
        delay.Start();
    }

    /// <summary>
    /// 重建后的逐段补跳: 每 350ms 看一次位置, 没到就**只往前挪一小段**(不是直接设目标)。
    ///
    /// ★★ 两个关键点(2026-10-17 实测):
    ///   ① 重建后第一次 seek 是**假受理**: 位置先跳到目标(实测 1.0s 时确实是 197605),
    ///      随后 libvlc 又退回低值(2.0s 时 2651), 一路从头播。
    ///      ⇒ 必须要求"连续 3 次都在目标附近"才算稳, 中途掉回去就继续补。
    ///   ② 补跳时**不能直接设目标**: 实测从 6.1s 直接跳 197.6s 会冲过头到 266.4s
    ///      (超目标 68 秒)。改成每次只挪 ≤150s(与分段跳同一个上限),
    ///      这样即使 libvlc 落点有偏差也不会越过目标。
    /// </summary>
    private void StartRebuildStepTimer(long myTarget)
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        timer.Tick += (_, _) =>
        {
            if (_rebuildSeekTarget != myTarget) { timer.Stop(); return; }   // 目标变了, 退休
            if (_closing || _mp == null) { timer.Stop(); return; }
            if (++_rebuildSeekTries > RebuildSeekMaxTries) { timer.Stop(); return; }

            var cur = SafeMpTime();
            if (cur > 0 && Math.Abs(cur - myTarget) < 3_000)
            {
                _rebuildSeekStable++;
                // 位置对了还不够 —— 实测会停在"时间轴正确但画面不动", 这里补一次播放
                if (_rebuildSeekStable >= 2) EnsurePlayingAfterSeek();
                if (_rebuildSeekStable >= 3)
                {
                    // 收工: 清干净看门狗。
                    // ★ 补跳期间一直在重置 _pendingSeekMs, 不清它就一直挂着,
                    //   后续进度回写/卡顿检测全被"正在跳转"挡住。
                    // ★ 别在这里再做"位置有没有推进"的二次确认并重设目标 ——
                    //   试过, 正常播放前进时也会误判成"没推进", 于是反复重设目标
                    //   把位置冲过头(实测冲到 746901ms, 超目标 22 秒)。播放推进交给
                    //   EnsurePlayingAfterSeek 和卡顿检测就够了。
                    ClearSeekWatchdog();
                    timer.Stop();
                    return;
                }
            }
            else
            {
                _rebuildSeekStable = 0;
                // 只往前挪一段(见上面第②点), 越过目标就钳回目标
                var want = cur > 0 ? cur : 0;
                var remain = myTarget - want;
                var step = Math.Abs(remain) <= SeekSingleMaxMs
                    ? myTarget
                    : want + Math.Sign(remain) * SeekSingleMaxMs;
                try { _mp.Time = step; } catch { }
                _pendingSeekMs = step;
                _seekDeadline = Environment.TickCount64 + 2500;
            }

            // ★ 补到上限还没稳: 这条 input 已经"认死"了(实测会一路从头播),
            //   再补也没用 —— 重建一次重来(限 2 次, 防止无限循环)。
            if (_rebuildSeekTries >= RebuildSeekMaxTries && _rebuildSeekStable < 3)
            {
                timer.Stop();
                if (_rebuildRebuilds < 2)
                {
                    _rebuildRebuilds++;
                    RebuildMediaAndSeek(myTarget);
                }
            }
        };
        timer.Start();
    }

    /// <summary>连续几次读到"位置在目标附近"(重建补跳用)</summary>
    private int _rebuildSeekStable;

    /// <summary>
    /// 确保播放器处于播放态。seek 之后 libvlc 偶尔会停在暂停/停止态(位置对但不推进),
    /// 用户看到的就是"时间轴对、画面卡死" —— 这正是用户报的第三个症状。
    ///
    /// ★ 只在不该暂停时动手: 用户**自己**按了暂停, _pauseByUser 会记下来, 不许被这里顶回去。
    /// </summary>
    private void EnsurePlayingAfterSeek()
    {
        if (_closing || _mp == null || _pausedByUser) return;
        try
        {
            // ★ 不能只看 IsPlaying: 实测它会停在"IsPlaying=true 但位置/解码都不动"
            //   的状态(用户报的"时间轴对、画面卡死")。这里用**位置有没有推进**来判。
            var now = SafeMpTime();
            // 第一次调用只建立基线, 不判"没推进"(否则必然误触发一次 Play)
            if (_ensurePlayProbeMs < 0) { _ensurePlayProbeMs = now; return; }
            var advanced = now > _ensurePlayProbeMs;
            _ensurePlayProbeMs = now;

            if (_mp.IsPlaying && advanced) return;   // 真的在播, 不动它

            // ★ 位置没动: 再 Play 一次(幂等)。实测 SeekTo 之后 libvlc 偶尔会停在
            //   "IsPlaying=true 但位置/解码都不动"(用户报的"时间轴会动、画面卡死"),
            //   调一次 Play() 能把解码线程唤醒。
            if (now > 0) _mp.Play();
            else if (!_mp.IsPlaying) _mp.Play();
        }
        catch { /* 播放器可能刚好失效 */ }
    }

    /// <summary>上一次"确保播放"探到的位置, 用来判"位置有没有真的推进"</summary>
    private long _ensurePlayProbeMs = -1;

    /// <summary>用户主动暂停了(seek 后的"确保播放"要尊重它)</summary>
    private bool _pausedByUser;

    /// <summary>重建后补跳仍失败时, 允许再重建几次(最多 2 次)</summary>
    private int _rebuildRebuilds;

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
        SeekTo(targetMs);
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
            //
            // ★ 只收**合法值**: libvlc 在 input 重建 / 分段 seek 途中会报出 -1 或
            //   超大值(实测读到 18446744073709551 —— ulong 溢出), 一旦写进 _lastKnownMs,
            //   依赖它的分段跳/看门狗就会算出一个荒谬的目标(2026-10-17 修 seek 时踩到)。
            if (e.Time >= 0 && e.Time < 24L * 3600 * 1000)
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
            // ★ 走统一入口: 它会按距离决定"一步到位 / 分段跳 / 重建后跳"
            //   (libvlc 对 frag-MP4 的单次 seek 有距离上限, 见 SeekTo 的说明)
            SeekTo(targetMs);
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
        // ★ 音量继承: 拖滑块这条路也要落盘(2026-11-12)。
        //   SetVolume 里已经存过一次, 这里是**直接拖滑块**的路径 —— 那条路不经过 SetVolume。
        //   两个入口都存, 才不会出现"滚轮调的继承了、拖滑块调的没继承"。
        Svc.Settings.SetPlayerVolume(v);
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

    /// <summary>倍速提示的淡出计时器(松手后"已恢复 1.0x"亮一下就靠它收场)</summary>
    private DispatcherTimer? _speedOsdTimer;

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
        // ★ 音量继承: 用户每调一次就落盘, 下一个视频/下一次打开都用它。
        //   放在这里而不是各处调用点 —— SetVolume 是滚轮/键盘/滑块的唯一入口。
        //   SetPlayerVolume 内部有"值没变就不写"的短路, 连滚到同一值不会反复写盘。
        Svc.Settings.SetPlayerVolume(v);
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
    /// <summary>
    /// 浮出倍速提示(长按快进开始 / 恢复 1x 时调用)。
    ///
    /// ★ 与音量提示的差别(所以没有复用 ShowVolumeHint):
    ///   音量是"调一下、闪一下、自己淡出"; 倍速提示必须在**整个长按期间一直挂着** ——
    ///   用户按住的那几秒里要始终知道"现在是 2 倍速, 松手会回去"。
    ///   松手时再调一次本方法(<c>hold=false</c>), 文案换成"已恢复 1.0x"并淡出。
    /// </summary>
    /// <param name="text">提示文案, 如 "2.0x 快进中" / "已恢复 1.0x"</param>
    /// <param name="hold">true = 常显不自动淡出(长按期间); false = 显示一下自己淡出</param>
    private void ShowSpeedHint(string text, bool hold)
    {
        if (_closing || SpeedOsd == null) return;

        SpeedOsdText.Text = text;
        _speedOsdTimer?.Stop();
        _speedOsdTimer = null;

        // 每次都从"当前透明度"补到 1(同 ShowVolumeHint 的理由:
        // 上一次的淡出可能还没跑完, 从当前值补最稳, 不会出现"提示淡到一半卡住")
        SpeedOsd.Visibility = Visibility.Visible;
        SpeedOsd.BeginAnimation(OpacityProperty, null);
        // ★★★ 淡入动画**绝不能写 FillBehavior=Stop**(2026-10-14 修"缓存中长按 2x,
        //   缓存结束后倍速提示消失"): XAML 里 SpeedOsd 的基准值是 `Opacity="0"`,
        //   而 Stop 语义是"播完动画把属性还原成**基准值**" —— 于是 120ms 淡入一结束,
        //   Opacity 立刻掉回 0, 提示虽然 Visibility=Visible 却整块透明。
        //   探针 bd-probe-speedosd 实测: 第一次显示 +180ms 后 Opacity=0.00。
        //   ★ 这解释了为什么"第二次显示是好的": 上一次淡出的 Completed 里写了
        //     `Opacity = 1`, 把基准值改成了 1, 之后所有淡入的 Stop 都还原到 1。
        //     ⇒ 只有**每个进程的第一次显示**中招, 与"缓存期间长按"完全对得上
        //       (缓存期间恰好是打开视频后的第一次长按)。
        //   ★ 与 ShowVolumeHint 保持一致: 它的淡入也没写 FillBehavior(默认 Hold)。
        SpeedOsd.BeginAnimation(OpacityProperty,
            new DoubleAnimation(Math.Min(1.0, SpeedOsd.Opacity), 1, TimeSpan.FromMilliseconds(120)));

        if (hold) return;   // 长按期间: 挂着等松手

        // 松手了: 亮一下"已恢复"再淡出
        _speedOsdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _speedOsdTimer.Tick += (_, _) =>
        {
            _speedOsdTimer?.Stop();
            _speedOsdTimer = null;
            if (_closing) return;
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(240))
            { FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop };
            fade.Completed += (_, _) =>
            {
                // 淡出期间用户可能又按住了(ShowSpeedHint 已重开): 那时不能藏
                if (_closing || _speedOsdTimer != null) return;
                // ★ 摘动画 + 归位(2026-10-13 清理动画 bug 时的同款问题):
                //   DoubleAnimation 默认 Hold 会把 Opacity 钉在 0, 下次常显就整块透明。
                SpeedOsd.BeginAnimation(OpacityProperty, null);
                SpeedOsd.Opacity = 1;
                SpeedOsd.Visibility = Visibility.Collapsed;
            };
            SpeedOsd.BeginAnimation(OpacityProperty, fade);
        };
        _speedOsdTimer.Start();
    }

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
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(240))
            { FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop };
            fade.Completed += (_, _) =>
            {
                // 淡出期间用户又调了一次 -> ShowVolumeHint 已经重开计时器并重播淡入,
                // 这时绝对不能把浮层藏掉(表现就是"滚轮调音量提示闪一下就不见了")
                if (_closing || _volumeOsdTimer != null) return;
                // ★ 摘动画 + 归位(2026-10-13 修): 原来这里是裸 Collapsed,
                //   而 DoubleAnimation 默认 FillBehavior=Hold ⇒ Opacity 被钉死在 0,
                //   下次滚轮时浮层虽 Visible 却整块透明 —— 提示像"消失"了一样。
                VolumeOsd.BeginAnimation(OpacityProperty, null);
                VolumeOsd.Opacity = 1;
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

    /// <summary>隐藏倍速提示并停掉它的计时器(关闭流程用)</summary>
    private void StopSpeedOsd()
    {
        _speedOsdTimer?.Stop();
        _speedOsdTimer = null;
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
        // ★ 顺序: 先让弹幕时钟按新倍速落定当前值, 再真正 SetRate。
        //   反过来先 SetRate 的话, SyncDanmakuClockRate 内部那句 DanmakuClock() 会
        //   用**旧速率**外推(而媒体已经按新速率跑了) —— 那一下就是用户看到的"顿一下"。
        SyncDanmakuClockRate(next);
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

    /// <summary>
    /// 清晰度 / 弹幕区域弹层是否**有任意一个开着**。
    ///
    /// ★ 为什么需要它(2026-10-13 用户报"点清晰度 → 点空白处收起 → 视频变 2 倍速"):
    ///   这两个 Popup 是 <c>StaysOpen="False"</c>, 而 Popup 是**独立 HWND**。
    ///   实测(探针 bd-probe-coin, 真实 Win32 鼠标点击, 时间戳):
    ///     <code>
    ///     12:14:38.955  VIDEO UP                ← 只有 Up, 没有 Down!
    ///     12:14:38.975  *** POPUP CLOSED ***    ← Closed 晚 20ms
    ///     </code>
    ///   ⇒ 三个"当前状态"信号在 Up 到达时**全部不可用**:
    ///     · <c>Popup.IsOpen</c> 已被 WPF 自己的收起逻辑置成 false;
    ///     · <c>Mouse.LeftButton</c> 已回报 Released;
    ///     · <c>Popup.Closed</c> 还没来。
    ///   ⇒ 第一版写在 Closed 里的时间窗是**死代码**(等它记上, Up 早跑完了),
    ///     第二版的 IsOpen 判据同样挡不住这一次 Up。
    ///
    ///   ★ 真正可靠的判据是**手势配对**: 关闭弹层那一次点击, 它的 Down 被弹层 HWND
    ///     吃掉, 只有 Up 漏进视频区 —— 也就是"没有配对 Down 的 Up"。
    ///     这种 Up 一律丢弃即可, 且**不依赖任何事件顺序**。
    /// </summary>
    private bool AnyPlayerPopupOpen
        => (QualityPopup != null && QualityPopup.IsOpen)
        || (DanmakuAreaPopup != null && DanmakuAreaPopup.IsOpen);

    /// <summary>
    /// 视频区手势是否已经由一次 **Down** 正式起步。
    ///
    /// ★ 唯一可靠的弹层余波判据(见 <see cref="AnyPlayerPopupOpen"/> 里的实测记录):
    ///   弹层开着时按下的那一下不算视频区手势(它属于浮层), 所以这里必须为 false;
    ///   否则随后漏进来的 Up 会被当成一次完整的"单击", 于是**收起清晰度列表会把视频暂停**,
    ///   更糟的是漏进来的 Down 会起长按计时器 → 500ms 后 SetRate(2.0) 无故快进。
    /// </summary>
    private bool _videoGestureActive;

    /// <summary>
    /// 视频区**右键**手势是否已由一次 Down 正式起步(右键那条线的 <see cref="_videoGestureActive"/>)。
    ///
    /// ★ 为什么右键要单独一份而不是复用上面那个: 两者是**并行的两条手势**,
    ///   用户完全可能左键按着拖、中途点右键; 共用一个标记会互相把对方的"配对"抹掉。
    /// </summary>
    private bool _rightGestureActive;

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
            // 「听视频」的音轨地址跟着新档位更新 —— 不清的话切完清晰度再进出聆听态,
            // 用的还是旧档那条音轨(可能码率不同、甚至已过期)。
            // ★ 必须取 **audioUrl** 而不是 _lastAudioUrl: 后者在本地合流那条路上是 null
            //   (单文件源不挂 input-slave), 而音轨本身是好的。理由同 LoadVideoAsync 里那段。
            _listenAudioUrl = audioUrl;
            // 必须先 Stop: LibVLC 3 里已有 input 在播时 play() 会被**直接忽略**,
            // 不 Stop 的话新 Media 根本不会起播 —— 这就是"切换清晰度没反应"
            try { _mp.Stop(); } catch { }
            // ★ 聆听态下切清晰度: 仍然只播音轨(用户此刻要的是声音, 不该突然出画面再切回去)。
            //   没有可用音轨时退回整路播放, 至少不会什么都不播。
            if (_isListenMode && !string.IsNullOrEmpty(_listenAudioUrl))
                PlayMedia(BuildMedia(_listenAudioUrl!, null));
            else
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

    // ------------------------------------------------------------ 弹幕外观三项(2026-10-12)

    /// <summary>
    /// 三个外观滑杆的统一入口。
    ///
    /// ★ 为什么不直接写 Svc.Settings.UpdateDanmakuSettings 之后各改各的:
    ///   这三项**都要重算运行时状态**(ApplyDanmakuLook), 而重算的第一步就是比较新旧值。
    ///   于是最省事也最不容易漏的写法就是: 先落设置(它会广播 OnDanmakuSettingsChanged,
    ///   那里已经调了 ApplyDanmakuLook), 自己只负责把"这一行的数值文字"刷新掉。
    ///   两条路径(设置页改 / 播放器改)因此走的是同一段重算逻辑, 不会出现两边算法漂移。
    ///
    /// ★ 必须防回灌: 滑杆赋值也会触发 ValueChanged。同步控件时那个标志是必需的,
    ///   否则一次同步 → 一次事件 → 一次保存 → 一次广播 → 又同步, 虽然值同值不会死循环,
    ///   但每次开窗都会白白写一遍配置文件。
    /// </summary>
    private void OnDanmakuOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DanmakuOpacityValue != null) DanmakuOpacityValue.Text = ((int)e.NewValue) + "%";
        if (_syncingDanmakuLook) return;
        Svc.Settings.UpdateDanmakuSettings(
            Svc.Settings.DanmakuEnabled, Svc.Settings.DanmakuAreaPercent, Svc.Settings.DanmakuSmartFilter,
            opacityPercent: (int)e.NewValue);
    }

    private void OnDanmakuFontChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DanmakuFontValue != null) DanmakuFontValue.Text = ((int)e.NewValue) + "%";
        if (_syncingDanmakuLook) return;
        Svc.Settings.UpdateDanmakuSettings(
            Svc.Settings.DanmakuEnabled, Svc.Settings.DanmakuAreaPercent, Svc.Settings.DanmakuSmartFilter,
            fontScalePercent: (int)e.NewValue);
    }

    private void OnDanmakuSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var level = (int)e.NewValue;
        // 显示的是"适中/较快"这类人话, 不是数字 —— 与用户给的截图一致
        if (DanmakuSpeedValue != null) DanmakuSpeedValue.Text = Helpers.DanmakuMotion.SpeedLabel(level);
        if (_syncingDanmakuLook) return;
        Svc.Settings.UpdateDanmakuSettings(
            Svc.Settings.DanmakuEnabled, Svc.Settings.DanmakuAreaPercent, Svc.Settings.DanmakuSmartFilter,
            speedLevel: level);
    }

    /// <summary>把外观三项的滑杆/数值同步到当前设置(开窗与设置页改动后各调一次)</summary>
    private void UpdateDanmakuLookTexts()
    {
        var s = Svc.Settings;
        // 回灌保护: 下面的赋值会触发 ValueChanged, 那三个处理器会写设置
        _syncingDanmakuLook = true;
        try
        {
            if (DanmakuOpacitySlider != null) DanmakuOpacitySlider.Value = s.DanmakuOpacityPercent;
            if (DanmakuOpacityValue != null) DanmakuOpacityValue.Text = s.DanmakuOpacityPercent + "%";
            if (DanmakuFontSlider != null) DanmakuFontSlider.Value = s.DanmakuFontScalePercent;
            if (DanmakuFontValue != null) DanmakuFontValue.Text = s.DanmakuFontScalePercent + "%";
            if (DanmakuSpeedSlider != null) DanmakuSpeedSlider.Value = s.DanmakuSpeedLevel;
            if (DanmakuSpeedValue != null)
                DanmakuSpeedValue.Text = Helpers.DanmakuMotion.SpeedLabel(s.DanmakuSpeedLevel);
        }
        finally
        {
            _syncingDanmakuLook = false;
        }
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
        // ★ 遮罩交给 FluentWindow 的 WM_ENTERSIZEMOVE/EXITSIZEMOVE 钩子管(见 OnModalResizeStarted):
        //   DefWindowProc 进入的是**系统模态缩放循环**, 拖动全程由它占着消息循环,
        //   WndProc 钩子照样能收到 ENTER/EXIT —— 所以这里不必手动压, 免得多一份状态。
        //   (2026-10-05 第一版就是在这里手动压的, 压一次就撤 ⇒ 闪白依旧;
        //    真因是"按一下就放行"而非"没压到", 正确修法见 ArmResizeCover。)
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

        // ★ 必须先确认光标**落在窗口里**。下面的判据全是"离某条边多远 < border",
        //   而"远在窗口外"的坐标同样满足它(比如窗口在 y=100, 光标在 y=0 ⇒ top=true)
        //   ⇒ 不先框住范围的话, 光标移到窗口外也会被判成"在缩放带上"。
        //   实测踩到: 光标停在屏幕最顶端(y=0)时, 这个函数返回了非 0,
        //   于是视频区右键被当成"点在缩放带"而整个吞掉。
        if (pt.X < r.Left || pt.X > r.Right || pt.Y < r.Top || pt.Y > r.Bottom) return 0;

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
        // ★ 弹层开着 ⇒ 这次按下属于浮层, 视频区手势**不起步**。
        //   这是整套防护的地基(见 _videoGestureActive 的说明): 不起步, 随后漏进来的
        //   那个孤立 Up 才会因为"没有配对 Down"而被丢掉。
        if (AnyPlayerPopupOpen) { _videoGestureActive = false; return; }
        // 记下"这一下是我起的" —— Up 里靠它识别弹层余波
        _videoGestureActive = true;

        // 双击: 立即全屏, 取消所有挂起的单击/长按动作, 并抑制后续 Up 的单击排队
        if (e.ClickCount >= 2)
        {
            _suppressSingleClick = true;
            _videoGestureActive = false;   // 这一下已被双击消费, 不再等 Up 配对
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
            // ★ 长按快进: 改倍速(SetRate)。为什么不用 seek 式、以及松手那点静音为何
            //   不值得用花招去治 —— 见 FastForwardRate 那段(含三个实测更糟的方案)。
            StartFastForward();
            // ★ 浮出提示(2026-10-13 用户要求): 长按快进是**无入口手势**,
            //   不提示的话用户只看到画面变快, 不知道发生了什么、也不知道松手会恢复。
            //   hold=true: 整个长按期间一直挂着, 松手时由 Up 那边换成"已恢复"并淡出。
            ShowSpeedHint(FastForwardRate.ToString("0.#") + "x 快进中", hold: true);
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
        // ★ 与 Down 对称: 跳过提示条上的「撤回」点击也不触发单击暂停
        if (SkipNoticeBorder != null && SkipNoticeBorder.IsMouseOver) return;
        // ★★ 弹层余波(2026-10-13 第三轮修正, 前两轮都没修对):
        //   关闭清晰度/弹幕弹层那一次点击, Down 被弹层 HWND 吃掉, 只有 Up 漏进视频区。
        //   实测它到达时 IsOpen 已 false、Mouse.LeftButton 已 Released、Closed 还没来
        //   —— 三个"当前状态"信号全不可用(完整时间戳见 _videoGestureActive 的说明)。
        //   唯一可靠判据: **没有配对 Down 的 Up**。视频区手势由 Down 起步,
        //   没起步就来的 Up 必然是浮层漏过来的, 直接丢。
        //   不丢的后果有两个: 排一个"单击暂停"(收起列表视频自己暂停了);
        //   万一 Down 也漏进来, 长按计时器 500ms 后 SetRate(2.0) 无故快进。
        if (!_videoGestureActive) { e.Handled = true; return; }
        _videoGestureActive = false;

        _longPressTimer?.Stop();

        // 双击已被 Down 处理过(全屏), 这里直接吞掉, 绝不能再排一次播放/暂停
        if (_suppressSingleClick)
        {
            _suppressSingleClick = false;
            e.Handled = true;
            return;
        }

        // 长按结束: 恢复按下前的速率
        if (_longPressFired)
        {
            _longPressFired = false;
            // 恢复速率。★ 恢复到**按下前那个速率**(见 _rateBeforeFastForward), 不是写死 1x ——
            //   用户可能先用倍速按钮设了 1.5x。
            //   也**不要**去写 SpeedText —— 那个指示归控制栏的倍速按钮管, 与长按无关。
            StopFastForward();
            // ★ 提示收场: 亮一下再淡出 —— 手势有始有终,
            //   用户才知道刚才那阵快进是自己按出来的、现在结束了。
            ShowSpeedHint("已结束快进", hold: false);
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

    /// <summary>
    /// 视频区右键按下: 只负责"记下这一下是我起的", 真正的动作在 Up。
    ///
    /// ★★ 为什么必须**成对判定**(Down 起步 + Up 才动作), 而不是直接在 Up 里切:
    ///   实测(见 <see cref="AnyPlayerPopupOpen"/> 的时间戳): 弹层(清晰度/弹幕设置)收起时,
    ///   那次关闭点击的 **Down 被弹层 HWND 吃掉、只有 Up 漏进主窗口**, 而 `Closed` 晚 13~70ms,
    ///   `Popup.IsOpen` 那一刻已经变 false —— 所有"当前状态"判据都不可用。
    ///   左键那套早就是这么防的(<see cref="_videoGestureActive"/>); 右键如果直接在 Up 里切,
    ///   就会出现"点空白处收起清晰度列表, 视频却顺手铺满了"这种灵异现象。
    ///   ⇒ 唯一可靠判据同样是"有没有配对的 Down"。
    /// </summary>
    private void OnVideoAreaMouseRightDown(object sender, MouseButtonEventArgs e)
    {
        // ★ 落在窗口边缘缩放带里就**不要**当成视频区手势, 但**也不能**调 TryBeginEdgeResize():
        //   它内部发的是 `WM_NCLBUTTONDOWN`(**左键**消息), 用右键去调等于"假装用户按了左键拖边框",
        //   会让系统进入模态缩放循环 —— 而那个循环等的是左键松开, 右键根本不会给它。
        //   ⇒ 这里只判断"在不在缩放带", 在就放行(交给窗口默认行为), 不启动任何东西。
        if (IsOnResizeEdge()) return;
        if (_mp == null) return;
        RestoreKeyboardFocus();
        // 控制栏 / 跳过提示条上的右键不算视频区手势(与左键同款理由)
        if (ControlBar != null && ControlBar.IsMouseOver) return;
        if (SkipNoticeBorder != null && SkipNoticeBorder.IsMouseOver) return;
        // 弹层开着 ⇒ 这一下属于浮层, 不起步(地基, 见方法说明)
        if (AnyPlayerPopupOpen) { _rightGestureActive = false; return; }
        _rightGestureActive = true;
        e.Handled = true;   // 别让它冒泡成系统菜单
    }

    /// <summary>指针是否落在窗口边缘的缩放带上(只判断, 不启动缩放 —— 见 OnVideoAreaMouseRightDown)。</summary>
    private bool IsOnResizeEdge()
    {
        if (_isFullscreen || WindowState != WindowState.Normal) return false;
        return HitTestResizeEdge() != 0;
    }

    /// <summary>
    /// 视频区右键抬起: 与 Down 配对时切换「铺满窗口」。
    /// 没有配对 Down 的 Up 一律丢弃(那是弹层余波, 见 Down 的说明)。
    /// </summary>
    private void OnVideoAreaMouseRightUp(object sender, MouseButtonEventArgs e)
    {
        if (!_rightGestureActive) { e.Handled = true; return; }
        _rightGestureActive = false;
        if (_closing) return;
        // 走**同一个开关**: 与右上角那个按钮共享 _isFillWindow, 不复制逻辑
        ToggleFillWindow();
        e.Handled = true;
    }

    /// <summary>鼠标离开视频区: 兜底, 防止按住时鼠标移出导致状态卡住</summary>
    private void OnVideoAreaMouseLeave(object sender, MouseEventArgs e)
    {
        _longPressTimer?.Stop();
        _singleClickTimer?.Stop();
        _suppressSingleClick = false;
        // ★ 手势状态一并复位: 鼠标移出去了, 这一次按下就不再属于视频区,
        //   之后即使漏一个 Up 进来也会因为"没有配对 Down"被正确丢掉。
        _videoGestureActive = false;
        // 右键那一路同样复位: 按下后拖到视频区外再松手, Up 不会再来 ——
        // 不复位的话下一次孤立的 Up 会被误当成"配对成功"而切一次铺满。
        _rightGestureActive = false;
        if (_longPressFired)
        {
            _longPressFired = false;
            // 同 Up 那条路: 恢复按下前的速率(见 _rateBeforeFastForward)
            StopFastForward();
            // ★ 兜底路径也要收掉提示: 鼠标移出时 Up 不会再来了,
            //   不在这里收的话提示会一直挂在屏幕上(用户报过同类"提示不消失")。
            ShowSpeedHint("已结束快进", hold: false);
        }
        // 兜底: 万一手势态已经乱了(比如 _longPressFired 被别处清掉), 快进态也不能留着
        StopFastForward();
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
        // 与卡片右键"下载视频"同一条链路: 弹独立的下载进度对话框。
        // ★ 下载是**一次性**动作(不像点赞可来回切), 所以这里等结果、成功后把按钮染成绿色
        //   并换成"已下载" —— 设计稿 v4 第 07 节把下载归为"拿走"这一组, 色相是绿。
        //   失败/取消不动按钮(保持中性), 有对话框自己的提示兜底。
        var bvid = _currentBvid;
        _ = DownloadFromPlayerAsync(bvid);
    }

    /// <summary>下载并收尾按钮状态(见 OnCacheClick)。</summary>
    private async Task DownloadFromPlayerAsync(string bvid)
    {
        try
        {
            var saved = await VideoDownloader.DownloadAsync(bvid, VideoTitleText.Text, this);
            if (_closing) return;
            // 期间用户可能已经换片: 只给"还是当前这条"的按钮点绿
            if (saved != null && string.Equals(bvid, _currentBvid, StringComparison.Ordinal))
                MarkCacheDownloaded();
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
        }
    }

    /// <summary>
    /// 把下载按钮标成"已下载"(绿)。
    /// ★ 走 UpdateActionCounts 收口, 不在这里自己刷颜色 —— 否则"绿色"会有两个出口,
    ///   迟早出现"这里绿了那里又灰回去"。
    /// </summary>
    private void MarkCacheDownloaded()
    {
        _cacheDownloaded = true;
        UpdateActionCounts();
        if (BtnCache != null) BtnCache.ToolTip = "已下载";
    }

    // ------------------------------------------------------------ 弹幕

    // --- 弹幕轨道状态(消除重叠的核心) ---
    //
    // 旧实现每条弹幕都 Rand.Next 取一个纵坐标, 弹幕一多必然层层叠在一起。
    // 现在把显示区域切成固定高度的"泳道"(lane), 每条滚动弹幕占据一条泳道;
    // 分配时选择"最早可用"的泳道, 并校验与同泳道前一条弹幕的间距,
    // 保证同一泳道里后一条不会追上(追尾)前一条。
    private double _laneHeight = DefaultLaneHeight();   // 单条泳道高度(随字号联动)
    private double[]? _laneFreeAt;                 // 每条泳道的"可用时间点"(秒, 播放时间轴)
    private double[]? _laneLastWidth;              // 每条泳道最后一条弹幕的宽度
    private double _lastLaneCalcWidth;             // 上次计算泳道时的画布宽度(尺寸变化时重算)
    private double _lastLaneCalcHeight;

    // ---- 外观三项(不透明度 / 字号 / 速度), 2026-10-12 新增 ----
    //
    // ★ 三者的落点各不相同, 所以各有各的重算代价, 别一股脑"改了就清画布":
    //   · 字号    → 改字号必须**重建泳道**: 泳道高是字号推出来的, 不重建的话
    //               新弹幕按新字号画、泳道却还是旧高度, 弹幕就会上下叠在一起
    //               (这正是 DanmakuLaneRatio 那条注释要防的历史 bug)。
    //   · 速度    → 横穿时长变了, 泳道的"可用时间"与追尾校验全部作废, 也必须重建,
    //               否则新弹幕会被按旧速度判成"追上了前一条"而被整批丢弃。
    //   · 不透明度 → 只改基线透明度, 不影响布局, 因此可以**就地改**, 不必清屏;
    //               这一条对"用户正在拖滑杆"很关键 —— 拖一次就闪一次是不能接受的。
    private double _danmakuFontSize = DefaultFontSize();     // 当前实际字号
    private double _danmakuOpacity = 0.8;                    // 基线不透明度 0~1
    private int _danmakuSpeedLevel = 2;                      // 速度档位
    private double _danmakuScrollSeconds = Helpers.DanmakuMotion.ScrollSeconds(2);  // 滚动横穿时长
    private double _danmakuFixedSeconds = Helpers.DanmakuMotion.FixedSeconds(2);    // 固定停留时长

    /// <summary>
    /// 同步弹幕外观滑杆时的回灌保护。
    /// 见 <see cref="UpdateDanmakuLookTexts"/>: 赋 Value 会触发 ValueChanged。
    /// </summary>
    private bool _syncingDanmakuLook;

    /// <summary>基准弹幕字号(倍率 100% 时的值)</summary>
    private const double DanmakuBaseFontSize = 24;

    private static double DefaultFontSize() => DanmakuBaseFontSize;

    /// <summary>默认泳道高(字号 × 系数)。字段初始化器要用到, 所以给个静态入口。</summary>
    private static double DefaultLaneHeight()
        => DanmakuBaseFontSize * DanmakuLaneRatio;


    // ★★★ 滚动/固定弹幕的时长不再是常量(2026-10-12 加了「弹幕速度」设置),
    //   改为按当前档位算出来存在 _danmakuScrollSeconds / _danmakuFixedSeconds,
    //   见 Helpers.DanmakuMotion.ScrollSeconds / FixedSeconds(那边是纯函数, 可单测)。
    //   ★ 下面所有用旧常量名的地方都必须换成实例字段 —— 漏一处就是"速度滑块拖了没反应"。
    // 屏幕上的滚动弹幕上限(内存/渲染保护)
    private const int MaxOnScreenDanmaku = 80;

    /// <summary>基准弹幕字号(2026-10-11 用户要求"弹幕字体调大一些": 18 → 24)。</summary>
    /// <remarks>
    /// 原来这个值直接写在 AddDanmaku 里, 而泳道高 _laneHeight 又另写了一个 28 ——
    /// 两个数没有任何关联, 调字号必得漏掉泳道, 于是弹幕上下压在一起。现在两者都由
    /// <see cref="DanmakuLaneRatio"/> 推出来, 改字号只需要改这一处。
    /// ★ 2026-10-12 起它不再是"写死的值", 而是 100% 档位的基准, 实际字号
    ///   = 本值 × (用户的字号百分比 / 100), 见 <see cref="_danmakuFontSize"/>。
    /// ★ 2026-10-13 删掉了这里的别名常量 `DanmakuFontSize`(它只是 = DanmakuBaseFontSize,
    ///   全项目无引用): 两个名字指同一个值, 改字号时"该改哪一个"会让人停一下。
    ///   真正的定义在文件上方(见 DanmakuBaseFontSize)。
    /// </remarks>

    /// <summary>泳道高 = 字号 × 该系数(留出上下各约四分之一行距, 密集时才不会糊成一片)</summary>
    private const double DanmakuLaneRatio = 1.5;

    // ---- 在飞的弹幕(位移由**视频时钟**驱动, 不再用 WPF 动画) ----
    //
    // ★★★ 2026-10-11 用户报"视频暂停时弹幕不动"——原话是"视频暂停时弹幕不动"(即要停),
    //   根因是原来用 DoubleAnimation 起滚动: WPF 动画走合成线程 + 墙钟, 与播放器状态无关。
    //
    //   为什么没有用"暂停动画"来修(这的路子全都实测堵死, 见 .probes/bd-probe-animfreeze):
    //     · Animation / AnimationStorage / AnimationClock 全是 internal, 外部拿不到;
    //     · SpeedRatio 只在 AnimationTimeline 上, 而 CreateClock() 返回的基类时钟上没有它
    //       ⇒ "begin 之后再回来改 SpeedRatio"在公开 API 里根本走不通;
    //     · 唯一公开的暂停入口 Storyboard.Pause, 在"动画挂在 Border 的 RenderTransform 上"
    //       这种写法下实测**静默无效**(GetCurrentTime 直接报"Storyboard 未应用到该对象")。
    //
    //   所以改成: 每帧从 _mp.Time 取真实媒体时间, 自己算该到哪儿。
    //   这样"暂停"根本不是一个需要处理的状态 —— 媒体时间不前进, 弹幕自然就不动;
    //   顺带还白送了 seek(清表重建)与拖进度条时的正确重同步。
    private readonly List<FlyingDanmaku> _flying = new();
    private bool _danmakuClockAttached;

    // ---- 弹幕时钟: Stopwatch 走时 + 低频校准(2026-11 修"弹幕滑动很卡, 几帧几帧地跳") ----
    //
    // ★★★ 第一版是**每帧**直接读 `_mp.Time`(还多读了一次 `_mp.Length` 判断有没有媒体)。
    //   那是两次**跨语言边界的原生调用 × 每帧 60 次** = 每秒 120 次 P/Invoke,
    //   足以把渲染回调拖慢 ⇒ 用户看到的就是"弹幕几帧几帧地跳"。
    //   ★ 教训: `CompositionTarget.Rendering` 里**一次原生调用都不能有**。
    //     判"是不是还在放媒体"这类检查更不能放这里 —— 它每帧都跑, 哪怕它自己认为很便宜。
    //   ⇒ 改成: 用 `Stopwatch` 自己走时(纯本地), 只在 `_mp.TimeChanged`(约 4Hz)校准一次,
    //     暂停/恢复由 `_mp.Paused` 冻结/解冻。既零 P/Invoke, 又跟得上真实媒体时间
    //     (seek 会在下一个 TimeChanged 立刻纠偏)。
    private readonly Stopwatch _dmWatch = Stopwatch.StartNew();
    /// <summary>上次校准到的媒体时间(秒)</summary>
    private double _dmClockMedia;
    /// <summary>校准那一刻 <see cref="_dmWatch"/> 的毫秒读数 —— 走时的起点</summary>
    private long _dmClockStamp;
    /// <summary>是否在走时。暂停/未播放时为 false, 此时弹幕时钟就是 _dmClockMedia</summary>
    private bool _dmClockRunning;

    /// <summary>
    /// 走时期间的**播放倍速**。外推必须乘上它, 否则倍速播放时弹幕会一顿一顿。
    ///
    /// ★★★ 2026-10-12 修用户报的"倍速下弹幕滑动不连贯、卡顿":
    ///   弹幕时钟是"校准点 + 墙钟流逝"外推出来的, 而**媒体时间在 2 倍速下走得比墙钟快一倍**。
    ///   乘 1.0 的话外推值始终落后真实媒体时间, 每约 250ms 被校准强行拉回一次 ——
    ///   表现就是弹幕匀速前进一小段、然后**突然跳**一截, 即"不连贯/卡顿"。
    ///   倍速越大越明显(0.5x 则反过来, 外推跑太快、每次校准往回拽)。
    ///
    /// ★ 取值时机: 每次校准(<see cref="CalibrateDanmakuClock"/>)时读一次 `_mp.Rate` ——
    ///   那是约 4Hz 的低频事件, 一次原生调用不心疼; 而这里(逐帧路径)必须是**纯本地字段读取**。
    ///   切换倍速后最迟 250ms 内就会被下一次校准刷新, 肉眼看不出来。
    private double _dmClockRate = 1.0;

    /// <summary>
    /// 当前弹幕时钟(媒体秒)。每帧调用, **零原生调用** —— 只做一次 Stopwatch 读数 + 一次乘加。
    ///
    /// ★ 算式本体在 <see cref="Helpers.DanmakuMotion.Extrapolate"/> 里(纯函数, 探针可断言) ——
    ///   这里只负责"取当前墙钟读数"这一步, 保持热路径不碰任何非托管资源。
    /// </summary>
    private double DanmakuClock() => _dmClockRunning
        ? Helpers.DanmakuMotion.Extrapolate(
            _dmClockMedia, (_dmWatch.ElapsedMilliseconds - _dmClockStamp) / 1000.0, _dmClockRate)
        : _dmClockMedia;

    /// <summary>
    /// 用播放器推来的真实媒体时间校准一次(顺带把走时起点重置到"现在")。
    /// 只在 <c>_mp.TimeChanged</c> 里调 —— 那是约 4Hz 的低频事件, 一帧一次的原生调用在这里不心疼。
    /// </summary>
    private void CalibrateDanmakuClock(double mediaSeconds)
    {
        _dmClockMedia = mediaSeconds;
        _dmClockStamp = _dmWatch.ElapsedMilliseconds;
        // ★ 顺便刷新倍速(这里读原生 Rate 是低频路径, 不违反"逐帧零 P/Invoke"那条铁律)。
        //   Rate 偶尔会返 0(切换/初始化的一瞬), 那时按 1x 走, 别让外推停摆。
        var rate = _mp?.Rate ?? 1.0f;
        _dmClockRate = rate > 0 ? rate : 1.0;
        _dmClockRunning = true;
    }

    /// <summary>
    /// 暂停/恢复时冻结/解冻弹幕时钟。
    ///
    /// ★★★ **暂停时必须冻结在"当前外推值", 不能直接读 `_mp.Time`**(2026-11-11 用户报
    ///   "暂停时弹幕会突然向后移动一点"): 校准是约 4Hz 的 TimeChanged 驱动的, 两次校准之间
    ///   弹幕钟按墙钟外推; 若外推快于实际播放(缓冲/掉帧时 TimeChanged 间隔被拉长), 冻结那一刻
    ///   `_mp.Time` 就落后于外推值 ⇒ 弹幕整体回跳一小段。冻结语义应当是"停在此刻所见",
    ///   即当前的 DanmakuClock() 读数, 而不是重新取一次媒体时间。
    /// ★ 恢复时**也**以暂停时的冻结值为基准直接续跑, 不重新读 `_mp.Time`:
    ///   暂停没 seek 的话两者本就几乎相等, 而读 `_mp.Time` 若略小于冻结值,
    ///   恢复瞬间弹幕又会回跳一次(与暂停回跳同机理)。真正需要纠偏的场景(seek)
    ///   由 TimeChanged → CalibrateDanmakuClock 在 250ms 内自然收敛, 不必抢这一帧。
    /// </summary>
    private void SetDanmakuClockRunning(bool running)
    {
        if (running == _dmClockRunning || _closing || _mp == null) return;
        // 两侧都不额外取媒体时间: 冻结取"此刻所见", 恢复从"冻结处"续跑。
        // 基准重置到"现在"是必须的 —— Stopwatch 一直在走, 不重置恢复瞬间会前跳一大段。
        _dmClockMedia = DanmakuClock();
        _dmClockStamp = _dmWatch.ElapsedMilliseconds;
        _dmClockRunning = running;
    }

    /// <summary>
    /// 倍速刚被改变时立刻同步弹幕时钟的倍速(而不是等下一次 4Hz 校准)。
    ///
    /// ★ 为什么需要它: <see cref="_dmClockRate"/> 是在校准里刷新的, 而校准约 250ms 一次。
    ///   用户点"倍速"那一瞬到下一次校准之间, 弹幕仍按旧倍速外推 —— 2x 时约 250ms 的
    ///   位置偏差, 下一次校准会**突然拽回去**, 那一顿仍然看得见。
    ///   ★ 做法与暂停/恢复同款: 先把当前外推值**落定**(重置基准), 再换速率 ——
    ///     这样位置连续, 只是速度从这一刻起变了, 不会有跳变。
    ///   ★ 必须在**改 Rate 之前**调用: 否则读到的还是旧速率, 等于没换。
    /// </summary>
    private void SyncDanmakuClockRate(float newRate)
    {
        if (_closing || _mp == null) return;
        // 先把"此刻所见"落成新的校准点(位置连续), 再换速率
        _dmClockMedia = DanmakuClock();
        _dmClockStamp = _dmWatch.ElapsedMilliseconds;
        _dmClockRate = newRate > 0 ? newRate : 1.0;
    }

    /// <summary>屏幕上正在飞的一条弹幕</summary>
    private sealed class FlyingDanmaku
    {
        public Border Element = null!;
        public TranslateTransform Translate = null!;
        /// <summary>上屏那一刻的媒体时间(秒)。位移 = f(当前媒体时间 - 它)</summary>
        public double SpawnClock;
        /// <summary>滚动: 横穿总时长。固定: 停留总时长。</summary>
        public double TotalSeconds;
        /// <summary>滚动弹幕自身宽度(用于算"完全离开左侧"的位置)</summary>
        public double Width;
        /// <summary>true = 顶部/底部固定弹幕(不动横向, 只按时间淡出)</summary>
        public bool IsFixed;
    }

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
        // ★ 校准弹幕时钟: 逐帧推进只读本地 Stopwatch, 靠这里(约 4Hz)把它拉回真实媒体时间。
        //   拖进度条/seek 之后, 下一个 TimeChanged 就会纠偏, 弹幕不会继续按旧位置飞。
        CalibrateDanmakuClock(currentSeconds);

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
        // ★ 在飞的弹幕一并收掉(2026-10-11): 跳转之后旧的弹幕必须立刻消失, 否则它们的
        //   SpawnClock 还停在跳转前的位置, elapsed 会变成负数/极大值 —— 前者被钳成 0
        //   (满屏弹幕突然全挤在右边缘), 后者直接被判定为"已飞完"集体闪没。
        //   收掉之后 DispatchDanmaku 会按新位置重新派发, 这才是"跳过去就该看到那边的弹幕"。
        if (_flying.Count > 0)
        {
            foreach (var f in _flying) DanmakuCanvas?.Children.Remove(f.Element);
            _flying.Clear();
        }
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

        // 弹幕从左(或右)走到完全离开所需时间 = _danmakuScrollSeconds(随速度档位变)
        // 速度(像素/秒) = (canvasWidth + width) / _danmakuScrollSeconds
        var speed = (canvasWidth + width) / _danmakuScrollSeconds;

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
            var prevSpeed = (canvasWidth + prevWidth) / _danmakuScrollSeconds;
            var prevElapsed = _danmakuScrollSeconds - remaining;
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
                table.Add((currentSeconds + _danmakuFixedSeconds, s));
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
    ///      几十条弹幕同屏就是几十次全像素处理。★ 曾经靠"半透明黑底衬"给描边感,
    ///      现已按用户要求去掉(见下面那段的说明) —— 但**仍然不许挂 Effect**;
    ///   2. **动画只动 RenderTransform**: 动 Canvas.Left 每帧都要走一遍 Canvas 布局,
    ///      动 TranslateTransform 只发生在合成线程的变换矩阵上, 完全不碰布局。
    /// </summary>
    private void AddDanmaku(string text, int mode, double time, uint color = 0xFFFFFF)
    {
        if (DanmakuCanvas == null || string.IsNullOrEmpty(text)) return;
        if (DanmakuCanvas.ActualWidth <= 0 || DanmakuCanvas.ActualHeight <= 0) return;
        // 屏幕弹幕上限, 防止高弹幕量视频把内存和渲染拖垮
        if (DanmakuCanvas.Children.Count >= MaxOnScreenDanmaku) return;

        // ★ 用弹幕时钟(已被 DispatchDanmaku 校准过), 不再读 `_mp.Time`:
        //   这里虽是低频路径, 但它与逐帧推进必须是**同一个时间源**, 否则新弹幕的出生点
        //   和在飞弹幕算 elapsed 的基准会差一次 Stopwatch 走时, 表现为"新弹幕进场位置偏一点"。
        var currentSeconds = DanmakuClock();
        if (currentSeconds <= 0) currentSeconds = time;   // 还没起播(极少见): 退回这条弹幕自带的时间

        // ★★★ 只留文字, 不要长方形底衬(2026-10-07 用户要求"弹幕文字有长方形背景阴影, 移除它")。
        //   原先这里给了 Background = 60% 黑 + CornerRadius=3, 屏幕上每条弹幕都压着一块
        //   深色小方块 —— 用户看到的就是"文字后面有个长方形阴影"。
        //   ⇒ Background 留 null(完全不画底), CornerRadius 随之无意义、一并去掉。
        //
        //   ★ Padding **保留**: 它不是装饰, 而是参与 width 计算(见下面的 Measure),
        //     泳道分配与"追尾校验"都拿这个宽度当"这条弹幕要占多宽"。
        //     去掉它会让相邻弹幕的横向间距归零, 密集时文字首尾相接糊成一片。
        //   ★ 不要为了"看不清"改回加底衬或挂 DropShadowEffect: 前者就是用户要去掉的东西,
        //     后者违反上面第 1 条铁律(几十条同屏 = 几十次全像素合成)。
        var border = new Border
        {
            Padding = new Thickness(6, 1, 6, 1),
            // ★ 基线不透明度: 压在亮画面上的白字太实反而晃眼, 这里给用户一个可调的档。
            //   滚动弹幕此后不会再被改 Opacity, 所以这里设一次就够;
            //   固定弹幕的淡出是"在这个基线上再乘一个系数"(见 OnDanmakuRendering)。
            Opacity = _danmakuOpacity,
            // RenderTransform 在这里预置好, 滚动弹幕直接拿它做位移动画
            RenderTransform = new TranslateTransform(0, 0),
            Child = new TextBlock
            {
                Text = text,
                Foreground = DanmakuBrush(color),
                FontSize = _danmakuFontSize
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

    /// <summary>
    /// 滚动弹幕: 分泳道 + 横向滚动。
    ///
    /// ★ 这里**不起任何 WPF 动画**, 只把这条弹幕登记进 <see cref="_flying"/>,
    ///   位置由 <see cref="OnDanmakuRendering"/> 按视频时钟每帧算。理由见 _flying 的说明。
    /// </summary>
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
        _laneFreeAt![lane] = currentSeconds + _danmakuScrollSeconds;
        if (_laneLastWidth != null) _laneLastWidth[lane] = width;

        Canvas.SetTop(border, 6 + lane * _laneHeight);

        var canvasWidth = DanmakuCanvas.ActualWidth;
        // 预置到"刚好在画面右侧外", 第一帧就是进场位置(下方每帧会立刻覆盖成精确值)
        var translate = new TranslateTransform(canvasWidth, 0);
        border.RenderTransform = translate;

        TrackFlying(new FlyingDanmaku
        {
            Element = border,
            Translate = translate,
            SpawnClock = currentSeconds,
            TotalSeconds = _danmakuScrollSeconds,
            Width = width,
        });
    }

    /// <summary>顶部/底部固定弹幕: 居中停留数秒后淡出(透明度同样由视频时钟算, 不用动画)</summary>
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

        // 横向不需要位移, 但 TranslateTransform 仍要挂一个: 逐帧代码按同一套逻辑处理两种弹幕
        var translate = new TranslateTransform(0, 0);
        border.RenderTransform = translate;
        border.Opacity = 1;

        TrackFlying(new FlyingDanmaku
        {
            Element = border,
            Translate = translate,
            SpawnClock = currentSeconds,
            TotalSeconds = _danmakuFixedSeconds,
            IsFixed = true,
        });
    }

    /// <summary>登记一条在飞的弹幕, 并确保逐帧推进器已经挂上</summary>
    private void TrackFlying(FlyingDanmaku f)
    {
        _flying.Add(f);
        StartDanmakuClock();
    }

    /// <summary>挂上逐帧推进器。CompositionTarget.Rendering 是**静态**事件, 必须在关窗时解绑</summary>
    private void StartDanmakuClock()
    {
        if (_danmakuClockAttached || _closing) return;
        _danmakuClockAttached = true;
        CompositionTarget.Rendering += OnDanmakuRendering;
    }

    private void StopDanmakuClock()
    {
        if (!_danmakuClockAttached) return;
        _danmakuClockAttached = false;
        CompositionTarget.Rendering -= OnDanmakuRendering;
    }

    /// <summary>
    /// 逐帧推进所有在飞的弹幕。
    ///
    /// ★ 时间的唯一来源是 _mp.Time(真实媒体时间, 毫秒精度), 不是墙钟。
    ///   于是"视频暂停"这件事**根本不需要分支**: 媒体时间不前进 ⇒ elapsed 不变 ⇒ 位置不变。
    ///   这正是原来用 DoubleAnimation 做不到的地方(那个走合成线程墙钟, 跟播放器无关)。
    ///
    /// 反过来在播放时它还顺带修好了另一件事: 弹幕现在与画面**帧同步**,
    /// 拖进度条后 DispatchDanmaku 检测到游标失效会 ResetLanes(连带清空 _flying),
    /// 所以不会看到"老弹幕从旧位置继续飞"。
    /// </summary>
    private void OnDanmakuRendering(object? sender, EventArgs e)
    {
        if (_flying.Count == 0)
        {
            // 一条都没在飞就没必要继续挂在渲染回调上(它每帧都会被调, 空转也是成本)
            StopDanmakuClock();
            return;
        }
        if (_closing) return;

        // ★ 只读一次本地 Stopwatch, **一次原生调用都不做**(见 _dmWatch 那段说明)
        var now = DanmakuClock();
        var canvasWidth = DanmakuCanvas?.ActualWidth ?? 0;
        // 倒序删: 末尾先走完, 正好与"新弹幕追加在尾部"的写入方向相反, 不会出现漏删
        for (var i = _flying.Count - 1; i >= 0; i--)
        {
            var f = _flying[i];
            var elapsed = now - f.SpawnClock;
            if (elapsed < 0) elapsed = 0;   // 刚上屏/刚重建泳道时的取整误差, 不让它闪到画面外

            if (f.IsFixed)
            {
                // ★★★ 必须**乘**基线不透明度, 不能直接赋值(2026-10-12 加不透明度时踩到):
                //   原来这一行是把 Opacity 直接设成淡出系数(0~1)。用户一旦把"不透明度"调到
                //   80%, 固定弹幕到了淡出阶段就会被这一行**覆盖回 100%** —— 表现为
                //   "滑杆拖了没用, 只有滚动弹幕变淡, 顶/底的始终全不透明"。
                //   现在 Opacity = 用户基线 × 淡出系数, 两个设置才真正叠加。
                f.Element.Opacity = _danmakuOpacity
                    * Helpers.DanmakuMotion.FixedOpacity(elapsed, f.TotalSeconds);
            }
            else
            {
                f.Translate.X = Helpers.DanmakuMotion.RollingX(canvasWidth, f.Width, elapsed, f.TotalSeconds);
            }

            if (Helpers.DanmakuMotion.IsFinished(elapsed, f.TotalSeconds))
            {
                DanmakuCanvas?.Children.Remove(f.Element);
                _flying.RemoveAt(i);
            }
        }
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

        // 外观三项(不透明度/字号/速度)。每项的重算代价不同, 由 ApplyDanmakuLook 内部判断,
        // 这里统一调一次即可 —— 播放器和设置页改的是同一批字段, 不该各写一套同步。
        ApplyDanmakuLook();
        UpdateDanmakuLookTexts();

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

    /// <summary>
    /// 把「不透明度 / 字号 / 速度」三项设置落到运行时, 并只做**必要**的重算。
    ///
    /// ★ 为什么不一律"改了就清屏重建":
    ///   · 不透明度: 就地改每个在飞弹幕的 Opacity 即可。不清屏 ——
    ///     用户是**拖着滑杆**在调的, 每动一格就闪一次是不能接受的。
    ///   · 字号/速度: 会让泳道表与追尾校验失效, 必须重建并清掉旧弹幕,
    ///     否则要么弹幕叠在一起, 要么新弹幕被旧判定整批丢弃(看起来就是"弹幕没了")。
    ///
    /// 三项都没变时直接返回 —— 设置页里改"关键词屏蔽"也会走到这里, 不该白清一次屏。
    /// </summary>
    private void ApplyDanmakuLook()
    {
        var s = Svc.Settings;
        var nextOpacity = Helpers.DanmakuMotion.OpacityFromPercent(s.DanmakuOpacityPercent);
        var nextFont = Helpers.DanmakuMotion.FontSizeFromPercent(s.DanmakuFontScalePercent, DanmakuBaseFontSize);
        var nextLevel = Math.Clamp(s.DanmakuSpeedLevel,
            Helpers.DanmakuMotion.MinSpeedLevel, Helpers.DanmakuMotion.MaxSpeedLevel);

        var opacityChanged = Math.Abs(nextOpacity - _danmakuOpacity) > 0.001;
        var fontChanged = Math.Abs(nextFont - _danmakuFontSize) > 0.01;
        var speedChanged = nextLevel != _danmakuSpeedLevel;

        if (!opacityChanged && !fontChanged && !speedChanged) return;

        _danmakuOpacity = nextOpacity;
        _danmakuFontSize = nextFont;
        _danmakuSpeedLevel = nextLevel;
        _danmakuScrollSeconds = Helpers.DanmakuMotion.ScrollSeconds(nextLevel);
        _danmakuFixedSeconds = Helpers.DanmakuMotion.FixedSeconds(nextLevel);

        // 字号变了必须同步泳道高 —— 这是历史 bug 的根(DanmakuLaneRatio 那条注释)
        _laneHeight = _danmakuFontSize * DanmakuLaneRatio;

        // 不透明度不需要清屏: 基线字段改完, 在飞的弹幕在下一帧自然按新基线渲染
        // (固定弹幕的 Opacity 每帧被"基线 × 淡出系数"覆写, 所以只改基线就够)。

        if (fontChanged || speedChanged)
        {
            // 布局变了, 在飞的那些按旧尺寸/旧速度继续飞会突然错位, 一并清掉
            ResetLanes();
            DanmakuCanvas.Children.Clear();
            _flying.Clear();
            if (_danmakuOn) StartDanmakuClock();
        }
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
        // ★ 走统一入口(分段跳 / 重建后跳), 见 SeekTo 的说明
        SeekTo((long)(_mp.Time + seconds * 1000));
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
        // ★★★ 全屏切换期间**不要**让"最大化/还原黑罩"插手(2026-10-17):
        //   全屏 = 最大化 + 收顶栏, 所以它必然触发 WM_SYSCOMMAND / 尺寸剧变;
        //   而这条路径本来就有自己的一套遮罩时序(下面的 ArmFullscreenCover / ReleaseFullscreenCover)。
        //   两套叠加的实测后果: 退全屏后视频区**黑约 0.9 秒**(被这边的兜底计时器按住)。
        //   ⇒ 整段(含之后由消息循环送达的状态变化)排掉, 时长给足 —— 进出全屏最慢也就几百毫秒。
        SuppressStateCoverFor(1600);

        if (on)
        {
            // ★ 全屏期间**禁止**"失焦换窗口底色"那套处理接管(2026-10-06):
            //   全屏要求所有缝隙纯黑(紧接着那两处赋 Black), 而全屏途中一旦点到别的窗口,
            //   失焦逻辑会把 Background 改成主题实色 ⇒ 视频周围露出浅色缝。
            //   ★ 必须放在**赋 Black 之前**: 放在后面的话, 这中间的失焦回调仍有可能插进来。
            //   退出全屏时在下面把它放开。
            SuppressAcrylicActivationSwap = true;

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

            // ★★ 进全屏同样会按新尺寸重建 vout ⇒ 新的 `Static` 子窗口又没被刷黑,
            //   全屏时画面两侧/上下会露白边。与退全屏对称, 这里也刷一次。
            StartVideoBackdropFix();
        }
        else
        {
            // 先改外观再改几何: 窗口在缩回去的过程中露出来的边角就是正确的主题色,
            // 而不是刚才那块"全屏黑"。(FluentWindow 按深浅主题挑 WindowSolid*)
            MaximizeCoversTaskbar = false;
            // 放开"失焦换底色"的闸门: ApplyChrome 紧接着会按当前激活态重新给出正确的窗口底色
            // (全屏期间它被上面那行拦住了, 现在要还给这套机制)。
            SuppressAcrylicActivationSwap = false;
            ApplyChrome();
            // ★★ 不要在这里补 WS_CAPTION: 它由下面对"窗口状态还原之后"那次
            //   SyncCaptionForAcrylic 统一补(那时 WindowState 已回到 Normal, 才补得回来) ——
            //   在这里补会因为窗口还是 Maximized 而被跳过, 退全屏后就没动画了。
            //   (重复按钮的克制现在靠去掉 WS_SYSMENU, 不再靠摘 WS_CAPTION。)

            ReleaseFullscreenCover();
            // ★★ 退出全屏后 libvlc 会按新尺寸**重建 vout**, 那里面的 `Static` 子窗口是新的,
            //   之前设的黑色类背景刷跟着旧窗口一起没了 ⇒ 视频区边缘又露出白(用户报的
            //   "先全屏再退全屏, 播放器页面部分出现白色区域")。
            //   所以这里必须重刷一次(和起播后那次同一个入口, 见 ApplyHwndHostBlackBackground)。
            StartVideoBackdropFix();
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
    /// 视频区遮罩: 在"窗口尺寸正在变"的窗口期把手势层压黑, 盖住 vout HWND 露出的白底。
    ///
    /// ★★ 2026-10-05 用户报"拖动窗口边缘调节大小时视频区闪白"—— 本方法原先只挂在**全屏切换**上,
    ///   而拖边缘 resize 走的是完全不同的代码路径, 从不经过这里, 于是白底照样露出来。
    ///   它挡的机制与全屏完全相同(LibVLC 要重协商 vout, 那一瞬没有新帧 → 露出 vout 的默认白底),
    ///   所以复用一个遮罩即可, 不必再造第二套。触发点见 <see cref="ArmFullscreenCover"/> /
    ///   <see cref="ArmResizeCover"/>(后者由 WM_ENTERSIZEMOVE 驱动)。
    /// </summary>
    ///
    /// <remarks>
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
    /// </remarks>
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

    /// <summary>
    /// 撤掉视频区遮罩(视频已经在出帧了, 再挡着就看不见画面了)。
    ///
    /// ★★ _resizeHold 这道闸是修"拖边缘闪白"的关键(2026-10-05):
    ///   拖动期间 LibVLC 会**反复**重协商 vout, 每轮都露一次白底。若此时 TimeChanged /
    ///   Playing 一发就把遮罩撤了, 后面每一轮重协商又白一下 —— 表现为"闪白依旧"。
    ///   所以按住期间(TimeChanged/Playing/兜底超时 都会走到这里)**一律不撤**,
    ///   只等 WM_EXITSIZEMOVE 由 ReleaseResizeHold 放闸后, 这里才真正生效。
    /// </summary>
    private void ReleaseFullscreenCover()
    {
        // ★ 拖动按住期间不放行(计时器也不停: 它只做"万一 EXIT 没送到"的兜底)
        if (_resizeHold) return;
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

    /// <summary>
    /// resize 期间的视频区遮罩。
    ///
    /// ★★★ 这一版与上一版的区别, 以及上一版为什么没修好(2026-10-05 用户反馈"闪白问题依旧"):
    ///   上一版是"进 resize 时压一次, 靠 TimeChanged/Playing 收到新帧就撤"。看着对, 但拖动期间
    ///   窗口尺寸**每帧都在变** ⇒ LibVLC 每一帧都在重协商 vout ⇒ 白底反复露出。
    ///   第一次重协商一结束遮罩就被撤了, 后面每一轮重协商又白一下 —— 观感就是"闪白依旧",
    ///   而且用户截图里那种整块白正是这一轮轮的叠加。
    ///   ★ 所以正确语义是"按住整个拖动期": 进入压黑, **期间谁来撤都不放行**(见 _resizeHold),
    ///     一直到 WM_EXITSIZEMOVE 才撤(见 OnModalResizeEnded)。
    ///   ★ 为什么仍然复用 _fullscreenCoverArmed 而不是另开一个字段: 两者最终都只改
    ///     VideoAreaRoot.Background, 各开一份状态就会出现两份状态互抢同一属性。
    ///     _resizeHold 是"额外的闸": 它为真时 ReleaseFullscreenCover 拒绝撤罩。
    /// </summary>
    private void ArmResizeCover()
    {
        if (_closing || VideoAreaRoot == null) return;
        _resizeHold = true;
        VideoAreaRoot.Background = Brushes.Black;
        _fullscreenCoverArmed = true;
        // ★ 计时器在按住期间**只做兜底**: 它到期时若还在拖动就不撤(由 _resizeHold 挡住),
        //   万一 WM_EXITSIZEMOVE 因为异常路径没送到, 至少不会永远黑屏。
        _fullscreenCoverTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(FullscreenCoverTimeoutMs)
        };
        _fullscreenCoverTimer.Tick -= OnFullscreenCoverTimeout;
        _fullscreenCoverTimer.Tick += OnFullscreenCoverTimeout;
        _fullscreenCoverTimer.Start();
    }

    /// <summary>拖动结束: 放掉"按住不放"的闸, 正常恢复由新帧驱动</summary>
    private void ReleaseResizeHold()
    {
        _resizeHold = false;
    }

    /// <summary>
    /// 拖动期间**按住**遮罩(2026-10-05 修"拖边缘 resize 闪白"的真正修法)。
    /// 见 <see cref="ArmResizeCover"/> 的说明。</summary>
    protected override void OnModalResizeStarted()
    {
        // ★★ 2026-10-06 修"拖动窗口**移动位置**时黑屏":
        //   这对钩子 WM_ENTERSIZEMOVE/EXITSIZEMOVE 对"移动"和"改大小"一视同仁, 但两者对视频
        //   的意义完全不同 ——
        //     · 改大小: 客户区尺寸每帧都变 ⇒ LibVLC 反复重协商 vout ⇒ 会露白底, **需要**压罩;
        //     · 纯移动: 窗口尺寸**一点没变** ⇒ LibVLC 根本不重协商 vout ⇒ 没有任何东西会露白底,
        //       此时压黑纯属多余, 而它的伤害是实的(见 OnModalResizeEnded 里"撤不掉"那条)。
        //   原先不分两者 ⇒ 拖完位置黑屏一直不撤。
        //
        // ★★ 为什么**不能**在 ENTER 时就判断"这次是不是改大小"(第一版这么写, 已否掉):
        //   ENTER 发在拖动**开始之前**, 那一刻尺寸当然还没变 ⇒ 任何"现在比一下"的判断都会
        //   把"斜向拖边缘"(位置和尺寸同时变)误判成纯移动 ⇒ 不压罩 ⇒ 闪白回归。
        //   所以改成**延迟判定**: ENTER 只记下矩形, 之后由尺寸真的变了再补压(见 StartModalResizeSizeWatch)。
        _lastModalRect = new Rect(Left, Top, Width, Height);
        _fullscreenCoverArmed = false;
        _resizeHold = false;
        // 不排计时器: 纯移动压根不该压罩, 计时器留给真正压上之后做兜底。
        StartModalResizeSizeWatch();
    }

    /// <summary>
    /// 拖动期间盯住"尺寸真的开始变了吗"。变了才补压遮罩(斜向拖边缘的场景)。
    /// 用 <see cref="CompositionTarget"/> 的 Rendering 回调而不是 DispatcherTimer:
    /// 它跟着渲染帧走, 每帧一次, 拖动结束自然停。
    /// </summary>
    private void StartModalResizeSizeWatch()
    {
        if (_modalSizeWatchAttached) return;
        _modalSizeWatchAttached = true;
        CompositionTarget.Rendering += OnModalResizeRendering;
    }

    private void OnModalResizeRendering(object? sender, EventArgs e)
    {
        if (_closing)
        {
            StopModalResizeSizeWatch();
            return;
        }

        // 尺寸确实变了 ⇒ 这次是"改大小"(可能同时在移动), 按住遮罩
        if (!_fullscreenCoverArmed &&
            (Math.Abs(Width - _lastModalRect.Width) > 0.5 ||
             Math.Abs(Height - _lastModalRect.Height) > 0.5))
        {
            ArmResizeCover();
        }

        // 一旦压上就不再需要盯了(遮罩要一直按住到 EXIT)
        if (_fullscreenCoverArmed) StopModalResizeSizeWatch();
    }

    private void StopModalResizeSizeWatch()
    {
        if (!_modalSizeWatchAttached) return;
        _modalSizeWatchAttached = false;
        CompositionTarget.Rendering -= OnModalResizeRendering;
    }

    /// <summary>这次模态拖动开始时记下的窗口矩形, 用来判断尺寸有没有变过</summary>
    private Rect _lastModalRect;
    private bool _modalSizeWatchAttached;

    /// <summary>
    /// 拖动结束才撤遮罩 —— 不是靠"新帧到达"(那样会在拖动中途被反复放行, 闪白依旧)。
    ///
    /// ★★ 2026-10-06: 纯移动(尺寸未变)根本没压过遮罩, 这里必须**无条件**把底色还原 ——
    ///   不能"只在压过时才撤": 用户拖完位置看到的黑屏, 恰恰是遮罩**已经撤不掉**的那一类
    ///   (靠 TimeChanged 等新帧恢复, 而移动时视频尺寸没变、LibVLC 不出新帧; 暂停时更是完全不发)。
    /// </summary>
    protected override void OnModalResizeEnded()
    {
        StopModalResizeSizeWatch();
        ReleaseResizeHold();
        // ★★ 2026-10-06 纯移动(尺寸未变)时压根没压过遮罩, 但底色**必须**还原 ——
        //   这正是"拖完位置黑屏"的那一类: 遮罩一旦压上就只能等 TimeChanged 等新帧来撤,
        //   而移动时窗口尺寸没变、LibVLC 不出新帧, 暂停时更是完全不发 ⇒ 撤不掉。
        //   这里无条件走一次撤罩, 无论压没压过。
        ReleaseFullscreenCover();
    }
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
    ///
    /// ★ 视频区**右键**也切同一个开关(见 OnVideoAreaMouseRightUp): 那个手势不需要文案
    ///   —— 右键按下去画面当场就变了, 状态是"看得见"的, 不用靠字形容器。
    /// </summary>
    private void UpdateFillWindowButton()
    {
        if (BtnFillWindow == null) return;
        // E9A6 = 虚线框(填充区域) / E73F = 向内的双箭头(还原)。两者在本字体里形状差异明显。
        if (FillWindowGlyph != null) FillWindowGlyph.Text = _isFillWindow ? "\uE73F" : "\uE9A6";
        BtnFillWindow.ToolTip = _isFillWindow ? "还原 (退出铺满窗口)" : "铺满窗口";
    }

    private void OnFullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();

    // ---------------------------------------- 最大化 / 还原: 视频区白块

    /// <summary>
    /// 窗口**最大化/还原**期间把视频区压黑, 盖住 libvlc 重协商 vout 时的白底。
    ///
    /// ★★★ 为什么必须有这一段(2026-10-17 用户澄清"全屏"其实是**窗口最大化**后立刻查清):
    ///   视频区原有的黑罩(<see cref="ArmResizeCover"/>)**只挂在
    ///   `WM_ENTERSIZEMOVE`/`WM_EXITSIZEMOVE` 上** —— 那是"用户拖窗口边缘改大小"的消息。
    ///   而最大化/还原走的是 **WM_SYSCOMMAND + WM_SIZE**, 根本不经过 ENTERSIZEMOVE
    ///   ⇒ 那段"客户区尺寸剧变 + vout 重协商"全程**没有任何遮罩**,
    ///   于是浅色主题下还原之后视频区上下就露出白块。
    ///   (深色主题下露出来的也是同一块白, 只是周围本来就暗, 不如浅色扎眼 —— 与用户
    ///     "浅色才明显"的描述一致。)
    ///
    /// ★ 为什么还要重刷原生窗口底色(调 `StartVideoBackdropFix`):
    ///   最大化/还原会让 libvlc 按新尺寸**重建 vout**, 新的 `Static` 子窗口又没被刷黑;
    ///   而第一版的定时器在起播后几拍就停了(`_videoBackdropTries` 用完), 这时没人管。
    ///
    /// ★ 为什么用"遮罩 + 延迟撤"而不是直接切窗口底色:
    ///   与全屏同一套理由 —— `VideoView` 是 HwndHost(airspace), 画在所有 WPF 元素之上,
    ///   窗口/RootGrid 的底色**盖不住它**; 只有 `VideoAreaRoot`(在 VideoView.Content 浮层里)
    ///   压得住, 见 <see cref="ArmFullscreenCover"/> 的说明。
    ///
    /// ★ 为什么不等状态稳定后再压、而是**先压**:
    ///   白块出现在"尺寸正在变"的那几十毫秒里, 等 `StateChanged` 送达时窗口往往已经重绘完,
    ///   那块白早就闪过去了。所以顺序是: 压黑 → 改状态 → 让新帧来撤。
    ///
    /// ★★★ 这里**不是"压一次就按住"**, 而是"每次尺寸变化都重新压 + 重新计时"(2026-10-17):
    ///   最大化/还原在 DWM 过渡动画里会连着发好几次尺寸变化(几十毫秒一次),
    ///   每次都会让 libvlc 重协商一轮 vout。如果只压第一次, 后面的每一轮又会露白
    ///   (实测: 只压第一次 → 还原后边带亮度 242; 每次都重压 → 0)。
    ///   ⇒ 语义是"**只要尺寸还在变, 就一直压着**", 与拖边缘那条(ENTER→EXIT)一致。
    /// </summary>
    private void ArmMaximizeCover()
    {
        if (_closing || VideoAreaRoot == null) return;
        _resizeHold = true;
        _maximizeCoverArmed = true;
        VideoAreaRoot.Background = Brushes.Black;
        _fullscreenCoverArmed = true;

        // ★★ 同时**立刻**重刷 vout 原生底色并起短时守护(2026-10-17 加):
        //   尺寸剧变后 libvlc 会重新协商 vout, 那一刻 `Static` 子窗口会用**自己的类默认背景(白)**
        //   擦一次背景 —— 而它铺满整个视频区, 盖在画面之外的地方(letterbox/控制栏下方)。
        //   实测(bd-probe-barwho): 三者的几何完全一致, 白条却出现在底部 12% 处
        //   ⇒ 白不是"窗口没跟上布局", 就是 `Static` 自己擦出来的。
        //   ⇒ 每次尺寸变化都重新藏它 + 刷黑, 并让守护把接下来 3 秒盖住。
        StartVideoBackdropFix();

        // ★ 关键: 每次进来都**重新起算** settle —— 尺寸还在变就一直压着。
        _maximizeSettle ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(MaximizeSettleMs)
        };
        _maximizeSettle.Tick -= OnMaximizeSettle;
        _maximizeSettle.Tick += OnMaximizeSettle;
        _maximizeSettle.Stop();
        _maximizeSettle.Start();

        // ★ 绝对兜底: `_resizeHold` 会让 ReleaseFullscreenCover 直接 return, 而最大化/还原
        //   **没有** WM_EXITSIZEMOVE 那样的结束消息。万一 settle 也没等到, 遮罩就永远黑着
        //   (用户会看到"视频区全黑")。所以再挂一道无条件放闸的计时器。
        _maximizeCoverSafety ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(MaximizeCoverSafetyMs)
        };
        _maximizeCoverSafety.Tick -= OnMaximizeCoverSafety;
        _maximizeCoverSafety.Tick += OnMaximizeCoverSafety;
        _maximizeCoverSafety.Stop();
        _maximizeCoverSafety.Start();

        // 普通兜底计时器在按住期间只做"万一"之用(见 OnFullscreenCoverTimeout → ReleaseFullscreenCover
        // 会被 _resizeHold 挡住), 这里启动只是保持与拖边缘那条路同构。
        _fullscreenCoverTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(FullscreenCoverTimeoutMs)
        };
        _fullscreenCoverTimer.Tick -= OnFullscreenCoverTimeout;
        _fullscreenCoverTimer.Tick += OnFullscreenCoverTimeout;
        _fullscreenCoverTimer.Start();
    }

    /// <summary>
    /// 尺寸已经稳定(一段时间没有再变)⇒ 放闸, 让"下一帧到达"立刻揭幕。
    /// ★ 只放闸、不直接揭幕: 尺寸刚停的这几帧 vout 可能还在重协商, 立刻揭幕会露白。
    ///   交给新帧(TimeChanged/Playing 会调 ReleaseFullscreenCover)是最准的时机。
    /// </summary>
    private void OnMaximizeSettle(object? sender, EventArgs e)
    {
        _maximizeSettle?.Stop();
        if (_closing) return;
        _resizeHold = false;
    }

    /// <summary>
    /// 绝对兜底: 无条件放闸并撤罩。
    /// ★ 走到这里说明"新帧"和"状态回调"都没来 —— 再不放闸就是永久黑屏。
    ///   宁可多露一帧白, 也不能让用户对着黑屏等下去。
    /// </summary>
    private void OnMaximizeCoverSafety(object? sender, EventArgs e)
    {
        _maximizeCoverSafety?.Stop();
        if (_closing) return;
        _resizeHold = false;
        _maximizeCoverArmed = false;
        ReleaseFullscreenCover();
    }

    /// <summary>
    /// 尺寸"稳定"的判定窗口(ms)。
    /// ★ 取 260 而不是 100: 最大化的 DWM 过渡动画实测约 200ms, 期间尺寸一直在变;
    ///   窗口太短会在动画中途就放闸, 后半段的白又露出来(实测 160ms 时仍有 242 的白)。
    ///   "尺寸停了再放闸"的代价只有这一小段黑屏, 而它正好被过渡动画盖住, 用户看不到。
    /// </summary>
    private const int MaximizeSettleMs = 260;

    /// <summary>绝对兜底时长(ms)。见 <see cref="ArmMaximizeCover"/> 里那段说明。</summary>
    private const int MaximizeCoverSafetyMs = 1200;

    private DispatcherTimer? _maximizeSettle;
    private DispatcherTimer? _maximizeCoverSafety;

    /// <summary>
    /// 最大化/还原结束: 放闸让遮罩能撤, 并把新 vout 的原生底色再刷一次。
    ///
    /// ★ 顺序必须是"先放闸再刷": 遮罩要靠"新帧到达"撤掉(TimeChanged/Playing 都会调
    ///   <see cref="ReleaseFullscreenCover"/>), 而它会检查 `_resizeHold`。
    /// ★ 这里**不**直接揭幕: 尺寸刚变完的这几帧 vout 可能还在重协商, 立刻揭幕会露白。
    ///   交给"新帧到达"撤(最准), 计时器只兜底(暂停中/极短片没有新帧)。
    /// </summary>
    private void ReleaseMaximizeCover()
    {
        if (_closing) return;
        _maximizeCoverSafety?.Stop();
        _resizeHold = false;
        _maximizeCoverArmed = false;

        // 状态已经落定 ⇒ 现在的窗口尺寸就是最终尺寸, 此时改类背景刷不会被打断
        StartVideoBackdropFix();
    }

    /// <summary>
    /// 窗口状态落定(基类在 <c>OnStateChanged</c> 里调): 放掉最大化期间的黑罩 + 重刷 vout 底色。
    /// ★ 全屏切换自己有一套更严格的收尾(见 ApplyFullscreenLayout), 这里跳过它免得两套互抢。
    /// </summary>
    protected override void OnWindowStateSettled()
    {
        if (_closing) return;
        if (_isFullscreen || _suppressStateCover) return;   // 全屏那条路由 ApplyFullscreenLayout 自己收尾
        if (!_maximizeCoverArmed) return;

        _maximizeCoverArmed = false;
        ReleaseMaximizeCover();
    }

    /// <summary>
    /// 标记"接下来一段时间内, 窗口状态变化由调用方自己负责遮罩"。
    ///
    /// 见 <see cref="_suppressStateCover"/> 的说明 —— 进出全屏必须走这个,
    /// 否则两套遮罩叠加会把画面压黑近一秒。
    /// </summary>
    private void SuppressStateCoverFor(int ms)
    {
        _suppressStateCover = true;
        _suppressStateCoverTimer ??= new DispatcherTimer();
        _suppressStateCoverTimer.Tick -= OnSuppressStateCoverDone;
        _suppressStateCoverTimer.Tick += OnSuppressStateCoverDone;
        _suppressStateCoverTimer.Stop();
        _suppressStateCoverTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _suppressStateCoverTimer.Start();
    }

    private void OnSuppressStateCoverDone(object? sender, EventArgs e)
    {
        _suppressStateCoverTimer?.Stop();
        _suppressStateCover = false;
    }

    /// <summary>
    /// 上一次看到的窗口尺寸, 用来发现"尺寸变了但没收到 WM_SYSCOMMAND"的那条路
    /// (例如代码里直接赋 `WindowState`, 或系统动画/贴边布局替我们最大化)。
    /// 见 <see cref="OnLayoutSizeChanged"/>。
    /// </summary>
    private Size _lastMeasuredSize;

    /// <summary>
    /// 兜底: 客户区尺寸**真的变了**但没压过遮罩时补压一次。
    ///
    /// ★ 为什么需要兜底: `WM_SYSCOMMAND` 只覆盖"用户点按钮/双击标题栏/快捷键"那几条路;
    ///   而**代码里直接赋 `WindowState`**(ScMaximize 那套之外)以及系统的"贴边布局(Aero Snap)"
    ///   不一定会经过它。2026-10-17 探针实测: 直接 `win.WindowState = Maximized` 就不发
    ///   WM_SYSCOMMAND, 那条路原本完全没有遮罩。
    /// ★ 只在"从没压过"时补, 且**不设** `_maximizeCoverArmed`: 这次补压没有对应的
    ///   WM_SYSCOMMAND, 由计时器兜底撤罩即可(见 ReleaseMaximizeCover 的说明)。
    /// </summary>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        if (_closing || !IsLoaded) return;
        if (_isFullscreen || _suppressStateCover) return;
        if (sizeInfo.PreviousSize.Width < 1 || sizeInfo.PreviousSize.Height < 1) return;
        _lastMeasuredSize = sizeInfo.NewSize;

        // ★★ 每次尺寸变化都**重新压 + 重新计时**(2026-10-17):
        //   最大化/还原的过渡动画期间尺寸会连着变好几次, 每次 libvlc 都重协商一轮 vout。
        //   只压第一次的话, 后面每一轮又露白(实测: 只压第一次 → 还原后边带 242; 每次都压 → 0)。
        //   ⇒ 只要尺寸还在变, 就一直压着(不管幅度大小); 尺寸停了由 settle 计时器放闸。
        //   ★ 这里**不能**按"变化幅度"过滤: 还原动画的后半段变化幅度会变小,
        //     一旦据此放闸, 那一刻遮罩就撤了, 而 vout 还在重协商 ⇒ 白块又回来了(实测 242)。
        var dw = Math.Abs(sizeInfo.NewSize.Width - sizeInfo.PreviousSize.Width);
        var dh = Math.Abs(sizeInfo.NewSize.Height - sizeInfo.PreviousSize.Height);
        if (dw > 0.5 || dh > 0.5) ArmMaximizeCover();
    }

    /// <summary>最大化/还原的黑罩是否压着(见 <see cref="ArmMaximizeCover"/>)</summary>
    private bool _maximizeCoverArmed;

    /// <summary>
    /// 即将执行"最大化/还原/最小化"(WM_SYSCOMMAND) 时压黑视频区。
    ///
    /// ★ 为什么必须抢在**尺寸变化之前**: 白块出现在客户区尺寸剧变的那几十毫秒里;
    ///   等 `OnStateChanged` 送达时窗口已经重绘完, 那块白早就闪过去了。
    /// ★ 最小化也压上: 它同样会让 vout 重协商, 而恢复时露出来的白更显眼
    ///   (窗口整个重新可见)。恢复(SC_RESTORE)同理。
    /// </summary>
    protected override void OnSystemCommand(int command)
    {
        if (_closing) return;
        // 视频全屏期间那条路由 ApplyFullscreenLayout 自己管(它连着改多次窗口状态,
        // 这里再压一次会和它的收尾打架)。
        if (_isFullscreen || _suppressStateCover) return;

        if (command is SC_MAXIMIZE or SC_RESTORE or SC_MINIMIZE)
        {
            ArmMaximizeCover();
        }
    }

    /// <summary>
    /// 为真时**不要**为窗口状态变化压黑罩。
    ///
    /// ★★★ 为什么必须有它(2026-10-17 加, 修一处自己引入的回归):
    ///   "全屏"本身就是"最大化 + 收顶栏", 所以进出全屏**也会**触发
    ///   `WM_SYSCOMMAND`/尺寸剧变 —— 而全屏那条路已经有一套更严格的遮罩时序
    ///   (见 ApplyFullscreenLayout, 它自己 Arm/Release)。两边都压的后果实测是:
    ///   退全屏时这边又压了一层、且它要等 `_resizeHold` 的兜底超时才放
    ///   ⇒ 视频区在退全屏后**黑约 0.9 秒**(探针 bd-probe-fullscreen 立刻抓到)。
    ///   ⇒ 用这个标志把"全屏切换期间"整段排除掉。
    ///
    /// ★ 用**计时器**而不是"方法开头置真/结尾置假": 状态变化与尺寸变化是
    ///   `ApplyFullscreenLayout` 返回**之后**才由消息循环送达的, 结尾置假根本盖不住。
    /// </summary>
    private bool _suppressStateCover;

    private DispatcherTimer? _suppressStateCoverTimer;

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

    /// <summary>
    /// 查询真实的投币状态并刷新按钮点亮态。
    ///
    /// ★ 为什么必须单独查一次: 详情接口(view)**不给**"本人是否投过币"这个信息
    ///   (见 <see cref="ApiClient.GetCoinCountAsync"/> 里那段实测记录), 所以换片之后
    ///   `_isCoined` 不能继续取自详情 —— 那正是"投过币、重开视频后键还是灰的"的根因。
    /// ★ 与 <see cref="RefreshLikeStateAsync"/> 同构: 失败时保持原状(不把已点亮的刷成灰),
    ///   只记一笔便于排查。
    /// ★ 只在已登录且是点播时查: 本地文件/直播没有投币这回事, 查了也是白问一次接口。
    /// </summary>
    private async Task RefreshCoinStateAsync()
    {
        if (_aid <= 0 || !Svc.Session.HasLogin || _isLocalPlayback || _isLive) return;
        var (ok, _, coins) = await Svc.Api.GetCoinCountAsync(_aid);
        if (!ok) { WriteLibVLCDebug($"\n[Coined] 查询失败 aid={_aid}\n"); return; }
        _isCoined = coins > 0;
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
            // 弹一下(设计稿 v4: 点赞是全栏最高频的动作, 反馈比别的按钮重一档)
            PlayActionPop(LikeIconBox);
            // 取消点赞后锁住 hover 预览: 否则鼠标还停在按钮上, 它会当场又变红
            if (!_isLiked) LockActionHoverPreview();
            Svc.Toast.Show(_isLiked ? "点赞成功" : "已取消点赞");
        }
        else
        {
            // ★ 风控提示已经是一整句可照做的指引(见 ApiClient.FriendlyRiskMessage),
            //   再套一层"操作失败: "只会把话拉长、把重点埋掉。这里按前缀判断, 只在
            //   普通错误上加动作名。
            var m = err ?? "未知错误";
            Svc.Toast.Show(m.StartsWith("B 站风控", StringComparison.Ordinal)
                ? m : "操作失败: " + m);
        }
    }

    /// <summary>
    /// 投币按钮: 弹出数量选择对话框(形态照 B 站官方那个投币弹窗, 见 <see cref="CoinWindow"/>)。
    ///
    /// ★ 为什么要有"选数量"这一步: 投币**不可撤销**, 而单视频上限是 2 枚 —— 原来写死投 1 枚,
    ///   想投 2 枚的用户只能去官方端补。
    /// ★ 用模态窗而不是 Popup: 播放器视频区有单击暂停 / 双击全屏手势, Popup 的"点外部关闭"
    ///   会把那一次点击漏进视频区(与收藏夹选择窗同一个理由)。
    /// ★ 已投过就不再弹: 投币没有"取消"语义, 让用户进到对话框里再撞服务端的"超过投币上限"
    ///   只会白跑一趟。
    /// </summary>
    private void OnCoinClick(object sender, RoutedEventArgs e)
    {
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("投币需要先登录"); return; }
        if (string.IsNullOrEmpty(_currentBvid) || _aid <= 0) return;
        if (_isCoined) { Svc.Toast.Show("这个视频已经投过币了"); return; }

        var dlg = new CoinWindow { Owner = this };
        dlg.ShowDialog();
        if (!dlg.Confirmed) return;
        _ = CoinAsync(dlg.Count, dlg.AlsoLike);
    }

    /// <summary>
    /// 投币(count = 1 或 2)。成功后点亮按钮, 计数按实际枚数累加(不是固定 +1)。
    /// alsoLike 走接口的 select_like, 点赞由服务端在同一次请求里完成。
    /// </summary>
    private async Task CoinAsync(int count, bool alsoLike)
    {
        if (string.IsNullOrEmpty(_currentBvid) || _aid <= 0) return;
        // 已经赞过就不带 select_like: 没有必要, 也免得撞服务端"已经点赞"那条分支
        var wantLike = alsoLike && !_isLiked;
        var (ok, err) = await Svc.Api.CoinVideoAsync(_aid, count, _currentBvid, wantLike);
        if (ok)
        {
            // 同一个视频最多投 2 枚, 本地只记"已投过"的点亮态(下次进来以详情接口为准)
            _isCoined = true;
            _coinCount += count;
            // 赞是服务端顺带点的, 本地状态要跟着走 —— 否则按钮还显示"未赞"、计数也少 1
            if (wantLike) { _isLiked = true; _likeCount += 1; }
            UpdateActionCounts();
            PlayActionPop(CoinIconBox);
            Svc.Toast.Show(count > 1 ? "已投 2 枚币" : "投币成功");
        }
        else
        {
            // 同上: 风控提示自成一句, 不再套"投币失败: "前缀
            var m = err ?? "未知错误";
            Svc.Toast.Show(m.StartsWith("B 站风控", StringComparison.Ordinal)
                ? m : "投币失败: " + m);
        }
    }

    /// <summary>
    /// 操作栏的"图标 + 数量/文字"刷新。**唯一决定这六个按钮颜色与图标形态的地方。**
    ///
    /// 2026-10-06 按设计稿「播放页_v4」重构, 规则:
    ///   · **未激活一律是灰**(TextSecondaryBrush), 颜色只在用户动过之后才出现;
    ///   · 每个动作有**固定色相**, 不跟主题强调色走(红=情绪 / 金=货币 / 蓝=收进我的库 / 绿=拿走)。
    ///     用户把主题色改成绿时, "点赞"跟着变绿会立刻毁掉这套分工 —— 所以色值写死在这里。
    ///   · 数字与文字 TextBlock 的前景**必须显式绑**到按钮 Foreground: 全局隐式 TextBlock 样式
    ///     带 Foreground setter(优先级高于继承), 不绑的话变色的只有图标(项目里反复踩的坑)。
    ///   · 「稍后再看」的两层文字在这里一处切换 —— 散在触发器里改必然出现
    ///     "图标点亮了文字没换"这种半状态(2026-10-06 改回两行布局时恢复)。
    /// </summary>
    private void UpdateActionCounts()
    {
        if (_closing) return;
        LikeLabel.Text = VideoItem.FormatCount(_likeCount);
        CoinLabel.Text = VideoItem.FormatCount(_coinCount);
        FavLabel.Text = VideoItem.FormatCount(_favCount);
        ApplyActionVisual(BtnLike, _isLiked, ActionLikeColor);
        ApplyActionVisual(BtnFavorite, _isFavorited, ActionSaveColor);
        ApplyActionVisual(BtnWatchLater, _isInWatchLater, ActionSaveColor);
        // 「稍后再看」的两层文字随点亮态互换(稍后再看 ↔ 已添加)。
        // ★ 宽度由 XAML 里的双层叠放兜住(两层叠在同一格, 宽度取最大值), 切换只做透明度过渡,
        //   所以整排按钮不会被推着走 —— 两行布局下这套是必需的(2026-10-06 改回两行时恢复)。
        if (LaterLabelOff != null)
        {
            LaterLabelOff.Opacity = _isInWatchLater ? 0 : 1;
            LaterLabelOn.Opacity = _isInWatchLater ? 1 : 0;
        }
        // 未点过的三个按钮也要走一次: 换片后要把上一次残留的色相清回灰。
        // ★ 下载的绿色终态由 _cacheDownloaded 决定(它只在换片时复位, 见 ResetForNewMedia)——
        //   不能在这里无条件刷成灰: 本方法会被点赞/收藏等**同一片内**的操作反复调用,
        //   那样用户一下载完、随手点个赞, 绿色就没了。
        ApplyActionVisual(BtnCoin, _isCoined, ActionCoinColor);
        ApplyActionVisual(BtnShare, false, ActionNeutralColor);
        ApplyActionVisual(BtnCache, _cacheDownloaded, ActionDownloadColor);
    }

    /// <summary>当前这条视频是否已下载完成(只用于下载按钮的绿色终态)</summary>
    private bool _cacheDownloaded;

    // ---- 操作栏色相(设计稿 v4: 固定色, 不跟主题强调色) ----

    /// <summary>点赞红(情绪)</summary>
    private static readonly Color ActionLikeColor = Color.FromRgb(0xF4, 0x3F, 0x5E);
    /// <summary>投币琥珀(货币)</summary>
    private static readonly Color ActionCoinColor = Color.FromRgb(0xF5, 0x9E, 0x0B);
    /// <summary>收藏 / 稍后再看蓝(收进我的库, 两者同族同色)</summary>
    private static readonly Color ActionSaveColor = Color.FromRgb(0x0E, 0xA5, 0xE9);
    /// <summary>下载绿(拿走)。只在"已下载"这一个终态出现, 不像其它色相有"未点亮/点亮"两态</summary>
    private static readonly Color ActionDownloadColor = Color.FromRgb(0x10, 0xB9, 0x81);
    /// <summary>中性(分享 / 下载): 走主题的次要文字色, 不做色相区分</summary>
    private static readonly Color? ActionNeutralColor = null;

    /// <summary>
    /// 把一个操作按钮刷成"点亮(用该动作的色相)"或"未点亮(灰)"。
    ///
    /// ★ 这里**直接操作控件**而不是靠 Style 触发器: hover 预览(.v4)需要在鼠标悬停时
    ///   临时借用色相, 而"取消点赞后鼠标还悬停着 → 按钮当场又变红"这个坑(v4 稿第 04 节)
    ///   要求"取消后短暂锁住 hover"。这套时序逃不出代码, 所以统一放在这里。
    /// </summary>
    private void ApplyActionVisual(Button? btn, bool on, Color? accent)
    {
        if (btn == null) return;
        btn.Foreground = on && accent.HasValue
            ? new SolidColorBrush(accent.Value)
            : (Brush)FindResource("TextSecondaryBrush");
        // Tag 驱动模板里的常驻浅底(见 PlayerActionBtn 的 DataTrigger)
        btn.Tag = on ? "on" : "off";
        if (!on) return;
        // 冻结笔刷: 这几个色相是常量, 冻结后 WPF 渲染时可以少走一次可变检查
        if (btn.Foreground is SolidColorBrush scb && scb.CanFreeze) scb.Freeze();
    }

    /// <summary>该动作的色相(按名字取, 点亮/hover 预览共用一套来源)</summary>
    private Color? ActionAccentOf(Button btn)
    {
        if (ReferenceEquals(btn, BtnLike)) return ActionLikeColor;
        if (ReferenceEquals(btn, BtnCoin)) return ActionCoinColor;
        if (ReferenceEquals(btn, BtnFavorite) || ReferenceEquals(btn, BtnWatchLater)) return ActionSaveColor;
        // 下载: 只有"已下载"这一个终态是绿的, 没有"未点亮→点亮"的来回切换
        if (ReferenceEquals(btn, BtnCache)) return ActionDownloadColor;
        return ActionNeutralColor;
    }

    /// <summary>
    /// 这个按钮有没有"hover 预览"。
    /// ★ 下载(和分享)没有: 下载的结果是**一次性**的, 鼠标扫过去就预先把按钮染绿等于
    ///   谎报"已经下载好了"。设计稿 v4 里 .act.dl 也刻意没有 hover 预览。
    /// </summary>
    private bool HasHoverPreview(Button btn)
        => !ReferenceEquals(btn, BtnCache) && !ReferenceEquals(btn, BtnShare);

    /// <summary>该按钮当前是不是"已点亮"态(取消后要用它判断该不该回灰)</summary>
    private bool IsActionOn(Button btn)
    {
        if (ReferenceEquals(btn, BtnLike)) return _isLiked;
        if (ReferenceEquals(btn, BtnFavorite)) return _isFavorited;
        if (ReferenceEquals(btn, BtnWatchLater)) return _isInWatchLater;
        if (ReferenceEquals(btn, BtnCoin)) return _isCoined;
        // 下载的"已下载"也是终态, 有它就说明不该回灰
        if (ReferenceEquals(btn, BtnCache)) return _cacheDownloaded;
        return false;
    }

    /// <summary>
    /// 操作按钮的 hover 预览(设计稿 v4 第 03 节): 鼠标上来先把该动作的色相透出来,
    /// 让用户"不用点就知道点了会怎样"; 底色由模板的 DataTrigger 负责(同色相浅底)。
    ///
    /// ★ 已在点亮态的按钮不需要预览 —— 它本来就是那个颜色, 变过去等于没变。
    /// ★★ 取消后 750ms 内不预览(hover 锁, 见 _actionHoverLockUntil): 取消的那一刻鼠标
    ///    必然还停在这个按钮上, 不给锁的话按钮会当场又变回红色, 用户以为自己没取消成功,
    ///    于是再点一次(这就是设计稿里 v3 的那个隐藏 bug)。
    /// </summary>
    private void OnActionButtonHover(object sender, MouseEventArgs e)
    {
        if (_closing || sender is not Button btn) return;
        if (DateTime.UtcNow < _actionHoverLockUntil) return;
        if (!HasHoverPreview(btn)) return;
        var accent = ActionAccentOf(btn);
        if (accent.HasValue && !IsActionOn(btn))
            btn.Foreground = new SolidColorBrush(accent.Value);
    }

    /// <summary>鼠标离开: 回到该按钮的"真实态"颜色(点亮保持色相, 未点亮回灰)</summary>
    private void OnActionButtonLeave(object sender, MouseEventArgs e)
    {
        if (_closing || sender is not Button btn) return;
        ApplyActionVisual(btn, IsActionOn(btn), ActionAccentOf(btn));
    }

    /// <summary>取消操作后锁住 hover 预览到这个时刻(UTC)。750ms 与设计稿一致。</summary>
    private DateTime _actionHoverLockUntil = DateTime.MinValue;

    /// <summary>锁住 hover 预览 750ms(取消点赞/收藏/稍后再看之后调)</summary>
    private void LockActionHoverPreview() => _actionHoverLockUntil = DateTime.UtcNow.AddMilliseconds(750);

    /// <summary>
    /// 给一个操作按钮播"弹一下"的反馈(设计稿 v4 第 03 节②)。
    /// 作用在图标盒子上而不是内部 Path: Path 自身没有 ScaleTransform, 直接缩放会和
    /// 别的动画/布局打架; 图标盒子是纯容器, 缩放它最干净。
    ///
    /// ★★★ 每次都必须**新建** ScaleTransform, 不能复用现有的那一个。
    ///   原因见 XAML 里 ActionIconBox 的注释: 样式 Setter 里的 Freezable 会被 WPF **冻结**,
    ///   对冻结对象 BeginAnimation 会直接抛
    ///     "无法在 System.Windows.Media.ScaleTransform 上激活 ScaleX 属性, 因为该对象已密封或已冻结"
    ///   —— 用户看到的就是"点一下点赞就弹错误框"(2026-10-06 实测踩到)。
    ///   新建一个 ScaleTransform 的开销可以忽略(一次点击一次), 而它从根上避开了冻不冻的问题。
    /// </summary>
    private void PlayActionPop(Grid? iconBox)
    {
        if (iconBox == null || _closing) return;
        var scale = new ScaleTransform(1, 1);
        iconBox.RenderTransform = scale;
        // 关键帧: 1 → 1.3 → 1(与设计稿的 40% 处 1.3 一致), 总时长 420ms
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromPercent(0)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.3, KeyTime.FromPercent(0.4))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    // ------------------------------------------------------------ 「听视频」纯音频模式

    /// <summary>
    /// 是否处于「听视频」纯音频模式。
    /// 登记在 <see cref="PersistentFields"/>: 它是**窗口级的播放模式**(同 _isFillWindow),
    /// 用户切到纯音频连听几集是常态, 换片不该把它弹回视频。
    /// </summary>
    private bool _isListenMode;

    /// <summary>
    /// 「听视频」用的 DASH 音轨地址(与 _lastAudioUrl 分开存)。
    ///
    /// ★ 为什么不能复用 _lastAudioUrl: 那个字段的语义是"当前 Media 的 input-slave",
    ///   退出聆听态要用它重建**原视频组合**。聆听态期间它会被改成音轨单流,
    ///   退出时就再也拼不回视频了。
    /// </summary>
    private string? _listenAudioUrl;

    /// <summary>进入聆听态前记下的进度(切流后恢复, 与切清晰度同一套机制)</summary>
    private long _listenResumeMs;

    /// <summary>声波律动定时器(顶栏小 5 根 + 画面区大 7 根共用这一个)</summary>
    private DispatcherTimer? _listenWaveTimer;
    private int _listenWaveTick;

    /// <summary>声波每根条的相位延迟(设计稿 .11s 一档), 单位是 tick</summary>
    private static readonly double[] ListenWavePhase = { 0, 0.11, 0.22, 0.33, 0.44, 0.55, 0.66 };

    /// <summary>
    /// 「听视频」入口的可用性。
    /// 直播是连续流(没有"音轨地址"这种可单取的东西), 本地文件也没有独立音轨 ——
    /// 点了必然失败, 所以整颗按钮收起来而不是让它点了报错(设计稿第 05 节"无音轨就别给")。
    /// </summary>
    private void UpdateListenButtonState()
    {
        if (BtnListen == null) return;
        var usable = !_isLive && !_isLocalPlayback;
        BtnListen.Visibility = usable ? Visibility.Visible : Visibility.Collapsed;
        // 切到不支持的模式(换成直播/本地文件)时必须把聆听态收掉 —— 否则聆听遮罩会盖在
        // 一个根本没有独立音轨的片源上(用户看到"正在聆听", 实际播的是视频)。
        // ★ 这里只做**状态与视觉**的收尾, 绝不去重建 Media: 本方法是在 ResetForNewMedia
        //   里被调的, 那时 _lastPlayUrl 还是**上一条视频**的地址 —— 让 ExitListenMode 跑
        //   完整流程会用旧地址重新起播一次, 而调用方紧接着就要播新片源了。
        if (!usable && _isListenMode)
        {
            _isListenMode = false;
            ApplyListenMode(false);
        }
    }

    private void OnListenToggleClick(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        if (_isListenMode) ExitListenMode();
        else EnterListenMode();
    }

    /// <summary>
    /// 进入「听视频」: 切到 DASH 音轨单流播放, 画面区盖上聆听视觉。
    ///
    /// ★ 为什么真的换流(而不是只给 Media 加 :no-video):
    ///   加 :no-video 只是让 LibVLC 不解码画面, **视频流照样在下载** —— 该省的流量一点没省,
    ///   而"听视频"这个功能的全部价值恰恰是省流量(以及省电/让笔记本安静)。
    ///   所以我们改播 playurl 里那条音频地址(input-slave 用的同一条), 视频流一个字节都不拉。
    ///
    /// ★ 起播必然要重建 Media, 所以进度必须自己接上: 走 _resumeAfterSwitchMs 那套
    ///   (Playing 事件里恢复), 与切清晰度完全同一条路 —— 别在这里再写一份恢复逻辑。
    /// </summary>
    private void EnterListenMode()
    {
        if (_closing || _isListenMode) return;
        if (_isLive || _isLocalPlayback) return;
        if (string.IsNullOrEmpty(_listenAudioUrl))
        {
            Svc.Toast.Show("这条视频没有可用的音轨");
            return;
        }

        _isListenMode = true;
        // 先在暂停/播到一半时也能接上: 用当前播放位置(拿不到就从 0 开始)
        try { _listenResumeMs = _mp.Time; } catch { _listenResumeMs = 0; }
        if (_listenResumeMs < 0) _listenResumeMs = 0;

        ApplyListenMode(true);
        // 音轨是单流: audioUrl 传 null, 否则等于给音频文件再挂一条音频
        ShowLoading("正在切换到听视频…");
        _resumeAfterSwitchMs = _listenResumeMs;
        try { _mp.Stop(); } catch { /* 已停/未起播 */ }
        PlayMedia(BuildMedia(_listenAudioUrl!, null));
    }

    /// <summary>
    /// 退出「听视频」: 用原来的视频 + 音轨组合重建 Media(进度同样接回来), 收掉聆听视觉。
    ///
    /// ★ 退出时**不能**直接 _mp.Stop() 就完事: 那样视频就再也不播了。
    ///   必须用 _lastPlayUrl/_lastAudioUrl 把"进入之前那条组合"原样拼回去 ——
    ///   这正是这两个字段必须原封不动保留的原因(见 _listenAudioUrl 的说明)。
    /// </summary>
    private void ExitListenMode()
    {
        if (_closing || !_isListenMode) return;
        _isListenMode = false;

        try { _listenResumeMs = _mp.Time; } catch { _listenResumeMs = 0; }
        if (_listenResumeMs < 0) _listenResumeMs = 0;

        ApplyListenMode(false);

        // 直播/本地最后走到了这里也没关系: _lastPlayUrl 就是它们的源, 原样重播
        var url = _lastPlayUrl;
        if (string.IsNullOrEmpty(url))
        {
            // 没有可回溯的源: 只能停下(比播着一个不知道是什么的流强)
            try { _mp.Stop(); } catch { }
            HideLoading();
            return;
        }
        ShowLoading("正在恢复视频…");
        _resumeAfterSwitchMs = _listenResumeMs;
        try { _mp.Stop(); } catch { /* 已停 */ }
        // 本地文件是单文件源, 不能带 input-slave
        PlayMedia(BuildMedia(url, _isLocalPlayback ? null : _lastAudioUrl));
    }

    /// <summary>
    /// 聆听态的视觉开关(**唯一改 ListenOverlay / 顶栏按钮形态的地方**)。
    /// 收在一个方法里: 顶栏按钮有 4 个部件(耳机/声波/文案/底色)要跟着状态换,
    /// 散着写必然出现"按钮点亮了但遮罩没出来"这种半状态。
    /// </summary>
    private void ApplyListenMode(bool on)
    {
        if (ListenOverlay != null)
            ListenOverlay.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (BtnListen != null)
            BtnListen.Tag = on ? "on" : "off";   // 驱动 ListenGhostBtn 的琥珀 DataTrigger
        if (ListenHeadIcon != null)
            ListenHeadIcon.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        // ★ 胶囊里原先那 5 根小波条已删除(2026-10-06 用户要求"胶囊里的音频波纹去掉,
        //   只保留视频播放器的"), 所以这里不再有 ListenWavePanel 要切显隐。
        //   聆听态画面里那组大波纹(BigWave1~7)照旧由 _listenWaveTimer 驱动, 不受影响。
        // ★ 2026-10-07: 文案改成**一个 TextBlock**整体写入(见 XAML 的说明)——
        //   原来这里是两个 TextBlock 叠放 + 切 Opacity, 那套既让待命态的文字被挤在一侧,
        //   又会在切换瞬间改变胶囊宽度、把顶栏右对齐的按钮推着走。现在只改一个 Text 的值。
        if (ListenLabelText != null)
        {
            ListenLabelText.Text = on ? FormatListenLabel() : "听视频";
            // 状态提示移进 ToolTip: 胶囊里只有"正在聆听 m:ss", 不再重复写一遍"正在聆听"
        }
        if (BtnListen != null)
            BtnListen.ToolTip = on ? "退出听视频" : "听视频 (只播声音, 省流量)";

        if (on)
        {
            // 封面 + 标题: 让"在听什么"一眼可读
            if (ListenOverlayTitle != null) ListenOverlayTitle.Text = _videoTitle;
            LoadListenCover(_coverUrl);
            StartListenWave();
        }
        else
        {
            StopListenWave();
        }
    }

    /// <summary>
    /// 聆听中的完整文案: 「正在聆听 m:ss」。秒数取自播放位置, 每秒由 UpdateListenLabel 刷。
    ///
    /// ★ 2026-10-07: 抽成FormatListenLabel(), 因为胶囊现在只有**一个** TextBlock,
    ///   初次进入与每秒刷新都必须调同一个函数, 否则两处格式会走偏。
    /// </summary>
    private string FormatListenLabel()
    {
        long ms = 0;
        try { ms = _mp.Time; } catch { /* 播放器未就绪 */ }
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        // ★ 分钟**补两位**(设计稿写的是 "0:14", 但那样字串长度会在 10 分钟那一刻从 4 位变 5 位,
        //   而按钮在顶栏右对齐的 StackPanel 里 —— 宽度一变右侧那排窗口按钮就整体挪一下,
        //   与「稍后再看」那条"整排被推走"是同一类问题。补零后小时以内长度恒定。
        //跨过 1 小时仍会多一段(视频普遍不止一小时, 不再为它牺牲可读性)。
        return t.TotalHours >= 1
            ? $"正在聆听 {(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"正在聆听 {(int)t.TotalMinutes:D2}:{t.Seconds:D2}";
    }

    private void UpdateListenLabel()
    {
        if (ListenLabelText == null || !_isListenMode) return;
        ListenLabelText.Text = FormatListenLabel();
    }

    /// <summary>封面图: 异步取一次(解码宽度按显示尺寸 260 的两倍, 够清晰又不浪费内存)</summary>
    private void LoadListenCover(string? url)
    {
        if (string.IsNullOrEmpty(url) || ListenCover == null) return;
        _ = LoadListenCoverAsync(url);
    }

    private async Task LoadListenCoverAsync(string url)
    {
        var img = await CoverLoader.LoadAsync(url, 520);
        if (img == null || _closing) return;
        await Dispatcher.InvokeAsync(() =>
        {
            if (_closing || ListenCover == null) return;
            ListenCover.Source = img;
        });
    }

    /// <summary>
    /// 启动声波律动。★ 用 DispatcherTimer 而不是 XAML Storyboard:
    ///   ① 七根条各有各的相位与高度, Storyboard 要写十几组 BeginStoryboard;
    ///   ② 更重要的是**退出聆听必须能一处停掉** —— Storyboard 挂在模板上时,
    ///      遮罩收起了动画还在跑, 白烧 CPU(项目里"关掉还在跑"的坑不止一个)。
    /// 33ms ≈ 30fps: 声波是随机的律动, 不需要 60fps, 省一半 CPU。
    /// ★ 现在只驱动聆听态画面里的 7 根大波纹(胶囊里那 5 根小波条已删除)。
    /// </summary>
    private void StartListenWave()
    {
        _listenWaveTick = 0;
        if (_listenWaveTimer == null)
        {
            _listenWaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _listenWaveTimer.Tick += (_, _) =>
            {
                if (_closing || !_isListenMode) { StopListenWave(); return; }
                _listenWaveTick++;
                UpdateListenWave();
                // 文案里的秒数也挂在这个心跳上: 另开一个 1 秒定时器纯属多余
                if (_listenWaveTick % 30 == 0) UpdateListenLabel();
            };
        }
        _listenWaveTimer.Start();
        UpdateListenWave();
    }

    private void StopListenWave() => _listenWaveTimer?.Stop();

    /// <summary>
    /// 逐帧算声波高度。每根条 = 相位偏移的正弦 + 一点点随机抖动,
    /// 比纯随机更像"随声音起伏"(纯随机会跳得没有节奏感)。
    ///
    /// ★ 2026-10-06: 胶囊里那 5 根小波条已按用户要求删除, 现在只驱动**聆听态画面**里的
    ///   大波纹(BigWave1~7)。胶囊待命/聆听都只显示耳机图标, 状态靠琥珀底色与文案区分。
    /// </summary>
    private void UpdateListenWave()
    {
        // tick 到相位: 0.82s 一个周期 ≈ 25 tick(33ms 一 tick)
        const double periodTicks = 25.0;
        var t = _listenWaveTick;

        var big = new[] { BigWave1, BigWave2, BigWave3, BigWave4, BigWave5, BigWave6, BigWave7 };
        for (var i = 0; i < big.Length; i++)
            SetWaveHeight(big[i], t, periodTicks, ListenWavePhase[i], min: 10, max: 32);
    }

    /// <summary>按相位算一根声波条的高度(0.5 + 0.5*sin 映射到 [min,max])</summary>
    private static void SetWaveHeight(Border? bar, int tick, double periodTicks, double phaseTicks,
        double min, double max)
    {
        if (bar == null) return;
        var phase = (tick + phaseTicks * periodTicks) / periodTicks * Math.PI * 2;
        var k = 0.5 + 0.5 * Math.Sin(phase);
        bar.Height = min + (max - min) * k;
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
            PlayActionPop(LaterIconBox);
            if (!target) LockActionHoverPreview();   // 移出后锁 hover 预览(同点赞的坑)
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
            PlayActionPop(FavIconBox);
            // 取消收藏后同样要锁 hover 预览(与取消点赞同一个坑)
            if (final.Count == 0) LockActionHoverPreview();
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

    /// <summary>
    /// 评论区排序: 3 = 热门, 2 = 时间(与 B 站接口的 mode 同值)。
    /// ★ 初值 3 与接口默认一致 —— 用户第一次进评论区看到的顺序不能因为这次改动而变。
    /// </summary>
    private int _commentSort = 3;

    /// <summary>当前视频的根评论总数(来自接口 cursor.all_count, 不是本页条数)</summary>
    private long _commentTotal;

    private async Task LoadCommentsAsync(bool force = false)
    {
        if (_closing) return;
        // force: 刚发表完评论要重新拉一次(rpid/楼层/审核状态本地编不出来)。
        // 但"看过就不再拉"这个短路必须保留 —— 切 Tab 会反复调到这里, 不短路就是白刷接口。
        // ★ 切换排序必须走 force=true: 它要的就是"同一份数据换个顺序重拉"。
        if (force) CloseCommentDetail();
        if (_commentsLoaded && !force) { CommentHint.Text = "已加载"; return; }
        if (_aid <= 0)
        {
            CommentHint.Text = _isLocalPlayback ? "本地文件没有评论" : "未获取到视频 aid";
            UpdateCommentSortUi(visible: false);
            return;
        }
        CommentLoadingRing.Visibility = Visibility.Visible;
        CommentHint.Text = force ? "正在刷新评论…" : "加载评论中…";
        // 重拉期间先把两个词收起来: 否则"正在刷新评论…"右边还挂着可点的排序词, 会出现
        // "点一下又刷新一次"的连点窗口期。
        UpdateCommentSortUi(visible: false);
        try
        {
            var (cmtOk, cmtErr, list, total) = await Svc.Api.GetCommentsAsync(_aid, 30, _commentSort);
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closing) return;
                CommentList.ItemsSource = list;
                _commentsLoaded = true;
                _commentTotal = total;
                CommentLoadingRing.Visibility = Visibility.Collapsed;
                // ★ 原来"接口失败"和"这视频真的一条评论都没有"都显示"暂无评论" ——
                //   网络失败被伪装成了正常状态。现在失败时说清原因。
                // ★ 条数用接口给的**总数**(_commentTotal), 不再拿 list.Count —— 一页只拉 30 条,
                //   拿它当总数的话几千条评论的视频会显示成"共 30 条评论"。总数缺失(0)时才回落到本页条数。
                CommentHint.Text = !cmtOk ? (cmtErr ?? "评论加载失败")
                    : list.Count == 0 ? "暂无评论"
                    : $"共 {VideoItem.FormatCount(total > 0 ? total : list.Count)} 条评论";
                // 只有真的加载成功且有评论, 排序入口才出现
                UpdateCommentSortUi(visible: cmtOk && list.Count > 0);
            });
        }
        catch
        {
            if (_closing) return;
            CommentLoadingRing.Visibility = Visibility.Collapsed;
            CommentHint.Text = "评论加载失败";
            UpdateCommentSortUi(visible: false);
        }
    }

    /// <summary>
    /// 同步排序入口的可见性与选中态。
    ///
    /// ★ 选中态只在这里写 Tag(与 ContentActionTabStyle 同一套约定: 字符串 "on"/"off")。
    ///   分散着改两个按钮的 Tag 必然出现"两边都亮"或"两边都不亮"。
    /// </summary>
    private void UpdateCommentSortUi(bool visible)
    {
        var vis = visible ? Visibility.Visible : Visibility.Collapsed;
        if (BtnCommentSortHot != null)
        {
            BtnCommentSortHot.Visibility = vis;
            BtnCommentSortHot.Tag = _commentSort == 3 ? "on" : "off";
        }
        if (BtnCommentSortTime != null)
        {
            BtnCommentSortTime.Visibility = vis;
            BtnCommentSortTime.Tag = _commentSort == 2 ? "on" : "off";
        }
    }

    private void OnCommentSortHotClick(object sender, RoutedEventArgs e) => SetCommentSort(3);

    private void OnCommentSortTimeClick(object sender, RoutedEventArgs e) => SetCommentSort(2);

    /// <summary>
    /// 切换评论排序。已经是当前排序就直接返回 —— 否则点一下会白刷一次接口,
    /// 而重拉期间入口是收起的, 用户看到的是"点了闪一下什么都没有"。
    /// </summary>
    private void SetCommentSort(int mode)
    {
        if (_closing || _commentSort == mode) return;
        _commentSort = mode;
        UpdateCommentSortUi(visible: true);
        _ = LoadCommentsAsync(force: true);
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
        // ★ 记下"刚才在看哪一条", 供返回后滚回去用(见 _commentReturnRpid)。
        //   必须在清空 _detailThreadRoot **之前**取, 否则信息就没了。
        _commentReturnRpid = _detailThreadRoot?.Rpid ?? 0;
        CommentDetailPanel.Visibility = Visibility.Collapsed;
        CommentDetailCard.Content = null;
        CommentDetailHost.DataContext = null;
        _detailThreadRoot = _detailFocus = null;
        ClearReplyTarget();
        // tab 文案与返回链接一起还原
        TabComments.Content = "评论";
        BtnBackToComments.Visibility = Visibility.Collapsed;
        CommentList.Visibility = Visibility.Visible;
        // ★ 用户报"从某条评论的回复返回后, 列表回到顶部, 得重新下拉找那条评论"
        //   (2026-10-13): 默认回到**刚才那条评论**所在的位置。
        //   ★ 走 Dispatcher 排队: 上面刚把 CommentList 从 Collapsed 恢复成 Visible,
        //     此刻容器还没生成(ContainerFromItem 会返回 null), 直接找必然落空。
        if (_commentReturnRpid > 0)
            Dispatcher.BeginInvoke(new Action(ScrollCommentIntoView),
                System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 从评论详情返回列表后要滚回去的那条评论(rpid); 0 = 不需要滚动。
    ///
    /// ★ 为什么单独存 rpid 而不是留着 CommentItem 引用: 重新拉列表(换排序/发表后刷新)时
    ///   会换成一批**新的** CommentItem 实例, 引用会失效; rpid 是服务端主键, 跨刷新仍然对得上。
    /// </summary>
    private long _commentReturnRpid;

    /// <summary>
    /// 把 <see cref="_commentReturnRpid"/> 对应的那条评论滚进视野并闪一下。
    ///
    /// ★ 找不到就**静默放弃**: 排序换过、或那条已被删除时它确实不在列表里,
    ///   这时弹提示反而打扰用户 —— 回到顶部是合理退路。
    /// ★ 闪一下是必要的: 光滚过去, 用户仍不知道"是哪一条"(与"查看对话"同一个理由)。
    /// </summary>
    private void ScrollCommentIntoView()
    {
        var rpid = _commentReturnRpid;
        _commentReturnRpid = 0;      // 一次性: 用完即清, 免得下次切 tab 又滚一次
        if (rpid <= 0 || _closing || CommentList == null) return;

        var target = (CommentList.ItemsSource as IEnumerable<CommentItem>)
            ?.FirstOrDefault(c => c.Rpid == rpid);
        if (target == null) return;

        if (CommentList.ItemContainerGenerator.ContainerFromItem(target) is not FrameworkElement fe) return;
        fe.BringIntoView();
        // 闪两下: 告诉用户"就是这一条"
        // ★ FillBehavior=Stop 是必须的: 默认 Hold 会让动画把 Opacity 停在末值(0.35),
        //   用户看到的就是"这条评论变透明了, 而且再也回不来"—— 因为它从此被动画接管,
        //   连可见性切换都拉不回 1.0。Stop 表示动画结束后属性**回到本地值**(1)。
        var flash = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(140))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(2),
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
        };
        fe.BeginAnimation(OpacityProperty, flash);
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

    // ------------------------------------------------------------ 视频分P

    /// <summary>
    /// 当前视频的全部分P(单P 视频也会有一项)。分P 弹层的唯一数据源。
    ///
    /// ★ 与合集严格分开存: 两者能同时存在(一个分P 稿件被收进合集), 混在一份列表里
    ///   用户根本分不清"点这条是换稿件还是换段落"。
    /// </summary>
    private List<VideoPage> _pages = new();

    /// <summary>分P 够不够两条(够才值得给入口)。单P 视频的 pages 里也只有 1 项</summary>
    private bool _hasPages;

    /// <summary>
    /// "正在换 P"的闸门, 与 <see cref="_switchingSeasonEpisode"/> 同款理由:
    /// 连点两个 P 会叠起两个 LoadVideoAsync, 后回来的覆盖先回来的。
    /// </summary>
    private bool _switchingPage;

    /// <summary>
    /// 详情加载完之后灌分P 数据。
    ///
    /// ★ 入口的显隐只认"有没有多于一个 P": 单P 视频给一个「分P」按钮纯属噪音
    ///   (对照: B 站自己在单P 视频上也不给选集)。
    /// </summary>
    private void ApplyPages(List<VideoPage> pages)
    {
        if (_closing) return;

        _pages = pages ?? new List<VideoPage>();
        _hasPages = _pages.Count > 1;

        if (BtnPages == null) return;
        BtnPages.Visibility = _hasPages ? Visibility.Visible : Visibility.Collapsed;
        if (!_hasPages) ClosePagesPopup();
        else UpdatePagesTitle();
    }

    /// <summary>关掉分P 弹层(换片 / 单P 视频时调用)。没开就别动, 免得白白改一次 IsOpen</summary>
    private void ClosePagesPopup()
    {
        if (PagesPopup == null || !PagesPopup.IsOpen) return;
        PagesPopup.IsOpen = false;
    }

    /// <summary>
    /// 按钮上显示 "分P 12/40" —— 把"现在在第几段 / 一共几段"直接写在入口上。
    /// 40 段的纪录片只靠弹层里的高亮, 用户永远不知道自己在哪一段。
    /// </summary>
    private void UpdatePagesTitle()
    {
        if (BtnPages == null || BtnPagesText == null) return;
        var cur = CurrentPageIndex();
        BtnPagesText.Text = cur > 0
            ? $"分P {cur}/{_pages.Count}"
            : $"分P ({_pages.Count})";
    }

    /// <summary>当前正在播的是第几个 P(1 起; 定位不上返回 0)</summary>
    private int CurrentPageIndex()
    {
        if (_currentCid <= 0) return 0;
        foreach (var p in _pages)
            if (p.Cid == _currentCid) return p.Page;
        return 0;
    }

    /// <summary>点「分P」: 开/收弹层。不请求接口 —— pages 跟合集一样在详情里就拿到了</summary>
    private void OnPagesToggleClick(object sender, RoutedEventArgs e)
    {
        if (_closing || !_hasPages || PagesPopup == null) return;
        var open = PagesPopup.IsOpen;
        PagesPopup.IsOpen = !open;
        BtnPages.Tag = !open ? "on" : "off";
        if (!open)
        {
            if (PagesList != null)
            {
                PagesList.ItemsSource = _pages;
                PagesList.SelectedItem = _pages.FirstOrDefault(p => p.Cid == _currentCid);
            }
            PagesMetaText.Text = $"共 {_pages.Count} 个分P";
        }
    }

    /// <summary>弹层外点/按 Esc 关掉弹层时, 把按钮的高亮态一并收回</summary>
    private void OnPagesPopupClosed(object sender, EventArgs e)
    {
        if (BtnPages != null) BtnPages.Tag = "off";
    }

    /// <summary>
    /// 选中某个分P → 换段播放。
    ///
    /// ★ 判据是 cid 不是 bvid: 分P 之间 **bvid 完全相同**, 用 bvid 判会永远短路成"没点"。
    ///   (这正是合集那条判据 `ep.Bvid != _currentBvid` 在分P 上不成立的原因。)
    /// </summary>
    private void OnPageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_closing || PagesList == null) return;
        if (PagesList.SelectedItem is not VideoPage p) return;
        if (p.Cid == _currentCid) return;
        PagesPopup?.SetCurrentValue(System.Windows.Controls.Primitives.Popup.IsOpenProperty, false);
        _ = SwitchToPageAsync(p);
    }

    private async Task SwitchToPageAsync(VideoPage p)
    {
        if (_closing || p.Cid <= 0) return;
        if (_switchingPage) return;
        _switchingPage = true;
        try
        {
            try { _mp.Stop(); } catch { }
            // ★ _currentBvid 一个字都不改: 分P 是同一个稿件的分段。改 bvid 会让
            //   弹幕游标 / 历史进度 / 三连状态全部挂到别的稿件上。
            //   ResetForNewMedia 照旧要调: cid 变了就是另一段内容, 进度、弹幕游标、
            //   视频总长、评论都得重来(它内部会把 _indexInLib 归零)。
            _isLocalPlayback = false;
            _isLive = false;
            _liveRoomId = 0;
            ResetForNewMedia();
            await LoadVideoAsync(p.Cid);
        }
        finally
        {
            _switchingPage = false;
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
    /// 把 HwndHost 那个原生宿主窗口的**类背景刷**改成黑色 —— 修"视频没填满时是一片白"。
    ///
    /// ★★★ 真根因(2026-10-17 探针 bd-probe-letterbox 实测钉死, 别再猜):
    ///   `VideoView` 是 <c>HwndHost</c>, 它下面由 libvlc 建了一串**同进程**的子窗口:
    ///   <code>
    ///     HwndWrapper[...]   ← HwndHost 宿主窗口(我们这侧建的)
    ///       Static           ← 铺满整个视频区(1000x945)   ★白边的作者
    ///       VLC video main   ← 铺满整个视频区
    ///       VLC video output ← 只盖住画面那一块(1000x562)
    ///   </code>
    ///   视频宽高比与视频区不一致时, `video output` 只占中间一条, 上下(或左右)剩下的区域
    ///   没有任何窗口画东西 ⇒ 露出 `Static` 的**窗口类默认背景(白色)**。
    ///   逐层隐藏实测: 藏 `Static` → 边带亮度 240→0; 藏另两个 → 不变。**作者就是 Static。**
    ///
    /// ★★ 为什么不能"把手势层涂黑"(2026-10-13 那条记录只说会全黑, 原因在此):
    ///   `VideoAreaRoot` 在 `VideoView.Content`(浮层窗口)里, **盖在整个视频区之上**。
    ///   实测: 涂品红 → 边带变品红(确实在最上层); 涂不透明黑 → **画面中心也黑了**。
    ///   ⇒ 它盖的是整块, 分不出"画面"和"边带", 这条路不可用。
    ///
    /// ★ 为什么改**父窗口**的类背景刷有效(而不是 Static 自己的):
    ///   实测 `Static` 的 `GCLP_HBRBACKGROUND` 本来就是 0(没设), 它的白来自 `Static` 类
    ///   自身的默认背景; 把**父窗口**设成黑之后, 那块区域由父窗口先擦黑, Static 再叠上去
    ///   就透不出白 —— 边带 152→0, 画面不受影响, 持续播放 2 秒仍正确(实测)。
    ///   ★ 关键: 它是**本进程的窗口**, 所以 `SetClassLongPtr` 可用;
    ///     libvlc 自己的 `VLC video *` 窗口怎么改都无效(探针 E/G/J 段都验证过)。
    ///
    /// ★ 起播/换片后必须再调一次(见调用点): libvlc 重建 vout 会重新建 `Static` 子窗口,
    ///   那时会再露出一次白 —— 这也是为什么"点一下就好"的观感像是"要碰一下才正常"。
    /// </summary>
    /// ★ 实测有效的只有一处: 把 `Static` 的**父窗口**(`HwndWrapper`, 也就是 HwndHost 的宿主)
    ///   的类背景刷设成黑 —— 边带 242→0 且画面正常, 持续播放不回弹。
    ///   ★ 反过来对 `Static` 自己涂黑**无效**(实测: 边带仍 242) —— 它自己那层不负责擦背景。
    ///     这条很容易搞反: "白边是 Static 画的" ≠ "改 Static 就能黑", 两个都试过才知道。
    /// </summary>
    private void ApplyHwndHostBlackBackground()
    {
        var hwnd = FindVideoStaticWindow();
        if (hwnd == IntPtr.Zero) return;
        // ★★ 只刷**宿主窗口**的类背景刷, 不隐藏 `Static`(2026-10-17 最终版):
        //   早两版试过 `ShowWindow(Static, SW_HIDE)`, 在探针里能把边带刷到 0,
        //   但真机一跑就发现 **整个视频画面也没了** —— 说明这台机器上画面就是 `Static`
        //   那条链画出来的(与探针顶部的拓扑假设不同, 不同 vout/显卡组合不一样)。
        //   ⇒ 隐藏它风险太大, 不能作为产品行为的默认值。
        //   ⇒ 回到"刷父窗口类背景刷"这一条: 探针实测 242→0, 且画面不受影响。
        VideoBackdropInterop.PaintClassBackgroundBlack(hwnd);
    }

    // --- 起播后把视频区原生窗口底色刷黑(见 ApplyHwndHostBlackBackground) ---

    /// <summary>
    /// **短时守护**: 每 <see cref="VideoBackdropGuardIntervalMs"/> 检查一次 `Static` 有没有
    /// 又被 libvlc 显示出来, 是就立刻再藏一次。
    ///
    /// ★★★ 为什么必须有它(2026-10-17 实测, 这是"白块依旧"的最后一层):
    ///   libvlc 在 **vout 建立 / 重建 / 首帧到达** 时会自己调 `ShowWindow(Static, SW_SHOW)`,
    ///   而我们的"藏一次"只在那一个瞬间有效 —— 它随后又被显示出来, 白色也跟着回来。
    ///   探针时间轴抓到的正是这个: 遮罩压着时边带 0, 撤罩后变成 242(白),
    ///   而那一刻 `Static` 的 `IsWindowVisible` 又是 True。
    ///   ⇒ 靠"事件触发时藏一次"必然有竞态(事件与 libvlc 的内部调用顺序由它决定);
    ///     只有**短时间反复确认**才能把这段时间盖死。
    ///
    /// ★ 为什么是"短时"而不是常驻: 起播/vout 重建之后约 2 秒内它才会折腾,
    ///   之后再频繁去摸原生窗口纯属浪费(而且 `CompositionTarget` 那条零原生调用的规矩
    ///   虽然只约束逐帧路径, 但也没必要给 DispatcherTimer 长期加活)。
    /// </summary>
    private DispatcherTimer? _videoBackdropGuard;
    private int _videoBackdropGuardTicks;

    /// <summary>守护检查间隔(ms)。比一帧长一点, 足够盖住"被重新显示"的窗口期。</summary>
    private const int VideoBackdropGuardIntervalMs = 120;

    /// <summary>守护跑多少拍收手。25 × 120ms = 3s, 覆盖 vout 重建 + 首帧那段。</summary>
    private const int VideoBackdropGuardTicks = 25;

    /// <summary>
    /// 启动短时守护(见 <see cref="RunVideoBackdropGuard"/>): 在接下来约 3 秒内反复确认
    /// `Static` 是隐藏的。尺寸剧变 / vout 重建 / 起播都要调一次。
    /// </summary>
    private void RunVideoBackdropGuard()
    {
        if (_closing) return;
        ApplyHwndHostBlackBackground();   // 立刻先来一次, 不等第一拍

        _videoBackdropGuard ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(VideoBackdropGuardIntervalMs)
        };
        _videoBackdropGuard.Tick -= OnVideoBackdropGuardTick;
        _videoBackdropGuard.Tick += OnVideoBackdropGuardTick;
        _videoBackdropGuardTicks = 0;
        _videoBackdropGuard.Stop();
        _videoBackdropGuard.Start();
    }

    private void OnVideoBackdropGuardTick(object? sender, EventArgs e)
    {
        if (_closing || ++_videoBackdropGuardTicks > VideoBackdropGuardTicks)
        {
            _videoBackdropGuard?.Stop();
            return;
        }
        ApplyHwndHostBlackBackground();
    }

    /// <summary>
    /// 起播 / vout 重建 / 尺寸剧变后, 把视频区原生窗口底色处理干净并启动短时守护。
    ///
    /// ★ 为什么是"守护一段时间"而不是"刷几次就停"(2026-10-17 实测):
    ///   `Playing` 触发时 `Static` 往往还没建出来(句柄 0); 建好之后 libvlc 还会在
    ///   首帧/重建时把它**重新显示**出来。固定 4 拍的预算在这两种情况下都不够用 ——
    ///   探针时间轴实测: 遮罩压着时边带 0, 撤罩后变 242, 而那一刻 `Static` 又是可见的。
    ///   ⇒ 改成 3 秒内每 120ms 确认一次(见 <see cref="RunVideoBackdropGuard"/>)。
    /// </summary>
    private void StartVideoBackdropFix() => RunVideoBackdropGuard();
    /// <summary>
    /// 找 libvlc 那个铺满视频区的 `Static` 窗口(白边的作者)。
    ///
    /// ★ 反射路径有两处必须踩准(2026-10-17 探针实测, 两处都错过一次, 错一处修复就**静默失效**):
    ///   ① `VideoView` 自己**不是** HwndHost —— 真正的宿主在私有字段 `_videoHwndHost`
    ///      (类型 `VideoHwndHost`)。
    ///   ② 该对象的句柄存在基类 `HwndHost._hwnd`, 类型是 **`HandleRef`**(不是 `IntPtr`),
    ///      必须读 `.Handle`。第一版只认 `IntPtr` ⇒ 永远拿到 0 ⇒ 边带一直白着。
    ///   ③ ★ 那个句柄拿到的**正是 `Static` 子窗口本身**(实测类名 `Static`, 父级才是
    ///      `HwndWrapper`), 所以直接对它涂黑就是对的; 不必再往上找父级
    ///      (改父级也有效, 但会波及宿主窗口的其它区域, 不如改 Static 精确)。
    ///   ★ 别用 `PresentationSource.FromVisual(VideoView)`: 那返回的是**外层播放器窗口**
    ///     的 HwndSource, 拿它改类背景刷会把主题底色一起改坏。
    /// </summary>
    private IntPtr FindVideoStaticWindow()
    {
        try
        {
            var vv = VideoView;
            if (vv == null) return IntPtr.Zero;

            var hostField = vv.GetType().GetField("_videoHwndHost",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (hostField?.GetValue(vv) is not System.Windows.Interop.HwndHost host) return IntPtr.Zero;

            var hwndField = typeof(System.Windows.Interop.HwndHost).GetField("_hwnd",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return hwndField?.GetValue(host) switch
            {
                HandleRef hr => hr.Handle,
                IntPtr ip => ip,
                _ => IntPtr.Zero
            };
        }
        catch { return IntPtr.Zero; }
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

            // 拖动尺寸监听订阅的是 CompositionTarget.Rendering —— 它是**静态**事件、不随窗口销毁,
            // 所以必须显式解绑(OnModalResizeRendering 里的 _closing 分支只是兜底:
            // 窗口真关闭时渲染循环可能已经停摆, 根本走不到那里)。
            StopModalResizeSizeWatch();
            // 弹幕逐帧推进器订阅的是**同一个**静态事件, 同理必须解绑, 否则窗口关了
            // 渲染回调还在, 回调里又摸 DanmakuCanvas —— 轻则空转, 重则已释放对象。
            StopDanmakuClock();

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
            // 长按快进也要收: 松手恢复速率的调用会碰 _mp, 而这时 _mp 可能正在 Dispose
            // (与上面两个手势定时器同理)
            StopFastForward();
            _localToastTimer?.Stop();
            _controlsHideTimer?.Stop();
            StopVolumeOsd();
            StopSpeedOsd();

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
        StopSpeedOsd();
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
