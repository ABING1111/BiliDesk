using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.Views;

/// <summary>
/// 下载选择窗(播放器"下载"按钮 / 卡片右键"下载视频到本地"弹出的模态对话框)。
///
/// 三件事:
///   1. **选清晰度** —— 列出接口这次真实返回的档位(可能是 8K/4K/1080P, 也可能只有 durl 的 720P 单流),
///      默认选中"当前播放清晰度", 没有就取不高于它的最高档;
///   2. **提示保存位置** —— 用户明确要求"下载前告诉我会存到哪", 所以完整路径 + 预计文件名常驻显示;
///      旁边的「更改…」直接改全局下载位置(与设置页同一项), 「打开」直接开目录;
///   3. **只负责收集选择** —— 真正的下载由调用方拿到 <see cref="Request"/> 后交给
///      <see cref="VideoDownloader"/>, 窗口立刻关闭。下载进度走右上角独立进度条。
///
/// ★ 不做片段裁剪(用户 2026-11-14 明确: 先只做清晰度 + 位置)。
/// </summary>
public partial class DownloadWindow : FluentWindow
{
    private readonly string _bvid;
    private readonly long _cid;
    private readonly string _title;
    private readonly int _preferredQn;

    private DownloadOptions? _options;
    private DownloadQualityOption? _selected;
    private bool _loading;

    /// <summary>用户点了「开始下载」后组装好的任务; null = 取消/没选。</summary>
    public VideoDownloadRequest? Request { get; private set; }

    /// <param name="preferredQn">默认选中的清晰度(播放器传当前实际清晰度; 列表页传设置里的默认画质)</param>
    public DownloadWindow(string bvid, long cid, string? title, int preferredQn)
    {
        InitializeComponent();
        _bvid = bvid;
        _cid = cid;
        _title = title ?? "";
        _preferredQn = preferredQn;

        TitleText.Text = string.IsNullOrWhiteSpace(_title) ? "下载视频" : _title;
        RefreshPathText();
        Loaded += OnLoadedOnce;
    }

    /// <summary>
    /// 在 <c>ShowDialog()</c> **之前**调用: 把高度上限夹进"窗口将要出现的那块显示器"。
    ///
    /// ★ 为什么不在构造函数里做: 那时 <see cref="Window.Owner"/> 还没赋上, 只能退回
    ///   鼠标位置去猜屏幕 —— 播放器在副屏、鼠标在主屏时就会按错的边界夹。
    /// ★ 为什么必须在 Show 之前: `CenterOwner` 在 Show 那一刻按**当时的高度**算位置,
    ///   之后高度再变, 位置不会跟着回来。
    /// </summary>
    public void PrepareBeforeShow()
    {
        // ★★ MaxHeight 的默认值是 PositiveInfinity, **不是 0** —— 不能写 `if (MaxHeight > 0) return;`
        //   那样恒成立, 等于永远不夹(而且编译/运行都不报错, 只是窗口位置又歪了)。
        if (!double.IsInfinity(MaxHeight) && MaxHeight > 0) return;   // 已经夹过
        ClampHeightToWorkArea();
    }

    /// <summary>首次显示: 读清晰度。</summary>
    private void OnLoadedOnce(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOnce;
        // 兜底: 调用方忘了 PrepareBeforeShow 时这里再夹一次(此时 Owner 一定已赋值)
        ClampHeightToWorkArea();
        LoadOptionsAsync().Forget();
    }

    /// <summary>把窗口高度限制在"窗口将要出现的那块显示器的工作区"内(留出标题栏与任务栏)</summary>
    private void ClampHeightToWorkArea()
    {
        try
        {
            var screen = TargetScreen();
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            var workH = screen.WorkingArea.Height / dpi.DpiScaleY;
            // 上下各留一点余量: 圆角窗口贴着屏幕边缘时观感很差, 也避免被 DWM 阴影裁到
            var max = Math.Max(240, workH - 80);
            if (MaxHeight <= 0 || MaxHeight > max) MaxHeight = max;
        }
        catch
        {
            // 拿不到屏幕信息就保持 XAML 尺寸, 不因尺寸优化影响打开
        }
    }

    /// <summary>
    /// 本窗将要出现的那块显示器。
    ///
    /// ★ 不能只用鼠标位置: 播放器在副屏、鼠标停主屏时, 用鼠标那块屏算出来的是**另一块**
    ///   工作区, 居中与夹取都会按错的边界算。有 Owner 就用 Owner 的中心点(与 CenterOwner
    ///   的实际落点一致), 没有才退回鼠标位置。
    /// </summary>
    private System.Windows.Forms.Screen TargetScreen()
    {
        if (Owner is { IsLoaded: true } owner && owner.ActualWidth > 0 && owner.ActualHeight > 0)
        {
            var dpiOwner = System.Windows.Media.VisualTreeHelper.GetDpi(owner);
            var center = new System.Drawing.Point(
                (int)((owner.Left + owner.ActualWidth / 2) * dpiOwner.DpiScaleX),
                (int)((owner.Top + owner.ActualHeight / 2) * dpiOwner.DpiScaleY));
            return System.Windows.Forms.Screen.FromPoint(center);
        }
        return System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
    }

