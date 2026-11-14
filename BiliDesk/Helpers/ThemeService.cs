using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BiliDesk.Models;
using BiliDesk.Services;
using Microsoft.Win32;

namespace BiliDesk.Helpers;

/// <summary>
/// 主题服务: 跟随系统深浅色自动切换 / 手动浅色 / 手动深色, 并注入强调色笔刷。
/// 强调色默认 B 站粉, 用户可以在设置页自己调, 也可以选"跟随系统"用 Windows 的强调色。
///
/// ★★★ 2026-11-08 换肤机制重做(用户第三次报"下滑加载视频后切深浅色卡顿 + 内存上涨")。
///
///   【旧机制为什么卡、为什么涨】
///   实测(探针 bd-probe-themevideo, 500 张卡 / 13521 个可视节点 / 1480x830@125%):
///   <code>
///     ThemeService.Apply() 总计            153ms
///       ├ 载入色板字典(每次 new + 解析 BAML)  74ms   ← 最大头, 每次点击都重来
///       ├ 整本替换动态字典                   24ms
///       ├ RefreshResourceReferences(全树)    13ms
///       └ Settle()(又一次全树)              14ms
///     点击 → 动画真正开始的同步段        中位 697ms / 最大 1327ms
///   </code>
///   旧机制是"换肤 = 整本替换色板字典"。它有两个结构性后果:
///     ① 每次点一下都要重新解析一遍 XAML 色板 + 新建两本字典 —— 开销随卡片数线性增长,
///        这正是"往下刷过视频之后才卡";
///     ② 字典每换一次就 new 一本。只要还有**一个**控件持有指向旧字典的 DynamicResource
///        表达式, 那本旧字典就整本活着 —— 内存随点击次数上涨。
///        而 `RefreshResourceReferences` + `Settle()` 那两遍全树 InvalidateProperty 的存在
///        本身就是这个缺陷的症状: 它们必须存在, 恰恰说明换字典的方式漏掉了引用。
///
///   【新机制: 改共享笔刷的 Color, 一个字节的字典结构都不动】
///     ① 启动时把两张色板解析成 `key → Color`(一次性), 之后不再碰 XAML;
///     ② 在 Application.Resources 里挂**一本持久字典**, 里面全是**可变** SolidColorBrush;
///     ③ 换肤 = 给这些笔刷的 Color 挂 ColorAnimation, 240ms 平滑插值。
///   收益:
///     · 没有 new 字典 / 没有 Insert / 没有 Remove ⇒ **结构上不可能泄漏字典**;
///     · 笔刷是**同一实例**, 改色后所有引用它的元素自动重绘 —— 不需要逐控件 InvalidateProperty,
///       也就不存在"缓存页面仍持有旧色"这个换字典方案独有的坑;
///     · 上万节点的重绘被摊到动画的每一帧, 不再是一段 500ms+ 的同步死时间。
///
///   ★★★ 【关键实现约束: 必须给笔刷预挂一个"恒等动画"】—— 别删, 这是整个方案能成立的前提。
///     WPF 会把**放进资源字典、且被 Style 的 Setter 消费**的 Freezable **自动 Freeze**
///     (全局资源的性能优化)。实测(临时探针 frz):
///     <code>
///       元素直接 SetResourceReference           IsFrozen=False  ✅
///       Style Setter + DynamicResource          IsFrozen=True   ❌
///       Application.Resources 直接项            IsFrozen=True   ❌
///     </code>
///     本工程有 156 处 `DynamicResource` 写在 Style 的 Setter 里 —— 所以"裸的可变笔刷"
///     一挂上去就被冻死, 之后 `brush.Color = x` 和 `BeginAnimation` 全部抛
///     InvalidOperationException。
///     破解办法(实测有效): **先给笔刷挂一个无限循环的恒等 ColorAnimation** ——
///     `CanFreeze` 会变成 false, WPF 就不再冻结它; 换色时用新的 BeginAnimation 覆盖它即可
///     (实测控件颜色真的从红变蓝)。
///     所以 <see cref="CreateLiveBrush"/> 里那个 `Prime` 动画不是装饰, 是**防冻结锁**。
/// </summary>
public class ThemeService
{
    public static ThemeService Instance { get; } = new();

    /// <summary>主题切换事件(所有窗口监听以更新自身)</summary>
    public event Action? ThemeChanged;

    public AppTheme Mode { get; private set; } = AppTheme.System;
    public bool IsDark { get; private set; }
    public bool IsSystemDark { get; private set; }

    // ---------------------------------------------------------------- 强调色

    /// <summary>
    /// 默认强调色: B 站粉。用户没自定义主题色时全应用统一使用它。
    ///
    /// 深浅两套主题用**同一个**粉色, 不做"深色下提亮"的调整 —— 粉色在深色底上是 5.2:1、
    /// 在白色卡片上 2.6:1, 两边都看得清, 调了反而不统一。
    /// </summary>
    public static readonly Color DefaultAccent = Color.FromRgb(0xFB, 0x72, 0x99);

    /// <summary>
    /// 设置里表示"强调色跟随系统"的哨兵值。
    /// 和颜色值塞进同一个字段(`SettingsStore.AccentColor`)而不是另开一个 bool: 这样
    /// "跟随系统"和"某个固定颜色"天然互斥, 不会出现两个字段互相打架、谁优先说不清的状态。
    /// </summary>
    public const string AccentSystem = "system";

    /// <summary>最近一次读到的系统强调色(读不到就是默认粉)</summary>
    private Color _systemAccent = DefaultAccent;

    /// <summary>
    /// 当前主题色。= 用户选的固定色 / 跟随系统时读到的系统强调色; 没设置过就是
    /// <see cref="DefaultAccent"/>。深浅两套主题共用同一个值。
    /// </summary>
    public Color Accent { get; private set; } = DefaultAccent;

