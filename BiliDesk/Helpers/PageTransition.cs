using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace BiliDesk.Helpers;

/// <summary>
/// 切页过渡: **旧页淡出 + 轻微上浮, 新页直接露出** —— 观感对齐 Windows 11 Fluent 的页面切换。
///
/// ★★★ 2026-11-07 第三次重做, 这一版同时解决两件事:
///
///   ① "切回首页空白一秒" —— 根因不在过渡, 而是**页面被摘出可视树**:
///      首页是零虚拟化卡片墙, 重新挂回时要把几百上千张卡重新测量/排布。
///      探针实测: 430 张卡 89ms、830 张 176ms、1530 张 278ms, 慢机器上就是白屏一秒。
///      现在页面常驻 MainWindow 的 PageLayer(只切 Visibility.Collapsed), 布局状态被保留:
///      830 张卡 Collapsed→Visible 的 UpdateLayout 只要 11ms。
///      而且切页时目标页先在**旧页遮挡下**完成布局, 用户永远看不到白底。
///
///   ② Fluent 观感 + 不闪白 —— 只动**旧页**, 新页全程不透明:
///      如果新旧两页同时淡入淡出, 中间那一拍两层都半透明, 底下的窗口底色会透出来
///      (材质模式下是壁纸, 纯色模式下是一片浅色) —— 那就是"闪白"。
///      所以这里给旧页做 Win11 风格的"淡出 + 上浮"(1→0, 0→-10px);
///      新页始终以不透明度 1 待在下面, 旧页移开多少就露出多少 —— **没有一帧是空的**。
///
/// ★ 为什么新页不做缩放/位移动画: 整页 ScaleTransform 会让整棵卡片墙每帧重新栅格化,
///   实测 p95 从 ~14ms 涨到 ~28ms(离屏探针)。淡出+上浮只需要合成旧页这一层, 明显更稳。
///
/// ★ 连点导航项: Run 会作废旧回调(epoch); 调用方在开始新过渡前先 <see cref="FinishNow"/>
///   把上一段立即收尾, 保证任何时候最多只有一层在动、状态不会错。
/// </summary>
public sealed class PageTransition
{
    /// <summary>旧页淡出时长(ms)。</summary>
    public const int FadeOutMs = 200;

    /// <summary>旧页上浮时长(ms)。过渡以这条结束。</summary>
    public const int MoveMs = 220;

    /// <summary>旧页上浮距离(DIP)。它移开后露出的是**新页**, 不会露出窗口底色。</summary>
    private const double LiftDistance = 10;

    private int _epoch;
    private UIElement? _outgoing;
    private UIElement? _incoming;
    private TranslateTransform? _outTransform;
    private Action? _completed;
    private DispatcherTimer? _safety;

    /// <summary>旧页在动画期间的原始 CacheMode(动画结束、页面收起后还原; 见 Run 里的说明)。</summary>
    private CacheMode? _outPrevCache;
    private bool _outCacheChanged;

    /// <summary>是否有过渡正在跑。</summary>
    public bool InFlight { get; private set; }

