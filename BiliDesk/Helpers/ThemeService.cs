using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BiliDesk.Models;
using BiliDesk.Services;
using Microsoft.Win32;

namespace BiliDesk.Helpers;

/// <summary>
/// 主题服务: 跟随系统深浅色自动切换 / 手动浅色 / 手动深色, 并注入强调色笔刷。
/// 强调色默认 B 站粉, 用户可以在设置页自己调, 也可以选"跟随系统"用 Windows 的强调色。
/// </summary>
public class ThemeService
{
    public static ThemeService Instance { get; } = new();

    /// <summary>主题切换事件(所有窗口监听以更新自身)</summary>
    public event Action? ThemeChanged;

    private ResourceDictionary? _activeColorsDict;

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
    /// <paramref name="settle"/> = false 时**跳过**那次逐控件 InvalidateProperty
    /// (见下面那段说明)。它是给"调色盘拖动中"用的: 拖动时每秒会来几十次, 每次都走一遍
    /// 全窗口可视树的开销是白烧的帧; 笔刷本身已经换掉了, 当前看得见的界面本来就立刻变色。
    /// 代价是"被缓存、当前不在可视树里的页面"要等拖动结束那次 settle 才跟上 —— 而那时候
    /// 用户正在看设置页, 根本看不见那些页面。
    /// </summary>
    public void SetAccent(string? value, bool settle = true)
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
        ApplyAccent();
        // 主题色变了也要重算: 亚克力开启时那几层基底的透明度是按当前深浅主题给的
        ApplyAcrylicSurfaces();

