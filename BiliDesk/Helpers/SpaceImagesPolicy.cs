using BiliDesk.Models;

namespace BiliDesk.Helpers;

/// <summary>
/// UP 主页「图文」Tab 收哪些动态。
///
/// ★ 2026-11-28 修「UP 主页图文 Tab 不显示专栏」:
///   feed/space 返回的是综合动态, 专栏解析出来是 <see cref="DynamicKind.Article"/>;
///   之前这里只认 <see cref="DynamicKind.ImageText"/>, 专栏被整条跳过。
///   专栏卡片本身(标题 / 封面 / 点开阅读正文)全链路早已支持, 缺的只是放行。
/// ★ 纯文字动态(<see cref="DynamicKind.Text"/>)仍不收: 图文 Tab 的语义是"有图的内容",
///   纯文字请到「动态」Tab 看。
/// </summary>
public static class SpaceImagesPolicy
{
    /// <summary>这条动态是否属于 UP 主页「图文」Tab(图文 / 专栏)。</summary>
    public static bool Accepts(DynamicItem? item)
        => item?.Kind is DynamicKind.ImageText or DynamicKind.Article;
}
