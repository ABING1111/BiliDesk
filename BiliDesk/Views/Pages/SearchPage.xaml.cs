using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using BiliDesk.Services;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

/// <summary>
/// 搜索页。
///
/// 页面上只有一个搜索入口: 浮在正上方中间的那张搜索卡(2026-09-26 用户要求
/// "搜索改成主页那种样式, 固定在正上方中间位置, 不会平移, 但有展开动画")。
/// 它与首页 SearchCard 是同一套视觉、同一套展开动作, 差别只有两点:
///
///   ① 位置固定 —— 卡片是 HorizontalAlignment=Center, 所以宽度 440→520 时布局自己会
///      继续保持居中, **完全没有位移补偿**。首页那张卡要从右上角"飞"到页面正中, 才有
///      关键帧 + PanXFor 那套推导。这里只需要动 Width / Height。
///   ② 触发方式 —— 这里**聚焦输入框**就展开、焦点离开就收起(用户定); 首页是点一下那条灰框。
///
/// 展开露出的内容只有「搜索历史」(用户定), 页面原先那条独立的历史条已挪进内容框。
/// </summary>
public partial class SearchPage : UserControl
{
    // 尺寸与时长都和首页搜索卡保持一致: 两处的观感必须是一个东西, 改这里时建议两边一起看。
    private const double SearchBoxW = 440;
    private const double SearchBoxH = 48;
    private const double SearchCardW = 520;
    /// <summary>搜索框与内容框之间的间隙(XAML 里内容框的 Margin 上边距同值)</summary>
    private const double SearchContentGap = 10;
    /// <summary>内容四周的余量: 免得"刚好放满"因为 1px 取整就冒出滚动条</summary>
    private const double SearchCardSlack = 8;
    private const int SearchExpandMs = 340;
    /// <summary>收起时先让内容淡出, 这段之后尺寸才开始缩 —— 反过来能看见内容被挤扁</summary>
    private const int SearchFadeOutMs = 90;
    private const int SearchCollapseMs = 300;
    /// <summary>历史增删后, 已展开卡片改高度的时长</summary>
    private const int SearchResizeMs = 220;
    /// <summary>聚焦时粉色描边淡入的最终不透明度(2026-09-26 起是描边、不再是光晕)</summary>
    private const double SearchRingOpacity = 1.0;

    /// <summary>卡片处于"打开"状态(含正在展开的那一段)</summary>
    private bool _open;
    /// <summary>正在收起: 这时不再接受"再展开", 免得从半路折返出怪动作</summary>
    private bool _closing;
    /// <summary>展开动画已结束(之后才允许按内容量改高度)</summary>
    private bool _ready;
    /// <summary>展开途中历史变了: 记下来, 等动画收尾再重新量一次高度</summary>
    private bool _needRemeasure;

    private SearchViewModel? Vm => DataContext as SearchViewModel;

    public SearchPage()
    {
        InitializeComponent();

        // 历史增删(自己搜一次 / 点掉一条 / 清空)都会改变展开高度, 也可能让历史变空。
        // 服务是单例, 页面实例在导航缓存里长住, 所以这里不需要退订。
        Svc.SearchHistory.Items.CollectionChanged += (_, _) => ScheduleHeightSync();

        // DataContext 是外面赋进来的, 构造时还拿不到 VM; 换 VM 就当作"换了一次展示", 把卡片收回原位。
        DataContextChanged += (_, _) => ResetCard();
        // 切到别的页时本页会被摘出可视树: 焦点跟着没了, 卡片也该收回去(不然后面切回来它是开着的)
        Unloaded += (_, _) => ResetCard();
    }

