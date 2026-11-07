using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;

namespace BiliDesk.Models;

// ★ 2026-11-07 由 Models.cs 拆分而来(纯移动、零逻辑改动)。本文件: 评论: 表情 / 图片 / 评论条目(含回复树)

/// 评论里的一个表情。
/// 字段名跟着接口走: `url` 是图片地址, `meta.size` 是尺寸档位
/// (1 = 常规小表情, 2 = 偏大的那种, 比如游戏联动表情)。
/// </summary>
public class CommentEmote
{
    public string Url { get; set; } = "";

    /// <summary>接口 meta.size。1 = 小表情(约一行文字高), 2 = 大表情</summary>
    public int Size { get; set; } = 1;
}

/// <summary>
/// 评论里的一张图片(接口 content.pictures 数组的一项)。
/// </summary>
public class CommentPicture
{
    /// <summary>图片地址(img_src)。点击看大图也用它(ImagePreviewWindow 会按 4096 解码)</summary>
    public string Url { get; set; } = "";

    /// <summary>原始宽高(像素)。布局用它算缩放比, 缺了按方形兜底</summary>
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>文件大小(字节)。接口可能不给, 0 = 未知, 只用于提示</summary>
    public long FileSize { get; set; }

    /// <summary>
    /// 缩略图显示宽(px)。照 B 站网页版: 缩略图**最高 160px**, 宽按原始宽高比算。
    /// ★ 原始尺寸缺失(0)时按 160 方形兜底 —— 按比例算会得 NaN, 图直接消失。
    /// </summary>
    public double ThumbWidth => Width > 0 && Height > 0
        ? Math.Round(160.0 * Width / Height)
        : 160;
}

