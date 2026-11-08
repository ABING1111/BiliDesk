using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace BiliDesk.Views;

public partial class PlayerWindow : FluentWindow
{
    /// <summary>
    /// 查询该视频当前的收藏状态。
    /// 用 /x/v3/fav/folder/created/list?rid= : 一次请求同时得到"有没有收藏"与
    /// "收藏在哪些夹里", 后面点按钮时就不用再查一遍。
    /// </summary>
    private async Task RefreshFavStateAsync()
    {
        if (_aid <= 0 || !Svc.Session.HasLogin || _closing) return;
        var (ok, _, folders) = await Svc.Api.GetVideoFavFoldersAsync(_aid);
        if (!ok || folders == null || _closing) return;
        // 只要在任意一个收藏夹里, 就算"已收藏"
        var favorited = folders.Any(f => f.HasVideo);
        SetFavorited(favorited);
    }

    // ------------------------------------------------------------ 稍后再看

    /// <summary>稍后再看的点亮态只有这一个入口(和收藏一样, 别在多处直接改字段)</summary>
    private void SetInWatchLater(bool inList)
    {
        _isInWatchLater = inList;
        if (BtnWatchLater != null)
            BtnWatchLater.ToolTip = inList ? "已在稍后再看（点击移出）" : "加入稍后再看";
        UpdateActionCounts();
    }

    /// <summary>
    /// 查询该视频是否已在「稍后再看」里。
    /// 服务端没有单条查询接口, 走的是"拉列表 + 本地缓存 aid 集合"(见 ApiClient.IsInWatchLaterAsync):
    /// 60 秒内复用, 所以连续点开视频不会每次都拉一遍两百多条。
    /// 返回 null(未登录/查询失败)时**不点亮也不熄灭**, 保持未知态 —— 谎报状态比不显示更糟。
    /// </summary>
    private async Task RefreshWatchLaterStateAsync()
    {
        if (_aid <= 0 || _closing) return;
        var (inOk, _, inList) = await Svc.Api.IsInWatchLaterAsync(_aid);
        // 没查到就保持现状 —— 早先 `inList == null` 的守卫就在这里, 语义不变
        if (_closing || !inOk) return;
        SetInWatchLater(inList);
    }

    /// <summary>
    /// 稍后再看按钮: 加入 / 移出(再次点击)。
    /// 只按**接口返回值**翻转本地状态, 不做乐观更新、也绝不"提交完再拉一次列表确认" ——
    /// 服务端列表本身有约 2 秒延迟, 回查只会读到旧数据。
    /// </summary>
    private async void OnWatchLaterClick(object sender, RoutedEventArgs e)
    {
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("稍后再看需要先登录"); return; }
        if (_isLive || _isLocalPlayback) { Svc.Toast.Show("直播和本地文件不支持稍后再看"); return; }
        if (_aid <= 0 || _watchLaterBusy) return;

