using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BiliDesk.Models;

/// <summary>
/// 动态主体类型(按 B 站 feed/all 的 major.type / type 归一)。
/// 动态页要"视频 + 图文"综合显示, 所以不能再用 VideoItem 一个模型硬扛。
/// </summary>
public enum DynamicKind
{
    Unknown = 0,
    Video,
    ImageText,
    Text,
    Article,
    Live,
    Forward,
}

/// <summary>动态里的一张图(图文 / 专栏封面 / 转发原动态共用)。</summary>
public sealed class DynamicImage
{
    /// <summary>原图地址。点开大图预览 / 下载用这一份。</summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// 卡片缩略图地址(约 480 宽)。卡片一屏可能同时挂 9 张图,
    /// 用原图会把解码队列和内存一起打爆, 所以列表一律走缩略档。
    /// </summary>
    public string ThumbUrl { get; set; } = "";

    /// <summary>详情窗用的中档图(约 720 宽), 比卡片清晰、又不到原图体积。</summary>
    public string DetailUrl { get; set; } = "";

    /// <summary>原始像素宽高。接口缺失时为 0, 布局按方形兜底。</summary>
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>动态详情窗「转发」页里的一条转发记录。</summary>
public sealed class DynamicRepostItem
{
    public string Id { get; set; } = "";
    public long Mid { get; set; }
    public string Name { get; set; } = "";
    public string Face { get; set; } = "";
    public string Text { get; set; } = "";
    public string PubTime { get; set; } = "";
}

/// <summary>
/// 动态流里的一条综合动态(视频 / 图文 / 纯文字 / 专栏 / 转发)。
///
/// ★ 与 VideoItem 的关系:
///   · 动态卡绑这个模型 —— 它有正文、图片、转发原文、动态统计(转发/评论/点赞);
///   · 卡片墙仍然只要视频, 通过 <see cref="AsVideoItem"/> 转成现有 VideoItem 复用 VideoCard;
///   · 单 UP 视图来自空间投稿接口, 只有视频, 用 <see cref="FromVideo"/> 包一层即可。
/// </summary>
public class DynamicItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>动态 id(id_str)。图文/转发没有 bvid, 去重与详情都靠它。</summary>
    public string Id { get; set; } = "";

    /// <summary>接口原文类型(DYNAMIC_TYPE_AV / 数字 8 等), 仅供排查与兜底判断。</summary>
    public string TypeText { get; set; } = "";

    public DynamicKind Kind { get; set; } = DynamicKind.Unknown;

    // ------------------------------------------------------------ 作者与时间

    public long AuthorMid { get; set; }
    public string AuthorName { get; set; } = "";
    public string AuthorFace { get; set; } = "";

    /// <summary>Unix 秒。扁平路径只有展示文本时是反推的近似值。</summary>
    public long Pubdate { get; set; }

    /// <summary>接口排好版的发布时间原文("昨天 20:50" / "2小时前")。</summary>
    public string PubTimeLabel { get; set; } = "";

    /// <summary>Pubdate 是否由展示文本反推(悬停提示要加"约")。</summary>
    public bool PubDateApprox { get; set; }

    /// <summary>接口给的发布动作原文("投稿了视频")。没有时按 Kind 推导。</summary>
    public string PubAction { get; set; } = "";

    /// <summary>卡片头部直接显示的时间: 优先接口原文, 其次本地相对时间。</summary>
    public string PubText
    {
        get
        {
            if (!string.IsNullOrEmpty(PubTimeLabel)) return PubTimeLabel;
            if (Pubdate > 0) return VideoItem.FormatPubRelative(Pubdate);
            return "";
        }
    }

    public string PubFullText
        => Pubdate > 0
            ? (PubDateApprox ? "约 " : "") + VideoItem.FormatPubDateTimeFull(Pubdate)
            : PubTimeLabel;

