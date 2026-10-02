using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.ViewModels;

/// <summary>
/// 历史页 ViewModel —— 只看 B 站账号的**云端历史**。
///
/// 本机历史(`history.json`)已整体移除: 观看记录本来就跟着账号走, 本机再存一份只会和云端对不上,
/// 还要额外维护"本机 / 云端"两套界面。注意播放时的**进度上报仍然保留**(`Svc.HistorySync`),
/// 那是往云端写的, 和本机存储是两码事。
///
/// 时间轴(今天 / 昨天 / 近一周 / 更早)也一并移除了, 列表直接按接口顺序(新的在前)铺开。
///
/// 这一版新增了**编辑模式**: 进入后可多选, 支持"删除所选"和"清空全部" ——
/// 两个动作都对应 B 站自己的接口(x/v2/history/delete 与 x/v2/history/clear), 不是本地假删。
/// </summary>
public class HistoryViewModel : VideoListSelectionViewModel
{
    private bool _loading;
    private bool _loadingMore;
    // 初值给 true: 界面用"!HasMore"显示"已经到底啦", 若默认 false, 首次加载和报错时
    // 都会在加载动画/错误提示上叠一块"已经到底啦"。只有成功拿到一页之后才由数据决定它。
    private bool _hasMore = true;
    private string _error = "";
    private int _pn;

    private bool _isEditMode;
    private bool _busy;
    private string _busyText = "";
    private string _searchKeyword = "";

    // ------------------------------------------------------------ 类别(视频 / 直播间)

    private ContentKind _kind = ContentKind.Video;

    /// <summary>
    /// 当前看的是视频历史还是**直播**历史(2026-10-03 用户要求"直播间浏览历史合并进观看记录,
    /// 同时也在顶部添加一个 tag, 可选择直播间和视频历史观看记录")。
    ///
    /// ★ "合并进观看记录"的落地方式: 两者是**同一页面的两个类别**(顶部 tag 切换), 而不是两个页面。
    ///   数据来自两个不同接口(视频走 /x/v2/history, 直播走 /x/web-interface/history/cursor?type=live,
    ///   见 ApiClient.GetLiveHistoryAsync 的说明), 所以切换时必须重新拉 —— 不能像普通过滤那样
    ///   只在已有数据里筛。
    /// </summary>
    public ContentKind Kind
    {
        get => _kind;
        set
        {
            if (!SetProperty(ref _kind, value)) return;
            OnPropertyChanged(nameof(IsVideoKind));
            OnPropertyChanged(nameof(IsLiveKind));
            OnPropertyChanged(nameof(EmptyText));
            OnPropertyChanged(nameof(SearchHintText));
            // 换类别 = 换一整套数据: 清掉旧类别的内容重新加载。
            // 编辑模式一并退出 —— 勾选状态属于"上一批记录", 留着会让"删除所选"删错东西。
            IsEditMode = false;
            // ★ 先让在途的那次加载作废(否则它回来会把旧类别的数据填进新类别的列表),
            //   再清一次 Loading/LoadingMore —— 防重入那道闸看到它们还立着就会直接返回。
            BumpGeneration();
            Loading = false;
            LoadingMore = false;
            _ = LoadAsync(reset: true);
        }
    }

    /// <summary>XAML 里两个 RadioButton 的绑定(IsChecked 只支持 bool, 所以给两个派生属性)</summary>
    public bool IsVideoKind
    {
        get => _kind == ContentKind.Video;
        set { if (value) Kind = ContentKind.Video; }
    }

    public bool IsLiveKind
    {
        get => _kind == ContentKind.Live;
        set { if (value) Kind = ContentKind.Live; }
    }

    /// <summary>空列表时的提示语(两类内容说法不同)</summary>
    public string EmptyText => _kind == ContentKind.Live ? "云端没有直播观看记录" : "云端没有观看记录";

