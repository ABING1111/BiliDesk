using System;

namespace BiliDesk.Helpers;

/// <summary>
/// 播放器简介「要不要折叠」的**纯判定逻辑**(不依赖 WPF, 可直接单测)。
///
/// ★ 为什么折叠本身要有一个判定: 简介折叠最初是**给下面的「相关视频」让位置**
///   (很多 UP 主的简介动辄几百字, 不折的话右半屏全被它吃掉, 用户要滚很久才看得到推荐)。
///   于是"要不要折"就不是纯粹的排版问题, 而是跟着「显示相关视频」开关走的:
///     · 相关视频显示 ⇒ 简介默认折到 <see cref="CollapsedHeight"/>, 超长才给「展开简介」;
///     · 相关视频关闭 ⇒ 下面没有内容要腾位置, 整段展开、连按钮都不该出现。
///   ★ 这条联动漏过一次(2026-11-16 用户报"关了相关视频, 简介还是折着"):
///     上一版 UpdateDescFold 不看设置, 一律按 108 折起来 —— 判定抽到这里并补上
///     `relatedVisible` 参数, 单测就能把这条不变量钉住。
///
/// ★ 与 PopupToggle / PlayerWindowPlacement 同款拆法: 判定是纯函数, 测量与应用
///   (MaxHeight / 按钮可见性)留在 PlayerWindow.UpdateDescFold 里。
/// </summary>
public static class DescFoldPolicy
{
    /// <summary>
    /// 折叠后的最大高度(12px 字号 × 行高 ≈ 18px, 这里约 6 行)。
    /// ★ 必须与 PlayerWindow.xaml 里的 DescCollapsedHeight 资源同值:
    ///   那个资源只负责"首次布局前的初值", 运行期全部由本类算出的结果覆盖。
    /// ★ 阈值与 MaxHeight 用同一个数, 否则会出现"明明没超却冒出展开按钮"或反之。
    /// </summary>
    public const double CollapsedHeight = 108;

    /// <summary>判定结果(三个值必须一起应用, 拆开写会自相矛盾)。</summary>
    /// <param name="ShowButton">是否显示「展开/收起简介」按钮</param>
    /// <param name="Expanded">应用后简介是否处于展开态(不显示按钮时一律复位)</param>
    /// <param name="MaxHeight">要写给 DescText.MaxHeight 的值(展开且超长时是内容实际高度)</param>
    public readonly record struct Result(bool ShowButton, bool Expanded, double MaxHeight);

    /// <summary>
    /// 按「相关视频是否显示 + 当前折叠态 + 简介不限高时需要的高度」判定结果。
    /// </summary>
    /// <param name="relatedVisible">设置里是否显示相关视频(关闭时简介完全不折叠)</param>
    /// <param name="expanded">当前是否已展开</param>
    /// <param name="needed">简介**不设上限**测量出来的高度(关闭相关视频时可不测量, 传 0 即可)</param>
    public static Result Decide(bool relatedVisible, bool expanded, double needed)
    {
        // 相关视频不显示了 ⇒ 简介没有"让位"的必要: 整段展开, 也不给展开/收起按钮。
        // ★ 状态必须复位(Expanded=false): 否则用户重新打开相关视频时, 简介会带着
        //   上一次的展开态回来 —— 与"默认折叠"的预期不一致。
        if (!relatedVisible) return new Result(false, false, double.PositiveInfinity);

        // +1 容差: 亚像素误差会让"刚好 6 行"被误判成没超, 按钮该出时不出。
        var overflows = needed > CollapsedHeight + 1;
        if (!overflows) return new Result(false, false, CollapsedHeight);

        // 展开态给实际高度(内容可能因为换字号/换行变化), 折叠态回到 6 行。
        return new Result(true, expanded, expanded ? Math.Max(needed, CollapsedHeight) : CollapsedHeight);
    }
}