    // ------------------------------------------------------------ 交互

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        RunSearch();
    }

    /// <summary>清空输入框。焦点留在框里 —— 卡片就一直保持展开, 方便接着点历史。</summary>
    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        Vm.Keyword = "";
        SearchBox.Focus();
    }

    private void OnSearchGoClick(object sender, RoutedEventArgs e) => RunSearch();

    /// <summary>
    /// 点历史词条: 命令(填回关键词并重搜)由 XAML 绑定执行, 这里只负责把卡片收起来。
    /// 顺序上 Click 先于 Command 触发, 所以不用担心把还没跑的命令关掉。
    /// </summary>
    private void OnHistoryChipClick(object sender, RoutedEventArgs e) => CollapseAfterSearch();

    /// <summary>Esc 收起卡片。挂在卡片上: PreviewKeyDown 从根往焦点元素隧道, 焦点在不在输入框都能收到。</summary>
    private void OnCardPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        // 收卡片 + 把焦点交出去。焦点必须一起交: 展开是"聚焦"驱动的, 只收卡片的话焦点还留在框里,
        // 之后再点它反而不会展开了(焦点压根没变过, 也就没有新的 GotKeyboardFocus)。
        CloseCard();
        Keyboard.ClearFocus();
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e) => Vm?.OnKeywordChanged();

    // ------------------------------------------------------------ 滚动到底自动加载

    /// <summary>离底部还剩这么多像素就算"到底了", 提前把下一页拉回来(与首页 TriggerAutoLoad 同值)</summary>
    private const double AutoLoadThreshold = 240;

    /// <summary>
    /// 连续自动填充的次数。与首页同款的一道闸: 内容不足一屏时"补了还是短"会立刻再来一次,
    /// 没有它就会一直往下拉(用户看到的是"页面自己在不停加载")。
    /// </summary>
    private int _autoFillCount;

    /// <summary>
    /// 结果区滚到接近底部(或内容不足一屏)就自动翻下一页 —— 与首页/稍后再看页同一套写法。
    ///
    /// 用 async void + await 是**故意的**: _autoFillCount 计数跨 await 必须还有效, 否则
    /// 几次请求同时在飞, 闸门等于没有(首页那边也是这么写的, 见 HomePage.TriggerAutoLoad)。
    /// </summary>
    private async void OnResultScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Vm is not { HasMore: true } vm) return;
        if (vm.LoadingMore || vm.Searching || vm.Blocked) return;
        if (_autoFillCount > 5) return;
        if (sender is not ScrollViewer sv) return;

        var distanceToBottom = sv.ScrollableHeight - sv.VerticalOffset;
        var contentShort = sv.ScrollableHeight < sv.ActualHeight * 0.8;
        if (distanceToBottom > AutoLoadThreshold && !contentShort) return;

        _autoFillCount++;
        try { await vm.LoadMoreAsync(); }
        finally { _autoFillCount--; }
    }

    /// <summary>
    /// 点在卡片外面 → 收起卡片。
    ///
    /// 为什么光靠"失焦"不够: 结果区的 VideoCard 是 `Focusable="False"` + MouseLeftButtonUp,
    /// 点它**不会**移走键盘焦点, 于是卡片会一直盖在结果区顶部收不掉。这里补一条兜底。
    ///
    /// ★ 刻意**不吞掉这次点击**(不设 e.Handled): 点在结果卡片上时, 卡片照常收起,
    ///   这一下点击也照常把视频打开 —— 要的是"点一下两件事都办", 而不是先点一下只收卡片。
    ///   这也是这里不用首页那种遮罩层的原因: 遮罩会把第一下点击吃掉。
    /// </summary>
    private void OnPagePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_open) return;
        // OriginalSource 是真实点击时的最内层元素; 探针手工造的事件没有它, 退回 Source
        var hit = (DependencyObject?)e.OriginalSource ?? (DependencyObject?)e.Source;
        if (IsInside(hit, SearchCard)) return;
        CloseCard();
        Keyboard.ClearFocus();
    }

    /// <summary>node 是不是 ancestor 自己或它的后代(沿可视树往上找)</summary>
    private static bool IsInside(DependencyObject? node, DependencyObject ancestor)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private void RunSearch()
    {
        if (Vm == null) return;
        // 空关键词: 不搜, 也不收卡片 —— 这时收起只会让用户觉得"点了没反应"
        if (string.IsNullOrWhiteSpace(Vm.Keyword)) return;

        // 走 VM 的命令而不是直接调 SearchAsync: 与首页搜索卡同一个入口, 免得两处慢慢长歪
        Vm.SearchCommand.Execute(null);
        CollapseAfterSearch();
    }

    /// <summary>
    /// 搜过之后把卡片收起来, 顺便把键盘焦点交出去。
    /// ★ 焦点必须交出去: 展开是"聚焦"驱动的, 焦点还留在输入框里的话, 卡片会停在
    ///   "聚焦着却是收起态"的矛盾状态上, 之后再点它也不会展开(焦点压根没变过)。
    /// </summary>
    private void CollapseAfterSearch()
    {
        CloseCard();
        Keyboard.ClearFocus();
    }

    // ------------------------------------------------------------ 聚焦 / 失焦

    private void OnCardGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        FadeRing(true);
        OpenCard();
    }

    private void OnCardLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 焦点可能只是在卡片内部换手(输入框 → 词条按钮 → 清空按钮), 那一刻不该收。
        // 等这次焦点转移彻底走完再回来看 IsKeyboardFocusWithin。
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (SearchCard.IsKeyboardFocusWithin) return;
            FadeRing(false);
            CloseCard();
        }));
    }

    // ------------------------------------------------------------ 展开 / 收起

    /// <summary>
    /// 展开: 宽度 440→520、高度 48→卡片高, 露出下面的内容框。
    ///
    /// 卡片是居中的, 所以**只需要动尺寸** —— 布局每帧都会把它重新摆在正中, 宽度长开是
    /// 左右对称的。没有历史可看时直接不展开: 长出一个空框比老实收着更难看。
    /// </summary>
    private void OpenCard()
    {
        if (_open || _closing) return;
        if (Svc.SearchHistory.Items.Count == 0) return;
        if (ActualWidth <= 0) return;

        _open = true;
        // 高度在变, 这段别让 Auto 闪出滚动条(动画结束再切回 Auto)
        SearchCardScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var ms = TimeSpan.FromMilliseconds(SearchExpandMs);

        // 搜索框自己不用淡入: 它一直都在, 展开只是"下面多长出一个框"
        SearchCard.BeginAnimation(FrameworkElement.WidthProperty,
            new DoubleAnimation(SearchBoxW, SearchCardW, ms) { EasingFunction = ease });

        var height = new DoubleAnimation(SearchBoxH, ComputeOpenHeight(), ms) { EasingFunction = ease };
        height.Completed += (_, _) => OnOpened();
        SearchCard.BeginAnimation(FrameworkElement.HeightProperty, height);

        // 内容框随展开淡入(底色硬切在动画里很跳); 框里的内容再晚一点, 看着像"被框一点点让出来"
        Fade(SearchContentBox, 0, 1, (int)(SearchExpandMs * 0.3), (int)(SearchExpandMs * 0.7));
        Fade(SearchCardScroll, 0, 1, (int)(SearchExpandMs * 0.45), (int)(SearchExpandMs * 0.55));
    }

    private void OnOpened()
    {
        _ready = true;
        SearchCardScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        // 展开途中历史变了: 这时才补一次高度(动画期间插一脚会把动画截断)
        if (_needRemeasure)
        {
            _needRemeasure = false;
            ScheduleHeightSync();
        }
    }

    /// <summary>收起: 内容先淡出 → 尺寸缩回灰框。任何时刻都能调(半路也收得回去)</summary>
    private void CloseCard()
    {
        if (!_open) return;
        _open = false;
        _ready = false;
        _closing = true;

        // 拿当前值当起点: 动画被打断时就从这一刻的尺寸收回去, 不跳
        var w0 = SearchCard.Width > 0 ? SearchCard.Width : SearchBoxW;
        var h0 = SearchCard.Height > 0 ? SearchCard.Height : SearchBoxH;

        var fade = TimeSpan.FromMilliseconds(SearchFadeOutMs);
        // From **必须显式给当前值**: 不写 From 时动画从属性**基值**起步, 而基值仍是收起态的 0,
        // 内容会先"啪"地跳成全不透明再淡出。
        SearchCardScroll.BeginAnimation(OpacityProperty,
            new DoubleAnimation(SearchCardScroll.Opacity, 0, fade));
        SearchContentBox.BeginAnimation(OpacityProperty,
            new DoubleAnimation(SearchContentBox.Opacity, 0,
                TimeSpan.FromMilliseconds(SearchCollapseMs)) { BeginTime = fade });
        FadeRing(false);

        var hold = SearchFadeOutMs;
        var end = SearchFadeOutMs + SearchCollapseMs;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // 尺寸用关键帧: 前 SearchFadeOutMs 保持不动(让内容先淡完), 之后再缩回去
        SearchCard.BeginAnimation(FrameworkElement.WidthProperty, Keys(
            K(0, w0), K(hold, w0), K(end, SearchBoxW, ease)));

        var height = Keys(K(0, h0), K(hold, h0), K(end, SearchBoxH, ease));
        height.Completed += (_, _) => ResetCard();
        SearchCard.BeginAnimation(FrameworkElement.HeightProperty, height);
    }

    /// <summary>
    /// 恢复成收起态并清掉全部动画。
    ///
    /// 必须清动画: 关键帧动画是 HoldEnd 的, 不清的话属性被动画值一直占着 ——
    /// 下次展开时动画的起点就不再是收起值, 窗口缩放后卡片也回不到原位。
    /// </summary>
    private void ResetCard()
    {
        _open = false;
        _closing = false;
        _ready = false;
        _needRemeasure = false;

        SearchCard.BeginAnimation(FrameworkElement.WidthProperty, null);
        SearchCard.BeginAnimation(FrameworkElement.HeightProperty, null);
        SearchContentBox.BeginAnimation(OpacityProperty, null);
        SearchCardScroll.BeginAnimation(OpacityProperty, null);
        SearchInputRing.BeginAnimation(OpacityProperty, null);

        // 先清动画再落本地值, 否则会被残留的动画值顶掉
        SearchCard.Width = SearchBoxW;
        SearchCard.Height = SearchBoxH;
        SearchContentBox.Opacity = 0;
        SearchCardScroll.Opacity = 0;
        SearchInputRing.Opacity = 0;
        SearchCardScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
    }

    // ------------------------------------------------------------ 高度自适应

    /// <summary>
    /// 展开态应有的高度 = 输入行 + 间隙 + 内容自然高度 + 余量。
    ///
    /// 为什么不写死: 搜索历史可能有两条也可能有 20 条, 写死不是底部空一大块就是内容被裁掉。
    /// 好在 ScrollViewer 即使在 0 高的行里也会拿无限高度去量内容, ExtentHeight 随时可读。
    ///
    /// ★ 量之前必须把宽度摆到展开值: 收起态宽 440、展开态宽 520, 标签的换行结果不一样
    ///   (实测同一批词条能差整整一行), 按收起态量出来的高度会偏大。
    /// </summary>
    private double ComputeOpenHeight()
    {
        // 历史被清空了: 没有内容可露, 卡片就停在收起态那一档(内容框那行是 0 高, 自然看不见)
        if (Svc.SearchHistory.Items.Count == 0) return SearchBoxH;

        var needRemeasure = Math.Abs(SearchCard.Width - SearchCardW) > 0.5;
        if (needRemeasure)
        {
            SearchCard.Width = SearchCardW;
            SearchCard.UpdateLayout();
        }

        var content = SearchCardScroll.ExtentHeight;

        if (needRemeasure)
        {
            SearchCard.Width = SearchBoxW;
            SearchCard.UpdateLayout();
        }

        // 兜底: 正常情况下布局早就跑过了, 这个分支不会走到
        if (content <= 0) return SearchBoxH + 120;

        // 上限留出上下各 48 的呼吸空间; 真超了就靠内容区自己的滚动条兜住
        var max = Math.Max(SearchBoxH + 120, ActualHeight - 96);
        // 内容框自己那 1px 上下边框会吃掉 2px, 由 SearchCardSlack 兜住(它本来就是留给取整误差的)
        return Math.Min(max, SearchBoxH + SearchContentGap + content + SearchCardSlack);
    }

    /// <summary>
    /// 历史变了 → 已展开的卡片高度要重算。**必须延后**到布局之后: 属性/集合通知都发在
    /// "数据刚变、容器还没重建"那一刻, 当场量到的 ExtentHeight 还是旧值(首页那张卡实测过:
    /// 点「展开更多」后立刻量, 卡片长得不够、底部溢出 62px)。
    /// 展开动画还没跑完就先记账, 由 OnOpened 收尾补一次 —— 动画中间插一脚会把动画截断。
    /// </summary>
    private void ScheduleHeightSync()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_open || !IsVisible) return;
            if (!_ready) { _needRemeasure = true; return; }
            UpdateLayout();
            ResizeOpenCard(ComputeOpenHeight());
        }));
    }

    /// <summary>已展开的卡片改高度 —— 顶部固定、往下长, 居中是布局自己保持的, 所以不用补偿任何位移</summary>
    private void ResizeOpenCard(double newHeight)
    {
        var h0 = SearchCard.Height;
        if (double.IsNaN(h0) || h0 <= 0 || Math.Abs(newHeight - h0) < 1) return;

        SearchCard.BeginAnimation(FrameworkElement.HeightProperty,
            new DoubleAnimation(h0, newHeight, TimeSpan.FromMilliseconds(SearchResizeMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    // ------------------------------------------------------------ 动画小工具

    /// <summary>聚焦描边淡入淡出。From 取当前值, 淡出途中再聚焦也不会跳。</summary>
    private void FadeRing(bool on)
        => SearchInputRing.BeginAnimation(OpacityProperty,
            new DoubleAnimation(SearchInputRing.Opacity, on ? SearchRingOpacity : 0,
                TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

    /// <summary>一帧关键帧。给了缓动, 就作用在"上一帧 → 这一帧"这一段上。</summary>
    private static EasingDoubleKeyFrame K(int ms, double value, IEasingFunction? ease = null)
    {
        var frame = new EasingDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)));
        if (ease != null) frame.EasingFunction = ease;
        return frame;
    }

    private static DoubleAnimationUsingKeyFrames Keys(params DoubleKeyFrame[] frames)
    {
        var anim = new DoubleAnimationUsingKeyFrames();
        foreach (var f in frames) anim.KeyFrames.Add(f);
        return anim;
    }

    /// <summary>从 from 淡到 to。每次新建动画实例 —— 同一个实例挂到两处容易出怪问题。</summary>
    private static void Fade(UIElement target, double from, double to, int beginMs, int durationMs)
    {
        target.BeginAnimation(OpacityProperty,
            new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(durationMs))
            {
                BeginTime = TimeSpan.FromMilliseconds(beginMs),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }
}