        var target = !_isInWatchLater;   // 当前不在 -> 加入; 已在 -> 移出
        _watchLaterBusy = true;
        try
        {
            var (ok, err) = target
                ? await Svc.Api.AddWatchLaterAsync(_aid)
                : await Svc.Api.RemoveWatchLaterAsync(_aid);
            if (_closing) return;
            if (!ok)
            {
                Svc.Toast.Show((target ? "加入失败: " : "移出失败: ") + (err ?? "未知错误"));
                return;
            }
            SetInWatchLater(target);
            PlayActionPop(LaterIconBox);
            if (!target) LockActionHoverPreview();   // 移出后锁 hover 预览(同点赞的坑)
            Svc.Toast.Show(target ? "已加入稍后再看" : "已从稍后再看移出");
        }
        finally
        {
            _watchLaterBusy = false;
        }
    }

    /// <summary>
    /// 收藏按钮: 弹出收藏夹勾选窗(预勾选视频当前所在的夹),
    /// 确定后把勾选差集(新增/移出)一次提交给 deal 接口 —— 与网页端"选择收藏夹"行为一致。
    /// "取消收藏"也走同一个窗(把勾全去掉), 不再单独走一键直存默认夹的旧路径。
    /// </summary>
    private async void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("收藏需要先登录"); return; }
        if (_aid <= 0 || _favBusy) return;

        _favBusy = true;
        try
        {
            var (ok, err, folders) = await Svc.Api.GetVideoFavFoldersAsync(_aid);
            if (_closing) return;
            if (!ok || folders == null)
            {
                Svc.Toast.Show("收藏状态获取失败: " + (err ?? "未知错误"));
                return;
            }
            if (folders.Count == 0)
            {
                Svc.Toast.Show("账号下还没有收藏夹, 请先去 B 站创建一个");
                return;
            }

            // 模态弹窗(Owner 指向播放器): 播放器若正在关窗, 不再开新窗
            if (_closing) return;
            var picker = new FavFolderPickerWindow(folders, _videoTitle) { Owner = this };
            picker.ShowDialog();
            if (_closing || !picker.Confirmed) return;

            var addIds = picker.AddedIds;
            var delIds = picker.RemovedIds;
            if (addIds.Count == 0 && delIds.Count == 0) return;   // 勾选没变化, 不发请求

            var (ok2, err2) = await Svc.Api.DealFavoriteAsync(_aid,
                addIds.Count > 0 ? addIds : null,
                delIds.Count > 0 ? delIds : null);
            if (_closing) return;
            if (!ok2) { Svc.Toast.Show("收藏失败: " + (err2 ?? "未知错误")); return; }

            // 高亮与提示跟最终勾选走(至少在一个夹里就算"已收藏")
            var final = picker.FinalCheckedIds;
            SetFavorited(final.Count > 0);
            PlayActionPop(FavIconBox);
            // 取消收藏后同样要锁 hover 预览(与取消点赞同一个坑)
            if (final.Count == 0) LockActionHoverPreview();
            Svc.Toast.Show(final.Count > 0 ? $"已收藏到 {final.Count} 个收藏夹" : "已取消收藏");
        }
        finally
        {
            _favBusy = false;
        }
    }

    /// <summary>查询当前 UP 主关注状态, 用于关注按钮切换显示</summary>
    private async Task RefreshFollowStateAsync()
    {
        if (_ownerMid <= 0) return;
        var (relOk, _, followed) = await Svc.Api.GetRelationAsync(_ownerMid);
        if (!relOk || _closing) return;
        _isFollowed = followed;
        UpdateFollowButton();
    }

    private void UpdateFollowButton()
    {
        if (FollowLabel == null) return;
        FollowLabel.Text = _isFollowed ? "已关注" : "+ 关注";
        // 与 UP 主主页同一套视觉: 已关注 = 浅灰底 + 次要色; 未关注 = 强调色实底白字。
        // 播放页没有 IsFollowed 绑定, 用 Tag 驱动样式里的 DataTrigger。
        if (BtnFollow != null) BtnFollow.Tag = _isFollowed ? "followed" : "follow";
    }

    private async void OnFollowClick(object sender, RoutedEventArgs e)
    {
        if (!Svc.Session.HasLogin || _ownerMid <= 0) { Svc.Toast.Show("关注需要先登录"); return; }
        // 按当前状态切换: 已关注 -> 取消关注, 未关注 -> 关注
        var (ok, err) = await Svc.Api.FollowUpAsync(_ownerMid, !_isFollowed);
        if (ok)
        {
            _isFollowed = !_isFollowed;
            UpdateFollowButton();
            Svc.Toast.Show(_isFollowed ? "已关注" : "已取消关注");
        }
        else
        {
            Svc.Toast.Show("操作失败: " + (err ?? "未知错误"));
        }
    }

    private void OnUpPageClick(object sender, RoutedEventArgs e)
    {
        if (_ownerMid <= 0) return;
        // 打开原生 UP 主主页(新窗口)
        var dispatcher = new NavigationDispatcher(this);
        dispatcher.OpenUserSpace(_ownerMid.ToString());
    }

    /// <summary>
    /// 左键点 UP 头像/名字 → UP 主主页。
    /// 原"UP 主页"文字入口已删(2026-09-26 用户要求), 这就是 UP 入口的唯一落点;
    /// 头像的右键菜单(看大图/下载)走 ContextMenu, 与左键互不干扰。
    /// </summary>
    private void OnUpAvatarClick(object sender, MouseButtonEventArgs e) => OnUpPageClick(sender, e);

    /// <summary>右键 UP 主头像 → 查看大图</summary>
    private void OnViewUpAvatar(object sender, RoutedEventArgs e)
        => MediaActions.ShowImage(_ownerFace,
            string.IsNullOrEmpty(_ownerName) ? "UP 主头像" : _ownerName + " 的头像", this);

    /// <summary>右键 UP 主头像 → 保存到本地</summary>
    private async void OnDownloadUpAvatar(object sender, RoutedEventArgs e)
        => await MediaActions.SaveImageAsync(_ownerFace,
            string.IsNullOrEmpty(_ownerName) ? "头像" : _ownerName + "_头像");

    private void OnCopyLinkClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentBvid)) return;
        try
        {
            Clipboard.SetText("https://www.bilibili.com/video/" + _currentBvid);
            Svc.Toast.Show("链接已复制");
        }
        catch
        {
            // 剪贴板被别的进程占着时 SetText 会抛(系统级竞争, 重试也可能再失败)。
            // 静默吞掉即可: 复制失败不该弹框打断播放, 而且没有"已复制"的提示用户自己会再点一次。
        }
    }

    /// <summary>
    /// "网页版"入口已随第二行小字链接一起删除(2026-09-26 用户要求)。
    /// 别处要"跳系统浏览器"时, 用 Process.Start(url) { UseShellExecute = true } 即可。
    /// </summary>

}
