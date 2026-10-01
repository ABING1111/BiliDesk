using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BiliDesk.Helpers;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

public partial class SettingsPage : UserControl
{
    /// <summary>作者 B 站 UID(空间主页) </summary>
    private const long AuthorMid = 697238372;

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
        }
        _ = LoadAvatarAsync(_vm.UserFace);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.UserFace) || _vm == null) return;
        _ = LoadAvatarAsync(_vm.UserFace);
    }

    /// <summary>
    /// 点击作者栏: 在应用内打开作者的 B 站主页(原生 UP 主空间页, 不是 WebView2)。
    /// 走 NavigationDispatcher.OpenUserSpace, 与播放器里点 UP 主头像的行为保持一致。
    /// </summary>
    private void OnAuthorClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dispatcher = Svc.Navigate
                ?? new BiliDesk.Services.NavigationDispatcher(Window.GetWindow(this));
            dispatcher.OpenUserSpace(AuthorMid.ToString());
        }
        catch (Exception ex)
        {
            Svc.Toast.Show("打开作者主页失败: " + ex.Message);
        }
    }

    /// <summary>异步加载头像到圆形容器</summary>
    private async System.Threading.Tasks.Task LoadAvatarAsync(string? faceUrl)
    {
        if (string.IsNullOrEmpty(faceUrl))
        {
            AvatarBox.Background = (Brush)FindResource("AccentSoftFillBrush");
            AvatarFallback.Visibility = Visibility.Visible;
            return;
        }

        var img = await CoverLoader.LoadAsync(faceUrl, 128);
        if (img == null || _vm == null) return;

        var brush = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
        brush.Freeze();
        AvatarBox.Background = brush;
        AvatarFallback.Visibility = Visibility.Collapsed;
    }
}