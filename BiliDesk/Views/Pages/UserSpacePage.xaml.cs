using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

public partial class UserSpacePage : UserControl
{
    /// <summary>当前订阅的 VM(DataContext 可能被换掉, 必须自己记一份以便退订事件)</summary>
    private UserSpaceViewModel? _vm;

    public UserSpacePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    public UserSpacePage(long mid)
    {
        InitializeComponent();
        DataContext = new UserSpaceViewModel();
        if (DataContext is UserSpaceViewModel vm) _ = vm.InitAsync(mid);
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is UserSpaceViewModel vm)
        {
            AttachVm(vm);
            if (vm.Mid > 0) await vm.InitAsync(vm.Mid);
            else if (Tag is long mid) await vm.InitAsync(mid);
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => AttachVm(e.NewValue as UserSpaceViewModel);

    private void OnUnloaded(object sender, RoutedEventArgs e) => AttachVm(null);

    /// <summary>
    /// 订阅 VM 的事件 / 把投稿列表纳入全局屏蔽。
    /// ★ FilterService 只是在集合的默认视图上装一个 Filter 谓词, 翻页时 Clear+Add
    ///   会自动重新过滤, 不需要在页面里再做"补页"。
    /// </summary>
    private void AttachVm(UserSpaceViewModel? vm)
    {
        if (_vm == vm) return;
        if (_vm != null) _vm.ScrollToTopRequested -= OnScrollToTopRequested;
        _vm = vm;
        if (vm == null) return;
        vm.ScrollToTopRequested += OnScrollToTopRequested;
        FilterService.Instance.Attach(vm.Videos);
    }

    /// <summary>翻页成功后把列表滚回顶部 —— 否则用户点了第 2 页, 眼前还停在上一页的底部。</summary>
    private void OnScrollToTopRequested()
        => Dispatcher.BeginInvoke(new Action(() => ListScroll?.ScrollToTop()),
            DispatcherPriority.Loaded);

    // ------------------------------------------------------------ 头像右键菜单

    private void OnViewAvatar(object sender, RoutedEventArgs e)
    {
        if (DataContext is not UserSpaceViewModel vm) return;
        MediaActions.ShowImage(vm.Face, vm.Name + " 的头像", Window.GetWindow(this));
    }

    private async void OnDownloadAvatar(object sender, RoutedEventArgs e)
    {
        if (DataContext is not UserSpaceViewModel vm) return;
        await MediaActions.SaveImageAsync(vm.Face, vm.Name + "_头像");
    }
}
