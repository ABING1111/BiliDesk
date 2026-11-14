using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BiliDesk.Helpers;
using BiliDesk.Models;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 动态图片网格(图文动态 / 专栏封面 / 详情窗共用)。
///
/// 版式照 B 站动态页:
///   · 1 张: 单张大图, 保留原始宽高比(最高约 320 宽);
///   · 2 张: 两列 4:3;
///   · 3 张: 三列方形;
///   · 4 张: 2×2 方形;
///   · 5~9 张: 三列方形;
///   · 超过 9 张: 只显示前 9 张, 最后一张盖 "+N"。
///
/// ★ 为什么不用 ItemsControl + UniformGrid:
///   张数不同列数/宽高比都不同, UniformGrid 只能等分, 做不出"单张保比例、两张 4:3、
///   其余方形"这套规则。这里直接按张数手摆 Grid, 逻辑一眼可读, 也方便盖 +N 角标。
/// ★ 图片加载仍走 Cover 附加属性(共享磁盘缓存/解码闸门), 不自己 new Image。
/// </summary>
public partial class DynamicImageGrid : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable<DynamicImage>), typeof(DynamicImageGrid),
        new PropertyMetadata(null, OnItemsSourceChanged));

    public IEnumerable<DynamicImage>? ItemsSource
    {
        get => (IEnumerable<DynamicImage>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>true = 详情窗用中档图(DetailUrl); false = 列表卡片用缩略图(ThumbUrl)。</summary>
    public static readonly DependencyProperty UseDetailProperty = DependencyProperty.Register(
        nameof(UseDetail), typeof(bool), typeof(DynamicImageGrid),
        new PropertyMetadata(false, OnUseDetailChanged));

    public bool UseDetail
    {
        get => (bool)GetValue(UseDetailProperty);
        set => SetValue(UseDetailProperty, value);
    }

    private readonly List<Border> _tiles = new();
    private List<DynamicImage> _images = new();

    public DynamicImageGrid()
    {
        InitializeComponent();
        SizeChanged += (_, _) => LayoutTiles();
        Loaded += (_, _) => LayoutTiles();
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((DynamicImageGrid)d).Rebuild();

    private static void OnUseDetailChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((DynamicImageGrid)d).Rebuild();

    private void Rebuild()
    {
        _images = ItemsSource?.ToList() ?? new List<DynamicImage>();

        // ★★★ 2026-11-14 修"滑动时报错: 指定的元素已经是另一个元素的逻辑子元素":
        //   虚拟化列表回收容器时, 同一个 DynamicImageGrid 会被重新绑定(ItemsSource 变化/DataContext 变化),
        //   旧 tile 如果还挂在 RootGrid 上就直接 Add 新的, 会在 Children.Add 里炸。
        //   这里先把上一轮的 tile 从可视树里彻底摘掉(Child 也要断), 再重建。
        for (var i = RootGrid.Children.Count - 1; i >= 0; i--)
        {
            if (RootGrid.Children[i] is Border old) old.Child = null;
            RootGrid.Children.RemoveAt(i);
        }
        _tiles.Clear();
        RootGrid.ColumnDefinitions.Clear();
        RootGrid.RowDefinitions.Clear();

        var show = Math.Min(_images.Count, 9);
        for (var i = 0; i < show; i++)
        {
            var img = _images[i];
            var tile = MakeTile(img);

            // 超过 9 张: 最后一格盖 "+N", 提示还有更多(点开详情看全部)
            if (i == show - 1 && _images.Count > 9)
            {
                var overlay = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
                    CornerRadius = new CornerRadius(8),
                    IsHitTestVisible = false,
                    Child = new TextBlock
                    {
                        Text = "+" + (_images.Count - 9),
                        Foreground = Brushes.White,
                        FontSize = 18,
                        FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    }
                };
                if (tile.Child is UIElement inner)
                {
                    // ★ 必须先把 inner 从 tile 上摘下来, 再放进新的 Grid。
                    //   Border.Child 还指向它时直接 g.Children.Add(inner) 会抛
                    //   "指定的元素已经是另一个元素的逻辑子元素" —— 滑动到 10 图以上的动态就崩。
                    tile.Child = null;
                    var g = new Grid();
                    g.Children.Add(inner);
                    g.Children.Add(overlay);
                    tile.Child = g;
                }
            }

            RootGrid.Children.Add(tile);
            _tiles.Add(tile);
        }

        LayoutTiles();
    }

    private Border MakeTile(DynamicImage img)
    {
        var url = UseDetail
            ? (string.IsNullOrEmpty(img.DetailUrl) ? img.Url : img.DetailUrl)
            : (string.IsNullOrEmpty(img.ThumbUrl) ? img.Url : img.ThumbUrl);

        var inner = new Border { CornerRadius = new CornerRadius(8) };
        Cover.SetSource(inner, url);

        var tile = new Border
        {
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = inner,
            DataContext = img,
            ToolTip = "点击查看大图",
        };
        tile.SetResourceReference(BackgroundProperty, "PlaceholderBrush");
        tile.MouseLeftButtonUp += OnTileClick;

        var menu = new ContextMenu();
        var preview = new MenuItem { Header = "查看大图" };
        // ★ 传**整组**: 打开后能在预览窗里左右翻, 而不是关掉再点下一张
        preview.Click += (_, _) => ShowPreview(img);
        var save = new MenuItem { Header = "下载图片到本地" };
        save.Click += async (_, _) => await MediaActions.SaveImageAsync(img.Url, "动态图片");
        menu.Items.Add(preview);
        menu.Items.Add(save);
        tile.ContextMenu = menu;

        return tile;
    }

    private void OnTileClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if ((sender as FrameworkElement)?.DataContext is not DynamicImage img) return;
        ShowPreview(img);
        e.Handled = true;
    }

    /// <summary>
    /// 打开预览窗, 并把**这一组图**一起交过去(起始下标 = 被点的那张)。
    ///
    /// ★ 只传缩略图/详情图会掉清晰度: 预览窗按 4096 解码, 传原始 Url 才能看到大图。
    /// ★ 用 _images 而不是可见的 9 张: 超过 9 张时列表只显示前 9 张, 但用户点开大图后
    ///   仍应能翻到后面那些(它们只是没在卡片上露脸)。
    /// </summary>
    private void ShowPreview(DynamicImage img)
    {
        var urls = new List<string>(_images.Count);
        foreach (var i in _images) urls.Add(i.Url);
        MediaActions.ShowImage(urls, _images.IndexOf(img), "动态图片", Window.GetWindow(this));
    }

    private void LayoutTiles()
    {
        var count = _tiles.Count;
        if (count == 0) return;
        var width = ActualWidth;
        if (width <= 0) return;

        var cols = count == 1 ? 1 : (count == 2 || count == 4) ? 2 : 3;
        var rows = (count + cols - 1) / cols;
        const double gap = 6;

        var cellW = (width - gap * (cols - 1)) / cols;
        if (count == 1) cellW = Math.Min(cellW, 320);

        double cellH;
        if (count == 1)
        {
            var img = _images[0];
            cellH = img.Width > 0 && img.Height > 0
                ? cellW * img.Height / img.Width
                : cellW * 0.75;
            cellH = Math.Clamp(cellH, 160, 420);
        }
        else if (count == 2)
        {
            cellH = cellW * 0.75;
        }
        else
        {
            cellH = cellW;
        }

        RootGrid.ColumnDefinitions.Clear();
        for (var c = 0; c < cols; c++)
            RootGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(c == cols - 1 ? cellW : cellW + gap)
            });

        RootGrid.RowDefinitions.Clear();
        for (var r = 0; r < rows; r++)
            RootGrid.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(r == rows - 1 ? cellH : cellH + gap)
            });

        for (var i = 0; i < count; i++)
        {
            var tile = _tiles[i];
            Grid.SetColumn(tile, i % cols);
            Grid.SetRow(tile, i / cols);
            tile.Width = cellW;
            tile.Height = cellH;
        }
    }
}