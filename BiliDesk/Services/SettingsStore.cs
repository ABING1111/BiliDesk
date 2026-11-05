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
    /// 窗口背景材质的**可选定值**(设置页那四个胶囊; 顺序就是界面顺序)。
    /// ★ 把"无"放在第一个: 它等价于以前的"亚克力开关关掉", 用户一眼能找到关闭项。
    /// </summary>
    public static readonly BackdropMaterial[] MaterialChoices =
    {
        BackdropMaterial.None, BackdropMaterial.Acrylic, BackdropMaterial.Mica, BackdropMaterial.MicaAlt
    };

    /// <summary>
    /// 当前背景材质(2026-10-06 起是**唯一**的开关+变体选择, 四种平级)。
    /// ★ 是否启用材质 = <c>AcrylicMaterial != None</c>。
    /// </summary>
    public BackdropMaterial AcrylicMaterial { get; set; } = BackdropMaterial.None;

    /// <summary>
    /// 是否启用了背景材质(由材质派生, 不再单独存储)。
    /// ★ 保留它是因为有多处旧调用点读这个语义; 一律由 <see cref="AcrylicMaterial"/> 算出来,
    ///   保证两者**不可能**不一致(以前是两个独立字段, 迟早对不上)。
    /// </summary>
    public bool AcrylicBackground => AcrylicMaterial != BackdropMaterial.None;

    /// <summary>
    /// 亚克力透明度——浅色主题的**可调范围**(用户 2026-10-06 定: 0~50)。
    ///
    /// ★ 语义: 这就是**页面底的不透明度** —— 0% 等于页面底全透(桌面直接透出来, 文字压在壁纸上),
    ///   50% 等于一半实色。数值越小越透、亚克力感越强。
    /// ★ 为什么两端不做成 0~100: 上限 100% 时页面底完全不透明, 亚克力一点都看不见,
    ///   等于白开了这个开关; 所以上限收到 50, 保证"在任何可调位置都看得出亚克力"。
    /// ★★★ **下限 0 是这个设计的核心**(2026-10-17 回归教训, 别再改):
    ///   "亚克力"这个材质的**全部意义就是半透明**。上一轮我为了修"发脏",
    ///   在 ApplyAcrylicSurfaces 里给亚克力加了一个"不透明度下限 0.70" ——
    ///   结果亚克力直接变成不透明材质, 用户当场反馈"亚克力现在不透明了"。
    ///   ⇒ **绝对不要**在这里面再加任何下限/上限/映射把用户的选择往中间拉:
    ///     滑杆给多少, 页面底就该是多少(双叠由 ThemeService.ToOverlayAlpha 反解)。
    ///     "发闷/显脏"要用**颜色**解决(把灰换成白), 不能用"提高不透明度"解决。
    /// ★ 范围只写在这里一处: Load 的兜底、setter 的夹取、设置页滑杆的上下限、默认值
    ///   **全部**引用它, 免得以后有人只改一处(会出现"滑杆能拉但存不进去", 或默认值落在范围外
    ///   导致滑杆被顶死在端点)。
    /// </summary>
    public const int AcrylicOpacityLightMin = 0;
    /// <inheritdoc cref="AcrylicOpacityLightMin"/>
    public const int AcrylicOpacityLightMax = 50;

    /// <summary>
    /// 亚克力透明度——深色主题的可调范围(30~70)。整体比浅色高一档:
    /// 深色底本来就暗, 压太低会和壁纸糊在一起、分不出层次。
    /// </summary>
    public const int AcrylicOpacityDarkMin = 30;
    /// <inheritdoc cref="AcrylicOpacityDarkMin"/>
    public const int AcrylicOpacityDarkMax = 70;

    /// <summary>
    /// 亚克力背景透明度——浅色主题(默认 30, 可调 0~50)。数值越小越透明, 越大越不透明。
    /// 只影响"页面底/侧边栏"这两层的透明度, 不影响卡片/浮层等实色块。
    /// ★ 默认 30 是历史沿用值: 亚克力这套最早写死的就是 0x4D(≈30%), 观感验证过。
    /// </summary>
    public int AcrylicOpacityLight { get; set; } = 30;

    /// <summary>
    /// 亚克力背景透明度——深色主题(默认 50, 可调 30~70)。深色主题取区间中值:
    /// 深色底不透明度给太低会整片发糊, 给太高又会盖掉壁纸的层次。
    /// </summary>
    public int AcrylicOpacityDark { get; set; } = 50;

    /// <summary>
    /// 卡片页每行显示几列(0 = 自动, 按窗口宽度算)。
    ///
    /// ★ 为什么要给用户定死列数的能力: 自动列数是"按宽度尽量塞", 在宽屏上会一直加到 6~8 列、
    ///   每张卡很小; 用户可能就想固定 4 列。所以给一个"我就要 N 列"的选项(0 保持原有自动行为)。
    /// ★ 范围与夹取见 <see cref="Helpers.CardWall.FixedColumnsMin"/> —— 只写在那里一处。
    /// </summary>
    public int CardColumns { get; set; }

    /// <summary>
    /// 「动态」页右栏的排版(2026-10-15 新增): false = 动态卡(单列, 头像+发布时间+视频),
    /// true = 卡片墙(多列网格)。默认动态卡, 见 AppSettings 的同名属性。
    ///
    /// ★ 这是**用户偏好**: 点一次切换就记住, 重启/切页回来还是他选的那档 ——
    ///   与"卡片列数"同一类(改了立刻落盘, 不跟着片源/页面走)。
    /// </summary>
    public bool FollowCardWall { get; set; }

    /// <summary>
    /// 软件缩放百分比(默认 100 = 不缩放)。范围见 <see cref="Helpers.UiScale.MinPercent"/>。
    ///
    /// ★ 与"系统 DPI 缩放"是两件事: 那个由 Windows 管、作用于所有程序;
    ///   这个是本程序自己按百分比缩放界面(给"系统 125% 但还想再大一点"这类需求用)。
    /// </summary>
    public int UiScalePercent { get; set; } = 100;

    /// <summary>
    /// **自定义下载/缓存位置**(空串 = 用默认的系统「下载」文件夹)。
    /// 下载的视频与"图片另存为"都存到这里(见 <see cref="AppPaths.DownloadDir"/>)。
    /// ★ 2026-10-11 由 <c>CacheRoot</c> 改名而来, 语义从"内部缓存根"改成"下载目录"
    ///   —— 用户理解的"缓存位置"就是"下载的视频存哪"(见 AppPaths.DownloadDir 的说明)。
    /// ★ 内部缓存(imgcache/vcache)不再暴露, 固定在 <see cref="AppPaths.DataDir"/>。
    /// ★ 读取时一律过 <see cref="AppPaths.NormalizeDir"/> 校验: 目录不存在/非法路径
    ///   就让下载回落默认目录, 而不是把脏路径带到下游去抛异常。
    /// </summary>
    public string DownloadDir { get; set; } = "";

    /// <summary>弹幕开关(全局)</summary>
    public bool DanmakuEnabled { get; set; } = true;

    /// <summary>弹幕显示区域占比(25~100)</summary>
    public int DanmakuAreaPercent { get; set; } = 25;

    /// <summary>弹幕智能屏蔽</summary>
    public bool DanmakuSmartFilter { get; set; } = true;

    /// <summary>彩色弹幕(关掉则统一白色)</summary>
    public bool DanmakuColorful { get; set; } = true;

    /// <summary>
    /// 弹幕关键词屏蔽 —— **2026-11-05 已删除**。
    ///
    /// ★ 字段本身**保留在存储层**(不再读取/写入)是为了老配置不报错: settings.json 里
    ///   还留着这条, 反序列化时多一个字段不会出错, 直接忽略即可。
    ///   真正删掉的是"生效链路"(设置项 + 播放器过滤分支 + 解析器)。
    /// </summary>

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

    /// <summary>弹幕不透明度(20~100)。★ 缺这条时白字压在亮画面上完全看不见</summary>
    public int DanmakuOpacityPercent { get; set; } = 80;

    /// <summary>弹幕字号(缩放百分比, 50~300)。★ 与泳道高联动, 见 DanmakuLaneRatio</summary>
    public int DanmakuFontScalePercent { get; set; } = 100;

    /// <summary>
    /// 弹幕速度档位(0 最慢 ~ 4 最快)。
    ///
    /// ★ 为什么用**整数档位**而不是"倍数/百分比":
    ///   B 站客户端也是档位(慢/较快/适中/快/极快), 而弹幕的横穿时长直接决定
    ///   同一条泳道能不能塞下两条(见 AllocateRollingLane 的追尾校验)。
    ///   存成百分比看着更"精确", 但换算出来的秒数会出现 7.7s 这种值,
    ///   滑杆每动一格观感几乎不变, 用户只会觉得"滑块失灵"。
    ///   整数档 → 一一对应的秒数, 改动幅度看得见。
    /// 升序: 档位越大越快。
    /// </summary>
    public int DanmakuSpeedLevel { get; set; } = 2;

    /// <summary>
    /// 弹幕是否带**半透明圆角胶囊底衬**(2026-11-05 新增)。
    ///
    /// ★ 背景: 2026-10-07 用户要求去掉弹幕的长方形底衬(纯文字更清爽), 底衬被整个删掉。
    ///   但纯白字压在明亮画面上会看不清 —— 这次把它作为**可选项**加回来, 默认开。
    /// ★ 与 AppSettings.DanmakuPillBackground 是**读写契约**, 两处必须一致。
    /// ★ 老配置缺这个字段 → 反序列化保持属性初始化值 → 自动落到默认(开), 不需要版本兼容。
    /// </summary>
    public bool DanmakuPillBackground { get; set; } = true;

    // ------------------------------------------------------------ 字幕(播放器)

    /// <summary>
    /// 字幕字号(缩放百分比, 50~300, 默认 200)。作用于播放器字幕层。
    /// ★ 默认从 100 上调到 200: 基准字号 SubtitleBaseFontSize 只有 20,
    ///   在 1080P / 全屏下 100% 的字幕比 B 站网页端明显小一圈, 200% 才是网页端的观感。
    /// </summary>
    public int SubtitleFontScalePercent { get; set; } = 200;

    /// <summary>
    /// 默认显示字幕。关掉后播放器取到人工字幕轨也不自动开(按钮仍是可点的入口), 适合不需要字幕的人。
    /// ★ 「默认显示字幕」开关的持久化字段。
    /// ★ 只作用于**作者自制的外挂字幕**(SubtitleTrack.IsAi == false)。AI 字幕不走这个开关 ——
    ///   AI 字幕默认不自动打开, 由用户自己在字幕弹层里选(见 LoadSubtitleAsync)。
    /// ★ 产品决策(2026-11-08 确认): 默认值保持 true —— 与 B 站 web 端一致, web 端取到字幕
    ///   且存在主轨时本来就自动开(无主轨才关), 这里对齐同一行为, 不做成默认关。
    /// </summary>
    public bool SubtitleEnabled { get; set; } = true;

    /// <summary>字幕底衬不透明度(0~100, 默认 45)。0 = 完全透明(只剩字 + 描边)。</summary>
    public int SubtitleBackgroundOpacityPercent { get; set; } = 45;

    /// <summary>
    /// 字幕距离画面底部的距离(px, 0~300, 默认 70)。
    /// ★ 原默认 24 会让字幕正好落在控制栏底下, 先后调到 92 再回到 70:
    ///   70 仍在安全区内, 控制栏展开时字幕基本压在它上方, 收起时又不会飘得太高。
    /// ★ 与 PlayerWindow.xaml 里 SubtitleBox 的 Margin 初值必须一致(同样是 70),
    ///   否则「设置里是默认值」和「刚从 XAML 建出来的样子」会跳一下。
    /// </summary>
    public int SubtitleBottomMargin { get; set; } = 70;

    /// <summary>
    /// 字幕默认值的一次性迁移标记: 旧配置(字号 100 / 底距 24)升级后迁到新默认(200 / 70)。
    /// 迁移完成即置 true 并落盘, 之后用户手动改回旧值不会被再改。见 Load()。
    /// </summary>
    private bool _subtitleDefaultsMigrated;

    /// <summary>旧版字幕字号默认值(迁移判据: 只搬"仍等于旧默认"的配置)</summary>
    private const int LegacySubtitleFontScalePercent = 100;

    /// <summary>旧版字幕底距默认值(px)</summary>
    private const int LegacySubtitleBottomMargin = 24;

    // ------------------------------------------------------------ 列表屏蔽(FilterService)

    /// <summary>按标题屏蔽的关键词(包含匹配, 空格/逗号分隔)。与 AppSettings 同名属性是读写契约。</summary>
    public string FilterTitleKeyword { get; set; } = "";

    /// <summary>按 UP 主屏蔽的关键词(包含匹配)。与 AppSettings 同名属性是读写契约。</summary>
    public string FilterUpKeyword { get; set; } = "";

    /// <summary>一键屏蔽的 UP 主(精确同名 + UID)。与 AppSettings 同名属性是读写契约。</summary>
    public List<BlockedUpEntry> FilterBlockedUps { get; set; } = new();

    /// <summary>一键屏蔽的标题(完全相同才命中)。与 AppSettings 同名属性是读写契约。</summary>
    public List<string> FilterBlockedTitles { get; set; } = new();

    /// <summary>
    /// 默认画质(qn)。用户在设置页**开始播放前**预先选定的清晰度,
    /// 之后每次点开视频/换集都用它起播; 该档不可用时自动降级(见 ApiClient.GetPlayUrlAsync)。
    /// ★ 默认取 <see cref="QualityPreference.DefaultQn"/>(1080P) —— 与新增本设置项之前
    ///   播放器里写死的 qn=80 一致, 老用户升级后起播画质不变。
    /// 老配置没有这个字段 → 反序列化保持属性初始化值 → 自动落到默认档。
    /// </summary>
    public int PreferredQualityQn { get; set; } = QualityPreference.DefaultQn;

    /// <summary>
    /// 首页「推荐」用哪套算法。★ 2026-10-03 起**固定为网页版**, 该字段仅为兼容老配置保留 ——
    /// 读取时丢弃、写入时固定写 Web, 代码里没有任何地方按它分支。
    ///
    /// 历史: 这里原来是"App 官方 / 浏览器网页版"二选一, 默认 App。
    ///   但实测 App 端接口(app.bilibili.com/x/v2/feed/index)对第三方客户端**不提供个性化** ——
    ///   即使拿到有效 access_key, 返回的仍是全站通用热门池(100 条里命中关注的 UP 只有 1~2 个,
    ///   平均播放量 66~103 万); 网页 rcmd 平均只有 17 万、内容明显更垂直。
    ///   所以选择项与 App 令牌机制整体删除(原因详见 ApiClient.GetRecommendAsync)。
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

    /// <summary>线路选择策略(默认跟随服务端, 2026-11-05 起)</summary>
    public CdnSelectMode CdnMode { get; set; } = CdnSelectMode.ServerDefault;

    /// <summary>
    /// 线路默认值的一次性迁移标记: 旧配置(全是 Auto)升级后迁到 ServerDefault;
    /// 迁移完成即置 true 并落盘, 之后用户手动选 Auto 不会再被改。见 Models.AppSettings。
    /// </summary>
    private bool _cdnModeMigratedToServer;

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
    /// 播放器音量(0~100), 跨视频继承 —— 调一次, 之后打开的每个视频都是这个音量。
    ///
    /// ★ 为什么不做"关窗即还原成 80": 用户的音量偏好是**长期**的(有人常年 30%, 有人常年
    ///   满格), 每个视频都要重调一遍纯属折腾。0 也照样存 —— 那是用户主动静音的,
    ///   下次打开还该是静音(与"静音键"的语义一致)。
    /// ★ 与 <see cref="Models.AppSettings.PlayerVolume"/> 是读写契约, 两处必须一致。
    /// </summary>
    public int PlayerVolume { get; set; } = 80;

    /// <summary>
    /// 是否记住播放器窗口大小(默认开)。
    ///
    /// ★ 关掉后: 播放器每次打开都用 XAML 里的默认 1280×760, 也不再写盘 ——
    ///   给"不想让窗口尺寸被上次操作影响"的用户一个干净入口(多显示器/演示场景常见)。
    /// ★ 只记**宽高**, 不记位置与最大化/全屏状态: 用户要的是"窗口大小", 位置交给
    ///   WindowStartupLocation=CenterScreen; 全屏是临时态, 不该带到下一次启动。
    /// </summary>
    public bool RememberPlayerWindowSize { get; set; } = true;

    /// <summary>上一次播放器窗口的宽度(DIP; 0 = 还没记过, 用 XAML 默认值)</summary>
    public int PlayerWindowWidth { get; set; }

    /// <summary>上一次播放器窗口的高度(DIP; 0 = 还没记过, 用 XAML 默认值)</summary>
    public int PlayerWindowHeight { get; set; }

    /// <summary>播放器窗口记忆尺寸的合法下限(与 PlayerWindow 的 FillWindowMin 一致, 防止存入不可用的小窗)</summary>
    public const int PlayerWindowMinWidth = 320;

    /// <inheritdoc cref="PlayerWindowMinWidth"/>
    public const int PlayerWindowMinHeight = 200;

    /// <summary>播放器窗口记忆尺寸的上限: 手改配置可能塞进离谱值, 挡住避免下次开到屏幕外</summary>
    public const int PlayerWindowMaxWidth = 10000;

    /// <inheritdoc cref="PlayerWindowMaxWidth"/>
    public const int PlayerWindowMaxHeight = 10000;

    /// <summary>
    /// LibVLC 的网络缓冲(毫秒)。
    ///
    /// ★ 2026-10-30 实测(探针 bd-probe-startcfix, 同一路 DASH):
    ///   `:network-caching=300` 与 `3000` 的"时间开始推进"分别是 1.07s / 0.96s ——
    ///   在**起播阶段几乎测不出差别**(缓存是按网络速度灌的, 不是按播放速度等)。
    ///   它真正影响的是**播放中的抗抖动余量**: 值越小越省内存/延迟低, 越大越能扛网络抖动。
    ///   所以默认保持旧的 1000 不动(不拿卡顿风险换一个测不出来的收益), 只把它开放给用户调。
    /// ★ 只对网络流生效; 按媒体加 `:network-caching=`, 改完下一条视频生效,
    ///   不需要重建 LibVLC 实例(全局值见 VlcCore.Get, 作为没有媒体选项时的兜底)。
    /// ★ 越界值(手改配置)由 <see cref="SettingsStore"/> 读取时夹回范围内。
    /// </summary>
    public int NetworkCachingMs { get; set; } = 1000;

    /// <summary>网络缓冲可选下限: 再低在丢包链路上会频繁卡顿</summary>
    public const int NetworkCachingMinMs = 300;

    /// <summary>网络缓冲可选上限: 弱网兜底档</summary>
    public const int NetworkCachingMaxMs = 3000;

    /// <summary>网络缓冲的默认值(与 1.3.2 及之前的全局值一致)</summary>
    public const int NetworkCachingDefaultMs = 1000;

    /// <summary>
    /// 缓冲策略(2026-11-01 新增): 自动按带宽/码率挑缓冲值, 还是手动用固定值。
    ///
    /// ★ 默认**自动**: 决策依据见 Services/BufferPolicy.cs —— 实测(探针 bd-probe-startcfix)
    ///   缓冲值对起播几乎无影响, 真正影响的是播放中的抗抖动余量, 而"该留多少余量"
    ///   恰恰取决于网络状况: 固定 1000ms 在快线上浪费内存与延迟, 在慢线上又不够扛抖动。
    ///   最坏情况(测速失败/没测过)自动档回落 1000, 与旧默认一致 —— 风险面极小。
    ///   想要完全旧行为的用户切到"手动"即可(那档恒用 NetworkCachingMs)。
    /// </summary>
    public bool AutoBufferPolicy { get; set; } = true;

    /// <summary>设置缓冲策略并落盘</summary>
    public void SetAutoBufferPolicy(bool auto)
    {
        if (AutoBufferPolicy == auto) return;
        AutoBufferPolicy = auto;
        Save();
    }

    /// <summary>
    /// 上次用的搜索类别(见 <see cref="Models.ContentKind"/>: 0=视频 1=直播间 2=番剧)。
    ///
    /// ★ 为什么要记住它(2026-11-12 用户报"番剧依然无法被搜索"):
    ///   从首页搜索框发起搜索时, 搜索页用的是**默认类别(视频)** —— 于是搜"咒术回战"
    ///   出来的是视频, 一条番剧都没有, 用户看到的就是"番剧搜不到"。
    ///   而切到「番剧」tab 后同一关键词立刻能搜到(实测 4 条), 所以真正缺的是
    ///   "记住用户上次停在哪个类别", 让他不必每次手动切。
    /// ★ 与 <see cref="Models.AppSettings.LastSearchKind"/> 是读写契约, 两处必须一致。
    /// </summary>
    public ContentKind SearchKind { get; set; } = ContentKind.Video;

    /// <summary>设置搜索类别并落盘(越界值回落视频)</summary>
    public void SetSearchKind(ContentKind kind)
    {
        if (!Enum.IsDefined(typeof(ContentKind), kind)) kind = ContentKind.Video;
        if (SearchKind == kind) return;
        SearchKind = kind;
        Save();
    }

    /// <summary>
    /// 设置播放器音量并落盘。0~100 之外的值被夹回范围内(老配置可能存着越界值)。
    /// </summary>
    public void SetPlayerVolume(int volume)
    {
        var v = Math.Clamp(volume, 0, 100);
        if (PlayerVolume == v) return;
        PlayerVolume = v;
        Save();
    }

    /// <summary>窗口尺寸是否落在可接受的闭区间内(0 表示"尚未记录", 由调用方单独处理)</summary>
    private static bool IsValidPlayerWindowSize(int value, int min, int max)
        => value >= min && value <= max;

    /// <summary>
    /// 改"是否记住播放器窗口大小"并落盘。
    /// 关掉时保留已记录的值(下次再开还能沿用), 只是不再读取/写入 ——
    /// 用户重新打开开关不会突然回到默认尺寸。
    /// </summary>
    public void SetRememberPlayerWindowSize(bool value)
    {
        if (RememberPlayerWindowSize == value) return;
        RememberPlayerWindowSize = value;
        Save();
    }

    /// <summary>
    /// 记录播放器窗口大小并落盘(只在"记住窗口大小"开着时生效)。
    /// ★ 调用方传的是 DIP, 这里四舍五入成整数: JSON 里不要出现 1279.999999 这种噪声。
    /// ★ 越界/异常值直接忽略, 不写盘 —— 宁可沿用上一次的好值, 也不要把坏值存进去。
    /// </summary>
    public void SetPlayerWindowSize(double width, double height)
    {
        if (!RememberPlayerWindowSize) return;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;

        var w = (int)Math.Round(width);
        var h = (int)Math.Round(height);
        if (!IsValidPlayerWindowSize(w, PlayerWindowMinWidth, PlayerWindowMaxWidth)) return;
        if (!IsValidPlayerWindowSize(h, PlayerWindowMinHeight, PlayerWindowMaxHeight)) return;
        if (PlayerWindowWidth == w && PlayerWindowHeight == h) return;

        PlayerWindowWidth = w;
        PlayerWindowHeight = h;
        Save();
    }

    /// <summary>
    /// 设置网络缓冲并落盘。夹回 300~3000 —— 手改配置可能塞进 0 或十万,
    /// 前者等于关掉缓冲(必卡), 后者白占内存。
    /// </summary>
    public void SetNetworkCaching(int ms)
    {
        var v = ms <= 0
            ? NetworkCachingDefaultMs
            : Math.Clamp(ms, NetworkCachingMinMs, NetworkCachingMaxMs);
        if (NetworkCachingMs == v) return;
        NetworkCachingMs = v;
        Save();
    }

    /// <summary>
    /// 是否已同意**当前版本**的免责声明。
    /// 算出来而不是存一个 bool: 声明正文一改, Version 一变, 这里自动变回 false,
    /// 用户会被再问一次 —— 不会出现"点了同意却看到的是另一份文本"。
    /// </summary>
    public bool DisclaimerAccepted => AcceptedDisclaimerVersion >= DisclaimerText.Version;

    /// <summary>设置变更通知(播放器等运行中的界面据此同步)</summary>
    public event Action? DanmakuSettingsChanged;

    /// <summary>
    /// 字幕设置变更通知: 设置页改字号/底衬/底距时, 正放着片的播放器要立刻跟上。
    /// ★ 必须单独发一个事件: 暂停时 TimeChanged 不跑, 没有广播就要等下一次播放才生效。
    /// ★ 与 DanmakuSettingsChanged 并列而不是合并: 弹幕一变要重建泳道+重筛, 字幕只需重落一次外观。
    /// </summary>
    public event Action? SubtitleSettingsChanged;

    /// <summary>全局亚克力开关变化。所有已打开的窗口据此重设背景(见 FluentWindow.ApplyChrome)</summary>
    public event Action? AcrylicChanged;

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
                if (s != null) ApplySanitized(s);
            }
        }
        catch
        {
            ThemeMode = AppTheme.System;
        }

        // ★ 列数要在 Load 的**最后**同步给 CardWall: 它是个全局静态属性, 卡片墙读的是它而不是设置对象。
        //   漏了这一步的表现是"设置里存着 4 列, 重启后却按宽度自动排" —— 改了像没改。
        Helpers.CardWall.SetFixedColumns(CardColumns);
        // 同理: 缩放倍率也是静态的, 启动时要先落到 UiScale 上, 窗口创建时才会按它施加变换。
        Helpers.UiScale.SetPercent(UiScalePercent);
        // ★ 同理(第三处): 下载目录也是静态的(AppPaths.DownloadDir)。漏了这一步的表现是
        //   "设置里明明存着 D:\视频, 重启后下载还是写回 C:\Users\...\Downloads" ——
        //   与列数/缩放是同一类错, 所以三处都放在 Load 的末尾一起同步。
        //   ★ 下载目录**不做文件迁移**: 已下载的视频是用户成品, 不是可再生缓存。
        AppPaths.SetDownloadDir(DownloadDir);
        // 内部缓存根目录固定回数据目录(2026-10-11 起不再暴露给用户, 见 AppPaths.CacheRoot)。
        AppPaths.ResetCacheRoot();
    }

    /// <summary>
    /// 把一份(多半来自磁盘的)配置**规范化**后灌进本实例。
    ///
    /// ★ 这里是"读配置"与"写配置"之间**唯一的契约**: 写出去用 <see cref="Snapshot"/>,
    ///   读进来用本方法, 两者的字段清单必须一一对应。以前 Load 与 BuildJson 各手写一遍
    ///   ~30 个字段, 加一个设置项就得记得改两处 —— 漏一处就是"改了存得下、重启却读不回来"。
    ///
    /// ★ 为什么需要"规范化"而不是直接赋值: 磁盘上的 JSON 是**用户可改**的, 也可能是
    ///   更老/更新版本写下的。越界的数字(0% 透明度、0 倍缩放)、已下线的枚举 Id、
    ///   坏掉的转义都会在下游变成难以定位的现象。这里一次性收口。
    /// </summary>
    private void ApplySanitized(AppSettings s)
    {
        ThemeMode = s.ThemeMode;

        // ★★★ 材质迁移(2026-10-06: 旧的"亚克力开关 + 材质变体"合并成一个选择器):
        //   · 新配置带 AcrylicMaterial(可空) ⇒ 校验合法性后直接用;
        //   · **老配置没有这个字段**(null) ⇒ 按旧 boolean 推导:
        //       旧 AcrylicBackground=true  → 旧的材质变体(默认 Acrylic)
        //       旧 AcrylicBackground=false → None(保持"关着")
        //   ★ 少了这条迁移, 老用户会出现两种错: 开着的被关掉, 或没开的被打开。
        AcrylicMaterial = s.AcrylicMaterial is { } m && Enum.IsDefined(typeof(BackdropMaterial), m)
            ? m
            : (s.AcrylicBackground ? BackdropMaterial.Acrylic : BackdropMaterial.None);

        // ★ 必须按**当前范围**夹取: 老配置里可能存在范围外的值(用户实测存着 0%),
        //   不夹的话滑杆会显示成贴着左端、而实际透明度比下限还透 —— 观感与数值对不上。
        AcrylicOpacityLight = Math.Clamp(s.AcrylicOpacityLight, AcrylicOpacityLightMin, AcrylicOpacityLightMax);
        AcrylicOpacityDark = Math.Clamp(s.AcrylicOpacityDark, AcrylicOpacityDarkMin, AcrylicOpacityDarkMax);

        // ★ 列数要夹取: 老配置没有(默认 0=自动), 手改过的可能有越界值。
        //   0 要保留(它的语义是"自动"), 所以只在 >0 时夹。
        CardColumns = s.CardColumns <= 0
            ? 0
            : Math.Clamp(s.CardColumns, Helpers.CardWall.FixedColumnsMin, Helpers.CardWall.FixedColumnsMax);

        // ★ 缩放也要夹取: 老配置没这个字段(默认 100), 手改过的可能有 0/负数/超大值。
        //   0 会让整个界面缩成不可见的点, 必须挡住。
        UiScalePercent = Helpers.UiScale.ClampPercent(s.UiScalePercent <= 0 ? 100 : s.UiScalePercent);

        // 动态页排版: 老配置没这个字段 ⇒ 默认 false(动态卡)。bool 没有越界值, 不需要夹取。
        FollowCardWall = s.FollowCardWall;

        // ★ 下载位置: 老配置没这个字段(默认空串 = 用系统下载文件夹)。
        //   非法/不存在的路径**不写进设置**(回落空串), 免得把脏路径带到 Path.Combine 下游 ——
        //   那种错的表现是"下载一直失败", 很难定位到是这行造成的。
        //   ★ 老配置里的旧字段 CacheRoot **故意不迁移**: 它的值指向用户数据目录下的
        //     imgcache/vcache, 与"下载目录"语义完全不同; 拿来当下载目录会让离线缓存页
        //     突然一个文件都没有(用户以为视频被删了)。宁可让用户重新设一次。
        DownloadDir = AppPaths.NormalizeDir(s.DownloadDir) ?? "";

        // 弹幕各项: 老配置缺字段时靠属性初始化值拿到默认(true / 25), 不需要版本兼容。
        DanmakuEnabled = s.DanmakuEnabled;
        DanmakuAreaPercent = Math.Clamp(s.DanmakuAreaPercent, 25, 100);
        DanmakuSmartFilter = s.DanmakuSmartFilter;
        DanmakuColorful = s.DanmakuColorful;
        DanmakuFilterScroll = s.DanmakuFilterScroll;
        DanmakuFilterFixed = s.DanmakuFilterFixed;
        DanmakuFilterColorful = s.DanmakuFilterColorful;
        DanmakuFilterAdvanced = s.DanmakuFilterAdvanced;

        // ★ 新增的弹幕外观三项(2026-10-12): 老配置没有这些字段, 会拿到属性初始化值
        //   (80 / 100 / 2), 正好就是设计默认值, 不需要版本兼容。
        //   仍要夹取: 手改过的配置可能有越界值(0 / 999), 不夹的话滑杆会贴边而观感与数值对不上。
        DanmakuOpacityPercent = Math.Clamp(s.DanmakuOpacityPercent, 0, 100);
        DanmakuFontScalePercent = Math.Clamp(s.DanmakuFontScalePercent, 50, 300);
        DanmakuSpeedLevel = Math.Clamp(s.DanmakuSpeedLevel,
            Helpers.DanmakuMotion.MinSpeedLevel, Helpers.DanmakuMotion.MaxSpeedLevel);
        // 胶囊底衬(2026-11-05): 纯 bool, 只需防止配置被手改成非布尔 —— 那一律当"开"。
        DanmakuPillBackground = s.DanmakuPillBackground;

        // 字幕字号夹取范围与字幕弹层里的滑杆一致(50~300)
        SubtitleFontScalePercent = Math.Clamp(s.SubtitleFontScalePercent, 50, 300);
        // 字幕外观三项: 老配置没有这些字段 → 拿到属性初始化值(开/45/70), 正好是默认
        SubtitleEnabled = s.SubtitleEnabled;
        SubtitleBackgroundOpacityPercent = Math.Clamp(s.SubtitleBackgroundOpacityPercent, 0, 100);
        SubtitleBottomMargin = Math.Clamp(s.SubtitleBottomMargin, 0, 300);

        // ★★★ 2026-11-07 字幕默认值迁移: 旧版本的默认字号是 100、底距是 24, 凡是用过旧版
        //   的用户配置里都实打实写着这两个数 —— 只改属性的初始化值, 老用户永远享受不到新默认。
        //   这里做**一次性**迁移: 迁移标记(false/缺失)时把"仍等于旧默认"的两项提到新默认,
        //   并把标记置 true 落盘; 之后用户手动改回 100 / 24 会被正常保留。
        //   ★ 只认"等于旧默认"的值: 用户自己拖过的值(比如 150 / 40)必须原样不动。
        if (!s.SubtitleDefaultsMigrated)
        {
            if (SubtitleFontScalePercent == LegacySubtitleFontScalePercent)
                SubtitleFontScalePercent = 200;
            if (SubtitleBottomMargin == LegacySubtitleBottomMargin)
                SubtitleBottomMargin = 70;
        }
        _subtitleDefaultsMigrated = true;

        // 列表屏蔽: 老配置没有这些字段 ⇒ 空串 / 空列表 = 不屏蔽, 正是期望的默认。
        // ★ 仍要清洗: 配置可能被手改坏(空项、重复、超长、负 mid),
        //   脏数据会让面板里出现一条删不掉的空标签。
        FilterTitleKeyword = s.FilterTitleKeyword ?? "";
        FilterUpKeyword = s.FilterUpKeyword ?? "";
        FilterBlockedUps = SanitizeBlockedUps(s.FilterBlockedUps);
        FilterBlockedTitles = SanitizeBlockedTitles(s.FilterBlockedTitles);


        // ★ 默认画质要 Normalize: 手改过的配置可能有 0 / 已下线的档位 / 串到别处
        //   的值, 直接流进取流请求会变成一个谁也不认识的 qn(表现为画质莫名其妙)。
        PreferredQualityQn = QualityPreference.Normalize(s.PreferredQualityQn);

        // ★ 算法选择已删除(2026-10-03): 固定网页版, 不再从配置读 ——
        //   老配置里存着 App 的会被纠正(那一档实测对第三方客户端不提供个性化),
        //   继续"尊重"旧值就是让用户看一屏通用热门。
        RecommendSource = RecommendSource.Web;

        // 主题色: 老配置没有这个字段(空串) → 保持默认的 B 站粉
        AccentColor = s.AccentColor ?? "";

        // 类别串要 Sanitize: 配置里可能留着本应用不做的 interaction, 或被手改坏 ——
        // 不清掉就会出现"界面上一个都没勾、后台却还在按旧类别跳"的矛盾。
        SponsorBlockEnabled = s.SponsorBlockEnabled;
        SponsorBlockCategories = SponsorCategories.Sanitize(s.SponsorBlockCategories);

        // 以下几项老配置都缺字段, 靠属性初始化值兜底(true / 空串), 不需要版本号兼容。
        CloseToTray = s.CloseToTray;
        AutoCheckUpdate = s.AutoCheckUpdate;
        SkippedVersion = s.SkippedVersion ?? "";
        NoLogin1080P = s.NoLogin1080P;

        // ★ CdnManualId 要校验: 老配置里的 Id 可能已从候选表下线,
        //   留着它会让"手动模式"指到一条不存在的线路(见 CdnService.SelectUrlAsync)。
        CdnMode = s.CdnMode;
        CdnManualId = CdnOption.ById(s.CdnManualId)?.Id ?? "";
        // ★★★ 2026-11-05 默认线路迁移: 旧版本的默认值是 Auto, 所有旧配置都带着
        //   `"CdnMode": "Auto"` —— 只改属性初始化值的话老用户永远享受不到新默认。
        //   这里做**一次性**迁移: 迁移标记(false/缺失)时把 Auto 改成 ServerDefault,
        //   并把标记置 true 落盘; 之后再手动选回 Auto 会被正常保留。
        if (!s.CdnModeMigratedToServer && CdnMode == CdnSelectMode.Auto)
            CdnMode = CdnSelectMode.ServerDefault;
        _cdnModeMigratedToServer = true;
        // ★ "手动指定"却没选中任何线路(老配置写坏/用户从未选过)必须归一成默认策略 ——
        //   否则设置页显示"手动指定", 实际 SelectUrlAsync 因为拿不到目标而静默走了别的路,
        //   用户会以为手动设置没生效, 排查时也看不出真相(2026-10-30 实测).
        if (CdnMode == CdnSelectMode.Manual && string.IsNullOrEmpty(CdnManualId))
            CdnMode = CdnSelectMode.ServerDefault;
        BlockPcdn = s.BlockPcdn;

        // 老配置里只有 DisclaimerAccepted(bool)、没有版本号。
        // 那时用户同意的是第 1 版声明, 所以按 1 记 —— 这一版改动后仍会正常再问一次。
        AcceptedDisclaimerVersion = s.DisclaimerVersion > 0
            ? s.DisclaimerVersion
            : (s.DisclaimerAccepted ? 1 : 0);

        // 音量继承: 老配置没这个字段 ⇒ 保持默认 80。越界值夹回 0~100。
        PlayerVolume = Math.Clamp(s.PlayerVolume, 0, 100);

        // 播放器窗口大小记忆(2026-11-03 新增): 老配置没有这三个字段 ⇒
        // AppSettings 的初始化值就是"开启 + 尚未记过尺寸(0)", 不需要版本兼容。
        // ★ 宽高必须夹取/丢弃: 手改配置可能塞进负数或离谱值, 直接拿去设 Width 会让窗口
        //   开到屏幕外或抛异常。非法值统一归 0(= 用 XAML 默认尺寸)。
        RememberPlayerWindowSize = s.RememberPlayerWindowSize;
        PlayerWindowWidth = IsValidPlayerWindowSize(s.PlayerWindowWidth, PlayerWindowMinWidth, PlayerWindowMaxWidth)
            ? s.PlayerWindowWidth : 0;
        PlayerWindowHeight = IsValidPlayerWindowSize(s.PlayerWindowHeight, PlayerWindowMinHeight, PlayerWindowMaxHeight)
            ? s.PlayerWindowHeight : 0;

        // 网络缓冲: 老配置缺字段 ⇒ AppSettings 的初始化值 1000(与旧版一致)。
        // ★ 这里仍要夹取: 手改配置可能塞进 0(等于关掉缓冲)或十万(白占内存)。
        // ★ 0 不能直接 Math.Clamp 成 300 —— 那会把"坏了的值"悄悄推进最容易卡的那一档。
        NetworkCachingMs = s.NetworkCachingMs <= 0
            ? NetworkCachingDefaultMs
            : Math.Clamp(s.NetworkCachingMs, NetworkCachingMinMs, NetworkCachingMaxMs);

        // 缓冲策略: 老配置缺字段 ⇒ bool 默认 false…但这里的语义默认是**自动**,
        // 所以不能靠反序列化的默认值, 必须显式写(老配置没有该字段时 s.* 就是 false)。
        // ★ JSON 反序列化无法区分"没写"与"写了 false" —— 折衷: 用可空类型接,
        //   null = 老配置没这个字段 = 自动(true)。
        AutoBufferPolicy = s.AutoBufferPolicy ?? true;

        // 上次的搜索类别: 老配置没这个字段 ⇒ 视频(与搜索页初始类别一致)。
        SearchKind = Enum.IsDefined(typeof(ContentKind), s.LastSearchKind)
            ? (ContentKind)s.LastSearchKind
            : ContentKind.Video;
    }

    // ------------------------------------------------------------ 列表屏蔽的清洗

    /// <summary>每类一键屏蔽规则最多存多少条(面板里也不可能有人翻 500 条)</summary>
    private const int MaxFilterRules = 500;

    /// <summary>
    /// 清洗"一键屏蔽 UP"列表: 去空、去重(UID 优先, 其次同名)、限制长度。
    /// 配置可能被手改坏或来自更早的实验版本, 脏数据会让面板出现删不掉的空标签。
    /// </summary>
    private static List<BlockedUpEntry> SanitizeBlockedUps(List<BlockedUpEntry>? raw)
    {
        var result = new List<BlockedUpEntry>();
        if (raw == null) return result;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mids = new HashSet<long>();
        foreach (var e in raw)
        {
            if (e == null) continue;

            var name = (e.Name ?? "").Trim();
            if (name.Length > 100) name = name[..100];
            var mid = e.Mid > 0 ? e.Mid : 0;
            if (name.Length == 0 && mid == 0) continue;

            // ★ 先判重再登记: 先 Add 后判重会在"名字重复但 UID 不重复"时把 UID
            //   提前写进集合, 于是后面真正该保留的那条反而被丢掉。
            if (mid > 0 && mids.Contains(mid)) continue;
            if (name.Length > 0 && names.Contains(name)) continue;

            if (mid > 0) mids.Add(mid);
            if (name.Length > 0) names.Add(name);
            result.Add(new BlockedUpEntry { Name = name, Mid = mid });
            if (result.Count >= MaxFilterRules) break;
        }
        return result;
    }

    /// <summary>清洗"一键屏蔽标题"列表: 去空、去重(忽略大小写)、限制长度。</summary>
    private static List<string> SanitizeBlockedTitles(List<string>? raw)
    {
        var result = new List<string>();
        if (raw == null) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in raw)
        {
            var v = (t ?? "").Trim();
            if (v.Length == 0) continue;
            if (v.Length > 300) v = v[..300];
            if (!seen.Add(v)) continue;

            result.Add(v);
            if (result.Count >= MaxFilterRules) break;
        }
        return result;
    }

    /// <summary>
    /// 列表屏蔽条件被修改后调用: 持久化并广播。
    /// 参数传 null 表示"不改这一项" —— 输入关键词和右键一键屏蔽走的是同一个入口,
    /// 各自只该动自己那一份状态, 顺手把别的项抄一遍很容易漏掉新加的字段。
    /// </summary>
    public void UpdateFilterSettings(
        string? titleKeyword = null,
        string? upKeyword = null,
        IEnumerable<BlockedUpEntry>? blockedUps = null,
        IEnumerable<string>? blockedTitles = null)
    {
        var nextTitle = titleKeyword ?? FilterTitleKeyword;
        var nextUp = upKeyword ?? FilterUpKeyword;
        var nextUps = blockedUps != null
            ? SanitizeBlockedUps(blockedUps.ToList())
            : FilterBlockedUps;
        var nextTitles = blockedTitles != null
            ? SanitizeBlockedTitles(blockedTitles.ToList())
            : FilterBlockedTitles;

        var changed = !string.Equals(nextTitle, FilterTitleKeyword, StringComparison.Ordinal)
            || !string.Equals(nextUp, FilterUpKeyword, StringComparison.Ordinal)
            || !SameUps(nextUps, FilterBlockedUps)
            || !SameTitles(nextTitles, FilterBlockedTitles);

        FilterTitleKeyword = nextTitle;
        FilterUpKeyword = nextUp;
        FilterBlockedUps = nextUps;
        FilterBlockedTitles = nextTitles;

        if (changed) Save();
    }

    private static bool SameUps(List<BlockedUpEntry> a, List<BlockedUpEntry> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Mid != b[i].Mid) return false;
            if (!string.Equals(a[i].Name, b[i].Name, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static bool SameTitles(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>
    /// 把当前设置拍成一份待落盘的 <see cref="AppSettings"/> 快照(纯内存操作, 微秒级)。
    ///
    /// ★ 与 <see cref="ApplySanitized"/> 成对: 那一边定义"怎么读", 这一边定义"怎么写"。
    ///   新增设置项时两处都要加 —— 这是本类唯一需要成对维护的地方, 所以刻意把两个方法
    ///   挨在一起、字段顺序也保持一致, 便于逐行对照。
    ///
    /// ★ AcrylicBackground 是**派生值**(由 AcrylicMaterial 算出来), 落盘时同步写一份,
    ///   只是为了让用户回退到旧版本时状态不丢; 代码里别再按它判断(见 AppSettings 的说明)。
    /// </summary>
    private AppSettings Snapshot() => new()
    {
        ThemeMode = ThemeMode,
        AcrylicMaterial = AcrylicMaterial,
        AcrylicBackground = AcrylicBackground,
        AcrylicOpacityLight = AcrylicOpacityLight,
        AcrylicOpacityDark = AcrylicOpacityDark,
        CardColumns = CardColumns,
        UiScalePercent = UiScalePercent,
        FollowCardWall = FollowCardWall,
        DownloadDir = DownloadDir,
        DanmakuEnabled = DanmakuEnabled,
        DanmakuAreaPercent = DanmakuAreaPercent,
        DanmakuSmartFilter = DanmakuSmartFilter,
        DanmakuColorful = DanmakuColorful,
        DanmakuFilterScroll = DanmakuFilterScroll,
        DanmakuFilterFixed = DanmakuFilterFixed,
        DanmakuFilterColorful = DanmakuFilterColorful,
        DanmakuFilterAdvanced = DanmakuFilterAdvanced,
        DanmakuOpacityPercent = DanmakuOpacityPercent,
        DanmakuFontScalePercent = DanmakuFontScalePercent,
        DanmakuSpeedLevel = DanmakuSpeedLevel,
        DanmakuPillBackground = DanmakuPillBackground,
        SubtitleFontScalePercent = SubtitleFontScalePercent,
        SubtitleEnabled = SubtitleEnabled,
        SubtitleBackgroundOpacityPercent = SubtitleBackgroundOpacityPercent,
        SubtitleBottomMargin = SubtitleBottomMargin,
        SubtitleDefaultsMigrated = _subtitleDefaultsMigrated,
        FilterTitleKeyword = FilterTitleKeyword,
        FilterUpKeyword = FilterUpKeyword,
        FilterBlockedUps = FilterBlockedUps,
        FilterBlockedTitles = FilterBlockedTitles,
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
        CdnModeMigratedToServer = _cdnModeMigratedToServer,
        BlockPcdn = BlockPcdn,
        DisclaimerAccepted = DisclaimerAccepted,
        DisclaimerVersion = AcceptedDisclaimerVersion,
        PlayerVolume = PlayerVolume,
        RememberPlayerWindowSize = RememberPlayerWindowSize,
        PlayerWindowWidth = PlayerWindowWidth,
        PlayerWindowHeight = PlayerWindowHeight,
        NetworkCachingMs = NetworkCachingMs,
        AutoBufferPolicy = AutoBufferPolicy,
        LastSearchKind = (int)SearchKind
    };

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
            return JsonSerializer.Serialize(Snapshot(), JsonOpts);
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
    /// 字幕字号百分比(50~300)并落盘。
    ///
    /// ★ 播放器里拖滑杆时**每一格都会调到这里**, 所以必须"没变就直接 return" ——
    ///   Save() 内部虽有节流, 但白跑一次序列化没有意义(与 SetPreferredQuality 同款)。
    /// ★ 夹取范围与 ApplySanitized 保持一致: 两处不同步的话, 拖到端点后重启会跳回另一个值。
    /// </summary>
    public void SetSubtitleFontScalePercent(int percent)
    {
        var next = Math.Clamp(percent, 50, 300);
        if (SubtitleFontScalePercent == next) return;
        SubtitleFontScalePercent = next;
        Save();
        SubtitleSettingsChanged?.Invoke();
    }

    /// <summary>默认显示字幕开关并落盘(播放器在取到轨时读它决定是否自动打开)。</summary>
    public void SetSubtitleEnabled(bool enabled)
    {
        if (SubtitleEnabled == enabled) return;
        SubtitleEnabled = enabled;
        Save();
        SubtitleSettingsChanged?.Invoke();
    }

    /// <summary>字幕底衬不透明度(0~100)并落盘。夹取范围与设置页滑杆一致。</summary>
    public void SetSubtitleBackgroundOpacityPercent(int percent)
    {
        var next = Math.Clamp(percent, 0, 100);
        if (SubtitleBackgroundOpacityPercent == next) return;
        SubtitleBackgroundOpacityPercent = next;
        Save();
        SubtitleSettingsChanged?.Invoke();
    }

    /// <summary>字幕距画面底部距离(px, 0~300)并落盘。</summary>
    public void SetSubtitleBottomMargin(int px)
    {
        var next = Math.Clamp(px, 0, 300);
        if (SubtitleBottomMargin == next) return;
        SubtitleBottomMargin = next;
        Save();
        SubtitleSettingsChanged?.Invoke();
    }

    /// <summary>
    /// 切换**窗口背景材质**并落盘, 广播给所有已打开的窗口。
    ///
    /// ★ 2026-10-06 起这一个方法同时承担"开关 + 变体选择"(四种材质平级):
    ///   传 <see cref="BackdropMaterial.None"/> 就是关闭。
    ///   ⇒ 原来那个 SetAcrylic(bool) 已删除 —— 两个入口并存迟早出现"开关说开、材质说无"的矛盾。
    /// ★ 必须走事件(不能只改资源字典): 材质要动窗口的**原生合成属性**(DWM),
    ///   每个窗口都得自己重下一次。
    /// </summary>
    public void SetAcrylicMaterial(BackdropMaterial material)
    {
        // 非法枚举值挡在这里: 否则会下发一个 DWM 不认识的 DWMSBT, 材质静默不生效
        if (!Enum.IsDefined(typeof(BackdropMaterial), material)) return;
        if (AcrylicMaterial == material) return;
        AcrylicMaterial = material;
        Save();
        AcrylicChanged?.Invoke();
    }

    /// <summary>
    /// 设置卡片页列数(0 = 自动)并落盘, 广播给所有窗口。
    /// ★ 它不是"改个资源"那种轻量变更: 列数写进 Helpers.CardWall 的静态属性后,
    ///   所有已存在的卡片墙都要**立刻重算**, 所以这里调 CardWall.SetFixedColumns(它内部会 RefreshAll)。
    /// </summary>
    public void SetCardColumns(int columns)
    {
        var v = columns <= 0 ? 0 : Math.Clamp(columns, Helpers.CardWall.FixedColumnsMin, Helpers.CardWall.FixedColumnsMax);
        if (CardColumns == v) return;
        CardColumns = v;
        Helpers.CardWall.SetFixedColumns(v);
        Save();
        LayoutChanged?.Invoke();
    }

    /// <summary>
    /// 设置软件缩放百分比并落盘, 广播给所有窗口。
    /// ★ 与列数同理: 倍率落在静态的 <see cref="Helpers.UiScale"/> 上, 各窗口的根元素
    ///   各自持有 LayoutTransform, 所以必须通知每个窗口重新施加一次(见 FluentWindow.OnLayoutChanged)。
    /// </summary>
    public void SetUiScalePercent(int percent)
    {
        var v = Helpers.UiScale.ClampPercent(percent);
        if (UiScalePercent == v) return;
        UiScalePercent = v;
        Helpers.UiScale.SetPercent(v);
        Save();
        LayoutChanged?.Invoke();
    }

    /// <summary>
    /// 影响**布局**的设置变化(列数/缩放)。界面据此重新排版 ——
    /// 与主题那类"换笔刷"不同, 这些必须让可视树重新测量。
    /// </summary>
    public event Action? LayoutChanged;

    /// <summary>
    /// **下载位置**变化。改位置只影响"以后下载到哪", 订阅方(设置页/离线缓存页)要把
    /// 显示的路径换成新值。
    /// ★ 已下载的视频**不迁移**: 那是用户的成品文件, 静默搬动等于替他做决定
    ///   (可能几十 GB, 也可能他就是想留在原处)。
    /// </summary>
    public event Action? DownloadDirChanged;

    /// <summary>
    /// 设置自定义下载位置(空串 = 恢复默认的系统下载文件夹)并落盘, 广播给界面。
    ///
    /// ★ <paramref name="path"/> 先过 AppPaths 校验, 非法/不存在一律回落默认目录 ——
    ///   返回的是**实际生效**的目录, 调用方拿它回显(用户能看到自己填的到底生效没)。
    /// ★ 与旧的 SetCacheRoot 不同: **没有 migrate 参数** —— 下载目录下的文件是成品,
    ///   不该被程序自动搬走。
    /// </summary>
    public string SetDownloadDir(string? path)
    {
        // 归一化后再存: 用户填的 "D:\视频\" 与存着的 "D:\视频" 应当视为同一个。
        var normalized = AppPaths.NormalizeDir(path) ?? "";
        var effective = AppPaths.SetDownloadDir(normalized);
        // ★ 存"生效值"而不是"用户输入": 存了非法路径的话, 下次启动 Normalize 又会把它
        //   判掉回落默认 —— 那等于用户看到的路径和实际用的不是一个。
        //   (还原默认时 effective == DefaultDownloadDir, 存空串以保持"没自定义过"的语义)
        DownloadDir = string.Equals(effective, AppPaths.DefaultDownloadDir, StringComparison.OrdinalIgnoreCase)
            ? ""
            : effective;

        Save();
        DownloadDirChanged?.Invoke();
        return effective;
    }

    /// <summary>
    /// 调节浅色主题亚克力透明度(可调范围见 <see cref="AcrylicOpacityLightMin"/>)并落盘, 广播给所有窗口重设背景
    /// </summary>
    public void SetAcrylicOpacityLight(int opacity)
    {
        opacity = Math.Clamp(opacity, AcrylicOpacityLightMin, AcrylicOpacityLightMax);
        if (AcrylicOpacityLight == opacity) return;
        AcrylicOpacityLight = opacity;
        Save();
        AcrylicChanged?.Invoke();
    }

    /// <summary>调节深色主题亚克力透明度(可调范围见 <see cref="AcrylicOpacityDarkMin"/>)并落盘, 广播给所有窗口重设背景</summary>
    public void SetAcrylicOpacityDark(int opacity)
    {
        opacity = Math.Clamp(opacity, AcrylicOpacityDarkMin, AcrylicOpacityDarkMax);
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
        // 用户一旦显式改过线路设置, 就认为"默认值迁移"已完成:
        // 否则新装用户主动选 Auto 后, 下次启动会被一次性迁移又推回 ServerDefault。
        _cdnModeMigratedToServer = true;
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
        bool? colorful = null,
        bool? filterScroll = null, bool? filterFixed = null,
        bool? filterColorful = null, bool? filterAdvanced = null,
        int? opacityPercent = null, int? fontScalePercent = null, int? speedLevel = null,
        bool? pillBackground = null)
    {
        var nextColorful = colorful ?? DanmakuColorful;
        var nextFScroll = filterScroll ?? DanmakuFilterScroll;
        var nextFFixed = filterFixed ?? DanmakuFilterFixed;
        var nextFColorful = filterColorful ?? DanmakuFilterColorful;
        var nextFAdvanced = filterAdvanced ?? DanmakuFilterAdvanced;
        // 三个外观项同样走"传 null 表示不改": 播放器调滑杆时只想改不透明度,
        // 要是顺手把字号/速度重置回默认, 用户会觉得"我刚调好的速度又变了"。
        var nextOpacity = Math.Clamp(opacityPercent ?? DanmakuOpacityPercent, 0, 100);
        var nextFont = Math.Clamp(fontScalePercent ?? DanmakuFontScalePercent, 50, 300);
        var nextSpeed = Math.Clamp(speedLevel ?? DanmakuSpeedLevel,
            Helpers.DanmakuMotion.MinSpeedLevel, Helpers.DanmakuMotion.MaxSpeedLevel);
        // 胶囊底衬(2026-11-05): 同样走"传 null 表示不改"。
        var nextPill = pillBackground ?? DanmakuPillBackground;
        var changed = enabled != DanmakuEnabled
            || areaPercent != DanmakuAreaPercent
            || smartFilter != DanmakuSmartFilter
            || nextColorful != DanmakuColorful
            || nextFScroll != DanmakuFilterScroll
            || nextFFixed != DanmakuFilterFixed
            || nextFColorful != DanmakuFilterColorful
            || nextFAdvanced != DanmakuFilterAdvanced
            || nextOpacity != DanmakuOpacityPercent
            || nextFont != DanmakuFontScalePercent
            || nextSpeed != DanmakuSpeedLevel
            || nextPill != DanmakuPillBackground;
        DanmakuEnabled = enabled;
        DanmakuAreaPercent = Math.Clamp(areaPercent, 25, 100);
        DanmakuSmartFilter = smartFilter;
        DanmakuColorful = nextColorful;
        DanmakuFilterScroll = nextFScroll;
        DanmakuFilterFixed = nextFFixed;
        DanmakuFilterColorful = nextFColorful;
        DanmakuFilterAdvanced = nextFAdvanced;
        DanmakuOpacityPercent = nextOpacity;
        DanmakuFontScalePercent = nextFont;
        DanmakuSpeedLevel = nextSpeed;
        DanmakuPillBackground = nextPill;
        Save();
        if (changed) DanmakuSettingsChanged?.Invoke();
    }
}
