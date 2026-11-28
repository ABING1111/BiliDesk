using System;
using System.IO;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;
using BiliDesk.Views.Windows;

namespace BiliDesk.ViewModels;

/// <summary>设置页 ViewModel: 外观 + 账户 + 数据 + 关于</summary>
public class SettingsViewModel : ObservableObject
{
    private bool _isLoggedIn;
    private string _userName = "";
    private string _userFace = "";
    private int _userLevel;
    private double _userCoins;
    private string _cacheSizeText = "计算中…";
    private string _videoCacheSizeText = "计算中…";

    public bool IsLoggedIn
    {
        get => _isLoggedIn;
        private set
        {
            if (!SetProperty(ref _isLoggedIn, value)) return;
            // 免登录 1080P 的副标题跟着登录态走(见 NoLogin1080PHint) ——
            // 登录/退出后不刷它, 用户会看到"已登录"却还写着"未登录也能看 1080P"。
            OnPropertyChanged(nameof(NoLogin1080PHint));
        }
    }
    public string UserName { get => _userName; private set => SetProperty(ref _userName, value); }
    public string UserFace { get => _userFace; private set => SetProperty(ref _userFace, value); }
    public int UserLevel { get => _userLevel; private set => SetProperty(ref _userLevel, value); }
    public double UserCoins { get => _userCoins; private set => SetProperty(ref _userCoins, value); }
    public string CacheSizeText { get => _cacheSizeText; private set => SetProperty(ref _cacheSizeText, value); }

    /// <summary>短视频下载缓存(1080P 本地合流的产物)的占用</summary>
    public string VideoCacheSizeText
    {
        get => _videoCacheSizeText;
        private set => SetProperty(ref _videoCacheSizeText, value);
    }

    /// <summary>
    /// 自动清理规则。直接读 ShortVideoCache 里的常量拼出来 ——
    /// 界面上写的规则必须和 Prune 真正在用的阈值是同一个数, 否则就是骗用户。
    /// </summary>
    public string VideoCacheHint =>
        $"位置: {ShortVideoCache.Dir}\n" +
        $"自动清理: 超 {ShortVideoCache.MaxAge.TotalDays:0} 天 / {ShortVideoCache.MaxFiles} 个 / " +
        $"{FormatBytes(ShortVideoCache.MaxTotalBytes)} 时按最久没播放过的先删 (启动时也会清一次)";

    /// <summary>
    /// 封面图片缓存的自动清理规则。与 <see cref="VideoCacheHint"/> 同款: 直接读
    /// <see cref="CoverCachePrune"/> 里的常量, 界面上写的必须和真在跑的是同一个数。
    /// </summary>
    public string CoverCacheHint =>
        $"位置: {AppPaths.CacheDir}\n" +
        $"自动清理: 超 {CoverCachePrune.MaxAge.TotalDays:0} 天 / {CoverCachePrune.MaxFiles} 个 / " +
        $"{FormatBytes(CoverCachePrune.MaxTotalBytes)} 时按最久未使用的先删 (启动时也会清一次)";

    // ------------------------------------------------------------ 下载/缓存位置

    /// <summary>
    /// 当前**实际生效**的下载位置(下载的视频 / 图片另存为都存这里)。
    /// ★ 读 AppPaths.DownloadDir 而不是 Svc.Settings.DownloadDir: 设置里存的是"用户填了什么",
    ///   而这里要显示的是"真的落在哪" —— 用户填了非法路径(比如拔掉的移动盘)时两者会不同,
    ///   显示实际值才能让他看出"我那个填错了/盘没插"。
    /// ★ 界面上叫"缓存位置"是沿用用户的说法(离线缓存页也是这个说法), 实际语义是下载目录。
    /// </summary>
    public string CacheLocation => AppPaths.DownloadDir;

    /// <summary>下载位置那一行的说明: 是否用了自定义位置</summary>
    public string CacheLocationHint => string.Equals(
        AppPaths.DownloadDir, AppPaths.DefaultDownloadDir, StringComparison.OrdinalIgnoreCase)
        ? "默认位置(系统「下载」文件夹)。可改到其它盘, 只影响以后下载的视频"
        : "自定义位置; 下载的视频与另存的图片都会存到这里";

    /// <summary>当前是否用的是自定义位置(决定"恢复默认"按钮是否可点)</summary>
    public bool IsCustomCacheLocation => !string.Equals(
        AppPaths.DownloadDir, AppPaths.DefaultDownloadDir, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 弹窗让用户选下载目录。
    ///
    /// ★ 用 .NET 8 自带的 <see cref="Microsoft.Win32.OpenFolderDialog"/>: 它就是系统原生的
    ///   选文件夹对话框(与 Windows 设置里选目录同款), 不需要引 WinForms 的 FolderBrowserDialog
    ///   (那是另一套老界面), 也不用自己 P/Invoke。
    /// ★ 已下载的视频**不会被搬走**: 那是用户的成品文件。改了位置只影响以后的新下载。
    /// </summary>
    private void PickCacheLocation()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择下载位置",
                // 打开时就落在当前下载目录上, 用户不用自己一路点过去
                InitialDirectory = AppPaths.DownloadDir,
                Multiselect = false
            };
            if (dialog.ShowDialog() != true) return;

