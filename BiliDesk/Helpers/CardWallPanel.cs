using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace BiliDesk.Helpers;

/// <summary>
/// 卡片墙专用面板: 行为与 <see cref="WrapPanel"/>(ItemWidth/ItemHeight=NaN 路径)完全一致,
/// 但把"窗口宽度变化引发的全墙重测"**分帧摊开**, 这是"最大化/还原卡顿"的根治点。
///
/// ## 问题(2026-10-01 探针实测)
/// 旧结构 = 零虚拟化 WrapPanel + 卡片 Width 绑定 <see cref="CardWall.ItemWidth"/>。
/// 窗口宽度一变(最大化/还原/拖边), CardWall 就写出新的 ItemWidth ⇒ **每一张**卡片的
/// Width 变化 ⇒ 整墙重新测量。卡片里最贵的是标题 TextBlock 的**换行排版**:
/// 240 张卡的墙, 一次宽度变化 = 180~250ms 的 UI 线程冻结(简单页只有 5ms)——
/// 这正是"含视频卡片的页面在最大化时明显卡顿、设置页流畅"的根因, 且冻结时长随
/// 滚动加载的卡片数线性恶化。
///
/// ## 解法: 本地覆盖 + 分帧渐进
/// 卡片的尺寸来源从"继承墙的 ItemWidth"改成"**卡片容器上的本地覆盖值**"
/// (WPF 规则: 有本地值的元素不参与继承失效 ⇒ 墙上的 ItemWidth 变化不再把卡片弄脏)。
/// 于是宽度变化被拆成两步:
///   ① 布局当帧: 所有孩子都"干净"(约束没变 ⇒ WPF 直接用上次的测量缓存),
///      重排只剩排位置, 240 张卡 ≈ 几毫秒 —— 不再冻结;
///   ② 之后每帧(Background 优先级)只把一批(16 张)孩子的本地值切到新宽度,
///      可见区优先。每帧的真实重测 ≈ 16 × ~0.8ms ≈ 13ms, 混在最大化动画里
///      画面的帧间隔仍是 60fps 档; ~15 帧后整墙收敛到新宽度。
/// 用户观感与旧的"整墙拉伸"完全一致 —— 只是摊到了动画期间。
///
/// ## 与 CardWall 的协作(协议不变)
/// CardWall 仍然负责"按容器宽度算列数/槽宽"并写在墙元素的
/// <see cref="CardWall.ItemWidthProperty"/>(含拖动时的 120ms 合流)。本面板:
///   · 初始化/新增孩子时, 把**当时的**继承值抄成本地覆盖;
///   · 监听自己身上的 ItemWidth/CoverHeight 变化 = "目标变了", 排队渐进切换;
///   · 孩子的排列完全照抄 WrapPanel(逐个按期望尺寸排, 放不下换行, 行高=行内最高),
///     所以覆盖值切换过程中的混合宽度排版也是合法的中间态。
/// </summary>
public class CardWallPanel : Panel
{
    /// <summary>
    /// 每帧切换多少个孩子。
    /// ★ 是按"单帧预算"选的: 实测每张卡重测 ≈0.8ms(Debug), 16 张 ≈13ms —— 加上渲染本身
    ///   的 ~7ms, 动画期间的帧间隔仍能贴着 60fps; 调大就会把帧间隔顶过 20ms(探针 M1 实测)。
    /// </summary>
    private const int BatchSize = 16;

    private bool _pumpQueued;

    /// <summary>
    /// 这一轮缩放期间宽度变过、但还没在"尺寸稳定"后收敛。
    /// 见 <see cref="CardWall.IsResizing"/> / <see cref="OnResizeSettled"/>。
    /// </summary>
    private bool _resizePending;

    /// <summary>上次 Arrange 时每个孩子的 Y 坐标(与 InternalChildren 对齐), 供"可见区优先"排序。</summary>
    private readonly List<double> _arrangeY = new();

    /// <summary>
    /// 上次 Arrange 时每个孩子的**底边** Y(= Y + 高度), 与 <see cref="_arrangeY"/> 对齐。
    /// 封面内存窗口要判"这张卡整个在带外", 只看顶边不够 —— 一张卡可能顶边在带上方、
    /// 身体还伸进视野里。
    /// </summary>
    private readonly List<double> _arrangeBottom = new();

