using System;
using System.Collections.Specialized;
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
        SizeChanged += (_, _) => UpdateFolderOverflow();
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
        => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
            UpdateFolderOverflow);

    // ------------------------------------------------------------ 收藏夹溢出折叠

    /// <summary>
    /// 重算"主条能放下几颗 chip": 放不下时显示「⋯」按钮, 并把主条宽度钳到
    /// 按钮左侧 —— 超出的 chips 由 Clip 裁掉(它们仍然存在于可视树, 但看不见)。
    /// 布局必须已经跑过才有可信的 ActualWidth, 调用方负责排到 Loaded 优先级。
    /// </summary>
    private void UpdateFolderOverflow()
    {
        if (FolderChips.Items.Count == 0 || FolderBarHost.ActualWidth <= 0) return;

        // 量 chips 总宽: 让容器先按无限宽测量
        FolderBarClip.Width = double.NaN;
        FolderChips.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var totalW = FolderChips.DesiredSize.Width;
        var availW = FolderBarHost.ActualWidth;

        // 放得下: 全部显示, 按钮藏掉
        if (totalW <= availW)
        {
            FolderMoreButton.Visibility = Visibility.Collapsed;
            FolderMoreDot.Visibility = Visibility.Collapsed;
            FolderBarClip.Width = double.NaN;
            FolderBarClip.Clip = null;
            return;
        }

        // 放不下: 按钮占一份宽, 主条钳到剩余宽度
        FolderMoreButton.Visibility = Visibility.Visible;
        FolderMoreButton.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var moreW = FolderMoreButton.DesiredSize.Width;
        var clipW = Math.Max(80, availW - moreW - 4);

        // 找出"宽度累积超过 clipW"的第一颗 chip: 它之前(不含)的都显示。
        // chip 宽度要从生成的容器(ContentPresenter)里取 —— 直接遍历 ItemsHost。
        var acc = 0.0;
        var visibleCount = FolderChips.Items.Count;
        var host = FindItemsHost(FolderChips);
        if (host != null)
        {
            visibleCount = host.Children.Count;
            for (var i = 0; i < host.Children.Count; i++)
            {
                if (host.Children[i] is not FrameworkElement fe) continue;
                var w = fe.ActualWidth > 0 ? fe.ActualWidth : fe.DesiredSize.Width;
                acc += w;
                if (acc > clipW)
                {
                    visibleCount = i;
                    break;
                }
            }
        }

        FolderBarClip.Width = Math.Min(clipW, acc > 0 ? acc : clipW);
        ApplyRoundedClip(FolderBarClip, 18);

        // 红点 = 当前选中的夹子排不进主条(它藏在浮层里)
        var currentIdx = -1;
        for (var i = 0; i < FolderChips.Items.Count; i++)
        {
            if (FolderChips.Items[i] is FavFolder f && f.IsCurrent) { currentIdx = i; break; }
        }
        FolderMoreDot.Visibility = currentIdx >= visibleCount ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>找到 ItemsControl 生成的横向 ItemsHost(StackPanel)。</summary>
    private static StackPanel? FindItemsHost(DependencyObject root)
    {
        if (root is StackPanel { Orientation: System.Windows.Controls.Orientation.Horizontal } sp
            && sp.Parent is ItemsPresenter) return sp;
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var found = FindItemsHost(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>给主条加圆角裁剪: 裁掉超宽部分的同时保留胶囊右缘的圆角观感。</summary>
    private static void ApplyRoundedClip(UIElement host, double radius)
    {
        if (host is FrameworkElement fe && fe.ActualWidth > 0)
        {
            fe.Clip = new RectangleGeometry(
                new Rect(0, 0, fe.ActualWidth, fe.ActualHeight), radius, radius);
        }
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
