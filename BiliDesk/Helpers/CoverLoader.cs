using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.Helpers;

/// <summary>
/// 封面图异步加载器: 内存缓存 + 磁盘缓存, 后台解码, 冻结后供 UI 使用
/// </summary>
public static class CoverLoader
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private sealed class Entry
    {
        public BitmapImage Image = null!;
        /// <summary>解码后占用的字节估算值(内存占用取决于位图尺寸, 不是文件大小)</summary>
        public long Bytes;
        /// <summary>最近一次被命中的逻辑时钟, 用于 LRU 淘汰</summary>
        public long LastUse;
    }

    private static readonly Dictionary<string, Entry> MemCache = new();
    /// <summary>正在进行中的加载(键 = 缓存键)。同一张图被多处同时请求时共用一次下载+解码。</summary>
    private static readonly Dictionary<string, Task<BitmapImage?>> InFlight = new();
    private static readonly object Lock = new();

    /// <summary>
    /// 被"屏幕上正在显示它"的卡片钉住的缓存键(键 → 引用计数)。
    ///
    /// ★★ 为什么需要它: 卡片墙会把**离屏很远**的卡片封面放掉(见 <see cref="Cover.Release"/>),
    ///   靠的就是"滚回来时从 MemCache 里直接取回同一个实例"。如果那一张恰好在这期间被 LRU
    ///   淘汰了, 滚回来就要重新下载+解码, 用户看到的是"占位底色 → 淡入"的闪一下。
    ///   钉住可见区内的图, 这个窗口期才真的看不见。
    ///
    /// ★ 钉住**不是无上限的**: 若被钉住的字节数已经超过整个预算(见 TrimLocked),
    ///   仍然按 LRU 淘汰 —— 宁可偶尔闪一下, 也不能让缓存突破 32MB 变成新的内存黑洞。
    /// </summary>
    private static readonly Dictionary<string, int> Pins = new();

    /// <summary>
    /// 解码并发上限: 卡片墙一次铺 24 张, 不加闸就是 24 个线程池任务同时跑 WIC 解码,
    /// 瞬时 CPU/内存尖峰正好撞在"刚进页面"那一下。取 min(CPU/2, 4) —— 再高对吞吐没帮助
    /// (解码是内存带宽瓶颈), 只会让尖峰更陡。
    /// </summary>
    private static readonly SemaphoreSlim DecodeGate =
        new(Math.Clamp(Environment.ProcessorCount / 2, 2, 4));

    /// <summary>
    /// 每一格"要不要图"的线程安全标记(键 = 元素, 值 = 标记对象)。
    ///
    /// ★★★ 为什么必须绕开 `DependencyObject.GetValue`:
    ///   <see cref="LoadAsync(string?, int, Func{bool})"/> 的 `abandoned` 回调运行在
    ///   **后台线程**(`LoadCoreAsync` 里 `ConfigureAwait(false)` 之后), 而 `GetValue` 只能在
    ///   UI 线程调用 —— 跨线程会抛 `InvalidOperationException: 调用线程无法访问此对象`。
    ///   调用方是 `_ = LoadAsync(...)`(fire-and-forget), 异常被吞进"无人观察的任务",
    ///   界面上只表现为"这一格永远灰、loading/failed 都是 0"。
    ///   探针实测: 播放器「相关视频」8 条只有 4 条出图, 评论区头像同样成片不显示。
    ///
    /// ★ 用 `ConditionalWeakTable`: 键是 UI 元素, 强引用会让被回收的卡片永久留在表里。
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object>
        AbandonFlags = new();

    private sealed class AbandonFlag { public volatile bool Value; }

    /// <summary>标记"这一格已经不要图了"(UI 线程调)。</summary>
    internal static void MarkAbandoned(object key)
        => ((AbandonFlag)AbandonFlags.GetValue(key, _ => new AbandonFlag())).Value = true;

    /// <summary>重新要图时清掉标记(UI 线程调)。</summary>
    internal static void ClearAbandoned(object key)
        => ((AbandonFlag)AbandonFlags.GetValue(key, _ => new AbandonFlag())).Value = false;

    /// <summary>
    /// 取这一格的"放弃"判定闭包(**只读不建**条目, 后台线程安全)。
    ///
    /// ★ 与旧写法的关键差别: 旧写法在后台线程读 `border.GetValue(Released)` 会抛异常,
    ///   而那个异常**恰好**让 `InFlight` 保留一个 faulted 任务、阻止了重复加载 ——
    ///   卡片墙的"首屏 24 张全有图"一直靠这个副作用维持。所以这里**必须显式**
    ///   复现"放弃后不重试"的语义, 否则一放掉就会陷入"放掉 → 重发"的循环。
    ///   做法: 放弃时在 <see cref="LoadCoreAsync"/> 里写失败冷却(见那里的说明)。
    /// </summary>
    internal static Func<bool> AbandonFlagFor(object key)
        => () => AbandonFlags.TryGetValue(key, out var f) && ((AbandonFlag)f).Value;

    /// <summary>下载并发上限(与解码分开: 网络等待不占 CPU, 可以比解码宽一点)</summary>
    private static readonly SemaphoreSlim DownloadGate = new(8);

    /// <summary>
    /// 加载**失败**(抛异常)的次数。
    ///
    /// ★ 保留这个计数器: 这类失败**在界面上完全没有痕迹** —— 调用方是 `_ = LoadAsync(...)`,
    ///   异常被吞进"无人观察的任务", 表现只是"这一格永远灰, loading/failed 都是 0"。
    ///   2026-11-07 的"相关视频 8 条只出 4 条图"就是靠它定位的。
    ///   排查封面问题时先看这个值有没有在涨。
    /// </summary>
    /// <summary>
    /// 加载**失败**(抛异常)的次数。
    ///
    /// ★ 保留这个计数器: 这类失败**在界面上完全没有痕迹** —— 调用方是 `_ = LoadAsync(...)`,
    ///   异常被吞进"无人观察的任务", 表现只是"这一格永远灰, loading/failed 都是 0"。
    ///   2026-11-07 的"相关视频 8 条只出 4 条图"就是靠它定位的。
    ///   排查封面问题时先看这个值有没有在涨。
    /// </summary>
    internal static long _loadAsyncFailed;
    public static long LoadAsyncFailed => System.Threading.Interlocked.Read(ref _loadAsyncFailed);
    internal static ref long LoadAsyncFailedRaw => ref _loadAsyncFailed;

    private static long _clock;
    private static long _cachedBytes;

    /// <summary>
    /// 内存缓存上限。
    /// 为什么按"字节预算"而不是"条数": 条数上限完全约束不住内存 —— 同样是 250 条,
    /// 全是小头像可能只有十几 MB, 全是 1280 宽的封面能到两百多 MB。按解码后的像素字节算才准。
    ///
    /// 2026-09-24 从 48MB 收到 32MB: 配合"解码宽度跟着 DPI 走"(见 Cover.LoadAsync),
    /// 单张封面的开销已经降到原来的一半左右, 32MB 仍能装下 120 张以上大封面,
    /// 足够覆盖"来回滚一屏"的复用; 而首页四个 tab 全点一遍时的峰值内存明显下降。
    /// </summary>
    private const long MaxCacheBytes = 32L * 1024 * 1024;

    /// <summary>条数上限作为第二道保险(极端情况下大量小图也不至于让字典无限增长)</summary>
    private const int MaxCacheCount = 400;

    /// <summary>
    /// 解码宽度的"档位"。
    ///
    /// 为什么要量化: `Cover.Source` 是按控件**实时**宽度 ×2 去请求解码的, 布局上差几个像素
    /// (窗口缩放、字体度量差异、不同页面的卡片宽度)就会算出不同的值。缓存键里带上原始值,
    /// 同一张封面就会在缓存里存在好几份不同尺寸的副本 —— 既白占内存, 又必然反复 miss,
    /// 每次 miss 都要重新解码(解码是这条链路上最贵的一步)。
    /// 量化到固定档位后同一张图最多只留一档; 向上取档, 所以清晰度不会因此下降。
    ///
    /// 2560/4096 两档只有图片预览窗用(它传 4096 当"解码上限"): 不加这两档的话
    /// Quantize 会把 4096 砍回 1280, 预览大图时清晰度白白回退一截。
    /// 列表/头像的请求永远 ≤1280, 落在低档, 不受影响。
    /// </summary>
    private static readonly int[] WidthSteps = { 64, 128, 192, 320, 480, 720, 960, 1280, 2560, 4096 };

    static CoverLoader()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(HttpDefaults.UserAgent);
    }

    /// <summary>加载图片。decodeWidth>0 时按目标像素宽解码(大幅降低内存),
    /// 0 表示按原图解码。不同档位的结果分别缓存</summary>
    public static Task<BitmapImage?> LoadAsync(string? url, int decodeWidth = 0)
        => LoadAsync(url, decodeWidth, null);

    /// <summary>
    /// 带"放弃判定"的加载。<paramref name="abandoned"/> 在**拿到解码槽之后**被调用一次:
    /// 返回 true 表示"这张图已经没人要了, 别再花解码时间", 直接把槽让给别人。
    ///
    /// ★★★ 它运行在**后台线程**上, 所以**绝对不能在回调里访问 DependencyObject**
    ///   (那会抛 "调用线程无法访问此对象")。传进来的必须是纯字段读取 ——
    ///   见 <see cref="AbandonFlagFor"/>。
    /// </summary>
    internal static Task<BitmapImage?> LoadAsync(string? url, int decodeWidth,
        Func<bool>? abandoned)
    {
        url = UrlUtil.Normalize(url);
        if (url.Length == 0) return Task.FromResult<BitmapImage?>(null);

        var w = Quantize(decodeWidth);
        var cacheKey = w > 0 ? $"{url}|w{w}" : url;

        lock (Lock)
        {
            if (MemCache.TryGetValue(cacheKey, out var hit))
            {
                hit.LastUse = ++_clock;
                return Task.FromResult<BitmapImage?>(hit.Image);
            }
            // 并发去重: 同一个键已经有请求在跑, 直接复用它的 Task
            if (InFlight.TryGetValue(cacheKey, out var running)) return running;
        }

        var task = LoadCoreAsync(url, cacheKey, w, abandoned);
        lock (Lock)
        {
            // 极端情况下两个线程同时走到这里, 后者会覆盖前者 —— 两者结果等价(都会写同一个缓存键),
            // 唯一的代价是可能多下载一次, 不影响正确性。
            InFlight[cacheKey] = task;
        }
        return task;
    }

    /// <summary>
    /// **同步**查一次内存缓存(不触发任何下载/解码)。
    ///
    /// 给"已经加载过、又被销毁重建"的元素用 —— 典型场景是首页切 tab: 摘掉 ItemsSource 会把
    /// 卡片容器连同它的 ImageBrush 一起销毁, 但位图本身还在 MemCache 里。重新挂载时若命中,
    /// 调用方应当**同步贴图、不闪不淡入**(见 Cover.LoadAsync 的快路径), 而不是走完整异步路径
    /// 再来一次 240ms 淡入 —— 否则用户看到的就是"每次切 tab 封面都重新加载一遍"。
    /// </summary>
    public static bool TryGetCached(string? url, int decodeWidth, out BitmapImage? img)
    {
        url = UrlUtil.Normalize(url);
        if (url.Length == 0) { img = null; return false; }

        var w = Quantize(decodeWidth);
        var cacheKey = w > 0 ? $"{url}|w{w}" : url;

        lock (Lock)
        {
            if (MemCache.TryGetValue(cacheKey, out var hit))
            {
                hit.LastUse = ++_clock;
                img = hit.Image;
                return true;
            }
        }
        img = null;
        return false;
    }

    private static async Task<BitmapImage?> LoadCoreAsync(string url, string cacheKey, int decodeWidth,
        Func<bool>? abandoned = null)
    {
        try
        {
            var cacheFile = Path.Combine(AppPaths.CacheDir, Hashing.Md5Hex(url) + ".jpg");
            // ★★★ 下载与写盘分开记账(2026-10-11 修"封面加载不出来、标题却正常"):
            //   原来这两句在同一个 try 里, 一旦 File.WriteAllBytesAsync 抛异常(缓存目录不可写/
            //   磁盘满/被杀软锁住), 就走 catch 里的 `if (!File.Exists(cacheFile)) return null` ——
            //   **明明已经把图片字节拿到手了, 却因为写不进磁盘而整张封面不显示**。
            //   用户看到的就是"标题有(标题来自接口 JSON, 与图片链路无关)、封面一片灰"。
            //   ⇒ 现在把字节留在 downloaded 里, 写盘失败就退回内存解码: 封面照常显示,
            //     只是这一张不进磁盘缓存(下次再看会重新下载)。
            byte[]? downloaded = null;
            try
            {
                if (!File.Exists(cacheFile))
                {
                    await DownloadGate.WaitAsync().ConfigureAwait(false);
                    try { downloaded = await Http.GetByteArrayAsync(url).ConfigureAwait(false); }
                    finally { DownloadGate.Release(); }
                    await File.WriteAllBytesAsync(cacheFile, downloaded).ConfigureAwait(false);
                }
            }
            catch
            {
                // 写盘失败**不等于**取图失败 —— 下面的内存兜底会处理; 真没字节才返回 null
            }

            // 解码放后台线程: 一张 1280 宽的 JPEG 解码要几毫秒到几十毫秒,
            // 放在 UI 线程上会直接表现为滚动/切页的卡顿。
            // ★ 并发闸门见 DecodeGate 的说明: 一次铺 24 张时不再让 24 个解码同时开跑。
            //
            // ★★ 放弃判定(abandoned)只做"省一次解码"的优化, **不是正确性闸门** ——
            //    真正决定贴不贴图的是 ApplyDecoded 里那道 Released 检查(在 UI 线程)。
            //    所以这里即使判断偏了, 也只是多/少解一张图, 不会贴错。
            BitmapImage? img;
            if (File.Exists(cacheFile))
            {
                var path = cacheFile;
                await DecodeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (abandoned != null && abandoned()) { img = null; }
                    else img = await Task.Run(() => FromFile(path, decodeWidth)).ConfigureAwait(false);
                }
                finally { DecodeGate.Release(); }
            }
            else if (downloaded != null)
            {
                // 磁盘上没有文件(写失败), 但字节在手上 ⇒ 直接从内存解码, 绝不让封面空着
                var bytes = downloaded;
                await DecodeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (abandoned != null && abandoned()) { img = null; }
                    else img = await Task.Run(() => FromBytes(bytes, decodeWidth)).ConfigureAwait(false);
                }
                finally { DecodeGate.Release(); }
            }
            else
            {
                img = null;   // 既没缓存也没下载到: 这才算真的取不到图
            }
            if (img == null) return null;

            lock (Lock)
            {
                if (!MemCache.ContainsKey(cacheKey))
                {
                    var entry = new Entry
                    {
                        Image = img,
                        Bytes = EstimateBytes(img),
                        LastUse = ++_clock
                    };
                    MemCache[cacheKey] = entry;
                    _cachedBytes += entry.Bytes;
                    TrimLocked();
                }
            }
            return img;
        }
        finally
        {
            lock (Lock) InFlight.Remove(cacheKey);
        }
    }

    private static int Quantize(int decodeWidth)
    {
        if (decodeWidth <= 0) return 0; // 0 = 原图, 不量化
        foreach (var s in WidthSteps)
            if (decodeWidth <= s) return s;
        return WidthSteps[^1];
    }

    /// <summary>估算解码后位图占用的字节(Pbgra32: 每像素 4 字节)</summary>
    private static long EstimateBytes(BitmapImage img)
    {
        var w = img.PixelWidth > 0 ? img.PixelWidth : 1;
        var h = img.PixelHeight > 0 ? img.PixelHeight : 1;
        return (long)w * h * 4;
    }

    /// <summary>
    /// 超出水位时按 LRU 淘汰。
    ///
    /// 原实现是"超过条数就删枚举到的第一条" —— 那既不看使用频率也不看占用大小,
    /// 冷图可能留下、热图反而被踢掉, 于是反复重新解码。
    /// 现在按 LastUse 从旧到新删, 并一次删到目标水位(留 20% 余量),
    /// 避免"每加一张就淘汰一张"的抖动。
    ///
    /// ★ 被钉住的条目(见 <see cref="Pins"/>)不参与淘汰 —— 那是"屏幕上正显示着它"的图,
    ///   淘汰了滚回来就得重新下载解码(用户看到闪一下)。但若钉住的量本身已经超过预算,
    ///   就**退回**不理会钉子: 宁可闪一下, 也不能让缓存无限增长。
    /// </summary>
    private static void TrimLocked()
    {
        if (_cachedBytes <= MaxCacheBytes && MemCache.Count <= MaxCacheCount) return;

        // ★ 先清掉**失效的钉子**: 卡片被销毁/换源时若漏了解钉, 这个键就会永久占着钉子。
        //   钉子只在"条目在缓存里"时才有意义, 所以不在缓存里的钉子一定是过期的,
        //   直接丢掉 —— 否则它们会让淘汰永远跳过这些键, 缓存突破预算。
        if (Pins.Count > 0)
        {
            var stale = new List<string>();
            foreach (var k in Pins.Keys)
                if (!MemCache.ContainsKey(k)) stale.Add(k);
            foreach (var k in stale) Pins.Remove(k);
        }

        long pinnedBytes = 0;
        foreach (var kv in Pins)
            if (MemCache.TryGetValue(kv.Key, out var pe)) pinnedBytes += pe.Bytes;

        // 钉子把预算吃穿了 ⇒ 这一轮不认钉子(否则一个都删不掉, 缓存只涨不落)
        var honorPins = pinnedBytes < MaxCacheBytes;

        var targetBytes = MaxCacheBytes * 8 / 10;
        var targetCount = MaxCacheCount * 8 / 10;

        foreach (var kv in MemCache.OrderBy(kv => kv.Value.LastUse).ToList())
        {
            if (_cachedBytes <= targetBytes && MemCache.Count <= targetCount) break;
            if (honorPins && Pins.ContainsKey(kv.Key)) continue;
            _cachedBytes -= kv.Value.Bytes;
            MemCache.Remove(kv.Key);
        }
    }

    /// <summary>
    /// 把某个缓存键钉住(引用计数 +1) —— 表示"现在屏幕上正显示着它, 别淘汰"。
    /// 由 <see cref="Cover"/> 在贴图成功时调用, 释放封面时 <see cref="Unpin"/>。
    /// 键不存在(还没进缓存)时也记一笔: 条目进缓存的同一刻起就受保护, 中间没有窗口期。
    /// </summary>
    public static void Pin(string cacheKey)
    {
        if (string.IsNullOrEmpty(cacheKey)) return;
        lock (Lock)
        {
            Pins[cacheKey] = Pins.TryGetValue(cacheKey, out var n) ? n + 1 : 1;
        }
    }

    /// <summary>解除一次钉住(引用计数 -1, 归零即删)。与 <see cref="Pin"/> 成对调用。</summary>
    public static void Unpin(string cacheKey)
    {
        if (string.IsNullOrEmpty(cacheKey)) return;
        lock (Lock)
        {
            if (!Pins.TryGetValue(cacheKey, out var n)) return;
            if (n <= 1) Pins.Remove(cacheKey);
            else Pins[cacheKey] = n - 1;
        }
    }

    /// <summary>算某个 url + 解码宽度对应的缓存键(与 <see cref="LoadAsync"/> 内部完全一致)</summary>
    public static string CacheKeyFor(string? url, int decodeWidth)
    {
        url = UrlUtil.Normalize(url);
        if (url.Length == 0) return "";
        var w = Quantize(decodeWidth);
        return w > 0 ? $"{url}|w{w}" : url;
    }

    private static BitmapImage? FromFile(string path, int decodeWidth = 0)
    {
        try
        {
            // ★ decodeWidth 的语义是"解码宽度的**上限**", 必须先探测自然尺寸再决定是否降采样。
            //   直接设 DecodePixelWidth 的话, WIC 会把**小于该值**的图也放大到目标宽度
            //   (探针实测: 512x512 传 4096 解出来是 4096x4096) —— 内存白吃几十倍,
            //   图片预览的适配比例还会被搅乱(图被虚增到 4096 宽, 适配算出来只有 23%)。
            //   BitmapDecoder 只读元数据不解像素, 探测开销可以忽略。
            var effectiveDecodeWidth = 0;
            if (decodeWidth > 0)
            {
                try
                {
                    using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var decoder = BitmapDecoder.Create(probe, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnDemand);
                    if (decoder.Frames.Count > 0 && decoder.Frames[0].PixelWidth > decodeWidth)
                        effectiveDecodeWidth = decodeWidth;
                }
                catch
                {
                    // 探测失败(坏文件/不认识格式)时保持不降采样 —— 后面的解码自然会失败并清缓存
                    effectiveDecodeWidth = 0;
                }
            }

            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (effectiveDecodeWidth > 0) bi.DecodePixelWidth = effectiveDecodeWidth;
            // ★ using: BitmapCacheOption.OnLoad 会在 EndInit() 里把像素全部读进内存,
            //   流之后再没人用 —— 不关的话每张封面留一个文件句柄, 而且要等终结器才释放
            //   (缓存文件被锁住时"清缓存"删不掉, 长会话下句柄数还会一路涨)。
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bi.StreamSource = fs;
                bi.EndInit();
            }
            // 必须 Freeze: 位图是在后台线程创建的, 只有冻结后才能安全地被 UI 线程使用
            bi.Freeze();
            return bi;
        }
        catch
        {
            try { File.Delete(path); } catch { /* 忽略损坏缓存文件 */ }
            return null;
        }
    }

    /// <summary>
    /// 从**内存字节**解码(不落盘)。
    ///
    /// 为什么需要它: LoadCoreAsync 里"下载"与"写盘"是两步, 写盘可能失败(缓存目录不可写/
    /// 磁盘满/被占用), 而那时图片字节**已经在手上**。没有这条兜底就只能返回 null,
    /// 表现为"标题正常、封面全灰"。见 LoadCoreAsync 里的说明。
    /// ★ 与 FromFile 的关键差别: 这里**不能**在失败时删文件(压根没有文件);
    ///   DecodePixelWidth 也不能无条件设 —— WIC 会把小于该值的图放大, 所以同样要先用
    ///   BitmapDecoder 探自然宽, 只在原图更宽时才降采样。
    /// </summary>
    private static BitmapImage? FromBytes(byte[] bytes, int decodeWidth = 0)
    {
        try
        {
            var effectiveDecodeWidth = 0;
            if (decodeWidth > 0)
            {
                try
                {
                    using var probe = new MemoryStream(bytes, writable: false);
                    var decoder = BitmapDecoder.Create(probe, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnDemand);
                    if (decoder.Frames.Count > 0 && decoder.Frames[0].PixelWidth > decodeWidth)
                        effectiveDecodeWidth = decodeWidth;
                }
                catch
                {
                    effectiveDecodeWidth = 0;
                }
            }

            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;   // 立刻读完, 之后 MemoryStream 可以释放
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (effectiveDecodeWidth > 0) bi.DecodePixelWidth = effectiveDecodeWidth;
            // ★ using: 与 FromFile 同理, OnLoad 之后流就没用了, 别把它挂在那儿等 GC
            using (var ms = new MemoryStream(bytes, writable: false))
            {
                bi.StreamSource = ms;
                bi.EndInit();
            }
            // 必须 Freeze: 位图是在后台线程创建的, 只有冻结后才能安全地被 UI 线程使用
            bi.Freeze();
            return bi;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>清空磁盘缓存并返回删除的总字节数</summary>
    public static long ClearDiskCache()
    {
        // 先清内存缓存再删磁盘文件: 避免删掉的文件又被加载回内存
        lock (Lock)
        {
            MemCache.Clear();
            _cachedBytes = 0;
        }
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.CacheDir))
            {
                try
                {
                    var len = new FileInfo(f).Length;
                    File.Delete(f);
                    total += len;
                }
                catch { /* 被占用的跳过 */ }
            }
        }
        catch { /* 忽略 */ }
        return total;
    }

    /// <summary>磁盘缓存大小(字节)</summary>
    public static long GetDiskCacheSize()
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.CacheDir))
            {
                try { total += new FileInfo(f).Length; } catch { /* 忽略 */ }
            }
        }
        catch { /* 忽略 */ }
        return total;
    }
}

