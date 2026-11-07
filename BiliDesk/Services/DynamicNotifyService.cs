using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Models;

namespace BiliDesk.Services;

/// <summary>一个 UP 主的未读动态状态(只在服务内部维护, 对外给快照)。</summary>
public sealed class UpNotifyState
{
    public long Mid { get; init; }

    /// <summary>UP 昵称(左栏"有新视频"置顶分组要显示名字, 未加载的 UP 也要能显示)。</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// UP 头像地址(2026-11-06)。
    ///
    /// ★ 为什么必须有: 左栏那些"有未读、但还没被分页加载出来"的 UP 是**占位行**,
    ///   它们不来自关注列表接口, 只能从 portal 的 up_list 拿 —— 而 up_list 是带 face 的
    ///   (实测 31/31 全有)。不给 face 的话占位行就没有头像。
    /// ★ 更要命的是虚拟化回收: 容器被复用给下一个 UP 时, 旧头像会留在那一格上,
    ///   于是"这个 UP 显示的是别人的头像"(用户 2026-11-06 报的头像错乱)。
    /// </summary>
    public string Face { get; set; } = "";

    public string LatestId { get; set; } = "";
    public long LatestPubdate { get; set; }
    public int NewCount { get; set; }
    public bool HasNew => NewCount > 0;

    /// <summary>
    /// 这个红点是 portal 兜底补的(动态流里没有具体条目, 所以 LatestId 为空)。
    /// 用户点开/全部已读后置 true, 之后同一轮里不再被 portal 重新补上 ——
    /// 否则点一下红点又立刻亮回来, 像"点了没用"。
    /// 动态流下次给出这个 UP 的真实条目时会被清回 false。
    /// </summary>
    public bool PortalDismissed { get; set; }
}

/// <summary>
/// 「UP 主新动态」的应用级检测服务(2026-11-06 重做)。
///
/// 为什么不再放在 FollowViewModel 里:
///   · 旧实现只在你停留在动态页时才轮询, 人在首页/设置页/托盘时收不到红点;
///   · 只看动态流第一页, 繁忙时新视频被挤到第二页就漏;
///   · 本地拿 bvid 逐个比对, 只能判断"最新一条变没变", 很容易漏掉多条更新。
///
/// 现在的做法:
///   · 每 60 秒请求一次 feed/nav, 带上一次的 `update_baseline`;
///   · 接口返回的 `update_num` 是服务端统计的新动态数, 用它判断要不要多翻几页;
///   · 用动态 id(雪花 id, 单调递增)做新旧判断, 比 bvid 可靠;
///   · 状态在应用生命周期内常驻, 动态页/导航栏都订阅它的事件。
///
/// 只统计**视频投稿**: feed/nav 本身就是视频动态列表, 与动态页右栏内容一致。
/// </summary>
public sealed class DynamicNotifyService
{
    public static DynamicNotifyService Instance { get; } = new();

    /// <summary>轮询间隔。1 分钟是"提示够及时"与"接口压力"的折中。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    /// <summary>页面打开时如果距上次轮询不到这个时间, 不重复请求。</summary>
    private static readonly TimeSpan FreshWindow = TimeSpan.FromSeconds(25);

    /// <summary>一次轮询最多翻多少页(update_num 很大时防止无限请求)。</summary>
    private const int MaxPagesPerPoll = 5;

    /// <summary>每个 UP 最多显示多少条未读(角标不至于撑破)。</summary>
    private const int MaxCountPerUp = 99;

    private readonly Dictionary<long, UpNotifyState> _states = new();
    private readonly object _gate = new();

    private DispatcherTimer? _timer;
    private bool _started;
    private bool _busy;
    private bool _initialized;
    private string? _pollBaseline;
    private long _lastPollAt;

    /// <summary>状态有任何变化时触发(总未读数、某个 UP 的条数/顺序)。在 UI 线程触发。</summary>
    public event Action? Updated;

    /// <summary>全部 UP 的未读条数之和(导航栏红点用)。</summary>
    public int TotalUnread
    {
        get
        {
            lock (_gate)
            {
                var n = 0;
                foreach (var s in _states.Values) n += s.NewCount;
                return n;
            }
        }
    }

    public bool HasUnread => TotalUnread > 0;

