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
    /// 最大化/还原动画开始前调用: 把**可视区**(滚动视野 ±40px)的孩子一次性切到目标宽度,
    /// 然后挂起渐进泵。可视区收敛的重测(≈30 张 × 0.8ms)由调用方随后的一次 UpdateLayout
    /// 一次付清 —— 动画期间树不再变化。
    /// </summary>
    public void ConvergeVisibleThenHold()
    {
        _pumpQueued = false;    // 撤掉已排队的渐进帧, 防止它和这次收敛交错
        _held = true;

        var targetW = TargetWidth;
        var targetCover = TargetCover;
        if (double.IsNaN(targetW) || targetW <= 0) return;

        // 可视带只算一次; 上下各放 40px 余量, 盖住半截露出的卡片
        var sv = VisualTreeUtil.FindAncestor<ScrollViewer>(this);
        double top = 0, bottom = double.MaxValue;
        if (sv != null)
        {
            top = sv.VerticalOffset - 40;
            bottom = sv.VerticalOffset + Math.Max(1, sv.ViewportHeight) + 40;
        }

        int n = InternalChildren.Count;
        for (int i = 0; i < n; i++)
        {
            var child = InternalChildren[i];
            double y = i < _arrangeY.Count ? _arrangeY[i] : double.MaxValue;
            if (y < top || y > bottom) continue;    // 视野外: 交给动画结束后的渐进

            EnsureLocalOverride(child);
            child.SetValue(CardWall.ItemWidthProperty, targetW);
            child.SetValue(CardWall.CoverHeightProperty, targetCover);
        }
        InvalidateMeasure();
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

        if (pending.Count > BatchSize)
        {
            // 可视带只算一次(别放进比较器 —— 那是 O(N log N) 次可视树遍历)
            var sv = VisualTreeUtil.FindAncestor<ScrollViewer>(this);
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
