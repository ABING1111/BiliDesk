using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Views;   // VolumeChanged / SetPlayerVolume 的 origin 参数是 PlayerWindow

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

    // ---- 描边(2026-10-14 新增, 用户要求"设置界面可以选开启和粗细") ----

    /// <summary>
    /// 弹幕文字要不要带黑色描边。
    /// ★ 为什么默认开: 描边存在的原意是"白色画面上也看得清"(用户 2026-10-13 提的需求)。
    ///   而它在深色画面上几乎看不出, 所以开着几乎无害 —— 真正需要关的用户是"深色视频多、
    ///   觉得黑边让字显脏"的那种, 交给开关而不是替他决定。
    /// </summary>
    public bool DanmakuOutlineEnabled { get; set; } = true;

    /// <summary>
    /// 描边的**可见**宽度(设备无关像素), 即字**外面**那一圈黑边有多厚。
    ///
    /// ★★★ 为什么存"可见宽度"而不是内部的 <c>StrokeThickness</c>(它是可见宽度的两倍):
    ///   WPF 的 Stroke 沿路径中心线**向两侧各扩一半**, 而当前的两层画法(描边层在下、
    ///   填充层在上, 见 PlayerWindow.BuildDanmakuElement)把**向内的那一半完全盖住**,
    ///   用户看到的只有向外那一半。于是对用户讲"1.8 的描边"毫无意义 —— 他看到的是 0.9。
    ///   存可见宽度还有个长远好处: 将来若改成真正的轮廓膨胀(Geometry.Combine),
    ///   这个数字的语义不变, 用户的设置不会突然变粗一倍。
    /// ★ 合法范围 0.4 ~ 1.6。低于 0.4 在 24px 中文上等于没有; 高于 1.6 笔画会被啃粗。
    /// </summary>
    public double DanmakuOutlineWidth { get; set; } = 0.8;

    /// <summary>
    /// 默认画质(qn)。用户在设置页**开始播放前**预先选定的清晰度,
    /// 之后每次点开视频/换集都用它起播; 该档不可用时自动降级(见 ApiClient.GetPlayUrlAsync)。
    /// ★ 默认取 <see cref="QualityPreference.DefaultQn"/>(1080P) —— 与新增本设置项之前
    ///   播放器里写死的 qn=80 一致, 老用户升级后起播画质不变。
    /// 老配置没有这个字段 → 反序列化保持属性初始化值 → 自动落到默认档。
    /// </summary>
    public int PreferredQualityQn { get; set; } = QualityPreference.DefaultQn;

    /// <summary>
    /// 是否允许同时打开多个播放器窗口(默认**开**)。
    ///
    /// · 开: 每次播放(视频/直播/本地文件)都**新建**一个播放器窗口, 已开着的窗口继续播
    ///   各自的 —— 用户可以一边看 A 一边点开 B, 不会互相覆盖;
    /// · 关: 复用同一个播放器窗口(旧行为), 打开新视频会替换当前播放。
    ///
    /// ★ 只影响"新窗口怎么来": 播放中改它不会动已经开着的窗口(下次播放才按新值走),
    ///   所以不需要广播事件 —— 与「默认画质」同款(见 <see cref="SetPreferredQuality"/> 的说明)。
    /// ★ 默认开的理由: 复用窗口会覆盖正在看的视频, 是用户明确不要的行为;
    ///   想要旧行为的在设置页关掉即可(见 <see cref="SetMultiPlayerWindow"/>)。
    /// ★ 读取点只有一个: PlayerService.GetOrCreateWindow。
    /// </summary>
    public bool MultiPlayerWindow { get; set; } = true;

    // ------------------------------------------------------------ 播放器窗口尺寸记忆
    //
    // ★ 用户要求(2026-10-13): 上一次把播放器窗口拖成多大, 下一次打开就是多大。
    //
    // ★ **只记大小, 不记位置**: 双屏/拔掉外接屏之后, 记下来的坐标可能落在已经不存在的
    //   屏幕上 ⇒ 窗口开在"看不见的地方", 用户得自己拖出来。多屏位置记忆要配合
    //   "当前显示器是否还挂着"来判断, 那是另一件事, 这里不做。
    //
    // ★ 0 / 负数一律当"没有记忆", 走 XAML 里的默认尺寸。老配置没有这两个字段时
    //   属性初始化值就是 0, 天然兼容, 不需要版本号。
    public double PlayerWindowWidth { get; set; }
    public double PlayerWindowHeight { get; set; }

    /// <summary>
    /// 收起详情栏模式下的窗口大小。★ 与上面两个是**不同模式的不同量**, 不能共用 ——
    /// 理由见 <see cref="RememberPlayerWindowCollapsedSize"/>。
    /// </summary>
    public double PlayerWindowCollapsedWidth { get; set; }
    public double PlayerWindowCollapsedHeight { get; set; }

    /// <summary>
    /// 记下窗口当前大小。
    ///
    /// ★ 这里只做"**能不能存**"的判断, 不做"能不能用"的判断 —— 后者是
    ///   PlayerWindow.IsUsablePlayerWindowSize 的活, 而且它得用**窗口自己的**最小尺寸
    ///   (1100x600)去判。为什么不合并: 这一层不认识 WPF 的窗口类型, 把界面常量抄一份
    ///   过来早晚会长歪, 一旦长歪症状是"存的是 A 值、按 B 值筛", 极难查。
    /// ★ 这层只拦两类值: **非有限数**(NaN / ±∞; 手改配置或将来某个 bug 都可能写进来,
    ///   一旦存进 JSON, 下次启动读出来照样非有限, 会一路传到窗口上)与**非正数**。
    ///   常规模式 1100x600 那道下限不在这里判 —— 存都存下来了, 套不套是窗口的事。
    /// ★ 调用方(PlayerWindow)在 Closing 时已过滤掉铺满/全屏/最小化与非当前窗口:
    ///   那几种状态下量到的是"整块铺满"的尺寸, 存下来会让下次普通模式变成满屏大小。
    /// </summary>
    public void RememberPlayerWindowSize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            return;   // 无效值不写
        if (Math.Abs(width - PlayerWindowWidth) < 0.5
            && Math.Abs(height - PlayerWindowHeight) < 0.5) return;   // 没变就别触发落盘
        PlayerWindowWidth = width;
        PlayerWindowHeight = height;
        Save();
    }

    /// <summary>
    /// 记下**收起右侧详情栏**(铺满窗口)状态下的窗口大小(2026-10-13 新增)。
    ///
    /// ★ 为什么必须与普通模式**分开存**, 而不是一个字段两个模式共用:
    ///   收起模式下允许缩到 320x200, 展开时最小是 1100x600 —— 两个模式的取值范围完全不重叠。
    ///   共用一个字段的结果是"用户在收起状态下把窗口缩到 500x400, 关掉, 下次以普通模式打开"
    ///   得到一个 500x400 的窗口, 而 WPF 会把它**抬到** 1100x600; 反过来则是"展开时调好的
    ///   大尺寸, 收起时被原样套上, 视频区小得看不见"。两个方向都表现为"尺寸记忆坏了"。
    /// </summary>
    public void RememberPlayerWindowCollapsedSize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            return;
        if (Math.Abs(width - PlayerWindowCollapsedWidth) < 0.5
            && Math.Abs(height - PlayerWindowCollapsedHeight) < 0.5) return;
        PlayerWindowCollapsedWidth = width;
        PlayerWindowCollapsedHeight = height;
        Save();
    }

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
    /// 播放器音量(0~100), 跨视频继承 —— 调一次, 之后打开的每个视频都是这个音量。
    ///
    /// ★ 为什么不做"关窗即还原成 80": 用户的音量偏好是**长期**的(有人常年 30%, 有人常年
    ///   满格), 每个视频都要重调一遍纯属折腾。0 也照样存 —— 那是用户主动静音的,
    ///   下次打开还该是静音(与"静音键"的语义一致)。
    /// ★ 与 <see cref="Models.AppSettings.PlayerVolume"/> 是读写契约, 两处必须一致。
    /// ★ 2026-10-12 起它同时是设置页的「默认音量」项(可直接预置, 不必先播一次);
    ///   范围常量见 <see cref="VolumeMin"/>/<see cref="VolumeMax"/>, 变更事件见
    ///   <see cref="VolumeChanged"/>(带发起方, 多窗口下才不会互相串音量)。
    /// </summary>
    public int PlayerVolume { get; set; } = 80;

    /// <summary>音量可调范围(含)。设置页滑杆上下限、Load 兜底、setter 夹取全引这两个常量,
    /// 不在别处重复写字面量(与 AppSettings.PlayerVolume 是读写契约)。</summary>
    public const int VolumeMin = 0;
    /// <inheritdoc cref="VolumeMin"/>
    public const int VolumeMax = 100;

    /// <summary>
    /// 音量变更。事件参数 = **发起方**(null = 设置页/外部, 非 null = 那个播放器窗口)。
    ///
    /// ★ 为什么必须带来源(2026-10-12 审查发现, 多窗口下的真 bug):
    ///   多播放器窗口同时开着时, 每个窗口都订阅本事件。如果无脑广播, 在 B 窗口调音量 →
    ///   写回设置 → 事件 → **A 窗口也跟着变**(两个窗口一起被调), 与"音量是长期偏好"
    ///   的语义不符 —— 用户在 A 里听的声音不该被 B 动。
    ///   带来源后: 设置页发起 ⇒ 广播给所有窗口(这是用户明确要求"全部生效"的);
    ///   某个播放器发起 ⇒ 只写回设置供**下次**开窗使用, 不打扰其他窗口。
    /// </summary>
    public event Action<PlayerWindow?>? VolumeChanged;

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
    /// 设置播放器音量并落盘, 并按需广播。0~100 之外的值被夹回范围内(老配置可能存着越界值)。
    ///
    /// <paramref name="origin"/> = 谁发起的这次修改(null = 设置页/外部, 非 null = 某个播放器窗口)。
    /// ★ 它决定事件怎么被消费, 别随手传 null: 多窗口下"在 B 窗口调音量却把 A 也调了"
    ///   就是漏传 origin 造成的(见 <see cref="VolumeChanged"/> 的说明)。
    /// ★ 同值短路不只是省磁盘: 程序性回写(Playing 补写、事件触发后的同步)因此不会
    ///   产生额外落盘, 也不需要调用方判重。
    /// </summary>
    public void SetPlayerVolume(int volume, PlayerWindow? origin = null)
    {
        var v = Math.Clamp(volume, VolumeMin, VolumeMax);
        if (PlayerVolume == v) return;
        PlayerVolume = v;
        Save();
        VolumeChanged?.Invoke(origin);
    }

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
        DanmakuBlockKeywords = s.DanmakuBlockKeywords ?? "";
        DanmakuFilterScroll = s.DanmakuFilterScroll;
        DanmakuFilterFixed = s.DanmakuFilterFixed;
        DanmakuFilterColorful = s.DanmakuFilterColorful;
        DanmakuFilterAdvanced = s.DanmakuFilterAdvanced;
    // ★★ 描边(2026-10-14 新增)的两个字段都要**纠正老配置**, 不能照抄:
    //   bool 的默认值是 false, 而"描边开着"才是我们要的默认 —— 老配置里根本没这个键,
    //   反序列化给的就是 false。照抄的话每个老用户升级后描边都会**凭空消失**, 而且
    //   界面上那个开关也是关的, 用户只会觉得"这版本把描边删了"。
    //   判据用"有没有这个键"最准, 但 AppSettings 是普通 POCO 拿不到原始 JSON,
    //   所以退一步用值判断: 老配置只可能是 false(从未开过), 新配置关掉也是 false ——
    //   两者无法区分, 因此这里选择"以默认值为准, 关掉由用户自己再关一次"。
    //   代价只有一个: 从 1.2.7 升上来、且**主动关过**描边的用户, 升级后描边会回来一次。
    //   这个代价远小于"所有人都丢描边"。
    DanmakuOutlineEnabled = s.DanmakuOutlineEnabled || DanmakuOutlineEnabled;
    // 宽度同理: 老配置里没有这个键 ⇒ 0 ⇒ 替换成默认 0.8 而不是夹到下界 0.4,
    //   因为 0.4 那一档的描边在白底上几乎看不见, 等于"有开关但没效果"。
    DanmakuOutlineWidth = Math.Clamp(
        s.DanmakuOutlineWidth > 0.01 ? s.DanmakuOutlineWidth : DanmakuOutlineWidth,
        0.4, 1.6);

        // ★ 默认画质要 Normalize: 手改过的配置可能有 0 / 已下线的档位 / 串到别处
        //   的值, 直接流进取流请求会变成一个谁也不认识的 qn(表现为画质莫名其妙)。
        PreferredQualityQn = QualityPreference.Normalize(s.PreferredQualityQn);

        // 多窗口开关: 老配置缺字段时靠属性初始化值(true), 不需要版本兼容。
        MultiPlayerWindow = s.MultiPlayerWindow;

        // 播放器窗口尺寸记忆: 老配置缺字段时为 0, PlayerWindow.IsUsablePlayerWindowSize
        // 会当成"没有记忆"走默认尺寸。
        // ★ 不在这里做有效性过滤: 脏值原样读进来, 由套用侧(IsUsablePlayerWindowSize)统一判断,
        //   否则"读取时清理"与"套用时判断"两处规则会长歪 —— 而套用侧那道门槛还依赖
        //   窗口的 MinWidth, 那是个界面常量, 这一层拿不到也不该拿。
        PlayerWindowWidth = s.PlayerWindowWidth;
        PlayerWindowHeight = s.PlayerWindowHeight;
        PlayerWindowCollapsedWidth = s.PlayerWindowCollapsedWidth;
        PlayerWindowCollapsedHeight = s.PlayerWindowCollapsedHeight;

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
        BlockPcdn = s.BlockPcdn;

        // 老配置里只有 DisclaimerAccepted(bool)、没有版本号。
        // 那时用户同意的是第 1 版声明, 所以按 1 记 —— 这一版改动后仍会正常再问一次。
        AcceptedDisclaimerVersion = s.DisclaimerVersion > 0
            ? s.DisclaimerVersion
            : (s.DisclaimerAccepted ? 1 : 0);

        // 音量继承: 老配置没这个字段 ⇒ 保持默认 80。越界值夹回 VolumeMin~VolumeMax。
        PlayerVolume = Math.Clamp(s.PlayerVolume, VolumeMin, VolumeMax);

        // 上次的搜索类别: 老配置没这个字段 ⇒ 视频(与搜索页初始类别一致)。
        SearchKind = Enum.IsDefined(typeof(ContentKind), s.LastSearchKind)
            ? (ContentKind)s.LastSearchKind
            : ContentKind.Video;
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
        DownloadDir = DownloadDir,
        DanmakuEnabled = DanmakuEnabled,
        DanmakuAreaPercent = DanmakuAreaPercent,
        DanmakuSmartFilter = DanmakuSmartFilter,
        DanmakuColorful = DanmakuColorful,
        DanmakuBlockKeywords = DanmakuBlockKeywords,
        DanmakuFilterScroll = DanmakuFilterScroll,
        DanmakuFilterFixed = DanmakuFilterFixed,
        DanmakuFilterColorful = DanmakuFilterColorful,
        DanmakuFilterAdvanced = DanmakuFilterAdvanced,
    DanmakuOutlineEnabled = DanmakuOutlineEnabled,
    DanmakuOutlineWidth = DanmakuOutlineWidth,
        PreferredQualityQn = PreferredQualityQn,
        MultiPlayerWindow = MultiPlayerWindow,
        PlayerWindowWidth = PlayerWindowWidth,
        PlayerWindowHeight = PlayerWindowHeight,
        PlayerWindowCollapsedWidth = PlayerWindowCollapsedWidth,
        PlayerWindowCollapsedHeight = PlayerWindowCollapsedHeight,
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
        DisclaimerVersion = AcceptedDisclaimerVersion,
        PlayerVolume = PlayerVolume,
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
    /// 改"多个播放器窗口"并落盘。
    /// ★ 不需要广播事件: 它只在**新建播放器窗口那一刻**被读(见 PlayerService.GetOrCreateWindow),
    ///   已经开着的窗口不受影响 —— 与「默认画质」「免登录 1080P」同款, 下次播放即按新值走。
    /// </summary>
    public void SetMultiPlayerWindow(bool value)
    {
        if (MultiPlayerWindow == value) return;
        MultiPlayerWindow = value;
        Save();
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
        bool? filterColorful = null, bool? filterAdvanced = null,
        bool? outlineEnabled = null, double? outlineWidth = null)
    {
        var nextKeywords = blockKeywords ?? DanmakuBlockKeywords;
        var nextColorful = colorful ?? DanmakuColorful;
        var nextFScroll = filterScroll ?? DanmakuFilterScroll;
        var nextFFixed = filterFixed ?? DanmakuFilterFixed;
        var nextFColorful = filterColorful ?? DanmakuFilterColorful;
        var nextFAdvanced = filterAdvanced ?? DanmakuFilterAdvanced;
        // 描边(2026-10-14 新增): 同样走"可选覆盖", 调用点只传自己关心的那一项。
        // ★ 宽度要夹到区间: 滑杆给的是 0.4~1.6, 但手改配置或将来某个 bug 都可能送进
        //   区间外的值, 而一个负宽度会让 Stroke 抛 ArgumentException, 直接把弹幕线程带崩。
        var nextOutlineW = Math.Clamp(outlineWidth ?? DanmakuOutlineWidth, 0.4, 1.6);
        var nextOutlineOn = outlineEnabled ?? DanmakuOutlineEnabled;
        var changed = enabled != DanmakuEnabled
            || areaPercent != DanmakuAreaPercent
            || smartFilter != DanmakuSmartFilter
            || nextColorful != DanmakuColorful
            || nextFScroll != DanmakuFilterScroll
            || nextFFixed != DanmakuFilterFixed
            || nextFColorful != DanmakuFilterColorful
            || nextFAdvanced != DanmakuFilterAdvanced
            || nextOutlineOn != DanmakuOutlineEnabled
            || Math.Abs(nextOutlineW - DanmakuOutlineWidth) > 1e-6
            || !string.Equals(nextKeywords, DanmakuBlockKeywords, StringComparison.Ordinal);
        DanmakuEnabled = enabled;
        DanmakuAreaPercent = Math.Clamp(areaPercent, 25, 100);
        DanmakuSmartFilter = smartFilter;
        DanmakuColorful = nextColorful;
        DanmakuFilterScroll = nextFScroll;
        DanmakuFilterFixed = nextFFixed;
        DanmakuFilterColorful = nextFColorful;
        DanmakuFilterAdvanced = nextFAdvanced;
        DanmakuOutlineEnabled = nextOutlineOn;
        DanmakuOutlineWidth = nextOutlineW;
        DanmakuBlockKeywords = nextKeywords;
        Save();
        if (changed) DanmakuSettingsChanged?.Invoke();
    }
}
