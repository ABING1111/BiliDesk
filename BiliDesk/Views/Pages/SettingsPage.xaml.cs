using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BiliDesk.Helpers;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

public partial class SettingsPage : UserControl
{
    private SettingsViewModel? _vm;
    private bool _subscribed;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm = DataContext as SettingsViewModel;
        if (_vm == null) return;

        _vm.OnNavigated();

        if (!_subscribed)
        {
            _subscribed = true;
            _vm.PropertyChanged += OnVmPropertyChanged;
            // 调色盘拖动起止: 拖的时候跳过全窗口可视树重解析, 松手补一次
            // (理由见 SettingsViewModel.AccentPickerColor 上的说明)
            //
            // ★ 2026-10-03 重做后调色盘在"通用"Tab 的模板里, 切 Tab 会整个换掉可视树,
            //   所以每次 TabIndex 变化后要重新找一次 Palette 实例(见 OnVmPropertyChanged)。
        }
        _ = LoadAvatarAsync(_vm.UserFace);
        HookPalette();
    }

    /// <summary>
    /// 设置页滚动时的折叠吸顶(2026-11-12 用户要求):
    ///   0~24px   大标题与 Tab 随内容自然上滑;
    ///   24~80px  吸顶栏从下方 10px 处滑入 + 淡入; 吸顶栏里的「应用设置」是 14px 小字、
    ///            Tab 也用了更小的内边距, 高度(44)比原头部矮一档 —— 即"减小高度后固定";
    ///   >80px    吸顶栏常驻, 原头部已滚出视口。
    /// ★ 只改 Opacity 与 TranslateTransform, 不碰布局: 每帧不会触发重新测量/排版。
    /// </summary>
    private void OnSettingsScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        const double fadeStart = 24;
        const double fadeEnd = 80;
        var t = System.Math.Clamp((e.VerticalOffset - fadeStart) / (fadeEnd - fadeStart), 0, 1);
        StickyHeader.Opacity = t;
        // ★★★ 透明 ≠ 不挡鼠标: WPF 的命中测试不看 Opacity, Opacity=0 的 Border 照样会吃掉
        //   它 44px 高度内的所有点击。滚回顶部时原头部(标题 + Tab)正好从 y=20 开始、Tab 胶囊
        //   上半部分落在 0~44 这条带里 —— 不关掉命中测试的话, 用户点 Tab 上半截是没反应的。
        StickyHeader.IsHitTestVisible = t > 0.01;
        // ★★★ 关键: 吸顶栏跨在窗口标题栏边界上(主窗口 CaptionHeight=20), 标题栏内的点击
        //   会被 WindowChrome 当成"拖动窗口"吞掉 —— 用户实测"吸顶后点不动/碰撞体积不一致"
        //   就是这个(点击点正好落在标题栏那 20px 上)。IsHitTestVisibleInChrome 让吸顶栏
        //   在标题栏区域内也能收到点击; 只在吸顶栏出现后启用, 否则滚动前会挡住拖窗口。
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(StickyHeader, t > 0.01);
    }

    /// <summary>在当前可视树里找调色盘并接上拖动事件(找不到就是"通用"Tab 不在前, 静默跳过)。</summary>
    private void HookPalette()
    {
        if (_vm == null) return;
        if (FindDescendant<Controls.ColorPalette>(this) is { } palette)
        {
            palette.DragStarted -= _vm.BeginAccentDrag;
            palette.DragEnded -= _vm.EndAccentDrag;
            palette.DragStarted += _vm.BeginAccentDrag;
            palette.DragEnded += _vm.EndAccentDrag;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : class
    {
        if (root is T match) return match;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var found = FindDescendant<T>(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm == null) return;
        if (e.PropertyName == nameof(SettingsViewModel.UserFace))
        {
            _ = LoadAvatarAsync(_vm.UserFace);
        }
        else if (e.PropertyName == nameof(SettingsViewModel.TabIndex))
        {
            // 模板切换发生在绑定通知之后的一帧, 用 Dispatcher 等可视树就绪:
            // · "通用"Tab 需要重新挂调色盘的拖动事件(每次切换都是新的模板实例);
            // · "数据管理"Tab 需要重新加载头像 —— 头像在这份模板里, 切回来时是全新的
            //   Border, 之前的加载结果不会自己贴上去(真机症状: 头像不显示)。
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() =>
                {
                    HookPalette();
                    if (_vm != null) _ = LoadAvatarAsync(_vm.UserFace);
                }));
        }
    }

    /// <summary>
    /// 异步加载头像到圆形容器。
    /// ★ 重做后头像在"数据管理"Tab 的 DataTemplate 里 —— 模板内的元素不是页面的字段,
    ///   每次都要从可视树现找(找不到 = 该 Tab 未显示, 直接放弃; Tab 切回来时
    ///   PropertyChanged(TabIndex)/OnLoaded 会再触发一次)。
    /// </summary>
    private async System.Threading.Tasks.Task LoadAvatarAsync(string? faceUrl)
    {
        var (box, fallback) = FindAvatarElements();
        if (box == null) return;   // 数据管理 Tab 不在前台, 不用加载

        if (string.IsNullOrEmpty(faceUrl))
        {
            box.Background = (Brush)FindResource("AccentSoftFillBrush");
            if (fallback != null) fallback.Visibility = Visibility.Visible;
            return;
        }

        var img = await CoverLoader.LoadAsync(faceUrl, 128);
        if (img == null || _vm == null) return;

        // 模板可能在异步加载期间被换掉, 回来再找一次, 找不到就丢
        var (box2, fallback2) = FindAvatarElements();
        if (box2 == null) return;

        var brush = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
        brush.Freeze();
        box2.Background = brush;
        if (fallback2 != null) fallback2.Visibility = Visibility.Collapsed;
    }

    private (Border? box, TextBlock? fallback) FindAvatarElements()
        => (FindDescendantByName(this, "AvatarBox") as Border,
            FindDescendantByName(this, "AvatarFallback") as TextBlock);

    /// <summary>按名字在可视树里找元素(模板内元素没有页面字段, 只能这么找)。</summary>
    private static FrameworkElement? FindDescendantByName(DependencyObject root, string name)
    {
        if (root is FrameworkElement { Name: var n } && n == name) return root as FrameworkElement;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindDescendantByName(VisualTreeHelper.GetChild(root, i), name);
            if (found != null) return found;
        }
        return null;
    }
}