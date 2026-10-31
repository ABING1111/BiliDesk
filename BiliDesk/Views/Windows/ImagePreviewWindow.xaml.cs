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
    ///
    /// ★★ 尺寸必须用 **DIP(设备无关像素)** 而不是 PixelWidth(2026-10-31 修"窗口小的时候
    /// 显示不全"): BitmapImage 的 DPI 元数据不是恒为 96 —— 截图工具保存的 PNG 带 72/120/144
    /// 都见过。DPI≠96 时 WPF 的显示尺寸 = PixelWidth × 96/DPI, 直接拿像素数算适配比例
    /// 就系统性偏大, 图永远比窗口大一圈。Width/Height 属性已经是 WPF 算好的 DIP 尺寸,
    /// 直接用它, 与窗口的 ActualWidth 同一把尺子。
    /// </summary>
    private void FitToWindow(BitmapSource img)
    {
        // ★ 容器还没完成布局(ActualWidth=0)时**推迟适配**, 而不是用保守兜底值:
        //   Show() 后同步命中内存缓存时, LoadAsync 在首帧布局前就跑到这里 ——
        //   兜底 900x600 算出的比例与真实窗口无关, 窗口一大图就只占中间一小块,
        //   窗口一小则又显示不全。推迟到 Loaded 优先级, ImageHost 尺寸就绪后再算。
        if (ImageHost.ActualWidth <= 0 || ImageHost.ActualHeight <= 0)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ReferenceEquals(PreviewImage.Source, img) && ImageHost.ActualWidth > 0)
                    FitToWindow(img);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }

        var availW = Math.Max(100, ImageHost.ActualWidth - 16);
        var availH = Math.Max(100, ImageHost.ActualHeight - 16);

        // DPI 感知: 拿不到 DIP 尺寸(=0)时退回像素数, 96/DPI 修正只对像素路径生效
        var dipW = img.Width > 0 ? img.Width : img.PixelWidth * 96.0 / img.DpiX;
        var dipH = img.Height > 0 ? img.Height : img.PixelHeight * 96.0 / img.DpiY;
        if (dipW <= 0 || dipH <= 0) return;

        var sx = availW / dipW;
        var sy = availH / dipH;
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
        // ★ 缩放后钳制平移(2026-10-31 修"缩小后仍看到中间裁片"):
        //   拖动过的图 trans 残留非零, 此时缩小, 图心随 origin 保持在偏移处 ——
        //   缩得越小, 图越"缩回"到偏移位置, 用户看到的还是局部而不是整图。
        //   钳制规则: 图比容器小 ⇒ 必须居中(trans=0); 比容器大 ⇒ 平移不得把图边缘
        //   拖进容器内(留边), 即 ±(渲染尺寸-容器尺寸)/2。这样任何操作序列之后
        //   图要么完整居中、要么边缘贴边, 永远能拖回完整视图。
        ClampTranslation();
    }

    /// <summary>
    /// 把平移钳制到有效范围。
    /// ★ 渲染尺寸(图片 DIP × scale) ≤ 容器时图必须**居中** —— 用户截图里的
    ///   "缩到 60% 图还偏向一角、四周黑边"就是这里没钳制, trans 残留导致的。
    /// </summary>
    private void ClampTranslation()
    {
        if (PreviewImage.Source is not BitmapSource img) return;
        var hostW = ImageHost.ActualWidth;
        var hostH = ImageHost.ActualHeight;
        if (hostW <= 0 || hostH <= 0) return;

        var dipW = img.Width > 0 ? img.Width : img.PixelWidth * 96.0 / img.DpiX;
        var dipH = img.Height > 0 ? img.Height : img.PixelHeight * 96.0 / img.DpiY;
        var rw = dipW * _scale.ScaleX;
        var rh = dipH * _scale.ScaleY;

        // 图比容器小: 居中(平移归零)。origin=0.5 时缩放不改元素中心, 平移是唯一偏移源。
        if (rw <= hostW) _trans.X = 0;
        else _trans.X = Math.Clamp(_trans.X, -(rw - hostW) / 2, (rw - hostW) / 2);

        if (rh <= hostH) _trans.Y = 0;
        else _trans.Y = Math.Clamp(_trans.Y, -(rh - hostH) / 2, (rh - hostH) / 2);
    }

    /// <summary>用户有没有手动缩放/拖过图 —— 动过之后窗口尺寸变化就**不再**自动重适配,
    /// 不然用户刚摆好的视图会被拽走。</summary>
    private bool _userAdjusted;

    private bool _fittedOnce;

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (PreviewImage.Source is not BitmapSource b || info.NewSize.Width <= 0) return;

        // ★ 窗口大小变化时自动重适配(2026-10-31 修"不同窗口大小时显示不全"):
        //   适配只发生一次的话, 开窗后拖一拖窗口边框, 图立刻又超出去。
        //   用户手动缩放/拖动过(_userAdjusted)就不打扰 —— 尊重用户自己摆的视图。
        //   双击可以随时回到完整视图。
        if (!_fittedOnce)
        {
            _fittedOnce = true;
            FitToWindow(b);
        }
        else if (!_userAdjusted)
        {
            // ★ 推到渲染队列之后再适配: SizeChanged 这一刻 ImageHost 的 ActualWidth/Height
            //   可能还停留在旧布局, 直接算会按旧尺寸适配(大→小缩窗时图还是超的)。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (PreviewImage.Source is BitmapSource b2 && !_userAdjusted)
                    FitToWindow(b2);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    // ------------------------------------------------------------ 交互

    private void OnImageWheel(object sender, MouseWheelEventArgs e)
    {
        var factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
        ApplyScale(_scale.ScaleX * factor);
        _userAdjusted = true;
        e.Handled = true;
    }

    private void OnImageMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            // 双击在"完整视图"与 1:1 之间切换(2026-10-31: 完整视图替换原来写死的 0.5 ——
            // 小窗口里 0.5 一样显示不全, 双击就该回到"肯定能看全"的那一档)
            if (Math.Abs(_scale.ScaleX - 1.0) < 0.01)
            {
                if (PreviewImage.Source is BitmapSource b) FitToWindow(b);
            }
            else
            {
                ApplyScale(1.0);
            }
            _userAdjusted = false;   // 回到自适应档: 窗口再变化继续跟随
            return;
        }
        _dragging = true;
        _dragStart = e.GetPosition(this);
        PreviewImage.CaptureMouse();
        PreviewImage.Cursor = Cursors.SizeAll;
    }

    private void OnImageMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            // 拖动结束钳制: 图比容器小时回到居中, 比容器大时不许把边缘拖进容器内 ——
            // 保证"想看全图"永远只需要往回拖, 图不会整个跑出视野找不回来。
            ClampTranslation();
        }
        _dragging = false;
        PreviewImage.ReleaseMouseCapture();
        PreviewImage.Cursor = Cursors.Hand;
    }

    private void OnImageMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStart.X) > 2 || Math.Abs(p.Y - _dragStart.Y) > 2)
            _userAdjusted = true;
        _trans.X += p.X - _dragStart.X;
        _trans.Y += p.Y - _dragStart.Y;
        _dragStart = p;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.OemPlus or Key.Add: ApplyScale(_scale.ScaleX * 1.15); _userAdjusted = true; break;
            case Key.OemMinus or Key.Subtract: ApplyScale(_scale.ScaleX / 1.15); _userAdjusted = true; break;
            case Key.D0 or Key.NumPad0: ApplyScale(1.0); _userAdjusted = true; break;
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
