using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Threading;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.Helpers;

/// <summary>
/// 全局列表**屏蔽**: 把标题或 UP 主里含指定词的视频从列表中隐藏掉。
///
/// 语义是"屏蔽"而不是"查找": 命中关键词的内容**不再显示**, 没命中的照常列出来。
/// 之前做成了"只显示包含关键词的内容", 那本质上是二次搜索 —— 用户想要的却是
/// "我不想再看到这类东西", 于是输入一个词之后满屏只剩这个词的内容, 完全是反的。
///
/// 三类条件(全部**叠加**生效):
///   · 标题关键词 / UP 关键词: 手打, 包含匹配(模糊)。适合"综艺 / 剧透"这类词。
///   · 屏蔽 UP 主: 卡片右键一键加, **精确同名 + UID**。不会因为屏蔽"小明"而误伤"小明明"。
///   · 屏蔽标题: 卡片右键一键加, **标题完全相同**才隐藏。适合"这条视频不想再看",
///     但不想连带屏蔽同 UP 的其它视频。
///
/// 实现方式 —— 不动各页面自己的 ObservableCollection, 而是给它们的**默认 CollectionView**
/// 装一个 Filter 谓词。WPF 的 ItemsControl / ListBox 绑定的就是这个默认视图, 所以
/// 一处设置, 所有绑到该集合的界面同时生效; 也不用给每个页面再维护一份"屏蔽后的副本集合"
/// (那样两边很容易不同步)。
///
/// 生命周期说明: <see cref="Attach"/> 用弱引用记视图 —— 个人空间这类页面每次打开都会
/// new 一份 ViewModel/集合, 强引用会把旧集合一直挂在服务上(内存只涨不降)。
/// </summary>
public sealed class FilterService : INotifyPropertyChanged
{
    public static FilterService Instance { get; } = new();

    /// <summary>设置持久化的上限, 防止配置文件被"手滑点了 200 次"撑爆</summary>
    private const int MaxRulesPerKind = 500;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 条件真正落到列表上之后触发。页面据此判断"筛完之后内容不够一屏"并补下一页,
    /// 否则屏蔽一开就会看到大片空白(滚动事件不会因为刷新而触发)。
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// 已纳管的视图。值是该视图自己的额外过滤条件(可以为 null)。
    ///
    /// 用 ConditionalWeakTable 而不是 Dictionary: 键是弱引用, 页面/集合被回收后
    /// 条目自动消失。一个视图只能有一个 `Filter` 谓词, 所以"全局屏蔽"和"页面自己的搜索"
    /// 必须合并进同一个谓词里(谓词里同时问两边), 否则谁后设置谁就把对方顶掉。
    /// </summary>
    private readonly ConditionalWeakTable<ICollectionView, ViewScope> _views = new();

    /// <summary>刷新时要遍历的视图。ConditionalWeakTable 不可枚举, 所以另存一份弱引用表。</summary>
    private readonly List<WeakReference<ICollectionView>> _viewOrder = new();

    private sealed class ViewScope
    {
        /// <summary>页面自己的附加条件(目前没有调用方传, 保留给"页内搜索"接入)</summary>
        public Func<object, bool>? Extra;
    }

    // ---- 关键词(包含匹配) ----

    private string _title = "";
    private string _up = "";

    /// <summary>拆好的屏蔽词。每次输入变化只算一次, 而不是在谓词里对**每个条目**反复 split。</summary>
    private string[] _titleTerms = Array.Empty<string>();
    private string[] _upTerms = Array.Empty<string>();

    // ---- 一键屏蔽(精确匹配) ----

    /// <summary>被屏蔽的 UP。最新加的排最前面(面板里先看到刚点的那条)。</summary>
    private readonly List<BlockedUp> _blockedUps = new();

    /// <summary>被屏蔽的标题(已 Trim, 完全相同才命中)。</summary>
    private readonly List<string> _blockedTitles = new();

    private readonly HashSet<string> _upNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<long> _upMids = new();
    private readonly HashSet<string> _titleSet = new(StringComparer.OrdinalIgnoreCase);

