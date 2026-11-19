using System;

namespace BiliDesk.Helpers;

/// <summary>
/// 页码分页的两条"一定要算对"的换算(2026-11-19 从收藏页分页改造抽出)。
///
/// ★ 为什么单独抽: 这两个式子在"总条数为 0 / 零头 / 当前页越界"时最容易写错,
///   而写错的表现是"最后一页永远点不到"或"第 1 页就显示成空"。
///   抽成纯函数之后可以直接单测(见 BiliDesk.Tests/PagerMathTests.cs)。
///
/// 注意与 <see cref="PageBar"/> 的分工:
///   · <see cref="PageBar"/> 只负责"页码栏里该显示哪些数字";
///   · 本类负责"一共几页 / 这一页合法吗"。
/// </summary>
public static class PagerMath
{
    /// <summary>
    /// 总页数 = ⌈总条数 ÷ 每页条数⌉。
    /// 总条数或每页条数非法时返回 0(表示"没有页"), 调用方据此收起页码栏。
    /// </summary>
    public static int TotalPages(int totalItems, int pageSize)
    {
        if (totalItems <= 0 || pageSize <= 0) return 0;
        // 用 long 参与运算: 别让"总条数 + 页大小"在极端值上溢出成负数
        return (int)((totalItems + (long)pageSize - 1) / pageSize);
    }

    /// <summary>
    /// 把页码夹进 [1, totalPages]。没有页(totalPages ≤ 0)时返回 1 ——
    /// 这样"空收藏夹"仍然会去请求第 1 页, 从而拿到权威的"空"结果, 而不是停在
    /// 一个不存在的页码上什么都不做。
    /// </summary>
    public static int ClampPage(int page, int totalPages)
    {
        if (totalPages <= 0) return 1;
        if (page < 1) return 1;
        return page > totalPages ? totalPages : page;
    }
}
