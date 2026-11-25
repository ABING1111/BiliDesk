using System;
using System.Collections.Generic;
using BiliDesk.Models;

namespace BiliDesk.Helpers;

/// <summary>播放历史里一条记录的种类。三种"片源"的判等方式完全不同(见 SameVideo)。</summary>
public enum PlayerHistoryKind
{
    /// <summary>在线稿件(bvid + cid)。</summary>
    Online,
    /// <summary>直播间(roomId)。</summary>
    Live,
    /// <summary>本地文件(路径)。</summary>
    Local,
}

/// <summary>
/// 播放器历史里的一条记录(2026-11-25): "刚才看过的那一条"。
///
/// ★ 只装"重新打开它需要什么", 不装播放位置 —— 回退走正常加载链,
///   续播由已有的进度记录负责(与本机进度/云端进度的优先级完全一致)。
/// ★ <see cref="Bangumi"/> 是打开那一刻**正在生效**的番剧分集表: 回退到某一集时
///   要把它恢复回去, 否则选集面板与"播完自动下一集"会凭空消失。普通视频是 null。
/// </summary>
public sealed class PlayerHistoryEntry
{
    private PlayerHistoryEntry(PlayerHistoryKind kind, string title,
        string bvid, long cid, long roomId, string path, BangumiPlaylist? bangumi)
    {
        Kind = kind;
        Title = title ?? "";
        Bvid = bvid ?? "";
        Cid = cid;
        RoomId = roomId;
        Path = path ?? "";
        Bangumi = bangumi;
    }

    public PlayerHistoryKind Kind { get; }

    /// <summary>箭头 ToolTip 上显示的标题。直播拿不到房名时是"直播间 {roomId}"。</summary>
    public string Title { get; }

    public string Bvid { get; }

    /// <summary>要播的分P; 0 = 默认分P(见 LoadVideoAsync 的同名参数)。</summary>
    public long Cid { get; }

    public long RoomId { get; }

    public string Path { get; }

    public BangumiPlaylist? Bangumi { get; }

    /// <summary>在线稿件。cid 传当前正在播的那个分P(0 也行, 会落到默认分P)。</summary>
    public static PlayerHistoryEntry ForVideo(string bvid, long cid, string title,
        BangumiPlaylist? bangumi = null)
        => new(PlayerHistoryKind.Online, title, bvid, cid, 0, "", bangumi);

    public static PlayerHistoryEntry ForLive(long roomId, string title)
        => new(PlayerHistoryKind.Live, title, "", 0, roomId, "", null);

    public static PlayerHistoryEntry ForLocal(string path, string title)
        => new(PlayerHistoryKind.Local, title, "", 0, 0, path, null);

    /// <summary>
    /// 是不是"同一条视频"。
    ///
    /// ★★ 在线条目**只比 bvid, 不比 cid**: 分P 之间 bvid 完全相同, 而"上一个视频"
    ///   指的是上一条稿件, 不是上一个分P —— 若连 cid 一起比, 换个 P 就会多出一条
    ///   历史(用户连看 P1/P2/P3 之后要连按三次「上一个」才回到上一个视频)。
    ///   当前条目的 cid 由 <see cref="PlayerHistoryStack.OnOpened"/> 跟着最新那次打开更新。
    /// ★ 种类不同一律不等: bvid 为空串的直播/本地条目不能互相认成同一条。
    /// ★ 本地路径按不区分大小写比: Windows 文件系统本来就不区分。
    /// </summary>
    public bool SameVideo(PlayerHistoryEntry? other)
    {
        if (other == null || other.Kind != Kind) return false;
        return Kind switch
        {
            PlayerHistoryKind.Online => string.Equals(Bvid, other.Bvid, StringComparison.Ordinal),
            PlayerHistoryKind.Live => RoomId == other.RoomId,
            _ => string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase),
        };
    }
}

/// <summary>
/// 播放器的「上一个 / 下一个视频」记录栈(2026-11-25) —— **纯逻辑**, 可直接单测。
///
/// 语义与浏览器的后退/前进完全一致:
///   · 打开一条新视频 → 它成为当前, 刚才那条进后退栈, **前进栈清空**;
///   · 左箭头 = 后退(回到上一条), 右箭头 = 前进(回到后退之前所在的那条);
///   · 同一条稿件内换分P、切清晰度重载等"还是这一条"的打开, 只更新当前条目。
///
/// ★★ 栈的变动发生在"**真的打开了**"(<see cref="OnOpened"/>)那一刻, 而不是点箭头的时候:
///   加载失败(视频已删 / 直播已下播 / 本地文件不见了)时栈保持原样 ——
///   不会出现"退了一步, 却停在失败页上, 还要再按一次才能回去"。
///   <see cref="RequestBack"/> / <see cref="RequestForward"/> 只记下"这次要去哪",
///   由紧接着的那次 OnOpened 兑现; 若中途被别的视频顶替, 那条按新访问处理。
///
/// ★ 记录**只在进程内**保留(不落盘): 关掉播放器再打开另一条视频, 左箭头照样能回到
///   刚才看的那条 —— "观看过的"不因关窗而失忆。每栈上限 50 条, 超出丢最旧的。
/// ★ 只在 UI 线程访问(与播放器窗口的一切状态同线程)。
/// </summary>
public sealed class PlayerHistoryStack
{
    /// <summary>每栈保留的条数上限。够"看了半小时切来切去"用, 又不至于无限增长。</summary>
    public const int MaxEntries = 50;

