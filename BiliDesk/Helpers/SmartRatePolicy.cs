namespace BiliDesk.Helpers;

/// <summary>
/// 长按“智能”倍速: 把 `x/player/wbi/v2` 的 `script_pace` 映射成实际倍速。
///
/// 官方播放器的逻辑(2026-11 实机/源码核对):
///   · 字段缺失 / null → 3x(旧版长按默认值);
///   · NaN / 非数字 → 1.5x(NaN &lt; 5 为 false);
///   · &lt; 5 → 2x;
///   · &gt;= 5 → 1.5x(注意 5.0 走这一档, 不是严格小于才 2x)。
/// </summary>
public static class SmartRatePolicy
{
    public const double FastPaceThreshold = 5.0;
    public const float SlowDefaultRate = 3.0f;
    public const float SlowPaceRate = 2.0f;
    public const float FastPaceRate = 1.5f;

    public static float Resolve(double? scriptPace)
    {
        if (!scriptPace.HasValue) return SlowDefaultRate;
        var pace = scriptPace.Value;
        if (double.IsNaN(pace) || double.IsInfinity(pace)) return FastPaceRate;
        return pace < FastPaceThreshold ? SlowPaceRate : FastPaceRate;
    }
}