    /// <summary>
    /// 内容高度变化后**重新居中**, 并夹进工作区。
    ///
    /// ★ 为什么 SizeToContent 的窗口必须手动重算一次位置: `CenterOwner` 只在 Show 那一刻
    ///   算一次, 之后窗口变高时只把下边缘往下推。用户看到的就是"窗在上面、按钮掉到屏幕外"。
    ///   这里在正文铺开后再算一次: 居中于 Owner(没有 Owner 就居中于屏幕), 再夹进工作区。
    /// </summary>
    private void ReCenterInWorkArea()
    {
        try
        {
            UpdateLayout();   // 先把新高度结算出来, 否则 ActualHeight 还是旧值

            var screen = TargetScreen();
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            var work = new Rect(
                screen.WorkingArea.Left / dpi.DpiScaleX,
                screen.WorkingArea.Top / dpi.DpiScaleY,
                screen.WorkingArea.Width / dpi.DpiScaleX,
                screen.WorkingArea.Height / dpi.DpiScaleY);

            var w = ActualWidth > 0 ? ActualWidth : Width;
            var h = ActualHeight > 0 ? ActualHeight : 300;

            // 优先居中于 Owner(与 CenterOwner 的语义一致), 否则居中于屏幕工作区
            double left, top;
            if (Owner is { IsLoaded: true } owner && owner.ActualWidth > 0 && owner.ActualHeight > 0)
            {
                var ownerRect = new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight);
                left = ownerRect.Left + (ownerRect.Width - w) / 2;
                top = ownerRect.Top + (ownerRect.Height - h) / 2;
            }
            else
            {
                left = work.Left + (work.Width - w) / 2;
                top = work.Top + (work.Height - h) / 2;
            }

            // 夹进工作区: 窗口比工作区还大时贴左上, 至少保证标题栏与底部按钮都在屏幕内
            left = Math.Max(work.Left, Math.Min(left, work.Right - w));
            top = Math.Max(work.Top, Math.Min(top, work.Bottom - h));

            Left = left;
            Top = top;
        }
        catch
        {
            // 定位失败就保持系统给的初始位置, 不影响使用
        }
    }

    // ------------------------------------------------------------ 读取选项

    private async System.Threading.Tasks.Task LoadOptionsAsync()
    {
        if (_loading) return;
        _loading = true;
        ShowLoading();

        try
        {
            var (ok, err, data) = await Svc.Api.GetDownloadOptionsAsync(_bvid, _cid, _title);
            if (!IsLoaded && !IsVisible) return;   // 用户在加载途中关了窗口

            if (!ok || data == null || data.Qualities.Count == 0)
            {
                ShowError(err ?? "没有可用的下载地址");
                // 错误态的正文也换了长度, 同样重新居中
                ReCenterInWorkArea();
                return;
            }

            _options = data;
            BuildQualityList(data);
            ShowContent();
            // 正文从"加载中"变成完整列表后窗口会变高, 必须重新居中一次(见 ReCenterInWorkArea)
            ReCenterInWorkArea();
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            ShowError("读取失败: " + ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>按接口返回的档位生成单选卡片(从高到低, 默认选中的那张自动打勾)</summary>
    private void BuildQualityList(DownloadOptions options)
    {
        QualityPanel.Children.Clear();
        _selected = DownloadSelection.PickDefault(options.Qualities, _preferredQn);

        foreach (var q in options.Qualities)
        {
            var radio = new RadioButton
            {
                GroupName = "DownloadQuality",
                Style = (Style)FindResource("QualityCardStyle"),
                // ★ Tag 必须排在 IsChecked 之前: IsChecked=true 会**当场**触发 Checked,
                //   而处理器要从 Tag 里读档位。顺序写反了默认档就设不进 _selected,
                //   界面看着已勾选、点"开始下载"却提示"还没有可下载的清晰度"。
                Tag = q,
                Content = BuildQualityContent(q),
                IsChecked = ReferenceEquals(q, _selected)
            };
            radio.Checked += OnQualityChecked;
            QualityPanel.Children.Add(radio);
        }

        // 音轨/单流提示
        if (options.UseMuxed)
        {
            AudioText.Visibility = Visibility.Collapsed;
            FallbackHint.Text = "这个视频没有可用的高清分流, 只能下载音视频已合并的单流文件(最高 720P)。";
            FallbackHint.Visibility = Visibility.Visible;
        }
        else
        {
            AudioText.Text = string.IsNullOrEmpty(options.AudioLabel)
                ? "音轨: 自动选择可用最高音质"
                : $"音轨: {options.AudioLabel}";
            AudioText.Visibility = Visibility.Visible;
            FallbackHint.Visibility = Visibility.Collapsed;
        }

        UpdateFileNameText();
    }

    private UIElement BuildQualityContent(DownloadQualityOption q)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock
        {
            Text = q.Label,
            FontSize = 14.5,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (q.Qn >= 125)
        {
            // 8K / 杜比视界 / HDR: 提醒"下得下来但未必播得好", 免得用户以为文件坏了
            var warn = new TextBlock
            {
                Text = "部分播放器可能不支持",
                FontSize = 11,
                Margin = new Thickness(8, 1, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            warn.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
            left.Children.Add(warn);
        }
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var right = new TextBlock
        {
            Text = string.IsNullOrEmpty(q.CodecLabel) ? q.SizeText : $"{q.CodecLabel} · {q.SizeText}",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        // ★ 用 SetResourceReference 而不是 FindResource 取笔刷: 主题切换时颜色要跟着变,
        //   直接拿实例只是"当下这一帧的颜色"(见 MEMORY 里主题笔刷可变的那套机制)。
        right.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        return grid;
    }

    private void OnQualityChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: DownloadQualityOption q })
            _selected = q;
    }

    // ------------------------------------------------------------ 保存位置

    private void RefreshPathText()
    {
        PathText.Text = AppPaths.DownloadDir;
        PathText.ToolTip = AppPaths.DownloadDir;
        UpdateFileNameText();
    }

    /// <summary>预计文件名(下载器遇到重名会自动加序号, 这里只是让用户有个预期)</summary>
    private void UpdateFileNameText()
    {
        if (FileNameText == null) return;
        var baseName = string.IsNullOrWhiteSpace(_title) ? _bvid : _title;
        var safe = FileNameUtil.Sanitize(baseName, "video");
        FileNameText.Text = $"文件名: {safe}.mp4";
    }

    private void OnChangeFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择下载位置",
                InitialDirectory = AppPaths.DownloadDir,
                Multiselect = false
            };
            if (dialog.ShowDialog() != true) return;

            var effective = Svc.Settings.SetDownloadDir(dialog.FolderName);
            RefreshPathText();
            Svc.Toast.Show("下载位置已改为 " + effective);
        }
        catch (Exception ex)
        {
            Svc.Toast.Show("选择下载位置失败: " + ex.Message);
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = AppPaths.DownloadDir;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Svc.Toast.Show("打开文件夹失败: " + ex.Message);
        }
    }

    // ------------------------------------------------------------ 确认 / 取消

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_options == null || _selected == null)
        {
            Svc.Toast.Show("还没有可下载的清晰度");
            return;
        }

        var useMuxed = _options.UseMuxed;
        Request = new VideoDownloadRequest
        {
            Bvid = _bvid,
            Cid = _cid,
            Title = string.IsNullOrWhiteSpace(_title) ? _bvid : _title,
            Qn = _selected.Qn,
            UseMuxed = useMuxed,
            VideoUrls = useMuxed ? new List<string>() : new List<string>(_selected.VideoUrls),
            MuxedUrls = useMuxed ? new List<string>(_selected.VideoUrls) : new List<string>(),
            AudioUrls = new List<string>(_options.AudioUrls),
            DurationMs = _options.DurationMs,
            EstimatedBytes = _selected.EstimatedBytes,
            EstimatedVideoBytes = _selected.EstimatedVideoBytes,
            EstimatedAudioBytes = _selected.EstimatedAudioBytes,
            OpenFolderWhenDone = OpenFolderCheck.IsChecked == true
        };
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnRetryClick(object sender, RoutedEventArgs e) => LoadOptionsAsync().Forget();

    // ------------------------------------------------------------ 三态切换

    private void ShowLoading()
    {
        LoadingPanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ContentPanel.Visibility = Visibility.Collapsed;
        if (StartButton != null) StartButton.IsEnabled = false;
    }

    private void ShowError(string message)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        ContentPanel.Visibility = Visibility.Collapsed;
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
        if (StartButton != null) StartButton.IsEnabled = false;
    }

    private void ShowContent()
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ContentPanel.Visibility = Visibility.Visible;
        if (StartButton != null) StartButton.IsEnabled = true;
        UpdateFileNameText();
    }
}
