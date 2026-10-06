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
    public string AcrylicMaterialHint => Svc.Settings.AcrylicMaterial switch
    {
        BackdropMaterial.Acrylic => "亚克力: 半透明磨砂, 实时透出桌面内容；窗口失焦时改用主题底色(系统限制)",
        BackdropMaterial.Mica => "云母: 不透明, 只取壁纸色调 —— 不会透出后面的窗口",
        BackdropMaterial.MicaAlt => "云母 Alt: 不透明, 层次比云母更平",
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

    // ----------------- 推荐算法(已固定为网页版, 不再提供选择) -----------------
    //
    // ★ 2026-10-03 删除了"算法选择"(原来可在 App 官方 / 浏览器网页版之间切换)与整套
    //   App 令牌机制, 原因见 ApiClient.GetRecommendAsync 的说明:
    //   App 端接口(app.bilibili.com/x/v2/feed/index)对第三方客户端**不提供个性化** ——
    //   即便拿到有效 access_key, 返回的仍是全站通用热门池(实测 100 条里命中关注的 UP
    //   只有 1~2 个、平均播放量 66~103 万), 而网页 rcmd 平均只有 17 万、明显更贴口味。
    //   留着那个选项只会让用户以为"App 算法"能更准, 是误导。
    //   RecommendSource 枚举与 SettingsStore 字段保留(老配置反序列化不会炸), 但读取处
    //   一律走网页路。

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
            ? "每次起播自动取当前账号可用的最高清晰度"
            : $"每次起播都请求 {QualityPreference.LabelOf(Svc.Settings.PreferredQualityQn)}; " +
              "该清晰度不存在或没有权限时自动降到可用的下一档";

    // ----------------- CDN(线路)设置 -----------------
    // 逻辑见 Services/CdnService.cs; 实测依据见该文件顶部注释

    /// <summary>线路策略下拉框的显示项(顺序即 CdnSelectMode 的取值顺序)</summary>
    public string[] CdnModeOptions { get; } = { "自动测速", "手动指定", "跟随服务端" };

    /// <summary>当前策略(用字符串投影, 理由同 ThemeSelected —— 服务对象没有变化通知)</summary>
    public string CdnModeSelected
    {
        get => CdnModeOptions[(int)Svc.Settings.CdnMode];
        set
        {
            var idx = Array.IndexOf(CdnModeOptions, value);
            if (idx < 0) return;
            var mode = (CdnSelectMode)idx;
            if (mode == Svc.Settings.CdnMode) return;
            Svc.Settings.SetCdn(mode, Svc.Settings.CdnManualId, Svc.Settings.BlockPcdn);
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

    /// <summary>最近一次测速结果的文字说明(让用户看到"测了什么、多快")</summary>
    public string CdnSpeedReport => Svc.Cdn.LastReport;

    /// <summary>正在测速时的提示(按钮防连点 + 反馈)</summary>
    public string CdnTestStatus
    {
        get => _cdnTestStatus;
        private set => SetProperty(ref _cdnTestStatus, value);
    }
    private string _cdnTestStatus = "";

    /// <summary>
    /// 手动触发一轮测速。
    ///
    /// 用途: 换了网络(切 Wi-Fi / 换运营商)后排名会变, 而缓存有 10 分钟 ——
    /// 给用户一个"立刻重测"的按钮, 比等缓存过期或重启程序友好。
    /// </summary>
    public ICommand TestCdnCommand { get; }

    private async System.Threading.Tasks.Task TestCdnAsync()
    {
        if (_testingCdn) return;
        _testingCdn = true;
        CdnTestStatus = "正在测速…";
        try
        {
            // 借一次真实取流拿到带签名的候选 URL 当测速样本
            var (ok, err, items) = await Svc.Api.GetPopularAsync(1);
            var first = items?.FirstOrDefault();
            if (!ok || first == null)
            {
                CdnTestStatus = "测速失败: 拿不到测试视频" + (string.IsNullOrEmpty(err) ? "" : $" ({err})");
                return;
            }
            var (dOk, dErr, detail) = await Svc.Api.GetVideoAsync(first.Bvid);
            if (!dOk || detail == null || detail.Cid <= 0)
            {
                CdnTestStatus = "测速失败: 拿不到视频信息" + (string.IsNullOrEmpty(dErr) ? "" : $" ({dErr})");
                return;
            }

            var sample = await Svc.Api.GetCdnSampleUrlsAsync(first.Bvid, detail.Cid);
            if (sample.Count == 0)
            {
                CdnTestStatus = "测速失败: 没有可用的测速样本";
                return;
            }

            Svc.Cdn.InvalidateCache();
            var ranked = await Svc.Cdn.RankAsync(sample);
            CdnTestStatus = ranked.Count == 0
                ? "测速失败: 候选线路都不可达"
                : $"测速完成, 最快 {ranked[0].BytesPerSecond / 1024}KB/s";
            OnPropertyChanged(nameof(CdnSpeedReport));
        }
        catch (Exception ex)
        {
            CdnTestStatus = "测速异常: " + ex.Message;
        }
        finally
        {
            _testingCdn = false;
        }
    }
    private bool _testingCdn;

    /// <summary>CDN 相关属性整组刷新(切换模式会影响联动项, 一次全推开免漏)</summary>
    private void RaiseCdnChanged()
    {
        OnPropertyChanged(nameof(CdnModeSelected));
        OnPropertyChanged(nameof(CdnHostSelected));
        OnPropertyChanged(nameof(CanPickCdn));
        OnPropertyChanged(nameof(BlockPcdn));
        OnPropertyChanged(nameof(CdnSpeedReport));
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

        // CDN 手动重测: 换网络后排名会变, 而自动测速结果有 10 分钟缓存
        TestCdnCommand = new AsyncRelayCommand(TestCdnAsync);

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
