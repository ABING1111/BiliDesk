using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BiliDesk.Helpers;

namespace BiliDesk.Views;

public partial class PlayerWindow : FluentWindow
{
    // ------------------------------------------------------------ 小窗模式(2026-11-25, issue #127)
    //
    // 用途: "边打游戏边看攻略视频" —— 播放器缩成一颗 16:9 的小窗, 置顶浮在别的窗口上面。
    //
    // 与另外两个窗口级模式的关系:
    //   · 「铺满窗口」(_isFillWindow): 只收信息栏, 窗口尺寸不动、不置顶 —— 与小窗可以叠加;
    //   · 全屏(_isFullscreen): 与小窗**互斥**。全屏是"铺满整块显示器", 小窗是"缩到角落",
    //     两者不可能同时成立; 小窗里按 F/双击进全屏时先退出小窗(ToggleFullscreen),
    //     退全屏回到**常规窗口**而不是小窗 —— 每个模式都有一个明确的"退路"。
    //
    // ★ 几何记忆: 小窗期间窗口的尺寸/位置**不记**进设置(IsNormalStateForPlacement 把它挡掉),
    //   否则下次按常规布局打开时会被 840×600 的最小尺寸顶回去、记了等于白记。
    //   进入前先把常规几何落盘(与全屏/铺满同一条路), 退出时按快照还原。
    //   小窗自己的几何只在本进程内复用(s_lastMiniRect): 这次拖到哪、拉多大, 下次进小窗还在那儿
    //   (换片后新开的播放器窗口也算), 但不写进设置文件。

    /// <summary>「小窗」是否开着。窗口级显示模式, 换片不清(登记在 PersistentFields)。</summary>
    private bool _isMiniWindow;

    /// <summary>默认小窗宽度 = 所在显示器工作区宽度 × 这个比例(再夹进下面的 min/max)。</summary>
    private const double MiniWindowWidthRatio = 0.28;

    /// <summary>
    /// 小窗允许缩到的最小尺寸。与「铺满窗口」同一档(320×200 的下限在这里收紧到 16:9 的 180):
    /// 控制栏窄于 520 时会收起弹幕区域与音量滑杆(见 OnVideoAreaSizeChanged), 收完约 340 DIP,
    /// 320 是已经验证过"整条控制栏还排得开"的宽度, 再小按钮就要被裁掉了。
    /// </summary>
    private const double MiniWindowMinWidth = 320;
    private const double MiniWindowMinHeight = 180;

    /// <summary>小窗宽度上限: 4K 屏上 0.28 倍会大到不像"小窗", 封顶 560。</summary>
    private const double MiniWindowMaxWidth = 560;

    /// <summary>默认位与工作区四边的留白。工作区不含任务栏 ⇒ 小窗浮在任务栏上方。</summary>
    private const double MiniWindowMargin = 16;

    /// <summary>与 XAML 根元素上的默认尺寸一致(快照缺失时的兜底尺寸)。</summary>
    private const double DefaultWindowWidth = 1280;
    private const double DefaultWindowHeight = 760;

    /// <summary>
    /// 本进程里上一次的小窗几何(DIP)。做成 static 是因为换片后可能是**新窗口**,
    /// "上次拖到哪儿"应当跨窗口保留; 不落盘(见本节开头)。
    /// </summary>
    private static PlayerWindowPlacement.PlacementRect? s_lastMiniRect;

    private void OnMiniWindowClick(object sender, RoutedEventArgs e) => ToggleMiniWindow();

    private void ToggleMiniWindow()
    {
        if (_closing) return;
        if (_isMiniWindow) ExitMiniWindow();
        else EnterMiniWindow();
    }

