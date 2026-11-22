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

namespace BiliDesk.Views;

public partial class PlayerWindow : FluentWindow
{

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
        // 2026-11-22(mpv): 覆盖层现在由 PlayerVideoView 自己持有, 清内容 + 隐藏即可,
        // 不再需要反射 LibVLCSharp 的 ForegroundWindow。
        try { VideoView.OverlayContent = null; } catch { }

        try
        {
            if (VideoView.OverlayWindow is Window fw) fw.Hide();
        }
        catch { /* 拿不到就算了, 内容已清空 */ }
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
            if (VideoView.OverlayWindow is not Window fw) return;
            if (ReferenceEquals(fw, _overlayKeyboardWindow)) return;
            _overlayKeyboardWindow = fw;
            fw.PreviewKeyDown += OnPreviewKeyDown;
            // ★ 长按 → 的"松开"也挂上来: 键盘焦点压在视频区时, KeyDown/KeyUp 都属于这层
            //   浮动窗口(WPF 跨 HWND 不会把 KeyUp 送到播放器主窗口)。漏了这条, 长按会一直
            //   停在 3 倍速上(用户实测"3.0x 快进中"不消失)。
            fw.PreviewKeyUp += OnPreviewKeyUp;
            fw.PreviewMouseDown += OnPreviewMouseDown;
            // 滚轮也要挂上来: 鼠标压在视频画面上时事件属于这层浮动窗口(见 OnVideoAreaMouseWheel)
            fw.PreviewMouseWheel += OnVideoAreaMouseWheel;
            // ★ 激活点击补偿(2026-11-12): 这层窗口不是激活窗口时, 用户点视频区的
            //   **第一次点击只会把它激活**, WPF 不会把这次点击报成鼠标事件 ——
            //   表现就是"点一下只是把播放器叫到前面, 再点一下才暂停"。见 OnOverlayActivated。
            fw.Activated += OnOverlayActivated;
            // ★ 失焦兜底也必须挂上来(2026-11-21): 点过视频区之后焦点就归这层覆盖窗口,
            //   这时切走整个程序, **主窗口早已是失活状态**, 不会再收到 Deactivated ——
            //   只挂主窗口的话, 长按倍速的临时速率与浮层都不会被收掉
            //   (用户报的"失焦切回来倍速框不消失"就是从这条缺口进来的)。
            //   处理函数里有"是否还在播放器体系内"的延迟判定, 焦点在两层之间来回切不会误收。
            fw.Deactivated += OnPlayerDeactivated;
            // 这个窗口不该出现在任务栏里(它是跟随主窗口的装饰层)
            fw.ShowInTaskbar = false;
        }
        catch { /* 拿不到就算了, 键盘至少在主窗口里是可用的 */ }
    }


    // ------------------------------------------------------------ resize 期间临时停用材质
    //
    // ★★★ 2026-11-16(用户第二次报"云母/亚克力下改窗口大小画面依然会闪"):
    //   近黑笔刷只解决了"黑边被 DWM 当透明"那一层; 画面本身仍会闪, 因为材质开启时
    //   整个客户区是 DWM 玻璃区(GlassFrameThickness = -1), 而播放器里有一个 HwndHost
    //   (LibVLC 的 video output)。WindowChrome + 玻璃区 + HwndHost 在连续 resize 时
    //   是 WPF 的已知闪烁组合(dotnet/wpf#1176、SO 6500336; 普通材质不透明, 所以没有)。
    //   ⇒ 尺寸变化期间把背景材质**临时收掉**(窗口落回普通不透明实色底), 尺寸稳定后
    //     再挂回去。这样视频区回到"普通材质"下那条已经验证稳定的路径 —— 不压黑布,
    //     画面照常可见, 只是拖动那一下右栏/顶栏的材质会暂时变成实色。

    /// <summary>尺寸稳定多久后把材质挂回来。180ms: 覆盖 DWM 最大化/还原动画的尾部。</summary>
    private const int BackdropResumeDelayMs = 180;

    private DispatcherTimer? _backdropResumeTimer;

    /// <summary>是否已经排了一次"延迟停材质"(见 <see cref="QueueBackdropSuspendForResize"/>)。</summary>
    private bool _backdropSuspendQueued;

    /// <summary>
    /// 启动/首次定位期间**不**因 SizeChanged 停材质。
    /// ★ 打开播放器时窗口会按记忆尺寸做一次二次定位并触发 SizeChanged; 若当成用户 resize,
    ///   材质会先消失 180ms 再回来 —— 观感就是"打开播放器时材质闪一下"。
    ///   这个闸门在 OnLoaded 的首次布局跑完后才放开。
    /// </summary>
    private bool _backdropResizeArmed;

    /// <summary>
    /// 是否正处于"拖边缘缩放"的模态循环(WM_ENTERSIZEMOVE → WM_EXITSIZEMOVE)。
    /// ★ 这个期间停掉的材质**只在 EXITSIZEMOVE 后恢复**: 用户可能拖一下停一下,
    ///   若按"尺寸静止 180ms"恢复, 下一次继续拖时又会先闪一帧。
    /// </summary>
    private bool _modalResizeInProgress;

    /// <summary>
    /// 尺寸变化开始: 临时停用背景材质(只停材质, 不盖黑布)。
    /// ★ 材质本来就没开 / 已经停了 → 什么都不做。
    /// </summary>
    private void SuspendBackdropForResize()
    {
        if (_closing || BackdropSuspended) return;
        if (!IsBackdropMaterialActive) return;
        SetBackdropSuspended(true);
        // 非模态 resize(状态切换/Win+方向键/DPI)没有可靠的结束消息, 靠尺寸静止恢复;
        // 拖边缘的模态 resize 则等 EXITSIZEMOVE 统一恢复(见 _modalResizeInProgress)。
        if (!_modalResizeInProgress) ResumeBackdropAfterResize();
    }

    /// <summary>
    /// 没有走 WM_ENTERSIZEMOVE / 状态切换钩子的尺寸变化(Win+方向键吸附、DPI 变化等):
    /// 排到下一轮渲染前再停材质, 避免在 SizeChanged 里直接改 WindowChrome 造成重入。
    /// </summary>
    private void QueueBackdropSuspendForResize()
    {
        if (_closing || _backdropSuspendQueued || BackdropSuspended) return;
        _backdropSuspendQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _backdropSuspendQueued = false;
            if (!_closing) SuspendBackdropForResize();
        }), DispatcherPriority.Render);
    }

    /// <summary>
    /// 尺寸还在变(或刚变完): 把"恢复材质"的定时器往后推。
    /// 每次都续期, 所以只有尺寸真正稳定 <see cref="BackdropResumeDelayMs"/> 后才恢复。
    /// </summary>
    private void ResumeBackdropAfterResize()
    {
        if (_closing || !BackdropSuspended) return;
        _backdropResumeTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(BackdropResumeDelayMs)
        };
        _backdropResumeTimer.Tick -= OnBackdropResumeTick;
        _backdropResumeTimer.Tick += OnBackdropResumeTick;
        _backdropResumeTimer.Stop();
        _backdropResumeTimer.Start();
    }

    private void OnBackdropResumeTick(object? sender, EventArgs e)
    {
        _backdropResumeTimer?.Stop();
        if (_closing) return;
        SetBackdropSuspended(false);
    }

    /// <summary>
    /// 播放器窗口尺寸变化: 续期恢复定时器; 若还没停材质(非模态 resize),
    /// 补一次延迟停用。★ 这里**不能**直接调 ApplyChrome —— SizeChanged 还在 WPF 布局里,
    /// 改 WindowChrome 会重入; 所以只排队(见 <see cref="QueueBackdropSuspendForResize"/>)。
    /// </summary>
    private void OnPlayerResizedForBackdrop()
    {
        if (_closing || !IsLoaded || !IsVisible) return;
        if (!_backdropResizeArmed) return;
        // 首次定位/全屏切稳定期里的 SizeChanged 不能当成用户 resize; 但已经停了材质时
        // 仍要续期恢复定时器(否则最大化动画中途材质会回来)。
        if (_placementSettling && !BackdropSuspended) return;
        // 模态拖边缘期间统一在 EXITSIZEMOVE 恢复; 中间每一次 SizeChanged 都不续期。
        if (_modalResizeInProgress) return;
        if (BackdropSuspended)
        {
            ResumeBackdropAfterResize();
            return;
        }
        if (WindowState == WindowState.Minimized) return;
        if (IsBackdropMaterialActive) QueueBackdropSuspendForResize();
    }

    /// <summary>
    /// 用户开始拖动/缩放(WM_ENTERSIZEMOVE)。
    /// ★ 只有拖在**窗口缩放带**上才停材质: 拖标题栏只是移动窗口, 不该让材质闪一下消失。
    ///   判据用 <see cref="HitTestResizeEdge"/> —— 与视频区边缘缩放共用同一套边缘检测。
    /// </summary>
    protected override void OnModalResizeStarted()
    {
        base.OnModalResizeStarted();
        if (_closing || !IsLoaded) return;
        if (HitTestResizeEdge() == 0) return;
        _modalResizeInProgress = true;
        // 上一轮 resize 的恢复定时器可能还在跑: 必须掐掉, 否则它会在这次拖动中途
        // 把材质挂回来, 又闪一下。
        _backdropResumeTimer?.Stop();
        SuspendBackdropForResize();
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

    // ------------------------------------------------------------ 激活点击补偿

    /// <summary>
    /// 覆盖层窗口被"点击"激活时, 补一次被它吞掉的视频区手势。
    ///
    /// ★★★ 根因(2026-11-12 实测, 用户报"第一次点击只是把窗口叫到前面, 第二次才暂停"):
    ///   弹幕层 + 控制栏渲染在 LibVLCSharp 的 ForegroundWindow 里 —— 那是**独立的顶层窗口**
    ///   (见 HookOverlayKeyboard)。它不是激活窗口时, 用户点视频区的**第一次点击只用于把它激活**,
    ///   WPF 不会把这次点击报成 MouseDown/MouseUp(实测: 点击后 GetForegroundWindow 立刻变成
    ///   "LibVLCSharp.WPF", 播放状态不变; 第二次点击才暂停)。
    ///
    /// ★ 修法: 激活事件里若左键正按着、指针在视频区(且不在控制栏/跳过条/弹层上), 就起一个
    ///   40ms 的轮询:
    ///     · 正常手势一旦起步(第二下点击/双击) ⇒ 立即让位(_videoGestureActive/_suppressSingleClick);
    ///     · 按住不到 500ms 就松手 ⇒ 按单击处理, 播放/暂停;
    ///     · 按住 ≥ 500ms ⇒ 按长按快进处理(与 OnVideoAreaMouseLeftDown 同一阈值), 松手恢复速率。
    ///   轮询而不是"等一个 Up": 激活点击的 Up 也可能一起被吞, 不能依赖它。
    /// </summary>
    private void OnOverlayActivated(object? sender, EventArgs e)
    {
        if (_closing || _engine == null) return;
        // 直播: 点画面本来就不该切播放/暂停, 这套"补一次被吞掉的单击"对直播没有意义
        // (2026-11-20, issue #98 —— 只认控制栏按钮)。
        if (_isLive) return;
        // 只认"鼠标按着把窗口激活": Alt+Tab / 程序自己激活时左键是松开的
        if (Mouse.LeftButton != MouseButtonState.Pressed) return;
        // 控制栏按钮 / 跳过提示条 / 弹层上的点击有各自的处理, 不能补成"暂停"
        if (ControlBar != null && ControlBar.IsMouseOver) return;
        if (SkipNoticeBorder != null && SkipNoticeBorder.IsMouseOver) return;
        if (AnyPlayerPopupOpen) return;
        if (VideoAreaRoot != null && !VideoAreaRoot.IsMouseOver) return;

        _activationClickPending = true;
        _activationLongPress = false;
        _activationClickStartTick = Environment.TickCount64;
        _activationClickTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _activationClickTimer.Tick -= OnActivationClickTick;
        _activationClickTimer.Tick += OnActivationClickTick;
        _activationClickTimer.Start();
    }

    private void OnActivationClickTick(object? sender, EventArgs e)
    {
        if (!_activationClickPending) { StopActivationClickCompensation(); return; }
        // 正常手势已经起步(第二下点击会置 _videoGestureActive / _suppressSingleClick) ⇒ 让位
        if (_videoGestureActive || _suppressSingleClick) { StopActivationClickCompensation(); return; }

        var held = Environment.TickCount64 - _activationClickStartTick;
        if (Mouse.LeftButton == MouseButtonState.Pressed)
        {
            if (!_activationLongPress && held >= 500)
            {
                _activationLongPress = true;
                StartFastForward();
                ShowSpeedHint("倍速播放中", hold: true);
            }
            return;
        }

        // 已经松手: 短按补一次播放/暂停, 长按补一次"结束快进"
        if (_activationLongPress)
        {
            // ★ 先清标记再收尾: 否则下面的 StopActivationClickCompensation 会认为
            //   "长按还活着", 再走一次 CancelFastForward 把刚亮的「已恢复」浮层掐掉。
            _activationLongPress = false;
            StopFastForward();
            ShowSpeedHint(RestoredSpeedText(), hold: false);
        }
        else if (held < 500)
        {
            OnPlayPauseClick(this, new RoutedEventArgs());
        }
        StopActivationClickCompensation();
    }

    /// <summary>
    /// 结束激活点击补偿(正常手势接管 / 鼠标离开 / 关窗)。长按态要先恢复速率。
    /// ★ 2026-11-21: 长按态用 <see cref="CancelFastForward"/> 收尾, 把浮层一起收掉 ——
    ///   这条补偿路径的松手可能永远不来(指针已离开/窗口已切走), 只恢复速率会留下
    ///   一个「倍速播放中」的浮层挂在屏幕上。
    /// </summary>
    private void StopActivationClickCompensation()
    {
        if (_activationLongPress)
        {
            _activationLongPress = false;
            CancelFastForward();
        }
        _activationClickPending = false;
        _activationClickTimer?.Stop();
    }

    /// <summary>是否有一笔"被激活吞掉、等待补偿"的点击</summary>
    private bool _activationClickPending;

    /// <summary>这笔激活点击已经进入长按快进(见 <see cref="OnActivationClickTick"/>)</summary>
    private bool _activationLongPress;

    /// <summary>激活点击按下的时刻(TickCount64)</summary>
    private long _activationClickStartTick;

    private DispatcherTimer? _activationClickTimer;

    /// <summary>已经挂过键盘处理的覆盖层窗口(避免重复订阅)</summary>
    private Window? _overlayKeyboardWindow;
}
