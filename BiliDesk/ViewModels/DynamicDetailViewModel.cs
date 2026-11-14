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
            var (ok, err, detail) = await Svc.Api.GetDynamicDetailAsync(Item.Id);
            if (!ok || detail == null)
            {
                Error = err ?? "";
                return;
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