using System;
using System.Threading.Tasks;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.ViewModels;

/// <summary>
/// 动态详情视图(右侧面板)的内容部分: 补全动态正文/图片 + 动态点赞/复制链接。
/// 评论由独立的 <see cref="CommentSectionViewModel"/> 管理(评论区逻辑与视频评论区一致)。
/// </summary>
public class DynamicDetailViewModel : ObservableObject
{
    private DynamicItem _item;
    public DynamicItem Item
    {
        get => _item;
        private set => SetProperty(ref _item, value);
    }

    private bool _loadingDetail;
    public bool LoadingDetail
    {
        get => _loadingDetail;
        private set => SetProperty(ref _loadingDetail, value);
    }

    private string _error = "";
    public string Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public ICommand CopyLinkCommand { get; }
    public ICommand LikeDynamicCommand { get; }

    public DynamicDetailViewModel(DynamicItem item)
    {
        _item = item;
        CopyLinkCommand = new RelayCommand(CopyLink);
        LikeDynamicCommand = new RelayCommand(() => _ = LikeDynamicAsync());
    }

    public async Task InitAsync()
    {
        if (string.IsNullOrEmpty(Item.Id)) return;
        LoadingDetail = true;
        try
        {
            // 专栏正文和动态详情可以并行: 卡片解析时就已经存了 ArticleId。
            var articleTask = Item.IsArticle
                ? Svc.Api.GetArticleContentAsync(Item.ArticleId)
                : null;

            var (ok, err, detail) = await Svc.Api.GetDynamicDetailAsync(Item.Id);
            if (!ok || detail == null)
            {
                Error = err ?? "";
                return;
            }

            // 详情接口会把专栏的 major 换成 OPUS, ArticleId 由解析器从 type/basic 补回。
            // 这里把正文文字和全部图片补上, 否则详情窗永远只有那一张封面。
            if (detail.IsArticle)
            {
                var (aok, aerr, article) = articleTask != null
                    ? await articleTask
                    : await Svc.Api.GetArticleContentAsync(detail.ArticleId);
                if (aok) ApplyArticle(detail, article);
                else if (Error.Length == 0 && !string.IsNullOrEmpty(aerr)) Error = aerr;
            }
            else if (articleTask != null)
            {
                // 卡片认成专栏、详情没认出来: 也把正文补上, 避免退回到"只显示封面"
                var (aok, aerr, article) = await articleTask;
                if (aok && article != null)
                {
                    detail.Kind = DynamicKind.Article;
                    detail.ArticleId = Item.ArticleId;
                    ApplyArticle(detail, article);
                }
                else if (Error.Length == 0 && !string.IsNullOrEmpty(aerr))
                {
                    Error = aerr;
                }
            }

            // 转发原文是专栏时也补全(转发不常见, 顺序请求即可)
            if (detail.Orig is { IsArticle: true } orig)
            {
                var (_, _, origArticle) = await Svc.Api.GetArticleContentAsync(orig.ArticleId);
                ApplyArticle(orig, origArticle);
            }

            // 保留本地已点赞状态(详情接口也会给, 但以本地最新操作为准)
            var liked = Item.IsLiked;
            var likeCount = Item.LikeCount;
            Item = detail;
            if (Item.HasStat && liked && !Item.IsLiked)
            {
                Item.IsLiked = true;
                Item.LikeCount = Math.Max(Item.LikeCount, likeCount);
            }
        }
        finally
        {
            LoadingDetail = false;
        }
    }

    /// <summary>
    /// 把专栏正文套到动态上: 正文换成文章原文(不再带 `[图片]` 占位符),
    /// 图片列表换成正文里的全部图片(原来那张封面会被替换)。
    /// </summary>
    private static void ApplyArticle(DynamicItem item, DynamicArticle? article)
    {
        if (article == null) return;
        if (article.HasText) item.ContentText = article.Text;
        if (article.Images.Count == 0) return;
        item.Images.Clear();
        item.Images.AddRange(article.Images);
    }

    private void CopyLink()
    {
        var url = Item.DynamicUrl;
        if (string.IsNullOrEmpty(url))
        {
            Svc.Toast.Show("这条动态没有可复制的链接");
            return;
        }
        try
        {
            System.Windows.Clipboard.SetText(url);
            Svc.Toast.Show("动态链接已复制");
        }
        catch
        {
            Svc.Toast.Show("复制失败, 请重试");
        }
    }

    public async Task<bool> LikeDynamicAsync()
    {
        if (Item.LikeBusy || string.IsNullOrEmpty(Item.Id)) return false;
        if (!Svc.Session.HasLogin)
        {
            Svc.Toast.Show("请先登录后再点赞");
            return false;
        }
        var target = !Item.IsLiked;
        var oldCount = Item.LikeCount;
        var oldLiked = Item.IsLiked;
        Item.LikeBusy = true;
        Item.IsLiked = target;
        Item.LikeCount = Math.Max(0, oldCount + (target ? 1 : -1));
        try
        {
            var (ok, err) = await Svc.Api.LikeDynamicAsync(Item.Id, target);
            if (!ok)
            {
                Item.IsLiked = oldLiked;
                Item.LikeCount = oldCount;
                Svc.Toast.Show(err ?? "点赞失败");
            }
            return ok;
        }
        finally
        {
            Item.LikeBusy = false;
        }
    }
}