    private sealed class BlockedUp
    {
        public string Name = "";
        public long Mid;
    }

    /// <summary>面板里展示的规则(只含一键屏蔽的 UP / 标题; 关键词单独两份, 见下)</summary>
    public ObservableCollection<FilterRule> Rules { get; } = new();

    /// <summary>
    /// 「屏蔽标题含」当前生效的词条(面板里画成可单条删除的标签)。
    /// ★ 与 <see cref="Rules"/> 分开: 两者的数据来源与增删入口都不同,
    ///   挤进同一个集合会让面板只能靠 Badge 猜该显示在哪一栏。
    /// </summary>
    public ObservableCollection<FilterRule> TitleKeywordRules { get; } = new();

    /// <inheritdoc cref="TitleKeywordRules"/>
    public ObservableCollection<FilterRule> UpKeywordRules { get; } = new();

    private readonly DispatcherTimer _debounce;

    private FilterService()
    {
        // 输入防抖。
        //
        // 为什么必须有: 输入框是 UpdateSourceTrigger=PropertyChanged, 每敲一个字符就会
        // Refresh 一次**所有**注册过的视图(首页四个 tab + 收藏 + 历史 + 搜索 + 分区 + 关注…)。
        // 每次 Refresh 都要把容器全部重建一遍(VideoCard 还不轻, 带封面加载和入场动画),
        // 键盘按住不放时表现就是明显卡顿。攒 220ms 再刷一次, 手感上依然是"边打边变"。
        _debounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            RefreshAll();
        };

