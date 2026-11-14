using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 动态详情的评论区。逻辑与视频评论区一致:
///   · 列表态: 最热/最新排序、游标分页、点赞/点踩/回复;
///   · 点楼中楼预览框 → 评论详情态(根评论 + 全部回复 + 返回按钮);
///   · 进入详情自动补全回复(最多 3 页), 失败不阻塞已加载内容;
///   · 发布评论/回复走同一个输入框。
/// </summary>
public partial class CommentSection : UserControl
{
    private readonly CommentSectionViewModel _vm = new();

    /// <summary>用户点了某条评论的"回复"或评论入口: 请外层打开右下角浮动输入面板。</summary>
    public event EventHandler? ComposerRequested;

    public CommentSection()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CommentSectionViewModel.IsHotSort)
                or nameof(CommentSectionViewModel.IsTimeSort)
                or nameof(CommentSectionViewModel.IsDetailMode)
                or nameof(CommentSectionViewModel.IsListMode))
                UpdateSortTags();
        };
        UpdateSortTags();
    }

    public bool CanPost => _vm.CanPost;

    public Task PostAsync() => _vm.PostAsync();

    public void CancelReply() => _vm.ReplyTarget = null;

    public void ResetDraft()
    {
        _vm.ComposerText = "";
        _vm.ReplyTarget = null;
    }

    public void SetReplyTarget(CommentItem? target) => _vm.ReplyTarget = target;

    /// <summary>由详情视图在拿到 basic.comment_id_str / comment_type 后调用。</summary>
    public void SetTarget(long oid, int commentType, long upperMid)
    {
        _vm.SetTarget(oid, commentType, upperMid);
        _ = _vm.ReloadAsync();
    }

    /// <summary>用户点了"评论"入口: 滚到评论区(输入由右下角 FAB 负责)。</summary>
    public void ScrollToComments() => this.BringIntoView();

    private void UpdateSortTags()
    {
        SortHot.Tag = _vm.IsHotSort ? "on" : "off";
        SortTime.Tag = _vm.IsTimeSort ? "on" : "off";
    }

    // ------------------------------------------------------------ 排序 / 分页

    private void OnSortHotClick(object sender, RoutedEventArgs e)
    {
        _ = _vm.SwitchSortAsync(3);
        UpdateSortTags();
    }

    private void OnSortTimeClick(object sender, RoutedEventArgs e)
    {
        _ = _vm.SwitchSortAsync(2);
        UpdateSortTags();
    }

    private void OnLoadMoreCommentsClick(object sender, RoutedEventArgs e)
        => _ = _vm.LoadMoreAsync();

    private void OnLoadMoreRepliesClick(object sender, RoutedEventArgs e)
        => _ = _vm.LoadMoreRepliesAsync();

    // ------------------------------------------------------------ 楼中楼详情

    private void OpenThreadAndScroll(CommentItem root)
    {
        _ = _vm.OpenThreadAsync(root);
        Dispatcher.BeginInvoke(new Action(() => BackButton.BringIntoView()),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnReplyBoxClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CommentItem c) return;
        // ★ 点在预览里的链接上: 那是"打开链接", 不是"看这条评论的全部回复"。
        if (VisualTreeUtil.FindAncestor<System.Windows.Documents.Hyperlink>(
                e.OriginalSource as DependencyObject) != null)
            return;
        // ★ 拖选正文后松手不算"点框": 预览里的 CommentText 是 RichTextBox, 可以拖选,
        //   松手时也会走到这里。按下/松开的距离超过阈值就当"选完了", 不跳详情。
        if (_replyBoxPressPoint is { } down && e.GetPosition(this) is var up
            && (Math.Abs(up.X - down.X) > ReplyBoxClickTolerance
                || Math.Abs(up.Y - down.Y) > ReplyBoxClickTolerance))
            return;
        OpenThreadAndScroll(c);
        e.Handled = true;
    }

    /// <summary>楼中楼预览框里"按下 → 松开"位移超过它就算拖选, 不当点击。</summary>
    private const double ReplyBoxClickTolerance = 4;

    private Point? _replyBoxPressPoint;

    private void OnReplyBoxPress(object sender, MouseButtonEventArgs e)
    {
        _replyBoxPressPoint = e.GetPosition(this);
    }

    private void OnBackToCommentsClick(object sender, RoutedEventArgs e)
    {
        _vm.CloseThread();
        this.BringIntoView();
    }

    private void OnCommentReplyClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CommentItem c) return;
        if (!Svc.Session.HasLogin)
        {
            Svc.Toast.Show("请先登录后再回复");
            return;
        }
        _ = _vm.ReplyToAsync(c);
        ComposerRequested?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------ 评论条目操作

    private void OnCommentAvatarClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CommentItem c || c.Mid <= 0) return;
        new NavigationDispatcher(Window.GetWindow(this)).OpenUserSpace(c.Mid.ToString());
    }

    private void OnCommentPictureClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CommentPicture pic) return;
        MediaActions.ShowImage(pic.Url, "评论图片", Window.GetWindow(this));
    }

    private void OnCommentLikeClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CommentItem c) return;
        _ = _vm.LikeCommentAsync(c, !c.IsLiked);
    }

    private void OnCommentDislikeClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CommentItem c) return;
        _ = _vm.DislikeCommentAsync(c, !c.IsDisliked);
    }
}