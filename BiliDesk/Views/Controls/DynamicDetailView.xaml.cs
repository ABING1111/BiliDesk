using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 右侧动态详情面板的内容部分: 完整正文/图片/视频 + 操作栏 + 评论区。
/// 由 MainWindow 的遮罩面板承载, 不再单独开窗口。
/// </summary>
public partial class DynamicDetailView : UserControl
{
    private DynamicDetailViewModel? _vm;
    private bool _focusComments;
    private bool _loadedOnce;

    private bool _composerOpen;
    private bool _composerExpanded;
    private int _composerAnimGen;

    private const double ComposerNormalHeight = 80;
    private const double ComposerExpandedHeight = 220;

    public DynamicDetailView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateVideoHeight();
        Comments.ComposerRequested += (_, _) => OpenComposer();
        ApplyComposerSize();
    }

    /// <summary>评论输入面板是否展开(Esc 先收面板, 再关详情)。</summary>
    public bool IsComposerOpen => _composerOpen;

    public void SetItem(DynamicItem item, bool focusComments)
    {
        _vm = new DynamicDetailViewModel(item);
        DataContext = _vm;
        _focusComments = focusComments;
        _loadedOnce = false;
        Comments.ResetDraft();
        ResetComposer();
        UpdateVideoHeight();
        _ = InitAsync();
    }

    private async System.Threading.Tasks.Task InitAsync()
    {
        var vm = _vm;
        var focus = _focusComments;
        if (vm == null || _loadedOnce) return;
        _loadedOnce = true;
        UpdateVideoHeight();
        await vm.InitAsync();
        if (!ReferenceEquals(vm, _vm)) return;

        // 用详情接口补全后的 basic.comment_id_str / comment_type 作为评论目标
        Comments.SetTarget(vm.Item.CommentOid, vm.Item.CommentType, vm.Item.AuthorMid);
        if (focus) Comments.ScrollToComments();
    }

    private void UpdateVideoHeight()
    {
        if (DetailVideoHost == null) return;
        var w = DetailVideoHost.ActualWidth;
        if (w <= 0) return;
        DetailVideoHost.Height = Math.Min(w * 9 / 16, 420);
    }

    private void OnAuthorClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null || _vm.Item.AuthorMid <= 0) return;
        new NavigationDispatcher(Window.GetWindow(this)).OpenUserSpace(_vm.Item.AuthorMid.ToString());
    }

    private void OnVideoClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null) return;
        var bvid = _vm.Item.VideoBvid;
        if (string.IsNullOrEmpty(bvid)) return;
        Svc.Player.PlayVideo(bvid, _vm.Item.VideoTitle);
    }

    private void OnOrigVideoClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DynamicItem orig) return;
        if (string.IsNullOrEmpty(orig.Bvid)) return;
        Svc.Player.PlayVideo(orig.Bvid, orig.Title);
    }

    private void OnLiveClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null || _vm.Item.RoomId <= 0) return;
        Svc.Player.PlayLive(_vm.Item.RoomId, _vm.Item.Title);
    }

    private void OnScrollToCommentsClick(object sender, RoutedEventArgs e)
        => Comments.ScrollToComments();

    private async void OnLikeClick(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var ok = await _vm.LikeDynamicAsync();
        if (ok) LikeFeedback.PlayPop(LikeIconBox);
    }

    // ------------------------------------------------------------ 右下角评论 FAB / 输入面板

    private void OnCommentFabClick(object sender, RoutedEventArgs e)
    {
        if (!Svc.Session.HasLogin)
        {
            Svc.Toast.Show("登录后才能评论");
            return;
        }
        if (_composerOpen) CloseComposer();
        else OpenComposer();
    }

    public void OpenComposer()
    {
        if (_composerOpen) return;
        _composerOpen = true;
        ++_composerAnimGen;
        CommentFabIcon.Text = "\uE74A";   // ChevronDown: 收起
        CommentFab.ToolTip = "收起评论框";

        var panel = CommentComposerPanel;
        panel.Visibility = Visibility.Visible;
        panel.Opacity = 0;
        ApplyComposerSize();
        var translate = new TranslateTransform(0, 24);
        panel.RenderTransform = translate;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        panel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (_composerOpen) CommentDraft.Focus();
        }));
    }

    public void CloseComposer()
    {
        if (!_composerOpen) return;
        _composerOpen = false;
        var gen = ++_composerAnimGen;
        CommentFabIcon.Text = "\uE70F";   // Edit: 笔
        CommentFab.ToolTip = "写评论";

        var panel = CommentComposerPanel;
        var anim = new DoubleAnimation(panel.Opacity, 0, TimeSpan.FromMilliseconds(140))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        anim.Completed += (_, _) => FinishCloseComposer(gen, panel);
        panel.BeginAnimation(OpacityProperty, anim);
    }

    private void FinishCloseComposer(int gen, Border panel)
    {
        if (gen != _composerAnimGen || _composerOpen) return;
        panel.BeginAnimation(OpacityProperty, null);
        panel.Opacity = 1;   // 复位基准值, 下次从 0 淡入
        panel.Visibility = Visibility.Collapsed;
    }

    private void ResetComposer()
    {
        ++_composerAnimGen;
        _composerOpen = false;
        if (CommentComposerPanel == null) return;
        CommentComposerPanel.BeginAnimation(OpacityProperty, null);
        CommentComposerPanel.Opacity = 1;
        CommentComposerPanel.Visibility = Visibility.Collapsed;
        CommentFabIcon.Text = "\uE70F";
        CommentFab.ToolTip = "写评论";
    }

    private void OnPostCommentClick(object sender, RoutedEventArgs e)
        => _ = Comments.PostAsync();

    private void OnComposerExpandClick(object sender, RoutedEventArgs e)
    {
        _composerExpanded = !_composerExpanded;
        ApplyComposerSize();
        if (_composerOpen) CommentDraft.Focus();
    }

    /// <summary>输入框普通 80 / 展开 220, 与播放器评论输入面板同一套交互。</summary>
    private void ApplyComposerSize()
    {
        if (CommentDraft == null || BtnComposerExpand == null || ComposerExpandIcon == null) return;
        ComposerExpandIcon.Text = _composerExpanded ? "\uE70E" : "\uE70D";
        BtnComposerExpand.ToolTip = _composerExpanded
            ? "收回输入框"
            : "展开输入框, 写长评时能通读全文";
        CommentDraft.Height = _composerExpanded ? ComposerExpandedHeight : ComposerNormalHeight;
    }

    private void OnCancelReplyClick(object sender, RoutedEventArgs e)
        => Comments.CancelReply();

    private void OnComposerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            if (Comments.CanPost) _ = Comments.PostAsync();
        }
    }
}