/// <summary>评论条目(顶层评论与楼中楼回复共用同一个模型)</summary>
public class CommentItem : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    /// <summary>评论 id。取楼中楼回复时要拿它当 root 参数</summary>
    public long Rpid { get; set; }

    /// <summary>
    /// 评论者 mid。头像点一下跳他的个人主页要用。
    /// 顶层评论和楼中楼回复的 JSON 里都有 `mid` 字段(不必绕 member.mid)。
    /// </summary>
    public long Mid { get; set; }

    /// <summary>评论者等级(0~6)。0 = 接口没给, 不显示徽章</summary>
    public int Level { get; set; }

    public bool HasLevel => Level > 0;

    public string LevelText => "LV" + Level;

    /// <summary>
    /// 这条评论的作者是不是**本视频的 UP 主** → 昵称后挂 [UP] 标。
    /// 判据是评论者 mid == 顶层 data.upper.mid(接口按"当前稿件"给, 不是按评论本身),
    /// 所以只有取列表时能算, 解析单条时拿不到。
    /// </summary>
    public bool IsUp { get; set; }

    /// <summary>
    /// 这条是不是**置顶评论**(UP 主置顶 / 管理员置顶)。
    ///
    /// ★ 2026-10-03 修"置顶评论不可见": 置顶那条**不在** `data.replies` 里, 而在
    ///   `data.top.upper`(UP 主置顶)或 `data.top.admin`(管理员置顶) —— 只读 replies 的话
    ///   它永远不会出现在列表里。实测: 置顶 rpid=315658667873 在 replies 里**找不到**。
    ///   解析时由调用方置位, 界面据此在最前面挂一个"置顶"标。
    /// </summary>
    public bool IsPinned { get; set; }

    /// <summary>
    /// 被回复的那条评论 id。
    /// 直接回复楼主时它等于线程根(可用它判"这是给楼主的第一层回复"), 回复楼中楼里某个人时
    /// 指向那个人 —— 与 root 不同就说明这是"对话中的再回复"。
    /// </summary>
    public long ParentRpid { get; set; }

    public string UserName { get; set; } = "";
    public string UserFace { get; set; } = "";
    public string Content { get; set; } = "";

    private int _likeCount;
    public int LikeCount
    {
        get => _likeCount;
        set
        {
            if (_likeCount == value) return;
            _likeCount = value;
            Raise(nameof(LikeCount));
            Raise(nameof(LikeText));
        }
    }

    private bool _isLiked;

    /// <summary>
    /// 当前登录用户有没有给这条评论点过赞(接口的 `action` 字段: 1 = 已赞)。
    /// 带 INPC: 点赞是**从代码侧**改这个值的, 没有通知按钮的高亮不会跟着变。
    /// </summary>
    public bool IsLiked
    {
        get => _isLiked;
        set
        {
            if (_isLiked == value) return;
            _isLiked = value;
            Raise(nameof(IsLiked));
        }
    }

    /// <summary>点赞请求进行中(防连点: 连点会造成 赞/取消 交叉, 服务端最终状态就不确定了)</summary>
    private bool _likeBusy;
    public bool LikeBusy
    {
        get => _likeBusy;
        set
        {
            if (_likeBusy == value) return;
            _likeBusy = value;
            Raise(nameof(LikeBusy));
            Raise(nameof(CanLike));
        }
    }

    /// <summary>
    /// 当前用户有没有给这条评论点过**踩**。
    ///
    /// ★ 注意接口层面的实情(2026-09-27 修正): 赞和踩是**两个不同的接口**
    /// (`/x/v2/reply/action` 与 `/x/v2/reply/hate`), 踩的 action 只在 1/0 之间取值。
    /// 评论列表里的 `action` 字段是不是真会下发 2(已踩) 并没有验证过, 所以这个值更准确的
    /// 含义是"**本地已知的踩状态**": 拿不到就当未踩, 由用户点一下来纠正。
    /// 踩**没有计数**: 接口不下发 dislike 总数(网页版也只是一个图标), 所以只有状态、没有数字。
    ///
    /// 赞与踩在服务端是互相覆盖的(点赞会消掉踩、点踩会消掉赞), 所以界面上的两个状态也必须互斥。
    /// </summary>
    private bool _isDisliked;
    public bool IsDisliked
    {
        get => _isDisliked;
        set
        {
            if (_isDisliked == value) return;
            _isDisliked = value;
            Raise(nameof(IsDisliked));
        }
    }

    public bool CanLike => !_likeBusy;

    public string RcTime { get; set; } = "";

    /// <summary>
    /// 评论发布时间的 Unix 秒。界面只用 <see cref="RcTime"/> 显示, 这里留原始值是为了**排序**:
    /// 详情页里切「时间」时要按真实时间重排楼中楼, 拿格式化过的字符串比虽然也能用,
    /// 但一旦哪天改了显示格式就会静默排错。
    /// </summary>
    public long Ctime { get; set; }

    /// <summary>
    /// 正文里出现的表情: 短代码(`[微笑]`) → 图片地址。
    ///
    /// 为什么不能用 Unicode 直接替: B 站的评论区里绝大多数是**自定义表情**
    /// (大会员表情、直播表情、游戏联动表情), 根本没有对应的 Unicode 字符。
    /// 接口在 `content.emote` 里把"这条评论用到的表情"连图片地址一起给了,
    /// key 就是正文里原样出现的短代码, 所以按它替换即可。
    /// </summary>
    public Dictionary<string, CommentEmote> Emotes { get; } = new();

    /// <summary>
    /// IP 属地。来自接口的 `reply_control.location`, 是**已经带前缀的展示串**
    /// (形如 "IP属地：上海"), 所以直接显示、不再自己拼前缀。
    /// 可能为空(用户关了展示 / 接口没给), 空则不显示。
    /// </summary>
    public string Location { get; set; } = "";

    public bool HasLocation => Location.Length > 0;

    // ------------------------------------------------------------ @提及(2026-10-30)

    /// <summary>
    /// 正文里 @ 到的**昵称列表**(按出现顺序, 可重复)。
    ///
    /// ★ 为什么要自己解析: B 站接口的 <c>content</c> 字段是**纯文本**(emote 另给),
    ///   但回复楼中楼里的 "@某某" 是**结构化引用** —— 昵称在正文里,
    ///   而真正的主键(rpid)在 <c>reply_control.reply_id</c>。
    ///   界面只要"把 @ 出来的那几个字染成强调色", 自己扫文本就够了。
    ///
    /// ★ 解析规则(与 B 站网页版一致):
    ///   · <c>@</c> 后面跟一段**非空白**字符, 直到下一个空白或标点为止;
    ///   · 昵称里可能含中文、字母、数字、点、下划线、减号 —— 所以不能只认单词字符;
    ///   · 长度上限 20(超长的多半是误匹配, 例如邮箱)。
    /// </summary>
    public List<string> Mentions { get; } = new();

    /// <summary>正文里有没有 @ 人(用于决定要不要做高亮扫描)</summary>
    public bool HasMentions => Mentions.Count > 0;

    // ------------------------------------------------------------ 评论图片(2026-10-31)

    /// <summary>
    /// 这条评论附带的图片(接口 <c>content.pictures</c>)。带图评论才有内容。
    ///
    /// ★ 接口形态(2026-10-31 实测, /x/v2/reply/main):
    ///   pictures: [ { img_src, img_width, img_height, img_size, img_tag?, img_type? } ]
    ///   · 尺寸是**原始像素**, 网页版按它把图缩到最高 160px 的缩略图;
    ///   · 带图评论的 message 里**不含**图片占位符, 图是"正文下面挂一排缩略图"。
    /// </summary>
    public List<CommentPicture> Pictures { get; } = new();

    /// <summary>有没有图(用于决定图片行的可见性)</summary>
    public bool HasPictures => Pictures.Count > 0;

    public string LikeText => LikeCount > 0 ? VideoItem.FormatCount(LikeCount) : "";

    // ------------------------------------------------------------ 楼中楼

    private int _replyCount;

    /// <summary>这条评论下的回复总数(服务端的 rcount)。
    /// 注意它可能**大于**已取到的条数 —— 首页接口只内联前几条。</summary>
    public int ReplyCount
    {
        get => _replyCount;
        set
        {
            if (_replyCount == value) return;
            _replyCount = value;
            Raise(nameof(ReplyCount));
            Raise(nameof(HasReplies));
            Raise(nameof(HasMoreReplies));
            Raise(nameof(ReplyMoreText));
            Raise(nameof(ReplyBoxText));
        }
    }

    /// <summary>已取到的回复(内联的前几条 + 后续展开补齐的)</summary>
    public ObservableCollection<CommentItem> Replies { get; } = new();

    /// <summary>
    /// 列表页「楼中楼方框」里展示的**预览**回复(最多 3 条)。
    ///
    /// ★ 2026-11-04: 方框以前直接绑 <see cref="Replies"/>, 详情页自动补全后
    ///   同一份集合被撑到上百条 —— 从详情页返回列表时, 那条评论底下突然挂着
    ///   一整屏回复, 不会自己收起来。详情页继续用 <see cref="Replies"/> 看全部,
    ///   列表只认这份短预览, 返回时自然维持原样。
    /// </summary>
    public ObservableCollection<CommentItem> PreviewReplies { get; } = new();

    // ------------------------------------------------------------ 楼中楼预览方框

    /// <summary>方框底部那行提示文字(点整框进详情页)</summary>
    public string ReplyBoxText => ReplyCount > 0 ? $"共 {ReplyCount} 条回复" : "";

    /// <summary>
    /// 方框/详情页里一条回复的**前缀**(用户名那一截)。
    ///
    /// 规则跟 B 站网页版一致:
    ///   · 正文以 "回复 @XXX：" 开头(接口对"回复楼中楼里的人"的正文**自带**这截前缀)
    ///     → 前缀只给"用户名 "一个空格, 免得出现"用户名 回复 @XX 回复 @XX"；
    ///   · 否则(直接回复楼主) → 前缀给"用户名：", 冒号由我们补。
    /// UP 主的评论在用户名后面挂 [UP]。
    /// </summary>
    public string ReplyPrefix
    {
        get
        {
            var name = UserName;
            if (IsUp) name += " [UP]";
            return Content.StartsWith("回复 @", StringComparison.Ordinal) ? name + " " : name + "：";
        }
    }

    /// <summary>
    /// 正文是否以 "回复 @XXX：" 开头 —— 接口对"回复楼中楼里某个人"的正文**自带**这截前缀。
    /// 详情页据此挂"查看对话"入口(直接回复楼主的那条没有对话可追)。
    /// </summary>
    public bool IsReplyToSomeone => Content.StartsWith("回复 @", StringComparison.Ordinal);

    /// <summary>
    /// 楼中楼"展开更多"下一次要请求的页码。
    /// 之前固定 pn=1: 超过 20 条的楼中楼第二页永远拉不到(去重后一条不增,
    /// 按钮却一直显示"展开 N 条回复"点不动)。首次展开前由加载方置为 1, 之后自增。
    /// </summary>
    public int RepliesNextPage { get; set; } = 1;

    /// <summary>还有没取到的回复 → 显示"展开 N 条回复"</summary>
    public bool HasMoreReplies => ReplyCount > Replies.Count;

    /// <summary>
    /// 有没有楼中楼 → 决定"查看详情"入口显不出来。
    /// 没有楼中楼的评论详情页就是它自己, 没有可看的内容, 入口不显示。
    /// </summary>
    public bool HasReplies => ReplyCount > 0;

    public string ReplyMoreText => ReplyCount > Replies.Count
        ? $"展开 {ReplyCount - Replies.Count} 条回复"
        : "";

    private bool _loadingReplies;

    public bool IsLoadingReplies
    {
        get => _loadingReplies;
        set
        {
            if (_loadingReplies == value) return;
            _loadingReplies = value;
            Raise(nameof(IsLoadingReplies));
        }
    }

    /// <summary>
    /// 补齐回复后手动通知一次。
    /// ObservableCollection 的数量变化只会通知集合本身, 不会带上 HasMoreReplies / ReplyMoreText
    /// 这两个依赖 Count 的计算属性, 所以必须显式补一刀。
    /// </summary>
    public void NotifyRepliesChanged()
    {
        Raise(nameof(HasMoreReplies));
        Raise(nameof(ReplyMoreText));
        // 展开后"共 N 条回复"可能跟着变(用实际条数收口时), 一并通知
        Raise(nameof(ReplyBoxText));
    }

    /// <summary>
    /// 把 <see cref="PreviewReplies"/> 同步成 <see cref="Replies"/> 的前几条。
    /// 列表方框只展示预览, 详情页/自动补全只动 <see cref="Replies"/>;
    /// 发表回复后重新拉了第一页时, 用这个方法把预览也刷新一下。
    /// </summary>
    public void SyncPreviewReplies(int max = 3)
    {
        PreviewReplies.Clear();
        foreach (var r in Replies.Take(max)) PreviewReplies.Add(r);
    }
}