    /// <summary>
    /// 所有"有未读红点"的 UP 快照, 按最近发布时间从新到旧。
    ///
    /// ★ 用途: 关注 2790 人时, 一个刚更新的 UP 很可能还在左栏第 2 页之后,
    ///   用户根本翻不到。动态页把这份快照渲染成「有新视频」置顶分组,
    ///   保证"谁刚更了"永远在可见区最上面, 而不是靠排序把它排到前面(排序只影响已加载的行)。
    /// </summary>
    public List<(long Mid, string Name, string Face, int NewCount, long LatestPubdate)> GetUnreadSnapshot()
    {
        lock (_gate)
        {
            var list = new List<(long, string, string, int, long)>();
            foreach (var s in _states.Values)
                if (s.NewCount > 0)
                    list.Add((s.Mid, s.Name, s.Face, s.NewCount, s.LatestPubdate));
            list.Sort((a, b) => b.Item5.CompareTo(a.Item5));
            return list;
        }
    }

    /// <summary>取某个 UP 的状态快照; 没有状态返回 false。</summary>
    public bool TryGetState(long mid, out int newCount, out long latestPubdate)
    {
        lock (_gate)
        {
            if (_states.TryGetValue(mid, out var s))
            {
                newCount = s.NewCount;
                latestPubdate = s.LatestPubdate;
                return true;
            }
        }
        newCount = 0;
        latestPubdate = 0;
        return false;
    }

    /// <summary>
    /// 启动服务。App.OnStartup 调一次; 未登录时只挂事件, 登录后自动开始轮询。
    /// </summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        _pollBaseline = DynamicPollStore.Load();

        Svc.Session.Changed += OnSessionChanged;
        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += (_, _) => _ = RefreshAsync(force: false);
        _timer.Start();

