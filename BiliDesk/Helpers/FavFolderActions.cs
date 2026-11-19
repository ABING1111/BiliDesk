using System.Threading.Tasks;
using System.Windows;
using BiliDesk.Models;
using BiliDesk.Services;
using BiliDesk.Views.Windows;

namespace BiliDesk.Helpers;

/// <summary>
/// 收藏夹的公共动作(2026-11-19): 新建 / 删除。
///
/// ★ 新建有三个入口, 共用这一条路径:
///     ① 收藏页右下角的「+」
///     ② 播放器收藏勾选窗左下角的「新建收藏夹」
///     ③ 「我的」页收藏夹标题行的「+」
///   三处各写一遍"弹窗 → 校验 → 请求 → 提示"必然会漂移(错误文案不同、校验规则不同、
///   有一处忘了 Trim), 所以集中到这里; 各页面只负责"建完以后怎么刷新自己"。
/// ★ 删除同样要集中: 确认文案、默认夹拦截、删除后广播"集合变了"必须只有一份。
/// ★ 增删成功后 <see cref="FavFolderSync.NotifyChanged"/> 让其它页面知道自己缓存过期了
///   (收藏页/「我的」页下次显示时按版本号重拉, 不会出现"这边改了那边还是旧列表")。
/// </summary>
public static class FavFolderActions
{
    /// <summary>
    /// 弹出新建窗并创建。返回新收藏夹(服务端没回 id 时返回 null —— 调用方按"重新拉列表"兜底);
    /// 用户取消 / 失败 / 未登录都返回 null。
    /// </summary>
    /// <param name="owner">模态父窗口。页面里传 SvcWindow.Main, 弹窗里传窗口自己。</param>
    public static async Task<FavFolder?> CreateAsync(Window? owner = null)
    {
        if (!Svc.Session.HasLogin)
        {
            Svc.Toast.Show("新建收藏夹需要先登录");
            return null;
        }

        var dlg = new FavFolderCreateWindow
        {
            Owner = owner ?? SvcWindow.Main ?? Application.Current?.MainWindow
        };
        if (dlg.ShowDialog() != true) return null;

        var (ok, err, folder) = await Svc.Api.CreateFavFolderAsync(
            dlg.FolderTitle, dlg.IsPrivate ? 1 : 0);
        if (!ok)
        {
            Svc.Toast.Show("新建收藏夹失败: " + (err ?? "未知错误"));
            return null;
        }

        FavFolderSync.NotifyChanged();
        Svc.Toast.Show($"已创建收藏夹「{dlg.FolderTitle}」");
        return folder;
    }

    /// <summary>
    /// 删除收藏夹(2026-11-19): 先确认, 再调接口; 成功返回 true。
    ///
    /// ★ 默认收藏夹直接拦下: 服务端本来也不让删, 但先弹一个"确认删除"再失败,
    ///   对用户是白跑一趟 —— 入口处就应该禁掉(见各页面的 IsDefault 绑定)。
    /// ★ 删除不可撤销: 确认框要写清夹里还有多少条, 并提醒这是不可恢复操作。
    /// </summary>
    public static async Task<bool> DeleteAsync(FavFolder folder)
    {
        if (folder == null) return false;
        if (folder.IsDefault)
        {
            Svc.Toast.Show("默认收藏夹不能删除");
            return false;
        }
        if (!Svc.Session.HasLogin)
        {
            Svc.Toast.Show("删除收藏夹需要先登录");
            return false;
        }

        var body = $"确定要删除收藏夹「{folder.Title}」吗?\n\n";
        if (folder.MediaCount > 0)
            body += $"夹内还有 {folder.MediaCount} 个视频，请确认不再需要这个分组。\n\n";
        body += "删除后无法恢复。";

        if (!ConfirmDialog.Show(body, "删除收藏夹", "删除")) return false;

        var (ok, err) = await Svc.Api.DeleteFavFolderAsync(folder.Id);
        if (!ok)
        {
            Svc.Toast.Show("删除收藏夹失败: " + (err ?? "未知错误"));
            return false;
        }

        FavFolderSync.NotifyChanged();
        Svc.Toast.Show($"已删除收藏夹「{folder.Title}」");
        return true;
    }
}
