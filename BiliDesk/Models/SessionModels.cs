using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;

namespace BiliDesk.Models;

// ★ 2026-11-07 由 Models.cs 拆分而来(纯移动、零逻辑改动)。本文件: 会话(Session) / 屏蔽规则 / 应用设置持久化模型(AppSettings)

/// <summary>会话(登录 Cookie)</summary>
public class Session
{
    public string? SessData { get; set; }
    public string? BiliJct { get; set; }
    public string? DedeUserID { get; set; }
    public string? DedeUserIDCkMd5 { get; set; }
    public string? Buvid3 { get; set; }

    /// <summary>
    /// 设备指纹的第二半(buvid4)。
    ///
    /// ★ 必须持久化, 不能只放内存。推荐系统靠 (buvid3 + buvid4) 这一对认"这是同一台设备",
    ///   而 `x/frontend/finger/spi` **每次调用都发一对全新的** —— 实测: 即使把已有的
    ///   buvid3/buvid4 带上去, 它照样回吐两个新的, 不做 round-trip。所以只要不在本地存下来,
    ///   每次启动都会变成一台"从未见过的设备", 推荐模型每次都要从零冷启动,
    ///   表现就是"推的东西东一榔头西一棒槌"。
    /// 老配置没有这个字段 → 反序列化保持 null → 下次启动补上并落盘。
    /// </summary>
    public string? Buvid4 { get; set; }

    // ------------------------------------------------------------------
    // ★★★ 写操作(点赞/投币/收藏/三连)必需的风控 Cookie(2026-11-12 加)
    //
    // 背景: B 站的**读**接口只看 SESSDATA + buvid3/4 就放行, 但**写**接口还会看
    // 这一组"浏览器会话特征"。实测(探针 bd-probe-like, 登录态 nav 正常、只读
    // has/like 也正常): 缺它们时写接口间歇性返回 -352「被风控拦截」/ -403「账号异常」。
    //
    // ★ 为什么这与"扫码登录"有关: 旧流程从浏览器 Profile 里直接拿整套 Cookie,
    //   天然带着这些; 新登录流程(2026-10 引入扫码/账密)只挑了 6 个存进 session.json,
    //   其余全丢 —— 于是"换了登录方式之后点赞就不行了", 而登录态本身是好的。
    //
    // 值全部可空: 老配置没有这些字段, 缺了就只带能带的(退回到改动前的行为, 不会更坏)。
    public string? BNut { get; set; }
    public string? Sid { get; set; }
    public string? BuvidFp { get; set; }
    public string? Blsid { get; set; }
}

/// <summary>
/// 一条"一键屏蔽 UP 主"规则(落盘用)。
/// Name 只用于面板显示/UID 缺失时的兜底匹配; Mid &gt; 0 时优先按 UID 命中, UP 改名也拦得住。
/// </summary>
public class BlockedUpEntry
{
    public string Name { get; set; } = "";
    public long Mid { get; set; }
}

/// <summary>应用设置</summary>
public class AppSettings
{
    public AppTheme ThemeMode { get; set; } = AppTheme.System;
    /// <summary>
    /// 播放器音量(0~100)。用户调过一次就记住, 之后打开的每个视频都用它 ——
    /// 这就是"音量继承"(2026-11-12 用户要求"调节音量以后下一个视频的音量也一样")。
    /// ★ 与 Services/SettingsStore.cs 的同名属性是**读写契约**, 两处必须一致。
    /// </summary>
    public int PlayerVolume { get; set; } = 80;

    /// <summary>
    /// 优先高音质音轨(默认开)。开启时取流请求会带上 fnval=4048,
    /// 并在有 Hi-Res 无损(30251)/杜比(30250)音轨时优先使用; 普通视频仍是 192K AAC。
    /// ★ 与 Services/SettingsStore.cs 的同名属性是**读写契约**, 两处必须一致。
    /// </summary>
    public bool PreferHighQualityAudio { get; set; } = true;

    /// <summary>
    /// 播放器音质档位(2026-11-06 新增, 控制栏音质按钮)。
    /// Auto = 沿用"无损 &gt; 杜比 &gt; 192K &gt; 132K &gt; 64K"自动排序;
    /// 指定档位时只取那一档, 不可用则降级。
    /// ★ 与 Services/SettingsStore.cs 的同名属性是**读写契约**, 两处必须一致。
    /// </summary>
    public AudioTier PreferredAudioTier { get; set; } = AudioTier.Auto;

