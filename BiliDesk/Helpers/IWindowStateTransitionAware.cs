namespace BiliDesk.Helpers;

/// <summary>
/// "窗口状态即将变化"的提前通知(最大化 / 还原 / 最小化)。
///
/// ★ 为什么需要这个接口:
///   WPF 在还原最大化时,**先**发 SizeChanged/LocationChanged(此时 WindowState 已经是
///   Normal), **后**发 StateChanged。等 StateChanged 再挂"稳定期"闸门就晚了 ——
///   中间那次事件会把"位置已还原、尺寸还是最大化尺寸"的中间态采成常规几何
///   (播放器窗口记忆 2026-11-14 实测)。
///
/// 由 <see cref="BiliDesk.Views.Controls.WindowButtons"/> 在改 WindowState 之前调用,
/// 以及 <see cref="BiliDesk.Views.FluentWindow"/> 在收到 WM_SYSCOMMAND(标题栏双击 /
/// 系统菜单 / 任务栏右键最大化)时调用。
/// </summary>
public interface IWindowStateTransitionAware
{
    /// <summary>马上要改 WindowState 了 —— 实现方应在此之前进入"几何不可信"状态。</summary>
    void OnWindowStateTransitionStarting();
}
