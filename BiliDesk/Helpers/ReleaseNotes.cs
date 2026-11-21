using System;

namespace BiliDesk.Helpers;

/// <summary>
/// Release 正文(发布脚本写的那段 Markdown)的清洗规则。
///
/// ★ 为什么需要: GitHub Release 的 body 固定是"## x.y.z 更新内容 + 分组列表 + --- + 下载/SHA256",
///   而站点生成脚本(gen_changelog.py)会把重复标题与下载段裁掉才上页面 ——
///   应用里如果不裁, 用户就会在「关于 → 更新日志」和更新弹窗里看到一串
///   `BiliDesk-1.8.1-setup.exe` 与 64 位校验和, 真正的更新内容反而被挤到上面一点点。
///
/// ★ 裁剪只认"行首标记": 标题以 # 开头、下载段以 `### 下载` / `**下载**` 开头。
///   正文里偶尔出现的"下载"二字(比如功能说明)不会被误伤。
/// </summary>
public static class ReleaseNotes
{
    /// <summary>
    /// 清洗一份 Release 正文: 去掉开头的重复版本标题、末尾的下载与校验和段。
    /// </summary>
    /// <param name="markdown">Release body 原文</param>
    /// <param name="version">版本号(不带 v), 用于识别重复标题; 可为空</param>
    public static string Clean(string? markdown, string? version = null)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";

        var text = markdown
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .TrimStart('\uFEFF')
            .Trim();

        var lines = text.Split('\n');

        // 1) 开头第一个非空行是"## 1.8.1 更新内容"这类重复标题 ⇒ 跳过它
        var start = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            start = IsDuplicateTitle(line, version) ? i + 1 : i;
            break;
        }

        // 2) 从下载段标记起整段不要(它后面只有安装包名与 SHA256)
        var end = lines.Length;
        for (var i = start; i < lines.Length; i++)
        {
            if (IsDownloadMarker(lines[i].Trim()))
            {
                end = i;
                break;
            }
        }

        // 3) 尾部残留的空行与分隔线(下载段前面的 ---)一并去掉
        while (end > start)
        {
            var line = lines[end - 1].Trim();
            if (line.Length == 0 || IsRule(line)) end--;
            else break;
        }

        return string.Join("\n", lines[start..end]).Trim();
    }

    /// <summary>
    /// 是否"重复的版本标题"。判据放宽到"含『更新内容』"——
    /// 发布脚本可能写成 `## 1.8.1 更新内容`、`## BiliDesk v1.8.1 更新内容`, 甚至英文标题。
    /// </summary>
    private static bool IsDuplicateTitle(string trimmedLine, string? version)
    {
        if (trimmedLine.Length == 0 || trimmedLine[0] != '#') return false;
        var text = trimmedLine.TrimStart('#').Trim();
        if (text.Length == 0) return false;
        if (text.Contains("更新内容", StringComparison.Ordinal)) return true;
        return !string.IsNullOrEmpty(version)
               && text.StartsWith(version, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 下载段标记: `### 下载` / `## 下载` / `**下载**` / `下载`(标题或加粗独占一行)。
    /// 把开头的 # 与 * 都抹掉再比, 四种写法一次覆盖。
    /// </summary>
    private static bool IsDownloadMarker(string trimmedLine)
    {
        if (trimmedLine.Length == 0) return false;
        var text = trimmedLine.TrimStart('#', '*', ' ').TrimEnd('*', ' ').Trim();
        return text.Equals("下载", StringComparison.Ordinal);
    }

    private static bool IsRule(string trimmedLine)
    {
        if (trimmedLine.Length < 3) return false;
        var c = trimmedLine[0];
        if (c != '-' && c != '*' && c != '_') return false;
        foreach (var ch in trimmedLine)
            if (ch != c) return false;
        return true;
    }
}
