using System;

namespace BiliDesk.Helpers;

/// <summary>
/// 播放器弹层「点一下展开, 再点一下收起」的**纯判定逻辑**(不依赖 WPF, 可直接单测)。
///
/// ★ 为什么需要这个判定: 播放器的弹层(倍速/清晰度/音质/弹幕区域/分P)全是 StaysOpen="False"。
///   弹层开着时再点它的入口按钮, WPF 在 **MouseDown 阶段就把它自动收起了**(对弹层而言那是
///   "外部点击"), 等按钮的 Click 触发时 IsOpen 恒为 false —— 只判 IsOpen 取反(或无条件置 true)
///   永远走"展开"分支, 这就是用户报的"点击一律是打开, 收不起来"。
///
/// ★ 判定三分支:
///   · 还开着                → 收起(键盘触发 Click、或 WPF 尚未自动收起的时序);
///   · 刚收起(抑制窗口内)    → 保持收起: 这次 Click 正是"再点一下收起"的那一次,
///                             WPF 已代劳收起, 绝不能重新打开;
///   · 其余                  → 展开。
///
/// ★ 与 PlayerWindowPlacement 同款拆法: 判定是纯函数, 事件接线(HookPopupToggle/TogglePlayerPopup)
///   留在 PlayerWindow 里 —— 那里才有 Popup/Button 与真实输入时序。
/// </summary>
public static class PopupToggle
{
    /// <summary>
    /// 抑制窗口(毫秒): 收起后这么久之内, 入口按钮的 Click 被视为"收起它的那一次点击"。
    ///
    /// ★ 为什么要时间窗: Click 到达时 IsOpen 已是 false, 无法区分"刚被这一下点击收起"
    ///   与"早就收起了"。而前者的收起发生在同一次按压的 MouseDown 阶段, 到 Click(MouseUp)
    ///   的间隔就是一次按压时长, 常见 50~250ms —— 取 350ms 连慢按也能覆盖。
    /// ★ 调用方(PlayerWindow.HookPopupToggle)只在"收起那一刻光标正压在入口按钮上"时
    ///   才记收起时刻, 所以"点别处收起 → 立刻点入口"不会被误吞; 窗口内残留的极端情况
    ///   (按住按钮拖到别处松手后 350ms 内再点)最多吞一下, 再点即恢复。
    /// </summary>
    public const long SuppressReopenMs = 350;

    /// <summary>入口按钮这一下该执行的动作</summary>
    public enum Action
    {
        /// <summary>展开弹层</summary>
        Open,
        /// <summary>收起弹层(此刻它还开着)</summary>
        Close,
        /// <summary>保持收起: 这一下就是收起它的点击, WPF 已在 MouseDown 阶段代劳</summary>
        StayClosed,
    }

    /// <summary>
    /// 按"当前是否开着 + 最近一次收起时刻"判定入口按钮这一下的动作。
    /// </summary>
    /// <param name="isOpen">弹层此刻的 IsOpen(Click 到达时读到的值)</param>
    /// <param name="closedAtTick">最近一次"被入口按钮点击收起"的时刻(Environment.TickCount64); null = 没有这类收起</param>
    /// <param name="nowTick">当前时刻(与 closedAtTick 同一时钟源)</param>
    /// <param name="suppressMs">抑制窗口, 默认 <see cref="SuppressReopenMs"/></param>
    public static Action Decide(bool isOpen, long? closedAtTick, long nowTick, long suppressMs = SuppressReopenMs)
    {
        // 还开着 ⇒ 这一下的语义就是"收起"(键盘触发时 WPF 不会替我们自动收)。
        if (isOpen) return Action.Close;
        // 刚收起 ⇒ 这次 Click 与收起是同一次按压: 保持收起, 不重开。
        if (closedAtTick.HasValue && nowTick - closedAtTick.Value < suppressMs) return Action.StayClosed;
        return Action.Open;
    }
}