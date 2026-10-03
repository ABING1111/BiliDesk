using System;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

/// <summary>
/// 收藏页: 只展示 B 站账号的云端收藏夹。
/// 本机收藏夹(本地 JSON)已整体移除 —— 两套互不相通的数据源只会让人困惑,
/// 播放页的"收藏"现在也直接保存到云端。
/// </summary>
public partial class FavoritesPage : UserControl
{
    private FavoritesViewModel? Vm => DataContext as FavoritesViewModel;

    /// <summary>懒加载: 只在首次显示时拉一次, 避免每次切页都重新请求</summary>
    private bool _loadedOnce;

    /// <summary>夹子集合变化的订阅目标(切 DataContext 时退订旧的)</summary>
    private System.Collections.ObjectModel.ObservableCollection<FavFolder>? _observedFolders;

    public FavoritesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += (_, _) => ScheduleOverflowUpdate();
        // 纳入全局屏蔽(命中关键词的内容会被隐藏), 见 FilterService
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is FavoritesViewModel vm) FilterService.Instance.Attach(vm.Items);
            if (e.OldValue is FavoritesViewModel old && _observedFolders != null)
                _observedFolders.CollectionChanged -= OnFoldersChanged;
            _observedFolders = null;
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        HookFolderCollection();
        ScheduleOverflowUpdate();
        // 已经拿到内容就不再重复请求(页面会被主窗口反复挂载/卸载);
        // 但"上次没登录 / 请求失败"这两种情况要再给一次机会, 否则用户登录后回到这一页
        // 只能看到旧错误提示, 必须手动点刷新。
        if (_loadedOnce && (Vm.Folders.Count > 0 || Vm.Items.Count > 0)) return;
        _loadedOnce = true;
        await Vm.LoadAsync();
    }

    /// <summary>
    /// 订阅收藏夹集合变化(加载完成 / 追加分页都会改它): 每次都要重算折叠。
    /// 集合实例可能被 VM 整个换掉, 所以这里按当前实例挂/退订。
    /// </summary>
    private void HookFolderCollection()
    {
        if (Vm == null) return;
        if (ReferenceEquals(_observedFolders, Vm.Folders)) return;
        if (_observedFolders != null) _observedFolders.CollectionChanged -= OnFoldersChanged;
        _observedFolders = Vm.Folders;
        _observedFolders.CollectionChanged += OnFoldersChanged;
    }

    private void OnFoldersChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => ScheduleOverflowUpdate();

    /// <summary>夹子条的文字宽度全部按这个字号/字体估 —— 与 chip 样式(SettingsTabStyle)一致</summary>
    private const double ChipFontSize = 13;
    /// <summary>chip 的左右内边距(SettingsTabStyle Padding 20,8)+ 描边 + 右间距 4 的合计</summary>
    private const double ChipChromeWidth = 20 * 2 + 4 + 4;

    /// <summary>单条 DisplayText 的测量结果缓存(文本 → 像素宽), 夹子多时省重复排版</summary>
    private readonly System.Collections.Generic.Dictionary<string, double> _textWidthCache = new();

    /// <summary>折叠重算的合并节流: 连续变化(加载分页时一次进好几条)只算最后一遍</summary>
    private bool _overflowUpdatePending;

    private void ScheduleOverflowUpdate()
    {
        if (_overflowUpdatePending) return;
        _overflowUpdatePending = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            _overflowUpdatePending = false;
            UpdateFolderOverflow();
        }));
    }

    // ------------------------------------------------------------ 收藏夹溢出折叠

    /// <summary>主条上显示的收藏夹(划分在 UpdateFolderOverflow 完成)</summary>
    public System.Collections.ObjectModel.ObservableCollection<FavFolder> VisibleFolders { get; } = new();

    /// <summary>被折叠进「⋯」浮层的那部分收藏夹</summary>
    public System.Collections.ObjectModel.ObservableCollection<FavFolder> HiddenFolders { get; } = new();

    /// <summary>
    /// 把 Folders 划分成 主条(VisibleFolders) / 浮层(HiddenFolders)。
    ///
    /// ★ 第二版(2026-10-03): 第一版"裁剪 + 视觉树量宽"在夹子被删除后重算时,
    ///   视觉树给的是过期宽度, 主条会整个空掉只剩按钮 —— 不可救药, 换成
    ///   **纯文本测量 + 集合划分**: 不碰视觉树, 每颗 chip 宽度 = 文字宽 + 固定 chrome,
    ///   结果与布局引擎实测一致(同字体同字号), 且天然没有"半颗 chip"问题。
    /// </summary>
    private void UpdateFolderOverflow()
    {
        if (Vm == null || FolderBarHost.ActualWidth <= 0) return;

        var availW = FolderBarHost.ActualWidth;

        // 「⋯」按钮自己的宽度(有折叠才显示, 但估算时先按"显示"算, 少一颗也比溢出强)
        var moreW = 46;

        var acc = 0.0;
        var firstOverflowIdx = -1;   // 第一颗放不下的夹子下标
        for (var i = 0; i < Vm.Folders.Count; i++)
        {
            acc += MeasureChip(Vm.Folders[i].DisplayText);
            if (acc > availW - moreW && firstOverflowIdx < 0)
            {
                firstOverflowIdx = i;
            }
        }

        // 全放得下: 主条 = 全部
        if (firstOverflowIdx < 0)
        {
            ReplaceIfChanged(VisibleFolders, Vm.Folders);
            HiddenFolders.Clear();
            FolderMoreButton.Visibility = Visibility.Collapsed;
            FolderMoreDot.Visibility = Visibility.Collapsed;
            return;
        }

        // 放不下: [0, firstOverflowIdx) 进主条, 其余进浮层
        VisibleFolders.Clear();
        for (var i = 0; i < firstOverflowIdx; i++)
            VisibleFolders.Add(Vm.Folders[i]);

        HiddenFolders.Clear();
        for (var i = firstOverflowIdx; i < Vm.Folders.Count; i++)
            HiddenFolders.Add(Vm.Folders[i]);

        FolderMoreButton.Visibility = Visibility.Visible;

        // 红点 = 当前选中的夹子被折进了浮层(别让用户以为它丢了)
        var dotVisible = false;
        for (var i = firstOverflowIdx; i < Vm.Folders.Count; i++)
        {
            if (Vm.Folders[i].IsCurrent) { dotVisible = true; break; }
        }
        FolderMoreDot.Visibility = dotVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>量一颗 chip 的总宽(文字 + 内边距 + 间距)。文本宽有缓存。</summary>
    private double MeasureChip(string text)
    {
        if (!_textWidthCache.TryGetValue(text, out var w))
        {
            var typeface = new Typeface(FontFamily.Source);
            var ft = new FormattedText(
                text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, ChipFontSize, Brushes.Black,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            w = ft.Width;
            _textWidthCache[text] = w;
        }
        return w + ChipChromeWidth;
    }

    /// <summary>集合内容替换(引用不同但内容相同的项不重复 Add, 减少容器重建)</summary>
    private static void ReplaceIfChanged(
        System.Collections.ObjectModel.ObservableCollection<FavFolder> target,
        System.Collections.ObjectModel.ObservableCollection<FavFolder> source)
    {
        if (target.Count == source.Count)
        {
            var same = true;
            for (var i = 0; i < source.Count; i++)
            {
                if (!ReferenceEquals(target[i], source[i])) { same = false; break; }
            }
            if (same) return;
        }
        target.Clear();
        foreach (var f in source) target.Add(f);
    }

    private void OnFolderMoreClick(object sender, RoutedEventArgs e)
        => FolderMorePopup.IsOpen = !FolderMorePopup.IsOpen;

    /// <summary>浮层里点选了某个夹子: 收起浮层(选中本身由 SelectFolderCommand 完成)。</summary>
    private void OnFolderPopupChipClick(object sender, RoutedEventArgs e)
        => FolderMorePopup.IsOpen = false;

    /// <summary>右上角返回按钮: 历史/收藏/缓存的入口都在「我的」页, 统一回那里</summary>
    private void OnBackToMineClick(object sender, RoutedEventArgs e)
        => App.MainVm.Navigate(PageKey.Mine);

    /// <summary>列表滚动到底部自动加载下一页</summary>
    private async void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeight > 0 && e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 120
            && Vm is { HasMore: true } vm)
        {
            await vm.LoadMoreAsync();
        }
    }
}
