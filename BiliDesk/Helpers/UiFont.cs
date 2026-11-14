using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using BiliDesk.Services;

namespace BiliDesk.Helpers;

/// <summary>
/// 全局「界面字体」(2026-11-14 新增): 用户可选系统已安装字体, 也可导入 .ttf/.otf/.ttc 字体文件。
/// 弹幕跟随同一字体; 图标字体不受影响(它们是元素上的局部值, 优先级高于窗口继承)。
///
/// ## 生效链路(与 UiScale / WheelScroll 同一套路: 配置驱动的静态值)
///   SettingsStore.Load 末尾 → <see cref="SetFromSettings"/> → <see cref="Current"/> 落定;
///   所有窗口继承 Views/FluentWindow, 构造时读 Current, 并订阅 <see cref="Changed"/> 即时换字体。
///   弹幕层在 LibVLC 的独立浮层可视树里, 不跟窗口继承走 —— PlayerWindow 在 AddDanmaku 里
///   显式设字体, 并订阅 Changed 走 ApplyDanmakuLook 清屏重建(字宽变了, 泳道/追尾校验失效)。
///
/// ## 存储语义(单字段 + 哨兵, 与 ThemeService.AccentSystem 的 "system" 同构)
///   空串          = 默认字体栈(<see cref="DefaultSource"/>);
///   普通字体名    = 系统已安装字体(存 FontFamily.Source, 如 "Microsoft YaHei UI");
///   "file:目录名" = 导入的字体文件(存在 AppPaths.FontDir 下的独立子目录里)。
///   ★ 为什么不用两个字段(字体名 + 文件路径): 两个字段迟早出现"名说是系统字体、路径却指着文件"
///     的矛盾(见 SettingsStore.AcrylicBackground 那段教训); 单字段天然互斥。
///
/// ## 导入文件的生命周期
///   每次导入复制到 fonts/&lt;文件名&gt;_&lt;时间戳&gt;/ 独立子目录 —— 用唯一目录名而不是覆盖同名文件:
///   WPF 可能还握着旧字体文件的句柄, 覆盖/删除会失败; 新目录永远写得进去。
///   同一时刻只有一个导入字体生效; 切换/恢复默认/启动时**尽力**清理其它子目录
///   (删不掉就留到下次启动 —— 那时字体还没被加载, 一定删得掉)。
///
/// ## 中文回退
///   选系统字体时拼回退栈 "<所选>, Microsoft YaHei UI, Microsoft YaHei": 纯西文字体(如 Arial)
///   下中文仍可读。导入字体无法与系统字体拼进同一个 FontFamily(位置 URI 不同), 靠 WPF 的
///   逐字形自动回退(GlobalUserInterface 复合字体)兜底 CJK —— 这正是"只写 Segoe UI 也能显示中文"的机制。
/// </summary>
public static class UiFont
{
    // ------------------------------------------------------------ 存储语义(纯逻辑, 可单测)

    /// <summary>导入字体的存储前缀(后跟 fonts 下的子目录名)</summary>
    public const string FilePrefix = "file:";

    /// <summary>
    /// 默认字体栈。★ 原写死在 Views/FluentWindow 构造函数里, 2026-11-14 搬到这里作单一来源 ——
    /// FluentWindow 与设置页「默认」项的预览都引用它, 别在别处再抄一份。
    /// </summary>
    public const string DefaultSource = "Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI, Microsoft YaHei";

    /// <summary>系统字体的中文回退栈(拼在用户所选字体之后, 见类注释"中文回退")</summary>
    public const string CjkFallback = "Microsoft YaHei UI, Microsoft YaHei";

    /// <summary>存储值的三种形态</summary>
    public enum StoredKind { Default, System, File }

    /// <summary>
    /// 把存储值解析成 (形态, 名字)。纯字符串操作, 不做任何 IO/WPF 校验 ——
    /// 校验在 <see cref="SanitizeStored"/>(那里才知道字体到底还在不在)。
    /// </summary>
    public static (StoredKind Kind, string Name) ParseStored(string? stored)
    {
        var v = (stored ?? "").Trim();
        if (v.Length == 0) return (StoredKind.Default, "");
        if (v.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = v[FilePrefix.Length..].Trim();
            return name.Length == 0 ? (StoredKind.Default, "") : (StoredKind.File, name);
        }
        return (StoredKind.System, v);
    }

    /// <summary>系统字体名 → 带中文回退的 FontFamily 源串(纯拼接)</summary>
    public static string BuildSystemSource(string familyName) => familyName + ", " + CjkFallback;

