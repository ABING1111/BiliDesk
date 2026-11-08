using System.Collections.Generic;

namespace BiliDesk.Models;

/// <summary>
/// 一条直播弹幕(2026-11-13 新增)。
///
/// ★ 为什么单独一个类型而不复用 <see cref="Helpers.RawDanmaku"/>:
///   直播弹幕没有"出现时间"这个概念 —— 它是**到达即显示**, 而 RawDanmaku.Time 是点播
///   弹幕按时间轴排序/二分定位的关键字段。硬塞一个假时间进去, 会让"这条弹幕是什么时候的"
///   变成一处随时可能被误读的隐式约定; 直播侧只保留真正需要的四个字段。
/// </summary>
public readonly struct LiveDanmakuItem
{
    /// <summary>正文(已 Trim, 非空)</summary>
    public readonly string Text;

    /// <summary>显示模式: 1 = 滚动(默认), 4 = 底部固定, 5 = 顶部固定</summary>
    public readonly int Mode;

    /// <summary>颜色 0xRRGGBB(0xFFFFFF = 白)。与点播弹幕的十进制颜色字段同义。</summary>
    public readonly uint Color;

    /// <summary>发送者昵称(当前只用于诊断/将来做"按用户屏蔽", 渲染不显示)</summary>
    public readonly string UserName;

    /// <summary>粉丝勋章等级(0 = 没有勋章)。&gt;0 时聊天列表显示"粉丝团N"徽章。</summary>
    public readonly int MedalLevel;

    /// <summary>粉丝勋章名称(主播的粉丝团名), 作为徽章的 ToolTip。</summary>
    public readonly string MedalName;

    /// <summary>粉丝勋章颜色 0xRRGGBB(0 = 未指定)。</summary>
    public readonly uint MedalColor;

    /// <summary>是否房管(info[2][2] == 1)。</summary>
    public readonly bool IsAdmin;

    /// <summary>用户等级(info[4][0]), 当前只保留备用。</summary>
    public readonly int UserLevel;

    public LiveDanmakuItem(string text, int mode, uint color, string userName,
        int medalLevel = 0, string medalName = "", uint medalColor = 0,
        bool isAdmin = false, int userLevel = 0)
    {
        Text = text;
        Mode = mode;
        Color = color;
        UserName = userName;
        MedalLevel = medalLevel;
        MedalName = medalName;
        MedalColor = medalColor;
        IsAdmin = isAdmin;
        UserLevel = userLevel;
    }
}

/// <summary>
/// 直播弹幕服务器的连接参数(由 room_init + getDanmuInfo 两段接口拼出来)。
/// </summary>
public sealed class LiveDanmakuEndpoint
{
    /// <summary>真实房间号。★ 短号必须换成长号, 否则会连到别的/不存在的房间。</summary>
    public long RealRoomId { get; init; }

    /// <summary>主播 UID(room_init 的 uid), 当前只用于诊断。</summary>
    public long AnchorUid { get; init; }

    /// <summary>鉴权密钥(getDanmuInfo 的 token), 会过期, 重连必须重新取。</summary>
    public string Token { get; init; } = "";

    /// <summary>候选弹幕服务器(按接口返回顺序, 失败时依次尝试)。</summary>
    public List<LiveDanmakuHost> Hosts { get; } = new();
}

/// <summary>一个弹幕服务器地址(wss 端口)。</summary>
public readonly struct LiveDanmakuHost
{
    public readonly string Host;
    public readonly int Port;

    public LiveDanmakuHost(string host, int port)
    {
        Host = host;
        Port = port;
    }
}

/// <summary>直播弹幕连接状态(用于在播放器里给出"需要登录"这类提示)。</summary>
public enum LiveDanmakuConnectionState
{
    /// <summary>正在连接/重新连接</summary>
    Connecting,
    /// <summary>鉴权成功, 正在接收</summary>
    Connected,
    /// <summary>需要登录才能接收(未登录被服务端踢掉/鉴权被拒)</summary>
    NeedLogin,
    /// <summary>连接结束(正常关闭或重连次数用尽)</summary>
    Disconnected,
    /// <summary>拿不到房间/密钥等硬失败(不重连)</summary>
    Failed,
}
