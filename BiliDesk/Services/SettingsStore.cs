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

    /// <summary>
    /// 全局亚克力背景(实验性, 默认**关**)。
    ///
    /// 开启后所有窗口改用 Win11 的 Acrylic 系统材质做背景(见 DwmInterop.SetAcrylicBackdrop)。
    /// 默认关的原因: ① 它是实验性观感, 不同机器/显卡上合成效果有差异;
    /// ② 亚克力要实时采样背后的桌面内容, 比纯色背景多一层 GPU 合成开销;
    /// ③ 关掉时一切照旧(纯色背景), 不影响任何既有功能。
    /// 老配置没有这个字段 → 属性初始化值(false)自动生效。
    /// </summary>
    public bool AcrylicBackground { get; set; }

    /// <summary>
    /// 亚克力背景透明度——浅色主题(0~100, 默认 60)。数值越小越透明, 越大越不透明。
    /// 只影响"页面底/侧边栏"这两层的透明度, 不影响卡片/浮层等实色块。
    /// </summary>
    public int AcrylicOpacityLight { get; set; } = 60;

    /// <summary>
    /// 亚克力背景透明度——深色主题(0~100, 默认 75)。深色底本身就更暗, 默认稍不透明一档。
    /// </summary>
    public int AcrylicOpacityDark { get; set; } = 75;

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
    /// 弹幕类型过滤: 只显示勾选的类型(滚动/固定/彩色/高级)。
    ///
    /// ★ 为什么用"四个 bool"而不是一个位标志/枚举串:
    ///   · 界面上就是四个独立开关(截图里的胶囊按钮), 位运算每次都要拆合, 容易写错;
    ///   · 存 JSON 时 bool 可读、老配置缺字段自动拿默认值, 不需要版本兼容代码;
    ///   · 与 DanmakuColorful 那种"单项开关"语义一致 —— 新增一类只要加一个 bool。
    /// ★ 默认全部显示(true): 过滤是"减法"功能, 默认不该动用户能看到的弹幕。
    /// </summary>
    public bool DanmakuFilterScroll { get; set; } = true;

    /// <summary>固定弹幕(顶部/底部)</summary>
    public bool DanmakuFilterFixed { get; set; } = true;

    /// <summary>彩色弹幕(与"彩色弹幕"显示开关不同: 这里是从过滤角度决定"要不要放行")</summary>
    public bool DanmakuFilterColorful { get; set; } = true;

    /// <summary>高级弹幕(mode 7 高级 / 8 代码 / 9 BAS)</summary>
    public bool DanmakuFilterAdvanced { get; set; } = true;

    /// <summary>
    /// 默认画质(qn)。用户在设置页**开始播放前**预先选定的清晰度,
    /// 之后每次点开视频/换集都用它起播; 该档不可用时自动降级(见 ApiClient.GetPlayUrlAsync)。
    /// ★ 默认取 <see cref="QualityPreference.DefaultQn"/>(1080P) —— 与新增本设置项之前
    ///   播放器里写死的 qn=80 一致, 老用户升级后起播画质不变。
    /// 老配置没有这个字段 → 反序列化保持属性初始化值 → 自动落到默认档。
    /// </summary>
    public int PreferredQualityQn { get; set; } = QualityPreference.DefaultQn;

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

    // ----------------- CDN(线路)设置 -----------------
    // 详见 Models/CdnOption.cs 与 Services/CdnService.cs

    /// <summary>线路选择策略(默认自动测速)</summary>
    public CdnSelectMode CdnMode { get; set; } = CdnSelectMode.Auto;

    /// <summary>手动模式下选定的 CDN Id(见 CdnOption.All; 空/失效时自动退回测速)</summary>
    public string CdnManualId { get; set; } = "";

    /// <summary>
    /// 屏蔽 PCDN(点对点分发)。默认**开** —— PCDN 拿其他用户的带宽做节点,
    /// 速度不稳定且对他人有影响; 实测同一档清晰度常规 CDN 吞吐高一倍以上。
    /// </summary>
    public bool BlockPcdn { get; set; } = true;

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

    /// <summary>全局亚克力开关变化。所有已打开的窗口据此重设背景(见 FluentWindow.ApplyChrome)</summary>
    public event Action? AcrylicChanged;

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
                    // 老配置没有这个字段 → 属性初始化值(false)自动生效, 不需要版本兼容。
                    AcrylicBackground = s.AcrylicBackground;
                    AcrylicOpacityLight = Math.Clamp(s.AcrylicOpacityLight, 0, 100);
                    AcrylicOpacityDark = Math.Clamp(s.AcrylicOpacityDark, 0, 100);
                    DanmakuEnabled = s.DanmakuEnabled;
                    DanmakuAreaPercent = Math.Clamp(s.DanmakuAreaPercent, 25, 100);
                    DanmakuSmartFilter = s.DanmakuSmartFilter;
                    // 老配置同样靠"属性初始化值"拿到默认 true
                    DanmakuColorful = s.DanmakuColorful;
                    DanmakuBlockKeywords = s.DanmakuBlockKeywords ?? "";
                    // 弹幕类型过滤: 老配置没有这四个字段 → 属性初始化值(true)自动生效, 不需要版本兼容。
                    DanmakuFilterScroll = s.DanmakuFilterScroll;
                    DanmakuFilterFixed = s.DanmakuFilterFixed;
                    DanmakuFilterColorful = s.DanmakuFilterColorful;
                    DanmakuFilterAdvanced = s.DanmakuFilterAdvanced;
                    // ★ 默认画质要 Normalize: 手改过的配置可能有 0 / 已下线的档位 / 串到别处
                    //   的值, 直接流进取流请求会变成一个谁也不认识的 qn(表现为画质莫名其妙)。
                    PreferredQualityQn = QualityPreference.Normalize(s.PreferredQualityQn);
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
                    // CDN 设置: 同样靠属性初始化值兜底(自动测速 / 空 / 屏蔽 PCDN 打开)。
                    // ★ CdnManualId 要校验: 老配置里的 Id 可能已从候选表下线,
                    //   留着它会让"手动模式"指到一条不存在的线路(见 CdnService.SelectUrlAsync)。
                    CdnMode = s.CdnMode;
                    CdnManualId = CdnOption.ById(s.CdnManualId)?.Id ?? "";
                    BlockPcdn = s.BlockPcdn;
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
                AcrylicBackground = AcrylicBackground,
                AcrylicOpacityLight = AcrylicOpacityLight,
                AcrylicOpacityDark = AcrylicOpacityDark,
                DanmakuEnabled = DanmakuEnabled,
                DanmakuAreaPercent = DanmakuAreaPercent,
                DanmakuSmartFilter = DanmakuSmartFilter,
                DanmakuColorful = DanmakuColorful,
                DanmakuBlockKeywords = DanmakuBlockKeywords,
                DanmakuFilterScroll = DanmakuFilterScroll,
                DanmakuFilterFixed = DanmakuFilterFixed,
                DanmakuFilterColorful = DanmakuFilterColorful,
                DanmakuFilterAdvanced = DanmakuFilterAdvanced,
                PreferredQualityQn = PreferredQualityQn,
                RecommendSource = RecommendSource,
                AccentColor = AccentColor,
                SponsorBlockEnabled = SponsorBlockEnabled,
                SponsorBlockCategories = SponsorBlockCategories,
                CloseToTray = CloseToTray,
                AutoCheckUpdate = AutoCheckUpdate,
                SkippedVersion = SkippedVersion,
                NoLogin1080P = NoLogin1080P,
                CdnMode = CdnMode,
                CdnManualId = CdnManualId,
                BlockPcdn = BlockPcdn,
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

    /// <summary>
    /// 改"默认画质"并落盘。
    ///
    /// ★ 不需要广播事件: 它只在**取流那一刻**被读(播放器 ResetForNewMedia 快照给 _currentQn,
    ///   以及 ApiClient.GetPlayUrlAsync 的降级判定), 播放中的视频不会因为改它而中断 ——
    ///   要立刻生效, 换一集/重进即可。这与"免登录 1080P"同款(见 SetNoLogin1080P)。
    ///
    /// 非法档位(Normalize 之后与现值相同)直接 return, 不写盘: 下拉框回写时可能把一个
    /// 表外的值送进来, 落盘一个表外值会让下次启动的 Normalize 白跑一趟。
    /// </summary>
    public void SetPreferredQuality(int qn)
    {
        var next = QualityPreference.Normalize(qn);
        if (PreferredQualityQn == next) return;
        PreferredQualityQn = next;
        Save();
    }

    /// <summary>
    /// 开关"全局亚克力"背景并落盘, 广播给所有已打开的窗口。
    /// 与主题色那类"纯资源替换"不同: 它要动窗口的原生合成属性(DWM), 必须让每个窗口
    /// 自己重设一次, 所以走事件而不是只改资源字典。
    /// </summary>
    public void SetAcrylic(bool on)
    {
        if (AcrylicBackground == on) return;
        AcrylicBackground = on;
        Save();
        AcrylicChanged?.Invoke();
    }

    /// <summary>调节浅色主题亚克力透明度(0~100)并落盘, 广播给所有窗口重设背景</summary>
    public void SetAcrylicOpacityLight(int opacity)
    {
        opacity = Math.Clamp(opacity, 0, 100);
        if (AcrylicOpacityLight == opacity) return;
        AcrylicOpacityLight = opacity;
        Save();
        AcrylicChanged?.Invoke();
    }

    /// <summary>调节深色主题亚克力透明度(0~100)并落盘, 广播给所有窗口重设背景</summary>
    public void SetAcrylicOpacityDark(int opacity)
    {
        opacity = Math.Clamp(opacity, 0, 100);
        if (AcrylicOpacityDark == opacity) return;
        AcrylicOpacityDark = opacity;
        Save();
        AcrylicChanged?.Invoke();
    }


    /// <summary>
    /// 改 CDN 线路设置并落盘。
    /// 三个值放在同一个方法里: 它们总是被同一张设置卡片一起改,
    /// 分开写迟早出现"改了模式却把手动选的那家重置掉"。
    ///
    /// ★ 只要这三项里有任何一项变了, 就必须让测速缓存失效 ——
    ///   否则用户从"自动"切到"手动指定腾讯云"后会仍然用着缓存里的阿里云,
    ///   表现是"改了设置根本没生效"。
    /// </summary>
    public void SetCdn(CdnSelectMode mode, string manualId, bool blockPcdn)
    {
        var nextId = CdnOption.ById(manualId)?.Id ?? "";
        if (CdnMode == mode &&
            string.Equals(CdnManualId, nextId, StringComparison.Ordinal) &&
            BlockPcdn == blockPcdn) return;

        CdnMode = mode;
        CdnManualId = nextId;
        BlockPcdn = blockPcdn;
        Save();
        CdnService.Instance.InvalidateCache();
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
        string? blockKeywords = null, bool? colorful = null,
        bool? filterScroll = null, bool? filterFixed = null,
        bool? filterColorful = null, bool? filterAdvanced = null)
    {
        var nextKeywords = blockKeywords ?? DanmakuBlockKeywords;
        var nextColorful = colorful ?? DanmakuColorful;
        var nextFScroll = filterScroll ?? DanmakuFilterScroll;
        var nextFFixed = filterFixed ?? DanmakuFilterFixed;
        var nextFColorful = filterColorful ?? DanmakuFilterColorful;
        var nextFAdvanced = filterAdvanced ?? DanmakuFilterAdvanced;
        var changed = enabled != DanmakuEnabled
            || areaPercent != DanmakuAreaPercent
            || smartFilter != DanmakuSmartFilter
            || nextColorful != DanmakuColorful
            || nextFScroll != DanmakuFilterScroll
            || nextFFixed != DanmakuFilterFixed
            || nextFColorful != DanmakuFilterColorful
            || nextFAdvanced != DanmakuFilterAdvanced
            || !string.Equals(nextKeywords, DanmakuBlockKeywords, StringComparison.Ordinal);
        DanmakuEnabled = enabled;
        DanmakuAreaPercent = Math.Clamp(areaPercent, 25, 100);
        DanmakuSmartFilter = smartFilter;
        DanmakuColorful = nextColorful;
        DanmakuFilterScroll = nextFScroll;
        DanmakuFilterFixed = nextFFixed;
        DanmakuFilterColorful = nextFColorful;
        DanmakuFilterAdvanced = nextFAdvanced;
        DanmakuBlockKeywords = nextKeywords;
        Save();
        if (changed) DanmakuSettingsChanged?.Invoke();
    }
}
