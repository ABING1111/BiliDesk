using System;
using System.Collections.Generic;

namespace BiliDesk.Helpers;

/// <summary>声明里的一行。Level: 0=大标题, 1=小节标题, 2=正文, 3=列表项</summary>
public readonly record struct DisclaimerLine(int Level, string Text);

/// <summary>
/// 免责声明 / 非官方声明的正文。
///
/// 原文是 Markdown, 这里以**逐字保留**的方式存着, 只做最小的行级标记解析
/// (标题级别 / 列表项), 不改动任何一个字 —— 这是要给人看并要人"同意"的法律文本,
/// 顺手润色措辞是不合适的。
///
/// 剩下的方括号占位符 (`[你的邮箱]`) 是原文里就有的, 也原样保留,
/// 需要填的时候在下面这段原文里改即可。
/// </summary>
public static class DisclaimerText
{
    /// <summary>
    /// 正文版本号。**改动下面这段正文时把它 +1** ——
    /// SettingsStore 会据此判断"用户同意过的那一份"和"现在这一份"是不是同一份,
    /// 不是的话启动时会重新弹一次确认。
    /// 1 = 首版; 2 = 去掉"开源"表述、把"最新版本以仓库地址为准"改成"以作者 B 站发布为准"。
    /// </summary>
    public const int Version = 2;
    private const string Markdown =
"""
# 免责声明

> 本项目为非官方、非商业、仅供个人学习研究的技术项目。使用前请仔细阅读。若不同意，请勿使用。

## 1. 非官方与无授权

BiliDesk（以下简称“本项目”）是第三方开发者个人发起的实验性项目，与哔哩哔哩（Bilibili，简称“B站”）及其关联公司、运营方没有任何隶属、代理、合作、赞助、授权或认可关系。本项目未获得 B 站官方授权。

“哔哩哔哩”“Bilibili”“B站”等商标、标识、名称归其各自权利人所有。本项目仅在描述性意义上提及，不主张任何商标权或其他权利。

## 2. 目的与性质

本项目仅供个人学习、技术研究、交流与自用，不用于商业目的。禁止将本项目用于销售、出租、预装、捆绑、广告引流、代下载、代刷、商业运营等任何商业或非法用途。

## 3. 内容与版权

本项目不制作、不存储、不上传、不传播任何音视频、弹幕、评论、用户资料等内容。通过本项目访问的所有内容均来自第三方平台，版权归原权利人所有。开发者不拥有相关内容权利，也不对内容的合法性、准确性或完整性负责。

## 4. 账号、Cookie 与隐私

本项目可能通过 WebView2 获取用户登录 Cookie，并仅保存在用户本机，用于以用户身份调用相关接口。开发者不会主动收集、上传、出售或公开用户账号、密码、Cookie 等敏感信息。

用户应自行保护账号与设备安全。因使用第三方客户端导致的账号风控、限制、封禁、数据丢失等风险，由用户自行承担。

## 5. 禁止用途

用户不得利用本项目：

- 破解、绕过或规避会员、付费、DRM、地区限制、风控、验证码等；
- 批量抓取、爬虫、下载、录播、刷量、代刷或干扰平台正常服务；
- 侵犯他人著作权、商标权、隐私权、个人信息权益或其他合法权益；
- 从事任何违反法律法规、平台规则或公序良俗的行为。

## 6. 广告与界面清理

本项目如包含界面清理、隐藏推荐/广告等个性化功能，仅为个人视觉偏好，不代表开发者鼓励或支持破坏平台商业模式。用户应自行判断并遵守平台规则；如平台不允许，请停止使用相关功能。

## 7. 无担保

本项目按“现状”提供，不保证可用性、稳定性、安全性、准确性、无错误或持续更新。接口可能随时失效，开发者不承担由此产生的任何责任。

## 8. 责任限制

在适用法律允许的最大范围内，开发者不对因使用或无法使用本项目导致的任何直接、间接、附带、特殊或后果性损失负责，包括但不限于账号封禁、数据丢失、设备损坏、法律纠纷、利润损失等。

本声明不排除依法不能排除或限制的责任。

## 9. 第三方组件

本项目可能使用 LibVLCSharp、VideoLAN.LibVLC.Windows、Microsoft.Web.WebView2 等第三方组件，其版权与许可证归各自权利人所有，使用须遵守相应许可证。

## 10. 侵权通知与配合

如任何权利人认为本项目名称、代码、文档、图标、功能或其他内容侵犯其合法权益，请通过 [你的邮箱] 联系。开发者将在收到有效通知后及时核实，并采取删除、修改、停止分发或下架项目等措施。

开发者无意侵犯任何第三方权益。

## 11. 声明更新

本声明可能随项目更新而调整，最新版本以作者b站发布为准。使用本项目即表示你已阅读、理解并同意本声明；若不同意，请立即停止使用。
""";

    private static IReadOnlyList<DisclaimerLine>? _lines;

    /// <summary>解析好的正文行(首次访问时解析并缓存)</summary>
    public static IReadOnlyList<DisclaimerLine> Lines => _lines ??= Parse();

    private static IReadOnlyList<DisclaimerLine> Parse()
    {
        var list = new List<DisclaimerLine>();
        foreach (var raw in Markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("## ", StringComparison.Ordinal))
                list.Add(new DisclaimerLine(1, line[3..].Trim()));
            else if (line.StartsWith("# ", StringComparison.Ordinal))
                list.Add(new DisclaimerLine(0, line[2..].Trim()));
            else if (line.StartsWith("- ", StringComparison.Ordinal))
                list.Add(new DisclaimerLine(3, "· " + line[2..].Trim()));
            else if (line.StartsWith("> ", StringComparison.Ordinal))
                list.Add(new DisclaimerLine(2, line[2..].Trim()));
            else
                list.Add(new DisclaimerLine(2, line));
        }
        return list;
    }
}
