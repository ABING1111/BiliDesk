using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BiliDesk.Helpers;
using BiliDesk.Models;

namespace BiliDesk.Services;

/// <summary>应用设置存储(settings.json)</summary>
public class SettingsStore
{
    public static SettingsStore Instance { get; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string FilePath => Path.Combine(AppPaths.DataDir, "settings.json");

    public AppTheme ThemeMode { get; set; } = AppTheme.System;

    /// <summary>弹幕开关(全局)</summary>
    public bool DanmakuEnabled { get; set; } = true;

    /// <summary>弹幕显示区域占比(25~100)</summary>
    public int DanmakuAreaPercent { get; set; } = 25;

    /// <summary>弹幕智能屏蔽</summary>
    public bool DanmakuSmartFilter { get; set; } = true;

    /// <summary>彩色弹幕(关掉则统一白色)</summary>
    public bool DanmakuColorful { get; set; } = true;

    /// <summary>弹幕关键词屏蔽(换行/逗号分隔, "re:" 前缀表示正则)</summary>
    public string DanmakuBlockKeywords { get; set; } = "";

    /// <summary>
    /// 首页「推荐」用哪套算法。★ 2026-10-03 起**固定为网页版**, 字段只为兼容老配置保留。
    ///
    /// 历史: 这里原来是"App 官方 / 浏览器网页版"二选一, 默认 App。
    ///   但实测 App 端接口(app.bilibili.com/x/v2/feed/index)对第三方客户端**不提供个性化** ——
    ///   即使拿到有效 access_key, 返回的仍是全站通用热门池(100 条里命中关注的 UP 只有 1~2 个,
    ///   平均播放量 66~103 万); 网页 rcmd 平均只有 17 万、内容明显更垂直。
    ///   所以选择项与 App 令牌机制整体删除, 默认值也改成 Web —— 老配置里存着 App 的用户
    ///   在启动时会被下面的迁移逻辑纠正。
    /// </summary>
    public RecommendSource RecommendSource { get; set; } = RecommendSource.Web;

    /// <summary>是否启用"跳过赞助片段"(默认关, 原因见 AppSettings.SponsorBlockEnabled)</summary>
    public bool SponsorBlockEnabled { get; set; }

    /// <summary>要跳过的片段类别(逗号分隔的服务端 category 原值)</summary>
    public string SponsorBlockCategories { get; set; } = SponsorCategories.Sponsor;

    /// <summary>
    /// 主题色(强调色)的 `#RRGGBB`。空 = 用默认的 B 站粉。
    /// 老配置没有这个字段 → 反序列化保持初始化值(空串) → 自动落到默认色。
    /// </summary>
    public string AccentColor { get; set; } = "";

    /// <summary>关闭主窗口时是否最小化到托盘。
    ///
    /// 默认 **true**: 点右上角关闭键只是把窗口收进托盘(进程继续跑, 双击托盘图标恢复),
    /// 要真正退出得走托盘右键菜单的「退出」。这是用户在 2026-09-24 明确要求的行为 ——
    /// 但必须给一个关掉的入口: 有一部分用户不接受"点了关闭程序还在后台跑"。
    /// </summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>启动后自动检查更新(默认开; 关掉后仍可在设置页手动检查)</summary>
    public bool AutoCheckUpdate { get; set; } = true;

    /// <summary>用户点过「跳过此版本」的版本号(空 = 没跳过; 只影响自动检查, 见 UpdateChecker.AutoCheckAsync)</summary>
    public string SkippedVersion { get; set; } = "";

    /// <summary>
    /// 免登录 1080P(默认开)。未登录时给取流请求补 try_look + dm_img_* 四件套,
    /// 把匿名可见清晰度从 360P/480P 提到 720P/1080P —— 依据见 ApiClient.ApplyTryLook。
    /// </summary>
    public bool NoLogin1080P { get; set; } = true;

    /// <summary>用户同意过的是第几版声明(见 DisclaimerText.Version)</summary>
    public int AcceptedDisclaimerVersion { get; set; }

    /// <summary>
    /// 是否已同意**当前版本**的免责声明。
    /// 算出来而不是存一个 bool: 声明正文一改, Version 一变, 这里自动变回 false,
    /// 用户会被再问一次 —— 不会出现"点了同意却看到的是另一份文本"。
    /// </summary>
    public bool DisclaimerAccepted => AcceptedDisclaimerVersion >= DisclaimerText.Version;

    /// <summary>设置变更通知(播放器等运行中的界面据此同步)</summary>
    public event Action? DanmakuSettingsChanged;

    /// <summary>
    /// 推荐算法来源变更。首页据此清掉旧列表并按新来源重新拉一次 ——
    /// 不通知的话用户切完算法还得自己去点刷新才生效, 看起来像"改了没用"。
    /// </summary>
    public event Action? RecommendSourceChanged;

    /// <summary>
    /// SponsorBlock 开关 / 类别变更。播放器据此重算"当前该跳哪些片段" ——
    /// 关掉要立刻停跳, 改类别要立刻生效, 否则用户会以为开关是坏的。
    /// </summary>
    public event Action? SponsorBlockChanged;

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts);
                if (s != null)
                {
                    ThemeMode = s.ThemeMode;
                    DanmakuEnabled = s.DanmakuEnabled;
                    DanmakuAreaPercent = Math.Clamp(s.DanmakuAreaPercent, 25, 100);
                    DanmakuSmartFilter = s.DanmakuSmartFilter;
                    // 老配置同样靠"属性初始化值"拿到默认 true
                    DanmakuColorful = s.DanmakuColorful;
                    DanmakuBlockKeywords = s.DanmakuBlockKeywords ?? "";
                    // ★ 算法选择已删除(2026-10-03): 一律落到网页版。
                    //   老配置里存着 App 的会被这里纠正 —— 那一档实测对第三方客户端
                    //   不提供个性化(通用热门池), 让它继续生效就是让用户看一屏热门。
                    RecommendSource = RecommendSource.Web;
                    // 主题色: 老配置没有这个字段(空串) → 保持默认的 B 站粉
                    AccentColor = s.AccentColor ?? "";
                    // 同理: 老配置的 SponsorBlockEnabled 保持 false(默认关), 类别保持默认的 sponsor。
                    // 类别串要 Sanitize 一下: 配置里可能留着本应用不做的 interaction, 或被手改坏 ——
                    // 不清掉就会出现"界面上一个都没勾、后台却还在按旧类别跳"的矛盾。
                    SponsorBlockEnabled = s.SponsorBlockEnabled;
                    SponsorBlockCategories = SponsorCategories.Sanitize(s.SponsorBlockCategories);
                    // 老配置里可能残留 StartupPage 字段 —— 启动页设置已删除, 这里不再读它
                    // (留着旧值无害: 反序列化比手写的多认字段, 序列化时不再写出去)。
                    // 老配置的 JSON 里没有这个字段 —— 属性初始化默认值是 true, 反序列化时
                    // "缺失的字段保持初始化值", 所以老配置会自动拿到"默认打开", 不需要版本号兼容。
                    CloseToTray = s.CloseToTray;
                    // 老配置没有这两个字段 —— 属性初始化默认值(true / 空串)自动生效, 不需要版本号兼容。
                    AutoCheckUpdate = s.AutoCheckUpdate;
                    SkippedVersion = s.SkippedVersion ?? "";
                    // 老配置没有这个字段 —— 属性初始化默认值(true)自动生效, 不需要版本号兼容。
                    NoLogin1080P = s.NoLogin1080P;
                    // 老配置里只有 DisclaimerAccepted(bool)、没有版本号。
                    // 那时用户同意的是第 1 版声明, 所以按 1 记 —— 这一版改动后仍会正常再问一次。
                    AcceptedDisclaimerVersion = s.DisclaimerVersion > 0
                        ? s.DisclaimerVersion
                        : (s.DisclaimerAccepted ? 1 : 0);
                }
            }
        }
        catch
        {
            ThemeMode = AppTheme.System;
        }
    }

    /// <summary>
    /// 保存设置。**异步落盘, 合并写入**。
    ///
    /// 为什么不能在这里同步写文件:
    ///   这个方法跑在 UI 线程上, 而它的调用点大多在**对帧率敏感**的路径里 ——
    ///   换肤(ThemeService.SetMode)、拖弹幕区域滑块(每个 tick 一次)、改启动页…
    ///   一次几十字节的 File.WriteAllText 在 SSD 上通常不到 1ms, 但杀软实时扫描/机械盘
    ///   偶发能到几十毫秒, 落在换肤动画的首帧上就是"点一下先卡一下"。
    ///
    /// 做法: 在调用线程上把当前设置**快照**成 JSON(纯内存, 微秒级), 再交给后台线程写。
    /// 写的过程中如果又有变更, 只更新"待写内容", 不重复排任务 —— 连点/连拖时最终只会
    /// 落盘最后一次的值, 磁盘 I/O 次数也从 N 降到 1。
    /// 进程退出前由 App.OnExit 调 Flush 兜住最后一次写。
    /// </summary>
    public void Save()
    {
        var json = BuildJson();
        if (json == null) return;

        lock (_saveGate)
        {
            _pendingJson = json;
            _version++;
            if (_writing) return;
            _writing = true;
        }
        Task.Run(WriteLoop);
    }

    /// <summary>
    /// 同步落盘。给"写完就应该立刻生效、且不在意这点耗时"的场景用 ——
    /// 目前只有"同意免责声明": 那是启动流程里的一道门, 用户点完可能马上就去点关闭键,
    /// 走异步队列万一赶上异常退出就要再被问一遍。
    /// </summary>
    public void SaveNow()
    {
        var json = BuildJson();
        if (json == null) return;
        lock (_saveGate)
        {
            // 让"已经取走旧内容、还没落盘"的后台写失效(见 WriteLoop 的版本校验),
            // 否则那次旧写可能落在这份新内容之后, 把新值覆盖回旧的。
            _version++;
            _pendingJson = null;
        }
        try { File.WriteAllText(FilePath, json); } catch { /* 写入失败不影响使用 */ }
    }

    /// <summary>把当前设置序列化成 JSON(纯内存操作, 微秒级)</summary>
    private string? BuildJson()
    {
        try
        {
            return JsonSerializer.Serialize(new AppSettings
            {
                ThemeMode = ThemeMode,
                DanmakuEnabled = DanmakuEnabled,
                DanmakuAreaPercent = DanmakuAreaPercent,
                DanmakuSmartFilter = DanmakuSmartFilter,
                DanmakuColorful = DanmakuColorful,
                DanmakuBlockKeywords = DanmakuBlockKeywords,
                RecommendSource = RecommendSource,
                AccentColor = AccentColor,
                SponsorBlockEnabled = SponsorBlockEnabled,
                SponsorBlockCategories = SponsorBlockCategories,
                CloseToTray = CloseToTray,
                AutoCheckUpdate = AutoCheckUpdate,
                SkippedVersion = SkippedVersion,
                NoLogin1080P = NoLogin1080P,
                DisclaimerAccepted = DisclaimerAccepted,
                DisclaimerVersion = AcceptedDisclaimerVersion
            }, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    // --- 异步写盘状态 ---
    private readonly object _saveGate = new();
    private string? _pendingJson;
    private bool _writing;

    /// <summary>写入内容的版本号。用于让"已经取走旧内容的后台写"作废(见 WriteLoop)。</summary>
    private int _version;

    /// <summary>后台写循环: 有内容就写, 没有就退出并把"在写"标记还给下一个调用者</summary>
    private void WriteLoop()
    {
        while (true)
        {
            string json;
            int version;
            lock (_saveGate)
            {
                if (_pendingJson == null)
                {
                    _writing = false;
                    return;
                }
                json = _pendingJson;
                version = _version;
                _pendingJson = null;
            }
            try
            {
                // 取内容到真正落盘之间可能又有更新的内容进来(新的 Save, 或 SaveNow 的同步写) ——
                // 那这次写就已经过时, 跳过它, 免得用旧值把新值覆盖回去。
                // 跳过是安全的: 新内容要么还在 _pendingJson 里(循环下一轮会写), 要么已被 SaveNow 写完。
                if (Volatile.Read(ref _version) != version) continue;
                File.WriteAllText(FilePath, json);
            }
            catch
            {
                // 写入失败不影响使用
            }
        }
    }

    /// <summary>
    /// 同步把待写内容落盘(进程退出前调用)。
    /// 退出时后台线程可能刚被排上还没跑, 不 Flush 会丢掉"刚点的同意/刚改的设置"。
    /// </summary>
    public void Flush()
    {
        string? json;
        lock (_saveGate)
        {
            json = _pendingJson;
            _pendingJson = null;
        }
        if (json == null) return;
        try { File.WriteAllText(FilePath, json); } catch { /* 退出阶段忽略 */ }
    }

    /// <summary>
    /// 改"首页推荐用哪套算法"并落盘。
    /// 变了才广播 RecommendSourceChanged —— 重复选中同一项不该触发首页重拉一遍。
    /// </summary>
    public void SetRecommendSource(RecommendSource value)
    {
        if (RecommendSource == value) return;
        RecommendSource = value;
        Save();
        RecommendSourceChanged?.Invoke();
    }

    /// <summary>
    /// 改"跳过赞助片段"的开关与类别并落盘。
    /// 两个值放在同一个方法里: 它们总是被同一个界面动, 分开写迟早出现"改了类别却把开关重置"。
    /// </summary>
    public void SetSponsorBlock(bool enabled, string categories)
    {
        var next = categories ?? "";
        if (SponsorBlockEnabled == enabled &&
            string.Equals(SponsorBlockCategories, next, StringComparison.Ordinal)) return;
        SponsorBlockEnabled = enabled;
        SponsorBlockCategories = next;
        Save();
        SponsorBlockChanged?.Invoke();
    }

    /// <summary>改"关闭时最小化到托盘"并落盘</summary>
    public void SetCloseToTray(bool value)
    {
        if (CloseToTray == value) return;
        CloseToTray = value;
        Save();
    }

    /// <summary>改"自动检查更新"并落盘</summary>
    public void SetAutoCheckUpdate(bool value)
    {
        if (AutoCheckUpdate == value) return;
        AutoCheckUpdate = value;
        Save();
    }

    /// <summary>记下"跳过此版本"并落盘。只对自动检查生效(见 UpdateChecker.AutoCheckAsync)。</summary>
    public void SetSkippedVersion(string version)
    {
        var next = version ?? "";
        if (string.Equals(SkippedVersion, next, StringComparison.Ordinal)) return;
        SkippedVersion = next;
        Save();
    }

    /// <summary>
    /// 改"免登录 1080P"并落盘。
    /// 不需要广播事件: 该开关只在下一次取流时被读取(ApiClient.ApplyTryLook),
    /// 播放中的视频不会因为改它而中断 —— 用户想立刻生效, 换一集/重进即可。
    /// </summary>
    public void SetNoLogin1080P(bool value)
    {
        if (NoLogin1080P == value) return;
        NoLogin1080P = value;
        Save();
    }

    /// <summary>标记已同意免责声明并落盘(记下同意的是哪一版)</summary>
    public void AcceptDisclaimer()
    {
        if (DisclaimerAccepted) return;
        AcceptedDisclaimerVersion = DisclaimerText.Version;
        // 同步落盘: 这是启动流程里的门, 用户点完可能立刻关闭程序, 不能等后台队列
        SaveNow();
    }

    /// <summary>
    /// 弹幕相关设置被修改后调用: 持久化并广播。
    /// 后两个参数是"可选覆盖": 传 null 表示不改这一项 —— 调用点(播放器滑块/开关)大多
    /// 只关心其中一两个值, 让它们把不关心的项显式传一遍很容易漏掉新加的字段
    /// (表现就是"改 A 顺手把 B 重置回默认")。
    /// </summary>
    public void UpdateDanmakuSettings(bool enabled, int areaPercent, bool smartFilter,
        string? blockKeywords = null, bool? colorful = null)
    {
        var nextKeywords = blockKeywords ?? DanmakuBlockKeywords;
        var nextColorful = colorful ?? DanmakuColorful;
        var changed = enabled != DanmakuEnabled
            || areaPercent != DanmakuAreaPercent
            || smartFilter != DanmakuSmartFilter
            || nextColorful != DanmakuColorful
            || !string.Equals(nextKeywords, DanmakuBlockKeywords, StringComparison.Ordinal);
        DanmakuEnabled = enabled;
        DanmakuAreaPercent = Math.Clamp(areaPercent, 25, 100);
        DanmakuSmartFilter = smartFilter;
        DanmakuColorful = nextColorful;
        DanmakuBlockKeywords = nextKeywords;
        Save();
        if (changed) DanmakuSettingsChanged?.Invoke();
    }
}
