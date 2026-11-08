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
    private void OnFullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();

    // ---------------------------------------- 最大化 / 还原: 视频区白块
    //
    // ★★ 2026-11-03: 这里的"最大化/还原期间压黑视频区"整套已删除。
    //   白块的根因是 LibVLCSharp 的 Static 宿主窗口在尺寸剧变时重绘白底,
    //   现在由 VideoBackdropInterop.EnsureBlackVideoHost 在原生层治掉
    //   (WS_CLIPCHILDREN + WM_ERASEBKGND/WM_PAINT 填黑), 不再需要 WPF 黑布。
    //   ★ 别再复活 ArmMaximizeCover / OnMaximizeSettle / OnSystemCommand 那一套。

    /// <summary>收藏状态只有这一个入口 —— 分散着改很容易出现两边显示不一致</summary>
    private void SetFavorited(bool favorited)
    {
        _isFavorited = favorited;
        UpdateActionCounts();
    }
    // "关闭"按钮已随顶栏改版删除(2026-09-26): 关播放器统一走 Esc/鼠标侧键的 HandleBackRequest。

    private void OnRetryClick(object sender, RoutedEventArgs e)
    {
        FailOverlay.Visibility = Visibility.Collapsed;
        // 直播失败要重连直播, 不能落回点播那条路(那时 _currentBvid 是空的)
        if (_isLive) _ = LoadLiveAsync(_liveRoomId);
        else _ = LoadVideoAsync();
    }

    // ------------------------------------------------------------ 按钮事件

    private void UpdateButtonState()
    {
        if (_closing) return;
        // 本地文件没有点赞/投币/收藏/关注这些在线操作, 一律禁用而不是让它点了报错
        var canInteract = Svc.Session.HasLogin && !_isLocalPlayback;
        if (BtnLike != null) BtnLike.IsEnabled = canInteract;
        if (BtnCoin != null) BtnCoin.IsEnabled = canInteract;
        if (BtnFavorite != null) BtnFavorite.IsEnabled = canInteract;
        if (BtnWatchLater != null) BtnWatchLater.IsEnabled = canInteract;
        if (BtnFollow != null) BtnFollow.IsEnabled = canInteract && _ownerMid > 0;
        // 评论输入框的可用性跟着同一套前提走(已登录 + 在线点播)
        UpdateCommentComposer();
    }

    /// <summary>查询当前视频是否已点赞, 用于按钮状态切换</summary>
    private async Task RefreshLikeStateAsync()
    {
        if (_aid <= 0 || !Svc.Session.HasLogin) return;
        var (ok, _, liked) = await Svc.Api.HasLikedAsync(_aid);
        // 查不到就保持原状(别把按钮图标刷成"未赞"), 但把原因记一笔便于排查
        if (!ok) WriteLibVLCDebug($"\n[HasLiked] 查询失败 aid={_aid}\n");
        else _isLiked = liked;
        if (!_closing) UpdateActionCounts();
    }

    /// <summary>
    /// 查询真实的投币状态并刷新按钮点亮态。
    ///
    /// ★ 为什么必须单独查一次: 详情接口(view)**不给**"本人是否投过币"这个信息
    ///   (见 <see cref="ApiClient.GetCoinCountAsync"/> 里那段实测记录), 所以换片之后
    ///   `_isCoined` 不能继续取自详情 —— 那正是"投过币、重开视频后键还是灰的"的根因。
    /// ★ 与 <see cref="RefreshLikeStateAsync"/> 同构: 失败时保持原状(不把已点亮的刷成灰),
    ///   只记一笔便于排查。
    /// ★ 只在已登录且是点播时查: 本地文件/直播没有投币这回事, 查了也是白问一次接口。
    /// </summary>
    private async Task RefreshCoinStateAsync()
    {
        if (_aid <= 0 || !Svc.Session.HasLogin || _isLocalPlayback || _isLive) return;
        var (ok, _, coins) = await Svc.Api.GetCoinCountAsync(_aid);
        if (!ok) { WriteLibVLCDebug($"\n[Coined] 查询失败 aid={_aid}\n"); return; }
        _isCoined = coins > 0;
        if (!_closing) UpdateActionCounts();
    }

    private async void OnLikeClick(object sender, RoutedEventArgs e)
    {
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("点赞需要先登录"); return; }
        if (string.IsNullOrEmpty(_currentBvid)) return;
        // 按当前状态切换: 已赞 -> 取消, 未赞 -> 点赞
        var (ok, err) = await Svc.Api.LikeVideoAsync(_currentBvid, !_isLiked);
        if (ok)
        {
            _isLiked = !_isLiked;
            _likeCount = Math.Max(0, _likeCount + (_isLiked ? 1 : -1));
            UpdateActionCounts();
            // 弹一下(设计稿 v4: 点赞是全栏最高频的动作, 反馈比别的按钮重一档)
            PlayActionPop(LikeIconBox);
            // 取消点赞后锁住 hover 预览: 否则鼠标还停在按钮上, 它会当场又变红
            if (!_isLiked) LockActionHoverPreview();
            Svc.Toast.Show(_isLiked ? "点赞成功" : "已取消点赞");
        }
        else
        {
            // ★ 风控提示已经是一整句可照做的指引(见 ApiClient.FriendlyRiskMessage),
            //   再套一层"操作失败: "只会把话拉长、把重点埋掉。这里按前缀判断, 只在
            //   普通错误上加动作名。
            var m = err ?? "未知错误";
            Svc.Toast.Show(m.StartsWith("B 站风控", StringComparison.Ordinal)
                ? m : "操作失败: " + m);
        }
    }

    /// <summary>
    /// 投币按钮: 弹出数量选择对话框(形态照 B 站官方那个投币弹窗, 见 <see cref="CoinWindow"/>)。
    ///
    /// ★ 为什么要有"选数量"这一步: 投币**不可撤销**, 而单视频上限是 2 枚 —— 原来写死投 1 枚,
    ///   想投 2 枚的用户只能去官方端补。
    /// ★ 用模态窗而不是 Popup: 播放器视频区有单击暂停 / 双击全屏手势, Popup 的"点外部关闭"
    ///   会把那一次点击漏进视频区(与收藏夹选择窗同一个理由)。
    /// ★ 已投过就不再弹: 投币没有"取消"语义, 让用户进到对话框里再撞服务端的"超过投币上限"
    ///   只会白跑一趟。
    /// </summary>
    private void OnCoinClick(object sender, RoutedEventArgs e)
    {
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("投币需要先登录"); return; }
        if (string.IsNullOrEmpty(_currentBvid) || _aid <= 0) return;
        if (_isCoined) { Svc.Toast.Show("这个视频已经投过币了"); return; }

        var dlg = new CoinWindow { Owner = this };
        dlg.ShowDialog();
        if (!dlg.Confirmed) return;
        _ = CoinAsync(dlg.Count, dlg.AlsoLike);
    }

    /// <summary>
    /// 投币(count = 1 或 2)。成功后点亮按钮, 计数按实际枚数累加(不是固定 +1)。
    /// alsoLike 走接口的 select_like, 点赞由服务端在同一次请求里完成。
    /// </summary>
    private async Task CoinAsync(int count, bool alsoLike)
    {
        if (string.IsNullOrEmpty(_currentBvid) || _aid <= 0) return;
        // 已经赞过就不带 select_like: 没有必要, 也免得撞服务端"已经点赞"那条分支
        var wantLike = alsoLike && !_isLiked;
        var (ok, err) = await Svc.Api.CoinVideoAsync(_aid, count, _currentBvid, wantLike);
        if (ok)
        {
            // 同一个视频最多投 2 枚, 本地只记"已投过"的点亮态(下次进来以详情接口为准)
            _isCoined = true;
            _coinCount += count;
            // 赞是服务端顺带点的, 本地状态要跟着走 —— 否则按钮还显示"未赞"、计数也少 1
            if (wantLike) { _isLiked = true; _likeCount += 1; }
            UpdateActionCounts();
            PlayActionPop(CoinIconBox);
            Svc.Toast.Show(count > 1 ? "已投 2 枚币" : "投币成功");
        }
        else
        {
            // 同上: 风控提示自成一句, 不再套"投币失败: "前缀
            var m = err ?? "未知错误";
            Svc.Toast.Show(m.StartsWith("B 站风控", StringComparison.Ordinal)
                ? m : "投币失败: " + m);
        }
    }

    /// <summary>
    /// 操作栏的"图标 + 数量/文字"刷新。**唯一决定这六个按钮颜色与图标形态的地方。**
    ///
    /// 2026-10-06 按设计稿「播放页_v4」重构, 规则:
    ///   · **未激活一律是灰**(TextSecondaryBrush), 颜色只在用户动过之后才出现;
    ///   · 每个动作有**固定色相**, 不跟主题强调色走(红=情绪 / 金=货币 / 蓝=收进我的库 / 绿=拿走)。
    ///     用户把主题色改成绿时, "点赞"跟着变绿会立刻毁掉这套分工 —— 所以色值写死在这里。
    ///   · 数字与文字 TextBlock 的前景**必须显式绑**到按钮 Foreground: 全局隐式 TextBlock 样式
    ///     带 Foreground setter(优先级高于继承), 不绑的话变色的只有图标(项目里反复踩的坑)。
    ///   · 「稍后再看」的两层文字在这里一处切换 —— 散在触发器里改必然出现
    ///     "图标点亮了文字没换"这种半状态(2026-10-06 改回两行布局时恢复)。
    /// </summary>
    private void UpdateActionCounts()
    {
        if (_closing) return;
        LikeLabel.Text = VideoItem.FormatCount(_likeCount);
        CoinLabel.Text = VideoItem.FormatCount(_coinCount);
        FavLabel.Text = VideoItem.FormatCount(_favCount);
        ApplyActionVisual(BtnLike, _isLiked, ActionLikeColor);
        ApplyActionVisual(BtnFavorite, _isFavorited, ActionSaveColor);
        ApplyActionVisual(BtnWatchLater, _isInWatchLater, ActionSaveColor);
        // 「稍后再看」的两层文字随点亮态互换(稍后再看 ↔ 已添加)。
        // ★ 宽度由 XAML 里的双层叠放兜住(两层叠在同一格, 宽度取最大值), 切换只做透明度过渡,
        //   所以整排按钮不会被推着走 —— 两行布局下这套是必需的(2026-10-06 改回两行时恢复)。
        if (LaterLabelOff != null)
        {
            LaterLabelOff.Opacity = _isInWatchLater ? 0 : 1;
            LaterLabelOn.Opacity = _isInWatchLater ? 1 : 0;
        }
        // 未点过的三个按钮也要走一次: 换片后要把上一次残留的色相清回灰。
        // ★ 下载的绿色终态由 _cacheDownloaded 决定(它只在换片时复位, 见 ResetForNewMedia)——
        //   不能在这里无条件刷成灰: 本方法会被点赞/收藏等**同一片内**的操作反复调用,
        //   那样用户一下载完、随手点个赞, 绿色就没了。
        ApplyActionVisual(BtnCoin, _isCoined, ActionCoinColor);
        ApplyActionVisual(BtnShare, false, ActionNeutralColor);
        ApplyActionVisual(BtnCache, _cacheDownloaded, ActionDownloadColor);
    }

    /// <summary>当前这条视频是否已下载完成(只用于下载按钮的绿色终态)</summary>
    private bool _cacheDownloaded;

    // ---- 操作栏色相(设计稿 v4: 固定色, 不跟主题强调色) ----

    /// <summary>点赞红(情绪)</summary>
    private static readonly Color ActionLikeColor = Color.FromRgb(0xF4, 0x3F, 0x5E);
    /// <summary>投币琥珀(货币)</summary>
    private static readonly Color ActionCoinColor = Color.FromRgb(0xF5, 0x9E, 0x0B);
    /// <summary>收藏 / 稍后再看蓝(收进我的库, 两者同族同色)</summary>
    private static readonly Color ActionSaveColor = Color.FromRgb(0x0E, 0xA5, 0xE9);
    /// <summary>下载绿(拿走)。只在"已下载"这一个终态出现, 不像其它色相有"未点亮/点亮"两态</summary>
    private static readonly Color ActionDownloadColor = Color.FromRgb(0x10, 0xB9, 0x81);
    /// <summary>中性(分享 / 下载): 走主题的次要文字色, 不做色相区分</summary>
    private static readonly Color? ActionNeutralColor = null;

    /// <summary>
    /// 把一个操作按钮刷成"点亮(用该动作的色相)"或"未点亮(灰)"。
    ///
    /// ★ 这里**直接操作控件**而不是靠 Style 触发器: hover 预览(.v4)需要在鼠标悬停时
    ///   临时借用色相, 而"取消点赞后鼠标还悬停着 → 按钮当场又变红"这个坑(v4 稿第 04 节)
    ///   要求"取消后短暂锁住 hover"。这套时序逃不出代码, 所以统一放在这里。
    /// </summary>
    private void ApplyActionVisual(Button? btn, bool on, Color? accent)
    {
        if (btn == null) return;
        if (on && accent.HasValue)
        {
            var own = new SolidColorBrush(accent.Value);
            // 冻结笔刷: 这个色相是常量, 冻结后 WPF 渲染时可以少走一次可变检查。
            // ★ 只冻结**自己新建**的这一个(2026-11-08): 主题笔刷是共享的可变实例,
            //   冻结它会让后续换肤的 ColorAnimation 直接抛异常(实测 TextSecondaryBrush 被冻死)。
            if (own.CanFreeze) own.Freeze();
            btn.Foreground = own;
        }
        else
        {
            btn.Foreground = (Brush)FindResource("TextSecondaryBrush");
        }
        // Tag 驱动模板里的常驻浅底(见 PlayerActionBtn 的 DataTrigger)
        btn.Tag = on ? "on" : "off";
    }

    /// <summary>该动作的色相(按名字取, 点亮/hover 预览共用一套来源)</summary>
    private Color? ActionAccentOf(Button btn)
    {
        if (ReferenceEquals(btn, BtnLike)) return ActionLikeColor;
        if (ReferenceEquals(btn, BtnCoin)) return ActionCoinColor;
        if (ReferenceEquals(btn, BtnFavorite) || ReferenceEquals(btn, BtnWatchLater)) return ActionSaveColor;
        // 下载: 只有"已下载"这一个终态是绿的, 没有"未点亮→点亮"的来回切换
        if (ReferenceEquals(btn, BtnCache)) return ActionDownloadColor;
        return ActionNeutralColor;
    }

    /// <summary>
    /// 这个按钮有没有"hover 预览"。
    /// ★ 下载(和分享)没有: 下载的结果是**一次性**的, 鼠标扫过去就预先把按钮染绿等于
    ///   谎报"已经下载好了"。设计稿 v4 里 .act.dl 也刻意没有 hover 预览。
    /// </summary>
    private bool HasHoverPreview(Button btn)
        => !ReferenceEquals(btn, BtnCache) && !ReferenceEquals(btn, BtnShare);

    /// <summary>该按钮当前是不是"已点亮"态(取消后要用它判断该不该回灰)</summary>
    private bool IsActionOn(Button btn)
    {
        if (ReferenceEquals(btn, BtnLike)) return _isLiked;
        if (ReferenceEquals(btn, BtnFavorite)) return _isFavorited;
        if (ReferenceEquals(btn, BtnWatchLater)) return _isInWatchLater;
        if (ReferenceEquals(btn, BtnCoin)) return _isCoined;
        // 下载的"已下载"也是终态, 有它就说明不该回灰
        if (ReferenceEquals(btn, BtnCache)) return _cacheDownloaded;
        return false;
    }

    /// <summary>
    /// 操作按钮的 hover 预览(设计稿 v4 第 03 节): 鼠标上来先把该动作的色相透出来,
    /// 让用户"不用点就知道点了会怎样"; 底色由模板的 DataTrigger 负责(同色相浅底)。
    ///
    /// ★ 已在点亮态的按钮不需要预览 —— 它本来就是那个颜色, 变过去等于没变。
    /// ★★ 取消后 750ms 内不预览(hover 锁, 见 _actionHoverLockUntil): 取消的那一刻鼠标
    ///    必然还停在这个按钮上, 不给锁的话按钮会当场又变回红色, 用户以为自己没取消成功,
    ///    于是再点一次(这就是设计稿里 v3 的那个隐藏 bug)。
    /// </summary>
    private void OnActionButtonHover(object sender, MouseEventArgs e)
    {
        if (_closing || sender is not Button btn) return;
        if (DateTime.UtcNow < _actionHoverLockUntil) return;
        if (!HasHoverPreview(btn)) return;
        var accent = ActionAccentOf(btn);
        if (accent.HasValue && !IsActionOn(btn))
            btn.Foreground = new SolidColorBrush(accent.Value);
    }

    /// <summary>鼠标离开: 回到该按钮的"真实态"颜色(点亮保持色相, 未点亮回灰)</summary>
    private void OnActionButtonLeave(object sender, MouseEventArgs e)
    {
        if (_closing || sender is not Button btn) return;
        ApplyActionVisual(btn, IsActionOn(btn), ActionAccentOf(btn));
    }

    /// <summary>取消操作后锁住 hover 预览到这个时刻(UTC)。750ms 与设计稿一致。</summary>
    private DateTime _actionHoverLockUntil = DateTime.MinValue;

    /// <summary>锁住 hover 预览 750ms(取消点赞/收藏/稍后再看之后调)</summary>
    private void LockActionHoverPreview() => _actionHoverLockUntil = DateTime.UtcNow.AddMilliseconds(750);

    /// <summary>
    /// 给一个操作按钮播"弹一下"的反馈(设计稿 v4 第 03 节②)。
    /// 作用在图标盒子上而不是内部 Path: Path 自身没有 ScaleTransform, 直接缩放会和
    /// 别的动画/布局打架; 图标盒子是纯容器, 缩放它最干净。
    ///
    /// ★★★ 每次都必须**新建** ScaleTransform, 不能复用现有的那一个。
    ///   原因见 XAML 里 ActionIconBox 的注释: 样式 Setter 里的 Freezable 会被 WPF **冻结**,
    ///   对冻结对象 BeginAnimation 会直接抛
    ///     "无法在 System.Windows.Media.ScaleTransform 上激活 ScaleX 属性, 因为该对象已密封或已冻结"
    ///   —— 用户看到的就是"点一下点赞就弹错误框"(2026-10-06 实测踩到)。
    ///   新建一个 ScaleTransform 的开销可以忽略(一次点击一次), 而它从根上避开了冻不冻的问题。
    /// </summary>
    private void PlayActionPop(Grid? iconBox)
    {
        if (iconBox == null || _closing) return;
        var scale = new ScaleTransform(1, 1);
        iconBox.RenderTransform = scale;
        // 关键帧: 1 → 1.3 → 1(与设计稿的 40% 处 1.3 一致), 总时长 420ms
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromPercent(0)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.3, KeyTime.FromPercent(0.4))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

}
