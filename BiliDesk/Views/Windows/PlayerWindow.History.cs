using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using BiliDesk.Helpers;
using BiliDesk.Services;

namespace BiliDesk.Views;

/// <summary>
/// 播放器 · **上一个 / 下一个视频**(2026-11-25, 用户要求: 箭头表示, 左 = 上一个, 右 = 下一个)。
///
/// 分片职责: 把「看过哪些」记到 <see cref="PlayerHistoryStack"/>, 并提供"退回去 / 回到刚才那条"
/// 的两个动作。判定(栈怎么动、什么算同一条)全在 <see cref="PlayerHistoryStack"/> 里,
/// 这里只负责"真的换片"——与 <see cref="AutoPlayPolicy"/> 同一种拆法。
///
/// ★★ 为什么栈的变动挂在"**真的打开了**"(<see cref="NoteHistoryOpened"/>)而不是点箭头那一刻:
///   加载失败(视频已删 / 直播已下播 / 本地文件不见了)时栈必须保持原样, 否则用户会
///   停在失败页上, 还得再按一次箭头才能回到能看的那条。
///   所以点箭头只是 <see cref="PlayerHistoryStack.RequestBack"/>(记下"要去哪"),
///   紧接着那次成功的打开才兑现。
///
/// ★ 记录是**进程级**的(<see cref="PlayerHistoryStack.Shared"/>): 关掉播放器再打开另一条视频,
///   左箭头照样能回到刚才看过的那条。
/// ★ 分P / 切清晰度这类"还是这一条"的重新打开只更新当前条目, 不新增历史
///   (见 <see cref="PlayerHistoryEntry.SameVideo"/>)。
/// </summary>
public partial class PlayerWindow : FluentWindow
{
    /// <summary>
    /// "正在为历史导航换片"的闸门, 与 <see cref="_switchingSeasonEpisode"/> 同款理由:
    /// 连点两次箭头(或点完箭头又点了合集里的一集)会叠起两条 LoadVideoAsync,
    /// 后回来的那条覆盖先回来的 —— 点了「上一个」却停在第三个视频上。
    /// ★ 登记在 <c>PersistentFields</c>: 闸门由自己的 finally 收尾, 而 ResetForNewMedia
    ///   是在闸门**里面**调用的, 换片清它等于把闸门打开。
    /// </summary>
    private bool _switchingHistory;

    // ---------------------------------------------------------------- 记账

    /// <summary>
    /// 一条视频真的打开了(在线: 详情到手、cid 确定; 直播 / 本地: 已起播)。
    /// 三个起播入口各调一次, 其余逻辑全在栈里。
    /// </summary>
    private void NoteHistoryOpened(PlayerHistoryEntry entry)
    {
        PlayerHistoryStack.Shared.OnOpened(entry);
        UpdateHistoryButtons();
    }

    /// <summary>
    /// 同步两颗箭头的可用态与 ToolTip(ToolTip 里带上目标标题, 用户不必点一下才知道要去哪)。
    /// ★ 不可用时**置灰而不是收起**: 收起会让播放键左右横跳, 灰掉的箭头同时告诉他"这是第一层"。
    /// </summary>
    private void UpdateHistoryButtons()
    {
        var history = PlayerHistoryStack.Shared;
        ApplyHistoryButton(BtnPrevVideo, history.Previous, "上一个视频（Alt+←）");
        ApplyHistoryButton(BtnNextVideo, history.Next, "下一个视频（Alt+→）");
    }

    private static void ApplyHistoryButton(Button? button, PlayerHistoryEntry? target, string label)
    {
        if (button == null) return;
        button.IsEnabled = target != null;
        var title = target?.Title;
        button.ToolTip = target == null
            ? "没有" + label
            : string.IsNullOrWhiteSpace(title) ? label : $"{label}：{title}";
    }

    // ---------------------------------------------------------------- 点击 / 快捷键

    private void OnPrevVideoClick(object sender, RoutedEventArgs e) => _ = GoHistoryAsync(back: true);

    private void OnNextVideoClick(object sender, RoutedEventArgs e) => _ = GoHistoryAsync(back: false);

