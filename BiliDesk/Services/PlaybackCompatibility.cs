using System.Threading;

namespace BiliDesk.Services;

/// <summary>
/// 播放兼容性开关(2026-11-22, issue #119)。
///
/// ★ 为什么需要“本次运行强制软解”这个进程级标记:
///   libvlc 3.0.24 在 NVIDIA 上会把 d3d11va 退回一个“刚好够解码器用”的内部输出池,
///   暂停再恢复后可能再也创建不出 DecoderOutputView —— 画面永久停在最后一帧、音频继续。
///   上游 3.0.25 才修, 而 NuGet 只有 3.0.24。检测到这种情况后, 我们把**接下来新建的
///   Media** 都加上 `:avcodec-hw=none`(软件解码), 这样重建能立刻恢复, 同一进程后续
///   也不会再踩同一个池缺陷。
///
/// ★ 为什么不在检测到后直接改用户设置:
///   自动切换只是应急, 软解更吃 CPU(4K/HEVC 尤其明显)。是否永久关闭硬解留给用户在
///   「设置 → 兼容性」决定; 这里只影响当前进程, 退出应用即恢复默认。
/// </summary>
public static class PlaybackCompatibility
{
    private static int _forceSoftwareDecode;

    /// <summary>本次运行是否已经因为“画面冻结”切到软件解码</summary>
    public static bool ForceSoftwareDecode => Volatile.Read(ref _forceSoftwareDecode) != 0;

    /// <summary>新建 Media 时是否要加 `:avcodec-hw=none`(用户设置关硬解, 或本次运行已回退)</summary>
    public static bool ShouldDisableHardwareDecoding =>
        !SettingsStore.Instance.HardwareDecodingEnabled || ForceSoftwareDecode;

    /// <summary>本次运行改用软件解码(幂等; 只在检测到画面冻结时调用)</summary>
    public static void UseSoftwareDecode() => Interlocked.Exchange(ref _forceSoftwareDecode, 1);
}