    /// <summary>
    /// 下次打开视频时自动跳到此前的观看进度(默认开)。
    /// ★ 与 Services/SettingsStore.cs 的同名属性是**读写契约**, 两处必须一致。
    /// </summary>
    public bool AutoResume { get; set; } = true;

    /// <summary>
    /// 是否记住播放器窗口大小**和位置**(默认开)。关闭后播放器每次都用 XAML 默认尺寸并居中.
    /// ★ 与 Services/SettingsStore.cs 的同名属性是**读写契约**, 两处必须一致。
    /// </summary>
    public bool RememberPlayerWindowSize { get; set; } = true;

    /// <summary>上次播放器窗口宽度(DIP; 0 = 尚未记录, 用默认 1280)。范围由 SettingsStore 夹取。</summary>
    public int PlayerWindowWidth { get; set; }

    /// <summary>上次播放器窗口高度(DIP; 0 = 尚未记录, 用默认 760)。范围由 SettingsStore 夹取。</summary>
    public int PlayerWindowHeight { get; set; }

    /// <summary>
    /// 上次播放器窗口左上角坐标(2026-11-07 新增; DIP; null = 尚未记录, 保持 CenterScreen 居中)。
    /// ★ 用可空接: 老配置没有这个字段 ⇒ null, 恰好就是"没记过"的语义, 不需要版本兼容;
    ///   也不能用 0 哨兵 —— (0,0) 是合法坐标(多屏布局常见), 会把没记过误判成记在原点。
    /// ★ 与 Services/SettingsStore.cs 的同名属性是**读写契约**, 两处必须一致。
    /// </summary>
    public int? PlayerWindowLeft { get; set; }

    /// <summary>上次播放器窗口左上角的 Y(DIP; null = 尚未记录, 保持居中)。契约同 <see cref="PlayerWindowLeft"/>。</summary>
    public int? PlayerWindowTop { get; set; }

    /// <summary>
    /// LibVLC 网络缓冲(毫秒)。控制流式播放的预读深度: 越小越省内存, 越大越抗网络抖动。
    /// 老配置没有这个字段 ⇒ 反序列化保持初始化值(1000, 与旧版全局值一致)。
    /// 手改成 0/越界值由 SettingsStore 读取时夹回 300~3000。
    /// ★ 与 Services/SettingsStore.cs 的同名属性是**读写契约**, 两处必须一致。
    /// </summary>
    public int NetworkCachingMs { get; set; } = 1000;

    /// <summary>
    /// 缓冲策略(true=自动按带宽挑值, false=手动用 NetworkCachingMs)。
    /// ★ 用可空类型接: JSON 反序列化分不清"老配置没这个字段"和"明确写了 false",
    ///   null(=字段不存在)要按"自动"处理 —— 那是新配置的默认(见 SettingsStore.AutoBufferPolicy)。
    /// ★ 与 Services/SettingsStore.cs 的同名属性是**读写契约**, 两处必须一致。
    /// </summary>
    public bool? AutoBufferPolicy { get; set; }

    /// <summary>
    /// 上次用的搜索类别(见 <see cref="ContentKind"/>: 0=视频 1=直播间 2=番剧 3=UP主)。
    /// 存 int 而不是枚举本身: JSON 里枚举按数字走, 老配置没有这个字段时默认 0(视频),
    /// 与搜索页的初始类别一致。★ 取值范围由 SettingsStore.SearchKind 夹取。
    /// </summary>
    public int LastSearchKind { get; set; }

    /// <summary>
    /// 旧字段(仅用于**读取**老配置, 兼容用)。
    ///
    /// ★★★ 2026-10-06 起"窗口背景材质"合并成了**一个**选择器(无/亚克力/云母/云母Alt),
    ///   真正的开关是 <see cref="AcrylicMaterial"/>(None = 关); 这个 bool 已降级为**派生值**
    ///   (由材质算出来, 落盘时同步写一份, 免得用户回退旧版本时状态丢失)。
    ///   ⇒ 代码里**别再按它判断开关**, 一律用 AcrylicMaterial。
    /// </summary>
    public bool AcrylicBackground { get; set; }

