using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace BiliDesk.Views;

public partial class PlayerWindow : FluentWindow
{
    // ------------------------------------------------------------ 头像跳转 / 踩 / 回复 / 详情页发表

    /// <summary>
    /// 点评论头像 → 进 TA 的个人主页。
    /// 走的是和"播放器里点 UP 主头像"完全相同的一条路(NavigationDispatcher):
    /// 在主窗口内容区盖一层带返回按钮的主页, 而不是另开独立窗口。
    /// </summary>
    private void OnCommentAvatarClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_closing || (sender as FrameworkElement)?.DataContext is not CommentItem item) return;
        if (item.Mid <= 0)
        {
            // 极少数情况接口没给 mid(已注销账号等), 给个明确反馈而不是"点了没反应"
            Svc.Toast.Show("没拿到 TA 的 UID, 打不开主页");
            return;
        }
        new NavigationDispatcher(this).OpenUserSpace(item.Mid.ToString());
    }

    /// <summary>
    /// 点评论缩略图 → 看大图(2026-10-31 评论图片功能)。
    /// DataContext 是 <see cref="CommentPicture"/>(模板直接绑图片项), 原图地址就在 Url 里。
    ///
    /// ★ 2026-11-14: 改成把**整条评论的图**一起交给预览窗, 打开后能左右翻。
    ///   图片 Border 的 DataContext 是图片项, "这条评论"在包住它的 ItemsControl 上
    ///   (CommentPicturesTemplate 里是 {Binding Pictures}) —— 沿可视树往上找即可。
    /// </summary>
    private void OnCommentPictureClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_closing || (sender as FrameworkElement)?.DataContext is not CommentPicture pic) return;

        var list = VisualTreeUtil.FindAncestor<ItemsControl>(
            sender as DependencyObject, ic => ic.DataContext is CommentItem);
        if (list?.DataContext is CommentItem c && c.Pictures.Count > 0)
        {
            var urls = new List<string>(c.Pictures.Count);
            foreach (var p in c.Pictures) urls.Add(p.Url);
            MediaActions.ShowImage(urls, c.Pictures.IndexOf(pic), title: "评论图片", owner: this);
            return;
        }

        MediaActions.ShowImage(pic.Url, title: "评论图片", owner: this);
    }

    /// <summary>
    /// 点"回复" → 打开详情页并把输入框切到"回复 @某人"模式。
    ///
    /// 线程根要分清两种情况: 在详情页里点某条回复 → 根是详情页的线程根;
    /// 在列表里点顶层评论 → 根就是它自己(发出去的是给楼主的第一层回复)。
    /// </summary>
    private void OnCommentReplyClick(object sender, RoutedEventArgs e)
    {
        if (_closing || (sender as FrameworkElement)?.DataContext is not CommentItem item) return;
        if (!Svc.Session.HasLogin)
        {
            Svc.Toast.Show("请先登录后再回复");
            return;
        }
        var root = _detailThreadRoot != null && _detailThreadRoot.Replies.Contains(item)
            ? _detailThreadRoot
            : item;
        ShowCommentDetail(threadRoot: root, focus: root);
        // 点"回复"按钮: 用户明确要打字 → 弹出输入面板并抢焦点
        SetReplyTarget(item, focus: true, openPanel: true);
    }

    /// <summary>
    /// 把详情页输入框切到"回复 @XXX"模式(顶部冒出提示条, 发布时带 root/parent)。
    /// focus: 是否顺手把光标放进输入框 —— 点"回复"按钮时该给(用户接着就要打字),
    /// 进详情页时不给(一进门就抢焦点容易误触输入法)。
    /// openPanel: 是否**弹出输入面板**(2026-11-01 加)。
    /// ★ 只有"点回复按钮"才弹 —— 进详情页只是切回复**语义**, 每次都弹面板是打扰
    ///   (用户点进详情页多半只是想看回复, 上一版因此变成"一进详情页就弹输入框")。
    /// </summary>
    private void SetReplyTarget(CommentItem target, bool focus = true, bool openPanel = false)
    {
        _replyTarget = target;
        if (CommentDetailReplyHint != null) CommentDetailReplyHint.Visibility = Visibility.Visible;
        if (CommentDetailReplyTarget != null) CommentDetailReplyTarget.Text = "回复 @" + target.UserName;
        // 面板要开着才能打字; focus=false(进详情页)时展开但不抢键盘焦点
        if (openPanel && !_composerOpen) OpenComposer();
        if (focus) CommentDraft?.Focus();
        // 提示文案("Enter 发表" → "Enter 发送回复")与按钮可用性都跟着回复目标变
        UpdateCommentComposer();
        UpdatePostCommentButton();
    }

    private void ClearReplyTarget()
    {
        _replyTarget = null;
        if (CommentDetailReplyHint != null) CommentDetailReplyHint.Visibility = Visibility.Collapsed;
        if (CommentDetailReplyTarget != null) CommentDetailReplyTarget.Text = "";
        // 退出回复态 → 提示文案要退回"Enter 发表"
        UpdateCommentComposer();
    }

    /// <summary>退出"回复 @某人"模式, 退回发表普通评论</summary>
    private void OnCommentReplyCancel(object sender, RoutedEventArgs e)
    {
        ClearReplyTarget();
        CommentDraft?.Focus();
        UpdatePostCommentButton();
    }


    /// <summary>重新拉一条线程的第一页回复(发表成功后调用)。失败就保持原样, 不清空已有内容。</summary>
    private async Task ReloadThreadRepliesAsync(CommentItem root)
    {
        try
        {
            var (_, _, list) = await Svc.Api.GetReplyRepliesAsync(_aid, root.Rpid, pn: 1, ps: 20);
            if (_closing || list.Count == 0) return;
            // 重拉只覆盖第一页: 页码复位, "展开更多"重新可用(已加载的第 2 页会被第一页取代)
            root.RepliesNextPage = 2;
            var seen = new HashSet<long>();
            var merged = new List<CommentItem>();
            foreach (var r in list)
                if (r.Rpid > 0 && seen.Add(r.Rpid)) merged.Add(r);
            root.Replies.Clear();
            foreach (var r in merged) root.Replies.Add(r);
            if (root.ReplyCount < root.Replies.Count) root.ReplyCount = root.Replies.Count;
            SortRepliesInPlace(root);
            root.SyncPreviewReplies();
            root.NotifyRepliesChanged();
        }
        catch (Exception ex)
        {
            // 重拉失败不影响"已发布成功"这个结论, 记日志即可
            App.ReportError(ex);
        }
    }

    /// <summary>
    /// 点"查看对话" → 定位到被回复的那条(滚进视野 + 闪两下)。
    /// 目标还没加载出来(楼中楼只内联了前几条)时给明确提示, 而不是静默无反应。
    /// </summary>
    private void OnViewConversationClick(object sender, RoutedEventArgs e)
    {
        if (_closing || (sender as FrameworkElement)?.DataContext is not CommentItem reply) return;
        var root = _detailThreadRoot;
        if (root == null || reply.ParentRpid <= 0) return;

        var parent = root.Replies.FirstOrDefault(r => r.Rpid == reply.ParentRpid);
        if (parent == null)
        {
            Svc.Toast.Show("被回复的那条还没加载, 先点下方\u201c展开更多回复\u201d");
            return;
        }
        if (DetailRepliesList?.ItemContainerGenerator.ContainerFromItem(parent) is not FrameworkElement fe) return;
        fe.BringIntoView();
        // 闪一下: 告诉用户"就是这一条", 不然滚过去了也对不上号
        var flash = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(140))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(2)
        };
        fe.BeginAnimation(UIElement.OpacityProperty, flash);
    }

    private async void OnLoadMoreRepliesClick(object sender, RoutedEventArgs e)
    {
        if (_closing || _aid <= 0) return;
        if ((sender as FrameworkElement)?.DataContext is not CommentItem item) return;
        if (item.Rpid <= 0 || item.IsLoadingReplies) return;

        item.IsLoadingReplies = true;
        try
        {
            var (_, _, more) = await Svc.Api.GetReplyRepliesAsync(_aid, item.Rpid, pn: item.RepliesNextPage, ps: 20);
            item.RepliesNextPage++;
            if (_closing) return;
            // 详情页已经关了(或换了一条线程): 不能再往这条线程里加回复 ——
            // 关闭时 TrimRepliesToPreview 已经把内存收回到预览量了。
            if (!IsCommentDetailOpen || !ReferenceEquals(_detailThreadRoot, item)) return;

            // 内联的那几条也在返回结果里, 用 rpid 去重再追加
            var seen = new HashSet<long>(item.Replies.Select(x => x.Rpid));
            foreach (var r in more)
                if (r.Rpid > 0 && seen.Add(r.Rpid)) item.Replies.Add(r);

            // 服务端也有取不全的情况(风控/已删除), 用实际条数收口,
            // 否则按钮会永远停在"展开 N 条回复"上点不动
            if (more.Count == 0 || item.Replies.Count >= item.ReplyCount)
                item.ReplyCount = item.Replies.Count;
            // 新补进来的一页按"当前排序"归位(接口固定按时间正序返回)
            SortRepliesInPlace(item);
            item.NotifyRepliesChanged();
        }
        finally
        {
            item.IsLoadingReplies = false;
        }
    }

    // ------------------------------------------------------------ 评论点赞

    /// <summary>
    /// 给一条评论点赞 / 取消点赞。
    ///
    /// 先**乐观更新**界面(数字立刻动), 请求失败再回滚 —— 点赞是高频小动作,
    /// 等一次网络往返再变数字会有明显的"点了没反应"感。
    /// LikeBusy 防连点: 连点会让 赞 / 取消 两个请求交叉, 服务端的最终状态就不确定了。
    /// </summary>
    private async void OnCommentLikeClick(object sender, RoutedEventArgs e)
    {
        if (_closing || _aid <= 0) return;
        if ((sender as FrameworkElement)?.DataContext is not CommentItem item) return;
        if (item.Rpid <= 0 || item.LikeBusy) return;
        if (!Svc.Session.HasLogin)
        {
            Svc.Toast.Show("请先登录后再点赞");
            return;
        }

        var target = !item.IsLiked;
        var oldCount = item.LikeCount;
        // 服务端行为: 点赞成功会自动消掉踩 → 本地状态也要跟着互斥
        var wasDisliked = item.IsDisliked;

        item.LikeBusy = true;
        item.IsLiked = target;
        item.LikeCount = Math.Max(0, oldCount + (target ? 1 : -1));
        if (target) item.IsDisliked = false;
        try
        {
            var (ok, err) = await Svc.Api.CommentLikeAsync(_aid, item.Rpid, target);
            if (_closing) return;
            if (!ok)
            {
                // 回滚
                item.IsLiked = !target;
                item.LikeCount = oldCount;
                item.IsDisliked = wasDisliked;
                Svc.Toast.Show(err ?? "点赞失败");
            }
        }
        finally
        {
            item.LikeBusy = false;
        }
    }

    /// <summary>
    /// 点踩 / 取消点踩。与点赞对称(乐观更新 + 失败回滚), 区别是踩**没有计数** ——
    /// 接口不下发 dislike 总数, 所以只有图标的状态变化。
    ///
    /// ★ 踩走的是**另一个接口**(`/x/v2/reply/hate`), 不是点赞接口的 action=2 ——
    ///   2026-09-27 修: 之前发 action=2 给 /reply/action, 服务端回 "12011 不合法的赞或踩",
    ///   表现就是"点踩点了报错"。
    ///   两者在服务端互相覆盖(点踩成功会自动消掉赞), 所以本地状态也必须互斥。
    /// </summary>
    private async void OnCommentDislikeClick(object sender, RoutedEventArgs e)
    {
        if (_closing || _aid <= 0) return;
        if ((sender as FrameworkElement)?.DataContext is not CommentItem item) return;
        if (item.Rpid <= 0 || item.LikeBusy) return;
        if (!Svc.Session.HasLogin)
        {
            Svc.Toast.Show("请先登录后再点踩");
            return;
        }

        var target = !item.IsDisliked;       // true = 现在要点踩
        var wasLiked = item.IsLiked;
        var oldCount = item.LikeCount;

        item.LikeBusy = true;
        item.IsDisliked = target;
        // 点踩会顶掉赞(服务端行为), 本地那个数字也要跟着减回去
        if (target && wasLiked)
        {
            item.IsLiked = false;
            item.LikeCount = Math.Max(0, oldCount - 1);
        }
        try
        {
            var (ok, err) = await Svc.Api.CommentDislikeAsync(_aid, item.Rpid, target);
            if (_closing) return;
            if (!ok)
            {
                item.IsDisliked = !target;
                if (target && wasLiked)
                {
                    item.IsLiked = true;
                    item.LikeCount = oldCount;
                }
                Svc.Toast.Show(err ?? "点踩失败");
            }
        }
        finally
        {
            item.LikeBusy = false;
        }
    }

    // ------------------------------------------------------------ 发表评论

    private bool _postingComment;

    /// <summary>
    /// 输入框的可用状态与提示。三个前提缺一个就不能发: 已登录 / 有 aid / 不是本地文件播放。
    /// 直接把它们做进"输入框能不能打字"里, 比让用户敲完一大段再点按钮报错要好得多。
    /// </summary>
    private void UpdateCommentComposer()
    {
        if (_closing || CommentDraft == null) return;

        var canCompose = Svc.Session.HasLogin && _aid > 0 && !_isLocalPlayback && !_isLive;
        CommentDraft.IsEnabled = canCompose;
        // 提示文案要跟着"当前是发表还是回复"走, 否则回复态下还写着"Enter 发表"会让人困惑
        CommentPostHint.Text = _isLive
            ? "直播没有评论区"
            : !Svc.Session.HasLogin
                ? "登录后才能发表评论"
                : _aid <= 0
                    ? "没拿到视频 aid, 暂时不能评论"
                    : _replyTarget != null
                        ? "Enter 发送回复 · Shift+Enter 换行 · 点提示条上的“取消”改发评论"
                        : "Enter 发表 · Shift+Enter 换行";
        UpdatePostCommentButton();
        // FAB 的显隐跟着同一套前提走(2026-11-01 评论输入改为悬浮按钮)
        UpdateCommentFabVisibility();
    }

    // ------------------------------------------------------------ 评论输入 FAB(悬浮按钮 + 展开面板)

    /// <summary>面板是否已展开。true = 显示输入面板、FAB 变收起图标</summary>
    private bool _composerOpen;

    /// <summary>
    /// 输入框是否处于"展开高度"(2026-11-01 加, 写长评用)。
    /// ★ 它与 <see cref="_composerOpen"/> 是两回事: 面板收起后再打开, 展开**高度**保持
    ///   —— 用户写了长文收起面板看一眼列表再回来, 不该被打回 66px 的"摸黑"模式。
    ///   只有发成功(草稿清空)才回到普通高度。
    /// </summary>
    private bool _composerExpanded;

    /// <summary>输入框普通高度(两三行, 与旧版内嵌输入框一致)</summary>
    private const double ComposerNormalHeight = 66;

    /// <summary>输入框展开高度(约 9 行, 1000 字长评能整篇通读)</summary>
    private const double ComposerExpandedHeight = 200;

    /// <summary>
    /// 展开/收回输入框高度。
    /// ★ 直接赋值 Height, 不做动画: 试过 DoubleAnimation, 探针/真机上都出现过"动画时钟
    ///   未推进、Height 弹回原值"的不稳(WPF 高度动画依赖渲染节拍, 在面板刚展开/布局
    ///   未稳时不可靠)。这里是功能性切换不是装饰, 行为确定性优先 —— 高度瞬变虽不如
    ///   动画顺滑, 但 66↔200 的变化只发生在用户主动点击的那一下, 视觉可接受。
    /// </summary>
    private void ApplyComposerSize()
    {
        if (_closing || CommentDraft == null || ComposerExpandIcon == null) return;
        var target = _composerExpanded ? ComposerExpandedHeight : ComposerNormalHeight;
        ComposerExpandIcon.Text = _composerExpanded ? "\uE70E" : "\uE70D";  // 上下箭头互换
        var btn = BtnComposerExpand;
        if (btn != null) btn.ToolTip = _composerExpanded ? "收回输入框" : "展开输入框, 写长评时能通读全文";

        // 先摘掉可能残留的旧高度动画(历史遗留防御: 早期版本用过动画), 再落本地值
        CommentDraft.BeginAnimation(HeightProperty, null);
        CommentDraft.Height = target;
    }

    /// <summary>面板头行的"展开/收回"按钮</summary>
    private void OnComposerExpandClick(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        _composerExpanded = !_composerExpanded;
        ApplyComposerSize();
        // 展开后把光标挪回输入框(用户就是要看/写内容)
        if (_composerOpen) CommentDraft?.Focus();
    }

    /// <summary>
    /// FAB 的显隐。只在「评论」tab 且有"能评论的前提"时出现 ——
    /// 直播/本地文件/简介 tab 下整颗收起来, 别让用户点了个寂寞。
    /// ★ tab 状态从 TabComments.IsChecked 读, 换片/切 tab 后由 UpdateCommentComposer 统一刷新。
    /// </summary>
    private void UpdateCommentFabVisibility()
    {
        if (_closing || CommentFab == null) return;
        var usable = !_isLive && !_isLocalPlayback && _aid > 0 &&
                     CommentsPanel != null && CommentsPanel.Visibility == Visibility.Visible;
        CommentFab.Visibility = usable ? Visibility.Visible : Visibility.Collapsed;
        // 前提没了(切到直播/本地文件)时把展开的面板一起收掉
        if (!usable && _composerOpen) CloseComposer();
        // 回到顶部与 FAB 同一套前提: 不可评论时整颗熄灭, 可评论时按当前滚动位置重算
        UpdateCommentBackToTop(usable ? InfoScroll : null);
    }

    /// <summary>FAB 点击: 展开 ↔ 收起。未登录等"不能评论"的情形给 Toast 说明, 不展开空面板</summary>
    private void OnCommentFabClick(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        if (_composerOpen) { CloseComposer(); return; }

        // 与 UpdateCommentComposer 同一套前提; 不满足时说明原因(不展开空面板)
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("登录后才能发表评论"); return; }
        if (_isLive) { Svc.Toast.Show("直播没有评论区"); return; }
        if (_isLocalPlayback) { Svc.Toast.Show("本地文件没有评论区"); return; }
        if (_aid <= 0) { Svc.Toast.Show("没拿到视频 aid, 暂时不能评论"); return; }

        OpenComposer();
    }

    /// <summary>
    /// 展开输入面板: 面板从底部滑入(位移 + 淡入), FAB 图标换成收起箭头, 焦点进输入框。
    /// ★ 动画用 RenderTransform(TranslateY)而不是改 Margin: 不触发布局重排, 顺滑且零开销。
    /// </summary>
    private void OpenComposer()
    {
        if (_composerOpen || CommentComposerPanel == null || CommentFabIcon == null) return;
        _composerOpen = true;
        CommentFabIcon.Text = "\uE74A";   // ChevronDown: 收起
        CommentFab.ToolTip = "收起评论框";

        var panel = CommentComposerPanel;
        panel.Visibility = Visibility.Visible;
        // 初始态: 压在底下 24px + 全透明, 再动画回来。
        // ★ Opacity 必须先显式归 0: 收起路径(FinishCloseComposer)会把基准值复位成 1,
        //   不归 0 的话"收起 → 立刻再打开"这次淡入是 1→1, 看不到过渡。
        panel.Opacity = 0;
        var translate = new TranslateTransform(0, 24);
        panel.RenderTransform = translate;
        panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(200))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        // 焦点进输入框(等可见性生效之后; 走 Dispatcher 排一轮)
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
            new Action(() => { if (!_closing && _composerOpen) CommentDraft?.Focus(); }));

        // 输入框高度按"展开"状态对齐(上次展开过高、收起面板后再开, 仍保持大框)
        ApplyComposerSize();
    }

    /// <summary>收起输入面板: 淡出后 Visibility=Collapsed。草稿**不清**(只是收起, 不是放弃)</summary>
    private void CloseComposer()
    {
        if (!_composerOpen || CommentComposerPanel == null || CommentFabIcon == null) return;
        _composerOpen = false;
        CommentFabIcon.Text = "\uE70F";   // Edit: 笔
        CommentFab.ToolTip = "写评论";

        var panel = CommentComposerPanel;
        var anim = new DoubleAnimation(panel.Opacity, 0, TimeSpan.FromMilliseconds(140))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        anim.Completed += (_, _) => FinishCloseComposer(panel);
        panel.BeginAnimation(OpacityProperty, anim);
        // ★ 兜底(2026-11-01): Completed 回调依赖渲染节拍, 个别环境(布局刚抖过/窗口后台)会不回调,
        //   面板就"看不见但还占着命中测试"。300ms 后(动画早已该结束)再落一次 ——
        //   FinishCloseComposer 幂等, 重复调无副作用; 若期间面板又被打开(_composerOpen=true)则跳过。
        _ = Task.Delay(300).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() =>
            FinishCloseComposer(panel))));
    }

    /// <summary>把面板真正落回 Collapsed(幂等: 淡出动画回调与兜底定时器都会调它)</summary>
    private void FinishCloseComposer(Border panel)
    {
        if (_closing || _composerOpen) return;   // 期间又被打开就不收
        panel.BeginAnimation(OpacityProperty, null);
        panel.Opacity = 1;                        // 复位基准值, 下次 OpenComposer 从 0 淡入
        panel.Visibility = Visibility.Collapsed;
    }

    private void UpdatePostCommentButton()
    {
        if (_closing || PostCommentButton == null || CommentDraft == null) return;
        PostCommentButton.IsEnabled = CommentDraft.IsEnabled && !_postingComment
                                      && !string.IsNullOrWhiteSpace(CommentDraft.Text);
    }

    private void OnCommentDraftChanged(object sender, TextChangedEventArgs e)
        => UpdatePostCommentButton();

    /// <summary>
    /// Enter 发表, Shift+Enter 换行。用 Preview 阶段拦: 输入框开着 AcceptsReturn,
    /// 换行是在 TextBox 自己的 KeyDown 里插进去的, 冒泡到我们的处理器时已经晚了。
    /// </summary>
    private void OnCommentDraftPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;   // 换行, 放它过去
        e.Handled = true;
        _ = PostCommentAsync();
    }

    private void OnPostCommentClick(object sender, RoutedEventArgs e) => _ = PostCommentAsync();

    /// <summary>
    /// 发表。**同一个输入框两种语义**(用户规则: 在列表页评论 = 自己开新楼层; 在详情页评论 = 回复这层楼):
    /// 有回复目标(_replyTarget) 就带 root/parent 发成楼中楼回复, 否则发普通评论。
    ///
    /// 失败**不清空输入框** —— 网络抖一下就把用户写的字抹掉, 是最让人恼火的那种"智能"。
    ///
    /// 成功后重新拉一次列表, 而不是本地插一条假的: 服务端返回的 rpid / 楼层 / 是否要审核
    /// 都不是本地能猜的, 拼一条出来看着即时, 一旦用户去点赞就会发现那条根本不存在。
    /// </summary>
    private async Task PostCommentAsync()
    {
        if (_closing || _postingComment) return;
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("发表评论需要先登录"); return; }
        if (_aid <= 0) { Svc.Toast.Show("没拿到视频 aid, 暂时不能评论"); return; }

        var text = CommentDraft.Text?.Trim() ?? "";
        if (text.Length == 0) return;

        var target = _replyTarget;                     // null = 发表新楼层
        var root = _detailThreadRoot;
        // 回复必须**同时**带 root 与 parent: 少了 root 服务端会把回复挂到根楼层上(回错位置)
        var rootRpid = root != null && target != null ? root.Rpid : 0;
        var parentRpid = target?.Rpid ?? 0;
        var isReply = rootRpid > 0 && parentRpid > 0;

        _postingComment = true;
        PostCommentButton.Content = isReply ? "回复中…" : "发表中…";
        UpdatePostCommentButton();
        try
        {
            var (ok, err) = await Svc.Api.PostCommentAsync(_aid, text, rootRpid, parentRpid);
            if (_closing) return;
            if (!ok)
            {
                Svc.Toast.Show(err ?? (isReply ? "回复发送失败" : "评论发表失败"));
                return;
            }

            CommentDraft.Clear();
            ClearReplyTarget();     // 发完退回"发表"模式, 免得下一条手滑又回给同一个人
            Svc.Toast.Show(isReply ? "回复已发布" : "评论已发表");
            // 发完把面板收起来: 输入使命完成, 别挡着用户看列表。
            // ★ 展开高度一并归位: 内容已清空, 大框没有存在的意义(下次写长评再点展开)。
            if (_composerExpanded)
            {
                _composerExpanded = false;
                ApplyComposerSize();
            }
            CloseComposer();

            if (isReply && root != null)
                await ReloadThreadRepliesAsync(root);   // 回复: 只重拉这条线程
            else
                await LoadCommentsAsync(force: true);   // 评论: 重拉整页列表

            if (!_closing && CommentHint != null)
            {
                // 说实话: 列表是"按热度"排的、回复还要过审排队 ——
                // 不说这句, 用户会以为内容没发出去(或发了但被吞了)。
                CommentHint.Text = isReply
                    ? "回复已发布 · 服务端要过审排队, 可能不会立刻出现在回复列表里"
                    : "评论已发表 · 列表按热度排序, 刚发的可能不在前几条";
            }
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            if (!_closing) Svc.Toast.Show(isReply ? "回复发送失败" : "评论发表失败");
        }
        finally
        {
            _postingComment = false;
            if (!_closing)
            {
                PostCommentButton.Content = "发表";
                UpdatePostCommentButton();
            }
        }
    }

}
