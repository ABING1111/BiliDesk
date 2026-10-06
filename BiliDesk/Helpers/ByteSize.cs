using System;
using System.Globalization;

namespace BiliDesk.Helpers;

/// <summary>
/// 字节数 → 人类可读的容量文本(如 "1.5 GB")。
///
/// 为什么单独抽出来: 设置页(视频缓存大小)与缓存页(每个文件的大小、总占用)各写过一份,
/// 单位进位与小数位都不一样 —— 同一个文件在两处显示的位数不同, 用户会以为其中一个是错的。
/// 集中一次实现, 只保留"按量级给不同精度"这一条规则:
/// 越大的单位小数位越少(GB 两位、MB/KB 一位), 免得 "0.0 GB" 这种既占位又没信息量的写法。
/// </summary>
public static class ByteSize
{
    private const long Kb = 1024L;
    private const long Mb = Kb * 1024;
    private const long Gb = Mb * 1024;

    /// <summary>格式化字节数。负数按 0 处理(调用方拿到的目录大小不该是负的)。</summary>
    public static string Format(long bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes >= Gb) return (bytes / (double)Gb).ToString("0.##", CultureInfo.InvariantCulture) + " GB";
        if (bytes >= Mb) return (bytes / (double)Mb).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        if (bytes >= Kb) return (bytes / (double)Kb).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        return bytes + " B";
    }
}