    /// <summary>
    /// 亚克力背景透明度——浅色主题(默认 30, 可调 0~50, 范围见 SettingsStore.AcrylicOpacityLightMin)。
    /// 数值越小越透明, 越大越不透明。只影响"页面底/侧边栏"这两层的透明度, 不影响卡片/浮层等实色块。
    /// ★ 默认值/范围与 Services/SettingsStore.cs 的同名属性是**读写契约**, 两处必须一致。
    /// </summary>
    public int AcrylicOpacityLight { get; set; } = 30;

    /// <summary>
    /// 亚克力背景透明度——深色主题(默认 50, 可调 30~70, 范围见 SettingsStore.AcrylicOpacityDarkMin)。
    /// 深色底本身就更暗, 默认稍不透明一档。
    /// </summary>
    public int AcrylicOpacityDark { get; set; } = 50;

    /// <summary>
    /// **窗口背景材质**(2026-10-06 起是唯一的开关 + 变体选择, 与云母/云母Alt 平级)。
    ///   · None(1)   = 不用任何材质(普通纯色背景) —— 这就是"关闭"
    ///   · Mica(2)   = 云母(不透明, 只采样壁纸)
    ///   · Acrylic(3)= 亚克力(半透明磨砂, 实时透出桌面)
    ///   · MicaAlt(4)= 云母 Alt
    ///
    /// ★★★ 为什么是**可空**的: 老配置里根本没有这个字段, 而枚举默认值恰好是 None ——
    ///   那样就分不清"老用户(该按旧 bool 推导)"和"新用户显式选了无材质"。
    ///   可空之后: null = 老配置(缺字段, 走迁移), 有值 = 新配置。
    /// ★ 枚举值直接等于 DWMSBT_*, 见 <see cref="BackdropMaterial"/>。
    /// </summary>
    public BackdropMaterial? AcrylicMaterial { get; set; }

    /// <summary>
    /// 卡片页每行显示几列(0 = 自动, 按窗口宽度算)。范围见 Helpers/CardWall.FixedColumnsMin/Max。
    /// ★ 作用于**所有**卡片页(首页/历史/收藏/搜索/分区/稍后再看/关注/个人空间), 它们共用同一套卡片墙。
    /// </summary>
    public int CardColumns { get; set; }

    /// <summary>
    /// 「动态」页右栏的排版: false = 动态卡(单列: 头像 + 发布时间 + 视频), true = 卡片墙(多列网格)。
    ///
    /// ★ 默认 false(动态卡): 用户 2026-10-15 要求"右侧不要全是视频卡片, 要有视频结合时间和
    ///   头像的卡片"; 卡片墙是留给他按需切回去的那一档, 而不是默认档。
    /// ★ 与 Services/SettingsStore.cs 的同名属性是**读写契约**, 两处必须一致。
    /// </summary>
    public bool FollowCardWall { get; set; }

    /// <summary>
    /// 软件缩放百分比(80~150, 默认 100)。把整个界面按比例放大/缩小, 与系统 DPI 无关。
    /// 范围见 Helpers/UiScale.MinPercent/MaxPercent。
    /// </summary>
    public int UiScalePercent { get; set; } = 100;

    /// <summary>
    /// 鼠标滚轮速度百分比(100 = 与系统默认一格的距离相同, 默认 250 = 2.5 倍)。
    /// 只放大"一格滑多远", 缓动曲线由 Helpers/WheelScroll 统一施加。
    /// 范围见 Helpers/WheelScroll.MinPercent/MaxPercent。
    /// ★ 与 Services/SettingsStore.cs 的同名属性是**读写契约**, 两处必须一致。
    /// </summary>
    public int WheelSpeedPercent { get; set; } = 250;

