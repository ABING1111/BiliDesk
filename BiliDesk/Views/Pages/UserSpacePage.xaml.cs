using System.Windows;
using System.Windows.Controls;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

public partial class UserSpacePage : UserControl
{
    public UserSpacePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        // ★ 本页每次打开都会 new 一个新实例(见 NavigationDispatcher.OpenUserSpace),
        //   所以订阅/退订必须成对: 不摘掉的话 FilterService 会一直攥着旧页面不放。
        Unloaded += (_, _) => FilterService.Instance.Changed -= OnFilterApplied;
    }

    public UserSpacePage(long mid)
    {
        InitializeComponent();
        DataContext = new UserSpaceViewModel();
        if (DataContext is UserSpaceViewModel vm) _ = vm.InitAsync(mid);
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => FilterService.Instance.Changed -= OnFilterApplied;
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is UserSpaceViewModel vm)
        {
            // 个人空间的投稿列表也纳入全局屏蔽(以前这里怎么屏蔽都不生效)
            FilterService.Instance.Attach(vm.Videos);
            FilterService.Instance.Changed -= OnFilterApplied;
            FilterService.Instance.Changed += OnFilterApplied;

            if (vm.Mid > 0) await vm.InitAsync(vm.Mid);
            else if (Tag is long mid) await vm.InitAsync(mid);
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is UserSpaceViewModel vm) FilterService.Instance.Attach(vm.Videos);
    }

    /// <summary>屏蔽筛掉一批投稿后, 等布局量完再补下一页</summary>
    private void OnFilterApplied()
    {
        if (!IsLoaded) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (!IsLoaded || DataContext is not UserSpaceViewModel vm) return;
            var sv = ListScroll;
            if (sv == null) return;
            if (sv.ScrollableHeight - sv.VerticalOffset > 240) return;
            _ = vm.LoadMoreAsync();
        }));
    }

    private void Scroll_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not UserSpaceViewModel vm) return;
        var sv = (ScrollViewer)sender;
        var distanceToBottom = sv.ScrollableHeight - sv.VerticalOffset;
        if (distanceToBottom > 240) return;
        _ = vm.LoadMoreAsync();
    }

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