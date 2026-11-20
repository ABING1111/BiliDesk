using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;
using BiliDesk.Views.Windows;

namespace BiliDesk.ViewModels;

/// <summary>
/// 收藏页 ViewModel。
///
/// 这里**只有云端收藏**了: 本机收藏夹(本地 JSON)已按需求整体移除。
/// 之前的"本机/云端"双模式看着像多一种保险, 实际上是两套互不相通的数据 ——
/// 用户在播放页点"收藏"存进本地 JSON, 换台机器或者去网页端都看不到, 反而更像 bug。
/// 现在统一以 B 站账号的收藏夹为准, 语义单一、跨端一致。
///
/// 编辑模式(多选 + 批量移出 + 清空当前收藏夹)同样走云端接口, 不做本地假删。
///
/// ★★★ 2026-11-19: 列表从"滚动到底追加"改成**页码分页**(与 UP 主主页投稿同一套,
///   用户要求对齐)。一次只保留当前页(20 条), 底部页码栏负责翻页:
///     · 总数取自收藏夹列表接口的 <c>media_count</c>(选中夹子时就已经有了);
///     · 翻页后把列表滚回顶部;
///     · 编辑模式的"全选"范围 = **当前页**(只显示一页, 跨页勾选没有意义)。
///   好处不只是"能跳页": 卡片墙是零虚拟化 + 每张卡攥着解码后的封面, 一次只铺 20 张
///   比滚了几百张之后的内存小得多。
///
/// ★ 两个必须记住的接口坑(都实测过, 见 ApiClient.GetFavResourcesAsync 的注释):
///   ① 请求**越界页**时接口仍然回 code=0, 但 medias 为 null 且 media_count 变成 0 ——
///      所以绝不能无条件拿响应里的 total 覆盖总数;
///   ② 越界页在旧代码里会解析成"空列表且成功", 于是一路显示"这个收藏夹还是空的"。
///      现在按"本页 0 条 + 总数 > 0 ⇒ 页码越界"自动回退一页。
/// </summary>
public class FavoritesViewModel : VideoListSelectionViewModel, IPagedViewModel
{
    /// <summary>每页条数(B 站网页版收藏夹也是 20; 与 pn/ps 的分页边界一致)</summary>
    private const int PageSize = 20;

    private bool _loading;
    private string _error = "";
    private long _currentFolderId;

    // ---------------- 分页状态 ----------------
    private int _currentPage = 1;
    private int _totalPages;
    private int _totalCount;
    private bool _isPaging;
    /// <summary>请求代次: 快速切夹 / 连点页码时只认最后一次的结果, 防止旧响应盖掉新页</summary>
    private int _loadGen;

    private bool _isEditMode;
    private bool _busy;
    private string _busyText = "";

    // ------------------------------------------------------------ 搜索(2026-11-20)

    /// <summary>搜索框文本(输入即触发 350ms 防抖, 然后走服务端 keyword 搜索)。</summary>
    private string _searchText = "";

    /// <summary>当前生效的关键词(空串 = 整个收藏夹)。分页请求带着它, 翻页不会跳回全量列表。</summary>
    private string _activeKeyword = "";

    private readonly System.Windows.Threading.DispatcherTimer _searchDebounce;

    /// <summary>搜索请求在整页加载/切夹途中被搁置, 等那一轮结束后补跑。</summary>
    private bool _searchPending;

