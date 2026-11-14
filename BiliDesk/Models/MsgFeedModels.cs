using System.Collections.Generic;

namespace BiliDesk.Models;

/// <summary>
/// 消息页的四个 Tab。
/// ★ 顺序就是界面上的顺序(聊天列表 / 回复我的 / @我的 / 收到的赞), 不要重排 ——
///   页面的 RadioButton 与 VM 里的下标是按下标对应的。
/// </summary>
public enum MsgTab
{
    /// <summary>私信会话(两栏: 会话列表 + 聊天内容)</summary>
    Chat = 0,
    /// <summary>回复我的</summary>
    Reply = 1,
    /// <summary>@我的</summary>
    At = 2,
    /// <summary>收到的赞</summary>
    Like = 3
}

/// <summary>
/// 一个私信表情。
///
/// 私信的表情**不是**评论那套 `content.emote` 字典, 而是接口在 data.e_infos 里
/// 一次性给出「整段会话用到过的表情」: text 是短代码(带中括号, 如 `[tv_doge]`),
/// uri 是图片地址, size 1 = 小 / 2 = 大。
/// </summary>
public class MsgEmote
{
    /// <summary>短代码, 形如 "[tv_doge]" —— 正文里原样出现的就是它</summary>
    public string Text { get; set; } = "";

    public string Url { get; set; } = "";

    /// <summary>1 = 小表情(约一行文字高) 2 = 大表情</summary>
    public int Size { get; set; } = 1;
}

/// <summary>表情包(表情面板按包分组显示)</summary>
public class MsgEmotePackage
{
    public long Id { get; set; }
    public string Text { get; set; } = "";
    public List<MsgEmote> Emotes { get; } = new();
}

/// <summary>未读数(消息页四个 Tab 的角标)</summary>
public class MsgUnread
{
    public int Chat { get; set; }
    public int Reply { get; set; }
    public int At { get; set; }
    public int Like { get; set; }
}

/// <summary>
/// 互动消息(回复我的 / @我的 / 收到的赞)的一条。
///
/// 三个接口的返回结构高度相似, 这里统一成一种模型:
///   · reply/at: user + item.source_content 是"别人说了什么";
///   · like:     user(或 users) + counts 是"多少人赞了你"。
/// </summary>
public class MsgFeedItem
{
    /// <summary>通知 id(分页游标的一半)</summary>
    public long Id { get; set; }

    public long UserMid { get; set; }
    public string UserName { get; set; } = "";
    public string UserFace { get; set; } = "";

    /// <summary>别人的原话(回复内容 / @ 到你的评论内容); 点赞通知为空</summary>
    public string Content { get; set; } = "";

    /// <summary>动作描述: "回复了你" / "@了你" / "赞了你"</summary>
    public string ActionText { get; set; } = "";

    /// <summary>多人点赞时的补充说明("等 3 人"), 单人时为空</summary>
    public string ExtraText { get; set; } = "";

    /// <summary>来源标题(视频标题 / 动态摘要), 界面上作为次要行</summary>
    public string ContextTitle { get; set; } = "";

    /// <summary>点击后要打开的地址(web uri; 视频会优先解析出 BV 号走内置播放器)</summary>
    public string ContextUri { get; set; } = "";

    /// <summary>从 ContextUri 解析出的 BV 号, 有值就进内置播放器</summary>
    public string Bvid { get; set; } = "";

    /// <summary>来源业务: 1=视频 11=动态 …(只用于显示类型标签, 取不到就是 0)</summary>
    public int BusinessId { get; set; }

    public long Time { get; set; }
    public string TimeText { get; set; } = "";

    /// <summary>界面上的主标题行: "昵称 动作 补充"</summary>
    public string HeaderText
        => string.IsNullOrEmpty(ExtraText)
            ? $"{UserName} {ActionText}".Trim()
            : $"{UserName} {ExtraText} {ActionText}".Trim();

    public bool HasContent => Content.Length > 0;
    public bool HasContext => ContextTitle.Length > 0;
    public bool CanOpen => ContextUri.Length > 0 || Bvid.Length > 0;
}

/// <summary>一页互动消息</summary>
public class MsgFeedPage
{
    public List<MsgFeedItem> Items { get; } = new();

    /// <summary>还有下一页</summary>
    public bool HasMore { get; set; }

    /// <summary>下一页游标(接口 cursor.id / cursor.time)</summary>
    public long NextId { get; set; }
    public long NextTime { get; set; }
}
