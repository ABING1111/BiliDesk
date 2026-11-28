using System;
using System.Collections.Generic;

namespace BiliDesk.Helpers;

/// <summary>
/// 图片预览窗的"相邻图预取"策略(纯函数, 有单测)。
///
/// ★ 为什么需要它(2026-11-28 用户报"快速点 →/← 时新图半天不出来, 翻过一圈就好转"):
///   预览窗要的是**原图**(动态九宫格/详情窗用的是 `@480w.jpg` / `@720w.jpg`),
///   缓存键与磁盘文件都不同 ⇒ 每张图第一次在预览窗出现都要走完整下载 + 按 4096 档解码。
///   一张 2400×3200 的壁纸就是 3~4MB 网络 + 30MB 位图, 点一下等一两秒是必然的。
///   "翻过一圈就好转"正是因为那一圈把磁盘缓存预热完了 —— 不是代码在第二轮变快了。
///
///   解法不是在点下去之后想办法加速, 而是**把等待挪到用户看当前这张的时间里**:
///   当前图一上屏就预取相邻的几张, 点的时候大概率已经在手里(或至少已在下载)。
///
/// ★ 这两个函数被两边共用, 必须**只读纯值**: <see cref="IsWanted"/> 会被
///   <see cref="CoverLoader"/> 在**后台线程**当 `abandoned` 回调调用, 碰 DependencyObject 会炸。
/// </summary>
public static class PreviewPreload
{
    /// <summary>往"下一张"方向预取几张(主方向: 用户多半在往后翻)。</summary>
    public const int Ahead = 2;

    /// <summary>往"上一张"方向预取几张。只留 1 张: 回翻通常是"刚才那张", 而它多半还在窗口缓存里。</summary>
    public const int Behind = 1;

    /// <summary>
    /// 同时在飞的预取通道数。
    /// ★ 为什么不是"全部一起发": 预取会占 <see cref="CoverLoader"/> 的下载/解码槽(解码只有
    ///   min(CPU/2,4) 个), 全发出去会把用户真正在等的那张挤到队尾 —— 正是要修的那个毛病。
    /// </summary>
    public const int MaxConcurrent = 2;

    /// <summary>
    /// 预取顺序(返回的是**偏移量**, 不含 0): 下一张优先, 交错左右, 越界的一律省略。
    /// 例: 组里 9 张、当前第 3 张(下标 2) ⇒ <c>[+1, -1, +2]</c>;
    ///     当前首张 ⇒ <c>[+1, +2]</c>; 当前末张 ⇒ <c>[-1]</c>; 只有一张 ⇒ 空。
    /// </summary>
    public static int[] Order(int count, int index)
    {
        if (count <= 1 || index < 0 || index >= count) return Array.Empty<int>();

        var list = new List<int>(Ahead + Behind);
        var max = Math.Max(Ahead, Behind);
        for (var d = 1; d <= max; d++)
        {
            // ★ 先加正向: 同一个 d 下"下一张"必须排在"上一张"前面(SchedulePrefetch 按顺序填通道)。
            if (d <= Ahead && index + d < count) list.Add(d);
            if (d <= Behind && index - d >= 0) list.Add(-d);
        }
        return list.ToArray();
    }

    /// <summary>
    /// 某一张是否**还要**(= 当前这张, 或落在预取窗口内)。
    ///
    /// ★★ 这是 `abandoned` 回调的唯一判据, 别改回"看加载代数": 代数一变就把请求判死的话,
    ///   用户往回点一张时, 那张图在途的加载会把自己判成"没人要"而放弃解码 ⇒ 画面停在旧图上,
    ///   也就是这次要修的现象。判"还在窗口内"才既能让真正过期的请求让出解码槽, 又不会误杀。
    /// </summary>
    public static bool IsWanted(int count, int index, int target)
    {
        if (count <= 0 || index < 0 || index >= count) return false;
        if (target < 0 || target >= count) return false;
        if (target == index) return true;
        return target > index ? target - index <= Ahead : index - target <= Behind;
    }
}
