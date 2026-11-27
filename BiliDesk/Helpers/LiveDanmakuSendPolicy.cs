using System.Text;

namespace BiliDesk.Helpers;

/// <summary>
/// 直播间发送弹幕的纯逻辑(2026-11-28 新增): 正文归一化 + 接口错误码转人话。
///
/// ★ 为什么单独抽出来:
///   发送接口对正文有硬限制(普通用户 20 字), 超长会被服务端整条拒绝;
///   而"输入框 MaxLength 已经卡了 20"并不够 —— 粘贴、输入法上屏、快捷键发送
///   都可能绕过控件层。归一化放到纯函数里, 单测可以直接穷举换行/超长/emoji 边界,
///   不必真去连直播间。
///
/// ★ 直播 qn / 颜色 / 模式都不在这里处理: 基础版固定白色 + 滚动,
///   以后要加颜色/位置时再扩展参数, 不要把这些常量散到界面代码里。
/// </summary>
public static class LiveDanmakuSendPolicy
{
    /// <summary>
    /// 直播弹幕正文上限。
    /// ★ B 站默认是 20 字; 只有拿过"小有所成"成就的账号才放宽到 30 字。
    ///   这里按最保守的 20 卡, 避免普通账号发长弹幕被服务端拒绝。
    /// </summary>
    public const int MaxTextLength = 20;

    /// <summary>
    /// 把用户输入整理成可以直接发送的正文:
    ///   · 去掉首尾空白;
    ///   · 换行/制表符折成单个空格(直播弹幕是单行, 原样发出去会被服务端截断或拒绝);
    ///   · 超过 <see cref="MaxTextLength"/> 时截断, 且**不截出半个代理对** ——
    ///     单独的高位代理会让 JSON/XML 序列化出问题(表情变问号甚至抛异常)。
    /// </summary>
    public static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Trim())
        {
            if (ch is '\r' or '\n' or '\t')
            {
                // 连续空白只留一个空格, 避免 "a\r\n\r\nb" 变成 "a   b"
                if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                continue;
            }
            sb.Append(ch);
        }

        var normalized = sb.ToString().TrimEnd();
        if (normalized.Length <= MaxTextLength) return normalized;

        // ★ 按 UTF-16 长度截断时, 如果第 20 个 char 正好是代理对的前半,
        //   再往前退一格, 宁可少一个字也不产生非法字符。
        var cut = MaxTextLength;
        if (char.IsHighSurrogate(normalized[cut - 1])) cut--;
        return normalized[..cut];
    }

    /// <summary>
    /// 把发送接口的业务码转成用户能看懂的话。
    ///
    /// ★ 错误码来自直播发送接口(/msg/send)的实测与社区文档:
    ///   -101 未登录 / -111 csrf 失效 / 10031 频率过快 / 10032 弹幕冻结 /
    ///   10065 房管禁言 / 10066 系统禁言 / 1003212 超长。
    ///   其余一律退回服务端 message, 没有 message 才用 code 兜底 ——
    ///   不要把裸数字直接甩给用户。
    /// </summary>
    public static string DescribeError(int code, string? message)
    {
        var fallback = string.IsNullOrWhiteSpace(message)
            ? $"发送失败 (code {code})"
            : message!.Trim();

        return code switch
        {
            -101 => "请先登录后再发送弹幕",
            -111 => "登录状态已失效, 请重新登录",
            -400 => "弹幕内容不合法, 请修改后再试",
            -403 => "当前账号无权在该直播间发送弹幕",
            -412 => "当前网络请求过于频繁, 请稍后再试",
            10030 => "发送频率过快或账号存在风险, 请稍后再试",
            10031 => "发送频率过快, 请稍后再试",
            10032 => "弹幕功能已被冻结",
            1003212 => "弹幕内容超出长度限制",
            10065 => "你已被房间管理员禁言",
            10066 => "你已被系统管理员禁言",
            _ => fallback
        };
    }
}