    public CardWallPanel()
    {
        // 挂上 CardWall.Enable 之后, SizeChanged 会驱动 CardWall.Update 写 ItemWidth ——
        // 那正是本面板要监听的"目标变化"信号, 见 OnPropertyChanged。
        //
        // ★ 尺寸变化分两条路(2026-11-09):
        //   · 普通宽度变化(不是连续缩放): 当帧收敛可见带 + 补锚点, 见 OnWallResized;
        //   · 缩放进行中(CardWall.IsResizing): 只登记 _resizePending, 等 ResizeSettled
        //     后一次性收敛 —— 逐帧重排正是"缩放时卡片抽搐"的来源。
        SizeChanged += (_, e) =>
        {
            // ★ 先登记"宽度变了"再走重排逻辑: 两者都挂在 SizeChanged 上时, 处理器顺序
            //   取决于订阅先后 —— 在这里先置位, OnWallResized 才能可靠地看到"缩放中"
            //   而暂缓逐帧收敛(见 CardWall.IsResizing)。
            //   ★ 0 → 实际宽度是首次布局, 不是用户缩放(见 CardWall.OnWallSizeChanged)。
            if (e.WidthChanged && e.PreviousSize.Width > 0.5) CardWall.NotifyLayoutResize();
            OnWallResized();
        };

        // 封面内存窗口(2026-11-07 P1): 卡片是零虚拟化的, 容器永不销毁, 于是"滚过多少张"
        // 就等于"常驻多少张解码后的封面位图"。滚出视口足够远之后必须把引用放开,
        // 否则内存随滚动线性上涨。挂 Loaded/Unloaded 而不是构造: 那时才拿得到 ScrollViewer。
        Loaded += (_, _) =>
        {
            CardWall.ResizeSettled -= OnResizeSettled;
            CardWall.ResizeSettled += OnResizeSettled;
            AttachCoverWindow();
        };
        Unloaded += (_, _) =>
        {
            CardWall.ResizeSettled -= OnResizeSettled;
            DetachCoverWindow();
        };
        // 切页时页面只是 Visibility.Collapsed(见 MainWindow.ShowPage): 墙没被卸载,
        // 但用户看不见它了 —— 回到这一页时必须重扫一次, 把这一屏的封面贴回来。
        //
        // ★ 回来时派到 **Loaded 优先级**(布局之后)再扫: 此刻布局还没走, _arrangeY 是上一版,
        //   TransformToAncestor 甚至可能抛。等布局落地再算, 才能一次贴对位置。
        //   用 Loaded 而不是 150ms 合并, 是因为切页动画只有 200ms —— 拖到 150ms 后
        //   用户会看到"新页面已经淡入、卡片却是空的"闪一下。
        //
        // ★ IsVisibleChanged 是**普通 CLR 事件**(不是路由事件), sender 恒为本元素,
        //   子元素的可见性变化不会传到这里 —— 所以这里不需要再判来源。
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                // ★ 缩放期间在别的 tab / 别的页面上攒下的宽度变化, 回到这一屏时补一次收敛。
                //   放在 UpdateLayout 之前: ConvergeForResize 自己会付清布局并补锚点。
                if (_resizePending && !CardWall.IsResizing)
                {
                    _resizePending = false;
                    ConvergeForResize();
                }
                // ★★ 先把布局付清再扫(2026-11-07 修"排行榜在小窗口下部分封面永久灰"):
                //   页面从 Collapsed 变 Visible 的这一刻, `ArrangeTop` 可能还是**上一次可见时**
                //   的旧值(折叠期间从不参与布局), 甚至还没被赋过。直接扫会拿到过期坐标,
                //   把"其实在视口里"的卡片判成视口外放掉、或反过来不去恢复。
                //   而且这一轮扫完之后, **用户不滚动就不会再有下一轮** —— 一旦判错就永久灰,
                //   只有改变窗口尺寸(强制重排)才救得回来, 正是用户报的现象。
                //   这里 UpdateLayout 只付一次(可见的那一屏, 十几毫秒), 换来的是"切回来必定正确"。
                try { UpdateLayout(); } catch { /* 布局尚未就绪: 下面的扫描会自然跳过 */ }
                // ★ 顺便重试"发现 ScrollViewer": 挂载时可能还没进可视树(见 AttachCoverWindow)。
                TryAttachScroll();
                UpdateCoverWindow();
                // 回到这一屏: 取消"隐藏后延迟全放"的等待(详见 ScheduleFullReleaseWhenHidden)。
                _hiddenReleaseGeneration++;
            }
            else
            {
                ReleaseOffscreenCovers();
                // 隐藏超过一小会儿后, 把**视口那一屏**也放掉 —— 隐藏页/隐藏 tab 不需要
                // 常驻任何封面(内存优先, 回来时再按可见带补, 见 Cover.Restore)。
                ScheduleFullReleaseWhenHidden();
            }
        };
    }

    /// <summary>
    /// 墙宽变了(拖窗口边缘 / 最大化还原)。
    ///
    /// ★★ 为什么必须**当帧**把可见带同步过去, 而不是交给渐进泵(2026-10-01 探针实测):
    ///   折行是按孩子**当前**宽度算的, 而卡宽是"上一次可用宽度"算出来的 ⇒ 窗口一窄, 一行里
    ///   最后那张就塞不下、掉进下一行(整墙行数 +1, 可见内容整体下移一行); 等泵把卡宽换过来
    ///   它又跳回来。探针实测: 一次 1700→1100 的拖动里可见内容这样上下跳 30 多次、累计 6000px。
    ///   SizeChanged 是在**排列之后**发的, 此刻改孩子的本地尺寸值并让布局重新失效, WPF 会在
    ///   **同一次布局更新里**重排完 —— 那个"多塞/少塞一张"的中间态一帧都不会被渲染。
    ///
    /// ★ 视野上方/下方的卡片仍交给渐进泵(看不见, 分帧付), 它们引起的可见位移由 Reanchor 抵掉。
    /// </summary>
    private void OnWallResized()
    {
        // ★★ 缩放进行中(拖边 / 最大化还原的系统动画 / 滚动条出现消失): **只登记, 不重排**。
        //   旧写法每帧都在这里 RefreshNow + ConvergeBand, 而 CardWallPanel 的渐进泵与
        //   Reanchor 也在同时跑 —— 一次拖动里可见带被重测几十次、偏移被补来补去,
        //   用户看到的就是"卡片抽搐乱动"。现在统一等 CardWall.ResizeSettled, 由
        //   ConvergeForResize 一次性收敛(见 OnResizeSettled)。
        if (CardWall.IsResizing)
        {
            _resizePending = true;
            return;
        }

        // 补偿要等这次重排落地之后再算 —— 此刻 _arrangeY 还是"翻过一行"的旧版式。
        // 派到 Loaded 优先级 = 排在本次布局之后、渲染之前(渲染在 Render, 优先级更高)。
        QueueReanchor();

        // ★★ 折行还成立就到此为止 —— 别白白把整屏卡片重测一遍。
        //   这是拖动窗口边缘时的**性能主开关**: 鼠标拖动一帧只挪一两个像素, 而"卡宽 =
        //   按可用宽度整除列数算出来的"本来就有富余, 得真的塞不下/多塞一张才会破 ——
        //   实测大半的帧走这条早退(每步 ~16ms 的整屏重测全省了)。
        //
        //   ★ 判据必须用 CardWall 的**目标卡宽 × 列数**, 不能用孩子实际的宽度: 泵跑到一半
        //     的墙是半新半旧的, 拿它算会误判(见 CardWall.GetColumns 的说明)。
        //
        //   ★ 最大化/还原那条路走不到这里: 它的收敛由 ConvergeForResize 在状态变化后一次性做完,
        //     不需要靠这一手省时间。(2026-10-02 前这里还带一个 _held 开关挡在中间, 已随自绘动画删除。)
        if (WrapStillFits(ActualWidth)) return;

        // 折行坏了: 目标按**当前**宽度立刻重算(拖动期间也照算, 不走 120ms 合流),
        // 并在**同一拍**把可见带切过去(见 ConvergeBand)。
        var beforeW = TargetWidth;
        var beforeCover = TargetCover;
        CardWall.RefreshNow(this);
        // 目标值一个都没变(拖动幅度小到取整后还是同一个卡宽): 整屏重测可以省掉
        if (Math.Abs(TargetWidth - beforeW) < 0.01 && Math.Abs(TargetCover - beforeCover) < 0.01) return;
        ConvergeBand();
    }

    /// <summary>尺寸稳定后由 <see cref="CardWall.ResizeSettled"/> 调用: 一次性收敛整墙。</summary>
    private void OnResizeSettled()
    {
        if (!_resizePending) return;
        // 不可见(别的 tab / 别的页面)时不付这笔钱: 等它重新可见时再收敛(见 IsVisibleChanged)。
        if (!IsVisible) return;
        _resizePending = false;
        ConvergeForResize();
    }

    /// <summary>
    /// 现在这一版折行在 <paramref name="avail"/> 这个可用宽度下还成立吗 —— 成立就一个孩子都不用动。
    ///
    /// 判据 = "按目标卡宽算出的张数"在新宽度下既塞得下、又塞不下再多一张。
    /// 用 <see cref="CardWall.GetColumns"/> 与 <see cref="TargetWidth"/>(两者都是全墙唯一的目标值)
    /// 而不是孩子的实际宽度 —— 理由见 CardWall.GetColumns 的说明。
    /// </summary>
    private bool WrapStillFits(double avail)
    {
        if (InternalChildren.Count == 0 || double.IsNaN(avail) || avail <= 0) return true;

        var cols = CardWall.GetColumns(this);
        if (cols <= 0) return false;

        var slot = TargetWidth + CardWall.Gap;    // 一张卡连外边距占的横向宽度
        if (double.IsNaN(slot) || slot <= 0) return false;

        // 塞得下 cols 张、又塞不下第 cols+1 张 ⇒ 折行跟当前宽度一致, 不用重排
        return cols * slot <= avail + 0.5 && (cols + 1) * slot > avail;
    }

    /// <summary>把指定 Y 区间内的孩子切到目标尺寸, 返回切了几个。目标由调用方保证已经算过。</summary>
    /// <param name="fromTop">
    /// true = 区间从**墙顶**开始(最大化/还原收敛用, 见 ConvergeForResize);
    /// false = 只处理"视口上下各 <see cref="BandSlack"/>"这一带(墙宽变化用, 见 OnWallResized)。
    /// </param>
    private int ConvergeBand(bool fromTop = false)
    {
        var targetW = TargetWidth;
        var targetCover = TargetCover;
        if (double.IsNaN(targetW) || targetW <= 0) return 0;

        var sv = VisualTreeUtil.FindAncestor<ScrollViewer>(this);
        var top = fromTop || sv == null ? 0 : Math.Max(0, sv.VerticalOffset - BandSlack);
        var bottom = sv == null
            ? double.MaxValue
            : sv.VerticalOffset + Math.Max(1, sv.ViewportHeight) + BandSlack;

        var changed = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            if (CardWall.GetItemWidth(child) == targetW &&
                CardWall.GetCoverHeight(child) == targetCover) continue;   // 已收敛

            var y = i < _arrangeY.Count ? _arrangeY[i] : double.MaxValue;
            // 行高(封面 + 标题 + UP 主)不超过 BandSlack, 所以"行首在带上方"= 整行在带外
            if (y < top || y > bottom) continue;

            EnsureLocalOverride(child);
            child.SetValue(CardWall.ItemWidthProperty, targetW);
            child.SetValue(CardWall.CoverHeightProperty, targetCover);
            changed++;
        }
        if (changed > 0) InvalidateMeasure();
        return changed;
    }

    /// <summary>排一次"布局落地之后再补偏移"。重复调用只会排一次。</summary>
    private void QueueReanchor()
    {
        if (_reanchorQueued) return;
        _reanchorQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _reanchorQueued = false;
            Reanchor();
        });
    }

    private bool _reanchorQueued;

    // ------------------------------------------------------------ 目标宽度变化 → 排渐进
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // CardWall.Update 把新槽位写在这面墙元素上(继承源); 对本面板而言这就是"目标变了"。
        // ★ 只负责排队, 不在这里做任何判断 —— 渐进链路由 PumpStep 里的 pending 驱动,
        //   直到所有孩子收敛才自然停止(见 PumpStep 尾部注释)。
        if (e.Property == CardWall.ItemWidthProperty || e.Property == CardWall.CoverHeightProperty)
            QueuePump();
    }

    /// <summary>目标值(墙元素的继承值)。孩子切换到它就算收敛。</summary>
    private double TargetWidth => CardWall.GetItemWidth(this);
    private double TargetCover => CardWall.GetCoverHeight(this);

    // ------------------------------------------------------------ 状态变化(最大化/还原)时的收敛

    /// <summary>
    /// 窗口最大化/还原后调用: 把**从墙顶到视野下沿**的孩子一次性切到目标尺寸。
    ///
    /// ★★ 为什么收敛段要一直管到墙顶(2026-10-01 验证): 卡片高度是跟着宽度算的
    ///   (封面高 = 卡宽 × 9/16), 视野**上方**每一行的高度都会随宽度变化 ⇒ 它们下面的一切
    ///   (包括用户正看着的这一屏)整体纵向位移。这些卡片要是留到之后才由渐进泵分帧收敛,
    ///   可见区就会被一批一批顶走 —— 滚动锚定能抵掉大部分, 但锚点在这段里会重挑, 抵不干净:
    ///   实测"只收敛可见带"那一版, 验收探针从 PASS 变 FAIL(仍有 194px 位移)。
    ///
    /// ★ 迭代最多 3 趟: 收敛本身会改变行高, 把原本在段外一点的卡片挪进来, 所以每趟收敛后
    ///   重新量一次边界。段内每趟只处理"还没到目标尺寸"的孩子, 没有新的就 break。
    ///
    /// ★ 2026-10-02: 这里**不再挂"保持"开关**(旧 `_held` / `ReleaseHold` 已随自绘动画删除)。
    ///   原因: 那个开关存在的唯一理由是"动画期间不许子树变化, 否则整窗 BitmapCache 纹理失效
    ///   重栅格化"。动画现在由 Windows 自己播, 我们不挂任何纹理, 于是也没有"必须让树静止"的约束;
    ///   收敛完照常让渐进泵接着处理视野外的卡片即可。
    /// </summary>
    public void ConvergeForResize()
    {
        _pumpQueued = false;    // 撤掉已排队的渐进帧, 防止它和这次收敛交错
        _resizePending = false;

        // ★★ 先把目标尺寸按**当前**宽度算出来: Wall 的 SizeChanged 要等布局走完才发, 不强制这一下
        //   这里读到的还是旧窗口算出来的旧目标 ⇒ 下面一个孩子都不会被收敛(探针实测 收敛=0)。
        CardWall.RefreshNow(this);

        if (double.IsNaN(TargetWidth) || TargetWidth <= 0) { QueuePump(); return; }

        // 收敛这一整段(含末尾那次布局)在同一个 dispatcher 回调里跑完, 中间态不会被渲染;
        // 所以只补一次滚动偏移就够。开头先调一次 = 保证锚点已经挑好(不变量成立时是空操作)。
        Reanchor();

        // 迭代最多 3 趟: 收敛本身会改变行高, 把原本在带外一点的卡片挪进带内, 所以每趟收敛后
        // 重新量一次边界。收敛范围内每趟只处理"还没到目标尺寸"的孩子, 没有新的就 break。
        //
        // ★ `fromTop: true` = 从**墙顶**一直收敛到视野下沿(而不是只收敛可见带)。
        //   2026-10-01 试过缩到"只可见带"(想把收敛的同步耗时降下来), 结果验收探针
        //   `bd-probe-cardtwitch` 从 PASS 变 FAIL: 之后仍有 194px 位移。
        //   原因是视野上方那些卡片随后由渐进泵分帧收敛, 每一批都在改上方行高 ⇒ 把可见区
        //   一批批顶走; 滚动锚定能抵掉大部分, 但锚点在这段里会重挑, 抵不干净。
        //   结论: 这条路的正确性依赖"一次性收敛整段", 别再为省那点同步耗时缩范围。
        //   (拖动窗口边缘那条路没有这个问题 —— 那边本来就持续在改版式, 锚定跟得上。)
        for (var pass = 0; pass < 3; pass++)
        {
            if (ConvergeBand(fromTop: true) == 0) break;  // 没有新卡片落进收敛段 → 收敛完备
            UpdateLayout();                  // 付清这一趟的布局, 才能量到新边界
        }

        Reanchor();
        // 收敛完接着把视野外剩下的卡片分帧处理完(以前由 ReleaseHold 在动画收尾时踢这一脚)
        QueuePump();
    }

    /// <summary>
    /// 可见带上下各多收敛的余量。取 320 是因为它比"一行卡片"高(封面 ≈ 0.5625×卡宽 + 标题两行
    /// + UP 主一行, 最宽档下也就 250 上下) —— 于是"行首在带外"就等价于"整行在带外"。
    /// </summary>
    private const double BandSlack = 320;

    /// <summary>
    /// 锚点 = 视野顶部**下方第一行**的第一个孩子(第一个 Y ≥ 滚动偏移的)。
    ///
    /// 为什么不取"视野顶部所在那一行"(最后一个 Y ≤ 偏移的孩子): 那一行被压在视野上沿、
    /// 只露一截, 而它自己的高度在收敛时也会变 —— 钉住它的**上沿**会让它下面的整屏跟着它
    /// 的高度变化一起位移(探针实测残留 758px)。改钉它下面那一行的上沿, 那一行与它下面的
    /// 所有内容(也就是用户真正在看的部分)就都不动了; 只有上沿那一截卡片自己在原地改尺寸。
    ///
    /// ★ 折行是**顺序流式**的: 一行里放几张只由前面已排下的内容决定。所以只要锚点那一行的
    ///   行首钉住了, 它下面每一行的位置与折行结果就都跟着钉住了 —— 可见区不可能再动。
    /// </summary>
    private int AnchorIndex(double offset)
    {
        for (var i = 0; i < _arrangeY.Count; i++)
            if (_arrangeY[i] >= offset) return i;
        return _arrangeY.Count > 0 ? _arrangeY.Count - 1 : -1;
    }

    /// <summary>
    /// 当前盯住的锚点卡片。**身份是粘的**: 一旦选中就一直是它, 直到
    /// ① 偏移被"别人"改掉(用户自己滚了 / 被 ScrollableHeight 夹住), 或 ② 它不再挂在墙上。
    ///
    /// ★★ 为什么必须粘(2026-10-01 探针实测): 每批重新挑"第一个 Y ≥ 偏移的孩子"会**一行一行往上爬** ——
    ///   补偿之后锚点行首正好落在偏移上, 一次取整就能让它翻到上一行, 于是"把上一行的行首钉到视口
    ///   顶部"= 视图瞬间上跳一行; 下一批再翻一行……实测偏移 825→583→553→295→55→0 一路爬到顶。
    ///   身份固定之后不变量变成"这张卡永远贴在屏幕同一个 Y", 每批重算的是**绝对值**,
    ///   既不累积误差也不级联。
    ///
    /// ★★ 为什么记的是**卡片本身**而不是下标: 下标会在"列表被换掉 / 容器重新生成"之后指向另一个
    ///   条目。记卡片的话, 切页(容器还在, 只是整棵树被摘下来再挂回去)能继续钉住同一张卡 ——
    ///   探针实测: 在别的页面上把窗口拉大再切回来, 记下标会漂 96px, 记卡片是 0px。
    ///   卡片真的没了(刷新把列表换了一批)→ IndexOf 返回 -1 → 自然重挑, 也不会钉错人。
    /// </summary>
    private UIElement? _anchorChild;

    /// <summary>
    /// 锚点卡片承载的数据条目(容器上的 DataContext)。
    /// ★ 整页被摘下来再挂回去时 WPF 会**把卡片容器重新生成一遍**, 原来那个 UIElement 已经不在墙上了;
    ///   靠这个数据条目才能把"同一张卡"找回来接着钉(探针实测: 找不到就漂 48px, 找得到 0px)。
    ///   列表真被换掉(刷新)时连条目也一起没了 → 找不到 → 自然重挑, 不会钉错人。
    /// </summary>
    private object? _anchorItem;

    /// <summary>锚点应当贴在屏幕上的 Y(= 面板内 Y − 滚动偏移)</summary>
    private double _anchorScreenY;

    /// <summary>
    /// 上一次排列时**第一个孩子**的数据条目。用来认"整批列表被换掉了"。
    ///
    /// ★★ 为什么必须有这一道(2026-10-01, "点推荐页刷新后页面自己滑动"的最后一环, 探针实测 131px):
    ///   刷新是 `Clear()` + 逐条 `Add`, 换的是**一整批新对象**。这时候旧锚点记的"这张卡该贴在屏幕上
    ///   哪个 Y"已经完全没有意义 —— 版式(每行几张、行高)整个重排过, 拿旧位置去补偏移就是把视图
    ///   硬拽到另一个位置。而 `_anchorItem` 那道"找不到就重挑"拦不住它: 推荐接口每次给的内容**有重叠**,
    ///   同一个条目在刷新后依然找得到(探针里 `条目还在=True`), 于是锚点照旧按旧位置补偿, 一路把
    ///   偏移从 700 拽到 569。
    ///   判据取"第一个孩子的 DataContext"而不是数量: 数量会随分帧填充一格格变(30→10→20→30),
    ///   分不清"换了一批"和"还在填"; 而第一个条目换人只可能是整批被换掉(切 tab / 刷新)。
    /// </summary>
    private object? _firstItem;

    /// <summary>
    /// 挑锚点 / 校验锚点 / 把偏移补回来。**每一次"墙的版式变了"之后都要调**:
    ///   · 渐进泵改完一批卡片尺寸并 UpdateLayout 之后;
    ///   · 墙自己被重新排列之后(SizeChanged, 例如拖窗口边缘让折行变了)。
    ///
    /// 三种情况:
    ///   ① 还没有锚点 / 锚点被回收 → 按当前偏移挑一个(见 <see cref="AnchorIndex"/>), 这一趟不补;
    ///   ② 偏移不是"我们自己设过的值" → 有人动过(用户滚轮 / 被 ScrollableHeight 夹住),
    ///      重新挑一个 —— **绝不跟用户抢滚动条**(判定见 <see cref="IsOurOffset"/>);
    ///   ③ 其余 → 锚点该在屏幕哪个 Y 就把它放回哪个 Y(见 <see cref="EndAnchor"/>)。
    ///
    /// ★ 幂等: 补过之后不变量成立, 再调一次算出来是 0 位移 → 不会来回抖, 也不会死循环。
    /// </summary>
    private void Reanchor()
    {
        var sv = VisualTreeUtil.FindAncestor<ScrollViewer>(this);

        // ★ 整批列表被换掉了(刷新 / 切 tab): 旧锚点记的屏幕位置对新内容没有意义 —— 立刻作废,
        //   否则会把偏移硬拽回旧位置(见 _firstItem 的说明)。判据放在最前面: 这一步必须**先于**
        //   下面那些"内容太短就不补"的短路, 否则刷新中途(视图短暂为空)会把这个信号漏掉。
        var first = (InternalChildren.Count > 0 ? InternalChildren[0] as FrameworkElement : null)?.DataContext;
        if (!ReferenceEquals(first, _firstItem))
        {
            _firstItem = first;
            ForgetAnchor();
            return;
        }

        // 内容短到不用滚动: 这一趟没什么可钉的, 但**不要丢掉锚点** —— 列表被清空重建的那几帧
        // 正好落在这里, 丢了的话"重新长出来"的那一次版式变化就没人补了。
        if (sv == null || sv.ScrollableHeight <= 0) return;
        // 墙被清空: 锚点和"我们设过的偏移"都作废, 等新卡片长出来重新挑
        if (InternalChildren.Count == 0) { ForgetAnchor(); return; }

        var current = sv.VerticalOffset;
        var index = AnchorIndexOf();
        if (index < 0 || !IsOurOffset(current))
        {
            PickAnchor(sv, current);
            return;                 // 刚挑好锚点: 这一趟没有"旧位置"要钉
        }

        EndAnchor(sv, index);
    }

    /// <summary>锚点卡片现在排在墙上第几个。先按容器实例找, 找不到再按数据条目找(见 _anchorItem)。</summary>
    private int AnchorIndexOf()
    {
        if (_anchorChild != null)
        {
            var i = InternalChildren.IndexOf(_anchorChild);
            if (i >= 0) return i;
        }
        if (_anchorItem == null) return -1;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            if (InternalChildren[i] is FrameworkElement fe && ReferenceEquals(fe.DataContext, _anchorItem))
            {
                _anchorChild = InternalChildren[i];    // 容器换过了, 换成新的那个
                return i;
            }
        }
        return -1;
    }

    /// <summary>挑锚点: 记住它是哪张卡、它此刻贴在屏幕哪个 Y, 并把当前偏移记进"我们自己设过的值"。</summary>
    private void PickAnchor(ScrollViewer sv, double current)
    {
        RememberOffset(current);
        var i = AnchorIndex(current);
        _anchorChild = i >= 0 && i < InternalChildren.Count ? InternalChildren[i] : null;
        _anchorItem = (_anchorChild as FrameworkElement)?.DataContext;
        if (_anchorChild == null) return;
        _anchorScreenY = _arrangeY[i] - current;
    }

    /// <summary>丢掉锚点与"我们设过的偏移"记账(内容短到不用滚动时**不要**调它, 见 Reanchor)。</summary>
    private void ForgetAnchor()
    {
        _anchorChild = null;
        _anchorItem = null;
        _recentCount = 0;
        _recentNext = 0;
    }

    /// <summary>
    /// <paramref name="offset"/> 是不是"我们自己设过的值"。
    ///
    /// ★★ 这里必须记**一串**值, 不能只记最后一个(2026-10-01 探针实测踩到): **ScrollToVerticalOffset 是
    ///   延迟生效的**(WPF 排进命令队列, 下一次布局才落位), 而布局变化期间我们会连续请求好几次 ——
    ///   队列里那几个中间值会依次落位。只认最后一个的话, 中间某个值一落位就被当成"用户滚了",
    ///   锚点被丢掉重挑, 于是锚点沿着列表一路往上爬(实测 #18→#15→#14→#10→#5, 可见内容净漂 373px)。
    ///   反向也一样: 请求还没落位时读到的是旧值, 那个也得算"我们的"。
    /// </summary>
    private bool IsOurOffset(double offset)
    {
        for (var i = 0; i < _recentCount; i++)
            if (Math.Abs(offset - _recentOffsets[i]) < 2) return true;
        return false;
    }

    private void RememberOffset(double offset)
    {
        _recentOffsets[_recentNext] = offset;
        _recentNext = (_recentNext + 1) % _recentOffsets.Length;
        if (_recentCount < _recentOffsets.Length) _recentCount++;
    }

    /// <summary>最近 8 次出现过的偏移(我们请求落到的 + 请求前读到的)。超过 8 次的老值会被顶掉</summary>
    private readonly double[] _recentOffsets = new double[8];
    private int _recentNext;
    private int _recentCount;

    /// <summary>
    /// 把锚点那一行"钉"回它原来的屏幕位置: 它上方那些卡片改了高度 ⇒ 锚点在面板内的 Y 变了多少,
    /// 滚动偏移就同步挪多少 ⇒ 锚点以下的一切(也就是用户正看着的这一屏)原地不动。
    ///
    /// ★★ 这就是"拖动窗口边缘 / 切页 / 刷新之后页面自己滑动"的根治点: 卡片高度 = 卡宽 × 9/16,
    ///   窗口一变宽整墙都要重算, 而渐进泵是分帧收敛的 —— 视野**上方**那些卡片晚几帧才收敛,
    ///   它们一变高就把下面整屏往下顶, 于是用户看到"页面自己在滑"。偏移跟着补, 这一下就没了。
    ///
    /// ★ 调用前必须已经 UpdateLayout(): 要读的是**这一批改完之后**的真实位置。
    /// ★ 锚点身份是**粘的**(见 <see cref="Reanchor"/>), 这里算的是"锚点该在屏幕哪个 Y"的
    ///   **绝对值**, 不是逐批累加的增量 —— 既不会累积取整误差, 也不会一行一行往上爬。
    /// ★ 上界夹 ScrollableHeight: 内容变矮、偏移越界时只能滚到能滚的地方, 不硬顶。
    /// </summary>
    private void EndAnchor(ScrollViewer sv, int index)
    {
        if (index < 0 || index >= _arrangeY.Count) return;
        var target = Math.Clamp(_arrangeY[index] - _anchorScreenY, 0, Math.Max(0, sv.ScrollableHeight));
        var before = sv.VerticalOffset;
        if (Math.Abs(target - before) >= 0.5)
        {
            // 记一笔"这是卡片墙自己的滚动": 各页面的自动加载要忽略它(见 WasProgrammaticScroll)。
            CardWall.MarkProgrammaticScroll();
            sv.ScrollToVerticalOffset(target);
        }
        // 两个值都记账: "要求落到的"和"要求之前读到的" —— 延迟生效时读回来的就是后者
        // (见 IsOurOffset 的说明)。不记账的话下一次 Reanchor 会把自己的请求当成"用户滚了"。
        RememberOffset(target);
        RememberOffset(before);
    }

    // ------------------------------------------------------------ 封面内存窗口

    /// <summary>
    /// 滚动停止后多久扫一次(合并滚动过程中的高频 ScrollChanged)。
    /// </summary>
    private const int CoverWindowDebounceMs = 150;

    /// <summary>
    /// 每轮扫描里, **视口外**的余量带最多发起多少个恢复请求。
    ///
    /// ★ 为什么必须限量(2026-11-07 第五次): 释放之后会有几十上百张卡同时变成"没图",
    ///   而 <see cref="CoverLoader"/> 的解码闸门只有 min(CPU/2, 4) 个槽。一次把余量带
    ///   全部发起, 就是几百个任务抢 4 个槽 —— 用户正看着的那一屏排在队伍最后。
    ///   探针实测: 回顶后 InFlight 峰值 212, 视口内 24 张要等近 2 秒才出来。
    ///   ⇒ 视口内的**不限量**(用户看得见, 必须立刻出图), 余量带每轮只发一小批,
    ///     剩下的交给 150ms 防抖/1.5s 心跳的下一轮 —— 反正它们还没进视野。
    /// </summary>
    private const int PrefetchBurst = 8;
    private ScrollViewer? _coverScroll;
    private DispatcherTimer? _coverWindowTimer;
    private bool _coverWindowQueued;

    /// <summary>
    /// **保底心跳**: 即使所有事件都漏了, 也保证每 1.5 秒重扫一次。
    ///
    /// ★★★ 为什么必须有它(2026-11-07 修"排行榜在小窗口下部分封面永久灰"):
    ///   这套机制原来完全**事件驱动** —— ScrollChanged / ArrangeOverride / IsVisibleChanged
    ///   三个来源。任何一个环节漏掉(布局被复用、可见性变化发生在挂载之前、扫描时坐标还是旧值),
    ///   那一格就**永远**停在占位色上, 因为"没有事件 ⇒ 没有下一轮扫描 ⇒ 没有机会纠正"。
    ///   用户看到的就是"小窗口下灰一片, 把窗口调大(强制重排)就好了"。
    ///   ⇒ 加一个低频心跳, 让"最终一定会被扫描到"成为不依赖任何事件的性质。
    ///
    /// ★ 成本: 每 1.5 秒一次 O(N) 的比较(几百张卡 ≈ 微秒级), 且**只在可见时跑**
    ///   (不可见时停掉, 见 OnCoverHeartbeatTick)。这比"某张封面永久灰"便宜得多。
    /// </summary>
    private const int CoverHeartbeatMs = 1500;

    private DispatcherTimer? _coverHeartbeat;

    private void AttachCoverWindow()
    {
        // ★★★ 心跳**必须无条件启动**(2026-11-07 修"切回首页封面不加载"):
        //   旧代码在 `_coverScroll == null` 时直接 return —— 而 Loaded 只发一次,
        //   于是"挂载那一刻 ScrollViewer 还没进可视树"就变成**永久放弃**:
        //   心跳不启动、ScrollChanged 也不订阅, 之后只剩 IsVisibleChanged /
        //   ArrangeOverride 偶然触发扫描。用户看到的就是"切回来一片灰, 滑一下才出来"
        //   (滑动会产生 ScrollChanged —— 但那个事件当时根本没订阅上)。
        //   现在心跳先跑起来, 它每一拍都会调 <see cref="TryAttachScroll"/> 重试发现。
        StartCoverHeartbeat();
        TryAttachScroll();
    }

    /// <summary>
    /// 找到 ScrollViewer 并订阅它的滚动事件。**可以反复调用**(已订阅则直接返回)。
    ///
    /// ★ 为什么要能重试: 卡片墙可能在 ScrollViewer 之前就 Loaded(分帧构建的页面),
    ///   那时找不到 —— 不能让这个"一时找不到"变成永久失效, 见 <see cref="AttachCoverWindow"/>。
    /// </summary>
    private void TryAttachScroll()
    {
        if (_coverScroll != null) return;
        var sv = VisualTreeUtil.FindAncestor<ScrollViewer>(this);
        if (sv == null) return;
        _coverScroll = sv;
        sv.ScrollChanged += OnCoverScrollChanged;
        // 首屏: 数据是分帧填进来的, 填完不会有 ScrollChanged —— 这里补一次。
        QueueCoverWindow();
    }

    private void DetachCoverWindow()
    {
        if (_coverScroll != null)
        {
            _coverScroll.ScrollChanged -= OnCoverScrollChanged;
            _coverScroll = null;
        }
        _coverWindowTimer?.Stop();
        StopCoverHeartbeat();
    }

    private void StartCoverHeartbeat()
    {
        _coverHeartbeat ??= CreateCoverHeartbeat();
        _coverHeartbeat.Start();
    }

    private void StopCoverHeartbeat() => _coverHeartbeat?.Stop();

    private DispatcherTimer CreateCoverHeartbeat()
    {
        var t = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(CoverHeartbeatMs),
        };
        t.Tick += (_, _) =>
        {
            // 不可见时不扫(省 CPU, 也让切走的页面不再动); 可见性一变 IsVisibleChanged 会重排扫描
            if (!IsVisible) return;
            // ★ 顺便重试"发现 ScrollViewer"(见 TryAttachScroll): 挂载时可能还没进可视树。
            TryAttachScroll();
            UpdateCoverWindow();
        };
        return t;
    }

    private void OnCoverScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 只有纵向偏移/视口变化才影响可见带; 内容高度变化(分帧填充)也要重扫
        if (e.VerticalChange == 0 && e.ViewportHeightChange == 0 &&
            e.ExtentHeightChange == 0) return;
        QueueCoverWindow();
    }

    /// <summary>
    /// 合并窗口: 滚动过程中每帧都会发 ScrollChanged, 每次都全表扫一遍是白费。
    ///
    /// ★ **只保证"有一个在途", 不重置计时器**(2026-11-07): 原来的写法是每次进来都
    ///   Stop+Start, 于是持续滚动时这个计时器**永远不触发** —— 一路滚到底的整段时间里
    ///   一张封面都不放, 内存峰值照样堆起来。改成"已排队就直接返回", 它就变成
    ///   一个稳定的 150ms 节流: 长滚动期间每 150ms 收一次, 松手后自然还有最后一次。
    /// </summary>
    private void QueueCoverWindow()
    {
        if (_coverWindowQueued) return;
        _coverWindowQueued = true;
        _coverWindowTimer ??= CreateCoverWindowTimer();
        _coverWindowTimer.Start();
    }

    private bool _coverImmediateQueued;

    /// <summary>
    /// 排版刚变化后**尽快**扫一次(不等 150ms 防抖)。
    ///
    /// ★ 为什么需要: 封面改成懒加载后(见 <see cref="Cover.OnSourceChanged"/>), 首屏卡片
    ///   要等这一轮扫描才会去取图。用 150ms 防抖会让用户先看到一屏占位色再淡入;
    ///   排到 Loaded 优先级(本次布局之后、渲染之前)扫一次, 看起来就是"卡片出来即有图"。
    /// ★ 同一时刻只排一次: 分帧填充连续排版的十几帧里, 前一轮还没跑完就合并掉。
    /// </summary>
    private void QueueCoverWindowImmediate()
    {
        if (_coverImmediateQueued || CardWall.IsResizing) return;
        _coverImmediateQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _coverImmediateQueued = false;
            UpdateCoverWindow();
        });
    }

    private DispatcherTimer CreateCoverWindowTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CoverWindowDebounceMs) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            _coverWindowQueued = false;
            UpdateCoverWindow();
        };
        return t;
    }

    /// <summary>
    /// 按当前滚动位置补图, 并把**常驻封面张数**压在上限之内。
    ///
    /// ★★★ 2026-11-07 第五次重做(用户报"首页一直往下滑刷新, 内存不断涨, 不回落"):
    ///   上一版为了彻底消灭"滚回来封面不加载", 把滚动时的释放**整个拿掉了** ——
    ///   只有"离开屏幕"(切页/切 tab)才放。于是**无限滚动的首页**上滚过多少张就常驻多少张,
    ///   永远不回落。这不是漏了回收, 而是那一版的**设计后果**: 不离开页面就没有释放时机。
    ///
    ///   ⇒ 这次在滚动时恢复释放, 但**判据换了** —— 不再按"哪张在视口内"(前三版都栽在这里:
    ///     坐标缓存过期 / `TransformToAncestor` 滞后 / 滚到底时 `ScrollableHeight` 被夹住,
    ///     一旦把"马上要看到的那张"判成离屏就是不可逆的永久灰)。
    ///     现在按 **"多久没被看到"**:
    ///       · 每轮扫描给"补图区间内"的卡片打一次 <see cref="LastSeenTickProperty"/>;
    ///       · 常驻张数超上限时, 只放**时间戳最旧**的那些(必然是最早被滚过去的);
    ///       · 视口内的卡片每轮都被打戳 ⇒ **结构上不可能是最旧的** ⇒ 不可能被误放。
    ///     再加上"回到屏幕是无条件补图(force)", 即使判错也只是多解码一次, 不会永久灰。
    ///
    ///   这也正好落实用户明确表达的优先级:"单页内存不能超过某个值"优先于"绝不出现灰块"。
    /// </summary>
    private void UpdateCoverWindow()
    {
        if (!CoverWindow.Enabled) return;

        var sv = _coverScroll ??= VisualTreeUtil.FindAncestor<ScrollViewer>(this);
        if (InternalChildren.Count == 0) return;
        if (!IsVisible) return;

        // ★ 兜底: 这面墙不在任何 ScrollViewer 里(正常页面都有, 但模板/测试宿主可能没有)。
        //   没有滚动区就没有"可见带"可算 —— 直接把缺图的补上, 总比永远灰着好。
        if (sv == null)
        {
            RestoreAllMissingCovers();
            return;
        }

        var viewport = sv.ViewportHeight;
        // ★★★ 视口还量不出来(切回来/首次上屏的那一拍)时**必须安排重试**:
        //   这时直接 return 就等于"这一轮什么也没做", 而用户不滚动就不会有下一轮 ——
        //   要等 1.5s 心跳, 表现就是"切回首页一片灰, 过一会儿才慢慢出来"。
        //   短延迟重扫(120ms)让它在布局落地后立刻补上, 用户看不到那段空窗。
        if (double.IsNaN(viewport) || viewport <= 0)
        {
            QueueCoverWindowShort();
            return;
        }

        // 补图区间与边界判据在 CoverWindow(纯函数、可单测)。
        var bands = CoverWindow.For(viewport);
        if (bands == null) return;
        var b = bands.Value;

        // ★★ 只在**确实需要**时付一次同步布局: 孩子数变了(分帧填充)或上一轮布局还没结算。
        //   旧版每轮扫描都无条件 UpdateLayout 两次 —— 扫描本身是 150ms 防抖 + 1.5s 心跳,
        //   但切 tab/首屏那几拍会连着来, 两次全量布局叠起来就是肉眼可见的顿。
        if (!IsArrangeValid || _arrangeY.Count != InternalChildren.Count)
        {
            try { sv.UpdateLayout(); } catch { /* 布局异常: 按现有位置继续(总比不扫好) */ }
        }

        // ★★ 面板在滚动区里的位置只量一次, 之后每个孩子的纵向坐标 = panelTop + _arrangeY[i]。
        //   旧写法对**每个孩子**做一次 TransformToAncestor(几百张卡 = 几百次可视树遍历),
        //   而且拿的是渲染位置, 在布局刚变化时可能滞后。_arrangeY 是 ArrangeOverride 里
        //   亲手下发的坐标, 更准也更便宜(见 ArrangeOverride)。
        double panelTop;
        try { panelTop = TransformToAncestor(sv).Transform(new Point(0, 0)).Y; }
        catch { return; }
        if (double.IsNaN(panelTop)) return;

        var tick = Environment.TickCount64;
        var missingInViewport = 0;
        var visibleCount = 0;

        // ★★★ 2026-11-07 第五次修"回顶要 2 秒才出图":
        //   释放会让几十上百张卡同时变成"没图", 而下一轮扫描会把**补图区间内**的全部
        //   一起发起恢复 —— 探针实测 InFlight 峰值 212 个任务抢 4 个解码槽,
        //   用户正看着的那 24 张排在 200 多个任务后面, 要等近 2 秒。
        //
        //   ⇒ 分两趟:
        //     第 1 趟只补**严格视口内**(用户真正看得见的), 立刻发起;
        //     第 2 趟补视口外的余量带, 而且**限量**(见 PrefetchBurst),
        //        避免一次把解码队列塞满。
        //   这样"看得见的先出图"成为结构性保证, 而不是靠运气。
        var prefetchPending = new List<(double Top, FrameworkElement Card)>();

        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < InternalChildren.Count; i++)
            {
                var child = InternalChildren[i];
                if (child is not FrameworkElement fe) continue;

                // 优先用 ArrangeOverride 记下的坐标(见上面 panelTop 的说明);
                // 极少数还没排过版的孩子才退回一次 TransformToAncestor。
                double top;
                if (i < _arrangeY.Count) top = panelTop + _arrangeY[i];
                else
                {
                    try { top = fe.TransformToAncestor(sv).Transform(new Point(0, 0)).Y; }
                    catch { continue; }
                }
                var bottom = i < _arrangeBottom.Count ? panelTop + _arrangeBottom[i] : top + fe.ActualHeight;
                if (double.IsNaN(top) || double.IsNaN(bottom)) continue;

                var inViewport = CoverWindow.InViewport(top, bottom, viewport);
                if (pass == 0)
                {
                    if (inViewport) visibleCount++;
                    // ★ 记下来给"离开屏幕时只放视口外"用(见 WasInViewportProperty)。
                    child.SetValue(WasInViewportProperty, inViewport);
                }

                if (!CoverWindow.ShouldRestore(top, bottom, b)) continue;

                // ★★★ 打"最后被看到"的时间戳 —— 这是新的释放判据(见方法头)。
                //   补图区间内的卡片每一轮都会刷新它, 所以越早滚过去的时间戳越旧。
                child.SetValue(LastSeenTickProperty, tick);

                // 第 1 趟只要视口内的; 第 2 趟收视口外的余量带。
                if (pass == 0 && !inViewport) continue;
                if (pass == 1 && inViewport) continue;   // 第 1 趟已经处理过

                var borders = CacheCoverBorders(fe);
                var needsAny = false;
                foreach (var border in borders)
                    if (!Cover.HasImage(border)) { needsAny = true; break; }
                if (!needsAny) continue;

                if (pass == 1)
                {
                    prefetchPending.Add((top, fe));
                    continue;
                }

                foreach (var border in borders)
                {
                    if (Cover.HasImage(border)) continue;
                    missingInViewport++;
                    // ★ force: 用户看得到的地方必须立刻有图, 忽略失败冷却。
                    Cover.Restore(border, force: true);
                }
            }
        }

        // ★ 视口外的余量带: 按"离视口近"优先, 且**每轮限量**发起 ——
        //   见上面那段说明(一次铺几百个任务会把解码队列堵死, 用户正看着的反而最后出图)。
        prefetchPending.Sort((x, y) => x.Top.CompareTo(y.Top));
        var budget = PrefetchBurst;
        foreach (var (_, fe) in prefetchPending)
        {
            if (budget <= 0) break;
            foreach (var border in CacheCoverBorders(fe))
            {
                if (Cover.HasImage(border)) continue;
                if (budget-- <= 0) break;
                Cover.Restore(border, force: true);
            }
        }

        // ★ 常驻张数超上限 ⇒ 放掉**最久没被看到**的那些(见 CoverWindow.ResidentScreens)。
        //
        // ★★ 为什么这里**不看** missingInViewport(2026-11-07 第五次, 改了三版):
        //   试过"视口补齐后才释放", 结果回顶时 24 张要 1.8 秒才齐, 这 1.8 秒里
        //   **一张都不放**, 全墙常驻从 67 涨到 238 —— 释放被补图堵死了。
        //   正确的分工是**两件独立的事**:
        //     · 补图: 视口内缺图 → 无条件补(force);
        //     · 释放: 只按"最久没被看到"淘汰, **与补图进度无关**。
        //   ReleaseStalestCovers 里已经排除了"视口内"的卡片, 所以它永远动不到
        //   用户正看着的那一屏 —— 不需要再加一层"等补完"的门。
        var released = ReleaseStalestCovers(CoverWindow.ResidentCap(visibleCount), tick);
        if (released > 0) MemoryTrim.RequestTrim();

        // ★ 视口内还有空封面 ⇒ 再排一轮（恢复是异步的，本轮结束时它们还没贴上）。
        //   若这之后没有任何事件（用户不再滚动），就不会有下一轮扫描 —— 那几张会停在
        //   占位色上直到下一次心跳（1.5s）。这里主动收尾，让"滚到哪都有图"立刻成立。
        if (missingInViewport > 0) QueueCoverWindowShort();
    }

    /// <summary>
    /// 常驻封面超过 <paramref name="cap"/> 时, 放掉**最久没被看到**的卡片封面。
    ///
    /// ★ 为什么按"最后被看到"而不是按"离视口多远": 距离需要一次可靠的坐标判断
    ///   (前三版全栽在这里); 而"最后被看到"是一个**单调递增的时间戳** ——
    ///   谁早被滚过去谁就旧, 与布局时序、`ScrollableHeight` 是否被夹住都无关。
    ///
    /// ★★ 三条**结构性保证**(这次能安全在滚动里释放的全部依据):
    ///   ① **视口内的卡片永不释放** —— 直接读 <see cref="WasInViewportProperty"/>,
    ///      不靠"时间戳够不够新"这种间接推理;
    ///   ② 时间戳为 0(从没被扫描过, 通常是跳滚时被跳过的中间那几屏)按**最旧**处理 ——
    ///      它们必然不在视口内(视口内的每轮都会被扫描并打戳);
    ///   ③ **名额按"视口内有多少张卡"算, 不按"视口内有几张图"算** ——
    ///      后者会让"视口内的图还没加载出来"被误算成"视口外可以多留",
    ///      于是多放一批 → 下一轮又补 → 再放, 形成震荡。
    ///      探针实测: 用"有图张数"时全墙在 54↔77 之间反复跳, 视口封面永远出不来。
    ///
    /// ★ 返回真正放掉的张数(给调用方决定要不要请求一次 GC)。
    /// </summary>
    private int ReleaseStalestCovers(int cap, long now)
    {
        if (cap <= 0) return 0;

        // 收集"当前有图且**不在视口内**"的卡片 + 它们的时间戳。
        // 没有图的卡片不需要放, 也不占常驻名额。
        var candidates = new List<(UIElement Child, long Tick)>(InternalChildren.Count);
        var visibleCards = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            if (child is not DependencyObject d) continue;

            var inViewport = (bool)d.GetValue(WasInViewportProperty);
            if (inViewport) visibleCards++;

            // ★ 保证 ①: 用户正看着的, 一律不动。
            if (inViewport) continue;

            var borders = CacheCoverBorders(child);
            if (borders.Length == 0) continue;

            var hasAny = false;
            foreach (var border in borders)
                if (Cover.HasImage(border)) { hasAny = true; break; }

            // ★★★ 正在加载的卡片**不参与淘汰**(2026-11-07 第五次):
            //   放掉它并不能省下什么 —— 位图还没生成, 而任务已经在解码队列里了。
            //   更糟的是它会立刻被下一轮扫描重新发起, 形成"放掉 → 重发"的循环,
            //   把解码槽全占满(探针实测 InFlight 积压 212 个, 用户回顶 2 秒看不到图)。
            //   ⇒ 只淘汰"已经真的有图"的卡片。它们的位图才是真正的内存大头。
            if (!hasAny) continue;
            if (Cover.IsLoadingNow(child)) continue;

            // ★★★ 保证 ②(2026-11-07 第六次, 修"卡片墙首屏前几张永远灰"):
            //   **从没被扫描到过**的卡片(tick == 0)一律不释放。
            //   首轮扫描时 `TransformToAncestor` 可能因为布局还没结算而抛异常,
            //   那时 `WasInViewport` 保持默认 false —— 这张卡会被误判成"离屏",
            //   于是**用户正看着的那几张被放掉**, 而它们又因为 tick=0 排在最前面,
            //   必然被选中。探针实测: 首屏 24 张里稳定缺 4~12 张, 且总是最前面那几张。
            //   ⇒ 只有"确实被看到过、而且已经很久没被看到"的才允许淘汰。
            //     没扫描到说明位置未知, 宁可多留一张, 也不要在信息不足时放掉。
            var seen = (long)d.GetValue(LastSeenTickProperty);
            if (seen == 0) continue;

            candidates.Add((child, seen));
        }

        if (candidates.Count == 0) return 0;

        // ★ 保证 ③: 名额按**视口内有多少张卡**算(不管有没有图)。
        var keepOutside = Math.Max(0, cap - visibleCards);
        if (candidates.Count <= keepOutside) return 0;

        var need = candidates.Count - keepOutside;
        // ★ 多放一点点(上限的 1/4), 避免"每轮都刚好卡在线上"的反复释放/恢复。
        var margin = Math.Max(1, cap / 4);
        var target = Math.Min(candidates.Count, need + margin);

        candidates.Sort((a, c) => a.Tick.CompareTo(c.Tick));

        var released = 0;
        for (var i = 0; i < candidates.Count && released < target; i++)
        {
            var (child, seen) = candidates[i];
            // ★ 刚被看到的不放: 它们可能是"上一轮刚补上、用户刚滚过去"的。
            //   1 秒的窗口远大于一次扫描间隔(150ms), 正常滚动时不会挡住真正的淘汰
            //   (要淘汰的都是好几屏之前的, 时间戳早就超过 1 秒了)。
            if (seen != 0 && now - seen < 1000) continue;

            foreach (var border in CacheCoverBorders(child))
            {
                if (!Cover.HasImage(border)) continue;
                Cover.Release(border);
                released++;
            }
        }
        return released;
    }

    /// <summary>
    /// 短延迟重扫(120ms): 给"刚发起的恢复"留出异步贴图的时间。
    /// 只用于"视口内还有空封面"的收尾；下一轮若已贴好就不再排，不会形成长循环。
    /// </summary>
    private void QueueCoverWindowShort()
    {
        if (_coverShortQueued) return;
        _coverShortQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, async () =>
        {
            await Task.Delay(120);
            _coverShortQueued = false;
            UpdateCoverWindow();
        });
    }

    private bool _coverShortQueued;

    /// <summary>
    /// 找到并记住这个孩子的封面 Border 列表(找不到返回空数组, 下次再试)。
    ///
    /// ★★★ 只在**找到**时缓存(2026-11-07 修): 旧代码把"搜到 0 个"也写进缓存,
    ///   而"搜到 0 个"有两种情况 —— 一种是这张卡真的没有封面, 另一种是**模板还没应用**。
    ///   后者被缓存下来就是毒药: 这张卡从此永远不参与释放/恢复, 封面要么一直占内存、
    ///   要么永远灰。判据(子树是否为空)并不能可靠区分两者(模板应用了但封面还没实例化也算空)。
    ///   ⇒ 只缓存非空结果。代价是"确实没有封面的卡片"每轮都要 BFS 一次 —— 那种卡片
    ///     在列表里是少数(收藏夹卡片、占位行), 而扫描本身已经很廉价。
    /// </summary>
    private static Border[] CacheCoverBorders(UIElement child)
    {
        if (child is not DependencyObject d) return Array.Empty<Border>();
        if (d.GetValue(CoverBordersProperty) is Border[] cached) return cached;

        var borders = Cover.FindCoverBorders(d);
        if (borders.Length == 0) return Array.Empty<Border>();   // 不缓存"空"(见上)

        d.SetValue(CoverBordersProperty, borders);
        return borders;
    }

    /// <summary>
    /// 整面墙要离开屏幕了(切 tab / 切页)时, 把**视口外**的封面放掉。
    ///
    /// ★★★ 2026-11-07 第四次(用户报"设置页再切回首页 → 封面未加载, 往下滑才出现"):
    ///   上一版是**全放**, 切回来要重新解码整屏 —— 探针实测 150ms 时视口内还缺 24 张、
    ///   到 500ms 才补齐。用户看到的就是"切回来一片灰, 滑一下才慢慢出来"。
    ///
    ///   现在改成**只放视口外**:
    ///     · 视口那一屏原位保留 ⇒ 切回来**零等待**, 一帧都不灰;
    ///     · 视口外(占大头的几十张)照样释放 ⇒ 内存上界仍然成立。
    ///
    /// ★ 为什么"只放视口外"这次不会再翻车(前两轮正是栽在这里):
    ///   前两轮是**滚动时**做释放 —— 判错了就永久灰, 因为滚动路径上没有"一定会恢复"的时机。
    ///   现在释放只发生在**离开屏幕**那一刻, 而**回到屏幕时 <see cref="UpdateCoverWindow"/>
    ///   会把补图区间内所有缺图的卡片无条件贴回来**(force)。所以哪怕这里判错、放多了一张,
    ///   切回来也会立刻补上 —— 错的代价只是"多解码一次", 不是"永久灰"。
    ///
    /// ★ 判据用**上一轮扫描记下的 inViewport**(<see cref="WasInViewportProperty"/>),
    ///   而不是在这里重新算 `TransformToAncestor`: 这一刻布局正在失效(Visibility 刚变),
    ///   位置信息不可靠; 而"上一次可见时的视口"正是用户最后看到的画面。
    ///
    /// ★ 内存上界: 这里先只放"视口外", 让 2 秒内快速切回来的操作为零等待;
    ///   真的停在不看的页面由 <see cref="ScheduleFullReleaseWhenHidden"/> 在
    ///   <see cref="HiddenReleaseDelayMs"/> 后把视口那一屏也放掉。
    ///   ⇒ 隐藏页面的封面不会长期常驻, 与"滚过多少张"无关(2026-11-09 内存优化)。
    /// </summary>
    private void ReleaseOffscreenCovers()
    {
        if (!CoverWindow.Enabled) return;

        var released = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            // ★ 视口内那一屏留着(切回来零等待) —— 见上面的说明。
            if (child is DependencyObject d && (bool)d.GetValue(WasInViewportProperty)) continue;

            foreach (var border in CacheCoverBorders(child))
            {
                Cover.Release(border);
                released++;
            }
        }
        if (released > 0) MemoryTrim.RequestTrim();
    }

    // ------------------------------------------------------------ 隐藏后的延迟全放

    /// <summary>
    /// "隐藏后延迟全放"的代数。每次隐藏/显示都 +1, 用来让之前排队的释放自然作废
    /// (用户在 2 秒内切回来就不该放)。
    /// </summary>
    private int _hiddenReleaseGeneration;

    /// <summary>隐藏多久后连视口那一屏也放掉。</summary>
    private const int HiddenReleaseDelayMs = 2000;

    /// <summary>
    /// 卡片墙不可见(切 tab / 切页面 / 最小化到托盘)后, 延迟把**所有**封面放掉。
    ///
    /// ★ 为什么不在隐藏那一刻立刻全放: 用户经常在 tab 之间快速来回点, 2 秒的宽限期
    ///   让这种操作仍然是"零等待"; 真的停在那儿不看的墙才会被清空。
    /// ★ 为什么必须连视口那一屏也放(2026-11-09 内存优化): 首页有 4 个常驻 tab 墙,
    ///   每面墙都留着"上次可见时那一屏"(≈24 张 ≈ 12MB) ⇒ 光隐藏 tab 就常驻几十 MB。
    ///   释放后回到这一屏时, <see cref="UpdateCoverWindow"/> 会按可见带无条件补回来
    ///   (MemCache 命中则同步贴图; 没命中就多一次解码, 这是内存换观感的刻意取舍)。
    /// </summary>
    private void ScheduleFullReleaseWhenHidden()
    {
        if (!CoverWindow.Enabled) return;
        var gen = ++_hiddenReleaseGeneration;
        _ = ReleaseAllCoversWhenStillHiddenAsync(gen);
    }

    private async System.Threading.Tasks.Task ReleaseAllCoversWhenStillHiddenAsync(int gen)
    {
        await System.Threading.Tasks.Task.Delay(HiddenReleaseDelayMs);
        // await 之后正常会回到 UI 线程(WPF 的 DispatcherSynchronizationContext); 万一没有,
        // 不要跨线程碰视觉树 —— 直接放弃这一轮(下一轮隐藏/心跳还会再排)。
        if (!Dispatcher.CheckAccess()) return;
        if (gen != _hiddenReleaseGeneration) return;   // 期间又显示/隐藏过, 这一轮作废
        if (IsVisible) return;
        ReleaseAllCovers();
    }

    /// <summary>把这一面墙所有缺图的封面都补上(只在"找不到 ScrollViewer、无法按可见带判断"时用)。</summary>
    private void RestoreAllMissingCovers()
    {
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            foreach (var border in CacheCoverBorders(InternalChildren[i]))
            {
                if (!Cover.HasImage(border)) Cover.Restore(border, force: true);
            }
        }
    }

    /// <summary>把这一面墙所有卡片的封面引用都放开(不管在不在视口), 并解钉。</summary>
    private void ReleaseAllCovers()
    {
        var released = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            foreach (var border in CacheCoverBorders(InternalChildren[i]))
            {
                if (!Cover.HasImage(border)) continue;
                Cover.Release(border);
                released++;
            }
        }
        if (released > 0) MemoryTrim.RequestTrim();
    }

    /// <summary>孩子 → 它的封面 Border 列表(缓存, 避免每次扫描都做一次子树遍历)</summary>
    private static readonly DependencyProperty CoverBordersProperty =
        DependencyProperty.RegisterAttached("CoverBorders", typeof(Border[]), typeof(CardWallPanel),
            new PropertyMetadata(null));

    /// <summary>
    /// 上一次扫描时这个孩子是否在**严格视口内**(0 ~ ViewportHeight)。
    ///
    /// ★ 用途: 离开屏幕时只释放"视口外"的封面(<see cref="ReleaseOffscreenCovers"/>) ——
    ///   视口那一屏留着, 切回来零等待。
    /// ★ 为什么不在离开那一刻现算: 那一刻 Visibility 刚变、布局正在失效,
    ///   `TransformToAncestor` 拿到的位置不可靠; 而"上一次可见时的视口"正是用户最后看到的画面。
    /// ★ 判错的代价是**有限的**: 回到屏幕时 <see cref="UpdateCoverWindow"/> 会无条件补图,
    ///   所以放多了只是多解码一次, 不会出现"永久灰"。
    /// </summary>
    private static readonly DependencyProperty WasInViewportProperty =
        DependencyProperty.RegisterAttached("WasInViewport", typeof(bool), typeof(CardWallPanel),
            new PropertyMetadata(false));

    /// <summary>
    /// 这个孩子上一次被**补图区间覆盖**的时刻(<see cref="Environment.TickCount64"/>)。
    ///
    /// ★★★ 这是"滚动时该放掉谁"的**唯一判据**(2026-11-07 第五次)。
    ///   为什么不用坐标: 前三版都按"离视口多远"判, 而那个判断依赖布局时序 ——
    ///   坐标缓存会过期、`TransformToAncestor` 会滞后于 `VerticalOffset`、滚到底时
    ///   `ScrollableHeight` 被夹住会让最后一行的判定翻转。一旦把"用户马上要看到的那张"
    ///   判成离屏, 就是不可逆的永久灰。
    ///   ⇒ 时间戳没有这些问题: 它是一个单调递增的整数, 谁早被滚过去谁就旧。
    ///     视口内的卡片每轮扫描都会刷新它, 所以**结构上不可能**是最旧的。
    /// </summary>
    private static readonly DependencyProperty LastSeenTickProperty =
        DependencyProperty.RegisterAttached("LastSeenTick", typeof(long), typeof(CardWallPanel),
            new PropertyMetadata(0L));

    // ------------------------------------------------------------ 测量 / 排列

    protected override Size MeasureOverride(Size availableSize)
    {
        double constraintW = availableSize.Width;

        foreach (UIElement child in InternalChildren)
        {
            EnsureLocalOverride(child);

            // 与 WrapPanel(ItemWidth=NaN 路径)一致: 孩子按自己的显式 Width 自量。
            // 干净的孩子在这步直接命中 WPF 的测量缓存 —— 这就是"当帧只剩排位置"的关键。
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }

        // ↓↓↓ 以下与 WrapPanel.MeasureOverride 逐行同构(逐个累加, 放不下换行) ↓↓↓
        double x = 0, lineH = 0, extentW = 0, y = 0;
        bool firstInLine = true;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var size = InternalChildren[i].DesiredSize;
            if (double.IsNaN(size.Width) || double.IsNaN(size.Height)) continue;

            if (x + size.Width > constraintW && !firstInLine)
            {
                y += lineH;
                x = 0;
                lineH = 0;
                firstInLine = true;
            }
            x += size.Width;
            lineH = Math.Max(lineH, size.Height);
            extentW = Math.Max(extentW, x);
            firstInLine = false;
        }

        // 渐进泵可能还没排上(比如刚挂进树就跨档): 顺手排一次
        QueuePump();

        return new Size(
            double.IsInfinity(constraintW) ? extentW : constraintW,
            y + lineH);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // 与 WrapPanel.ArrangeOverride 同构。★ 孩子的期望尺寸在渐进切换期间是"混合"的
        // (有的新宽有的旧宽) —— 这只是合法的中间态, 每批切完就整体收敛, 可见区优先。
        double x = 0, lineH = 0, y = 0;
        bool firstInLine = true;

        _arrangeY.Clear();
        _arrangeBottom.Clear();
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var size = child.DesiredSize;
            if (double.IsNaN(size.Width) || double.IsNaN(size.Height))
            {
                _arrangeY.Add(y);
                _arrangeBottom.Add(y);
                continue;
            }

            if (x + size.Width > finalSize.Width && !firstInLine)
            {
                y += lineH;
                x = 0;
                lineH = 0;
                firstInLine = true;
            }

            child.Arrange(new Rect(x, y, size.Width, size.Height));
            _arrangeY.Add(y);
            _arrangeBottom.Add(y + size.Height);

            x += size.Width;
            lineH = Math.Max(lineH, size.Height);
            firstInLine = false;
        }

        // 分帧填充会一批批地把新卡片塞进来(不会发 ScrollChanged), 首屏也可能一次排完
        // 却从没滚过 —— 每次排列之后都排一次封面窗口扫描(内部有 150ms 合并)。
        QueueCoverWindow();
        // ★ 再排一次"布局稳定后立刻扫": 封面现在改成**按可见带懒加载**(见 Cover.OnSourceChanged),
        //   首次上屏不能等 150ms 防抖, 否则会看到一屏占位色再淡入。扫描本身已经改成
        //   O(N) 的坐标比较(不再逐孩子 TransformToAncestor), 每次排列付得起。
        QueueCoverWindowImmediate();

        return finalSize;
    }

    // ------------------------------------------------------------ 渐进切换

    /// <summary>孩子没有本地覆盖值就补上(初值=当时的继承值)。这是"继承变化不再弄脏孩子"的前提。</summary>
    private void EnsureLocalOverride(UIElement child)
    {
        if (child.ReadLocalValue(CardWall.ItemWidthProperty) == DependencyProperty.UnsetValue)
        {
            double w = CardWall.GetItemWidth(child);          // 此刻的继承值
            child.SetValue(CardWall.ItemWidthProperty, w);
            child.SetValue(CardWall.CoverHeightProperty, CardWall.GetCoverHeight(child));
        }
    }

    private void QueuePump()
    {
        // 缩放进行中不排渐进帧: 等 ResizeSettled 后由 ConvergeForResize 一次性收敛整墙。
        // 否则拖动/最大化的每一帧都会排一轮"改 16 张 + 补偏移", 与 OnWallResized 叠成抽搐。
        if (CardWall.IsResizing)
        {
            _resizePending = true;
            return;
        }
        if (_pumpQueued) return;
        _pumpQueued = true;
        // Background 优先级 = DeferredFill 同款: 输入和渲染先走, 重活插在空闲间隙里。
        // 每次只排一跳, 不捕获长生命周期之外的引用 —— 墙被摘掉后链路自然断掉。
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, PumpStep);
    }

    private void PumpStep()
    {
        _pumpQueued = false;

        // 缩放期间不收敛(见 QueuePump): 已经排上的那一跳也要在这里让路。
        if (CardWall.IsResizing)
        {
            _resizePending = true;
            return;
        }

        var targetW = TargetWidth;
        var targetCover = TargetCover;
        if (double.IsNaN(targetW) || targetW <= 0) return;

        // ★ 先把**可见带**整屏一次切过去(ConvergeBand 是一次性、不分批的): 分批切会出现
        //   "一行里一半新一半旧"的中间态, 折行跟着翻一下 —— 那一帧正好在用户眼皮底下。
        //   视野外的照旧分批付(看不见)。拖动时 OnWallResized 已经切过, 这里就是一次空扫描。
        var bandChanged = ConvergeBand();

        // 再收集"还没切到目标"的孩子下标, 再按"离可见区中心近的优先"排,
        // 让用户视野里的卡片第一批就长成新宽度, 中间态尽量不进视野。
        List<int>? pending = null;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            if (CardWall.GetItemWidth(child) != targetW ||
                CardWall.GetCoverHeight(child) != targetCover)
            {
                (pending ??= new List<int>(InternalChildren.Count)).Add(i);
            }
        }
        if (pending == null)
        {
            if (bandChanged == 0) return;   // 全部收敛, 链路自然终止
            InvalidateMeasure();
            return;
        }

        // ScrollViewer 只找一次: 排序要用它的可视带
        var sv = VisualTreeUtil.FindAncestor<ScrollViewer>(this);

        // ★ 改尺寸之前先钉住"用户正看着的那一行"(见 EndAnchor)。这一批里可能有视野**上方**的
        //   卡片 —— 它们一变高就把下面整屏往下顶, 而旧写法对此毫无补偿, 表现就是
        //   "拖动窗口边缘 / 切页 / 刷新之后页面自己在往下滑"(探针实测一次拖动滑走 125~262px)。
        Reanchor();

        if (pending.Count > BatchSize)
        {
            // 可视带只算一次(别放进比较器 —— 那是 O(N log N) 次可视树遍历)
            double top = sv?.VerticalOffset ?? 0;
            double bottom = top + Math.Max(1, sv?.ViewportHeight ?? double.MaxValue);
            pending.Sort((a, b) => OrderByVisibleFirst(a, b, top, bottom));
            pending.RemoveRange(BatchSize, pending.Count - BatchSize);
        }

        foreach (var i in pending)
        {
            var child = InternalChildren[i];
            child.SetValue(CardWall.ItemWidthProperty, targetW);
            child.SetValue(CardWall.CoverHeightProperty, targetCover);
        }

        // 切了值的孩子已脏, 让下一轮测量/排列收走。
        // ★ 链路的存续条件是"还有 pending"(由上面那次扫描决定), 而不是"目标又变了" ——
        //   之前写成"目标没变就提前 return", 结果第一批切完链条就断, 整墙永远停在混合宽度
        //   (探针 M1c 实测 240 个孩子卡在 236 个未收敛)。别再改回去。
        InvalidateMeasure();

        // ★ 当场把这一批的布局付清, 再按锚点把偏移补回去 —— 两件事在同一个 dispatcher 回调里,
        //   中间态没有任何一帧被渲染过, 所以用户看到的是"卡片原地换了尺寸", 而不是"页面滑了一下"。
        //   代价只是把本来就要跑的那次布局提前到这一刻(渲染优先级排在 Background 之后, 总量不变)。
        if (_anchorChild != null && sv != null)
        {
            UpdateLayout();
            Reanchor();
        }

        QueuePump();
    }

    /// <summary>可见区优先: 孩子上次排列的 Y 落在 ScrollViewer 视野内的排最前, 其余按与视野的距离。</summary>
    private int OrderByVisibleFirst(int a, int b, double top, double bottom)
    {
        double ya = a < _arrangeY.Count ? _arrangeY[a] : double.MaxValue;
        double yb = b < _arrangeY.Count ? _arrangeY[b] : double.MaxValue;

        double da = DistanceToBand(ya, top, bottom);
        double db = DistanceToBand(yb, top, bottom);
        int c = da.CompareTo(db);
        // 同带内按索引稳定排序, 免得排序结果每帧乱跳
        return c != 0 ? c : a.CompareTo(b);
    }

    private static double DistanceToBand(double y, double top, double bottom)
        => y < top ? top - y : (y > bottom ? y - bottom : 0);
}