    /// <summary>进入小窗: (必要时先退全屏) → 收顶栏与信息栏 → 置顶 → 缩到目标位。</summary>
    private void EnterMiniWindow()
    {
        if (_closing || _isMiniWindow) return;

        // ★ 先算目标位再动任何状态: 一块工作区都拿不到(理论上不会)就整体不进, 不留半截状态
        var target = ComputeMiniWindowTarget();
        if (target == null) return;

        // 全屏与小窗互斥: 走 HandleBackRequest 里"退全屏"的同一条路径(窗口状态、WS_CAPTION、
        // 底色都由 ApplyFullscreen(false) 收尾), 不在这里另写一套。
        if (_isFullscreen)
        {
            BeginPlacementTransition();
            _isFullscreen = false;
            ApplyFullscreen(false);
        }

        // 与全屏/铺满同理: 小窗几何不记, 切之前把常规几何存好(见 IsNormalStateForPlacement)
        CaptureNormalPlacement();
        PersistNormalPlacement();

        // 尺寸剧变 + vout 重建: 先收材质(见 VideoArea.cs 的"resize 期间临时停用材质")
        SuspendBackdropForResize();
        // 切换前先进稳定期: 还原动画/尺寸变化途中的几何不能采
        BeginPlacementTransition();
        _isMiniWindow = true;

        Topmost = true;
        TopBar.Visibility = Visibility.Collapsed;
        TopRow.Height = new GridLength(0);
        // 信息栏收起 + 最小尺寸放到小窗那一档。★ 必须排在改尺寸**之前**:
        //   MinWidth 还是 840 的话, 下面那句 Width = 430 会被最小尺寸顶住。
        ApplyInfoPanelState();
        MiniWindowBar.Visibility = Visibility.Visible;
        UpdateMiniWindowButton();

        Width = target.Value.Width;
        Height = target.Value.Height;
        Left = target.Value.Left;
        Top = target.Value.Top;
        WritePlacementLog("enter-mini",
            $"=({Left:0.##},{Top:0.##},{Width:0.##},{Height:0.##})");

        // vout 会按新尺寸重建, 新的 Static 子窗口要重新治一次(同进全屏)
        StartVideoBackdropFix();
        ResumeBackdropAfterResize();
    }

    /// <summary>退出小窗: 记住这次的小窗几何 → 放开置顶 → 顶栏与信息栏回来 → 还原常规几何。</summary>
    private void ExitMiniWindow()
    {
        if (!_isMiniWindow) return;

        RememberMiniWindowRect();

        SuspendBackdropForResize();
        // ★★★ 稳定期必须排在**所有**状态改动之前(2026-11-25 探针实测):
        //   下面 ApplyInfoPanelState 会把最小尺寸从 320×180 放回 840×600, 而此刻窗口还是小窗的
        //   430×240 —— 它会被立刻顶到 840×600。稳定期若还没开始, 这次 SizeChanged 就会把
        //   "840×600 的中间态"当成常规几何采进快照, 紧接着的还原用的就是这份被污染的快照
        //   (实测: 退出后窗口停在 840×600 而不是进入前的 1100×720)。
        BeginPlacementTransition();
        _isMiniWindow = false;
        // 拖到一半按 Esc 退出小窗: 拖动定时器立刻停(不停的话它下一拍也会自己停, 这里只是不等多一拍)
        StopMiniWindowDrag();

        Topmost = false;
        TopBar.Visibility = Visibility.Visible;
        TopRow.Height = new GridLength(44);
        MiniWindowBar.Visibility = Visibility.Collapsed;
        // 最小尺寸回到常规那一档(840×600), 同样必须排在还原尺寸之前
        ApplyInfoPanelState();
        UpdateMiniWindowButton();
        RestoreNormalPlacementAfterMini();

        // 还原后的几何要等稳定期结束才允许被重新采(此时采到的就是刚还原的这份, 结果一致)
        StartVideoBackdropFix();
        ResumeBackdropAfterResize();
        WritePlacementLog("exit-mini",
            $"=({Left:0.##},{Top:0.##},{Width:0.##},{Height:0.##})");
    }

