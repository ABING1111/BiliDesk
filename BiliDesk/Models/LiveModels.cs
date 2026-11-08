using System.Collections.Generic;
using System.ComponentModel;

namespace BiliDesk.Models;

/// <summary>
/// 直播父分区(网游 / 手游 / 单机游戏 / 娱乐 …)。
///
/// ★ 带 <see cref="IsSelected"/> 的变更通知: 分区胶囊的选中态是**从代码侧**改的
///   (点一个就把上一个的选中清掉), 没有 INPC 时界面不会跟着变 —— 表现就是"点哪个都不亮"。
/// </summary>
public class LiveAreaGroup : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public int Id { get; init; }
    public string Name { get; init; } = "";
    public List<LiveArea> Children { get; } = new();

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}

/// <summary>直播子分区(英雄联盟 / CS2 / 三角洲行动 …)。<see cref="Id"/> = 0 表示「全部」。</summary>
public class LiveArea : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public int Id { get; init; }
    public int ParentId { get; init; }
    public string Name { get; init; } = "";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}

/// <summary>
/// 直播间概览(2026-11-13 新增): 播放器右侧"主播信息面板"的数据源。
///
/// ★ 来源是 <c>getInfoByRoom</c> 的 room_info / anchor_info.base_info / watched_show:
///   · <see cref="LiveStartTime"/> 是开播时间(Unix 秒), 用来算"已开播多久";
///   · <see cref="WatchedText"/> 是服务端直接给的文案(如 "9.9万人看过" / "602.3万人气"),
///     比自己按 num 拼更稳 —— 两种口径服务端会按房间类型切换;
///   · <see cref="Online"/> 是 room_info.online, 作为文案缺失时的兜底。
/// </summary>
public class LiveRoomInfo
{
    public long RoomId { get; set; }

    /// <summary>主播 UID(room_info.uid), 点击头像/昵称进主页用</summary>
    public long AnchorUid { get; set; }

    public string AnchorName { get; set; } = "";
    public string AnchorFace { get; set; } = "";
    public string Title { get; set; } = "";
    public string AreaName { get; set; } = "";

    /// <summary>0 未开播 / 1 直播中 / 2 轮播</summary>
    public int LiveStatus { get; set; }

    /// <summary>开播时间(Unix 秒, 0 = 未知); 用于"已开播 X"</summary>
    public long LiveStartTime { get; set; }

    /// <summary>看过/人气数值(watched_show.num), 文案缺失时的兜底</summary>
    public long Watched { get; set; }

    /// <summary>服务端文案: "9.9万人看过" / "602.3万人气" 等</summary>
    public string WatchedText { get; set; } = "";

    /// <summary>当前人气(room_info.online)</summary>
    public long Online { get; set; }
}
