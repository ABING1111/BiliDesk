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

    // ------------------------------------------------------------ 弹幕显示区域

    private void OnDanmakuAreaClick(object sender, RoutedEventArgs e)
    {
        // 点一下展开, 再点一下收起(见 PlayerWindow.Controls.cs 的 HookPopupToggle)
        TogglePlayerPopup(DanmakuAreaPopup);
    }

    /// <summary>调整弹幕显示区域占比(弹幕只在画面上方 ratio 比例内滚动)</summary>
    private void OnDanmakuAreaChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var pct = (int)e.NewValue;
        _danmakuAreaRatio = pct / 100.0;
        // 占比不再在控制栏里摆小字(2026-11-14 图标化), 改成写进按钮的 ToolTip
        if (BtnDanmakuArea != null) BtnDanmakuArea.ToolTip = $"弹幕显示区域 {pct}%";
        if (DanmakuAreaValue != null) DanmakuAreaValue.Text = pct + "%";
        // ★ 初始化/程序同步期间只刷文字, 不落库 —— 否则 XAML 里的初始 Value=25
        //   会在 InitializeComponent() 时把用户存的值覆写成默认(见 _syncingDanmakuLook 的说明)。
        if (_syncingDanmakuLook) return;
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
        // 回灌保护: 下面的赋值会触发 ValueChanged, 那三个处理器会写设置。
        // ★ 保存/恢复上一个值而不是无条件 false —— 本方法可能在"窗口初始化"的大闸门里被调用,
        //   直接置 false 会提前把闸门打开(2026-10-30)。
        var prev = _syncingDanmakuLook;
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

            // 胶囊底衬开关(2026-11-05): 按钮上的文字要跟着设置走。
            // 设置页改了它, 播放器这边也要立刻反映(否则两边说法不一致)。
            if (DanmakuPillButtonText != null)
                DanmakuPillButtonText.Text = s.DanmakuPillBackground ? "开" : "关";
        }
        finally
        {
            _syncingDanmakuLook = prev;
        }
    }

    /// <summary>
    /// 切换「胶囊底衬」(2026-11-05)。
    ///
    /// ★ 落库后由 SettingsStore 广播 DanmakuSettingsChanged → OnDanmakuSettingsChanged
    ///   → ApplyDanmakuLook 统一重算(泳道高 + 清屏重建), 所以这里**只管改设置**,
    ///   不自己写渲染逻辑 —— 免得两处各改一半(历史上"字号变了泳道没跟着变"就是这么漏的)。
    ///
    /// ★★★ 这两处都不是随手写的, 是实测出来的:
    ///   ① 触发源是 **Border, 不是 Button** —— 这个弹层是 StaysOpen=False, WPF 会替它
    ///      CaptureMouse(PopupRoot); 而 ButtonBase 在按下时 CaptureMouse() 抢走捕获,
    ///      结果是点击永远"完不成"(Click 不触发), 且捕获一直挂在按钮上 ——
    ///      **整个应用的点击从此全被它吃掉**(用户报的"点完之后光标变了、哪里都点不动")。
    ///   ② 事件是 **PreviewMouseLeftButtonDown, 不是 MouseLeftButtonUp** ——
    ///      同一个捕获机制下, 鼠标**抬起根本没被送进弹层的可视树**
    ///      (实测: Border 的 PreviewDown=1 / PreviewUp=0), 等 Up 等于永远等不到。
    /// </summary>
    private void OnDanmakuPillClick(object sender, MouseButtonEventArgs e)
    {
        var next = !Svc.Settings.DanmakuPillBackground;
        Svc.Settings.UpdateDanmakuSettings(
            Svc.Settings.DanmakuEnabled, Svc.Settings.DanmakuAreaPercent, Svc.Settings.DanmakuSmartFilter,
            pillBackground: next);
        // 广播没有触发(值没变)时也要把按钮文字刷一次, 否则点了没反应。
        UpdateDanmakuLookTexts();
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
        // ★ 2026-11-03: 拖边缘不再由 WPF 压黑遮罩 —— 白闪改由原生宿主窗口治理
        //   (VideoBackdropInterop.EnsureBlackVideoHost)。这里直接把"按在缩放带上"
        //   交给 DefWindowProc 进入系统模态缩放循环即可, 不再需要 ENTER/EXIT 的消息钩子。
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
        // 正常手势起步 ⇒ 撤掉"激活点击补偿"(见 PlayerWindow.VideoArea.cs 的 OnOverlayActivated)
        StopActivationClickCompensation();
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
            ShowSpeedHint(FormatRate(_fastForwardTarget) + " 快进中", hold: true);
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
    /// 视频区右键按下: 只记下"这一下是我起的", 真正的动作在 Up(切换铺满窗口)。
    ///
    /// ★★ 2026-11-03 修复"右键收起视频详情页会错误触发 2 倍速":
    ///   之前这里挂过一条 500ms 长按计时器, 右键按住稍久就会 StartFastForward。
    ///   但右键的既定语义是"铺满窗口 / 收起右侧详情栏", 用户点它时按住的时间并不固定 ——
    ///   于是一次正常的收起动作只要稍慢就会莫名变成 2 倍速。
    ///   ⇒ 右键**不再参与**长按快进; 长按快进只保留在左键上(见 OnVideoAreaMouseLeftDown)。
    ///   两条手势的状态与计时器也因此彻底分开, 不再互相复位/抢用。
    /// ★ 与左键 Down 同款的成对判定(弹层余波防护)在这里照抄:
    ///   没有配对 Down 的 Up 一律丢弃, 这是唯一可靠的判据(见 AnyPlayerPopupOpen)。
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
    /// 视频区右键抬起: 切「铺满窗口 / 还原」(收起或展开右侧详情栏)。
    /// 没有配对 Down 的 Up 一律丢弃(那是弹层余波, 见 Down 的说明)。
    ///
    /// ★ 右键**不参与**长按快进(2026-11-03), 见 <see cref="OnVideoAreaMouseRightDown"/>。
    ///   所以这里不再检查 _longPressFired, 也不会误碰左键那条快进链。
    /// </summary>
    private void OnVideoAreaMouseRightUp(object sender, MouseButtonEventArgs e)
    {
        if (!_rightGestureActive) { e.Handled = true; return; }
        _rightGestureActive = false;
        if (_closing) return;
        // 走**同一个开关**(与顶栏按钮共享 _isFillWindow, 不复制逻辑)
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
        // 激活点击补偿也一并撤掉: 指针已经离开视频区, 这一下不再属于它
        StopActivationClickCompensation();
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
        if (SpeedPopup != null && SpeedPopup.IsOpen) return false;
        if (SubtitlePopup != null && SubtitlePopup.IsOpen) return false;   // 字幕弹层开着别藏(2026-11-15)
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

        // 2026-11-14(重绘): 淡出的同时向下沉 8px —— 像"滑走"而不是原地变透明, 观感更连贯。
        // ★ FillBehavior.Stop: 动画一结束 Y 就回到基准值 0 —— 此刻已经全透明, 看不出跳回。
        if (ControlBarShift != null)
        {
            var down = new System.Windows.Media.Animation.DoubleAnimation(0, 8,
                TimeSpan.FromMilliseconds(180))
            { FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop };
            ControlBarShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, down);
        }
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

        // 从下方 8px 处滑回原位(与 HideControls 的下沉对称)
        if (ControlBarShift != null)
        {
            ControlBarShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            ControlBarShift.Y = 0;
            var up = new System.Windows.Media.Animation.DoubleAnimation(8, 0,
                TimeSpan.FromMilliseconds(180))
            {
                FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            ControlBarShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, up);
        }
    }

    // ------------------------------------------------------------ 缓存当前视频

    private void OnCacheClick(object sender, RoutedEventArgs e)
    {
        if (_closing || string.IsNullOrEmpty(_currentBvid)) return;
        // 与卡片右键"下载视频"同一条链路: 弹选择窗(清晰度 + 保存位置) → 后台下载。
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
            // ★ 传当前实际清晰度做默认档、传当前 cid(播放器手里就有, 不用再查详情),
            //   这样用户"看着 1080P 点下载"时菜单里默认就是 1080P。
            var saved = await DownloadActions.ShowAndDownloadAsync(
                bvid, _currentCid, VideoTitleText.Text, _currentQn, this);
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
    /// 弹幕字体(2026-11-14 新增): 跟随全局界面字体(Helpers.UiFont)。
    /// ★ 必须显式设到每条弹幕的 TextBlock 上(见 AddDanmaku): 弹幕层在 LibVLC 的独立浮层
    ///   可视树里, 不跟 FluentWindow 的字体继承走。
    /// ★ 与字号同一类是**设置驱动的派生量**: 字体变了字宽就变, 泳道追尾校验失效,
    ///   所以 ApplyDanmakuLook 把它归入"清屏重建"那一档。
    /// </summary>
    private FontFamily _danmakuFont = Helpers.UiFont.Current;

    /// <summary>
    /// 是否给弹幕加**半透明圆角胶囊底衬**(2026-11-05 新增)。
    ///
    /// ★ 默认开 —— 纯白字压在明亮画面上会看不清, 这正是底衬要解决的问题。
    ///   关掉就回到 2026-10-07 之后的纯文字观感(那时用户明确要求去掉长方形底衬)。
    /// ★ 它是**用户偏好** ⇒ 必须登记在 PersistentFields 里(换片保持),
    ///   否则启动自检会点名, 且换片后设置会被重置。
    /// </summary>
    private bool _danmakuPill = true;

    /// <summary>
    /// 同步弹幕外观滑杆时的回灌保护。
    /// 见 <see cref="UpdateDanmakuLookTexts"/>: 赋 Value 会触发 ValueChanged。
    ///
    /// ★★★ 初值必须是 **true**(2026-10-30 修"弹幕字号/覆盖面永远回到默认"):
    ///   XAML 里四个滑杆都写了初始 `Value`(25 / 80 / 100 / 2) 且挂了 `ValueChanged` ——
    ///   `InitializeComponent()` 装载这些初始值时会**立刻触发**四个处理器, 把用户的设置
    ///   覆写成默认值。构造函数后面再从设置里读, 读到的已经是刚被覆写的 25/80/100/2。
    ///   闸门从一开始就关着, 等构造函数把滑杆同步成用户的真实设置后才打开。
    /// </summary>
    private bool _syncingDanmakuLook = true;

    /// <summary>基准弹幕字号(倍率 100% 时的值)</summary>
    private const double DanmakuBaseFontSize = 24;

    private static double DefaultFontSize() => DanmakuBaseFontSize;

    /// <summary>
    /// 默认泳道高(字号 × 系数)。字段初始化器要用到, 所以给个静态入口。
    ///
    /// ★ 按"**开胶囊**"算(2026-11-05): 胶囊是默认状态, 而这个值只是给
    ///   "设置还没读进来之前"的极短窗口用的 —— 真正生效的泳道高由 ApplyDanmakuLook
    ///   无条件同步(它必须无条件, 否则设置等于默认值时会早退, 泳道高就停在旧值上)。
    /// </summary>
    private static double DefaultLaneHeight()
        => Helpers.DanmakuMotion.LaneHeight(DanmakuBaseFontSize, pill: true);


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
    /// <see cref="Helpers.DanmakuMotion.LaneHeight"/> 推出来, 改字号只需要改这一处。
    /// ★ 2026-10-12 起它不再是"写死的值", 而是 100% 档位的基准, 实际字号
    ///   = 本值 × (用户的字号百分比 / 100), 见 <see cref="_danmakuFontSize"/>。
    /// ★ 2026-10-13 删掉了这里的别名常量 `DanmakuFontSize`(它只是 = DanmakuBaseFontSize,
    ///   全项目无引用): 两个名字指同一个值, 改字号时"该改哪一个"会让人停一下。
    ///   真正的定义在文件上方(见 DanmakuBaseFontSize)。
    /// </remarks>
    //
    // ★ 泳道高系数本身搬到了 Helpers/DanmakuMotion.cs(LaneRatio / PillLaneRatio):
    //   2026-11-05 加"胶囊底衬"后, 泳道高要看**有没有底衬**取两个不同的系数,
    //   而这个值必须能被探针单独验(见 .probes/bd-probe-danmaku), 放在窗口里就没法测。

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
                // 原始列表按时间排好序存下来, 供后续"改过滤设置立即重算"使用
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
    /// ★ 关键词屏蔽已于 2026-11-05 整条链路删除(设置项 + 存储字段 + 解析器),
    ///   所以这里只剩"智能屏蔽"与"类型过滤"两件事。
    /// </summary>
    private static IEnumerable<RawDanmaku> ApplyDanmakuFilter(IEnumerable<RawDanmaku> list)
    {
        var s = Svc.Settings;
        var smart = s.DanmakuSmartFilter;

        // 类型过滤(滚动/固定/彩色/高级): 四项全开 = 不过滤。
        // ★ 用"是否全开"而不是"是否有任一项关闭"来判断能不能跳过 —— 后者写反过一次,
        //   表现是默认状态下弹幕全没了。
        var typeFilter = !(s.DanmakuFilterScroll && s.DanmakuFilterFixed
                           && s.DanmakuFilterColorful && s.DanmakuFilterAdvanced);

        // 两件事都没开就原样返回, 省掉一次完整遍历
        if (!smart && !typeFilter) return list;
        return FilterCore(list, smart, typeFilter,
            s.DanmakuFilterScroll, s.DanmakuFilterFixed,
            s.DanmakuFilterColorful, s.DanmakuFilterAdvanced);

        static IEnumerable<RawDanmaku> FilterCore(
            IEnumerable<RawDanmaku> src,
            bool smart,
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

                // 类型过滤: 用户没勾的类型直接丢弃(与智能屏蔽解耦)
                if (typeFilter && !IsTypeAllowed(d, allowScroll, allowFixed, allowColorful, allowAdvanced))
                    continue;

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
    ///      几十条弹幕同屏就是几十次全像素处理。
    ///      ★ 2026-11-05: 底衬(半透明黑胶囊)作为**可选项**加回来了, 但它只是一次
    ///        矩形填充, 成本远低于阴影 —— **仍然不许挂 Effect**;
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

        // ★★★ 底衬(2026-11-05): 曾经整块删掉(2026-10-07 用户要求"弹幕文字有长方形背景阴影, 移除它"),
        //   现在作为**可选项**加回来, 默认开 —— 纯白字压在明亮画面上会看不清。
        //   关闭时走的是**原路径**: Background=null、CornerRadius 不设、内边距也回到旧的
        //   (6,1,6,1), 观感与 2026-10-07 之后**逐像素一致**, 不会"关掉还留个淡淡的框"。
        //
        //   ★ Padding **保留**(两种模式都保留): 它不是装饰, 而是参与 width 计算(见下面的 Measure),
        //     泳道分配与"追尾校验"都拿这个宽度当"这条弹幕要占多宽"。
        //     去掉它会让相邻弹幕的横向间距归零, 密集时文字首尾相接糊成一片。
        //   ★ 绝不给它挂 DropShadowEffect: 违反上面第 1 条铁律(几十条同屏 = 几十次全像素合成)。
        var border = new Border
        {
            Padding = _danmakuPill ? DanmakuPillPadding : DanmakuPlainPadding,
            // ★ 基线不透明度: 压在亮画面上的白字太实反而晃眼, 这里给用户一个可调的档。
            //   滚动弹幕此后不会再被改 Opacity, 所以这里设一次就够;
            //   固定弹幕的淡出是"在这个基线上再乘一个系数"(见 OnDanmakuRendering)。
            //   ★ 底衬画在**同一个 Border** 上 ⇒ 它的透明度跟着这个 Opacity 一起走,
            //     固定弹幕淡出时是"文字与胶囊一起淡", 不会出现"字淡了黑块还杵在那"。
            Opacity = _danmakuOpacity,
            // RenderTransform 在这里预置好, 滚动弹幕直接拿它做位移动画
            RenderTransform = new TranslateTransform(0, 0),
            Child = new TextBlock
            {
                Text = text,
                Foreground = DanmakuBrush(color),
                FontSize = _danmakuFontSize,
                // 跟随全局界面字体(2026-11-14): 弹幕层不跟窗口继承走, 必须显式设(见 _danmakuFont)
                FontFamily = _danmakuFont
            }
        };

        if (_danmakuPill)
        {
            // 胶囊底衬: 半透明黑 + 全圆角。
            // ★ 笔刷**只建一次**(静态冻结): 几千条弹幕各 new 一个笔刷就是几千个对象,
            //   还得各自走一遍 Freeze —— 与 DanmakuBrush 那套缓存是同一个道理。
            border.Background = DanmakuPillBrush;
        }

        DanmakuCanvas.Children.Add(border);
        border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Max(1, border.DesiredSize.Width);

        if (_danmakuPill)
        {
            // ★ 真正的"胶囊": 圆角半径 = 高度的一半 ⇒ 左右两端正好是半圆。
            //   必须**测量之后**才能算 —— 高度取决于字号与内边距, 构造时还不知道。
            //   (CornerRadius 超过高度一半时 WPF 会自动按比例缩, 但先量后算更直白。)
            border.CornerRadius = new CornerRadius(border.DesiredSize.Height / 2.0);
        }

        if (mode == 5 || mode == 4)
        {
            RenderFixed(border, mode, currentSeconds);
        }
        else
        {
            RenderRolling(border, width, currentSeconds);
        }
    }

    // ---- 胶囊底衬的常量(2026-11-05) ----

    /// <summary>
    /// 纯文字模式的内边距(与 2026-10-07 之后的行为**完全一致**, 一个数都没动)。
    /// </summary>
    private static readonly Thickness DanmakuPlainPadding = new(6, 1, 6, 1);

    /// <summary>
    /// 胶囊模式的内边距: 左右比纯文字宽(底衬要包住文字, 留出呼吸位), 上下留出高度。
    ///
    /// ★ 上下内边距是**必须的**, 不是装饰: 胶囊要比纯文字高, 而泳道高要跟着长
    ///   (见 DanmakuMotion.LaneHeight), 否则相邻胶囊会上下压在一起。
    /// </summary>
    private static readonly Thickness DanmakuPillPadding = new(12, 3, 12, 3);

    /// <summary>
    /// 胶囊底衬的笔刷: 55% 黑(与 2026-10-07 之前那条"长方形底衬"的 60% 接近, 但更轻)。
    ///
    /// ★ 冻结 + 复用: 同 <see cref="DanmakuWhiteBrush"/> 的理由(几千条弹幕不该各 new 一个笔刷)。
    /// </summary>
    private static readonly Brush DanmakuPillBrush = MakeFrozen(Color.FromArgb(0x8C, 0, 0, 0));

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
    /// ★ 2026-11-13 起**直播也可用**(接了直播间 WebSocket 弹幕), 只剩本地文件没有弹幕可放
    ///   (离线缓存里没有弹幕文件), 按钮置灰 —— 留一个按了没反应的按钮比没有按钮更糟。
    /// 注意这里给 TextBlock 设的是**局部值** Foreground: 全局那个隐式 TextBlock 样式会压过继承值,
    /// 所以"关"态的灰字必须显式写在元素上(见 MEMORY 里那条隐式样式的坑)。
    /// </summary>
    private void UpdateDanmakuButton()
    {
        if (DanmakuBtnText == null || BtnDanmaku == null) return;

        // ★ 必须是 `_isLive || !_isLocalPlayback`, 不能写成 `!_isLocalPlayback`:
        //   直播入口 PlayLive() 会把 _isLocalPlayback 也置 true(为了跳过互动/上报),
        //   只判它会把刚接好的直播弹幕按钮又置灰。
        var usable = _isLive || !_isLocalPlayback;
        BtnDanmaku.IsEnabled = usable;
        BtnDanmaku.Opacity = usable ? 1.0 : 0.4;

        // ★ 2026-11-14 控制栏图标化: 不再换文案(图标按钮里没有文字位), 状态由**颜色 + 斜线**表达
        //   —— 开 = 接近纯白且无斜线, 关 = 压暗到 0x99 白并划一道左上→右下的斜线。
        //   只压暗颜色在深色玻璃上不够显眼, 那道斜线才是"关了"的主信号(与静音图标同一套语义)。
        //   局部值直接给刷子, 不引资源: 控制栏压在画面上, 颜色要跟主题无关(永远是白系)
        var iconBrush = new SolidColorBrush(_danmakuOn
            ? Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)   // 开: 接近纯白
            : Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)); // 关: 压暗, 一眼看出状态
        DanmakuBtnText.Foreground = iconBrush;
        if (DanmakuOffSlash != null)
        {
            // 斜线沿用图标同一支刷子: 两处颜色永远一致, 不会出现"图标灰了线还是白的"
            DanmakuOffSlash.Stroke = iconBrush;
            DanmakuOffSlash.Visibility = _danmakuOn ? Visibility.Collapsed : Visibility.Visible;
        }
        BtnDanmaku.ToolTip = _danmakuOn ? "弹幕: 开 (D)" : "弹幕: 关 (D)";
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
        if (BtnDanmakuArea != null) BtnDanmakuArea.ToolTip = $"弹幕显示区域 {Svc.Settings.DanmakuAreaPercent}%";
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
        // 智能屏蔽 / 类型过滤都可能刚被改过: 用新规则重算一遍已加载的弹幕,
        // 让用户在设置页改开关时看到播放器里的即时效果。
        RefilterDanmaku();
    }

    /// <summary>
    /// 把「不透明度 / 字号 / 速度 / 胶囊底衬 / 字体」五项设置落到运行时, 并只做**必要**的重算。
    ///
    /// ★ 为什么不一律"改了就清屏重建":
    ///   · 不透明度: 就地改每个在飞弹幕的 Opacity 即可。不清屏 ——
    ///     用户是**拖着滑杆**在调的, 每动一格就闪一次是不能接受的。
    ///   · 字号/速度/胶囊底衬/字体: 会让泳道表与追尾校验失效, 必须重建并清掉旧弹幕,
    ///     否则要么弹幕叠在一起, 要么新弹幕被旧判定整批丢弃(看起来就是"弹幕没了")。
    ///     ★ 胶囊底衬也在这一档: 它的**内边距/宽度/圆角**都变了, 在飞的那些按旧尺寸
    ///       继续飞会与新弹幕重叠(底衬还比原来宽, 追尾校验的宽度也不准了)。
    ///     ★ 字体(2026-11-14)同理: 换字体后字宽变了, 在飞弹幕的实测宽度全部作废。
    ///
    /// 五项都没变时直接返回 —— 设置页里改"关键词屏蔽"也会走到这里, 不该白清一次屏。
    /// </summary>
    private void ApplyDanmakuLook()
    {
        var s = Svc.Settings;
        var nextOpacity = Helpers.DanmakuMotion.OpacityFromPercent(s.DanmakuOpacityPercent);
        var nextFont = Helpers.DanmakuMotion.FontSizeFromPercent(s.DanmakuFontScalePercent, DanmakuBaseFontSize);
        var nextLevel = Math.Clamp(s.DanmakuSpeedLevel,
            Helpers.DanmakuMotion.MinSpeedLevel, Helpers.DanmakuMotion.MaxSpeedLevel);
        var nextPill = s.DanmakuPillBackground;
        var nextFamily = Helpers.UiFont.Current;

        var opacityChanged = Math.Abs(nextOpacity - _danmakuOpacity) > 0.001;
        var fontChanged = Math.Abs(nextFont - _danmakuFontSize) > 0.01;
        var speedChanged = nextLevel != _danmakuSpeedLevel;
        var pillChanged = nextPill != _danmakuPill;
        // UiFont 每次换字体都 new 一个 FontFamily, 引用不同即变化(同值时 SetFromSettings 不会广播)
        var familyChanged = !ReferenceEquals(nextFamily, _danmakuFont);

        // ★★★ 泳道高是"字号 × 胶囊开关"的**派生量**, 所以这里**无条件**同步 ——
        //   放在早退之前是关键: 否则"设置恰好等于默认值"时会早退, 泳道高就停在字段
        //   初始值上(那个初始值按"无胶囊"算的), 于是默认配置(胶囊开)下相邻胶囊
        //   会上下压在一起 —— 正是"字号改了泳道没跟着改"那类历史 bug 的同一形状。
        _laneHeight = Helpers.DanmakuMotion.LaneHeight(nextFont, nextPill);

        if (!opacityChanged && !fontChanged && !speedChanged && !pillChanged && !familyChanged) return;

        _danmakuOpacity = nextOpacity;
        _danmakuFontSize = nextFont;
        _danmakuSpeedLevel = nextLevel;
        _danmakuScrollSeconds = Helpers.DanmakuMotion.ScrollSeconds(nextLevel);
        _danmakuFixedSeconds = Helpers.DanmakuMotion.FixedSeconds(nextLevel);
        _danmakuPill = nextPill;
        _danmakuFont = nextFamily;

        // 不透明度不需要清屏: 基线字段改完, 在飞的弹幕在下一帧自然按新基线渲染
        // (固定弹幕的 Opacity 每帧被"基线 × 淡出系数"覆写, 所以只改基线就够)。

        if (fontChanged || speedChanged || pillChanged || familyChanged)
        {
            // 布局变了, 在飞的那些按旧尺寸/旧速度继续飞会突然错位, 一并清掉
            ResetLanes();
            DanmakuCanvas.Children.Clear();
            _flying.Clear();
            if (_danmakuOn) StartDanmakuClock();
        }
    }

    /// <summary>
    /// 全局界面字体变化(2026-11-14, 来自设置页): 弹幕跟随同一字体。
    /// 窗口本体的字体由 FluentWindow 基类重设; 弹幕层在独立浮层可视树里不跟继承走,
    /// 所以这里显式走一遍外观重算(字宽变了 ⇒ 清屏重建, 见 ApplyDanmakuLook)。
    /// </summary>
    private void OnUiFontChanged()
    {
        if (_closing) return;
        ApplyDanmakuLook();
    }
}
