using System;
using System.Windows;
using System.Windows.Controls;
using BiliDesk.Models;

namespace BiliDesk.Views.Controls;

/// <summary>
/// 评论输入框的表情墙(视频评论区与图文动态评论区共用)。
///
/// ★ 只管"显示 + 选中", 不碰输入框: 插到光标处的计算在 Helpers/EmoteInsert 里,
///   写回 TextBox 的时机由宿主窗口决定(两个输入框的绑定方式不同, 这里不该替它们做)。
/// ★ 数据来自 Svc.CommentEmotes(单例缓存), 两个输入框共用一次请求;
///   退出登录时服务会清缓存并触发 Changed, 这里跟着刷新。
/// ★ 不用 Popup: 见 XAML 头部的说明。
/// </summary>
public partial class CommentEmotePanel : UserControl
{
    /// <summary>用户点了一个表情。宿主据此把短代码插进输入框。</summary>
    public event EventHandler<MsgEmote>? EmotePicked;

    /// <summary>面板当前是否展开。</summary>
    public bool IsOpen => Root.Visibility == Visibility.Visible;

    public CommentEmotePanel()
    {
        InitializeComponent();
        // Packages 是同一个集合实例, 绑一次就够; 之后的增删由集合通知驱动
        PackageList.ItemsSource = Svc.CommentEmotes.Packages;
        Svc.CommentEmotes.Changed += OnEmotesChanged;
        Unloaded += (_, _) => Svc.CommentEmotes.Changed -= OnEmotesChanged;
        Loaded += (_, _) =>
        {
            // 控件被模板重建/换宿主时重新挂上(Unloaded 里摘过)
            Svc.CommentEmotes.Changed -= OnEmotesChanged;
            Svc.CommentEmotes.Changed += OnEmotesChanged;
            UpdateState();
        };
        UpdateState();
    }

    /// <summary>展开(第一次展开时按需拉数据)。</summary>
    public void Open()
    {
        Root.Visibility = Visibility.Visible;
        _ = Svc.CommentEmotes.EnsureLoadedAsync();
        UpdateState();
    }

    public void Close() => Root.Visibility = Visibility.Collapsed;

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    private void OnEmotesChanged()
    {
        // Changed 可能来自后台线程(网络回调): 切回 UI 线程再动控件
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(UpdateState));
            return;
        }
        UpdateState();
    }

    /// <summary>
    /// 按服务状态切换"加载中 / 错误 / 表情墙"三种样子。
    /// ★ 有表情就一律显示表情墙 —— 哪怕这次请求失败: 旧数据比一片空白有用得多。
    /// </summary>
    private void UpdateState()
    {
        var svc = Svc.CommentEmotes;
        var hasEmotes = svc.HasEmotes;
        var loading = svc.IsLoading;

        // Packages 是 ObservableCollection, 集合变化会自己刷表情墙, 这里不用重绑
        LoadingPanel.Visibility = loading && !hasEmotes ? Visibility.Visible : Visibility.Collapsed;
        EmoteScroll.Visibility = hasEmotes ? Visibility.Visible : Visibility.Collapsed;

        var showError = !hasEmotes && !loading && svc.Error.Length > 0;
        ErrorText.Text = showError ? svc.Error : "";
        ErrorText.Visibility = showError ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnEmoteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MsgEmote emote) return;
        if (emote.Text.Length == 0) return;
        EmotePicked?.Invoke(this, emote);
    }
}
