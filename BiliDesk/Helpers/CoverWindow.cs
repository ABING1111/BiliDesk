namespace BiliDesk.Helpers;

/// <summary>
/// 封面内存窗口的**纯计算**部分(不依赖任何 WPF 类型, 可单测)。
///
/// ★★★ 2026-11-07 第三次重做的结果: 这里只剩"**要不要补图**"的判断, **没有"要不要释放"**。
///
///   为什么把释放判据整个删掉(它曾经是这套机制的核心):
///     前两版都在**滚动时**做"放掉离屏封面、滚回来再贴"。探针反复复现同一个 bug ——
///     只要"释放"与"滚动"耦合, 就必然存在"用户马上要看到的那张正好被放掉"的窗口,
///     而释放是不可逆的(要等下一轮扫描才恢复), 表现就是"封面不加载, 改窗口大小才好"。
///     三次修都没消除这个窗口(标记只置不清 / 坐标缓存过期 / 变换滞后 / 滚动到底时
///     ScrollableHeight 被夹住)。
///
///   ⇒ 现在的分工(结构上不存在"放了又要用"的窗口):
///     · **滚动**: 只补不放(<see cref="CardWallPanel"/> 的 UpdateCoverWindow);
///     · **离开屏幕**(切页 / 切 tab): 整面墙全放, 此时用户看不见, 零风险。
///
///   所以本类只保留"补图余量"这一个概念 —— 它是纯几何, 可以严格单测。
/// </summary>
public static class CoverWindow
{
    /// <summary>
    /// 是否启用"离开屏幕时释放封面"。
    ///
    /// ★ 设环境变量 `BILIDESK_DISABLE_COVER_WINDOW=1` 可一键关闭: 关闭后封面全部常驻
    ///   (与 2026-11-07 之前一致) —— 代价是内存不回落, 但绝对不会出现"封面不加载"。
    ///   留这个开关是为了在观感出问题时能在不改代码的情况下立刻回到最稳的状态。
    /// </summary>
    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("BILIDESK_DISABLE_COVER_WINDOW") != "1";

    /// <summary>
    /// 补图余量(屏): 视口上下各多准备这么多屏的封面。
    ///
    /// ★ 为什么要有余量: 用户滚动时, 下一行卡片可能在**滚动停止之前**就已经进入视野。
    ///   只补"当前严格可见"的话, 快速滚动会看到一片占位色。留半屏 ≈ 一两行卡片,
    ///   让"马上要看到的"提前准备好。
    /// ★ 为什么不更大: 余量越大, 单页常驻的封面越多(内存)。半屏是"顺滑"与"省内存"
    ///   的折中; 用户已确认"单页内存有上限"比"绝不出现灰块"更重要, 但两者在这个值上
    ///   并不冲突 —— 因为**滚动时不再释放**, 余量只影响"提前准备多少", 不影响"会不会灰"。
    /// </summary>
    public const double PrefetchScreens = 0.5;

    /// <summary>补图区间: 与视口有交集、或落在上下各 <see cref="PrefetchScreens"/> 屏之内。</summary>
    public readonly record struct Bands(double Top, double Bottom);

    /// <summary>
    /// 按视口高度算出补图区间。
    /// <paramref name="viewport"/> 非法(0/NaN/负)时返回 <c>null</c> —— 调用方应当直接跳过,
    /// 不要拿 0 去算(那会把区间塌成一条线, 什么都补不到)。
    /// </summary>
    public static Bands? For(double viewport)
    {
        if (double.IsNaN(viewport) || double.IsInfinity(viewport) || viewport <= 0) return null;

        return new Bands(
            Top: -viewport * PrefetchScreens,
            Bottom: viewport * (1 + PrefetchScreens));
    }

    /// <summary>
    /// 这张卡是否应当补图: 它的纵向区间与补图区间有交集。
    ///
    /// ★ 边界用 <c>&gt;=</c> / <c>&lt;=</c>(含等号): 正好贴着区间边沿的卡片算"要补",
    ///   宁可多贴一张, 也不要让视口边缘出现半张灰卡。
    /// </summary>
    public static bool ShouldRestore(double cardTop, double cardBottom, in Bands bands)
        => cardBottom >= bands.Top && cardTop <= bands.Bottom;

    /// <summary>这张卡是否在**严格视口内**(0 ~ viewport)—— 用于"还有空封面就再扫一轮"的收尾判断。</summary>
    public static bool InViewport(double cardTop, double cardBottom, double viewport)
        => !double.IsNaN(viewport) && viewport > 0
           && cardBottom > 0 && cardTop < viewport;
}
