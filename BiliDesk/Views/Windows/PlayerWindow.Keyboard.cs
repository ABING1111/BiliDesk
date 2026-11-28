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

        // ★ 2026-11-28: 快捷键改为可配置(设置 → 快捷键)。这里不再按 e.Key 写死 switch,
        //   而是交给 ShortcutService 匹配当前生效映射。
        //   · 精确匹配修饰键 ⇒ 默认绑定里没有 Ctrl, Ctrl+C/V/X/A/Z 继续放行给文本复制
        //     (2026-11-16 修过的那个坑不能因为可配置而复发);
        //   · Alt+←/→ 是"上一个/下一个视频"两条独立绑定, 不再需要上面那段 Alt 特判;
        //   · RealKey() 仍负责 IME(Key.ImeProcessed)与 Alt(Key.System)的还原。
        var key = ShortcutService.RealKey(e);
        var action = Svc.Shortcuts.Match(key, Keyboard.Modifiers);
        if (action == null) return;

        // ★ 同一个**物理键**可能带着不同修饰键绑给了别的动作(例如 Alt+→ = 下一个视频,
        //   而裸 → = 快进)。这类 KeyDown 一来就把"快进键还按着"作废 —— 否则随后的 KeyUp
        //   会把它当成短按, 视频莫名多跳 5 秒。
        if (action != ShortcutAction.SeekForward &&
            key != Key.None &&
            key == ShortcutService.KeyOf(Svc.Shortcuts.GestureOf(ShortcutAction.SeekForward)))
        {
            _forwardKeyArmed = false;
            _forwardKeyTimer?.Stop();
        }

        if (ExecuteShortcut(action.Value, e)) e.Handled = true;
    }

    /// <summary>
    /// 执行一个快捷键动作。返回 true = 已处理(调用方设 e.Handled)。
    /// ★ 这里只放"动作本身"; 匹配、输入框放行、IME/Alt 还原都在上面与 ShortcutService 里。
    /// </summary>
    private bool ExecuteShortcut(ShortcutAction action, KeyEventArgs e)
    {
        switch (action)
        {
            // ★ 直播的暂停只留控制栏那颗按钮(2026-11-20, issue #98): 空格/点画面太容易误触,
            //   而直播暂停是低频操作。这里只是不放行**键盘**入口, 按钮本身仍走 OnPlayPauseClick。
            case ShortcutAction.PlayPause:
                if (!_isLive) OnPlayPauseClick(this, new RoutedEventArgs());
                return true;

            // ★ 长视频快进体验: 普通方向键 ±5s, **Shift+方向键 ±30s** —— 长视频里 5s 一跳
            //   要按十几下才够用, Shift 档一下抵六下。走同一条 SeekRelative, 所有 seek 防护
            //   (看门狗/静音/进度钉制)自动继承。
            case ShortcutAction.SeekBackward:
                SeekRelative(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -30 : -5);
                return true;

            // ★ 长按 → 是倍速播放(与 B 站一致), 不是连续快进: 短按在松开时跳 5 秒
            //   (Shift 30 秒), 继续按住由计时器进入倍速播放, 松开恢复原速。
            //   系统的自动重复 KeyDown(e.IsRepeat)一律忽略 —— 否则又会变成连续快进。
            case ShortcutAction.SeekForward:
                if (e.IsRepeat) return true;
                _forwardKeyArmed = true;
                OnForwardKeyPressed();
                return true;

            // 音量: ↑↓ 走统一入口(顺带弹音量提示), M 在静音与 80 之间切换
            // 基数用 _volumeCache: aout 未就绪时 _mp.Volume 原生读回是 -1/旧值(见 _volumeCache 的说明)
            // ★ 2026-11-21: 长按倍速期间 ↑↓ 改为"切换 1.5x / 2x / 3x", 不再调音量 ——
            //   这是用户明确要求处理的快捷键争用。判据是 _isFastForwarding(手势态),
            //   不是"OSD 可见": 淡出动画期间 OSD 还亮着, 那时按 ↑↓ 必须已经回到音量。
            case ShortcutAction.VolumeUp:
                if (TryStepFastForwardSpeed(+1)) return true;
                SetVolume(_volumeCache + VolumeStep);
                return true;

            case ShortcutAction.VolumeDown:
                if (TryStepFastForwardSpeed(-1)) return true;
                SetVolume(_volumeCache - VolumeStep);
                return true;

            case ShortcutAction.Mute:
                SetVolume(_volumeCache == 0 ? 80 : 0);
                return true;

            case ShortcutAction.Fullscreen:
                ToggleFullscreen();
                return true;

            case ShortcutAction.Like:
                OnLikeClick(this, new RoutedEventArgs());
                return true;

            case ShortcutAction.OpenUpSpace:
                OnUpPageClick(this, new RoutedEventArgs());
                return true;

            case ShortcutAction.ToggleDanmaku:
                ToggleDanmaku();
                return true;

            // 字幕开关(2026-11-15): 与 B 站 web 端一致用 C。关→开走"上次语言/自动选轨";
            // 开→关只关显示(轨道数据留着, 再按一下 C 就回来)。
            case ShortcutAction.ToggleSubtitle:
                ToggleSubtitleByKey();
                return true;

            case ShortcutAction.Back:
                HandleBackRequest();
                return true;

            case ShortcutAction.PreviousVideo:
                _ = GoHistoryAsync(back: true);
                return true;

            case ShortcutAction.NextVideo:
                _ = GoHistoryAsync(back: false);
                return true;

            // 「刷新当前页面」只属于主界面(MainWindow.OnRefreshKey); 播放器不处理。
            default:
                return false;
        }
    }

    /// <summary>
    /// "返回上一层"的统一处理(键盘 Esc / 鼠标侧键共用)。
    ///
    /// 五层语义, **由内到外**逐级退, 与所有播放器一致:
    ///   评论详情中 → 先回评论列表(用户点进楼中楼后, "返回"指的是这一层);
    ///   全屏中     → 先退全屏(而不是直接关窗口, 否则用户按一下就"连界面一起没了");
    ///   小窗中     → 再还原成常规窗口(2026-11-25, issue #127: 小窗里没有顶栏, 「还原」除了
    ///                小窗栏那颗按钮就只有 Esc —— 少了这一级, 在小窗里按 Esc 会直接关播放器);
    ///   已铺满窗口 → 再退铺满窗口(信息栏回来);
    ///   都没有     → 关闭播放器回到主界面。
    /// ★ 铺满窗口必须夹在后面: 它是最"外层"的一个显示模式(窗口尺寸都没变), 用户按 Esc 的
    ///   预期是"先把我刚展开的这个视图收回去", 而不是"直接关掉播放器"。
    ///   少了这一级, 用户在铺满窗口状态下按 Esc 会直接关窗 —— 与真全屏下按 Esc 的行为不一致。
    /// 两个入口必须走同一个方法, 不然很容易出现"Esc 是对的、侧键直接关窗"这种不一致。
    /// </summary>
    private void HandleBackRequest()
    {
        // ★ 2026-11-20(issue #98): 评论详情是**最内层**. 旧实现不知道它开着, 在楼中楼里按侧键
        //   会直接 Close() —— 用户看到的是"播放器整个没了, 回到主界面", 而光标下方随即变成
        //   后面的其它窗口(用户报的还是 Firefox 跟着后退)。Esc 与侧键共用本方法, 一处修两边。
        if (IsCommentDetailOpen)
        {
            CloseCommentDetail();
            return;
        }
        if (_isFullscreen)
        {
            // ★ 先进稳定期再退: WPF 的 SizeChanged/LocationChanged 会先于 StateChanged 到达,
            //   晚一步置位就会把还原动画的中间态采下来(见 ToggleFullscreen 的说明)。
            BeginPlacementTransition();
            _isFullscreen = false;
            ApplyFullscreen(false);
            return;
        }
        if (_isMiniWindow)
        {
            // 小窗没有系统还原动画, 但尺寸剧变后的稳定期照旧要进(由 ExitMiniWindow 自己续上)
            ExitMiniWindow();
            return;
        }
        // ★ 详情栏展开(2026-11-28): 它排在「铺满窗口」**之前** —— 两者是同一个位置的两个方向,
        //   但展开态下窗口里只剩详情栏(视频整个被盖住), 用户的"返回"预期是先回到视频。
        if (_isInfoExpand)
        {
            BeginPlacementTransition();
            _isInfoExpand = false;
            ApplyInfoPanelState();
            UpdateInfoExpandButton();
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

    // ------------------------------------------------------------ 快进键长按 = 倍速播放

    /// <summary>长按"快进"键的判定阈值(ms): 超过就进入倍速播放, 而不是连续快进。</summary>
    private const int ForwardKeyLongPressMs = 250;

    private DispatcherTimer? _forwardKeyTimer;

    /// <summary>当前是否处于"键盘长按快进"触发的加速态 —— KeyUp 只停自己这一条, 不动鼠标长按的快进。</summary>
    private bool _forwardKeyFast;

    /// <summary>
    /// 这一次"快进键"的 KeyDown 是否仍归快进行为管(2026-11-28 快捷键可配置后新增)。
    ///
    /// ★ 为什么不能只看 KeyUp 的物理键: 同一个键可以带着不同修饰键绑给别的动作
    ///   (默认就有 Alt+→ = 下一个视频)。如果用户先按 Alt 再按 →, 这次 KeyDown 走的是
    ///   "下一个视频"; 松手时若照旧把 → 当短按处理, 视频会莫名再快进 5 秒。
    /// ★ 失焦/换片时也把它清掉: 配对的那次松手可能永远不会回来, 回来时也不该再生效。
    /// </summary>
    private bool _forwardKeyArmed;

    /// <summary>
    /// 快进键按下(非自动重复):
    ///   短按(不到 <see cref="ForwardKeyLongPressMs"/> 毫秒) = 松开时跳 5 秒(Shift 30 秒), 保持原来的即时手感;
    ///   继续按住超过阈值 = 进入倍速播放, 松开恢复按下前的倍速。
    ///   长按期间 ↑/↓ 在 1.5x / 2x / 3x 之间切换(<see cref="TryStepFastForwardSpeed"/>)。
    /// ★ 2026-11-28(issue #131): 起步档不再是写死的 2x, 而是上次长按记住的档位
    ///   (见 StartFastForward 与 SettingsStore.FastForwardRate)。鼠标长按与键盘长按共用同一个记忆值。
    /// </summary>
    private void OnForwardKeyPressed()
    {
        // ★ 按下时**不跳**: 用户实测"长按会先触发一次快进" —— 长按应当只加速、不改变播放位置。
        //   短按的跳放在 KeyUp 里(没到长按阈值就松开 = 短按), 按下与松开之间的延迟只有按住的那一小段。
        _forwardKeyTimer?.Stop();
        if (_closing || _isLive || _ended) return;

        _forwardKeyTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ForwardKeyLongPressMs) };
        _forwardKeyTimer.Tick -= OnForwardKeyLongPressTick;
        _forwardKeyTimer.Tick += OnForwardKeyLongPressTick;
        _forwardKeyTimer.Start();
    }

    /// <summary>按住快进键超过阈值: 进入倍速播放(复用鼠标长按那条 StartFastForward, 只是入口不同)。</summary>
    private void OnForwardKeyLongPressTick(object? sender, EventArgs e)
    {
        _forwardKeyTimer?.Stop();
        if (_closing || _mp == null || _isLive || _ended) return;
        if (_isFastForwarding) return;   // 鼠标长按快进中, 不叠加

        _forwardKeyFast = true;
        // ★ 不传 rate: 用记住的档位(见 StartFastForward)。键盘长按与鼠标长按必须是同一个入口。
        StartFastForward();
        ShowSpeedHint("倍速播放中", hold: true);
    }

    /// <summary>快进键松开: 若是键盘长按触发的倍速, 恢复按下前的速率; 否则按短处理跳 5/30 秒。</summary>
    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        // ★ 必须与 OnPreviewKeyDown 用同一条输入框判据: 焦点在可编辑 TextBox 里时,
        //   KeyDown 被放行(不启计时器), 而 KeyUp 若照常走到 "短按 → 跳 5 秒",
        //   就会变成"在弹幕/评论输入框里按 → 视频往前跳" —— 按住方向键移光标更是一路狂跳。
        if (Keyboard.FocusedElement is TextBox { IsReadOnly: false }) return;

        // ★ 键位可配置: 这里比较"当前绑定的快进键", 而不是写死的 Key.Right。
        var forwardKey = ShortcutService.KeyOf(Svc.Shortcuts.GestureOf(ShortcutAction.SeekForward));
        if (forwardKey == Key.None || ShortcutService.RealKey(e) != forwardKey) return;

        _forwardKeyTimer?.Stop();

        // 这一次按下没有进入快进语义(被 Alt+→ 之类的组合抢走 / 失焦作废 / 换片复位):
        // 松手只清标记, 不要退化成"短按跳 5 秒"。
        if (!_forwardKeyArmed) return;
        _forwardKeyArmed = false;

        if (_forwardKeyFast)
        {
            // 长按结束: 恢复按下前的速率
            _forwardKeyFast = false;
            StopFastForward();
            ShowSpeedHint(RestoredSpeedText(), hold: false);
            e.Handled = true;
            return;
        }

        // 没到长按阈值就松开 = 短按: 现在才跳, 长按不会先跳一次
        SeekRelative(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 30 : 5);
        e.Handled = true;
    }

    /// <summary>
    /// 窗口失去激活(Alt+Tab / 点了别的窗口): 结束所有"按住才成立"的加速态。
    /// ★ 按住快进键时切走窗口, KeyUp 会送到新窗口, 播放器永远收不到 —— 不在这里兜底,
    ///   切回来画面会一直停在 3x("3.0x 快进中"不消失)。鼠标长按快进同理(Up 丢失)。
    /// ★★★ 必须延迟一拍再判定: 点视频区/控制栏时焦点会交给播放器自己的**覆盖层窗口**
    ///   (见 HookOverlayKeyboard), 本窗口同样会收到 Deactivated —— 若在这里同步收尾,
    ///   用户刚按下起步的长按快进、长按三连进度环会被立刻掐掉。
    ///   延迟后检查"是否还在播放器体系内"(本窗口或覆盖层窗口处于激活), 都不在才算真离开。
    /// ★ 2026-11-21: 收尾必须走 <see cref="CancelFastForward"/> 而不是 StopFastForward ——
    ///   只恢复速率的话, hold:true 显示的倍速浮层(没有自动淡出计时器)会一直挂着,
    ///   切回来看到「倍速播放中 + 三档选择器」而实际已是原速(用户复现的失焦 case)。
    /// </summary>
    private void OnPlayerDeactivated(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing) return;
            if (IsActive) return;                                  // 又切回来了
            if (_overlayKeyboardWindow?.IsActive == true) return;  // 焦点在视频区覆盖层上, 没离开播放器
            // 这一次"按住手势"整体作废: 计时器停掉、标记清掉。
            // ★ 不清的话, 切回来时那个 Up 会被当成"配对成功的松手" —— 不但弹一次过期的
            //   「已恢复」, 若 500ms 计时器还没到点, 它还会在后台起来切一次倍速并重新亮浮层。
            _longPressTimer?.Stop();
            _longPressFired = false;
            _videoGestureActive = false;
            // 激活点击补偿的轮询也要停: 它同样会在失焦期间把长按倍速点起来(见 OnOverlayActivated)
            StopActivationClickCompensation();
            _forwardKeyTimer?.Stop();
            _forwardKeyFast = false;
            // 按键可能还按着: 这一次按下整体作废, 回来松手不再跳 5 秒
            _forwardKeyArmed = false;
            CancelFastForward();
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
    ///   原生宿主窗口治理(见 VideoBackdropInterop.EnsureLetterboxVideoHost), 所以下面只保留
    ///   "窗口底色先黑"与 `StartVideoBackdropFix()` 两条 —— 前者挡 WPF 缝隙, 后者刷原生底色。
    ///
    /// 窗口现在**始终**是 WindowStyle=None + WindowChrome 的自绘标题栏, 所以全屏不再靠
    /// 切换 WindowStyle 实现(之前那套 `None + Maximized` 会让自绘标题栏和系统框打架),
    /// 而是: 最大化 + 把顶栏那一行收成 0 高 + 藏起右侧信息栏。
    /// </summary>
}
