using System.Windows;

namespace BiliDesk.Helpers;

/// <summary>
/// 行内 chip 的圆角(2026-11-28)。
///
/// 为什么需要这个附加属性: `Themes/Styles.xaml` 的 <c>SortChipStyle</c> 被三处共用 ——
/// 播放器评论区「热门 / 时间」、动态页「动态卡 / 卡片墙」、UP 主页投稿排序。
/// 前两处是 12px 行内小词, 圆角必须是 6(全圆角会显得像按钮而不是"行内的一个可点词");
/// 而 UP 主页那一排要和旁边的搜索圆(34×34、圆角 17)并排, 得是 34 高的胶囊。
///
/// ★ 圆角写在 ControlTemplate 的 Border 上, **Button 自己没有 CornerRadius 属性**,
///   所以没法用 Style Setter 覆盖 —— 复制一份模板是最容易走歪的做法(改了一处另一处忘)。
///   这里用一个 Inherits 的附加属性让**同一份模板**既能出小矩形也能出胶囊。
/// ★ Inherits: 写在容器(StackPanel)上, 里面所有 chip 自动读到, 不必逐个设。
/// ★ 默认 6 = 三处老调用方的既有观感, 不设就完全不变。
/// </summary>
public static class ChipShape
{
    /// <summary>chip 底衬的圆角半径(DIP)。默认 6(行内小词); 34 高的胶囊用 17。</summary>
    public static readonly DependencyProperty RadiusProperty =
        DependencyProperty.RegisterAttached(
            "Radius", typeof(double), typeof(ChipShape),
            new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.Inherits));

    public static double GetRadius(DependencyObject d) => (double)d.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject d, double value) => d.SetValue(RadiusProperty, value);
}
