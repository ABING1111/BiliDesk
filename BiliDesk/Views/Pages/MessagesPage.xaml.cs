using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.ViewModels;

namespace BiliDesk.Views.Pages;

/// <summary>
/// B 站消息页: 四个分类 Tab(聊天列表 / 回复我的 / @我的 / 收到的赞)。
/// 聊天 Tab 是"左会话列表 + 右聊天内容", 另外三个是互动消息卡片流。
/// </summary>
public partial class MessagesPage : UserControl, IRefreshable
{
    private MessagesViewModel? Vm => DataContext as MessagesViewModel;

    /// <summary>互动列表是否已经请求过下一页(防止滚到底时连发几次)</summary>
    private bool _feedMorePending;

    /// <summary>
    /// 有一次右键引起的选中变化还没处理。WPF 的 ListBoxItem.OnMouseRightButtonDown 默认
    /// 也会把行选中(它内部调 NotifyListItemClicked), 而选中变化会走 SelectionChanged →
    /// 打开会话。这个标记让**紧接着的那一次**选中变化只回滚、不加载私信,
    /// 处理完立刻清掉 —— 否则我们自己的回滚会再进一次 SelectionChanged, 又被当成右键。
    /// 见 <see cref="OnSessionRightButtonDown"/>。
    /// </summary>
    private bool _pendingRightClickRestore;

    /// <summary>右键按下前右栏正在显示的会话(回滚选中用)</summary>
    private MsgSession? _selectionBeforeRightClick;

    /// <summary>正在把选中回滚到右键前的会话(回滚自身触发的 SelectionChanged 要忽略)</summary>
    private bool _restoringSelection;

