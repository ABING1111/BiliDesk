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

    /// <summary>
    /// 顶部 Tab 胶囊的折叠吸顶。算法抽在 <see cref="StickyPin"/>(收藏页/历史页共用同一份),
    /// 构造时就要建好 —— 它从胶囊的 XAML Margin 里取水平边距与初始自然顶边。
    /// </summary>
    private readonly StickyPin _sticky;

    public SettingsPage()
    {
        InitializeComponent();
        _sticky = new StickyPin(PageScroll, CapsuleSlot, StickyCapsule, StickyCapsuleShift);
        Loaded += OnLoaded;
        // 页面常驻 PageLayer、只切 Visibility: 切回来时 Loaded 不会再发, 补量一次自然位置。
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) _sticky.SyncDeferred(); };
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
        // 吸顶胶囊: 等布局落定后量一次它的自然位置(见 StickyPin.Sync)
        _sticky.SyncDeferred();
    }

    /// <summary>
    /// Tab 胶囊吸顶(2026-11-14 用户要求"只折叠吸顶胶囊, 且不要改变胶囊的大小"):
    /// 胶囊是滚动内容里的**唯一实例**, 滚动多少就用 TranslateTransform 反向抵消多少 ——
    /// 视觉上先跟着内容一起上滑, 到距顶端 8px 处钉住; 大标题照常滚走。
    /// ★ 只改 RenderTransform, 不碰布局: 每帧不触发测量/排版。实现在 <see cref="StickyPin"/>。
    /// </summary>
    private void OnSettingsScrollChanged(object sender, ScrollChangedEventArgs e) => _sticky.Update();

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