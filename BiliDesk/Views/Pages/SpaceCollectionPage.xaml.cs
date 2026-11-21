using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

/// <summary>合集 / 系列详情页(共用一个页面, 接口由 <see cref="SpaceCollectionItem.Kind"/> 决定)。</summary>
public partial class SpaceCollectionPage : UserControl
{
    private SpaceCollectionViewModel? _vm;
    private bool _initialized;

    /// <summary>顶部信息区的自动收起/展开(与 UP 主主页共用同一套逻辑)。</summary>
    private readonly HeaderAutoHideController _headerAutoHide;

    public SpaceCollectionPage()
    {
        InitializeComponent();
        _headerAutoHide = new HeaderAutoHideController(HeaderHost);
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    public SpaceCollectionPage(SpaceCollectionItem item, string upName = "")
        : this()
    {
        DataContext = new SpaceCollectionViewModel(item, upName);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 从更深的内嵌页返回时, 头部可能还停在收起状态: 重新进场先恢复展开。
        _headerAutoHide.Reset();
        if (DataContext is not SpaceCollectionViewModel vm) return;
        Attach(vm);
        // Loaded 可能因内嵌页重新进场而多次触发: 只在第一次真正拉数据
        if (_initialized) return;
        _initialized = true;
        await vm.InitAsync();
    }

    private void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
        => _headerAutoHide.HandleScroll(e.VerticalOffset, e.ExtentHeight, e.ViewportHeight);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Attach(e.NewValue as SpaceCollectionViewModel);
        _initialized = false;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Attach(null);

    private void Attach(SpaceCollectionViewModel? vm)
    {
        if (_vm == vm) return;
        if (_vm != null) _vm.ScrollToTopRequested -= OnScrollToTopRequested;
        _vm = vm;
        if (vm == null) return;
        vm.ScrollToTopRequested += OnScrollToTopRequested;
    }

    private void OnScrollToTopRequested()
        => Dispatcher.BeginInvoke(new Action(() => ListScroll?.ScrollToTop()),
            DispatcherPriority.Loaded);
}
