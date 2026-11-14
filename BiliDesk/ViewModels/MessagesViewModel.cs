using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.ViewModels;

/// <summary>
/// B 站消息页。
///
/// ★ 2026-11-14 改版(用户要求"对齐官方 b 站"):
///   顶部四个 Tab: 聊天列表 / 回复我的 / @我的 / 收到的赞。
///   前三个互动 Tab 各自持有一个 <see cref="MsgFeedViewModel"/>, 聊天 Tab 仍是
///   "左会话列表 + 右聊天内容"的两栏结构。
///
/// ★ 私信气泡改成微信式(左边对方 / 右边自己), 支持表情与图片。
/// </summary>
public class MessagesViewModel : ObservableObject
{
    // ---------------------------------------------------------------- 会话(聊天 Tab)

    public ObservableCollection<MsgSession> Sessions { get; } = new();
    public ObservableCollection<MsgItem> Messages { get; } = new();

    // ---------------------------------------------------------------- 互动 Tab

    public MsgFeedViewModel ReplyFeed { get; } = new(MsgTab.Reply);
    public MsgFeedViewModel AtFeed { get; } = new(MsgTab.At);
    public MsgFeedViewModel LikeFeed { get; } = new(MsgTab.Like);

    // ---------------------------------------------------------------- 状态字段

    private bool _loading;
    private string _error = "";
    private MsgSession? _current;
    private bool _threadLoading;
    private string _threadError = "";
    private string _draft = "";
    private bool _sending;
    private bool _uploadingImage;

    private MsgTab _tab = MsgTab.Chat;
    private MsgUnread _unread = new();
    private bool _emojiPanelOpen;
    private bool _emojiLoading;
    private string _emojiError = "";
    private bool _emojiLoaded;

    private string _myName = "";
    private string _myFace = "";
    private bool _myProfileLoaded;