    /// <summary>
    /// 自定义**下载/缓存位置**(空串 = 用默认的系统「下载」文件夹)——
    /// 下载的视频(.mp4)与"图片另存为"都存到这里, 设置页那一行与离线缓存页显示的就是它。
    /// ★ 2026-10-11 由 <c>CacheRoot</c> 改名而来: 用户理解的"缓存位置"是"下载的视频存哪",
    ///   而封面图/短视频合流那些内部缓存用户看不见也不需要管, 已固定留在 %LocalAppData%。
    /// ★ 旧配置里的 <c>CacheRoot</c> 值**不迁移**(见 SettingsStore.Load 的兼容读取) ——
    ///   那条值过去指向用户数据目录下的 imgcache/vcache, 与"下载目录"语义完全不同,
    ///   拿来当下载目录会让已下好的视频"凭空消失"在设置页的显示里。
    /// ★ 与 Services/SettingsStore.cs 的同名属性是**读写契约**, 两处必须一致。
    /// </summary>
    public string DownloadDir { get; set; } = "";

    /// <summary>弹幕开关(全局, 播放器不再提供弹幕按钮)</summary>
    public bool DanmakuEnabled { get; set; } = true;

    /// <summary>弹幕显示区域占比(25~100, 单位: 画面高度百分比)</summary>
    public int DanmakuAreaPercent { get; set; } = 25;

    /// <summary>弹幕智能屏蔽(过滤超长/刷屏弹幕, 降低卡顿)</summary>
    public bool DanmakuSmartFilter { get; set; } = true;

    /// <summary>
    /// 彩色弹幕。接口给的颜色非白时按原色渲染; 关掉之后所有弹幕统一白色。
    /// 默认开 —— 彩色弹幕是 B 站的既定玩法, 不看颜色等于丢信息。
    /// </summary>
    public bool DanmakuColorful { get; set; } = true;

    /// <summary>
    /// 弹幕关键词屏蔽规则 —— **2026-11-05 已删除**。
    /// ★ 老配置里还留着这条字段, 反序列化会直接忽略(未知成员不报错), 不需要迁移代码。
    /// </summary>

    /// <summary>
    /// 弹幕是否带**半透明圆角胶囊底衬**(2026-11-05 新增)。
    ///
    /// ★ 背景: 2026-10-07 用户要求去掉弹幕的长方形底衬(纯文字更清爽), 底衬被整个删掉,
    ///   但纯白字压在明亮画面上会看不清 —— 这次把底衬作为**可选项**加回来, 默认开。
    /// ★ 与 <see cref="AppSettings.DanmakuPillBackground"/> 的**读写契约**, 两处必须一致。
    /// ★ 老配置缺这个字段 ⇒ 反序列化保持属性初始化值(自动落到默认(开)), 不需要版本兼容。
    /// </summary>
    public bool DanmakuPillBackground { get; set; } = true;

    /// <summary>
    /// 弹幕类型过滤(只显示勾选的类型)。四个独立开关而不是位标志: 界面上就是四个胶囊按钮,
    /// 位运算每次都要拆合容易写错; 存 JSON 时 bool 可读、老配置缺字段自动拿默认值。
    /// 默认全部 true —— 过滤是减法功能, 默认不该动用户能看到的弹幕。
    /// </summary>
    public bool DanmakuFilterScroll { get; set; } = true;

    /// <summary>固定弹幕(顶部/底部)</summary>
    public bool DanmakuFilterFixed { get; set; } = true;

    /// <summary>彩色弹幕(按颜色过滤; 与 DanmakuColorful 的"是否上色"是两件事)</summary>
    public bool DanmakuFilterColorful { get; set; } = true;

    /// <summary>高级弹幕(mode 7 高级 / 8 代码 / 9 BAS)</summary>
    public bool DanmakuFilterAdvanced { get; set; } = true;

    /// <summary>
    /// 弹幕不透明度(20~100)。★ 与 Services/SettingsStore.cs 的同名属性是**读写契约**,
    /// 两处必须一致 —— 老配置缺这个字段时靠属性初始化值拿到 80, 不需要版本兼容。
    /// </summary>
    public int DanmakuOpacityPercent { get; set; } = 80;

    /// <summary>弹幕字号倍率(50~300)。与 SettingsStore 同名属性是读写契约。</summary>
    public int DanmakuFontScalePercent { get; set; } = 100;

    /// <summary>弹幕速度档位(0 最慢 ~ 4 最快)。与 SettingsStore 同名属性是读写契约。</summary>
    public int DanmakuSpeedLevel { get; set; } = 2;

    // ------------------------------------------------------------ 列表屏蔽(FilterService)

