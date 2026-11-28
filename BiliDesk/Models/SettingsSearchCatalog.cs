using System;
using System.Collections.Generic;
using System.Linq;

namespace BiliDesk.Models;

/// <summary>
/// 设置页搜索索引里的一条(2026-11-28)。
///
/// ★ 为什么用"行的标题文字"当定位锚点, 而不是给 XAML 每个行加 x:Name / Tag:
///   五个 Tab 的内容在 <c>Views/Pages/Settings/SettingsTabs.xaml</c> 里, 是 DataTemplate
///   而不是页面字段 —— 每次切 Tab 都是全新的一棵可视树, x:Name 拿不到、Tag 要手写 52 遍。
///   标题文字本来就是唯一的(测试会校验), 定位时在**当前 Tab 的子树**里按 Text 找即可。
///
/// ★ 维护约定: **新增/改名一个设置行, 必须同步这里**, 否则
///   <c>BiliDesk.Tests/SettingsSearchTests</c> 里的契约测试会直接失败(它会数
///   SettingsTabs.xaml 里 SettingsRowTitleStyle 的出现次数并逐条核对 Text="…")。
/// </summary>
public sealed class SettingsSearchEntry
{
    public SettingsSearchEntry(
        int tabIndex,
        string tabTitle,
        string group,
        string title,
        string keywords = "",
        string? locateTitle = null,
        bool isShortcutEntry = false)
    {
        TabIndex = tabIndex;
        TabTitle = tabTitle;
        Group = group;
        Title = title;
        Keywords = keywords;
        // 默认就用标题自己当锚点; 少数"会随状态折叠"的行指到它所在分组里常驻的那一行。
        LocateTitle = locateTitle ?? title;
        IsShortcutEntry = isShortcutEntry;
    }

    /// <summary>目标 Tab 序号(与 SettingsViewModel.TabIndex 同一套: 0=通用 … 4=关于)</summary>
    public int TabIndex { get; }

    /// <summary>目标 Tab 的名字(匹配用, 也是结果里"播放 · 兼容性"的前半段)</summary>
    public string TabTitle { get; }

    /// <summary>所在区块/分组名(结果里的后半段)</summary>
    public string Group { get; }

    /// <summary>行的标题 —— 也是定位锚点(必须与 XAML 的 Text="…" 完全一致)</summary>
    public string Title { get; }

    /// <summary>同义词(搜索用, 不进界面): 把用户嘴里的说法映射到设置项上</summary>
    public string Keywords { get; }

    /// <summary>真正要滚到并高亮的那一行的标题(默认 = <see cref="Title"/>)</summary>
    public string LocateTitle { get; }

    /// <summary>快捷键 Tab 里由 ShortcutCatalog 生成的行(标题是绑定出来的, 不进 XAML 契约测试)</summary>
    public bool IsShortcutEntry { get; }

    /// <summary>结果行右侧的位置小字, 例如 "播放 · 兼容性"</summary>
    public string LocationText => TabTitle + " · " + Group;

    /// <summary>命中排序分: 越小越靠前(-1 = 不命中)</summary>
    internal int ScoreOf(string token)
    {
        if (Title.Equals(token, StringComparison.OrdinalIgnoreCase)) return 0;
        if (Title.StartsWith(token, StringComparison.OrdinalIgnoreCase)) return 1;
        if (Title.Contains(token, StringComparison.OrdinalIgnoreCase)) return 2;
        if (Keywords.Contains(token, StringComparison.OrdinalIgnoreCase)) return 3;
        if (Group.Contains(token, StringComparison.OrdinalIgnoreCase)) return 4;
        if (TabTitle.Contains(token, StringComparison.OrdinalIgnoreCase)) return 5;
        return -1;
    }
}

/// <summary>
/// 设置页搜索的索引(2026-11-28, 用户要求"设置页增加搜索功能")。
///
/// 纯本地: 不请求网络、不需要防抖 —— 52 条静态行 + 15 条快捷键, 每次输入遍历一遍即可。
/// 命中后由页面负责"切 Tab + 滚到那一行 + 高亮一下"(见 SettingsPage.xaml.cs)。
/// </summary>
public static class SettingsSearchCatalog
{
    /// <summary>五个 Tab 的显示名(与 SettingsPage.xaml 的胶囊一致, 也是定位时的 TabIndex 依据)</summary>
    public static IReadOnlyList<string> TabTitles { get; } = new[] { "通用", "播放", "快捷键", "数据管理", "关于" };

    private const int TabGeneral = 0;
    private const int TabPlayback = 1;
    private const int TabShortcut = 2;
    private const int TabData = 3;
    private const int TabAbout = 4;

    /// <summary>全量索引: 按 Tab 顺序 + 页面上的视觉顺序排列(命中分相同时保持这个顺序)</summary>
    public static IReadOnlyList<SettingsSearchEntry> All { get; } = Build();

