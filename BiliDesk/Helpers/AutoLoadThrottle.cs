using System;
using System.Windows.Controls;
using System.Windows.Threading;

namespace BiliDesk.Helpers;

/// <summary>
/// "滚动到底自动加载更多"的公共闸门。
///
/// ★★★ 2026-11-09 新增(用户报"改变窗口大小时, 视频卡片抽搐乱动还伴随程序自己下滑刷新加载更多")。
///
///   原来的各个页面(首页 5 个 tab、搜索、分区、稍后再看、动态)都是**只要收到
///   <c>ScrollChanged</c> 就判一次**"离底很近 / 内容不满一屏"。而窗口缩放时 WPF 会连续发
///   <c>ScrollChanged</c>, 中间态的 <c>ScrollableHeight</c> 是按新宽度重算的、<c>VerticalOffset</c>
///   还是旧值 —— 两个判据会临时为真, 于是用户只是拖了一下窗口, 页面却在连续拉下一页。
///
///   这里的策略:
///     · <see cref="CardWall.IsResizing"/> 期间一律不加载;
///     · 只有 <c>ExtentHeightChange == 0 && ViewportHeightChange == 0</c> 的
///       <c>ScrollChanged</c> 才是"用户真的滚了"(或至少偏移独立变化), 才立即判定;
///     · 其余(布局/分帧填充/缩放引起的高度变化)推迟 <see cref="LayoutSettleMs"/> 再判一次,
///       期间每来一个新事件就重新计时 —— 保证判的是**稳定后**的高度;
///     · 真正发起加载前还有 <see cref="CooldownMs"/> 的冷却, 防止连续事件触发多次请求;
///     · 卡片墙为了钉住可见区自己调的 <c>ScrollToVerticalOffset</c> 会被忽略
///       (见 <see cref="CardWall.WasProgrammaticScroll"/>)。
///
/// ★ 用法: 每个滚动区一个实例。构造函数必须在 UI 线程调用(DispatcherTimer 要求)。
/// </summary>
public sealed class AutoLoadThrottle
{
    /// <summary>布局变化后等多久再判"到底/不满一屏"。见类型注释。</summary>
    private const int LayoutSettleMs = 300;

    /// <summary>两次自动加载之间至少间隔多久, 防止同一批 ScrollChanged 连续拉多页。</summary>
    private const int CooldownMs = 500;

    /// <summary>卡片墙自己滚动的忽略窗口。</summary>
    private const int ProgrammaticScrollGraceMs = 250;

    private readonly DispatcherTimer _settleTimer;
    private Action? _settledCheck;
    private long _lastTriggerTick;

    public AutoLoadThrottle()
    {
        _settleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(LayoutSettleMs),
        };
        _settleTimer.Tick += (_, _) =>
        {
            _settleTimer.Stop();
            RunSettledCheck();
        };
    }

    /// <summary>
    /// 这个 <paramref name="e"/> 是不是"布局变化"引起的。是的话安排一次稳定后检查并返回 true
    /// (调用方应当直接 return, 不要立即加载); 返回 false 表示可以走正常的立即判定。
    /// </summary>
    /// <param name="settledCheck">
    /// 稳定后要执行的那次判定(通常就是调用方自己的 TriggerAutoLoad)。多次调用只保留最后一次。
    /// </param>
    public bool ShouldSkipLayoutChange(ScrollChangedEventArgs e, Action settledCheck)
    {
        if (CardWall.IsResizing)
        {
            Schedule(settledCheck);
            return true;
        }

        // ★ 高度/视口变化 ⇒ 这一拍的高度不是"用户看着的稳定版式", 不能立即判。
        //   注意 VerticalChange 也可能是被夹出来的(内容变矮), 所以这里**连它一起不信任**。
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
        {
            Schedule(settledCheck);
            return true;
        }

        // 卡片墙的锚点补偿: 忽略它, 否则"重排后补偏移"会被当成用户滚到底。
        if (CardWall.WasProgrammaticScroll(ProgrammaticScrollGraceMs)) return true;

        return false;
    }

    /// <summary>
    /// 真正准备发起一次加载前调用。返回 false = 冷却中, 这次跳过。
    /// </summary>
    public bool TryEnter()
    {
        var now = Environment.TickCount64;
        if (_lastTriggerTick != 0 && now - _lastTriggerTick < CooldownMs) return false;
        _lastTriggerTick = now;
        return true;
    }

    /// <summary>安排/重置一次"布局稳定后检查"。</summary>
    public void Schedule(Action check)
    {
        _settledCheck = check;
        _settleTimer.Stop();
        _settleTimer.Start();
    }

    private void RunSettledCheck()
    {
        var check = _settledCheck;
        _settledCheck = null;
        if (check == null) return;

        // 计时期间窗口又开始缩放了: 等它再次稳定, 不要在这一拍判。
        if (CardWall.IsResizing)
        {
            Schedule(check);
            return;
        }

        check();
    }
}
