using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
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

    public static string Normalize(string? url) => UrlUtil.Normalize(url);

    /// <summary>加载图片。decodeWidth>0 时按目标像素宽解码(大幅降低内存),
    /// 0 表示按原图解码。不同档位的结果分别缓存</summary>
    public static Task<BitmapImage?> LoadAsync(string? url, int decodeWidth = 0)
    {
        url = Normalize(url);
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

        var task = LoadCoreAsync(url, cacheKey, w);
        lock (Lock)
        {
            // 极端情况下两个线程同时走到这里, 后者会覆盖前者 —— 两者结果等价(都会写同一个缓存键),
            // 唯一的代价是可能多下载一次, 不影响正确性。
            InFlight[cacheKey] = task;
        }
        return task;
    }

    private static async Task<BitmapImage?> LoadCoreAsync(string url, string cacheKey, int decodeWidth)
    {
        try
        {
            var cacheFile = Path.Combine(AppPaths.CacheDir, Hashing.Md5Hex(url) + ".jpg");
            try
            {
                if (!File.Exists(cacheFile))
                {
                    var bytes = await Http.GetByteArrayAsync(url);
                    await File.WriteAllBytesAsync(cacheFile, bytes);
                }
            }
            catch
            {
                if (!File.Exists(cacheFile)) return null; // 下载失败且无缓存
            }

            // 解码放后台线程: 一张 1280 宽的 JPEG 解码要几毫秒到几十毫秒,
            // 放在 UI 线程上会直接表现为滚动/切页的卡顿。
            var img = await Task.Run(() => FromFile(cacheFile, decodeWidth));
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
    /// </summary>
    private static void TrimLocked()
    {
        if (_cachedBytes <= MaxCacheBytes && MemCache.Count <= MaxCacheCount) return;

        var targetBytes = MaxCacheBytes * 8 / 10;
        var targetCount = MaxCacheCount * 8 / 10;

        foreach (var kv in MemCache.OrderBy(kv => kv.Value.LastUse))
        {
            if (_cachedBytes <= targetBytes && MemCache.Count <= targetCount) break;
            _cachedBytes -= kv.Value.Bytes;
            MemCache.Remove(kv.Key);
        }
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
            bi.StreamSource = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            bi.EndInit();
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

    /// <summary>显示需求(物理像素)超过已解码宽度这么多倍才重解码 —— 量化档位本身有 0~40% 富余,
    /// 阈值太小会在档位内反复重解码。</summary>
    public const double RedecodeFactor = 1.12;

    /// <summary>纯函数(探针可断言): 显示物理需求是否已经明显超过已解码宽度。</summary>
    public static bool NeedsRedecode(double needPx, int decodedPx)
        => decodedPx > 0 && needPx > decodedPx * RedecodeFactor;

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border) return;
        border.Opacity = 0.0;
        border.SetValue(DecodedPixelWidthProperty, 0);
        var url = e.NewValue as string;

        // 尺寸变化监听: 变宽超过已解码宽度的一定比例就重解码。先 - 再 + 防同元素换源时重复订阅;
        // 处理器挂在静态类上、目标随卡片销毁, 无泄漏面。
        border.SizeChanged -= OnCoverSizeChanged;
        if (string.IsNullOrWhiteSpace(url)) return;
        border.SizeChanged += OnCoverSizeChanged;

        _ = LoadAsync(border, url);
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
        var img = await CoverLoader.LoadAsync(url, w);
        if (img == null) return;

        await Application.Current.Dispatcher.InvokeAsync(
            () => ApplyDecoded(border, url, img, w));
    }

    /// <summary>
    /// 把解好的位图贴到卡片上(**必须在 UI 线程**)。闸门关着时只排队不上屏, 见 <see cref="_suspendDepth"/>。
    /// </summary>
    private static void ApplyDecoded(Border border, string url, BitmapImage img, int requestedWidth)
    {
        void Apply()
        {
            if (GetSource(border) != url) return; // 排队期间这一格被回收/换了源

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
            border.Background = brush;
            // 图片加载成功后隐藏占位符(如头像里的人形图标), 否则会一直叠在图片上
            if (border.Child != null) border.Child.Visibility = Visibility.Collapsed;
            border.SetValue(DecodedPixelWidthProperty, img.PixelWidth);

            // ★★ 只有"这一格第一次出图"才淡入。变宽触发的重解码时卡片上**已经有图**了,
            //   再播一次 0→1 的淡入 = 封面先整块消失再浮出来; 最大化那一下视口内几十张同时重解码,
            //   几十张一起闪, 用户看到的就是"抽动"(2026-10-01 定位)。已经有图就静默换掉 ——
            //   底下换的是更清晰的位图, 画面不该有任何跳变。
            if (ShouldPlayFadeIn(prev))
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

        if (_suspendDepth > 0) DeferredApplies.Add(Apply);
        else Apply();
    }

    /// <summary>纯函数(探针可断言): 这一次上屏要不要播淡入 —— 只有"这一格还没有图"时才播。</summary>
    public static bool ShouldPlayFadeIn(int previouslyDecodedWidth) => previouslyDecodedWidth <= 0;

    // ------------------------------------------------------------ 状态过渡期间: 只记账不上屏

    /// <summary>
    /// 关闸深度(=0 表示开闸)。用深度而不是 bool: 连点最大化时新旧动画会重叠, 各关一次各开一次。
    /// </summary>
    private static int _suspendDepth;

    /// <summary>关闸期间欠下的"贴图"动作, 开闸后按顺序补跑。</summary>
    private static readonly List<Action> DeferredApplies = new();

    /// <summary>
    /// 关闸最长时间。过渡动画只有 200ms, 这里给 2s; 万一动画的 Completed 没回来
    /// (时钟被丢弃/窗口异常关闭), 也必须自动开闸 —— 否则封面会永远不再上屏, 卡片全空。
    /// </summary>
    private static readonly TimeSpan SuspendGuardInterval = TimeSpan.FromSeconds(2);

    private static System.Windows.Threading.DispatcherTimer? _suspendGuard;

    /// <summary>
    /// 关闸: 最大化/还原的过渡动画开始前调用。
    ///
    /// ★★ 为什么必须有它(2026-10-01, "界面抽动"的根因): 过渡动画期间窗口根元素挂着
    ///   `BitmapCache`(整窗栅格化成**一张** GPU 纹理), 而**任何子树变化都会让这张纹理失效
    ///   并重栅格化整个窗口**。卡片墙自己的宽度收敛已经被 `CardWallPanel._held` 冻住了, 但
    ///   封面这条链是**异步**的: 卡片一变宽就触发"按新宽度重新解码"(见 NeedsRedecode),
    ///   解码完成后再回到 UI 线程贴图 —— 这一下正好落在动画中间 ⇒ 动画每帧都在重栅格化整窗,
    ///   观感就是"一抽一抽的"。之前那层模糊遮罩只是**盖住**症状(已按用户要求回退), 这里才治根:
    ///   闸门关着时把"贴图"记在 <see cref="DeferredApplies"/> 里, 动画收尾(CacheMode 已摘掉)
    ///   再一次性补上 —— 那时换图不再经过纹理, 谁也看不见。
    /// </summary>
    public static void SuspendVisualUpdates()
    {
        if (!OnUiThread()) { Application.Current?.Dispatcher.Invoke(SuspendVisualUpdates); return; }

        _suspendDepth++;
        if (_suspendGuard == null)
        {
            _suspendGuard = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background)
            {
                Interval = SuspendGuardInterval
            };
            _suspendGuard.Tick += (_, _) =>
            {
                _suspendGuard!.Stop();
                if (_suspendDepth == 0) return;
                // 自救: 动画收尾没回来也必须开闸(宁可画面闪一下, 也不能让封面永远不上屏)
                _suspendDepth = 0;
                FlushDeferred();
            };
        }
        _suspendGuard.Stop();
        _suspendGuard.Start();
    }

    /// <summary>开闸: 过渡动画收尾后调用; 把欠下的贴图一次性补跑。</summary>
    public static void ResumeVisualUpdates()
    {
        if (!OnUiThread()) { Application.Current?.Dispatcher.Invoke(ResumeVisualUpdates); return; }

        if (_suspendDepth > 0) _suspendDepth--;
        if (_suspendDepth > 0) return;
        _suspendGuard?.Stop();
        FlushDeferred();
    }

    private static void FlushDeferred()
    {
        if (DeferredApplies.Count == 0) return;
        // 先取快照再清空: 补跑过程中可能又排进新的(补跑会触发"再解码一轮"), 那些走正常路径
        var pending = DeferredApplies.ToArray();
        DeferredApplies.Clear();
        foreach (var apply in pending)
        {
            try { apply(); }
            catch (Exception ex) { App.ReportError(ex); }
        }
    }

    private static bool OnUiThread()
    {
        var app = Application.Current;
        if (app == null) return true;   // 探针/无 Application: 当作就在当前线程
        return app.Dispatcher.CheckAccess();
    }

    // ------------------------------------------------------------ Image 版本

    /// <summary>
    /// 等 Border 量出真实宽度(容器刚生成、还没走过布局时用)。
    /// 返回 <c>null</c> 表示"不用等" —— 已经量出来了; 元素被卸载时也会放行(调用方自己判源是否已变),
    /// 免得留下一个永远等不到的挂起任务。
    /// </summary>
    private static Task? WaitForRealWidth(Border border)
    {
        if (border.ActualWidth > 0) return null;

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        SizeChangedEventHandler? onSize = null;
        RoutedEventHandler? onUnload = null;
        onSize = (_, e) =>
        {
            if (e.NewSize.Width <= 0) return;
            border.SizeChanged -= onSize!;
            border.Unloaded -= onUnload!;
            tcs.TrySetResult(null);
        };
        onUnload = (_, _) =>
        {
            border.SizeChanged -= onSize!;
            border.Unloaded -= onUnload!;
            tcs.TrySetResult(null);
        };
        border.SizeChanged += onSize;
        border.Unloaded += onUnload;
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