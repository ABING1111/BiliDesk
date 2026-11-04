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

    /// <summary>面板里展示的规则(只含一键屏蔽的 UP / 标题; 关键词在输入框里有回显, 不重复列)</summary>
    public ObservableCollection<FilterRule> Rules { get; } = new();

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
            if (_titleTerms.Length > 0) parts.Add($"标题含「{string.Join(" / ", _titleTerms)}」");
            if (_upTerms.Length > 0) parts.Add($"UP 含「{string.Join(" / ", _upTerms)}」");
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

            default:
                return false;
        }

        if (!changed) return false;
        RebuildIndexes();
        AfterRulesChanged();
        return true;
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
        _titleTerms = SplitTerms(_title);
        _upTerms = SplitTerms(_up);

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
        terms = SplitTerms(v);

        Raise(propName);
        Raise(nameof(HasFilter));
        Raise(nameof(Description));

        Persist();
        // 不在这里直接 Refresh: 交给定时器攒一会儿, 见构造函数里的说明
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>
    /// 把输入拆成屏蔽词。空格 / 逗号 / 顿号 / 分号都当分隔符, 顺便去掉空白项。
    /// 支持多个词是为了真正好用 —— "鬼畜"和"剪辑"常常要一起屏蔽。
    /// </summary>
    private static string[] SplitTerms(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw.Split(new[] { ' ', ',', '，', '、', ';', '；', '\t' },
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private void Raise(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// 屏蔽谓词。**命中条件的返回 false(隐藏)**, 其余放行。
    /// 只认 VideoItem —— 其它类型一律放行, 免得在没想到的集合上把内容屏蔽没了。
    /// </summary>
    private bool Matches(object? item)
    {
        if (!HasFilter) return true;
        if (item is not VideoItem v) return true;

        // 一键屏蔽: 先 UID(最稳), 再精确同名
        if (v.OwnerMid > 0 && _upMids.Contains(v.OwnerMid)) return false;
        var author = v.Author?.Trim();
        if (!string.IsNullOrEmpty(author) && _upNames.Contains(author)) return false;

        var title = v.Title?.Trim();
        if (!string.IsNullOrEmpty(title) && _titleSet.Contains(title)) return false;

        // 关键词: 包含匹配
        if (ContainsAny(title ?? "", _titleTerms)) return false;
        if (ContainsAny(author ?? "", _upTerms)) return false;
        return true;
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
    Title
}

/// <summary>面板里的一条一键屏蔽规则(UP 主 / 标题)。值本身不可变, 删掉后重建即可。</summary>
public sealed class FilterRule
{
    public FilterRuleKind Kind { get; set; }
    public string Value { get; set; } = "";
    public long Mid { get; set; }
    public string Label { get; set; } = "";

    /// <summary>标签左侧的小徽标文字</summary>
    public string Badge => Kind == FilterRuleKind.Up ? "UP" : "标题";
}