        // 构造时先读一次设置。正常启动路径上 SettingsStore.Load() 已经先跑过
        // (见 App.OnStartup); App 里还会再调一次 Reload(), 兜住静态初始化顺序的意外。
        Reload();
    }

    /// <summary>按标题屏蔽的关键词(空 = 不屏蔽)。多个词用空格 / 逗号隔开, 命中任意一个就屏蔽。</summary>
    public string TitleKeyword
    {
        get => _title;
        set => SetKeyword(ref _title, ref _titleTerms, value, nameof(TitleKeyword));
    }

    /// <summary>按 UP 主昵称屏蔽的关键词(空 = 不屏蔽)。同样支持多个词。</summary>
    public string UpKeyword
    {
        get => _up;
        set => SetKeyword(ref _up, ref _upTerms, value, nameof(UpKeyword));
    }

    public bool HasFilter
        => _titleTerms.Length > 0 || _upTerms.Length > 0
           || _blockedUps.Count > 0 || _blockedTitles.Count > 0;

    /// <summary>是否有一键屏蔽的规则(面板据此显示/隐藏标签区)</summary>
    public bool HasRules => _blockedUps.Count > 0 || _blockedTitles.Count > 0;

    /// <summary>给界面看的条件描述</summary>
    public string Description
    {
        get
        {
            if (!HasFilter) return "未屏蔽任何内容";
            var parts = new List<string>();
            if (_blockedUps.Count > 0) parts.Add($"UP 主 {_blockedUps.Count} 个");
            if (_blockedTitles.Count > 0) parts.Add($"标题 {_blockedTitles.Count} 条");
            // ★ 词条可能多达 200 个: 超过 3 个就只报数量, 否则这行会被撑成一条长带。
            if (_titleTerms.Length > 0)
                parts.Add(_titleTerms.Length <= 3
                    ? $"标题含「{string.Join(" / ", _titleTerms)}」"
                    : $"标题含 {_titleTerms.Length} 个词");
            if (_upTerms.Length > 0)
                parts.Add(_upTerms.Length <= 3
                    ? $"UP 含「{string.Join(" / ", _upTerms)}」"
                    : $"UP 含 {_upTerms.Length} 个词");
            return "已屏蔽 " + string.Join("  ·  ", parts);
        }
    }

    /// <summary>
    /// 把某个集合的默认视图纳入屏蔽。页面初始化时调用一次即可(重复调用不会重复注册)。
    /// <paramref name="extra"/> 是页面自己的附加条件(例如页内搜索), 与全局屏蔽取"与"。
    /// </summary>
    public void Attach(System.Collections.IEnumerable source, Func<object, bool>? extra = null)
    {
        if (source == null) return;
        try
        {
            var view = CollectionViewSource.GetDefaultView(source);
            if (view == null || _views.TryGetValue(view, out _)) return;

            var scope = new ViewScope { Extra = extra };
            _views.Add(view, scope);
            _viewOrder.Add(new WeakReference<ICollectionView>(view));

            // 谓词同时问"全局屏蔽"和"这个视图自己的条件"。捕获 scope 而不是把它传进 Matches,
            // 是为了让每个视图拿到的是自己那一份额外条件。
            view.Filter = item => Matches(item) && ScopeAllows(scope, item);

            // 挂上来时立即按当前条件过滤一次, 否则新页面会先把被屏蔽的内容显示出来再消失
            if (HasFilter) view.Refresh();
        }
        catch
        {
            // 个别集合拿不到默认视图(比如已经绑到别的视图上), 忽略即可 —— 只是它不参与屏蔽
        }
    }

    private static bool ScopeAllows(ViewScope scope, object? item)
    {
        if (item == null || scope.Extra == null) return true;
        try { return scope.Extra(item); }
        catch { return true; } // 页面自己的谓词抛了不该连累整个列表
    }

    // ------------------------------------------------------------ 一键屏蔽

    /// <summary>
    /// 屏蔽一个 UP 主。优先用 UID 记规则(UP 改名也拦得住), 同时记下昵称给面板显示、
    /// 以及给"列表没下发 UID"的接口(搜索/推荐的部分条目)兜底。
    ///
    /// ★ 精确同名而不是包含匹配: 用户点的是"这个 UP", 不是"名字里带这几个字的所有 UP"。
    ///   想要模糊屏蔽, 面板里的"屏蔽 UP 主含"就是干这个的。
    /// </summary>
    public void BlockUp(string? author, long mid = 0)
    {
        var name = (author ?? "").Trim();
        if (name.Length == 0 && mid <= 0) return;

        // 已经有同 UID / 同名的规则: 顺手把缺的 UID 补上, 不重复加一条
        var existing = _blockedUps.FirstOrDefault(u =>
            (mid > 0 && u.Mid == mid) ||
            (name.Length > 0 && string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase)));
        if (existing != null)
        {
            if (existing.Mid <= 0 && mid > 0)
            {
                existing.Mid = mid;
                _upMids.Add(mid);
                RebuildRules();
                Persist();
            }
            return;
        }

        if (_blockedUps.Count >= MaxRulesPerKind) _blockedUps.RemoveAt(_blockedUps.Count - 1);
        _blockedUps.Insert(0, new BlockedUp
        {
            // 昵称缺失(极少数接口)时用 UID 占位, 面板里至少有东西可看
            Name = name.Length > 0 ? name : $"UID {mid}",
            Mid = mid
        });
        // 重建索引而不是增量 Add: 上面可能刚挤掉一条旧规则, 增量写法会把它留在
        // 匹配集合里 —— 表现就是"明明删掉了/列表里没有, 内容却还是不显示"。
        RebuildIndexes();

        AfterRulesChanged();
    }

    /// <summary>
    /// 批量屏蔽 UP 主(2026-11-20, issue #98): 导入 B 站黑名单时一次加几十上百条。
    /// ★ 不能循环调 <see cref="BlockUp"/>: 每一条都会 RebuildIndexes + 落盘 + RefreshAll,
    ///   500 条就是 500 次全列表刷新(界面会卡住几秒)。这里合并成一次。
    /// 已存在(同 UID 或同名)的规则不重复加; 缺 UID 的顺手补上。
    /// </summary>
    /// <returns>实际新增的条数</returns>
    public int BlockUps(IEnumerable<BlockedUpEntry>? entries)
    {
        if (entries == null) return 0;

        var added = 0;
        var changed = false;
        foreach (var e in entries)
        {
            if (e == null) continue;
            var name = (e.Name ?? "").Trim();
            var mid = e.Mid > 0 ? e.Mid : 0;
            if (name.Length == 0 && mid == 0) continue;

            var existing = _blockedUps.FirstOrDefault(u =>
                (mid > 0 && u.Mid == mid) ||
                (name.Length > 0 && string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase)));
            if (existing != null)
            {
                if (existing.Mid <= 0 && mid > 0)
                {
                    existing.Mid = mid;
                    changed = true;
                }
                continue;
            }

            if (_blockedUps.Count >= MaxRulesPerKind) break;
            _blockedUps.Add(new BlockedUp
            {
                Name = name.Length > 0 ? name : $"UID {mid}",
                Mid = mid
            });
            added++;
            changed = true;
        }

        // 只补了 UID、一条没新增时也要落盘(下次启动才拦得住改名的 UP)
        if (!changed) return 0;
        RebuildIndexes();
        AfterRulesChanged();
        return added;
    }

    /// <summary>
    /// 屏蔽一个标题。只有**标题完全相同**的条目才隐藏 —— 用户点的是"这一条",
    /// 不该顺手把同 UP 的其它视频一起干掉。要屏蔽整个系列请用标题关键词。
    /// </summary>
    public void BlockTitle(string? title)
    {
        var t = (title ?? "").Trim();
        if (t.Length == 0) return;
        if (!_titleSet.Add(t)) return;

        if (_blockedTitles.Count >= MaxRulesPerKind) _blockedTitles.RemoveAt(_blockedTitles.Count - 1);
        _blockedTitles.Insert(0, t);
        RebuildIndexes();
        AfterRulesChanged();
    }

    /// <summary>撤销一条一键规则(面板里点标签上的 ×)</summary>
    public bool RemoveRule(FilterRule? rule)
    {
        if (rule == null) return false;

        bool changed;
        switch (rule.Kind)
        {
            case FilterRuleKind.Up:
                var index = _blockedUps.FindIndex(u => rule.Mid > 0
                    ? u.Mid == rule.Mid
                    : string.Equals(u.Name, rule.Value, StringComparison.OrdinalIgnoreCase));
                changed = index >= 0;
                if (changed) _blockedUps.RemoveAt(index);
                break;

            case FilterRuleKind.Title:
                changed = _blockedTitles.RemoveAll(t =>
                    string.Equals(t, rule.Value, StringComparison.OrdinalIgnoreCase)) > 0;
                break;

            // 关键词标签走自己的增删入口(改的是 _titleTerms / _upTerms):
            // 这两个集合不是"一键规则", 不经过 BlockedUps/BlockedTitles 那套索引。
            case FilterRuleKind.TitleKeyword:
                return RemoveKeyword(FilterRuleKind.TitleKeyword, rule.Value);

            case FilterRuleKind.UpKeyword:
                return RemoveKeyword(FilterRuleKind.UpKeyword, rule.Value);

            default:
                return false;
        }

        if (!changed) return false;
        RebuildIndexes();
        AfterRulesChanged();
        return true;
    }

    /// <summary>
    /// 添加屏蔽关键词(2026-11-20, issue #98)。<paramref name="raw"/> 可以一次带多个词
    /// (空格 / 逗号 / 顿号 / 分号分隔) —— 与旧版单行文本框的分隔规则完全一致,
    /// 老用户"一次贴一串词"的习惯不用改。
    /// ★ 单条超长直接截断、重复(忽略大小写)跳过、总数封顶 <see cref="FilterKeywords.MaxTerms"/>:
    ///   面板是标签墙, 一条超长词会把整行撑开, 而配置被手改坏时也不能让列表无限长。
    /// </summary>
    /// <returns>实际新增的条数(重复/空项不计)</returns>
    public int AddKeywords(FilterRuleKind kind, string? raw)
    {
        if (kind is not (FilterRuleKind.TitleKeyword or FilterRuleKind.UpKeyword)) return 0;

        var current = kind == FilterRuleKind.TitleKeyword ? _titleTerms : _upTerms;
        var merged = FilterKeywords.Merge(current, raw);
        var added = merged.Count - current.Length;
        if (added <= 0) return 0;

        CommitKeywords(kind, merged);
        return added;
    }

    /// <summary>删除一个屏蔽关键词(面板里点标签上的 ×); 命中返回 true。</summary>
    public bool RemoveKeyword(FilterRuleKind kind, string? value)
    {
        if (kind is not (FilterRuleKind.TitleKeyword or FilterRuleKind.UpKeyword)) return false;

        var term = (value ?? "").Trim();
        if (term.Length == 0) return false;

        var current = kind == FilterRuleKind.TitleKeyword ? _titleTerms : _upTerms;
        var next = FilterKeywords.Remove(current, term);
        if (next.Count == current.Length) return false;

        CommitKeywords(kind, next);
        return true;
    }

    /// <summary>关键词增删的唯一落点: 同步字符串/数组/标签集合, 落盘并排一次刷新。</summary>
    private void CommitKeywords(FilterRuleKind kind, List<string> terms)
    {
        if (kind == FilterRuleKind.TitleKeyword)
        {
            _title = FilterKeywords.Join(terms);
            _titleTerms = terms.ToArray();
            Raise(nameof(TitleKeyword));
        }
        else
        {
            _up = FilterKeywords.Join(terms);
            _upTerms = terms.ToArray();
            Raise(nameof(UpKeyword));
        }

        RebuildRules();
        Raise(nameof(HasFilter));
        Raise(nameof(HasRules));
        Raise(nameof(Description));

        Persist();
        // 与文本框那条同款: 攒 220ms 再刷。连续加几个词时不会每加一个就重建全场列表。
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>清空条件</summary>
    public void Clear()
    {
        // 直接改字段(而不是走属性 setter): 这里要一次刷完, 不能触发多次防抖/落盘
        _title = "";
        _up = "";
        _titleTerms = Array.Empty<string>();
        _upTerms = Array.Empty<string>();
        _blockedUps.Clear();
        _blockedTitles.Clear();
        _upNames.Clear();
        _upMids.Clear();
        _titleSet.Clear();

        RebuildRules();
        Raise(nameof(TitleKeyword));
        Raise(nameof(UpKeyword));
        Raise(nameof(HasFilter));
        Raise(nameof(HasRules));
        Raise(nameof(Description));

        // 清空要**立刻**生效, 不要等防抖: 用户点"清除"时期待的是马上看到全部内容。
        // 顺手把已经排上的那一次防抖取消掉, 免得 220ms 后又白刷一遍。
        _debounce.Stop();
        Persist();
        RefreshAll();
    }

    /// <summary>从设置里重新载入(启动时 / 外部改了配置文件后调用)</summary>
    public void Reload()
    {
        var s = Svc.Settings;

        _title = s.FilterTitleKeyword ?? "";
        _up = s.FilterUpKeyword ?? "";
        _titleTerms = FilterKeywords.Split(_title);
        _upTerms = FilterKeywords.Split(_up);

        _blockedUps.Clear();
        _blockedTitles.Clear();

        foreach (var entry in s.FilterBlockedUps ?? Enumerable.Empty<BlockedUpEntry>())
        {
            if (entry == null) continue;
            var name = (entry.Name ?? "").Trim();
            if (name.Length == 0 && entry.Mid <= 0) continue;
            // 反序列化出来的列表可能被手改坏, 这里的去重在 Persist 侧还会再做一次
            if (name.Length > 0 && _blockedUps.Any(u =>
                    string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            if (entry.Mid > 0 && _blockedUps.Any(u => u.Mid == entry.Mid)) continue;
            _blockedUps.Add(new BlockedUp { Name = name, Mid = entry.Mid });
        }

        foreach (var t in s.FilterBlockedTitles ?? Enumerable.Empty<string>())
        {
            var v = (t ?? "").Trim();
            if (v.Length == 0 || _blockedTitles.Any(x =>
                    string.Equals(x, v, StringComparison.OrdinalIgnoreCase))) continue;
            _blockedTitles.Add(v);
        }

        RebuildIndexes();
        RebuildRules();

        Raise(nameof(TitleKeyword));
        Raise(nameof(UpKeyword));
        Raise(nameof(HasFilter));
        Raise(nameof(HasRules));
        Raise(nameof(Description));

        // 启动时还没有视图, 这里是空转; 运行中重新载入配置时才会真的刷一遍
        if (_viewOrder.Count > 0) RefreshAll();
    }

    private void AfterRulesChanged()
    {
        RebuildRules();
        Raise(nameof(HasFilter));
        Raise(nameof(HasRules));
        Raise(nameof(Description));
        Persist();

        // 一键屏蔽是"点了就要看到结果", 不走防抖
        _debounce.Stop();
        RefreshAll();
    }

    private void RebuildRules()
    {
        Rules.Clear();
        foreach (var u in _blockedUps)
            Rules.Add(new FilterRule
            {
                Kind = FilterRuleKind.Up,
                Value = u.Name,
                Mid = u.Mid,
                Label = u.Name.Length > 0 ? u.Name : $"UID {u.Mid}"
            });
        foreach (var t in _blockedTitles)
            Rules.Add(new FilterRule
            {
                Kind = FilterRuleKind.Title,
                Value = t,
                Label = t
            });

        // 关键词单独两份(2026-11-20, issue #98): 面板里分栏展示, 每条都能单独删。
        TitleKeywordRules.Clear();
        foreach (var t in _titleTerms)
            TitleKeywordRules.Add(new FilterRule
            {
                Kind = FilterRuleKind.TitleKeyword,
                Value = t,
                Label = t
            });
        UpKeywordRules.Clear();
        foreach (var t in _upTerms)
            UpKeywordRules.Add(new FilterRule
            {
                Kind = FilterRuleKind.UpKeyword,
                Value = t,
                Label = t
            });
    }

    private void RebuildIndexes()
    {
        _upNames.Clear();
        _upMids.Clear();
        foreach (var u in _blockedUps)
        {
            if (u.Name.Length > 0) _upNames.Add(u.Name);
            if (u.Mid > 0) _upMids.Add(u.Mid);
        }

        _titleSet.Clear();
        foreach (var t in _blockedTitles) _titleSet.Add(t);
    }

    /// <summary>把当前条件写回设置(异步合并落盘, 逐字调用也没有 I/O 压力)</summary>
    private void Persist()
    {
        Svc.Settings.UpdateFilterSettings(
            titleKeyword: _title,
            upKeyword: _up,
            blockedUps: _blockedUps.Select(u => new BlockedUpEntry { Name = u.Name, Mid = u.Mid }),
            blockedTitles: _blockedTitles);
    }

    /// <summary>
    /// 取出某个集合在**当前屏蔽条件下真正可见**的那些项, 顺序与界面一致。
    ///
    /// 编辑模式(全选 / 删除所选)必须基于这个而不是原集合:
    /// 被屏蔽掉的条目用户根本看不见, 却因为"全选"被一起删掉, 是最典型的"我明明没选它"。
    /// </summary>
    public static IEnumerable<T> Visible<T>(IEnumerable<T> source)
    {
        if (source == null) return Array.Empty<T>();
        var view = CollectionViewSource.GetDefaultView(source);
        return view == null ? source : view.Cast<T>();
    }

    private void SetKeyword(ref string field, ref string[] terms, string value, string propName)
    {
        var v = value ?? "";
        if (field == v) return;
        field = v;
        terms = FilterKeywords.Split(v);
        RebuildRules();   // 关键词标签墙跟着变(即使今天没有界面再绑这两个属性)

        Raise(propName);
        Raise(nameof(HasFilter));
        Raise(nameof(HasRules));
        Raise(nameof(Description));

        Persist();
        // 不在这里直接 Refresh: 交给定时器攒一会儿, 见构造函数里的说明
        _debounce.Stop();
        _debounce.Start();
    }

    private void Raise(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// 屏蔽谓词。**命中条件的返回 false(隐藏)**, 其余放行。
    /// 认 VideoItem 与 DynamicItem(综合动态卡) —— 其它类型一律放行,
    /// 免得在没想到的集合上把内容屏蔽没了。
    /// </summary>
    private bool Matches(object? item)
    {
        if (!HasFilter) return true;

        if (item is DynamicItem d)
        {
            // 转发动态: 转发者与被转发原作者都纳入屏蔽
            if (UpBlocked(d.AuthorMid, d.AuthorName)) return false;
            if (d.Orig != null && UpBlocked(d.Orig.AuthorMid, d.Orig.AuthorName)) return false;

            var title = d.VideoTitle?.Trim();
            if (string.IsNullOrEmpty(title)) title = d.Title?.Trim();
            if (!string.IsNullOrEmpty(title) && _titleSet.Contains(title)) return false;
            if (ContainsAny(title ?? "", _titleTerms)) return false;
            if (ContainsAny(d.AuthorName ?? "", _upTerms)) return false;
            return true;
        }

        if (item is not VideoItem v) return true;

        // 一键屏蔽: 先 UID(最稳), 再精确同名
        if (UpBlocked(v.OwnerMid, v.Author)) return false;

        var vtitle = v.Title?.Trim();
        if (!string.IsNullOrEmpty(vtitle) && _titleSet.Contains(vtitle)) return false;

        // 关键词: 包含匹配
        if (ContainsAny(vtitle ?? "", _titleTerms)) return false;
        if (ContainsAny(v.Author ?? "", _upTerms)) return false;
        return true;
    }

    private bool UpBlocked(long mid, string? author)
    {
        if (mid > 0 && _upMids.Contains(mid)) return true;
        var name = author?.Trim();
        return !string.IsNullOrEmpty(name) && _upNames.Contains(name);
    }

    private static bool ContainsAny(string text, string[] terms)
    {
        if (terms.Length == 0 || string.IsNullOrEmpty(text)) return false;
        // 用 IndexOf 而不是 Contains(string): 后者在旧目标框架上会做文化敏感比较,
        // 屏蔽词大小写不敏感才符合直觉(英文 UP 名尤其明显)
        foreach (var t in terms)
            if (text.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private void RefreshAll()
    {
        // 倒序遍历, 顺手把已经失效(页面/集合被回收)的视图摘掉
        for (var i = _viewOrder.Count - 1; i >= 0; i--)
        {
            if (!_viewOrder[i].TryGetTarget(out var view))
            {
                _viewOrder.RemoveAt(i);
                continue;
            }
            try { view.Refresh(); }
            catch { _viewOrder.RemoveAt(i); }
        }

        // 页面可能因为"筛掉一屏"而需要补内容。回调里不要抛到筛选流程上。
        try { Changed?.Invoke(); }
        catch { /* 页面自己的补内容失败, 不该让屏蔽失效 */ }
    }
}

public enum FilterRuleKind
{
    Up,
    Title,
    /// <summary>标题关键词(手打 / 标签墙), 与 <see cref="Title"/> 的"精确整条标题"不同</summary>
    TitleKeyword,
    /// <summary>UP 主关键词(包含匹配)</summary>
    UpKeyword
}

/// <summary>面板里的一条屏蔽规则(UP 主 / 标题 / 关键词)。值本身不可变, 删掉后重建即可。</summary>
public sealed class FilterRule
{
    public FilterRuleKind Kind { get; set; }
    public string Value { get; set; } = "";
    public long Mid { get; set; }
    public string Label { get; set; } = "";

    /// <summary>标签左侧的小徽标文字</summary>
    public string Badge => Kind switch
    {
        FilterRuleKind.Up => "UP",
        FilterRuleKind.Title => "标题",
        FilterRuleKind.TitleKeyword => "标题词",
        _ => "UP词",
    };
}
