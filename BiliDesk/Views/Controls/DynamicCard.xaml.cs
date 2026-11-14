using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.Views.Controls;

/// <summary>动态卡请求在右侧面板打开详情时携带的参数。</summary>
public sealed class DynamicDetailRequestedEventArgs : EventArgs
{
    public DynamicDetailRequestedEventArgs(DynamicItem item, bool focusComments, FrameworkElement? source)
    {
        Item = item;
        FocusComments = focusComments;
        Source = source;
    }

    public DynamicItem Item { get; }
    public bool FocusComments { get; }

    /// <summary>触发详情的动态卡(用于详情面板"从卡片长出来"的动画锚点)。</summary>
    public FrameworkElement? Source { get; }
}

/// <summary>
/// 动态流里的一条**综合动态卡**(视频 / 图文 / 纯文字 / 专栏 / 转发)。
///
/// 版式参考 B 站动态页: 头部 [头像 + 昵称 + 时间 · 发布动作], 正文, 媒体区
/// (视频大封面 / 图片网格 / 直播 / 转发原文), 底部 [转发 / 评论 / 点赞] 操作栏。
///
/// ★ 与旧版"左封面右标题"的视频卡不同: 现在媒体区是整宽 16:9 / 图片网格,
///   数据模型也从 VideoItem 换成 DynamicItem(见 Models/DynamicModels.cs)。
/// ★ 卡片墙仍用 VideoCard + VideoItem, 由 FollowViewModel.SyncWallItems 转换, 不受这里影响。
/// </summary>
public partial class DynamicCard : UserControl
{
    /// <summary>请求 FollowPage 在右侧面板打开详情(由 DataTemplate 在页面里接)。</summary>
    public event EventHandler<DynamicDetailRequestedEventArgs>? DetailRequested;

    /// <summary>本次左键手势是否从这张卡片上按下(虚拟化回收复用容器时防误触)。</summary>
    private bool _pressInside;

    private bool _prewarmRequested;
    private bool _expanded;
    private ContextMenu? _menu;

    public DynamicCard()
    {
        InitializeComponent();
        ContextMenuOpening += OnContextMenuOpening;
        DataContextChanged += (_, _) =>
        {
            _pressInside = false;
            _expanded = false;
            _menu = null;
            ContextMenu = null;
            // 虚拟化回收: 上一条是展开态时, 复用到下一条必须复位, 否则新卡片一进来就是"全文"态
            if (ContentTextBlock != null) ContentTextBlock.Visibility = Visibility.Visible;
            if (ContentRichText != null)
            {
                ContentRichText.Visibility = Visibility.Collapsed;
                // 富文本是按需灌的: 回收时一并清掉, 免得下一条卡片展开瞬间闪一下旧正文
                ContentRichText.Text = "";
            }
        };
        // 鼠标在卡片上真实移动 = 可能要看视频: 预热 LibVLC。
        MouseMove += (_, _) =>
        {
            if (_prewarmRequested) return;
            _prewarmRequested = true;
            VlcCore.PrewarmOnDemand();
        };
        SizeChanged += (_, _) =>
        {
            UpdateCardWidth();
            UpdateVideoHeight();
            UpdateExpandButton();
        };
        Loaded += (_, _) =>
        {
            UpdateCardWidth();
            UpdateVideoHeight();
            UpdateExpandButton();
        };
    }

    private DynamicItem? Current => DataContext as DynamicItem;

    // ------------------------------------------------------------ 点击路由