    /// <summary>
    /// 点了左/右箭头(或按了 Alt+←/→): 取目标 → 换片。
    ///
    /// ★ 与其它换片入口一样先过闸门: 正在切合集/分P/历史时再点一次不能叠第二条加载链。
    /// ★ 本地文件在**请求之前**先判存在: 文件已被删时把那条从历史里摘掉并提示,
    ///   否则用户每按一次都撞在同一颗打不开的记录上(而且方向标记会挂在那儿等下一次打开)。
    /// </summary>
    private async Task GoHistoryAsync(bool back)
    {
        if (_closing || _switchingHistory || _switchingSeasonEpisode || _switchingPage) return;

        var history = PlayerHistoryStack.Shared;
        var peek = back ? history.Previous : history.Next;
        if (peek == null) return;

        if (peek.Kind == PlayerHistoryKind.Local && !SafeFileExists(peek.Path))
        {
            history.Remove(peek);
            UpdateHistoryButtons();
            Svc.Toast.Show("这条本地文件已经不存在了，已从播放历史里移除");
            return;
        }

        var target = back ? history.RequestBack() : history.RequestForward();
        if (target == null) return;
        await SwitchToHistoryEntryAsync(target);
    }

    private static bool SafeFileExists(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try { return File.Exists(path); }
        catch { return false; }
    }

    // ---------------------------------------------------------------- 换片

    /// <summary>
    /// 真正切到历史里的某一条。三种片源各走各的起播路径:
    ///   在线 → 与自动连播 / 合集换片同一套(停流 → 换 bvid/cid → 重建每片状态 → 加载);
    ///   直播 / 本地 → 直接调已有的公开入口(它们自己会停流、重置、起播)。
    ///
    /// ★ 在线这条路**不调** <c>Svc.Player.PlayVideo</c>: 那条路在窗口不可复用时会新建一个
    ///   播放器窗口(与 SwitchToSeasonEpisodeAsync / SwitchToAutoPlayEntryAsync 同一个理由),
    ///   而历史导航必须留在本窗口。
    /// ★ cid 必须传给 LoadVideoAsync: 0 会落回 pages[0], "回到刚才那个分P"就变成回到 P1。
    /// ★ 番剧分集表要一起恢复: 条目录了打开那一刻生效的 _bangumi, 这里写回去,
    ///   选集面板与"播完自动下一集"才会跟着回来(普通视频读到 null, 与手动换片一致)。
    /// ★ 用 <see cref="StopPlayerAsync"/> 而不是 _mp.Stop(): 前者在后台等解码/vout 线程退出,
    ///   当前线程不会定格 ~1 秒(与自动连播换片同款)。
    /// </summary>
    private async Task SwitchToHistoryEntryAsync(PlayerHistoryEntry entry)
    {
        if (_closing || entry == null) return;
        if (_switchingHistory) return;

        _switchingHistory = true;
        try
        {
            WriteLibVLCDebug(
                $"[History] 切换 → {entry.Kind} bvid={entry.Bvid} cid={entry.Cid} " +
                $"room={entry.RoomId} {entry.Title}\n");

            switch (entry.Kind)
            {
                case PlayerHistoryKind.Online:
                    if (string.IsNullOrEmpty(entry.Bvid)) return;
                    SetBangumiPlaylist(entry.Bangumi);
                    await StopPlayerAsync();
                    if (_closing) return;
                    _currentBvid = entry.Bvid;
                    _isLocalPlayback = false;
                    _isLive = false;
                    _liveRoomId = 0;
                    ResetForNewMedia();
                    await LoadVideoAsync(entry.Cid);
                    break;

                case PlayerHistoryKind.Live:
                    if (entry.RoomId <= 0) return;
                    PlayLive(entry.RoomId,
                        string.IsNullOrWhiteSpace(entry.Title) ? null : entry.Title);
                    break;

                default:
                    if (string.IsNullOrEmpty(entry.Path)) return;
                    PlayLocalFile(entry.Path);
                    break;
            }
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
        }
        finally
        {
            _switchingHistory = false;
        }
    }
}
