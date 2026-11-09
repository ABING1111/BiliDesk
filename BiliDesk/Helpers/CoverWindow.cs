namespace BiliDesk.Helpers;

/// <summary>
/// 封面内存窗口的**纯计算**部分(不依赖任何 WPF 类型, 可单测)。
///
/// ★★★ 2026-11-07 第五次重做: 加了"**常驻张数上限**"(<see cref="ResidentScreens"/>)。
///
///   为什么必须加(用户报"首页一直往下滑刷新, 内存不断涨, 不回落"):
///     上一版为了彻底消灭"滚回来封面不加载", 把**滚动时的释放**整个拿掉了 ——
///     只有"离开屏幕"(切页/切 tab)才放。于是**无限滚动的首页**上,
///     滚过多少张就常驻多少张封面(每张 480x270x4 ≈ 0.5MB), 永远不回落。
///     这不是"漏了回收", 而是那一版的**设计后果**: 只要不离开页面, 就没有任何释放时机。
///
///   为什么这次敢在滚动时释放了 —— 判据换了, 不再依赖"哪张在视口内":
///     前三版都是**按坐标**判"离屏"再释放。那个判断会受布局时序影响(坐标缓存过期 /
///     `TransformToAncestor` 滞后于 `VerticalOffset` / 滚到底时 `ScrollableHeight` 被夹住),
///     一旦把"用户马上要看到的那张"判成离屏, 就是**不可逆**的永久灰 ——
///     因为当时没有"一定会把它补回来"的机制。
///     现在改为**按"多久没被看到"释放**(见 CardWallPanel 的 LastSeenTick):
///       · 每轮扫描给"补图区间内"的卡片打一次时间戳;
///       · 常驻张数超过上限时, 只放**时间戳最旧**的那些 —— 它们必然是最早被滚过去的;
///       · 视口内的卡片每轮都被打戳, **永远不可能是最旧的**, 结构上不可能被误放。
///     而且现在"回到屏幕"是**无条件补图**(force), 即使判错也只是多解码一次。
///
///   这也正好对应用户明确表达的优先级:"单页内存不能超过某个值"比"绝不出现灰块"更重要。
/// </summary>
public static class CoverWindow
{
    /// <summary>
    /// 是否启用整套封面内存管理。
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
    /// </summary>
    public const double PrefetchScreens = 0.5;

    /// <summary>
    /// 常驻封面的上限, 以"当前视口能放几张卡"为单位。
    ///
    /// ★ 为什么是"视口张数的倍数"而不是固定数字: 窗口大小/列数不同, 一屏的张数差很多
    ///   (小窗口 12 张、大屏 40 张)。按倍数算, 上限自动跟着视口走, 不用调参。
    ///
    /// ★ 为什么是 2.5(2026-11-09 从 3 降下来): 补图区间本身就是"视口 ± 半屏"= 2 屏,
    ///   2.5 屏仍有半屏余量 —— 正常滚动时被放掉的永远是"早就滚过去一屏多"的卡片,
    ///   用户不可能马上看到。省下的是每面墙约 1/6 的常驻封面(≈6MB/屏 × 0.5 屏)。
    ///   首页有 4 个常驻 tab 墙, 这一项在隐藏 tab 全放(B2)之外再兜一层。
    ///
    /// ★ 内存含义: 常驻封面 ≈ 2.5 屏 × 每张 0.33~0.5MB。以首页 24 张/屏计 ≈ 60 张 ≈ 20~30MB,
    ///   与"滚过多少张"无关 —— 这正是用户要的"单页内存有上限"。
    /// </summary>
    public const double ResidentScreens = 2.5;

    /// <summary>
    /// 当前允许常驻的封面张数。视口还没量出来(<paramref name="visibleCount"/> ≤ 0)时返回 0,
    /// 调用方应当**跳过释放**(没有视口就没有"上限"可言, 更不能一次放光)。
    /// </summary>
    public static int ResidentCap(int visibleCount)
        => visibleCount <= 0 ? 0 : (int)Math.Ceiling(visibleCount * ResidentScreens);

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
