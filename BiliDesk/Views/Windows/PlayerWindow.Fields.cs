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

// ★★★ 本类原有 7215 行单文件, 2026-11-07 拆成 17 个 partial 分片(**纯移动、零逻辑改动**)。
//   动机: 单文件已经大到"方法列表都要靠工具翻"的程度, 按职责切片后:
//     · 改某个功能只需要打开对应分片 —— 弹幕在 Danmaku.cs, 评论在 Comments.cs, 关闭在 Lifetime.cs;
//     · partial 只是同一个类在编译期的拼接, private 互访与运行时行为完全不变;
//     · 反射自检(VerifyResetCoverage 的 DeclaredOnly 扫描、TimerFields)与 partial 不冲突。
//   分片清单:
//     .Fields.cs      字段(全部状态)          .Ctor.cs       构造函数
//     .Loading.cs     播放入口与加载          .Playback.cs   播放控制与 seek
//     .Controls.cs    气泡/音量/倍速/画质/音质  .Danmaku.cs   弹幕全链路
//     .Keyboard.cs    键盘与返回              .Fullscreen.cs 全屏/铺满/窗口记忆
//     .Actions.cs     三连/关注/收藏          .Listen.cs     听视频
//     .States.cs      互动状态刷新            .Comments.cs   评论加载与排序
//     .Season.cs      合集与分P               .CommentActions.cs 评论互动
//     .Sponsor.cs     Toast + 赞助片段        .VideoArea.cs  视频区 HWND/背景
//     .Lifetime.cs    关闭/定时器清理/诊断
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

    /// <summary>
    /// 直播清晰度 qn(0 = 服务端默认档位)。
    /// ★ 与 <see cref="_currentQn"/> 分开: 后者是"点播清晰度", 直播的 qn 是另一套编码
    ///   (80/150/250/400/10000/20000…)。直播重连/重播时沿用这里选过的档, 换片源归零。
    /// </summary>
    private int _liveQn;
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
    /// "播完后的跳转"已经发起过 —— 用来**挡住同一次点击的第二条路**。
    ///
    /// ★★★ 2026-11-05 修「播完显示重播页 → 点进度条跳转 → 画面全黑、重播按钮消失、
    ///   但没有画面」的根因。
    ///
    ///   机理(探针 bd-probe-eofseek 实锤):
    ///     ① EOF 时 <see cref="OnEndReached"/> **刻意不调 `_mp.Stop()`**, 那条 input 就停在
    ///        ENDED 状态(实测 `state=Ended` / `vout=0`), vout 已被拆掉 —— 这正是"重播页"
    ///        需要一层不透明黑遮罩盖住白底的原因(见 RestoreVideoAreaBackground)。
    ///     ② 对一条 **ENDED 的 input** 发 seek, libvlc 只是把 `Time` 改成目标值当回执,
    ///        **不会重建任何东西**: 探针实测 `state` 一直是 Ended、`vout=0`、位置纹丝不动。
    ///     ③ 而 `MarkSeekIssued` 里那句 `EndOverlay.Visibility = Collapsed` 已经把"重播页"
    ///        收起来了 —— 于是"重播按钮消失"、遮罩没了、vout 又不在 ⇒ **屏幕上只剩一片黑**。
    ///        (底层那层 EOF 黑手势层还压着, 所以是纯黑而不是白。)
    ///
    ///   ⇒ 结论: **在已结束的 input 上, seek 是无效操作**。要让画面回来只能起一条
    ///     **全新的 input**(与"点重播"走同一条路)。这条判断由 <see cref="_ended"/> 负责。
    ///
    /// ★ 本字段负责的是**另一件事**: 同一次点进度条会被 WPF 走**两条路**
    ///   (ValueChanged + Thumb.DragCompleted, 见 OnProgressDragEnd 的说明)。
    ///   第一条走重建路径时已经把 `_ended` 清掉了, 第二条若按普通 seek 处理,
    ///   就会对**刚起播的新 input** 再发一次 seek —— 正好把它的解复用器打断,
    ///   于是"画面卡住/又要缓冲一次"; 更糟的是它可能落在新 input 还没稳定的窗口里,
    ///   把 :start-time 定位好的落点又冲掉(就是"回到开头 8 秒"那类问题)。
    ///
    /// ★ 它在**三个点**被清掉, 缺一不可:
    ///   ① <see cref="MarkSeekIssued"/> —— 新 input 已起播并打上跳转看门狗, 从此
    ///      重复触发由那个看门狗自带的"同目标忽略"规则接手(见 SeekTo 开头的判定);
    ///   ② <see cref="RestoreEndedOverlay"/> —— 重建失败退回"已播完"态, 此时用户
    ///      还要能再点进度条, 不能被这个闸一直拦着;
    ///   ③ <see cref="ResetForNewMedia"/> / <see cref="EnterReplayState"/> —— 换片与
    ///      重播之后它描述的是上一条视频的旧事, 必须清。
    /// </summary>
    private bool _seekAfterEndedGuard;

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
    /// 本次跳转是否已经"重建 Media 救过一次"(2026-10-17 手术新增)。
    ///
    /// ★★★ 这个预算必须**跨重建存活**, 绝不能在重建后复位 —— 否则会无限循环(2026-10-17
    ///   实测用户报"快进后无限缓冲"): 重建 → Playing → MarkSeekIssued 把 _seekRetries 清零
    ///   → 补跳又失败 → 再重建 …… 每轮都要重新拉一次流, 而进度条照样在跑,
    ///   用户看到的就是"永远缓冲不完"。**重建救不回来的 DASH 长跳就该如实放弃**
    ///   (停在真实位置), 好过让播放器无限重试。
    ///
    ///   因此复位只发生在**用户真正发起一次新跳转**时(见 <see cref="SeekTo"/>),
    ///   重建路径自己不碰它。
    /// </summary>
    private bool _seekRebuildTried;

    /// <summary>
    /// "落点弹回起点"的连续确认值(-1 = 尚未观察到)。
    ///
    /// ★★★ 2026-10-31 修"长视频有时快进进度条失效、回到起点"的第二层兜底:
    ///   看门狗的既有判据是"距目标 900ms 以内才算到位", 而"跳 30 分钟 → 落回片子开头"
    ///   的偏差是分钟级, 永远进不了这个窗口 —— 于是每 2.5s 重发一次、重发 2 次、再重建
    ///   一次, 全部落回开头; 重建后 MarkSeekIssued 又把 _seekRetries 清零, 再来一轮 ……
    ///   用户看到的是**无限缓冲**。修法: 落点连续两次同时远离"跳转前位置"与"目标"
    ///   (即跳到了**别处**)时, 不再重试同一条 input, 直接重建 Media 一次 ——
    ///   重建=起新 input, 是"换片/切清晰度"那条一直好用的路。重建预算
    ///   (<see cref="_seekRebuildTried"/>)仍然只给一次, 防循环。
    /// </summary>
    private long _seekBounceSeenMs = -1;

    /// <summary>
    /// 本次"落点弹回别处"第一次被观察到的时刻(TickCount64, -1 = 当前没有候选)。
    ///
    /// ★★★ 2026-11-05 修"点进度条快进 → 一直闪正在缓冲 → 最后回到开头 8 秒":
    ///   原来的判据只要求**连续两次心跳**同时远离起点与目标(等待期粒度 100ms ⇒ 200ms),
    ///   而 libvlc 在 seek 受理后本来就会把 Time 先**冲过目标、再回退 1.5s 左右**逐步爬回。
    ///   这个正常过渡会被 900ms 的到位窗口排除, 于是 200ms 就被误判成"弹回别处"→ 重建 Media;
    ///   重建后同样的过渡再来一次, 用户看到的就是**反复缓冲 + 闪屏**, 最后 Input 被折腾到开头。
    ///   修法: 弹回必须**持续** <see cref="SeekBounceConfirmMs"/> 才算数, 正常的回退过渡
    ///   会在确认期内走出"远离目标"状态, 自动作废。重建预算仍然只给一次。
    /// </summary>
    private long _seekBounceAt = -1;

    /// <summary>最近一次 seek 发出的时刻(TickCount64, -1 = 本片还没发过)。</summary>
    private long _seekIssuedAt = -1;

    /// <summary>最近一次 seek 的目标(毫秒, -1 = 本片还没发过)。</summary>
    private long _lastSeekTargetMs = -1;

    /// <summary>
    /// 本次恢复进度是否由 Media 的 <c>:start-time</c> 完成(重建路径用)。
    ///
    /// ★ 为什么要单独记: 起播后 Playing 事件里如果**再**设一次 <see cref="MediaPlayer.Time"/>,
    ///   等于在解复用器刚打开、还没稳定的窗口里打断它 —— 实测这正是"重建后补跳又落回开头"
    ///   的来源。走 start-time 时只 MarkSeekIssued 钉 UI / 启动看门狗, 不再二次设 Time。
    /// </summary>
    private bool _resumeByStartTime;

    /// <summary>
    /// "EOF 反而是 seek 没落地"的补救预算(每次用户 seek 只给一次)。
    /// 见 OnEndReached 里那条"跳转中绝不自救"的说明。
    /// </summary>
    private bool _eofSeekRebuildTried;

    /// <summary>进度条开始拖动那一刻的值(用来识别"点击轨道"这种伪拖动, 见 OnProgressDragEnd)。</summary>
    private double _progressDragStartValue;

    /// <summary>
    /// 用户**真的拖动**过进度条(与"点击轨道"区分开, 见 OnProgressDragDelta)。
    ///
    /// ★ 为什么单独记: WPF 的 <c>IsMoveToPointEnabled</c> 让"点击轨道"也走完整的拖动流程,
    ///   光看 <c>_isSeeking</c> 分不清"点了轨道"和"拖着走"。而"点一下轨道就闪出个时间气泡"
    ///   是纯噪音, 只有真拖动才该提示。
    /// </summary>
    private bool _progressDragMoved;

    /// <summary>
    /// seek 静音: 跳转后视频画面要等缓冲(几百 ms~几秒), 但音频解复用快, 会**先出声** ——
    /// 用户听到的是"画面还黑着/冻着, 声音已经播到那边去了"。所以 seek 一发出就静音,
    /// 画面确认到位(或放弃/换片/播完)再恢复。-1 = 当前没有处于 seek 静音。
    /// </summary>
    private int _seekMuteSavedVolume = -1;
    private const int SeekRetryMax = 2;
    private const long SeekConfirmWindowMs = 900;  // "偏早/跑偏"判别的起步窗口(到位容差见 SeekSettledToleranceMs)

    /// <summary>
    /// "到位"判定的实际容差(毫秒)。比 <see cref="SeekConfirmWindowMs"/> 宽:
    /// libvlc 受理 seek 后 Time 会先冲过目标再回退 ~1.5s, 900ms 会把这个正常过渡当成"没到位",
    /// 进而喂给弹回判据。3s 以内且位置在推进, 对用户就是"已经跳过去了"(DASH 分片本身
    /// 也常有 4~5s 的落点偏早, 见 SeekLandingMinEarlyMs)。
    /// </summary>
    private const long SeekSettledToleranceMs = 3_000;

    /// <summary>弹回判据的持续时间: 落点必须连续这么久都同时远离起点与目标, 才认定为整体失效。</summary>
    private const long SeekBounceConfirmMs = 1_500;

    /// <summary>一次 seek 从发出到"重发/重建"的等待上限。必须大于正常回退过渡(~2.2s)。</summary>
    private const long SeekConfirmTimeoutMs = 3_500;

    /// <summary>EOF 时把"最近一次 seek 还没落地"视作假 EOF 的时间窗。</summary>
    private const long SeekEofRecoverWindowMs = 15_000;

    /// <summary>最近一次 seek 的目标与实际位置差多少才算"根本没落地"(毫秒)。</summary>
    private const long SeekEofRecoverMinMissMs = 60_000;


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

    // --- 提前结束的自救(片源在离结尾不远处 EOF) ---
    // 历史上这条逻辑是为"DASH 音轨(input-slave)先于视频 EOF"写的。2026-11-05 查明:
    // 真凶其实是 libvlc 的 mp4 demux **读不出 B 站 fMP4 的最后一个分片**(见 BuildMedia
    // 里 `:demux=avformat` 的说明) —— 画面和声音会一起在离结尾 1.5~2.4s 处结束。
    // 换 avformat 后这个 EOF 已经消失, 但这里仍保留"从断点续播"的兜底:
    // 远端流因网络/服务端截断而提前 EOF 时, 它还能把剩下的几秒接上。
    // 次数 + 推进距离双保险, 防"片源确实更短"时陷入无限续播循环。
    //
    // ★ 尾部截断(2026-10-01, 用户报"结尾不断缓冲 + 进度条回退重播几秒前内容"的修复):
    //   离结尾太近时**绝不自救**。原因链: ① 续播要重建 input, 必然重新预缓冲(黑屏);
    //   ② 续播的 seek 在远端 DASH 双流上落点固定偏早 4.5~5s(见 notes-player-internals.md),
    //     所以每次自救都是"回退 5 秒重播已看过的内容"; ③ 播完回退的那几秒又 EOF,
    //     再触发一次自救 —— 推进距离检查被"回退"本身满足, 于是缓冲+回退反复最多 3 次。
    //   ★★ 阈值取 10s(2026-10-01 第二次校准): 第一版 6.5s 实测不够 —— 用户实测一条
    //     08:39 的视频, EOF 点在剩 7s 处, 表现 = 黑屏缓冲几秒 → 重建后续播点若音轨
    //     已物理缺失 → 立刻再 EOF → 重播遮罩, 纯白等一趟。
    //   ★ 2026-11-05 之后这 10s 不再是为"B 站音轨缺口"留的(缺口是 demux bug, 已修);
    //     它现在的意义是"最后 10s 内任何提前 EOF 都不值得重建"(重建的黑屏比少几秒更糟)。
    private long _lastKnownMs;            // TimeChanged 报告的最后位置(EOF 后 _mp.Time 会归零, 不能用)
    private int _eofResumeCount;
    private long _lastEofResumeMs;
    private const int EofResumeMax = 3;
    private const long EofResumeMinAdvanceMs = 2000;
    /// <summary>
    /// 尾部静默区宽度(毫秒)。距结尾不足这个距离时, 三件事都不做:
    ///   ① EOF 不自救(重建 input 只会黑屏缓冲一趟);
    ///   ② 卡顿检测不报"正在缓冲"(收尾阶段的短暂停滞是常态);
    ///   ③ 进度显示改用"卡住起点 + 墙钟×倍速"外推(让进度条平滑走完最后一程)。
    /// ★ 2026-11-05: 造成"最后几秒没声音"的 fMP4 最后分片 bug 已由 avformat 修掉,
    ///   这个常量不再需要覆盖"音轨缺口上限"; 保留 10s 只是给残余的远端截断留缓冲。
    /// </summary>
    private const long TailQuietZoneMs = 10000;

    /// <summary>
    /// 视频区手势层的常态底色(#01000000 —— 近透明但可命中鼠标)。
    ///
    /// ★★ 2026-10-13 的回归教训: 我曾为了"盖住 letterbox 白底"把它改成**不透明黑**,
    ///   结果用户报"视频播放时全黑"。层级搞反了 —— 这一层在 VideoView.Content
    ///   (浮层窗口)里, 位于 vout HWND **之上**, 不透明会盖住**视频画面**而不是白底。
    ///   想盖 vout 白底要用独立的遮罩层(EndOverlay / LoadingOverlay / FailOverlay 那几层,
    ///   平时 Collapsed), 手势层必须保持近透明。
    ///
    /// EOF 时手势层会切黑盖住露出白底的 vout HWND(见 OnEndReached), 起播时用它还原。
    /// </summary>
    private static readonly Brush VideoAreaBackBrush = CreateVideoAreaBackBrush();

    /// <summary>
    /// EOF(播完)时手势层是否压成了不透明黑。
    ///
    /// ★ 2026-11-03 起播放器**不再**为"改窗口大小/最大化/全屏"压黑布 —— 那些白闪改由
    ///   原生宿主窗口(`VideoBackdropInterop.EnsureBlackVideoHost`)在根上治掉。
    ///   剩下唯一需要压黑的路径就是 EOF: 那时 vout 被拆掉, 视频 HWND 会露出白底,
    ///   而"重播"遮罩是半透明的, 不垫一层黑就会变成"浅色主题下一片白"。
    /// </summary>
    private bool _eofBlackActive;

    private static Brush CreateVideoAreaBackBrush()
    {
        // 近透明黑(#01000000): 见 VideoAreaBackBrush 的说明 —— 绝不能改成不透明,
        // 那会盖住视频画面(2026-10-13 实测回归: 用户看到整个画面全黑)。
        // 之所以不是完全透明: 背景全透明时鼠标事件会穿透, 手势就收不到了。
        var b = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x01, 0, 0, 0));
        b.Freeze();
        return b;
    }

    /// <summary>
    /// 撤掉 EOF 时压上的不透明黑, 把手势层还原成常态的近透明底。
    ///
    /// ★ 2026-11-03: 这是**唯一**还允许切黑手势层的路径(播完时 vout 被拆掉, 会露白底)。
    ///   改窗口大小/最大化/全屏的白闪已经改由 <see cref="ApplyHwndHostBlackBackground"/>
    ///   在原生宿主窗口上治理, 不再走这里。
    /// </summary>
    private void RestoreVideoAreaBackground()
    {
        if (VideoAreaRoot == null || !_eofBlackActive) return;
        _eofBlackActive = false;
        VideoAreaRoot.Background = VideoAreaBackBrush;
    }

    private bool _danmakuOn = true;

    /// <summary>是否显示彩色弹幕(全局设置)。关掉之后所有弹幕统一白色, 只看内容不受颜色干扰。</summary>
    private bool _danmakuColorful = true;

    // 弹幕条目类型见 Helpers/RawDanmaku(时间秒 / 文本 / 模式 / 颜色)
    private readonly List<RawDanmaku> _danmaku = new();

    /// <summary>
    /// 未经过滤的原始弹幕(按时间有序)。
    /// 保留它是为了让"设置页改了智能屏蔽/类型过滤"能在不重新请求接口的前提下立刻重算
    /// _danmaku —— 否则用户想试一种过滤就得重新拉一遍弹幕, 体验很差
    /// (而且长视频要连拉几十个分段)。
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

    // --- 音质(2026-11-06) ---
    /// <summary>本次播放可选的音轨 id(从高到低), 音质弹层用它决定哪些档可点。</summary>
    private readonly List<int> _audioIds = new();
    /// <summary>当前实际在播的音轨 id(0 = 无独立音轨 / 未知)</summary>
    private int _currentAudioId;
    /// <summary>当前音轨对应的音轨地址(切音质时用 resume 逻辑重新起播)</summary>
    private bool _switchingAudio;
    private bool _fillingAudioMenu;

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

    /// <summary>当前是否处于"长按快进"中(速率已被切成 <see cref="_fastForwardTarget"/>)。</summary>
    private bool _isFastForwarding;

    /// <summary>
    /// 长按快进开始: 切到 <see cref="_fastForwardTarget"/>(至少 <see cref="FastForwardRate"/>)。
    ///
    /// ★ 松手时要恢复到的速率由 <c>_rateBeforeFastForward</c> 记住 —— 不能写死 1.0f:
    ///   用户可能先用倍速按钮设了 1.5x, 写死会让松手后悄悄变成 1x。
    ///   (旧版就有这个 bug, 2026-10-16 一并对齐。)
    /// </summary>
    private float _rateBeforeFastForward = 1.0f;

    /// <summary>
    /// 本次长按实际要切到的倍速 = max(2.0x, 按下前的倍速)。
    /// ★ 不能写死 2.0x: 自定义倍速允许到 4.0x, 用户已经 3x 时再长按**不应该反而变慢**。
    /// ★ 浮层提示也读这个值, 保证"看到的倍速 = 实际切到的倍速"。
    /// </summary>
    private float _fastForwardTarget = FastForwardRate;

    /// <summary>开始长按快进(改倍速)。见上方那段说明为什么用 SetRate 而不是 seek。</summary>
    private void StartFastForward()
    {
        if (_closing || _mp == null) return;
        // 直播没有"加速往前放"这回事(本来就一直在最新处); 播完了也没有
        if (_isLive || _ended) return;

        try { _rateBeforeFastForward = _mp.Rate; }
        catch { _rateBeforeFastForward = 1.0f; }
        if (_rateBeforeFastForward <= 0) _rateBeforeFastForward = 1.0f;

        // ★ 2026-11-03: 不再把"当前已经是 2.0x"强行记成 1.0x。
        //   自定义倍速下 2.0x/3x 都是常态, 那样会在松手时把用户的倍速偷偷改回 1x。
        //   目标取 max(2x, 当前): 当前更快就保持更快, 绝不因为"快进"而减速。
        _fastForwardTarget = Math.Max(FastForwardRate, _rateBeforeFastForward);

        _isFastForwarding = true;
        // ★ 顺序: 先让弹幕时钟按新倍速落定当前值, 再真正 SetRate(同 OnSpeedClick 那条说明)
        SyncDanmakuClockRate(_fastForwardTarget);
        try { _mp.SetRate(_fastForwardTarget); } catch { StopFastForward(); }
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
        // ★ 2026-10-31: 变速收尾也是音轨偶发掉线的暴露点(同 seek 后没声的机理)。
        //   挂一次带宽限期的自愈检查, 命中才动手(见 HealAudioIfDead)。
        HealAudioIfDead("fastforward-end");
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

    /// <summary>片段剩余不足这么多毫秒时不跳: 跳到片段末尾只前进一点点, 反而像画面抽了一下</summary>
    private const long SponsorMinRemainMs = 500;
}
