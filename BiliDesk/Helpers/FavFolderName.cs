namespace BiliDesk.Helpers;

/// <summary>
/// 收藏夹名称的客户端前置校验(2026-11-19 新增"新建收藏夹")。
///
/// ★ 这里**只做界面层的"太明显"的拦截**(空串 / 超长), 真正的裁决仍在服务端:
///   重名、敏感词、收藏夹数量上限都不该在客户端猜 —— 猜错就是"输入框明明合法,
///   点创建却被本地说不行"。所以服务端 message 原样透出(见 FavFolderActions)。
/// ★ 上限取 20 字, 与 B 站网页端新建收藏夹输入框的限制一致。
///   用 <see cref="string.Length"/>(UTF-16 码元)计数, 与输入框 MaxLength 的语义对齐 ——
///   两边若用不同口径(比如按"字素簇"数), 会出现"计数显示 20/20, 创建按钮却灰着"的鬼故事。
/// </summary>
public static class FavFolderName
{
    /// <summary>名称上限(B 站网页端也是 20)</summary>
    public const int MaxLength = 20;

    /// <summary>
    /// 规整 + 校验。成功时 <paramref name="title"/> 是 Trim 之后真正要提交的文本;
    /// 失败时 <paramref name="error"/> 是可以直接显示给用户的短句。
    /// </summary>
    public static bool TryNormalize(string? raw, out string title, out string? error)
    {
        title = (raw ?? "").Trim();

        if (title.Length == 0)
        {
            error = "收藏夹名称不能为空";
            return false;
        }
        if (title.Length > MaxLength)
        {
            error = $"收藏夹名称最多 {MaxLength} 个字";
            return false;
        }

        error = null;
        return true;
    }
}