    /// <summary>
    /// 加载代次。**每次"从头加载"(切类别 / 点刷新) +1**, 请求回来时代次对不上就整包丢弃。
    ///
    /// ★ 为什么必须有(2026-10-03 加类别切换时暴露出来的):
    ///   ① `LoadAsync` 开头有防重入(`if (Loading || LoadingMore) return;`)。切换类别时若上一类
    ///      还在飞, 新的那次会**直接返回** —— 列表里于是留着上一类的内容, 而顶部的 tag 已经
    ///      切过去了(用户看到"选了直播间, 列出来的却是视频");
    ///   ② 就算不返回, `await` 之后那段过滤/分页用的是 `_kind` 的**当前值**。类别在请求途中被改掉时,
    ///      就会拿新类别去过滤旧类别的响应。
    ///   用代次把"这一次加载属于哪一代"钉住, 上面两种错配都消失。
    /// </summary>
    private int _generation;

    /// <summary>把当前代次 +1, 让在途的加载结果作废</summary>
    private void BumpGeneration() => _generation++;

    /// <summary>
    /// 搜索输入的防抖定时器(220ms) —— **和 FilterService 里屏蔽词那套是同一个坑、同一个参数**。
    ///
    /// 为什么必须有: 输入框是 UpdateSourceTrigger=PropertyChanged, 每敲一个字符都会走到
    /// ApplySearch → CollectionView.Refresh(); 而 Refresh 会让卡片墙的容器**整墙重建**
    /// (本页是无虚拟化的 WrapPanel 卡片墙, 每张 VideoCard 还带封面加载与 360ms 入场动画)。
    /// 历史一次能滚出几百条, 键盘按住不放时每敲一下重建一遍 ⇒ 明显卡顿。
    /// 攒 220ms 只刷一次, 手感上依然是"边打边变"; 清空(空词)不走防抖, 见 ScheduleSearch。
    /// (全局屏蔽词那边早就有这个防抖 —— 见 FilterService 构造函数, 本页自己的搜索当时漏了。)
    /// </summary>
    private readonly DispatcherTimer _searchDebounce;

    /// <summary>云端历史条目(接口顺序, 新的在前)。分页追加。</summary>
    public ObservableCollection<VideoItem> Entries { get; } = new();

