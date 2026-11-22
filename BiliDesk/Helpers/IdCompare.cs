using System;

namespace BiliDesk.Helpers;

/// <summary>
/// 动态 id 的新旧比较(2026-11-22 从 <c>DynamicNotifyService</c> 抽出)。
///
/// ★ 为什么单独抽: 这是"红点亮不亮、亮几条"的唯一判据, 而错的地方都在边界上 ——
///   雪花 id 是数字串要按数值比; 旧版基准存的是 bvid(非数字), 只能退化成"不同即更新";
///   任意一边是空串时必须一律返回 false(否则每轮轮询都会把红点重新点亮)。
///   抽成纯函数后可以直接穷举(见 BiliDesk.Tests/PerformanceFixTests.cs)。
/// ★ 同一轮里基准 id 不变: 用 <see cref="ParseBaseline"/> 解析一次, 再逐条比较 ——
///   别在几百条的循环里反复 TryParse。
/// </summary>
public static class IdCompare
{
    /// <summary>基准 id 的预解析结果。<c>IsNumeric == false</c> 时 <c>Value</c> 无意义。</summary>
    public readonly record struct Baseline(bool IsNumeric, ulong Value);

    /// <summary>解析基准 id(一轮只做一次)。空串/非数字得到 <c>IsNumeric == false</c>。</summary>
    public static Baseline ParseBaseline(string? id)
        => ulong.TryParse(id, out var value) ? new Baseline(true, value) : new Baseline(false, 0);

    /// <summary>id 是否比基准"更新"(单次调用用这个, 内部自己解析基准)。</summary>
    public static bool IsNewer(string? id, string? baseline)
        => IsNewer(id, baseline, ParseBaseline(baseline));

    /// <summary>基准已解析好的版本: 同一轮里逐条比较用这个。</summary>
    public static bool IsNewer(string? id, string? baseline, Baseline parsed)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(baseline)) return false;
        if (parsed.IsNumeric && ulong.TryParse(id, out var value)) return value > parsed.Value;
        // 非雪花 id(极少见, 旧版基准是 bvid): 退化成字符串比较, 只用于"不同"判断。
        return !string.Equals(id, baseline, StringComparison.Ordinal);
    }
}