        if (Svc.Session.HasLogin)
            _ = RefreshAsync(force: true);
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
        if (_started) Svc.Session.Changed -= OnSessionChanged;
        _started = false;
    }

    private void OnSessionChanged()
    {
        if (!Svc.Session.HasLogin)
        {
            lock (_gate) _states.Clear();
            _initialized = false;
            FireUpdated();
            return;
        }
        _ = RefreshAsync(force: true);
    }

    /// <summary>
    /// 刷新一次。force=false 时 25 秒内不重复请求(定时器/页面打开可能撞在一起)。
    /// 失败静默: 保留上一轮红点, 下一拍再试。
    /// </summary>
    public async Task RefreshAsync(bool force)
    {
        if (!Svc.Session.HasLogin) return;
        if (_busy) return;

        var now = Environment.TickCount64;
        if (!force && _lastPollAt > 0 && now - _lastPollAt < (long)FreshWindow.TotalMilliseconds)
            return;

        _busy = true;
        try
        {
            // 第一次(或刚登录)不带 baseline, 拿完整第一页来建立基准/统计历史未读;
            // 之后带 baseline 走增量, update_num 会告诉我们新增了几条。
            // ★ 重启后 _pollBaseline 是持久化的: 即使本进程 _initialized=false, 也优先用它,
            //   这样 update_num 能准确告诉我们"离线期间新增了多少", 再按需多翻页。
            var firstRun = !_initialized;
            var hasBaseline = !string.IsNullOrEmpty(_pollBaseline);
            var oldBaseline = _pollBaseline;
            var (ok, _, entries, nextOffset, hasMore, updateBaseline, updateNum) =
                await Svc.Api.GetDynamicNavAsync(null, hasBaseline ? _pollBaseline : null);
            if (!ok || entries == null) return;

            var all = new List<DynamicNavEntry>(entries);
            var offset = nextOffset;
            var pages = 1;

            // 判断"这一页是否已经回到上次的基线": 若旧基线出现在本页, 说明新增条目都拿到了,
            // 不需要再翻。若没出现, 说明新增比一页还多, 需要继续翻到旧基线为止。
            // ★ 这比 update_num 可靠: 实测 update_num 有时恒为 0, 但 update_baseline 确实前进了。
            var covered = string.IsNullOrEmpty(oldBaseline) ||
                          all.Exists(e => string.Equals(e.Id, oldBaseline, StringComparison.Ordinal));

            var maxPages = hasBaseline ? MaxPagesPerPoll : 3;
            while (pages < maxPages && hasMore && !string.IsNullOrEmpty(offset))
            {
                var needMore = string.IsNullOrEmpty(oldBaseline)
                    ? pages < 3
                    : (!covered || updateNum > all.Count);
                if (!needMore) break;

                var (nOk, _, nEntries, nNext, nHasMore, _, _) =
                    await Svc.Api.GetDynamicNavAsync(offset, null);
                if (!nOk || nEntries == null) break;
                all.AddRange(nEntries);
                if (!string.IsNullOrEmpty(oldBaseline) &&
                    nEntries.Exists(e => string.Equals(e.Id, oldBaseline, StringComparison.Ordinal)))
                    covered = true;
                offset = nNext;
                hasMore = nHasMore;
                pages++;
                if (nEntries.Count == 0) break;
            }

            ApplyEntries(all, firstRun);
            _pollBaseline = updateBaseline ?? _pollBaseline;
            if (!string.IsNullOrEmpty(_pollBaseline)) DynamicPollStore.Save(_pollBaseline);
            _initialized = true;
            _lastPollAt = Environment.TickCount64;

            // ---- 兜底: portal 的 up_list ----
            // feed/nav 只给近期动态, 关注 2790 人时, 一个 UP 前天发的视频早就被挤出流;
            // 这时红点永远不会亮。portal 的 up_list 是 B 站自己算的"这个 UP 最近有更新",
            // 覆盖更广 —— 用它把"有更新但动态流里没出现"的 UP 也标上红点。
            // ★ 只标 1 条: 没有具体条目, 算不出准确条数, 但"有没有"这件事它是权威的。
            await ApplyPortalFallbackAsync();

            FireUpdated();
        }
        catch
        {
            // 静默: 红点是后台提示, 网络抖动/风控不应该打扰用户。
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// 把一批动态条目按 UP 归并, 更新未读状态。
    ///
    /// 增量轮询与首次全量轮询的差异:
    ///   · 首次/重启后: 用本地 ack 与条目比较, 统计"当前还能看到的未读";
    ///   · 增量轮询: 只统计 id &gt; 上次已见最新 id 的条目, 累加到 NewCount,
    ///     这样"只返回增量"的接口形态也不会把之前的未读覆盖成 0。
    /// </summary>
    private void ApplyEntries(IReadOnlyList<DynamicNavEntry> entries, bool firstRun)
    {
        if (entries.Count == 0) return;

        var byMid = new Dictionary<long, List<DynamicNavEntry>>();
        foreach (var e in entries)
        {
            if (e == null || e.Mid <= 0 || string.IsNullOrEmpty(e.Id)) continue;
            if (!byMid.TryGetValue(e.Mid, out var list))
                byMid[e.Mid] = list = new List<DynamicNavEntry>();
            list.Add(e);
        }

        lock (_gate)
        {
            // 首次运行(DynamicSeenStore 里一条基准都没有)只建立基准、不亮红点;
            // 之后出现的"没有基准的 UP"说明它以前没进过动态流, 现在有新视频 —— 应该亮。
            var seedMissingAcks = firstRun && DynamicSeenStore.Count == 0;

            foreach (var (mid, list) in byMid)
            {
                var latest = list[0];   // 动态流按时间倒序, 第一条就是最新

                if (!_states.TryGetValue(mid, out var state))
                {
                    // 没有内存状态 = 本进程第一次看到这个 UP(或刚重启)。
                    if (!DynamicSeenStore.TryGet(mid, out var ack))
                    {
                        if (seedMissingAcks)
                        {
                            // 首次运行: 只建立基准, 不亮红点。
                            DynamicSeenStore.Set(mid, latest.Id);
                            _states[mid] = new UpNotifyState
                            {
                                Mid = mid,
                                Name = latest.AuthorName,
                                LatestId = latest.Id,
                                LatestPubdate = latest.Pubdate,
                                NewCount = 0,
                            };
                            continue;
                        }

                        // 运行中第一次见到的 UP(以前没有动态/新关注): 本页这些条目都算未读,
                        // 但不推进 ack —— 用户点开后才算已读, 重启后红点仍然在。
                        _states[mid] = new UpNotifyState
                        {
                            Mid = mid,
                            Name = latest.AuthorName,
                            LatestId = latest.Id,
                            LatestPubdate = latest.Pubdate,
                            NewCount = Math.Min(MaxCountPerUp, Math.Max(1, list.Count)),
                        };
                        continue;
                    }

                    var count = CountNewerThanAck(list, ack);
                    _states[mid] = new UpNotifyState
                    {
                        Mid = mid,
                        Name = latest.AuthorName,
                        LatestId = latest.Id,
                        LatestPubdate = latest.Pubdate,
                        NewCount = Math.Min(MaxCountPerUp, count),
                    };
                    continue;
                }

                // 增量: 统计比"上次已见最新 id"更新的条目。旧版一轮只返回增量时
                // 这些条目不会出现在后面的页里, 所以必须累加而不是重算。
                var added = 0;
                foreach (var e in list)
                {
                    if (IsIdNewer(e.Id, state.LatestId)) added++;
                }
                if (added > 0)
                    state.NewCount = Math.Min(MaxCountPerUp, state.NewCount + added);

                if (latest.Pubdate > state.LatestPubdate) state.LatestPubdate = latest.Pubdate;
                if (IsIdNewer(latest.Id, state.LatestId)) state.LatestId = latest.Id;
                if (!string.IsNullOrEmpty(latest.AuthorName)) state.Name = latest.AuthorName;
            }

            DynamicSeenStore.Flush();
        }
    }

    /// <summary>当前列表里比 ack 新的条目数(首次/重启后的统计)。</summary>
    private static int CountNewerThanAck(List<DynamicNavEntry> list, string ack)
    {
        // 旧版 DynamicSeenStore 存的是 bvid: 找到那条在列表里的位置, 它之前的都是新的。
        if (!IsNumericId(ack))
        {
            for (var i = 0; i < list.Count; i++)
                if (string.Equals(list[i].Bvid, ack, StringComparison.Ordinal)) return i;
            // 找不到(旧视频已滚出本页): 把本页该 UP 的条目都算新的, 至少保证红点亮。
            return list.Count;
        }

        var n = 0;
        foreach (var e in list)
            if (IsIdNewer(e.Id, ack)) n++;
        return n;
    }

    /// <summary>
    /// portal up_list 兜底: 服务端说"这个 UP 最近有更新"、而动态流里没出现过它的条目时,
    /// 至少给它亮 1 条红点。
    ///
    /// ★ 为什么不做成"直接覆盖 NewCount":
    ///   up_list 只给布尔值, 没有条数; 若动态流已经算出 3 条, 用 1 覆盖是倒退。
    ///   所以只在 NewCount == 0 时补 1。
    /// ★ 也不推进 DynamicSeenStore 基准: 没有动态 id 可存。用户点开这个 UP 后
    ///   MarkRead 找不到 LatestId 会跳过, 红点保持到下一轮 —— 但下一轮动态流若给了
    ///   具体条目就会正常推进, 属于可接受的过渡态。
    /// </summary>
    private async Task ApplyPortalFallbackAsync()
    {
        var (ok, _, ups) = await Svc.Api.GetRecentUpdatedUpsAsync();
        if (!ok || ups == null) return;

        lock (_gate)
        {
            foreach (var (mid, name, face, hasUpdate) in ups)
            {
                if (mid <= 0) continue;

                if (!hasUpdate)
                {
                    // 服务端说"没更新": 只有在动态流里也从没出现过这个 UP 时才清掉
                    // 兜底红点。已经有具体条目的 UP 不动 —— 那可能是 portal 延迟。
                    if (_states.TryGetValue(mid, out var clean) &&
                        clean.NewCount > 0 && string.IsNullOrEmpty(clean.LatestId))
                        clean.NewCount = 0;
                    continue;
                }

                if (_states.TryGetValue(mid, out var state))
                {
                    if (!string.IsNullOrEmpty(name) && string.IsNullOrEmpty(state.Name))
                        state.Name = name;
                    // 头像也补上: 占位行要靠它才有头像(虚拟化回收时没头像就会显示上一行的图)
                    if (!string.IsNullOrEmpty(face) && string.IsNullOrEmpty(state.Face))
                        state.Face = face;
                    // 动态流里已经有这个 UP 的真实条目: 以条数为准, 并解除 portal 兜底。
                    if (!string.IsNullOrEmpty(state.LatestId))
                    {
                        state.PortalDismissed = false;
                        continue;
                    }
                    // 用户已经点过这个兜底红点: 本轮不再补。
                    if (state.PortalDismissed) continue;
                    if (state.NewCount <= 0) state.NewCount = 1;
                    continue;
                }

                _states[mid] = new UpNotifyState
                {
                    Mid = mid,
                    Name = name,
                    Face = face,
                    LatestId = "",
                    LatestPubdate = 0,
                    NewCount = 1,
                };
            }
        }
    }

    private static bool IsNumericId(string id) =>
        !string.IsNullOrEmpty(id) && ulong.TryParse(id, out _) &&
        !id.StartsWith("BV", StringComparison.OrdinalIgnoreCase);

    private static bool IsIdNewer(string id, string baseline)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(baseline)) return false;
        if (ulong.TryParse(id, out var a) && ulong.TryParse(baseline, out var b)) return a > b;
        // 非雪花 id(极少见): 退化成字符串比较, 只用于"不同"判断。
        return !string.Equals(id, baseline, StringComparison.Ordinal);
    }

    /// <summary>点开某个 UP: 把它的基准推进到最新一条, 清红点。</summary>
    public void MarkRead(long mid)
    {
        if (mid <= 0) return;
        lock (_gate)
        {
            if (!_states.TryGetValue(mid, out var state))
                return;

            if (string.IsNullOrEmpty(state.LatestId))
            {
                // portal 兜底的红点(动态流里没有具体条目): 只清红点并记住"用户已看过",
                // 否则下一轮 portal 又把它补回来。
                state.NewCount = 0;
                state.PortalDismissed = true;
            }
            else
            {
                DynamicSeenStore.Set(mid, state.LatestId);
                state.NewCount = 0;
                state.PortalDismissed = false;
            }
            DynamicSeenStore.Flush();
        }
        FireUpdated();
    }

    /// <summary>「全部已读」: 所有红点清零并把基准推进到各自最新一条。</summary>
    public void MarkAllRead()
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var s in _states.Values)
            {
                if (s.NewCount <= 0) continue;
                if (string.IsNullOrEmpty(s.LatestId))
                {
                    s.NewCount = 0;
                    s.PortalDismissed = true;
                }
                else
                {
                    DynamicSeenStore.Set(s.Mid, s.LatestId);
                    s.NewCount = 0;
                    s.PortalDismissed = false;
                }
                changed = true;
            }
            if (changed) DynamicSeenStore.Flush();
        }
        if (changed) FireUpdated();
    }

    /// <summary>取消关注: 忘掉这个 UP 的红点与本地基准(重新关注不会继承旧进度)。</summary>
    public void Forget(long mid)
    {
        if (mid <= 0) return;
        var changed = false;
        lock (_gate)
        {
            if (_states.Remove(mid)) changed = true;
            DynamicSeenStore.Remove(mid);
            DynamicSeenStore.Flush();
        }
        if (changed) FireUpdated();
    }

    private void FireUpdated()
    {
        var app = Application.Current;
        if (app == null) return;
        try
        {
            if (app.Dispatcher.CheckAccess()) Updated?.Invoke();
            else app.Dispatcher.BeginInvoke(new Action(() => Updated?.Invoke()));
        }
        catch
        {
            // 关闭过程中 Dispatcher 可能已停; 红点事件丢了不影响退出。
        }
    }

    /// <summary>保存全局轮询基线(update_baseline), 跨进程复用。</summary>
    private static class DynamicPollStore
    {
        private static string FilePath => Path.Combine(AppPaths.DataDir, "dynamic_poll.json");
        private static readonly object Gate = new();

        public static string? Load()
        {
            try
            {
                if (string.IsNullOrEmpty(AppPaths.DataDir) || !File.Exists(FilePath)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                return doc.RootElement.TryGetProperty("baseline", out var b) &&
                       b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            }
            catch { return null; }
        }

        public static void Save(string baseline)
        {
            if (string.IsNullOrEmpty(baseline) || string.IsNullOrEmpty(AppPaths.DataDir)) return;
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(AppPaths.DataDir);
                    AtomicFile.WriteAllText(FilePath,
                        JsonSerializer.Serialize(new { baseline }));
                }
            }
            catch { /* 只影响下次是否多翻一页, 静默 */ }
        }
    }
}
