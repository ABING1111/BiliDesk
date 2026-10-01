using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.Views.Windows;

/// <summary>
/// 图片预览窗: 双击/右键"查看大图"时打开, 支持缩放/拖动/另存为。
///
/// 设计取舍:
///   - 用 WindowStyle=None + 自绘标题栏, 让图片区域能铺满整个窗口(看图时边框越少越好);
///   - 缩放用 RenderTransform 上的 ScaleTransform, 而不是改 Image 尺寸 —— 前者只影响渲染,
///     不触发布局重算, 滚轮缩放才能跟手;
///   - 读取用"先查本地缓存, 再下载"。封面/头像在列表里显示时已经落过盘了, 这里直接复用,
///     点开大图几乎瞬时可见, 不需要再等一次网络往返。
/// </summary>
public partial class ImagePreviewWindow : FluentWindow
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _trans = new(0, 0);
    private string _imageUrl = "";
    private string _suggestedName = "image";
    private Point _dragStart;
    private bool _dragging;

    static ImagePreviewWindow()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(HttpDefaults.UserAgent);
    }

    public ImagePreviewWindow()
    {
        InitializeComponent();

        var group = new TransformGroup();
        group.Children.Add(_scale);
        group.Children.Add(_trans);
        PreviewImage.RenderTransform = group;

        // ★ 缩放中心必须是**元素自身的中心**, 不能是默认的左上角(0,0)。
        //
        // 为什么这是"封面显示不全甚至看不到"的根因:
        // Image 是 Stretch=None, 它的布局尺寸就是图片的**原始像素尺寸**(比如 1920×1080),
        // 然后由 HorizontalAlignment=Center 把这个"大框"摆在容器中间 —— 一半在容器外。
        // 缩放默认绕左上角进行, 而那个左上角本来就在容器外侧:
        //   1920 宽的图缩到 0.49 倍后, 可见区域落在容器右下角, 左上半张图直接跑到屏幕外。
        // 图片越大偏得越狠(4K 封面几乎只剩一个角), 用户看到的就是"显示不全 / 不显示"。
        // 把原点设成中心之后, 缩放后的图像仍然居中, 整张图都看得见。
        PreviewImage.RenderTransformOrigin = new Point(0.5, 0.5);

        PreviewImage.MouseWheel += OnImageWheel;
        PreviewImage.MouseLeftButtonDown += OnImageMouseDown;
        PreviewImage.MouseLeftButtonUp += OnImageMouseUp;
        PreviewImage.MouseMove += OnImageMouseMove;
        PreviewImage.MouseRightButtonUp += (_, _) => Close();
        KeyDown += OnKey;
    }

    /// <summary>
    /// 加载并显示图片。
    /// decodeWidth=4096: 超大原图(动态/专栏长图能到 4 万像素)全尺寸解码会直接失败或吃掉几个 GB,
    /// 这里给解码宽度上限 —— WIC 的 DecodePixelWidth 只在原图更大时降采样, 小图完全不受影响;
    /// 磁盘缓存按 URL 存原字节, 与列表共用同一份文件, 不会因此重新下载。
    /// </summary>
    /// <param name="url">图片地址(封面/头像)</param>
    /// <param name="title">窗口标题(通常传视频标题或 UP 名, 便于"另存为"时起个有意义的名字)</param>
    public async Task LoadAsync(string url, string? title = null)
    {
        _imageUrl = UrlUtil.Normalize(url);
        _suggestedName = FileNameUtil.Sanitize(
            string.IsNullOrWhiteSpace(title) ? "image" : title!, "image");

        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "图片预览" : title!;
        if (_imageUrl.Length == 0)
        {
            HintText.Text = "没有可显示的图片";
            return;
        }

        var img = await CoverLoader.LoadAsync(_imageUrl, 4096);
        if (img == null)
        {
            HintText.Text = "图片加载失败";
            return;
        }

        PreviewImage.Source = img;
        HintText.Visibility = Visibility.Collapsed;

        // 图片比窗口大时先缩到能完整看见, 避免一打开只看到局部
        FitToWindow(img);
    }

    /// <summary>
    /// 把图片缩放到刚好完整显示在窗口内。
    /// ★ 这里必须**绕过 ApplyScale 的 0.1 下限**: 超大图(比如 11520x8640 的动态长图)
    /// 的适配比例约 0.07, 被 clamp 抬回 0.1 后渲染尺寸(1152x864)仍比窗口大 ——
    /// 用户看到的就是"只有中心一小块", 即真机反馈的"封面显示不全"。
    /// 初始适配的目标就是"完整可见", 多小都合法。
    /// </summary>
    private void FitToWindow(BitmapSource img)
    {
        var availW = Math.Max(100, ActualWidth > 0 ? ActualWidth - 40 : 900);
        var availH = Math.Max(100, ActualHeight > 0 ? ActualHeight - 100 : 600);
        var sx = availW / img.PixelWidth;
        var sy = availH / img.PixelHeight;
        var s = Math.Min(1.0, Math.Min(sx, sy));
        _scale.ScaleX = s;
        _scale.ScaleY = s;
        if (ZoomText != null) ZoomText.Text = (int)Math.Round(s * 100) + "%";
        _trans.X = 0;
        _trans.Y = 0;
    }

    private void ApplyScale(double s)
    {
        // 下限动态化: 当前 scale 已经低于 0.1(超大图 Fit 出来的)时, 以当前值为下限 ——
        // 否则滚轮"缩小"会被 clamp 弹回 0.1, 表现为越滚越大。
        var min = Math.Min(0.1, _scale.ScaleX);
        s = Math.Clamp(s, min, 8.0);
        _scale.ScaleX = s;
        _scale.ScaleY = s;
        if (ZoomText != null) ZoomText.Text = (int)Math.Round(s * 100) + "%";
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        // 首次布局完成后如果还没适配过, 补一次适配(构造时 ActualWidth 还是 0)
        if (!_fittedOnce && PreviewImage.Source is BitmapSource b && info.NewSize.Width > 0)
        {
            _fittedOnce = true;
            FitToWindow(b);
        }
    }

    private bool _fittedOnce;

    // ------------------------------------------------------------ 交互

    private void OnImageWheel(object sender, MouseWheelEventArgs e)
    {
        var factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
        ApplyScale(_scale.ScaleX * factor);
        e.Handled = true;
    }

    private void OnImageMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            // 双击在 1:1 与自适应之间切换
            ApplyScale(Math.Abs(_scale.ScaleX - 1.0) < 0.01 ? 0.5 : 1.0);
            return;
        }
        _dragging = true;
        _dragStart = e.GetPosition(this);
        PreviewImage.CaptureMouse();
        PreviewImage.Cursor = Cursors.SizeAll;
    }

    private void OnImageMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        PreviewImage.ReleaseMouseCapture();
        PreviewImage.Cursor = Cursors.Hand;
    }

    private void OnImageMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(this);
        _trans.X += p.X - _dragStart.X;
        _trans.Y += p.Y - _dragStart.Y;
        _dragStart = p;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.OemPlus or Key.Add: ApplyScale(_scale.ScaleX * 1.15); break;
            case Key.OemMinus or Key.Subtract: ApplyScale(_scale.ScaleX / 1.15); break;
            case Key.D0 or Key.NumPad0: ApplyScale(1.0); break;
        }
    }

    // ------------------------------------------------------------ 保存

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_imageUrl.Length == 0) return;
        SaveButton.IsEnabled = false;
        try
        {
            var path = await ImageDownloader.SaveAsync(_imageUrl, _suggestedName);
            if (path != null)
            {
                Svc.Toast.Show("已保存到 " + Path.GetFileName(path));
                HintText.Text = "已保存: " + path;
                HintText.Visibility = Visibility.Visible;
            }
            else
            {
                Svc.Toast.Show("保存失败, 请检查网络或磁盘权限");
            }
        }
        catch (Exception ex)
        {
            Svc.Toast.Show("保存失败: " + ex.Message);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
