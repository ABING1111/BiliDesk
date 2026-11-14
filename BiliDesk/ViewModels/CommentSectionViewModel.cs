using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.ViewModels;

/// <summary>
/// 动态详情里的评论区(逻辑与视频评论区一致):
/// 列表态(排序/分页/点赞/点踩/回复) + 评论详情态(根评论 + 全部楼中楼)。
///
/// ★ 为什么单独一个 VM 而不是塞进 DynamicDetailViewModel:
///   评论的目标是 basic.comment_id_str / comment_type(视频动态是 aid+type1,
///   图文动态是动态/相簿 id), 与动态本身的生命周期/点赞是两件事。
///   分开后 DynamicDetailView 只负责内容, 评论控件自己管分页与线程详情。
/// </summary>
public class CommentSectionViewModel : ObservableObject
{
    public CommentSectionViewModel()
    {
        // 面板打开期间用户去登录/退出, 输入框可用状态要跟着变
        Svc.Session.Changed += OnSessionChanged;
    }

    private void OnSessionChanged()
    {
        OnPropertyChanged(nameof(IsNotLoggedIn));
        OnPropertyChanged(nameof(CanPost));
    }

    private long _oid;
    private int _commentType = 17;
    private long _upperMid;

    public ObservableCollection<CommentItem> Comments { get; } = new();

    // ------------------------------------------------------------ 列表状态

    private bool _loadingComments;
    public bool LoadingComments
    {
        get => _loadingComments;
        private set => SetProperty(ref _loadingComments, value);
    }

    private bool _loadingMore;
    public bool LoadingMore
    {
        get => _loadingMore;
        private set => SetProperty(ref _loadingMore, value);
    }

    private bool _hasMore;
    public bool HasMore
    {
        get => _hasMore;
        private set => SetProperty(ref _hasMore, value);
    }

    private string _error = "";
    public string Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    private long _total;
    public long Total
    {
        get => _total;
        private set
        {
            if (SetProperty(ref _total, value))
                OnPropertyChanged(nameof(CommentCountText));
        }
    }

    public string CommentCountText
        => Total > 0 ? VideoItem.FormatCount(Total) : "";

    private int _sortMode = 3;
    public int SortMode
    {
        get => _sortMode;
        private set
        {
            if (SetProperty(ref _sortMode, value))
            {
                OnPropertyChanged(nameof(IsHotSort));
                OnPropertyChanged(nameof(IsTimeSort));
            }
        }
    }

    public bool IsHotSort => SortMode == 3;
    public bool IsTimeSort => SortMode == 2;

    private long _nextCursor;
    private int _generation;

    // ------------------------------------------------------------ 详情态

    private CommentItem? _detailRoot;
    public CommentItem? DetailRoot
    {
        get => _detailRoot;
        private set
        {
            if (!SetProperty(ref _detailRoot, value)) return;
            OnPropertyChanged(nameof(IsDetailMode));
            OnPropertyChanged(nameof(IsListMode));
            OnPropertyChanged(nameof(DetailHeaderText));
            OnPropertyChanged(nameof(HasMoreReplies));
        }
    }

    public bool IsDetailMode => DetailRoot != null;
    public bool IsListMode => DetailRoot == null;

    public string DetailHeaderText
        => DetailRoot == null ? "" : $"相关回复共 {DetailRoot.ReplyCount} 条";

    public bool HasMoreReplies => DetailRoot?.HasMoreReplies == true;

    private bool _loadingReplies;
    public bool LoadingReplies
    {
        get => _loadingReplies;
        private set => SetProperty(ref _loadingReplies, value);
    }

    // ------------------------------------------------------------ 输入框

    private string _composerText = "";
    public string ComposerText
    {
        get => _composerText;
        set
        {
            if (!SetProperty(ref _composerText, value)) return;
            OnPropertyChanged(nameof(CanPost));
        }
    }

    private bool _isPosting;
    public bool IsPosting
    {
        get => _isPosting;
        private set
        {
            if (!SetProperty(ref _isPosting, value)) return;
            OnPropertyChanged(nameof(CanPost));
            OnPropertyChanged(nameof(PostButtonText));
        }
    }

