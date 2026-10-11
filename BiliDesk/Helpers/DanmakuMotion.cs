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
}