    /// <summary>搜索框文本。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            OnPropertyChanged(nameof(HasSearch));
            _searchDebounce.Stop();
            if (value.Trim().Length == 0) _ = ApplySearchAsync();   // 清空立刻回到整个收藏夹
            else _searchDebounce.Start();
        }
    }

    public bool HasSearch => _searchText.Trim().Length > 0;

    /// <summary>搜索零命中: 只替换卡片区, 不弹整页错误(更不要「重试」按钮)。</summary>
    public bool ShowNoMatch => HasSearch && !IsPaging && !Loading
                               && Items.Count == 0 && Error.Length == 0;

    public string NoMatchText => $"没有找到包含「{_searchText.Trim()}」的视频";

    /// <summary>正在新建收藏夹(弹窗/请求期间禁用「+」, 防止连点建出两个同名夹)</summary>
    private bool _creatingFolder;

    /// <summary>正在删除收藏夹(请求期间禁用删除按钮)</summary>
    private bool _deletingFolder;

    /// <summary>
    /// 当前打开的收藏夹对象。
    /// ★ 单独存一份而不是每次按 id 去 Folders 里找: 删除按钮要读 IsDefault,
    ///   而重建列表的那一瞬 Folders 可能已经空了/换了对象, 按 id 找会得到 null。
    /// </summary>
    private FavFolder? _currentFolder;

    /// <summary>
    /// 上次拉收藏夹列表时 <see cref="FavFolderSync"/> 的版本号。
    /// 在别处(播放器收藏勾选窗 / 「我的」页)新建或删除了收藏夹后版本会自增,
    /// 本页下次显示时据此重拉 —— 否则 chip 条里会缺一个/多一个夹。
    /// </summary>
    private int _foldersVersion = -1;

    /// <summary>
    /// 列表需要重拉(与版本号独立的第二种"脏")。
    /// 场景: 刚建完收藏夹, 但 <c>created/list-all</c> 还没把新夹带出来 —— 本地先插一颗占位,
    /// 同时打上这个标记, 下次进页面一定再拉一次权威列表(封面/顺序/数量)。
    /// </summary>
    private bool _foldersDirty;

    public ObservableCollection<FavFolder> Folders { get; } = new();
    public ObservableCollection<VideoItem> Items { get; } = new();

    /// <summary>底部页码栏的条目(数字页或省略号)</summary>
    public ObservableCollection<PageItem> PageItems { get; } = new();

    /// <summary>请求页面把列表滚回顶部(翻页后调; 由 FavoritesPage 订阅)</summary>
    public event Action? ScrollToTopRequested;

    public bool Loading
    {
        get => _loading;
        private set
        {
            if (!SetProperty(ref _loading, value)) return;
            OnPropertyChanged(nameof(ShowLoadingOverlay));
            OnPropertyChanged(nameof(CanCreateFolder));
            OnPropertyChanged(nameof(CanDeleteFolder));
            OnPropertyChanged(nameof(ShowNoMatch));
        }
    }

    public string Error
    {
        get => _error;
        private set
        {
            if (!SetProperty(ref _error, value)) return;
            OnPropertyChanged(nameof(ShowNoMatch));
        }
    }

    /// <summary>
    /// 是否显示居中的大菊花。
    /// ★ 除了"整页加载收藏夹列表", 还包括"切夹/首次进页 && 当前页还没上屏" ——
    ///   否则换夹的那一小会儿卡片区是空的(页码栏已经画出来了, 列表却是白板),
    ///   看起来像"这个夹是空的"。翻到第 N 页时**不算**(那时旧页还在, 页码栏有自己的小转圈)。
    /// </summary>
    public bool ShowLoadingOverlay => Loading || (IsPaging && Items.Count == 0);

    /// <summary>顶栏的条数(当前收藏夹的总条数, 不是"已加载条数")</summary>
    public string CountText => _totalCount > 0 ? $"({_totalCount})" : "";

    /// <summary>是否处于编辑模式(每张卡片出现选中圆标, 顶部换成操作条)</summary>
    public bool IsEditMode
    {
        get => _isEditMode;
        private set
        {
            if (!SetProperty(ref _isEditMode, value)) return;
            if (!value) ClearSelection();
        }
    }

    /// <summary>正在执行批量移出: 期间禁用按钮, 避免重复点击把同一批删两遍</summary>
    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!SetProperty(ref _busy, value)) return;
            OnPropertyChanged(nameof(CanOperate));
            // 批量操作期间也不许翻页: 翻到别的页再回来会把"删的是哪一页"弄乱
            OnPropertyChanged(nameof(CanGoPrev));
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(CanChangePage));
        }
    }

    /// <summary>操作进度文案(如"正在移出 40/120…")</summary>
    public string BusyText { get => _busyText; private set => SetProperty(ref _busyText, value); }

    /// <summary>
    /// 新建收藏夹进行中(弹窗开着 / 请求在飞)。
    /// ★ 与 <see cref="Busy"/>(批量移出)分开: 建夹期间不该禁用翻页, 也不该让顶栏变成编辑态文案。
    /// </summary>
    public bool CreatingFolder
    {
        get => _creatingFolder;
        private set
        {
            if (!SetProperty(ref _creatingFolder, value)) return;
            OnPropertyChanged(nameof(CanCreateFolder));
        }
    }

    /// <summary>「+」是否可点: 已登录、不在新建中、也不在整页加载中</summary>
    public bool CanCreateFolder => !CreatingFolder && !Loading && Svc.Session.HasLogin;

    /// <summary>正在删除收藏夹(请求在飞)</summary>
    public bool DeletingFolder
    {
        get => _deletingFolder;
        private set
        {
            if (!SetProperty(ref _deletingFolder, value)) return;
            OnPropertyChanged(nameof(CanDeleteFolder));
        }
    }

    /// <summary>「删除当前收藏夹」是否可点: 有夹、不是默认夹、已登录、不在忙</summary>
    public bool CanDeleteFolder
        => !DeletingFolder && !Loading && _currentFolder is { IsDefault: false } && Svc.Session.HasLogin;

    /// <summary>删除按钮的提示。默认夹/没选中夹时点不动, 这里说明原因</summary>
    public string DeleteFolderToolTip
    {
        get
        {
            if (_currentFolder == null) return "请先打开一个收藏夹";
            if (_currentFolder.IsDefault) return "默认收藏夹不能删除";
            return "删除当前收藏夹（不可恢复）";
        }
    }

    /// <summary>
    /// 账号下还没有任何收藏夹(空状态)。
    /// 空状态面板据此显示「新建收藏夹」—— 之前这里只有一个「重试」,
    /// 新用户点进收藏页除了重试没有别的路。
    /// </summary>
    public bool HasNoFolders => Folders.Count == 0;

    /// <summary>
    /// 空状态里的「新建收藏夹」是否该出现。
    /// ★ 未登录时要藏起来: 建夹需要登录, 放一颗点了只会弹"需要先登录"的灰按钮,
    ///   不如让页面专心显示"请先登录"。
    /// </summary>
    public bool ShowEmptyCreateButton => HasNoFolders && Svc.Session.HasLogin;

    /// <summary>当前收藏夹名称, 编辑模式下显示"正在编辑: xxx"</summary>
    public string CurrentFolderTitle { get; private set; } = "";

    // 选中计数一律基于**当前可见项** —— 分页之后"可见项"就是当前页, 所以
    // "全选"自然只作用于本页, 不会跨页乱选。
    // (实现见基类 VideoListSelectionViewModel: 全选的基数只有一处定义, 不会与历史/稍后再看漂移)
    public bool CanOperate => !Busy;

    /// <summary>
    /// 全选按钮文案。★ 分页之后基数只有当前页, 文案必须跟着说清楚 ——
    /// 否则用户会以为"全选"选的是整个收藏夹(实际只选这一页的 20 条)。
    /// </summary>
    public override string SelectAllText => AllSelected ? "取消全选本页" : "全选本页";

    // ---------------- 分页公开属性 ----------------

    public int CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (!SetProperty(ref _currentPage, value)) return;
            OnPropertyChanged(nameof(CanGoPrev));
            OnPropertyChanged(nameof(CanGoNext));
        }
    }

    public int TotalPages
    {
        get => _totalPages;
        private set
        {
            if (!SetProperty(ref _totalPages, value)) return;
            OnPropertyChanged(nameof(ShowPager));
            OnPropertyChanged(nameof(CanGoPrev));
            OnPropertyChanged(nameof(CanGoNext));
        }
    }

    /// <summary>
    /// 当前收藏夹的总条数(权威值来自接口的 media_count)。
    /// ★ 名字别用 TotalCount: 基类 <see cref="VideoListSelectionViewModel.TotalCount"/> 是
    ///   "当前可见项数"(全选的基数), 两者语义完全不同, 撞名会静默遮住基类成员。
    /// </summary>
    public int FolderTotalCount => _totalCount;

    public bool CanGoPrev => !IsPaging && !Busy && CurrentPage > 1;
    public bool CanGoNext => !IsPaging && !Busy && TotalPages > 0 && CurrentPage < TotalPages;

    /// <summary>正在翻页(页码栏显示小转圈, 并暂时禁点, 防止连点)</summary>
    public bool IsPaging
    {
        get => _isPaging;
        private set
        {
            if (!SetProperty(ref _isPaging, value)) return;
            OnPropertyChanged(nameof(CanGoPrev));
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(CanChangePage));
            OnPropertyChanged(nameof(ShowLoadingOverlay));
            OnPropertyChanged(nameof(ShowNoMatch));
        }
    }

    /// <summary>页码数字是否可点(翻页在途 / 批量操作期间禁用)</summary>
    public bool CanChangePage => !IsPaging && !Busy;

    /// <summary>只有一页时页码栏没有意义, 直接收起</summary>
    public bool ShowPager => TotalPages > 1;

    public ICommand ReloadCommand { get; }
    public ICommand SelectFolderCommand { get; }
    public ICommand CreateFolderCommand { get; }
    public ICommand DeleteFolderCommand { get; }
    public ICommand ToggleEditCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand RemoveSelectedCommand { get; }
    public ICommand ClearFolderCommand { get; }
    public ICommand GoToPageCommand { get; }
    public ICommand PrevPageCommand { get; }
    public ICommand NextPageCommand { get; }

    /// <summary>
    /// 附加到视频卡片右键菜单里的"移出收藏"项。
    ///
    /// 为什么放进 ViewModel 而不是写在 XAML 里:
    /// 菜单项需要拿到"被右键的那张卡片"对应的 VideoItem 才能删除。菜单挂在卡片上时,
    /// 卡片的 DataContext 就是那个 VideoItem, 所以命令的参数由卡片侧注入即可 ——
    /// 比在代码后置里靠 sender 反查更直接、也不依赖可视树结构。
    /// </summary>
    public ObservableCollection<CardMenuItem> CardMenuItems { get; } = new();

    /// <summary>
    /// 预留: 「我的」页点收藏夹卡片时想打开的那个夹。
    /// 若页面还没做过首次加载, MainWindow 会先把要开的夹存在这里,
    /// LoadAsync 完成后优先选它而不是默认的第一夹 —— 否则用户点"化学"夹,
    /// 进收藏页看到的却是"默认收藏夹"。
    /// </summary>
    public FavFolder? PendingFolder { get; set; }

    public FavoritesViewModel()
    {
        _searchDebounce = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            _ = ApplySearchAsync();
        };

        CardMenuItems.Add(new CardMenuItem
        {
            Header = "移出收藏",
            Glyph = "\uE74D",
            Command = new RelayCommand(p =>
            {
                if (p is VideoItem v) Remove(v);
            })
        });

        ReloadCommand = new RelayCommand(() => _ = LoadAsync());
        SelectFolderCommand = new RelayCommand(p =>
        {
            if (p is FavFolder f) _ = SelectFolderAsync(f);
        });
        CreateFolderCommand = new AsyncRelayCommand(CreateFolderAsync);
        DeleteFolderCommand = new AsyncRelayCommand(DeleteCurrentFolderAsync);

        ToggleEditCommand = new RelayCommand(() =>
        {
            if (Busy) return;
            IsEditMode = !IsEditMode;
        });

        SelectAllCommand = new RelayCommand(() =>
        {
            if (Busy) return;
            // 只作用于**当前可见项** —— 分页之后就是当前页
            SetSelectionBulk(VisibleItems(), !AllSelected);
        });

        RemoveSelectedCommand = new AsyncRelayCommand(RemoveSelectedAsync);
        ClearFolderCommand = new AsyncRelayCommand(ClearFolderAsync);

        // 页码栏(与 UP 主主页同一套命令形状)
        PrevPageCommand = new AsyncRelayCommand(() => GoToPageAsync(CurrentPage - 1));
        NextPageCommand = new AsyncRelayCommand(() => GoToPageAsync(CurrentPage + 1));
        GoToPageCommand = new RelayCommand(p =>
        {
            var page = p switch
            {
                int i => i,
                string s when int.TryParse(s, out var n) => n,
                _ => 0
            };
            if (page > 0) _ = GoToPageAsync(page);
        });

        HookSelectionSource();

        // 空状态按钮的显隐跟着集合走(加载完成/重建列表都会改它)
        Folders.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasNoFolders));
            OnPropertyChanged(nameof(ShowEmptyCreateButton));
        };
    }

    // ------------------------------------------------------------ 选择状态
    // 下列实现(选中态派生属性、批量改选、集合增删时挂/退每一条的 PropertyChanged)
    // 全部来自基类 VideoListSelectionViewModel; 这里只留收藏页自己特有的部分。

    protected override ObservableCollection<VideoItem> SelectionSource => Items;

    /// <summary>集合增删时顺带刷一次顶栏的收藏夹条数(本页面特有, 另两个列表页没有)</summary>
    protected override void OnSourceItemsChanged()
    {
        UpdateCount();
        OnPropertyChanged(nameof(ShowLoadingOverlay));
        OnPropertyChanged(nameof(ShowNoMatch));
    }

    // ------------------------------------------------------------ 加载

    /// <summary>
    /// 加载: 先取收藏夹列表, 再自动打开第一个收藏夹。
    /// 默认落在第一个夹上是有意的 —— 进入收藏页就该立刻看到内容,
    /// 而不是面对一片空白等用户去点文件夹。
    /// </summary>
    public async Task LoadAsync()
    {
        if (!Svc.Session.HasLogin)
        {
            Error = "云端收藏需要登录后查看";
            return;
        }
        if (Loading) return;

        Loading = true;
        Error = "";
        Folders.Clear();
        Items.Clear();
        _currentFolderId = 0;
        _currentFolder = null;
        IsEditMode = false;
        ResetPaging();
        UpdateCount();
        OnPropertyChanged(nameof(CanDeleteFolder));
        OnPropertyChanged(nameof(DeleteFolderToolTip));

        var (ok, err, folders) = await Svc.Api.GetFavFoldersAsync();

        if (!ok || folders == null)
        {
            Loading = false;
            Error = err ?? "云端收藏夹加载失败";
            return;
        }

        // 记下拉取时的"收藏夹集合版本": 别处新建收藏夹后本页据此判断要不要重拉
        _foldersVersion = FavFolderSync.Version;
        _foldersDirty = false;

        if (folders.Count == 0)
        {
            Loading = false;
            Error = "账号下还没有创建收藏夹";
            return;
        }

        foreach (var f in folders) Folders.Add(f);

        // 默认打开第一个收藏夹(B 站返回顺序里第一个就是"默认收藏夹");
        // 「我的」页预留了要开的夹时, 优先选那个
        var target = PendingFolder != null
            ? Folders.FirstOrDefault(f => f.Id == PendingFolder.Id) ?? Folders[0]
            : Folders[0];
        PendingFolder = null;

        // ★ 第一页没回来之前不收 Loading: 否则会先闪一下"空收藏夹"再出现卡片。
        await SelectFolderAsync(target);
        Loading = false;

        // 整页加载期间用户已经在搜索框里打字了: 现在才轮到这次搜索
        if (_searchPending)
        {
            _searchPending = false;
            await ApplySearchAsync();
        }
    }

    // ------------------------------------------------------------ 新建收藏夹

    /// <summary>
    /// 新建收藏夹(2026-11-19): 弹窗 → 接口 → 重拉列表 → 自动选中新夹。
    ///
    /// ★ 为什么建完还要重拉一次列表: <c>created/list-all</c> 带着封面/顺序/媒体数,
    ///   比接口返回的那一颗更完整; 建夹是低频动作, 多一次请求换"列表一定是对的"很划算。
    /// ★ 但如果列表接口还没把新夹带出来(服务端偶有延迟), 就把返回的那一颗补进去先上屏,
    ///   并打上"脏"标记, 下次进页面再校准 —— 不能因为一次时序问题让用户以为没建成。
    /// ★ 顺带把 Error 清掉: 用户可能正是在"账号下还没有创建收藏夹"的空状态里点的新建。
    /// </summary>
    private async Task CreateFolderAsync()
    {
        if (CreatingFolder) return;
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("新建收藏夹需要先登录"); return; }

        CreatingFolder = true;
        try
        {
            var created = await FavFolderActions.CreateAsync(SvcWindow.Main);
            if (created == null) return;   // 取消 / 失败, 提示已由 FavFolderActions 发过

            var (ok, err, folders) = await Svc.Api.GetFavFoldersAsync();
            if (!ok || folders == null)
            {
                // 夹其实已经建好了, 只是列表没刷新出来: 不破坏当前界面, 下次进页面再拉
                Svc.Toast.Show("收藏夹已创建, 但列表刷新失败: " + (err ?? "未知错误"));
                _foldersDirty = true;
                return;
            }

            // 服务端列表偶尔有延迟, 还没把刚建的夹带出来: 先把接口返回的这一颗补进去,
            // chip 条立刻能看到新夹; 同时打脏标记, 下次进页面再拉权威列表校准封面/顺序。
            var includesCreated = created.Id > 0 && folders.Any(f => f.Id == created.Id);
            if (!includesCreated && created.Id > 0)
            {
                folders.Add(created);
                _foldersDirty = true;
            }
            else
            {
                _foldersDirty = false;
            }
            _foldersVersion = FavFolderSync.Version;

            if (folders.Count == 0)
            {
                Svc.Toast.Show("收藏夹已创建, 但列表刷新失败: 没拿到任何收藏夹");
                _foldersDirty = true;
                return;
            }

            // 选中新夹: 优先按接口返回的 id, 没有 id 时按标题, 再退到最后一个(新建夹通常排在末尾)
            var target = (created.Id > 0 ? folders.FirstOrDefault(f => f.Id == created.Id) : null)
                         ?? folders.FirstOrDefault(f => string.Equals(f.Title, created.Title, StringComparison.Ordinal))
                         ?? folders[^1];

            // ★ 重建集合而不是逐条 diff: 这是用户主动触发的低频操作, 重建简单且一定与
            //   服务端一致; 高亮由下面的 SelectFolderAsync 重新推上去(它会清掉旧的 IsCurrent)。
            //   chip 容器会重建一次 —— 只在"新建之后"发生, 不像删除那样频繁, 可以接受。
            //   先清 Error 再动集合, 免得清空的这一拍让空状态面板闪一下。
            Error = "";
            Folders.Clear();
            foreach (var f in folders) Folders.Add(f);

            await SelectFolderAsync(target);
        }
        finally
        {
            CreatingFolder = false;
        }
    }

    // ------------------------------------------------------------ 删除收藏夹

    /// <summary>
    /// 删除当前收藏夹(2026-11-19): 确认 → 接口 → 整页重载。
    ///
    /// ★ 默认收藏夹在这里就拦下(按钮本来也是禁用的, 防的是快捷键/程序调用这条边角路)。
    /// ★ 删除成功后必须重载而不是本地移除: 被删的正是当前夹, 分页/总数/当前选中都得换人;
    ///   LoadAsync 会落到第一个夹(默认收藏夹), 语义正好。
    /// </summary>
    private async Task DeleteCurrentFolderAsync()
    {
        var folder = _currentFolder;
        if (folder == null || DeletingFolder) return;
        if (folder.IsDefault) { Svc.Toast.Show("默认收藏夹不能删除"); return; }
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("删除收藏夹需要先登录"); return; }

        DeletingFolder = true;
        try
        {
            if (!await FavFolderActions.DeleteAsync(folder)) return;
            await LoadAsync();
        }
        finally
        {
            DeletingFolder = false;
        }
    }

    /// <summary>
    /// 别处(播放器收藏勾选窗 / 「我的」页)新建或删除了收藏夹之后, 本页下次显示时补一次重拉。
    /// 版本号没变、也没有脏标记时直接返回 —— 不能每次切页都重拉, 那会让收藏页每次进来都闪一下。
    /// ★ 重拉时用 PendingFolder 保留当前选中的夹: 用户只是去播放器建了个夹回来,
    ///   不该被甩回第一个收藏夹。
    /// </summary>
    public async Task RefreshFoldersIfStaleAsync()
    {
        if (!_foldersDirty && _foldersVersion == FavFolderSync.Version) return;
        if (Loading || !Svc.Session.HasLogin) return;

        PendingFolder = Folders.FirstOrDefault(f => f.Id == _currentFolderId);
        await LoadAsync();
    }

    /// <summary>切换收藏夹并加载其第一页</summary>
    public async Task SelectFolderAsync(FavFolder folder)
    {
        if (folder == null) return;
        _currentFolderId = folder.Id;
        _currentFolder = folder;
        CurrentFolderTitle = folder.Title;
        OnPropertyChanged(nameof(CurrentFolderTitle));
        OnPropertyChanged(nameof(CanDeleteFolder));
        OnPropertyChanged(nameof(DeleteFolderToolTip));

        // ★ 换夹时**保留搜索词**, 用它在新夹里重新搜一次(2026-11-20):
        //   清掉输入框里的字会让人以为"搜索被取消", 而显示全量列表又与输入框里的词矛盾。
        //   停掉尚在防抖中的那一跳, 直接以当前文本为准。
        _searchDebounce.Stop();
        _activeKeyword = _searchText.Trim();
        OnPropertyChanged(nameof(HasSearch));
        OnPropertyChanged(nameof(ShowNoMatch));

        // chip 高亮跟着数据走: 用户点击时 RadioButton 自己会选上, 但"程序切夹"
        // (我的页点卡片 / PendingFolder)不经过点击, 高亮必须在这里同步,
        // 否则就是"点了 A、视频是 A 的、高亮却停在 B"(2026-09-26 用户实测踩中)。
        foreach (var f in Folders)
        {
            if (f.IsCurrent) f.IsCurrent = false;
        }
        folder.IsCurrent = true;

        // 换夹时退出编辑模式: 上一夹的勾选对新夹没有意义
        IsEditMode = false;

        Items.Clear();
        ResetPaging();
        UpdateCount();

        // ★ 权威总数在收藏夹列表里就有(media_count), 不必等内容回来才知道页数 ——
        //   于是页码栏可以立刻按真实页数画出来, 而不是先猜一个再改。
        SetTotal(folder.MediaCount);
        RecalcPages(1);

        await LoadPageAsync(1);

        // 换夹 = 换一整套内容: 回到顶部。否则会停在上一个夹的滚动位置,
        // 看到的是新夹中间的一截(页码却是第 1 页)。
        ScrollToTopRequested?.Invoke();
    }

    // ------------------------------------------------------------ 搜索(2026-11-20)

    /// <summary>
    /// 执行一次关键词搜索(整个收藏夹范围, 走服务端 keyword)。
    ///
    /// ★ 不清空 Items: 保留上一批卡片直到新结果回来, 避免每次输入都闪一下整页大菊花;
    ///   返回 0 条时由 <see cref="ShowNoMatch"/> 给一行轻提示。
    /// ★ 页码 / 总数按**命中数**重算 —— 接口带 keyword 时 info.media_count 就是命中数
    ///   (实测, 见 ApiClient.GetFavResourcesAsync), 所以分页栏不用特殊处理。
    /// </summary>
    private async Task ApplySearchAsync()
    {
        if (_currentFolderId <= 0 || Loading)
        {
            _searchPending = true;
            return;
        }

        var keyword = _searchText.Trim();
        if (keyword == _activeKeyword) return;

        _activeKeyword = keyword;
        OnPropertyChanged(nameof(HasSearch));
        OnPropertyChanged(nameof(NoMatchText));
        OnPropertyChanged(nameof(ShowNoMatch));

        // 结果集换了, 上一批的勾选/编辑态没有意义
        IsEditMode = false;
        // 页码归 1、总数清零; 让在途的翻页作废(_loadGen 自增)
        ResetPaging();

        await LoadPageAsync(1);
        ScrollToTopRequested?.Invoke();
    }

    /// <summary>静默清空搜索(不触发搜索请求): 收藏夹被清空 / 删空之后, 搜索词已经无处可搜。</summary>
    private void ClearSearchQuietly()
    {
        _searchDebounce.Stop();
        _activeKeyword = "";
        _searchPending = false;
        if (_searchText.Length == 0) return;
        _searchText = "";
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(HasSearch));
        OnPropertyChanged(nameof(NoMatchText));
        OnPropertyChanged(nameof(ShowNoMatch));
    }

    // ------------------------------------------------------------ 分页

    /// <summary>清空分页状态(重载 / 切夹共用)</summary>
    private void ResetPaging()
    {
        // 让在途请求作废: 否则旧夹的响应回来会把内容填进新夹
        _loadGen++;
        _isPaging = false;
        _currentPage = 1;
        _totalPages = 0;
        _totalCount = 0;
        PageItems.Clear();

        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(FolderTotalCount));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ShowPager));
        OnPropertyChanged(nameof(IsPaging));
        OnPropertyChanged(nameof(CanGoPrev));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanChangePage));
    }

    /// <summary>写总条数并重算总页数(同时刷新顶栏的"(N)")</summary>
    private void SetTotal(int total)
    {
        if (total < 0) total = 0;
        if (_totalCount == total && _totalPages == PagerMath.TotalPages(total, PageSize)) return;

        _totalCount = total;
        OnPropertyChanged(nameof(FolderTotalCount));
        OnPropertyChanged(nameof(CountText));

        TotalPages = PagerMath.TotalPages(_totalCount, PageSize);
    }

    /// <summary>
    /// 加载指定页并**替换**当前列表(不是追加 —— 这是"页码分页"与"无限滚动"的分界)。
    ///
    /// ★ 用代次(_loadGen)丢弃过期响应: 用户快速点 1→2→3 时, 如果 1 的响应最后才回来,
    ///   列表会被错误地盖回第 1 页的内容, 而页码栏显示的却是 3。
    /// </summary>
    /// <param name="allowFallback">
    /// 允许"本页 0 条 ⇒ 判定页码越界并回退一页"。只允许回退一次, 防止接口异常时无限递归。
    /// </param>
    /// <param name="raiseScrollToTop">
    /// 成功后是否请求页面滚回顶部。换页时 true; "删除后原地重取"时 false ——
    /// 那时内容只是往前挪了几张, 把用户拽回顶部反而丢掉了观看位置。
    /// </param>
    /// <returns>这一页是否加载成功(失败时调用方负责回滚页码高亮)。</returns>
    private async Task<bool> LoadPageAsync(int page, bool allowFallback = true, bool raiseScrollToTop = true)
    {
        if (_currentFolderId <= 0) return false;
        if (page < 1) page = 1;
        if (TotalPages > 0 && page > TotalPages) page = TotalPages;

        var gen = ++_loadGen;
        var keyword = _activeKeyword;   // 钉住这一代的关键词(请求途中换词/换夹都靠 gen 作废)
        IsPaging = true;
        try
        {
            var (ok, err, items, hasMore, total) =
                await Svc.Api.GetFavResourcesAsync(_currentFolderId, page, PageSize, keyword);

            if (gen != _loadGen) return false;   // 已经有更新的一次请求接管了, 这份结果作废

            if (!ok || items == null)
            {
                // ★ 翻页失败**不要**用整页错误盖掉列表: 用户只想从第 46 页跳到 47 页,
                //   一次请求失败就让他眼前一黑、连页码栏都没了(只能整页刷新回到第 1 页),
                //   代价远大于收益。翻页失败改为 Toast 说明 + 保留原页内容 + 回滚高亮;
                //   只有"第 1 页都拿不到"才走整页错误态(那时确实没有内容可显示)。
                if (page > 1)
                {
                    Svc.Toast.Show($"第 {page} 页加载失败: {err ?? "未知错误"}");
                    return false;
                }
                Error = err ?? "收藏夹内容加载失败";
                return false;
            }

            // ★★ 越界页兜底(实测 pn 超过末页时: code=0 / medias=null / media_count=0):
            //   "本页一条都没有, 但夹子里明明有内容" ⇒ 这个页码不存在。回退一页重取,
            //   而不是给用户看一页空白 + 一句"这个收藏夹还是空的"。
            if (items.Count == 0 && _totalCount > 0 && page > 1 && allowFallback)
            {
                var fallback = Math.Max(1, page - 1);
                TotalPages = fallback;
                RecalcPages(fallback);
                return await LoadPageAsync(fallback, allowFallback: false, raiseScrollToTop);
            }

            // 换页 = 整页替换。按 bvid 去重只防"同一页里重复下发"(分页边界偶尔会重复)。
            Items.Clear();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var it in items)
            {
                if (string.IsNullOrEmpty(it.Bvid) || !seen.Add(it.Bvid)) continue;
                Items.Add(it);
            }

            CurrentPage = page;
            // ★ 只有真有数据时才采纳总条数: 越界页会把 media_count 报成 0(见上)。
            if (total > 0) SetTotal(total);
            else if (items.Count == 0) SetTotal(0);   // 第 1 页就空: 夹子确实是空的

            // hasMore 只是"接口认为还有下一页"的旁证; 页数以 media_count 为准,
            // 兜底用: 总数拿不到时(0)至少按 has_more 让"下一页"可用。
            if (TotalPages <= 1 && hasMore) TotalPages = page + 1;

            RecalcPages(page);
            // ★ 搜索无结果不是"收藏夹为空" —— 给空字符串, 由 ShowNoMatch 那个轻量提示接管,
            //   不能把整页错误面板(带「重试」)盖上来。
            Error = Items.Count == 0 ? (keyword.Length > 0 ? "" : "这个收藏夹还是空的") : "";

            if (page > 1 && raiseScrollToTop) ScrollToTopRequested?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            if (gen == _loadGen)
            {
                if (page > 1) Svc.Toast.Show($"第 {page} 页加载失败: {ex.Message}");
                else Error = ex.Message;
            }
            return false;
        }
        finally
        {
            if (gen == _loadGen) IsPaging = false;
        }
    }

    /// <summary>翻到指定页。失败时把页码高亮回滚, 不会停在错误的高亮上。</summary>
    private async Task GoToPageAsync(int page)
    {
        if (IsPaging || Busy) return;
        if (page < 1) return;
        if (TotalPages > 0 && page > TotalPages) return;
        if (page == CurrentPage) return;

        // ★ 先把高亮移到目标页, 让用户**立刻**看到"点到了"(与 UP 主主页同一套处理)。
        //   请求失败时再回滚。
        var previous = CurrentPage;
        CurrentPage = page;
        RecalcPages(page);

        if (!await LoadPageAsync(page))
        {
            CurrentPage = previous;
            RecalcPages(previous);
        }
    }

    /// <summary>按当前总页数重建页码序列(0 表示省略号)</summary>
    private void RecalcPages(int current)
    {
        PageItems.Clear();
        if (TotalPages <= 1) return;
        foreach (var p in PageBar.Build(current, TotalPages))
        {
            PageItems.Add(p == PageBar.Ellipsis
                ? new PageItem { IsEllipsis = true, Display = "…" }
                : new PageItem { Page = p, Display = p.ToString(), IsCurrent = p == current });
        }
    }

    /// <summary>
    /// 删除之后重取当前页。
    /// ★ 为什么必须重取: 收藏夹是"按收藏时间倒序"的流, 删掉中间几条之后后面的内容会
    ///   前移到当前页 —— 只在本页做本地移除会留下"空洞", 下一页的边界也对不上。
    /// </summary>
    private async Task RefreshCurrentPageAsync()
    {
        if (_currentFolderId <= 0) return;
        var page = CurrentPage;
        // 不滚回顶部: 删除之后用户还想待在刚才那一屏附近
        await LoadPageAsync(page, raiseScrollToTop: false);
        if (Items.Count == 0 && page > 1) await GoToPageAsync(page - 1);
    }

    private void UpdateCount() => OnPropertyChanged(nameof(CountText));

    /// <summary>把当前收藏夹在本地的 media_count 减掉已移出的条数(顶栏的"(N)"跟着变)</summary>
    private void ShrinkFolderCount(int removed)
    {
        if (removed <= 0) return;
        var folder = Folders.FirstOrDefault(f => f.Id == _currentFolderId);
        // ★ FavFolder.MediaCount 自己会通知 DisplayText 刷新, 这里**不要**去动 Folders 集合:
        //   移除再插回来会重建 RadioButton 容器, 把收藏夹标签的选中高亮弄丢。
        if (folder != null) folder.MediaCount = System.Math.Max(0, folder.MediaCount - removed);
    }

    // ------------------------------------------------------------ 右键菜单: 单条移出

    /// <summary>右键菜单: 从当前收藏夹移出</summary>
    public void Remove(VideoItem item)
    {
        if (item == null || string.IsNullOrEmpty(item.Bvid)) return;
        _ = RemoveFromCloudAsync(item);
    }

    /// <summary>
    /// 调服务端接口取消收藏, 成功后再从列表移除。
    ///
    /// deal 接口只认 avid, 而部分列表来源(搜索/推荐)不返回 aid, 所以这里按需补查一次详情。
    /// 只从**当前**收藏夹移出而不是"从所有含它的夹里删": 用户在这个夹里点的右键,
    /// 期望就是"从我看的这个夹里拿掉", 顺手删掉别的夹反而会造成意外丢失。
    /// </summary>
    private async Task RemoveFromCloudAsync(VideoItem item)
    {
        if (_currentFolderId <= 0) return;

        var aid = await EnsureAidAsync(item);
        if (aid <= 0)
        {
            Svc.Toast.Show("无法获取视频 avid, 移出失败");
            return;
        }

        var (ok, err) = await Svc.Api.DealFavoriteAsync(aid, delIds: new[] { _currentFolderId });
        if (!ok)
        {
            Svc.Toast.Show("移出收藏失败: " + (err ?? "未知错误"));
            return;
        }
        Items.Remove(item);
        UpdateCount();
        ShrinkFolderCount(1);
        // 总条数少了一条 ⇒ 总页数可能要少一页(删到末页最后一条时)
        SetTotal(Math.Max(0, _totalCount - 1));
        RecalcPages(CurrentPage);

        // ★ 本页被删空(只可能发生在末页只剩一条时): 退回上一页, 否则会停在一张空页上。
        //   这里只在"真的空了"时补一次请求 —— 普通情况不做重取, 右键移出保持即时反馈。
        if (Items.Count == 0 && CurrentPage > 1) await RefreshCurrentPageAsync();

        Svc.Toast.Show("已移出收藏");
    }

    /// <summary>拿到条目对应的 avid; 列表没带 aid 就按 bvid 补查一次详情</summary>
    private async Task<long> EnsureAidAsync(VideoItem item)
    {
        if (item.Aid > 0) return item.Aid;
        var (_, _, detail) = await Svc.Api.GetVideoAsync(item.Bvid);
        if (detail != null && detail.Aid > 0)
        {
            item.Aid = detail.Aid; // 回填, 免得同一条反复补查
            return detail.Aid;
        }
        return 0;
    }

    // ------------------------------------------------------------ 编辑模式: 批量移出

    /// <summary>
    /// 把选中的条目批量移出当前收藏夹。
    ///
    /// 走 /x/v3/fav/resource/batch-del, 一次请求可以带多个 `avid:2`,
    /// 所以这里按 20 个一批发 —— 既不用逐条请求, 也不会把 URL 撑得过大。
    /// </summary>
    private async Task RemoveSelectedAsync()
    {
        if (Busy) return;
        if (_currentFolderId <= 0) return;

        var targets = VisibleItems().Where(x => x.IsSelected).ToList();
        if (targets.Count == 0)
        {
            Svc.Toast.Show("还没有选择要移出的内容");
            return;
        }

        if (!ConfirmDialog.Show(
                $"确定要把选中的 {targets.Count} 个视频移出「{CurrentFolderTitle}」吗?",
                "移出收藏夹", "移出"))
            return;

        Busy = true;
        var removed = 0;
        var fail = 0;
        try
        {
            // 逐条补 avid(列表接口通常会带上, 少数情况为 0)
            var withAid = new List<VideoItem>();
            for (var i = 0; i < targets.Count; i++)
            {
                BusyText = $"正在准备 {i + 1}/{targets.Count}…";
                if (await EnsureAidAsync(targets[i]) > 0) withAid.Add(targets[i]);
                else fail++;
            }

            var batches = withAid.Chunk(20).ToList();
            for (var b = 0; b < batches.Count; b++)
            {
                BusyText = $"正在移出 {removed}/{withAid.Count}…";
                var batch = batches[b];
                var (ok, err) = await Svc.Api.BatchDelFavResourcesAsync(_currentFolderId, batch.Select(x => x.Aid));
                if (!ok)
                {
                    fail += batch.Length;
                    continue;
                }
                foreach (var it in batch)
                {
                    Items.Remove(it);
                    removed++;
                }
            }
        }
        finally
        {
            Busy = false;
            BusyText = "";
        }

        UpdateCount();
        ShrinkFolderCount(removed);
        SetTotal(Math.Max(0, _totalCount - removed));
        RaiseSelection();

        if (fail == 0) Svc.Toast.Show($"已移出 {removed} 个视频");
        else if (removed == 0) Svc.Toast.Show("移出失败, 请稍后重试");
        else Svc.Toast.Show($"已移出 {removed} 个, {fail} 个失败");

        // ★ 批量移出之后后面的内容会前移到本页, 必须重取当前页(见 RefreshCurrentPageAsync)。
        //   本页被删空且不是第一页时会自动退回上一页。
        if (removed > 0) await RefreshCurrentPageAsync();

        if (Items.Count == 0) IsEditMode = false;
    }

    /// <summary>
    /// 清空当前收藏夹(移出**全部**内容, 不只是当前页那几条)。
    ///
    /// 做法是"反复取第 1 页 → 批量移出 → 再取第 1 页": 每批删掉之后后面的内容会自动补到第 1 页,
    /// 所以循环几轮就能把整个夹掏空, 不需要先把全部条目拉进内存。
    /// 两个保险: 硬上限轮数, 以及"这一轮和上一轮拿到的内容完全一样就停" ——
    /// 后者防的是"删除请求返回成功但服务端其实没删"时死循环。
    /// </summary>
    private async Task ClearFolderAsync()
    {
        if (Busy) return;
        if (_currentFolderId <= 0) return;

        if (!ConfirmDialog.Show(
                $"确定要清空收藏夹「{CurrentFolderTitle}」吗?\n\n" +
                "夹内全部内容都会被移出(不只是当前页已显示的), 且无法撤销。",
                "清空收藏夹", "清空"))
            return;

        const int MaxRounds = 80;   // 每轮最多 30 条 → 上限约 2400 条, 远超正常收藏夹规模
        Busy = true;
        var removed = 0;
        string? lastSig = null;
        var stopped = "";
        try
        {
            for (var round = 0; round < MaxRounds; round++)
            {
                var (ok, err, items, _, _) = await Svc.Api.GetFavResourcesAsync(_currentFolderId, 1, 30);
                if (!ok || items == null)
                {
                    stopped = err ?? "读取收藏夹内容失败";
                    break;
                }

                var batch = items.Where(x => x.Aid > 0).ToList();
                if (batch.Count == 0) break;   // 空了, 收工

                // 和上一轮拿到的是同一批 → 说明删了没生效, 再循环下去就是死循环
                var sig = string.Join(",", batch.Select(x => x.Aid));
                if (sig == lastSig)
                {
                    stopped = "服务端没有真正删除内容, 已停止";
                    break;
                }
                lastSig = sig;

                BusyText = $"正在清空, 已移出 {removed} 个…";
                var (ok2, err2) = await Svc.Api.BatchDelFavResourcesAsync(_currentFolderId, batch.Select(x => x.Aid));
                if (!ok2)
                {
                    stopped = err2 ?? "移出失败";
                    break;
                }
                removed += batch.Count;
            }
        }
        finally
        {
            Busy = false;
            BusyText = "";
        }

        Items.Clear();
        UpdateCount();
        ShrinkFolderCount(removed);
        IsEditMode = false;
        // 夹子被掏空: 搜索框里的词也没东西可搜了, 一并静默清掉
        ClearSearchQuietly();

        // 夹子被掏空 ⇒ 分页状态一起归零(否则页码栏还挂着一个不存在的页)
        // ★ 用 _loadGen 让"清空途中还在飞的翻页请求"作废, 不然它回来会往空列表里灌内容。
        ResetPaging();

        if (stopped.Length > 0 && removed == 0)
        {
            Error = "清空失败: " + stopped;
            return;
        }

        Error = "这个收藏夹还是空的";
        Svc.Toast.Show(stopped.Length > 0
            ? $"已移出 {removed} 个, 但中途停止: {stopped}"
            : $"已清空「{CurrentFolderTitle}」, 共移出 {removed} 个");
    }
}
