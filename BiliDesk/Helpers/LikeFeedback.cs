using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BiliDesk.Helpers;

/// <summary>
/// 点赞图标的"弹一下"反馈。参数与播放器三连按钮完全一致(1 → 1.3 → 1, 420ms),
/// 动态卡/动态详情/评论共用一个入口, 避免三处各写一套之后速度/幅度慢慢漂移。
///
/// ★ 每次都新建 ScaleTransform: 样式 Setter 里的 Freezable 会被 WPF 冻结,
///   对冻结对象 BeginAnimation 会直接抛异常(播放器那边踩过)。
/// </summary>
public static class LikeFeedback
{
    public static void PlayPop(FrameworkElement? target)
    {
        if (target == null) return;
        var scale = new ScaleTransform(1, 1);
        target.RenderTransformOrigin = new Point(0.5, 0.5);
        target.RenderTransform = scale;

        var anim = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(420)
        };
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromPercent(0)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.3, KeyTime.FromPercent(0.4))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        });

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }
}