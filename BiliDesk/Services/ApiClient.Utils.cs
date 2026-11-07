using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiliDesk.Helpers;
using BiliDesk.Models;

namespace BiliDesk.Services;

/// <summary>
/// 哔哩哔哩 API 客户端: 带浏览器 UA、Cookie、WBI 签名与风控降级逻辑。
/// ★ 2026-11-07 由单文件拆成 17 个 partial 分片(纯移动、零逻辑改动),
///   分片清单与动机见 ApiClient.Core.cs 头部说明。
/// </summary>
public partial class ApiClient
{
    // ---------------------------------------------------------------- 工具

    /// <summary>
    /// 同一档清晰度下, 各编码的优先级(数字越大越优先)。
    ///
    /// hevc 排最前是有实测依据的: 同一档 qn 下 hevc 的码率约为 avc 的一半
    /// (4K 档 7.03 vs 12.60 Mbps), 而 VLC 的硬解对两者一视同仁 —— 同样的画质少搬一半数据,
    /// 起播与跳转的缓冲自然短。
    /// av01 放最后: 体积更小, 但硬解支持面窄(软解 4K AV1 基本跑不动), 不主动选。
    /// </summary>
    private static int CodecRank(string codec)
    {
        if (codec.StartsWith("hev", StringComparison.OrdinalIgnoreCase) ||
            codec.StartsWith("hvc", StringComparison.OrdinalIgnoreCase)) return 3;   // hev1 / hvc1
        if (codec.StartsWith("avc", StringComparison.OrdinalIgnoreCase)) return 2;   // avc1
        if (codec.StartsWith("av01", StringComparison.OrdinalIgnoreCase)) return 1;  // av01
        return 0;
    }

    /// <summary>
    /// 线路选择已迁到 <see cref="CdnService"/>(2026-10-03)。
    ///
    /// 以前这里是两个静态方法(IsPcdnHost / PickFastestHost): 只按"域名里有没有 mcdn"
    /// 换一下顺序。现在换成 CdnService, 因为它要额外负责:
    ///   · 自动测速挑最快(各家 CDN 实测吞吐能差 4 倍)、手动指定、跟随服务端三种策略;
    ///   · 认出**伪装域名**的 PCDN(实测 `mv0bz14m.edge.mountaintoys.cn` 带 os=mcdn,
    ///     只看域名会漏 —— 旧规则的漏洞);
    ///   · 失败回退与测速缓存。
    /// 逻辑集中在一处, 设置页改设置 -> 下次取流生效, 不需要动这里。
    /// </summary>

    private static string GetStr(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) &&
           (v.ValueKind == JsonValueKind.String || v.ValueKind == JsonValueKind.Number)
            ? v.ToString() : "";

    private static int GetInt(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : 0;

    private static long GetLong(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64() : 0;

    /// <summary>
    /// 浮点也能取的 int/long(2026-10-31 修"部分视频评论区整页解析失败")。
    ///
    /// ★ 某些评论字段(实测 like)会被服务端下发成 **小数**(如 12.0):
    ///   GetInt/GetLong 对浮点调 GetInt32()/GetInt64() 会抛
    ///   "One of the identified items was in an invalid format",
    ///   而 ParseComment 外层只有一个大 try —— 一条评论抛异常, **整页评论全部丢掉**。
    ///   数值字段一律走这里, 按"先整后浮再截断"取值。
    /// </summary>
    private static int GetIntSafe(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return 0;
        if (v.TryGetInt32(out var i)) return i;
        return v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 0;
    }

    private static long GetLongSafe(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return 0;
        if (v.TryGetInt64(out var l)) return l;
        return v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : 0;
    }

    /// <summary>
    /// 数字**或数字字符串**都能取(动态接口把 mid/rid 当字符串下发: "mid": "1255510247")。
    ///
    /// ★ 为什么必须单独有一个: 动态接口里 `author.mid` 实测是**字符串**,
    ///   走 GetLong 会得 0 —— 表现为"点头像进不了主页""左栏未读角标永远不亮"(都按 mid 匹配)。
    ///   只给确认会用字符串下发的字段用它, 别的字段继续用 GetLong, 免得把"脏字符串当数字"
    ///   这种本该暴露的问题也一并咽下去。
    /// </summary>
    private static long GetLongLoose(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetInt64();
        if (v.ValueKind == JsonValueKind.String &&
            long.TryParse(v.GetString(), out var n)) return n;
        return 0;
    }

}