    /// <summary>F5 / 顶部刷新按钮: 刷当前 Tab</summary>
    public void Refresh() => _ = Vm?.RefreshAsync();

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
        if (e.OldValue is MessagesViewModel old)
        {
            old.Messages.CollectionChanged -= OnMessagesChanged;
            old.PropertyChanged -= OnVmPropertyChanged;
        }
        if (e.NewValue is MessagesViewModel vm)
        {
            vm.Messages.CollectionChanged += OnMessagesChanged;
            vm.PropertyChanged += OnVmPropertyChanged;
            // Tab 默认停在"聊天列表", 但 RadioButton 的初始 IsChecked 要在载入后才生效,
            // 这里显式同步一次, 免得 VM 与界面对不上。
            SyncTabFromVm(vm);
        }
    }

    /// <summary>VM 上 Tab 变了 → 跑一次切换副作用(翻页闸门 / 滚动)</summary>
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MessagesViewModel.Tab)) return;
        if (Vm != null) OnTabSwitched(Vm.Tab);
    }

    /// <summary>把 VM 的 Tab 反映到四个 RadioButton 上(程序化切换时用)</summary>
    private void SyncTabFromVm(MessagesViewModel vm)
    {
        // 现在是 TwoWay 绑定, 理论上不需要手推; 但 DataContext 是构造函数之后才赋的,
        // 绑定引擎在那一刻可能还没把初值拉过来 —— 这里显式同步一次最稳。
        ChatTabBtn.IsChecked = vm.Tab == MsgTab.Chat;
        ReplyTabBtn.IsChecked = vm.Tab == MsgTab.Reply;
        AtTabBtn.IsChecked = vm.Tab == MsgTab.At;
        LikeTabBtn.IsChecked = vm.Tab == MsgTab.Like;
    }

    /// <summary>消息有增删就滚到底部, 否则发完消息看不到自己的气泡</summary>
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 只有"追加"才滚到底; 往上翻历史(Insert 到头部)时绝不能滚, 否则用户刚翻到的位置会跳走
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex >= 0)
        {
            var vm = Vm;
            if (vm != null && e.NewStartingIndex < vm.Messages.Count - 1) return;
        }
        if (e.Action is NotifyCollectionChangedAction.Add) ScrollToBottom();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        // 已经有会话就不再重复拉取(切页回来时保留当前会话与已加载的消息)
        if (Vm.Sessions.Count > 0) return;
        await Vm.LoadAsync();
        ScrollToBottom();
    }

    // ---------------------------------------------------------------- Tab

    /// <summary>
    /// 切 Tab 的副作用(翻页闸门 + 滚动)。
    /// ★ 选中态本身由 VM 的 TabIsXxx 双向绑定负责, 这里不再碰 IsChecked ——
    ///   两边都写会互相打架(用户在界面上点一下, VM 改一次, 界面又被推回去)。
    /// </summary>
    private void OnTabSwitched(MsgTab tab)
    {
        // 切到互动 Tab 时列表是新的, 把"已请求翻页"的闸门放开
        _feedMorePending = false;

        // 聊天 Tab 切回来时保持原滚动位置, 不强制回到底部
        if (tab == MsgTab.Chat) ScrollToBottom();
    }

    // ---------------------------------------------------------------- 会话

    private async void OnSessionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Vm == null) return;
        // 直接用 ListBox 自己的选中项, 不绕 Vm.Current 一圈 ——
        // 少一层"选中 → 双向绑定回写 → 再读回来"的往返, 就少一个"点了没反应"的可能。
        if ((sender as ListBox)?.SelectedItem is not MsgSession session) return;

        // ★ 右键引起的选中: 不打开会话, 并把选中回滚到右键前的那条(2026-11-28)。
        //   WPF 的 ListBoxItem 右键按下时自己也会选中行, 只删掉旧版手动改 SelectedItem
        //   的代码还不够 —— 选中变化照样会走到这里。
        //
        // ★★ 回滚必须用**高于 Render(7) 的优先级**(2026-11-28 用户反馈"右键时右边会闪一下
        //   对方的聊天页"): 第一次写成 Input(5), 而 WPF 的布局/渲染跑在 Render(7) 上 ——
        //   优先级更高, 于是错误状态先被排了一帧版, 下一轮才轮到我们回滚, 肉眼就是闪一下。
        //   改成 Normal(9) 后, 回滚一定在渲染之前完成, 那一帧根本不会发生。
        //
        // ★★ 顺序也不能反(2026-11-28 第二版): 回滚自身也会触发一次 SelectionChanged。
        //   如果先判"右键标记"再判 _restoringSelection, 这次回滚就会被当成新的右键事件,
        //   而 _selectionBeforeRightClick 已经被清成 null —— 于是再回滚成"没有选中",
        //   右栏彻底变空。所以 _restoringSelection 必须最先判, 右键标记只用一次就清。
        if (_restoringSelection) return;

        if (_pendingRightClickRestore)
        {
            _pendingRightClickRestore = false;
            var shown = _selectionBeforeRightClick;
            _selectionBeforeRightClick = null;
            if (!ReferenceEquals(SessionList.SelectedItem, shown))
            {
                _restoringSelection = true;
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { SessionList.SelectedItem = shown; }
                    finally { _restoringSelection = false; }
                }), System.Windows.Threading.DispatcherPriority.Normal);
            }
            return;
        }

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
    private async System.Threading.Tasks.Task OpenSessionAsync(MsgSession session)
    {
        if (Vm == null) return;
        // 先把标题切过去, 点击有即时反馈, 不必等接口回来
        Vm.Current = session;
        await Vm.LoadThreadAsync(session);
        ScrollToBottom();
    }

    // ---------------------------------------------------------------- 头像 / 右键菜单

    /// <summary>打开 UP 主主页(视频卡片同款入口, 见 VideoCard.OnOpenUserSpace)</summary>
    private void OpenUserSpace(long mid)
    {
        if (mid <= 0)
        {
            Svc.Toast.Show("没拿到对方的 UID, 打不开主页");
            return;
        }
        new Services.NavigationDispatcher(Window.GetWindow(this)).OpenUserSpace(mid.ToString());
    }

    /// <summary>点击聊天区头像跳转对方主页(用户 2026-11-15 要求)。
    /// 自己的头像不跳 —— 跳自己主页应该从「我的」页进。</summary>
    private void OnPeerAvatarClick(object sender, MouseButtonEventArgs e)
    {
        // 气泡行 DataContext 是 MsgItem; 对方头像只在"对方消息"上可见
        if ((sender as FrameworkElement)?.DataContext is not MsgItem m) return;
        if (m.IsMine) return;
        OpenUserSpace(m.SenderUid);
    }

    /// <summary>
    /// 会话行的右键菜单(2026-11-15 用户要求, 视觉与首页视频卡片的右键菜单一致)。
    ///
    /// ★★ 为什么挂在 **PreviewMouseRightButtonDown**、并且不再改选中项(2026-11-28 修):
    ///   旧版在 ContextMenuOpening 里给 row.ContextMenu 赋值再手动 IsOpen —— WPF 的
    ///   ContextMenuService 在事件**之前**就已经完成了"取菜单"这一步, 拿到的是 null,
    ///   只能靠手动 IsOpen 补一枪; 而手动弹出的菜单绕过了服务正常的打开流程。
    ///   更直接的副作用是旧版顺手把 SelectedItem 改成被右键的那一行, 选中变化又触发
    ///   SelectionChanged → OpenSessionAsync, 表现就是"右键一下等于左键点了会话"
    ///   (右栏内容被切走、私信开始加载)。现在:
    ///   · 菜单在右键**按下**时就挂到行上, 由服务在右键抬起时按正常流程弹出;
    ///   · 不再手动改选中项。但 WPF 的 ListBoxItem 右键按下时**自己**也会选中行,
    ///     所以还要配合 <see cref="OnSessionSelectionChanged"/> 里的 `_pendingRightClickRestore`
    ///     分支: 那条选中变化只回滚、不加载, 右栏保持原样。
    /// </summary>
    private void OnSessionRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement row) return;
        if (row.DataContext is not MsgSession session) return;

        // 记下右键前的显示会话 + 标记"下一次选中变化是右键引起的":
        // 选中变化在本次事件里同步发生, 复位放到 Input 优先级(本次输入处理完之后),
        // 所以既能拦住它, 又不会误伤后续真正的左键。
        _pendingRightClickRestore = true;
        _selectionBeforeRightClick = Vm?.Current;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            _pendingRightClickRestore = false;
            _selectionBeforeRightClick = null;
        }), System.Windows.Threading.DispatcherPriority.Input);

        var menu = new ContextMenu();
        menu.Items.Add(MakeMenuItem("打开 UP 主主页", "\uE77B", () => OpenUserSpace(session.TalkerId)));
        menu.Items.Add(new Separator());

        // 打开官方消息页: 内置浏览器里有"举报/撤回"这类我们没做的操作
        menu.Items.Add(MakeMenuItem("在官方消息页打开", "\uE8A7", () =>
            Services.LinkLauncher.OpenInBrowser("https://message.bilibili.com/", Window.GetWindow(this))));

        // 每次右键都按当前行重建: ListBoxItem 会被虚拟化复用, 旧菜单的闭包指向旧会话。
        row.ContextMenu = menu;
    }

    /// <summary>与 VideoCard.MakeMenuItem 同款: 图标 + 文字(首页右键菜单的观感)</summary>
    private static MenuItem MakeMenuItem(string header, string glyph, System.Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>返回首页(本页不在导航栏上, 必须有明确的返回入口)</summary>
    private void OnBackClick(object sender, RoutedEventArgs e)
        => App.MainVm.Navigate(PageKey.Home);

    // ---------------------------------------------------------------- 输入

    /// <summary>
    /// 回车即发送(B 站网页端的习惯)。
    /// ★ 输入框现在支持多行自动扩展, Shift+Enter 留给换行 —— 不 Handle,
    ///   交给 TextBox 自己插入换行符(它已 AcceptsReturn=true)。
    /// </summary>
    private void OnDraftKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        e.Handled = true;
        if (Vm?.SendCommand.CanExecute(null) == true) Vm.SendCommand.Execute(null);
    }

    /// <summary>点表情: 把短代码插到输入框的**光标处**(不是末尾)</summary>
    private void OnEmoteClick(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        if ((sender as FrameworkElement)?.DataContext is not MsgEmote emote) return;
        if (emote.Text.Length == 0) return;

        if (DraftBox == null)
        {
            Vm.InsertEmote(emote);
            return;
        }

        // ★ 插到光标位置而不是直接追加: 用户常常是"写一半 → 点个表情 → 接着写",
        //   追加到末尾会让表情跑到句子外面。
        var text = DraftBox.Text ?? "";
        var caret = DraftBox.CaretIndex;
        if (caret < 0 || caret > text.Length) caret = text.Length;

        DraftBox.Text = text.Insert(caret, emote.Text);
        // 绑的是 UpdateSourceTrigger=PropertyChanged, 上面赋值已经把 VM 的 Draft 同步好了
        DraftBox.CaretIndex = caret + emote.Text.Length;
        DraftBox.Focus();
    }

    /// <summary>选图并发送</summary>
    private async void OnPickImageClick(object sender, RoutedEventArgs e)
    {
        if (Vm == null || !Vm.HasCurrent) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要发送的图片",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.gif;*.webp;*.bmp|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        await Vm.SendImageAsync(dialog.FileName);
        ScrollToBottom();
    }

    // ---------------------------------------------------------------- 消息体

    private void OnMessageImageClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MsgItem m && m.ImageUrl.Length > 0)
            MediaActions.ShowImage(m.ImageUrl, m.IsEmote ? "表情" : "私信图片", Window.GetWindow(this));
    }

    private async void OnLoadOlderClick(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        // 记住当前滚动高度: 往前插一页之后要保持视觉位置不跳
        var before = MsgScroll?.VerticalOffset ?? 0;
        var beforeExtent = MsgScroll?.ExtentHeight ?? 0;

        await Vm.LoadOlderAsync();

        // 插入的是**上方**内容, 视口要往下补上新增的高度, 否则用户会"被顶上去"
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (MsgScroll == null) return;
                var delta = MsgScroll.ExtentHeight - beforeExtent;
                if (delta > 0) MsgScroll.ScrollToVerticalOffset(before + delta);
            }
            catch { /* 页面已卸载 */ }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ScrollToBottom()
    {
        // 等布局跑完再滚: 刚 Add 的那一条还没进可视树, 立刻滚是滚不到底的
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { MsgScroll?.ScrollToEnd(); } catch { /* 页面已卸载 */ }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ---------------------------------------------------------------- 互动列表

    /// <summary>滚到接近底部就自动翻下一页</summary>
    private async void OnFeedScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Vm == null || !Vm.IsFeedTab) return;
        if (sender is not ScrollViewer sv) return;
        if (sv.ScrollableHeight <= 0) return;
        // 距离底部还有 200px 就开始加载, 让"滚到底"时内容已经在了
        if (sv.VerticalOffset < sv.ScrollableHeight - 200) return;

        if (_feedMorePending) return;
        var feed = Vm.CurrentFeed;
        if (feed == null) return;

        _feedMorePending = true;
        try { await feed.LoadMoreAsync(); }
        finally { _feedMorePending = false; }
    }

    /// <summary>点互动卡片: 视频进内置播放器, 其它用内置浏览器打开</summary>
    private void OnFeedItemClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MsgFeedItem item) return;

        if (item.Bvid.Length > 0)
        {
            Svc.Player.PlayVideo(item.Bvid);
            return;
        }
        if (item.ContextUri.Length > 0)
        {
            Services.LinkLauncher.OpenInBrowser(item.ContextUri, Window.GetWindow(this));
        }
    }
}
