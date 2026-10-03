using System;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.ViewModels;

/// <summary>设置页 ViewModel: 外观 + 账户 + 数据 + 关于</summary>
public class SettingsViewModel : ObservableObject
{
    /// <summary>作者 B 站 UID(设置页「关于」Tab 的作者栏)。与导航服务共用。</summary>
    private const long AuthorMid = 697238372;

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

    // ----------------- 顶部 Tab(2026-10-03 重做) -----------------

    private int _tabIndex;

    /// <summary>当前选中 Tab 的序号(0=通用 1=播放 2=数据管理 3=关于)。
    /// 页面用它切内容; 四个 bool 属性是给 XAML 胶囊 Tab 双向绑定的投影。</summary>
    public int TabIndex
    {
        get => _tabIndex;
        set
        {
            if (!SetProperty(ref _tabIndex, value)) return;
            OnPropertyChanged(nameof(IsGeneralTab));
            OnPropertyChanged(nameof(IsPlaybackTab));
            OnPropertyChanged(nameof(IsDataTab));
            OnPropertyChanged(nameof(IsAboutTab));
        }
    }

    /// <summary>
    /// Tab 投影。★ **只写 true**: RadioButton 组里的一个被勾上时, WPF 会先把**上一个**
    /// Radio 的 IsChecked 置 false —— TwoWay 绑定把 false 也写进来, 如果无条件执行
    /// TabIndex = 0, 刚切到的 Tab 会被立刻盖回去, 表现就是"四个 Tab 都点不动"。
    /// false 只来自"取消勾选", 目标 Tab 自己的 true 会跟着来, 忽略即可。
    /// </summary>
    public bool IsGeneralTab  { get => TabIndex == 0; set { if (value) TabIndex = 0; } }
    public bool IsPlaybackTab { get => TabIndex == 1; set { if (value) TabIndex = 1; } }
    public bool IsDataTab     { get => TabIndex == 2; set { if (value) TabIndex = 2; } }
    public bool IsAboutTab    { get => TabIndex == 3; set { if (value) TabIndex = 3; } }

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
    /// ★ 拖动中传 settle: false 跳过那次全窗口可视树重解析 —— 拖动每秒来几十次, 每次都遍历
    ///   一遍可视树是白烧的帧(笔刷本身换了, 眼前这一屏立刻就变色), 松手时再补一次
    ///   (见 <see cref="EndAccentDrag"/> 与控件的 drag 结束回调)。
    /// </summary>
    public Color AccentPickerColor
    {
        get => Svc.Theme.Accent;
        set
        {
            if (value == Svc.Theme.Accent) return;
            Svc.Theme.SetAccent(ThemeService.ToHex(value), settle: !_draggingAccent);
            RaiseAccentChanged();
        }
    }

    /// <summary>调色盘是否正在被拖动(决定换色要不要立刻做全量重解析, 见 AccentPickerColor)</summary>
    private bool _draggingAccent;

    /// <summary>调色盘开始 / 结束拖动。结束时补做一次被拖期间省掉的重解析。</summary>
    public void BeginAccentDrag() => _draggingAccent = true;

    public void EndAccentDrag()
    {
        if (!_draggingAccent) return;
        _draggingAccent = false;
        Svc.Theme.Settle();
    }

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

    public string[] RecommendOptions { get; } = { "B 站官方 App", "浏览器网页版" };

    public string RecommendSelected
    {
        get => RecommendOptions[(int)Svc.Settings.RecommendSource];
        set
        {
            var idx = Array.IndexOf(RecommendOptions, value);
            if (idx < 0 || (int)Svc.Settings.RecommendSource == idx) return;
            RecommendSource = (RecommendSource)idx;
            OnPropertyChanged();
        }
    }

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

    /// <summary>推荐算法下拉框的投影(RecommendSource 枚举 0=App 1=Web)。</summary>
    public int RecommendSourceInt
    {
        get => (int)Svc.Settings.RecommendSource;
        set
        {
            if ((int)Svc.Settings.RecommendSource == value) return;
            RecommendSource = (RecommendSource)value;
        }
    }

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
    /// <summary>打开作者 B 站主页。旧版是页面 code-behind 的 Click 处理器;
    /// 2026-10-03 重做后页面内容整体搬进 ResourceDictionary(模板里不能挂事件),
    /// 所以改成命令。与原实现同一条路: NavigationDispatcher.OpenUserSpace。</summary>
    public ICommand OpenAuthorCommand { get; }

    public SettingsViewModel()
    {
        ThemeSystemCommand = new RelayCommand(() => ThemeMode = AppTheme.System);
        ThemeLightCommand = new RelayCommand(() => ThemeMode = AppTheme.Light);
        ThemeDarkCommand = new RelayCommand(() => ThemeMode = AppTheme.Dark);

        // 恢复默认 = 落回空串(存"没自定义过", 而不是存 "#FB7299" —— 这样默认色以后调了会跟着变)
        ResetAccentCommand = new RelayCommand(() => AccentColor = "");

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

        // 作者主页(设置页「关于」Tab)。与原 code-behind 同一入口。
        OpenAuthorCommand = new RelayCommand(() =>
        {
            try
            {
                var dispatcher = Svc.Navigate
                    ?? new NavigationDispatcher(System.Windows.Application.Current.MainWindow);
                dispatcher.OpenUserSpace(AuthorMid.ToString());
            }
            catch (Exception ex)
            {
                Svc.Toast.Show("打开作者主页失败: " + ex.Message);
            }
        });

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