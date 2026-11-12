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
using System.Windows.Documents;
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
        // ★ 长按已经触发三连时, 松手这一刻还会再走一次 Click —— 不能让它把刚点上的赞取消掉。
        //   用时间戳而不是布尔标记: 长按后鼠标移出按钮再松手时 Click 根本不触发,
        //   布尔标记会留在原地把**下一次**正常点赞吞掉(见 OnLikeButtonPreviewMouseDown)。
        if (_tripleComboAt != 0 && Environment.TickCount64 - _tripleComboAt < 1500) return;
        // ★ 长按中途松手(超过点击阈值、但没到 2 秒): 只取消长按, 不点赞。
        if (_likePressCancelled) { _likePressCancelled = false; return; }
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

    // ------------------------------------------------------------ 长按点赞 = 一键三连

    /// <summary>
    /// 长按总时长(ms): 按住满 2 秒触发三连。
    /// ★ 分两段(2026-11-12 用户要求, 与 B 站实测一致):
    ///   前 <see cref="LikeConfirmMs"/> 只用于**确认长按意图**(不显示任何动画),
    ///   确认之后才进入 <see cref="LikeProgressMs"/> 的进度环动画, 扫满才触发三连。
    /// </summary>
    private const int LikeLongPressMs = 2000;

    /// <summary>长按确认时长: 按住不足它松开 = 普通点击; 超过才进入动画阶段。</summary>
    private const int LikeConfirmMs = 500;

    /// <summary>进度环填充时长(确认之后才开始扫)。</summary>
    private const int LikeProgressMs = LikeLongPressMs - LikeConfirmMs;

    /// <summary>
    /// 超过这个按住时长再松手, 就按"长按取消"处理, **不再点赞**。
    /// ★ 用户按下看到进度环起来, 中途(比如 1 秒)松手只是想放弃这次三连 ——
    ///   以前这条路径会走按钮的 Click 变成点赞, 用户反馈"取消长按反而点了个赞"。
    /// </summary>
    private const int LikeClickMaxMs = 300;

    /// <summary>
    /// 最近一次"长按触发三连"的时刻(TickCount64); 0 = 从未触发。
    /// 只用来吞掉长按松手后紧跟的那一次 Click(见 OnLikeClick), 不做别的语义。
    /// </summary>
    private long _tripleComboAt;

    /// <summary>三连请求进行中 —— 防止连按造成并发请求(投币不可撤销, 不能重复发)</summary>
    private bool _tripleComboBusy;

    /// <summary>本次按下的时刻(TickCount64); 0 = 没按下</summary>
    private long _likeDownTick;

    /// <summary>本次松开是否应按"长按取消"处理(按住超过 LikeClickMaxMs, 不点赞)</summary>
    private bool _likePressCancelled;

    /// <summary>当前长按的是哪个按钮(点赞/投币/收藏): 决定进度环挂在哪、初始用什么色相</summary>
    private Button? _pressedTripleButton;

    /// <summary>是否已通过"确认长按"阶段(前 500ms); 通过后才显示进度环并开始扫。</summary>
    private bool _likeConfirmed;

    /// <summary>
    /// 长按点赞按钮: 按住满 <see cref="LikeLongPressMs"/> 触发三连。
    /// 分两段(2026-11-12 用户要求, 与 B 站实测一致):
    ///   前 <see cref="LikeConfirmMs"/> **静默确认**(不显示任何动画);
    ///   确认之后进度环才从 0 扫到满(<see cref="LikeProgressMs"/>), 扫满触发三连。
    /// 中途(超过 <see cref="LikeClickMaxMs"/>)松手 = 取消长按, 不触发按钮的 Click。
    ///
    /// ★ 为什么用 Preview 事件: Button 会在 MouseLeftButtonDown 里捕获鼠标, 普通事件虽然
    ///   也能收到, 但 Preview 一定先到, 不会因为模板/内部元素把事件标记处理掉而漏掉按下。
    /// </summary>
    private void OnTripleButtonPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 按下就把计时起点记下来(即使后面因未登录/本地播放提前 return, Click 的判定也不受影响)
        _likeDownTick = Environment.TickCount64;
        _likePressCancelled = false;
        if (_closing) return;
        if (sender is not Button btn) return;
        // 与按钮可用性同一套前提(见 UpdateButtonState): 本地文件/直播/未登录没有三连
        if (!Svc.Session.HasLogin || _isLocalPlayback || _isLive) return;
        if (string.IsNullOrEmpty(_currentBvid) || _aid <= 0) return;

        _pressedTripleButton = btn;
        StartLikePressProgress();
    }

    private void OnTripleButtonPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        // ★ 按住不到 LikeClickMaxMs 才算"点击"; 超过(哪怕还没到 500ms 确认)一律按"取消长按"处理。
        //   Click 在 Up 之后才触发, 所以这里先落标记, 各按钮的 Click 再读它。
        _likePressCancelled = _likeDownTick != 0 &&
                              Environment.TickCount64 - _likeDownTick >= LikeClickMaxMs;
        CancelLikePressProgress();
    }

    /// <summary>指针离开按钮: 松手位置不在按钮上时 Click 不会触发, 取消长按。</summary>
    private void OnTripleButtonMouseLeave(object sender, MouseEventArgs e)
    {
        CancelLikePressProgress();
    }

    // ------------------------------------------------------------ 长按进度条

    /// <summary>进度环刷新间隔(约 60fps; 2 秒阈值≈125 帧)</summary>
    private static readonly TimeSpan LikePressTick = TimeSpan.FromMilliseconds(16);

    private DispatcherTimer? _likePressProgressTimer;

    /// <summary>长按开始的时刻(TickCount64), 用来算进度</summary>
    private long _likePressStartTick;

    /// <summary>
    /// 进度条是否已经"提交"给三连(长按到点后为 true)。
    /// 提交之后松手不能把它收掉 —— 它还要在三连执行期间按步骤换色。
    /// </summary>
    private bool _likePressCommitted;

    /// <summary>点赞按钮外框的进度环(Adorner: 不参与布局, 不会撑大按钮)</summary>
    private PressRingAdorner? _likePressAdorner;

    // ------------------------------------------------------------ 三连演出(约 1.8s)

    /// <summary>
    /// 一键三连的演出序列, 总时长约 1.8 秒(与用户实测的"动画约 1.5~2 秒"对齐):
    ///   0ms     进度条满格 + 脉冲一次(亮 → 暗 → 亮)
    ///   350ms   进度条转投币金
    ///   700ms   进度条转收藏蓝
    ///   1050ms  「一键三连」徽章缩放浮现
    ///   1650ms  徽章淡出 + 进度条淡出
    ///
    /// ★ 演出与真实请求**并行**: 按钮点亮仍由 RunTripleComboAsync 的成功结果驱动,
    ///   所以失败时不会出现"动画说成功、按钮却没亮"的假象 —— 动画只负责节奏, 状态以接口为准。
    /// </summary>
    private async Task PlayTripleComboShowAsync()
    {
        if (_likePressAdorner == null) return;
        // ★ 三连演出时**三个按钮依次弹跳** + 进度环换色:
        //   0ms 点赞红弹跳 → 350ms 投币金弹跳 → 700ms 收藏蓝弹跳 ——
        //   这是"三连时三个按钮都播放动画"的实现(而不是给投币/收藏各自加长按入口)。
        SetLikePressProgressColor(ActionLikeColor);
        PulseLikePressProgress();
        // 整个按钮弹一下(1 → 1.08 → 1), 比只弹图标盒明显得多, 三个按钮各弹一次
        PlayButtonPop(BtnLike);
        PlayActionPop(LikeIconBox);
        try
        {
            await Task.Delay(350);
            if (_closing) return;
            SetLikePressProgressColor(ActionCoinColor);
            PlayButtonPop(BtnCoin);
            PlayActionPop(CoinIconBox);
            // 投币/收藏也各闪一圈自己的色环: 只弹图标太不明显, 用户看不出"三个按钮都在动"
            FlashButtonRing(BtnCoin, ActionCoinColor);
            await Task.Delay(350);
            if (_closing) return;
            SetLikePressProgressColor(ActionSaveColor);
            PlayButtonPop(BtnFavorite);
            PlayActionPop(FavIconBox);
            FlashButtonRing(BtnFavorite, ActionSaveColor);
            await Task.Delay(350);
            if (_closing) return;
            ShowTripleComboBadge();
            await Task.Delay(600);
        }
        catch
        {
            // 窗口关闭 / 播放器释放: 演出直接结束, 不往外抛
        }
        finally
        {
            if (!_closing)
            {
                HideTripleComboBadge();
                FinishLikePressProgress();
            }
        }
    }

    /// <summary>进度环满格时闪一下: 亮 → 暗 → 亮, 让"满了"这一下有存在感。</summary>
    private void PulseLikePressProgress()
    {
        if (_likePressAdorner == null) return;
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(260) };
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0.95, KeyTime.FromPercent(0)));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0.55, KeyTime.FromPercent(0.4)));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0.95, KeyTime.FromPercent(1)));
        _likePressAdorner.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void ShowTripleComboBadge()
    {
        if (TripleComboBadge == null || TripleComboBadgeScale == null) return;
        // 先摘掉上一轮的动画, 否则 HoldEnd 会把 Opacity/Scale 钉在旧值(同进度条的坑)
        TripleComboBadge.BeginAnimation(UIElement.OpacityProperty, null);
        TripleComboBadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        TripleComboBadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
        var scale = new DoubleAnimation(0.8, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        TripleComboBadge.BeginAnimation(UIElement.OpacityProperty, fade);
        TripleComboBadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        TripleComboBadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    private void HideTripleComboBadge()
    {
        if (TripleComboBadge == null) return;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (_, _) =>
        {
            TripleComboBadge.BeginAnimation(UIElement.OpacityProperty, null);
            TripleComboBadge.Opacity = 0;
        };
        TripleComboBadge.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>
    /// 开始长按: 先进入 <see cref="LikeConfirmMs"/> 的静默确认阶段(**不显示任何动画**),
    /// 确认后才由 <see cref="OnLikePressProgressTick"/> 显示进度环并扫满。
    ///
    /// ★ 环用 Adorner 画(见 PressRingAdorner): 不参与按钮布局, 画到按钮边界上也不会把按钮撑大。
    /// ★ 逐帧算而不是 DoubleAnimation: 松手/移出时要能**立即**停住。
    /// </summary>
    private void StartLikePressProgress()
    {
        EnsureLikePressAdorner(_pressedTripleButton);
        if (_likePressAdorner == null) return;

        _likePressCommitted = false;
        _likeConfirmed = false;
        _likePressStartTick = Environment.TickCount64;

        // 确认阶段的 500ms 内不显示任何动画: 先收掉环(包括上一轮残留的淡出动画)
        _likePressAdorner.BeginAnimation(UIElement.OpacityProperty, null);
        _likePressAdorner.Opacity = 0;
        _likePressAdorner.Progress = 0;
        _likePressAdorner.InvalidateVisual();

        _likePressProgressTimer ??= new DispatcherTimer { Interval = LikePressTick };
        _likePressProgressTimer.Tick -= OnLikePressProgressTick;
        _likePressProgressTimer.Tick += OnLikePressProgressTick;
        _likePressProgressTimer.Start();
    }

    /// <summary>
    /// 把进度环挂到当前按下的按钮上(换按钮时先摘掉旧的)。
    /// 没有 AdornerLayer 时静默降级 —— 不影响按钮本身的点击行为。
    /// </summary>
    private void EnsureLikePressAdorner(Button? btn)
    {
        if (btn == null) return;
        if (_likePressAdorner != null && ReferenceEquals(_likePressAdorner.AdornedElement, btn)) return;

        if (_likePressAdorner != null)
        {
            AdornerLayer.GetAdornerLayer(_likePressAdorner.AdornedElement)?.Remove(_likePressAdorner);
            _likePressAdorner = null;
        }

        var layer = AdornerLayer.GetAdornerLayer(btn);
        if (layer == null) return;
        _likePressAdorner = new PressRingAdorner(btn);
        layer.Add(_likePressAdorner);
    }

    /// <summary>按钮对应的色相: 点赞红 / 投币金 / 收藏蓝(其它按点赞处理)。</summary>
    private Color TripleAccentOf(Button? btn)
    {
        if (ReferenceEquals(btn, BtnCoin)) return ActionCoinColor;
        if (ReferenceEquals(btn, BtnFavorite)) return ActionSaveColor;
        return ActionLikeColor;
    }

    /// <summary>
    /// 两段式长按的逐帧驱动:
    ///   ① 前 <see cref="LikeConfirmMs"/>: 只确认意图, 不显示动画(这一步让"点击"和"长按"分开);
    ///   ② 确认之后: 环出现并按 <see cref="LikeProgressMs"/> 扫满, 扫满的瞬间触发三连。
    /// </summary>
    private void OnLikePressProgressTick(object? sender, EventArgs e)
    {
        if (_closing) { StopLikePressProgress(); return; }
        var elapsed = Environment.TickCount64 - _likePressStartTick;

        // 阶段 ①: 确认期 —— 什么都不画
        if (elapsed < LikeConfirmMs) return;

        // 阶段 ②: 首次进入时把环亮出来(颜色取被按按钮的色相)
        if (!_likeConfirmed)
        {
            _likeConfirmed = true;
            if (_likePressAdorner != null)
            {
                var accent = TripleAccentOf(_pressedTripleButton);
                var brush = new SolidColorBrush(accent);
                if (brush.CanFreeze) brush.Freeze();
                _likePressAdorner.Stroke = brush;
                // ★ 必须先清掉淡出动画: HoldEnd 会把 Opacity 钉在 0(见 StartLikePressProgress)
                _likePressAdorner.BeginAnimation(UIElement.OpacityProperty, null);
                _likePressAdorner.Opacity = 0.95;
            }
        }

        var p = Math.Clamp((elapsed - LikeConfirmMs) / (double)LikeProgressMs, 0, 1);
        UpdateLikePressRing(p);
        if (p >= 1)
        {
            StopLikePressProgress();
            FireTripleCombo();
        }
    }

    /// <summary>长按满 2 秒: 提交进度环, 启动三连演出与真实请求。</summary>
    private void FireTripleCombo()
    {
        _tripleComboAt = Environment.TickCount64;
        CommitLikePressProgress();
        _ = PlayTripleComboShowAsync();
        _ = RunTripleComboAsync();
    }

    /// <summary>按进度刷新那圈描边(dash 换算在 PressRingAdorner 里)。</summary>
    private void UpdateLikePressRing(double p)
    {
        if (_likePressAdorner == null) return;
        _likePressAdorner.Progress = p;
        _likePressAdorner.InvalidateVisual();
    }

    /// <summary>停掉逐帧刷新(已画出的进度保持不变)。</summary>
    private void StopLikePressProgress() => _likePressProgressTimer?.Stop();

    /// <summary>长按到点: 进度环扫满, 交给三连步骤换色。</summary>
    private void CommitLikePressProgress()
    {
        StopLikePressProgress();
        UpdateLikePressRing(1);
        _likePressCommitted = true;
    }

    /// <summary>松手/移出: 没触发三连就立即收掉; 已提交给三连则保持到三连结束。</summary>
    private void CancelLikePressProgress()
    {
        if (_likePressCommitted) return;
        StopLikePressProgress();
        _likeConfirmed = false;
        FadeOutLikePressProgress();
    }

    /// <summary>三连每完成一步换一次色: 点赞红 → 投币金 → 收藏蓝。</summary>
    private void SetLikePressProgressColor(Color color)
    {
        if (_likePressAdorner == null) return;
        var brush = new SolidColorBrush(color);
        if (brush.CanFreeze) brush.Freeze();
        _likePressAdorner.Stroke = brush;
        _likePressAdorner.InvalidateVisual();
    }

    /// <summary>三连结束(无论成败): 进度环淡出并复位, 下一次长按重新开始。</summary>
    private void FinishLikePressProgress()
    {
        _likePressCommitted = false;
        StopLikePressProgress();
        FadeOutLikePressProgress();
    }

    /// <summary>立即复位(换片/关窗): 不走淡出, 下次进来从 0 开始。</summary>
    private void ResetLikePressProgress()
    {
        _likePressCommitted = false;
        _likeConfirmed = false;
        StopLikePressProgress();
        if (_likePressAdorner != null)
        {
            _likePressAdorner.BeginAnimation(UIElement.OpacityProperty, null);
            _likePressAdorner.Opacity = 0;
            _likePressAdorner.Progress = 0;
            _likePressAdorner.InvalidateVisual();
        }
        if (TripleComboBadge != null)
        {
            TripleComboBadge.BeginAnimation(UIElement.OpacityProperty, null);
            TripleComboBadge.Opacity = 0;
        }
    }

    private void FadeOutLikePressProgress()
    {
        if (_likePressAdorner == null) return;
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        anim.Completed += (_, _) =>
        {
            // 动画收尾后把动画摘掉并把本地值落成 0 —— 否则 HoldEnd 会一直占着 Opacity,
            // 下一次长按赋值 0.95 又会被它盖掉(见 StartLikePressProgress 的说明)。
            _likePressAdorner.BeginAnimation(UIElement.OpacityProperty, null);
            _likePressAdorner.Opacity = 0;
            _likePressAdorner.Progress = 0;
            _likePressAdorner.InvalidateVisual();
        };
        _likePressAdorner.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>
    /// 一键三连: 点赞 + 投币 2 枚 + 收藏到默认收藏夹。
    ///
    /// ★ 每一步都先看本地状态: 已赞/已投币/已收藏就跳过, 三项都完成时直接提示"已经三连过了"，
    ///   不白发请求、也不重复投币(投币**不可撤销**)。
    /// ★ 收藏目标: 优先"已经收录了本视频"的夹(说明服务端已有状态, 只同步本地),
    ///   否则找标题含「默认」的夹, 再否则用列表第一个 —— 与网页端"一键收藏到默认夹"一致。
    /// ★ 任何一步失败都不中断后面两步: 三连是三个独立动作, 能完成几个算几个, 最后用一条
    ///   提示把第一个失败原因带出来(风控文案已经由 ApiClient 翻译成可照做的指引)。
    /// </summary>
    private async Task RunTripleComboAsync()
    {
        if (_closing || _tripleComboBusy) return;
        if (!Svc.Session.HasLogin) { Svc.Toast.Show("三连需要先登录"); return; }
        if (string.IsNullOrEmpty(_currentBvid) || _aid <= 0 || _isLocalPlayback || _isLive)
        {
            Svc.Toast.Show("当前内容不支持三连");
            return;
        }
        if (_isLiked && _isCoined && _isFavorited)
        {
            Svc.Toast.Show("这个视频已经三连过了");
            return;
        }

        _tripleComboBusy = true;
        try
        {
            Svc.Toast.Show("一键三连中…");
            string? firstErr = null;

            // 1) 点赞
            if (!_isLiked)
            {
                var (ok, err) = await Svc.Api.LikeVideoAsync(_currentBvid, true);
                if (ok)
                {
                    _isLiked = true;
                    _likeCount = Math.Max(0, _likeCount + 1);
                    // 弹跳与换色由 PlayTripleComboShowAsync 统一负责, 这里不再重复
                }
                else firstErr ??= err;
            }

            // 2) 投币 2 枚(官方三连的默认枚数; 已投过就跳过 —— 单稿件上限就是 2 枚)
            if (!_isCoined)
            {
                var coinCount = 2;
                var (ok, err) = await Svc.Api.CoinVideoAsync(_aid, 2, _currentBvid);
                // ★ 有些稿件(活动稿/特殊稿)只允许投 1 枚, 服务端回 "非法的投币数量";
                //   三连的语义是"投币", 枚数不是重点 —— 降级到 1 枚再试一次, 成功就照常继续。
                if (!ok && err != null && err.Contains("数量", StringComparison.Ordinal))
                {
                    coinCount = 1;
                    (ok, err) = await Svc.Api.CoinVideoAsync(_aid, 1, _currentBvid);
                }
                if (ok)
                {
                    _isCoined = true;
                    _coinCount += coinCount;
                }
                else firstErr ??= err;
            }

            // 3) 收藏到默认收藏夹
            if (!_isFavorited)
            {
                var (ok, err, folders) = await Svc.Api.GetVideoFavFoldersAsync(_aid);
                if (ok && folders is { Count: > 0 })
                {
                    var target = folders.FirstOrDefault(f => f.HasVideo)
                                 ?? folders.FirstOrDefault(f => f.Title.Contains("默认", StringComparison.Ordinal))
                                 ?? folders[0];
                    if (target.HasVideo)
                    {
                        // 服务端本来就在这个夹里, 只是本地状态过期 —— 只需要同步本地
                        _isFavorited = true;
                    }
                    else
                    {
                        var (ok2, err2) = await Svc.Api.DealFavoriteAsync(_aid, addIds: new[] { target.Id });
                        if (ok2)
                        {
                            _isFavorited = true;
                        }
                        else firstErr ??= err2;
                    }
                }
                else firstErr ??= err;
            }

            if (_closing) return;
            UpdateActionCounts();
            Svc.Toast.Show(firstErr == null ? "一键三连成功" : "三连未完成: " + firstErr);
        }
        catch (Exception ex)
        {
            App.ReportError(ex);
            Svc.Toast.Show("三连失败: " + ex.Message);
        }
        finally
        {
            // 进度条与徽章的收尾交给 PlayTripleComboShowAsync(它要保证演出时长),
            // 这里只释放并发闸。
            _tripleComboBusy = false;
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
        // 长按触发/取消过三连时, 松开这一刻的 Click 不能再去弹投币窗
        if (_tripleComboAt != 0 && Environment.TickCount64 - _tripleComboAt < 1500) return;
        if (_likePressCancelled) { _likePressCancelled = false; return; }
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
    /// <summary>
    /// 整个按钮弹一下(1 → 1.08 → 1, 360ms)。
    /// ★ 三连演出要求"三个按钮都播放动画": 只弹图标盒(PlayActionPop)幅度太小, 远看几乎察觉不到;
    ///   缩放整个按钮更明显, 而且 RenderTransform 不参与布局, 不会把旁边的按钮挤走。
    /// ★ 每次都新建 ScaleTransform(与 PlayActionPop 同一个理由: 样式里的 Freezable 可能被冻结)。
    /// </summary>
    private void PlayButtonPop(Button? btn)
    {
        if (btn == null || _closing) return;
        var scale = new ScaleTransform(1, 1);
        btn.RenderTransformOrigin = new Point(0.5, 0.5);
        btn.RenderTransform = scale;
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromPercent(0)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.15, KeyTime.FromPercent(0.4))
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

    /// <summary>
    /// 给按钮闪一圈自己的色环(完整圆角描边, 450ms 淡出后自动摘掉)。
    /// ★ 三连演出里点赞本身已经有一圈进度环; 投币/收藏没有, 只靠弹跳几乎看不出动画,
    ///   所以给它们各补一圈同色相的闪光, 让"三个按钮都在播放动画"一眼可见。
    /// ★ 每次新建 Adorner, 动画结束后从 AdornerLayer 摘掉(短生命周期, 不会越挂越多)。
    /// </summary>
    private void FlashButtonRing(Button? btn, Color color)
    {
        if (btn == null || _closing) return;
        var layer = AdornerLayer.GetAdornerLayer(btn);
        if (layer == null) return;

        var brush = new SolidColorBrush(color);
        if (brush.CanFreeze) brush.Freeze();
        var ring = new PressRingAdorner(btn) { Progress = 1, Stroke = brush };
        layer.Add(ring);

        var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(450));
        anim.Completed += (_, _) =>
        {
            try { layer.Remove(ring); } catch { /* 窗口已关 */ }
        };
        ring.BeginAnimation(UIElement.OpacityProperty, anim);
    }

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
