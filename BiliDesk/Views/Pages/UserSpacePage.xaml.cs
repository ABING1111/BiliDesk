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
    }

    public UserSpacePage(long mid)
    {
        InitializeComponent();
        DataContext = new UserSpaceViewModel();
        if (DataContext is UserSpaceViewModel vm) _ = vm.InitAsync(mid);
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is UserSpaceViewModel vm)
        {
            if (vm.Mid > 0) await vm.InitAsync(vm.Mid);
            else if (Tag is long mid) await vm.InitAsync(mid);
        }
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