/// <summary>
/// 附加属性 Cover.Source: 绑定到 Border 上, 异步加载封面并淡入显示
/// 用法: behaviors:Cover.Source="{Binding Cover}"
///
/// ★ 清晰度(2026-10-01, 用户反馈"封面有些模糊"): 探针 A/B/C(cover-sharpness.png)裁决 ——
///   **主犯是 WPF 默认的 BitmapScalingMode.Linear**: 封面解码宽 > 显示物理像素,
///   Linear 缩小既偏软又出摩尔纹杂色(噪点糊成一团)。挂上 **Fant**(高质量重采样)后干净且细节足;
///   解码宽继续保留 ×1.25 余量(更多源像素 + Fant = 细节最好, A/B 实测它优于"贴近 1:1")。
///   ③ 另补"显示需求超过已解码宽度 12% 就重解码": 卡片墙会随窗口变宽(最大化 236→256 DIP),
///   旧逻辑只解一次, 变宽后等于在放大旧位图。
/// </summary>
public static class Cover
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(string), typeof(Cover),
        new PropertyMetadata(null, OnSourceChanged));

    public static string? GetSource(DependencyObject d) => (string?)d.GetValue(SourceProperty);
    public static void SetSource(DependencyObject d, string? value) => d.SetValue(SourceProperty, value);

    /// <summary>最近一次实际解码出的像素宽(应用成功后记录), 供"变宽了就重解码"判断。</summary>
    private static readonly DependencyProperty DecodedPixelWidthProperty =
        DependencyProperty.RegisterAttached("DecodedPixelWidth", typeof(int), typeof(Cover),
            new PropertyMetadata(0));

    /// <summary>
    /// 这张卡当前贴着的那张图在 <see cref="CoverLoader"/> 里的**缓存键**(没贴图时为空串)。
    ///
    /// ★ 为什么要记它: ① 贴新图时要把旧的那张**解钉**(否则钉子只增不减, 缓存再也不会淘汰);
    ///   ② 离屏释放(<see cref="Release"/>)时才知道该解谁的钉;
    ///   ③ 恢复(<see cref="Restore"/>)时可以直接命中缓存, 不必等一次异步往返。
    /// </summary>
    private static readonly DependencyProperty DecodedCacheKeyProperty =
        DependencyProperty.RegisterAttached("DecodedCacheKey", typeof(string), typeof(Cover),
            new PropertyMetadata(""));

    private static string GetDecodedCacheKey(DependencyObject d) =>
        (string?)d.GetValue(DecodedCacheKeyProperty) ?? "";

    /// <summary>这张卡现在有没有图(供卡片墙判断"要不要恢复"/"能不能释放")。</summary>
    public static bool HasImage(DependencyObject d)
        => (int)d.GetValue(DecodedPixelWidthProperty) > 0;

    /// <summary>
    /// 这一格是否**正在**下载/解码。
    ///
    /// ★ 卡片墙每次扫描都会对"没有图且在恢复带内"的卡片调 <see cref="Restore"/>,
    ///   而下载/解码要几十到几百毫秒 —— 没有这个标记的话, 150ms 一次的扫描会为同一张图
    ///   反复发起 LoadAsync(虽然 CoverLoader 内部有 InFlight 去重, 但每次都要重新算宽度、
    ///   排队回 UI 线程、跑一遍 ApplyDecoded, 白费 CPU 还容易让淡入动画重复触发)。
    ///
    /// ★★★ 存的是**开始时间**(Environment.TickCount64), 不是 bool(2026-11-07 修"排行榜
    ///   在小窗口下部分封面永久灰"): 布尔标记一旦漏清(异常路径、超时挂起、卡片被回收又复用),
    ///   就永久为 true, `Restore` 会被永久拦住 ⇒ 封面永久灰, 只有改变窗口尺寸重新布局
    ///   才可能解开。存时间戳就能自愈: 超过 <see cref="LoadingStaleMs"/> 一律当成"没在加载"。
    /// </summary>
    private static readonly DependencyProperty LoadingSinceProperty =
        DependencyProperty.RegisterAttached("LoadingSince", typeof(long), typeof(Cover),
            new PropertyMetadata(0L));

    /// <summary>
    /// 超过这么久还没结束, 就认为那一轮已经死了(见 <see cref="LoadingSinceProperty"/>)。
    ///
    /// ★ 取 1.5 秒而不是更长: 正常一次加载(命中缓存几毫秒 / 下载+解码通常 <1s)不会碰到它,
    ///   而真的卡住时用户最多等 1.5 秒就会自动重试 —— 观感是"过一下就自己好了",
    ///   而不是"必须把窗口拉大才有图"。
    /// </summary>
    private const int LoadingStaleMs = 1500;

    /// <summary>这一格现在是否真的有一轮加载在跑(带过期自愈)</summary>
    private static bool IsLoading(DependencyObject d)
    {
        var since = (long)d.GetValue(LoadingSinceProperty);
        if (since == 0) return false;
        return Environment.TickCount64 - since < LoadingStaleMs;
    }

    /// <summary>这一格现在是否真的有一轮加载在跑(带过期自愈)。供卡片墙判断"该不该释放"。</summary>
    public static bool IsLoadingNow(DependencyObject d) => IsLoading(d);

    private static void MarkLoading(DependencyObject d, bool on)
        => d.SetValue(LoadingSinceProperty, on ? Environment.TickCount64 : 0L);

    /// <summary>
    /// 这一格被卡片墙**放掉**了(离屏太远)。在途的下载/解码回来时看到它为 true 就不贴图 ——
    /// 见 <see cref="Release"/>。换源(<see cref="OnSourceChanged"/>)或恢复时清掉。
    /// </summary>
    private static readonly DependencyProperty ReleasedProperty =
        DependencyProperty.RegisterAttached("Released", typeof(bool), typeof(Cover),
            new PropertyMetadata(false));

    /// <summary>
    /// 这一格的加载**已经失败**(网络错误 / 图片坏了), 值为失败时刻。
    ///
    /// ★ 为什么必须记: 卡片墙每 150ms 扫一次"没有图且在恢复带内"的卡片并调 <see cref="Restore"/>,
    ///   没有这个标记的话, 一张加载失败的封面会**每 150ms 重试一次**, 一直重试到用户离开页面 ——
    ///   弱网下就是持续的请求风暴。
    /// ★ 为什么存时间戳而不是 bool(2026-11-07): 存 bool 的话一次**临时**断网就会让这张封面
    ///   永久灰下去(只有换源才会清)。存时刻就能在一段时间后自动再试一次 ——
    ///   真坏图会再次失败并重新标记, 代价只是每 30 秒一次请求。
    /// </summary>
    private static readonly DependencyProperty FailedAtProperty =
        DependencyProperty.RegisterAttached("FailedAt", typeof(long), typeof(Cover),
            new PropertyMetadata(0L));

    /// <summary>失败后多久允许重试一次</summary>
    private const int FailedRetryMs = 30_000;

    /// <summary>这一格是否处于"失败冷却"中(失败过且还没到重试时间)</summary>
    private static bool IsFailedCoolingDown(DependencyObject d)
    {
        var at = (long)d.GetValue(FailedAtProperty);
        if (at == 0) return false;
        return Environment.TickCount64 - at < FailedRetryMs;
    }

    /// <summary>
    /// 这一格是否在**卡片墙**里(卡片墙是唯一会 Release/Restore 封面的地方)。
    ///
    /// ★ 只有卡片墙里的封面才值得钉住(见 <see cref="ApplyDecoded"/> 里的说明);
    ///   别处(动态页/私信/播放器/我的页)的封面不会被释放, 钉了只会把缓存钉死。
    /// ★ 判据是"往上找得到 <see cref="CardWallPanel"/>"。不要用"ItemWidth 是不是默认值"
    ///   之类的旁证 —— 卡片墙正好 236 宽时就会误判。这次查找每张图只做一次(贴图时)。
    /// </summary>
    private static bool GetInCardWall(DependencyObject d)
        => VisualTreeUtil.FindAncestor<CardWallPanel>(d) != null;

    /// <summary>
    /// 从一棵子树里找出**所有**承载封面的 Border(挂了 <c>Cover.Source</c> 的那些)。
    ///
    /// ★ 为什么需要它: 卡片墙的孩子是 <c>ContentPresenter</c>(里面才是 VideoCard/BangumiCard),
    ///   封面 Border 埋在卡片模板里好几层。卡片墙只拿得到孩子, 必须往下找才能
    ///   调 <see cref="Release"/>/<see cref="Restore"/>。
    /// ★ 为什么是**复数**: DynamicCard 一张卡上有两个封面 —— 36×36 的头像和 200×112 的
    ///   正文封面。只找第一个(BFS 先撞到头像)会漏掉真正占内存的那张。
    /// ★ 找不到(还没实例化/这卡片压根没有封面)返回空数组, 调用方下次扫描再试。
    /// </summary>
    public static Border[] FindCoverBorders(DependencyObject? root)
    {
        if (root == null) return Array.Empty<Border>();
        var found = new List<Border>(2);
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (cur is Border b && b.ReadLocalValue(SourceProperty) != DependencyProperty.UnsetValue)
                found.Add(b);

            var n = VisualTreeHelper.GetChildrenCount(cur);
            for (var i = 0; i < n; i++)
                queue.Enqueue(VisualTreeHelper.GetChild(cur, i));
        }
        return found.Count == 0 ? Array.Empty<Border>() : found.ToArray();
    }

    /// <summary>
    /// 放掉这一格的封面位图, 退回占位底色 —— **离屏卡片的内存窗口**(2026-11-07 P1)。
    ///
    /// ★★ 为什么必须由卡片墙统一驱动, 而不是靠 LRU: 卡片墙是零虚拟化 ItemsControl,
    ///   卡片容器**永远不销毁**, 它自己的 <c>ImageBrush</c> 对位图是**强引用** ——
    ///   <see cref="CoverLoader"/> 那 32MB 上限只能把字典里的条目挤掉, 挤不掉卡片手里的这份。
    ///   于是"滚过多少张"就等于"常驻多少张位图", 内存随滚动线性上涨。
    ///
    /// ★ 只松引用, **不动** <see cref="SourceProperty"/>、不动 SizeChanged 订阅、不改布局:
    ///   所以卡片的位置/尺寸/滚动位置一律不变, 恢复时走的是同一条 <see cref="LoadAsync"/> 路径。
    /// ★ 恢复时位图多半还在 MemCache 里(我们钉住了可见区), 命中即**同步贴图、不淡入** ——
    ///   正常滚动速度下用户看不到"占位 → 淡入"这个过程。
    /// </summary>
    public static void Release(Border border)
    {
        // ★★★ 2026-11-07 第五次修: 这里**不能**在"没图"时直接返回。
        //   旧代码第一句是 `if (!HasImage(border)) return;` —— 于是"正在加载中(还没贴图)"
        //   的卡片**完全被跳过**: 既没解钉, 也没打 `Released` 标记。
        //   后果: 那张卡的在途任务回来后照样贴图(`rel=False`), 释放等于白做;
        //   而且这些在途任务会一直占着解码槽 —— 探针实测滚过 6 页后
        //   InFlight 积压 212 个、DecodeGate 一个空槽都没有, 用户回顶时要等近 2 秒
        //   才能看到第一张(200 多个任务排在前面)。
        //   ⇒ 现在无论有没有图都要走完整流程: 解钉、打标记、还原占位。
        //     打上 `Released` 后, 在途结果回来时 ApplyDecoded 会直接丢弃, 不再贴图。
        var hadImage = HasImage(border);

        var key = GetDecodedCacheKey(border);
        if (key.Length > 0) CoverLoader.Unpin(key);
        border.SetValue(DecodedCacheKeyProperty, "");
        // ★ 打上"已释放"标记: 若此刻正好有一轮下载/解码在途, 它回来时**不许再贴图** ——
        //   否则刚放掉的离屏卡片又被贴回去, 这一轮释放等于白做(下一轮扫描再放, 反复抖动)。
        //   在途结果仍会进 MemCache, 滚回来时 Restore 直接命中, 一点不浪费。
        border.SetValue(ReleasedProperty, true);
        // ★ 同步到线程安全的放弃标记(后台线程只能读它, 不能读 DependencyObject)。
        CoverLoader.MarkAbandoned(border);

        // 还原成"没图之前的样子": 占位底色 + 占位图标(头像里的人形)重新可见
        if (hadImage)
        {
            border.Background = GetPlaceholderBrush(border);
            if (border.Child != null) border.Child.Visibility = Visibility.Visible;
            border.SetValue(DecodedPixelWidthProperty, 0);
            // 淡入动画可能还挂着: 清掉, 免得恢复时被旧的动画值接管
            border.BeginAnimation(UIElement.OpacityProperty, null);
            border.Opacity = 1.0;
        }
    }

    /// <summary>
    /// 把被 <see cref="Release"/> 放掉的封面贴回来(滚回视口附近时由卡片墙调用)。
    ///
    /// ★ 命中缓存时是**同步**贴上的(走 <see cref="LoadAsync"/> 的快路径), 不会有淡入;
    ///   只有真的被淘汰了才重新下载+解码, 那时才看得见一次淡入 —— 这是刻意的代价,
    ///   好过让内存随滚动无上界增长。
    ///
    /// ★★★ <paramref name="force"/> = true 表示"**这一格用户正看着, 无论如何都要有图**"
    ///   (2026-11-07 第二次修"滚回来封面不加载"): 可见区内的卡片走强制路径 ——
    ///   忽略失败冷却、并且**先清掉在途标记**再重新发起。
    ///   正常路径(滚到附近但还没进视口)仍然走冷却/去重, 不会变成请求风暴。
    /// </summary>
    public static void Restore(Border border, bool force = false)
    {
        if (HasImage(border)) return;
        var url = GetSource(border);
        if (string.IsNullOrWhiteSpace(url)) return;

        // ★★★ 2026-11-07 第五次修: `force` 的语义收窄为"**忽略失败冷却**",
        //   **不再无条件打断在途加载**。
        //
        //   旧写法在 force 时先 `MarkLoading(false)` 再发 —— 本意是"那一轮可能早就死了"。
        //   但卡片墙的扫描是每 150ms 一轮, 于是**每一轮都会把上一轮的在途标记清掉、
        //   再发一个新的**。后果是同一张图被并发加载十几次, 而旧任务的 finally 又会把
        //   新任务的标记清掉 —— 标记彻底失去意义, 解码队列被重复任务塞满,
        //   真正该先出的视口封面排在后面。
        //
        //   现在: 只要确实有一轮在跑(且没超过 LoadingStaleMs)就**直接返回**;
        //   超过 1.5 秒才认为它死了, 才重新发起 —— 这条对 force 同样成立,
        //   否则"每轮都重发"的请求风暴会回来。
        if (!force && IsFailedCoolingDown(border)) return;    // 失败冷却中, 过一阵再试
        if (IsLoading(border)) return;                        // 已有一轮在跑(带过期自愈)

        // ★★★ 这里**必须**清 Released: 它的语义是"在途结果回来时不许贴图"。
        //   一旦那轮加载已经结束, 就再没人清它 —— 而 ApplyDecoded 见到它就**永远不贴图**,
        //   于是这张卡永久灰, 连滚回来都救不了(只能改窗口尺寸强制重排)。
        //   ⇒ Restore 是"我要这张图"的明确意图, 清掉它才能真的恢复。
        border.SetValue(ReleasedProperty, false);
        // ★ 同步清掉线程安全的放弃标记(见 CoverLoader.AbandonFlags)。
        CoverLoader.ClearAbandoned(border);
        if (force) border.SetValue(FailedAtProperty, 0L);
        _ = LoadAsync(border, url);
    }

    /// <summary>
    /// 贴图**之前**那一格原本的底色(头像的浅粉圆 / 卡片的占位底板)。
    ///
    /// ★ 为什么单独存一份: 贴图时 <c>border.Background</c> 被 ImageBrush **覆盖**了,
    ///   而 XAML 里写的 Background(浅粉圆)是**同一个属性**的本地值 —— 一旦被覆盖就找不回来,
    ///   直接置 null 会连浅粉圆一起丢掉(头像变成没有底的透明圆)。
    ///   换源清图时用它还原, 才是"回到没图之前的样子"。
    /// </summary>
    private static readonly DependencyProperty PlaceholderBrushProperty =
        DependencyProperty.RegisterAttached("PlaceholderBrush", typeof(Brush), typeof(Cover),
            new PropertyMetadata(null));

    private static Brush? GetPlaceholderBrush(DependencyObject d) =>
        (Brush?)d.GetValue(PlaceholderBrushProperty);

    private static void SetPlaceholderBrush(DependencyObject d, Brush? v) =>
        d.SetValue(PlaceholderBrushProperty, v);

    /// <summary>显示需求(物理像素)超过已解码宽度这么多倍才重解码 —— 量化档位本身有 0~40% 富余,
    /// 阈值太小会在档位内反复重解码。</summary>
    public const double RedecodeFactor = 1.12;

    /// <summary>纯函数(探针可断言): 显示物理需求是否已经明显超过已解码宽度。</summary>
    public static bool NeedsRedecode(double needPx, int decodedPx)
        => decodedPx > 0 && needPx > decodedPx * RedecodeFactor;

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border) return;
        // ★★ 这里**不再**无条件 `border.Opacity = 0`(2026-10-02 修"切 tab 封面闪几下"):
        //   切 tab 时整墙卡片被销毁重建, 每次重建这个回调都会跑 —— 无条件清透明度会让每张封面
        //   "先整块消失、等异步回调回来再出现", 和 VideoCard 的入场动画叠在一起, 就是用户看到的
        //   "切 tab 闪几下"。清空改到**真正要下载**的那条路径里(见 LoadAsync 的 miss 分支):
        //   命中内存缓存走快路径时**根本不碰 Opacity**, 封面稳稳地直接换上。
        border.SetValue(DecodedPixelWidthProperty, 0);
        // 换源 = 重新开始: 上一轮的"已释放/已失败"标记不再适用, 否则新源永远贴不上
        border.SetValue(ReleasedProperty, false);
        border.SetValue(FailedAtProperty, 0L);
        // ★ 同步清掉线程安全的放弃标记(见 CoverLoader.AbandonFlags)。
        CoverLoader.ClearAbandoned(border);
        var url = e.NewValue as string;

        // ★★★ 换源时**必须先把旧图清掉**(2026-11-06 用户报"更新的 UP 显示的是别人的头像"):
        //   虚拟化 ListBox(Recycling 模式)会把容器回收给下一个 UP, 同一块 Border 上的
        //   Background 还留着**上一个 UP 的 ImageBrush**。新图是异步下载+解码的,
        //   这中间(可达几百毫秒, 网络慢时更久)那一格就显示着别人的头像 ——
        //   滚动时表现为"头像乱跳/串行"。
        //   这里把 Background 还原成占位底色(浅粉圆)并让占位人形重新可见, 等新图到位再贴上。
        //   ★ 还原成"占位底色"而不是 null: null 会把 XAML 里写的浅粉圆一起丢掉
        //   (贴图时被覆盖了, 找不回来), 头像会变成没有底的透明圆。
        //   ★ 占位底色在这里捕获(而不是等 ApplyDecoded): 换源可能发生在**第一张图还没贴上**时,
        //   那时 Background 还是 XAML 写的浅粉圆, 正该记下来。
        if (GetPlaceholderBrush(border) == null && border.Background is not ImageBrush)
            SetPlaceholderBrush(border, border.Background);
        if (border.Background is ImageBrush)
        {
            border.Background = GetPlaceholderBrush(border);
            if (border.Child != null) border.Child.Visibility = Visibility.Visible;
        }
        // ★ 换源同样要**解钉**: 这张卡马上就不显示旧图了, 再钉着它等于让旧图永不淘汰。
        var oldKey = GetDecodedCacheKey(border);
        if (oldKey.Length > 0)
        {
            CoverLoader.Unpin(oldKey);
            border.SetValue(DecodedCacheKeyProperty, "");
        }

        // 尺寸变化监听: 变宽超过已解码宽度的一定比例就重解码。先 - 再 + 防同元素换源时重复订阅;
        // 处理器挂在静态类上、目标随卡片销毁, 无泄漏面。
        border.SizeChanged -= OnCoverSizeChanged;
        border.Unloaded -= OnCoverUnloaded;
        border.Unloaded += OnCoverUnloaded;
        if (string.IsNullOrWhiteSpace(url)) return;
        border.SizeChanged += OnCoverSizeChanged;

        _ = LoadAsync(border, url);
    }

    /// <summary>
    /// 卡片被移出可视树(列表被清空/刷新)时解钉。
    ///
    /// ★ 为什么必须做: 钉子是**引用计数**, 只有 <see cref="Release"/> 才会减。列表刷新时
    ///   卡片容器是被整体丢弃的, 没人会去调 Release —— 那些钉子就永久留在
    ///   <see cref="CoverLoader.Pins"/> 里, 把一批已经没人看的图钉在缓存里,
    ///   真正在用的图反而被挤出去。
    /// ★ 注意切页**不会**触发它: 页面是 Visibility.Collapsed, 仍在可视树里(见 MainWindow)。
    /// </summary>
    private static void OnCoverUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border border) return;
        var key = GetDecodedCacheKey(border);
        if (key.Length == 0) return;
        CoverLoader.Unpin(key);
        border.SetValue(DecodedCacheKeyProperty, "");
    }

    private static async void OnCoverSizeChanged(object sender, SizeChangedEventArgs e)
    {
        try
        {
            if (sender is not Border border || e.NewSize.Width <= 0) return;
            var url = GetSource(border);
            if (string.IsNullOrWhiteSpace(url)) return;
            var decoded = (int)border.GetValue(DecodedPixelWidthProperty);
            if (decoded <= 0) return;   // 首次解码还没落地, 那次本来就会按当前宽度算
            var dpi = VisualTreeHelper.GetDpi(border).DpiScaleX;
            if (dpi <= 0) dpi = 1.0;
            if (NeedsRedecode(border.ActualWidth * dpi, decoded))
                _ = LoadAsync(border, url);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
        }
    }

    private static async Task LoadAsync(Border border, string url)
    {
        // ★ 标记"这一格正在加载": 卡片墙每 150ms 扫一次, 没有这个标记就会对同一张图
        //   反复发起(见 LoadingSinceProperty 的说明)。
        // ★ 标记存的是开始时间戳 —— 即使这里因为任何原因没走到 finally(超时挂起、
        //   线程中止), 5 秒后也会被当成"没在加载"而自愈, 不会永久卡死。
        MarkLoading(border, true);
        try
        {
            await LoadCoreUIAsync(border, url);
        }
        finally
        {
            MarkLoading(border, false);
        }
    }

    private static async Task LoadCoreUIAsync(Border border, string url)
    {
        // 按"元素显示宽度 × 屏幕缩放"解码, 再留 25% 余量给缩放时的清晰度; 上限 1280。
        // (2026-10-01 曾试过去掉 ×1.25 走"≈1:1", 探针 A/B 后放弃: 解码像素多一点 + Fant
        //  高质量重采样, 细节比 1:1 更足 —— 模糊的主犯是 Linear 缩放模式, 不是解码宽度。)
        //
        // ★ 宽度还量不出来时**等一等**, 而不是退化成固定 480(2026-09-28 改):
        //   DataTemplate 一实例化绑定就生效, 那一刻 ActualWidth 必然还是 0 —— 于是
        //   "一屏几百张卡片全部按 480 解码", 而按真实宽度(VideoCard 236px → 320 档)其实
        //   只要 1/2.25 的内存。更要命的是 CoverLoader 那 32MB 的缓存上限**管不到它**:
        //   卡片自己攥着 ImageBrush/BitmapImage 的引用, 缓存把条目挤掉位图也释放不掉。
        //   这正是「稍后再看」两三百条一打开内存爆掉、加载也卡的直接原因。
        //   等到 SizeChanged 报出真实宽度再解码; 元素中途被卸载就放弃, 不留挂起的等待。
        var dpi = VisualTreeHelper.GetDpi(border).DpiScaleX;
        if (dpi <= 0) dpi = 1.0;
        if (border.ActualWidth <= 0)
        {
            var wait = WaitForRealWidth(border);
            if (wait != null) await wait;
            if (GetSource(border) != url) return; // 等待期间源变了/元素被丢弃, 放弃
        }
        var w = border.ActualWidth > 0
            ? (int)Math.Min(1280, Math.Max(160, border.ActualWidth * dpi * 1.25))
            : 480;

        // ★★ 快路径(2026-10-02, 用户报"切 tab 每次都重新加载封面"): 位图已经在内存缓存里,
        //   直接同步贴上、**不闪不淡入**。原来这里无条件走完整异步路径 + 240ms 淡入 ——
        //   即便 CoverLoader.LoadAsync 命中缓存是零等待, ApplyDecoded 里那句
        //   `ShouldPlayFadeIn(prev<=0)` 还是会因为"这是新容器、prev 被重置成 0"而重新播一遍
        //   0→1 淡入, 于是每次切 tab 几百张封面一起闪一遍, 观感就是"封面重新加载了"。
        //   命中缓存说明这格之前见过、不是真正的首次加载, 不该再淡入。
        if (CoverLoader.TryGetCached(url, w, out var cached) && cached != null)
        {
            await Application.Current.Dispatcher.InvokeAsync(
                () => ApplyDecoded(border, url, cached, w, fade: false));
            return;
        }

        // 缓存 miss: 真正要下载/解码。这里**也不清透明度** —— 封面 Border 默认 Opacity=1 但还没
        // 贴背景(透明), 于是外层 CoverCard 的 PlaceholderBrush 会透出来当占位符; 等图解好
        // ApplyDecoded(fade:true) 再播 0→1 淡入盖上去。全程没有"先整块变黑再变回来"的闪。
        // ★★★ 2026-11-07 撤掉 `abandoned` 优化(它引入了"相关视频封面不显示"这个真 bug)。
        //
        //   那个参数原本是为了"卡片已被卡片墙 Release 时跳过解码、把解码槽让给视口内的"。
        //   但它要求回调在**后台线程**判断状态, 而判断所需的 `border.GetValue(Released)`
        //   是 DependencyObject API, 只能在 UI 线程调 —— 跨线程就抛
        //   `InvalidOperationException: 调用线程无法访问此对象`。
        //   而调用方是 `_ = LoadAsync(...)`(fire-and-forget), 异常被吞进"无人观察的任务",
        //   界面上只表现为"这一格永远灰、loading/failed 都是 0"。
        //   探针实测: 8 条相关视频只有 4 条出图。
        //
        //   ★ 为什么是**撤掉**而不是"改成线程安全地读": 试过三种写法(弱表 / 提前快照 /
        //     dispatcher.Invoke), 每一种都会让卡片墙首屏出现 4~12 张缺图 ——
        //     因为卡片墙原来能 14/14 全过, 恰恰是靠"这个回调抛异常 ⇒ 任务 faulted ⇒
        //     InFlight 保留失败任务 ⇒ 不重复加载"这个**副作用**在兜底。那是 bug 互相抵消,
        //     不是设计。要留住它只能刻意保留一个抛异常的路径, 那比不要这个优化更糟。
        //
        //   ★ 正确性没有损失: "该不该贴图"的闸门一直只有一处 —— ApplyDecoded 里的
        //     `Released` 检查(在 UI 线程执行)。这个优化省下的只是"解码一张没人要的图"
        //     那点 CPU, 而它带来的时序脆弱性远大于收益。
        var img = await CoverLoader.LoadAsync(url, w,
            // ★★★ 2026-11-07 修"相关视频 / 评论头像封面不显示"(真凶):
            //   `abandoned` 在**后台线程**执行(LoadCoreAsync 里 ConfigureAwait(false) 之后),
            //   而 `border.GetValue(...)` 是 DependencyObject API, **只能在 UI 线程调用** ——
            //   跨线程调用会抛 InvalidOperationException, 而调用方是 `_ = LoadAsync(...)`,
            //   异常被吞进"无人观察的任务"⇒ 这一格永远灰(探针实测: 8 条相关视频只出 4 条)。
            //   ⇒ 改读 UI 线程维护的**普通 bool**(见 CoverLoader.AbandonFlagFor)。
            abandoned: CoverLoader.AbandonFlagFor(border)).ConfigureAwait(true);
        if (img == null)
        {
            // ★ 记下"这一张取不到"的时刻: 卡片墙每 150ms 会重扫一遍没有图的卡片并调 Restore,
            //   不标记的话弱网下就是每 150ms 重试一次(见 FailedAtProperty 的说明)。
            //   ★ 但"被卡片墙放掉"导致的不贴图**不算失败** —— 那不是取不到图, 是故意不贴。
            if (!(bool)border.GetValue(ReleasedProperty))
                border.SetValue(FailedAtProperty, Environment.TickCount64);
            return;
        }
        // 成功了就把失败记录清掉(否则 30 秒冷却期会挡住以后真正的重试)
        border.SetValue(FailedAtProperty, 0L);

        await Application.Current.Dispatcher.InvokeAsync(
            () => ApplyDecoded(border, url, img, w, fade: true));
    }

    /// <summary>
    /// 把解好的位图贴到卡片上(**必须在 UI 线程**)。
    /// </summary>
    private static void ApplyDecoded(Border border, string url, BitmapImage img, int requestedWidth, bool fade)
    {
        void Apply()
        {
            if (GetSource(border) != url) return; // 排队期间这一格被回收/换了源
            // ★ 排队期间卡片被卡片墙放掉了(滚出视口很远): 不要贴图, 否则刚放掉的位图又被
            //   挂回去, 这一轮释放白做(下一轮扫描再放, 反复抖动)。位图已经进 MemCache,
            //   滚回来时 Restore 直接命中。见 ReleasedProperty。
            if ((bool)border.GetValue(ReleasedProperty)) return;

            // 乱序收尾防护: 更小的解码结果后到(宽了两次、两次重解码并发) —— 丢弃,
            // 已应用的那份更大; 若显示需求此时又超过它, SizeChanged 会再触发一轮。
            var prev = (int)border.GetValue(DecodedPixelWidthProperty);
            if (prev > 0 && img.PixelWidth < prev) return;

            var brush = new ImageBrush(img)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
            brush.Freeze();
            // ★ Fant(高质量重采样): 默认 Linear 在"解码宽 > 显示宽"的缩小场景下明显偏软。
            border.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.Fant);
            // 记住贴图前的占位底色(浅粉圆等): 换源清图时要用它还原 —— 见 PlaceholderBrushProperty。
            // (正常流程里 OnSourceChanged 已经记下了; 这里兜底, 防它没跑到。)
            if (GetPlaceholderBrush(border) == null && border.Background is not ImageBrush)
                SetPlaceholderBrush(border, border.Background);
            border.Background = brush;

            // ★ 换图先解旧钉再钉新键(引用计数必须成对, 否则钉子只增不减, 缓存再也不会淘汰)。
            //   钉住 = "屏幕上正显示着它", 让 LRU 别把它挤掉 —— 这样离屏释放后再滚回来能同步命中,
            //   不会出现"占位 → 淡入"的闪动。见 CoverLoader.Pins。
            //
            // ★★ 只给**卡片墙里**的封面钉: 卡片墙是唯一会 Release/Restore 的地方,
            //   也只有它需要"滚回来能同步命中"。动态页/私信/播放器里那些封面从头到尾不会
            //   被释放, 钉它们毫无意义 —— 反而会因为**永远不解钉**把缓存逐步钉死。
            var newKey = CoverLoader.CacheKeyFor(url, requestedWidth);
            var prevKey = GetDecodedCacheKey(border);
            if (prevKey.Length > 0 && prevKey != newKey) CoverLoader.Unpin(prevKey);

            var inWall = GetInCardWall(border);
            if (newKey.Length > 0 && prevKey != newKey && inWall) CoverLoader.Pin(newKey);
            border.SetValue(DecodedCacheKeyProperty, inWall ? newKey : "");

            // 图片加载成功后隐藏占位符(如头像里的人形图标), 否则会一直叠在图片上
            if (border.Child != null) border.Child.Visibility = Visibility.Collapsed;
            border.SetValue(DecodedPixelWidthProperty, img.PixelWidth);

            // ★★ 只有"这一格第一次出图"才淡入(且调用方明确要 fade)。变宽触发的重解码、以及
            //   切 tab 重建容器后的缓存命中, 卡片上**都已经有/刚有过图**, 再播一次 0→1 的淡入
            //   = 封面先整块消失再浮出来; 最大化那一下视口内几十张同时重解码, 几十张一起闪,
            //   用户看到的就是"抽动"(2026-10-01 定位)。已经有图就静默换掉 ——
            //   底下换的是更清晰的位图, 画面不该有任何跳变。
            if (fade && ShouldPlayFadeIn(prev))
            {
                border.Opacity = 1.0;
                border.BeginAnimation(UIElement.OpacityProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(0, 1,
                        TimeSpan.FromMilliseconds(240)));
            }
            else
            {
                // 清掉可能残留的淡入动画, 保证不透明度稳定在 1
                border.BeginAnimation(UIElement.OpacityProperty, null);
                border.Opacity = 1.0;
            }

            // 解码期间卡片又变宽了(渐进重排/最大化): 立刻补一轮, 别等下一次 SizeChanged。
            // ★ img.PixelWidth < w 说明**源图本身**比请求档位还小 —— 没有更多像素可取了,
            //   上面记录的 0 会让 SizeChanged 的重解码判断直接放行, 不会陷入无意义的循环。
            if (img.PixelWidth >= requestedWidth)
            {
                var dpiNow = VisualTreeHelper.GetDpi(border).DpiScaleX;
                if (dpiNow <= 0) dpiNow = 1.0;
                if (NeedsRedecode(border.ActualWidth * dpiNow, img.PixelWidth))
                    _ = LoadAsync(border, url);
            }
        }

        Apply();
    }

    /// <summary>纯函数(探针可断言): 这一次上屏要不要播淡入 —— 只有"这一格还没有图"时才播。</summary>
    public static bool ShouldPlayFadeIn(int previouslyDecodedWidth) => previouslyDecodedWidth <= 0;

    // ★★★ 这里原来有一套"状态过渡期间封面只记账不上屏"的闸门
    //   (SuspendVisualUpdates / ResumeVisualUpdates / _suspendDepth / DeferredApplies /
    //    _suspendGuard, 2s 自救超时), 2026-10-02 **整体删除**。
    //
    // 删除原因: 它存在的唯一理由是保护最大化/还原那层**自绘**动画 —— 动画期间窗口根元素挂着
    //   BitmapCache(把整棵树栅格化成一张 GPU 纹理), 而异步解码回来贴图那一下会让纹理失效、
    //   整个窗口重栅格化 ⇒ 用户看到的"一抽一抽"。现在动画交给 Windows/DWM 播, 我们**不挂任何
    //   纹理**, 也就没有"贴图必须让路"的约束, 封面解码完直接上屏即可。
    //   ★ 别因为它"看起来只是个优化"就删了又留一半: 那套闸门一旦只留 Suspend 不留 Resume,
    //     封面会永远不上屏(卡片全空)。既然自绘动画已不存在, 就一并不留。

    // ------------------------------------------------------------ Image 版本

    /// <summary>
    /// 等 Border 量出真实宽度(容器刚生成、还没走过布局时用)。
    /// 返回 <c>null</c> 表示"不用等" —— 已经量出来了; 元素被卸载时也会放行(调用方自己判源是否已变)。
    ///
    /// ★★★ **必须有超时**(2026-11-07 修"排行榜在小窗口下部分封面永久灰"):
    ///   原来这里只等 SizeChanged, 没有兜底。而 SizeChanged 只在**尺寸真的变了**时才发 ——
    ///   卡片被创建在一个宽度已定、但还没参与布局的容器里时(折叠的 tab / 重排的中间态),
    ///   它可能**一直**收不到这个事件。于是:
    ///     `LoadAsync` 卡在 await 上 → `Loading` 永远为真 → `Restore` 被永久拦住
    ///     → 这张封面**再也不会被贴上去**, 除非用户改变窗口尺寸把它重新布局一次。
    ///   这正好对上用户报的现象:"小窗口下灰一片, 把窗口调大就好了"。
    ///   ⇒ 超时后不再等, 直接按"拿不到宽度"的兜底值解码 —— 宁可这张图多占一点内存,
    ///     也不能让它永远空着。之后尺寸真的变了, SizeChanged 照常会触发重解码。
    /// </summary>
    private const int RealWidthTimeoutMs = 400;

    private static Task? WaitForRealWidth(Border border)
    {
        if (border.ActualWidth > 0) return null;

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        SizeChangedEventHandler? onSize = null;
        RoutedEventHandler? onUnload = null;
        Timer? timeout = null;
        var done = 0;

        void Finish()
        {
            // 只跑一次: SizeChanged / Unloaded / 超时三选一
            if (Interlocked.Exchange(ref done, 1) != 0) return;
            border.SizeChanged -= onSize!;
            border.Unloaded -= onUnload!;
            timeout?.Dispose();
            tcs.TrySetResult(null);
        }

        onSize = (_, e) =>
        {
            if (e.NewSize.Width <= 0) return;
            Finish();
        };
        onUnload = (_, _) => Finish();

        border.SizeChanged += onSize;
        border.Unloaded += onUnload;
        // ★ 兜底: 等不到尺寸也必须放行(见上面的说明)。用 Timer 而不是 Task.Delay,
        //   是为了在 SizeChanged 先到的时候能把定时器直接 Dispose 掉。
        timeout = new Timer(_ =>
        {
            try { border.Dispatcher.BeginInvoke(new Action(Finish)); }
            catch { /* 元素已关闭 */ }
        }, null, RealWidthTimeoutMs, Timeout.Infinite);

        return tcs.Task;
    }

    /// <summary>
    /// 附加属性 Cover.ImageSource: 给 **Image 元素**用的异步图片来源。
    ///
    /// 为什么不直接把 URL 绑到 Image.Source: WPF 会用内置的 ImageSourceConverter 去**同步**
    /// 下载并解码, 每张图都会把 UI 线程卡住一下 —— 这正是"点开消息页要顿一下"的成因。
    /// 走这里就和封面一样: 后台下载 + 后台解码 + 冻结后再交给 UI。
    ///
    /// 与 Cover.Source 的区别: 这个用 Stretch=Uniform, 保持原始宽高比(私信里的图片不裁剪)。
    /// </summary>
    public static readonly DependencyProperty ImageSourceProperty = DependencyProperty.RegisterAttached(
        "ImageSource", typeof(string), typeof(Cover),
        new PropertyMetadata(null, OnImageSourceChanged));

    public static string? GetImageSource(DependencyObject d) => (string?)d.GetValue(ImageSourceProperty);
    public static void SetImageSource(DependencyObject d, string? value) => d.SetValue(ImageSourceProperty, value);

    private static void OnImageSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image) return;
        var url = e.NewValue as string;
        if (string.IsNullOrWhiteSpace(url))
        {
            image.Source = null;
            return;
        }
        _ = LoadIntoImageAsync(image, url);
    }

    private static async Task LoadIntoImageAsync(Image image, string url)
    {
        // 私信图片也就是聊天气泡那点尺寸, 512 宽解码足够, 比原图省一大截内存
        var bmp = await CoverLoader.LoadAsync(url, 512);
        if (bmp == null) return;

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (GetImageSource(image) != url) return; // 已换源, 放弃旧结果
            image.Source = bmp;
        });
    }
}