namespace BiliDesk.Models;


/// <summary>字幕轨道(外挂字幕 / AI 字幕共用)。对应 /x/player/wbi/v2 的 subtitle.subtitles。</summary>
public class SubtitleTrack
{
    /// <summary>语言代码, 如 "zh-CN" / "en-US"</summary>
    public string Lan { get; set; } = "";

    /// <summary>界面显示名, 如 "中文(自动生成)" / "英文"</summary>
    public string LanDoc { get; set; } = "";

    /// <summary>轨道类型。1 = AI 字幕, 其余(0/缺失)为视频自带(UP 主上传/外挂)字幕。AI 判定的主判据。</summary>
    public int Type { get; set; }

    /// <summary>AI 字幕子类型。1 = AI 翻译, 其他值为普通 AI 识别。参与 AI 判定的兜底。</summary>
    public int? AiType { get; set; }

    /// <summary>AI 字幕状态(0 = 关闭, 2 = 开启)。参与 AI 判定的兜底 —— 实测开启态是 2。</summary>
    public int? AiStatus { get; set; }

    /// <summary>
    /// 是否 AI 字幕。**全项目唯一的判定口径**(接口注释与本属性必须同口径, 评审意见第 8 条)。
    /// 主判据: type == 1。
    /// 兜底: ai_status == 2(实测 AI 轨开启态就是 2, 不是 1)或 ai_type == 1(AI 翻译)也算 AI。
    /// </summary>
    public bool IsAi => Type == 1 || AiStatus == 2 || AiType == 1;

    /// <summary>字幕 JSON 下载地址(body 数组)</summary>
    public string SubtitleUrl { get; set; } = "";

    /// <summary>选轨列表/按钮显示名。AI 轨统一追加 " · AI"(不再区分 AI 识别与 AI 翻译, 避免同一弹层里出现两种后缀), 视频自带字幕保持原名。</summary>
    public string DisplayName
    {
        get
        {
            var label = string.IsNullOrWhiteSpace(LanDoc) ? Lan : LanDoc;
            if (!IsAi) return label;
            return label + " · AI";
        }
    }
}

/// <summary>一条字幕 cue(时间区间 + 文本)。对应字幕 JSON 的 body 元素。</summary>
public class SubtitleCue
{
    /// <summary>开始时间(秒)</summary>
    public double From { get; set; }

    /// <summary>结束时间(秒)</summary>
    public double To { get; set; }

    /// <summary>字幕文本</summary>
    public string Content { get; set; } = "";
}