    /// <summary>进程内唯一的一份历史(关窗/换窗口共用)。</summary>
    public static PlayerHistoryStack Shared { get; } = new();

    private enum Direction { None, Back, Forward }

    /// <summary>后退栈: 末尾 = 最近看过的那条。</summary>
    private readonly List<PlayerHistoryEntry> _back = new();

    /// <summary>前进栈: 末尾 = 后退之前所在的那条。</summary>
    private readonly List<PlayerHistoryEntry> _forward = new();

    /// <summary>已经"请求"但还没兑现的方向(见类型注释里的两阶段说明)。</summary>
    private Direction _pending;

    /// <summary>正在看的那条; 还没打开过任何东西时为 null。</summary>
    public PlayerHistoryEntry? Current { get; private set; }

    /// <summary>左箭头要去的那条(没有则 null)。</summary>
    public PlayerHistoryEntry? Previous => _back.Count > 0 ? _back[^1] : null;

    /// <summary>右箭头要去的那条(没有则 null)。</summary>
    public PlayerHistoryEntry? Next => _forward.Count > 0 ? _forward[^1] : null;

    /// <summary>要求后退: 返回目标条目并记下方向; 没有可退的返回 null(不动任何状态)。</summary>
    public PlayerHistoryEntry? RequestBack()
    {
        var target = Previous;
        if (target == null) return null;
        _pending = Direction.Back;
        return target;
    }

    /// <summary>要求前进: 返回目标条目并记下方向; 没有可进的返回 null(不动任何状态)。</summary>
    public PlayerHistoryEntry? RequestForward()
    {
        var target = Next;
        if (target == null) return null;
        _pending = Direction.Forward;
        return target;
    }

    /// <summary>
    /// 一条视频**真的打开了**(详情/取流成功, 或直播/本地文件已起播)。
    /// 这是唯一会改动两栈的入口, 三分支:
    ///   ① 兑现后退且目标对得上 → 弹出后退栈, 当前进前进栈;
    ///   ② 兑现前进且目标对得上 → 弹出前进栈, 当前进后退栈;
    ///   ③ 其余(新访问 / 同一条的重新打开) → 同一条只更新当前; 否则当前进后退栈、前进栈清空。
    /// ★ 方向标记在**任何**一次打开后都清掉: 方向只对紧接着的那次打开有效。
    /// </summary>
    public void OnOpened(PlayerHistoryEntry entry)
    {
        if (entry == null) return;

        var pending = _pending;
        _pending = Direction.None;

        if (pending == Direction.Back && Previous is { } backTarget && backTarget.SameVideo(entry))
        {
            _back.RemoveAt(_back.Count - 1);
            Push(Current, _forward);
            Current = entry;
            return;
        }
        if (pending == Direction.Forward && Next is { } forwardTarget && forwardTarget.SameVideo(entry))
        {
            _forward.RemoveAt(_forward.Count - 1);
            Push(Current, _back);
            Current = entry;
            return;
        }

        // 同一条稿件(换分P / 切清晰度后重载): 只更新当前条目 —— 不新增历史、不清前进栈,
        // 否则"退回上一条 → 切个分P → 前进"会凭空少一条可前进的记录。
        if (Current != null && Current.SameVideo(entry))
        {
            Current = entry;
            return;
        }

        Push(Current, _back);
        _forward.Clear();
        Current = entry;
    }

    /// <summary>
    /// 摘掉某一条(两个栈里所有和它同一条的)。给"本地文件已被删掉"那种
    /// "回不去了"的情况用: 留着它的话, 用户每按一次左箭头都撞在一个打不开的条目上。
    /// ★ 不动 <see cref="Current"/>: 正在播的那条不会走这条路。
    /// </summary>
    public void Remove(PlayerHistoryEntry entry)
    {
        if (entry == null) return;
        _back.RemoveAll(e => e.SameVideo(entry));
        _forward.RemoveAll(e => e.SameVideo(entry));
    }

    private static void Push(PlayerHistoryEntry? entry, List<PlayerHistoryEntry> stack)
    {
        if (entry == null) return;
        stack.Add(entry);
        if (stack.Count > MaxEntries) stack.RemoveAt(0);
    }
}
