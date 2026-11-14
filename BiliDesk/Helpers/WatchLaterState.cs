using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BiliDesk.Services;

namespace BiliDesk.Helpers;

/// <summary>
/// 「稍后再看」的全局状态(2026-11-12 用户要求"加入后按钮变成对勾, 再次点击移除, 且状态持久化")。
///
/// ★ 单一真相在**服务端列表**: 启动/首次需要时拉一次全量 aid 集合, 增删成功后本地立即同步。
///   所以"持久化"不靠本地文件 —— 下次启动重新拉一次, 状态与账号里的稍后再看完全一致,
///   直到视频真的从列表里被移除。
/// ★ 变更通过 <see cref="Changed"/> 广播: 所有页面上的所有卡片同时刷新按钮图标,
///   不需要重新进页面, 也不会出现"这边显示已加入、那边还显示旗帜"。
/// ★ 未登录 / 拉取失败时集合为空且 <see cref="IsLoaded"/> 为 false: 按钮按"未加入"显示,
///   点击时再提示登录 —— 不要把"没查到"当成"不在列表里"去渲染成负面状态。
/// </summary>
public static class WatchLaterState
{
    private static readonly HashSet<long> Ids = new();
    private static readonly object Gate = new();
    private static bool _loaded;
    private static int _loading;

    /// <summary>
    /// 集合代次: <see cref="Reset"/> 与 <see cref="SetAll"/> 会自增。
    /// <see cref="EnsureLoadedAsync"/> 在发请求时记下代次, 回来时对不上就丢弃结果 ——
    /// 否则"登出瞬间还有一个在途请求"会把它抓到的**旧账号列表**重新写回集合,
    /// 刚清掉的对勾又冒出来(直到下一次会话变更为止)。
    /// </summary>
    private static int _version;

    /// <summary>集合变化(加入/移除/整表重载)时触发, 总是在 UI 线程。</summary>
    public static event Action? Changed;

    static WatchLaterState()
    {
        // ★ 登录 / 退出登录 / 切账号: 本地集合整份作废, 再按服务端重新拉一次。
        //   不这么做的话, "登出后用另一个账号登录"会继续沿用上一个账号的 aid 集合:
        //   _loaded 仍为 true ⇒ EnsureLoadedAsync 直接短路 ⇒ 所有卡片显示旧账号的对勾,
        //   直到重启才恢复。
        Svc.Session.Changed += OnSessionChanged;
    }

    /// <summary>
    /// 会话变了: 清空集合, 已登录就按服务端重拉。
    /// ★ 必须编组到 UI 线程: SessionManager.Changed 的触发点不只有 UI 线程上的登录/登出,
    ///   启动时线程池上的 buvid 预热也会走 Save() → Changed(见 SessionManager 的 _gate 说明);
    ///   而 <see cref="Changed"/> 的订阅者(视频卡片)会直接改 WPF 控件。
    /// </summary>
    private static void OnSessionChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        if (dispatcher.CheckAccess()) { ResetAndReload(); return; }
        dispatcher.BeginInvoke(new Action(ResetAndReload));
    }

    private static void ResetAndReload()
    {
        Reset();
        if (Svc.Session.HasLogin) _ = EnsureLoadedAsync();
    }

    /// <summary>是否已经从服务端拉到过一次列表(用于按钮在"未知"时不显示错误状态)。</summary>
    public static bool IsLoaded { get { lock (Gate) return _loaded; } }

    public static bool Contains(long aid)
    {
        if (aid <= 0) return false;
        lock (Gate) return Ids.Contains(aid);
    }

    /// <summary>首次需要时拉一次列表; 未登录/失败静默, 下次调用再试。</summary>
    public static async Task EnsureLoadedAsync()
    {
        if (!Svc.Session.HasLogin) return;

        int version;
        lock (Gate)
        {
            if (_loaded || _loading > 0) return;
            _loading++;
            version = _version;   // 记下代次: 期间登出/切账号则本次结果作废
        }

        try
        {
            var (ok, _, items, _) = await Svc.Api.GetWatchLaterAsync();
            if (!ok || items == null) return;

            lock (Gate)
            {
                if (version != _version) return;   // 会话已变, 这份属于旧账号, 丢掉
                Ids.Clear();
                foreach (var item in items)
                {
                    if (item.Aid > 0) Ids.Add(item.Aid);
                }
                _loaded = true;
            }
            Changed?.Invoke();
        }
        catch
        {
            // 网络异常: 保持"未加载", 不影响使用
        }
        finally
        {
            lock (Gate) { _loading--; }
        }
    }

    /// <summary>强制重新拉整个列表(例如刚进了稍后再看页, 或跨设备改过)。</summary>
    public static async Task ReloadAsync()
    {
        lock (Gate)
        {
            _version++;   // 在途的旧请求结果作废
            _loaded = false;
        }
        await EnsureLoadedAsync();
    }

    /// <summary>用一整份列表(如稍后再看页刚加载完)重建集合, 并通知所有卡片刷新。</summary>
    public static void SetAll(IEnumerable<long> aids)
    {
        lock (Gate)
        {
            _version++;   // 这份是权威列表, 期间在途的旧请求结果一律作废
            Ids.Clear();
            foreach (var id in aids)
            {
                if (id > 0) Ids.Add(id);
            }
            _loaded = true;
        }
        Changed?.Invoke();
    }

    /// <summary>本地立即反映一次增删(服务端请求成功后调用), 并通知所有卡片刷新。</summary>
    public static void Set(long aid, bool inList)
    {
        if (aid <= 0) return;
        bool changed;
        lock (Gate)
        {
            changed = inList ? Ids.Add(aid) : Ids.Remove(aid);
            if (inList) _loaded = true;
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>退出登录 / 切账号时清空(下次进页面重新拉)。</summary>
    public static void Reset()
    {
        bool any;
        lock (Gate)
        {
            any = Ids.Count > 0;
            _version++;   // 让在途请求的结果作废(见 _version)
            Ids.Clear();
            _loaded = false;
        }
        if (any) Changed?.Invoke();
    }
}
