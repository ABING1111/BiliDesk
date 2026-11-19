using System.Threading;

namespace BiliDesk.Helpers;

/// <summary>
/// 收藏夹集合的"脏标记"广播(2026-11-19 新增"新建收藏夹")。
///
/// ★ 为什么需要: 现在有**三处**能新建收藏夹(收藏页 / 播放器收藏勾选窗 / 「我的」页)。
///   在哪建的不重要, 重要的是另外两处缓存的旧列表得知道"该重拉了" ——
///   否则用户在播放器里建完夹, 切到收藏页看不到它, 只能手动点刷新。
/// ★ 用**版本号**而不是事件: 事件要求订阅者都活着并且记得退订(页面实例是主窗口缓存的,
///   生命周期很长, 但玩家窗口不是); 版本号是拉取式的 —— 消费方在自己方便的时候
///   比一下数字就行, 不会因为漏退订造成泄漏, 也不会在后台线程上碰 WPF 控件。
/// ★ 新建 / 删除收藏夹成功后自增(见 FavFolderActions)。移出/清空只改条目数,
///   不影响"有哪些夹", 收藏页自己会更新 chip 上的数字, 不需要让别的页面重拉。
/// </summary>
public static class FavFolderSync
{
    private static int _version;

    /// <summary>当前版本号。消费方记住"我上次拉列表时的版本", 对不上就该重拉。</summary>
    public static int Version => Volatile.Read(ref _version);

    /// <summary>收藏夹集合发生了"增删"级别的变化(新建 / 删除)。</summary>
    public static void NotifyChanged() => Interlocked.Increment(ref _version);
}