    /// <summary>头部第二行的动作说明("投稿了视频" / "发布了图文" / "转发了动态")。</summary>
    public string ActionLabel
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(PubAction)) return PubAction.Trim();
            return Kind switch
            {
                DynamicKind.Video => "投稿了视频",
                DynamicKind.ImageText => "发布了图文",
                DynamicKind.Text => "发布了动态",
                DynamicKind.Article => "投稿了专栏",
                DynamicKind.Live => "正在直播",
                DynamicKind.Forward => "转发了动态",
                _ => "",
            };
        }
    }

    // ------------------------------------------------------------ 内容

    /// <summary>动态正文(desc.text / opus.summary.text)。视频动态通常为空。</summary>
    public string ContentText { get; set; } = "";

    /// <summary>视频标题 / 专栏标题 / 图文标题(可为空)。</summary>
    public string Title { get; set; } = "";

    /// <summary>视频封面 / 专栏封面 / 直播封面。</summary>
    public string Cover { get; set; } = "";

    /// <summary>视频时长展示串("06:12")。拿不到为空。</summary>
    public string Duration { get; set; } = "";

    public string Bvid { get; set; } = "";
    public long Aid { get; set; }
    public long RoomId { get; set; }
    public string AreaName { get; set; } = "";

    /// <summary>视频播放/弹幕数(动态统计栏之外的视频自身数据)。</summary>
    public long ViewCount { get; set; }
    public long DanmakuCount { get; set; }

    /// <summary>跳转地址(专栏/直播/图文网页版), 复制动态链接时也用它兜底。</summary>
    public string JumpUrl { get; set; } = "";

    /// <summary>图文/专栏图片。视频动态为空。</summary>
    public List<DynamicImage> Images { get; } = new();

    /// <summary>转发动态里的原动态。非转发为 null。</summary>
    public DynamicItem? Orig { get; set; }

    // ------------------------------------------------------------ 动态统计(转发/评论/点赞)

    public long ForwardCount { get; set; }
    public long CommentCount { get; set; }

    private long _likeCount;
    public long LikeCount
    {
        get => _likeCount;
        set
        {
            if (_likeCount == value) return;
            _likeCount = value;
            Raise();
            Raise(nameof(LikeCountText));
            Raise(nameof(LikeLabel));
        }
    }

    private bool _isLiked;
    public bool IsLiked
    {
        get => _isLiked;
        set
        {
            if (_isLiked == value) return;
            _isLiked = value;
            Raise();
        }
    }

    private bool _likeBusy;
    public bool LikeBusy
    {
        get => _likeBusy;
        set
        {
            if (_likeBusy == value) return;
            _likeBusy = value;
            Raise();
        }
    }

    /// <summary>接口是否给了统计模块。没给时底部数字一律留空, 不显示 0。</summary>
    public bool HasStat { get; set; }

    /// <summary>
    /// 评论区目标 oid(来自 basic.comment_id_str)。
    /// 视频动态的评论挂在视频 aid 上, 图文/文字动态挂在动态/相簿 id 上, 不能一律用动态 id。
    /// </summary>
    public long CommentOid { get; set; }

    /// <summary>评论区类型(来自 basic.comment_type: 1=视频, 11/17=图文/动态等)。</summary>
    public int CommentType { get; set; } = 17;

    public string ForwardCountText => HasStat && ForwardCount > 0 ? VideoItem.FormatCount(ForwardCount) : "";
    public string CommentCountText => HasStat && CommentCount > 0 ? VideoItem.FormatCount(CommentCount) : "";
    public string LikeCountText => HasStat && LikeCount > 0 ? VideoItem.FormatCount(LikeCount) : "";

    /// <summary>底部按钮文字: 有计数显示计数, 没有就显示动作名(与 B 站一致)。</summary>
    public string ForwardLabel => HasStat && ForwardCount > 0 ? VideoItem.FormatCount(ForwardCount) : "转发";
    public string CommentLabel => HasStat && CommentCount > 0 ? VideoItem.FormatCount(CommentCount) : "评论";
    public string LikeLabel => HasStat && LikeCount > 0 ? VideoItem.FormatCount(LikeCount) : "赞";

    // ------------------------------------------------------------ 派生判断

    public bool HasText => !string.IsNullOrWhiteSpace(ContentText);
    public bool HasImages => Images.Count > 0;
    public bool HasTitle => !string.IsNullOrWhiteSpace(Title);

    /// <summary>直播动态(左栏已有直播看板, 这里只给一张可点的直播卡)。</summary>
    public bool IsLive => Kind == DynamicKind.Live && RoomId > 0;

    /// <summary>专栏标题行(视频标题在视频块里单独显示, 不重复)。</summary>
    public bool ShowArticleTitle => Kind == DynamicKind.Article && HasTitle;

    /// <summary>本条自身是不是可播视频。</summary>
    public bool HasPlayableVideo => Kind == DynamicKind.Video && !string.IsNullOrEmpty(Bvid);

    /// <summary>转发里的原动态是不是可播视频。</summary>
    public bool HasForwardVideo => Orig?.HasPlayableVideo == true;

    /// <summary>这条动态(含转发原文)能不能拿出一个视频给卡片墙/播放器。</summary>
    public bool HasAnyVideo => HasPlayableVideo || HasForwardVideo;

    public bool IsForward => Kind == DynamicKind.Forward || Orig != null;

    /// <summary>动态网页版链接。优先用 id 拼, 接口给了 jump_url 也认。</summary>
    public string DynamicUrl
    {
        get
        {
            if (!string.IsNullOrEmpty(Id)) return "https://t.bilibili.com/" + Id;
            return UrlUtil.Normalize(JumpUrl);
        }
    }

    /// <summary>视频标题(转发时取原文标题), 卡片标题行用。</summary>
    public string VideoTitle
        => HasPlayableVideo ? Title : (Orig?.Title ?? Title);

    /// <summary>视频封面(转发时取原文封面)。</summary>
    public string VideoCover
        => HasPlayableVideo ? Cover : (Orig?.Cover ?? Cover);

    public string VideoDuration
        => HasPlayableVideo ? Duration : (Orig?.Duration ?? Duration);

    /// <summary>视频 bvid(转发时取原文 bvid)。</summary>
    public string VideoBvid
        => HasPlayableVideo ? Bvid : (Orig?.Bvid ?? "");

    // ------------------------------------------------------------ 与 VideoItem 互转

    private VideoItem? _videoItem;

    /// <summary>
    /// 给卡片墙/播放器用的视频视图。同一条动态只建一次,
    /// 保证 FollowViewModel.SyncWallItems 的"首尾引用比对"稳定。
    /// </summary>
    public VideoItem? AsVideoItem()
    {
        if (!HasAnyVideo) return null;
        if (_videoItem != null) return _videoItem;

        var src = HasPlayableVideo ? this : Orig!;
        _videoItem = new VideoItem
        {
            Bvid = src.Bvid,
            Aid = src.Aid,
            Title = src.Title,
            Cover = UrlUtil.Normalize(src.Cover),
            Author = string.IsNullOrEmpty(src.AuthorName) ? AuthorName : src.AuthorName,
            AuthorFace = UrlUtil.Normalize(string.IsNullOrEmpty(src.AuthorFace) ? AuthorFace : src.AuthorFace),
            OwnerMid = src.AuthorMid > 0 ? src.AuthorMid : AuthorMid,
            Duration = src.Duration,
            ViewCount = src.ViewCount,
            DanmakuCount = src.DanmakuCount,
            Pubdate = Pubdate > 0 ? Pubdate : src.Pubdate,
            PubTimeLabel = PubTimeLabel,
            PubDateApprox = PubDateApprox,
            RoomId = src.RoomId,
            AreaName = src.AreaName,
        };
        return _videoItem;
    }

    /// <summary>单 UP 视图(空间投稿接口)只有视频, 包成 DynamicItem 以复用同一张动态卡。</summary>
    public static DynamicItem FromVideo(VideoItem v)
    {
        return new DynamicItem
        {
            Id = string.IsNullOrEmpty(v.Bvid) ? "" : "video_" + v.Bvid,
            TypeText = "DYNAMIC_TYPE_AV",
            Kind = DynamicKind.Video,
            AuthorMid = v.OwnerMid,
            AuthorName = v.Author,
            AuthorFace = v.AuthorFace,
            Pubdate = v.Pubdate,
            PubTimeLabel = v.PubTimeLabel,
            PubDateApprox = v.PubDateApprox,
            Title = v.Title,
            Cover = v.Cover,
            Duration = v.Duration,
            Bvid = v.Bvid,
            Aid = v.Aid,
            ViewCount = v.ViewCount,
            DanmakuCount = v.DanmakuCount,
            RoomId = v.RoomId,
            AreaName = v.AreaName,
            PubAction = v.IsLive ? "正在直播" : "投稿了视频",
            CommentOid = v.Aid,
            CommentType = 1,
        };
    }
}