            var effective = Svc.Settings.SetDownloadDir(dialog.FolderName);
            RefreshCacheLocation();
            Svc.Toast.Show($"下载位置已改为 {effective}");
        }
        catch (Exception ex)
        {
            Svc.Toast.Show("选择下载位置失败: " + ex.Message);
        }
    }

    /// <summary>把下载位置恢复成默认的系统「下载」文件夹</summary>
    private void ResetCacheLocation()
    {
        var effective = Svc.Settings.SetDownloadDir(null);
        RefreshCacheLocation();
        Svc.Toast.Show($"下载位置已恢复默认: {effective}");
    }

    /// <summary>在资源管理器里打开下载目录</summary>
    private void OpenCacheLocation()
    {
        try
        {
            var dir = AppPaths.DownloadDir;
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Svc.Toast.Show("打开下载目录失败: " + ex.Message);
        }
    }

    /// <summary>关于页外链(仓库 / Issues)走设置里的打开方式(默认内置浏览器, 也可改成系统浏览器)。
    /// LinkLauncher 已做同地址去重, 这里只需要把"没开起来"的情况反馈给用户。</summary>
    private static void OpenAboutLink(string url)
    {
        try
        {
            LinkLauncher.OpenInBrowser(url);
        }
        catch (Exception ex)
        {
            Svc.Toast.Show("打开链接失败: " + ex.Message);
        }
    }

    /// <summary>下载位置变化后, 重算这一行的绑定(内部缓存占用不受影响, 不必重量)</summary>
    private void RefreshCacheLocation()
    {
        OnPropertyChanged(nameof(CacheLocation));
        OnPropertyChanged(nameof(CacheLocationHint));
        OnPropertyChanged(nameof(IsCustomCacheLocation));
        // "恢复默认"的可用性跟着 IsCustomCacheLocation 变 —— 命令不会自己重查, 必须显式喊
        _resetCacheLocationCommand?.RaiseCanExecuteChanged();
    }
    /// <summary>
    /// 「关于」卡片里软件名下面那一行的副标题: 只写技术栈。
    /// 不在这里重复版本号 —— 卡片下方本来就有独立的「版本号」一栏,
    /// 两处都写版本会显得啰嗦, 而且改版本时要同步的地方多一处。
    /// </summary>
    public string RuntimeInfo => ".NET 10 / WPF";

    /// <summary>「关于 → 版本号」一栏右侧显示的版本(与 App.AppVersion 单一来源, 别在这里写死)</summary>
    public string VersionNumber => "v" + App.AppVersion;

    // ----------------- 顶部 Tab(2026-10-03 重做) -----------------

    private int _tabIndex;

    /// <summary>当前选中 Tab 的序号(0=通用 1=播放 2=快捷键 3=数据管理 4=关于)。
    /// 页面用它切内容; 五个 bool 属性是给 XAML 胶囊 Tab 双向绑定的投影。</summary>
    public int TabIndex
    {
        get => _tabIndex;
        set
        {
            if (!SetProperty(ref _tabIndex, value)) return;
            OnPropertyChanged(nameof(IsGeneralTab));
            OnPropertyChanged(nameof(IsPlaybackTab));
            OnPropertyChanged(nameof(IsShortcutTab));
            OnPropertyChanged(nameof(IsDataTab));
            OnPropertyChanged(nameof(IsAboutTab));
        }
    }

    /// <summary>
    /// Tab 投影。★ **只写 true**: RadioButton 组里的一个被勾上时, WPF 会先把**上一个**
    /// Radio 的 IsChecked 置 false —— TwoWay 绑定把 false 也写进来, 如果无条件执行
    /// TabIndex = 0, 刚切到的 Tab 会被立刻盖回去, 表现就是"五个 Tab 都点不动"。
    /// false 只来自"取消勾选", 目标 Tab 自己的 true 会跟着来, 忽略即可。
    /// </summary>
    public bool IsGeneralTab  { get => TabIndex == 0; set { if (value) TabIndex = 0; } }
    public bool IsPlaybackTab { get => TabIndex == 1; set { if (value) TabIndex = 1; } }
    public bool IsShortcutTab { get => TabIndex == 2; set { if (value) TabIndex = 2; } }
    public bool IsDataTab     { get => TabIndex == 3; set { if (value) TabIndex = 3; } }
    public bool IsAboutTab    { get => TabIndex == 4; set { if (value) TabIndex = 4; } }

    // ----------------- 设置项搜索(2026-11-28) -----------------
    //
    // 用户要求"设置页增加搜索功能, 按钮放在胶囊的右边默认收缩态"。
    // 形态与收藏页/历史页的吸顶搜索完全一致(Views/Controls/TabSearchBox),
    // 这里只管**搜什么、命中哪些**; "切 Tab + 滚到那一行 + 高亮"是页面的活
    // (它才拿得到可视树, 见 SettingsPage.OnLocateRequested)。
    //
    // ★ 索引在 Models/SettingsSearchCatalog 里: 纯静态、无网络, 52 条行 + 快捷键目录投影。
    //   每次输入遍历一遍即可, 不需要防抖 —— 也不该防抖: 本地几十条, 延迟只会让人觉得卡。

    private string _searchText = "";
    private readonly System.Collections.ObjectModel.ObservableCollection<SettingsSearchEntry> _searchHits = new();

    /// <summary>搜索框里的关键词(双向绑到 TabSearchBox)。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? "")) return;
            RebuildSearchHits();
        }
    }

    /// <summary>当前命中的设置项(右上角悬浮卡片的列表源)</summary>
    public System.Collections.ObjectModel.ObservableCollection<SettingsSearchEntry> SearchHits => _searchHits;

    /// <summary>有没有关键词(决定结果卡片显不显示)</summary>
    public bool HasSearch => SearchText.Trim().Length > 0;

    /// <summary>关键词有没有命中任何设置项</summary>
    public bool HasSearchHits => _searchHits.Count > 0;

    /// <summary>有词但零命中: 结果卡片里显示一句提示, 而不是整块消失(用户会以为没响应)</summary>
    public bool ShowSearchEmpty => HasSearch && _searchHits.Count == 0;

    /// <summary>结果卡片的标题行(只在有命中时显示)</summary>
    public string SearchSummaryText => $"找到 {_searchHits.Count} 项设置";

    /// <summary>点结果卡片里的一行</summary>
    public ICommand ActivateSearchHitCommand { get; }

    /// <summary>
    /// 请求页面把某条设置项滚到视野里并高亮。
    /// ★ 走事件而不是让 VM 直接碰可视树: VM 在设置页长期缓存、且完全没有 UI 引用;
    ///   页面在 OnLoaded 里订阅(见 SettingsPage)。
    /// </summary>
    public event Action<SettingsSearchEntry>? LocateRequested;

    private void RebuildSearchHits()
    {
        _searchHits.Clear();
        foreach (var hit in SettingsSearchCatalog.Search(_searchText))
            _searchHits.Add(hit);

        OnPropertyChanged(nameof(HasSearch));
        OnPropertyChanged(nameof(HasSearchHits));
        OnPropertyChanged(nameof(ShowSearchEmpty));
        OnPropertyChanged(nameof(SearchSummaryText));
    }

    /// <summary>
    /// 跳到一条命中项: 先清空关键词(结果卡片随之收起、搜索框收回圆), 再切 Tab 并请求定位。
    /// ★ 必须**先**清空: 否则结果卡片会盖在刚跳过去的那一行上, 用户看不到高亮。
    /// ★ 清空后 TabSearchBox 不会自己收回(它只在失焦/Esc/点外面时收), 由页面补一次
    ///   CollapseIfEmpty。
    /// </summary>
    public void ActivateSearchHit(SettingsSearchEntry? hit)
    {
        if (hit == null) return;
        SearchText = "";
        TabIndex = hit.TabIndex;
        LocateRequested?.Invoke(hit);
    }

    /// <summary>回车 = 跳到第一条命中项(没有命中就什么都不做, 让事件继续往下走)。</summary>
    public bool ActivateFirstSearchHit()
    {
        if (_searchHits.Count == 0) return false;
        ActivateSearchHit(_searchHits[0]);
        return true;
    }

    // ----------------- 主题色 -----------------

    /// <summary>
    /// 主题色(强调色)。读写设置并立刻重刷笔刷, 不用重启就生效。
    /// 「跟随系统」时返回系统强调色 —— 调色盘和 HEX 框显示的就是当前**实际生效**的颜色,
    /// 而不是"跟随系统"这个模式本身(模式由 <see cref="IsAccentSystem"/> 那个单选表示)。
    /// </summary>
    public string AccentColor
    {
        get => ThemeService.ToHex(Svc.Theme.Accent);
        set
        {
            // 跟随系统时不比较: 否则用户手打一个正好等于系统色的 HEX 会被当成"没变化",
            // 于是没法用它退出"跟随系统"。
            if (!Svc.Theme.IsAccentSystem &&
                string.Equals(value, AccentColor, StringComparison.OrdinalIgnoreCase)) return;
            Svc.Theme.SetAccent(value);
            RaiseAccentChanged();
        }
    }

    /// <summary>
    /// 调色盘双向绑定的那一份颜色。
    ///
    /// 为什么不直接把 <see cref="AccentColor"/> 给调色盘: 那个属性是字符串(存储格式),
    /// 拖动时每帧都要序列化一次; 更要紧的是拖到纯黑时 `#000000` 反推不出色相, 游标会自己跳
    /// (见 <see cref="Views.Controls.ColorPalette"/> 的类注释)。这里让调色盘直接持有 Color。
    ///
    /// ★ 拖动中不需要任何特殊处理(2026-11-08): 强调色笔刷是**共享实例**, 改 Color 后所有
    ///   引用它的元素在下一帧就变色, 没有"缓存页面还拿着旧色"这回事 —— 旧的
    ///   `settle: false` / `Settle()` 那一套全树 InvalidateProperty 已经随换字典方案一起删除。
    /// </summary>
    public Color AccentPickerColor
    {
        get => Svc.Theme.Accent;
        set
        {
            if (value == Svc.Theme.Accent) return;
            Svc.Theme.SetAccent(ThemeService.ToHex(value));
            RaiseAccentChanged();
        }
    }

    /// <summary>
    /// 调色盘开始 / 结束拖动。
    ///
    /// ★ 2026-11-08 起这两个回调**什么都不用做**: 强调色笔刷是共享可变实例, 拖动过程中每次
    ///   `SetAccent` 改 Color, 所有引用它的元素下一帧就跟着变, 不存在"要等松手才全量刷新"
    ///   的问题。旧的 `settle: false` + 松手补一次全树 InvalidateProperty 已随换字典方案删除。
    ///   保留这两个方法是因为 SettingsPage 已经把它们挂到调色盘的 DragStarted/DragEnded 上 ——
    ///   删掉签名会牵动 UI 代码, 而这里本来就没有副作用需要执行。
    /// </summary>
    public void BeginAccentDrag() { }

    public void EndAccentDrag() { }

    /// <summary>主题色用默认的 B 站粉(单选之一, 默认选中)</summary>
    public bool IsAccentDefault
    {
        get => Svc.Theme.IsDefaultAccent;
        set
        {
            // 单选按钮只会把选中项置 true。置 false 的那一次是"同组里被取消的旧项",
            // 必须忽略 —— 否则会先落一次默认色, 界面闪一下再变回目标色。
            if (!value || value == IsAccentDefault) return;
            // 落回空串 = "没自定义过", 而不是存 "#FB7299" —— 这样默认色以后调了会跟着变
            Svc.Theme.SetAccent("");
            RaiseAccentChanged();
        }
    }

    /// <summary>强调色跟随系统(单选之二)</summary>
    public bool IsAccentSystem
    {
        get => Svc.Theme.IsAccentSystem;
        set
        {
            if (!value || value == IsAccentSystem) return;
            Svc.Theme.SetAccent(ThemeService.AccentSystem);
            RaiseAccentChanged();
        }
    }

    /// <summary>强调色用自定义颜色(单选之三)。就是"既不是默认粉、也不是跟随系统"。</summary>
    public bool IsAccentCustom
    {
        get => !Svc.Theme.IsDefaultAccent && !Svc.Theme.IsAccentSystem;
        set
        {
            if (!value || value == IsAccentCustom) return;
            // 切到自定义: 拿**当前**颜色当起点, 用户想改再拖调色盘 ——
            // 直接跳回默认粉会让"我只是想微调一下当前色"变成"颜色整个变了"。
            Svc.Theme.SetAccent(ThemeService.ToHex(Svc.Theme.Accent));
            RaiseAccentChanged();
        }
    }

    /// <summary>
    /// 调色盘能不能用。
    /// 只有「跟随系统」时不可用 —— 那个颜色由 Windows 决定, 让用户拖了却改不动是骗人。
    /// 「B 站粉」下**保持可用**: 粉只是一个具体颜色, 用户想微调就拖, 一拖就自动变成「自定义」。
    /// </summary>
    public bool CanEditAccent => !Svc.Theme.IsAccentSystem;

    /// <summary>强调色是不是"没自定义过"(存储里空串 = 默认 B 站粉)</summary>
    public bool IsDefaultAccent => Svc.Theme.IsDefaultAccent;

    /// <summary>拖动调色盘 / 切来源之后, 一整组相关属性都要通知一遍</summary>
    private void RaiseAccentChanged()
    {
        OnPropertyChanged(nameof(AccentColor));
        OnPropertyChanged(nameof(AccentPickerColor));
        OnPropertyChanged(nameof(IsAccentDefault));
        OnPropertyChanged(nameof(IsAccentSystem));
        OnPropertyChanged(nameof(IsAccentCustom));
        OnPropertyChanged(nameof(CanEditAccent));
        OnPropertyChanged(nameof(IsDefaultAccent));
    }

    /// <summary>恢复默认的 B 站粉</summary>
    public ICommand ResetAccentCommand { get; }

    public AppTheme ThemeMode
    {
        get => Svc.Theme.Mode;
        set => Svc.Theme.SetMode(value);
    }

    /// <summary>颜色模式下拉框的投影(AppTheme 枚举 0=System 1=Light 2=Dark, 顺序即下拉顺序)。
    /// ComboBox 的 SelectedIndex 只吃 int, 枚举值本身按数字排列, 直接透传。</summary>
    public int ThemeModeInt
    {
        get => (int)Svc.Theme.Mode;
        set => ThemeMode = (AppTheme)value;
    }

    /// <summary>
    /// 下拉框的 ItemsSource(字符串)与选中投影。
    /// ★ 为什么不用 SelectedIndex + ComboBoxItem: ThemeService.Mode **没有**变化通知,
    ///   ComboBox 的 SelectedIndex 初始化读一次后, 主题在别处被改(跟随系统的自动逻辑)
    ///   下拉框不会跟着刷; 用"字符串列表 + SelectedItem"再靠 OnPropertyChanged 推,
    ///   至少能保证"打开页面就是当前值、点选就生效"。
    /// </summary>
    public string[] ThemeOptions { get; } = { "跟随系统", "浅色", "深色" };

    public string ThemeSelected
    {
        get => ThemeOptions[(int)Svc.Theme.Mode];
        set
        {
            var idx = Array.IndexOf(ThemeOptions, value);
            if (idx < 0 || (int)Svc.Theme.Mode == idx) return;
            ThemeMode = (AppTheme)idx;
            // ★ 必须显式通知: SelectedItem 的显示 = getter 的返回值, 但 TwoWay 回写时
            //   WPF 不会重新读一遍 —— 主题模式在 ThemeService 侧落定后, 这里不推通知的话
            //   方框里的文字保持空白/旧值(真机实证)。
            OnPropertyChanged();
        }
    }

    // ---------------- 窗口背景材质(2026-10-06 合并为四选一) ----------------
    //
    // ★★★ 为什么合并: 原来"亚克力背景"是个开关、云母/云母Alt 是它下面的子选项 ——
    //   这在语义上是错的(云母根本不是亚克力), 而且"关掉开关就选不了云母"很别扭。
    //   现在四种材质**平级**: 无 / 亚克力 / 云母 / 云母Alt, 选"无"就是关闭。
    //
    // ★ 为什么给用户选材质、而不是给"模糊度"滑杆: 系统**没有**改模糊半径的 API
    //   (模糊是 DWM 写死的内部常量, 唯一办法是注入 dwm.exe hook 私有符号, 普通应用做不到)。
    //   官方开放的只有"用哪种材质"(DWMWA_SYSTEMBACKDROP_TYPE), 所以把这一层开放出来。
    //   详见 Models.BackdropMaterial 的说明。
    //
    // ★★ 四个 bool 投影的写法照抄"主题色来源"那一组(那里已经踩过坑, 见 MEMORY):
    //    RadioButton 组切换时 WPF 会先把旧项 IsChecked 写 false, TwoWay 绑定会把 false 也送进
    //    setter —— 所以 setter **只处理 true**(`if (!value || ...) return;`),
    //    无条件处理 false 会把刚选中的那一项立刻盖回去(表现为"点了没反应")。

    /// <summary>材质: 无(不用任何材质=关闭)</summary>
    public bool IsMaterialNone
    {
        get => Svc.Settings.AcrylicMaterial == BackdropMaterial.None;
        set { if (!value || IsMaterialNone) return; ApplyMaterial(BackdropMaterial.None); }
    }

    /// <summary>材质: 亚克力(半透明磨砂, 实时透出桌面)</summary>
    public bool IsMaterialAcrylic
    {
        get => Svc.Settings.AcrylicMaterial == BackdropMaterial.Acrylic;
        set { if (!value || IsMaterialAcrylic) return; ApplyMaterial(BackdropMaterial.Acrylic); }
    }

    /// <summary>材质: 云母(不透明, 只采样壁纸)</summary>
    public bool IsMaterialMica
    {
        get => Svc.Settings.AcrylicMaterial == BackdropMaterial.Mica;
        set { if (!value || IsMaterialMica) return; ApplyMaterial(BackdropMaterial.Mica); }
    }

    /// <summary>材质: 云母 Alt / 标签页材质</summary>
    public bool IsMaterialMicaAlt
    {
        get => Svc.Settings.AcrylicMaterial == BackdropMaterial.MicaAlt;
        set { if (!value || IsMaterialMicaAlt) return; ApplyMaterial(BackdropMaterial.MicaAlt); }
    }

    /// <summary>
    /// 当前**是否用到了半透明材质**(亚克力) —— 只有它为 true 时, 下面那两条透明度滑杆才有意义。
    /// ★ 云母是不透明的(只采样壁纸), 对它调"页面底不透明度"没有实际效果,
    ///   所以 UI 上要把滑杆折叠掉, 免得用户调了半天发现没反应。
    /// </summary>
    public bool IsTransparentMaterial => Svc.Settings.AcrylicMaterial == BackdropMaterial.Acrylic;

    private void ApplyMaterial(BackdropMaterial material)
    {
        Svc.Settings.SetAcrylicMaterial(material);
        // 四个投影都要通知: 组里被取消选中的那一项也得跟着刷新
        OnPropertyChanged(nameof(IsMaterialNone));
        OnPropertyChanged(nameof(IsMaterialAcrylic));
        OnPropertyChanged(nameof(IsMaterialMica));
        OnPropertyChanged(nameof(IsMaterialMicaAlt));
        OnPropertyChanged(nameof(AcrylicMaterialHint));
        // 透明度滑杆的显隐跟着"是不是半透明材质"走
        OnPropertyChanged(nameof(IsTransparentMaterial));
    }

    /// <summary>当前材质对应的说明(用户一眼知道这个材质是透还是不透)</summary>
    /// ★ 2026-10-17 起云母的说明要提到"壁纸色调由程序自己算": 实测不少机器上 DWM 的云母
    ///   会退化成纯灰白(见 Helpers/DesktopTint 的说明), 只靠系统材质的话用户会觉得"没生效"。
    ///   现在颜色由我们自己保证, 说明里讲清楚这一点, 用户才不会以为那是 bug。
    public string AcrylicMaterialHint => Svc.Settings.AcrylicMaterial switch
    {
        BackdropMaterial.Acrylic => "亚克力: 半透明磨砂, 实时透出桌面内容；窗口失焦时改用主题底色(系统限制)",
        BackdropMaterial.Mica => "云母: 取壁纸色调染在窗口底色上(不透明, 不透出后面的窗口)",
        BackdropMaterial.MicaAlt => "云母 Alt: 与云母同源, 色调更淡、层次更平",
        _ => "不使用材质: 窗口用主题纯色背景"
    };

    /// <summary>
    /// 亚克力透明度——浅色主题(默认 30, 可调 0~50)。数值越小越透明, 越大越不透明。
    /// 改透明度与开关一样要走存储层落盘 + 广播, 所有窗口立即重设背景。
    /// </summary>
    public int AcrylicOpacityLight
    {
        get => Svc.Settings.AcrylicOpacityLight;
        set
        {
            if (value == Svc.Settings.AcrylicOpacityLight) return;
            Svc.Settings.SetAcrylicOpacityLight(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(AcrylicOpacityLightText));
        }
    }

    /// <summary>浅色透明度滑杆下方那行说明(与"弹幕显示区域"同款: 数值写进描述行)</summary>
    public string AcrylicOpacityLightText => $"页面底不透明度 {AcrylicOpacityLight}%";

    // ★ 滑杆上下限直接取存储层的常量(单一来源): 以后改范围只动 SettingsStore 一处,
    //   XAML 不用跟着改 —— 否则"滑杆能拉到 100 但 setter 夹回 70"会让滑杆自己弹回去。
    /// <summary>浅色透明度滑杆下限</summary>
    public double AcrylicOpacityLightMin => SettingsStore.AcrylicOpacityLightMin;
    /// <summary>浅色透明度滑杆上限</summary>
    public double AcrylicOpacityLightMax => SettingsStore.AcrylicOpacityLightMax;

    /// <summary>亚克力透明度——深色主题(默认 50, 可调 30~70)</summary>
    public int AcrylicOpacityDark
    {
        get => Svc.Settings.AcrylicOpacityDark;
        set
        {
            if (value == Svc.Settings.AcrylicOpacityDark) return;
            Svc.Settings.SetAcrylicOpacityDark(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(AcrylicOpacityDarkText));
        }
    }

    /// <summary>深色透明度滑杆下方那行说明</summary>
    public string AcrylicOpacityDarkText => $"页面底不透明度 {AcrylicOpacityDark}%";

    /// <summary>深色透明度滑杆下限</summary>
    public double AcrylicOpacityDarkMin => SettingsStore.AcrylicOpacityDarkMin;
    /// <summary>深色透明度滑杆上限</summary>
    public double AcrylicOpacityDarkMax => SettingsStore.AcrylicOpacityDarkMax;

    // ---------------- 界面字体(2026-11-14) ----------------
    //
    // ★ 全局文字字体: 所有窗口继承 FluentWindow, 选中即生效(不用重启); 弹幕跟随同一字体。
    //   图标字体不受影响(元素局部值优先于继承)。存储语义/导入生命周期见 Helpers/UiFont 类注释。
    // ★ 系统字体列表**懒加载**: 第一次打开设置页才枚举(进程级缓存), 不拖启动。

    private readonly System.Collections.ObjectModel.ObservableCollection<FontOption> _fontOptions = new();
    private bool _fontOptionsBuilt;

    /// <summary>字体下拉数据源(首次访问时构建 —— 那一刻正是设置页模板装载)</summary>
    public System.Collections.ObjectModel.ObservableCollection<FontOption> FontOptions
    {
        get { EnsureFontOptions(); return _fontOptions; }
    }

    private void EnsureFontOptions()
    {
        if (_fontOptionsBuilt) return;
        _fontOptionsBuilt = true;
        RebuildFontOptions();
    }

    /// <summary>
    /// 重建下拉: 「默认」+ 当前导入的字体(若有) + 系统已安装字体。
    /// ★ 每一项用自己的字体渲染(Preview) —— 所见即所得, 不用逐个试。
    /// </summary>
    private void RebuildFontOptions()
    {
        _fontOptions.Clear();
        _fontOptions.Add(new FontOption("默认", "", new FontFamily(UiFont.DefaultSource)));
        var (kind, name) = UiFont.ParseStored(Svc.Settings.UiFontFamily);
        if (kind == UiFont.StoredKind.File && UiFont.TryGetImport(name, out var importFamily, out var importDisplay))
            _fontOptions.Add(new FontOption(importDisplay + "（已导入）", Svc.Settings.UiFontFamily, importFamily));
        foreach (var (source, display) in UiFont.SystemFonts)
            _fontOptions.Add(new FontOption(display, source, new FontFamily(UiFont.BuildSystemSource(source))));
    }

    /// <summary>
    /// 当前选中的字体项(字符串投影思路同 ThemeSelected: 存储层没有变化通知, 靠显式推)。
    /// getter 按存储值在列表里找; 找不到(理论上不会, 存储值已清洗)回落「默认」。
    /// </summary>
    public FontOption? FontSelected
    {
        get
        {
            EnsureFontOptions();
            var stored = Svc.Settings.UiFontFamily;
            return _fontOptions.FirstOrDefault(o => string.Equals(o.Stored, stored, StringComparison.Ordinal))
                   ?? _fontOptions[0];
        }
        set
        {
            if (value == null) return;
            if (string.Equals(value.Stored, Svc.Settings.UiFontFamily, StringComparison.Ordinal)) return;
            var hadImport = HasImportOption;
            Svc.Settings.SetUiFont(value.Stored);
            // 「已导入」项的存在性变了才重建(从导入字体切到系统字体时它该消失, 文件也已被清理);
            // 其余情况不动集合 —— 避免下拉正在选择时做无谓的 Clear/Add。
            var wantImport = UiFont.ParseStored(Svc.Settings.UiFontFamily).Kind == UiFont.StoredKind.File;
            if (hadImport != wantImport) RebuildFontOptions();
            RaiseFontChanged();
        }
    }

    private bool HasImportOption => _fontOptions.Any(o =>
        o.Stored.StartsWith(UiFont.FilePrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>是否用的默认字体(决定「恢复默认」按钮是否可点)</summary>
    public bool IsDefaultFont => Svc.Settings.UiFontFamily.Length == 0;

    /// <summary>字体行说明: 当前生效的字体名 + 作用范围(弹幕跟随、图标不受影响)</summary>
    public string FontHint => IsDefaultFont
        ? "默认字体（Segoe UI Variable / 微软雅黑）；弹幕跟随同一字体，图标不受影响"
        : $"当前：{UiFont.CurrentDisplay}；弹幕跟随同一字体，图标不受影响";

    /// <summary>导入字体文件(.ttf / .otf / .ttc): 复制到数据目录, 无需安装到系统</summary>
    public ICommand ImportFontCommand { get; }

    /// <summary>恢复默认字体</summary>
    public ICommand ResetFontCommand { get; }

    /// <summary>强类型引用: 可用性随 IsDefaultFont 变, 状态变化时要显式 RaiseCanExecuteChanged</summary>
    private RelayCommand? _resetFontCommand;

    private void ImportFont()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择字体文件",
                Filter = "字体文件 (*.ttf;*.otf;*.ttc)|*.ttf;*.otf;*.ttc",
                Multiselect = false
            };
            if (dialog.ShowDialog() != true) return;

            var stored = UiFont.ImportFile(dialog.FileName, out var display);
            if (stored == null)
            {
                Svc.Toast.Show("导入失败: 不是可用的字体文件(支持 .ttf / .otf / .ttc)");
                return;
            }
            Svc.Settings.SetUiFont(stored);
            RebuildFontOptions();   // 把新导入的项加进下拉
            RaiseFontChanged();
            Svc.Toast.Show($"已导入并启用字体: {display}");
        }
        catch (Exception ex)
        {
            Svc.Toast.Show("导入字体失败: " + ex.Message);
        }
    }

    private void ResetFont()
    {
        Svc.Settings.SetUiFont("");
        RebuildFontOptions();   // 移除「已导入」项(文件已由存储层清理)
        RaiseFontChanged();
    }

    /// <summary>换字体后一整组相关属性都要通知一遍(思路同 RaiseAccentChanged)</summary>
    private void RaiseFontChanged()
    {
        OnPropertyChanged(nameof(FontSelected));
        OnPropertyChanged(nameof(FontHint));
        OnPropertyChanged(nameof(IsDefaultFont));
        _resetFontCommand?.RaiseCanExecuteChanged();
    }

    // ---------------- 快捷键自定义(2026-11-28) ----------------
    //
    // 目录(哪些操作、默认键、分组、说明)全部来自 Models/ShortcutCatalog, 这里只负责把它
    // 投影成设置页可绑定的分组列表。改绑/校验/落盘/重建映射都在 Services/ShortcutService 里;
    // 本 VM 的职责是"改完之后把每一行的显示都刷新一遍" —— 否则让出默认键的那一行会停在旧值。

    private readonly System.Collections.ObjectModel.ObservableCollection<ShortcutGroupViewModel> _shortcutGroups = new();
    private bool _shortcutGroupsBuilt;
    private RelayCommand? _resetAllShortcutsCommand;

    /// <summary>快捷键 Tab 的分组数据(首次访问时构建, 与字体下拉同一套懒加载思路)</summary>
    public System.Collections.ObjectModel.ObservableCollection<ShortcutGroupViewModel> ShortcutGroups
    {
        get { EnsureShortcutGroups(); return _shortcutGroups; }
    }

    /// <summary>全部恢复默认(只在有覆盖项时可点)</summary>
    public ICommand ResetAllShortcutsCommand { get; }

    private void EnsureShortcutGroups()
    {
        if (_shortcutGroupsBuilt) return;
        _shortcutGroupsBuilt = true;
        RebuildShortcutGroups();
    }

    private void RebuildShortcutGroups()
    {
        _shortcutGroups.Clear();
        foreach (var groupTitle in ShortcutCatalog.Groups)
        {
            var items = ShortcutCatalog.All
                .Where(d => d.Group == groupTitle)
                .Select((d, index) => new ShortcutItemViewModel(this, d, index == 0))
                .ToList();
            if (items.Count > 0)
                _shortcutGroups.Add(new ShortcutGroupViewModel(groupTitle, items));
        }
    }

    /// <summary>改绑一项(空串 = 清空)。失败原因交给行 VM 弹 Toast。</summary>
    internal bool TryAssignShortcut(ShortcutItemViewModel item, string? raw, out string error)
    {
        if (!Svc.Shortcuts.TryAssign(item.Action, raw, out error)) return false;
        RefreshShortcutRows();
        return true;
    }

    /// <summary>单项恢复默认</summary>
    internal void ResetShortcut(ShortcutItemViewModel item)
    {
        Svc.Shortcuts.Reset(item.Action);
        RefreshShortcutRows();
    }

    private void ResetAllShortcuts()
    {
        Svc.Shortcuts.ResetAll();
        RefreshShortcutRows();
        Svc.Toast.Show("已恢复默认快捷键");
    }

    /// <summary>
    /// 改绑/重置后刷新所有行。★ 不能只刷当前行: 一条覆盖项可能让出/占用另一个默认键
    /// (例如把「静音」改成 K 后, 原本用 K 的自定义项会被校验拦下, 但显示必须一致)。
    /// </summary>
    private void RefreshShortcutRows()
    {
        foreach (var group in _shortcutGroups)
            foreach (var item in group.Items)
                item.RaiseGestureChanged();
        _resetAllShortcutsCommand?.RaiseCanExecuteChanged();
    }

    // ---------------- 软件缩放(2026-10-06) ----------------
    //
    // ★ 与"系统 DPI 缩放"是两件事: 那个由 Windows 管、作用于所有程序; 这个是本程序自己缩界面。
    // ★★ 播放器的缩放范围: **只有右侧信息栏(含评论区)跟着缩, 视频画面与顶栏保持原尺寸**。
    //    不能连视频区一起缩 —— LibVLCSharp.WPF 3.8.0 把控制栏/弹幕层放在一棵独立浮层窗口里,
    //    那层按局部坐标定尺寸、不认祖先缩放, 实测会与视频区错开 195×186px(整窗缩放实测数据见
    //    .probes/bd-probe-uiscale-player)。说明文案要把这条讲清楚, 否则用户会以为"视频没缩放"是 bug。

    /// <summary>软件缩放百分比(80~150, 默认 100)</summary>
    public int UiScalePercent
    {
        get => Svc.Settings.UiScalePercent;
        set
        {
            if (value == Svc.Settings.UiScalePercent) return;
            Svc.Settings.SetUiScalePercent(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(UiScaleText));
        }
    }

    /// <summary>缩放滑杆下方那行说明</summary>
    public string UiScaleText => UiScalePercent == 100
        ? "界面缩放 100%（默认）"
        : $"界面缩放 {UiScalePercent}%";

    /// <summary>缩放滑杆下限(取自 Helpers.UiScale, 单一来源)</summary>
    public double UiScaleMin => Helpers.UiScale.MinPercent;
    /// <summary>缩放滑杆上限</summary>
    public double UiScaleMax => Helpers.UiScale.MaxPercent;

    // ---------------- 滚轮速度(2026-11-07) ----------------
    //
    // ★ 语义是"每格滑多远"的百分比: 100% = 与引擎默认一格的距离相同, 250% = 2.5 倍。
    //   缓动曲线(滑过去而不是瞬移)由 Helpers/WheelScroll 统一施加, 这里只调距离。
    // ★ 条目滚动的虚拟化列表(动态页动态流等)不受影响, 保持引擎原生的"一格 3 条"。

    /// <summary>滚轮速度百分比(100 = 原生距离; 默认 250)</summary>
    public int WheelSpeedPercent
    {
        get => Svc.Settings.WheelSpeedPercent;
        set
        {
            if (value == Svc.Settings.WheelSpeedPercent) return;
            Svc.Settings.SetWheelSpeedPercent(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(WheelSpeedText));
        }
    }

    /// <summary>滚轮速度滑杆下方那行说明(与其它滑杆同款: 数值写进描述行)</summary>
    public string WheelSpeedText => WheelSpeedPercent == 100
        ? "滚轮速度 100%（与系统默认距离相同）"
        : $"滚轮速度 {WheelSpeedPercent}%（系统默认的 {WheelSpeedPercent / 100.0:0.##} 倍）";

    /// <summary>滚轮速度滑杆下限(取自 Helpers/WheelScroll, 单一来源)</summary>
    public double WheelSpeedMin => Helpers.WheelScroll.MinPercent;
    /// <summary>滚轮速度滑杆上限</summary>
    public double WheelSpeedMax => Helpers.WheelScroll.MaxPercent;

    // ---------------- 卡片列数(2026-10-06) ----------------
    //
    // ★ 0 = 自动(按窗口宽度算, 沿用原有行为); 2~8 = 固定列数。
    // ★ 作用于**所有**卡片页: 首页/历史/收藏/搜索/分区/稍后再看/关注/个人空间 共用同一套卡片墙。

    /// <summary>卡片页每行显示几列(0 = 自动)</summary>
    public int CardColumns
    {
        get => Svc.Settings.CardColumns;
        set
        {
            if (value == Svc.Settings.CardColumns) return;
            Svc.Settings.SetCardColumns(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CardColumnsText));
            OnPropertyChanged(nameof(IsCardColumnsAuto));
        }
    }

    /// <summary>列数是"自动"还是"固定几列"</summary>
    public bool IsCardColumnsAuto => CardColumns <= 0;

    /// <summary>列数滑杆下方那行说明(与其它滑杆同款: 数值写进描述行)</summary>
    public string CardColumnsText => CardColumns <= 0
        ? "自动（按窗口宽度排列）"
        : $"固定每行 {CardColumns} 列";

    /// <summary>列数滑杆下限(0 = 自动)</summary>
    public double CardColumnsMin => 0;
    /// <summary>列数滑杆上限(取自 Helpers.CardWall, 单一来源)</summary>
    public double CardColumnsMax => Helpers.CardWall.FixedColumnsMax;

    // ----------------- 首页推荐来源(网页版 / App 端) -----------------

    /// <summary>推荐来源下拉框的显示项</summary>
    private static readonly (string Label, RecommendSource Value)[] RecommendChoices =
    {
        ("网页版推荐", RecommendSource.Web),
        ("App 端推荐", RecommendSource.App),
    };

    /// <summary>下拉框数据源(顺序即界面顺序)</summary>
    public string[] RecommendOptions { get; } =
        RecommendChoices.Select(c => c.Label).ToArray();

    /// <summary>
    /// 当前推荐来源(字符串投影, 理由同 ThemeSelected —— SettingsStore 没有变化通知)。
    /// setter 里落盘 + 推自己的通知: 下拉框的 SelectedItem 靠 getter 读回显示文字。
    /// </summary>
    public string RecommendSelected
    {
        get
        {
            foreach (var c in RecommendChoices)
                if (c.Value == Svc.Settings.RecommendSource) return c.Label;
            return RecommendChoices[0].Label;
        }
        set
        {
            var hit = RecommendChoices.FirstOrDefault(c => c.Label == value);
            if (hit.Label == null) return;
            if (hit.Value == Svc.Settings.RecommendSource) return;
            Svc.Settings.SetRecommendSource(hit.Value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(RecommendSourceHint));
        }
    }

    /// <summary>
    /// 当前来源的一句话说明。
    /// ★ 不强推 App "更准": 历史实测它对第三方客户端可能返回更通用的内容,
    ///   文案要如实说明差异, 由用户自己选。
    /// </summary>
    public string RecommendSourceHint => Svc.Settings.RecommendSource == RecommendSource.App
        ? "使用 B 站手机客户端首页推荐流, 每次 10 条; 第三方客户端拿到的内容可能比网页版更通用"
        : "使用网页版「为你推荐」流, 每次最多 30 条, 通常更贴近网页端口味";

    // ----------------- 跳过赞助片段(SponsorBlock) -----------------

    /// <summary>
    /// 总开关。关掉之后: 播放器不再自动跳过任何片段, 也不会再向第三方服务查询。
    /// ★ 默认关 —— 开着意味着每条视频的 BV 号都会被发到第三方服务器, 这必须由用户自己选。
    /// </summary>
    public bool SponsorBlockEnabled
    {
        get => Svc.Settings.SponsorBlockEnabled;
        set
        {
            Svc.Settings.SetSponsorBlock(value, CurrentCategoryCsv());
            OnPropertyChanged();
            OnPropertyChanged(nameof(SponsorBlockSummary));
        }
    }

    /// <summary>
    /// 片段类别勾选项(多选)。列表本身来自 Models 里的 SponsorCategories.All ——
    /// 界面顺序、id、中文名都只有那一处定义, 加一个类别不用改这里。
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<SponsorCategoryOption> SponsorCategoryOptions { get; } = new();

    /// <summary>当前勾选情况的一句话说明(让用户确认自己的勾选被解析成了什么)</summary>
    public string SponsorBlockSummary
    {
        get
        {
            var n = SponsorCategoryOptions.Count(o => o.IsChecked);
            if (n == 0) return "未勾选任何类别 —— 相当于不跳过";
            var names = string.Join("、", SponsorCategoryOptions.Where(o => o.IsChecked).Select(o => o.Label));
            return $"已勾选 {n} 类: {names}";
        }
    }

    /// <summary>把勾选项拼成逗号分隔的 category 串(存进设置的那份)</summary>
    private string CurrentCategoryCsv()
        => string.Join(",", SponsorCategoryOptions.Where(o => o.IsChecked).Select(o => o.Id));

    /// <summary>类别勾选变化: 重新落盘并刷新说明文字</summary>
    private void OnCategoryOptionChanged()
    {
        Svc.Settings.SetSponsorBlock(Svc.Settings.SponsorBlockEnabled, CurrentCategoryCsv());
        OnPropertyChanged(nameof(SponsorBlockSummary));
    }

    // ----------------- 托盘 -----------------

    /// <summary>
    /// 关闭主窗口时最小化到托盘(默认开)。
    /// 关闭这个开关后, 点右上角关闭键就是真的退出程序 —— 一部分用户不接受"关了还在后台跑"。
    /// </summary>
    public bool CloseToTray
    {
        get => Svc.Settings.CloseToTray;
        set
        {
            if (value == Svc.Settings.CloseToTray) return;
            Svc.Settings.SetCloseToTray(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CloseToTrayHint));
        }
    }

    public string CloseToTrayHint => Svc.Settings.CloseToTray
        ? "点右上角关闭键会收进系统托盘, 双击托盘图标可恢复"
        : "点右上角关闭键将直接退出程序";

    /// <summary>立即把主界面收进托盘(等价于点关闭键的效果, 方便先试一下手感)</summary>
    public ICommand MinimizeToTrayCommand { get; }

    // ----------------- 链接打开方式(issue #111) -----------------

    /// <summary>
    /// 点击正文里的链接时用系统默认浏览器打开(默认关 = 用内置浏览器)。
    /// ★ 只影响链接点击入口(<see cref="LinkLauncher.OpenInBrowser"/>); 内置浏览器里的
    ///   "在浏览器中打开"按钮始终走系统浏览器, 不受这个开关影响。
    /// </summary>
    public bool OpenLinksInExternalBrowser
    {
        get => Svc.Settings.OpenLinksInExternalBrowser;
        set
        {
            if (value == Svc.Settings.OpenLinksInExternalBrowser) return;
            Svc.Settings.OpenLinksInExternalBrowser = value;
            Svc.Settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(OpenLinksInExternalBrowserHint));
        }
    }

    public string OpenLinksInExternalBrowserHint => Svc.Settings.OpenLinksInExternalBrowser
        ? "链接交给系统默认浏览器打开; 内置浏览器里的「在浏览器中打开」也照旧可用"
        : "链接在应用内置浏览器中打开, 共用 B 站登录态; 需要插件或下载时可改用系统浏览器";

    // ----------------- 免登录 1080P -----------------

    /// <summary>
    /// 未登录时把匿名可见清晰度从 360P/480P 提到 720P/1080P(默认开)。
    /// 已登录用户不受影响 —— 那种情况按账号等级取流, 开关不参与(见 ApiClient.ApplyTryLook)。
    /// </summary>
    public bool NoLogin1080P
    {
        get => Svc.Settings.NoLogin1080P;
        set
        {
            if (value == Svc.Settings.NoLogin1080P) return;
            Svc.Settings.SetNoLogin1080P(value);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 副标题随登录态变化 —— 这个开关只对未登录有意义, 登录用户看到"已登录, 按账号等级取流"
    /// 才不会以为是开关坏了。
    /// </summary>
    public string NoLogin1080PHint => IsLoggedIn
        ? "已登录, 按账号等级取流, 此开关不参与"
        : "未登录也能看 1080P; 关闭后回到 360P/480P";

    // ----------------- 播放器窗口大小记忆 -----------------

    /// <summary>
    /// 是否记住播放器窗口大小和位置(默认开, 2026-11-03 新增, 位置 2026-11-07)。
    /// 只记宽高与位置, 不记最大化状态 —— 全屏是临时态, 不该带到下一次启动。
    /// </summary>
    public bool RememberPlayerWindowSize
    {
        get => Svc.Settings.RememberPlayerWindowSize;
        set
        {
            if (value == Svc.Settings.RememberPlayerWindowSize) return;
            Svc.Settings.SetRememberPlayerWindowSize(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(RememberPlayerWindowSizeHint));
        }
    }

    /// <summary>副标题: 说明它记什么、什么时候生效, 以及关掉后的行为</summary>
    public string RememberPlayerWindowSizeHint => Svc.Settings.RememberPlayerWindowSize
        ? "关闭播放器时记住窗口大小和位置, 下次打开沿用"
        : "播放器每次打开都用默认窗口大小并居中显示";

    /// <summary>
    /// 是否记住**主界面**窗口大小和位置(2026-11-20, issue #98)。
    /// ★ 与播放器开关独立: 两者是完全不同的窗口, 用户可能只想让其中一个保持固定。
    /// </summary>
    public bool RememberMainWindowPlacement
    {
        get => Svc.Settings.RememberMainWindowPlacement;
        set
        {
            if (value == Svc.Settings.RememberMainWindowPlacement) return;
            Svc.Settings.SetRememberMainWindowPlacement(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(RememberMainWindowPlacementHint));
        }
    }

    /// <summary>副标题: 同一套"开启后沿用、关闭后回到 XAML 默认"的说法, 两个开关才好对比。</summary>
    public string RememberMainWindowPlacementHint => Svc.Settings.RememberMainWindowPlacement
        ? "退出时记住主界面窗口大小和位置, 下次启动沿用"
        : "主界面每次启动都用默认窗口大小并居中显示";

    // ----------------- 视频输出(2026-11-20, issue #98) -----------------

    /// <summary>下拉框显示项; 下标与 <see cref="VideoOutputMode"/> 的数值一一对应。</summary>
    public string[] VideoOutputOptions { get; } =
        { "自动(推荐)", "Direct3D 11", "Direct3D 9", "OpenGL" };

    /// <summary>
    /// 当前视频输出模块(用字符串投影, 理由同 QualitySelected: SettingsStore 没有变化通知)。
    /// ★ LibVLC 是进程级单例, 改完要重启应用才生效 —— Hint 里写清楚, 免得用户以为没生效。
    /// </summary>
    public string VideoOutputSelected
    {
        get
        {
            var mode = Svc.Settings.VideoOutputMode;
            var index = Enum.IsDefined(typeof(VideoOutputMode), mode) ? (int)mode : 0;
            return VideoOutputOptions[index];
        }
        set
        {
            var index = Array.IndexOf(VideoOutputOptions, value);
            if (index < 0) return;

            var mode = (VideoOutputMode)index;
            if (mode == Svc.Settings.VideoOutputMode) return;
            Svc.Settings.SetVideoOutputMode(mode);
            OnPropertyChanged();
            OnPropertyChanged(nameof(VideoOutputHint));
        }
    }

    /// <summary>说明默认行为、排查方向与"需要重启"这件容易踩的事。</summary>
    public string VideoOutputHint => Svc.Settings.VideoOutputMode == VideoOutputMode.Auto
        ? "由播放器自动选择(通常是 Direct3D 11); 修改后需重启应用生效"
        : "若画面异常或 NVIDIA 叠加层误判为游戏, 可换一档; 修改后需重启应用生效";

    // ----------------- 硬件解码(2026-11-22, issue #119) -----------------

    /// <summary>
    /// 是否允许 libvlc 硬件解码(默认开)。关掉后新起播的 Media 会带 `:avcodec-hw=none`,
    /// 走 CPU 软解 —— 这是 VLC 3.0.24“暂停后画面卡死、音频继续”的永久规避手段。
    /// ★ 与视频输出不同, 它按 Media 级选项生效, **不需要重启应用**, 下一次起播就生效。
    /// </summary>
    public bool HardwareDecoding
    {
        get => Svc.Settings.HardwareDecodingEnabled;
        set
        {
            if (value == Svc.Settings.HardwareDecodingEnabled) return;
            Svc.Settings.SetHardwareDecodingEnabled(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(HardwareDecodingHint));
        }
    }

    /// <summary>说明用途、代价与生效时机(避免用户以为关了马上会对当前画面生效)。</summary>
    public string HardwareDecodingHint
    {
        get
        {
            if (!Svc.Settings.HardwareDecodingEnabled)
                return "已关闭：新起播的视频用 CPU 软解，可规避部分显卡暂停后画面卡住；下次起播生效";
            // 自动自愈是**会话级**的: 设置本身仍写着“开”, 但本次运行已经临时软解。
            // 不解释的话用户会看到开关是开的、却一直在软解。
            if (PlaybackCompatibility.ForceSoftwareDecode)
                return "本次运行检测到画面冻结，已临时改用软解；重新打开应用可恢复硬解，也可在此关闭此项";
            return "默认开启; 若暂停/恢复后画面不再更新而声音正常，可关闭此项（软解更吃 CPU，下次起播生效）";
        }
    }

    // ----------------- 默认画质 -----------------

    /// <summary>默认画质下拉框的显示项(顺序即 QualityPreference.All 的顺序)</summary>
    public string[] QualityOptions { get; } =
        QualityPreference.All.Select(c => c.Label).ToArray();

    /// <summary>
    /// 当前默认画质(用**字符串投影**, 理由同 CdnModeSelected —— SettingsStore 没有变化通知,
    /// 直接绑 qn 会在别处改动后不刷新)。
    ///
    /// setter 里落盘 + 推自己的通知: 下拉框的 SelectedItem 靠 getter 读回显示文字,
    /// WPF 在 TwoWay 回写后不会重新读一遍 —— 不推通知的话方框里可能停在旧文字上。
    /// </summary>
    public string QualitySelected
    {
        get => QualityPreference.LabelOf(Svc.Settings.PreferredQualityQn);
        set
        {
            var qn = QualityPreference.QnOf(value);
            if (qn == Svc.Settings.PreferredQualityQn) return;
            Svc.Settings.SetPreferredQuality(qn);
            OnPropertyChanged();
            OnPropertyChanged(nameof(QualityHint));
        }
    }

    /// <summary>
    /// 副标题: 把"预先选定 + 不可用时自动降级"这条行为写清楚 ——
    /// 这是本设置项唯一容易误解的地方(用户会以为选了 4K 就一定有 4K)。
    /// </summary>
    public string QualityHint =>
        Svc.Settings.PreferredQualityQn == QualityPreference.AutoQn
            ? "每次起播自动取当前账号可用的最高清晰度(最高到 4K; 杜比视界/HDR/8K 播放器暂不支持, 不在清晰度菜单里)"
            : $"每次起播都请求 {QualityPreference.LabelOf(Svc.Settings.PreferredQualityQn)}; " +
              "该清晰度不存在或没有权限时自动降到可用的下一档";

    // ----------------- 音质(2026-11-06) -----------------

    /// <summary>音质下拉框的显示项(顺序即 AudioPreference.All 的顺序)</summary>
    public string[] AudioTierOptions { get; } =
        AudioPreference.All.Select(AudioPreference.LabelOf).ToArray();

    /// <summary>
    /// 当前音质档位(字符串投影, 理由同 QualitySelected)。
    /// 与播放器控制栏那枚音质按钮读写同一个配置, 两处随时一致。
    /// </summary>
    public string AudioTierSelected
    {
        get => AudioPreference.LabelOf(Svc.Settings.PreferredAudioTier);
        set
        {
            var tier = AudioPreference.Normalize(
                AudioPreference.All.FirstOrDefault(t => AudioPreference.LabelOf(t) == value));
            if (tier == Svc.Settings.PreferredAudioTier) return;
            Svc.Settings.PreferredAudioTier = tier;
            Svc.Settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(AudioTierHint));
        }
    }

    /// <summary>副标题: 说明"只换音轨不换画质"与降级规则</summary>
    public string AudioTierHint =>
        Svc.Settings.PreferredAudioTier == AudioTier.Auto
            ? "有 Hi-Res 无损 / 杜比音轨时优先使用, 否则用可用的最高音质; 普通视频仍是 192K AAC"
            : $"播放器优先使用 {AudioPreference.LabelOf(Svc.Settings.PreferredAudioTier)}; " +
              "该视频没有这一档时自动降到可用音质, 不影响画面清晰度";

    // ----------------- 音频重采样(2026-11-22) -----------------

    /// <summary>下拉框选项。字符串投影, 理由同 AudioTierSelected。</summary>
    public string[] AudioResamplerOptions =>
        AudioResamplerPreference.All.Select(AudioResamplerPreference.LabelOf).ToArray();

    /// <summary>
    /// 当前重采样档位(默认「自动」)。
    /// ★ 自动 = 按默认输出端点决定: 直连音响用 speex(修电流音), 蓝牙耳机用 any(防卡顿)。
    /// </summary>
    public string AudioResamplerSelected
    {
        get => AudioResamplerPreference.LabelOf(Svc.Settings.AudioResamplerMode);
        set
        {
            var mode = AudioResamplerPreference.Normalize(
                AudioResamplerPreference.All.FirstOrDefault(m =>
                    AudioResamplerPreference.LabelOf(m) == value));
            if (mode == Svc.Settings.AudioResamplerMode) return;
            Svc.Settings.AudioResamplerMode = mode;
            Svc.Settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(AudioResamplerHint));
        }
    }

    /// <summary>副标题: 说清"哪种情况该改" —— 普通用户不应为了音质乱切。</summary>
    public string AudioResamplerHint =>
        AudioResamplerPreference.HintOf(Svc.Settings.AudioResamplerMode) +
        "（切换后对新打开的视频生效）";

    // ----------------- 高音质音轨(2026-11-06) -----------------

    /// <summary>
    /// 优先 Hi-Res 无损 / 杜比音轨。开启后取流请求带 fnval=4048,
    /// 有 30251(FLAC) / 30250(杜比) 时优先使用; 普通视频仍回落到 192K AAC。
    /// </summary>
    public bool PreferHighQualityAudio
    {
        get => Svc.Settings.PreferHighQualityAudio;
        set
        {
            if (value == Svc.Settings.PreferHighQualityAudio) return;
            Svc.Settings.PreferHighQualityAudio = value;
            Svc.Settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>副标题: 说清楚"需要大会员且视频本身提供", 避免普通视频没变化被当成开关坏了</summary>
    public string PreferHighQualityAudioHint =>
        "有 Hi-Res 无损 / 杜比音轨时优先使用; 需要大会员且视频本身提供, 普通视频仍是 192K AAC";

    // ----------------- 自动续播(2026-11-06) -----------------

    /// <summary>打开视频时自动跳到此前的本机 / B 站云端进度</summary>
    public bool AutoResume
    {
        get => Svc.Settings.AutoResume;
        set
        {
            if (value == Svc.Settings.AutoResume) return;
            Svc.Settings.AutoResume = value;
            Svc.Settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>副标题: 说明关闭后仍然记录, 只是不自动跳</summary>
    public string AutoResumeHint =>
        "打开视频时自动跳到此前的观看进度(本机记录 + B 站云端); 关闭后仍会记录, 只是从头播放";

    // ----------------- 相关视频(2026-11-14) -----------------

    /// <summary>
    /// 是否在播放器信息栏显示「相关视频」(默认开)。
    /// 关闭后播放器不再请求相关视频接口, 信息栏也不显示那一块。
    /// </summary>
    public bool ShowRelatedVideos
    {
        get => Svc.Settings.ShowRelatedVideos;
        set
        {
            if (value == Svc.Settings.ShowRelatedVideos) return;
            Svc.Settings.SetShowRelatedVideos(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowRelatedVideosHint));
        }
    }

    /// <summary>
    /// 副标题: 说清关闭后的行为(不再请求 + 不再显示 + 简介不再折叠), 以及立即生效。
    /// ★ 简介折叠是为了给相关视频让位置, 两者联动(见 DescFoldPolicy):
    ///   不写明的话, 用户会以为"关掉推荐"与"简介折着"是两件互不相干的事。
    /// </summary>
    public string ShowRelatedVideosHint => Svc.Settings.ShowRelatedVideos
        ? "播放器右侧简介下方显示相关推荐; 关闭后不再加载相关视频, 简介也不再折叠"
        : "已关闭, 播放器不再请求与显示相关视频, 简介整段展开";

    // ----------------- 详情栏展开把手(2026-11-28) -----------------

    /// <summary>
    /// 播放器详情栏左缘那条「展开/收起」把手是否显示(默认开)。
    /// 关掉之后那条窄条不再出现, 右栏保持常规的 400 宽。
    /// </summary>
    public bool ShowInfoExpandGrip
    {
        get => Svc.Settings.ShowInfoExpandGrip;
        set
        {
            if (value == Svc.Settings.ShowInfoExpandGrip) return;
            Svc.Settings.SetShowInfoExpandGrip(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowInfoExpandGripHint));
        }
    }

    /// <summary>
    /// 副标题: 说清这个把手干什么、关掉之后怎么用, 以及立即生效。
    /// ★ 必须写明"关掉只是没有入口, 已展开的仍能按 Esc 收回" —— 否则用户会担心
    ///   关掉开关时万一正处于展开态, 就再也回不到视频了。
    /// </summary>
    public string ShowInfoExpandGripHint => Svc.Settings.ShowInfoExpandGrip
        ? "详情栏左缘显示一条展开把手, 点一下让详情栏(含评论区)铺满整个窗口"
        : "已隐藏该把手; 详情栏保持常规宽度, 已展开的窗口仍可按 Esc 收回";

    // ----------------- 自动连播(2026-11-19) -----------------

    /// <summary>
    /// 播完自动播下一条(默认开)。多P 视频先接下一个 P, 然后是合集/番剧的下一集,
    /// 都没有才接相关视频列表; 换片前有 5 秒倒计时, 可随时取消。
    /// </summary>
    public bool AutoPlayNext
    {
        get => Svc.Settings.AutoPlayNext;
        set
        {
            if (value == Svc.Settings.AutoPlayNext) return;
            Svc.Settings.SetAutoPlayNext(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoPlayNextHint));
        }
    }

    /// <summary>
    /// 副标题: 说清"下一条"从哪来 + 有倒计时可取消。
    /// ★ 必须写明"倒计时可取消": 否则用户会担心"开了就停不下来"。
    /// ★ 也要说明它独立于「显示相关视频」: 关了显示时连播仍然用那份推荐当队列。
    /// </summary>
    public string AutoPlayNextHint => Svc.Settings.AutoPlayNext
        ? "播完后 5 秒自动播下一条: 多P 视频先接下一个 P, 再接合集/番剧的下一集, 都没有才播相关视频; 倒计时中可随时取消"
        : "已关闭; 播完后停在「已播完」, 需要自己点下一条";

    // ----------------- 字幕(2026-11-15) -----------------

    /// <summary>有字幕的视频自动显示(默认关; 在播放器点一次 CC 也会记住)</summary>
    public bool SubtitleEnabled
    {
        get => Svc.Settings.SubtitleEnabled;
        set
        {
            if (value == Svc.Settings.SubtitleEnabled) return;
            Svc.Settings.UpdateSubtitleSettings(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubtitleEnabledHint));
        }
    }

    /// <summary>副标题: 说清"AI 字幕需要登录"这个最常见的困惑</summary>
    public string SubtitleEnabledHint => Svc.Settings.SubtitleEnabled
        ? "有字幕的视频自动显示(优先上次选择的语言); AI 字幕需要登录后才能获取"
        : "已关闭; 在播放器点「CC」或按 C 键可临时开启, 开过的语言会被记住";

    /// <summary>字幕字号缩放(80~160)。即时生效, 播放中的字幕立刻跟着变。</summary>
    public int SubtitleFontPercent
    {
        get => Svc.Settings.SubtitleFontPercent;
        set
        {
            if (value == Svc.Settings.SubtitleFontPercent) return;
            Svc.Settings.SetSubtitleFontPercent(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubtitleFontText));
        }
    }

    public string SubtitleFontText
    {
        get
        {
            var p = SubtitleFontPercent;
            if (p == 100) return "默认大小";
            return p > 100 ? $"放大到 {p}%" : $"缩小到 {p}%";
        }
    }

    public int SubtitleFontMin => Services.SettingsStore.SubtitleFontMinPercent;
    public int SubtitleFontMax => Services.SettingsStore.SubtitleFontMaxPercent;

    // ----------------- 起播缓冲(网络缓存) -----------------

    /// <summary>
    /// 缓冲策略下拉框的显示项: 自动(按带宽挑值) / 手动(固定值)。
    /// 顺序即 CdnSelectMode…不对, 是 BufferPolicy 的语义顺序, 取值 = index 0 自动 / 1 手动。
    /// </summary>
    public string[] BufferPolicyOptions { get; } = { "自动(推荐)", "手动" };

    /// <summary>当前缓冲策略(字符串投影, 理由同 CdnModeSelected —— 设置对象没有变化通知)</summary>
    public string BufferPolicySelected
    {
        get => BufferPolicyOptions[Svc.Settings.AutoBufferPolicy ? 0 : 1];
        set
        {
            var idx = Array.IndexOf(BufferPolicyOptions, value);
            if (idx < 0) return;
            var auto = idx == 0;
            if (auto == Svc.Settings.AutoBufferPolicy) return;
            Svc.Settings.SetAutoBufferPolicy(auto);
            OnPropertyChanged();
            OnPropertyChanged(nameof(BufferPolicyHint));
            OnPropertyChanged(nameof(CanPickNetworkCaching));
        }
    }

    /// <summary>
    /// 副标题: 写清自动档挑值的依据与手动档的语义 ——
    /// 自动档的值随每条视频变化, 固定写一个数字反而会变成错报。
    /// </summary>
    public string BufferPolicyHint =>
        Svc.Settings.AutoBufferPolicy
            ? "按最近一次 CDN 测速的带宽与码率自动挑预读深度(快线更省内存, 慢线更抗卡顿), 下一条视频生效"
            : $"恒用下面设置的固定缓冲值 · 当前 {Svc.Settings.NetworkCachingMs}ms, 下一条视频生效";

    /// <summary>手动缓冲值下拉框只在手动档可用(自动档置灰但保留可见, 同 CanPickCdn 的做法)</summary>
    public bool CanPickNetworkCaching => !Svc.Settings.AutoBufferPolicy;

    /// <summary>
    /// 网络缓冲的可选档位(文字 + 毫秒)。顺序即下拉框顺序: 从"最小/最省"到"最抗抖动"。
    /// ★ 值为 300/500/1000/3000, 与 SettingsStore.NetworkCachingMin/Max 的夹取范围一致。
    /// </summary>
    private static readonly (string Label, int Ms)[] NetworkCachingChoices =
    {
        ("300ms · 最小", 300),
        ("500ms · 偏小", 500),
        ("1000ms · 默认", 1000),
        ("3000ms · 弱网", 3000),
    };

    /// <summary>网络缓冲下拉框的显示项</summary>
    public string[] NetworkCachingOptions { get; } =
        NetworkCachingChoices.Select(c => c.Label).ToArray();

    /// <summary>
    /// 当前网络缓冲(字符串投影, 理由同 CdnModeSelected)。落盘走 SetNetworkCaching,
    /// 它在读取时已经夹到合法范围, 所以这里不会出现"选了个不存在的档"。
    /// </summary>
    public string NetworkCachingSelected
    {
        get
        {
            foreach (var c in NetworkCachingChoices)
                if (c.Ms == Svc.Settings.NetworkCachingMs) return c.Label;
            return NetworkCachingChoices[2].Label;   // 越界值(手改配置)显示默认档
        }
        set
        {
            var hit = NetworkCachingChoices.FirstOrDefault(c => c.Label == value);
            if (hit.Label == null) return;
            if (hit.Ms == Svc.Settings.NetworkCachingMs) return;
            Svc.Settings.SetNetworkCaching(hit.Ms);
            OnPropertyChanged();
            OnPropertyChanged(nameof(NetworkCachingHint));
        }
    }

    /// <summary>
    /// 副标题: 说清它是"抗抖动余量"而不是"起播开关" —— 实测起播阶段 300/3000 几乎测不出差别
    /// (见 SettingsStore.NetworkCachingMs 的说明), 不写清楚会让人以为调小就能秒开。
    /// 自动档时这个值只是"手动时的预选项", 提示里要交代清楚。
    /// </summary>
    public string NetworkCachingHint =>
        $"固定预读缓冲(手动档生效): 越大越抗网络抖动, 越小越省内存 · 当前 {Svc.Settings.NetworkCachingMs}ms, 下一条视频生效";

    // ----------------- CDN(线路)设置 -----------------
    // 逻辑见 Services/CdnService.cs; 实测依据见该文件顶部注释

    /// <summary>
    /// 线路策略下拉框的显示项。
    /// ★ 2026-11-21: "自动测速"已删除(见 CdnService 顶注), 只剩这两项。
    ///   取值与 <see cref="CdnSelectMode"/> 的数值**不再对应**, 所以下面用显式映射,
    ///   不要再用 `(CdnSelectMode)idx` —— 那个下标玩法正是漏改一处就静默错位的地方。
    /// </summary>
    private static readonly (string Label, CdnSelectMode Mode)[] CdnModeChoices =
    {
        ("跟随服务端", CdnSelectMode.ServerDefault),
        ("手动指定", CdnSelectMode.Manual),
    };

    /// <summary>线路策略下拉框的显示项</summary>
    public string[] CdnModeOptions { get; } = CdnModeChoices.Select(c => c.Label).ToArray();

    /// <summary>当前策略(用字符串投影, 理由同 ThemeSelected —— 服务对象没有变化通知)</summary>
    public string CdnModeSelected
    {
        get
        {
            foreach (var c in CdnModeChoices)
                if (c.Mode == Svc.Settings.CdnMode) return c.Label;
            return CdnModeChoices[0].Label;   // Auto(旧配置残留)显示成默认项
        }
        set
        {
            var hit = CdnModeChoices.FirstOrDefault(c => c.Label == value);
            if (hit.Label == null) return;
            if (hit.Mode == Svc.Settings.CdnMode) return;
            Svc.Settings.SetCdn(hit.Mode, Svc.Settings.CdnManualId, Svc.Settings.BlockPcdn);
            RaiseCdnChanged();
        }
    }

    /// <summary>可选 CDN 的名字列表(下拉框数据源)</summary>
    public string[] CdnHostOptions { get; } = CdnOption.All.Select(o => o.Name).ToArray();

    /// <summary>
    /// 手动模式下选中的 CDN 名。
    /// 老配置里 Id 已失效时 CdnManualId 是空串 → 下拉框落到第一项(不显示空白)。
    /// </summary>
    public string CdnHostSelected
    {
        get
        {
            var cur = CdnOption.ById(Svc.Settings.CdnManualId);
            return cur?.Name ?? CdnOption.All[0].Name;
        }
        set
        {
            var opt = CdnOption.All.FirstOrDefault(o => o.Name == value);
            if (opt == null || opt.Id == Svc.Settings.CdnManualId) return;
            Svc.Settings.SetCdn(Svc.Settings.CdnMode, opt.Id, Svc.Settings.BlockPcdn);
            RaiseCdnChanged();
        }
    }

    /// <summary>
    /// 手动选择的下拉框是否可用 —— 只有"手动指定"模式下才让它可点。
    /// 置灰而不是隐藏: 用户要能看见"有这么个选项、只是当前模式用不到"。
    /// </summary>
    public bool CanPickCdn => Svc.Settings.CdnMode == CdnSelectMode.Manual;

    /// <summary>屏蔽 PCDN(点对点分发)</summary>
    public bool BlockPcdn
    {
        get => Svc.Settings.BlockPcdn;
        set
        {
            if (value == Svc.Settings.BlockPcdn) return;
            Svc.Settings.SetCdn(Svc.Settings.CdnMode, Svc.Settings.CdnManualId, value);
            RaiseCdnChanged();
        }
    }

    /// <summary>CDN 相关属性整组刷新(切换模式会影响联动项, 一次全推开免漏)</summary>
    private void RaiseCdnChanged()
    {
        OnPropertyChanged(nameof(CdnModeSelected));
        OnPropertyChanged(nameof(CdnHostSelected));
        OnPropertyChanged(nameof(CanPickCdn));
        OnPropertyChanged(nameof(BlockPcdn));
    }

    // ----------------- 检查更新 -----------------

    /// <summary>启动后自动检查更新(默认开; 关掉后仍可用下面的「检查更新」按钮手动查)</summary>
    public bool AutoCheckUpdate
    {
        get => Svc.Settings.AutoCheckUpdate;
        set
        {
            if (value == Svc.Settings.AutoCheckUpdate) return;
            Svc.Settings.SetAutoCheckUpdate(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoCheckUpdateHint));
        }
    }

    public string AutoCheckUpdateHint => Svc.Settings.AutoCheckUpdate
        ? "启动后在后台检查一次, 有新版本或网络异常时弹窗提醒(网络异常每天最多提示一次)"
        : "已关闭, 仍可随时点「检查更新」手动检查";

    public string[] UpdateMirrorOptions { get; } = { "直连", "gh-proxy", "ghfast", "ghproxy", "自定义" };

    private const string UpdateMirrorGhProxy = "https://gh-proxy.com/";
    private const string UpdateMirrorGhFast = "https://ghfast.top/";
    private const string UpdateMirrorGhProxyNet = "https://ghproxy.net/";

    private string _updateMirrorPreset = UpdateMirrorPresetOf(Svc.Settings.UpdateMirror);

    /// <summary>镜像源下拉: 预设选项 + 自定义。选自定义时保留输入框里的值，由文本框写设置。</summary>
    public string UpdateMirrorPreset
    {
        get => _updateMirrorPreset;
        set
        {
            var next = value ?? "自定义";
            if (string.Equals(next, _updateMirrorPreset, StringComparison.Ordinal)) return;
            _updateMirrorPreset = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCustomUpdateMirror));
            switch (next)
            {
                case "直连": SetUpdateMirrorSetting(""); break;
                case "gh-proxy": SetUpdateMirrorSetting(UpdateMirrorGhProxy); break;
                case "ghfast": SetUpdateMirrorSetting(UpdateMirrorGhFast); break;
                case "ghproxy": SetUpdateMirrorSetting(UpdateMirrorGhProxyNet); break;
            }
        }
    }

    public bool IsCustomUpdateMirror => _updateMirrorPreset == "自定义";

    private void SetUpdateMirrorSetting(string value)
    {
        Svc.Settings.SetUpdateMirror(value);
        OnPropertyChanged(nameof(UpdateMirror));
        OnPropertyChanged(nameof(UpdateMirrorHint));
    }

    private static string UpdateMirrorPresetOf(string? mirror) => (mirror ?? "").Trim() switch
    {
        "" => "直连",
        UpdateMirrorGhProxy => "gh-proxy",
        UpdateMirrorGhFast => "ghfast",
        UpdateMirrorGhProxyNet => "ghproxy",
        _ => "自定义"
    };

    /// <summary>更新镜像源(空 = 直连 GitHub); 输入框失焦时落盘。</summary>
    public string UpdateMirror
    {
        get => Svc.Settings.UpdateMirror;
        set
        {
            var next = (value ?? "").Trim();
            if (string.Equals(next, Svc.Settings.UpdateMirror, StringComparison.Ordinal)) return;
            Svc.Settings.SetUpdateMirror(next);
            OnPropertyChanged();
            OnPropertyChanged(nameof(UpdateMirrorHint));
        }
    }

    public string UpdateMirrorHint => string.IsNullOrEmpty(Svc.Settings.UpdateMirror)
        ? "留空直连 GitHub；选「自定义」可填镜像前缀或 {url} 模板"
        : $"当前走镜像：{Svc.Settings.UpdateMirror}";

    /// <summary>最近一次检查的结果(手动检查的反馈就落在这里, 让用户确认按钮点下去有反应)</summary>
    private string _updateStatusText = "";
    public string UpdateStatusText
    {
        get => _updateStatusText;
        private set => SetProperty(ref _updateStatusText, value);
    }

    /// <summary>防抖: 手动检查进行中再点按钮直接忽略, 避免连点弹两个窗</summary>
    private bool _checkingUpdate;

    public ICommand CheckUpdateCommand { get; }

    /// <summary>
    /// 手动检查更新。与新版弹窗共用 UpdatePromptWindow —— 但手动检查时用户是主动来的,
    /// 「跳过此版本」没有意义, 那个按钮会被隐藏。
    /// </summary>
    private async System.Threading.Tasks.Task CheckUpdateAsync()
    {
        if (_checkingUpdate) return;
        _checkingUpdate = true;
        UpdateStatusText = "正在检查更新…";
        try
        {
            var info = await UpdateChecker.CheckAsync();
            if (info == null)
            {
                UpdateStatusText = $"已是最新版本 v{App.AppVersion}";
                Svc.Toast.Show("已是最新版本");
                return;
            }

            UpdateStatusText = $"发现新版本 v{info.Version}";
            var owner = System.Windows.Application.Current.MainWindow;
            var win = new Views.Windows.UpdatePromptWindow(info)
            {
                Owner = owner,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner
            };
            win.AllowSkip = false;
            win.ShowDialog();
        }
        catch (UpdateCheckException ex)
        {
            // 失败原因已经分好类了(VPN/超时/限流…), 直接给用户看具体的那一句,
            // 不再笼统地说"检查更新失败"。
            UpdateStatusText = ex.UserMessage;
            App.ReportError(ex);
            Svc.Toast.Show(ex.UserMessage);
        }
        catch (Exception ex)
        {
            UpdateStatusText = "检查失败, 请稍后再试";
            // catch 不静默是本项目的硬规矩: 记日志但不打断用户
            App.ReportError(ex);
            Svc.Toast.Show("检查更新失败, 请检查网络后重试");
        }
        finally
        {
            _checkingUpdate = false;
        }
    }

    // ----------------- 弹幕设置(全局) -----------------

    public bool DanmakuEnabled
    {
        get => Svc.Settings.DanmakuEnabled;
        set
        {
            Svc.Settings.UpdateDanmakuSettings(value, Svc.Settings.DanmakuAreaPercent, Svc.Settings.DanmakuSmartFilter);
            OnPropertyChanged();
        }
    }

    /// <summary>弹幕显示区域占比(25~100)</summary>
    public int DanmakuAreaPercent
    {
        get => Svc.Settings.DanmakuAreaPercent;
        set
        {
            Svc.Settings.UpdateDanmakuSettings(Svc.Settings.DanmakuEnabled, value, Svc.Settings.DanmakuSmartFilter);
            OnPropertyChanged();
            OnPropertyChanged(nameof(DanmakuAreaText));
        }
    }

    public string DanmakuAreaText => $"画面上方 {DanmakuAreaPercent}%";

    // ---- 外观三项: 不透明度 / 字号 / 速度(2026-10-12 新增) ----
    //
    // ★ 三个属性写法与上面的 DanmakuAreaPercent 完全一致: 只把"自己这一项"作为可选覆盖
    //   传给存储层, 其余传 null(= 不改)。这样加新字段时不需要回头改这些调用点。

    /// <summary>弹幕不透明度(20~100)。低于 100 时弹幕会半透明, 白字压亮画面不再晃眼。</summary>
    public int DanmakuOpacityPercent
    {
        get => Svc.Settings.DanmakuOpacityPercent;
        set
        {
            Svc.Settings.UpdateDanmakuSettings(
                Svc.Settings.DanmakuEnabled, Svc.Settings.DanmakuAreaPercent, Svc.Settings.DanmakuSmartFilter,
                opacityPercent: value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(DanmakuOpacityText));
        }
    }

    public string DanmakuOpacityText => $"{DanmakuOpacityPercent}%";

    /// <summary>弹幕字号倍率(50~300)。★ 改了会连带泳道高一起变, 见 PlayerWindow.DanmakuLaneRatio</summary>
    public int DanmakuFontScalePercent
    {
        get => Svc.Settings.DanmakuFontScalePercent;
        set
        {
            Svc.Settings.UpdateDanmakuSettings(
                Svc.Settings.DanmakuEnabled, Svc.Settings.DanmakuAreaPercent, Svc.Settings.DanmakuSmartFilter,
                fontScalePercent: value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(DanmakuFontScaleText));
        }
    }

    /// <summary>
    /// 字号的人话描述。倍率直接印成 "150%" 不好懂 —— 玩家关心的是"比默认大多少/小多少"。
    /// </summary>
    public string DanmakuFontScaleText
    {
        get
        {
            var p = DanmakuFontScalePercent;
            if (p == 100) return "默认大小";
            return p > 100 ? $"放大到 {p}%" : $"缩小到 {p}%";
        }
    }

    /// <summary>弹幕速度档位(0 最慢 ~ 4 最快)</summary>
    public int DanmakuSpeedLevel
    {
        get => Svc.Settings.DanmakuSpeedLevel;
        set
        {
            var v = Math.Clamp(value,
                BiliDesk.Helpers.DanmakuMotion.MinSpeedLevel, BiliDesk.Helpers.DanmakuMotion.MaxSpeedLevel);
            if (v == Svc.Settings.DanmakuSpeedLevel) return;
            Svc.Settings.UpdateDanmakuSettings(
                Svc.Settings.DanmakuEnabled, Svc.Settings.DanmakuAreaPercent, Svc.Settings.DanmakuSmartFilter,
                speedLevel: v);
            OnPropertyChanged();
            OnPropertyChanged(nameof(DanmakuSpeedText));
        }
    }

    public string DanmakuSpeedText => BiliDesk.Helpers.DanmakuMotion.SpeedLabel(DanmakuSpeedLevel);

    /// <summary>
    /// 弹幕是否带半透明圆角胶囊底衬(2026-11-05 新增)。
    ///
    /// ★ 默认开: 纯白字压在明亮画面上会看不清, 底衬正是为了解决这个问题。
    ///   关掉就回到 2026-10-07 之后的纯文字观感(那时用户明确要求去掉长方形底衬)。
    /// ★ 走"只把自己这一项作为可选覆盖传下去"的写法, 与上面三个外观项完全一致。
    /// </summary>
    public bool DanmakuPillBackground
    {
        get => Svc.Settings.DanmakuPillBackground;
        set
        {
            if (value == Svc.Settings.DanmakuPillBackground) return;
            Svc.Settings.UpdateDanmakuSettings(
                Svc.Settings.DanmakuEnabled, Svc.Settings.DanmakuAreaPercent, Svc.Settings.DanmakuSmartFilter,
                pillBackground: value);
            OnPropertyChanged();
        }
    }

    /// <summary>速度可选值的文案列表(给播放器里的说明文案复用)</summary>
    public string[] DanmakuSpeedOptions => BiliDesk.Helpers.DanmakuMotion.SpeedLevelLabels;

    // ---- 五个档位各自的"选中态" ----
    //
    // ★ 为什么是 5 个 bool 属性而不是把 int 档位绑进 RadioButtonGroup:
    //   RadioButton 的 IsChecked 是 bool, 档位是 int, 中间隔着一个不存在的转换器。
    //   那种写法(Slider→int 直绑)在这里会静默失灵, 而且失效时不报任何错。
    //   5 个属性各自"读档位、判等号、转发 setter", 选中逻辑一眼可读, 也好加档。
    //   命名末尾用数字而不是名字: 档位号与 DanmakuSpeedLevel 的值必须一一对应。

    /// <summary>是否选中「慢」档(档位 0)。set 为 true 时把档位设成 0。</summary>
    public bool DanmakuSpeedIs0 { get => IsSpeed(0); set { if (value) SetSpeed(0); } }

    /// <summary>是否选中「较慢」档(档位 1)</summary>
    public bool DanmakuSpeedIs1 { get => IsSpeed(1); set { if (value) SetSpeed(1); } }

    /// <summary>是否选中「适中」档(档位 2)</summary>
    public bool DanmakuSpeedIs2 { get => IsSpeed(2); set { if (value) SetSpeed(2); } }

    /// <summary>是否选中「较快」档(档位 3)</summary>
    public bool DanmakuSpeedIs3 { get => IsSpeed(3); set { if (value) SetSpeed(3); } }

    /// <summary>是否选中「极快」档(档位 4)</summary>
    public bool DanmakuSpeedIs4 { get => IsSpeed(4); set { if (value) SetSpeed(4); } }

    private bool IsSpeed(int level) => DanmakuSpeedLevel == level;

    /// <summary>切档: 走 DanmakuSpeedLevel 的 setter, 然后把五个选中态一起刷新</summary>
    private void SetSpeed(int level)
    {
        DanmakuSpeedLevel = level;
        OnPropertyChanged(nameof(DanmakuSpeedIs0));
        OnPropertyChanged(nameof(DanmakuSpeedIs1));
        OnPropertyChanged(nameof(DanmakuSpeedIs2));
        OnPropertyChanged(nameof(DanmakuSpeedIs3));
        OnPropertyChanged(nameof(DanmakuSpeedIs4));
    }


    public bool DanmakuSmartFilter
    {
        get => Svc.Settings.DanmakuSmartFilter;
        set
        {
            Svc.Settings.UpdateDanmakuSettings(Svc.Settings.DanmakuEnabled, Svc.Settings.DanmakuAreaPercent, value);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 弹幕关键词屏蔽(用户手写黑名单)—— **2026-11-05 已删除这个功能**。
    ///
    /// ★ 为什么删: 关键词黑名单要用户自己手写词表, 命中率低、维护成本高, 而"智能屏蔽"
    ///   (超长弹幕 + 刷屏弹幕)已经覆盖了绝大多数真实场景。列表侧的"标题/UP 屏蔽"
    ///   仍在首页那套面板里(那是另一套东西, 见 Helpers/FilterService)。
    /// ★ 删的时候是**整条链路**一起删的: 设置项 + 存储字段 + 解析器 + 播放器过滤分支,
    ///   只删界面会让"老配置里的关键词"变成看不见却仍生效的幽灵设置。
    /// </summary>

    // ---- 弹幕类型过滤(滚动/固定/彩色/高级) ----
    // 四个属性写法完全一致, 只是把"自己这一项"作为可选覆盖传给存储层 ——
    // UpdateDanmakuSettings 的其余参数传 null 表示"不改那一项",
    // 所以这里不需要把别的开关再抄一遍(抄了反而会在加字段时漏掉)。

    /// <summary>滚动弹幕(mode 1/2/3/6)</summary>
    public bool DanmakuFilterScroll
    {
        get => Svc.Settings.DanmakuFilterScroll;
        set
        {
            if (value == Svc.Settings.DanmakuFilterScroll) return;
            Svc.Settings.UpdateDanmakuSettings(
                Svc.Settings.DanmakuEnabled, Svc.Settings.DanmakuAreaPercent,
                Svc.Settings.DanmakuSmartFilter, filterScroll: value);
            OnPropertyChanged(nameof(DanmakuTypeFilterHint));
            OnPropertyChanged();
        }
    }

    /// <summary>固定弹幕(顶部/底部)</summary>
    public bool DanmakuFilterFixed
    {
        get => Svc.Settings.DanmakuFilterFixed;
        set
        {
            if (value == Svc.Settings.DanmakuFilterFixed) return;
            Svc.Settings.UpdateDanmakuSettings(
                Svc.Settings.DanmakuEnabled, Svc.Settings.DanmakuAreaPercent,
                Svc.Settings.DanmakuSmartFilter, filterFixed: value);
            OnPropertyChanged(nameof(DanmakuTypeFilterHint));
            OnPropertyChanged();
        }
    }

    /// <summary>彩色弹幕(按颜色过滤, 与 DanmakuColorful 的"是否上色"不同)</summary>
    public bool DanmakuFilterColorful
    {
        get => Svc.Settings.DanmakuFilterColorful;
        set
        {
            if (value == Svc.Settings.DanmakuFilterColorful) return;
            Svc.Settings.UpdateDanmakuSettings(
                Svc.Settings.DanmakuEnabled, Svc.Settings.DanmakuAreaPercent,
                Svc.Settings.DanmakuSmartFilter, filterColorful: value);
            OnPropertyChanged(nameof(DanmakuTypeFilterHint));
            OnPropertyChanged();
        }
    }

    /// <summary>高级弹幕(mode 7/8/9)</summary>
    public bool DanmakuFilterAdvanced
    {
        get => Svc.Settings.DanmakuFilterAdvanced;
        set
        {
            if (value == Svc.Settings.DanmakuFilterAdvanced) return;
            Svc.Settings.UpdateDanmakuSettings(
                Svc.Settings.DanmakuEnabled, Svc.Settings.DanmakuAreaPercent,
                Svc.Settings.DanmakuSmartFilter, filterAdvanced: value);
            OnPropertyChanged(nameof(DanmakuTypeFilterHint));
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 类型过滤的说明文案(四个开关上方的提示)。
    /// 全部打开时说"不过滤", 这样用户一眼知道当前是默认态。
    /// </summary>
    public string DanmakuTypeFilterHint =>
        Svc.Settings.DanmakuFilterScroll && Svc.Settings.DanmakuFilterFixed
        && Svc.Settings.DanmakuFilterColorful && Svc.Settings.DanmakuFilterAdvanced
            ? "当前显示全部类型; 关掉某一类即可把它从画面里去掉"
            : "只显示已选中的类型";

    public ICommand ThemeSystemCommand { get; }
    public ICommand ThemeLightCommand { get; }
    public ICommand ThemeDarkCommand { get; }
    public ICommand LoginCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand ClearCacheCommand { get; }
    public ICommand ClearVideoCacheCommand { get; }
    /// <summary>改缓存位置(弹系统选目录对话框)</summary>
    public ICommand ChangeCacheLocationCommand { get; }
    /// <summary>缓存位置恢复默认</summary>
    public ICommand ResetCacheLocationCommand { get; }
    /// <summary>在资源管理器里打开缓存目录</summary>
    public ICommand OpenCacheLocationCommand { get; }

    /// <summary>
    /// 保留"恢复默认"命令的强类型引用。
    /// ★ 它的可用性取决于当前是不是自定义位置(见 RefreshCacheLocation) ——
    ///   WPF 的 ICommand 不会自己重新询问 CanExecute, 必须在状态变化时显式喊一声
    ///   (只声明 canExecute 而不 RaiseCanExecuteChanged 的典型症状: 按钮该亮/该灰时不动)。
    /// </summary>
    private RelayCommand? _resetCacheLocationCommand;
    public ICommand ShowDisclaimerCommand { get; }

    /// <summary>关于页显示的真实地址(绑在副标题上, 让用户点之前就知道会去哪)。
    /// ★ 必须是**实例属性**: WPF 绑定按实例属性描述符查找, static 属性绑不上。</summary>
    public string GitHubRepoUrl => UpdateChecker.RepoPageUrl;
    public string FeedbackUrl => UpdateChecker.IssuesPageUrl;

    /// <summary>打开 GitHub 仓库。2026-11-17 用户要求: 原来的「作者 B 站主页」栏
    /// 改成开源仓库地址 + 反馈入口, 统一走**内置浏览器**(与正文链接同一入口,
    /// 共用 WebView2 Profile, 不用切到系统浏览器)。</summary>
    public ICommand OpenRepoCommand { get; }
    /// <summary>打开 GitHub Issues(反馈入口)。</summary>
    public ICommand OpenFeedbackCommand { get; }

    // ----------------- 问题诊断(2026-11-28 新增) -----------------

    /// <summary>是否开启崩溃转储(见 <see cref="CrashDumpService"/>)。</summary>
    public bool CrashDumpEnabled
    {
        get => Svc.Settings.CrashDumpEnabled;
        set
        {
            if (value == Svc.Settings.CrashDumpEnabled) return;
            var ok = Svc.Settings.SetCrashDumpEnabled(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CrashDumpHint));
            // ★ 能力没落地必须告诉用户 —— 否则界面显示"已开启"、崩了却没有转储,
            //   是最难排查的那种"看起来生效了"。关不掉也要说(说明处理器还装着)。
            if (!ok)
                Svc.Toast.Show(value
                    ? "崩溃转储没能开启(转储目录不可写), 详情已记入日志"
                    : "崩溃转储没能关闭, 详情已记入日志");
        }
    }

    /// <summary>崩溃转储那一行的说明。写清楚"什么时候开、开了会发生什么"。</summary>
    public string CrashDumpHint
    {
        get
        {
            if (!CrashDumpEnabled)
                return "默认关闭。遇到「卡死或闪退」且能稳定复现时打开它, 复现一次后再导出报告 —— " +
                       "崩溃现场会被存成转储文件, 是定位原生崩溃的唯一手段";
            var dir = CrashDumpService.CurrentDumpDirectory() ?? CrashDumpService.DefaultDumpDirectory;
            var count = CrashDumpService.ListDumpFiles(dir).Length;
            return $"已开启。转储文件保存在 {dir}(当前 {count} 个); 崩溃后导出报告时会一并打包";
        }
    }

    /// <summary>导出诊断报告(zip)。</summary>
    public ICommand ExportDiagnosticReportCommand { get; }

    /// <summary>在资源管理器里打开报告所在目录。</summary>
    public ICommand OpenReportFolderCommand { get; }

    /// <summary>
    /// 最近一次导出的报告路径(空 = 还没导过)。
    /// ★ 只给「打开目录」用: 没导过时它落到下载目录, 导过之后落到报告所在的目录 ——
    ///   用户点完"导出"紧接着去"打开目录", 期望看到的就是刚生成的那个文件。
    /// </summary>
    private string _lastReportPath = "";

    /// <summary>导出诊断报告的说明文字。</summary>
    public string ExportReportHint =>
        "把运行日志、播放诊断、崩溃记录和系统信息打包成一个 zip, 存到「下载」文件夹。" +
        "反馈问题时把这个文件一起发出来, 定位会快很多(不含登录凭据与个人信息)";

    /// <summary>
    /// 本次运行的"界面卡住"统计。
    ///
    /// ★ 为什么把它显示给用户看: 用户报"卡住了"时, 最难的是分不清"真卡了"还是"我以为卡了"。
    ///   看门狗是**客观**测出来的(从另一个线程问 UI 线程), 显示出来用户就能自己确认 ——
    ///   顺便也让人知道"这功能一直在跑, 卡了会被记下来"。
    /// </summary>
    public string FreezeStatsText
    {
        get
        {
            var watchdog = Svc.FreezeWatchdog;
            if (watchdog == null) return "界面卡住检测: 未启动";
            var count = watchdog.FreezeCount;
            return count == 0
                ? "界面卡住检测: 本次运行未检测到卡住"
                : $"界面卡住检测: 本次运行检测到 {count} 次卡住(最近一次: {watchdog.LastFreezeSummary})";
        }
    }

    /// <summary>
    /// 导出诊断报告。
    ///
    /// ★ 打包要读几个日志文件 + 事件日志, 都是磁盘 IO ⇒ 放后台线程,
    ///   免得点了按钮界面卡住(日志动辄几百 KB, 事件日志还要解码)。
    /// ★ 导出完**在资源管理器里选中那个文件** —— 用户下一步就是把它发出去,
    ///   直接把文件摆在他眼前比只弹一句"导出成功"有用得多。
    /// </summary>
    private async Task ExportDiagnosticReportAsync()
    {
        try
        {
            var path = await System.Threading.Tasks.Task.Run(() =>
                DiagnosticReportService.Export());
            _lastReportPath = path;
            Svc.Toast.Show("诊断报告已导出: " + path);
            RevealInExplorer(path);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            Svc.Toast.Show("导出诊断报告失败: " + ex.Message);
        }
    }

    /// <summary>在资源管理器里选中文件(失败就退化成打开所在目录)。</summary>
    private static void RevealInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
            {
                Arguments = "/select,\"" + path + "\"",
                UseShellExecute = true
            });
        }
        catch
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    Path.GetDirectoryName(path) ?? AppPaths.DownloadDir)
                {
                    UseShellExecute = true
                });
            }
            catch { /* 打开不了就算了, 路径已经显示给用户了 */ }
        }
    }

    /// <summary>打开上次导出的报告所在目录。</summary>
    private void OpenReportFolder()
    {
        try
        {
            var dir = string.IsNullOrEmpty(_lastReportPath)
                ? AppPaths.DownloadDir
                : Path.GetDirectoryName(_lastReportPath) ?? AppPaths.DownloadDir;
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Svc.Toast.Show("打开目录失败: " + ex.Message);
        }
    }

    public SettingsViewModel()
    {
        ThemeSystemCommand = new RelayCommand(() => ThemeMode = AppTheme.System);
        ThemeLightCommand = new RelayCommand(() => ThemeMode = AppTheme.Light);
        ThemeDarkCommand = new RelayCommand(() => ThemeMode = AppTheme.Dark);

        // 恢复默认 = 落回空串(存"没自定义过", 而不是存 "#FB7299" —— 这样默认色以后调了会跟着变)
        ResetAccentCommand = new RelayCommand(() => AccentColor = "");

        // 设置项搜索(2026-11-28): 结果卡片里每一行带自己的 entry 当 CommandParameter
        ActivateSearchHitCommand = new RelayCommand(
            p => ActivateSearchHit(p as SettingsSearchEntry));

        // 界面字体(2026-11-14): 导入文件 / 恢复默认。下拉选择走 FontSelected(TwoWay)。
        ImportFontCommand = new RelayCommand(ImportFont);
        _resetFontCommand = new RelayCommand(ResetFont,
            // 已经是默认字体时按钮置灰(与"缓存位置恢复默认"同款)
            canExecute: () => !IsDefaultFont);
        ResetFontCommand = _resetFontCommand;

        CheckUpdateCommand = new AsyncRelayCommand(CheckUpdateAsync);

        // 只读查看免责声明。和首次启动那个弹窗是**同一个窗口 + 同一份正文**:
        // 用户事后能翻到的, 就是他当初点"同意"时看到的那一份。
        ShowDisclaimerCommand = new RelayCommand(() =>
        {
            var owner = System.Windows.Application.Current.MainWindow;
            var win = new Views.Windows.DisclaimerWindow(viewer: true)
            {
                Owner = owner,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner
            };
            win.ShowDialog();
        });

        LoginCommand = new RelayCommand(() =>
        {
            var owner = System.Windows.Application.Current.MainWindow;
            var win = new Views.LoginWindow
            {
                Owner = owner,
                WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner
            };
            win.ShowDialog();
        });

        LogoutCommand = new AsyncRelayCommand(LogoutAsync);

        // GitHub 仓库 / 问题反馈(设置页「关于」Tab, 2026-11-17 取代原作者主页入口)
        OpenRepoCommand = new RelayCommand(() => OpenAboutLink(UpdateChecker.RepoPageUrl));
        OpenFeedbackCommand = new RelayCommand(() => OpenAboutLink(UpdateChecker.IssuesPageUrl));

        // 问题诊断(2026-11-28 新增): 一键导出诊断报告 + 打开报告所在目录
        ExportDiagnosticReportCommand = new AsyncRelayCommand(ExportDiagnosticReportAsync);
        OpenReportFolderCommand = new RelayCommand(OpenReportFolder);

        // 立即收进托盘(按钮点一下就能验证"关闭到托盘"是什么效果, 不用真的去点关闭键)
        MinimizeToTrayCommand = new RelayCommand(() => TrayService.Instance.MinimizeToTray());

        // 快捷键: 全部恢复默认(没有覆盖项时按钮置灰)
        _resetAllShortcutsCommand = new RelayCommand(
            ResetAllShortcuts,
            canExecute: () => Svc.Settings.Shortcuts.Count > 0);
        ResetAllShortcutsCommand = _resetAllShortcutsCommand;

        // 原来这里还有"清空本机观看历史" —— 本机历史已整体移除(历史统一走云端), 一并删掉。

        ClearCacheCommand = new AsyncRelayCommand(async () =>
        {
            var freed = await System.Threading.Tasks.Task.Run(() => CoverLoader.ClearDiskCache());
            UpdateCacheSizeText();
            Svc.Toast.Show($"图片缓存已清理, 释放 {FormatBytes(freed)}");
        });

        // 清短视频缓存。删文件要遍历目录, 同样放后台线程 ——
        // 虽然一般只有几十个文件, 但万一用户缓存了几百个, 同步删会在 UI 线程上卡出白屏。
        //
        // 清完把"正在播的那个视频"也一并忘掉是不必要的: 播放器手里握的是文件句柄,
        // 正在播的文件删不掉(Windows 会拦), 所以不会把播放中的视频从脚底下抽走。
        ClearVideoCacheCommand = new AsyncRelayCommand(async () =>
        {
            var freed = await System.Threading.Tasks.Task.Run(() => ShortVideoCache.ClearAll());
            UpdateVideoCacheSizeText();
            Svc.Toast.Show(freed > 0
                ? $"短视频缓存已清理, 释放 {FormatBytes(freed)}"
                : "没有可清理的短视频缓存");
        });

        // 下载位置: 改 / 恢复默认 / 打开目录。三个都只做很小的事 ——
        // 真正的写入在 SettingsStore.SetDownloadDir 里(它会过 AppPaths 的校验)。
        // ★ 不搬文件: 已下载的视频是用户成品, 不该被程序自动挪走。
        ChangeCacheLocationCommand = new RelayCommand(PickCacheLocation);
        _resetCacheLocationCommand = new RelayCommand(ResetCacheLocation,
            // 已经是默认位置时按钮置灰: 点了没有任何效果, 留着反而让人怀疑"点了没反应"
            canExecute: () => IsCustomCacheLocation);
        ResetCacheLocationCommand = _resetCacheLocationCommand;
        OpenCacheLocationCommand = new RelayCommand(OpenCacheLocation);

        SessionManager.Instance.Changed += () => _ = RefreshUserAsync();

        // ★ 缓存位置变化也可能来自别处(将来别的入口/多窗口), 统一在这里跟一次 ——
        //   只靠"点了按钮自己刷", 换个入口就会看到界面上的路径是旧的。
        Svc.Settings.DownloadDirChanged += RefreshCacheLocation;
        _ = RefreshUserAsync();
        UpdateCacheSizeText();
        UpdateVideoCacheSizeText();
        BuildSponsorCategoryOptions();
    }

    /// <summary>
    /// 按 Models 里的类别表建勾选项, 并把设置里存的勾选状态还原上去。
    /// 认不出来的 id(用户手改过 settings.json / 以后服务端加了新类别)直接忽略 ——
    /// 界面按自己认识的这几类显示, 不认识的类别的开关状态保持原样(不会被这次改写抹掉)。
    /// </summary>
    private void BuildSponsorCategoryOptions()
    {
        var enabled = SponsorBlockService.SplitCsv(Svc.Settings.SponsorBlockCategories);
        SponsorCategoryOptions.Clear();
        foreach (var (id, label) in SponsorCategories.All)
            SponsorCategoryOptions.Add(new SponsorCategoryOption(id, label, enabled.Contains(id), OnCategoryOptionChanged));
    }

    /// <summary>进入设置页时刷新</summary>
    public void OnNavigated()
    {
        // 下载位置可能被外部改过(设置页长期缓存着, 不会重建 ViewModel), 进来先同步一次路径。
        RefreshCacheLocation();
        // 两块内部缓存的占用也重新量一次: 播放过程中会新增封面/短视频合流缓存,
        // 而这个 ViewModel 是长期存活的, 只在构造时量一次的话隔天再看就是旧数字。
        UpdateCacheSizeText();
        UpdateVideoCacheSizeText();
        _ = RefreshUserAsync();
        // 快捷键 Tab: 若已经建过列表, 进来时同步一次(覆盖项可能被手改配置或未来别的入口改过)
        if (_shortcutGroupsBuilt) RefreshShortcutRows();
        // 冻结统计与崩溃转储状态都会随运行变化, 进来时同步一次
        OnPropertyChanged(nameof(FreezeStatsText));
        OnPropertyChanged(nameof(CrashDumpHint));
    }

    private void UpdateVideoCacheSizeText()
    {
        VideoCacheSizeText = "计算中…";
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            var (count, bytes) = ShortVideoCache.GetStats();
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                VideoCacheSizeText = count == 0
                    ? "尚无缓存(只对 1 分钟 / 8MB 以内的视频做本地合流)"
                    : $"{count} 个视频 · 共 {FormatBytes(bytes)}");
        });
    }

    private void UpdateCacheSizeText()
    {
        // 缓存目录可能有数千个文件, 枚举大小必须在后台线程做,
        // 否则 UI 线程同步遍历会冻结界面("清缓存卡死"的根因)
        CacheSizeText = "计算中…";
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            var bytes = CoverLoader.GetDiskCacheSize();
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                CacheSizeText = FormatBytes(bytes));
        });
    }

    private static string FormatBytes(long bytes) => ByteSize.Format(bytes);

    public async System.Threading.Tasks.Task RefreshUserAsync()
    {
        IsLoggedIn = Svc.Session.HasLogin;
        if (!IsLoggedIn)
        {
            UserName = "";
            UserFace = "";
            UserLevel = 0;
            UserCoins = 0;
            return;
        }

        var (_, _, user) = await Svc.Api.GetUserStateAsync();
        if (user is { IsLogin: true })
        {
            UserName = user.Name;
            UserFace = user.Face;
            UserLevel = user.Level;
            UserCoins = user.Coins;
        }
        else
        {
            UserName = "(登录状态可能已过期)";
        }
    }

    /// <summary>退出登录: 清除会话 + 清除 WebView2 中的 Cookie</summary>
    private async System.Threading.Tasks.Task LogoutAsync()
    {
        if (!ConfirmDialog.Show(
                "退出登录后需要重新扫码才能登录, 确定退出吗?",
                "退出登录", "退出登录"))
            return;

        Svc.Session.Clear();
        try
        {
            // ★ 2026-11-07 P4: 临时建一个隐藏 WebView2 去清 Profile 里的 Cookie, 用完 Dispose
            //   (不再依赖主窗口里那个常驻的 1×1 控件 —— 它已经删掉了)。
            await Svc.WebView2.WithHiddenAsync(wv => Svc.WebView2.ClearProfileCookiesAsync(wv));
        }
        catch
        {
            // 清理失败不影响本地退出
        }
        await RefreshUserAsync();
    }
}

