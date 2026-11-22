using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using BiliDesk.Models;

namespace BiliDesk.Services;

/// <summary>
/// 评论输入框的表情面板数据源(视频评论区与图文动态评论区共用一份)。
///
/// ★ 为什么要做成单例缓存: 表情面板一屏几十张图, 每开一次输入框就重拉一次的话,
///   面板会先清空再填回, 用户看到的是"闪一下"; 而且两个输入框(播放器 / 动态详情)
///   拿的是同一份账号表情, 没有理由各拉一遍。
/// ★ 与 MessagesViewModel 里那份表情数据是**分开**的: 那边跟着页面生命周期走,
///   这里的要跨窗口存活, 且要能独立重试(失败后下次展开再试)。
/// </summary>
public sealed class CommentEmoteService
{
    public static CommentEmoteService Instance { get; } = new();

    private CommentEmoteService()
    {
        // 退出登录要清缓存: 表情按账号下发(大会员表情包不同), 留着会让下一个账号看到别人的表情。
        // ★ 订阅放在构造里而不是 Svc 的静态初始化里: 第一次访问 Svc.CommentEmotes 时才发生,
        //   那时 Svc 的静态构造已经完成, 不会绕回自身。
        Svc.Session.Changed += () =>
        {
            if (!Svc.Session.HasLogin) Clear();
        };
    }

    private bool _loaded;
    private bool _loading;

    /// <summary>
    /// 表情包分组(按包分组显示, 与私信面板同一套模型)。
    /// ★ 用 ObservableCollection 而不是 List: 控件直接绑它, 拉取完成/退出登录时
    ///   表情墙会自动跟着变, 不需要宿主再手动重绑一遍 ItemsSource。
    /// </summary>
    public ObservableCollection<MsgEmotePackage> Packages { get; } = new();

    /// <summary>已经拿到过表情(空列表也算拉过, 但会允许重试, 见 <see cref="EnsureLoadedAsync"/>)</summary>
    public bool HasEmotes => Packages.Count > 0;

    public bool IsLoading => _loading;

    /// <summary>上一次拉取失败的原因; 空串 = 没出错。</summary>
    public string Error { get; private set; } = "";

    /// <summary>加载态 / 错误串变化时通知界面(表情墙本身靠集合通知)。</summary>
    public event Action? Changed;

    /// <summary>
    /// 首次展开面板时调一次。已经有表情就直接返回; 上次失败过(没有表情且不在加载中)
    /// 则重试 —— 否则一次网络抖动会让表情面板永远空着。
    /// </summary>
    public async Task EnsureLoadedAsync()
    {
        if (_loaded && Packages.Count > 0) return;
        if (_loading) return;

        _loading = true;
        Error = "";
        RaiseChanged();
        try
        {
            var (ok, err, packages) = await Svc.Api.GetCommentEmotePanelAsync();

            // ★★ 必须切回 UI 线程再动 Packages: ApiClient 内部有 ConfigureAwait(false),
            //   调用方又可能不是从 UI 线程发起的(探针/后台预热), 这时续体会落在线程池上 ——
            //   直接改绑在界面上的 ObservableCollection 会抛
            //   "该类型的 CollectionView 不支持从调度程序线程以外的线程对其 SourceCollection 进行的更改"。
            await OnUiAsync(() =>
            {
                if (!ok || packages == null)
                {
                    Error = err ?? "表情加载失败";
                    _loaded = false;
                    return;
                }

                Packages.Clear();
                foreach (var p in packages) Packages.Add(p);
                _loaded = Packages.Count > 0;
                if (!_loaded) Error = "没有可用的表情";
            });
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _loaded = false;
        }
        finally
        {
            await OnUiAsync(() => { _loading = false; });
            RaiseChanged();
        }
    }

    /// <summary>把一段改动搬到 UI 线程执行(没有 Application 时——单测/无界面场景——直接原地跑)。</summary>
    private static async Task OnUiAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
            return;
        }
        await dispatcher.InvokeAsync(action);
    }

    private void RaiseChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            Changed?.Invoke();
            return;
        }
        dispatcher.BeginInvoke(new Action(() => Changed?.Invoke()));
    }

    /// <summary>
    /// 退出登录后清空缓存: 表情是**按账号**下发的(大会员表情包不同),
    /// 留着上一个账号的表情会让换号后的人看到自己没有的表情。
    /// </summary>
    public void Clear()
    {
        if (Packages.Count == 0 && !_loaded && Error.Length == 0) return;

        // ★ 退出登录事件可能来自任意线程(网络回调/后台), 清空绑在界面上的集合必须回 UI 线程
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(Clear));
            return;
        }

        Packages.Clear();
        _loaded = false;
        _loading = false;
        Error = "";
        Changed?.Invoke();
    }

    /// <summary>把表情包摊平成一张表(按短代码找图片用)。</summary>
    public IEnumerable<MsgEmote> AllEmotes()
        => Packages.SelectMany(p => p.Emotes);
}
