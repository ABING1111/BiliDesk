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

    // ------------------------------------------------------------ 键盘

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 只在焦点位于"可编辑"输入框时让按键走默认路径; 只读展示框不拦截空格
        if (Keyboard.FocusedElement is TextBox { IsReadOnly: false }) return;
        switch (e.Key)
        {
            case Key.Space: OnPlayPauseClick(this, new RoutedEventArgs()); e.Handled = true; return;
            // ★ 2026-10-31 长视频快进体验: 普通方向键 ±5s(原样), **Shift+方向键 ±30s** ——
            //   长视频里 5s 一跳要按十几下才够用, Shift 档一下抵六下。走同一条 SeekRelative,
            //   所有 seek 防护(看门狗/静音/进度钉制)自动继承。
            case Key.Left: SeekRelative(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -30 : -5); e.Handled = true; return;
            case Key.Right:
                // ★ 2026-11-12 用户要求: 长按 → 应该是 3 倍速播放(与 B 站一致), 不是连续快进。
                //   短按仍然立即跳 5 秒(Shift 30 秒); 继续按住由计时器切到 3x, 松开恢复。
                //   系统的自动重复 KeyDown(e.IsRepeat)一律忽略 —— 否则又会变成连续快进。
                if (e.IsRepeat) { e.Handled = true; return; }
                OnRightKeyPressed();
                e.Handled = true;
                return;
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
            // ★ 先进稳定期再退: WPF 的 SizeChanged/LocationChanged 会先于 StateChanged 到达,
            //   晚一步置位就会把还原动画的中间态采下来(见 ToggleFullscreen 的说明)。
            BeginPlacementTransition();
            _isFullscreen = false;
            ApplyFullscreen(false);
            return;
        }
        if (_isFillWindow)
        {
            BeginPlacementTransition();
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

    // ------------------------------------------------------------ 方向键 → 长按 = 3 倍速播放

    /// <summary>长按 → 的判定阈值(ms): 超过就进入 3 倍速播放, 而不是连续快进。</summary>
    private const int RightKeyLongPressMs = 250;

    /// <summary>方向键长按进入的倍速(与 B 站一致)。</summary>
    private const float RightKeyFastRate = 3.0f;

    private DispatcherTimer? _rightKeyTimer;

    /// <summary>当前是否处于"键盘长按 →"触发的加速态 —— KeyUp 只停自己这一条, 不动鼠标长按的快进。</summary>
    private bool _rightKeyFast;

    /// <summary>
    /// → 键按下(非自动重复):
    ///   短按(不到 <see cref="RightKeyLongPressMs"/> 毫秒) = 松开时跳 5 秒(Shift 30 秒), 保持原来的即时手感;
    ///   继续按住超过阈值 = 切到 3 倍速播放(与 B 站一致), 松开恢复按下前的倍速。
    /// </summary>
    private void OnRightKeyPressed()
    {
        // ★ 按下时**不跳**: 用户实测"长按会先触发一次快进" —— 长按应当只加速、不改变播放位置。
        //   短按的跳放在 KeyUp 里(没到长按阈值就松开 = 短按), 按下与松开之间的延迟只有按住的那一小段。
        _rightKeyTimer?.Stop();
        if (_closing || _isLive || _ended) return;

        _rightKeyTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RightKeyLongPressMs) };
        _rightKeyTimer.Tick -= OnRightKeyLongPressTick;
        _rightKeyTimer.Tick += OnRightKeyLongPressTick;
        _rightKeyTimer.Start();
    }

    /// <summary>按住 → 超过阈值: 切到 3 倍速播放(复用鼠标长按那条 StartFastForward, 只是倍速不同)。</summary>
    private void OnRightKeyLongPressTick(object? sender, EventArgs e)
    {
        _rightKeyTimer?.Stop();
        if (_closing || _mp == null || _isLive || _ended) return;
        if (_isFastForwarding) return;   // 鼠标长按快进中, 不叠加

        _rightKeyFast = true;
        StartFastForward(RightKeyFastRate);
        ShowSpeedHint(FormatRate(_fastForwardTarget) + " 快进中", hold: true);
    }

    /// <summary>→ 键松开: 若是键盘长按触发的 3x, 恢复按下前的速率。</summary>
    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        // ★ 必须与 OnPreviewKeyDown 用同一条输入框判据: 焦点在可编辑 TextBox 里时,
        //   KeyDown 被放行(不启计时器), 而 KeyUp 若照常走到 "短按 → 跳 5 秒",
        //   就会变成"在弹幕/评论输入框里按 → 视频往前跳" —— 按住方向键移光标更是一路狂跳。
        if (Keyboard.FocusedElement is TextBox { IsReadOnly: false }) return;
        if (e.Key != Key.Right) return;
        _rightKeyTimer?.Stop();

        if (_rightKeyFast)
        {
            // 长按结束: 恢复按下前的速率
            _rightKeyFast = false;
            StopFastForward();
            ShowSpeedHint("已结束快进", hold: false);
            e.Handled = true;
            return;
        }

        // 没到长按阈值就松开 = 短按: 现在才跳, 长按不会先跳一次
        SeekRelative(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 30 : 5);
        e.Handled = true;
    }

    /// <summary>
    /// 窗口失去激活(Alt+Tab / 点了别的窗口): 结束所有"按住才成立"的加速态。
    /// ★ 按住 → 时切走窗口, KeyUp 会送到新窗口, 播放器永远收不到 —— 不在这里兜底,
    ///   切回来画面会一直停在 3x("3.0x 快进中"不消失)。鼠标长按快进同理(Up 丢失)。
    /// ★★★ 必须延迟一拍再判定: 点视频区/控制栏时焦点会交给播放器自己的**覆盖层窗口**
    ///   (见 HookOverlayKeyboard), 本窗口同样会收到 Deactivated —— 若在这里同步收尾,
    ///   用户刚按下起步的长按快进、长按三连进度环会被立刻掐掉。
    ///   延迟后检查"是否还在播放器体系内"(本窗口或覆盖层窗口处于激活), 都不在才算真离开。
    /// </summary>
    private void OnPlayerDeactivated(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing) return;
            if (IsActive) return;                                  // 又切回来了
            if (_overlayKeyboardWindow?.IsActive == true) return;  // 焦点在视频区覆盖层上, 没离开播放器
            _rightKeyTimer?.Stop();
            _rightKeyFast = false;
            StopFastForward();
        }), DispatcherPriority.Input);
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
    /// ★ 2026-11-03: 全屏不再压"全屏黑幕"。LibVLC 重新协商 vout 时的白底空窗改由
    ///   原生宿主窗口治理(见 VideoBackdropInterop.EnsureBlackVideoHost), 所以下面只保留
    ///   "窗口底色先黑"与 `StartVideoBackdropFix()` 两条 —— 前者挡 WPF 缝隙, 后者刷原生底色。
    ///
    /// 窗口现在**始终**是 WindowStyle=None + WindowChrome 的自绘标题栏, 所以全屏不再靠
    /// 切换 WindowStyle 实现(之前那套 `None + Maximized` 会让自绘标题栏和系统框打架),
    /// 而是: 最大化 + 把顶栏那一行收成 0 高 + 藏起右侧信息栏。
    /// </summary>
}
