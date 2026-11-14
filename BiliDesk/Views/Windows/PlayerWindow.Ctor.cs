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

    public PlayerWindow() : this("") { }
    public PlayerWindow(string bvid)
    {
        InitializeComponent();
        // 窗口大小/位置记忆(2026-11-03 新增大小, 2026-11-07 增加位置): 必须在 Show() 之前套用,
        // 否则会先按 XAML 的 1280×760 闪一下再跳成记忆尺寸。SizeChanged/LocationChanged 用来
        // 持续记录"最后一次常规状态"的几何(全屏/最大化/铺满期间不记), 关闭时统一落盘
        // (见 SavePlayerWindowPlacement)。
        ApplyRememberedPlayerWindowPlacement();
        SizeChanged += OnPlayerSizeChanged;
        LocationChanged += OnPlayerLocationChanged;
        Closed += OnClosed;
        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
        // 松开 → 时结束"长按 3 倍速"(见 OnPreviewKeyUp)
        PreviewKeyUp += OnPreviewKeyUp;
        // 切窗口时兜底结束所有"按住才成立"的加速态(见 OnPlayerDeactivated)
        Deactivated += OnPlayerDeactivated;
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

        // 清晰度/弹幕区域/倍速弹层的定位目标**必须代码直连**: XAML 的 ElementName 绑定在
        // VideoView 的 ForegroundWindow 浮层里解析失败(内容被移进另一个可视树后名字
        // 查找断链), PlacementTarget 为空 → 弹层跑到了浮层左上角(真机截图实证)。
        // 同时右缘对齐: Placement=Top 默认左缘对齐, 这几个弹层在控制栏右段, 左对齐偏丑;
        // Opened 时量出宽度差做负偏移, 弹层右缘与按钮右缘平齐。
        QualityPopup.PlacementTarget = BtnQuality;
        DanmakuAreaPopup.PlacementTarget = BtnDanmakuArea;
        // 倍速弹层(2026-11-03 自定义倍速): 与清晰度弹层同款, 右缘对齐 SpeedBtn。
        SpeedPopup.PlacementTarget = SpeedBtn;
        // 右缘对齐(弹层右缘 = 按钮右缘)。注意必须等弹层布局完成后再量宽:
        // Opened 那一刻 ActualWidth 还是 0, 偏移会算成正数把弹层推出窗口右缘(真机实证)。
        // Loaded 优先级在 Popup 自身布局跑完之后, 那时 ActualWidth 才可信。
        QualityPopup.Opened += (_, _) => Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                RightAlignPopup(QualityPopup, BtnQuality)));
        DanmakuAreaPopup.Opened += (_, _) => Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                RightAlignPopup(DanmakuAreaPopup, BtnDanmakuArea)));
        SpeedPopup.Opened += (_, _) => Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                RightAlignPopup(SpeedPopup, SpeedBtn);
                // 当前倍速可能是列表底部的预设(如 3x/4x): 打开时滚到高亮项,
                // 否则用户看到的永远是列表顶部, 以为没选中。
                if (SpeedList.SelectedItem != null) SpeedList.ScrollIntoView(SpeedList.SelectedItem);
            }));
        // ★ 弹层「点一下展开, 再点一下收起」(2026-11-14): 四个入口统一登记。
        //   StaysOpen=False 的弹层在"再点入口按钮"那一下会被 WPF 先行自动收起,
        //   Click 到达时 IsOpen 恒为 false —— 旧写法(无条件置 true / 取反 IsOpen)一律展开,
        //   用户报的"点击一律是打开"就是它。两层防线的接线见 HookPopupToggle。
        HookPopupToggle(QualityPopup, BtnQuality);
        HookPopupToggle(DanmakuAreaPopup, BtnDanmakuArea);
        HookPopupToggle(SpeedPopup, SpeedBtn);
        HookPopupToggle(PagesPopup, BtnPages);
        // ★ 视频区手势判定仍直接用"是否开着"作判据(见 AnyPlayerPopupOpen), 不订阅 Closed:
        //   2026-10-13 第一版在这里订阅过 Closed 记时间窗, 实测证明 Closed 晚于视频区的
        //   Up(70ms), 时间窗一次都没生效过 —— 已删除。
        //   ★ 别与上面入口按钮的"再点收起"混为一谈: 那条路记收起时刻用的是 IsOpen 的
        //   依赖属性变更回调(与赋值同步触发, 不等 Closed 事件), 正是为了绕开这里记录的
        //   "Closed 迟到"坑 —— 两种机制各管各的判定, 互不依赖。
        // 主题无需在此订阅: FluentWindow 基类已监听 ThemeChanged 并重建窗体 chrome,
        // 播放器自身的 DynamicResource 会随之自动刷新。之前这里多订阅了一次且从不退订,
        // 导致每个关闭过的播放器窗口都无法被 GC 回收。
        // 弹幕设置(全局): 初始化运行时状态, 并订阅设置页的变更
        _danmakuOn = Svc.Settings.DanmakuEnabled;
        _danmakuColorful = Svc.Settings.DanmakuColorful;
        _danmakuAreaRatio = Svc.Settings.DanmakuAreaPercent / 100.0;
        Svc.Settings.DanmakuSettingsChanged += OnDanmakuSettingsChanged;
        // 界面字体(2026-11-14): 弹幕层在独立浮层可视树里, 不跟窗口字体继承走,
        // 换字体时要显式走一遍外观重算(见 OnUiFontChanged)。窗口本体由 FluentWindow 基类负责。
        Helpers.UiFont.Changed += OnUiFontChanged;
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
        // 显示相关视频: 开关一变, 已打开的信息栏立即隐藏/重新加载(2026-11-14)
        Svc.Settings.RelatedVideosChanged += OnRelatedVideosSettingChanged;
        if (DanmakuAreaSlider != null) DanmakuAreaSlider.Value = Svc.Settings.DanmakuAreaPercent;
        // ★ 到这里四个滑杆都已经同步成"用户的真实设置", 才打开回灌闸门。
        //   在这之前的所有 ValueChanged 都只刷文字、不落库 —— 否则 XAML 里的初始
        //   Value(25/80/100/2)会在 InitializeComponent() 时把用户设置整个覆写成默认。
        _syncingDanmakuLook = false;
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
            // 首帧耗时(从 PlayMedia 算起): 与 [Timing] 那行对齐, 用来验证起播优化有没有效果
            if (_playWatch != null)
            {
                WriteLibVLCDebug($"[Timing] first-frame={_playWatch.ElapsedMilliseconds}ms\n");
                _playWatch = null;
            }
            // aout(音频输出)就绪后把应用侧记住的音量补写回去:
            // 无 aout 时 libvlc_audio_set_volume 是静默丢弃的(反编译 3.8.0 确认 setter 不看返回值),
            // 长视频远端 DASH 在起播/缓冲/换清晰度期间常出现"用户滚轮调了音量但 aout 还没建好"的窗口,
            // 不补写的话 aout 就绪后音量会回到原生侧的旧值 —— 表现就是"滚轮调音量没反应"。
            // 放在函数最前: 后面 _resumeAfterSwitchMs 的 MarkSeekIssued 可能立刻进入 seek 静音,
            // 顺序反了会把静音状态顶掉(声音先于画面出来)。
            try { _mp.Volume = _volumeCache; } catch { }
            LogAudioStateOnce();
            // ★ 2026-11-05: 这次起播后面还要"恢复进度"时不收遮罩 —— 否则遮罩淡出、
            //   下一秒 seek 等待又淡入, 就是用户看到的"闪一下"。让它一直盖到 seek 到位
            //   (由 ClearSeekWatchdog 统一收), 期间文案换成"正在缓冲…"。
            var willResumeSeek = _resumeAfterSwitchMs > 0;
            if (!willResumeSeek) HideLoading();
            // 把 EOF 时切黑的手势层还原成近透明(见 OnEndReached 里切黑的原因):
            // 新 input 的 vout 已经起来, 后面要能看见画面。
            // ★ 2026-11-03: 只处理 EOF 那一层; 改窗口大小/最大化/全屏不再压黑布。
            RestoreVideoAreaBackground();
            WriteLibVLCDebug(
                $"\n[Playing] eofBlack={_eofBlackActive} ended={_ended} " +
                $"bg={DescribeVideoAreaBg()} time={SafeReadTime()} state={_mp.State}\n");
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
                var byStartTime = _resumeByStartTime;
                _resumeByStartTime = false;

                // ★★★ 2026-11-05: 重建路径用 Media 的 :start-time 定位时, **不要再设一次
                //   Time** —— 那会在解复用器刚打开的窗口里二次打断 input, 实测正是
                //   "重建后补跳又落回开头"的来源(用户报的"最后回到开头 8 秒")。
                //   其余路径(切清晰度/本地文件/聆听退出)保持原样。
                if (!byStartTime) { try { _mp.Time = resume; } catch { } }

                // 遮罩不在这里收: 换成"正在缓冲…"继续盖着, 由看门狗确认到位后统一收。
                ShowLoading(BufferingText);

                // 走一次 MarkSeekIssued: 钉住进度条/时间显示, 并启动 seek 看门狗,
                // 否则恢复进度那一下也可能被回写顶回 0(表现是"切完清晰度从头播")。
                // ★ 不再叠"补跳"定时器(2026-10-17 手术删): 补跳那一套(反复重设 Time
                //   + 要求连续 3 次稳住)实测会把位置冲过头, 而且主路已换成 seek 一次
                //   就准的合流单流。万一这一次没接住, 交给下面的看门狗重建一次即可。
                MarkSeekIssued(resume);
            }
            else
            {
                _resumeByStartTime = false;
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
            // 诊断: 把出错时正在播的 URL 也写进 debug 文件
            WriteLibVLCDebug(
                $"\n[EncounteredError] bvid={_currentBvid} cid={_currentCid} lastUrl={_lastPlayUrl}\n");
            // ★ 2026-11-14(issue #81): 地址过期导致的重连 403 —— 手里有新地址就先自救一次,
            //   救不了(或与过期无关)再弹失败遮罩, 不打断几个小时的连续播放。
            if (TryAutoRecoverFromError()) return;
            HideLoading();
            FailOverlay.Visibility = Visibility.Visible;
            FailText.Text = "视频播放出错, 请检查网络或尝试其他清晰度";
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
                    // vout 重建 ⇒ EOF 那层黑一定可以撤了(新画面已经在出帧)。
                    // ★ 2026-11-03: 这里只揭 EOF 黑层; resize/最大化/全屏不再有黑布。
                    RestoreVideoAreaBackground();
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
            // 播放地址保鲜(2026-11-14, issue #81): B 站 playurl 签名只有约 2 小时,
            // 长播放必须在到期前后台换一条新地址, 否则断流后重建会直接 403。
            MaybeRefreshPlayUrl();
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

}
