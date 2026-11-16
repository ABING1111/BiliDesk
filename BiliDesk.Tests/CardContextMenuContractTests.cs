using System;
using System.IO;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 卡片右键菜单的契约测试(issue #84, 2026-11-16)。钉住两个曾经踩过的坑:
///
/// ① **页面追加项插不进去**: 收藏页 / 稍后再看页往卡片菜单里追加「移出收藏」「移出稍后再看」,
///    走 <c>ExtraMenuItems</c> → ApplyExtraMenuItems。2026-11-09 的内存优化把菜单改成
///    "第一次右键才建"(BuildMenu) 之后, ApplyExtraMenuItems 改成从字段 _menu 取目标菜单 ——
///    而 BuildMenu 里调用它时 _menu 还没被赋值(方法没返回), 于是每次都早退,
///    两个页面追加的菜单项一条都插不进去。
///
/// ② **要点两下才弹**: 菜单懒建 ⇒ 第一次右键时 ContextMenu 还是 null。WPF 的 ContextMenuService
///    是 class handler, 执行顺序在实例 handler 之前, 它那一趟取到 null 就直接放弃弹出
///    (见 PopupControlService.RaiseContextMenuOpeningEvent: TargetElement 为 null 时 return false)。
///    所以懒建的那一趟必须自己 e.Handled + IsOpen, 否则"第一次只是把菜单挂上, 第二次才弹"。
///    DynamicCard 还会在 DataContextChanged 里清掉 _menu(虚拟化复用), 因此每换一条数据都要重来一次。
///
/// ★ 为什么用源码契约测试而不是 UI 测试: 本测试工程按约定不引用 WPF(被测对象全是纯逻辑类),
///   而这两条 bug 恰恰只发生在"控件构造/事件时序"上 —— 端到端验证靠探针(构造真实卡片与页面,
///   走 PopupControlService 的右键入口读实际 IsOpen 与菜单项)。这里钉住结构, 防止退化。
/// </summary>
public class CardContextMenuContractTests
{
    private static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BiliDesk.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("找不到 BiliDesk.sln, 无法定位仓库根");
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(RepoRoot, "BiliDesk", "Views", "Controls", file));

    /// <summary>
    /// 截出某个方法的源码(从签名到方法体收尾的 4 空格缩进大括号)。
    /// ★ 不能只按 "方法名(" 定位: 声明处和调用处长得一样(_menu ??= BuildMenu())。
    ///   这里取"右括号后面紧跟 4 空格缩进的方法体大括号"的那一处 —— 只有声明满足。
    /// </summary>
    private static string MethodSource(string source, string file, string name)
    {
        var from = 0;
        while (true)
        {
            var start = source.IndexOf(" " + name + "(", from, StringComparison.Ordinal);
            Assert.True(start >= 0, $"{file} 里找不到方法 {name} 的声明");
            var paren = source.IndexOf(')', start);
            if (paren > 0 && source.AsSpan(paren + 1).StartsWith("\n    {"))
            {
                var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
                Assert.True(end > start, $"方法 {name} 的收尾大括号没找到");
                return source.Substring(start, end - start);
            }
            from = start + 1;
        }
    }

    // ------------------------------------------------------------ VideoCard

    private const string VideoCardFile = "VideoCard.xaml.cs";
    private static string VideoCardSource { get; } = Read(VideoCardFile);

    private static string VideoCardMethod(string name) => MethodSource(VideoCardSource, VideoCardFile, name);

    [Fact]
    public void ApplyExtraMenuItems_必须由调用方传入目标菜单()
    {
        // 签名里要有 ContextMenu 参数 —— 不能再靠读字段 _menu(首次建菜单时它还是 null)
        Assert.Contains("ContextMenu menu", VideoCardMethod("ApplyExtraMenuItems"));
    }

    [Fact]
    public void ApplyExtraMenuItems_不得读字段_menu()
    {
        Assert.DoesNotContain("_menu", VideoCardMethod("ApplyExtraMenuItems"));
    }

    [Fact]
    public void BuildMenu_把刚建好的菜单传给ApplyExtraMenuItems()
    {
        // BuildMenu 里的局部变量就叫 menu; 传进去才可能真的插上条目
        Assert.Contains("ApplyExtraMenuItems(menu,", VideoCardMethod("BuildMenu"));
    }

    [Fact]
    public void 重建时的订阅只挂一次()
    {
        // 订阅若在 Rebuild 与 Apply 两处都挂, 菜单建好后再重建会重复订阅,
        // 定义集合增删一次会触发多次重建(条目被反复摘插)。
        Assert.Contains("items.CollectionChanged += OnExtraSourceChanged", VideoCardMethod("RebuildExtraMenuItems"));
        Assert.DoesNotContain("CollectionChanged +=", VideoCardMethod("ApplyExtraMenuItems"));
    }

    // ------------------------------------------------------------ 懒建菜单: 首次必须自己弹

    [Theory]
    [InlineData(VideoCardFile)]
    [InlineData("DynamicCard.xaml.cs")]
    public void 懒建菜单的首次右键要自己弹(string file)
    {
        var source = Read(file);
        var body = MethodSource(source, file, "OnContextMenuOpening");
        // 首次弹出可能抽成独立方法(OpenMenuSelf): 抽了就把它一并纳入判定
        if (source.Contains("void OpenMenuSelf", StringComparison.Ordinal))
            body += MethodSource(source, file, "OpenMenuSelf");

        // 记住"这一次是不是首次"(建之前 _menu 为 null)
        Assert.Contains("_menu == null", body);
        // 首次那一趟必须补上 WPF 跳过的那一步
        Assert.Contains("e.Handled = true", body);
        Assert.Contains("IsOpen = true", body);
    }

    [Theory]
    [InlineData(VideoCardFile)]
    [InlineData("DynamicCard.xaml.cs")]
    public void 键盘触发时不要弹到鼠标位置(string file)
    {
        // Shift+F10 的 CursorLeft 是 -1: 用 Center, 否则会弹到鼠标上次停留处
        var source = Read(file);
        var body = MethodSource(source, file, "OnContextMenuOpening");
        if (source.Contains("void OpenMenuSelf", StringComparison.Ordinal))
            body += MethodSource(source, file, "OpenMenuSelf");
        Assert.Contains("CursorLeft", body);
    }
}
