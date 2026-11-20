namespace BiliDesk.Helpers;

/// <summary>
/// 播放器评论区右下角工具列「按滚动方向自动收起」的纯判定(不依赖 WPF, 可直接单测)。
///
/// ★ 解决的问题(issue #89): 三颗 46px 的悬浮按钮贴在信息栏右下角,
///   评论文字滚到那里时最右侧两三列字会被压住, 而按钮又一直挡着不挪开。
///   规则很简单:
///     · 向下滚过 <see cref="MinOffset"/> —— 收起(文字正往按钮底下走);
///     · 向上滚 / 回到顶部附近 —— 放回(用户要找「回到顶部」「返回」时会先往上滚);
///     · 输入面板展开时不收 —— 那时 FAB 是「收起输入框」按钮, 藏起来就没法关面板。
/// ★ 只认视口位移(<c>verticalChange</c>), 不认内容被追加撑高(<c>extentChange</c>):
///   翻页/图片加载让列表变长不是"用户在往下翻", 不该让按钮闪一下。
///
/// ★ 与 DescFoldPolicy / PopupToggle 同款拆法: 判定是纯函数,
///   动画与命中测试留在 PlayerWindow.SetCommentFabBarHidden 里。
/// </summary>
public static class CommentFabAutoHide
{
    /// <summary>向下滚过这个距离才收起。太小会让"轻轻一滚按钮就没了"像故障。</summary>
    public const double MinOffset = 120;

    /// <summary>回到这个偏移以内一定放回(底部内容就是评论区头部, 按钮在这里有用)。</summary>
    public const double TopOffset = 8;

    /// <summary>
    /// 算出滚动之后工具列是否该收起。
    /// </summary>
    /// <param name="hidden">滚动前的收起状态</param>
    /// <param name="offset">滚动后的垂直偏移</param>
    /// <param name="verticalChange">本次视口位移(正 = 往下)</param>
    /// <param name="extentChange">本次内容高度变化(追加评论/图片加载)</param>
    /// <param name="composerOpen">输入面板是否展开(FAB 此刻是「收起」按钮)</param>
    public static bool Decide(
        bool hidden, double offset, double verticalChange, double extentChange, bool composerOpen)
    {
        if (composerOpen) return false;

        // 内容变高、视口没动: 与滚动方向无关, 维持现状(不闪)
        if (verticalChange == 0 && extentChange != 0) return hidden;

        if (offset <= TopOffset) return false;
        if (verticalChange > 0 && offset > MinOffset) return true;
        if (verticalChange < 0) return false;
        return hidden;
    }
}