/// <summary>
/// 设置页里"跳过赞助片段"的一个类别勾选项。
///
/// 单独做一个 ViewModel 项(而不是直接在 XAML 里绑字符串集合的 CheckBox):
/// 勾选状态要能回写、还要触发"重新拼 category 串 + 落盘 + 刷新说明文字",
/// 这三件事需要一个带变更通知的对象来承载。
/// </summary>
public sealed class SponsorCategoryOption : ObservableObject
{
    private readonly Action _onChanged;
    private bool _isChecked;

    public SponsorCategoryOption(string id, string label, bool isChecked, Action onChanged)
    {
        Id = id;
        Label = label;
        _isChecked = isChecked;
        _onChanged = onChanged;
    }

    /// <summary>服务端的 category 原值(存进设置用的就是它)</summary>
    public string Id { get; }

    /// <summary>界面显示的中文名</summary>
    public string Label { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            // 构造时赋初值不走通知, 也不该触发落盘
            if (!SetProperty(ref _isChecked, value)) return;
            _onChanged();
        }
    }
}

/// <summary>
/// 界面字体下拉的一项(2026-11-14): 显示名 + 存储值 + 预览字体。
/// ★ Preview 让每一项用自己的字体渲染(所见即所得); 「默认」项用默认字体栈。
/// ★ 相等性只看 Stored: 下拉重建后新旧实例仍能匹配成同一项 ——
///   否则 ComboBox.SelectedItem 在重建后找不到相等项, 选中显示会落空。
/// </summary>
public sealed class FontOption : IEquatable<FontOption>
{
    public FontOption(string label, string stored, FontFamily preview)
    {
        Label = label;
        Stored = stored;
        Preview = preview;
    }

    /// <summary>界面显示名(系统字体优先中文名; 导入项带"（已导入）"后缀)</summary>
    public string Label { get; }

    /// <summary>存储值(空串 = 默认; 字体名 = 系统字体; "file:目录名" = 导入), 见 Helpers.UiFont</summary>
    public string Stored { get; }

    /// <summary>预览用字体(下拉项与选中框都用它渲染)</summary>
    public FontFamily Preview { get; }

    public bool Equals(FontOption? other)
        => other != null && string.Equals(Stored, other.Stored, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as FontOption);

    public override int GetHashCode() => Stored.GetHashCode();

    /// <summary>
    /// ★ 必须重写: SettingsComboStyle 关闭态的选中框是 `Text="{Binding SelectionBoxItem}"`
    ///   (为字符串数组写的模板), 对象项不重写就会显示成类名。下拉项本身仍走 ItemTemplate
    ///   用各自字体渲染; 选中框显示字体名(用当前界面字体), 不改共享模板免得波及其它下拉。
    /// </summary>
    public override string ToString() => Label;
}