    public string PostButtonText => IsPosting ? "发布中…" : "发布";

    private CommentItem? _replyTarget;
    public CommentItem? ReplyTarget
    {
        get => _replyTarget;
        set
        {
            if (!SetProperty(ref _replyTarget, value)) return;
            OnPropertyChanged(nameof(IsReplying));
            OnPropertyChanged(nameof(ComposerHint));
        }
    }

    public bool IsReplying => ReplyTarget != null;

    public string ComposerHint
        => ReplyTarget == null ? "发一条友善的评论" : $"回复 @{ReplyTarget.UserName}";

    public bool IsNotLoggedIn => !Svc.Session.HasLogin;

    public bool CanPost
        => _oid > 0 && Svc.Session.HasLogin && !IsPosting && !string.IsNullOrWhiteSpace(ComposerText);

    // ------------------------------------------------------------ 目标

    public void SetTarget(long oid, int commentType, long upperMid)
    {
        _oid = oid;
        _commentType = commentType > 0 ? commentType : 17;
        _upperMid = upperMid;
        _generation++;
        _nextCursor = 0;
        HasMore = false;
        Error = "";
        Total = 0;
        Comments.Clear();
        ComposerText = "";
        CloseThread();
        OnPropertyChanged(nameof(CanPost));
        OnPropertyChanged(nameof(IsNotLoggedIn));
    }

    // ------------------------------------------------------------ 列表加载

    public async Task ReloadAsync() => await LoadCommentsAsync(true);

    public async Task LoadCommentsAsync(bool reset)
    {
        if (_oid <= 0)
        {
            Error = "没拿到评论区 id, 暂时不能看评论";
            HasMore = false;
            return;
        }

        if (reset)
        {
            _generation++;
            _nextCursor = 0;
            HasMore = false;
            Error = "";
            Total = 0;
            LoadingComments = true;
            Comments.Clear();
        }
        else
        {
            if (LoadingMore || !HasMore) return;
            LoadingMore = true;
        }

        var gen = _generation;
        try
        {
            var (ok, err, items, total, next, isEnd) = await Svc.Api.GetCommentsAsync(
                _oid, 20, SortMode, reset ? 0 : _nextCursor,
                type: _commentType, upperMid: _upperMid);

            if (gen != _generation) return;
            LoadingComments = false;
            LoadingMore = false;

            if (!ok)
            {
                Error = err ?? "评论加载失败";
                if (reset) HasMore = false;
                return;
            }

            Error = "";
            if (total > 0) Total = total;
            foreach (var c in items) Comments.Add(c);
            _nextCursor = next;
            HasMore = !isEnd && next > 0;
            if (Total <= 0) Total = Comments.Count;
        }
        catch (Exception ex)
        {
            if (gen != _generation) return;
            LoadingComments = false;
            LoadingMore = false;
            Error = ex.Message;
        }
    }

    public async Task LoadMoreAsync() => await LoadCommentsAsync(false);

    public async Task SwitchSortAsync(int mode)
    {
        if (SortMode == mode) return;
        SortMode = mode;
        if (IsDetailMode) SortRepliesInPlace(DetailRoot!);
        else await LoadCommentsAsync(true);
    }

    // ------------------------------------------------------------ 评论详情(楼中楼)

    public async Task OpenThreadAsync(CommentItem root)
    {
        if (root == null || root.Rpid <= 0) return;
        DetailRoot = root;
        ReplyTarget = root;
        SortRepliesInPlace(root);
        OnPropertyChanged(nameof(DetailHeaderText));

        // 视频评论区进入详情时会自动补全这条线程(最多 3 页), 这里保持一致
        if (root.HasMoreReplies)
        {
            LoadingReplies = true;
            try { await AutoCompleteThreadRepliesAsync(root, reset: false); }
            finally { LoadingReplies = false; }
        }

        SortRepliesInPlace(root);
        root.NotifyRepliesChanged();
        OnPropertyChanged(nameof(HasMoreReplies));
        OnPropertyChanged(nameof(DetailHeaderText));
    }

