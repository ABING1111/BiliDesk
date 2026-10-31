using System;
using System.IO;
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
/// ★★★ 2026-10-31 整体重写(用户反馈"只能看到图片中间一部分"修了四轮仍在):
///   旧方案是 Stretch=None + 手算 RenderTransform 缩放比(FitToWindow) —— "完整显示"
///   依赖一串时序前提: 容器 ActualWidth 就绪、位图 DPI 元数据正确、窗口尺寸回调次序,
///   开发机探针全绿但真机任何一环没对上, 图就比容器大, 被 ClipToBounds 裁成中间一块。
///
///   新方案把"完整显示"交给 **WPF 布局引擎**:
///     · Stretch=Uniform —— 测量/排列阶段就把图像按比例缩进 ImageHost, **结构上不可能裁剪**;
///       窗口尺寸变化时布局自动重排, 不需要任何 SizeChanged 回调;
///     · 小图不放大 —— Image 的 MaxWidth/MaxHeight 在加载完成时钉到图片自然 DIP 尺寸
///       (img.Width/Height), Uniform 只会"缩小到能放下", 不会把 200px 头像撑满窗口;
///     · 滚轮缩放/拖动平移是叠加在布局结果之上的 RenderTransform, 双击随时回到
///       "必然完整"的布局基线(清掉全部变换)。
/// </summary>
public partial class ImagePreviewWindow : FluentWindow
{
    /// <summary>加载 URL(保存用)。空串 = 无图。</summary>
    private string _imageUrl = "";
    private string _suggestedName = "image";

    /// <summary>
    /// 缩放平移变换。★ 挂在 **ScrollContentPresenter 里的内容层**上没有意义 ——
    /// 这里直接用 Canvas 布局 + RenderTransform 的经典组合: Image 在 Canvas 里
    /// 只吃 Canvas 的可用空间, 变换只在渲染时生效, 不触发布局重算, 滚轮缩放才跟手。
    /// </summary>
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _trans = new(0, 0);
    private Point _dragStart;
    private bool _dragging;
    /// <summary>图片自然 DIP 尺寸(加载后钉 MaxWidth/MaxHeight 用, 0 = 未加载)。</summary>
    private double _natW, _natH;

    public ImagePreviewWindow()
    {
        InitializeComponent();

        var group = new TransformGroup();
        group.Children.Add(_scale);
        group.Children.Add(_trans);
        PreviewImage.RenderTransform = group;
        // 缩放绕元素自身中心: 平移钳制的"±(渲染尺寸-容器)/2"公式才成立
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
    /// CoverLoader 会先查磁盘缓存(列表里显示过的图几乎瞬时命中), 再走网络下载。
    /// </summary>
    /// <param name="url">图片地址(封面/头像/评论图)</param>
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

        ShowImage(img);
    }

    /// <summary>
    /// 把解好的位图放上屏。
    /// ★ 布局契约: Uniform + MaxWidth/MaxHeight=自然 DIP 尺寸 ⇒
    ///   大图被等比缩到容器内(完整), 小图保持原尺寸(不糊), 两者都居中。
    ///   这里**没有任何手算比例** —— "完整显示"由 WPF 测量/排列保证。
    /// </summary>
    private void ShowImage(BitmapSource img)
    {
        // 自然 DIP 尺寸: img.Width/Height 是 WPF 按 DPI 元数据换算好的显示尺寸,
        // 与 ActualWidth 同一把尺子; 拿不到(=0)时退回像素数(96 DPI 假设, 与旧行为一致)。
        _natW = img.Width > 0 ? img.Width : img.PixelWidth;
        _natH = img.Height > 0 ? img.Height : img.PixelHeight;

        PreviewImage.Source = img;
        PreviewImage.MaxWidth = _natW;
        PreviewImage.MaxHeight = _natH;
        PreviewImage.Stretch = Stretch.Uniform;
        HintText.Visibility = Visibility.Collapsed;

        ResetView();
    }

    /// <summary>
    /// 回到布局基线: 清掉全部缩放/平移, 让 Uniform 自己把图完整摆进容器。
    /// 双击 / 加载完成 / 尺寸变化后都走这里 —— 它是"必然完整"的那一档。
    /// </summary>
    private void ResetView()
    {
        _scale.ScaleX = 1;
        _scale.ScaleY = 1;
        _trans.X = 0;
        _trans.Y = 0;
        UpdateZoomText(1.0);
    }

    private void UpdateZoomText(double s)
    {
        if (ZoomText != null) ZoomText.Text = (int)Math.Round(s * 100) + "%";
    }

    // ------------------------------------------------------------ 缩放 / 平移

    private void ApplyScale(double s)
    {
        // 下限动态化: 当前 scale 已低于 0.1(Fit 出来的)时以当前值为下限,
        // 否则滚轮"缩小"会被 clamp 弹回, 表现为越滚越大。
        var min = Math.Min(0.1, _scale.ScaleX);
        s = Math.Clamp(s, min, 8.0);
        _scale.ScaleX = s;
        _scale.ScaleY = s;
        UpdateZoomText(s);
        ClampTranslation();
    }

    /// <summary>
    /// 把平移钳制到有效范围: 图比容器小 ⇒ 必须居中(trans=0);
    /// 比容器大 ⇒ 平移不得把图边缘拖进容器内。任何操作序列之后,
    /// 图要么完整居中、要么边缘贴边, 永远能拖回完整视图。
    /// ★ 注意 Uniform 基线下"图比容器小"是常态(大图被布局缩小后), 钳制保证
    ///   缩放不会把图推出视野。
    /// </summary>
    private void ClampTranslation()
    {
        if (_natW <= 0 || _natH <= 0) return;
        var hostW = ImageHost.ActualWidth;
        var hostH = ImageHost.ActualHeight;
        if (hostW <= 0 || hostH <= 0) return;

        // 布局尺寸 = Uniform 缩进容器后的尺寸(≤ 自然尺寸), 渲染尺寸 = 布局 × scale。
        // PreviewImage.ActualWidth 就是布局算好的, 不需要自己再算一遍 Uniform 比例。
        var lw = PreviewImage.ActualWidth;
        var lh = PreviewImage.ActualHeight;
        if (lw <= 0 || lh <= 0) { _trans.X = 0; _trans.Y = 0; return; }

        var rw = lw * _scale.ScaleX;
        var rh = lh * _scale.ScaleY;

        if (rw <= hostW) _trans.X = 0;
        else _trans.X = Math.Clamp(_trans.X, -(rw - hostW) / 2, (rw - hostW) / 2);

        if (rh <= hostH) _trans.Y = 0;
        else _trans.Y = Math.Clamp(_trans.Y, -(rh - hostH) / 2, (rh - hostH) / 2);
    }

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
            // 双击在"布局基线(必然完整)"与真 1:1 之间切换:
            // 基线时布局可能把大图缩小了, 真 1:1 = 放大回自然像素尺寸(渲染尺寸=自然 DIP);
            // 已不在基线则回基线。小图布局就是自然尺寸, 双击无可切(基线即 1:1)。
            if (Math.Abs(_scale.ScaleX - 1.0) < 0.01)
            {
                var lw = PreviewImage.ActualWidth;
                if (lw > 0 && _natW > lw) ApplyScale(_natW / lw);
            }
            else
                ResetView();
            return;
        }
        _dragging = true;
        _dragStart = e.GetPosition(this);
        PreviewImage.CaptureMouse();
        PreviewImage.Cursor = Cursors.SizeAll;
    }

    private void OnImageMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging) ClampTranslation();
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
