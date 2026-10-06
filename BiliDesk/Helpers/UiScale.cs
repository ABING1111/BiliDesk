using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace BiliDesk.Helpers;

/// <summary>
/// 全局「软件缩放」(2026-10-06 新增): 把整个界面按百分比放大/缩小, 与系统 DPI 无关。
///
/// ## 为什么用 LayoutTransform 而不是 RenderTransform
/// 两者语义完全不同, 这里要的是**布局也一起缩**(否则字放大了、行高没放, 全部叠在一起):
///   · <see cref="UIElement.LayoutTransform"/> —— **参与测量**: 根元素先按 1/scale 的逻辑尺寸排版,
///     再整体缩放到实际尺寸。效果等同浏览器 Ctrl+加号: 字与控件一起变大, 可见内容变少。
///   · RenderTransform —— 只影响绘制, 不影响测量 ⇒ 放大后控件互相重叠, 是错的。
///
/// ## 播放器只缩右侧信息栏(2026-10-06)
/// 视频区**不能**和周围 UI 一起缩 —— 但原因**不是**"原生 HWND 不受 WPF 变换影响"(这条实测是错的:
/// 视频子 HWND 其实是跟着 LayoutTransform 走的, 偏差 0px)。真正的原因在 LibVLCSharp.WPF:
/// 它把 `VideoView.Content`(控制栏 + 弹幕层)搬进一棵**独立的顶层浮层窗口**, 而该库 3.8.0 的
/// `ForegroundWindow` 是按**局部坐标** `ActualWidth/Height` 定尺寸的, 不认祖先的缩放 ——
/// 实测整窗缩到 125% 时那层浮层会小掉整整一个 1/1.25, 与视频区错开 195×186px(控制栏会跑偏)。
/// 该库 3.8.1 才补上"识别缩放并同步放大浮层内容"(`AlignWithBackground`/`ScaleWindowContent`)。
/// ⇒ 所以 <see cref="Views.PlayerWindow"/> 覆写 ScaleRoot 只返回右侧信息栏: 那一列里是
///   标题/UP 主卡/操作栏/简介与评论 Tab/评论列表, 以及盖在它上面的合集面板与浮层 Toast。
///   证据: `.probes/bd-probe-uiscale-player`(100%↔125% 逐项比对, 见该探针的判据)。
///
/// ## 为什么要"注册"而不是每次遍历所有窗口
/// 窗口会被反复创建/销毁(播放器、图片预览、各种对话框), 靠遍历拿不到"刚创建还没显示"的窗口,
/// 也容易漏。改成: 每个窗口在自己的 SourceInitialized/Loaded 里 <see cref="Register"/> 一次,
/// 集合用**弱引用**保存(窗口关掉后不该被这里钉住)。
/// </summary>
public static class UiScale
{
    /// <summary>可调范围(百分比)。设置页滑杆与存储层夹取都引用这一处。</summary>
    public const int MinPercent = 80;
    /// <inheritdoc cref="MinPercent"/>
    public const int MaxPercent = 150;

    /// <summary>当前缩放倍率(1.0 = 100%)</summary>
    public static double Factor { get; private set; } = 1.0;

    /// <summary>当前缩放百分比(便于设置页直接绑定)</summary>
    public static int Percent => (int)Math.Round(Factor * 100);

    /// <summary>已注册的根元素(弱引用: 窗口关掉后不该被这里钉住)</summary>
    private static readonly List<WeakReference<FrameworkElement>> _roots = new();

    /// <summary>把百分比夹到合法范围</summary>
    public static int ClampPercent(int percent) => Math.Clamp(percent, MinPercent, MaxPercent);

    /// <summary>设置缩放(按百分比)。越界会被夹到 <see cref="MinPercent"/>~<see cref="MaxPercent"/>。</summary>
    public static void SetPercent(int percent)
    {
        var f = ClampPercent(percent) / 100.0;
        if (Math.Abs(Factor - f) < 0.0001) return;
        Factor = f;
        ApplyAll();
    }

    /// <summary>把一个窗口的**根内容**登记进来(重复登记同一元素会被忽略)</summary>
    public static void Register(FrameworkElement? root)
    {
        if (root == null) return;
        lock (_roots)
        {
            _roots.RemoveAll(w => !w.TryGetTarget(out _));
            if (_roots.Any(w => w.TryGetTarget(out var t) && ReferenceEquals(t, root))) return;
            _roots.Add(new WeakReference<FrameworkElement>(root));
        }
        Apply(root);
    }

    /// <summary>取消登记(窗口关闭时调; 不调也只是留个死弱引用, 会被下次清理掉)</summary>
    public static void Unregister(FrameworkElement? root)
    {
        if (root == null) return;
        lock (_roots)
        {
            _roots.RemoveAll(w => !w.TryGetTarget(out var t) || ReferenceEquals(t, root));
        }
    }

    /// <summary>对已登记的所有根重新施加当前倍率(改设置时调)</summary>
    public static void ApplyAll()
    {
        List<FrameworkElement> alive;
        lock (_roots)
        {
            _roots.RemoveAll(w => !w.TryGetTarget(out _));
            alive = _roots.Select(w => w.TryGetTarget(out var t) ? t : null)
                          .Where(t => t != null).Select(t => t!).ToList();
        }
        foreach (var r in alive) Apply(r);
    }

    /// <summary>
    /// 对一个根元素施加当前倍率。
    ///
    /// ★ 100% 时**必须把 LayoutTransform 清成 null**(而不是设一个 1.0 的 ScaleTransform):
    ///   留着一个恒等变换会让 WPF 每次布局都多跑一遍变换管线, 而且某些控件(如 ScrollViewer)
    ///   会因"存在变换"走不同的测量分支。清掉才是"和没开过这个功能完全一样"。
    /// </summary>
    private static void Apply(FrameworkElement root)
    {
        try
        {
            if (Math.Abs(Factor - 1.0) < 0.0001)
            {
                if (root.LayoutTransform != null) root.LayoutTransform = null;
                return;
            }
            // 复用同一个 ScaleTransform 实例没有意义(不同根要各自持有), 但值相等就别重建,
            // 免得每次 ApplyAll 都给整棵树标一次"需要重新测量"。
            if (root.LayoutTransform is ScaleTransform st &&
                Math.Abs(st.ScaleX - Factor) < 0.0001 &&
                Math.Abs(st.ScaleY - Factor) < 0.0001) return;

            root.LayoutTransform = new ScaleTransform(Factor, Factor);
        }
        catch
        {
            // 个别元素不接受变换(例如还没初始化完)就跳过, 不该让整个缩放流程失败
        }
    }
}
