using BiliDesk.ViewModels;

namespace BiliDesk.Helpers;

/// <summary>
/// 「返回上一级」的页面关系(2026-11-28 用户要求: 订阅 / 历史 / 稍后再看 / 收藏 / 离线缓存 /
/// 私信 这几页要能用鼠标侧键、Esc 退回上一级)。
///
/// ★ 纯逻辑, 不碰 WPF —— 判定抽到这里, BiliDesk.Tests 里穷举边界(项目惯例见 长期记忆:
///   "凡是判定/换算能脱离 WPF 的, 都抽到 Helpers 下的静态类再配单测")。
///
/// ★ 为什么"上一级"要看**来源页**而不是写死「我的」:
///   这 6 个页面的入口都不在左侧导航栏上, 是从别的页面点进来的。写死目标(历史→我的、
///   私信→首页)只对**现在的**入口成立; 一旦以后从别处也能进(例如动态里点"观看记录"),
///   写死的目标就把用户送到一个他没去过的地方。来源页记在 MainViewModel 里, 这里只负责
///   "有来源用来源, 没有/不可用时用兜底"这条规则。
/// </summary>
public static class PageBackPolicy
{
    /// <summary>
    /// 有明确"上一级"的页面。共同点: 入口都不在左侧导航栏上, 必须自带一条回去的路。
    /// 其余页面(首页/动态/分区/搜索/我的/设置)没有"上一级"概念, 侧键落在它们上面不动作
    /// —— 见 MainWindow.OnPreviewMouseDown 里那条"不擅自把用户弹回首页"的原则。
    /// </summary>
    public static bool IsChildPage(PageKey key) => key switch
    {
        PageKey.History
            or PageKey.Favorites
            or PageKey.Cache
            or PageKey.WatchLater
            or PageKey.Subscription
            or PageKey.Messages => true,
        _ => false,
    };

    /// <summary>
    /// 来源页不可判定时的兜底上级 —— 与各页现有的返回按钮保持一致:
    /// 私信页的入口在首页右上角工具列(按钮文案就是"返回首页"), 其余五个的入口都在「我的」页。
    /// </summary>
    public static PageKey DefaultParent(PageKey key) => key switch
    {
        PageKey.Messages => PageKey.Home,
        PageKey.History
            or PageKey.Favorites
            or PageKey.Cache
            or PageKey.WatchLater
            or PageKey.Subscription => PageKey.Mine,
        _ => PageKey.Home,
    };

    /// <summary>
    /// 解析"返回上一级"的目标页。
    ///   · 不是子页面(没有上一级) ⇒ 原样返回当前页, 调用方据此判断"没得退";
    ///   · 有来源页且不是自己 ⇒ 回来源页(这才是"进入本页时的那一页");
    ///   · 没有来源(例如启动后直接落到该页)或来源就是自己 ⇒ 用 <see cref="DefaultParent"/>。
    /// </summary>
    public static PageKey Resolve(PageKey current, PageKey? origin)
    {
        if (!IsChildPage(current)) return current;
        if (origin is { } parent && parent != current) return parent;
        return DefaultParent(current);
    }
}
