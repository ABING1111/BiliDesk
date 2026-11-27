using System;
using System.Collections.Generic;
using System.Linq;

namespace BiliDesk.Models;

/// <summary>
/// 可自定义快捷键的操作。
///
/// ★ 用枚举而不是散落的字符串常量: 播放器/主界面的分发 switch 需要编译期穷举,
///   以后新增动作漏接一个分支很难被发现。存储用的 id 在 <see cref="ShortcutDefinition.Id"/> 里,
///   与枚举名解耦 —— 以后改枚举名不会影响用户已经保存的 settings.json。
/// </summary>
public enum ShortcutAction
{
    PlayPause,
    SeekBackward,
    SeekForward,
    PreviousVideo,
    NextVideo,
    VolumeUp,
    VolumeDown,
    Mute,
    Fullscreen,
    ToggleDanmaku,
    ToggleSubtitle,
    Like,
    OpenUpSpace,
    Back,
    Refresh,
}

/// <summary>
/// 一个可配置操作的完整定义(设置页的行 + 默认键 + 分发时的语义)。
/// ★ 默认键字符串同时是"展示/存储/匹配"的单一来源, 不要在别处再写一遍。
/// </summary>
public sealed class ShortcutDefinition
{
    public ShortcutDefinition(
        ShortcutAction action,
        string id,
        string group,
        string title,
        string description,
        string defaultGesture)
    {
        Action = action;
        Id = id;
        Group = group;
        Title = title;
        Description = description;
        DefaultGesture = defaultGesture;
    }

    public ShortcutAction Action { get; }

    /// <summary>settings.json 里 Shortcuts 字典的键(稳定, 不随枚举改名)</summary>
    public string Id { get; }

    /// <summary>设置页的分组标题</summary>
    public string Group { get; }

    public string Title { get; }

    /// <summary>行下方的一行说明(把长按 / Shift 这类隐藏手势写清楚)</summary>
    public string Description { get; }

    /// <summary>默认组合键(规范格式, 例如 "Alt+Left")</summary>
    public string DefaultGesture { get; }
}

/// <summary>
/// 快捷键目录: 设置页显示什么、默认值是什么、按什么顺序分组, 全部在这一处。
///
/// ★ 顺序就是"匹配优先级": 手改配置造成两条绑定冲突时, 目录里靠前的生效
///   (见 ShortcutMap.Build 的说明)。UI 正常改绑走 TryAssign, 冲突会直接被拦掉。
/// </summary>
public static class ShortcutCatalog
{
    public const string GroupPlayback = "播放控制";
    public const string GroupVolume = "音量";
    public const string GroupPlayer = "播放器操作";
    public const string GroupNavigation = "导航";
    public const string GroupApp = "主界面";

    /// <summary>设置页展示顺序</summary>
    public static IReadOnlyList<string> Groups { get; } = new[]
    {
        GroupPlayback, GroupVolume, GroupPlayer, GroupNavigation, GroupApp,
    };

    public static IReadOnlyList<ShortcutDefinition> All { get; } = new ShortcutDefinition[]
    {
        new(ShortcutAction.PlayPause, "player.playPause", GroupPlayback,
            "播放 / 暂停", "直播中键盘暂停不生效, 点控制栏按钮仍可暂停", "Space"),

        new(ShortcutAction.SeekBackward, "player.seekBackward", GroupPlayback,
            "快退", "每次 5 秒; 按住 Shift 一次退 30 秒", "Left"),

        new(ShortcutAction.SeekForward, "player.seekForward", GroupPlayback,
            "快进", "每次 5 秒; 按住 Shift 一次进 30 秒; 长按进入倍速播放", "Right"),

        new(ShortcutAction.PreviousVideo, "player.previousVideo", GroupPlayback,
            "上一个视频", "像浏览器后退一样, 回到刚看过的视频", "Alt+Left"),

        new(ShortcutAction.NextVideo, "player.nextVideo", GroupPlayback,
            "下一个视频", "像浏览器前进一样, 回到刚才那条视频", "Alt+Right"),

        new(ShortcutAction.VolumeUp, "player.volumeUp", GroupVolume,
            "音量 +", "长按快进期间改为切换 1.5x / 2x / 3x", "Up"),

        new(ShortcutAction.VolumeDown, "player.volumeDown", GroupVolume,
            "音量 −", "长按快进期间改为切换 1.5x / 2x / 3x", "Down"),

        new(ShortcutAction.Mute, "player.mute", GroupVolume,
            "静音", "在静音与 80% 音量之间切换", "M"),

        new(ShortcutAction.Fullscreen, "player.fullscreen", GroupPlayer,
            "全屏", "再按一次退出全屏", "F"),

        new(ShortcutAction.ToggleDanmaku, "player.danmaku", GroupPlayer,
            "弹幕开关", "显示或隐藏所有弹幕", "D"),

        new(ShortcutAction.ToggleSubtitle, "player.subtitle", GroupPlayer,
            "字幕开关", "显示或隐藏字幕(需要视频有字幕轨)", "C"),

        new(ShortcutAction.Like, "player.like", GroupPlayer,
            "点赞", "长按播放器里的赞按钮可一键三连", "L"),

        new(ShortcutAction.OpenUpSpace, "player.upSpace", GroupPlayer,
            "打开 UP 主主页", "打开当前视频 UP 主的主页", "U"),

        new(ShortcutAction.Back, "player.back", GroupNavigation,
            "返回上一级", "评论详情 → 全屏 → 小窗 → 铺满窗口 → 关闭播放器, 逐级返回", "Escape"),

        new(ShortcutAction.Refresh, "app.refresh", GroupApp,
            "刷新当前页面", "首页 / 动态 / 分区 / 我的 / 历史 / 收藏 / 稍后再看 / 缓存 / 消息", "F5"),
    };

    public static ShortcutDefinition? ByAction(ShortcutAction action)
        => All.FirstOrDefault(d => d.Action == action);

    public static ShortcutDefinition? ById(string id)
        => All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal));
}