    private void OnCardPress(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) _pressInside = true;
    }

    private bool TakePress()
    {
        if (!_pressInside) return false;
        _pressInside = false;
        return true;
    }

    /// <summary>点卡片空白处 → 打开动态详情(图文/文字看全文与评论, 视频看评论)。</summary>
    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        // 正文是可拖选的富文本(LinkText): 在它上面松手是"选完文字", 不是"点卡片"
        if (VisualTreeUtil.FindAncestor<LinkText>(e.OriginalSource as DependencyObject) != null)
            return;
        // 点在正文的链接上: 那是"打开这个链接", 别顺手把详情面板也拉出来。
        // (Hyperlink 自己会处理 Click, 但卡片的 MouseLeftButtonUp 挂在 Border 上,
        //  两件事必须在这里明确分开。收起态的 LinkTextBlock 不是可拖选控件,
        //  点在它身上仍应打开详情 —— 所以这里只挡 Hyperlink, 不挡 LinkTextBlock。)
        if (VisualTreeUtil.FindAncestor<Hyperlink>(e.OriginalSource as DependencyObject) != null)
            return;
        if (!TakePress()) return;
        if (Current is not { } item) return;
        RequestDetail(item);
        e.Handled = true;
    }

    private void OnUpClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (!TakePress()) return;
        if (Current is not { } item) return;
        OpenUser(item.AuthorMid);
        e.Handled = true;
    }

    private void OnOrigUpClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (!TakePress()) return;
        if ((sender as FrameworkElement)?.DataContext is not DynamicItem orig) return;
        OpenUser(orig.AuthorMid);
        e.Handled = true;
    }

    private void OnVideoClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (!TakePress()) return;
        if (Current is not { } item) return;
        var bvid = item.VideoBvid;
        if (string.IsNullOrEmpty(bvid)) return;
        Svc.Player.PlayVideo(bvid, item.VideoTitle);
        e.Handled = true;
    }

    private void OnOrigVideoClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (!TakePress()) return;
        if ((sender as FrameworkElement)?.DataContext is not DynamicItem orig) return;
        if (string.IsNullOrEmpty(orig.Bvid)) return;
        Svc.Player.PlayVideo(orig.Bvid, orig.Title);
        e.Handled = true;
    }

    private void OnArticleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (!TakePress()) return;
        if (Current is not { } item) return;
        RequestDetail(item);
        e.Handled = true;
    }

    private void OnLiveClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (!TakePress()) return;
        if (Current is not { } item || item.RoomId <= 0) return;
        Svc.Player.PlayLive(item.RoomId, item.Title);
        e.Handled = true;
    }

    private void OpenUser(long mid)
    {
        if (mid <= 0)
        {
            Svc.Toast.Show("没拿到 UP 主的 UID, 打不开主页");
            return;
        }
        new NavigationDispatcher(Window.GetWindow(this)).OpenUserSpace(mid.ToString());
    }

    private void RequestDetail(DynamicItem item, bool focusComments = false)
        => DetailRequested?.Invoke(this, new DynamicDetailRequestedEventArgs(item, focusComments, this));

    // ------------------------------------------------------------ 底部操作栏

    private void OnForwardClick(object sender, RoutedEventArgs e)
    {
        if (Current is not { } item) return;
        var url = item.DynamicUrl;
        if (string.IsNullOrEmpty(url))
        {
            Svc.Toast.Show("这条动态没有可复制的链接");
            return;
        }
        try
        {
            Clipboard.SetText(url);
            Svc.Toast.Show("动态链接已复制");
        }
        catch
        {
            Svc.Toast.Show("复制失败, 请重试");
        }
    }

    private void OnCommentClick(object sender, RoutedEventArgs e)
    {
        if (Current is not { } item) return;
        RequestDetail(item, focusComments: true);
    }

    private async void OnLikeClick(object sender, RoutedEventArgs e)
    {
        if (Current is not { } item) return;
        if (!Svc.Session.HasLogin)
        {
            Svc.Toast.Show("请先登录后再点赞");
            return;
        }
        if (item.LikeBusy || string.IsNullOrEmpty(item.Id)) return;

        var target = !item.IsLiked;
        var oldCount = item.LikeCount;
        var oldLiked = item.IsLiked;

        item.LikeBusy = true;
        item.IsLiked = target;
        item.LikeCount = Math.Max(0, oldCount + (target ? 1 : -1));
        try
        {
            var (ok, err) = await Svc.Api.LikeDynamicAsync(item.Id, target);
            if (!ok)
            {
                item.IsLiked = oldLiked;
                item.LikeCount = oldCount;
                Svc.Toast.Show(err ?? "点赞失败");
                return;
            }
            // 与播放器点赞按钮同款反馈(1 → 1.3 → 1, 420ms)
            LikeFeedback.PlayPop(LikeIconBox);
        }
        finally
        {
            item.LikeBusy = false;
        }
    }

    private void UpdateCardWidth()
    {
        // ★ 不能只写 MaxWidth + Center: WPF 会让 Border 按内容宽度收缩, 短文案卡变窄、
        //   长文案卡变宽。这里按列宽显式给宽度, 所有动态卡才真正等宽。
        var w = ActualWidth;
        if (w <= 0) return;
        CardBorder.Width = Math.Min(w, 720);
    }

    // ------------------------------------------------------------ 正文展开 / 视频尺寸

    private void OnExpandClick(object sender, RoutedEventArgs e)
    {
        _expanded = !_expanded;
        // 展开态切到 LinkText(可拖选复制 + 链接可点), 收起态切回 LinkTextBlock(可做省略号截断)
        ContentTextBlock.Visibility = _expanded ? Visibility.Collapsed : Visibility.Visible;
        ContentRichText.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        // 富文本是按需灌的(见 XAML 注释): 展开时才把正文交过去
        if (_expanded) ContentRichText.Text = Current?.ContentText ?? "";
        ExpandButton.Content = _expanded ? "收起" : "全文";
    }

    private void UpdateExpandButton()
    {
        if (Current is not { HasText: true })
        {
            ExpandButton.Visibility = Visibility.Collapsed;
            return;
        }
        var width = ContentTextBlock.ActualWidth;
        if (width <= 0) return;

        // 临时放开高度量一次完整高度, 超过 176 才给"全文"按钮
        var oldMax = ContentTextBlock.MaxHeight;
        ContentTextBlock.MaxHeight = double.PositiveInfinity;
        ContentTextBlock.Measure(new Size(width, double.PositiveInfinity));
        var full = ContentTextBlock.DesiredSize.Height;
        ContentTextBlock.MaxHeight = oldMax;

        ExpandButton.Visibility = full > 176 ? Visibility.Visible : Visibility.Collapsed;
        if (full <= 176 && _expanded)
        {
            _expanded = false;
            ContentTextBlock.Visibility = Visibility.Visible;
            ContentRichText.Visibility = Visibility.Collapsed;
            // 富文本是按需灌的: 收回时一并清掉, 与虚拟化回收那条路保持一致
            ContentRichText.Text = "";
            ExpandButton.Content = "全文";
        }
    }

    private void UpdateVideoHeight()
    {
        var w = VideoHost.ActualWidth;
        if (w <= 0) return;
        VideoHost.Height = Math.Min(w * 9 / 16, 405);
    }

    private void OnVideoEnter(object sender, MouseEventArgs e) => PlayOverlay.Opacity = 1;
    private void OnVideoLeave(object sender, MouseEventArgs e) => PlayOverlay.Opacity = 0;

    // ------------------------------------------------------------ 右键 / 更多菜单

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        _menu ??= BuildMenu();
        ContextMenu = _menu;
        if (sender is UIElement target) _menu.PlacementTarget = target;
        _menu.IsOpen = true;
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _menu ??= BuildMenu();
        ContextMenu = _menu;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        var item = Current;

        menu.Items.Add(MakeMenuItem("查看动态详情", "\uE8A7", (_, _) =>
        {
            if (Current is { } it) RequestDetail(it);
        }));
        menu.Items.Add(MakeMenuItem("复制动态链接", "\uE8C8", (_, _) => OnForwardClick(this, new RoutedEventArgs())));
        menu.Items.Add(new Separator());

        if (item?.HasAnyVideo == true)
        {
            menu.Items.Add(MakeMenuItem("预览封面", "\uE8B9", (_, _) =>
            {
                if (Current is { } it) MediaActions.ShowImage(it.VideoCover, it.VideoTitle, Window.GetWindow(this));
            }));
            menu.Items.Add(MakeMenuItem("下载封面到本地", "\uE896", async (_, _) =>
            {
                if (Current is { } it) await MediaActions.SaveImageAsync(it.VideoCover, it.VideoTitle);
            }));
            menu.Items.Add(MakeMenuItem("下载视频到本地", "\uE896", async (_, _) =>
            {
                if (Current is not { } it) return;
                var bvid = it.VideoBvid;
                if (string.IsNullOrEmpty(bvid)) return;
                await DownloadActions.ShowAndDownloadAsync(
                    bvid, 0, it.VideoTitle, QualityPreference.RequestQn(Svc.Settings.PreferredQualityQn),
                    Window.GetWindow(this));
            }, "可选清晰度与保存位置"));
            menu.Items.Add(MakeMenuItem("加入稍后再看", "\uE7C1", async (_, _) => await AddWatchLaterAsync()));
            menu.Items.Add(new Separator());
        }
        else if (item?.HasImages == true)
        {
            menu.Items.Add(MakeMenuItem("预览第一张图片", "\uE8B9", (_, _) =>
            {
                if (Current is { Images.Count: > 0 } it)
                {
                    // ★ 传整组: 从第一张开始, 之后可在预览窗里左右翻
                    var urls = new System.Collections.Generic.List<string>(it.Images.Count);
                    foreach (var i in it.Images) urls.Add(i.Url);
                    MediaActions.ShowImage(urls, 0, "动态图片", Window.GetWindow(this));
                }
            }));
            menu.Items.Add(MakeMenuItem("下载第一张图片", "\uE896", async (_, _) =>
            {
                if (Current is { Images.Count: > 0 } it)
                    await MediaActions.SaveImageAsync(it.Images[0].Url, "动态图片");
            }));
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(MakeMenuItem("屏蔽此 UP 主", "\uE77B", (_, _) => BlockUp(),
            "所有列表里都不再显示这个 UP 主"));
        var title = item?.VideoTitle;
        if (string.IsNullOrEmpty(title)) title = item?.Title;
        if (!string.IsNullOrEmpty(title))
        {
            menu.Items.Add(MakeMenuItem("屏蔽此标题", "\uE7BA", (_, _) => BlockTitle(),
                "只隐藏标题完全相同的这一条"));
        }
        return menu;
    }

    private static MenuItem MakeMenuItem(string header, string glyph, RoutedEventHandler onClick, string? toolTip = null)
    {
        var item = new MenuItem { Header = header };
        if (!string.IsNullOrEmpty(toolTip)) item.ToolTip = toolTip;
        item.Icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        item.Click += onClick;
        return item;
    }

    private async Task AddWatchLaterAsync()
    {
        if (Current is not { } item) return;
        var bvid = item.VideoBvid;
        if (string.IsNullOrEmpty(bvid)) return;
        if (!Svc.Session.HasLogin)
        {
            Svc.Toast.Show("稍后再看需要先登录");
            return;
        }
        var aid = item.AsVideoItem()?.Aid ?? 0;
        if (aid <= 0)
        {
            var (_, _, vd) = await Svc.Api.GetVideoAsync(bvid);
            aid = vd?.Aid ?? 0;
        }
        if (aid <= 0)
        {
            Svc.Toast.Show("没拿到这条视频的 avid, 加不了");
            return;
        }
        var (ok, err) = await Svc.Api.AddWatchLaterAsync(aid);
        Svc.Toast.Show(ok ? "已加入稍后再看" : "加入失败: " + (err ?? "未知错误"));
    }

    private void BlockUp()
    {
        if (Current is not { } item) return;
        var author = (item.AuthorName ?? "").Trim();
        if (author.Length == 0 && item.AuthorMid <= 0)
        {
            Svc.Toast.Show("这条没有 UP 主信息，屏蔽不了");
            return;
        }
        FilterService.Instance.BlockUp(author, item.AuthorMid);
        var label = author.Length > 0 ? author : $"UID {item.AuthorMid}";
        Svc.Toast.Show($"已屏蔽 UP 主「{label}」，可在首页「屏蔽」面板撤销");
    }

    private void BlockTitle()
    {
        if (Current is not { } item) return;
        var title = (item.VideoTitle ?? "").Trim();
        if (title.Length == 0) title = (item.Title ?? "").Trim();
        if (title.Length == 0) return;
        FilterService.Instance.BlockTitle(title);
        Svc.Toast.Show("已屏蔽这条动态，可在首页「屏蔽」面板撤销");
    }
}