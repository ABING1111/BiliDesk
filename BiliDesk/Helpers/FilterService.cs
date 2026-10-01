using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using BiliDesk.Models;

namespace BiliDesk.Helpers;

/// <summary>
/// 全局列表**屏蔽**: 把标题或 UP 主里含指定词的视频从列表中隐藏掉。
///
/// 语义是"屏蔽"而不是"查找": 命中关键词的内容**不再显示**, 没命中的照常列出来。
/// 之前做成了"只显示包含关键词的内容", 那本质上是二次搜索 —— 用户想要的却是
/// "我不想再看到这类东西", 于是输入一个词之后满屏只剩这个词的内容, 完全是反的。
///
/// 实现方式 —— 不动各页面自己的 ObservableCollection, 而是给它们的**默认 CollectionView**
/// 装一个 Filter 谓词。WPF 的 ItemsControl / ListBox 绑定的就是这个默认视图, 所以
/// 一处设置, 所有绑到该集合的界面同时生效; 也不用给每个页面再维护一份"屏蔽后的副本集合"
/// (那样两边很容易不同步)。
///
/// 生命周期说明: 这里强引用着注册过的视图。注册方都是 MainWindow 长期缓存的页面级集合,
/// 生命周期等同于应用进程, 所以不会造成泄漏; 若将来有临时集合要注册, 需要改成弱引用。
/// </summary>
public sealed class FilterService : INotifyPropertyChanged
{
    public static FilterService Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 已纳管的视图 → 该视图自己的额外过滤条件(可以为 null)。
    ///
    /// 用字典而不是列表: 一个视图只能有一个 `Filter` 谓词, 所以"全局屏蔽"和"页面自己的搜索"
    /// 必须合并进同一个谓词里。这里给每个视图存一份它专属的条件, 谓词里同时问两边 ——
    /// 否则谁后设置谁就把对方顶掉(历史页加搜索会把全局屏蔽顶掉, 反之亦然)。
    /// </summary>
    private readonly Dictionary<ICollectionView, Func<object, bool>?> _views = new();

    private string _title = "";
    private string _up = "";

    /// <summary>拆好的屏蔽词。每次输入变化只算一次, 而不是在谓词里对**每个条目**反复 split。</summary>
    private string[] _titleTerms = Array.Empty<string>();
    private string[] _upTerms = Array.Empty<string>();

    private readonly DispatcherTimer _debounce;

    private FilterService()
    {
        // 输入防抖。
        //
        // 为什么必须有: 输入框是 UpdateSourceTrigger=PropertyChanged, 每敲一个字符就会
        // Refresh 一次**所有**注册过的视图(首页三个 tab + 收藏 + 历史 + 搜索)。每次 Refresh
        // 都要把容器全部重建一遍(VideoCard 还不轻, 带封面加载和入场动画), 键盘按住不放时
        // 表现就是明显卡顿。攒 220ms 再刷一次, 手感上依然是"边打边变"。
        _debounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            RefreshAll();
        };
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

    public bool HasFilter => _titleTerms.Length > 0 || _upTerms.Length > 0;

    /// <summary>给界面看的条件描述</summary>
    public string Description
    {
        get
        {
            if (!HasFilter) return "未屏蔽任何内容";
            var parts = new List<string>();
            if (_titleTerms.Length > 0) parts.Add($"标题含「{string.Join(" / ", _titleTerms)}」");
            if (_upTerms.Length > 0) parts.Add($"UP 含「{string.Join(" / ", _upTerms)}」");
            return "已屏蔽 " + string.Join("  ·  ", parts);
        }
    }

    /// <summary>
    /// 把某个集合的默认视图纳入屏蔽。页面初始化时调用一次即可(重复调用不会重复注册)。
    /// </summary>
    public void Attach(System.Collections.IEnumerable source)
    {
        if (source == null) return;
        try
        {
            var view = CollectionViewSource.GetDefaultView(source);
            if (view == null || _views.ContainsKey(view)) return;
            _views[view] = null;
            // 谓词同时问"全局屏蔽"和"这个视图自己的条件"。捕获 view 而不是把它传进 Matches,
            // 是为了让每个视图拿到的是自己那一份额外条件。
            view.Filter = item => Matches(item) && ScopeAllows(view, item);
            // 挂上来时立即按当前条件过滤一次, 否则新页面会先把被屏蔽的内容显示出来再消失
            if (HasFilter) view.Refresh();
        }
        catch
        {
            // 个别集合拿不到默认视图(比如已经绑到别的视图上), 忽略即可 —— 只是它不参与屏蔽
        }
    }

    /// <summary>
    /// 给某个集合挂一个"这个页面自己的"过滤条件(例如历史页的搜索框)。
    ///
    /// 必须走这里而不是页面自己去 `view.Filter = ...`: 一个视图只能有一个谓词,
    /// 直接赋值会把全局屏蔽顶掉。传 null 表示撤销。
    /// </summary>
    public void SetScopeFilter(System.Collections.IEnumerable source, Func<object, bool>? extra)
    {
        if (source == null) return;
        try
        {
            var view = CollectionViewSource.GetDefaultView(source);
            if (view == null) return;

            if (!_views.ContainsKey(view))
            {
                // 页面可能还没 Attach 过(构造顺序不定), 这里补挂一次, 免得条件被丢掉
                Attach(source);
                if (!_views.ContainsKey(view)) return;
            }
            _views[view] = extra;
            view.Refresh();
        }
        catch
        {
            // 视图失效时忽略
        }
    }

    private bool ScopeAllows(ICollectionView view, object? item)
    {
        if (item == null) return true;
        if (!_views.TryGetValue(view, out var extra) || extra == null) return true;
        try { return extra(item); }
        catch { return true; } // 页面自己的谓词抛了不该连累整个列表
    }

    /// <summary>清空条件</summary>
    public void Clear()
    {
        TitleKeyword = "";
        UpKeyword = "";
        // 清空要**立刻**生效, 不要等防抖: 用户点"清除"时期待的是马上看到全部内容。
        // 顺手把已经排上的那一次防抖取消掉, 免得 220ms 后又白刷一遍。
        _debounce.Stop();
        RefreshAll();
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
    /// 屏蔽谓词。**命中关键词的返回 false(隐藏)**, 其余放行。
    /// 只认 VideoItem —— 其它类型一律放行, 免得在没想到的集合上把内容屏蔽没了。
    /// </summary>
    private bool Matches(object? item)
    {
        if (!HasFilter) return true;
        if (item is not VideoItem v) return true;

        return !ContainsAny(v.Title, _titleTerms) && !ContainsAny(v.Author, _upTerms);
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
        // 倒序不可用(字典), 先取出键快照; Refresh 失败说明该视图已经失效(界面被回收), 顺手摘掉
        foreach (var view in _views.Keys.ToList())
        {
            try { view.Refresh(); }
            catch { _views.Remove(view); }
        }
    }
}
