using System;

namespace BiliDesk.Helpers;

/// <summary>
/// 弹幕运动数学(2026-11 新增)。
///
/// ★ 为什么从 PlayerWindow 里抽成纯函数:
///   弹幕的位移/透明度现在**完全由视频时钟算出**(见 PlayerWindow.OnDanmakuRendering),
///   不再走 WPF 动画 —— 视频一暂停, 媒体时间就不前进, 弹幕自然停住。
///   这套算式是这套设计的全部要害(线性、无缓动、到点即退), 必须能脱离播放器窗口单独验,
///   否则探针只能照抄一份副本去测, 测的不是产品在跑的那份代码。
///
///   抽出来还有个附带好处: 改算式只有一个地方, 探针与产品永远同源。
/// </summary>
public static class DanmakuMotion
{
    /// <summary>
    /// 滚动弹幕当前的横坐标。
    ///
    /// <paramref name="canvasWidth"/> 画布宽; <paramref name="width"/> 本条弹幕宽;
    /// <paramref name="elapsed"/> 自上屏起经过的**媒体**秒数; <paramref name="totalSeconds"/> 横穿总时长。
    ///
    /// 起点是画布右缘之外(canvasWidth), 终点是 -width —— 即右边缘刚好完全离开左侧。
    /// 线性: 弹幕必须匀速, 任何缓动都会看起来忽快忽慢。
    /// </summary>
    public static double RollingX(double canvasWidth, double width, double elapsed, double totalSeconds)
    {
        if (totalSeconds <= 0) return canvasWidth;
        if (elapsed <= 0) return canvasWidth;
        var p = Math.Min(1.0, elapsed / totalSeconds);
        return canvasWidth - p * (canvasWidth + width);
    }

    /// <summary>
    /// 固定弹幕(顶部/底部)当前的不透明度。
    ///
    /// 前 (total-淡出) 秒保持全不透明, 最后 <paramref name="fadeSeconds"/> 秒线性淡出。
    /// 与改造前那条 Opacity 动画的参数完全一致(原是 BeginTime = 总时长-0.6, 1→0)。
    /// </summary>
    public static double FixedOpacity(double elapsed, double totalSeconds, double fadeSeconds = 0.6)
    {
        if (elapsed <= 0) return 1.0;
        if (elapsed >= totalSeconds) return 0.0;
        var fadeStart = Math.Max(0, totalSeconds - fadeSeconds);
        if (elapsed < fadeStart) return 1.0;
        return Math.Max(0, 1.0 - (elapsed - fadeStart) / fadeSeconds);
    }

    /// <summary>这条弹幕是否已经到点、该从画布上摘掉了</summary>
    public static bool IsFinished(double elapsed, double totalSeconds) => elapsed >= totalSeconds;

    // ------------------------------------------------------------ 速度/字号/不透明度档位(2026-10-12)

    /// <summary>
    /// 速度档位的上下界(闭区间)。0 = 最慢, <see cref="MaxSpeedLevel"/> = 最快。
    /// 与 SettingsStore.DanmakuSpeedLevel 的夹取范围保持一致, 别各写一个。
    /// </summary>
    public const int MinSpeedLevel = 0;
    public const int MaxSpeedLevel = 4;

    /// <summary>各档的**中文文案**(0→最慢 … 4→最快)。设置页与播放器共用这一份。</summary>
    public static readonly string[] SpeedLevelLabels = { "慢", "较慢", "适中", "较快", "极快" };

    /// <summary>滚动弹幕横穿屏幕的时长(秒), 按速度档位索引。★ 档位越大越快。</summary>
    private static readonly double[] ScrollSecondsByLevel = { 12.0, 10.0, 8.0, 6.5, 5.0 };

    /// <summary>固定弹幕停留时长(秒), 按速度档位索引。滚动快则固定久一点才不至于一闪而过。</summary>
    private static readonly double[] FixedSecondsByLevel = { 6.0, 5.0, 4.0, 3.5, 3.0 };

    /// <summary>
    /// 把速度档位换算成滚动弹幕横穿时长(秒)。
    ///
    /// ★ 越界先夹再索引: 配置可能被手改成 -3 或 99, 直接拿去当下标会抛
    ///   IndexOutOfRange —— 那种崩溃发生在**播放弹幕的时候**, 现场几乎无法复现。
    /// </summary>
    public static double ScrollSeconds(int level)
        => ScrollSecondsByLevel[Clamp(level, MinSpeedLevel, MaxSpeedLevel)];

    /// <summary>把速度档位换算成固定弹幕停留时长(秒)。越界同样先夹。</summary>
    public static double FixedSeconds(int level)
        => FixedSecondsByLevel[Clamp(level, MinSpeedLevel, MaxSpeedLevel)];

    /// <summary>该档位的文案; 越界返回中间档的文案(与上面的夹取保持一致)。</summary>
    public static string SpeedLabel(int level) => SpeedLevelLabels[Clamp(level, MinSpeedLevel, MaxSpeedLevel)];

    /// <summary>
    /// 不透明度百分比 → alpha(0~1)。
    ///
    /// ★ 低于 100 时弹幕会**半透明**, 而固定弹幕的淡出是乘在这个基线上的
    ///   (见 <see cref="FixedOpacity"/> 的调用方), 所以这里返回的是"基线 alpha"。
    /// </summary>
    public static double OpacityFromPercent(int percent) => Math.Clamp(percent, 0, 100) / 100.0;

    /// <summary>
    /// 字号百分比 → 实际字号。
    /// <paramref name="baseSize"/> 是基准字号(现在 24)。越界先夹, 免得 0 号字看不见 / 3000 号字铺满屏。
    /// </summary>
    public static double FontSizeFromPercent(int percent, double baseSize)
        => baseSize * Math.Clamp(percent, 20, 400) / 100.0;

    /// <summary>夹取到 [min, max]。比 Math.Clamp 多一层: 纯函数好测, 且不给 Math 换个壳。</summary>
    private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);

    // ------------------------------------------------------------ 弹幕时钟外推(2026-10-12)

    /// <summary>
    /// 由"校准点 + 墙钟流逝"外推出当前媒体时间(秒)。
    ///
    /// ★★★ 为什么这里**必须乘倍速**(用户报"倍速下弹幕滑动不连贯、卡顿"):
    ///   弹幕时钟平时靠 Stopwatch 外推, 只在约 4Hz 的 TimeChanged 上被校准回真实媒体时间。
    ///   而**媒体时间在 2 倍速下走得比墙钟快一倍** —— 外推不乘倍速时, 值会持续落后真实媒体
    ///   时间, 每约 250ms 被校准强行拉回一次。表现就是弹幕匀速走一小段、然后**突然跳**一截,
    ///   也就是"不连贯/卡顿"。倍速越大越明显(0.5x 则反过来, 外推跑太快、每次校准往回拽)。
    ///
    /// ★ 抽成纯函数是为了**能验**: 播放器里那段是"每帧调用、零原生调用"的热路径,
    ///   不适合在探针里起一个真播放器去测; 而这条算式正是修复的全部内容。
    /// </summary>
    /// <param name="baseMediaSeconds">上次校准到的媒体时间(秒)</param>
    /// <param name="wallSeconds">自那次校准起, 墙钟流逝的秒数</param>
    /// <param name="rate">当前播放倍速(1.0 = 原速)。&lt;=0 的脏值按 1.0 处理。</param>
    public static double Extrapolate(double baseMediaSeconds, double wallSeconds, double rate)
        => baseMediaSeconds + wallSeconds * (rate > 0 ? rate : 1.0);
}