    /// <summary>点某条评论的"回复": 在线程根不是它时先切到对应线程, 再把输入框指向它。</summary>
    public async Task ReplyToAsync(CommentItem target)
    {
        if (target == null) return;
        // 先同步切到"回复 @目标", 外层浮动输入面板会立刻拿到正确的提示文案
        ReplyTarget = target;
        var root = DetailRoot != null && DetailRoot.Replies.Contains(target)
            ? DetailRoot
            : target;
        if (!ReferenceEquals(root, DetailRoot))
            await OpenThreadAsync(root);
        ReplyTarget = target;
    }

    public void CloseThread()
    {
        if (DetailRoot == null) return;
        DetailRoot.TrimRepliesToPreview();
        DetailRoot = null;
        ReplyTarget = null;
        OnPropertyChanged(nameof(HasMoreReplies));
    }

    private async Task AutoCompleteThreadRepliesAsync(CommentItem root, bool reset)
    {
        try
        {
            if (reset) root.RepliesNextPage = 1;
            var guard = 0;
            var firstPage = reset;
            while ((firstPage || root.HasMoreReplies) && guard++ < 3)
            {
                var (ok, _, more) = await Svc.Api.GetReplyRepliesAsync(
                    _oid, root.Rpid, pn: root.RepliesNextPage, ps: 20,
                    type: _commentType, upperMid: _upperMid);
                if (!ok) break;
                if (!ReferenceEquals(DetailRoot, root)) return;

                if (more.Count == 0)
                {
                    root.ReplyCount = root.Replies.Count;
                    break;
                }

                // 发帖后的刷新: 先拿到非空的第一页再清掉旧列表, 网络失败/服务端延迟时不会把已有回复清空
                if (firstPage)
                {
                    root.Replies.Clear();
                    firstPage = false;
                }

                root.RepliesNextPage++;
                var seen = new HashSet<long>(root.Replies.Select(x => x.Rpid));
                var added = 0;
                foreach (var r in more)
                    if (r.Rpid > 0 && seen.Add(r.Rpid)) { root.Replies.Add(r); added++; }
                if (added == 0) break;

                // 让出一帧: 20 条评论模板的实例化/排版会占满 UI 线程
                await System.Windows.Threading.Dispatcher.Yield(
                    System.Windows.Threading.DispatcherPriority.Background);
            }
            if (root.ReplyCount < root.Replies.Count) root.ReplyCount = root.Replies.Count;
            root.SyncPreviewReplies();
            root.NotifyRepliesChanged();
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
        }
    }

    public async Task LoadMoreRepliesAsync()
    {
        var root = DetailRoot;
        if (root == null || LoadingReplies) return;
        LoadingReplies = true;
        try
        {
            var (ok, err, more) = await Svc.Api.GetReplyRepliesAsync(
                _oid, root.Rpid, pn: root.RepliesNextPage, ps: 20,
                type: _commentType, upperMid: _upperMid);
            if (!ReferenceEquals(DetailRoot, root)) return;
            if (!ok)
            {
                if (!string.IsNullOrEmpty(err)) Svc.Toast.Show(err);
                return;
            }
            if (more.Count == 0)
            {
                root.ReplyCount = root.Replies.Count;
                root.NotifyRepliesChanged();
                OnPropertyChanged(nameof(HasMoreReplies));
                OnPropertyChanged(nameof(DetailHeaderText));
                return;
            }
            var seen = new HashSet<long>(root.Replies.Select(x => x.Rpid));
            foreach (var r in more)
                if (r.Rpid > 0 && seen.Add(r.Rpid)) root.Replies.Add(r);
            root.RepliesNextPage++;
            if (root.ReplyCount < root.Replies.Count) root.ReplyCount = root.Replies.Count;
            SortRepliesInPlace(root);
            root.NotifyRepliesChanged();
            OnPropertyChanged(nameof(HasMoreReplies));
            OnPropertyChanged(nameof(DetailHeaderText));
        }
        finally
        {
            LoadingReplies = false;
        }
    }

