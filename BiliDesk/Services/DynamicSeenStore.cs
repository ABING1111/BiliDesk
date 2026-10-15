using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace BiliDesk.Services;

/// <summary>
/// 动态页左栏「未读」角标的**本地基准**: 每个 UP 主上次看到的最新一条动态的 bvid。
///
/// ★ 为什么是本地判断而不是问接口(2026-10-15 用户选定):
///   B 站官方的"新动态"是服务端基线(请求带 update_baseline, 返回 update_num),
///   而本客户端走的那条动态接口没接这套。要拿它就得换接口 + 改请求参数, 风险大;
///   于是在本地记一份"上次看到的最新一条", 刷新时对不上就打角标 —— 零接口改动。
///
/// ★ 语义边界(用户可能问"为什么角标不消失"): 角标表示"这个 UP 有新投稿, 你还没单独点进去看过",
///   **看一眼全部动态不算已读**(否则页面默认就停在全部动态, 角标一进页面就被自己清掉, 等于没有)。
///   点开某个 UP 才清它自己的角标。
///
/// ★ 首次运行只会建立基准、不打角标: 没有基准时若直接判定"全是新的", 第一次打开动态页
///   会满屏角标 —— 那不是"有更新", 那只是"以前没记过"。
///
/// 落盘位置: %LocalAppData%\BiliDesk\dynamic_seen.json(与其它数据文件同一处)。
/// 读写全部包在 try/catch 里: 角标是锦上添花, 文件损坏/无权限都不能影响动态页本身。
/// </summary>
public static class DynamicSeenStore
{
    private static string FilePath => Path.Combine(AppPaths.DataDir, "dynamic_seen.json");

    private static Dictionary<long, string>? _map;
    private static bool _dirty;

    private static Dictionary<long, string> Map => _map ??= Load();

    private static Dictionary<long, string> Load()
    {
        try
        {
            // DataDir 还没就位时(理论上只有启动极早期)不碰文件系统 ——
            // Path.Combine("", "x.json") 会算出**相对路径**, 写出去就落到进程当前目录了
            if (string.IsNullOrEmpty(AppPaths.DataDir)) return new Dictionary<long, string>();
            if (!File.Exists(FilePath)) return new Dictionary<long, string>();
            var map = JsonSerializer.Deserialize<Dictionary<long, string>>(File.ReadAllText(FilePath));
            return map ?? new Dictionary<long, string>();
        }
        catch
        {
            // 读失败(损坏/无权限)等价于"没有基准": 下一次全部动态加载会重建基准, 不会崩
            return new Dictionary<long, string>();
        }
    }

    /// <summary>取某个 UP 上次看到的最新一条动态的 bvid; 没有基准时返回 false</summary>
    public static bool TryGet(long mid, out string bvid)
    {
        if (mid <= 0) { bvid = ""; return false; }
        return Map.TryGetValue(mid, out bvid!) && !string.IsNullOrEmpty(bvid);
    }

    /// <summary>记下某个 UP 最新看到的动态(只改内存, 落盘见 <see cref="Flush"/>)</summary>
    public static void Set(long mid, string bvid)
    {
        if (mid <= 0 || string.IsNullOrEmpty(bvid)) return;
        if (Map.TryGetValue(mid, out var old) && old == bvid) return;
        Map[mid] = bvid;
        _dirty = true;
    }

    /// <summary>
    /// 把这一批改动写回磁盘。
    /// ★ 单独留一个 Flush 而不是让 Set 自己写: 首次加载会一次性给几十个 UP 建基准,
    ///   每个都写一遍文件就是几十次磁盘 IO —— 批量处理完写一次即可。
    /// </summary>
    public static void Flush()
    {
        if (!_dirty) return;
        if (string.IsNullOrEmpty(AppPaths.DataDir)) return;   // 同上: 宁可不存, 也不写到相对路径
        _dirty = false;
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Map));
        }
        catch
        {
            // 写不进去只影响"下次启动角标准不准", 不该打扰用户
        }
    }
}