    public bool Loading { get => _loading; private set => SetProperty(ref _loading, value); }
    public bool LoadingMore { get => _loadingMore; private set => SetProperty(ref _loadingMore, value); }
    public bool HasMore { get => _hasMore; private set => SetProperty(ref _hasMore, value); }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }

    /// <summary>是否处于编辑模式(每行出现复选框, 顶部换成操作条)</summary>
    public bool IsEditMode
    {
        get => _isEditMode;
        private set
        {
            if (!SetProperty(ref _isEditMode, value)) return;
            // 退出编辑模式时把选择清掉, 免得下次进来还留着上次的勾
            if (!value) ClearSelection();
        }
    }

    /// <summary>正在执行删除/清空: 期间禁用操作按钮, 避免重复点击把同一批删两遍</summary>
    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value)) OnPropertyChanged(nameof(CanOperate));
        }
    }

    /// <summary>操作进度文案(如"正在删除 3/10…")</summary>
    public string BusyText { get => _busyText; private set => SetProperty(ref _busyText, value); }

    // ------------------------------------------------------------ 搜索

    /// <summary>
    /// 按标题 / UP 主在当前已加载的云端历史里筛选。
    ///
    /// 注意它和"全局屏蔽"是**两回事**, 两者都要生效:
    ///   - 全局屏蔽是黑名单(命中就隐藏), 装在所有列表上, 入口在首页右上角;
    ///   - 这里是本页的搜索(命中才显示)。
    /// 一个 CollectionView 只能有一个 Filter 谓词, 所以这里**不**自己去设 `view.Filter`,
    /// 而是通过 `FilterService.SetScopeFilter` 把条件交给它, 由它合并成一个谓词。
    /// 直接设会把全局屏蔽顶掉(反过来也一样)。
    ///
    /// 只筛已加载的内容 —— 云端历史是分页拉的, 没拉到的页面自然搜不到,
    /// 所以界面上会明确写出"在已加载的 N 条里搜索"。
    /// </summary>
    public string SearchKeyword
    {
        get => _searchKeyword;
        set
        {
            if (!SetProperty(ref _searchKeyword, value ?? "")) return;
            OnPropertyChanged(nameof(HasSearch));
            OnPropertyChanged(nameof(SearchHintText));
            ScheduleSearch();
        }
    }

    public bool HasSearch => _searchKeyword.Trim().Length > 0;

    public string SearchHintText => HasSearch
        ? $"在已加载的 {Entries.Count} 条记录里匹配「{_searchKeyword.Trim()}」"
        : "只搜索已加载的记录";

    /// <summary>搜索把内容全筛掉了 —— 和"云端本来就没记录"要区分开, 提示语不一样</summary>
    public bool ShowNoMatch => HasSearch && TotalCount == 0;

    /// <summary>
    /// 排一次过滤。**空词立刻生效** —— 点「清空」/按 Esc/把最后一个字符删掉时, 用户期待的是
    /// 马上看到全部内容(与 FilterService.Clear 的取舍一致); 非空词走 220ms 防抖, 理由见
    /// <see cref="_searchDebounce"/>。顺手把已排期的那一次取消掉, 免得防抖到点又白刷一遍。
    /// </summary>
    private void ScheduleSearch()
    {
        _searchDebounce.Stop();
        if (_searchKeyword.Trim().Length == 0)
        {
            ApplySearch();
            return;
        }
        _searchDebounce.Start();
    }

    /// <summary>
    /// 搜索词变化时重挂本页自己的过滤条件(走 FilterService, 别直接赋 view.Filter ——
    /// 一个视图只能有一个谓词, 直接赋值会把全局屏蔽顶掉)。
    /// </summary>
    private void ApplySearch()
    {
        var kw = _searchKeyword.Trim();
        FilterService.Instance.SetScopeFilter(Entries,
            kw.Length == 0 ? null : item => MatchKeyword(item, kw));
        RaiseSelection();
        OnPropertyChanged(nameof(ShowNoMatch));
    }

    private static bool MatchKeyword(object? item, string keyword)
    {
        if (item is not VideoItem v) return true;
        return v.Title.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0
            || v.Author.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public void ClearSearch() => SearchKeyword = "";

    // ------------------------------------------------------------ 选择状态
    // 选中态派生属性(SelectedCount / TotalCount / HasSelection / AllSelected /
    // SelectedText / SelectAllText)与批量改选、集合增删时挂退 PropertyChanged 的逻辑
    // 全部来自基类 VideoListSelectionViewModel, 这里只留历史页自己特有的部分。
    //
    // 注: 原来这里还有一个 VisibleCount, 与 TotalCount 是**同一个表达式**的重复定义
    // (都等于 VisibleItems().Count()), 而 XAML 只绑 TotalCount —— 已合并掉。

    protected override ObservableCollection<VideoItem> SelectionSource => Entries;

    /// <summary>历史页特有的绑定: 搜索提示语与"搜不到"空态</summary>
    protected override void RaiseSelectionExtra()
    {
        OnPropertyChanged(nameof(ShowNoMatch));
        OnPropertyChanged(nameof(SearchHintText));
    }

    // ------------------------------------------------------------ 加载

    /// <summary>删除按钮是否可用</summary>
    public bool CanOperate => !Busy;

    public ICommand ReloadCommand { get; }
    public ICommand ToggleEditCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand DeleteSelectedCommand { get; }
    public ICommand ClearAllCommand { get; }

    public HistoryViewModel()
    {
        ReloadCommand = new RelayCommand(() => _ = LoadAsync(reset: true));

        ToggleEditCommand = new RelayCommand(() =>
        {
            if (Busy) return;
            IsEditMode = !IsEditMode;
        });

        // 全选 / 取消全选做成同一个按钮: 已经是全选状态时点它就是取消。
        // 只作用于**当前可见项** —— 被屏蔽掉的记录用户看不见, 不该被"全选"带走。
        SelectAllCommand = new RelayCommand(() =>
        {
            if (Busy) return;
            SetSelectionBulk(VisibleItems(), !AllSelected);
        });

        DeleteSelectedCommand = new AsyncRelayCommand(DeleteSelectedAsync);
        ClearAllCommand = new AsyncRelayCommand(ClearAllAsync);

        // 条数变化要同步到界面上的计数与"全选"按钮文案。
        // 只能监听**增删**, IsSelected 的变化得逐条监听 —— 集合本身不会因此收到通知。
        HookSelectionSource();

        // 搜索防抖: 参数(220ms / Background 优先级)与 FilterService 里屏蔽词那套完全一致 ——
        // 两处做的是同一件事(改过滤条件 → 整墙重建), 手感和代价都该一样。
        _searchDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            ApplySearch();
        };
    }

    // ------------------------------------------------------------ 加载

    /// <summary>加载历史(首页或翻页)。视频 / 直播走两个不同接口, 由 <see cref="Kind"/> 分流。</summary>
    public async Task LoadAsync(bool reset)
    {
        if (!Svc.Session.HasLogin)
        {
            Error = "云端历史需要登录后查看";
            return;
        }
        // 防重入: 滚动到接近底部的事件会连续来好几次, 不挡住就会并发发同一页
        if (Loading || LoadingMore) return;

        if (reset)
        {
            _pn = 1;
            Entries.Clear();
            Error = "";
            Loading = true;
            IsEditMode = false;
            // 直播历史是**游标式**接口: 重新从头拉之前必须把游标清掉, 否则第 2 页会接着上次的位置
            if (_kind == ContentKind.Live) Svc.Api.ResetLiveHistoryCursor();
            // "从头加载"开启新的一代(见 _generation)。翻页不换代 —— 它属于同一批数据。
            BumpGeneration();
        }
        else
        {
            _pn++;
            LoadingMore = true;
        }

        // ★ 把这一代用到的类别**钉在局部变量**里: await 之后不能再看 _kind ——
        //   请求在途时用户可能已经切到另一类了, 那样就会拿新类别去过滤旧响应(见 _generation)。
        var gen = _generation;
        var kind = _kind;
        var page = _pn;

        var (ok, err, items) = kind == ContentKind.Live
            ? await Svc.Api.GetLiveHistoryAsync(page)
            : await Svc.Api.GetCloudHistoryAsync(page);

        // 这一代已经被更新的一次加载取代了: 整包丢弃(别动 Loading —— 那是新那次的状态)
        if (gen != _generation) return;

        Loading = false;
        LoadingMore = false;

        if (ok && items != null)
        {
            // 视频按 bvid 过滤(没有 bvid 的条目播不了); 直播没有 bvid, 按房间号过滤。
            foreach (var it in items.Where(x => kind == ContentKind.Live
                                                    ? x.RoomId > 0
                                                    : !string.IsNullOrEmpty(x.Bvid)))
                Entries.Add(it);
            // 视频一页 30 条、直播一页 20 条(见各自接口的 ps); 不满一页说明到底了。
            // ★ 直播还有个额外信号: 游标归零时接口会返回空列表, 上面的 items.Count==0 就涵盖了。
            var pageSize = kind == ContentKind.Live ? 20 : 30;
            HasMore = items.Count >= pageSize;
            if (Entries.Count == 0) Error = EmptyText;
        }
        else
        {
            Error = err ?? "加载失败";
            // 失败时不改 HasMore: 错误提示已经占屏, 再叠一个"已经到底啦"只会更乱。
            // 下次成功的加载会重新决定它。
        }
        RaiseSelection();
    }

    // ------------------------------------------------------------ 删除 / 清空

    /// <summary>
    /// 删除选中的记录。
    ///
    /// 接口是**单条**删除(只认一个 kid), 所以这里逐条调 —— 好在用户手选的条数不会太多。
    /// 每条成功才从列表里移除: 某一条失败时不要把它一起抹掉, 否则界面上看起来删掉了,
    /// 下次同步又冒出来。
    /// </summary>
    private async Task DeleteSelectedAsync()
    {
        if (Busy) return;
        var targets = VisibleItems().Where(x => x.IsSelected).ToList();
        if (targets.Count == 0)
        {
            Svc.Toast.Show("还没有选择要删除的记录");
            return;
        }

        var r = MessageBox.Show($"确定要删除选中的 {targets.Count} 条观看记录吗?", "BiliDesk",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;

        Busy = true;
        var done = 0;
        var fail = 0;
        try
        {
            foreach (var it in targets)
            {
                // 两类内容的目标 id 不同: 视频用 avid, 直播用房间号。
                // kid 前缀也跟着变(archive_/live_), 见 ApiClient.DeleteHistoryAsync。
                // ★ 用**条目自己**的类型而不是当前 _kind: targets 是进循环前就抓下来的快照,
                //   删除途中用户切了类别也不会删错类型(切类别时列表已清空, 这里是纯防御)。
                var itemKind = it.IsLive ? ContentKind.Live : ContentKind.Video;
                var targetId = it.IsLive ? it.RoomId : it.Aid;
                if (targetId <= 0)
                {
                    // 缺 id 时列表接口给不出 kid, 只能跳过 —— 说清楚是哪一条, 不要静默略过
                    fail++;
                    continue;
                }
                BusyText = $"正在删除 {done + fail + 1}/{targets.Count}…";
                var (ok, _) = await Svc.Api.DeleteHistoryAsync(targetId, itemKind);
                if (ok) { Entries.Remove(it); done++; }
                else fail++;
            }
        }
        finally
        {
            Busy = false;
            BusyText = "";
        }

        if (fail == 0) Svc.Toast.Show($"已删除 {done} 条记录");
        else if (done == 0) Svc.Toast.Show("删除失败, 请稍后重试");
        else Svc.Toast.Show($"已删除 {done} 条, {fail} 条失败");

        // 删完如果列表空了, 给个空状态提示, 免得留一片空白
        if (Entries.Count == 0) Error = EmptyText;
    }

    /// <summary>
    /// 清空全部云端历史。走的是 B 站自己的"清空历史记录"接口, 一次请求删干净,
    /// 不需要先拉全部条目再逐条删。
    /// </summary>
    private async Task ClearAllAsync()
    {
        if (Busy) return;
        if (Entries.Count == 0 && !HasMore)
        {
            Svc.Toast.Show("历史已经是空的");
            return;
        }

        // 双重确认: 这个动作在服务端不可撤销。
        // ★ 说明必须写"全部"而不是"当前这一类": /x/v2/history/clear 是**账号级**的清空接口,
        //   没有按类型分的参数, 所以它会连视频带直播一起清掉。含糊其辞会让用户在直播类别下
        //   以为只清直播记录 —— 那是数据损失。
        var r = MessageBox.Show(
            "确定要清空**全部**云端观看历史吗?\n\n这会删除账号下所有观看记录(视频与直播都包含, 不只是当前已加载的), 且无法撤销。",
            "BiliDesk", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;

        Busy = true;
        BusyText = "正在清空…";
        (bool ok, string? err) res;
        try
        {
            res = await Svc.Api.ClearHistoryAsync();
        }
        finally
        {
            Busy = false;
            BusyText = "";
        }

        if (!res.ok)
        {
            Svc.Toast.Show("清空失败: " + (res.err ?? "未知错误"));
            return;
        }

        Entries.Clear();
        HasMore = false;
        IsEditMode = false;
        Error = EmptyText;
        Svc.Toast.Show("已清空云端历史");
    }

    /// <summary>播放(页面代码后置调用)</summary>
    public void Play(VideoItem item)
    {
        if (item == null) return;
        // 直播记录走直播那条路(没有 bvid, 走视频路径会直接失败)
        if (item.IsLive)
        {
            Svc.Player.PlayLive(item.RoomId, item.Title);
            return;
        }
        if (string.IsNullOrEmpty(item.Bvid)) return;
        Svc.Player.PlayVideo(item.Bvid, item.Title);
    }
}