    private void SortRepliesInPlace(CommentItem root)
    {
        var sorted = SortMode == 3
            ? root.Replies.OrderByDescending(r => r.LikeCount).ThenByDescending(r => r.Ctime).ToList()
            : root.Replies.OrderBy(r => r.Ctime).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var cur = root.Replies.IndexOf(sorted[i]);
            if (cur >= 0 && cur != i) root.Replies.Move(cur, i);
        }
    }

    // ------------------------------------------------------------ 发评论 / 回复

    public async Task PostAsync()
    {
        if (!CanPost) return;
        var text = ComposerText.Trim();
        if (text.Length == 0) return;

        long root = 0, parent = 0;
        if (DetailRoot != null)
        {
            root = DetailRoot.Rpid;
            parent = ReplyTarget?.Rpid ?? root;
        }
        else if (ReplyTarget != null)
        {
            root = ReplyTarget.Rpid;
            parent = ReplyTarget.Rpid;
        }

        IsPosting = true;
        try
        {
            var (ok, err) = await Svc.Api.PostCommentAsync(
                _oid, text, root, parent, type: _commentType,
                referer: _commentType == 1 ? "https://www.bilibili.com/" : "https://t.bilibili.com/");
            if (!ok)
            {
                Error = err ?? "评论发布失败";
                Svc.Toast.Show(err ?? "评论发布失败");
                return;
            }

            ComposerText = "";
            Error = "";
            Svc.Toast.Show("评论已发布");

            if (DetailRoot != null)
            {
                await AutoCompleteThreadRepliesAsync(DetailRoot, reset: true);
                SortRepliesInPlace(DetailRoot);
                OnPropertyChanged(nameof(HasMoreReplies));
                OnPropertyChanged(nameof(DetailHeaderText));
            }
            else
            {
                ReplyTarget = null;
                await LoadCommentsAsync(true);
            }
        }
        finally
        {
            IsPosting = false;
        }
    }

    // ------------------------------------------------------------ 评论点赞 / 点踩

    public async Task LikeCommentAsync(CommentItem c, bool like)
    {
        if (c == null || c.Rpid <= 0 || c.LikeBusy) return;
        if (!Svc.Session.HasLogin)
        {
            Svc.Toast.Show("请先登录后再点赞");
            return;
        }

        var oldLiked = c.IsLiked;
        var oldDisliked = c.IsDisliked;
        var oldCount = c.LikeCount;
        c.LikeBusy = true;
        c.IsLiked = like;
        c.LikeCount = Math.Max(0, oldCount + (like ? 1 : -1));
        if (like) c.IsDisliked = false;
        try
        {
            var (ok, err) = await Svc.Api.CommentLikeAsync(
                _oid, c.Rpid, like, type: _commentType);
            if (!ok)
            {
                c.IsLiked = oldLiked;
                c.IsDisliked = oldDisliked;
                c.LikeCount = oldCount;
                Svc.Toast.Show(err ?? "点赞失败");
            }
        }
        finally
        {
            c.LikeBusy = false;
        }
    }

    public async Task DislikeCommentAsync(CommentItem c, bool dislike)
    {
        if (c == null || c.Rpid <= 0 || c.LikeBusy) return;
        if (!Svc.Session.HasLogin)
        {
            Svc.Toast.Show("请先登录后再操作");
            return;
        }

        var wasLiked = c.IsLiked;
        var oldCount = c.LikeCount;
        c.LikeBusy = true;
        c.IsDisliked = dislike;
        if (dislike && wasLiked)
        {
            c.IsLiked = false;
            c.LikeCount = Math.Max(0, oldCount - 1);
        }
        try
        {
            var (ok, err) = await Svc.Api.CommentDislikeAsync(
                _oid, c.Rpid, dislike, type: _commentType);
            if (!ok)
            {
                c.IsDisliked = !dislike;
                if (dislike && wasLiked)
                {
                    c.IsLiked = true;
                    c.LikeCount = oldCount;
                }
                Svc.Toast.Show(err ?? "点踩失败");
            }
        }
        finally
        {
            c.LikeBusy = false;
        }
    }
}