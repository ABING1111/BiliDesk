namespace BiliDesk.Models;

/// <summary>
/// 「默认画质」可选项 —— 用户在设置页**开始播放前**选定的清晰度档位。
/// ★ 2026-10-04 新增。
///
/// 与 <see cref="Services.ApiClient.QnToLabel"/> 的分工(两者别混):
///   · <see cref="Services.ApiClient.QnToLabel"/> 是**显示名**转换 —— 拿到接口返回的真实
///     id 之后把它翻成给用户看的中文(含 杜比视界/HDR/智能修复 这些本类不提供的档位);
///   · 本类是**可选档位表** —— 只列出"值得让用户预先挑"的那些档, 顺序即下拉框顺序。
/// 所以本表刻意比 QnToLabel 短: 杜比视界/杜比全景声/HDR 对第三方客户端基本拿不到,
/// 列进下拉框只会让用户选一个永远降级的档位。
///
/// ★★ 「自动」为什么不能借用某个真实 qn(比如 127):
///   `QnToLabel(127)` 在**真实**流里是"杜比视界"(8K 档位)。把 127 当"自动"用会立刻撞车 ——
///   起播失败 / 该视频没有可用档位(actualQn == 0)时按钮上会把"自动"显示成"杜比视界",
///   而用户根本没有 8K 权限。所以「自动」用**表外的哨兵值 0** 表示, 真实请求的 qn 由
///   <see cref="RequestQn"/> 单独换算。
/// </summary>
public static class QualityPreference
{
    /// <summary>一个可选档位: qn = 落盘用的取值, Label = 下拉框里显示的文字。</summary>
    public sealed record Choice(int Qn, string Label);

    /// <summary>
    /// 「自动(最高可用)」的哨兵值。**不是** B 站的真实清晰度 id —— 真实 id 全是正数
    /// (16/32/64/80/112/120/127...), 0 不可能是其中之一, 所以拿它当"未指定"是安全的。
    /// 它只用于**落盘与显示**; 真正发给接口的 qn 见 <see cref="RequestQn"/>。
    /// </summary>
    public const int AutoQn = 0;

    /// <summary>
    /// 请求「最高可用」时实际发给 playurl 的 qn。
    ///
    /// 取 127 是因为 B 站清晰度 id 的上界就在这一档附近, 而取流逻辑的选流规则是
    /// "不超过请求 qn 的**最高**一条" —— 于是"请求 127"等价于"把我能拿到的最高档给我",
    /// 拿不到高档时会自然落到拥有的最高档(见 ApiClient.GetPlayUrlAsync 的选流与降级分支)。
    /// 这与"qn=0 交给服务端默认"不同: 那个拿到的是**默认**档(常见 1080P)而不是最高档。
    /// </summary>
    public const int HighestQn = 127;

    /// <summary>
    /// 全部可选档位。**顺序即设置页下拉框的显示顺序**(从高到低)。
    /// 加档位前先确认取流侧真的能拿到它 —— 拿不到的档位就是"永远降级", 属于骗用户。
    /// </summary>
    public static readonly Choice[] All =
    {
        new(AutoQn, "自动 (最高可用)"),
        new(120, "4K"),
        new(116, "1080P 60帧"),
        new(112, "1080P 高码率"),
        new(80,  "1080P"),
        new(74,  "720P 60帧"),
        new(64,  "720P"),
        new(32,  "480P"),
        new(16,  "360P"),
    };

    /// <summary>
    /// 默认档位 = 1080P(80)。
    /// ★ 与改动之前播放器写死的 <c>_currentQn = 80</c> 完全一致 —— 新增这个设置项
    ///   不能悄悄改变老用户的起播画质, 否则"加了个选项"会变成"画质被改了"。
    ///   (所以默认**不是**「自动」: 那会让老用户的起播行为跟着变。)
    /// </summary>
    public const int DefaultQn = 80;

    /// <summary>这一档是否在本表里(读配置时用它挡掉手改/已下线/串到别的字段的值)</summary>
    public static bool IsKnown(int qn) => All.Any(c => c.Qn == qn);

    /// <summary>
    /// 把任意读到的值折算成表内的合法档位: 不认识的值一律落回默认档, 绝不让它流进取流请求。
    /// </summary>
    public static int Normalize(int qn) => IsKnown(qn) ? qn : DefaultQn;

    /// <summary>
    /// 落盘/显示的档位 → **实际发给接口**的 qn。
    /// 「自动」换算成 <see cref="HighestQn"/>, 其余原样(它们本来就是真实 id)。
    /// </summary>
    public static int RequestQn(int qn) =>
        Normalize(qn) == AutoQn ? HighestQn : Normalize(qn);

    /// <summary>档位对应的显示文字(下拉框/按钮显示用; 未知值先 Normalize 再取, 不会返回空)</summary>
    public static string LabelOf(int qn) =>
        All.First(c => c.Qn == Normalize(qn)).Label;

    /// <summary>
    /// 档位的**短**显示文字, 给播放器控制栏那个小胶囊按钮用。
    ///
    /// 为什么不直接用 <see cref="LabelOf"/>: 下拉框里「自动 (最高可用)」那串文字是为
    /// **解释**服务的, 放进控制栏那个只有十几像素高的小按钮里会把它撑得很宽, 挤到旁边的
    /// 倍速/弹幕按钮(控制栏是一整行, 宽度有限)。短标签对"当前在自动挑档"这件事同样说得清。
    /// 其余档位本来就是短词(1080P / 4K / 1080P 60帧), 与下拉框共用同一个即可。
    /// </summary>
    public static string ShortLabelOf(int qn) =>
        Normalize(qn) == AutoQn ? "自动" : LabelOf(qn);

    /// <summary>
    /// 显示文字反查档位(下拉框 SelectedItem 是字符串, 回写时按文字找回 qn)。
    /// 找不到(理论上不可能, 下拉框数据源就是本表)落回默认档而不是抛异常 ——
    /// 设置页是 UI 路径, 这里崩一下就是"点一下下拉框整个页面炸"。
    /// </summary>
    public static int QnOf(string? label) =>
        All.FirstOrDefault(c => c.Label == label)?.Qn ?? DefaultQn;
}