    /// <summary>
    /// 把窗口放回进入小窗之前的常规几何。优先用快照 <see cref="_normalPlacement"/>;
    /// 从没采到过快照时(理论上只剩"窗口刚实体化就进小窗"这一种)退到 <see cref="RestoreBounds"/>,
    /// 再不行用 XAML 的默认尺寸。最后按虚拟桌面整窗夹一次 —— 中间副屏被拔掉的话,
    /// 旧位置可能已经在无人区了(负坐标合法, 见 PlayerWindowPlacement.ClampToBounds)。
    /// </summary>
    private void RestoreNormalPlacementAfterMini()
    {
        PlayerWindowPlacement.PlacementRect target;
        if (_hasNormalPlacement)
        {
            target = _normalPlacement;
        }
        else
        {
            var rb = RestoreBounds;
            target = new PlayerWindowPlacement.PlacementRect(rb.Left, rb.Top, rb.Width, rb.Height);
        }

        // 快照小于常规布局的最小尺寸(手改过设置 / 旧版本存的小窗尺寸): 不拿它还原, 用默认尺寸
        if (!PlayerWindowPlacement.IsUsable(target, MinWidth, MinHeight))
            target = new PlayerWindowPlacement.PlacementRect(Left, Top, DefaultWindowWidth, DefaultWindowHeight);

        var virtualScreen = new PlayerWindowPlacement.PlacementRect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        // 可见下限取窗口自身大小 ⇒ 尽量整窗留在桌面内(窗口比桌面还大时自动收敛)
        target = PlayerWindowPlacement.ClampToBounds(target, virtualScreen, target.Width, target.Height);

        Width = target.Width;
        Height = target.Height;
        Left = target.Left;
        Top = target.Top;
    }

    /// <summary>这次的小窗几何若可用, 记进 <see cref="s_lastMiniRect"/>(下次进小窗复用)。</summary>
    private void RememberMiniWindowRect()
    {
        var rect = new PlayerWindowPlacement.PlacementRect(Left, Top, ActualWidth, ActualHeight);
        if (PlayerWindowPlacement.IsUsable(rect, MiniWindowMinWidth, MiniWindowMinHeight))
            s_lastMiniRect = rect;
    }

    /// <summary>
    /// 小窗的目标几何: 各显示器工作区(DIP) + 当前窗口位置 + 上次的小窗位置, 交给纯函数算
    /// (规则与多屏/屏外兜底见 <see cref="PlayerWindowPlacement.ComputeMiniWindowRect"/>)。
    /// ★ 工作区是**主屏 DPI 的物理像素**, 这里按本窗口当前所在显示器的 DPI 换算 ——
    ///   混合缩放的多屏上副屏会有小误差, 与 EnsureVisibleOnScreen 同一个近似。
    /// </summary>
    private PlayerWindowPlacement.PlacementRect? ComputeMiniWindowTarget()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1;
        var scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;

