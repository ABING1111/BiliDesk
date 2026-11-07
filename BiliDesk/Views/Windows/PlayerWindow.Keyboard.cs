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
            case Key.Right: SeekRelative(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 30 : 5); e.Handled = true; return;
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
    /// ★ 2026-11-03: 全屏不再压"全屏黑幕"。LibVLC 重新协商 vout 时的白底空窗改由
    ///   原生宿主窗口治理(见 VideoBackdropInterop.EnsureBlackVideoHost), 所以下面只保留
    ///   "窗口底色先黑"与 `StartVideoBackdropFix()` 两条 —— 前者挡 WPF 缝隙, 后者刷原生底色。
    ///
    /// 窗口现在**始终**是 WindowStyle=None + WindowChrome 的自绘标题栏, 所以全屏不再靠
    /// 切换 WindowStyle 实现(之前那套 `None + Maximized` 会让自绘标题栏和系统框打架),
    /// 而是: 最大化 + 把顶栏那一行收成 0 高 + 藏起右侧信息栏。
    /// </summary>
}
