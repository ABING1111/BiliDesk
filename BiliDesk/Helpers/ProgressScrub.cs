using System;

namespace BiliDesk.Helpers;

/// <summary>
/// 进度条"按住任意位置拖动"的**纯换算逻辑**(不依赖 WPF, 可直接单测)。
///
/// ★ 为什么单独抽出来(与 <see cref="PopupToggle"/> / <see cref="PagerMath"/> 同款拆法):
///   指针位置 → 量程值的映射一旦算错, 表现是"气泡说跳到 12:34, 实际落在 12:31" ——
///   长视频里 1% 就是 50 秒, 这种偏差非常刺眼, 但真机上很难复现(取决于窗口宽度/缩放)。
///   把公式放进纯函数, 事件接线(<c>PlayerWindow.OnProgressSliderPreviewMouseDown</c> 等)
///   留在窗口里, 边界值就能被穷举验证。
///
/// ★ 换算必须用**滑块中心的真实行程**, 不能用控件宽度:
///   滑块的 Value=0 时中心在 x = 半宽 处, Value=Max 时中心在 x = 宽 - 半宽 处
///   (见 <c>PlayerWindow.TrackMetrics</c>)。按控件宽度反推会让指针与滑块差半个圆点。
/// </summary>
public static class ProgressScrub
{
    /// <summary>
    /// 把"指针在进度条内的横坐标"换算成量程值。
    /// </summary>
    /// <param name="pointerX">指针在 Slider 坐标系里的横坐标(DIP)</param>
    /// <param name="trackOffset">行程起点 = 滑块半宽(TrackMetrics 的 offset)</param>
    /// <param name="usableWidth">行程长度 = 轨道宽 - 滑块宽(TrackMetrics 的 usable)</param>
    /// <param name="minimum">量程下限(进度条是 0)</param>
    /// <param name="maximum">量程上限(进度条是 1000)</param>
    /// <returns>夹在 [minimum, maximum] 之间的值</returns>
    public static double ValueFromPointer(double pointerX, double trackOffset, double usableWidth,
        double minimum, double maximum)
    {
        // 行程还没量出来(模板未应用/控件被折叠): 返回下限, 让调用方按"没有有效输入"处理。
        // ★ 不能返回 NaN —— 赋给 Slider.Value 会抛异常。
        if (!(usableWidth > 0) || double.IsNaN(usableWidth) || double.IsInfinity(usableWidth))
            return minimum;

        var ratio = (pointerX - trackOffset) / usableWidth;
        // 指针拖出轨道(有鼠标捕获时很常见)夹到两端: 拖过头就是"跳到片头/片尾", 不是失控。
        ratio = Math.Clamp(ratio, 0, 1);
        return minimum + (maximum - minimum) * ratio;
    }

    /// <summary>
    /// 松手时是否该真正发起跳转。
    ///
    /// ★ 为什么要判: 用户可能只是"按了一下滑块"又松开(没挪动), 这时值没变,
    ///   再发一次 seek 只会打断正在播的解复用器(表现: 卡一下/无限缓冲)。
    /// ★ 阈值与旧版 <c>Thumb.DragCompleted</c> 那条一致(0.01, 量程 0~1000):
    ///   1px 的拖动在常见轨道宽度下约等于 1, 不会被误吞; 而原地按下不会产生任何差值。
    /// </summary>
    public static bool ShouldSeek(double startValue, double endValue, double epsilon = 0.01)
        => Math.Abs(endValue - startValue) > epsilon;
}
