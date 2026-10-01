using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.ViewModels;

/// <summary>
/// B 站消息页: 左侧会话列表 + 右侧私信内容。
/// 接口都是 vc.bilibili.com 那一套(见 ApiClient 里的私信段落), 全部需要登录。
/// </summary>
public class MessagesViewModel : ObservableObject
{
    public ObservableCollection<MsgSession> Sessions { get; } = new();
    public ObservableCollection<MsgItem> Messages { get; } = new();

    private bool _loading;
    private string _error = "";
    private MsgSession? _current;
    private bool _threadLoading;
    private string _threadError = "";
    private string _draft = "";
    private bool _sending;

    public bool Loading { get => _loading; private set => SetProperty(ref _loading, value); }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }

    /// <summary>当前选中的会话(ListBox 的 SelectedItem 双向绑过来)</summary>
    public MsgSession? Current
    {
        get => _current;
        set
        {
            if (!SetProperty(ref _current, value)) return;
            OnPropertyChanged(nameof(HasCurrent));
            OnPropertyChanged(nameof(CurrentTitle));
            OnPropertyChanged(nameof(CanSend));
        }
    }

    public bool HasCurrent => _current != null;
    public string CurrentTitle => _current?.DisplayName ?? "";

    public bool ThreadLoading { get => _threadLoading; private set => SetProperty(ref _threadLoading, value); }
    public string ThreadError { get => _threadError; private set => SetProperty(ref _threadError, value); }

    /// <summary>输入框内容(空/全空白时发送按钮会自己变灰)</summary>
    public string Draft
    {
        get => _draft;
        set { if (SetProperty(ref _draft, value)) OnPropertyChanged(nameof(CanSend)); }
    }

    public bool Sending { get => _sending; private set => SetProperty(ref _sending, value); }

    /// <summary>输入框有内容且不是正在发送 → 发送按钮可用</summary>
    public bool CanSend => !_sending && !string.IsNullOrWhiteSpace(_draft) && _current != null;

    public ICommand RefreshCommand { get; }
    public ICommand SendCommand { get; }

    public MessagesViewModel()
    {
        RefreshCommand = new RelayCommand(() => _ = LoadAsync());
        SendCommand = new RelayCommand(() => _ = SendAsync());
    }

    /// <summary>加载会话列表</summary>
    public async Task LoadAsync()
    {
        if (Loading) return;
        if (!Svc.Session.HasLogin)
        {
            Error = "查看私信需要先登录 B 站账号";
            return;
        }

        Loading = true;
        Error = "";
        MsgSession? reload = null;
        try
        {
            var (ok, err, sessions) = await Svc.Api.GetMsgSessionsAsync();
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
        }

        if (reload != null) await LoadThreadAsync(reload);
    }

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
        try
        {
            var (ok, err, items, _) = await Svc.Api.GetMsgHistoryAsync(session.TalkerId);
            if (!ok || items == null)
            {
                ThreadError = err ?? "私信内容加载失败";
                return;
            }
            foreach (var m in items) Messages.Add(m);
            if (Messages.Count == 0) ThreadError = "还没有聊过, 发条消息打个招呼吧";

            // 顺带把会话标成已读: 未读角标清掉, 服务端也同步一下
            var lastSeq = Messages.Count > 0 ? Messages[^1].Seqno : 0;
            if (lastSeq > 0)
            {
                session.UnreadCount = 0;
                _ = Svc.Api.MarkMsgReadAsync(session.TalkerId, lastSeq);
            }
        }
        catch (Exception ex)
        {
            ThreadError = ex.Message;
        }
    }

    /// <summary>发送当前输入框里的文字</summary>
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
            Messages.Add(new MsgItem
            {
                IsMine = true,
                Text = text,
                Seqno = Messages.Count > 0 ? Messages[^1].Seqno + 1 : 1,
                TimeText = DateTime.Now.ToString("MM-dd HH:mm")
            });
            session.Preview = text;
            session.TimeText = DateTime.Now.ToString("MM-dd HH:mm");
            Draft = "";
        }
        finally
        {
            Sending = false;
            OnPropertyChanged(nameof(CanSend));
        }
    }
}