    /// <summary>
    /// 按标题屏蔽的关键词(手打, 包含匹配)。多个词用空格 / 逗号分隔。
    /// ★ 与 Services/SettingsStore.cs 的同名属性是**读写契约**, 两处必须一致。
    /// </summary>
    public string FilterTitleKeyword { get; set; } = "";

    /// <summary>按 UP 主屏蔽的关键词(手打, 包含匹配)。与 SettingsStore 同名属性是读写契约。</summary>
    public string FilterUpKeyword { get; set; } = "";

    /// <summary>
    /// 卡片右键"屏蔽此 UP 主"加的一键规则。精确同名 + UID(见 FilterService)。
    /// 存 Name/Mid 两个字段而不是一个拼接串: UP 改名后 UID 仍然有效,
    /// 面板里又需要昵称给人看。★ 与 SettingsStore 同名属性是读写契约。
    /// </summary>
    public List<BlockedUpEntry> FilterBlockedUps { get; set; } = new();

    /// <summary>卡片右键"屏蔽此标题"加的一键规则(标题完全相同才命中)。</summary>
    public List<string> FilterBlockedTitles { get; set; } = new();

    /// <summary>
    /// 默认画质(qn)。用户在设置页**开始播放前**预先选定的清晰度档位,
    /// 每次起播都用它取流; 该档拿不到时自动降级(见 ApiClient.GetPlayUrlAsync)。
    /// 默认取 QualityPreference.DefaultQn(1080P) —— 与新增本设置项之前播放器里
    /// 写死的 qn=80 一致, 老用户升级后起播画质不变。
    /// </summary>
    public int PreferredQualityQn { get; set; } = QualityPreference.DefaultQn;

    /// <summary>
    /// 首页「推荐」tab 的算法来源(网页版 / App 端)。默认网页版。
    /// 老配置缺这个字段时保持初始化值, 自动落到 Web。
    /// </summary>
    public RecommendSource RecommendSource { get; set; } = RecommendSource.Web;

    /// <summary>
    /// 是否启用"跳过赞助片段"(SponsorBlock)。
    ///
    /// ★ **默认关**(用户 2026-09-27 拍板): 开启后每条视频的 BV 号都会被发到第三方服务器
    /// (www.bsbsb.top), 等于把"你在看什么"告诉别人。这是真实的隐私代价, 必须由用户
    /// 明确选择, 不能默认替他送出去。
    ///
    /// (属性初始化就是 false —— 老配置缺这个字段时会保持初始化值, 自动落到"关"。)
    /// </summary>
    public bool SponsorBlockEnabled { get; set; }

    /// <summary>
    /// 要跳过的片段类别(逗号分隔, 存服务端的 category 原值)。
    /// 默认只勾"赞助广告" —— 那是这个功能的立身之本; 片头片尾属于个人口味, 交给用户自己开。
    /// 存字符串而不是位标志: 类别以后可能增删, 位标志会像枚举重排那样错位。
    /// </summary>
    public string SponsorBlockCategories { get; set; } = SponsorCategories.Sponsor;

    /// <summary>
    /// 主题色(强调色)。空串 = 用默认的 B 站粉(见 <see cref="Helpers.ThemeService.DefaultAccent"/>),
    /// 非空时是 `#RRGGBB` 形式。
    ///
    /// 为什么默认空串而不是直接存 "#FB7299": 默认色以后可能会调, 空串表示"跟着默认走",
    /// 老配置(没这个字段 → 空串)自动落到默认值, 用户显式选过的颜色才被记住。
    /// </summary>
    public string AccentColor { get; set; } = "";

    /// <summary>
    /// 界面字体(2026-11-14 新增)。空串 = 默认字体栈; 普通字体名 = 系统已安装字体;
    /// "file:目录名" = 导入的字体文件(存在 %LocalAppData%\BiliDesk\fonts 下)。
    /// 存储语义与清洗见 Helpers.UiFont。
    /// ★ 与 Services/SettingsStore.cs 的同名属性是**读写契约**, 两处必须一致。
    /// 老配置缺这个字段 ⇒ 空串 ⇒ 默认字体, 不需要版本兼容。
    /// </summary>
    public string UiFontFamily { get; set; } = "";

