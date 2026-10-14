using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.ViewModels;

/// <summary>
/// 「关注 / 粉丝」二级页 ViewModel。
///
/// 一个 VM 管两种模式(<see cref="RelationMode"/>), 因为两个列表的数据形状、交互、
/// 分页方式完全一样, 只有"请求哪个接口"和标题不同(理由见 <see cref="Models.RelationMode"/>):
///   · 关注 = <see cref="ApiClient.GetFollowingsAsync"/>(followings), 无分页上限;
///   · 粉丝 = <see cref="ApiClient.GetFansAsync"/>(fans), **最多 5 页**。
///
/// # ★ 这一版(1.2.7)修掉的六件事
///
/// ① **请求坐标化**。早先每条请求在"发出去"的那一刻才去读 <c>_mode</c>/<c>_selectedGroup</c>/<c>_page</c>,
///    而这些字段在 await 期间随时可能被人改掉 —— 于是"关注第二页"这个请求可能**真的按粉丝发出去**,
///    或者按旧分组发出去。现在每一轮请求都先把 (账号/模式/分组/页码/代号) 冻成
///    <see cref="ReqKey"/> 这个不可变值再发, 回来时逐项核对, 对不上就整份丢掉。
///    症状: "延迟关注的第二页 → 切到粉丝 → 最终粉丝列表里混进 30 条关注的人"。
///
/// ② **换代 + 取消**。切模式/切分组/换账号/刷新都会让旧请求**作废**(代号 +1)并顺手
///    把在飞的 HTTP 请求取消掉(见 <see cref="BeginGeneration"/>)。取消回来的响应一律
///    不写 Error —— 用户自己点的刷新, 不该收到一条红色的"请求失败"。
///
/// ③ **分组缓存归属账号**。<see cref="_groupsAccount"/> 记着当前 Groups 是谁的;
///    退出登录或换账号立刻连列表带分组一起清空。同一账号刷新可以保留选中项,
///    跨账号一律退回「全部」。
///
/// ④ **计数不再互相覆盖**。全量关注数、当前分组人数、已加载数、匹配数是**四个不同的量**,
///    以前只有 _total 一个字段, 于是三人组显示"3 / 652"。见 <see cref="CountText"/>。
///
/// ⑤ **搜索任务编号化**。早先换一次词就把 _autoLoadRunning 直接置 false, 旧任务还在跑,
///    却已经不再代表"正在搜索" —— 于是出现"任务早停了, 界面还在转圈"。
///    现在搜索有独立的 <see cref="_searchTaskId"/>, 旧任务不能替新任务收尾。
///
/// ⑥ **分页失败不再冒充到底**。以前翻页失败只把 HasMore 置 false, 于是"已经到底啦"
///    照常弹出来, 列表还停在第 1 页。现在 <see cref="_pageState"/> 把
///    空闲/加载中/失败/到底/达上限分开, 失败时列表尾部给"重试"。
/// </summary>
public class RelationViewModel : ObservableObject
{
    /// <summary>
    /// 粉丝列表的翻页上限(B 站服务端限制, 与 ps 无关)。
    ///
    /// ★ 为什么必须自己收手: 实测 pn=6 起服务端返回 code=63000「前方风控」,
    ///   而且**没有明确的分页结束标志**(不像关注接口那样一页不满就是到底)。
    ///   不设上限的话滚动加载会一直打到第 6 页, 每打一次就报一次风控错误。
    /// </summary>
    public const int MaxPage = 5;

    /// <summary>每页条数。与关注接口保持一致(FollowViewModel 也是 30)</summary>
    private const int PageSize = 30;

    /// <summary>一次搜索最多自动续翻几页</summary>
    private const int SearchBatchPages = 5;

    /// <summary>搜索防抖(毫秒)。中文输入法拼到一半不该打一次请求</summary>
    private const int SearchDebounceMs = 300;

    /// <summary>搜索批次内两页之间的最小间隔(毫秒), 别把风控打出来</summary>
    private const int SearchPageGapMs = 300;

    /// <summary>卡片最小宽度(逻辑像素)。列数按它 + 缝隙算, 而不是按窗口档位</summary>
    private const double CardMinWidth = 240;

    /// <summary>卡片之间/卡片与下一行之间的缝隙</summary>
    private const double CardGap = 12;

    /// <summary>最多几列</summary>
    private const int MaxColumns = 4;

    // ============================================================ 状态字段

    private RelationMode _mode = RelationMode.Following;
    private bool _loading;
    private bool _loadingMore;
    private bool _hasMore;
    private string _error = "";

    /// <summary>当前账号; 0 = 未知(未登录 / 还没拉到 mid)</summary>
    private long _account;

    /// <summary>已成功提交的页码。**只在结果真的落地之后**才前进, 失败不前进 → 重试是同一页</summary>
    private int _page = 1;

    /// <summary>
    /// 左栏当前这份 <see cref="Groups"/> 是**谁**的分组。
    ///
    /// ★ 这就是"退出登录后左栏还留着上一个账号的分组"的解药: 分组是账号私有数据,
    ///   tagid 还是小整数, 新账号很可能**恰好也有 3 号分组** —— 于是选中项看着还在,
    ///   请求也成功, 列出来的却是另一组人, 界面上没有任何地方能看出不对。
    ///   记下归属, 跨账号就整体丢掉重来。
    /// </summary>
    private long _groupsAccount;

    // ---- 计数: 四个独立的量, 不许互相覆盖(见类注释 ④)

    /// <summary>「全部」时的全量关注数。-1 = 没拿到精确值</summary>
    private long _totalAll = -1;

    /// <summary>当前分组的人数(接口给)。-1 = 没拿到</summary>
    private int _groupTotal = -1;

    /// <summary>已加载(已列出来)的条数 —— 直接就是 <c>Users.Count</c>, 不另存</summary>
    private int _mutualCount;

    // ---- 分页状态机

    private enum PageState
    {
        /// <summary>还能继续翻</summary>
        Idle,
        /// <summary>正在翻</summary>
        Loading,
        /// <summary>翻页失败, 保留列表等重试(见 <see cref="RetryPageAsync"/>)</summary>
        Failed,
        /// <summary>真的到底了(服务端给空页, 或整页都重)</summary>
        Ended,
        /// <summary>被人为上限截断(粉丝 5 页)</summary>
        Capped,
    }

    private PageState _pageState = PageState.Idle;

    /// <summary>失败的那一页(重试时就用它, 绝不跳页)</summary>
    private int _failedPage = 1;

    // ---- 搜索

    private string _search = "";
    private DispatcherTimer? _searchDebounce;

    /// <summary>
    /// 当前搜索任务的编号。换词/切组会让它 +1;
    /// 旧任务收尾时发现编号对不上就**什么都不做** —— 不能替新任务清标志。
    /// </summary>
    private int _searchTaskId;

    /// <summary>本轮还允许自动续翻几页</summary>
    private int _searchBudget;

    /// <summary>本轮已经翻过的页数(用于"只在已加载的前 N 位里搜过"这句话)</summary>
    private int _searchScannedPages;

    /// <summary>搜索任务是否正在跑</summary>
    private bool _searchRunning;

    /// <summary>
    /// 输入法正在拼词(拼音/五笔/日文假名)。
    /// ★ 中文用户每次打字都会经过这一段, 在这期间发请求等于按"每个字母"搜一次。
    ///   由页面在 PreviewTextInput / TextInputUpdate 上调 (见 RelationPage.xaml.cs)。
    /// </summary>
    private bool _imeComposing;

    /// <summary>批量填充中(见 <see cref="_bulkFilling"/>)</summary>
    private bool _bulkFilling;

    // ---- 卡片墙: 行虚拟化

    private double _wallWidth;
    private int _columns = 3;
    private ObservableCollection<RelationUserRow> _rows = new();

