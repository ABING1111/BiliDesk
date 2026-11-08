using System;
using System.Collections.Generic;

namespace BiliDesk.Helpers;

/// <summary>
/// 页码栏的页码序列生成(UP 主主页投稿分页, 2026-11-08)。
///
/// 参考 B 站投稿页的形态: `上一页 | 1 2 3 4 5 6 7 … 33 | 下一页`。
/// 这里只负责"中间该出现哪些数字", 按钮/省略号怎么画由页面决定。
///
/// ★ 为什么抽成纯函数:
///   页码的边界条件(第一页 / 最后一页 / 中间 / 总页数很少)最容易写错,
///   一旦写错表现是"点了省略号"或"少了一页翻不到"; 纯函数可以直接单测。
/// </summary>
public static class PageBar
{
    /// <summary>序列里的省略号占位值(页面据此渲染成不可点的 "...")</summary>
    public const int Ellipsis = 0;

    /// <summary>
    /// 生成页码序列。<see cref="Ellipsis"/> 表示省略号。
    ///
    /// 规则(以 <paramref name="maxSlots"/> = 7 为例):
    ///   · 总页数 ≤ 9: 全部列出(1..N);
    ///   · 当前页 ≤ 5: `1..7 … N`;
    ///   · 当前页 ≥ N-4: `1 … N-6..N`;
    ///   · 其余: `1 … cur-2..cur+2 … N`。
    /// 首尾页永远可见 —— 用户想跳到最后/回到第一页时不该先去找省略号。
    /// </summary>
    /// <param name="current">当前页(1 基; 越界会被夹到合法范围)</param>
    /// <param name="total">总页数(≤ 0 返回空列表)</param>
    /// <param name="maxSlots">中段最多显示几个连续页码(最少按 3 处理)</param>
    public static List<int> Build(int current, int total, int maxSlots = 7)
    {
        var result = new List<int>();
        if (total <= 0) return result;

        current = Math.Clamp(current, 1, total);
        maxSlots = Math.Max(3, maxSlots);

        // 页数不多: 全部列出来, 不出现省略号
        if (total <= maxSlots + 2)
        {
            for (var i = 1; i <= total; i++) result.Add(i);
            return result;
        }

        // 靠前: 1..maxSlots … total
        if (current <= maxSlots - 2)
        {
            for (var i = 1; i <= maxSlots; i++) result.Add(i);
            result.Add(Ellipsis);
            result.Add(total);
            return result;
        }

        // 靠后: 1 … (total-maxSlots+1)..total
        if (current >= total - (maxSlots - 2) + 1)
        {
            result.Add(1);
            result.Add(Ellipsis);
            for (var i = total - maxSlots + 1; i <= total; i++) result.Add(i);
            return result;
        }

        // 中间: 1 … cur-2..cur+2 … total
        const int middle = 5;
        var start = Math.Max(2, current - middle / 2);
        var end = Math.Min(total - 1, start + middle - 1);
        start = Math.Max(2, end - middle + 1);

        result.Add(1);
        result.Add(Ellipsis);
        for (var i = start; i <= end; i++) result.Add(i);
        result.Add(Ellipsis);
        result.Add(total);
        return result;
    }
}
