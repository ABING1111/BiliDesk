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
            // ★ 激活点击补偿(2026-11-12): 这层窗口不是激活窗口时, 用户点视频区的
            //   **第一次点击只会把它激活**, WPF 不会把这次点击报成鼠标事件 ——
            //   表现就是"点一下只是把播放器叫到前面, 再点一下才暂停"。见 OnOverlayActivated。
            fw.Activated += OnOverlayActivated;
            // 这个窗口不该出现在任务栏里(它是跟随主窗口的装饰层)
            fw.ShowInTaskbar = false;
        }
        catch { /* 拿不到就算了, 键盘至少在主窗口里是可用的 */ }
    }

    /// <summary>
    /// 把视频区原生宿主窗口处理成"黑底 + 不覆盖子窗口"。
    ///
    /// ★ 根因(2026-10-17 探针 bd-probe-letterbox 钉死; 2026-11-03 去掉黑布后复核):
    ///   `VideoView` 是 <c>HwndHost</c>, 它下面由 LibVLCSharp/libvlc 建了一串**同进程**子窗口:
    ///   <code>
    ///     HwndWrapper[...]   ← HwndHost 宿主窗口
    ///       Static           ← LibVLCSharp 建的 VideoHwndHost 窗口   ★白边/白闪的作者
    ///       VLC video main   ← 铺满整个视频区
    ///       VLC video output ← 只盖住画面那一块
    ///   </code>
    ///   视频宽高比与视频区不一致时, `video output` 只占中间一条, 上下(或左右)剩下的区域
    ///   没有任何窗口画东西 ⇒ 露出 `Static` 的**窗口类默认背景(白色)**。
    ///   尺寸剧变(vout 重协商)时, `Static` 重绘还会把白底刷到视频子窗口上 ⇒ 整块白闪。
    ///
    /// ★ 处理(见 <see cref="VideoBackdropInterop.EnsureBlackVideoHost"/>):
    ///   · `WS_CLIPCHILDREN`: 父窗口/Static 重绘时排除子窗口区域, 不把白底刷到画面上;
    ///   · 子类化 Static 的 `WM_ERASEBKGND` / `WM_PAINT`: 客户区被擦/重绘时直接填黑;
    ///   · 父窗口 + Static 的类背景刷也设黑: 兜住 libvlc 重新显示 Static 的瞬间。
    ///
    /// ★ 不隐藏 Static: 真机上 `ShowWindow(SW_HIDE)` 会把**整个画面**一起藏掉
    ///   (不同 vout/显卡组合拓扑不一样, 不能赌)。
    /// ★ 起播 / vout 重建 / 换清晰度 / 进出全屏后都要再调一次 —— 见 `Vout`、`Playing`
    ///   与 <see cref="StartVideoBackdropFix"/> 的调用点。
    /// </summary>
    private void ApplyHwndHostBlackBackground()
    {
        var hwnd = FindVideoStaticWindow();
        if (hwnd == IntPtr.Zero) return;

        // ★★ 2026-11-03(去掉 resize/最大化/全屏黑布后的根修):
        //   不再只刷父窗口类背景刷, 而是把 `Static` 自己治成"黑底 + 不覆盖子窗口":
        //     · WS_CLIPCHILDREN: 父窗口/Static 重绘时不把白底刷到 `VLC video output` 上;
        //     · 子类化 WM_ERASEBKGND / WM_PAINT: 客户区被擦/重绘时直接填黑。
        //   这样尺寸剧变时新暴露的区域是黑的, 而不是 Static 的默认白底。
        VideoBackdropInterop.EnsureBlackVideoHost(hwnd);
    }

    // --- 起播后把视频区原生窗口底色刷黑(见 ApplyHwndHostBlackBackground) ---

    /// <summary>
    /// **短时守护**: 每 <see cref="VideoBackdropGuardIntervalMs"/> 检查一次 `Static` 有没有
    /// 又被 libvlc 显示出来, 是就立刻再藏一次。
    ///
    /// ★★★ 为什么必须有它(2026-10-17 实测, 2026-11-03 去掉黑布后保留):
    ///   libvlc 在 **vout 建立 / 重建 / 首帧到达** 时会重新显示/重绘 `Static`,
    ///   白色也可能跟着回来。我们的 `EnsureBlackVideoHost` 在那一刻可能还没拿到句柄
    ///   (`Playing` 触发时 Static 往往还没建出来), 所以需要短时间反复套用。
    ///   ⇒ 靠"事件触发时刷一次"必然有竞态; 只有**短时间反复确认**才能把这段时间盖死。
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
    /// 启动短时守护(见 <see cref="RunVideoBackdropGuard"/>): 在接下来约 3 秒内反复套用
    /// "黑底 + 不覆盖子窗口"的原生修复。尺寸剧变 / vout 重建 / 起播都要调一次。
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
    ///   首帧/重建时重新显示/重绘它。固定几拍的预算在这两种情况下都不够用 ——
    ///   ⇒ 改成 3 秒内每 120ms 套用一次(见 <see cref="RunVideoBackdropGuard"/>)。
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
    ///      `HwndWrapper`) —— 它由 LibVLCSharp 的 VideoHwndHost 创建, 我们这侧可以安全地
    ///      改样式/子类化。处理入口见 <see cref="ApplyHwndHostBlackBackground"/>。
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
        if (_closing || _mp == null) return;
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
                ShowSpeedHint(FormatRate(_fastForwardTarget) + " 快进中", hold: true);
            }
            return;
        }

        // 已经松手: 短按补一次播放/暂停, 长按补一次"结束快进"
        if (_activationLongPress)
        {
            StopFastForward();
            ShowSpeedHint("已结束快进", hold: false);
        }
        else if (held < 500)
        {
            OnPlayPauseClick(this, new RoutedEventArgs());
        }
        StopActivationClickCompensation();
    }

    /// <summary>结束激活点击补偿(正常手势接管 / 鼠标离开 / 关窗)。长按态要先恢复速率。</summary>
    private void StopActivationClickCompensation()
    {
        if (_activationLongPress)
        {
            _activationLongPress = false;
            StopFastForward();
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
