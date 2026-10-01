using System;
using System.Linq;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

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

    public bool IsLoggedIn { get => _isLoggedIn; private set => SetProperty(ref _isLoggedIn, value); }
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
    /// 「关于」卡片里软件名下面那一行的副标题: 只写技术栈。
    /// 不在这里重复版本号 —— 卡片下方本来就有独立的「版本号」一栏,
    /// 两处都写版本会显得啰嗦, 而且改版本时要同步的地方多一处。
    /// </summary>
    public string RuntimeInfo => ".NET 8 / WPF";

    /// <summary>「关于 → 版本号」一栏右侧显示的版本(与 App.AppVersion 单一来源, 别在这里写死)</summary>
    public string VersionNumber => "v" + App.AppVersion;

    public AppTheme ThemeMode
    {
        get => Svc.Theme.Mode;
        set => Svc.Theme.SetMode(value);
    }

    /// <summary>
    /// 启动时打开哪个页面。
    /// 只给导航栏上的 4 项(首页/动态/我的/设置)选择 —— 离线缓存 / 消息 / 搜索没有导航入口,
    /// 设成启动页会"有进无出"; 历史与收藏虽在 PageKey 里, 但已收进「我的」页, 同样不在候选内。
    /// </summary>
    public PageKey StartupPage
    {
        get => MainViewModel.ParseStartupPage(Svc.Settings.StartupPage);
        set
        {
            // 越界/非法值一律当首页, 免得从界面写进一个解析不出来的名字
            var safe = MainViewModel.NavPages.Contains(value) ? value : PageKey.Home;
            Svc.Settings.SetStartupPage(safe.ToString());
            OnPropertyChanged();
            OnPropertyChanged(nameof(StartupPageHint));
        }
    }

    public string StartupPageHint => StartupPage == PageKey.Home
        ? "下次启动直接进首页"
        : $"下次启动直接进「{StartupPageName(StartupPage)}」";

    /// <summary>
    /// PageKey → 界面名。
    /// History / Favorites 两个分支目前命中不到(启动页被 NavPages 限制在 4 项内),
    /// 但它们仍留在 PageKey 里、也在别处当导航目标用 —— 留着是为了以后启动页选项放开时不必再想一遍译名。
    /// </summary>
    private static string StartupPageName(PageKey key) => key switch
    {
        PageKey.Follow => "关注",
        PageKey.History => "历史记录",
        PageKey.Favorites => "收藏",
        PageKey.Settings => "设置",
        _ => "首页"
    };

    // ----------------- 推荐算法 -----------------

    /// <summary>
    /// 首页「推荐」用哪套算法(App 官方 / 浏览器网页版)。
    /// 直接读写 SettingsStore —— 换算法要立刻落盘, 不然改完重启又变回去了。
    /// </summary>
    public RecommendSource RecommendSource
    {
        get => Svc.Settings.RecommendSource;
        set
        {
            Svc.Settings.SetRecommendSource(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(RecommendSourceHint));
        }
    }

    /// <summary>当前算法的一句话说明(切完立刻能看到它变了, 不用猜有没有生效)</summary>
    public string RecommendSourceHint => Svc.Settings.RecommendSource == RecommendSource.App
        ? "与手机 App 首页推荐一致 · 每次刷新给你全新的一批 10 条"
        : "与网页版 bilibili.com 首页推荐一致 · 每次最多 30 条";

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
        ? "启动后在后台静默检查一次, 有新版本才弹窗提醒"
        : "已关闭, 仍可随时点「检查更新」手动检查";

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

    public bool DanmakuSmartFilter
    {
        get => Svc.Settings.DanmakuSmartFilter;
        set
        {
            Svc.Settings.UpdateDanmakuSettings(Svc.Settings.DanmakuEnabled, Svc.Settings.DanmakuAreaPercent, value);
            OnPropertyChanged();
        }
    }

    /// <summary>彩色弹幕。关掉之后所有弹幕统一白色(有些视频满屏五颜六色反而看不清内容)</summary>
    public bool DanmakuColorful
    {
        get => Svc.Settings.DanmakuColorful;
        set
        {
            Svc.Settings.UpdateDanmakuSettings(
                Svc.Settings.DanmakuEnabled,
                Svc.Settings.DanmakuAreaPercent,
                Svc.Settings.DanmakuSmartFilter,
                colorful: value);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 弹幕关键词屏蔽(用户手写黑名单)。
    /// 用 TextChanged 慢一点也关系不大: 每敲一个字就重算规则并让播放器重新过滤,
    /// 体感上是"边打边生效"。实时过滤只作用于已加载的弹幕列表, 不发新请求。
    /// </summary>
    public string DanmakuBlockKeywords
    {
        get => Svc.Settings.DanmakuBlockKeywords;
        set
        {
            if (value == Svc.Settings.DanmakuBlockKeywords) return;
            Svc.Settings.UpdateDanmakuSettings(
                Svc.Settings.DanmakuEnabled,
                Svc.Settings.DanmakuAreaPercent,
                Svc.Settings.DanmakuSmartFilter,
                value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(DanmakuRuleCountText));
        }
    }

    /// <summary>当前生效的屏蔽规则条数(让用户确认自己的输入被解析成了几条规则)</summary>
    public string DanmakuRuleCountText
    {
        get
        {
            var n = BiliDesk.Helpers.DanmakuKeywordFilter.Build(Svc.Settings.DanmakuBlockKeywords)?.Count ?? 0;
            return n == 0 ? "尚未设置屏蔽规则" : $"已启用 {n} 条屏蔽规则";
        }
    }

    public ICommand ThemeSystemCommand { get; }
    public ICommand ThemeLightCommand { get; }
    public ICommand ThemeDarkCommand { get; }
    public ICommand LoginCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand ClearCacheCommand { get; }
    public ICommand ClearVideoCacheCommand { get; }
    public ICommand ShowDisclaimerCommand { get; }

    public SettingsViewModel()
    {
        ThemeSystemCommand = new RelayCommand(() => ThemeMode = AppTheme.System);
        ThemeLightCommand = new RelayCommand(() => ThemeMode = AppTheme.Light);
        ThemeDarkCommand = new RelayCommand(() => ThemeMode = AppTheme.Dark);

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

        // 立即收进托盘(按钮点一下就能验证"关闭到托盘"是什么效果, 不用真的去点关闭键)
        MinimizeToTrayCommand = new RelayCommand(() => TrayService.Instance.MinimizeToTray());

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

        SessionManager.Instance.Changed += () => _ = RefreshUserAsync();
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
        // 短视频缓存是"看过视频才会长"的, 每次进设置页都重新量一次
        UpdateCacheSizeText();
        UpdateVideoCacheSizeText();
        _ = RefreshUserAsync();
    }

    private void UpdateVideoCacheSizeText()
    {
        VideoCacheSizeText = "计算中…";
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            var (count, bytes) = ShortVideoCache.GetStats();
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                VideoCacheSizeText = count == 0
                    ? "尚无缓存(只对 3 分钟以内的视频做本地合流)"
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

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1 << 30) return (bytes / 1073741824.0).ToString("0.00") + " GB";
        if (bytes >= 1 << 20) return (bytes / 1048576.0).ToString("0.0") + " MB";
        if (bytes >= 1 << 10) return (bytes / 1024.0).ToString("0") + " KB";
        return bytes + " B";
    }

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
        var r = System.Windows.MessageBox.Show(
            "退出登录后需要重新扫码才能登录, 确定退出吗?",
            "BiliDesk",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (r != System.Windows.MessageBoxResult.Yes) return;

        Svc.Session.Clear();
        try
        {
            await Svc.WebView2.ClearProfileCookiesAsync(SvcWindow.HiddenWebView);
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