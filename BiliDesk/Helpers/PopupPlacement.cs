using System;

namespace BiliDesk.Helpers;

/// <summary>
/// 弹层水平位置的**纯判定逻辑**(不依赖 WPF, 可直接单测)。
///
/// ★ 为什么需要: WPF 的 <c>Placement=Bottom</c> 只让弹层**左缘**与入口按钮左缘对齐,
///   自己的兜底也只夹**显示器**边缘, 不管窗口。播放器的分P 入口在右侧 400px 信息栏里,
///   又排在简介/评论/合集之后 —— 300px 宽的弹层从那里往右铺, 必然顶出窗口右缘
///   (用户截图: 分P 看板展开后整体偏右、超出播放器窗口)。
///
/// ★ 判定只做一件事: 装得下就**不挪**(保持左缘对齐入口按钮, 与其它弹层手感一致),
///   装不下才整体左移, 移到右缘离容器右缘正好剩 <see cref="EdgeMargin"/>。
///   容器比弹层还窄时反过来保左缘 —— 宁可右边超一点, 也不能让弹层开头跑出窗口。
///
/// ★ 与 PopupToggle / PlayerWindowPlacement 同款拆法: 判定与单位换算是纯函数,
///   量屏幕坐标、改 HorizontalOffset 的接线留在 PlayerWindow(见 PositionPagesPopupAtButton)。
/// </summary>
public static class PopupPlacement
{
    /// <summary>弹层右缘与容器右缘之间至少保留的空隙(DIP)。</summary>
    public const double EdgeMargin = 8;

    /// <summary>
    /// 算出让弹层留在容器内的水平偏移。
    /// </summary>
    /// <param name="anchorLeft">入口按钮左缘在容器坐标系里的位置(弹层默认就落在这里)</param>
    /// <param name="popupWidth">弹层宽度</param>
    /// <param name="containerLeft">容器左缘</param>
    /// <param name="containerRight">容器右缘(通常是窗口客户区右缘)</param>
    /// <param name="margin">右缘至少保留的空隙, 默认 <see cref="EdgeMargin"/></param>
    /// <returns>
    /// ≤ 0 的偏移: 0 = 保持左缘对齐入口按钮; 负数 = 整体左移这么多。
    /// 结果与三个入参同一单位(调用方统一用屏幕物理像素算, 再折算回
    /// <c>Popup.HorizontalOffset</c> 所在的目标本地 DIP —— 见 PositionPagesPopupAtButton)。
    /// </returns>
    public static double ClampHorizontalOffset(
        double anchorLeft, double popupWidth,
        double containerLeft, double containerRight,
        double margin = EdgeMargin)
    {
        // 尺寸还没量出来 / 容器本身不成立: 不插手, 保持 WPF 的默认摆法
        if (popupWidth <= 0 || containerRight <= containerLeft) return 0;

        // 默认右缘: 左缘对齐入口按钮
        var right = anchorLeft + popupWidth;
        // 装不下 → 左移到右缘恰好留出 margin
        var offset = right + margin > containerRight
            ? containerRight - margin - right
            : 0;

        // 反过来保左缘: 容器比弹层还窄时, 上面那次左移会把弹层开头推出窗口
        if (anchorLeft + offset < containerLeft)
            offset = containerLeft - anchorLeft;

        return offset;
    }

    /// <summary>弹层与入口按钮之间的默认竖直间隙(DIP)。</summary>
    public const double AnchorGap = 4;

    /// <summary>
    /// 算出让弹层挂在入口按钮**下方**的顶边坐标。
    ///
    /// ★ 为什么不用 WPF 的 <c>Placement=Bottom</c>: 那条路会把弹层钉在按钮上, 按钮所在的
    ///   滚动区域一滚, 弹层就跟着走(用户报"分P 面板随滚轮一起向下移动")。播放器改成把弹层
    ///   锚在**不滚动**的信息栏外层, 位置在打开时按按钮的当前坐标算一次 —— 这里就是那条
    ///   "算一次"的纯逻辑。
    /// ★ 下方放不下时翻到按钮上方(与 WPF 自己的翻面行为一致); 两边都放不下才夹回容器内,
    ///   保证弹层至少整块可见, 不会一半在窗口外。
    /// </summary>
    /// <param name="anchorTop">入口按钮顶边(与容器同一坐标系)</param>
    /// <param name="anchorBottom">入口按钮底边</param>
    /// <param name="popupHeight">弹层高度</param>
    /// <param name="containerTop">容器顶边(通常是窗口客户区)</param>
    /// <param name="containerBottom">容器底边</param>
    /// <param name="gap">弹层与按钮之间的竖直间隙, 默认 <see cref="AnchorGap"/></param>
    /// <param name="margin">与容器上下缘至少保留的空隙, 默认 <see cref="EdgeMargin"/></param>
    /// <returns>弹层顶边在容器坐标系里的位置</returns>
    public static double DropdownTop(
        double anchorTop, double anchorBottom, double popupHeight,
        double containerTop, double containerBottom,
        double gap = AnchorGap, double margin = EdgeMargin)
    {
        if (popupHeight <= 0 || containerBottom <= containerTop) return anchorBottom + gap;

        var top = anchorBottom + gap;
        // 下方放不下 → 翻到按钮上方
        if (top + popupHeight + margin > containerBottom)
        {
            var above = anchorTop - gap - popupHeight;
            if (above >= containerTop + margin) top = above;
        }
        // 兜底: 整块夹回容器内; 容器比弹层还矮时保顶部
        if (top + popupHeight + margin > containerBottom)
            top = containerBottom - margin - popupHeight;
        if (top < containerTop + margin)
            top = containerTop + margin;

        return top;
    }
}