    /// <summary>整个列表区的可用宽度(代码里由 <c>SizeChanged</c> 推进来)。0 = 还没量过</summary>
    public double WallWidth
    {
        get => _wallWidth;
        set
        {
            if (Math.Abs(_wallWidth - value) < 0.5) return;
            _wallWidth = value;
            RecalcColumns();
        }
    }

    /// <summary>
    /// 按**实际内容宽度**算列数, 最低卡宽 <see cref="CardMinWidth"/>, 最多 <see cref="MaxColumns"/> 列。
    ///
    /// ★ 为什么不用 helpers:Responsive 的窗口档位: 档位按**主窗口**宽度算, 而这一页左边还有
    ///   一列 164 的分组栏、四周还有 20+36 的边距 —— 同一个档位下"有左栏"和"没左栏"(粉丝模式)
    ///   的卡片宽度能差 200, 硬套档位就会出现"右边空一大块"或者"卡片挤成两列"。
    ///   直接拿容器实宽来除, 差多少是差多少。
    /// </summary>
    private void RecalcColumns()
    {
        var n = (int)Math.Floor((_wallWidth + CardGap) / (CardMinWidth + CardGap));
        n = Math.Clamp(n, 1, MaxColumns);
        if (n == _columns) return;
        _columns = n;
        OnPropertyChanged(nameof(Columns));
        RebuildRows(replace: true);
    }

    /// <summary>当前列数(给行模板里的 UniformGrid 用)</summary>
    public int Columns => _columns;

    /// <summary>
    /// 分好行的卡片墙。外层 <c>VirtualizingStackPanel</c> 回收的是**行**,
    /// 一行销毁时里面的 N 张卡一起没了 ⇒ 控件数量只跟视口高度走, 不跟总条数走。
    /// </summary>
    public ObservableCollection<RelationUserRow> Rows => _rows;

    /// <summary>
    /// 卡片墙该不该显示。空列表 / 未登录 / 出错 / 搜索没结果时都该让位给对应的整列说明,
    /// 所以显隐跟 <see cref="Rows"/> 的条数联动 —— XAML 里没法直接写
    /// <c>Rows.Count &gt; 0</c>(没有那个转换器), 在这里给一个。
    /// </summary>
    public bool ShowCardWall => _rows.Count > 0 && Error.Length == 0;

