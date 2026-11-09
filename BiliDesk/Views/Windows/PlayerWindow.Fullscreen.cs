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

        _isFullscreen = !_isFullscreen;
        ApplyFullscreen(_isFullscreen);
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

    // ------------------------------------------------------------ 播放器窗口大小/位置记忆(2026-11-03, 位置 2026-11-07)

    /// <summary>最后一次"常规态"的窗口尺寸(全屏/最大化/铺满期间不更新)。关闭时从这里落盘。</summary>
    private double _lastNormalWidth;
    private double _lastNormalHeight;

    /// <summary>最后一次"常规态"的窗口位置(DIP; 与尺寸同一套过滤条件)。关闭时从这里落盘。</summary>
    private double _lastNormalLeft;
    private double _lastNormalTop;

    /// <summary>_lastNormalLeft/Top 是否采到了真实值。
    /// ★ 不能拿 0 当哨兵: 多屏布局里 0 是合法坐标; 也不能直接读 Left ——
    ///   WindowStartupLocation=CenterScreen 的窗口只要用户没动过, Left/Top 一直是 NaN。</summary>
    private bool _lastNormalPosValid;

    /// <summary>本次窗口是否已经保存过几何(OnClosing 可能被调用两次: 先取消、拆解完再真关)。</summary>
    private bool _windowPlacementSaved;

    // 记忆位置不可用时, 保证窗口至少露出的可见区域(标题栏 + 一块可点面积),
    // 参考 Chrome/资源管理器对"屏幕外窗口"的救援标准。
    private const double PlacementVisibleMinWidth = 160;
    private const double PlacementVisibleMinHeight = 120;

    /// <summary>
    /// 套用设置里记住的窗口宽高与位置(设置项关闭时什么都不做, 保持 XAML 默认 1280×760 + CenterScreen)。
    ///
    /// ★ 夹取规则(尺寸): 不能小于窗口最小尺寸(常规态要放得下右侧详情栏), 也不能超过当前
    ///   主屏工作区 —— 用户换到更小的显示器/改过缩放后, 旧的大尺寸会让窗口伸出屏幕外。
    /// ★ 夹取规则(位置, 2026-11-07): 按**虚拟桌面**夹, 而不是主屏工作区 ——
    ///   多屏布局里负坐标完全合法(副屏在主屏左侧/上方), 按"必须是正数"夹会误杀;
    ///   真正要防的是"上次用的副屏被拔掉"之后整窗落进无人区。做法是保证左上角(标题栏)
    ///   始终留在虚拟桌面内, 且至少 160×120 的窗口面积可见, 位置尽量不变。
    /// ★ 位置必须配合 WindowStartupLocation=Manual: XAML 里是 CenterScreen, 不改成 Manual
    ///   的话 WPF 会用居中结果覆盖手写的 Left/Top。
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

        var vsL = SystemParameters.VirtualScreenLeft;
        var vsT = SystemParameters.VirtualScreenTop;
        var vsR = vsL + SystemParameters.VirtualScreenWidth;
        var vsB = vsT + SystemParameters.VirtualScreenHeight;
        // 可见的右/下边界: 左上角最多向左/向上退到"还留得出 160×120 可见区"的位置。
        var maxLeft = vsR - PlacementVisibleMinWidth;
        var maxTop = vsB - PlacementVisibleMinHeight;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Math.Clamp((double)left.Value, vsL - (Width - PlacementVisibleMinWidth), maxLeft);
        Top = Math.Clamp((double)top.Value, vsT - (Height - PlacementVisibleMinHeight), maxTop);
    }

    /// <summary>
    /// 记录最后一次常规几何。最大化/全屏时 SizeChanged 也会发, 但那时记录的是屏幕尺寸,
    /// 带进下一次启动就会"一打开就铺满" —— 所以按窗口状态过滤。
    /// ★ 铺满模式(_isFillWindow)也不记: 那个布局里窗口下限是 320×200, 用户拖出来的小窗
    ///   下次按**常规布局**打开时会被 840×600 的最小尺寸顶回去, 记了等于没记。
    /// ★ 位置(LocationChanged)与尺寸(SizeChanged)走同一套过滤, 两个事件都汇到这里,
    ///   保证拖尺寸和拖位置总是成对更新(拖边缘时 WPF 两个事件都会发)。
    /// </summary>
    private bool IsNormalStateForPlacement()
    {
        return IsLoaded && !_closing && !_isFullscreen && !_isFillWindow
            && WindowState == WindowState.Normal
            && ActualWidth >= 100 && ActualHeight >= 100;
    }

    private void OnPlayerSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!IsNormalStateForPlacement()) return;
        _lastNormalWidth = ActualWidth;
        _lastNormalHeight = ActualHeight;
    }

    private void OnPlayerLocationChanged(object? sender, EventArgs e)
    {
        if (!IsNormalStateForPlacement()) return;
        _lastNormalLeft = Left;
        _lastNormalTop = Top;
        _lastNormalPosValid = double.IsFinite(Left) && double.IsFinite(Top);
    }

    /// <summary>
    /// 关闭时保存窗口几何(设置项关掉时直接跳过, 也不清掉旧值 —— 重新打开还能沿用)。
    /// ★ 在 OnClosing **最前面**调用: 后面会把全屏态还原成 Normal, 那时再读几何就不准了。
    /// </summary>
    private void SavePlayerWindowPlacement()
    {
        if (_windowPlacementSaved) return;
        _windowPlacementSaved = true;
        if (!Svc.Settings.RememberPlayerWindowSize) return;

        var w = _lastNormalWidth > 0 ? _lastNormalWidth : Width;
        var h = _lastNormalHeight > 0 ? _lastNormalHeight : Height;
        // 位置没采到(窗口居中打开且用户没拖动过 ⇒ Left/Top 还是 NaN)就不写坐标,
        // 按上次记录的位置沿用 —— 与尺寸"没采到就沿用旧值"同一个策略。
        Svc.Settings.SetPlayerWindowPlacement(w, h,
            _lastNormalPosValid ? _lastNormalLeft : double.NaN,
            _lastNormalPosValid ? _lastNormalTop : double.NaN);
    }

    // ------------------------------------------------------------ 窄视频区控制栏

    /// <summary>
    /// 视频区窄于这个宽度时, 控制栏先收起"弹幕区域"和音量滑杆这两个次要项。
    ///
    /// ★ 背景(issue #31): 竖屏副屏 864 DIP, 右栏占 400 后视频只剩 464 DIP;
    ///   而操作行的固定项(播放/时间/音量/倍速/清晰度/弹幕/全屏)加起来约 530 DIP,
    ///   不收起就会挤成一团甚至被裁掉。只收这两项后约 390 DIP, 常规窄窗仍可用。
    /// ★ 阈值 560 → 620(评审意见第 2 条): 加了字幕按钮后固定项又多了约 60~70 DIP
    ///   (按钮文案固定为"字幕", 异常态的"字幕重试"/"字幕失败" 是最宽的一档), 沿用 560 会让
    ///   560~620 这段宽度里控制栏正好卡在临界点 —— 不收起次要项也放不下, 于是整体被裁一截。
    ///   这里刻意**不**把字幕按钮纳入 compact 收起范围: 它是本功能唯一的入口,
    ///   收起来等于在这段宽度里彻底没有字幕开关, 比起让后面的次要项先让位, 用户更可能想点字幕。
    /// ★ 用 VideoAreaRoot 的实际宽度判断而不是窗口宽度: 信息栏一收/一展、
    ///   窗口一拖, 视频列宽度都会变, 这个信号最直接。
    /// </summary>
    private const double CompactControlBarWidth = 620;

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
