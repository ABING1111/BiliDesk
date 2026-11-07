using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;

namespace BiliDesk.Models;

// ★ 2026-11-07 由 Models.cs 拆分而来(纯移动、零逻辑改动)。本文件: 云端收藏夹与私信(会话 / 消息)

/// <summary>云端收藏夹(账号下的一个收藏夹分组)</summary>
public class FavFolder : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public long Id { get; set; }
    public string Title { get; set; } = "";

    private int _mediaCount;

    /// <summary>
    /// 夹内条目数。
    /// 带变更通知是因为"批量移出"之后这个数要跟着减 —— 而**不能**靠"从集合里移除再插回来"
    /// 来触发刷新: 那会重建收藏夹 chip 的容器, 而 RadioButton 的分组选中态是挂在容器上的,
    /// 容器一重建, 高亮就没了(用户看到的是"删了几个之后选中的收藏夹标签突然不亮了")。
    /// </summary>
    public int MediaCount
    {
        get => _mediaCount;
        set
        {
            if (_mediaCount == value) return;
            _mediaCount = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(MediaCount)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayText)));
        }
    }

    /// <summary>
    /// 某个**具体视频**是否在这个收藏夹里。
    /// 只有"按 rid 查收藏夹"接口(/x/v3/fav/folder/created/list-all?rid=)才会带上这个语义
    /// (响应里的 fav_state 字段); 普通的收藏夹列表接口不返回它, 此时保持 false。
    /// </summary>
    public bool HasVideo { get; set; }

    private bool _isCurrent;

    /// <summary>
    /// 是否是当前选中的收藏夹(收藏页 chip 的高亮)。
    /// 必须带通知: "从「我的」页点卡片进来"这类**程序切换**不经过用户点击,
    /// RadioButton 的选中态只能靠这里推过去 —— 否则数据换了、高亮还停在旧夹上,
    /// 看起来就像"点了 A 打开了 B"。
    /// </summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    /// <summary>下拉里显示的 "标题 (数量)"</summary>
    public string DisplayText => MediaCount > 0 ? $"{Title} ({MediaCount})" : Title;

    /// <summary>
    /// 收藏夹封面(「我的」页的文件夹卡片用)。
    /// created/list 接口才下发; 个别夹(比如刚建的空夹)可能为空串, 界面自己放兜底底色。
    /// </summary>
    public string Cover { get; set; } = "";

    /// <summary>隐私状态: 0=公开 1=私密(接口不下发时按 0 处理)</summary>
    public int Privacy { get; set; }

    public string PrivacyText => Privacy == 1 ? "私密" : "公开";
}

/// <summary>
/// 私信会话(消息页左侧列表的一项)。
/// 需要 INotifyPropertyChanged 是因为: 打开会话要清未读角标、发完消息要更新摘要,
/// 这两件事都发生在对象已经绑到界面上之后。
/// </summary>
public class MsgSession : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    /// <summary>对话对象的 UID(群聊时是群 id)</summary>
    public long TalkerId { get; set; }

    private string _name = "";
    public string Name
    {
        get => _name;
        set { if (_name == value) return; _name = value; Raise(nameof(Name)); Raise(nameof(DisplayName)); }
    }

    private string _face = "";
    public string Face
    {
        get => _face;
        set { if (_face == value) return; _face = value; Raise(nameof(Face)); }
    }

    private string _preview = "";
    /// <summary>最后一条消息的摘要</summary>
    public string Preview
    {
        get => _preview;
        set { if (_preview == value) return; _preview = value; Raise(nameof(Preview)); }
    }

    private string _timeText = "";
    /// <summary>最后活跃时间</summary>
    public string TimeText
    {
        get => _timeText;
        set { if (_timeText == value) return; _timeText = value; Raise(nameof(TimeText)); }
    }

    private int _unreadCount;
    public int UnreadCount
    {
        get => _unreadCount;
        set { if (_unreadCount == value) return; _unreadCount = value; Raise(nameof(UnreadCount)); Raise(nameof(HasUnread)); }
    }

    /// <summary>已读到的序号 / 服务端最新序号(标记已读用)</summary>
    public long AckSeqno { get; set; }
    public long MaxSeqno { get; set; }

    public bool HasUnread => UnreadCount > 0;

    /// <summary>服务端名字拿不到时兜底显示</summary>
    public string DisplayName => string.IsNullOrEmpty(Name) ? $"UID {TalkerId}" : Name;
}

/// <summary>一条私信</summary>
public class MsgItem
{
    public long SenderUid { get; set; }
    /// <summary>消息序号: 用它排序而不是靠接口返回顺序 —— 接口在新旧方向上没有稳定保证</summary>
    public long Seqno { get; set; }
    /// <summary>是不是自己发的(决定气泡靠左还是靠右)</summary>
    public bool IsMine { get; set; }
    public string Text { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string TimeText { get; set; } = "";

    public bool IsImage => ImageUrl.Length > 0;
    public bool IsText => ImageUrl.Length == 0;
}