    /// <summary>当前是不是"强调色跟随系统"</summary>
    public bool IsAccentSystem => IsSystemSentinel(Svc.Settings.AccentColor);

    /// <summary>当前是不是"从没自定义过"(存储里是空串 = 默认 B 站粉)</summary>
    public bool IsDefaultAccent => !IsAccentSystem && Svc.Settings.AccentColor.Length == 0;

    private static bool IsSystemSentinel(string? v)
        => string.Equals(v?.Trim(), AccentSystem, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 设置强调色。三种输入:
    ///   null / 空串 / 解析不了 -> 恢复默认 B 站粉(而不是把界面刷成透明或黑色)
    ///   <see cref="AccentSystem"/> -> 跟随系统强调色
    ///   `#RRGGBB` / `#AARRGGBB`(不写 # 也行) -> 用这个颜色
    ///
    /// ★ 强调色是**直接赋值**、不做过渡: 调色盘拖动时每秒来几十次, 每次都插值只会让颜色
    ///   拖在手指后面。深浅色切换才需要过渡(那是整屏换色, 突兀感明显)。
    /// </summary>
    public void SetAccent(string? value)
    {
        if (IsSystemSentinel(value))
        {
            Svc.Settings.AccentColor = AccentSystem;
            ReadSystemAccent();
            Accent = _systemAccent;
        }
        else
        {
            var next = ParseAccent(value) ?? DefaultAccent;
            Accent = next;
            // ★ 存的是"用户选了什么"而不是"颜色值": 等于默认色就存空串(= 跟着默认走),
            //   默认色以后要是调了, 没自定义过的用户会跟着变 —— 这正是我们要的。
            //   (注意必须放在 else 里: 跟随系统时即使系统色正好等于默认粉, 也不能被压成空串,
            //    否则"跟随系统"这个选择会被悄悄丢掉。)
            Svc.Settings.AccentColor = next == DefaultAccent ? "" : ToHex(next);
        }

        Svc.Settings.Save();
        ApplyAccentColors();
    }

    /// <summary>
    /// 读 Windows 的强调色(设置 → 个性化 → 颜色 → 强调色)。
    ///
    /// ★ 值存在 `HKCU\Software\Microsoft\Windows\DWM\AccentColor`, 格式是 **AABBGGRR**
    ///   (高字节 alpha, 低字节红 —— 和 COLORREF 一样是"反的")。直接当 AARRGGBB 读会拿到反色:
    ///   本机实测 0xFFD47800 按 ABGR 解是标准的 Windows 蓝 #0078D4, 按 ARGB 解会变成橙色 #D47800。
    ///   `AccentPalette`(同键下的 8 段色阶)也是同样的字节序, 可以拿来交叉验证。
    ///
    /// 不用 WPF 的 `SystemParameters.WindowGlassColor`: 那个属性第一次取值会初始化整套系统主题
    /// 信息, 有实际开销; 而且它反映的是"标题栏玻璃色"(会被透明/亚克力效果再加工), 不是用户选的
    /// 那个强调色。这里只读一个注册表值, 便宜且就是用户看到的那一个。
    /// </summary>
    private void ReadSystemAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int raw)
            {
                var v = unchecked((uint)raw);
                // alpha 为 0 = 没有有效值(某些精简系统/未设置过), 当作读不到
                if ((v >> 24) != 0)
                {
                    _systemAccent = Color.FromRgb(
                        (byte)(v & 0xFF),
                        (byte)((v >> 8) & 0xFF),
                        (byte)((v >> 16) & 0xFF));
                    return;
                }
            }
        }
        catch
        {
            // 读不到就用默认粉, 不影响功能
        }
        _systemAccent = DefaultAccent;
    }

    /// <summary>把设置里存的值解析成颜色; 空/非法返回 null(= 用默认色)</summary>
    private static Color? ParseAccent(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try
        {
            var text = hex.Trim().TrimStart('#');
            if (text.Length == 6) text = "FF" + text;
            if (text.Length != 8) return null;
            return Color.FromArgb(
                Convert.ToByte(text.Substring(0, 2), 16),
                Convert.ToByte(text.Substring(2, 2), 16),
                Convert.ToByte(text.Substring(4, 2), 16),
                Convert.ToByte(text.Substring(6, 2), 16));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>颜色 → 设置里存/界面显示的 `#RRGGBB`</summary>
    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>
    /// 放在强调色上的文字 / 图标颜色。
    /// 默认粉底白字是 B 站的标准搭配; 用户自定义 / 跟随系统的主题色深浅不定, 所以按**对比度**
    /// 挑黑或白 —— 亮黄/浅绿上用白字会糊成一片(系统强调色也可能是浅色)。
    /// </summary>
    private static Color OnAccentFor(Color accent)
    {
        // 相对亮度(WCAG 的近似式)。>0.6 视为浅色底, 用深色字。
        var r = accent.R / 255.0;
        var g = accent.G / 255.0;
        var b = accent.B / 255.0;
        double Lin(double v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        var luminance = 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
        return luminance > 0.6 ? Color.FromRgb(0x20, 0x20, 0x20) : Colors.White;
    }

    // ---------------------------------------------------------------- 笔刷载体

    /// <summary>
    /// 全应用**唯一**的一本动态字典 —— 启动时建一次, 之后只改里面笔刷的 Color。
    /// 挂在 MergedDictionaries 的**最后**: 后添加的字典优先级更高, 才能盖过
    /// `Themes/Colors.*.xaml` 与 `Themes/Accent.xaml` 里的同名键。
    /// </summary>
    private ResourceDictionary? _dict;

    /// <summary>键 → 我们自己的可变笔刷。整本字典只创建一次, 这里只是按名字索引。</summary>
    private readonly Dictionary<string, SolidColorBrush> _brushes = new(StringComparer.Ordinal);

    /// <summary>浅色 / 深色两张色板的 `key → Color`(启动时读一次, 之后不再碰 XAML)</summary>
    private readonly Dictionary<string, Color> _light = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Color> _dark = new(StringComparer.Ordinal);
    private bool _palettesLoaded;

    /// <summary>
    /// 颜色过渡时长。240ms 与 Windows 自己的深浅色切换同量级:
    /// 再短会显得生硬(整屏换色跳变很刺眼), 再长会拖慢操作感。
    /// </summary>
    private const int FadeMs = 240;

    /// <summary>当前过渡的代号。连点换肤时用它作废旧动画的收尾回调。</summary>
    private int _fadeEpoch;

    /// <summary>
    /// 是否还有一次深浅色过渡在跑(供探针/调试观察)。
    ///
    /// ★ 为什么不让调用方读笔刷的 `HasAnimatedProperties`: 我们的笔刷上**永远**挂着那个
    ///   防冻结的恒等动画(见类注释), 所以那个标志恒为 true, 用它判断会永远等下去。
    ///   这里用收尾回调 + 兜底定时器自己记账。
    /// </summary>
    public bool IsFading { get; private set; }

    public void Initialize()
    {
        Mode = Svc.Settings.ThemeMode;
        // 只有"跟随系统"才去读注册表 —— 默认那条路上一次系统调用都不用发。
        if (IsAccentSystem)
        {
            ReadSystemAccent();
            Accent = _systemAccent;
        }
        else
        {
            Accent = ParseAccent(Svc.Settings.AccentColor) ?? DefaultAccent;
        }
        ReadSystemTheme();
        LoadPalettes();
        Apply(animate: false);
        HookSystemEvents();
    }

    private void HookSystemEvents()
    {
        try
        {
            // Windows 主题改变时(浅色/深色切换)会触发该事件
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category != UserPreferenceCategory.General) return;
                RunOnUi(() =>
                {
                    // 只在"跟随系统"模式下才有必要重算; CheckSystemThemeChanged 内部会判断
                    CheckSystemThemeChanged();
                    CheckSystemAccentChanged();
                    // ★ 换壁纸也走这个事件: 云母的页面底是**我们按壁纸算出来的**,
                    //   缓存不失效的话换了壁纸色调还停在旧的那张上。DesktopTint 内部有缓存。
                    DesktopTint.Invalidate();
                    RefreshSurfaces();
                });
            };
        }
        catch
        {
            // 订阅失败不影响功能, 应用内仍可手动切换
        }

        // 亚克力开关切换: 基底那几层的透明度要跟着换(见 ComputeSurfaceColors)。
        Svc.Settings.AcrylicChanged += () => RunOnUi(RefreshSurfaces);
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    /// <summary>系统主题变化时重新应用</summary>
    private void CheckSystemThemeChanged()
    {
        var old = IsSystemDark;
        ReadSystemTheme();
        if (old == IsSystemDark || Mode != AppTheme.System) return;
        Apply();
    }

    /// <summary>
    /// 系统强调色变化时重新应用(只在用户选了"跟随系统"时才动界面)。
    ///
    /// 只在颜色真的变了才重刷: `UserPreferenceChanged` 在切深浅色/换壁纸/插拔显示器时都会来,
    /// 每次都重刷一遍笔刷是白烧的帧。
    /// </summary>
    private void CheckSystemAccentChanged()
    {
        if (!IsAccentSystem) return;
        var old = _systemAccent;
        ReadSystemAccent();
        if (old == _systemAccent) return;
        Accent = _systemAccent;
        ApplyAccentColors();
    }

    /// <summary>读取系统深浅色(注册表)</summary>
    private void ReadSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var v = key?.GetValue("AppsUseLightTheme");
            IsSystemDark = v is int i && i == 0;
        }
        catch
        {
            IsSystemDark = false;
        }
    }

    /// <summary>
    /// 手动切换主题。
    ///
    /// 这里刻意「先算状态、再落盘、最后换肤」:
    /// 之前的实现是 Mode 赋值 -> Save() -> Apply(), 而 SetMode 由设置页 RadioButton 的
    /// TwoWay 绑定触发。WPF 在 GroupName 分组下会先把旧项 IsChecked 置 false(回写),
    /// 再置新项 true; 由于 ConvertBack 对 false 返回 Binding.DoNothing, 期间界面上会出现
    /// 「三项都没选」的瞬时状态。若此时 Save/Apply 被额外触发一次, 色板会被按旧的 Mode
    /// 再应用一遍, 用户感知就是"点第一下没反应, 点第二下才变色"。
    ///
    /// 现在把"是否真的需要换肤"判断提前, 且只在 Mode 真正变化时才落盘, 单击一次即生效。
    /// </summary>
    public void SetMode(AppTheme mode)
    {
        if (mode == Mode)
        {
            // 模式没变也必须保证当前配色与它一致(防止启动/外部流程留下的不一致状态)。
            // 不播过渡: 这一刻界面本来就该是目标色。
            Apply(animate: false);
            return;
        }

        Mode = mode;
        Svc.Settings.ThemeMode = mode;
        Svc.Settings.Save();
        Apply();
    }

    /// <summary>应用主题: 把当前色板 + 材质覆盖色插值到笔刷上</summary>
    public void Apply() => Apply(animate: true);

    private void Apply(bool animate)
    {
        IsDark = Mode switch
        {
            AppTheme.Light => false,
            AppTheme.Dark => true,
            _ => IsSystemDark
        };

        var app = Application.Current;
        if (app == null) return;

        EnsureDictionary(app);
        ApplyThemeColors(animate);
        ApplyAccentColors();

        // 通知所有窗口重建 chrome(标题栏深浅色 / 窗口底色)
        ThemeChanged?.Invoke();
    }

    /// <summary>建好那本**唯一**的动态字典(只做一次), 挂在 MergedDictionaries 最后。</summary>
    private void EnsureDictionary(Application app)
    {
        if (_dict != null) return;
        _dict = new ResourceDictionary();
        app.Resources.MergedDictionaries.Add(_dict);
    }

    /// <summary>
    /// 把两张色板读成 `key → Color`。
    /// 只读 SolidColorBrush 类型的项(色板里全是笔刷), 读完就把字典丢掉 ——
    /// 我们要的只是颜色值, 不需要让那本字典留在内存里参与查找。
    /// </summary>
    private void LoadPalettes()
    {
        if (_palettesLoaded) return;
        _palettesLoaded = true;
        try
        {
            ReadPalette("Themes/Colors.Light.xaml", _light);
            ReadPalette("Themes/Colors.Dark.xaml", _dark);
        }
        catch (Exception ex)
        {
            // 读不到色板 = 换肤失效, 必须留下线索而不是静默变成"点了没反应"
            App.ReportError(ex);
        }
    }

    private static void ReadPalette(string relativeUri, Dictionary<string, Color> into)
    {
        var dict = new ResourceDictionary { Source = new Uri(relativeUri, UriKind.Relative) };
        foreach (var key in dict.Keys)
        {
            if (key is string k && dict[k] is SolidColorBrush b) into[k] = b.Color;
        }
    }

    /// <summary>
    /// 换肤主体: 算出每个键的目标色, 再写到共享笔刷上(可选插值)。
    /// 只改 Color, 不动字典结构 —— 这是整个重做的核心。
    /// </summary>
    private void ApplyThemeColors(bool animate)
    {
        var palette = IsDark ? _dark : _light;
        if (palette.Count == 0) return;

        var targets = new Dictionary<string, Color>(palette.Count, StringComparer.Ordinal);
        foreach (var kv in palette) targets[kv.Key] = kv.Value;
        // 材质覆盖色(云母/亚克力)优先级高于色板: 同键直接盖掉
        foreach (var kv in ComputeSurfaceColors()) targets[kv.Key] = kv.Value;

        var epoch = ++_fadeEpoch;
        var anyAnimated = false;
        foreach (var kv in targets)
            if (SetColor(kv.Key, kv.Value, animate, epoch)) anyAnimated = true;

        // 记账: 只要有一个笔刷真的挂了动画, 就认为过渡进行中。收尾回调负责清掉。
        IsFading = anyAnimated;
        if (!anyAnimated) return;
        // 兜底: 动画被渲染挂起(窗口最小化)时 Completed 可能不来, 到点自己收尾
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(FadeMs + 200)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (epoch == _fadeEpoch) IsFading = false;
        };
        timer.Start();
    }

    /// <summary>材质开关 / 壁纸变化时重算那几个"被材质覆盖"的键(不涉及色板切换, 直接到位)</summary>
    private void RefreshSurfaces()
    {
        if (Application.Current == null) return;
        EnsureDictionary(Application.Current);
        var epoch = ++_fadeEpoch;
        var surfaces = ComputeSurfaceColors();
        foreach (var kv in surfaces) SetColor(kv.Key, kv.Value, animate: false, epoch);
        // 材质关掉时覆盖项消失 ⇒ 那几层要回到色板原值, 否则会停在上一套材质色上
        var palette = IsDark ? _dark : _light;
        foreach (var key in SurfaceKeys)
        {
            if (surfaces.ContainsKey(key)) continue;
            if (palette.TryGetValue(key, out var c)) SetColor(key, c, animate: false, epoch);
        }
    }

    /// <summary>会被材质覆盖的那几个键(关掉材质时要显式回落, 见 RefreshSurfaces)</summary>
    private static readonly string[] SurfaceKeys =
    {
        "AppBackgroundBrush", "SideBarBackgroundBrush", "CardBackgroundBrush",
        "PanelBackgroundBrush",
        "InfoBarBackgroundBrush", "InputBackgroundBrush", "FlyoutBackgroundBrush",
        "ToastBackgroundBrush", "FloatingFillBrush", "FloatingFillHoverBrush",
        "FloatingFillPressedBrush"
    };

    /// <summary>
    /// 建一个**可变**笔刷。
    ///
    /// ★★★ 那个 `Prime` 恒等动画**不是装饰, 是防冻结锁, 别删**(见类注释的实测):
    ///   WPF 会把"放进资源字典 + 被 Style Setter 消费"的 Freezable 自动 Freeze。
    ///   本工程 156 处 DynamicResource 写在 Style setter 里, 裸笔刷一挂上去就被冻死。
    ///   先挂一个无限循环的恒等动画后 `CanFreeze` 变 false, WPF 就不再冻结它;
    ///   换色时用新的 BeginAnimation 覆盖这个恒等动画即可。
    /// </summary>
    private static SolidColorBrush CreateLiveBrush(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
        {
            From = c,
            To = c,
            Duration = TimeSpan.FromDays(1),
            RepeatBehavior = RepeatBehavior.Forever,
            FillBehavior = FillBehavior.HoldEnd
        });
        return brush;
    }

    /// <summary>
    /// 把某个键设成目标色。返回 true 表示**真的挂上了一段过渡动画**。
    ///
    /// ★ 过渡用新的 `BeginAnimation` 直接覆盖旧动画(WPF 的 snapshot-and-replace):
    ///   不需要先 `BeginAnimation(null)`, 那会让颜色瞬间回落到基准值(生硬跳变)。
    /// ★ 收尾时把 Color 落定并重新挂上恒等动画: 既保证属性值正确, 又继续保持"不可冻结"。
    /// </summary>
    private bool SetColor(string key, Color target, bool animate, int epoch)
    {
        if (_dict == null) return false;

        if (!_brushes.TryGetValue(key, out var brush))
        {
            brush = CreateLiveBrush(target);
            _brushes[key] = brush;
            _dict[key] = brush;
            return false;
        }

        if (!animate)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, IdentityAnimation(target));
            brush.Color = target;
            return false;
        }

        // 从**当前有效色**起步: 有动画时读到的就是当前帧的插值结果, 连点时不会跳回旧色
        var from = brush.Color;
        if (from == target) return false;

        var anim = new ColorAnimation(from, target, TimeSpan.FromMilliseconds(FadeMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        var b = brush;
        anim.Completed += (_, _) =>
        {
            if (epoch != _fadeEpoch) return;   // 已被更晚的一次换肤取代
            // 落定: 换成恒等动画(= 保持该颜色, 同时继续保持不可冻结)
            b.BeginAnimation(SolidColorBrush.ColorProperty, IdentityAnimation(target));
            b.Color = target;
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        return true;
    }

    /// <summary>恒等动画(From == To == c): 让笔刷保持颜色, 同时让它不可冻结。见 CreateLiveBrush。</summary>
    private static ColorAnimation IdentityAnimation(Color c) => new()
    {
        From = c,
        To = c,
        Duration = TimeSpan.FromDays(1),
        RepeatBehavior = RepeatBehavior.Forever,
        FillBehavior = FillBehavior.HoldEnd
    };

    /// <summary>把整套 Accent 笔刷(用户主题色 / 系统强调色, 默认 B 站粉)写到共享笔刷上</summary>
    private void ApplyAccentColors()
    {
        if (Application.Current == null) return;
        EnsureDictionary(Application.Current);

        var accent = Accent;
        var onAccent = OnAccentFor(accent);
        var epoch = _fadeEpoch;

        // 强调色一律直接到位(不做过渡): 调色盘拖动时每秒几十次, 插值只会拖在手指后面
        void Set(string key, Color c) => SetColor(key, c, animate: false, epoch);

        Set("AccentBrush", accent);
        // 悬停/按下用与白色/黑色做小幅混合, 而不是再定义一组色值 —— 换主题色时只需改一处
        Set("AccentHoverBrush", Blend(accent, Colors.White, 0.12));
        Set("AccentPressedBrush", Blend(accent, Colors.Black, 0.12));
        Set("AccentSoftFillBrush", Color.FromArgb(0x1C, accent.R, accent.G, accent.B));
        // 胶囊 Tab 的选中底(侧边栏选中态那种观感)。
        // ★ 分深浅主题(2026-10-03 用户报"深色模式下胶囊按钮可读性差"):
        //   浅色: 白卡片上 0x1C 太淡, 用 0x2E;
        //   深色: 深灰卡片上叠半透明强调色会"糊"进底色, 文字读不清 —— 底色反而要**提亮**:
        //   用强调色与白色 30% 混合的实色, 亮出一截, 配深色文字对比才够。
        Set("AccentSoftFillStrongBrush", IsDark
            ? Blend(accent, Colors.White, 0.30)
            : Color.FromArgb(0x2E, accent.R, accent.G, accent.B));
        Set("AccentBorderFillBrush", Color.FromArgb(0x66, accent.R, accent.G, accent.B));

        // 强调色被当文字色用(导航选中项 / 图标 / kaomoji)。深浅两套主题共用同一个值 ——
        // 不再按主题做提亮/压深, 保证深浅色下的主题色完全一致。
        Set("AccentTextBrush", accent);

        // 深一档的强调色文字(2026-10-03, 胶囊 Tab 选中态用): 柔和底上叠 AccentTextBrush
        // 两者亮度太接近, 选中项的文字读不清。
        // ★ 跟着底色走: 浅色底(白/淡强调)文字往黑压 35%; 深色下选中底是"提亮的强调色",
        //   文字用深色(压黑 55%)保证对比。换强调色/换主题时都会跟着重算。
        Set("AccentTextStrongBrush", IsDark
            ? Blend(accent, Colors.Black, 0.55)
            : Blend(accent, Colors.Black, 0.35));

        // ★★ 压在**卡片底色**上的强调色文字(2026-10-12, 分段选择器的选中文字用)。
        //
        //   场景与上面那个 AccentTextStrongBrush **不一样**, 别混用:
        //     · AccentTextStrongBrush 是给"强调色实底胶囊"配的 —— 浅色下底是白/淡强调,
        //       深色下底是**提亮的强调色**(亮底), 所以深色时文字要压黑。
        //     · 这里是"**卡片底**胶囊"(CardBackgroundBrush): 浅色下底是纯白 #FFFFFF,
        //       深色下底是深灰 #2E2E2E。要读得清, 浅色需要**压深**的强调色,
        //       深色需要**原色**(亮)的强调色 —— 两者的方向正好相反。
        //
        //   ★ 为什么必须新增而不是复用: 实测选中文字用原色强调色压白底只有
        //     **2.64:1**、压页面浅底(#E9E9E9)更是只有 **2.17:1**
        //     (WCAG AA 要求 4.5:1), 观感就是"粉字发飘、读不清"。
        //   ★ 压深比例按**浅色主题的实际底色**算过:
        //     底色是 #F3F3F3 叠 4% 黑 ≈ #E9E9E9, 原色 #FB7299 压深 45% 后
        //     实测 **5.92:1**, 达标且留有余量(用户换自定义强调色时同样按这个比例走,
        //     深浅关系不会翻车)。压 35% 只有 4.65:1, 余量太少 —— 一旦用户选了个
        //     更浅的强调色就会掉到线下。
        //   ★ 跟着 accent 走: 用户自定义主题色时这里自动重算, 不会退回硬编码粉。
        Set("AccentTextOnCardBrush", IsDark
            ? accent
            : Blend(accent, Colors.Black, 0.45));

        Set("TextOnAccentBrush", onAccent);
        Set("TextOnAccentSecondaryBrush", onAccent);
    }

    /// <summary>
    /// 按当前**材质**给"页面基底 / 侧边栏 / 卡片 / 浮层"这一整套配色做覆盖。
    ///
    /// ★★★ 2026-10-17 整段重做(**这一版才是对的**, 之前几版的取向都错)。用户报了两件事:
    ///   ① "云母材质效果很差, 基本和白色背景没区别"
    ///   ② "亚克力背景看起来很脏, 不通透"
    ///   下面两条是它们的**实测根因**(探针 bd-probe-backdrop / bd-probe-tune / bd-probe-stack):
    ///
    /// ★ 根因一: **云母在多数机器上会被 DWM 退化成纯灰白**, 而我们的页面底同色 ⇒ 两层一样白。
    ///   实测(build 26200, 同一窗口分别对 白/青绿/深灰/橙 四张纯色壁纸):
    ///   <code>
    ///     壁纸         Mica 读到的像素
    ///     白 #FFFFFF   243,243,243
    ///     青绿         243,243,243   ← 一点青绿都没有
    ///     深灰         243,243,243   ← 也没有变暗
    ///     橙           232,248,243
    ///   </code>
    ///   而我们的页面底是色板里的 <c>#B3F3F3F3</c>(70% 的 #F3F3F3)—— **两者同一个颜色**。
    ///   ⇒ 云母"设上了"却一个像素的色差都看不出来, 用户看到的就是"和白色没区别"。
    ///   ⇒ 修法: **自己算一份壁纸色调**染到页面底上(<see cref="DesktopTint"/>)。
    ///
    /// ★ 根因二: **页面底被叠了两层 ⇒ 实际不透明度远高于滑杆设定值**。
    ///   `MainWindow.RootGrid` 与每个页面(UserControl)都写了
    ///   `Background="{DynamicResource AppBackgroundBrush}"` —— 同一个半透明笔刷在同一块像素上
    ///   画了两遍。合成后不透明度是 **1-(1-a)²** 而不是 a:
    ///   <code>
    ///     a=30% ⇒ 实测 51%     a=45% ⇒ 实测 70%     a=70% ⇒ 实测 91%
    ///   </code>
    ///   ⇒ 这就是"亚克力不通透"的真根因: 用户把滑杆拉到 30%, 实际得到 51%。
    ///   ⇒ 修法: 这里下发的 alpha 一律按 **1-(1-a)² = 目标** 反解, 即
    ///     `a = 1-sqrt(1-target)`, 让"滑杆上写的数字"就是"屏幕上实际的不透明度"。
    ///
    /// ★ 根因三: **亚克力的基准色是中性灰(实测 211), 页面底也是灰(#ECEDEF)** ⇒ 灰压灰 = 脏。
    ///   浅色主题下正确的做法是**用白提亮**(白压灰不会变脏), 而不是再压一层灰。
    ///
    /// ★ 适用面(四种材质各一套, 别再合并):
    ///   · None      → 色板原值(纯色, 不涉及材质)
    ///   · Mica/Alt  → 页面底 = **壁纸色调提亮**; 卡片/浮层仍用色板值(材质是"底", 卡片要浮起来)
    ///   · Acrylic   → 页面底 = **白**(按滑杆反解后的 alpha); 实色块压暗一档(否则白卡在白底上糊成一片)
    ///
    /// ★ 返回的是"覆盖项": 没列出的键由调用方回落到色板原值。
    ///   这也正好让"关掉材质"变成一个**空字典** —— 不需要记旧值, 也不需要逐个还原。
    /// </summary>
    private Dictionary<string, Color> ComputeSurfaceColors()
    {
        var into = new Dictionary<string, Color>(16, StringComparer.Ordinal);

        // ★★★ 清单里**刻意不含** OverlaySurfaceBrush(2026-10-05): 那个键是合集面板那种"浮在内容上"
        //   的面板底色, 必须恒为不透明。工具列(FloatingFillBrush)要半透明是因为它底下是卡片封面、
        //   透出壁纸正是想要的效果; 浮层面板底下是文字列表, 透出来就是"一团糊字"。
        //   **别因为"要跟着压暗"把它加进来** —— 那正是 2026-10-05 用户报"亚克力下合集页
        //   背景几乎透明"的根因: 早期合集面板与工具列共用 FloatingFillBrush, 一开亚克力就一起变透。
        var material = Svc.Settings.AcrylicMaterial;
        if (!Svc.Settings.AcrylicBackground || material == BackdropMaterial.None)
        {
            // 关掉材质: 不写任何覆盖项 —— 调用方回落到色板里的原值
            return into;
        }

        // ============ 云母 / 云母 Alt ============
        // 页面底与侧边栏上**壁纸色调**(提亮后的), 其余实色块保持色板原值。
        // ★ 为什么只动这两层: 云母是"底色材质", 它的意义就是让**大面积留白**带上壁纸色;
        //   卡片/输入框/浮层是"浮在底上的实体", 跟着变色反而会让整屏糊成一片
        //   (它们还要与卡片里的白字/封面形成层次)。
        // ★ 不透明度取"反解后的 70%"(两层叠完正好 70%): 太透会露出 DWM 那块灰白基色,
        //   太实又白压一层把色调闷掉。70% 是实测最平衡的一档(探针 bd-probe-tune)。
        if (material is BackdropMaterial.Mica or BackdropMaterial.MicaAlt)
        {
            // 云母Alt 比云母"层次更平"(系统文档的原话), 这里让它更透一点、色调更淡一点
            var alt = material == BackdropMaterial.MicaAlt;
            var target = alt ? 0.66 : 0.74;
            var chroma = IsDark ? DesktopTint.DarkChroma : DesktopTint.LightChroma;
            // 云母Alt 的层次"更平", 色度收一点, 两种云母才看得出区别
            var tint = IsDark
                ? WithAlpha(DesktopTint.TintedBase(0.13, alt ? chroma * 0.8 : chroma), ToOverlayAlpha(target))
                : WithAlpha(DesktopTint.TintedBase(0.93, alt ? chroma * 0.8 : chroma), ToOverlayAlpha(target));

            into["AppBackgroundBrush"] = tint;
            // 侧边栏比页面底略透一档(它本来就该更"贴"材质)
            into["SideBarBackgroundBrush"] = WithAlpha(
                IsDark ? DesktopTint.TintedBase(0.13, chroma) : DesktopTint.TintedBase(0.93, chroma),
                ToOverlayAlpha(alt ? 0.50 : 0.58));

            // 其余键保持色板原值: 这里没写它们, 调用方自然回落。
            return into;
        }

        // ============ 亚克力 ============
        // 页面底压到很低的不透明度(让材质透出来); 实色块压暗一档(白卡压在白底上会糊成一片)。
        // ★ 透明度可由用户在设置里分开调节浅色/深色, 侧边栏比页面底更透明一档。
        // ★★ 用户给的是**目标**不透明度(屏幕上该看到多少), 而下发的 alpha 必须反解掉"双叠"
        //   (见方法头"根因二"): a = 1 - sqrt(1 - target)。
        var target2 = Math.Clamp(
            (IsDark ? Svc.Settings.AcrylicOpacityDark : Svc.Settings.AcrylicOpacityLight) / 100.0, 0, 1);

        // ★★★ 页面底**必须真的很透**, 这是亚克力的全部意义(2026-10-17 二次返工)。
        //   上一版我给亚克力设了**不透明度下限 0.70**, 理由是"0.34 时实测 217 发闷" ——
        //   结果把亚克力做成了不透明材质, 用户当场反馈"亚克力现在不透明了"。
        //   ⇒ 那个推理是错的: "发闷"要用**颜色**解决(下面改成白), 绝不能用"提高不透明度"解决。
        //
        //   浅色主题怎么既通透又干净(实测取舍, 探针 bd-probe-alpha):
        //     · 透出量随不透明度直接下降: 0% → 55、50% → 20、100% → 5(品红成分)
        //     · 发闷的真正来源是**那层灰**(DWM 亚克力实测 211), 而白压灰只会提亮
        //     ⇒ 页面底取**白**, 亮度高、色度极低(带一点点壁纸倾向), 不透明度**交回给用户**。
        //   ★ 不设下限/上限: 滑杆 0 就是 0(最通透), 100 就是 100(最实),
        //     用户要什么给什么 —— 这才是一个"透明度"滑杆该有的行为。
        var acrylicBase = IsDark
            ? DesktopTint.TintedBase(0.30, 0.055)
            : DesktopTint.TintedBase(0.985, 0.030);

        // ★★ 滑杆数值直接就是目标不透明度, 不做任何映射 —— 双叠由 ToOverlayAlpha 反解(见其说明)。
        var alpha = ToOverlayAlpha(target2);
        // 侧边栏比页面底**更透一档**: 导航栏本来就该更"贴"材质, 但不能透成全透明
        // (那样左侧会直接看到桌面, 文字压不住)。差值随用户给的通透程度缩放。
        var sidebarTarget = Math.Clamp(target2 * 0.75, 0.12, 0.96);
        var sidebarAlpha = ToOverlayAlpha(sidebarTarget);

        into["AppBackgroundBrush"] = WithAlpha(acrylicBase, alpha);
        into["SideBarBackgroundBrush"] = WithAlpha(acrylicBase, sidebarAlpha);

        // 卡片(动态卡/缓存页卡/聊天气泡/播放器 UP 卡等): 亚克力下**也半透明**
        //   (2026-11-14 用户拍板"卡片也半透明" —— 大面板透出来之后, 实色卡片反而成了
        //   整页最突兀的纯色块)。比板块面板**更实一档**(alpha + 0.15): 卡片是内容主体,
        //   太透文字会发飘。默认滑杆档下合成不透明度约 5 成(浅)/7 成(深), 材质透得出、
        //   卡片仍比面板"浮"一层。封面图本身不透明, 不受影响。
        // ★ 视频卡/番剧卡没有卡片底(封面+文字直接衬在页面底上), 天然不受这里影响。
        // ★ 浮在内容上的弹层(ComboBox 下拉/收藏夹折叠浮层)与开关圆点**不能**跟着透,
        //   它们已改绑 FlyoutBackgroundBrush(恒不透明), 见 Styles.xaml / FavoritesPage。
        // ★ 普通/云母模式: 色板原值(不透明), 这里不写覆盖项时由调用方回落。
        into["CardBackgroundBrush"] = WithAlpha(acrylicBase, (byte)Math.Min(255, alpha + 38));
        into["InfoBarBackgroundBrush"] = IsDark
            ? Color.FromRgb(0x2A, 0x2A, 0x2A)
            : Color.FromRgb(0xFC, 0xFC, 0xFD);

        // ★★ 板块面板(设置页大卡片 / 消息页左栏 / UP 主页头 / 胶囊条): 亚克力下**半透明**,
        //   让材质从大面板里透出来(2026-11-14 用户报"某些板块显示时亚克力不生效,
        //   那一块纯色背景很突兀")。它们直接坐在页面底上, 底下透出的是材质而不是文字列表,
        //   半透明不会糊字 —— 与合集面板那种"浮在内容上"的浮层(OverlaySurfaceBrush,
        //   恒不透明)是两回事。
        // ★★ 透明度与页面底**同一档**(同一个反解后的 alpha): 第一版给的是固定 70%,
        //   用户实机反馈"和之前区别不大" —— 70% 压在半透明页面底上, 材质只剩约两成可见,
        //   观感仍接近实色。改成同档后默认滑杆档下合成不透明度约 4 成(浅)/6.5 成(深),
        //   材质清晰可见; 面板比页面底多叠一层、仍然更实, 靠边框/投影分层, 不会糊成一片。
        //   滑杆因此同时控制页面底与面板, 调一处两边一起变。
        // ★ 卡片(CardBackgroundBrush)比面板再实一档, 见上面那组覆盖。
        into["PanelBackgroundBrush"] = WithAlpha(acrylicBase, alpha);

        // 浮层/输入框/Toast: 保持不透明(浮层要压住底下内容; 输入框/Toast 接近色板原值)
        into["InputBackgroundBrush"] = IsDark
            ? Color.FromRgb(0x2E, 0x2E, 0x2E)
            : Color.FromRgb(0xF6, 0xF7, 0xF9);
        into["FlyoutBackgroundBrush"] = IsDark
            ? Color.FromRgb(0x2E, 0x2E, 0x2E)
            : Color.FromRgb(0xFC, 0xFC, 0xFD);
        into["ToastBackgroundBrush"] = IsDark
            ? Color.FromRgb(0x2E, 0x2E, 0x2E)
            : Color.FromRgb(0xF6, 0xF7, 0xF9);
        // ★★★ 工具列按钮: 亚克力模式下改成**半透明**, 让它也跟着呈现亚克力质感
        //   (2026-10-05 用户要求"亚克力时右下角工具列也应用亚克力效果")。
        //   ★ 为什么只能"半透明"而不能真做局部亚克力: DWM 的亚克力是**挂在窗口 HWND 上的
        //     整窗一层材质**, WPF 没有 per-element backdrop API —— 元素层面拿不到"自己那块区域的
        //     背景采样"。唯一可行的近似是**留出透明度让底下那层(页面底的亚克力)透上来**,
        //     视觉上就成了一片亚克力浮层。
        //   ★ 为什么普通模式不能也跟着半透明: 这排按钮浮在**卡片封面(彩色图片)**上,
        //     底色一透就会露出封面图案、图标糊掉(见 Colors.Light.xaml 里 FloatingFill 的说明)。
        //     普通模式没有"底下透出亚克力"这个收益, 所以两套材质继续分开: 普通=不透明, 亚克力=半透明。
        //   ★ 悬停/按下要比常态**更实**(不透明度递增) —— 这样交互反馈方向与原"压暗一档"一致。
        into["FloatingFillBrush"] = IsDark
            ? Color.FromArgb(0xC7, 0x2A, 0x2A, 0x2A)
            : Color.FromArgb(0xB3, 0xEC, 0xED, 0xEF);
        into["FloatingFillHoverBrush"] = IsDark
            ? Color.FromArgb(0xD9, 0x33, 0x33, 0x33)
            : Color.FromArgb(0xC7, 0xE2, 0xE4, 0xE7);
        into["FloatingFillPressedBrush"] = IsDark
            ? Color.FromArgb(0xEB, 0x3E, 0x3E, 0x3E)
            : Color.FromArgb(0xD9, 0xD5, 0xD9, 0xDE);

        return into;
    }

    /// <summary>
    /// 把"屏幕上该看到的不透明度"反解成**单层笔刷的 alpha** —— 因为这个笔刷会被
    /// 页面层与窗口根网格各画一次(双叠), 合成结果是 <c>1-(1-a)²</c> 而不是 <c>a</c>。
    ///
    /// <code>
    ///   target  a=1-sqrt(1-target)   实测叠完
    ///   0.30    0.163                0.30
    ///   0.50    0.293                0.50
    ///   0.70    0.452                0.70
    /// </code>
    ///
    /// ★ 为什么必须在这里补: `MainWindow.RootGrid` 与每个页面都绑同一个
    ///   `AppBackgroundBrush`(见该方法头上的"根因二"), 而两处都不能删 ——
    ///   RootGrid 那层是"页面切走/加载中"时的兜底底色, 页面层是"这个页面自己的底",
    ///   缺任何一个都会在某些时刻露出一块纯材质甚至桌面。
    ///   ⇒ 既然去不掉一层, 就在**下发处**把 alpha 反解掉, 让用户滑杆上的数字等于实际观感。
    ///
    /// ★ 边界: target ≤ 0 时返回 0(全透); target → 1 时 alpha → 1(全不透), 不会溢出。
    /// </summary>
    private static byte ToOverlayAlpha(double target)
    {
        target = Math.Clamp(target, 0, 1);
        var a = 1 - Math.Sqrt(1 - target);
        return (byte)Math.Round(Math.Clamp(a, 0, 1) * 255);
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color Blend(Color a, Color b, double t)
    {
        byte Mix(byte x, byte y) => (byte)Math.Round(x + (y - x) * Math.Clamp(t, 0, 1));
        return Color.FromRgb(Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B));
    }
}
