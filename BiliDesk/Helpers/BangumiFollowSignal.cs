namespace BiliDesk.Helpers;

/// <summary>
/// 「追番状态被改过」的全局信号(2026-11-19)。
///
/// ★ 为什么需要它: 追番/取消追番发生在**番剧详情页**(内嵌页), 而「我的追番」那一栏在
///   **首页的追番 tab** 里。内嵌页关掉时 HomePage 并不会重新拉数据 —— 用户在详情页里
///   追了一部番, 退回去却在「我的追番」里找不到它, 会以为追番功能是坏的。
///
/// ★ 为什么是"版本号"而不是事件:
///   唯一的订阅方是常驻的 HomeViewModel, 用事件就要多写一对订阅/退订 ——
///   而这个类的生命周期是进程级, 漏退订就是永久泄漏。
///   版本号只在"切回追番 tab"时读一次, 成本为零, 也不存在生命周期问题。
///
/// ★ 为什么只影响「我的追番」而不影响「番剧推荐」:
///   推荐栏是"滚了很久才翻到的"分页列表, 重拉会把它清回第一页
///   (见 HomeViewModel.SwitchToBangumiAsync 的说明)。追番只改前者。
/// </summary>
public static class BangumiFollowSignal
{
    /// <summary>每次追番/取消追番成功后 +1。</summary>
    public static int Version { get; private set; }

    /// <summary>追番状态变了: 通知下一次进入追番页重新拉「我的追番」。</summary>
    public static void Bump() => Version++;
}
