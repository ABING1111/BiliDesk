using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

/// <summary>
/// B 站消息页: 左侧会话列表, 右侧私信内容 + 发送框。
/// </summary>
public partial class MessagesPage : UserControl
{
    private MessagesViewModel? Vm => DataContext as MessagesViewModel;

    public MessagesPage()
    {
        InitializeComponent();
        // 注意: DataContext 是外面用对象初始化器赋的, 构造函数里拿到的是 null ——
        // 订阅集合变化必须放在 DataContextChanged 里, 否则永远挂不上。
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MessagesViewModel old) old.Messages.CollectionChanged -= OnMessagesChanged;
        if (e.NewValue is MessagesViewModel vm) vm.Messages.CollectionChanged += OnMessagesChanged;
    }

    /// <summary>消息有增删就滚到底部, 否则发完消息看不到自己的气泡</summary>
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScrollToBottom();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        // 已经有会话就不再重复拉取(切页回来时保留当前会话与已加载的消息)
        if (Vm.Sessions.Count > 0) return;
        await Vm.LoadAsync();
        ScrollToBottom();
    }

    private async void OnSessionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Vm == null) return;
        // 直接用 ListBox 自己的选中项, 不绕 Vm.Current 一圈 ——
        // 少一层"选中 → 双向绑定回写 → 再读回来"的往返, 就少一个"点了没反应"的可能。
        if ((sender as ListBox)?.SelectedItem is not MsgSession session) return;
        await OpenSessionAsync(session);
    }

    /// <summary>
    /// 点击左侧会话行的显式入口(挂在 ListBoxItem 的 PreviewMouseLeftButtonDown 上)。
    ///
    /// 为什么不只靠 SelectionChanged: 两者都会走同一条加载逻辑(VM 内部对同一会话去重,
    /// 不会重复发请求), 但这条通路是隧道事件, 不会被任何子元素或 ListBox 自身的
    /// 选中逻辑吞掉 —— 等于给"点一下没反应"上了个保险。
    /// </summary>
    private void OnSessionRowPressed(object sender, MouseButtonEventArgs e)
    {
        if (Vm == null) return;
        if ((sender as FrameworkElement)?.DataContext is not MsgSession session) return;

        // 显式钳住选中项: 高亮不依赖 ListBox 自己的点击选中行为
        if (!ReferenceEquals(SessionList.SelectedItem, session))
            SessionList.SelectedItem = session;

        _ = OpenSessionAsync(session);
    }

    /// <summary>打开某个会话: 标题立刻切过去 + 拉取私信内容 + 滚到底</summary>
    private async Task OpenSessionAsync(MsgSession session)
    {
        if (Vm == null) return;
        // 先把标题切过去, 点击有即时反馈, 不必等接口回来
        Vm.Current = session;
        await Vm.LoadThreadAsync(session);
        ScrollToBottom();
    }

    /// <summary>返回首页(本页不在导航栏上, 必须有明确的返回入口)</summary>
    private void OnBackClick(object sender, RoutedEventArgs e)
        => App.MainVm.Navigate(PageKey.Home);

    /// <summary>回车即发送(B 站网页端的习惯)</summary>
    private void OnDraftKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (Vm?.SendCommand.CanExecute(null) == true) Vm.SendCommand.Execute(null);
    }

    private void OnMessageImageClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MsgItem m && m.ImageUrl.Length > 0)
            MediaActions.ShowImage(m.ImageUrl, "私信图片", Window.GetWindow(this));
    }

    private void ScrollToBottom()
    {
        // 等布局跑完再滚: 刚 Add 的那一条还没进可视树, 立刻滚是滚不到底的
        Dispatcher.BeginInvoke(new System.Action(() =>
        {
            try { MsgScroll?.ScrollToEnd(); } catch { /* 页面已卸载 */ }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
