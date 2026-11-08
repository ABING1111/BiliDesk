using System.Windows;
using BiliDesk.Models;

namespace BiliDesk.Helpers;

/// <summary>
/// 深浅色切换的入口。
///
/// ★★★ 2026-11-08 整体重做(**这一版把"波纹扩散"整个删掉了**, 用户明确要求"删除臃肿代码,
///   改用更轻量更低占用更流畅的切换动画")。
///
/// 旧实现为什么必须删 —— 它是一整套"为了遮住换肤卡顿而造出来的东西":
///   ① 先用 `RenderTargetBitmap` 把整窗拍成一张位图(4K 全窗要 200ms+, 这段 UI 线程是死的);
///   ② 给这张图挖一个圆形洞, 再让洞从点击点扩散出去;
///   ③ 为了让"拍快照 / 换肤 / 起动画"三件重活不撞在同一个 UI 回合里, 用三层
///      `Dispatcher.BeginInvoke`(Loaded → Background → ContextIdle)排空;
///   ④ 再加两段兜底 `DispatcherTimer`、一个位图池(两张交替)、终点外扩 15% 防"收尾爬行"。
///   实测(500 张卡 / 13521 个可视节点): 点击到波纹真正开始之间, **同步段中位 697ms、
///   最大 1327ms** —— 用户感知就是"点一下先僵住, 然后动画突然冒出来"。
///   而波纹动画本身 p50 15.4ms 其实是流畅的 —— 也就是说这 400 多行代码,
///   换来的只是一层"盖住卡顿的遮羞布", 卡顿本身一点没解决。
///
/// 现在: **卡顿的根因在 ThemeService 里被消掉了**(不再换字典, 只改共享笔刷的 Color),
///   于是这里只剩一句话 —— 切模式。过渡效果由笔刷自己的 ColorAnimation 提供:
///   整屏颜色在 240ms 内平滑插值, 零快照、零位图、零几何体、零 dispatcher 编排。
///
/// ★ 为什么不做"遮罩/擦除"式的花哨过渡: 任何需要**把当前画面拍成一张图**的方案,
///   都要付一次整窗渲染(几十到几百毫秒的同步阻塞), 而且那张位图本身就是内存占用。
///   纯颜色插值是唯一"零成本"的过渡方式 —— 它不需要知道屏幕长什么样, 只需要知道目标色。
/// </summary>
public static class ThemeTransition
{
    /// <summary>在浅色 / 深色之间切换。
    /// <paramref name="origin"/> 保留参数是为了不动调用点; 颜色过渡与点击位置无关。</summary>
    public static void ToggleLightDark(Window? window, Point? origin)
    {
        // 当前是"跟随系统"时, 就切到当前实际外观的反面 —— 用户的预期是"按一下换一种"
        var target = ThemeService.Instance.IsDark ? AppTheme.Light : AppTheme.Dark;
        SwitchWithRipple(window, origin, target);
    }

    /// <summary>切换到指定主题。</summary>
    public static void SwitchWithRipple(Window? window, Point? origin, AppTheme target)
        // 过渡由 ThemeService 里的 ColorAnimation 完成, 这里不需要窗口/坐标。
        => ThemeService.Instance.SetMode(target);
}
