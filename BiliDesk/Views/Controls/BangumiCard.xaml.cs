using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 番剧海报卡(竖版 3:4): 入场淡入 + 悬停放大 + 点击打开**选集页**。
///
/// ★ 点卡片为什么不直接开播: 番剧一季几十上百集, 点一下就放第 1 集几乎总是错的。
///   这里点开的是选集页(见 <see cref="Views.Pages.BangumiPage"/>), 由用户挑一集再播 ——
///   这也是 B 站网页版的行为。
///
/// 动画与 VideoCard 同一套做法(入场淡入 160ms / 悬停封面放大 1.04), 两处各写一份而不是
/// 抽公共基类: 两者除了比例与角标语义不同, 继承树里并没有第三个用户, 抽出去只是把
/// 两条互不相干的卡片耦合到同一个基类上。
/// </summary>
public partial class BangumiCard : UserControl
{
    private TranslateTransform? _trans;
    private ScaleTransform? _coverScale;
    private bool _entered;

    /// <summary>
    /// 已经亮过相的番剧条目(与 VideoCard._seenItems 同款理由)。
    /// 切 tab 会重建整墙卡片, 但条目对象是同一份 —— 用弱引用表记住就跳过重复的入场动画,
    /// 避免每次切到追番页一百多张卡同时重播一遍淡入(用户报的"切 tab 闪几下")。
    /// </summary>
    private static readonly ConditionalWeakTable<BangumiItem, object?> _seenItems = new();

    public BangumiCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private BangumiItem? Current => DataContext as BangumiItem;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_entered) return;
        _entered = true;

        _trans = new TranslateTransform(0, 18);
        _coverScale = new ScaleTransform(1, 1);
        Root.RenderTransform = _trans;
        CoverCard.RenderTransform = _coverScale;
        CoverCard.RenderTransformOrigin = new Point(0.5, 0.5);

        if (DataContext is BangumiItem item && _seenItems.TryGetValue(item, out _))
        {
            _trans.Y = 0;
            Opacity = 1;
            return;
        }
        if (DataContext is BangumiItem item2) _seenItems.Add(item2, null);

        Opacity = 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        _trans.Y = 0;
    }

    private void OnMouseEnter(object sender, MouseEventArgs e) => AnimateCoverScale(1.04);

    private void OnMouseLeave(object sender, MouseEventArgs e) => AnimateCoverScale(1);

    private void AnimateCoverScale(double zoom)
    {
        if (_coverScale == null) return;
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var ms = TimeSpan.FromMilliseconds(180);
        _coverScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(_coverScale.ScaleX, zoom, ms) { EasingFunction = ease });
        _coverScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(_coverScale.ScaleY, zoom, ms) { EasingFunction = ease });
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        Open();
    }

    /// <summary>打开这部番剧的选集页</summary>
    private void Open()
    {
        if (Current is not { } b) return;
        new NavigationDispatcher(Window.GetWindow(this)).OpenBangumi(b);
    }

    private async void OnDownloadCover(object sender, RoutedEventArgs e)
    {
        var b = Current;
        if (b == null) return;
        await MediaActions.SaveImageAsync(b.Cover, b.Title);
    }

    // ★ OnOpenWebPage 已删(2026-10-11 用户要求): 右键菜单里不再有"在浏览器中打开番剧主页"。
    //   跳 B 站网页版会把用户从原生体验里拽出来, 首页那三个「查看全部 / 我的追番 ›」同理由头删除。
    //   番剧主页 URL 仍留在 BangumiItem.Url 上 —— 万一以后要接站内"番剧详情页", 数据已经齐了,
    //   不用再回头找接口。
}