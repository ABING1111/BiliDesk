using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using BiliDesk.Helpers;
using BiliDesk.Models;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 动态图片网格(图文动态 / 专栏封面 / 详情窗共用)。
///
/// 版式照 B 站动态页:
///   · 1 张: 单图按**真实宽高比**排版(详情最宽 720, 卡片最宽 320), 长图卡片最高 420;
///     接口没给宽高时按 16:9 兜底; Contain + 模糊铺底, 保证整张图都在;
///   · 2 张: 两列 4:3;
///   · 3 张: 三列方形;
///   · 4 张: 2×2 方形;
///   · 5~9 张: 三列方形;
///   · 超过 9 张: 卡片只显示前 9 张, 最后一张盖 "+N";
///     详情窗(<see cref="ShowAll"/>=true)显示全部, 3 列、按各自宽高比定高(不裁切)。
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

    /// <summary>
    /// true = 显示全部图片(详情窗), false = 卡片最多 9 张。
    ///
    /// ★ 动态详情接口的语义就是"补全全文/完整图片", 但网格原来一律只画前 9 张,
    ///   第 10 张以后只能靠点开预览再左右翻 —— 专栏正文几十张图时就是用户看到的
    ///   "图片显示不全"。详情窗把这一项打开即可。
    /// ★ 卡片仍然保留 9 张 + "+N": 一屏几十张图会把信息流撑爆, 也不符合 B 站版式。
    /// </summary>
    public static readonly DependencyProperty ShowAllProperty = DependencyProperty.Register(
        nameof(ShowAll), typeof(bool), typeof(DynamicImageGrid),
        new PropertyMetadata(false, OnShowAllChanged));

    public bool ShowAll
    {
        get => (bool)GetValue(ShowAllProperty);
        set => SetValue(ShowAllProperty, value);
    }

    /// <summary>
    /// true = 点图不打开大图预览, 而是请求打开动态详情。
    ///
    /// ★ 专栏卡片用的就是这一档: 点封面应该进文章看全部图片, 而不是只弹出一张封面预览。
    ///   详情窗自己的图集保持 false(点图 → 大图预览, 仍然可以左右翻)。
    /// </summary>
    public static readonly DependencyProperty ClickOpensDetailProperty = DependencyProperty.Register(
        nameof(ClickOpensDetail), typeof(bool), typeof(DynamicImageGrid),
        new PropertyMetadata(false));

    public bool ClickOpensDetail
    {
        get => (bool)GetValue(ClickOpensDetailProperty);
        set => SetValue(ClickOpensDetailProperty, value);
    }

    /// <summary>点图请求打开详情(<see cref="ClickOpensDetail"/>=true 时由卡片自己接)。</summary>
    public event EventHandler? DetailRequested;

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

    private static void OnShowAllChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
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

        var show = ShowAll ? _images.Count : Math.Min(_images.Count, 9);
        for (var i = 0; i < show; i++)
        {
            var img = _images[i];
            var tile = MakeTile(img);

            // 超过 9 张: 最后一格盖 "+N", 提示还有更多(点开详情看全部)
            if (!ShowAll && i == show - 1 && _images.Count > 9)
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

        UIElement content = inner;
        if (_images.Count == 1)
        {
            // 单图: 按真实宽高比排版(见 LayoutSingle), 再用 Contain 兜底 ——
            // 接口没给宽高(专栏封面)或遇到长图/竖图时, 整张图都在, 不会被裁掉一部分。
            // 空出来的边由同一张位图的模糊版铺满(与视频卡片同一套 Cover.Backdrop, 不额外解码)。
            Cover.SetFit(inner, Cover.CoverFit.Contain);
            var backdrop = new Border
            {
                CornerRadius = new CornerRadius(8),
                Opacity = 0.9,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1.12, 1.12),
                Effect = new BlurEffect
                {
                    Radius = 28,
                    KernelType = KernelType.Gaussian,
                    RenderingBias = RenderingBias.Performance,
                },
            };
            Cover.SetBackdrop(inner, backdrop);

            var layered = new Grid();
            layered.Children.Add(backdrop);
            layered.Children.Add(inner);
            content = layered;
        }

        var tile = new Border
        {
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = content,
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
        if (ClickOpensDetail)
        {
            DetailRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
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

        if (count == 1)
        {
            LayoutSingle(width);
            return;
        }

        if (ShowAll)
        {
            LayoutAll(width, count);
            return;
        }

        var cols = (count == 2 || count == 4) ? 2 : 3;
        var rows = (count + cols - 1) / cols;
        const double gap = 6;

        var cellW = (width - gap * (cols - 1)) / cols;

        // 2 张 = 4:3, 3 张以上 = 方形。多图网格维持 B 站式裁切铺满:
        // 网格要的是"墙面齐整", 完整显示交给单图和详情图集。
        var cellH = count == 2 ? cellW * 0.75 : cellW;

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

    /// <summary>
    /// 单图: 按真实宽高比排版, 不再用固定 4:3 格子 + UniformToFill 裁切。
    ///
    /// ★ 接口没给宽高时(典型: 专栏 `article.covers` 只有 URL)按 16:9 兜底,
    ///   配合 <see cref="Cover.CoverFit.Contain"/> 保证整张图都在。
    /// ★ 卡片里的长图仍限制最高 420, 免得一张竖屏长截图把信息流撑成几屏;
    ///   详情窗不限高, 完整展开(外面本来就有滚动条)。
    /// </summary>
    private void LayoutSingle(double width)
    {
        var img = _images[0];
        var aspect = img.Width > 0 && img.Height > 0 ? (double)img.Width / img.Height : 16.0 / 9.0;
        if (aspect <= 0) aspect = 16.0 / 9.0;

        var maxW = UseDetail ? 720.0 : 320.0;
        var w = Math.Min(width, maxW);
        var h = w / aspect;

        if (!UseDetail && h > 420)
        {
            h = 420;
            w = h * aspect;
        }

        RootGrid.ColumnDefinitions.Clear();
        RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        RootGrid.RowDefinitions.Clear();
        RootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(h) });

        var tile = _tiles[0];
        Grid.SetColumn(tile, 0);
        Grid.SetRow(tile, 0);
        tile.Width = w;
        tile.Height = h;
    }

    /// <summary>
    /// 详情窗的完整图集: 3 列(窄窗口 2 列), 每格按图片自身宽高比定高, 不裁切。
    /// 行高取本行最高的那一格, 短图顶部对齐。
    /// </summary>
    private void LayoutAll(double width, int count)
    {
        const double gap = 6;
        var cols = count == 2 || count == 4 ? 2 : (width >= 480 ? 3 : 2);
        var rows = (count + cols - 1) / cols;
        var cellW = (width - gap * (cols - 1)) / cols;
        if (cellW <= 0) return;

        var rowHeights = new double[rows];
        for (var i = 0; i < count; i++)
        {
            var img = _images[i];
            var aspect = img.Width > 0 && img.Height > 0 ? (double)img.Width / img.Height : 1.0;
            if (aspect <= 0) aspect = 1.0;
            rowHeights[i / cols] = Math.Max(rowHeights[i / cols], cellW / aspect);
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
                Height = new GridLength(r == rows - 1 ? rowHeights[r] : rowHeights[r] + gap)
            });

        for (var i = 0; i < count; i++)
        {
            var img = _images[i];
            var aspect = img.Width > 0 && img.Height > 0 ? (double)img.Width / img.Height : 1.0;
            if (aspect <= 0) aspect = 1.0;

            var tile = _tiles[i];
            Grid.SetColumn(tile, i % cols);
            Grid.SetRow(tile, i / cols);
            tile.Width = cellW;
            tile.Height = cellW / aspect;
            tile.HorizontalAlignment = HorizontalAlignment.Left;
            tile.VerticalAlignment = VerticalAlignment.Top;
        }
    }
}