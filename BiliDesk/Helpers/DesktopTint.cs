using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace BiliDesk.Helpers;

/// <summary>
/// 读出**当前桌面壁纸的平均色**, 供"云母/云母 Alt"给窗口底调色。
///
/// ★★★ 为什么要自己做这件事(2026-10-17, 用户报"云母材质基本和白色背景没区别"):
///   云母本该"采样一次壁纸、把壁纸色调染到窗口上"。但实测(探针 bd-probe-backdrop,
///   build 26200, 同一窗口分别对白/青绿/橙/深灰四张纯色壁纸):
///   <code>
///     壁纸        Mica 读到的像素
///     白 #FFFFFF  243,243,243
///     青绿        243,243,243   ← 完全没有青绿
///     深灰        243,243,243   ← 也没有变暗
///     橙          232,248,243   ← 偶尔才有色偏
///   </code>
///   也就是说 **DWM 的云母采样在很多机器上会退化成它自己的基色 #F3F3F3**(纯灰白),
///   而且退化成什么颜色、哪一次会成功, 都不受我们控制。
///   这时再把我们的页面底(#B3F3F3F3, 70% 的 #F3F3F3)压上去 —— 两个几乎相同的灰白
///   叠在一起, 用户看到的自然就是"和白色背景没区别"。
///
/// ★ 所以这里补一层**确定性的**壁纸色调: 直接读壁纸文件、降采样求平均色。
///   它不替代 DWM 的材质(材质照旧下发, 有模糊/纹理的机器上照样享受), 只保证
///   **任何机器上"云母"都能透出壁纸的颜色**, 不会退化成一个纯白窗口。
///
/// ★ 只取平均色不做亮度归一: 用户用深色壁纸时云母本来就该偏深(系统也一样),
///   把亮度拉平反而会与 Windows 自己的观感不一致。
/// ★ 缓存: 读图 + 降采样要几十毫秒, 只在**启动**与**壁纸变化**时算一次。
/// </summary>
public static class DesktopTint
{
    /// <summary>降采样到的边长。64 已足够求平均色, 再大只是白花 CPU。</summary>
    private const int SampleSize = 64;

    private static Color? _cached;

    /// <summary>从没算过就现在算一次(拿不到壁纸就返回"无色调")</summary>
    public static Color Get()
    {
        if (_cached is { } c) return c;
        _cached = Compute();
        return _cached.Value;
    }

    /// <summary>壁纸换了, 丢掉缓存(下次 <see cref="Get"/> 重算)</summary>
    public static void Invalidate() => _cached = null;

    /// <summary>
    /// 壁纸色调**提亮**到"当页面底用"的浅色(材质感靠它)。
    ///
    /// ★ 为什么不直接用壁纸平均色: 用户的壁纸平均色往往很深(实测本机 #5F7F74),
    ///   直接当页面底色会得到一整片深绿 —— 文字都压不住。
    ///   所以只保留**色相**, 亮度与"色度(chroma)"由这里给定。
    ///
    /// ★★★ 为什么参数是 chroma 而不是 HSL 的 saturation(2026-10-17 实测踩到):
    ///   HSL 的 saturation 在**高亮度**下会被严重压缩 —— l=0.955 时给它 s=0.16,
    ///   实际 RGB 只有 242~245 的差别(跨度 3), 再被半透明页面底一稀释就彻底看不见
    ///   (实测最终合成色偏只有 1)。用户看到的还是"白底"。
    ///   ⇒ 这里直接按"**最大通道与最小通道差多少**(chroma)"反解 saturation:
    ///     <code>
    ///       l > 0.5:  s = chroma / (2(1-l))      l ≤ 0.5:  s = chroma / (2l)
    ///     </code>
    ///   于是"给 0.08 就是给 20 阶色差", 与亮度解耦, 可预测。
    /// </summary>
    /// <param name="lightness">目标亮度(0~1)。浅色主题取 0.93 左右, 深色主题取 0.13 左右。</param>
    /// <param name="chroma">目标色度 = (max-min)/255。它与页面底的 alpha 相乘后才是最终色偏。</param>
    public static Color TintedBase(double lightness, double chroma = 0.078)
    {
        var src = Get();
        double h;
        if (src == Colors.Transparent)
        {
            h = DefaultHue;
        }
        else
        {
            ToHsl(src, out h, out _, out _);
            // 壁纸几乎无彩(纯白/纯灰壁纸)时用默认淡蓝色相, 否则会得到一片死灰
            if (toChroma(src) < 0.02) h = DefaultHue;
        }

        var l = Math.Clamp(lightness, 0, 1);
        var denom = l > 0.5 ? 2 * (1 - l) : 2 * l;
        var s = denom <= 0.0001 ? 0 : Math.Clamp(chroma / denom, 0, 1);
        return FromHsl(h, s, l);
    }

