using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
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

    /// <summary>上次 Arrange 时每个孩子的 Y 坐标(与 InternalChildren 对齐), 供"可见区优先"排序。</summary>
    private readonly List<double> _arrangeY = new();

    public CardWallPanel()
    {
        // 挂上 CardWall.Enable 之后, SizeChanged 会驱动 CardWall.Update 写 ItemWidth ——
        // 那正是本面板要监听的"目标变化"信号, 见 OnPropertyChanged。
        //
        // ★ 这里还要做两件事(拖窗口边缘 / 最大化时每帧都走这条路):
        //   ① **当帧**把可见带的孩子切到"按本次宽度算出的目标宽度" —— 见 OnWallResized;
        //   ② 按锚点把滚动偏移补回来 —— 见 Reanchor。
        SizeChanged += (_, _) => OnWallResized();
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
        if (Math.Abs(target - before) >= 0.5) sv.ScrollToVerticalOffset(target);
        // 两个值都记账: "要求落到的"和"要求之前读到的" —— 延迟生效时读回来的就是后者
        // (见 IsOurOffset 的说明)。不记账的话下一次 Reanchor 会把自己的请求当成"用户滚了"。
        RememberOffset(target);
        RememberOffset(before);
    }

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
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var size = child.DesiredSize;
            if (double.IsNaN(size.Width) || double.IsNaN(size.Height))
            {
                _arrangeY.Add(y);
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

            x += size.Width;
            lineH = Math.Max(lineH, size.Height);
            firstInLine = false;
        }

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
        if (_pumpQueued) return;
        _pumpQueued = true;
        // Background 优先级 = DeferredFill 同款: 输入和渲染先走, 重活插在空闲间隙里。
        // 每次只排一跳, 不捕获长生命周期之外的引用 —— 墙被摘掉后链路自然断掉。
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, PumpStep);
    }

    private void PumpStep()
    {
        _pumpQueued = false;

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
