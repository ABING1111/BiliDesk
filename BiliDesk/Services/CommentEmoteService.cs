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

        // ★ 摊平索引跟着集合走: 任何一处往 Packages 里加/删(拉取完成、清空、探针塞假数据),
        //   索引都必须失效。靠调用点手动 InvalidateIndex 迟早漏一处 —— 漏了的表现是
        //   "面板里明明有表情, 私信气泡还是显示成短代码"。
        Packages.CollectionChanged += (_, _) => InvalidateIndex();
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

    /// <summary>
    /// 短代码 → 表情的摊平索引(**只读**), 供私信气泡兜底查图。
    ///
    /// ★★★ 为什么私信要用它(2026-11-28 修"切页面回来表情变文字"):
    ///   私信表情图的权威来源是服务端的 `data.e_infos`, 而它**经常漏掉"我自己发的"表情**。
    ///   漏了就只能拿这份面板表补 —— 同一个接口(`business=reply`)、同一份解析,
    ///   本来就没有理由再让私信自己拉一遍。
    /// ★ 懒建 + 每次 Packages 变化就作废: 面板只拉一次, 但退出登录会 Clear,
    ///   那时索引必须跟着失效(否则下一个账号会看到别人的大会员表情)。
    /// </summary>
    public IReadOnlyDictionary<string, MsgEmote> ByCode
    {
        get
        {
            // Packages 只在 UI 线程被改(EnsureLoadedAsync/Clear 都切了线程), 这里不必加锁
            var cached = _byCode;
            if (cached != null) return cached;

            var map = new Dictionary<string, MsgEmote>(StringComparer.Ordinal);
            foreach (var pkg in Packages)
                foreach (var e in pkg.Emotes)
                    if (e.Text.Length > 0) map.TryAdd(e.Text, e);

            _byCode = map;
            return map;
        }
    }

    /// <summary>按短代码查一个表情; 面板还没加载/没有这个短代码时返回 null(调用方按文字显示)。</summary>
    public MsgEmote? FindByCode(string? code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        return ByCode.TryGetValue(code, out var e) ? e : null;
    }

    /// <summary>
    /// 让摊平索引作废(下次访问 ByCode 时重建)。
    /// ★ 必须在**每一处**改动 Packages 之后调用: 拉取完成、退出登录清空。
    ///   用"集合条数"当版本号是不行的 —— Clear 之后重新灌同一批包, 条数一样但内容已经换了账号。
    /// </summary>
    private void InvalidateIndex() => _byCode = null;

    private IReadOnlyDictionary<string, MsgEmote>? _byCode;
}