    public MessagesViewModel()
    {
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        SendCommand = new RelayCommand(() => _ = SendAsync());
        LoadMoreCommand = new RelayCommand(() => _ = LoadOlderAsync());
        ToggleEmojiPanelCommand = new RelayCommand(() => EmojiPanelOpen = !EmojiPanelOpen);

        // ★ 集合本身的变化也要推一次派生属性:
        //   HasMessages / HasMoreHistory 是算出来的(看 Messages.Count), 而消息集合在
        //   "加载会话 / 往上翻页插入 / 乐观追加"三条路径上都会变 —— 只靠各调用点手动
        //   OnPropertyChanged 迟早漏一处(漏了的表现是"有消息了但'加载失败'的提示还压在上面")。
        Messages.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasMessages));
            OnPropertyChanged(nameof(HasMoreHistory));
        };
    }

    // ---------------------------------------------------------------- 基础状态

    public bool Loading { get => _loading; private set => SetProperty(ref _loading, value); }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }

    /// <summary>当前选中的 Tab</summary>
    public MsgTab Tab
    {
        get => _tab;
        set
        {
            if (!SetProperty(ref _tab, value)) return;
            OnPropertyChanged(nameof(IsChatTab));
            OnPropertyChanged(nameof(IsReplyTab));
            OnPropertyChanged(nameof(IsAtTab));
            OnPropertyChanged(nameof(IsLikeTab));
            OnPropertyChanged(nameof(IsFeedTab));
            OnPropertyChanged(nameof(TabIsChat));
            OnPropertyChanged(nameof(TabIsReply));
            OnPropertyChanged(nameof(TabIsAt));
            OnPropertyChanged(nameof(TabIsLike));
            // ★ CurrentFeed 是算出来的属性, 不通知的话界面上 {Binding CurrentFeed.Items}
            //   永远停在旧那个 Tab 的列表上 —— 表现为"切了 Tab 内容不变"。
            OnPropertyChanged(nameof(CurrentFeed));
            // 切 Tab 时把表情面板收起来: 它属于输入框, 不该跟着跑到互动列表上
            EmojiPanelOpen = false;
            _ = OnTabChangedAsync();
        }
    }

    public bool IsChatTab => _tab == MsgTab.Chat;
    public bool IsReplyTab => _tab == MsgTab.Reply;
    public bool IsAtTab => _tab == MsgTab.At;
    public bool IsLikeTab => _tab == MsgTab.Like;

    /// <summary>
    /// 四个 RadioButton 的 IsChecked 都绑到这里(双向)。
    ///
    /// ★ 为什么不让界面自己管选中态: RadioButton 的 IsChecked 一旦只由"用户点击"驱动,
    ///   代码里改 <see cref="Tab"/>(比如"点未读角标跳到回复页")就不会反映到界面上 ——
    ///   表现为"内容已经是回复列表, 高亮的还是聊天列表"。
    ///   绑成 TwoWay 之后 VM 是唯一数据源, 两边不会再各说各话。
    /// ★ setter 只认 true: 分组切换时 WPF 会把另一颗置 false, 那一下不该触发任何动作。
    /// </summary>
    public bool TabIsChat
    {
        get => _tab == MsgTab.Chat;
        set { if (value) Tab = MsgTab.Chat; }
    }

    public bool TabIsReply
    {
        get => _tab == MsgTab.Reply;
        set { if (value) Tab = MsgTab.Reply; }
    }

    public bool TabIsAt
    {
        get => _tab == MsgTab.At;
        set { if (value) Tab = MsgTab.At; }
    }

    public bool TabIsLike
    {
        get => _tab == MsgTab.Like;
        set { if (value) Tab = MsgTab.Like; }
    }

    /// <summary>三个互动 Tab 中的任意一个(它们共用同一个列表容器)</summary>
    public bool IsFeedTab => _tab != MsgTab.Chat;

    /// <summary>当前 Tab 对应的互动列表(聊天 Tab 下为 null)</summary>
    public MsgFeedViewModel? CurrentFeed => _tab switch
    {
        MsgTab.Reply => ReplyFeed,
        MsgTab.At => AtFeed,
        MsgTab.Like => LikeFeed,
        _ => null
    };

    // ---------------------------------------------------------------- 未读角标

    public int UnreadChat => _unread.Chat;
    public int UnreadReply => _unread.Reply;
    public int UnreadAt => _unread.At;
    public int UnreadLike => _unread.Like;

    public bool HasUnreadChat => _unread.Chat > 0;
    public bool HasUnreadReply => _unread.Reply > 0;
    public bool HasUnreadAt => _unread.At > 0;
    public bool HasUnreadLike => _unread.Like > 0;

    private void RaiseUnread()
    {
        OnPropertyChanged(nameof(UnreadChat));
        OnPropertyChanged(nameof(UnreadReply));
        OnPropertyChanged(nameof(UnreadAt));
        OnPropertyChanged(nameof(UnreadLike));
        OnPropertyChanged(nameof(HasUnreadChat));
        OnPropertyChanged(nameof(HasUnreadReply));
        OnPropertyChanged(nameof(HasUnreadAt));
        OnPropertyChanged(nameof(HasUnreadLike));
    }

    // ---------------------------------------------------------------- 我的资料(微信式右侧头像)

    /// <summary>我的昵称(拿不到时界面显示"我")</summary>
    public string MyName
    {
        get => string.IsNullOrEmpty(_myName) ? "我" : _myName;
        private set { _myName = value; OnPropertyChanged(nameof(MyName)); }
    }

    public string MyFace { get => _myFace; private set => SetProperty(ref _myFace, value); }

    /// <summary>对方昵称/头像(微信式左侧)</summary>
    public string PeerName => _current?.DisplayName ?? "";
    public string PeerFace => _current?.Face ?? "";

    // ---------------------------------------------------------------- 会话状态

    /// <summary>当前选中的会话(ListBox 的 SelectedItem 双向绑过来)</summary>
    public MsgSession? Current
    {
        get => _current;
        set
        {
            if (!SetProperty(ref _current, value)) return;
            OnPropertyChanged(nameof(HasCurrent));
            OnPropertyChanged(nameof(CurrentTitle));
            // ★ CanType 也要跟着通知: 输入框与图片按钮的 IsEnabled 绑的是它 ——
            //   漏掉的话"选中会话之后输入框仍然禁用"(用户报的"输入框无法点击")。
            //   表情按钮绑 HasCurrent 同理, 那条已经通知了。
            OnPropertyChanged(nameof(CanSend));
            OnPropertyChanged(nameof(CanType));
            OnPropertyChanged(nameof(PeerName));
            OnPropertyChanged(nameof(PeerFace));
        }
    }

    public bool HasCurrent => _current != null;
    public string CurrentTitle => _current?.DisplayName ?? "";

    /// <summary>当前会话里是否有消息(决定"加载失败"提示要不要占位)</summary>
    public bool HasMessages => Messages.Count > 0;

    public bool ThreadLoading { get => _threadLoading; private set => SetProperty(ref _threadLoading, value); }
    public string ThreadError { get => _threadError; private set => SetProperty(ref _threadError, value); }

    /// <summary>输入框内容(空/全空白时发送按钮会自己变灰)</summary>
    public string Draft
    {
        get => _draft;
        set { if (SetProperty(ref _draft, value)) OnPropertyChanged(nameof(CanSend)); }
    }

    public bool Sending { get => _sending; private set => SetProperty(ref _sending, value); }

    /// <summary>正在上传图片(界面上显示"正在发送图片…", 同时禁用输入)</summary>
    public bool UploadingImage
    {
        get => _uploadingImage;
        private set
        {
            if (!SetProperty(ref _uploadingImage, value)) return;
            OnPropertyChanged(nameof(CanSend));
            OnPropertyChanged(nameof(CanType));
        }
    }

    /// <summary>输入框有内容且不是正在发送 → 发送按钮可用</summary>
    public bool CanSend => !_sending && !_uploadingImage && !string.IsNullOrWhiteSpace(_draft) && _current != null;

    /// <summary>输入框可用(发送中/上传中也允许继续打字, 只是发不出去)</summary>
    public bool CanType => _current != null && !_uploadingImage;

    // ---------------------------------------------------------------- 表情面板

    /// <summary>表情面板是否展开(内嵌在输入区上方, 不用 Popup)</summary>
    public bool EmojiPanelOpen
    {
        get => _emojiPanelOpen;
        set
        {
            if (!SetProperty(ref _emojiPanelOpen, value)) return;
            if (value) _ = EnsureEmotesAsync();
        }
    }

    public bool EmojiLoading { get => _emojiLoading; private set => SetProperty(ref _emojiLoading, value); }
    public string EmojiError { get => _emojiError; private set => SetProperty(ref _emojiError, value); }

    /// <summary>表情包分组(面板按包横向分组显示)</summary>
    public ObservableCollection<MsgEmotePackage> EmotePackages { get; } = new();

    /// <summary>面板里是否真的有表情可点(决定错误提示要不要占位)</summary>
    public bool HasEmotes => EmotePackages.Count > 0;

    // ---------------------------------------------------------------- 命令

    public ICommand RefreshCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand LoadMoreCommand { get; }
    public ICommand ToggleEmojiPanelCommand { get; }

    // ---------------------------------------------------------------- 加载

    /// <summary>页面进入时调一次: 会话列表 + 未读数 + 我的资料</summary>
    public async Task LoadAsync()
    {
        if (Loading) return;
        if (!Svc.Session.HasLogin)
        {
            Error = "查看消息需要先登录 B 站账号";
            return;
        }

        Loading = true;
        Error = "";
        MsgSession? reload = null;
        // 三件事互不依赖, 一起发出去 —— 串行的话首屏要多等两三个往返
        var sessionsTask = Svc.Api.GetMsgSessionsAsync();
        var unreadTask = Svc.Api.GetMsgUnreadAsync();
        var profileTask = LoadMyProfileAsync();
        try
        {
            var (ok, err, sessions) = await sessionsTask;
            if (!ok || sessions == null)
            {
                Error = err ?? "会话列表加载失败";
                return;
            }

            // 重建列表时尽量保持"原来打开的那个会话"还开着, 而不是粗暴跳回第一个
            var keepId = _current?.TalkerId ?? 0;

            Sessions.Clear();
            foreach (var s in sessions.OrderByDescending(x => x.TimeText)) Sessions.Add(s);
            if (Sessions.Count == 0)
            {
                Error = "没有会话记录";
                return;
            }

            reload = Sessions.FirstOrDefault(s => s.TalkerId == keepId) ?? Sessions[0];
            Current = reload;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Loading = false;

            // ★ 两个附属请求放在 finally 里等: 上面几条 return 分支会让它们变成
            //   "没人观察的任务"(异常被吞掉、角标也永远不更新)。它们自己内部已经
            //   try/catch 过, 失败只是没有角标, 不影响主列表。
            try
            {
                _unread = await unreadTask;
                RaiseUnread();
            }
            catch { /* 角标拿不到不影响页面 */ }
            try { await profileTask; } catch { /* 资料拿不到就显示"我" */ }
        }

        if (reload != null) await LoadThreadAsync(reload);
    }

    /// <summary>刷新当前 Tab(顶部的刷新按钮)</summary>
    public async Task RefreshAsync()
    {
        if (!Svc.Session.HasLogin)
        {
            Error = "查看消息需要先登录 B 站账号";
            return;
        }

        if (_tab == MsgTab.Chat)
        {
            await LoadAsync();
            return;
        }

        // 互动 Tab: 重拉当前列表 + 顺手把角标刷新一次
        var feed = CurrentFeed;
        if (feed != null) await feed.RefreshAsync();
        await RefreshUnreadAsync();
    }

    /// <summary>只刷新未读角标</summary>
    public async Task RefreshUnreadAsync()
    {
        try
        {
            _unread = await Svc.Api.GetMsgUnreadAsync();
            RaiseUnread();
        }
        catch { /* 角标失败不影响列表 */ }
    }

    private async Task OnTabChangedAsync()
    {
        var feed = CurrentFeed;
        if (feed == null) return;

        await feed.EnsureLoadedAsync();

        // 进入某个 Tab 即视为"看过了": 本地把对应角标清零。
        // ★ 不清服务端 —— 官方消息中心也是打开即本地清零, 下次刷新时以服务端为准。
        switch (_tab)
        {
            case MsgTab.Reply: _unread.Reply = 0; break;
            case MsgTab.At: _unread.At = 0; break;
            case MsgTab.Like: _unread.Like = 0; break;
        }
        RaiseUnread();
    }

    /// <summary>我的昵称与头像(nav 接口)。失败就退回"我" + 无头像。</summary>
    private async Task LoadMyProfileAsync()
    {
        if (_myProfileLoaded) return;
        try
        {
            var (ok, _, user) = await Svc.Api.GetUserStateAsync();
            if (!ok || user == null || !user.IsLogin) return;
            MyName = user.Name;
            MyFace = user.Face;
            _myProfileLoaded = true;
        }
        catch { /* 拿不到就显示"我" */ }
    }

    // ---------------------------------------------------------------- 会话内容

    /// <summary>最近一次被请求打开的会话(上一个还没加载完时先记下来)</summary>
    private MsgSession? _pendingThread;

    /// <summary>当前正在加载的那个会话</summary>
    private MsgSession? _loadingThread;

    /// <summary>
    /// 加载某个会话的私信内容。
    ///
    /// 这里用"最新一次请求说了算"的写法, 而不是"正在加载就直接 return" ——
    /// 后者会把加载期间的点击**静默丢掉**: 首次进入时那条请求往往最慢, 用户这时点别的会话,
    /// 看到的就是"点了没反应"。现在改成记下最后点的那个, 等当前这次跑完立刻补上。
    /// </summary>
    public async Task LoadThreadAsync(MsgSession? session)
    {
        if (session == null) return;

        // 先把标题切过去, 让点击有即时反馈(不必等接口回来)
        Current = session;

        if (ThreadLoading)
        {
            // 被重复请求的是同一个会话(例如"选中变化"和"加载完成"两条路径撞在一起)就不用排队重来
            if (!ReferenceEquals(_loadingThread, session)) _pendingThread = session;
            return;
        }

        ThreadLoading = true;
        try
        {
            var target = session;
            while (target != null)
            {
                _pendingThread = null;
                _loadingThread = target;
                await LoadThreadCoreAsync(target);
                target = _pendingThread; // 期间用户又点了别的会话 → 接着加载它
            }
        }
        finally
        {
            _loadingThread = null;
            _pendingThread = null;
            ThreadLoading = false;
        }
    }

    private async Task LoadThreadCoreAsync(MsgSession session)
    {
        ThreadError = "";
        Messages.Clear();
        // 换会话就把"已到头"的标记清掉: 上一个会话翻完了, 不代表这个会话也没有更早的消息
        _noMoreHistory = false;
        OnPropertyChanged(nameof(HasMoreHistory));
        try
        {
            var (ok, err, items, hasMore) = await Svc.Api.GetMsgHistoryAsync(session.TalkerId);
            if (!ok || items == null)
            {
                ThreadError = err ?? "私信内容加载失败";
                return;
            }
            foreach (var m in items) Messages.Add(m);
            MarkGroups();
            if (Messages.Count == 0) ThreadError = "还没有聊过, 发条消息打个招呼吧";

            // 首屏一次就拿全了(没有更早的) ⇒ 不必显示"查看更早的消息"
            if (!hasMore) _noMoreHistory = true;
            OnPropertyChanged(nameof(HasMoreHistory));

            // 顺带把会话标成已读: 未读角标清掉, 服务端也同步一下
            var lastSeq = Messages.Count > 0 ? Messages[^1].Seqno : 0;
            if (lastSeq > 0)
            {
                session.UnreadCount = 0;
                _ = Svc.Api.MarkMsgReadAsync(session.TalkerId, lastSeq);
                // 会话 Tab 的角标也跟着减一次
                if (_unread.Chat > 0) _unread.Chat--;
                RaiseUnread();
            }
        }
        catch (Exception ex)
        {
            ThreadError = ex.Message;
        }
    }

    /// <summary>
    /// 计算"哪些消息要显示头像"(微信式分组)。
    ///
    /// ★ 规则: 同一个人连续发的消息, 只在**第一条**显示头像 + 昵称;
    ///   换人(或中间夹了系统提示)就重新开一组。系统提示永远不显示头像。
    /// ★ 单独抽出来是因为"发完消息追加一条"之后也要重算末尾这一组 ——
    ///   否则刚发的第二条还会带着头像, 和上面那几条不一样。
    /// </summary>
    private void MarkGroups()
    {
        // ★ 分组键用"IsMine ? 0 : SenderUid"而不是裸 SenderUid:
        //   自己发的消息里, 服务端回来的带真实 mid, 乐观追加的那条填的是 MyMid
        //   (取不到时是 -1) —— 直接用 SenderUid 会让"刚发的这条"另起一组,
        //   界面上表现为自己连着发的两句之间突然又冒出一次头像。
        long prevKey = long.MinValue;
        foreach (var m in Messages)
        {
            if (m.IsSystem)
            {
                m.ShowHeader = true;
                prevKey = long.MinValue;
                continue;
            }

            var key = m.IsMine ? 0 : m.SenderUid;
            m.ShowHeader = key != prevKey;
            prevKey = key;
        }
    }

    /// <summary>加载更早的消息(向上翻页)</summary>
    public async Task LoadOlderAsync()
    {
        var session = _current;
        if (session == null || ThreadLoading || Messages.Count == 0) return;

        var firstSeq = Messages[0].Seqno;
        if (firstSeq <= 0) return;

        try
        {
            var (ok, _, items, hasMore) = await Svc.Api.GetMsgHistoryAsync(session.TalkerId, 30, firstSeq);
            if (!ok || items == null || items.Count == 0)
            {
                // 翻不出东西 = 到头了: 把入口收起来, 免得用户反复点
                _noMoreHistory = true;
                OnPropertyChanged(nameof(HasMoreHistory));
                return;
            }
            if (!hasMore)
            {
                _noMoreHistory = true;
            }

            // 往前插: 倒序插到 0 之前, 保持"旧 → 新"
            for (var i = items.Count - 1; i >= 0; i--) Messages.Insert(0, items[i]);
            MarkGroups();
        }
        catch { /* 翻页失败保持现状 */ }
    }

    private bool _noMoreHistory;

    /// <summary>
    /// 是否显示"查看更早的消息"入口。
    /// ★ 必须同时满足"有会话 + 有消息 + 还没翻到头" —— 空会话上挂一个"查看更早"是没意义的,
    ///   点了也只会静默返回, 用户会以为按钮坏了。
    /// </summary>
    public bool HasMoreHistory
        => _current != null && Messages.Count > 0 && !_noMoreHistory;

    // ---------------------------------------------------------------- 发送

    /// <summary>发送当前输入框里的文字(含表情短代码)</summary>
    public async Task SendAsync()
    {
        var session = _current;
        if (session == null || _sending) return;
        var text = (_draft ?? "").Trim();
        if (text.Length == 0) return;
        if (!Svc.Session.HasLogin)
        {
            Svc.Toast.Show("发送私信需要先登录");
            return;
        }

        Sending = true;
        OnPropertyChanged(nameof(CanSend));
        try
        {
            var (ok, err) = await Svc.Api.SendMsgAsync(session.TalkerId, text);
            if (!ok)
            {
                Svc.Toast.Show("发送失败: " + (err ?? "未知错误"));
                return;
            }

            // 乐观追加一条, 不等服务端回包: 体验上"发出去就出现"比等一秒重要得多
            var item = new MsgItem
            {
                SenderUid = MyMid,
                IsMine = true,
                Text = text,
                MsgType = 1,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Seqno = Messages.Count > 0 ? Messages[^1].Seqno + 1 : 1,
                TimeText = DateTime.Now.ToString("MM-dd HH:mm")
            };
            // 用本地表情表把这条消息的表情挂上, 否则刚发的表情会显示成短代码
            AttachEmotes(item);
            AppendMessage(item);

            session.Preview = MsgContentParser.ToPreview(text);
            session.TimeText = DateTime.Now.ToString("MM-dd HH:mm");
            Draft = "";
            // ★ 表情面板保持展开: 用户常常要连发几个表情, 每发一条就收起来会让人反复点开。
        }
        finally
        {
            Sending = false;
            OnPropertyChanged(nameof(CanSend));
        }
    }

    /// <summary>
    /// 上传并发送一张图片。
    /// ★ 选完就发, 不做二次确认 —— 微信也是这个流程; 多一步确认只会让发图变慢。
    /// </summary>
    public async Task SendImageAsync(string filePath)
    {
        var session = _current;
        if (session == null) return;
        if (!Svc.Session.HasLogin)
        {
            Svc.Toast.Show("发送图片需要先登录");
            return;
        }

        UploadingImage = true;
        try
        {
            var (ok, err, url, w, h) = await Svc.Api.UploadMsgImageAsync(filePath);
            if (!ok || url.Length == 0)
            {
                Svc.Toast.Show("图片上传失败: " + (err ?? "未知错误"));
                return;
            }

            // 上传期间用户可能已经切到别的会话 —— 那就别把图发错人
            if (!ReferenceEquals(_current, session)) return;

            var (sent, sendErr) = await Svc.Api.SendMsgImageAsync(session.TalkerId, url, w, h);
            if (!sent)
            {
                Svc.Toast.Show("发送失败: " + (sendErr ?? "未知错误"));
                return;
            }

            AppendMessage(new MsgItem
            {
                SenderUid = MyMid,
                IsMine = true,
                ImageUrl = url,
                MsgType = 2,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Seqno = Messages.Count > 0 ? Messages[^1].Seqno + 1 : 1,
                TimeText = DateTime.Now.ToString("MM-dd HH:mm")
            });

            session.Preview = "[图片]";
            session.TimeText = DateTime.Now.ToString("MM-dd HH:mm");
        }
        finally
        {
            UploadingImage = false;
        }
    }

    /// <summary>追加一条自己发的消息并重算分组(末尾这一组要合并进去)</summary>
    private void AppendMessage(MsgItem item)
    {
        Messages.Add(item);
        MarkGroups();
    }

    /// <summary>我的 UID(乐观追加的消息要填 sender_uid, 否则分组会把它当成对方)</summary>
    private long MyMid
        => long.TryParse(Svc.Session.Current.DedeUserID, out var v) ? v : -1;

    // ---------------------------------------------------------------- 表情

    /// <summary>把表情短代码插到草稿末尾(输入框没有光标位置概念时最稳的插入方式)</summary>
    public void InsertEmote(MsgEmote emote)
    {
        if (emote == null || emote.Text.Length == 0) return;
        Draft = (_draft ?? "") + emote.Text;
    }

    /// <summary>表情面板的数据(首次展开时才拉)</summary>
    private async Task EnsureEmotesAsync()
    {
        // ★ 已经有表情包就别再拉一次: 每次展开都重拉的话, 面板会先清空再填回,
        //   用户看到的是"闪一下"(探针里表现为错误提示和表情同时出现)。
        if (_emojiLoaded || EmojiLoading || EmotePackages.Count > 0) return;

        EmojiLoading = true;
        EmojiError = "";
        try
        {
            var (ok, err, packages) = await Svc.Api.GetMsgEmotePanelAsync();
            if (!ok || packages == null)
            {
                EmojiError = err ?? "表情加载失败";
                return;
            }

            EmotePackages.Clear();
            foreach (var p in packages) EmotePackages.Add(p);
            _emojiLoaded = EmotePackages.Count > 0;
            OnPropertyChanged(nameof(HasEmotes));
            if (!_emojiLoaded) EmojiError = "没有可用的表情";
        }
        catch (Exception ex)
        {
            EmojiError = ex.Message;
        }
        finally
        {
            EmojiLoading = false;
        }
    }

    /// <summary>给一条消息挂上它用到的表情(乐观追加时用)</summary>
    private void AttachEmotes(MsgItem item)
    {
        if (item.Text.Length == 0 || EmotePackages.Count == 0) return;
        foreach (var pkg in EmotePackages)
            foreach (var e in pkg.Emotes)
                if (item.Text.Contains(e.Text, StringComparison.Ordinal))
                    item.Emotes[e.Text] = e;
    }
}