    /// <summary>
    /// 重建行集合。
    /// ★ <paramref name="replace"/> 区分两种时机, 不是为了性能而是为了**滚动位置**:
    ///   · true(换模式/换搜索/换列数): 整墙换血, 换集合实例最干净;
    ///   · false(翻页追加): 往尾部 Add, 保住用户刚滚到的位置 ——
    ///     换成新集合会把所有容器重建, 视口回弹到顶部, 表现为"补一页之后列表跳回开头"。
    /// </summary>
    private void RebuildRows(bool replace)
    {
        var src = VisibleUsers;
        var cols = Math.Max(1, _columns);
        var fresh = new List<RelationUserRow>((src.Count + cols - 1) / cols);
        for (var i = 0; i < src.Count; i += cols)
            fresh.Add(new RelationUserRow(src.Skip(i).Take(cols).ToList(), cols));

        if (!replace)
        {
            // 追加: 只把原来没有的尾部行补上(列数没变时, 已有行的内容不该被重写)
            var hadRows = _rows.Count;
            for (var i = hadRows; i < fresh.Count; i++) _rows.Add(fresh[i]);
            if (fresh.Count != hadRows) OnPropertyChanged(nameof(ShowCardWall));
            // 末行在追加后可能变满 —— 只有这时才重写最后一行
            if (hadRows > 0 && src.Count % cols != 0 && src.Count > hadRows * cols)
                _rows[hadRows - 1] = fresh[hadRows - 1];
            return;
        }

        _rows = new ObservableCollection<RelationUserRow>(fresh);
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(ShowCardWall));
    }

    // ============================================================ 代与取消

    /// <summary>
    /// 一轮请求的**全部坐标**。不可变 —— 发出去之后就算界面怎么变, 它也不会改。
    /// </summary>
    private readonly record struct ReqKey(long Account, RelationMode Mode, int TagId, int Page, int Gen);

    private int _generation;
    private CancellationTokenSource? _cts;

    /// <summary>开始新一代: 旧请求一律作废并被取消, 新请求从这一代开始</summary>
    private void BeginGeneration()
    {
        _generation++;
        var old = _cts;
        _cts = new CancellationTokenSource();
        try { old?.Cancel(); } catch (ObjectDisposedException) { /* 上一代已经自己清掉了 */ }
        old?.Dispose();
    }

    /// <summary>这一轮还算不算数</summary>
    private bool IsLive(ReqKey k)
        => k.Gen == _generation && k.Account == _account
           && k.Mode == _mode && k.TagId == CurrentTagId;

    /// <summary>
    /// 提交前最后一道核对: 除了代号, 还要求账号/模式/分组确实没变。
    /// ★ 为什么代号不够: 代号只在"换代"时前进, 而有些路径(例如同账号刷新分组)会
    ///   在**不换代**的前提下改掉 SelectedGroup。两道都查才万无一失。
    /// </summary>
    private bool CanCommit(ReqKey k) => IsLive(k) && !_cts!.IsCancellationRequested;

    /// <summary>「全部」的 tagid 是 -1(见 <see cref="FollowGroup.AllTagId"/>)。请求前取值一次</summary>
    private int CurrentTagId => _mode == RelationMode.Fans || _selectedGroup == null
        ? FollowGroup.AllTagId : _selectedGroup.TagId;

    // ============================================================ 列表与过滤

    private readonly ObservableCollection<FollowUser> _users = new();
    private ObservableCollection<FollowUser> _visible = new();

    /// <summary>
    /// 全量数据源(当前模式 + 当前分组下已经拉到的全部条目)。
    /// ★ 卡片墙绑的是 <see cref="Rows"/>(按列数切好的行), 这里只给过滤/搜索用。
    /// </summary>
    public ObservableCollection<FollowUser> Users => _users;

    /// <summary>过滤后的结果。搜索词为空时就是 <see cref="Users"/> 本身, 零额外开销</summary>
    public ObservableCollection<FollowUser> VisibleUsers
    {
        get => _visible;
        private set
        {
            _visible = value;
            OnPropertyChanged(nameof(VisibleUsers));
        }
    }

    /// <summary>
    /// 整墙换血/批量填充期间静音逐条重算。
    /// ★ 652 个人逐条 Add 会触发 652 次 CollectionChanged, 每次都重跑一次过滤 + 刷新
    ///   三个计算属性 ⇒ 几万次 PropertyChanged。关掉它, 末尾统一算一次。
    /// </summary>
    private void Bulk(Action body)
    {
        _bulkFilling = true;
        try { body(); }
        finally { _bulkFilling = false; }
        RecountMutual();
        ApplyFilter(replaceRows: true);
    }

    /// <summary>
    /// 过滤 + 重排行 + 刷新那些算出来的属性。
    /// **所有会改 <see cref="Users"/> 的路径最终都要过这里**, 漏一处就会出现
    /// "删了一个人, 搜索框里还搜得到他"这种幽灵条目。
    /// </summary>
    /// <param name="replaceRows">
    /// 行集合是**整墙换血**还是**尾部追加**。
    /// ★ 这个参数曾经写成 "rebuildRows" 并且在 false 时**完全不重建行** ——
    ///   于是"逐条 Add"(Users.CollectionChanged 钩子走这条路)永远不建行,
    ///   卡片墙空着而 VisibleUsers 里明明有 652 条。探针直接测出 "行数:0, 卡片数:0"。
    ///   语义正确的做法: false ⇒ RebuildRows(replace: false), 也就是**追加**而不是"不重建"。
    ///   · true  —— 换模式/换搜索/换列数/整墙换血: 换集合实例, 干净;
    ///   · false —— 翻页/逐条 Add: 保住滚动位置(换集合会把容器全重建, 视口跳回顶部)。
    /// </param>
    private void ApplyFilter(bool replaceRows = true)
    {
        var q = _search.Trim();
        VisibleUsers = q.Length == 0
            ? _users
            : new ObservableCollection<FollowUser>(_users.Where(u => Matches(u, q)).ToList());

        RebuildRows(replace: replaceRows);

        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(ShowSearchEmpty));
        OnPropertyChanged(nameof(IsSearchRunning));
        OnPropertyChanged(nameof(ShowSearchContinue));
        OnPropertyChanged(nameof(SearchEmptyText));
        OnPropertyChanged(nameof(SearchScopeText));
        OnPropertyChanged(nameof(SubtitleText));
        NotifyListVisibility();
        NotifyPaging();
    }

    /// <summary>
    /// 命中判定: 昵称 / 用户 mid / 关注签名。
    ///
    /// ★ 为什么加 mid: 用户贴出来的往往是 mid 或 QQ 群号, 只搜昵称会一个都搜不到,
    ///   而"我没关注这个人?"的答案是"你关注了, 搜 mid"。
    ///   mid 用**前缀**匹配而不是"包含": mid 是等宽数字, 包含匹配会把 1234 命中到 91234 上,
    ///   而 1234 的前缀匹配才是用户真正想输的那串。
    /// </summary>
    private static bool Matches(FollowUser u, string q)
    {
        if (u.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if (u.Sign != null && u.Sign.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if (long.TryParse(q, out var mid) && mid > 0)
            return u.Mid.ToString().StartsWith(q, StringComparison.Ordinal);
        return false;
    }

    // ============================================================ 搜索词

    /// <summary>
    /// 搜索关键字。绑到输入框的 Text。
    ///
    /// ★ 这里**不做即时过滤**: 早先一改就过滤, 中文用户拼一个词要经过十几次 TextChanged,
    ///   每次都重跑一遍全量 Where —— 652 人时是一万多次字符串比较, 而且搜索任务会被
    ///   反复重启(见类注释 ⑤)。现在改成: 先走 <see cref="_searchDebounce"/> 等 300ms,
    ///   输入法还在拼词就继续等, 安静下来才真的开始搜。
    /// </summary>
    public string SearchText
    {
        get => _search;
        set
        {
            if (!SetProperty(ref _search, value)) return;
            ScheduleSearch();
        }
    }

    /// <summary>输入法正在拼词。由页面的 PreviewTextInput 置 true、TextInputUpdate 置 false</summary>
    public bool IsImeComposing => _imeComposing;

    /// <summary>开始拼词: 组合期间**一律不启动搜索**(见 <see cref="ScheduleSearch"/>)</summary>
    public void BeginImeComposition() => _imeComposing = true;

    /// <summary>拼完一个词: 这时候才重新开始 300ms 计时</summary>
    public void EndImeComposition()
    {
        if (!_imeComposing) return;
        _imeComposing = false;
        ScheduleSearch();
    }

    /// <summary>
    /// 用户按 Esc 取消了这次拼词。
    ///
    /// ★ 为什么必须显式解开: 输入法被 Esc 取消时**不会**再抛"拼完了"事件,
    ///   所以 <see cref="_imeComposing"/> 会永远卡在 true —— 而搜索被它挡住,
    ///   于是"取消一次拼音输入"会让**之后所有搜索都搜不动**。
    ///   这种"状态只由某个事件收尾、而那个事件不一定来"的坑, 记在这里免得再犯。
    /// </summary>
    public void CancelImeComposition() => _imeComposing = false;

    /// <summary>
    /// 防抖计时器。300ms 是"人打字"的自然停顿 —— 短于它会在打字过程中连续发请求,
    /// 长于它会让人觉得"搜不准"。输入法组合期间 <see cref="IsImeComposing"/> 直接按暂停。
    /// </summary>
    private void ScheduleSearch()
    {
        _searchDebounce ??= new DispatcherTimer(DispatcherPriority.Normal,
            Application_Dispatcher())
        {
            Interval = TimeSpan.FromMilliseconds(SearchDebounceMs),
        };
        _searchDebounce.Tick -= OnSearchDebounceTick;
        _searchDebounce.Tick += OnSearchDebounceTick;
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private static System.Windows.Threading.Dispatcher Application_Dispatcher()
        => System.Windows.Application.Current?.Dispatcher
           ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;

    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounce!.Stop();
        if (_imeComposing) return;      // 还在拼, 等 TextInputUpdate 再来一次
        BeginSearchRound();
    }

    /// <summary>开新一轮搜索: 重置额度与已翻页数, 立刻过滤当前已加载的, 再自动续翻一批</summary>
    private void BeginSearchRound()
    {
        _searchTaskId++;
        _searchBudget = SearchBatchPages;
        _searchScannedPages = 0;
        ApplyFilter(replaceRows: true);
        if (!IsSearching) return;
        _ = RunSearchBatchAsync(_searchTaskId);
    }

    /// <summary>搜索状态是否在跑</summary>
    public bool IsSearchRunning => _searchRunning;

    /// <summary>
    /// 还能不能"继续搜索": 本批翻完了、还有下一页、而且用户还没把这批额度用完。
    /// ★ 只在"还有下一页"时才给按钮 —— 否则按钮点了也换不来任何东西。
    /// </summary>
    /// ★ 2026-10-13 修: 条件里原来带着 `_searchBudget > 0`, 于是**批次一跑完按钮就消失** ——
    ///   而"这一批翻完了"正是最该让用户决定"要不要再翻一批"的时刻。探针抓到:
    ///   "任务停下后提供『继续搜索』" 失败, HasMore=True 而按钮不显示。
    ///   现在只判"还有下一页", 额度由 <see cref="ContinueSearchAsync"/> 在点击时重新给满。
    public bool ShowSearchContinue => IsSearching && !_searchRunning
                                      && HasMore && !Loading;

    /// <summary>
    /// 搜索范围说明。搜不完时必须说清"只在已加载的这批里搜过", 否则用户会以为
    /// 整个关注列表里都没有这个人(而其实可能在第 19 页)。
    /// </summary>
    public string SearchScopeText
    {
        get
        {
            if (!IsSearching) return "";
            var loaded = _users.Count;
            if (HasMore && _searchBudget > 0)
                return $"已搜过前 {_searchScannedPages} 页 · 已加载 {loaded} 位, 还可能有更多";
            if (HasMore)
                return $"已搜过前 {_searchScannedPages} 页 · 共 {loaded} 位, 点「继续搜索」再翻一批";
            return $"已搜完整份列表 · 共 {loaded} 位";
        }
    }

    /// <summary>点「继续搜索」: 再给一批额度接着翻</summary>
    public ICommand ContinueSearchCommand { get; }

    private async Task ContinueSearchAsync()
    {
        if (!IsSearching || _searchRunning || Loading) return;
        _searchBudget = SearchBatchPages;
        await RunSearchBatchAsync(_searchTaskId);
    }

    /// <summary>
    /// 自动续翻一批(最多 <see cref="SearchBatchPages"/> 页, 页间至少 300ms)。
    ///
    /// ★ 三个曾经出错的点都在这里:
    ///   ① <b>不再以"一个都没匹配"为继续条件</b> —— 早先一有命中就收工, 于是同名的人只能
    ///      找到第一个, 用户明知道还有第二个却找不到。改成**跑满本批**, 命中只是结果, 不是刹车。
    ///   ② <b>收尾认编号</b> —— 任务开始时把 <see cref="_searchTaskId"/> 存进局部变量,
    ///      收尾时比对。换词之后旧任务还在跑, 它没有资格去改新任务的状态(类注释 ⑤)。
    ///   ③ <b>中途换代就收手</b> —— 用户刷新/切组会让这一轮作废, 继续翻只是在白打接口。
    /// </summary>
    private async Task RunSearchBatchAsync(int taskId)
    {
        if (!IsSearching) return;
        _searchRunning = true;
        OnPropertyChanged(nameof(IsSearchRunning));
        OnPropertyChanged(nameof(ShowSearchContinue));
        try
        {
            var guard = 0;
            while (IsSearching && HasMore && _searchBudget > 0
                   && !Loading && taskId == _searchTaskId && guard++ < SearchBatchPages)
            {
                _searchBudget--;
                _searchScannedPages++;
                // 先睡一下: 连着打 5 个请求太快, 300ms 是"人在翻页"的速度
                await Task.Delay(SearchPageGapMs);
                if (taskId != _searchTaskId || !IsSearching) break;
                await LoadMoreAsync(LoadPurpose.Search);
            }
        }
        catch (Exception)
        {
            // 搜索过程中的失败已经在 LoadMoreAsync 里转成"尾部重试"了, 这里不重复报错
        }
        finally
        {
            // ★ 只有还在跑当前这一轮时才允许收尾(否则旧任务会替新任务清标志)
            if (taskId == _searchTaskId)
            {
                _searchRunning = false;
                OnPropertyChanged(nameof(IsSearchRunning));
                OnPropertyChanged(nameof(ShowSearchContinue));
                OnPropertyChanged(nameof(SearchScopeText));
                OnPropertyChanged(nameof(SearchEmptyText));
                OnPropertyChanged(nameof(SubtitleText));
                NotifyPaging();
            }
        }
    }

    /// <summary>搜索时"一个都没匹配上"的提示(含为什么没匹配)</summary>
    public string SearchEmptyText
    {
        get
        {
            var loaded = _users.Count;
            var 词 = _search.Trim();
            // ★ 四种状态里**只有"正在翻"这一条不回显搜索词**, 而那恰恰是最需要回显的时候:
            //   用户看到"正在继续加载"却不知道在搜什么, 会以为是残留的上一次搜索。
            //   (探针抓到的就是这条: 任务在跑时文案变成"已加载的 30 位里没有匹配, 正在继续翻…",
            //    搜的词整个不见了。)
            if (_searchRunning) return $"已加载的 {loaded} 位里没有匹配「{词}」, 正在继续翻…";
            if (HasMore && _searchBudget > 0)
                return $"已加载的 {loaded} 位里没有匹配「{词}」, 还可能有更多";
            if (HasMore)
                return $"已搜索前 {_searchScannedPages} 页 / {loaded} 位, 没有匹配「{词}」";
            return $"已搜索完整份列表 {loaded} 位, 没有匹配「{词}」";
        }
    }

    /// <summary>搜索为空结果时的整块说明该不该显示</summary>
    public bool ShowSearchEmpty => IsSearching && VisibleUsers.Count == 0 && !Loading && Error.Length == 0;

    /// <summary>"正在继续加载"那条副提示(现在绑定的是真正在跑的那个标志)</summary>
    public bool ShowSearchLoadingHint => IsSearching && _searchRunning;

    // ============================================================ 分组

    /// <summary>
    /// 关注分组(左栏那一列分类)。**只有关注模式有** —— B 站只给"我关注的人"建了分组,
    /// 粉丝那边没有这个概念, 所以粉丝模式整列隐藏(见 <see cref="ShowGroupPanel"/>)。
    /// </summary>
    public ObservableCollection<FollowGroup> Groups { get; } = new();

    /// <summary>虚拟的「全部」项。刻意不放进 <see cref="Groups"/>(理由见属性注释)</summary>
    public FollowGroup AllGroup { get; } = new() { TagId = FollowGroup.AllTagId, Name = "全部" };

    private FollowGroup? _selectedGroup;

    /// <summary>左栏当前选中的分组。切换它就重新加载列表</summary>
    public FollowGroup? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            var old = _selectedGroup;
            if (!SetProperty(ref _selectedGroup, value)) return;
            // ★ 两项都要换: 只置新的会留下"旧的还亮着"(界面上两个粉底),
            //   只清新的则在程序改选中时(切模式退回「全部」)没人负责关掉原来的高亮。
            if (old != null && !ReferenceEquals(old, value)) old.IsSelected = false;
            if (value != null) value.IsSelected = true;
            _ = LoadGroupAsync(value);
        }
    }

    /// <summary>是否该显示左栏分组(仅关注模式)</summary>
    public bool ShowGroupPanel => _mode == RelationMode.Following;

    private string _groupError = "";

    public string GroupError
    {
        get => _groupError;
        private set
        {
            if (SetProperty(ref _groupError, value))
                OnPropertyChanged(nameof(ShowGroupError));
        }
    }

    public bool ShowGroupError => _groupError.Length > 0;

    // ============================================================ 标题/副标题/计数

    /// <summary>是否正在按关键字过滤</summary>
    public bool IsSearching => _search.Trim().Length > 0;

    /// <summary>
    /// 搜索框空着时那句灰色提示, **随模式变**。
    /// ★ 关注列表能搜昵称、mid 与签名, 粉丝列表**根本没有 sign 字段**(见 ApiClient.GetFansAsync),
    ///   所以粉丝模式的提示要说清"只搜昵称与 mid", 别许诺一个做不到的事。
    /// </summary>
    public string SearchPlaceholder => _mode == RelationMode.Following
        ? "搜索关注的人"
        : "搜索粉丝";

    /// <summary>搜索框的悬浮提示(随模式变)</summary>
    public string SearchTip => _mode == RelationMode.Following
        ? "在你关注的人里搜(昵称 / mid / 签名)"
        : "在你粉丝里搜(粉丝列表没有签名, 只能搜昵称与 mid)";

    public RelationMode Mode
    {
        get => _mode;
        private set
        {
            if (!SetProperty(ref _mode, value)) return;
            OnPropertyChanged(nameof(TitleText));
            OnPropertyChanged(nameof(TitleIcon));
            OnPropertyChanged(nameof(CanUnfollow));
            OnPropertyChanged(nameof(EmptyText));
            OnPropertyChanged(nameof(EmptyIcon));
            OnPropertyChanged(nameof(SubtitleText));
            OnPropertyChanged(nameof(CountText));
            OnPropertyChanged(nameof(ShowLoadMoreHint));
            OnPropertyChanged(nameof(ShowGroupPanel));
            OnPropertyChanged(nameof(SearchPlaceholder));
            OnPropertyChanged(nameof(SearchTip));
            NotifyListVisibility();
            OnPropertyChanged(nameof(SelectedMode));
        }
    }

    /// <summary>给页头那个「关注 / 粉丝」分段控件双向绑定用(枚举 ↔ RadioButton.IsChecked)</summary>
    public RelationMode SelectedMode
    {
        get => _mode;
        set
        {
            if (value == _mode) return;
            _ = InitAsync(value);
        }
    }

    public string TitleText => _mode == RelationMode.Following ? "我的关注" : "我的粉丝";
    public string TitleIcon => _mode == RelationMode.Following ? "\uE716" : "\uE5D4";

    /// <summary>
    /// 标题旁那个大数字。**按作用域取**: 「全部」用全量关注数, 选中分组用分组人数,
    /// 拿不到就空着。
    ///
    /// ★ 2026-10-13 修的病: 三人组实际列了 3 个人, 标题却写着 652 ——
    ///   因为分组人数与全量总数挤在同一个字段里, 分组那次赋值被随后的"补一次全量统计"覆盖掉了。
    ///   现在两个量分字段存(<see cref="_totalAll"/> / <see cref="_groupTotal"/>), 谁也盖不掉谁。
    /// ★ 零人数也照实显示 0: 一个空分组是有效状态, 显示空白反而像"没加载出来"。
    /// </summary>
    public string CountText
    {
        get
        {
            if (_mode == RelationMode.Following && _selectedGroup is { IsAll: false })
                return _groupTotal >= 0 ? _groupTotal.ToString() : "";
            return _totalAll > 0 ? _totalAll.ToString() : "";
        }
    }

    /// <summary>
    /// 标题下那句说明。走"只说**增量**"的路子 —— 标题旁边已经有大数字了。
    /// 分组模式下额外带上组名, 让"这个数字是谁的"一目了然。
    /// </summary>
    public string SubtitleText
    {
        get
        {
            if (IsSearching)
            {
                var m = VisibleUsers.Count;
                return m > 0
                    ? $"匹配 {m} 位 · 已加载 {_users.Count} 位"
                    : $"没有匹配 · 已加载 {_users.Count} 位";
            }
            if (_users.Count == 0) return "";

            var parts = new List<string>(3);
            if (_mode == RelationMode.Following && _selectedGroup is { IsAll: false } g)
            {
                parts.Add($"「{g.DisplayName}」");
                if (_groupTotal >= 0 && _groupTotal > _users.Count)
                    parts.Add($"已列出 {_users.Count} / {_groupTotal} 位");
                else
                    parts.Add($"已列出 {_users.Count} 位");
            }
            else if (_totalAll > _users.Count)
            {
                parts.Add($"已列出 {_users.Count} / {_totalAll} 位");
            }
            else
            {
                parts.Add($"已列出 {_users.Count} 位");
            }
            if (_mutualCount > 0) parts.Add($"{_mutualCount} 位互相关注");
            return string.Join(" · ", parts);
        }
    }

    public string EmptyText => _mode == RelationMode.Following
        ? "还没有关注任何 UP 主"
        : "还没有人关注你";
    public string EmptyIcon => _mode == RelationMode.Following ? "\uE716" : "\uE5D4";

    /// <summary>粉丝模式「只翻了前 5 页」的提示。<b>只在真的被人为上限截断时</b>说话</summary>
    public bool ShowLoadMoreHint => _mode == RelationMode.Fans && _users.Count > 0
                                    && _pageState == PageState.Capped
                                    && !Loading && Error.Length == 0 && !NotLoggedIn;

    /// <summary>粉丝模式不显示「取消关注」(关注你的人不是你的关注)</summary>
    public bool CanUnfollow => _mode == RelationMode.Following;

    public bool Loading
    {
        get => _loading;
        private set
        {
            if (SetProperty(ref _loading, value)) NotifyListVisibility();
        }
    }

    public bool LoadingMore
    {
        get => _loadingMore;
        private set
        {
            if (SetProperty(ref _loadingMore, value))
            {
                OnPropertyChanged(nameof(ShowLoadMoreHint));
                NotifyPaging();
            }
        }
    }

    public bool HasMore
    {
        get => _hasMore;
        private set
        {
            if (SetProperty(ref _hasMore, value)) NotifyPaging();
        }
    }

    public string Error
    {
        get => _error;
        private set
        {
            if (!SetProperty(ref _error, value)) return;
            NotifyListVisibility();
            // ★ 错误态是"整页接管", 卡片墙必须让位(否则错误卡片与半屏旧卡片叠在一起)
            OnPropertyChanged(nameof(ShowCardWall));
        }
    }

    public bool NotLoggedIn => !Svc.Session.HasLogin;

    public bool ShowEmptyBlock => !Loading && Error.Length == 0 && !NotLoggedIn
                                && _users.Count == 0 && !ShowSearchEmpty;

    /// <summary>底部"已经到底啦"。**只在真的到底时**(而不是翻页失败时)出现</summary>
    public bool ShowEndHint => !Loading && Error.Length == 0 && !NotLoggedIn
                             && _users.Count > 0 && _pageState == PageState.Ended;

    // ---- 分页尾部状态: 失败可重试(2026-10-13 新增)

    /// <summary>翻页失败 → 列表尾部给一条"重试", 而不是谎称到底</summary>
    public bool ShowPageRetry => !Loading && _users.Count > 0
                               && _pageState == PageState.Failed && Error.Length == 0;

    /// <summary>重试那一行的说明(带上是第几页, 让用户知道在续哪一页)</summary>
    public string PageRetryText => _pageState == PageState.Failed
        ? $"第 {_failedPage} 页没加载出来" : "";

    public ICommand RetryPageCommand { get; }

    private void NotifyListVisibility()
    {
        OnPropertyChanged(nameof(ShowEmptyBlock));
        OnPropertyChanged(nameof(ShowEndHint));
        OnPropertyChanged(nameof(ShowLoadMoreHint));
        OnPropertyChanged(nameof(ShowPageRetry));
    }

    private void NotifyPaging()
    {
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(ShowEndHint));
        OnPropertyChanged(nameof(ShowLoadMoreHint));
        OnPropertyChanged(nameof(ShowPageRetry));
        OnPropertyChanged(nameof(PageRetryText));
        OnPropertyChanged(nameof(ShowSearchContinue));
        OnPropertyChanged(nameof(SearchScopeText));
        OnPropertyChanged(nameof(SearchEmptyText));
    }

    public ICommand RefreshCommand { get; }
    public ICommand LoadMoreCommand { get; }
    public ICommand LoginCommand { get; }
    public ICommand OpenUpCommand { get; }
    public ICommand ClearSearchCommand { get; }

    // ============================================================ 构造与生命周期

    public bool Inited { get; private set; }

    public RelationViewModel()
    {
        // ★ _selectedGroup 初始必须是 AllGroup, 不能留 null:
        //   SelectedGroup 的 setter 靠"把旧项 IsSelected 清掉"保证左栏互斥, "旧项"就是它。
        //   它若是 null, 用户第一次点某个分组时 old==null, 清旧项被跳过, 于是「全部」与
        //   新分组**同时高亮**(用户报"选中态乱了")。
        // ★ 走字段而不是 setter: setter 会触发 LoadGroupAsync → 刷新, 构造函数不该发网络请求。
        _selectedGroup = AllGroup;
        AllGroup.IsSelected = true;

        // Users 是"数据源", 卡片墙绑的是 Rows(按列数切好的行)。所以每次数据变动都要
        // ① 重算互关数 ② 重跑过滤切行 ③ 刷新标题副标题。
        // ★ mayAutoLoad: 搜索的自动续翻由 <see cref="RunSearchBatchAsync"/> 独立驱动,
        //   不再挂在这个钩子上 —— 否则翻页追加(逐条 Add)会与搜索任务互相重入。
        _users.CollectionChanged += (_, _) =>
        {
            if (_bulkFilling) return;   // 批量填充期间只在末尾统一算一次
            RecountMutual();
            ApplyFilter(replaceRows: false);   // 逐条 Add ⇒ 行**追加**, 不是"不重建"
        };

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(reloadGroups: true));
        LoadMoreCommand = new AsyncRelayCommand(() => LoadMoreAsync());
        RetryPageCommand = new AsyncRelayCommand(RetryPageAsync);
        ContinueSearchCommand = new AsyncRelayCommand(ContinueSearchAsync);
        LoginCommand = new RelayCommand(() =>
        {
            var win = new Views.LoginWindow
            {
                Owner = System.Windows.Application.Current.MainWindow,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner
            };
            win.ShowDialog();
        });
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        OpenUpCommand = new RelayCommand(p =>
        {
            if (p is FollowUser u) Svc.Navigate?.OpenUserSpace(u.Mid.ToString());
        });

        Svc.Session.Changed += OnSessionChanged;
    }

    /// <summary>
    /// 登录态变化。★ 必须先切线程: <c>SessionManager.Changed</c> 的触发点**不一定**在 UI 线程
    /// —— 其中一个是 <c>SetBuvid3</c>, 它在 API 的网络响应里调用, 会被任意请求触发。
    /// 在那里直接 OnPropertyChanged 就是跨线程访问绑定源。
    /// </summary>
    private void OnSessionChanged()
    {
        var app = System.Windows.Application.Current;
        if (app != null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(OnSessionChangedCore);
            return;
        }
        OnSessionChangedCore();
    }

    private void OnSessionChangedCore()
    {
        OnPropertyChanged(nameof(NotLoggedIn));
        // ★ 退出登录/换账号: 列表、分组、搜索、计数、错误**一起**清空。
        //   早先这里只调 RefreshAsync, 而 RefreshOnceAsync 的未登录分支走 ResetForModeSwitch ——
        //   那函数刻意**不清 Groups**(关注↔粉丝来回切不该重打这个请求), 于是左栏摆着上一个
        //   账号的分组名, 选中项还拿着上一个人的 tagid 去请求, 请求照样成功, 列出来的却是
        //   另一组人。账号乙看不见账号甲的分组, 这一条就靠 <see cref="ResetAll"/> 兜住。
        if (!Svc.Session.HasLogin) ResetAll();
        else _ = RefreshAsync(reloadGroups: true);

        NotifyListVisibility();
    }

    /// <summary>已加载的人里互相关注的有几个</summary>
    private void RecountMutual()
    {
        var n = 0;
        for (var i = 0; i < _users.Count; i++)
            if (_users[i].IsMutual) n++;
        _mutualCount = n;
    }

    /// <summary>
    /// 进入页面。<paramref name="mode"/> = 用户点的是「关注」还是「粉丝」。
    ///
    /// ★ 模式变了就**先把列表清空**再重拉: 新请求失败时, 屏幕上不该还留着上一个模式的
    ///   几百条数据而标题已经写着「我的粉丝」—— 用户看到的是"点了粉丝、列出来的还是我
    ///   关注的人"的页面, 而且没有任何提示能解释它。先清空, 最坏也只是"加载失败 + 重试"。
    /// </summary>
    public async Task InitAsync(RelationMode mode)
    {
        Inited = true;
        if (_mode != mode)
        {
            BeginGeneration();          // 让还在飞的旧模式请求当场作废
            Mode = mode;
            ResetForModeSwitch();
            await RefreshAsync(reloadGroups: true);
            return;
        }
        // 同模式: 不重复请求(右上角有刷新按钮); 空列表 + 没在加载才重拉
        if (_users.Count == 0 && !Loading) await RefreshAsync(reloadGroups: false);
    }

    /// <summary>切模式: 清列表与计数, 分组退回「全部」(粉丝没有分组)</summary>
    private void ResetForModeSwitch()
    {
        BeginGeneration();
        ResetListAndCounters();
        SelectAllGroup();
    }

    /// <summary>退出登录/换账号: 连分组一起清空(理由见 <see cref="OnSessionChangedCore"/>)</summary>
    private void ResetAll()
    {
        BeginGeneration();
        ResetListAndCounters();
        SelectAllGroup();
        Groups.Clear();
        _groupsAccount = 0;
        GroupError = "";
        OnPropertyChanged(nameof(ShowGroupPanel));
    }

    /// <summary>把列表、计数、分页状态清干净(不动分组 —— 分组归账号管)</summary>
    private void ResetListAndCounters()
    {
        Bulk(() => _users.Clear());
        _totalAll = -1;
        _groupTotal = -1;
        _mutualCount = 0;
        _account = 0;
        _page = 1;
        _failedPage = 1;
        _pageState = PageState.Idle;
        HasMore = false;
        LoadingMore = false;
        Error = "";
        _searchTaskId++;
        _searchRunning = false;
        _searchBudget = 0;
        _searchScannedPages = 0;
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(IsSearchRunning));
    }

    /// <summary>选中项退回「全部」(左栏高亮也要跟上)</summary>
    private void SelectAllGroup()
    {
        if (_selectedGroup == null) return;
        _selectedGroup.IsSelected = false;
        _selectedGroup = AllGroup;
        AllGroup.IsSelected = true;
        OnPropertyChanged(nameof(SelectedGroup));
    }

    // ============================================================ 刷新

    private bool _pendingReload;
    private bool _reloadGroupsRequested;
    private bool _refreshRunning;

    /// <summary>
    /// 刷新入口(刷新按钮 / 登录态变化 / 切模式 / 切分组 / F5 都走这里)。
    ///
    /// ★ 两层保护, 缺一不可:
    ///   · <b>串行</b>: 同一时刻只跑一轮, 飞行期间到来的刷新只置标记, 由循环尾巴再跑一轮
    ///     ⇒ 不会一口气打出去好几个请求, 顺序也可控;
    ///   · <b>代号</b>: <see cref="BeginGeneration"/> 让上一轮在飞的结果**作废**,
    ///     于是"关注那轮跑得慢、粉丝这轮跑得快"也不会互相串。
    ///   只做串行是不够的 —— 串行只保证"不并发", 保证不了"结果属于这一轮"。
    /// </summary>
    public async Task RefreshAsync(bool reloadGroups = false)
    {
        _reloadGroupsRequested |= reloadGroups;
        BeginGeneration();                 // 旧的一律作废并取消
        _pendingReload = true;
        if (_refreshRunning) return;       // 已在跑, 循环尾巴会替我们再跑一轮

        while (_pendingReload)
        {
            _pendingReload = false;
            _refreshRunning = true;
            var wantGroups = _reloadGroupsRequested;
            _reloadGroupsRequested = false;
            Loading = true;
            LoadingMore = false;
            Error = "";
            try
            {
                await RefreshOnceAsync(wantGroups);
            }
            catch (OperationCanceledException)
            {
                // 换代导致的取消: 安静收手, 不写 Error
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
            finally
            {
                _refreshRunning = false;
                Loading = false;
            }
        }
    }

    /// <summary>
    /// 真正干一轮活: 确认登录 → 确认账号 → (按需)拉分组 → 取第 1 页 → 铺进列表。
    /// 每一步都在动手前把 <see cref="_account"/>/模式/分组读进 <see cref="ReqKey"/>,
    /// 提交前再逐项核对 —— 见类注释 ①②。
    /// </summary>
    private async Task RefreshOnceAsync(bool reloadGroups)
    {
        if (!Svc.Session.HasLogin)
        {
            // ★ 包进 UI 线程: 会动 ObservableCollection 并发通知, 而 RefreshAsync
            //   也可能由后台线程的会话事件触发。
            await RunOnUiAsync(() =>
            {
                if (Svc.Session.HasLogin) return;   // 期间又登录回来了, 交给登录态事件
                ResetAll();
                OnPropertyChanged(nameof(NotLoggedIn));
                NotifyListVisibility();
            });
            return;
        }

        // 自己的 mid: 关注/粉丝/分组接口都要它, 而且必须**先**拿到才能判断分组要不要重拉
        var (sok, _, me) = await Svc.Api.GetUserStateAsync();
        if (!sok || me == null || me.Mid <= 0)
        {
            await RunOnUiAsync(() => { Error = "未登录或登录状态已过期, 请重新登录"; });
            return;
        }

        var accountChanged = _account != me.Mid;
        await RunOnUiAsync(() =>
        {
            _account = me.Mid;
            // 换账号: 上一账号的分组与计数一律作废
            if (accountChanged)
            {
                Groups.Clear();
                _groupsAccount = 0;
                _groupsAccount = me.Mid;   // 待会儿会重拉; 先占位防止中途又判一次"换账号"
                _totalAll = -1;
                _groupTotal = -1;
                SelectAllGroup();
                SearchText = "";
                NotifyListVisibility();
            }
        });
        accountChanged = false;   // 上面已同步处理, 后面的判断以 _account 为准

        // ★ 分组必须在取列表**之前**完成: LoadPage 要靠分组决定走哪个接口。
        if (_mode == RelationMode.Following
            && (Groups.Count == 0 || reloadGroups || _groupsAccount != _account))
            await LoadGroupsAsync(reloadGroups);

        var key = new ReqKey(_account, _mode, CurrentTagId, 1, _generation);
        var (ok, err, list, total) = await FetchPageAsync(key);

        await RunOnUiAsync(() =>
        {
            if (!CanCommit(key)) return;   // 已被换代/切走: 整份丢掉
            if (!ok || list == null)
            {
                // 已经在屏上的数据**不要清**: 刷新失败时留着一屏旧内容(用户还能看)
                // 比清成一片空白强。
                if (_users.Count == 0 && err != null) Error = err;
                HasMore = false;
                _pageState = PageState.Ended;
                NotifyPaging();
                return;
            }

            Bulk(() =>
            {
                _users.Clear();
                foreach (var u in list) _users.Add(u);
            });
            _page = 1;
            _failedPage = 1;

            // ★ 计数分家: 粉丝接口给 total 就当全量; 关注接口不给 total,
            //   下面再单独补一次 nav/stat。分组模式则由分组接口的 count 说了算。
            if (_mode == RelationMode.Fans && total > 0)
            {
                _totalAll = total;
            }
            else if (_mode == RelationMode.Following && _selectedGroup is { IsAll: false } g)
            {
                _groupTotal = g.Count;
            }
            else
            {
                _totalAll = -1;
            }
            OnPropertyChanged(nameof(CountText));
            OnPropertyChanged(nameof(SubtitleText));

            // 翻页依据: 本页拿满 + 没到页数上限
            HasMore = list.Count >= PageSize && (_mode != RelationMode.Fans || 2 <= MaxPage);
            _pageState = PageState.Idle;
            NotifyPaging();
        });

        // 关注模式补一次准确总数(只影响数字, 不影响列表)
        if (_mode == RelationMode.Following && _selectedGroup is { IsAll: true })
        {
            var (sok2, _, following, _, _) = await Svc.Api.GetUserNavStatAsync();
            if (!sok2 || following <= 0) return;
            await RunOnUiAsync(() =>
            {
                // ★ 只有在"还没定下总数"或"这个数更大"时才采纳:
                //   用户可能已经切走/切组/退登, 旧模式那个数不该把当前界面的数字改掉。
                if (_mode != RelationMode.Following || !(_selectedGroup is { IsAll: true }))
                    return;
                if (_totalAll >= following) return;
                _totalAll = following;
                OnPropertyChanged(nameof(CountText));
                OnPropertyChanged(nameof(SubtitleText));
            });
        }
    }

    /// <summary>
    /// 唯一发请求的地方。★ 坐标全部来自 <paramref name="key"/>, **不读任何可变字段** ——
    /// 这是"旧分页串进新列表"的根治点: 等待期间界面怎么变都不影响这一轮打向哪个接口、哪一页。
    /// </summary>
    private async Task<(bool ok, string? err, List<FollowUser>? list, long total)> FetchPageAsync(ReqKey key)
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        if (key.Mode == RelationMode.Fans)
            return await Svc.Api.GetFansAsync(key.Account, key.Page, PageSize, ct);

        // 「全部」走原接口; 选中真分组必须改走 /x/relation/tag ——
        // 实测 followings 会**静默忽略** tagid(不报错、不为空, 只是没过滤)。
        if (key.TagId != FollowGroup.AllTagId)
        {
            var g = await Svc.Api.GetFollowingsByTagAsync(key.Account, key.TagId, key.Page, PageSize, ct);
            return (g.ok, g.err, g.list, 0L);
        }

        var r = await Svc.Api.GetFollowingsAsync(key.Account, key.Page, PageSize, ct);
        return (r.ok, r.err, r.list, 0L);
    }

    // ============================================================ 分组

    /// <summary>
    /// 拉一次分组列表(左栏), 并顺手选中当前那个。
    ///
    /// ★ <paramref name="force"/> = 手动刷新时为 true: 网页端新增/删除分组后,
    ///   靠"没变化就不重拉"的短路是同步不出来的, 必须真的再打一次。
    /// ★ 提交前核对 <see cref="_groupsAccount"/>: 用户在这几百毫秒里换了账号的话,
    ///   这一份分组就不是他的, 摆上去就是"上一账号的分组"(类注释 ③)。
    /// </summary>
    private async Task LoadGroupsAsync(bool force = false)
    {
        var gen = _generation;
        var account = _account;
        if (_groupsAccount == account && Groups.Count > 0 && !force) return;

        var (ok, err, groups) = await Svc.Api.GetFollowGroupsAsync();
        await RunOnUiAsync(() =>
        {
            if (gen != _generation || account != _account) return;   // 已换代/换账号
            _groupsAccount = account;

            Groups.Clear();
            AllGroup.IsSelected = _selectedGroup is { IsAll: true };

            var oldTagId = _selectedGroup?.TagId;
            if (ok && groups != null)
                foreach (var g in groups) Groups.Add(g);

            GroupError = ok ? "" : (err ?? "关注分组加载失败");

            // 保持选中: 用户切到某个分组之后 RefreshAsync 会再跑一遍(比如登录态变化),
            // 那时不该把选择弹回「全部」。找不到原分组(用户自己删了)才退回「全部」。
            var keep = Groups.FirstOrDefault(x => x.TagId == oldTagId) ?? AllGroup;
            if (_selectedGroup != null && !ReferenceEquals(_selectedGroup, keep))
                _selectedGroup.IsSelected = false;
            _selectedGroup = keep;
            keep.IsSelected = true;
            OnPropertyChanged(nameof(SelectedGroup));

            // 选中组的人数跟着一起对一次(别拿旧的 652 顶着)
            if (_selectedGroup is { IsAll: false } g2) _groupTotal = g2.Count;
            else _groupTotal = -1;
            OnPropertyChanged(nameof(CountText));
            OnPropertyChanged(nameof(SubtitleText));
        });
    }

    /// <summary>点左栏某个分组: 换分组 → 清列表 → 重新拉那一组的第一页</summary>
    private async Task LoadGroupAsync(FollowGroup? g)
    {
        if (g == null) return;
        BeginGeneration();                  // 分组变了, 旧的翻页请求全部作废
        Bulk(() => _users.Clear());
        _page = 1;
        _failedPage = 1;
        _pageState = PageState.Idle;
        HasMore = false;
        _groupTotal = g.IsAll ? -1 : g.Count;
        _totalAll = -1;
        Error = "";
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(SubtitleText));

        // 切组之后搜索要重新开始: 只搜**当前组**(类注释 ⑤)
        if (IsSearching) { _searchTaskId++; _searchBudget = SearchBatchPages; _searchScannedPages = 0; }

        await RefreshAsync(reloadGroups: false);
    }

    // ============================================================ 翻页

    /// <summary>这次翻页是谁发起的。搜索批次与用户滚动共用同一条路径, 只为错误提示服务</summary>
    private enum LoadPurpose { User, Search }

    /// <summary>
    /// 追加下一页。
    ///
    /// ★ 失败/到底/重复页/达上限是**四种不同的结局**, 早先全塌成一句"HasMore=false",
    ///   于是"第 2 页没加载出来"与"确实没有第 2 页"在界面上长得一模一样 ——
    ///   列表停在 30 条、底部照常弹"已经到底啦"。现在分开记(见 <see cref="_pageState"/>)。
    /// ★ 页码**只在提交成功之后**才前进, 失败时保持原样 ⇒ 重试就是同一页, 不跳页不重复追加。
    /// </summary>
    public Task LoadMoreAsync() => LoadMoreAsync(LoadPurpose.User);

    private async Task LoadMoreAsync(LoadPurpose purpose)
    {
        if (Loading || LoadingMore || !HasMore || _account <= 0) return;
        if (_pageState is PageState.Failed or PageState.Ended or PageState.Capped) return;

        // ★ 坐标先冻结: 这一页是按**哪个账号 / 哪种模式 / 哪个分组 / 哪一页**发的, 从此不变
        var key = new ReqKey(_account, _mode, CurrentTagId, _page + 1, _generation);

        LoadingMore = true;
        _pageState = PageState.Loading;
        try
        {
            var (ok, err, list, total) = await FetchPageAsync(key);
            await RunOnUiAsync(() =>
            {
                LoadingMore = false;
                if (!CanCommit(key)) { NotifyPaging(); return; }   // 已换代: 整份丢掉

                // ★★★ 这里曾经写成 `if (err == null)`, 那会把**每一次成功**都当成"被取消"!
                //   原因: ApiClient 成功时返回的是 `(true, null, list, total)` —— err **也是 null**
                //   (那是元组里的 errorMessage 槽, 成功时当然空)。于是"成功"落进"安静收手"分支,
                //   直接 return, 列表一条不追加、页码不前进。
                //   症状非常隐蔽: 首次加载走 RefreshAsync 不经过这里, 页面看着完全正常;
                //   只有"往下滚 → 加载下一页"这条路是死的, 而翻页失败后的「重试」更是永远没反应。
                //   探针抓到: 「重试后 页码:1, 页状态:0(Idle), 一条没加」而 API 直问同页完全正常。
                //   正确的判据是 ApiClient 的约定: **ok=false 且 err=null** 才是取消(见 CodeCanceled)。
                if (!ok && err == null)
                {
                    _pageState = PageState.Idle;
                    NotifyPaging();
                    return;
                }
                if (!ok || list == null || list.Count == 0)
                {
                    // ★ 失败**不当成错误弹出来**, 也**绝不写 Error**: 错误态在 XAML 里是
                    //   "整页拦腰截断列表"的阻断态, 一次网络抖动就把一百多条换成一张错误页。
                    //   保留列表、保留待重试页码, 尾部给一条"重试"。
                    _failedPage = key.Page;
                    _pageState = PageState.Failed;
                    HasMore = false;
                    Svc.Toast.Show("加载下一页失败: " + err);
                    NotifyPaging();
                    return;
                }

                var seen = new HashSet<long>();
                foreach (var u in _users) seen.Add(u.Mid);
                var added = 0;
                Bulk(() =>
                {
                    foreach (var u in list)
                        if (u.Mid > 0 && seen.Add(u.Mid)) { _users.Add(u); added++; }
                });
                if (added == 0)
                {
                    // 整页跟已有的全重(分页期间数据变了, 或接口重复吐同一页):
                    // 此时**不能**只靠 HasMore 收手 —— 页码没前进, 下一次又请求同一页,
                    // 又拿回同一批重复项 ⇒ 永远停不下来, 每次还白打一次接口。直接判定到底。
                    _pageState = PageState.Ended;
                    HasMore = false;
                    NotifyPaging();
                    return;
                }

                _page = key.Page;   // ← 只在真的落地之后前进
                if (!_totalExactYet && total > 0) { _totalAll = total; }

                var full = list.Count >= PageSize;
                if (full && key.Mode == RelationMode.Fans && key.Page + 1 > MaxPage)
                {
                    // 真的撞上 5 页上限: 这一次要说清楚, 不是"到底了"
                    _pageState = PageState.Capped;
                    HasMore = false;
                }
                else if (full)
                {
                    _pageState = PageState.Idle;
                }
                else
                {
                    _pageState = PageState.Ended;
                    HasMore = false;
                }
                OnPropertyChanged(nameof(CountText));
                OnPropertyChanged(nameof(SubtitleText));
                NotifyPaging();
            });
        }
        catch (OperationCanceledException)
        {
            await RunOnUiAsync(() =>
            {
                LoadingMore = false;
                if (CanCommit(key)) { _pageState = PageState.Idle; NotifyPaging(); }
            });
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() =>
            {
                LoadingMore = false;
                if (!CanCommit(key)) return;
                _failedPage = key.Page;
                _pageState = PageState.Failed;
                HasMore = false;
                Svc.Toast.Show("加载下一页失败: " + ex.Message);
                NotifyPaging();
            });
        }
    }

    /// <summary>全量总数是否已经拿到(见 <see cref="_totalAll"/>)</summary>
    private bool _totalExactYet => _totalAll > 0;

    /// <summary>尾部「重试」: 重发**同一页**(页码在失败时压根没前进)</summary>
    private async Task RetryPageAsync()
    {
        if (_pageState != PageState.Failed) return;
        _page = _failedPage - 1;      // LoadMoreAsync 取的是 _page + 1
        _pageState = PageState.Idle;
        HasMore = true;
        NotifyPaging();
        await LoadMoreAsync();
    }

    // ============================================================ 取消关注

    /// <summary>
    /// 取消关注(仅关注模式)。成功后从列表移除并把相关计数各减一。
    ///
    /// ★ 失败**不写 Error**: 一次网络抖动就把几百条列表换成红色错误页, 代价与问题完全
    ///   不成比例。右下角弹一条 Toast 才是这类瞬时失败该去的地方。
    /// ★ 减一要减**三个**地方: 全量数、分组人数、以及左栏那个分组项的计数 ——
    ///   漏掉任何一个都会留下"我明明取消关注了, 数字却没动"的错觉。
    /// </summary>
    public async Task UnfollowAsync(FollowUser? u)
    {
        if (u == null || u.Mid <= 0 || !CanUnfollow) return;
        var (ok, err) = await Svc.Api.FollowUpAsync(u.Mid, follow: false);
        if (!ok)
        {
            Svc.Toast.Show("取消关注失败: " + (err ?? "未知错误"));
            return;
        }

        await RunOnUiAsync(() =>
        {
            Bulk(() => _users.Remove(u));
            if (_totalAll > 0) _totalAll--;
            if (_groupTotal > 0) _groupTotal--;
            if (_selectedGroup is { IsAll: false } g)
            {
                var item = Groups.FirstOrDefault(x => x.TagId == g.TagId);
                if (item != null && item.Count > 0) item.Count--;
            }
            else if (_selectedGroup is { IsAll: true })
            {
                // 「全部」下退 groupTotal 没必要, 但左栏所有分组的计数都该动 ——
                // 这个人可能同时在几个分组里, 而接口不给成员列表, 无法精确知道。
                // 退而求其次: 只改确实大于 0 的那些, 让用户看到"数字在往下走"。
                // (B 站本地缓存会在下次刷新时给出准数, 这里不会算错只会偏大。)
                foreach (var gi in Groups)
                    if (gi.Count > 0) gi.Count--;
            }
            OnPropertyChanged(nameof(CountText));
            OnPropertyChanged(nameof(SubtitleText));
            NotifyListVisibility();
        });
        Svc.Toast.Show($"已取消关注 {u.Name}");
    }

    /// <summary>
    /// 回到 UI 线程执行。应用正在退出时 Dispatcher 可能已经 shuts down,
    /// 那就直接在当前线程跑 —— 那时候不会有人看结果, 但至少别抛 NRE 把异常带崩。
    /// </summary>
    private static Task RunOnUiAsync(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app == null) { action(); return Task.CompletedTask; }
        return app.Dispatcher.InvokeAsync(action).Task;
    }
}