    /// <summary>
    /// 导入目录名的基础部分: 去扩展名、替换非法文件名字符、去首尾空白与点、限长。
    /// 纯字符串操作(时间戳后缀由 <see cref="ImportFile"/> 追加, 不进这个函数 —— 保持可单测)。
    /// </summary>
    public static string SanitizeBaseName(string? fileName)
    {
        var name = (fileName ?? "").Trim();
        var dot = name.LastIndexOf('.');
        // dot >= 0: ".ttf" 这种整体当扩展名的名字去完就是空串(与 GetFileNameWithoutExtension 一致)
        if (dot >= 0) name = name[..dot];
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        name = name.Trim().Trim('.', ' ');
        if (name.Length > 60) name = name[..60];
        return name.Length == 0 ? "font" : name;
    }

    // ------------------------------------------------------------ 当前生效值

    private static FontFamily _current = new(DefaultSource);
    private static string _currentStored = "";

    /// <summary>当前生效的界面字体(所有窗口与弹幕读它)</summary>
    public static FontFamily Current => _current;

    /// <summary>当前字体的显示名(设置页说明行用; 默认档显示"默认")</summary>
    public static string CurrentDisplay { get; private set; } = "默认";

    /// <summary>字体变化(所有已开窗口据此重设 FontFamily; 播放器据此重建弹幕)</summary>
    public static event Action? Changed;

    /// <summary>
    /// 按存储值落定当前字体。由 SettingsStore 在 Load 末尾与 SetUiFont 里调用 ——
    /// 与 CardWall/UiScale/WheelScroll 那几处静态同步是同一类(配置驱动的静态值必须显式同步)。
    /// ★ 传进来的值应当已经过 <see cref="SanitizeStored"/>; 这里再兜一层解析失败回落默认。
    /// </summary>
    public static void SetFromSettings(string? stored)
    {
        var next = stored ?? "";
        var (kind, name) = ParseStored(next);

        // 尽力清理不再使用的导入目录(启动时字体还没加载, 一定删得掉;
        // 运行中切换时旧文件可能还被 WPF 握着, 删不掉就留到下次启动)。
        PruneOtherImports(kind == StoredKind.File ? name : null);

        if (string.Equals(next, _currentStored, StringComparison.Ordinal)) return;
        var (family, display) = Resolve(next);
        _currentStored = next;
        _current = family;
        CurrentDisplay = display;
        Changed?.Invoke();
    }

    /// <summary>
    /// 清洗存储值: 已卸载的系统字体 / 丢失或损坏的导入文件一律回落空串(= 默认)。
    /// 与 AppPaths.NormalizeDir 对非法下载路径的处理同一思路: 不让脏值流到下游。
    /// </summary>
    public static string SanitizeStored(string? stored)
    {
        var (kind, name) = ParseStored(stored);
        return kind switch
        {
            StoredKind.File => TryResolveImport(name) != null ? FilePrefix + name : "",
            StoredKind.System => IsSystemFont(name) ? name : "",
            _ => ""
        };
    }

