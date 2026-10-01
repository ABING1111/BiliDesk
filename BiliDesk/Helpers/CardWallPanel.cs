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

    /// <summary>
    /// 状态过渡(最大化/还原动画)期间的"静止"开关, 由 <see cref="FluentWindow"/> 调
    /// <see cref="ConvergeVisibleThenHold"/> / <see cref="ReleaseHold"/> 控制。
    ///
    /// ★★ 为什么必须配合动画: 最大化动画给窗口根元素挂 BitmapCache(整窗栅格化成一张
    /// GPU 纹理), 而**任何子树内容变化都会让这张纹理失效重栅格化** —— 渐进切宽的每一批
    /// (16 张卡)正好都在动画的 200ms 里发生, 于是动画每帧都在重栅格化整窗,
    /// 用户看到的就是"放大过程抽动"(推荐页滑下去再最大化最明显; 设置页没有卡片墙所以正常)。
    /// 解法 = 动画前把**可视区**一次性收敛(树在动画期间完全静止 ⇒ 纹理不失效 ⇒ 平滑),
    /// 动画结束后放开, 剩下的都是视野外的卡片, 怎么切都看不见。
    /// </summary>
    private bool _held;

    /// <summary>上次 Arrange 时每个孩子的 Y 坐标(与 InternalChildren 对齐), 供"可见区优先"排序。</summary>
    private readonly List<double> _arrangeY = new();

    public CardWallPanel()
    {
        // 挂上 CardWall.Enable 之后, SizeChanged 会驱动 CardWall.Update 写 ItemWidth ——
        // 那正是本面板要监听的"目标变化"信号, 见 OnPropertyChanged。
    }

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

    // ------------------------------------------------------------ 状态过渡协作(见 _held 的说明)

    /// <summary>
    /// 最大化/还原动画开始前调用: 把**从整墙顶部一直到视野下沿**的孩子一次性切到目标尺寸,
    /// 然后挂起渐进泵。这批重测由本方法末尾那次 UpdateLayout 一次付清 —— 动画期间树不再变化。
    ///
    /// ★★ 为什么视野**上方**的卡片也必须一起收敛(2026-10-02, 用户报的"卡片抽动很明显"):
    ///   卡片高度是跟着宽度算的(封面高 = 卡宽 × 9/16), 所以变宽 = 变高 ⇒ 上方每一行的行高都变
    ///   ⇒ **它们下面的一切(包括用户正看着的这一屏)整体纵向位移**。只收敛"可视带"时, 上方那些
    ///   卡片要等动画结束后才按 16 张一批慢慢收敛, 可见区就被一批一批顶走; 更糟的是收敛期间整墙
    ///   处在**半宽半窄**的中间态, 折行位置跟着变, 版式churn 能到上千像素(探针实测内容高
    ///   6946↔8731), 逐批补偿也追不上。
    ///   探针 `%TEMP%\bd-probe-cardtwitch`(滚一屏后最大化/还原 3 轮)实测: 只收敛可视带时,
    ///   用户正看着的那张卡在屏幕上按 30px 一跳、光"动画之后"就跳 11 次累计 592px。
    ///
    /// ★ 一次性收敛之后, 可见区**只经历一次**版式变化, 再配上末尾的滚动锚定, 连这一次都不会动;
    ///   而且之后渐进泵只碰"视野下方"的孩子 —— 顺序流式折行里, 后面的孩子**不可能**影响前面行的
    ///   位置与折行结果, 所以可见区从此不可能再动(这是可证的, 不是"应该没问题")。
    ///
    /// ★ 代价与"视野上方的卡片数"成正比(每张约 0.8ms): 滚一屏约几十张(几十毫秒), 滚得越深越贵 ——
    ///   但这是一次性付清, 且发生在动画第一帧之前; 视野下方的仍交给渐进泵(看不见, 分帧付)。
    /// </summary>
    public void ConvergeVisibleThenHold()
    {
        _pumpQueued = false;    // 撤掉已排队的渐进帧, 防止它和这次收敛交错
        _held = true;

        // ★★ 先把目标尺寸按**当前**宽度算出来: Wall 的 SizeChanged 要等布局走完才发, 不强制这一下
        //   这里读到的还是旧窗口算出来的旧目标 ⇒ 下面一个孩子都不会被收敛(探针实测 收敛=0)。
        CardWall.RefreshNow(this);

        var targetW = TargetWidth;
        var targetCover = TargetCover;
        if (double.IsNaN(targetW) || targetW <= 0) return;

        var sv = VisualTreeUtil.FindAncestor<ScrollViewer>(this);

        // 收敛"整墙顶部 → 视野下沿"。★ 要迭代: 收敛本身会改变上方行高, 把原本在视野下方一点的
        // 卡片顶进视野(探针实测残留 565px 位移就是这么来的), 所以每趟收敛后重新量一次下沿。
        // 最多 3 趟: 每趟只处理"还没到目标尺寸"的孩子, 收敛完就 break。
        //
        // ★★ 这里**不做**滚动锚定(2026-10-02 实测后移除): 一次性收敛把版式变化全部挡在动画第一帧
        //   之前 —— 这一整段(收敛+布局+挂 BitmapCache+起动画)在同一个 dispatcher 回调里跑完,
        //   中间状态根本没被渲染过, 所以"内容位移"用户看不见。反过来说, 锚定会把滚动偏移一点点
        //   改掉(探针 8 轮实测偏移 700→818→506→…→0, 也就是来回切几次后视图自己爬到顶部), 得不偿失。
        for (var pass = 0; pass < 3; pass++)
        {
            var bottom = sv == null
                ? double.MaxValue
                : sv.VerticalOffset + Math.Max(1, sv.ViewportHeight) + 40;
            var changed = 0;
            var n = InternalChildren.Count;
            for (var i = 0; i < n; i++)
            {
                var child = InternalChildren[i];
                if (CardWall.GetItemWidth(child) == targetW &&
                    CardWall.GetCoverHeight(child) == targetCover) continue;   // 已收敛

                var y = i < _arrangeY.Count ? _arrangeY[i] : double.MaxValue;
                if (y > bottom) continue;    // 视野下方: 不影响可见区, 交给动画结束后的渐进

                EnsureLocalOverride(child);
                child.SetValue(CardWall.ItemWidthProperty, targetW);
                child.SetValue(CardWall.CoverHeightProperty, targetCover);
                changed++;
            }
            if (changed == 0) break;         // 没有新卡片落进视野 → 收敛完备

            InvalidateMeasure();
            UpdateLayout();                  // 付清这一趟的布局, 才能量到新下沿
        }
    }

    /// <summary>
    /// 锚点 = 视野顶部**下方第一行**的第一个孩子(第一个 Y ≥ 滚动偏移的)。
    ///
    /// 为什么不取"视野顶部所在那一行"(最后一个 Y ≤ 偏移的孩子): 那一行被压在视野上沿、
    /// 只露一截, 而它自己的高度在收敛时也会变 —— 钉住它的**上沿**会让它下面的整屏跟着它
    /// 的高度变化一起位移(探针实测残留 758px)。改钉它下面那一行的上沿, 那一行与它下面的
    /// 所有内容(也就是用户真正在看的部分)就都不动了; 只有上沿那一截卡片自己在原地改尺寸。
    /// </summary>
    private int AnchorIndex(double offset)
    {
        for (var i = 0; i < _arrangeY.Count; i++)
            if (_arrangeY[i] >= offset) return i;
        return _arrangeY.Count > 0 ? _arrangeY.Count - 1 : -1;
    }

    /// <summary>动画结束后调用: 放开渐进泵, 剩余(视野外)的孩子继续分帧收敛。</summary>
    public void ReleaseHold()
    {
        if (!_held) return;
        _held = false;
        QueuePump();
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
        if (_held) return;   // 状态过渡动画期间树必须静止(见 _held 的说明), 放开后由 ReleaseHold 重新排队

        var targetW = TargetWidth;
        var targetCover = TargetCover;
        if (double.IsNaN(targetW) || targetW <= 0) return;

        // 先收集"还没切到目标"的孩子下标, 再按"离可见区中心近的优先"排,
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
        if (pending == null) return;   // 全部收敛, 链路自然终止

        // ScrollViewer 只找一次: 排序要用它的可视带
        var sv = VisualTreeUtil.FindAncestor<ScrollViewer>(this);

        if (pending.Count > BatchSize)
        {
            // 可视带只算一次(别放进比较器 —— 那是 O(N log N) 次可视树遍历)
            double top = sv?.VerticalOffset ?? 0;
            double bottom = top + Math.Max(1, sv?.ViewportHeight ?? double.MaxValue);
            pending.Sort((a, b) => OrderByVisibleFirst(a, b, top, bottom));
            pending.RemoveRange(BatchSize, pending.Count - BatchSize);
        }

        // 这里**不做**滚动锚定: 视野上方＋可见区已经在 ConvergeVisibleThenHold 里一次性收敛过,
        // 剩下这些待收敛的孩子全在视野下方 —— 顺序流式折行里后面的孩子不可能改变前面行的位置,
        // 所以可见区不会再动。(2026-10-02 试过"每批按锚点补偏移", 实测反而更糟: 中间态折行churn
        //  使锚点每批换人, 可见内容被越推越远; 详见 ConvergeVisibleThenHold 的说明。)
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
