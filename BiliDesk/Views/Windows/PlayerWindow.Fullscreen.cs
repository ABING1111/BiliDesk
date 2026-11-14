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
    private void ToggleFullscreen()
    {
        if (_closing) return;

        // 进全屏前把常规几何落盘: 进全屏后 Width/Height 会变成全屏尺寸, 读不到可信值了。
        if (!_isFullscreen)
        {
            CaptureNormalPlacement();
            PersistNormalPlacement();
        }

        // ★★★ 必须在改状态**之前**进稳定期(2026-11-14 实测):
        //   退全屏时 WPF 会先发 SizeChanged/LocationChanged(此时 WindowState 已是 Normal、
        //   `_isFullscreen` 也已置 false), 之后才发 StateChanged。若等 OnStateChanged 里才置位,
        //   中间那次事件会把"位置已还原、尺寸还是全屏"的中间态采成常规几何。
        BeginPlacementTransition();

        _isFullscreen = !_isFullscreen;
        ApplyFullscreen(_isFullscreen);
        WritePlacementLog("toggle-fullscreen", $"isFullscreen={_isFullscreen} state={WindowState}");
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
            // ★★★ 上面那两层**都盖不住视频区**:
            //   VideoView 是 HwndHost(airspace), 画在所有 WPF 元素之上, 而"视频区那层
            //   黑底 Grid"是它的**父容器** —— 父容器画不到子 HWND 上面。
            //   视频区内部的白底改由原生宿主窗口治理(见 StartVideoBackdropFix 的调用),
            //   不再往 VideoAreaRoot 上压黑布。

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

    // ------------------------------------------------------------ 尺寸变化的白闪
    //
    // ★★ 2026-11-03: 原先这里有一整套"改窗口大小时把手势层压黑"的遮罩
    //   (ArmFullscreenCover / ArmResizeCover / ArmMaximizeCover + _resizeHold 闸)。
    //   它确实能挡住 LibVLC vout 重协商时的白底, 但代价是拖动/最大化/全屏期间
    //   整个视频区变成一块黑布。
    //   ⇒ 现在改为在原生宿主窗口上根治: 给 Static 加 WS_CLIPCHILDREN + 子类化填黑,
    //     见 Helpers/VideoBackdropInterop.EnsureBlackVideoHost 与
    //     PlayerWindow.ApplyHwndHostBlackBackground。
    //   ★ 不要再把这里的黑布加回来 —— 它挡的只是表象, 而且会让"改大小"看起来像黑屏。
    //   ★ EOF(播完)那一层黑不受影响, 见 OnEndReached / RestoreVideoAreaBackground。

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
        // 与全屏同理: 铺满模式下的几何不记(见 IsNormalStateForPlacement), 切之前先把常规几何存好。
        if (!_isFillWindow)
        {
            CaptureNormalPlacement();
            PersistNormalPlacement();
        }
        // 状态切换前先进稳定期(与 ToggleFullscreen 同一个理由)。
        BeginPlacementTransition();
        _isFillWindow = !_isFillWindow;
        ApplyInfoPanelState();
        UpdateFillWindowButton();
    }

    /// <summary>常规模式(信息栏展开)的窗口最小尺寸 —— 与 XAML 里的 MinWidth/MinHeight 保持一致</summary>
    private const double NormalWindowMinWidth = 840;
    private const double NormalWindowMinHeight = 600;

    /// <summary>
    /// 视频列在常规模式下的最小宽度(与 XAML 的 MinWidth 一致); 信息栏收起后放开到 0。
    /// ★ 480 → 320(2026-11-03, issue #31): 竖屏副屏 864 DIP 要能同时放下"视频 + 400 信息栏"。
    /// </summary>
    private const double VideoColumnNormalMinWidth = 320;

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
        //   常规的 840×600 是按"视频列 320 + 信息栏 400"的布局定的下限(2026-11-03 从
        //   1100/480 降下来, 为了竖屏副屏能放下); 信息栏收起后整个窗口只剩视频,
        //   再拿它当底线没有道理。视频列的 MinWidth 同理 —— 不跟着放开,
        //   窗口缩到 320 以下时列宽比窗口还宽, 画面会被裁偏。
        //   (FluentWindow 的 WM_GETMINMAXINFO 在每次拖动时重读 MinWidth/MinHeight, 改属性即可。)
        if (VideoColumn != null)
            VideoColumn.MinWidth = hide ? 0 : VideoColumnNormalMinWidth;
        MinWidth = hide ? FillWindowMinWidth : NormalWindowMinWidth;
        MinHeight = hide ? FillWindowMinHeight : NormalWindowMinHeight;
    }

    // ------------------------------------------------------------ 播放器窗口大小/位置记忆
    //
    // 2026-11-03 加入尺寸, 2026-11-07 加入位置, 2026-11-14 重做为"快照 + 稳定期"。
    //
    // ★★★ 这次重做修的是什么(用户报: 双击全屏后退出视频, 第二次打开窗口跑到左上角):
    //   旧实现里 `LocationChanged/SizeChanged` 只要 WindowState==Normal 就直接记, 而
    //   "退出全屏/还原最大化"是一个**有系统动画的过程** —— `_isFullscreen` 在窗口状态
    //   真正还原之前就被置 false 了, 动画途中的中间坐标会被当成常规几何记下来; 关闭时
    //   再落盘, 下一次打开就被钉在动画半路(屏幕左上角)。同时 `Window.Width/Height` 在
    //   最大化时会变成**全屏尺寸**(实测 800×600 → 1536×864), 旧代码用它们做兜底,
    //   一旦快照没采到就把全屏尺寸存进设置。
    //
    //   现在: 只在"常规态 + 过了稳定期"采一份完整快照, 切换前后再各兜一次;
    //   落盘只认快照, 兜底用 RestoreBounds(它保留最大化前的常规几何), **绝不再读当前
    //   Width/Height**。

    /// <summary>
    /// 最后一次"常规态"的完整几何(DIP)。全屏/最大化/铺满/切换动画期间不更新。
    /// 关闭与切换时都从这里落盘 —— 不再直接读窗口当前值(原因见本节开头)。
    /// </summary>
    private PlayerWindowPlacement.PlacementRect _normalPlacement;

    /// <summary>快照是否有效(0 尺寸 / NaN 都视为没有)。</summary>
    private bool _hasNormalPlacement;

    /// <summary>
    /// 是否处于"几何不可信"的稳定期。全屏/最大化/铺满切换、二次定位之后置位;
    /// 期间任何 SizeChanged/LocationChanged 都只**续期**、不采几何 —— 那是在系统还原动画里。
    ///
    /// ★ 为什么不能用"一个固定的截止时间"(第一版就是这么写的, 实测不够):
    ///   DWM 的还原动画在慢机器上能拖到 400ms 以上, 而且位置先到位、尺寸后到位。
    ///   固定 300ms 的窗口一过就采, 会把"位置已是常规值、尺寸还是全屏尺寸"的中间态
    ///   存进设置(实测: 退全屏后 440ms 关闭 → 下次打开窗口铺满屏幕)。
    ///   现在是"尾部防抖 + 连续两次采样一致": 只要还在动就续期, 两次读到同一几何才提交;
    ///   关闭时若仍在稳定期, 直接沿用上一份快照 —— 两端口径一致。
    /// </summary>
    private bool _placementSettling;

    /// <summary>稳定期里上一次采到的几何样本: 连续两次一致才认为动画结束(见 OnPlacementSettleTick)。</summary>
    private PlayerWindowPlacement.PlacementRect _placementSettleSample;

    /// <summary>是否已经有上一轮的稳定期样本。</summary>
    private bool _hasPlacementSettleSample;

    /// <summary>稳定期结束后补采一次(不依赖"最后一个事件一定会到")。</summary>
    private DispatcherTimer? _placementSettleTimer;

    /// <summary>本次窗口是否已经保存过几何(OnClosing 可能被调用两次: 先取消、拆解完再真关)。</summary>
    private bool _windowPlacementSaved;

    /// <summary>
    /// 从设置里读到、等待套用的常规几何(DIP)。构造时先按它定位一次(避免居中闪一下),
    /// 窗口实体化之后再套一次 —— 见 <see cref="ReapplyRememberedPlacementAfterRealized"/>。
    /// </summary>
    private PlayerWindowPlacement.PlacementRect? _rememberedPlacement;

    /// <summary>
    /// 稳定期采样间隔。注意: 真正的"稳定"判据是**连续两次采样一致**(见 OnPlacementSettleTick),
    /// 不是"过了这么久就算稳定" —— 慢机器/RDP 上 DWM 的还原动画能拖到 500ms 以上,
    /// 只等一次会把"位置已还原、尺寸还是全屏"的中间态采下来(2026-11-14 实测)。
    /// </summary>
    private const int PlacementSettleMs = 250;

    // 记忆位置不可用时, 保证窗口至少露出的可见区域(标题栏 + 一块可点面积),
    // 参考 Chrome/资源管理器对"屏幕外窗口"的救援标准。
    private const double PlacementVisibleMinWidth = 160;
    private const double PlacementVisibleMinHeight = 120;

    /// <summary>
    /// 套用设置里记住的窗口宽高与位置(设置项关闭时什么都不做, 保持 XAML 默认 1280×760 + CenterScreen)。
    ///
    /// ★ 夹取规则(尺寸): 不能小于窗口最小尺寸(常规态要放得下右侧详情栏), 也不能超过当前
    ///   主屏工作区 —— 用户换到更小的显示器/改过缩放后, 旧的大尺寸会让窗口伸出屏幕外。
    /// ★ 夹取规则(位置, 2026-11-07 / 2026-11-14 抽到 PlayerWindowPlacement):
    ///   按**虚拟桌面**夹, 而不是主屏工作区 —— 多屏布局里负坐标完全合法(副屏在主屏左侧/上方),
    ///   按"必须是正数"夹会误杀; 真正要防的是"上次用的副屏被拔掉"之后整窗落进无人区。
    ///   做法是保证左上角(标题栏)始终留在虚拟桌面内, 且至少 160×120 的窗口面积可见。
    /// ★ 位置必须配合 WindowStartupLocation=Manual: XAML 里是 CenterScreen, 不改成 Manual
    ///   的话 WPF 会用居中结果覆盖手写的 Left/Top。
    /// ★★ 这里只是**初值**: `Show()` 之前写 Left/Top, PerMonitorV2 下 WPF 会按"猜的显示器
    ///   DPI"换算, 125%/150% 缩放下可能把窗口放到偏左上。真正的定位由
    ///   <see cref="ReapplyRememberedPlacementAfterRealized"/> 在窗口实体化后再做一次。
    /// </summary>
    private void ApplyRememberedPlayerWindowPlacement()
    {
        if (!Svc.Settings.RememberPlayerWindowSize) return;
        var w = Svc.Settings.PlayerWindowWidth;
        var h = Svc.Settings.PlayerWindowHeight;
        if (w <= 0 || h <= 0) return;

        var maxW = Math.Max(MinWidth, SystemParameters.WorkArea.Width - 32);
        var maxH = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 32);
        Width = Math.Clamp((double)w, MinWidth, maxW);
        Height = Math.Clamp((double)h, MinHeight, maxH);

        // 没记过位置(老配置/只在旧版用过) ⇒ 保持 XAML 的 CenterScreen 居中, 只还原大小。
        var left = Svc.Settings.PlayerWindowLeft;
        var top = Svc.Settings.PlayerWindowTop;
        if (!left.HasValue || !top.HasValue) return;

        var virtualScreen = new PlayerWindowPlacement.PlacementRect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        var clamped = PlayerWindowPlacement.ClampToBounds(
            new PlayerWindowPlacement.PlacementRect(left.Value, top.Value, Width, Height),
            virtualScreen,
            PlacementVisibleMinWidth, PlacementVisibleMinHeight);

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = clamped.Left;
        Top = clamped.Top;
        _rememberedPlacement = clamped;
        WritePlacementLog("restore(ctor)",
            $"saved=({left.Value},{top.Value},{w},{h}) applied=({clamped.Left},{clamped.Top},{clamped.Width},{clamped.Height})");
    }

    /// <summary>
    /// 窗口实体化(HWND 已建、DPI 已确定)之后，把记住的位置**再套一次**并核对。
    ///
    /// ★ 为什么必须在 Loaded 再做一次(dotnet/wpf#4127): WPF 的 Left/Top 在窗口创建**之前**
    ///   是按"猜的显示器 DPI"换算的; PerMonitorV2 + 125%/150% 缩放下会换算错, 表现就是
    ///   "第一次打开(居中)正常, 第二次打开(按记忆定位)跑到左上角"。窗口实体化后 Left/Top
    ///   走的是真实显示器 DPI, 同一个值就落得准。
    /// ★ 先写回同一目标值, 再用 Win32 `GetWindowRect` 核对物理像素; 偏得明显才 `SetWindowPos`
    ///   校正。2~4px 的差异是 WindowChrome 的边框/圆角换算噪声, 不去抖。
    /// </summary>
    private void ReapplyRememberedPlacementAfterRealized()
    {
        if (!Svc.Settings.RememberPlayerWindowSize) return;
        if (_rememberedPlacement is not { } target) return;

        // 只重写位置: 尺寸已在构造时按屏幕夹过, 这里再写会把小屏上的夹取结果顶回去。
        Left = target.Left;
        Top = target.Top;
        WritePlacementLog("restore(reapply)", $"target=({target.Left},{target.Top}) actual=({Left},{Top})");

        // 物理像素核对放到 Loaded 优先级: 等 WPF 处理完可能的 WM_DPICHANGED。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(VerifyAppliedPlacement));
    }

    /// <summary>核对"套用后的物理位置"是否就是目标位置; 偏差过大时校正一次。</summary>
    private void VerifyAppliedPlacement()
    {
        if (_closing || !IsLoaded) return;
        if (_isFullscreen || _isFillWindow || WindowState != WindowState.Normal) return;

        if (_rememberedPlacement is { } target)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect))
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                var expectedLeft = (int)Math.Round(target.Left * dpi.DpiScaleX);
                var expectedTop = (int)Math.Round(target.Top * dpi.DpiScaleY);
                if (Math.Abs(rect.Left - expectedLeft) > 8 || Math.Abs(rect.Top - expectedTop) > 8)
                {
                    SetWindowPos(hwnd, IntPtr.Zero, expectedLeft, expectedTop, 0, 0,
                        SwpNoSize | SwpNoZOrder | SwpNoActivate);
                    WritePlacementLog("nudge",
                        $"actual=({rect.Left},{rect.Top}) expected=({expectedLeft},{expectedTop}) dpi={dpi.DpiScaleX:0.##}");
                }
            }
        }

        // 副屏被拔掉 / 显示器重新排列后, 记忆点可能落在虚拟桌面的"洞"里:
        // 虚拟桌面矩形包含屏幕之间的空隙, 夹完仍可能不可见。实体化后按真实显示器再兜一次。
        EnsureVisibleOnScreen();
        BeginPlacementTransition();
    }

    /// <summary>
    /// 保证窗口在"离它最近的显示器"上有足够可见面积(至少 160×120 DIP);
    /// 不够就把窗口居中到那块显示器的工作区 —— 这是"副屏被拔掉"的救援路径。
    /// </summary>
    private void EnsureVisibleOnScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
        if (screen == null) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var work = new PlayerWindowPlacement.PlacementRect(
            screen.WorkingArea.Left / dpi.DpiScaleX,
            screen.WorkingArea.Top / dpi.DpiScaleY,
            screen.WorkingArea.Width / dpi.DpiScaleX,
            screen.WorkingArea.Height / dpi.DpiScaleY);
        var actual = new PlayerWindowPlacement.PlacementRect(Left, Top, ActualWidth, ActualHeight);
        if (PlayerWindowPlacement.IntersectionArea(actual, work)
            >= PlacementVisibleMinWidth * PlacementVisibleMinHeight) return;

        var w = Math.Min(actual.Width, work.Width);
        var h = Math.Min(actual.Height, work.Height);
        Width = w;
        Height = h;
        Left = work.Left + (work.Width - w) / 2;
        Top = work.Top + (work.Height - h) / 2;
        WritePlacementLog("ensure-visible", $"centered=({Left},{Top},{w},{h}) work=({work.Left},{work.Top},{work.Width},{work.Height})");
    }

    /// <summary>
    /// 记录最后一次常规几何的唯一入口。最大化/全屏/铺满时 SizeChanged 也会发, 但那时
    /// 记录的是屏幕尺寸, 带进下一次启动就会"一打开就铺满" —— 所以按窗口状态过滤。
    /// ★ 铺满模式(_isFillWindow)也不记: 那个布局里窗口下限是 320×200, 用户拖出来的小窗
    ///   下次按**常规布局**打开时会被 840×600 的最小尺寸顶回去, 记了等于没记。
    /// ★ 位置(LocationChanged)与尺寸(SizeChanged)走同一套过滤, 两个事件都汇到这里,
    ///   保证拖尺寸和拖位置总是成对更新(拖边缘时 WPF 两个事件都会发)。
    /// ★ 稳定期内一律不采: 那是在系统的还原动画里(见本节开头)。
    /// </summary>
    private bool IsNormalStateForPlacement()
    {
        return IsLoaded && !_closing && !_isFullscreen && !_isFillWindow
            && !_placementSettling
            && WindowState == WindowState.Normal
            && ActualWidth >= 100 && ActualHeight >= 100;
    }

    /// <summary>从当前窗口值采一份常规态快照(只有稳定期结束、状态也正常时才采)。</summary>
    private void CaptureNormalPlacement()
    {
        // ★ 稳定期内一律不采: 这时读到的是系统还原动画的中间值(可能"位置已还原、尺寸还是全屏")。
        if (_placementSettling) return;
        if (!IsNormalStateForPlacement()) return;
        var rect = new PlayerWindowPlacement.PlacementRect(Left, Top, ActualWidth, ActualHeight);
        if (!PlayerWindowPlacement.IsUsable(rect, 100, 100)) return;
        _normalPlacement = rect;
        _hasNormalPlacement = true;
    }

    /// <summary>尺寸/位置仍在变化 ⇒ 说明动画没结束, 把稳定期往后推(尾部防抖)。</summary>
    private void OnPlayerSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_placementSettling) { ArmPlacementSettleTimer(); return; }
        CaptureNormalPlacement();
    }

    private void OnPlayerLocationChanged(object? sender, EventArgs e)
    {
        if (_placementSettling) { ArmPlacementSettleTimer(); return; }
        CaptureNormalPlacement();
    }

    /// <summary>
    /// 进入"几何不可信"的稳定期: 全屏/最大化/铺满切换、二次定位之后调用。
    /// 期间每个几何事件都会把定时器往后推; 直到几何真正静止 PlacementSettleMs 后才补采一次。
    /// </summary>
    private void BeginPlacementTransition()
    {
        if (_closing) return;
        _placementSettling = true;
        _hasPlacementSettleSample = false;
        ArmPlacementSettleTimer();
    }

    /// <summary>(重新)启动稳定期定时器 —— 从**现在**算起; 期间每个几何事件都会再续期。</summary>
    private void ArmPlacementSettleTimer()
    {
        if (_placementSettleTimer == null)
        {
            _placementSettleTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(PlacementSettleMs)
            };
            _placementSettleTimer.Tick += OnPlacementSettleTick;
        }
        _placementSettleTimer.Stop();
        _placementSettleTimer.Start();
    }

    /// <summary>
    /// 稳定期到点: 读一次当前几何, 与上一轮样本比较。
    ///   · 两次一致 ⇒ 动画真的结束了, 提交快照并落盘;
    ///   · 还不一致 ⇒ 动画仍在跑(或刚被二次定位挪过), 存下新样本再等一轮。
    /// 这样无论 DWM 动画多长, 都不会把中间态当成常规几何。
    /// </summary>
    private void OnPlacementSettleTick(object? sender, EventArgs e)
    {
        _placementSettleTimer?.Stop();
        if (_closing) return;

        // 全屏/最大化/铺满态没有"常规几何"可采: 退出这些状态时会重新进稳定期。
        if (WindowState != WindowState.Normal || _isFullscreen || _isFillWindow)
        {
            _placementSettling = false;
            _hasPlacementSettleSample = false;
            return;
        }

        var rect = new PlayerWindowPlacement.PlacementRect(Left, Top, ActualWidth, ActualHeight);
        if (!PlayerWindowPlacement.IsUsable(rect, 100, 100))
        {
            _placementSettling = false;
            _hasPlacementSettleSample = false;
            return;
        }

        if (_hasPlacementSettleSample && NearlyEqual(_placementSettleSample, rect))
        {
            _placementSettling = false;
            _hasPlacementSettleSample = false;
            _normalPlacement = rect;
            _hasNormalPlacement = true;
            PersistNormalPlacement();
            WritePlacementLog("settled",
                $"=({rect.Left:0.##},{rect.Top:0.##},{rect.Width:0.##},{rect.Height:0.##})");
            return;
        }

        _placementSettleSample = rect;
        _hasPlacementSettleSample = true;
        ArmPlacementSettleTimer();
    }

    /// <summary>两次稳定期采样是否可视为同一个几何(小于 0.5 DIP 的抖动算噪声)。</summary>
    private static bool NearlyEqual(PlayerWindowPlacement.PlacementRect a, PlayerWindowPlacement.PlacementRect b)
    {
        const double tolerance = 0.5;
        return Math.Abs(a.Left - b.Left) < tolerance
            && Math.Abs(a.Top - b.Top) < tolerance
            && Math.Abs(a.Width - b.Width) < tolerance
            && Math.Abs(a.Height - b.Height) < tolerance;
    }

    /// <summary>把快照写进设置(不改变"本次窗口是否已保存"的状态, 供切换/拖动结束时复用)。</summary>
    private void PersistNormalPlacement()
    {
        if (!_hasNormalPlacement || !Svc.Settings.RememberPlayerWindowSize) return;
        Svc.Settings.SetPlayerWindowPlacement(
            _normalPlacement.Width, _normalPlacement.Height,
            _normalPlacement.Left, _normalPlacement.Top);
    }

    /// <summary>
    /// 关闭时保存窗口几何(设置项关掉时直接跳过, 也不清掉旧值 —— 重新打开还能沿用)。
    /// ★ 在 OnClosing **最前面**调用: 后面会把全屏态还原成 Normal, 那时再采几何就不准了。
    /// ★ 只认快照: 从全屏/最大化直接关闭时, 当前 Width/Height 是**全屏尺寸**, 绝不能存。
    /// </summary>
    private void SavePlayerWindowPlacement()
    {
        if (_windowPlacementSaved) return;
        _windowPlacementSaved = true;
        if (!Svc.Settings.RememberPlayerWindowSize) return;

        // 常规态关闭: 先按当前值补采一次(覆盖"程序改了几何但没发事件"的边角情况)。
        CaptureNormalPlacement();

        // 快照无效(整场都在全屏/最大化里, 或首帧还没采到) ⇒ 用 RestoreBounds:
        // 它保留最大化/全屏之前的常规矩形; 仍不可用就放弃本次保存, 沿用旧值。
        if (!_hasNormalPlacement)
        {
            var rb = RestoreBounds;
            var fallback = new PlayerWindowPlacement.PlacementRect(rb.Left, rb.Top, rb.Width, rb.Height);
            if (PlayerWindowPlacement.IsUsable(fallback,
                    SettingsStore.PlayerWindowMinWidth, SettingsStore.PlayerWindowMinHeight))
            {
                _normalPlacement = fallback;
                _hasNormalPlacement = true;
            }
        }

        if (!_hasNormalPlacement)
        {
            WritePlacementLog("save(skip)", $"state={WindowState} fullscreen={_isFullscreen} fill={_isFillWindow}");
            return;
        }

        PersistNormalPlacement();
        WritePlacementLog("save",
            $"=({_normalPlacement.Left},{_normalPlacement.Top},{_normalPlacement.Width},{_normalPlacement.Height}) state={WindowState} fullscreen={_isFullscreen} settling={_placementSettling}");
    }

    /// <summary>几何记忆的诊断日志(只在 DEBUG 构建输出, 发布版零开销)。</summary>
    [Conditional("DEBUG")]
    private void WritePlacementLog(string tag, string detail)
    {
        WriteLibVLCDebug($"[Placement] {tag} {detail}\n");
    }

    // ------------------------------------------------------------ 几何记忆的 Win32 兜底

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after,
        int x, int y, int cx, int cy, uint flags);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>
    /// 窗口状态变化(正常 ↔ 最大化 ↔ 最小化)。
    ///
    /// ★ 标题栏双击/窗口按钮最大化**不经过** ToggleFullscreen, 所以快照要在这里补:
    ///   最大化时 `RestoreBounds` 仍是最大化前的常规矩形, 是这条路径下最可靠的来源。
    /// ★ 全屏(`_isFullscreen`)不在这里采: 进全屏前已经在 ToggleFullscreen 里存过常规几何,
    ///   而全屏本身也是"最大化", 再采会把全屏尺寸记进去。
    /// ★ 任何状态变化之后都进稳定期 —— 系统还原动画期间的坐标不能采。
    /// </summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (!IsLoaded || _closing) return;

        WritePlacementLog("state", $"={WindowState} fullscreen={_isFullscreen} fill={_isFillWindow}");

        if (WindowState == WindowState.Maximized && !_isFullscreen)
        {
            var rb = RestoreBounds;
            var rect = new PlayerWindowPlacement.PlacementRect(rb.Left, rb.Top, rb.Width, rb.Height);
            if (PlayerWindowPlacement.IsUsable(rect,
                    SettingsStore.PlayerWindowMinWidth, SettingsStore.PlayerWindowMinHeight))
            {
                _normalPlacement = rect;
                _hasNormalPlacement = true;
                PersistNormalPlacement();
                WritePlacementLog("maximize-snapshot",
                    $"=({rect.Left},{rect.Top},{rect.Width},{rect.Height})");
            }
        }

        BeginPlacementTransition();
    }

    /// <summary>
    /// 标题栏双击/窗口按钮/系统菜单即将最大化或还原。
    /// ★ 只是**提前**进入稳定期(赶在 WPF 的 SizeChanged 之前), 不改任何状态。
    /// </summary>
    protected override void OnWindowStateTransitionStarting()
    {
        if (_closing || !IsLoaded) return;
        BeginPlacementTransition();
    }

    /// <summary>
    /// 用户刚结束一次"拖窗口 / 拖边缘"(WM_EXITSIZEMOVE)。
    ///
    /// ★ 这是常规几何最可靠的时刻: 模态循环结束, 窗口已经停在最终位置。
    ///   在这里立刻采一次并落盘, 进程就算随后被杀也不会丢记忆。
    /// ★ 若刚好还在稳定期(例如刚退全屏就马上拖), 交给稳定期结束后的补采。
    /// </summary>
    protected override void OnModalMoveResizeEnded()
    {
        base.OnModalMoveResizeEnded();
        if (_closing || !IsLoaded) return;

        if (IsNormalStateForPlacement())
        {
            CaptureNormalPlacement();
            PersistNormalPlacement();
        }
        else
        {
            BeginPlacementTransition();
        }
    }

    // ------------------------------------------------------------ 窄视频区控制栏

    /// <summary>
    /// 视频区窄于这个宽度时, 控制栏先收起"弹幕区域"和音量滑杆这两个次要项。
    ///
    /// ★ 背景(issue #31): 竖屏副屏 864 DIP, 右栏占 400 后视频只剩 464 DIP;
    ///   而操作行的固定项(播放/时间/音量/倍速/清晰度/弹幕/全屏)加起来约 530 DIP,
    ///   不收起就会挤成一团甚至被裁掉。只收这两项后约 390 DIP, 常规窄窗仍可用。
    /// ★ 用 VideoAreaRoot 的实际宽度判断而不是窗口宽度: 信息栏一收/一展、
    ///   窗口一拖, 视频列宽度都会变, 这个信号最直接。
    /// </summary>
    private const double CompactControlBarWidth = 560;

    private void OnVideoAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (BtnDanmakuArea == null || VolumeSlider == null) return;
        var compact = e.NewSize.Width > 0 && e.NewSize.Width < CompactControlBarWidth;
        BtnDanmakuArea.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        VolumeSlider.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
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

}
