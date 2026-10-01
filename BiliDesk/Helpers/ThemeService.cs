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
/// 强调色**固定为 B 站粉**, 不再跟随系统强调色。
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

    // ---------------------------------------------------------------- 品牌强调色

    /// <summary>
    /// 品牌强调色: B 站粉。全应用统一使用它。
    ///
    /// 为什么改成固定值(原先跟随系统强调色):
    ///   1. 跟随系统时, 用户换壁纸/换主题就会让整个应用的观感漂移, 而且系统色可能是
    ///      深蓝/墨绿这类和 B 站气质完全无关的颜色 —— 对"一个产品"来说固定品牌色更稳;
    ///   2. 顺带省掉了启动路径上的 3 次注册表读取, 以及 `SystemParameters.WindowGlassColor`
    ///      的首次访问(那个静态属性第一次取值会初始化整套系统主题信息, 有实际开销)。
    /// 深浅两套主题用**同一个**粉色, 不做"深色下提亮"的调整 —— 粉色在深色底上是 5.2:1、
    /// 在白色卡片上 2.6:1, 两边都看得清, 调了反而不统一。
    /// </summary>
    private static readonly Color BrandAccent = Color.FromRgb(0xFB, 0x72, 0x99);

    /// <summary>
    /// 放在品牌强调色上的文字 / 图标颜色。
    /// 粉底白字是 B 站的标准搭配, 这里直接按品牌色板写死, 不再做对比度推导。
    /// (对比度约 2.6:1, 属于"大号/半粗文字可接受"的区间; 深色字能到 6.6:1 更保险,
    ///  但那样按钮就不再是 B 站的观感了 —— 这是刻意的观感取舍, 不是疏漏。)
    /// </summary>
    private static readonly Color BrandOnAccent = Colors.White;

    public void Initialize()
    {
        Mode = Svc.Settings.ThemeMode;
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
                });
            };
        }
        catch
        {
            // 订阅失败不影响功能, 应用内仍可手动切换
        }
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

    /// <summary>注入整套 Accent 笔刷(固定品牌粉)</summary>
    private void ApplyAccent()
    {
        var app = Application.Current;
        if (app == null) return;

        var accent = BrandAccent;

        app.Resources["AccentBrush"] = Frozen(accent);
        // 悬停/按下用与白色/黑色做小幅混合, 而不是再定义一组色值 —— 换品牌色时只需改一处
        app.Resources["AccentHoverBrush"] = Frozen(Blend(accent, Colors.White, 0.12));
        app.Resources["AccentPressedBrush"] = Frozen(Blend(accent, Colors.Black, 0.12));
        app.Resources["AccentSoftFillBrush"] = Frozen(Color.FromArgb(0x1C, accent.R, accent.G, accent.B));
        app.Resources["AccentBorderFillBrush"] = Frozen(Color.FromArgb(0x66, accent.R, accent.G, accent.B));

        // 强调色被当文字色用(导航选中项 / 图标 / kaomoji)。因为品牌粉在浅底深底上都够用,
        // 这里直接用原色 —— 不再按主题做提亮/压深, 保证深浅两套的主题色完全一致。
        app.Resources["AccentTextBrush"] = Frozen(accent);

        app.Resources["TextOnAccentBrush"] = Frozen(BrandOnAccent);
        app.Resources["TextOnAccentSecondaryBrush"] = Frozen(BrandOnAccent);
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