    /// <summary>
    /// 关闭主窗口时最小化到托盘(默认 true)。
    /// 关掉之后点右上角关闭键就是真的退出程序, 不再收到托盘里。
    /// 属性初始化就是 true —— 老配置缺这个字段时会保持初始化值, 于是"默认打开"自动成立。
    /// </summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>
    /// 启动后自动检查更新(默认 true)。
    /// 检查是匿名的 GitHub Releases 请求, 不带任何账号信息; 关掉后仍可在设置页手动检查。
    /// 老配置缺这个字段时保持初始化值 ⇒ "默认打开"自动成立, 不需要版本号兼容。
    /// </summary>
    public bool AutoCheckUpdate { get; set; } = true;

    /// <summary>
    /// 用户点过「跳过此版本」的版本号(如 "1.2.0")。空 = 没跳过任何版本。
    /// 只对**自动**检查生效: 手动点「检查更新」说明用户就是想看, 一样会弹窗。
    /// </summary>
    public string SkippedVersion { get; set; } = "";

    /// <summary>
    /// 免登录 1080P(默认 true)。
    ///
    /// 未登录时在取流请求里补上 try_look + 四个 dm_img_* 设备指纹参数, 把匿名可见的
    /// 清晰度从 360P/480P 提到 720P/1080P —— 做法与参数依据见 ApiClient.ApplyTryLook。
    ///
    /// 默认 true 与 PiliPlus 一致: 只影响"未登录"这一种状态, 已登录用户按账号等级取流,
    /// 这个开关对它们没有任何作用(ApplyTryLook 会直接跳过)。
    /// 老配置缺这个字段时保持初始化值 ⇒ "默认打开"自动成立, 不需要版本号兼容。
    /// </summary>
    public bool NoLogin1080P { get; set; } = true;

    /// <summary>
    /// 视频线路(CDN)选择策略。默认**跟随服务端**(2026-11-05 改; 旧默认是自动测速) ——
    /// 自动测速虽然能挑到瞬时吞吐最高的线路, 但换 host 后部分线路的持续吞吐/抖动更差,
    /// 用户侧表现为播放中卡顿反而更多; 跟随服务端用接口原样返回的线路最稳。
    /// 设置页仍可切回自动测速 / 手动指定。详见 Services/CdnService.cs。
    /// </summary>
    public CdnSelectMode CdnMode { get; set; } = CdnSelectMode.ServerDefault;

    /// <summary>
    /// "线路默认值迁移"标记(2026-11-05)。旧配置里 CdnMode 都是 Auto(旧默认),
    /// 升级后只迁移一次到 ServerDefault; 之后用户手动选回 Auto 不会被再次改掉。
    /// false/缺失 = 尚未迁移。
    /// </summary>
    public bool CdnModeMigratedToServer { get; set; }

    /// <summary>
    /// 手动模式下选定的 CDN Id(见 Models/CdnOption.All)。
    /// 存 Id 而不是 host: B 站换域名时 Id 不变, 老配置才不会失效。
    /// 读配置时用 CdnOption.ById 校验, 已下线的 Id 会被清成空串(退回自动)。
    /// </summary>
    public string CdnManualId { get; set; } = "";

    /// <summary>
    /// 屏蔽 PCDN(点对点分发)。默认开 —— PCDN 拿其他用户的带宽做节点, 速度不稳定;
    /// 实测同一档清晰度常规 CDN 吞吐高一倍以上。
    /// </summary>
    public bool BlockPcdn { get; set; } = true;

    /// <summary>
    /// 是否已同意首次启动的免责声明。
    /// 默认 false —— 老版本升级上来的用户也会被问一次, 这是有意的:
    /// 声明里有"账号风控/数据丢失风险自担"这类实质内容, 让所有人在同一份文本上确认一次更稳妥。
    /// </summary>
    public bool DisclaimerAccepted { get; set; }

    /// <summary>
    /// 用户同意的是**第几版**声明(对应 <see cref="Helpers.DisclaimerText.Version"/>)。
    ///
    /// 单独记版本号而不是只留一个 true/false: 声明正文以后还会改, 改完要再让用户确认一次 ——
    /// 否则"你事后能翻到的, 就是你当初点同意时看到的那一份"这句话就不成立了。
    /// 老配置里没有这个字段(值为 0), 兼容处理见 SettingsStore.Load。
    /// </summary>
    public int DisclaimerVersion { get; set; }
}