    private static IReadOnlyList<SettingsSearchEntry> Build()
    {
        var list = new List<SettingsSearchEntry>
        {
            // ==================== 通用 ====================
            new(TabGeneral, TabTitles[TabGeneral], "首页推荐", "推荐来源",
                "首页 推荐 网页版 app 端 算法 内容源"),
            new(TabGeneral, TabTitles[TabGeneral], "外观", "应用主题",
                "主题 深色 浅色 夜间 暗色 跟随系统 颜色模式"),
            new(TabGeneral, TabTitles[TabGeneral], "外观", "主题色",
                "强调色 配色 颜色 hex 调色盘 粉色 跟随系统"),
            new(TabGeneral, TabTitles[TabGeneral], "外观", "界面字体",
                "字体 字形 导入字体 ttf otf 微软雅黑"),
            new(TabGeneral, TabTitles[TabGeneral], "外观", "窗口背景材质",
                "材质 亚克力 云母 mica 模糊 透明 磨砂 背景"),
            new(TabGeneral, TabTitles[TabGeneral], "外观", "亚克力透明度 · 浅色",
                "透明度 不透明 浅色 亚克力 磨砂", locateTitle: "窗口背景材质"),
            new(TabGeneral, TabTitles[TabGeneral], "外观", "亚克力透明度 · 深色",
                "透明度 不透明 深色 亚克力 磨砂", locateTitle: "窗口背景材质"),
            new(TabGeneral, TabTitles[TabGeneral], "交互", "滚轮速度",
                "滚轮 滚动 速度 鼠标 灵敏度 一格"),
            new(TabGeneral, TabTitles[TabGeneral], "交互", "卡片列数",
                "列数 网格 排版 每行 卡片 布局"),
            new(TabGeneral, TabTitles[TabGeneral], "交互", "软件缩放",
                "缩放 放大 缩小 界面大小 dpi 比例"),
            new(TabGeneral, TabTitles[TabGeneral], "交互", "在系统浏览器中打开链接",
                "浏览器 外链 链接 系统浏览器 内置浏览器"),
            new(TabGeneral, TabTitles[TabGeneral], "托盘", "关闭时最小化到托盘",
                "托盘 关闭 最小化 后台 退出"),
            new(TabGeneral, TabTitles[TabGeneral], "托盘", "立即收进托盘",
                "托盘 最小化 后台 隐藏"),

            // ==================== 播放 ====================
            new(TabPlayback, TabTitles[TabPlayback], "跳过赞助片段", "自动跳过赞助片段",
                "赞助 广告 恰饭 sponsorblock 跳过 片段"),
            new(TabPlayback, TabTitles[TabPlayback], "跳过赞助片段", "跳过的类别",
                "类别 片段 类型 赞助 广告 跳过"),
            new(TabPlayback, TabTitles[TabPlayback], "画质", "记住播放器窗口大小和位置",
                "播放器 窗口 大小 位置 记忆 尺寸"),
            new(TabPlayback, TabTitles[TabPlayback], "画质", "记住主界面窗口大小和位置",
                "主界面 窗口 大小 位置 记忆 尺寸"),
            new(TabPlayback, TabTitles[TabPlayback], "画质", "默认画质",
                "画质 清晰度 分辨率 1080p 4k 高清 码率 qn"),
            new(TabPlayback, TabTitles[TabPlayback], "画质", "免登录 1080P",
                "1080p 未登录 免登录 清晰度 游客"),
            new(TabPlayback, TabTitles[TabPlayback], "音质", "音质档位",
                "音质 音频 无损 hires 杜比 192k 码率"),
            new(TabPlayback, TabTitles[TabPlayback], "音质", "音频重采样",
                "重采样 蓝牙 电流音 卡顿 音频 采样率"),
            new(TabPlayback, TabTitles[TabPlayback], "音质", "优先高音质音轨",
                "音轨 高音质 无损 hires 杜比"),
            new(TabPlayback, TabTitles[TabPlayback], "播放进度", "自动续播",
                "续播 进度 记忆 接着看 上次"),
            new(TabPlayback, TabTitles[TabPlayback], "播放页", "显示相关视频",
                "相关视频 推荐 信息栏 右侧"),
            new(TabPlayback, TabTitles[TabPlayback], "播放页", "自动连播",
                "连播 自动播放 下一条 队列"),
            new(TabPlayback, TabTitles[TabPlayback], "播放页", "详情栏展开把手",
                "详情栏 把手 展开 信息栏 评论"),
            new(TabPlayback, TabTitles[TabPlayback], "字幕", "自动显示字幕",
                "字幕 cc ai 字幕 自动"),
            new(TabPlayback, TabTitles[TabPlayback], "字幕", "字幕字号",
                "字幕 字号 大小 字体大小"),
            new(TabPlayback, TabTitles[TabPlayback], "缓冲", "缓冲策略",
                "缓冲 预读 策略 自动 手动 卡顿"),
            new(TabPlayback, TabTitles[TabPlayback], "缓冲", "网络缓冲",
                "缓冲 预读 网络 卡顿 延迟 内存"),
            new(TabPlayback, TabTitles[TabPlayback], "网络线路", "线路选择",
                "线路 cdn 节点 测速 网络"),
            new(TabPlayback, TabTitles[TabPlayback], "网络线路", "指定 CDN",
                "cdn 线路 指定 节点 域名"),
            new(TabPlayback, TabTitles[TabPlayback], "网络线路", "屏蔽 PCDN",
                "pcdn 屏蔽 点对点 带宽"),
            new(TabPlayback, TabTitles[TabPlayback], "兼容性", "视频输出",
                "视频输出 渲染 d3d11 d3d9 opengl 花屏 黑屏 显卡"),
            new(TabPlayback, TabTitles[TabPlayback], "兼容性", "硬件解码",
                "硬解 软解 解码 卡住 冻结 画面 显卡 nvidia"),
            new(TabPlayback, TabTitles[TabPlayback], "弹幕", "显示弹幕",
                "弹幕 显示 隐藏 danmaku"),
            new(TabPlayback, TabTitles[TabPlayback], "弹幕", "智能屏蔽",
                "弹幕 屏蔽 过滤 刷屏 长弹幕"),
            new(TabPlayback, TabTitles[TabPlayback], "弹幕", "弹幕显示区域",
                "弹幕 区域 高度 占比 屏幕"),
            new(TabPlayback, TabTitles[TabPlayback], "弹幕", "按类型过滤",
                "弹幕 过滤 类型 滚动 固定 彩色 高级"),
            new(TabPlayback, TabTitles[TabPlayback], "弹幕", "弹幕外观",
                "弹幕 外观 样式 不透明度 字号 速度 底衬"),
            new(TabPlayback, TabTitles[TabPlayback], "弹幕", "胶囊底衬",
                "弹幕 底衬 背景 胶囊 纯文字", locateTitle: "弹幕外观"),

            // ==================== 数据管理 ====================
            new(TabData, TabTitles[TabData], "缓存", "缓存位置",
                "缓存 下载 目录 路径 存储位置 文件夹"),
            new(TabData, TabTitles[TabData], "缓存", "封面图片缓存",
                "封面 图片 缓存 清理 占用 磁盘"),
            new(TabData, TabTitles[TabData], "缓存", "短视频下载缓存",
                "短视频 下载 缓存 清理 合流 占用"),

            // ==================== 关于 ====================
            new(TabAbout, TabTitles[TabAbout], "更新", "自动检查更新",
                "更新 升级 检查 新版本"),
            new(TabAbout, TabTitles[TabAbout], "更新", "检查更新",
                "更新 升级 检查 新版本"),
            new(TabAbout, TabTitles[TabAbout], "更新", "版本号",
                "版本 版本号 version"),
            new(TabAbout, TabTitles[TabAbout], "问题诊断", "导出诊断报告",
                "诊断 报告 日志 导出 zip 反馈"),
            new(TabAbout, TabTitles[TabAbout], "问题诊断", "界面卡住检测",
                "卡住 冻结 卡顿 检测 统计"),
            new(TabAbout, TabTitles[TabAbout], "问题诊断", "崩溃转储",
                "崩溃 闪退 转储 dump 诊断"),
            new(TabAbout, TabTitles[TabAbout], "声明", "非官方声明与免责声明",
                "声明 免责 协议 非官方 版权"),
        };

        // 快捷键 Tab: 目录在 Models/ShortcutCatalog, 这里只投影 —— 那边加一条这里自动有。
        // 分组直接用目录里的分组名(界面上就是那张卡片的区块标题)。
        list.Add(new SettingsSearchEntry(TabShortcut, TabTitles[TabShortcut], "快捷键", "自定义按键",
            "快捷键 按键 改键 组合键 恢复默认"));
        foreach (var d in ShortcutCatalog.All)
        {
            list.Add(new SettingsSearchEntry(
                TabShortcut, TabTitles[TabShortcut], d.Group, d.Title,
                d.Description, isShortcutEntry: true));
        }

        return list;
    }

    /// <summary>
    /// 按关键词搜索。空/空白 → 空结果; 多个词(空格分隔)按"全部命中"处理。
    /// 排序: 标题完全相等 > 标题前缀 > 标题包含 > 同义词 > 分组 > Tab 名; 同分保持索引顺序。
    /// </summary>
    public static IReadOnlyList<SettingsSearchEntry> Search(string? query)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return Array.Empty<SettingsSearchEntry>();

        var tokens = q.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var hits = new List<(int Score, int Index, SettingsSearchEntry Entry)>();

        for (var i = 0; i < All.Count; i++)
        {
            var entry = All[i];
            var best = int.MaxValue;
            var matchedAll = true;
            foreach (var token in tokens)
            {
                var score = entry.ScoreOf(token);
                if (score < 0) { matchedAll = false; break; }
                if (score < best) best = score;
            }
            if (!matchedAll) continue;
            hits.Add((best, i, entry));
        }

        return hits
            .OrderBy(h => h.Score)
            .ThenBy(h => h.Index)
            .Select(h => h.Entry)
            .ToList();
    }
}