        var areas = new List<PlayerWindowPlacement.PlacementRect>();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var wa = screen.WorkingArea;
            areas.Add(new PlayerWindowPlacement.PlacementRect(
                wa.Left / scaleX, wa.Top / scaleY, wa.Width / scaleX, wa.Height / scaleY));
        }

        var current = new PlayerWindowPlacement.PlacementRect(
            Left, Top,
            ActualWidth > 0 ? ActualWidth : Width,
            ActualHeight > 0 ? ActualHeight : Height);

        return PlayerWindowPlacement.ComputeMiniWindowRect(
            areas, current, s_lastMiniRect,
            MiniWindowWidthRatio, MiniWindowMinWidth, MiniWindowMinHeight, MiniWindowMaxWidth, MiniWindowMargin);
    }

    /// <summary>
    /// 同步顶栏「小窗」按钮的提示。图标是固定的"画中画"矢量(不随状态换字形):
    /// 小窗期间顶栏是收起的, "已进入"那一档的文案实际上只在小窗栏的「还原」上用得上。
    /// </summary>
    private void UpdateMiniWindowButton()
    {
        if (BtnMiniWindow == null) return;
        BtnMiniWindow.ToolTip = _isMiniWindow ? "还原 (退出小窗)" : "小窗播放（置顶小窗）";
    }

    // ------------------------------------------------------------ 小窗栏(浮层右上角)

    /// <summary>
    /// 指针是否停在小窗栏上。小窗里顶栏是收起的, 这条小栏是覆盖层里**除控制栏与跳过提示条之外**
    /// 第三个会吃掉左键的自绘元素 —— 视频区手势(单击暂停 / 双击全屏)与"激活点击补偿"
    /// 都要像对 ControlBar 那样对它让路, 否则点「还原」会顺带切一次播放/暂停。
    /// </summary>
    private bool IsPointerOnMiniBar =>
        _isMiniWindow && MiniWindowBar != null && MiniWindowBar.IsMouseOver;

    private void OnMiniGripMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isMiniWindow || _closing) return;
        e.Handled = true;
        StopActivationClickCompensation();
        BeginMiniWindowDrag();
    }

    private void OnMiniRestoreClick(object sender, RoutedEventArgs e) => ExitMiniWindow();

    private void OnMiniCloseClick(object sender, RoutedEventArgs e)
    {
        // ★ 激活点击补偿(QueueMiniBarButtonClick)可能与按钮自己的 Click 先后各到一次:
        //   关窗只做一次 —— 第二次走到这里时 _closing 已为 true。
        if (_closing) return;
        Close();
    }

    /// <summary>小窗拖动进行中的定时器(见 <see cref="BeginMiniWindowDrag"/>); 不在拖动时为 null。</summary>
    private DispatcherTimer? _miniDragTimer;

    /// <summary>
    /// 开始拖动小窗: 每 8ms 读一次光标位置, 让窗口跟着走, 左键松开即停。
    ///
    /// 用途就是"边打游戏边看", 所以必须同时成立两件事:
    ///   ① 别的窗口在前台时也拖得动 —— 浮层窗口被点击激活的那一刻由
    ///      <see cref="OnOverlayActivated"/> 补调本方法(那次点击不会派发成 MouseDown);
    ///   ② 不能卡住界面。
    ///
    /// ★★★ 为什么不用系统的"拖标题栏"(2026-11-25 探针 bd-probe-miniwindow 实测):
    ///   试过 <c>ReleaseCapture()</c> + <c>DefWindowProc(WM_NCLBUTTONDOWN, HTCAPTION)</c>
    ///   —— 与"拖边缘缩放"(TryBeginEdgeResize)同一条路。缩放那条是通的, 但移动这条会
    ///   **卡在模态循环里不返回**(松开鼠标也不退, 探针整轮超时)。所以这里自己跟着光标走:
    ///   没有模态循环, 不会卡死; 代价只是拖动时没有系统的窗口吸附(Win+方向键仍可用)。
    ///
    /// ★ 光标位置与按键状态都读 **Win32 的实时值**(GetCursorPos / GetAsyncKeyState), 不用 WPF 的
    ///   Mouse.LeftButton: 后者是 WPF 处理过鼠标消息之后的记录值 —— 指针一旦移出播放器窗口
    ///   (压到游戏窗口上), 鼠标消息就不再送到本线程, 那个值会永远停在"按着", 窗口跟着光标甩不掉。
    ///
    /// ★★★ 节拍是这里的关键(2026-11-25 探针实测, 用户报"拖动卡卡的"):
    ///   16ms 的 **Input 优先级**定时器在视频播放时**空转都只有 25Hz**, 拖动时掉到 11~15Hz ——
    ///   播放器的 UI 线程本来就忙, 低优先级回调被挤到帧后。改成下面三条后实测 55Hz:
    ///     · 优先级 <see cref="DispatcherPriority.Render"/>: 排在渲染之前, 移动与这一帧一起上屏;
    ///     · 拖动期间 <c>timeBeginPeriod(1)</c>: 默认 15.6ms 的定时器粒度会把 8ms 节拍拖成 16ms,
    ///       松手立刻 <c>timeEndPeriod(1)</c> 还原(只在按住这一小段时间生效);
    ///     · 每拍**只发一次** <c>SetWindowPos</c>(同时改 x/y): 分成 Left/Top 两次等于每拍移动窗口
    ///       两次(浮层也跟着动两次), 实测 2.7ms vs 1.8ms, 而且能看到"先横后竖"的抖。
    ///   移动用**物理像素**算: 起点换算一次, 之后直接 SetWindowPos, 不再每拍 DIP→px 取整。
    /// </summary>
    private void BeginMiniWindowDrag()
    {
        if (_closing || !_isMiniWindow) return;
        // 已经在拖(激活补偿与抓手自己的 MouseDown 可能先后各调一次): 不叠第二个定时器
        if (_miniDragTimer != null) return;
        if (!GetCursorPos(out var origin)) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1;
        var scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;
        var startPxLeft = (int)Math.Round(Left * scaleX);
        var startPxTop = (int)Math.Round(Top * scaleY);
        var lastPxLeft = int.MinValue;
        var lastPxTop = int.MinValue;

        timeBeginPeriod(1);

        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(MiniDragIntervalMs),
        };
        timer.Tick += (_, _) =>
        {
            // 左键松开 / 已退出小窗 / 窗口正在关: 拖动结束
            if (_closing || !_isMiniWindow || (GetAsyncKeyState(VkLButton) & AsyncKeyPressed) == 0)
            {
                StopMiniWindowDrag();
                return;
            }
            if (!GetCursorPos(out var pt)) return;

            var x = startPxLeft + (pt.X - origin.X);
            var y = startPxTop + (pt.Y - origin.Y);
            if (x == lastPxLeft && y == lastPxTop) return;   // 光标没动: 不必再挪一次窗口
            lastPxLeft = x;
            lastPxTop = y;
            // 同时改 x/y 的一次调用。WPF 的 Left/Top 会跟着 WM_MOVE 自己更新(与拖边缘缩放同一条路)。
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        };
        _miniDragTimer = timer;
        timer.Start();
    }

    private void StopMiniWindowDrag()
    {
        if (_miniDragTimer == null) return;
        _miniDragTimer.Stop();
        _miniDragTimer = null;
        // 与 BeginMiniWindowDrag 里的 timeBeginPeriod(1) 成对; 漏掉会在整个进程生命周期里
        // 把系统定时器精度钉在 1ms(耗电, 也影响别的程序)。
        timeEndPeriod(1);
    }

    private const int MiniDragIntervalMs = 8;
    private const int VkLButton = 0x0001;
    private const short AsyncKeyPressed = unchecked((short)0x8000);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint ms);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint ms);

    /// <summary>
    /// 小窗栏上的按钮被"激活点击"吞掉时, 等左键松开补一次 Click。
    ///
    /// ★ 背景(见 <see cref="OnOverlayActivated"/>): 浮层窗口不是激活窗口时, 用户点它的**第一次点击
    ///   只用于把它激活**, WPF 不派发 MouseDown/Up —— 按钮自己的 Click 就不会触发。小窗本来就是
    ///   给"别的应用在前台"时用的, 不补的话「还原」「关闭」都得点两下。
    /// ★ 等松开再触发, 而不是按下就触发: 与普通按钮一致 —— 按住拖走 = 取消。
    /// ★ 不新增字段: 状态全在这个闭包里。窗口关闭时 Tick 自己会停(见 _closing 判定)。
    /// ★ 可能与按钮自己的 Click 重复(激活那次点击偶发也被派发): 两个处理函数都幂等 ——
    ///   ExitMiniWindow 看 _isMiniWindow, Close 看 _closing。
    /// </summary>
    private void QueueMiniBarButtonClick(Button button)
    {
        if (button == null || _closing) return;

        var start = Environment.TickCount64;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            if (_closing || !_isMiniWindow) { timer.Stop(); return; }
            if ((GetAsyncKeyState(VkLButton) & AsyncKeyPressed) != 0)
            {
                // 按住不放超过 1.5s: 不是一次点击(比如按着拖拽), 放弃
                if (Environment.TickCount64 - start > 1500) timer.Stop();
                return;
            }
            timer.Stop();
            if (button.IsMouseOver && button.IsEnabled)
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        timer.Start();
    }
}
