using System;

namespace BiliDesk.Helpers;

/// <summary>
/// "自动检查更新失败要不要提示"的判定。
///
/// 抽成纯函数的原因: 这是典型的"多一天/少一天"边界问题 —— 24 小时节流、完全断网静默、
/// 系统时钟被改坏留下的未来时间, 在真机上几乎没法稳定复现, 放进单测才能穷举。
/// </summary>
public static class UpdateFailurePolicy
{
    /// <summary>同一个失败最多每 24 小时提示一次(每次启动都失败也不会天天/每次启动都弹)。</summary>
    public static readonly TimeSpan NoticeInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// 是否应该弹出"检查更新失败"提示窗。
    /// </summary>
    /// <param name="networkAvailable">本机是否有可用网络(完全断网时不提示, 用户明显知道没网)。</param>
    /// <param name="lastNoticeUtc">上一次弹出提示的时间(UTC); null = 从未提示过。</param>
    /// <param name="nowUtc">当前时间(UTC), 由调用方传入便于测试。</param>
    public static bool ShouldShowNotice(bool networkAvailable,
        DateTimeOffset? lastNoticeUtc, DateTimeOffset nowUtc)
    {
        if (!networkAvailable) return false;
        if (lastNoticeUtc is not { } last) return true;

        // 未来时间只可能来自改系统时钟或手改配置 —— 按"没记过"处理,
        // 否则用户会永远看不到失败提示, 还以为自动检查在正常工作。
        if (last > nowUtc) return true;
        return nowUtc - last >= NoticeInterval;
    }
}
