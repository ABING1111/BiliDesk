using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace BiliDesk.Helpers;

/// <summary>
/// 全局「中键自动滚动」(浏览器同款手感): 在任何可滚动区域点按/按住鼠标中键, 出现方向锚点,
/// 鼠标偏离锚点越远滚得越快, 可同时管横向; 再点一次中键、按其他键、滚轮、失去捕获都退出。
///
/// 挂载: App 启动时调一次 <see cref="Install"/>。注册的是 ScrollViewer 的**类处理器**,
/// 对全应用所有 ScrollViewer 生效 —— 包括 ListBox/ItemsControl 模板里那些以及动态创建的,
/// 不需要每个页面单独开开关(对比 <see cref="SmoothScroll"/>, 那个是逐处 opt-in 的)。
///
/// 手感对齐 Chrome(踩过的坑):
///   · 滚动必须挂在 CompositionTarget.Rendering 上按帧走、速度按真实 dt 计算 ——
///     最早用 15ms 的 DispatcherTimer, 不与渲染同步, 而且每 tick 跳一段常数距离, 实测卡顿;
///   · 速度 = 偏离死区的像素 × 系数, 帧间用小数偏移累计(ScrollViewer 偏移本身是 double,
///     内容亚像素渲染), 不做取整 —— 取整就是"一格一格"的来源;
///   · 光标保持可见、锚点圆标留在按下位置(不学浏览器把光标藏掉: 光标指哪儿一目了然,
///     和圆标的距离好控速 —— 试过隐藏, 反馈"看不到光标在哪");
///   · 箭头各画在各的方向位(上下左右) —— 初版把上下箭头画在了圆的左右两侧, 观感很怪。
///
/// 交互细节对齐 Chrome:
///   · 按住拖动 → 松开即退出; 点一下(没怎么动)就松开 → 进入"锁定模式", 鼠标随便走, 再点一次中键退出;
///   · 中键以外的键按下立即退出(不吞事件, 该给谁的给谁);
///   · WebView2/播放器视频面是 HwndHost, 事件到不了 WPF, 天然不受影响。
/// </summary>
public static class MiddleClickAutoscroll
{
    private static bool _installed;

    /// <summary>锚点死区: 这么近(像素)不动, 离开死区才开始滚</summary>
    private const double DeadZone = 10;

    /// <summary>速度系数: 偏离死区 1px = 每秒滚 8px。偏 100px → 800px/s, 偏 300px → 2400px/s。
    /// ★ 这是全应用默认值, 嫌快嫌慢只动这里。</summary>
    private const double PixelsPerSecondPerPixel = 8;

    /// <summary>判定"按住拖动"vs"点一下"的位移阈值</summary>
    private const double DragThreshold = 6;