    /// <summary>色度 = (max-min)/255 —— 与 HSL saturation 不同, 它直接等于"RGB 跨度"</summary>
    private static double toChroma(Color c)
    {
        var max = Math.Max(c.R, Math.Max(c.G, c.B));
        var min = Math.Min(c.R, Math.Min(c.G, c.B));
        return (max - min) / 255.0;
    }

    // ---------------------------------------------------------------- 取色

    /// <summary>没有壁纸(纯色桌面)时的兜底色相: Windows 默认的淡蓝, 最不像"忘了上色"</summary>
    private const double DefaultHue = 210.0 / 360.0;

    /// <summary>
    /// 两种主题各自的"页面底色度"(= RGB 跨度/255, 见 <see cref="TintedBase"/> 的说明)。
    /// ★ 浅色给 0.078(约 20 阶色差): 页面底是 93% 亮度的浅灰, 20 阶色差看得见"有颜色"
    ///   但不会像换了个主题; 再大会让界面显脏(实测 0.12 时整屏发绿)。
    /// ★ 深色给 0.10: 深底上同样的色度看起来更弱, 得给足一点才有材质感。
    /// ★ 必须是 **public**: 调用方(ThemeService)在另一个类里, 要按主题取不同值。
    /// </summary>
    public const double LightChroma = 0.078;
    /// <inheritdoc cref="LightChroma"/>
    public const double DarkChroma = 0.10;

    /// <summary>取"当前壁纸平均色"; 拿不到(纯色桌面/读不出图)返回全透明</summary>
    private static Color Compute()
    {
        try
        {
            var path = ReadWallpaperPath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return Colors.Transparent;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            // ★ 直接让解码器降到 64px: 4200x2965 的原图全解要几十 MB 内存与上百毫秒,
            //   DecodePixelWidth 是解码期降采样, 代价几乎为零。
            bmp.DecodePixelWidth = SampleSize;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();

            var w = bmp.PixelWidth;
            var h = bmp.PixelHeight;
            if (w <= 0 || h <= 0) return Colors.Transparent;

            // 统一转成 Bgra32 再取平均: 各种源格式(索引色/16bpp/CMYK JPEG)都能读
            var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            converted.Freeze();

            var stride = w * 4;
            var buf = new byte[stride * h];
            converted.CopyPixels(buf, stride, 0);

            long r = 0, g = 0, b = 0;
            var n = 0;
            for (var i = 0; i + 3 < buf.Length; i += 4)
            {
                var a = buf[i + 3];
                if (a < 16) continue;          // 全透明像素不参与(带 alpha 的 PNG 壁纸)
                b += buf[i];
                g += buf[i + 1];
                r += buf[i + 2];
                n++;
            }
            if (n == 0) return Colors.Transparent;
            return Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
        }
        catch
        {
            // 读不出就当作"没有色调": 调用方会回落到中性底, 不影响功能
            return Colors.Transparent;
        }
    }

    // ---------------------------------------------------------------- HSL

    private static void ToHsl(Color c, out double h, out double s, out double l)
    {
        var r = c.R / 255.0;
        var g = c.G / 255.0;
        var b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;

        l = (max + min) / 2.0;
        if (d <= 0.0001)
        {
            h = DefaultHue;
            s = 0;
            return;
        }

        s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
        h = max == r ? (g - b) / d + (g < b ? 6.0 : 0.0)
            : max == g ? (b - r) / d + 2.0
            : (r - g) / d + 4.0;
        h /= 6.0;
    }

    private static Color FromHsl(double h, double s, double l)
    {
        if (s <= 0.0001)
        {
            var v = (byte)Math.Round(Math.Clamp(l, 0, 1) * 255);
            return Color.FromRgb(v, v, v);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return Color.FromRgb(Channel(p, q, h + 1.0 / 3), Channel(p, q, h), Channel(p, q, h - 1.0 / 3));
    }

    private static byte Channel(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        var v = t < 1.0 / 6 ? p + (q - p) * 6 * t
            : t < 1.0 / 2 ? q
            : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6
            : p;
        return (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
    }

    /// <summary>
    /// 注册表里的壁纸路径。
    /// ★ 用 `TranscodedWallpaper` 而不是 `WallPaper`: 后者在"幻灯片"模式下是空的,
    ///   而前者永远是 shell 实际在显示的那张(已转码)。取不到再回落到 `WallPaper`。
    /// </summary>
    private static string? ReadWallpaperPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            var direct = key?.GetValue("WallPaper") as string;
            if (!string.IsNullOrWhiteSpace(direct) && File.Exists(direct)) return direct;
        }
        catch
        {
            // 忽略
        }

        try
        {
            var transcoded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
            if (File.Exists(transcoded)) return transcoded;
        }
        catch
        {
            // 忽略
        }

        return null;
    }
}
