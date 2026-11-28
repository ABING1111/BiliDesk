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

        // ★ 小窗与全屏互斥(2026-11-25, issue #127): 小窗里按 F/双击/控制栏全屏 ⇒ 先还原成常规窗口。
        //   ExitMiniWindow 会还原常规几何并把稳定期续上; 紧接着 CaptureNormalPlacement 因
        //   "稳定期内不采"直接返回, 落盘用的仍是进入小窗前存下的那份快照(两端口径一致)。
        if (!_isFullscreen && _isMiniWindow) ExitMiniWindow();

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
        // ★ 2026-11-16: 进出全屏也是一次尺寸剧变(而且会重建 vout)。材质开启时先把它
        //   临时收掉, 窗口回到普通不透明路径; 状态/布局全部改完后由定时器恢复。
        //   (见 PlayerWindow.VideoArea.cs 的"resize 期间临时停用材质")
        SuspendBackdropForResize();

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

            // ★★ 进全屏同样会按新尺寸重建 vout ⇒ 新的 `Static` 子窗口又没被刷成近黑,
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
            //   之前设的近黑类背景刷跟着旧窗口一起没了 ⇒ 视频区边缘又露出白(用户报的
            //   "先全屏再退全屏, 播放器页面部分出现白色区域")。
            //   所以这里必须重刷一次(和起播后那次同一个入口, 见 ApplyHwndHostLetterboxBackground)。
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

        // 控制栏那颗全屏按钮的箭头跟着状态翻: 全屏时朝里(退出), 常规时朝外(进入)。
        // ★ 放在 if/else 之后统一调用: 进/出两条分支、以及 Esc/侧键退全屏都汇到这里。
        UpdateFullscreenButton();

        // 几何/状态都改完了: 尺寸稳定后把材质挂回去(期间 SizeChanged 会不断续期)。
        ResumeBackdropAfterResize();
    }

    // ------------------------------------------------------------ 尺寸变化的白闪
    //
    // ★★ 2026-11-03: 原先这里有一整套"改窗口大小时把手势层压黑"的遮罩
    //   (ArmFullscreenCover / ArmResizeCover / ArmMaximizeCover + _resizeHold 闸)。
    //   它确实能挡住 LibVLC vout 重协商时的白底, 但代价是拖动/最大化/全屏期间
    //   整个视频区变成一块黑布。
    //   ⇒ 现在改为在原生宿主窗口上根治: 给 Static 加 WS_CLIPCHILDREN + 子类化填近黑,
    //     见 Helpers/VideoBackdropInterop.EnsureLetterboxVideoHost 与
    //     PlayerWindow.ApplyHwndHostLetterboxBackground。
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

    // ------------------------------------------------------------ 详情栏展开(2026-11-28)

    /// <summary>
    /// 「详情栏展开」是否开着: 右侧详情栏(标题/UP 主/操作栏/简介或评论)铺满整个窗口,
    /// 把视频画面整个盖住。
    ///
    /// ★ 与「铺满窗口」(_isFillWindow)是**相反方向**的同一个位置: 那个把详情栏收成 0 让视频
    ///   铺满, 这个把视频收成 0 让详情栏铺满。两者互斥(同时成立窗口里什么都没有), 所以
    ///   打开一个必须关掉另一个 —— 见 <see cref="ToggleInfoExpand"/>。
    /// ★ 与全屏/小窗也互斥: 那两种状态下详情栏本来就被整个 Collapsed 掉了
    ///   (见 ApplyInfoPanelState), 没有"展开"可言。
    /// ★ 登记在 <see cref="PersistentFields"/>: 它是**窗口级显示模式**(同 _isFillWindow),
    ///   连看几集时用户就是不想看画面只听/只看评论, 换片不该把它弹回去。
    /// </summary>
    private bool _isInfoExpand;

    /// <summary>
    /// 详情栏展开时视频列的宽度。
    ///
    /// ★ 为什么必须是 **0 且 MinWidth 也必须是 0**(2026-11-28 探针实测):
    ///   只把 Width 设成 0 的话, XAML 里 `VideoColumn.MinWidth="320"` 会把它顶在 320,
    ///   视频宿主窗口(HwndHost 的原生 HWND)仍然是 320 宽 —— 它画在所有 WPF 元素**之上**
    ///   (airspace), 于是左侧 320px 会是一块盖在详情栏上的视频画面。
    ///   实测(`.probes/bd-probe-infoexpand`): Width=0 但 MinWidth=320 时
    ///   `Static` 仍是 400×895、命中测试打到浮层窗口; 两样都归 0 之后 `Static` 是 0×895,
    ///   点击全部落到主窗口(详情栏), 画面/音频继续正常推进。
    /// </summary>
    private const double InfoExpandVideoColumnWidth = 0;

    /// <summary>详情栏展开时窗口的最小尺寸(此时窗口里只剩详情栏, 840×600 的下限没有意义)。</summary>
    private const double InfoExpandMinWindowWidth = 480;
    private const double InfoExpandMinWindowHeight = 400;

    /// <summary>
    /// 上一次 <see cref="ApplyInfoPanelState"/> 实际套用的"详情栏展开"状态。
    /// 用来判断这次调用是不是真的改变了布局(见该方法末尾的刷新条件)。
    /// </summary>
    private bool _infoExpandApplied;

    /// <summary>
    /// 展开态下把手距离窗口左边缘的内缩量(DIP)。
    ///
    /// ★ 2026-11-28 第二轮改成 **-1**(也就是真的贴边): 第一版为了躲开系统缩放带内缩了 8,
    ///   用户立刻发现"把手与最左端有一条空隙"。
    ///   现在不再靠内缩躲, 而是在 <see cref="OnNonClientHitTest"/> 里把把手那一块
    ///   判回 `HTCLIENT` —— 视觉贴边、点击照常。
    /// </summary>
    private const double InfoExpandGripEdgeInset = -1;

    private void OnInfoExpandClick(object sender, RoutedEventArgs e) => ToggleInfoExpand();

    /// <summary>
    /// 展开 / 还原详情栏。
    ///
    /// ★ 顺序: 先退掉互斥的模式, 再翻开关, 最后统一交给 <see cref="ApplyInfoPanelState"/> 落布局。
    ///   与「铺满窗口」那套完全同构, 好处是"哪个开关该收栏/该铺满"永远只有一处判断。
    /// ★ 展开前**必须**退掉全屏/小窗/铺满: 前两者会把 InfoPanel 整个 Collapsed 掉,
    ///   后者会把 RightColumn 设成 0 —— 带着它们进展开态, 用户点了看不到任何变化。
    /// </summary>
    private void ToggleInfoExpand()
    {
        if (_closing) return;
        if (!_isInfoExpand)
        {
            if (_isFullscreen)
            {
                BeginPlacementTransition();
                _isFullscreen = false;
                ApplyFullscreen(false);
            }
            if (_isMiniWindow) ExitMiniWindow();
            if (_isFillWindow)
            {
                _isFillWindow = false;
                UpdateFillWindowButton();
            }
        }

        // 与全屏/铺满同理: 展开态的几何也不该记进设置(见 IsNormalStateForPlacement),
        // 切之前先把常规几何存好。
        if (!_isInfoExpand)
        {
            CaptureNormalPlacement();
            PersistNormalPlacement();
        }
        BeginPlacementTransition();
        _isInfoExpand = !_isInfoExpand;
        ApplyInfoPanelState();
        UpdateInfoExpandButton();
    }

    /// <summary>
    /// 同步把手的方向、位置与提示。
    /// ★ 箭头指"点下去会往哪边动": 折叠态(详情栏 400)箭头朝左 = 面板将向左铺开;
    ///   展开态箭头朝右 = 面板将缩回右侧。用 ScaleX=-1 翻转同一个几何, 不换 Data ——
    ///   两个字形分别画的话粗细/位置很容易对不上。
    /// ★ 两种状态的左边距都是 -1(压住那条 1px 分隔线/窗口左缘), **不需要为展开态内缩**:
    ///   第一版内缩 8 DIP 躲系统缩放带, 结果用户一眼看出"把手与最左端有空隙"。
    ///   现在改成由 <see cref="OnNonClientHitTest"/> 把把手矩形判回 `HTCLIENT` ——
    ///   视觉贴边, 点击照常(见那个方法的说明)。
    /// </summary>
    private void UpdateInfoExpandButton()
    {
        if (BtnInfoExpand == null) return;
        if (InfoExpandGlyph?.RenderTransform is ScaleTransform st) st.ScaleX = _isInfoExpand ? -1 : 1;
        BtnInfoExpand.ToolTip = _isInfoExpand ? "还原详情栏（回到视频）" : "展开详情栏（铺满窗口）";
        // 贴边: 折叠态压信息栏的分隔线, 展开态压窗口左缘 —— 两者都是 -1。
        BtnInfoExpand.Margin = new Thickness(InfoExpandGripEdgeInset, 0, 0, 0);
        // 显隐跟着设置开关走(见 UpdateInfoExpandGripVisibility)。
        UpdateInfoExpandGripVisibility();
    }

    /// <summary>
    /// 设置页动了「详情栏展开把手」开关(见 SettingsStore.InfoExpandGripChanged)。
    ///
    /// ★ 只重算把手显隐, 不动布局: 开关不影响"展开态"本身 ——
    ///   关掉开关时如果用户正处在展开态, 窗口保持铺满(靠 Esc 收回),
    ///   否则会出现"面板铺满整窗、把手又消失、用户找不到回视频的入口"。
    /// </summary>
    private void OnInfoExpandGripSettingChanged()
    {
        if (_closing) return;
        UpdateInfoExpandGripVisibility();
    }

    /// <summary>
    /// 按设置项与当前布局同步把手的显隐。
    ///
    /// ★ 三条判据缺一不可:
    ///   ① 设置里开着(ShowInfoExpandGrip), **或者**当前正处在展开态;
    ///   ② 不是全屏/铺满/小窗(那三种状态详情栏整个 Collapsed, 把手自然不该出现)。
    /// ★ 展开态**必须保留**把手: 它是除了 Esc 之外唯一的还原入口。用户把设置关掉时
    ///   若正处于展开态, 我们仍然显示它, 直到用户还原 —— 否则就是"铺满整窗又找不到出口"。
    /// </summary>
    private void UpdateInfoExpandGripVisibility()
    {
        if (BtnInfoExpand == null) return;
        var hide = _isFullscreen || _isFillWindow || _isMiniWindow;
        var show = Svc.Settings.ShowInfoExpandGrip || _isInfoExpand;
        BtnInfoExpand.Visibility = hide || !show ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 右上角「铺满窗口」按钮的点击入口。
    /// Esc 的处理见 <see cref="HandleBackRequest"/> —— 它按"由内到外"退: 全屏 → 铺满窗口 → 关窗。
    /// </summary>
    private void OnFillWindowClick(object sender, RoutedEventArgs e) => ToggleFillWindow();

    private void ToggleFillWindow()
    {
        if (_closing) return;
        // ★ 与「详情栏展开」互斥(2026-11-28): 两者是同一个位置的两个方向 ——
        //   一个把详情栏收成 0, 一个把视频收成 0。同时开着窗口里就只剩两条 0 宽的列。
        //   点「铺满窗口」的意图是"我要看画面", 所以直接关掉详情栏展开。
        if (!_isFillWindow && _isInfoExpand)
        {
            _isInfoExpand = false;
            UpdateInfoExpandButton();
        }
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
    /// 按全屏 / 铺满窗口 / 小窗 / 详情栏展开四个开关把右侧信息栏与视频列设成"该收"或"该展"。
    /// **唯一改 InfoPanel / RightColumn / VideoColumn 的地方。**
    ///
    /// ★ 为什么信息栏必须**整个 Collapsed**, 不能只把列宽设成 0:
    ///   宽度为 0 的面板仍然参与 measure/arrange/render —— 里面挂着几百张评论卡
    ///   (无对象池, 见 notes-player-internals), 每切一次都要把这棵评论树重新量一遍,
    ///   这就是用户说的"卡顿一下"(退出时还要再付一次)。
    ///
    /// ★ 为什么必须收敛到一个入口: 收信息栏的现在有三个开关(全屏 / 铺满窗口 / 小窗), 各自改一遍
    ///   会立刻出现"退出全屏把铺满窗口的状态也顶掉"这种互相打架 ——
    ///   两个开关叠加时, 谁先谁后都不能让信息栏闪一下或者卡在错误状态。
    ///   ★ 2026-11-28 又加了第四个开关「详情栏展开」, 但它方向相反(收视频、展信息栏),
    ///     所以只在这里参与"该给谁宽度"的判断, 不与 hide 合并。
    /// </summary>
    private void ApplyInfoPanelState()
    {
        if (_closing) return;
        // ★ 详情栏展开(_isInfoExpand)与另外三个开关的方向**相反**: 那三个把信息栏收成 0,
        //   这个把视频列收成 0 让信息栏铺满。所以不能简单地把它并进下面的 hide 里。
        var hide = _isFullscreen || _isFillWindow || _isMiniWindow;
        var expand = _isInfoExpand && !hide;

        if (InfoPanel != null)
            InfoPanel.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        // ★★★ 展开态必须把视频的**浮层窗口**藏掉(2026-11-28 实测):
        //   弹幕层/控制栏住在 LibVLCSharp 的 ForegroundWindow 里 —— 那是一个**独立的顶层窗口**,
        //   不随 VideoView 的布局尺寸走。视频列收成 0 之后它仍然有约 2px 可见宽度,
        //   正好压在详情栏最左边那条边上; 弹幕在里面横向平移, 于是用户看到
        //   "左上角漏出播放器、边缘一闪一闪"。
        //   ★ 试过只折叠 VideoAreaRoot(浮层里的 WPF 内容): **无效** ——
        //     探针实测 overlayVis 仍是 True, 那条边照闪。必须藏窗口本身。
        //   ★ 也不能用 VideoView.Visibility=Collapsed: 那会把 HwndHost 一起拆掉,
        //     还原时 vout 要重建(白闪 + 起播延迟), 而只藏浮层窗口完全不动播放链路
        //     (实测时钟/画面统计照常推进, 还原后画面立刻回来)。
        //   ★ 只在 expand 时藏: 全屏/铺满窗口是"视频铺满整窗", 那时控制栏与弹幕必须可见,
        //     藏掉等于把控制栏弄没了。
        ApplyVideoOverlayVisibility(expand);
        // ★ 把手在两种布局里都留着: 展开态它就贴在**整窗最左边**(面板铺满了整窗),
        //   箭头翻成朝右表示"点一下缩回右侧"。收起它的话用户就只能靠 Esc 退出,
        //   而这条把手正是这次需求给的入口。
        //   ★ 显隐统一交给 UpdateInfoExpandGripVisibility(它还看设置开关);
        //     全屏/铺满/小窗时 InfoPanel 整个 Collapsed, 那里也会一并收起。
        UpdateInfoExpandGripVisibility();
        // ★ 列宽必须跟着一起给回去: 只把 Visibility 改回 Visible 而列宽还停在 0,
        //   面板会量成 0 宽(用户看到的是"信息栏没回来")。
        if (RightColumn != null)
            RightColumn.Width = expand ? new GridLength(1, GridUnitType.Star)
                            : new GridLength(hide ? 0 : 400);
        // ★★ 视频列收 0 时 MinWidth **也必须**归 0 —— 否则 XAML 里 320 的下限会把列顶住,
        //   视频宿主窗口(HwndHost 的原生 HWND)仍然是 320 宽, 一块原生画面就压在详情栏上面
        //   (airspace, 详见 InfoExpandVideoColumnWidth 的实测说明)。
        if (VideoColumn != null)
        {
            VideoColumn.Width = new GridLength(expand ? InfoExpandVideoColumnWidth : 1,
                expand ? GridUnitType.Pixel : GridUnitType.Star);
            VideoColumn.MinWidth = (hide || expand) ? 0 : VideoColumnNormalMinWidth;
        }

        // ★ 最小尺寸必须跟着状态走(2026-10-05 修"铺满窗口后缩到某个程度就被卡住"):
        //   常规的 840×600 是按"视频列 320 + 信息栏 400"的布局定的下限(2026-11-03 从
        //   1100/480 降下来, 为了竖屏副屏能放下); 信息栏收起后整个窗口只剩视频,
        //   再拿它当底线没有道理。视频列的 MinWidth 同理 —— 不跟着放开,
        //   窗口缩到 320 以下时列宽比窗口还宽, 画面会被裁偏。
        //   (FluentWindow 的 WM_GETMINMAXINFO 在每次拖动时重读 MinWidth/MinHeight, 改属性即可。)
        //   小窗(2026-11-25)把下限再收到 320×180: 它本来就是一颗"缩到角落"的窗口。
        //   ★ 详情栏展开(2026-11-28): 窗口里只剩详情栏, 840×600 也没有意义 ——
        //     放到 480×400, 窄屏上仍能把它缩成一块"只看评论"的窄条。
        MinWidth = _isMiniWindow ? MiniWindowMinWidth
               : hide ? FillWindowMinWidth
               : expand ? InfoExpandMinWindowWidth
               : NormalWindowMinWidth;
        MinHeight = _isMiniWindow ? MiniWindowMinHeight
                : hide ? FillWindowMinHeight
                : expand ? InfoExpandMinWindowHeight
                : NormalWindowMinHeight;

        // ★ 展开/还原之后详情栏的宽度整个变了: 简介折叠阈值、tab 下划线位置、
        //   评论内容窗口(按视口高度算的)都依赖新宽度/高度, 必须重新算一次,
        //   否则会出现"简介明明没超却冒出展开按钮""下划线停在旧位置""评论区一片空白"。
        //   ★ 只在"展开态真的变了"时做: ApplyInfoPanelState 每次换片/起播都会被调用,
        //     每次都跑一遍 UpdateDescFold + 恢复评论卡片是白费功夫(那些动作本身不便宜)。
        if (expand != _infoExpandApplied)
        {
            _infoExpandApplied = expand;
            if (!hide) QueueInfoPanelLayoutRefresh();
        }
    }

    /// <summary>
    /// 显示/隐藏视频区那层**浮层窗口**(LibVLCSharp 的 ForegroundWindow: 弹幕层 + 控制栏 + 各种遮罩)。
    ///
    /// ★★★ 为什么需要它(2026-11-28 用户报"展开详情栏后左上角漏出播放器, 边缘一闪一闪"):
    ///   那层窗口是 LibVLCSharp 自己建的**独立顶层窗口**, 尺寸跟着 VideoView 的 ActualWidth/Height
    ///   走, 但**不是** WPF 可视树里的一棵子树 —— 折叠 VideoAreaRoot 或把 InfoPanel 盖上去
    ///   都影响不到它。视频列收成 0 之后它仍有约 2px 宽, 正好压在详情栏最左边那条边上,
    ///   弹幕在里面横向平移 ⇒ 用户看到的就是"一条一闪一闪的播放器边缘"。
    ///   探针实测(`.probes/bd-probe-infoexpand`): 折叠 VideoAreaRoot 后 overlayVis 仍是 True;
    ///   改成 Hide() 那层窗口后 overlayVis=False, 且时钟/画面统计照常推进。
    ///
    /// ★ 为什么用 Window.Hide()/Show() 而不是 Visibility:
    ///   WPF 的 Window.Visibility=Hidden 同样会隐藏窗口, 但 Hide() 语义更明确(它就是
    ///   ShowWindow(SW_HIDE)), 而且 Show() 会顺带恢复激活状态, 与库自己管理该窗口的方式一致。
    ///
    /// ★ 字段 <c>_overlayKeyboardWindow</c> 是 HookOverlayKeyboard 里缓存的那同一个窗口,
    ///   直接复用即可 —— 但它在构造早期可能还没拿到, 所以这里再按反射取一次兜底。
    /// ★ 失败一律静默: 顶多还是那条 2px 的边, 绝不能因为"修外观"影响播放。
    /// </summary>
    private void ApplyVideoOverlayVisibility(bool hide)
    {
        if (_closing) return;
        try
        {
            var fw = _overlayKeyboardWindow;
            if (fw == null)
            {
                var prop = typeof(VideoView).GetProperty("ForegroundWindow",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                fw = prop?.GetValue(VideoView) as Window;
            }
            if (fw == null) return;

            if (hide)
            {
                if (fw.IsVisible) fw.Hide();
            }
            else
            {
                // ★ 只在"该显示但当前是隐藏的"时 Show(): 无脑 Show() 会把一个本来就正常的
                //   窗口再显示一次, 在视频区上可能闪一下(与 AllocConsole 那类重复显示同款坑)。
                if (!fw.IsVisible) fw.Show();
            }
        }
        catch { /* 外观修复失败不影响播放 */ }
    }

    /// <summary>
    /// 详情栏宽度/高度变化之后重算那些"按尺寸算一次就存着"的 UI 状态。
    ///
    /// ★ 必须排到布局之后(DispatcherPriority.Loaded): 列宽刚改完时 ActualWidth 还是旧值,
    ///   当场算会把新结论写进旧尺寸里(与 QueueContentTabIndicatorUpdate 同一个理由)。
    /// ★ 只做**幂等**的同步动作, 不碰滚动位置/选中项 —— 用户展开详情栏是想接着看当前这页,
    ///   不能把他滚到一半的评论列表弹回顶部。
    /// </summary>
    private void QueueInfoPanelLayoutRefresh()
    {
        if (_closing) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_closing) return;
            UpdateDescFold();
            QueueContentTabIndicatorUpdate(animate: false);
            // 评论卡片的"内容窗口"是按 InfoScroll 的视口算的: 面板变宽变高后
            // 视口里能装下的卡片数变了, 已释放的卡片要按新视口补回来。
            RestoreVisibleCommentCardsNow(forceLayout: true);
            QueueCommentContentWindow();
        }));
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
        if (_isFullscreen || _isFillWindow || _isMiniWindow || WindowState != WindowState.Normal) return;

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
    /// ★ 详情栏展开(_isInfoExpand)同理不记(2026-11-28): 那个布局的下限是 480×400,
    ///   而且窗口里根本没有视频 —— 记下来会让下次打开停在"只看详情栏"的尺寸上。
    /// ★ 位置(LocationChanged)与尺寸(SizeChanged)走同一套过滤, 两个事件都汇到这里,
    ///   保证拖尺寸和拖位置总是成对更新(拖边缘时 WPF 两个事件都会发)。
    /// ★ 稳定期内一律不采: 那是在系统的还原动画里(见本节开头)。
    /// </summary>
    private bool IsNormalStateForPlacement()
    {
        return IsLoaded && !_closing && !_isFullscreen && !_isFillWindow && !_isMiniWindow
            && !_isInfoExpand
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
        // ★ 2026-11-16: 材质开启时 resize 期间临时收掉 backdrop(见 VideoArea.cs 的
        //   "resize 期间临时停用材质"一节)。必须在 placement 逻辑之前 —— 那个分支会提前 return。
        OnPlayerResizedForBackdrop();
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

        // 全屏/最大化/铺满/小窗/详情栏展开态没有"常规几何"可采: 退出这些状态时会重新进稳定期。
        if (WindowState != WindowState.Normal || _isFullscreen || _isFillWindow || _isMiniWindow
            || _isInfoExpand)
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
        // 小窗里直接关窗: 小窗自己的几何先记进进程内缓存(下次进小窗复用), 再走常规几何的落盘
        if (_isMiniWindow) RememberMiniWindowRect();
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
        // 最大化/还原/最小化也是尺寸剧变: 先停材质, 等尺寸稳定后再挂回去。
        SuspendBackdropForResize();
        ResumeBackdropAfterResize();
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
        // 拖动结束: 尺寸已经定了, 延迟把材质挂回去(期间 SizeChanged 还会续期)。
        _modalResizeInProgress = false;
        ResumeBackdropAfterResize();
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
    ///   操作行放不下就会挤成一团甚至被裁掉。
    /// ★ 2026-11-14 图标化重绘后实测: 固定项(播放/时间/音量图标+滑杆/弹幕/弹幕区域/
    ///   倍速/清晰度/全屏)约 437 DIP, 加上面板左右内边距与 14px 外边距, 整条要
    ///   约 465 DIP 才排得开 —— 阈值取 520, 留出字体差异的余量。
    ///   收起这两项后约 340 DIP, 常规窄窗仍可用。
    /// ★ 用 VideoAreaRoot 的实际宽度判断而不是窗口宽度: 信息栏一收/一展、
    ///   窗口一拖, 视频列宽度都会变, 这个信号最直接。
    /// ★ 上/下一个视频的两颗箭头**不归这里管**(2026-11-25 用户指定放在顶栏「铺满窗口」右边),
    ///   顶栏本来就是另一套布局, 别为了它们动控制栏的阈值。
    /// </summary>
    private const double CompactControlBarWidth = 520;

    private void OnVideoAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (BtnDanmakuArea == null || VolumeSlider == null) return;
        var compact = e.NewSize.Width > 0 && e.NewSize.Width < CompactControlBarWidth;
        BtnDanmakuArea.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        VolumeSlider.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        // 窄窗收起滑杆时, 悬停展开出来的那截宿主宽度也要一起归零 ——
        // 否则滑杆没了, 却留着一块 81px 的空白把后面的按钮顶开。
        if (compact && VolumeSliderHost != null)
        {
            VolumeSliderHost.BeginAnimation(FrameworkElement.WidthProperty, null);
            VolumeSliderHost.Width = 0;
            // 滑条都收起了, 悬停百分比卡片不能继续挂在控制栏上
            HideVolumePctHint();
        }
        // 字幕的字号/最大宽跟着视频区尺寸走(全屏/铺满/窄窗都要重算)(2026-11-15)
        ApplySubtitleStyle();
        // 章节胶囊(2026-11-21): 窄窗收成图标按钮。
        // ★ 阈值(820)比这里收弹幕区域/音量滑杆的 520 高 —— 章节胶囊是最"软"的一项,
        //   该在它们之前先让位; 但它**只换形态不收入口**(600px 的窗口里照样要能跳章)。
        SetChapterCompact(e.NewSize.Width > 0 && e.NewSize.Width < ChapterCompactWidth);
        // ★ 展开态兜底(2026-11-28): 视频区尺寸一变, LibVLCSharp 就会把浮层窗口重新摆位
        //   甚至重新 Show 出来 —— 那层正是用户看到"左上角漏出一条闪动播放器边"的来源。
        //   排到布局之后(Loaded)再藏一次: 当场藏的话库的摆位可能紧接着又把它显示出来。
        if (_isInfoExpand)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RehideVideoOverlayIfExpanded));
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

    /// <summary>
    /// 同步控制栏「全屏」按钮的字形与提示(2026-11-28 用户要求: 全屏时箭头朝里)。
    ///
    /// E740 = 向外的双箭头("进入全屏") / E73F = 向内的双箭头("退出全屏")。
    /// 与顶栏「铺满窗口」的 UpdateFillWindowButton 同一套思路: 同一个位置点两下是进出,
    /// 字形不变的话用户看不出再点一下会发生什么。
    /// ★ 唯一调用点是 ApplyFullscreenLayout —— F 键 / 双击视频 / 控制栏 / Esc / 鼠标侧键
    ///   退全屏最终都经过那里, 不需要在别处补。
    /// </summary>
    private void UpdateFullscreenButton()
    {
        if (FullscreenGlyph != null) FullscreenGlyph.Text = _isFullscreen ? "\uE73F" : "\uE740";
        if (BtnFullscreen2 != null) BtnFullscreen2.ToolTip = _isFullscreen ? "退出全屏 (F)" : "全屏 (F)";
    }

    /// <summary>
    /// 展开态下把手**真的贴边**, 由这里把它的矩形从系统缩放带里要回来。
    ///
    /// ★★★ 为什么必须这么做(2026-11-28, 用户连着报了两轮):
    ///   第一轮: 把手贴 x=0 ⇒ 整条落在 `ResizeBorderThickness` 的缩放带里,
    ///           `WM_NCHITTEST` 先返回 `HTLEFT`, 按下变成拖窗口边缘, **点击永远不生效**。
    ///   第二轮: 改成内缩 8 DIP 躲开 ⇒ 点击好了, 但用户一眼看出"把手与最左端有空隙"。
    ///   第三轮(现在): 视觉贴边(-1), 命中测试在这里判回 `HTCLIENT`。
    ///
    /// ★★★ 钩子必须**自己挂一个**, 不能塞进 FluentWindow.WndProc —— 实测踩到:
    ///   `HwndSource` 的钩子是**后加的先执行**(倒序), 而 WindowChromeWorker 是在窗口
    ///   初始化早期挂的, 所以 FluentWindow 里那个钩子跑在它**之后**, 返回 HTCLIENT 会被
    ///   WindowChromeWorker 的 HTLEFT 覆盖(探针里表现成"钩子返回 1, 但 SendMessage 仍是 HTLEFT")。
    ///   在 Loaded 里再挂一个 = 比 WindowChromeWorker 更晚 ⇒ 真的抢先。
    ///   `.probes/bd-probe-infoexpand` 实测: 这样挂之后贴边点得到, 且窗口其它边缘仍是缩放带。
    ///
    /// ★ 只在**展开态**拦: 折叠态把手在视频/信息栏的分界线上, 离窗口边缘很远,
    ///   不该为了它去动窗口边缘的缩放带(那会让人拖不动窗口左边)。
    /// ★ 判据用屏幕物理像素(与 lParam 同单位), 并且**只认把手自己的矩形** ——
    ///   放宽一点点(四周各 1px)是为了容忍 DPI 取整误差, 不能再大,
    ///   否则窗口左边缘那条缩放带会被咬掉一块。
    /// </summary>
    private void HookNonClientHitTest()
    {
        if (_closing || _nonClientHookSource != null) return;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var src = HwndSource.FromHwnd(hwnd);
            if (src == null) return;
            src.AddHook(NonClientHitTestHook);
            _nonClientHookSource = src;
        }
        catch { /* 挂不上就退回"把手内缩"的旧行为(见 UpdateInfoExpandButton 的兜底) */ }
    }

    /// <summary>见 <see cref="HookNonClientHitTest"/>。</summary>
    private IntPtr NonClientHitTestHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmNcHitTest) return IntPtr.Zero;
        if (!_isInfoExpand || _closing) return IntPtr.Zero;
        if (BtnInfoExpand == null || !BtnInfoExpand.IsVisible) return IntPtr.Zero;
        if (BtnInfoExpand.ActualWidth <= 0 || BtnInfoExpand.ActualHeight <= 0) return IntPtr.Zero;

        // lParam 低 16 位 = x, 高 16 位 = y; 都是**屏幕物理像素**(有符号, 多屏时可能是负的)。
        var value = lParam.ToInt64();
        var x = (short)(value & 0xFFFF);
        var y = (short)((value >> 16) & 0xFFFF);

        try
        {
            var topLeft = BtnInfoExpand.PointToScreen(new Point(0, 0));
            var dpi = VisualTreeHelper.GetDpi(this);
            var left = (int)Math.Floor(topLeft.X) - 1;
            var top = (int)Math.Floor(topLeft.Y) - 1;
            var right = (int)Math.Ceiling(topLeft.X + BtnInfoExpand.ActualWidth * dpi.DpiScaleX) + 1;
            var bottom = (int)Math.Ceiling(topLeft.Y + BtnInfoExpand.ActualHeight * dpi.DpiScaleY) + 1;

            if (x >= left && x <= right && y >= top && y <= bottom)
            {
                handled = true;
                return new IntPtr(HtClient);   // 1 = HTCLIENT
            }
        }
        catch { /* 取不到几何就交回默认判定 */ }

        return IntPtr.Zero;
    }

    /// <summary>我们挂的 WM_NCHITTEST 钩子所在的 HwndSource(挂过就不再挂)。</summary>
    private HwndSource? _nonClientHookSource;

    /// <summary>Win32 `HTCLIENT`(客户端区, 鼠标消息正常派发给 WPF)。</summary>
    private const int HtClient = 1;

    /// <summary>Win32 `WM_NCHITTEST`。</summary>
    private const int WmNcHitTest = 0x0084;

    /// <summary>
    /// 展开态下浮层窗口的**兜底重藏**。
    ///
    /// ★ 为什么需要兜底(2026-11-28): LibVLCSharp 的 ForegroundWindow 是按 VideoView 的
    ///   ActualWidth/Height 摆位的, 视频区尺寸一变(进出展开/窗口缩放/vout 重建)它就会
    ///   重新 Show 一次自己 —— 我们 Hide 过的那一层会"又回来", 用户看到左上角那条边
    ///   又开始闪。所以凡是"视频区尺寸刚变过"的时机都再藏一次。
    /// ★ 只在展开态做(其余状态它本来就该可见), 由 ApplyVideoOverlayVisibility 内部判定,
    ///   这里只负责"多调一次", 幂等。
    /// </summary>
    private void RehideVideoOverlayIfExpanded()
    {
        if (_closing || !_isInfoExpand) return;
        ApplyVideoOverlayVisibility(true);
    }

}