    /// <summary>
    /// 系统里是否装着这个名字的字体。
    /// ★ 判据是"在系统字体列表里按 Source 匹配", 不是 new FontFamily(name).FamilyTypefaces ——
    ///   实测(探针 bd-probe-uifont)未知族名的 FamilyTypefaces 也返回非空的回退集, 判不出来。
    /// ★ 列表懒加载 + 进程级缓存(与设置页下拉共用): 只有存了自定义系统字体的用户
    ///   才会在启动时付一次枚举开销, 默认配置(空串)一个系统调用都不发。
    /// </summary>
    private static bool IsSystemFont(string name)
    {
        try
        {
            return SystemFonts.Any(x => string.Equals(x.Source, name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>存储值 → (生效字体, 显示名)。解析不了的一律回落默认。</summary>
    private static (FontFamily Family, string Display) Resolve(string? stored)
    {
        var (kind, name) = ParseStored(stored);
        if (kind == StoredKind.File)
        {
            var import = TryResolveImport(name);
            if (import != null) return import.Value;
        }
        else if (kind == StoredKind.System && IsSystemFont(name))
        {
            var display = SystemFonts
                .FirstOrDefault(x => string.Equals(x.Source, name, StringComparison.OrdinalIgnoreCase)).Display;
            return (new FontFamily(BuildSystemSource(name)),
                    string.IsNullOrEmpty(display) ? name : display);
        }
        return (new FontFamily(DefaultSource), "默认");
    }

    // ------------------------------------------------------------ 导入字体文件

    /// <summary>
    /// 导入一个字体文件: 复制到 fonts 下的独立子目录并验证可加载。
    /// 成功返回存储值("file:目录名"), 失败返回 null(调用方弹 Toast)。
    /// ★ 只支持 .ttf/.otf/.ttc; .ttc(集合)含多个族时取第一个可加载的。
    /// </summary>
    public static string? ImportFile(string sourcePath, out string display)
    {
        display = "";
        string? dir = null;
        try
        {
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (ext is not (".ttf" or ".otf" or ".ttc")) return null;
            if (!File.Exists(sourcePath)) return null;

            var fileName = Path.GetFileName(sourcePath);
            var dirName = SanitizeBaseName(fileName) + "_" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
            dir = Path.Combine(AppPaths.FontDir, dirName);
            Directory.CreateDirectory(dir);
            File.Copy(sourcePath, Path.Combine(dir, fileName), overwrite: true);

            var resolved = TryResolveImport(dirName);
            if (resolved == null)
            {
                // 不是可用的字体(损坏/伪装扩展名): 清掉刚复制的目录, 不留垃圾
                TryDeleteDir(dir);
                return null;
            }
            display = resolved.Value.Display;
            return FilePrefix + dirName;
        }
        catch
        {
            if (dir != null) TryDeleteDir(dir);
            return null;
        }
    }

    /// <summary>
    /// 读某个导入目录里的字体族并验证可加载。目录里只有本次导入的一个文件,
    /// 所以枚举到的第一个可加载族就是它(.ttc 集合取第一个)。
    /// </summary>
    private static (FontFamily Family, string Display)? TryResolveImport(string dirName)
    {
        try
        {
            var dir = Path.Combine(AppPaths.FontDir, dirName);
            if (!Directory.Exists(dir)) return null;
            var baseUri = new Uri(dir + Path.DirectorySeparatorChar);
            foreach (var ff in Fonts.GetFontFamilies(baseUri))
            {
                if (new Typeface(ff, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal)
                        .TryGetGlyphTypeface(out _))
                    return (ff, DisplayName(ff));
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>供设置页读当前导入字体的显示名与预览字体(下拉里那一项要用它自己的字体渲染)</summary>
    public static bool TryGetImport(string dirName, out FontFamily family, out string display)
    {
        var r = TryResolveImport(dirName);
        if (r == null)
        {
            family = new FontFamily(DefaultSource);
            display = "";
            return false;
        }
        family = r.Value.Family;
        display = r.Value.Display;
        return true;
    }

    /// <summary>尽力删除 keep 之外的导入子目录(删不掉的留到下次启动, 见类注释)</summary>
    private static void PruneOtherImports(string? keep)
    {
        try
        {
            if (!Directory.Exists(AppPaths.FontDir)) return;
            foreach (var dir in Directory.GetDirectories(AppPaths.FontDir))
            {
                var name = Path.GetFileName(dir);
                if (string.Equals(name, keep, StringComparison.OrdinalIgnoreCase)) continue;
                TryDeleteDir(dir);
            }
        }
        catch
        {
            // 清理失败不影响功能
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* 被占用就留到下次启动 */ }
    }

    // ------------------------------------------------------------ 系统字体列表(设置页下拉用)

    /// <summary>
    /// 图标/符号字体黑名单: 它们没有正常文字字形, 选成界面字体会满屏豆腐块。
    /// ★ 只排这几个确定的; 不按"字形覆盖率"启发式过滤 —— 误杀用户想用的字体比留几个怪字体更糟。
    /// </summary>
    private static readonly HashSet<string> IconFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Segoe Fluent Icons", "Segoe MDL2 Assets", "Marlett"
    };

    private static IReadOnlyList<(string Source, string Display)>? _systemFonts;

    /// <summary>
    /// 系统已安装字体(按显示名排序)。★ 懒加载 + 进程级缓存: 枚举全部字体要上百毫秒,
    /// 只在第一次打开设置页时付一次, 不拖启动。
    /// </summary>
    public static IReadOnlyList<(string Source, string Display)> SystemFonts => _systemFonts ??= LoadSystemFonts();

    private static IReadOnlyList<(string Source, string Display)> LoadSystemFonts()
    {
        try
        {
            return Fonts.SystemFontFamilies
                .Where(f => !IconFonts.Contains(f.Source))
                .Select(f => (f.Source, DisplayName(f)))
                .OrderBy(x => x.Item2, StringComparer.CurrentCulture)
                .ToList();
        }
        catch
        {
            return Array.Empty<(string Source, string Display)>();
        }
    }

    /// <summary>字体的显示名: 优先中文名, 其次英文名, 再其次任意语言, 最后退回 Source。</summary>
    private static string DisplayName(FontFamily ff)
    {
        if (ff.FamilyNames.TryGetValue(XmlLanguage.GetLanguage("zh-cn"), out var zh) && zh.Length > 0) return zh;
        if (ff.FamilyNames.TryGetValue(XmlLanguage.GetLanguage("en-us"), out var en) && en.Length > 0) return en;
        foreach (var name in ff.FamilyNames.Values)
            if (!string.IsNullOrEmpty(name)) return name;
        // 文件夹字体的 Source 形如 "./#族名", 去掉前缀给人看
        var src = ff.Source;
        var hash = src.IndexOf('#');
        return hash >= 0 ? src[(hash + 1)..] : src;
    }
}