        if (!settle) return;
        Settle();
    }

    /// <summary>
    /// 让所有窗口的可视树重新解析 DynamicResource(换色/换肤的最后一步)。
    ///
    /// ★ 和换深浅色同一个坑: 笔刷换了以后, 已 measure 过、当前不在可视树里的页面
    ///   (被 MainWindow 长期缓存着)可能仍持有旧笔刷的引用 —— 不强制重解析一次的话,
    ///   切回那个页面会看到它还是旧颜色。
    /// </summary>
    public void Settle()
    {
        RunOnUi(() =>
        {
            if (Application.Current is { } app) RefreshResourceReferences(app);
        });
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

    public void Initialize()
    {
        Mode = Svc.Settings.ThemeMode;
        // 只有"跟随系统"才去读注册表 —— 默认那条路上一次系统调用都不用发(以前固定品牌色,
        // 那次优化就是靠这个省下来的, 现在选了跟随系统才把这笔开销付回去)。
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
        Apply();
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
                });
            };
        }
        catch
        {
            // 订阅失败不影响功能, 应用内仍可手动切换
        }

        // 亚克力开关切换: 基底那几层的透明度要跟着换(见 ApplyAcrylicSurfaces),
        // 然后强制刷新可视树 —— 不刷新的话已缓存页面仍持有旧的半透明底,
        // 表现是"开了亚克力, 但切回首页还是实心的"。
        Svc.Settings.AcrylicChanged += () => RunOnUi(() =>
        {
            ApplyAcrylicSurfaces();
            Settle();
        });
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
    /// 每次都重刷一遍笔刷 + 全窗口 InvalidateProperty 是白烧的帧。
    /// </summary>
    private void CheckSystemAccentChanged()
    {
        if (!IsAccentSystem) return;
        var old = _systemAccent;
        ReadSystemAccent();
        if (old == _systemAccent) return;
        Accent = _systemAccent;
        ApplyAccent();
        Settle();
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
            // 模式没变也必须保证当前色板与它一致(防止启动/外部流程留下的不一致状态)
            Apply();
            return;
        }

        Mode = mode;
        Svc.Settings.ThemeMode = mode;
        Svc.Settings.Save();
        Apply();
    }

    /// <summary>应用主题: 切换色板字典 + 强调色</summary>
    public void Apply()
    {
        IsDark = Mode switch
        {
            AppTheme.Light => false,
            AppTheme.Dark => true,
            _ => IsSystemDark
        };

        var app = Application.Current;
        if (app == null) return;

        var colors = new ResourceDictionary
        {
            Source = new Uri(IsDark
                ? "Themes/Colors.Dark.xaml"
                : "Themes/Colors.Light.xaml", UriKind.Relative)
        };

        var merged = app.Resources.MergedDictionaries;

        // 清除所有残留的颜色字典(App.xaml 写死的 + 上一次 Insert 的),
        // 避免「两套相同字典」造成的查找歧义和资源浪费。
        //
        // 注意: 这里必须把**新旧两套都先从 MergedDictionaries 里摘干净再插入新的**。
        // 原实现会跳过 _activeColorsDict 不删, 而它正好排在第 0 位,
        // 导致新字典插入后旧字典仍留在列表里; WPF 对 DynamicResource 的查找是
        // "从后往前"命中第一个匹配键的字典, 于是部分控件仍解析到旧色板 ——
        // 这正是"要切两次颜色才对"的另一半原因。
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            var d = merged[i];
            if (ReferenceEquals(d, _activeColorsDict))
            {
                merged.RemoveAt(i);
                continue;
            }
            var src = d.Source?.OriginalString ?? "";
            if (src.IndexOf("Colors.Light.xaml", StringComparison.OrdinalIgnoreCase) >= 0 ||
                src.IndexOf("Colors.Dark.xaml", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                merged.RemoveAt(i);
            }
        }

        // 色板固定放第 0 项(Styles.xaml 依赖它); 此时列表中已不存在任何旧色板
        merged.Insert(0, colors);
        _activeColorsDict = colors;

        ApplyAccent();
        ApplyAcrylicSurfaces();

        // 通知所有窗口重建 chrome(标题栏深浅色 / 窗口底色)
        ThemeChanged?.Invoke();

        // 兜底: 强制刷新已渲染控件对 DynamicResource 的引用。
        //
        // 为什么需要这一步: DynamicResource 的解析结果会被控件缓存, 正常情况下字典被替换后
        // 会自动失效重解析。但当同一个键在"被移除的旧字典"和"新插入的字典"里都存在时,
        // 部分控件(尤其是已经 measure 过、处于非激活可视树里的窗口/页面)可能仍持有旧引用。
        // 这些页面都被 MainWindow 用 Dictionary 缓存着, 属于"长期存活但不总是可见"的典型情况。
        // 遍历可视树逐控件重新求值一次, 保证深浅色在单击后立刻全量生效。
        RunOnUi(() => RefreshResourceReferences(app));
    }

    /// <summary>
    /// 让所有已打开窗口的可视树重新解析 DynamicResource。
    ///
    /// 为什么需要这一步: DynamicResource 的解析结果会被控件缓存, 正常情况下字典被替换后
    /// 会自动失效重解析。但应用里的页面实例被 MainWindow 用 Dictionary 长期缓存,
    /// 属于"长期存活但不一定在当前可视树里"的状态; 这类元素在资源字典被整体替换后,
    /// 部分属性可能仍指向已被移除的旧字典项。逐个调用 InvalidateProperty 会强制 WPF
    /// 丢弃缓存并沿可视树重新查找, 保证深浅色单击一次即全量生效。
    /// </summary>
    private static void RefreshResourceReferences(Application app)
    {
        foreach (Window win in app.Windows)
        {
            try { Invalidate(win); }
            catch { /* 单个窗口刷新失败不影响其它窗口 */ }
        }
    }

    private static void Invalidate(DependencyObject node)
    {
        if (node is Control c)
        {
            c.InvalidateProperty(Control.BackgroundProperty);
            c.InvalidateProperty(Control.ForegroundProperty);
            c.InvalidateProperty(Control.BorderBrushProperty);
        }
        else if (node is TextBlock tb)
        {
            tb.InvalidateProperty(TextBlock.ForegroundProperty);
        }
        else if (node is Border b)
        {
            b.InvalidateProperty(Border.BackgroundProperty);
            b.InvalidateProperty(Border.BorderBrushProperty);
        }
        else if (node is Panel p)
        {
            p.InvalidateProperty(Panel.BackgroundProperty);
        }

        // 只有"会缓存资源引用"的元素才需要递归; 其他类型直接跳过子树会漏掉深层控件,
        // 所以这里仍然递归, 但每层只对上面几类打无效化。
        if (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
        {
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
                Invalidate(VisualTreeHelper.GetChild(node, i));
        }
    }

    /// <summary>
    /// 亚克力开启时, 把"页面基底"那几层刷成**更透明** —— 否则它们会把 DWM 合成出来的
    /// 亚克力整个盖住, 用户看到的就是"开了跟没开一样, 只是偏灰"(2026-10-05 用户反馈)。
    ///
    /// ★ 根因: 色板里的 AppBackgroundBrush 是 <c>#B3F3F3F3</c> = **70% 不透明**, 它铺在
    ///   窗口最底层之上, 亚克力只剩 30% 能透出来 —— 淡到几乎看不见, 且因为"白底掺灰"而发灰。
    ///   侧边栏 #8CF3F3F3(55%) 同理。所以开亚克力时必须把这两个键**覆盖**成低透明度版本。
    ///
    /// ★ 为什么覆盖成 Application.Resources 的直接项而不是改色板文件: 与强调色那套完全一致 ——
    ///   色板文件是"浅色/深色"两套静态资源, 而透明度要跟着"亚克力开关"变; 直接项优先级高于
    ///   字典项, 所以这里写入的值能压过色板, 关掉时移除即可自动回落到色板原值。
    ///
    /// ★ 覆盖的键必须与窗口/页面实际用的键完全一致(AppBackgroundBrush / SideBarBackgroundBrush),
    ///   少覆盖一层就会有一块"没透出亚克力"的区域(表现是某一片比别处更实)。
    /// </summary>
    /// <summary>
    /// 亚克力开启时, 把"不透明实色块"整体压暗一档, 让它们不再在透出壁纸的亮底上显白。
    ///
    /// ★★★ 为什么必须这么做(2026-10-05 用户连续两次反馈"还是白"的**真根因**):
    ///   亚克力会让页面底**透出桌面壁纸**, 实测合成后约 226(浅色壁纸时) —— 而卡片/浮层/输入框
    ///   是**不透明实色**(243 附近)。于是亮度差拉到 17 阶, 那一块块实色在浅色壁纸上就是"白块",
    ///   且因为**不透明**, 它完全不跟着亚克力呼吸, 视觉上格外突兀。
    ///   (前两轮只在色板里把 255 微调到 248, 只有 3~7 阶, 肉眼根本看不出 —— 所以用户说"还是白"。)
    ///
    /// ★ 为什么不直接把色板里的值压暗: 那会破坏**纯色模式**的层次 —— 纯色模式页面底是 236,
    ///   卡片必须比它亮才像"浮起来"; 压到 226 就比页面底还暗, 层次反了。
    ///   所以两套模式各给一套值: 纯色用色板原值, 亚克力用这里覆盖的暗一档的值。
    ///
    /// ★ 覆盖的是 Application.Resources 的直接项(优先级高于色板字典项), 关闭时移除即自动回落。
    /// </summary>
    private void ApplyAcrylicSurfaces()
    {
        var app = Application.Current;
        if (app == null) return;

        // ★★★ 清单里**刻意不含** OverlaySurfaceBrush(2026-10-05): 那个键是合集面板那种"浮在内容上"
        //   的面板底色, 必须恒为不透明。工具列(FloatingFillBrush)要半透明是因为它底下是卡片封面、
        //   透出壁纸正是想要的效果; 浮层面板底下是文字列表, 透出来就是"一团糊字"。
        //   **别因为"要跟着压暗"把它加进来** —— 那正是 2026-10-05 用户报"亚克力下合集页
        //   背景几乎透明"的根因: 早期合集面板与工具列共用 FloatingFillBrush, 一开亚克力就一起变透。
        // 亚克力开启时要覆盖的键: 页面基底 + 所有"不透明实色块"
        // (卡片/浮层/输入框/提示条/工具列/Toast) —— 漏一个就会有一块"没跟着压暗的白"。
        var keys = new[]
        {
            "AppBackgroundBrush", "SideBarBackgroundBrush",
            "CardBackgroundBrush", "InfoBarBackgroundBrush",
            "InputBackgroundBrush", "FlyoutBackgroundBrush",
            "ToastBackgroundBrush", "FloatingFillBrush",
            "FloatingFillHoverBrush", "FloatingFillPressedBrush",
        };

        if (!Svc.Settings.AcrylicBackground)
        {
            // 关掉: 全部移除覆盖项, 自动回落到色板里的原值(不需要记旧值)
            foreach (var k in keys) app.Resources.Remove(k);
            return;
        }

        // 开: 页面底压到很低的透明度(让亚克力质感透出来);
        // 实色块压暗一档 —— 深色主题压得更狠(深色底本来就暗, 不压会和页面底糊在一起)。
        // ★ 透明度可由用户在设置里分开调节浅色/深色(0~100), 侧边栏比页面底更透明一档。
        var opacity = Math.Clamp(IsDark ? Svc.Settings.AcrylicOpacityDark : Svc.Settings.AcrylicOpacityLight, 0, 100);
        var alpha = (byte)(opacity * 255 / 100);
        var sidebarAlpha = (byte)(alpha * 0.7);

        app.Resources["AppBackgroundBrush"] = IsDark
            ? Frozen(Color.FromArgb(alpha, 0x20, 0x20, 0x20))
            : Frozen(Color.FromArgb(alpha, 0xEC, 0xED, 0xEF));
        app.Resources["SideBarBackgroundBrush"] = IsDark
            ? Frozen(Color.FromArgb(sidebarAlpha, 0x20, 0x20, 0x20))
            : Frozen(Color.FromArgb(sidebarAlpha, 0xEC, 0xED, 0xEF));

        // 卡片/提示条: 比纯色模式的 #F0F1F3 再压 6 阶(→ 约 234)
        app.Resources["CardBackgroundBrush"] = Frozen(IsDark
            ? Color.FromRgb(0x26, 0x26, 0x26)
            : Color.FromRgb(0xE9, 0xEA, 0xEC));
        app.Resources["InfoBarBackgroundBrush"] = Frozen(IsDark
            ? Color.FromRgb(0x26, 0x26, 0x26)
            : Color.FromRgb(0xE9, 0xEA, 0xEC));

        // 浮层/输入框/工具列/Toast: 比纯色模式的 #F3F4F6 再压 6 阶(→ 约 237)
        app.Resources["InputBackgroundBrush"] = Frozen(IsDark
            ? Color.FromRgb(0x2A, 0x2A, 0x2A)
            : Color.FromRgb(0xEC, 0xED, 0xEF));
        app.Resources["FlyoutBackgroundBrush"] = Frozen(IsDark
            ? Color.FromRgb(0x2A, 0x2A, 0x2A)
            : Color.FromRgb(0xEC, 0xED, 0xEF));
        app.Resources["ToastBackgroundBrush"] = Frozen(IsDark
            ? Color.FromRgb(0x2A, 0x2A, 0x2A)
            : Color.FromRgb(0xEC, 0xED, 0xEF));
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
        app.Resources["FloatingFillBrush"] = Frozen(IsDark
            ? Color.FromArgb(0xC7, 0x2A, 0x2A, 0x2A)
            : Color.FromArgb(0xB3, 0xEC, 0xED, 0xEF));
        app.Resources["FloatingFillHoverBrush"] = Frozen(IsDark
            ? Color.FromArgb(0xD9, 0x33, 0x33, 0x33)
            : Color.FromArgb(0xC7, 0xE2, 0xE4, 0xE7));
        app.Resources["FloatingFillPressedBrush"] = Frozen(IsDark
            ? Color.FromArgb(0xEB, 0x3E, 0x3E, 0x3E)
            : Color.FromArgb(0xD9, 0xD5, 0xD9, 0xDE));
    }

    /// <summary>注入整套 Accent 笔刷(用户主题色 / 系统强调色, 默认 B 站粉)</summary>
    private void ApplyAccent()
    {
        var app = Application.Current;
        if (app == null) return;

        var accent = Accent;
        var onAccent = OnAccentFor(accent);

        app.Resources["AccentBrush"] = Frozen(accent);
        // 悬停/按下用与白色/黑色做小幅混合, 而不是再定义一组色值 —— 换主题色时只需改一处
        app.Resources["AccentHoverBrush"] = Frozen(Blend(accent, Colors.White, 0.12));
        app.Resources["AccentPressedBrush"] = Frozen(Blend(accent, Colors.Black, 0.12));
        app.Resources["AccentSoftFillBrush"] = Frozen(Color.FromArgb(0x1C, accent.R, accent.G, accent.B));
        // 胶囊 Tab 的选中底(侧边栏选中态那种观感)。
        // ★ 分深浅主题(2026-10-03 用户报"深色模式下胶囊按钮可读性差"):
        //   浅色: 白卡片上 0x1C 太淡, 用 0x2E;
        //   深色: 深灰卡片上叠半透明强调色会"糊"进底色, 文字读不清 —— 底色反而要**提亮**:
        //   用强调色与白色 30% 混合的实色, 亮出一截, 配深色文字对比才够。
        app.Resources["AccentSoftFillStrongBrush"] = IsDark
            ? Frozen(Blend(accent, Colors.White, 0.30))
            : Frozen(Color.FromArgb(0x2E, accent.R, accent.G, accent.B));
        app.Resources["AccentBorderFillBrush"] = Frozen(Color.FromArgb(0x66, accent.R, accent.G, accent.B));

        // 强调色被当文字色用(导航选中项 / 图标 / kaomoji)。深浅两套主题共用同一个值 ——
        // 不再按主题做提亮/压深, 保证深浅色下的主题色完全一致。
        app.Resources["AccentTextBrush"] = Frozen(accent);

        // 深一档的强调色文字(2026-10-03, 胶囊 Tab 选中态用): 柔和底上叠 AccentTextBrush
        // 两者亮度太接近, 选中项的文字读不清。
        // ★ 跟着底色走: 浅色底(白/淡强调)文字往黑压 35%; 深色下选中底是"提亮的强调色",
        //   文字用深色(压黑 55%)保证对比。换强调色/换主题时都会跟着重算。
        app.Resources["AccentTextStrongBrush"] = IsDark
            ? Frozen(Blend(accent, Colors.Black, 0.55))
            : Frozen(Blend(accent, Colors.Black, 0.35));

        app.Resources["TextOnAccentBrush"] = Frozen(onAccent);
        app.Resources["TextOnAccentSecondaryBrush"] = Frozen(onAccent);
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color Blend(Color a, Color b, double t)
    {
        byte Mix(byte x, byte y) => (byte)Math.Round(x + (y - x) * Math.Clamp(t, 0, 1));
        return Color.FromRgb(Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B));
    }
}