    /// <summary>
    /// 播一次过渡。outgoing 必须在上层(ZIndex 更高), incoming 在下面且**已经完成布局**。
    /// completed 在动画收尾后调用(调用方在这里收起旧页 / 更新当前页记账)。
    /// </summary>
    public void Run(UIElement outgoing, UIElement incoming, Action completed)
    {
        var epoch = ++_epoch;
        InFlight = true;
        _outgoing = outgoing;
        _incoming = incoming;
        _completed = completed;

        // 新页从头到尾不透明 —— 它不做任何动画, 只负责被旧页"揭开"。
        incoming.Opacity = 1;
        outgoing.Opacity = 1;

        // ★ 旧页在动画期间挂一层 BitmapCache: 把整页栅格化成一张纹理, 之后 220ms 里
        //   "透明度 + 上浮"只是在纹理上做合成, 不再每帧重走几百张卡片的可视树。
        //   不加的话实测 p95 会从 ~14ms 掉到 ~28ms, 位移动画看起来一顿一顿。
        //   纹理只在过渡期间存在; 收尾时页面已经 Collapsed, 还原 CacheMode 不会触发重绘。
        _outPrevCache = outgoing.CacheMode;
        _outCacheChanged = false;
        if (outgoing.CacheMode == null)
        {
            try
            {
                outgoing.CacheMode = new BitmapCache();
                _outCacheChanged = true;
            }
            catch
            {
                // 极端情况下(例如元素里有 HwndHost)不支持缓存, 退回普通渲染即可
            }
        }

        _outTransform = EnsureTranslate(outgoing);
        _outTransform.Y = 0;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        outgoing.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(FadeOutMs)) { EasingFunction = ease });

        // 收尾挂在**上浮**这条(220ms, 比淡出长 20ms): 若挂在淡出的 Completed 上,
        // 上浮还差 20ms 就会被硬清成 0, 看起来"顿一下"。
        var lift = new DoubleAnimation(0, -LiftDistance, TimeSpan.FromMilliseconds(MoveMs))
        {
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
        };
        lift.Completed += (_, _) =>
        {
            if (epoch != _epoch) return;   // 已被更晚的过渡取代
            Complete();
        };
        _outTransform.BeginAnimation(TranslateTransform.YProperty, lift);

        // 兜底: 窗口最小化/渲染被挂起时 Completed 可能不来。目标页在那之前一直是
        // IsHitTestVisible=false, 卡住就等于整页点不动 —— 必须让它准时落定。
        StopSafety();
        _safety = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(MoveMs + 600) };
        _safety.Tick += (_, _) =>
        {
            if (epoch == _epoch) Complete();
        };
        _safety.Start();
    }

    private void StopSafety()
    {
        _safety?.Stop();
        _safety = null;
    }

    /// <summary>
    /// 立即结束当前过渡(连点导航时用), 并把最终状态落定:
    /// incoming 完全不透明; outgoing 透明度 0(调用方随后把它 Collapsed)。
    /// </summary>
    public void FinishNow()
    {
        if (!InFlight) return;
        _epoch++;
        Complete();
    }

    /// <summary>
    /// 不播动画、清掉可能挂着的过渡(首次显示窗口 / 强制跳页时用)。
    /// 不会调用 completed —— 调用方自己设置"当前页"。
    /// </summary>
    public void Cancel()
    {
        _epoch++;
        InFlight = false;
        StopSafety();
        var o = _outgoing;
        var i = _incoming;
        var ot = _outTransform;
        var prevCache = _outPrevCache;
        var cacheChanged = _outCacheChanged;
        _outgoing = _incoming = null;
        _outTransform = null;
        _completed = null;
        _outPrevCache = null;
        _outCacheChanged = false;

        Reset(i, null, 1);
        Reset(o, ot, 1);
        if (cacheChanged) RestoreCache(o, prevCache);
    }

    private void Complete()
    {
        if (!InFlight) return;
        StopSafety();

        var o = _outgoing;
        var i = _incoming;
        var ot = _outTransform;
        var cb = _completed;
        var prevCache = _outPrevCache;
        var cacheChanged = _outCacheChanged;

        _outgoing = _incoming = null;
        _outTransform = null;
        _completed = null;
        _outPrevCache = null;
        _outCacheChanged = false;
        InFlight = false;

        Reset(i, null, 1);    // 新页: 完全不透明
        Reset(o, ot, 0);      // 旧页: 透明(调用方随后 Collapsed)

        cb?.Invoke();

        // ★ 必须在 cb 把旧页 Collapsed **之后**再还原 CacheMode:
        //   页面已经不可见, 撤掉纹理缓存不会触发一次可见的重新栅格化。
        if (cacheChanged) RestoreCache(o, prevCache);
    }

    private static void RestoreCache(UIElement? element, CacheMode? cache)
    {
        if (element == null) return;
        try { element.CacheMode = cache; } catch { /* 窗口可能已经关闭 */ }
    }

    private static void Reset(UIElement? element, TranslateTransform? translate, double opacity)
    {
        if (element == null) return;
        try
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = opacity;

            if (translate != null)
            {
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                translate.Y = 0;
            }
        }
        catch
        {
            // 窗口关闭途中可能已经断开, 不影响功能
        }
    }

    private static TranslateTransform EnsureTranslate(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform existing) return existing;
        var transform = new TranslateTransform();
        element.RenderTransform = transform;
        // 翻译与缩放锚点无关; 复位一下, 防止上一次(若做过缩放)的 0.5/0.5 留在页面上。
        element.RenderTransformOrigin = new Point(0, 0);
        return transform;
    }
}