    private static ScrollViewer? _active;
    private static Point _anchor;                 // 相对 _active 的锚点
    private static bool _movedBeyondClick;        // 按住拖动了(区别于"点一下进入锁定模式")
    private static AutoscrollGlyph? _glyph;
    private static double _targetV, _targetH;     // 小数累计的滚动目标(亚像素平滑的关键)
    private static long _lastTimestamp;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(ScrollViewer), Mouse.PreviewMouseDownEvent,
            new MouseButtonEventHandler(OnPreviewMouseDown));
        EventManager.RegisterClassHandler(typeof(ScrollViewer), Mouse.PreviewMouseUpEvent,
            new MouseButtonEventHandler(OnPreviewMouseUp));
        EventManager.RegisterClassHandler(typeof(ScrollViewer), Mouse.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewWheel));
        EventManager.RegisterClassHandler(typeof(ScrollViewer), Keyboard.PreviewKeyDownEvent,
            new KeyEventHandler(OnPreviewKeyDown));
    }

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_active != null)
        {
            // 已在自动滚动中: 中键=退出(吞掉, 别又开始一轮/点到东西); 其他键=退出且不吞
            if (e.ChangedButton == MouseButton.Middle) e.Handled = true;
            End();
            return;
        }
        if (e.ChangedButton != MouseButton.Middle) return;

        var sv = (ScrollViewer)sender;
        if (sv.ScrollableHeight <= 0 && sv.ScrollableWidth <= 0) return;

        _active = sv;
        _anchor = e.GetPosition(sv);
        _movedBeyondClick = false;
        _targetV = sv.VerticalOffset;
        _targetH = sv.HorizontalOffset;
        _lastTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        // 捕获后鼠标事件都路由到 ScrollViewer, 移出窗口也不会丢。
        // 光标保持可见(不学浏览器隐藏): 光标指哪儿一目了然, 和圆标一起看距离好控速
        sv.CaptureMouse();
        sv.MouseMove += OnMouseMove;
        sv.LostMouseCapture += OnLostCapture;
        CompositionTarget.Rendering += OnRendering;

        _glyph = new AutoscrollGlyph(sv, _anchor,
            upDown: sv.ScrollableHeight > 0, leftRight: sv.ScrollableWidth > 0);
        AdornerLayer.GetAdornerLayer(sv)?.Add(_glyph);
    }

    private static void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_active == null || e.ChangedButton != MouseButton.Middle) return;
        // 按住拖动的松手 → 退出; 点一下就松 → 进入锁定模式(继续跟鼠标)
        if (_movedBeyondClick) End();
    }

    private static void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_movedBeyondClick || _active == null) return;
        var p = e.GetPosition(_active);
        if (Math.Abs(p.X - _anchor.X) > DragThreshold || Math.Abs(p.Y - _anchor.Y) > DragThreshold)
            _movedBeyondClick = true;
    }

    private static void OnLostCapture(object? sender, MouseEventArgs e) => End();

    private static void OnPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        // 自动滚动中来了滚轮: 退出模式, 滚轮事件照常走(平滑滚动等既有逻辑不受影响)
        if (_active != null) End();
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_active != null && e.Key == Key.Escape) End();
    }

    /// <summary>每渲染帧驱动一次: 速度只由"此刻偏离锚点多少"决定, 距离 × dt = 本帧位移。</summary>
    private static void OnRendering(object? sender, EventArgs e)
    {
        if (_active is not ScrollViewer sv || !sv.IsVisible || Mouse.Captured != sv)
        {
            End();
            return;
        }

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var dt = (now - _lastTimestamp) / (double)System.Diagnostics.Stopwatch.Frequency;
        _lastTimestamp = now;
        if (dt <= 0 || dt > 0.5) return;   // 首帧/调试断点暂停等异常间隔, 别拿来瞬移

        var p = Mouse.GetPosition(sv);
        var dy = p.Y - _anchor.Y;
        var dx = p.X - _anchor.X;

        if (Math.Abs(dy) > DeadZone && sv.ScrollableHeight > 0)
        {
            _targetV += (dy - DeadZone * Math.Sign(dy)) * PixelsPerSecondPerPixel * dt;
            _targetV = Math.Clamp(_targetV, 0, sv.ScrollableHeight);
            sv.ScrollToVerticalOffset(_targetV);
        }
        else _targetV = sv.VerticalOffset;
        if (Math.Abs(dx) > DeadZone && sv.ScrollableWidth > 0)
        {
            _targetH += (dx - DeadZone * Math.Sign(dx)) * PixelsPerSecondPerPixel * dt;
            _targetH = Math.Clamp(_targetH, 0, sv.ScrollableWidth);
            sv.ScrollToHorizontalOffset(_targetH);
        }
        else _targetH = sv.HorizontalOffset;
    }

    private static void End()
    {
        CompositionTarget.Rendering -= OnRendering;
        if (_glyph != null)
        {
            var layer = AdornerLayer.GetAdornerLayer(_active ?? _glyph.AdornedElement);
            layer?.Remove(_glyph);
            _glyph = null;
        }
        if (_active != null)
        {
            _active.MouseMove -= OnMouseMove;
            _active.LostMouseCapture -= OnLostCapture;
            _active.ReleaseMouseCapture();
            _active = null;
        }
    }

    /// <summary>锚点指示器: 半透明圆 + 各可滚方向的三角, 画在 ScrollViewer 的装饰层上。
    /// 观感对齐浏览器: 白圆灰边, 箭头各在各的方向位(上↑下↓左←右→)。</summary>
    private sealed class AutoscrollGlyph : Adorner
    {
        private readonly bool _upDown;
        private readonly bool _leftRight;
        private readonly Point _at;
        private static readonly Brush CircleFill = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255));
        private static readonly Pen CircleStroke = new(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 1);
        private static readonly Brush ArrowFill = new SolidColorBrush(Color.FromRgb(0x5F, 0x5F, 0x5F));

        static AutoscrollGlyph()
        {
            CircleFill.Freeze();
            CircleStroke.Freeze();
            ArrowFill.Freeze();
        }

        public AutoscrollGlyph(ScrollViewer adorned, Point at, bool upDown, bool leftRight)
            : base(adorned)
        {
            _at = at;
            _upDown = upDown;
            _leftRight = leftRight;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext dc)
        {
            const double r = 15;
            dc.PushTransform(new TranslateTransform(_at.X, _at.Y));
            dc.DrawEllipse(CircleFill, CircleStroke, default, r, r);

            // 三角中心距圆心 9.5px, 尖端 14 恰收在圆内; 上下箭头在上/下, 左右箭头在左/右
            if (_upDown)
            {
                dc.DrawGeometry(ArrowFill, null, TriangleUpDown(0, -9.5, up: true));
                dc.DrawGeometry(ArrowFill, null, TriangleUpDown(0, 9.5, up: false));
            }
            if (_leftRight)
            {
                dc.DrawGeometry(ArrowFill, null, TriangleLeftRight(-9.5, 0, left: true));
                dc.DrawGeometry(ArrowFill, null, TriangleLeftRight(9.5, 0, left: false));
            }
            dc.Pop();
        }

        /// <summary>竖向三角: 尖朝上(up)或朝下, 居中于 (cx, cy)</summary>
        private static StreamGeometry TriangleUpDown(double cx, double cy, bool up)
        {
            const double h = 4.5, w = 3.5;   // 尖到中心的半高 / 底边半宽
            var tip = new Point(cx, cy + (up ? -h : h));
            var baseY = cy + (up ? w : -w);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(tip, true, true);
                ctx.LineTo(new Point(cx - w, baseY), true, false);
                ctx.LineTo(new Point(cx + w, baseY), true, false);
            }
            g.Freeze();
            return g;
        }

        /// <summary>横向三角: 尖朝左(left)或朝右, 居中于 (cx, cy)</summary>
        private static StreamGeometry TriangleLeftRight(double cx, double cy, bool left)
        {
            const double h = 4.5, w = 3.5;
            var tip = new Point(cx + (left ? -h : h), cy);
            var baseX = cx + (left ? w : -w);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(tip, true, true);
                ctx.LineTo(new Point(baseX, cy - w), true, false);
                ctx.LineTo(new Point(baseX, cy + w), true, false);
            }
            g.Freeze();
            return g;
        }
    }
}
