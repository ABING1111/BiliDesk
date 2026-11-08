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
    /// 是否正在关闭(窗口已隐藏, 但原生资源拆解尚未完成)。
    /// PlayerService 必须据此判断"不能复用" —— 否则复用到的窗口会在拆解结束时被 Close(),
    /// 表现为"点了视频又立刻自动退出"。
    /// </summary>
    public bool IsClosing => _closing;

    /// <summary>切换视频(用于 PlayerService 复用窗口场景): 停止当前播放, 加载新 bvid</summary>
    public void PlayVideo(string bvid)
    {
        if (_closing || string.IsNullOrEmpty(bvid)) return;
        if (bvid == _currentBvid && _mp.IsPlaying) return;
        try { _mp.Stop(); } catch { }
        _currentBvid = bvid;
        _isLocalPlayback = false;
        _isLive = false;
        ResetForNewMedia();
        _ = LoadVideoAsync();
    }

    // ------------------------------------------------------------ 简介折叠 + 相关视频(2026-10-12)

    /// <summary>点相关视频卡片: 换成那个视频</summary>
    public RelayCommand PlayRelatedCommand { get; }

    /// <summary>简介是否处于"展开"态。默认 false = 折叠(短简介看不出来, 长简介默认收起)。</summary>
    private bool _descExpanded;

    /// <summary>相关视频的加载代次: 换片后作废上一片的在飞请求</summary>
    private int _relatedGen;

    /// <summary>简介折叠后的最大高度(与 XAML 里 DescCollapsedHeight 同一个数)</summary>
    private const double DescCollapsedHeight = 108;

    /// <summary>
    /// 「展开简介」按钮。
    ///
    /// ★★ 判据必须是"文本**不限高**时需要多高", 所以 Measure 之前**必须先把 MaxHeight 摘掉**:
    ///   WPF 的 Measure 会遵守 MaxHeight —— 折叠态下 MaxHeight 就是 108,
    ///   于是 DesiredSize.Height 拿回来恒等于 108, 与阈值比较永远不成立
    ///   ⇒ 按钮**永远不出现**(实测: 40 行简介 + 展开按钮 Collapsed + DesiredHeight=108)。
    ///   这个坑很隐蔽: 逻辑读起来完全合理, 只有真跑一次界面才暴露。
    /// ★ 所以下面那三步的顺序不能换: 备份 → 摘掉 → 量 → 装回。
    /// ★ 另一个容易写错的替代品是读 ActualHeight: 折叠态下它也被 MaxHeight 夹住,
    ///   同样永远判不出"超没超"。必须用无上限的那次测量。
    /// ★ 宽度未知(窗口还没布局)时直接隐藏按钮: 那时的 0 宽度会算出 0 高度,
    ///   判成"不溢出"就对了, 但纯属巧合, 所以这里显式处理而不是让它自然走完。
    /// </summary>
    private void UpdateDescFold()
    {
        if (DescText == null || BtnDescFold == null) return;

        var width = DescText.ActualWidth;
        if (width <= 1)
        {
            // 还没量到宽度: 先不显示。等布局跑完由 SizeChanged 再调一次。
            BtnDescFold.Visibility = Visibility.Collapsed;
            return;
        }

        // ★ 临时摘掉 MaxHeight 再量 —— 见方法说明。try/finally 不能省:
        //   Measure 抛异常时若没装回去, 简介就会永远失去高度上限(撑满整屏)。
        var savedMax = DescText.MaxHeight;
        var needed = 0d;
        try
        {
            DescText.MaxHeight = double.PositiveInfinity;
            DescText.Measure(new Size(width, double.PositiveInfinity));
            needed = DescText.DesiredSize.Height;
        }
        finally
        {
            DescText.MaxHeight = _descExpanded
                ? (needed > DescCollapsedHeight ? needed : DescCollapsedHeight)
                : DescCollapsedHeight;
        }

        // +1 容差: 亚像素误差会让"刚好 6 行"被误判成没超, 按钮该出时不出。
        var overflows = needed > DescCollapsedHeight + 1;
        BtnDescFold.Visibility = overflows ? Visibility.Visible : Visibility.Collapsed;
        // 短简介没有"展开"可言, 状态复位, 否则下次换片会带着一个无意义的展开态
        if (!overflows) _descExpanded = false;

        ApplyDescFoldText();
    }

    /// <summary>
    /// 「展开/收起」那行文字的**唯一**写法点(文案 + 符号一起换方向)。
    ///
    /// ★ 为什么单独抽出来: 符号(⌄/⌃)与文案必须在**同一次**调用里同步更新,
    ///   而它们分属两个不同的对象(TextBlock 与 Run)。留在 UpdateDescFold 里就地写没问题,
    ///   但一旦以后新增第二个入口(比如键盘快捷键/右键菜单)忘了改符号方向,
    ///   就会出现"点了展开, 文字说收起、箭头还朝下"这种自相矛盾的按钮。
    /// </summary>
    private void ApplyDescFoldText()
    {
        if (BtnDescFoldLabel != null) BtnDescFoldLabel.Text = _descExpanded ? "收起简介" : "展开简介";
        // ⌄ = 往下展开, ⌃ = 往上收起
        if (BtnDescFoldGlyph != null) BtnDescFoldGlyph.Text = _descExpanded ? "⌃" : "⌄";
        if (BtnDescFold != null) BtnDescFold.ToolTip = _descExpanded ? "收起简介" : "展开完整简介";
    }

    /// <summary>展开/收起简介</summary>
    private void OnDescFoldClick(object sender, RoutedEventArgs e)
    {
        _descExpanded = !_descExpanded;
        UpdateDescFold();
        // 展开后要把焦点挪开: 按钮在浅色主题下有焦点框, 停在上挺难看;
        // 而且按空格(播放/暂停)会打在按钮上而不是视频。
        DescText.Focus();
    }

    /// <summary>
    /// 载入当前视频的相关视频。
    ///
    /// ★★ 2026-10-13 用户报"相关视频只有 8 个, 添加更多视频, 往下滑刷新":
    ///   接口一次就返 40 条且翻页无效(实测 ps/pn 都不影响, 见 GetRelatedAsync 的说明),
    ///   所以这里把**全量 40 条**存在 <see cref="_relatedAll"/>, 界面只挂前
    ///   <see cref="RelatedPageSize"/> 条; 滚到附近时由 <see cref="ShowMoreRelated"/>
    ///   再补一批。列表用 ObservableCollection, 追加即刷新, 不重排已有卡片。
    /// </summary>
    private async Task LoadRelatedAsync(string bvid)
    {
        if (_closing || string.IsNullOrEmpty(bvid)) return;

        // 直播/本地文件没有"相关视频"这回事(见 LoadLiveAsync 里对 DescText 的处理)
        if (RelatedPanel == null || RelatedList == null) return;

        var gen = ++_relatedGen;

        // 先清空再转圈: 不清的话上一次的卡片会一直挂在界面上, 而那批推荐与当前视频无关。
        _relatedAll = new List<VideoItem>();
        _relatedShown = 0;
        RelatedList.ItemsSource = null;
        RelatedPanel.Visibility = Visibility.Visible;
        RelatedLoadingRing.Visibility = Visibility.Visible;
        RelatedHint.Text = "正在加载…";

        var (ok, err, items) = await Svc.Api.GetRelatedAsync(bvid);
        // 代次比对: 换片后这批结果就不属于当前视频了, 丢掉(与 ClearDanmaku 那套同款闸门)
        if (_closing || gen != _relatedGen) return;

        RelatedLoadingRing.Visibility = Visibility.Collapsed;
        RelatedHint.Text = "";

        if (!ok || items == null || items.Count == 0)
        {
            // ★ 失败/为空时把整块藏起来, 而不是留一个空标题 + 一句报错:
            //   "相关视频"下面是空的比不显示更糟(看着像功能坏了)。
            //   真的想给用户一个交代, 那该走 Toast, 而这属于后台补充信息, 不该打扰。
            RelatedPanel.Visibility = Visibility.Collapsed;
            // 只写 errors.txt 不弹窗: 相关视频是后台补充信息, 失败不该打断看片。
            // 包一层 Exception 是因为 App.ReportError 只收 Exception(它的唯一用途就是落盘),
            // 这里不去动那个公共签名 —— 加一个 string 重载会让人以为"失败可以不记录"。
            App.ReportError(new InvalidOperationException($"相关视频加载失败({bvid}): {err ?? "未知错误"}"));
            return;
        }

        _relatedAll = items;
        // 先挂第一批(与旧行为一致: 打开播放器先看到 8 条, 不一下铺满 40 条)
        _relatedShown = Math.Min(RelatedPageSize, items.Count);
        RelatedList.ItemsSource = items.GetRange(0, _relatedShown);
    }

    /// <summary>
    /// 信息栏滚动: 滚到相关视频附近时把下一批补上(用户要求"往下滑刷新出更多相关视频")。
    ///
    /// ★ 判据是"相关视频列表的**底部**离视口底部还有不到一屏", 不是"滚到底":
    ///   等用户真的滚到最底才补, 会先出现"列表空了半屏"的空白期, 看着像没有了。
    /// ★ 只在往下滚的方向补: 往上滚回来时不该再 append(已经补过的就在那儿)。
    ///   <see cref="ShowMoreRelated"/> 本身幂等, 这里加方向判断是省掉无谓的 ItemsSource 重挂 ——
    ///   每次重挂都会让 ItemsControl 重新生成全部容器, 一屏卡片会闪一下。
    /// </summary>
    private void OnInfoScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_closing) return;
        if (sender is not ScrollViewer sv) return;
        // 往上滚不补(相关视频与评论都是"向下无限加载")
        if (e.VerticalChange < 0) return;

        MaybeShowMoreRelated(sv);
        // 评论翻页(issue #55, 2026-11-08): 不在评论 tab / 详情页打开时由内部闸门挡住
        MaybeLoadMoreComments(sv);
        // 评论内容窗口: 释放离屏评论的正文/图片, 防止无限加载把内存堆到上百 MB
        QueueCommentContentWindow();
    }

    /// <summary>
    /// 相关视频补页(逻辑与原 OnInfoScrollChanged 完全一致, 只是拆出来给两条加载路径共用入口)。
    /// ★ 判据是"相关视频列表的**底部**离视口底部还有不到一屏", 不是"滚到底":
    ///   等用户真的滚到最底才补, 会先出现"列表空了半屏"的空白期, 看着像没有了。
    /// </summary>
    private void MaybeShowMoreRelated(ScrollViewer sv)
    {
        if (_closing || _relatedShown >= _relatedAll.Count) return;
        // 评论 tab 下相关视频整块是隐藏的(IntroPanel 被折叠, 但 RelatedPanel 自身的
        // Visibility 仍是 Visible) —— 这里要显式挡一下, 否则滚评论也会一直给相关视频补页。
        if (CommentsPanel != null && CommentsPanel.Visibility == Visibility.Visible) return;
        // 换片后 _relatedAll 是空的, 上面的闸门已经挡住; 这里再确认面板是显示的,
        // 免得在"评论 tab"下也补(相关视频属于简介 tab, 评论 tab 下整块是隐藏的)。
        if (RelatedPanel == null || RelatedPanel.Visibility != Visibility.Visible) return;

        var targetBottom = RelatedList.TransformToVisual(sv)
            .Transform(new Point(0, RelatedList.ActualHeight)).Y;
        if (targetBottom - (sv.VerticalOffset + sv.ViewportHeight) < sv.ViewportHeight)
            ShowMoreRelated();
    }

    /// <summary>相关视频每批展示几条(滚动到附近时按这个量追加)</summary>
    private const int RelatedPageSize = 8;

    /// <summary>当前视频的相关视频**全量**(接口一次给 40 条; 界面只挂前 <see cref="_relatedShown"/> 条)</summary>
    private List<VideoItem> _relatedAll = new();

    /// <summary>当前已经挂到界面上的条数; 换片时归零</summary>
    private int _relatedShown;

    /// <summary>
    /// 再展示一批相关视频(信息栏滚到相关视频附近时调)。
    ///
    /// ★ 幂等 + 到顶即停: 已经全展示完时什么都不做, 不会重复 append。
    /// ★ 换片后 <see cref="_relatedAll"/> 是空的, 这里直接返回 —— 不会把上一片的
    ///   推荐补进新片的列表(那是"点下去跳到不相干视频"的老 bug)。
    /// </summary>
    private void ShowMoreRelated()
    {
        if (_closing || _relatedShown >= _relatedAll.Count) return;
        var next = Math.Min(_relatedShown + RelatedPageSize, _relatedAll.Count);
        var shown = _relatedAll.GetRange(0, next);
        _relatedShown = next;
        RelatedList.ItemsSource = shown;
    }

    /// <summary>相关视频卡片的点击: 切到那个 bvid</summary>
    private void PlayRelated(object? param)
    {
        // ★ 必须真的转成 string 再用: CommandParameter 从 XAML 过来是 object,
        //   直接拿它和 _currentBvid 比是**引用比较** —— 编译器会警告 CS0252,
        //   而运行时几乎永远不相等, 表现为"点了相关视频没反应"。
        var bvid = param as string;
        if (_closing || string.IsNullOrEmpty(bvid)) return;
        // 点自己没意义(接口那边已经滤掉了, 这里再挡一道是防"同一片快速点两次")
        if (bvid == _currentBvid) return;
        PlayVideo(bvid);
    }

    /// <summary>
    /// 打开一个直播间。
    /// 与点播的区别: 直播没有总时长/进度、没有弹幕回放、也没有三连与历史进度上报,
    /// 所以互动按钮全部禁用(复用 _isLocalPlayback 那套判断), 进度条置灰。
    /// </summary>
    public void PlayLive(long roomId, string? roomTitle = null)
    {
        if (_closing || roomId <= 0) return;
        if (_isLive && roomId == _liveRoomId && _mp.IsPlaying) return;
        try { _mp.Stop(); } catch { }
        _currentBvid = "";
        _isLocalPlayback = true;   // 没有在线上下文: 不打接口、不互动、不上报
        _isLive = true;
        _liveRoomId = roomId;
        ResetForNewMedia();
        if (!string.IsNullOrEmpty(roomTitle)) VideoTitleText.Text = roomTitle;
        _ = LoadLiveAsync(roomId);
    }

    /// <summary>连接直播间: 取直播流地址 → 起播</summary>
    private async Task LoadLiveAsync(long roomId)
    {
        ShowLoading("正在连接直播间…");
        FailOverlay.Visibility = Visibility.Collapsed;

        try
        {
            if (!string.IsNullOrEmpty(VideoTitleText.Text))
            {
                Title = VideoTitleText.Text + " - BiliDesk";
                TitleText.Text = VideoTitleText.Text;
            }
            ViewCountText.Text = "--";
            DanmakuCountText.Text = "--";
            PubDateText.Text = "直播中";
            UpNameText.Text = "直播间 " + roomId;
            UpDescText.Text = "B 站直播";
            DescText.Text = "这是一个直播间。弹幕实时来自直播间, 没有进度条与历史记录, 互动按钮在直播中不可用。";
            // 直播没有"相关视频"(接口只认视频稿件 bvid), 直接把整块藏掉,
            // 否则会停在上一片残留的推荐列表上 —— 那是**别的视频**的推荐。
            ClearRelatedPanel();
            UpAvatarBox.Background = (Brush)FindResource("AccentSoftFillBrush");
            UpAvatarFallback.Visibility = Visibility.Visible;

            // 直播没有总时长: 进度条禁用 + 时间位写"直播中"。
            // 注意 UpdateProgressUi 里有 `Length > 0` 的判断, 直播的 Length 一直是 0,
            // 所以它本来就不会去覆盖这里显示的文案。
            ProgressSlider.IsEnabled = false;
            TimeText.Text = "直播中";
            // 直播清晰度: 档位要等取流接口返回才知道(g_qn_desc), 先清空,
            // 否则按钮/弹层里还挂着上一条点播视频的档位。
            _qualities.Clear();
            FillQualityMenu(0);
            // 直播没有可选音轨: 清掉列表, 按钮写"直播"(与清晰度按钮一致)
            _audioIds.Clear();
            _currentAudioId = 0;
            FillAudioMenu(0);
            if (AudioText != null) AudioText.Text = "直播";
            if (AudioHintText != null) AudioHintText.Text = "直播的音质由主播推流决定, 不可切换";
            // 取流成功后会按 g_qn_desc 填入真实档位; 这一句只是"还没拿到时"的占位。
            if (QualityText != null) QualityText.Text = "直播";
            if (CommentHint != null) CommentHint.Text = "直播没有评论区";
            UpdateButtonState();

            var (ok, err, url, actualQn, qualities) = await Svc.Api.GetLivePlayUrlAsync(roomId, _liveQn);
            if (_closing) return;
            if (!ok || string.IsNullOrEmpty(url))
            {
                StopLiveDanmaku();
                ShowFail(err ?? "无法获取直播流地址");
                return;
            }
            _lastPlayUrl = url;
            _lastAudioUrl = null;   // 直播是单流, 没有独立的音轨

            // 直播清晰度菜单: 以接口给的 g_qn_desc 为准(直播 qn 与点播不是同一套)
            _qualities.Clear();
            _qualities.AddRange(qualities);
            if (actualQn > 0) { _currentQn = actualQn; _liveQn = actualQn; }
            FillQualityMenu(actualQn);

            // ★ 直播弹幕(2026-11-13): 与取流并行启动, 失败也不影响播放。
            //   先清掉上一次直播留在画布上的弹幕 —— 重连会把媒体时间归零, 旧弹幕的时间轴对不上。
            ResetLiveDanmakuLayer();
            StartLiveDanmaku(roomId);
            // 主播信息面板: 与取流并行, 晚一点回填不影响播放
            _ = LoadLiveRoomInfoAsync(roomId);

            if (!PlayLiveStream(url, roomId))
            {
                StopLiveDanmaku();
                ShowFail("直播流播放失败, 可能已经下播");
            }
        }
        catch (Exception ex)
        {
            ShowFail("打开直播间失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 用直播流地址起播(初次连接与"切换清晰度"共用)。
    ///
    /// ★ 直播流地址必须带 Referer / UA —— 直播 CDN 比点播校验得更严:
    ///   不带 Referer 时常见表现就是"能拿到地址但播不出来"(403/直接 EOF)。
    /// ★ 缓冲值走 BufferPolicy 统一决策(手动档取 max(用户值,1000), 自动档 2000)。
    /// </summary>
    private bool PlayLiveStream(string url, long roomId)
    {
        var liveCaching = BufferPolicy.Resolve(BufferMediaKind.Live, url, shortVideo: false,
            Svc.Settings.NetworkCachingMs, Svc.Settings.AutoBufferPolicy);
        var liveOptions = new List<string>
        {
            ":no-video-title-show",
            ":http-referrer=https://live.bilibili.com/" + roomId,
            ":http-user-agent=" + PlayerUserAgent,
            // 直播是持续流: 加大缓存, 别让 LibVLC 因为"看起来卡住"就早早判定结束
            ":network-caching=" + liveCaching,
            ":live-caching=" + liveCaching
        };

        var media = new Media(_libVLC, new Uri(url), liveOptions.ToArray());
        // 直播是持续流, 没有可 seek 的时间轴; 标成"可靠源"让看门狗别去重建它
        _isReliableSeekSource = true;
        return PlayMedia(media);
    }

    /// <summary>
    /// 清空并隐藏「相关视频」整块。
    ///
    /// ★ 必须在每条**没有在线上下文的**播放路径上调用(直播 / 本地文件 / 换片起点):
    ///   否则上一批推荐会一直挂在界面上, 而那批是**别的视频**的推荐 ——
    ///   点下去跳到不相干的视频, 却没有任何迹象说明它过期了。
    /// ★ 顺带把代次 +1: 让还在飞的请求回来时立刻作废, 不会把旧结果又灌进来。
    /// </summary>
    private void ClearRelatedPanel()
    {
        _relatedGen++;
        // 换片/切直播: 缓存也一起丢。不清的话 OnInfoScrollChanged 里的闸门
        // (_relatedShown >= _relatedAll.Count) 会看到上一片还有"没展示完"的条数,
        // 于是把上一片的推荐补进新片的列表 —— 那是"点下去跳到不相干视频"的老 bug。
        _relatedAll = new List<VideoItem>();
        _relatedShown = 0;
        if (RelatedList != null) RelatedList.ItemsSource = null;
        if (RelatedPanel != null) RelatedPanel.Visibility = Visibility.Collapsed;
        if (RelatedLoadingRing != null) RelatedLoadingRing.Visibility = Visibility.Collapsed;
        if (RelatedHint != null) RelatedHint.Text = "";
    }

    /// <summary>
    /// 播放本地文件(离线缓存页用)。
    /// 与在线播放的区别: 不查视频信息/播放地址、不加载弹幕、不上报历史、不查点赞收藏状态 ——
    /// 本地文件没有这些上下文。所以这里直接构造一个 FromPath 的 Media 丢给播放器。
    /// </summary>
    public void PlayLocalFile(string path)
    {
        if (_closing || string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try { _mp.Stop(); } catch { }
        _currentBvid = "";
        _isLocalPlayback = true;
        _isLive = false;
        ResetForNewMedia();
        // 本地文件没有 bvid, 自然也没有相关视频
        ClearRelatedPanel();
        LoadLocalFile(path);
    }

    /// <summary>
    /// 把"本片源要请求的清晰度"快照成设置里的「默认画质」。
    ///
    /// ★ 为什么单独一个方法: 有**三**条起播路径需要它, 各写一遍必然漏一条 ——
    ///   构造函数(带 bvid 直接开)、ResetForNewMedia(复用窗口换片)、以及将来新增的入口。
    ///   实际漏过一次的原形就摆在眼前: 只放 ResetForNewMedia 时, "带 bvid 开新窗口"这条
    ///   路径拿到的是字段初值而不是用户设置。
    ///
    /// ★ 为什么不沿用上一个视频手动切到的档位: 那个选项叫**默认**画质 —— 手动切清晰度只对
    ///   当前这一条有效, 下一条仍然从用户选定的默认档起播(B 站客户端同款语义)。
    ///
    /// ★ 为什么走 <see cref="QualityPreference.RequestQn"/> 而不是直接赋值:
    ///   设置页把「自动(最高可用)」存成哨兵值 0, 而发给接口的必须是"最高可用档"(120/4K,
    ///   见 QualityPreference.HighestQn —— 封顶在能正常渲染的档, 不含杜比视界/HDR/8K)。
    ///   同时 RequestQn 内部会 Normalize —— 配置被手改坏 / 存着已下线的档位时,
    ///   不会把一个谁也不认识的 qn 发进取流请求。
    /// </summary>
    private void ApplyPreferredQuality()
    {
        _currentQn = QualityPreference.RequestQn(Svc.Settings.PreferredQualityQn);
        // ★ 顺手把控件栏那个清晰度按钮的文案摆成"**本次要请求的**档位"(2026-10-04)。
        //
        // 为什么必须在这里写一次: XAML 里 `QualityText` 的初值是写死的 "1080P"。
        // 用户把默认画质设成 720P / 4K 之后, 从"点开视频"到"取流成功调用 FillQualityMenu"
        // 之间——以及**取流直接失败**那条路(ShowFail 后 return, 根本不会调 FillQualityMenu)
        // ——按钮上都会挂着一个 "1080P", 而那既不是用户选的档、也不是正在播的档。
        // 之前写死 qn=80 时它与请求一致所以看不出来, 有了这个设置项就变成实打实的错报。
        // 本地文件/直播那两条路随后会各自用 "本地"/"直播" 覆盖掉它, 不受这里影响。
        if (QualityText != null)
            QualityText.Text = QualityPreference.ShortLabelOf(Svc.Settings.PreferredQualityQn);
    }

    /// <summary>
    /// 换片源前把"上一条视频的痕迹"清干净。
    /// 尤其是 _commentsLoaded / CommentList.ItemsSource —— 原来没清, 于是同一个播放器窗口
    /// 换视频之后评论 tab 里一直挂着上一条视频的评论(而且不会再重新加载)。
    /// </summary>
    private void ResetForNewMedia()
    {
        _indexInLib = 0;
        // 音质切换闸必须在这里归零: 与 _switchingQuality 不同, SwitchAudioAsync 的 finally
        // 可能因为换片路径提前 return 而没跑到(它内部有 _closing/gen 判断),
        // 留着 true 会让新片的音质按钮永远点不动。
        _switchingAudio = false;
        _fillingAudioMenu = false;
        // 换片: 音频链路诊断重新记一次(每个片源一条, 见 LogAudioStateOnce)
        _audioStateLogged = false;
        // 「听视频」的音轨地址属于上一条视频 —— 不清的话换成新片时会拿旧音轨去播。
        // ★ _isListenMode 本身**不清**(登记在 PersistentFields): 用户切到纯音频连听几集是常态;
        //   新片的音轨地址在 LoadVideoAsync 里重新快照(见 ApplyListenAudioUrl)。
        _listenAudioUrl = null;
        _listenResumeMs = 0;
        _listenWaveTick = 0;
        // 音质: 可选音轨属于上一条视频 —— 不清的话换成新片时弹层还列着上一条的无损档。
        _audioIds.Clear();
        _currentAudioId = 0;
        _coverUrl = "";
        // ★ 每个片源开始时都把"要请求的清晰度"快照成设置里的「默认画质」(2026-10-04)。
        //   放在这里而不是只放构造函数里, 是为了让"设置页改完 → 点开下一个视频"立刻生效,
        //   不必重启。
        ApplyPreferredQuality();
        _danmaku.Clear();
        _danmakuRaw.Clear();
        // 相关视频同属"上一条视频"的界面内容: 换片瞬间就必须清掉, 不能等新片的
        // 异步请求回来 —— 那中间几百毫秒里, 界面上挂着的还是**上一条**的推荐,
        // 用户一点就跳到一个跟当前标题完全对不上的视频。
        ClearRelatedPanel();
        // SponsorBlock: 上一片的片段数据与"已处理"记录全部作废。
        // 掐掉那个查询同样重要 —— 否则上一个视频的响应回来了会写进新视频的状态。
        CancelSponsorFetch();
        _sponsorRaw = Array.Empty<SponsorSegment>();
        _sponsorActive = Array.Empty<SponsorSegment>();
        _sponsorHandled.Clear();
        _lastSkipSeg = null;
        HideSkipNotice();
        // 进度条上的旧色块也要立刻清掉: 本地文件/直播这两条路根本不会去取片段,
        // 不主动刷新的话上一条视频的色块会一直挂在进度条上。
        RenderSponsorMarks();
        _totalMs = 0;
        // 新的直播/视频都从服务端默认清晰度开始; 同一直播的重连/重播不经过这里, 会沿用上次选的档。
        _liveQn = 0;
        _currentCid = 0;
        _aid = 0;
        // 换片源: 上一片的合流任务立刻作废(否则会白下几十兆, 也可能把结果写进新片的键)
        CancelRemuxPrefetch();
        _ownerMid = 0;
        _commentsLoaded = false;
        // 评论翻页状态属于"上一条视频": 游标必须清, 否则新片会拿旧游标去要第二页,
        // 拿回来的是别人家评论。失败标记/防重入闸也一起复位。
        _commentNext = 0;
        _commentHasMore = false;
        _commentLoadingMore = false;
        _commentLoadMoreFailed = false;
        ResetCommentCardParts();
        _commentItems.Clear();
        _commentContentWindowTimer?.Stop();
        _commentContentWindowTimer = null;
        // 下载状态属于"上一条视频"(见 UpdateActionCounts 里那个绿色终态)
        _cacheDownloaded = false;
        // 评论总数属于"上一条视频" —— 不清的话换了片子那句"共 N 条评论"还挂着旧数字,
        // 而排序入口的可见性正是跟着它走的。排序**方式**(_commentSort)不清: 那是用户习惯。
        _commentTotal = 0;
        // 本次实际排序回到用户偏好(_commentSort); 新视频重新试热门, 热门空再回退成时间
        _effectiveCommentSort = _commentSort;
        _commentReturnRpid = 0;
        _videoBackdropGuardTicks = 0;
        // 音频自愈的"上次检查时刻"(_audioHealCheckAt)不清: 它是宽限期计时基准,
        // 跨片保持最多把新片的第一次检查推迟 1.5s; 清零反而会在 aout 未就绪时立刻误检。
        // 遮罩安全网的"上次位置"同理要归零: 留着上一片的进度会让新片的第一次 TimeChanged
        // 被当成"在推进", 把刚弹出的加载遮罩误收掉。
        _lastLoadingRevealMs = -1;
        UpdateCommentSortUi(visible: false);
        UpdateCommentMoreUi();
        // 换片时把评论详情也收掉(tab 文案与返回链接一并还原),
        // 否则新片子一进来右侧就挂着上一条视频的评论详情页
        if (CommentDetailPanel != null) CloseCommentDetail();
        if (CommentList != null) CommentList.ItemsSource = null;
        if (CommentHint != null) CommentHint.Text = "加载评论中…";
        // 上一条视频没写完的评论草稿不能带到下一条去(用户点开新视频发现输入框里
        // 还留着给上一个视频写了一半的话, 会以为是自己点错了)
        CommentDraft?.Clear();
        _postingComment = false;
        // 进度上报的节流状态也要重置, 让新视频的第一条云端历史能立刻落地
        Svc.HistorySync.Reset();
        // 直播弹幕连接属于"上一条片源": 换片/换直播间必须立即停。
        // ★ 渲染层虽然会按 _isLive 判断, 但不主动断开就会白占一条 WS 连接,
        //   切走之后还在持续收别人的弹幕。
        StopLiveDanmaku();
        // 清掉屏幕上残留的弹幕(防泄漏)并重置泳道占用
        DanmakuCanvas.Children.Clear();
        ResetLanes();
        TimeText.Text = "00:00 / 00:00";
        ProgressSlider.Value = 0;
        // 新片子当然还没播完: 清掉结尾状态与"重播"遮罩, 否则切下一条视频时会挂着上一段的遮罩
        _ended = false;
        _eofBlackActive = false;
        // "播完后的跳转"那个闸同样要清: 它描述的是**上一条**视频末尾那次操作。
        _seekAfterEndedGuard = false;
        if (VideoAreaRoot != null) VideoAreaRoot.Background = VideoAreaBackBrush;
        _seekGraceUntil = 0;
        // 清掉跳转看门狗与"提前结束自救"的上一片痕迹
        _pendingSeekMs = -1;
        _seekObservedMs = -1;
        _seekFromMs = -1;
        _seekElsewhereSeenMs = -1;
        _seekBounceSeenMs = -1;
        _seekBounceAt = -1;
        _seekIssuedAt = -1;
        _lastSeekTargetMs = -1;
        _resumeByStartTime = false;
        _eofSeekRebuildTried = false;
        _progressDragStartValue = 0;
        // 拖动气泡也要收: 换片时若正好在拖进度条, 气泡会带着上一条视频的时间留在屏幕上。
        _progressDragMoved = false;
        HideProgressDragHint();
        // ★ 长按快进也要收: 若停在长按态换片, MediaPlayer 上的速率会**跟着留到新片子**
        //   (速率是 MediaPlayer 的属性, 不随 Media 重置 —— 实测)。所以这里必须先恢复。
        //   顺带把 _longPressFired 清掉 —— 否则新片的第一次 Up 会走进"结束快进"分支。
        StopFastForward();
        _longPressFired = false;
        // 复位"该恢复到几倍速"的记录: 它只描述**本片**那次长按, 不该带到下一片
        // (下一片起播速率由 _mp.Rate 自己决定, 这里只是清掉上一个手势的账)。
        _rateBeforeFastForward = 1.0f;
        RestoreVolumeAfterSeek();   // 上一片若停在 seek 静音中, 别把静音带进新片
        // 心跳间隔也要还原: 上一片可能停在了"跳转等待期"的 100ms 上没来得及还原
        if (_progressTimer != null) _progressTimer.Interval = ProgressTickIdle;
        _lastKnownMs = 0;
        _eofResumeCount = 0;
        _lastEofResumeMs = 0;
        // 卡顿检测状态同样要清: 换片后上一片"时间停在哪"的参照已经没意义, 留着会立刻误判成卡顿
        _stallLastMs = -1;
        _stallTicks = 0;
        // 进度显示的两个基线一并清: 不清的话"尾部只进不退"钳制会把新片钉在上一片的旧进度上
        _anchorMs = -1;
        _displayedMs = -1;
        if (EndOverlay != null) EndOverlay.Visibility = Visibility.Collapsed;
        // 进度条默认可用; 直播那条路会再把它置灰
        if (ProgressSlider != null) ProgressSlider.IsEnabled = true;
        // 弹幕按钮的可用性取决于 _isLive / _isLocalPlayback, 调用方都已在这之前设好这两个标志
        UpdateDanmakuButton();
        // 直播看聊天列表, 点播看信息面板 —— 切换点就是换片源(调用方已设好 _isLive)
        UpdateLiveLayout();
        // 「听视频」的可用性同样取决于那两个标志(直播/本地文件没有独立音轨)。
        // ★ 必须在 _isListenMode 已经确定之后调: 它内部会在"不支持聆听"时顺手退出聆听态,
        //   否则聆听遮罩会盖在一个根本没有音轨的本地文件上。
        UpdateListenButtonState();
        // 「稍后再看」的点亮态属于"上一条视频", 一起清掉。放这里而不是 UpdateUiFromDetail:
        // 直播 / 本地离线缓存那两条路不走详情流程, 不清的话会挂着上一条视频的强调色。
        SetInWatchLater(false);
        // 合集: 换片就要把"上一个合集的列表"整个丢掉, 否则普通视频那一瞬间会挂着旧合集的条目
        // (点开面板那一帧还看得见)。★ 面板的**展开态**(_seasonOpen)不在这里清 —— 它登记在
        // PersistentFields 里: 用户连着看几集是常态, 每换一集都把面板收起来很烦。
        _currentSeasonEpisode = null;
        _season = null;
        _hasSeason = false;
        // ★ 这里**不**清 _switchingSeasonEpisode: 它的复位点是 SwitchToSeasonEpisodeAsync 的
        //   finally(与 _switchingQuality 同款)。本方法正是被那次换片调用的 —— 在这里清掉它,
        //   整个 await LoadVideoAsync() 期间闸门就是开的, 连点两集照样能叠起来跑。
        ClearSeasonUi(collapsePanel: false);
        // 换片时**不关**面板(collapsePanel: false): 用户开着面板点下一集是常态, 关掉会在
        // 整个加载过程里闪一下。列表内容已清空, 这里就地显示"加载中", 等 ApplySeason 灌数据。
        // 真到了没有合集的新片, ApplySeason 会把面板整个收起来。
        if (_seasonOpen)
        {
            if (SeasonHint != null) SeasonHint.Text = "正在获取合集…";
            if (SeasonLoadingRing != null) SeasonLoadingRing.Visibility = Visibility.Visible;
        }
        UpdateSeasonButtonState();

        // 分P: 与合集同款处理 —— 换片先把上一片的分P 列表整个丢掉, 入口按钮也收起来,
        // 等新详情到了由 ApplyPages 重新决定要不要露出来。
        // 弹层强制关掉: 分P 弹层是 Popup(独立 HWND), 不关的话它会**浮在下一条视频上面**,
        // 里面还是上一条的分P 列表 —— 而合集面板是窗口内的 Grid, 收起就跟着一起没了。
        _pages = new List<VideoPage>();
        _hasPages = false;
        ClosePagesPopup();
    }

    /// <summary>
    /// 把合集列表的内容清干净(入口按钮、标题、副标题、提示、加载圈、选中项、**条目本身**)。
    ///
    /// 单独抽出来是因为它有多个调用点 —— 换片时、详情加载完发现没有合集时。
    /// 两处各写一遍必然漏(尤其"底部加载圈没停"这种, 表现是加载完了圈还在转)。
    /// </summary>
    /// <param name="collapsePanel">
    /// 是否**顺便把面板收起来**。换片时必须传 false: 用户开着面板点下一集是常态,
    /// 换片期间把面板收掉会在整个加载过程里闪一下, 体验上像是"点一下面板没了"。
    /// 只有在"确定这个视频没有合集"时才收面板。
    /// </param>
    private void ClearSeasonUi(bool collapsePanel)
    {
        if (collapsePanel && SeasonPanel != null) SeasonPanel.Visibility = Visibility.Collapsed;
        if (SeasonTitleText != null) SeasonTitleText.Text = "";
        if (SeasonMetaText != null) SeasonMetaText.Text = "";
        if (SeasonHint != null) SeasonHint.Text = "";
        if (SeasonLoadingRing != null) SeasonLoadingRing.Visibility = Visibility.Collapsed;
        // ★★ 必须把 ItemsSource 也清掉, 不能只清 SelectedIndex(2026-10-02 探针实测出来的):
        //   换片时不关面板(见上面 collapsePanel 的说明), 于是从"点了下一集"到
        //   LoadVideoAsync 真正灌回新数据之间有几秒的窗口期 —— 那段时间面板是**开着**的。
        //   只清选中项的话, 列表里挂着的是**上一个合集**的条目, 而底部还写着"正在获取合集…",
        //   自相矛盾。更糟的是此时点一条旧条目会走到 OnSeasonEpisodeSelected:
        //   它的判据是 ep.Bvid != _currentBvid, 而 _currentBvid 已经是新视频了 ⇒ 恒真
        //   ⇒ 会拿旧合集的条目去换片, 播出一个跟用户点的东西无关的视频。
        //   (ApplySeason 保证重新灌数据, 所以这里清成 null 是安全的。)
        if (SeasonList != null)
        {
            SeasonList.ItemsSource = null;
            SeasonList.SelectedIndex = -1;
        }
    }

    // ------------------------------------------------------------ 换片重置的自检

    /// <summary>
    /// 「每片状态」字段清单 = <see cref="ResetForNewMedia"/> 必须清掉的那些。
    ///
    /// 为什么需要这份清单 + 自检: 换片重置那 60 多行是**手工维护**的, 而播放器是"换片就清"
    /// 的结构 —— 漏清一个字段不报错、不告警, 只会让新片悄悄继承上一片的残留(三连状态、
    /// 清晰度、seek 看门狗的锚点……), 表现为"换个视频还带着上一个的状态", 极难定位。
    ///
    /// 与 <see cref="StopAllTimers"/> 的区别: 定时器可以**自动**停(全停即可), 但重置**不能自动** ——
    /// 每个字段该清成什么值不一样, 有的还要连带清集合/退订/刷 UI, 反射猜不出来。
    /// 所以这里退一步: 清单仍然手写, 但用自检保证"新增字段时不会忘了登记"。
    /// </summary>
    private static readonly string[] PerMediaStateFields =
    {
        // 三连 / 关注 / 稍后再看等"上一条视频"的态
        nameof(_isLiked), nameof(_isFollowed), nameof(_isFavorited), nameof(_favBusy),
        nameof(_isInWatchLater), nameof(_watchLaterBusy), nameof(_isCoined),
        nameof(_likeCount), nameof(_coinCount), nameof(_favCount), nameof(_cacheDownloaded),
        // 播放源标识
        nameof(_isLocalPlayback), nameof(_isLive), nameof(_liveRoomId), nameof(_liveQn), nameof(_currentCid),
        nameof(_aid), nameof(_ownerMid), nameof(_ownerName), nameof(_ownerFace),
        nameof(_videoTitle), nameof(_currentQn), nameof(_currentBvid),
        nameof(_coverUrl),           // 封面属于"这一片"(聆听态的画面锚点)
        // 「听视频」的音轨地址与恢复进度: 换片后要重新取, 留着上一条的会播错东西
        nameof(_listenAudioUrl), nameof(_listenResumeMs), nameof(_listenWaveTick),
        nameof(_lastPlayUrl), nameof(_lastAudioUrl),   // 出错诊断日志要打"当时正在播的地址"
        // 进度 / 时长 / EOF 自救
        nameof(_totalMs), nameof(_lastKnownMs), nameof(_eofResumeCount), nameof(_lastEofResumeMs),
        nameof(_resumeAfterSwitchMs), nameof(_ended), nameof(_eofBlackActive), nameof(_anchorMs), nameof(_anchorWall),
        nameof(_displayedMs),
        // "播完后的跳转"那两个闸(见 SeekTo 里 _ended 的分支): 描述的是本片末尾那次操作
        nameof(_seekAfterEndedGuard),
        // 跳转看门狗
        nameof(_pendingSeekMs), nameof(_seekDeadline), nameof(_seekRetries),
        nameof(_seekObservedMs), nameof(_seekElsewhereSeenMs), nameof(_seekFromMs),
        nameof(_seekGraceUntil), nameof(_seekMuteSavedVolume), nameof(_isSeeking),
        nameof(_seekRebuildTried), nameof(_seekBounceSeenMs), nameof(_seekBounceAt),
        nameof(_seekIssuedAt), nameof(_lastSeekTargetMs), nameof(_resumeByStartTime),
        nameof(_eofSeekRebuildTried), nameof(_progressDragStartValue),
        nameof(_progressDragMoved),
        nameof(_audioHealCheckAt),
        nameof(_volumeRestoreDeadline), nameof(_volumeRestoreTries),
        nameof(_lastLoadingRevealMs),
        // 卡顿检测
        nameof(_stallLastMs), nameof(_stallTicks),
        // 弹幕
        nameof(_indexInLib), nameof(_audioStateLogged), nameof(_lastLaneCalcWidth),
        nameof(_lastLaneCalcHeight),
        // 直播聊天列表的"跟随底部"开关: 每次换片源/换直播间都重新开始跟随
        nameof(_liveChatAutoScroll),
        // 音质(2026-11-06): 可选音轨/当前音轨/切换闸都属于"这一片" ——
        // 换片后必须重取, 留着上一条视频的会让弹层列着别人家的无损档。
        nameof(_audioIds), nameof(_currentAudioId),
        // 异步代次(旧请求回来时必须已作废)
        nameof(_remuxGen), nameof(_loadingGen), nameof(_kaomojiIndex),
        // 界面交互态
        nameof(_switchingQuality), nameof(_fillingQualityMenu), nameof(_updatingSlider),
        // 音质切换的两个 UI 闸(2026-11-06): 与 _switchingQuality/_fillingQualityMenu 同类。
        // ★ 与 _switchingQuality 略有不同: SwitchAudioAsync 的 finally 可能因换片路径提前
        //   return 而没跑到, 所以这两个**在 ResetForNewMedia 里显式清零**(不能只靠 finally)。
        nameof(_switchingAudio), nameof(_fillingAudioMenu),
        nameof(_commentsLoaded), nameof(_postingComment), nameof(_longPressFired),
        // 长按快进的状态与 _longPressFired 同类(都是"这一次手势"的瞬时态): 换片必须复位。
        // ★ _rateBeforeFastForward 也登记在这里 —— 它决定"松手恢复到几倍速",
        //   不跟着换片复位的话, 新片子的第一次长按会恢复到上一片记下的速率。
        nameof(_isFastForwarding), nameof(_rateBeforeFastForward),
        // 长按快进的目标倍速(2026-11-03): 同属"这一次长按"的瞬时态, 换片必须复位。
        nameof(_fastForwardTarget),
        nameof(_commentTotal),
        // 评论翻页(2026-11-08, issue #55): 游标/是否还有下一页/在途闸/失败标记都属于"这一片"
        nameof(_commentNext), nameof(_commentHasMore),
        nameof(_commentLoadingMore), nameof(_commentLoadMoreFailed),
        nameof(_effectiveCommentSort),
        nameof(_suppressSingleClick), nameof(_controlsHidden), nameof(_isFullscreen),
        // ---- 2026-10-31 补登记(此前 errors.txt 里一直被自检点名) ----
        nameof(_descExpanded),          // 简介折叠态: UpdateUiFromDetail 每片复位
        nameof(_relatedGen),            // 相关视频加载代次: LoadRelatedAsync 自增
        nameof(_relatedShown),          // 相关视频已展示条数: LoadRelatedAsync/ClearRelatedPanel 复位
        nameof(_isReliableSeekSource),  // seek 可靠性标志: BuildMedia 按片源设置
        nameof(_dmClockRate),           // 弹幕时钟倍率: 换片后由 CalibrateDanmakuClock 重新校准
        nameof(_commentReturnRpid),     // 评论详情返回锚: 属于"上一条评论详情", 换片必须归零
        nameof(_videoBackdropGuardTicks), // vout 底色守护计数: 3 秒自收尾的瞬态
        nameof(_audioHealCheckAt),      // 音频自愈上次检查时刻: 跨片保持宽限意义, 不必清(见该字段)
        // 合集: 「这个视频有没有合集」属于"这一片"的属性, 换片必须重算。
        // 不清的话换了普通视频入口还挂着, 点开是个空面板。
        // (_currentSeasonEpisode / _season 是引用类型, 自检只审值类型与 string, 不在清单里;
        //  但它们在 ResetForNewMedia 里同样被清了 —— 清单只负责"别漏", 不负责穷举。)
        nameof(_hasSeason), nameof(_switchingSeasonEpisode),
        // 分P 与合集是一对: 同样是"每片重算 + 换片闸门"。别只加一个漏一个。
        nameof(_hasPages), nameof(_switchingPage),
    };

    /// <summary>
    /// 明确**跨片保持**的值类型字段 —— 不该进每片清单, 也不该被自检点名。
    /// 分类错了自检会报出来, 改这里即可, 别去动 <see cref="PerMediaStateFields"/> 消警告。
    ///
    /// ★ 这张表装两种东西, 别混淆:
    ///   ① **用户偏好/设置**(音量、弹幕开关、排序方式…)—— 换片必须保持, 清了是 bug;
    ///   ② **窗口级的瞬态与订阅标记**(关闭标记、黑幕闸、Rendering 订阅…)—
    ///      它们活不到换片那一刻, 或者由事件自动收敛, 所以"不需要清"。
    ///   两种都不该被自检点名, 但只有 ① 是"必须保持"的语义。
    /// </summary>
    private static readonly string[] PersistentFields =
    {
        nameof(_volumeCache),        // 音量是用户偏好, 换片不该重置
        nameof(_wheelAccum),         // 滚轮音量累计, 瞬时量
        nameof(_danmakuOn),          // 弹幕总开关是设置项
        nameof(_danmakuColorful),    // 彩色弹幕是设置项
        nameof(_danmakuAreaRatio),   // 弹幕显示区域是设置项
        nameof(_laneHeight),         // 泳道高随字号走, 跟着设置走
        // ★ 外观三项(2026-10-12)同理是**用户偏好**: 换片时必须保持, 清了就等于"换个视频
        //   弹幕又变回默认大小/速度"—— 那是用户马上会报上来的 bug。
        //   与 _danmakuAreaRatio 同一类, 三项+泳道高都是设置驱动的派生量。
        nameof(_danmakuFontSize), nameof(_danmakuOpacity), nameof(_danmakuSpeedLevel),
        nameof(_danmakuScrollSeconds), nameof(_danmakuFixedSeconds),
        // 胶囊底衬开关(2026-11-05)同理是**用户偏好**: 换片必须保持,
        // 清了就等于"换个视频弹幕又变回纯文字" —— 那是用户马上会报上来的 bug。
        nameof(_danmakuPill),
        nameof(_teardownDone),       // 拆解只做一次
        nameof(_playerRegistered),   // 注册计数, 生命周期是窗口而非片源
        // 倍速弹层/窗口几何记忆(2026-11-03 未提交改动, 位置 2026-11-07)的窗口级状态
        nameof(_fillingSpeedMenu),   // 填充倍速列表时抑制 SelectionChanged, 纯 UI 瞬时闸
        nameof(_lastNormalWidth), nameof(_lastNormalHeight), // 最后一次常规窗口尺寸
        nameof(_lastNormalLeft), nameof(_lastNormalTop), nameof(_lastNormalPosValid), // 最后一次常规窗口位置
        nameof(_windowPlacementSaved), // 本次窗口是否已落盘几何
        nameof(_seasonOpen),         // 合集面板的展开态: 用户开着面板连看几集是常态, 换片收起它很烦
        nameof(_isFillWindow),       // 「铺满窗口」是窗口级的显示模式(同 _isFullscreen), 换片不该把它弹回去
        nameof(_commentSort),        // 评论排序是用户看评论的习惯(同 _seasonOpen): 选过一次就一直是那个顺序
        nameof(_isListenMode),       // 「听视频」是窗口级的播放模式(同 _isFullscreen): 换片不该把它弹回视频
        // hover 预览锁是"上一次操作的瞬时反馈"(750ms 自己过期, 与片源无关), 不属于任何一片
        nameof(_actionHoverLockUntil),
        // ---- 2026-10-12 补登记: 以下 8 个都是**窗口级**的瞬态/订阅标记, 换片不该清 ----
        //
        // ★★ 为什么它们不属于"每片状态": 自检只问"这个字段换片时该不该清", 而答案可以是
        //   "不该"—— 登记在这里就是把这个答案固化下来, 免得后人看到 errors.txt 又去
        //   ResetForNewMedia 里补一堆实际不需要的重置代码(那反而会引入新 bug)。
        //
        // 关闭流程标记(见字段声明处的说明): 窗口生命周期, 与片源无关
        nameof(_closing),
        // 手势"这一下是否由 Down 起步"的标记(左键/右键各一份): 它们描述的是**当前这一次点击**,
        //   窗口关闭/换片时鼠标多半早已抬起, 留着初值 false 即可;
        //   真按住不放时换片本来就是不可能的操作, 清了也没有可观察差异。
        nameof(_videoGestureActive), nameof(_rightGestureActive),
        nameof(_danmakuClockAttached),
        // 弹幕时钟的三个量: 靠 _mp.TimeChanged(约 4Hz)自动校准 —— 换片后新片一开播,
        //   下一次 TimeChanged 就把 _dmClockMedia 推到新片时间, 260ms 内自然收敛。
        //   ★ 特意**不**在 ResetForNewMedia 里清: 那会与 CalibrateDanmakuClock 抢写,
        //     而后者才是权威来源(2026-11-11 为修"暂停回跳"刚把权威收敛到它)。
        nameof(_dmClockMedia), nameof(_dmClockStamp), nameof(_dmClockRunning),
        // ---- 2026-10-30 补登记: 起播优化新增的三个窗口级瞬态 ----
        // 加载链代次与闸门: 换片时**故意不清** —— 新一次加载会自己 ++_loadGen 让旧的作废;
        //   清成 0 反而可能让旧任务的代次与新任务撞上。闸门由 LoadVideoAsync 的 finally 收敛。
        nameof(_loadGen), nameof(_loadRunning),
        // 首帧计时器(引用类型, 自检本来也不审, 这里登记只是表明它是窗口级瞬态)
        nameof(_playWatch),
        // 弹幕滑杆的回灌闸门(2026-10-30): 构造期间必须保持 true 挡住 XAML 初始 Value 的写回;
        //   换片不该碰它 —— 它是窗口级初始化状态, 不是"这一片"的弹幕设置。
        nameof(_syncingDanmakuLook),
        // 评论输入面板的展开态(2026-11-01): 窗口级 UI 瞬态 ——
        //   用户开着面板换片是常态(草稿都不清, 面板更不能收), 保持原样即可。
        nameof(_composerOpen),
        // 输入框的"展开高度"态(2026-11-01): 同样是窗口级 UI 偏好 ——
        //   用户展开了写长文, 换片/收面板再回来不该被打回 66px。发成功才归位(见 PostCommentAsync)。
        nameof(_composerExpanded),
    };

    /// <summary>
    /// 换片重置覆盖度自检: 找出既没登记为"每片状态"、也没登记为"跨片保持"的字段。
    ///
    /// 新增 <c>private bool/int/long/double/string</c> 字段却忘了在上面两张表里登记时,
    /// 它会出现在 errors.txt 里 —— 这是"漏清字段"唯一能被自动发现的入口。
    ///
    /// 只在 DEBUG 下编译进调用点(<see cref="ConditionalAttribute"/>), Release 零开销。
    /// 定时器字段不在审计范围内(它们由 <see cref="StopAllTimers"/> 自动处理)。
    /// </summary>
    [Conditional("DEBUG")]
    private static void VerifyResetCoverage()
    {
        var known = new HashSet<string>(
            PerMediaStateFields.Concat(PersistentFields), StringComparer.Ordinal);

        var unlisted = StateFieldNames()
            .Where(n => !known.Contains(n))
            .ToList();

        if (unlisted.Count == 0) return;

        App.ReportError(new InvalidOperationException(
            "[PlayerWindow] 以下字段未登记到 PerMediaStateFields(每片需清)或 PersistentFields(跨片保持): "
            + string.Join(", ", unlisted)
            + " —— 换片时它会保留上一条视频的残留。请归类后补进对应清单。"));
    }

    /// <summary>
    /// XAML 编译器自动生成、但**不带任何特性标记**的字段名 —— 必须显式排除。
    ///
    /// ★★ 为什么不能靠特性过滤(2026-10-12 实测):
    ///   `PlayerWindow.g.cs` 生成的 `private bool _contentLoaded;` 是 `InitializeComponent()`
    ///   的防重入标记。它与手写字段同属一个部分类, 所以 `DeclaredOnly` 拦不住;
    ///   类型正好是 bool, 于是被自检当成"漏登记的状态字段"—— errors.txt 里因此刷了 **397 条**
    ///   (同一句话每打开一个视频报一次, 日志涨到 797KB)。
    ///   而它**既没有 `CompilerGeneratedAttribute` 也没有 `GeneratedCodeAttribute`**
    ///   (只有相邻的 `InitializeComponent` 方法带 GeneratedCode, 字段本身光秃秃),
    ///   类上同样没有标记 ⇒ 反射层面没有任何判据能区分它与手写字段。
    ///   (试过按 CompilerGenerated 过滤, 探针实测生成字段数为 0, 那条路走不通。)
    /// ⇒ 只能按名字排除。这个名字由 XAML 编译器固定生成, 全项目唯一, 不会与业务字段撞。
    /// </summary>
    private static readonly HashSet<string> XamlGeneratedFieldNames = new(StringComparer.Ordinal)
    {
        "_contentLoaded",
    };

    /// <summary>取本类里所有"简单状态"字段名(值类型 + string, 不含 UI 元素/集合/定时器/服务引用)</summary>
    private static IEnumerable<string> StateFieldNames()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        return typeof(PlayerWindow).GetFields(flags)
            // ★★ XAML 编译器生成的字段必须排除(2026-10-12, errors.txt 里刷了 381 次的那条),
            //   原因与为什么只能按名字排除, 见 XamlGeneratedFieldNames 的注释。
            .Where(f => !XamlGeneratedFieldNames.Contains(f.Name))
            .Where(f => !typeof(DispatcherTimer).IsAssignableFrom(f.FieldType))
            .Where(f => f.FieldType.IsPrimitive || f.FieldType == typeof(string)
                        || f.FieldType.IsEnum
                        || f.FieldType == typeof(decimal) || f.FieldType == typeof(DateTime))
            .Select(f => f.Name);
    }

    /// <summary>
    /// 打开本地缓存文件并起播。
    ///
    /// 全程同步 —— 以前它是 <c>async Task</c> 却没有任何 await, 靠一句
    /// <c>await Task.CompletedTask;</c> 收尾。那种写法有两个坏处: ① 让"这里其实不异步"这件事
    /// 看不出来; ② 异常会进 Task 里, 而调用方是 <c>_ = LoadLocalAsync(...)</c>(丢弃返回值),
    /// 等于无人观察。方法内已有 try/catch 兜底, 所以这里直接做成同步方法最诚实。
    /// </summary>
    private void LoadLocalFile(string path)
    {
        ShowLoading("正在打开本地文件…");
        FailOverlay.Visibility = Visibility.Collapsed;
        try
        {
            var name = Path.GetFileNameWithoutExtension(path);
            TitleText.Text = name;
            Title = name + " - BiliDesk";
            VideoTitleText.Text = name;
            ViewCountText.Text = "--";
            DanmakuCountText.Text = "--";
            PubDateText.Text = "--";
            UpNameText.Text = "本地文件";
            UpDescText.Text = "离线缓存";
            DescText.Text = "这是离线缓存里的本地文件, 没有在线视频的简介与评论。";
            UpAvatarBox.Background = (Brush)FindResource("AccentSoftFillBrush");
            UpAvatarFallback.Visibility = Visibility.Visible;
            UpdateButtonState();

            // 本地文件没有清晰度可选, 清空菜单避免显示上一条视频的清晰度
            _qualities.Clear();
            FillQualityMenu(0);
            if (QualityText != null) QualityText.Text = "本地";
            // 本地文件也没有音轨可选(单文件源里音轨已经合好)
            _audioIds.Clear();
            _currentAudioId = 0;
            FillAudioMenu(0);
            if (AudioText != null) AudioText.Text = "本地";
            if (AudioHintText != null) AudioHintText.Text = "本地文件的音轨已经合在文件里, 不可切换";

            var media = new Media(_libVLC, path, FromType.FromPath);
            if (_closing) { media.Dispose(); return; }
            _lastPlayUrl = path;
            // 本地文件是单文件源, seek 一次到位(与 durl 合流单流同性质)
            _isReliableSeekSource = true;
            PlayMedia(media);
        }
        catch (Exception ex)
        {
            if (!_closing) ShowFail("打开本地文件失败: " + ex.Message);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (TabIntro != null && IntroPanel != null) TabIntro.IsChecked = true;
        UpdateButtonState();
        // 窗口是复用的: 按当前状态(铺满/常规)同步一次信息栏与窗口最小尺寸
        ApplyInfoPanelState();
        // 窗口大小/位置记忆: 起播时先记下当前常规几何, 用户不动窗口直接关闭也能沿用
        // (铺满模式下的几何不记: 那是在"详情栏已收起"的布局里拖出来的, 下次按常规布局
        //  打开会被最小宽度顶回去, 记了也用不上, 见 OnPlayerSizeChanged/OnPlayerLocationChanged)
        if (!_isFullscreen && !_isFillWindow && WindowState == WindowState.Normal)
        {
            _lastNormalWidth = ActualWidth;
            _lastNormalHeight = ActualHeight;
            // ★ 位置只有真正生效过才记: 按记忆位置打开时 Left/Top 已是有限值;
            //   CenterScreen 居中时是 NaN, 不能把 NaN 记进去(保存层会拒绝它)。
            if (double.IsFinite(Left) && double.IsFinite(Top))
            {
                _lastNormalLeft = Left;
                _lastNormalTop = Top;
                _lastNormalPosValid = true;
            }
        }
        // 「铺满窗口」按钮的字形初值: XAML 里写的是"进入前"那个字形(E9A6), 这里按实际状态
        // 再同步一次 —— 窗口是复用的, 复用到一个"上次已铺满"的窗口时不能显示成未铺满。
        UpdateFillWindowButton();
        // ★ 这条路径(带 bvid 直接 new 出窗口)**不走 ResetForNewMedia**(与 ApplyPreferredQuality
        //   当年踩的是同一个坑), 所以「听视频」按钮的可用性也要在这里同步一次。
        //   位置在 LoadVideoAsync **之前**: UpdateListenButtonState 依赖 _isLive/_isLocalPlayback,
        //   而这两个标志由调用方在构造后、Show 前就已设好。
        UpdateListenButtonState();
        // 聆听态是跨窗口复用的窗口级模式: 复用一个"上次停在聆听态"的窗口时,
        // 顶栏按钮与遮罩都要还原成聆听态(否则按钮说"听视频"、实际播的是音轨)
        if (_isListenMode) ApplyListenMode(true);
        // 覆盖层窗口(VideoView 的 ForegroundWindow)这时才建出来 —— 把键盘处理也挂上去,
        // 否则点过一次视频/控制栏之后快捷键就全哑了(见 HookOverlayKeyboard 的说明)
        HookOverlayKeyboard();
        // ★ 2026-11-03: 尽早把视频宿主窗口治成黑底(不等第一帧/Vout)。
        //   放在 Loaded 优先级: 此时 VideoView 的模板与原生宿主窗口已经建好;
        //   拿不到句柄也没关系, Vout/Playing 还会再调(见 StartVideoBackdropFix)。
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_closing) ApplyHwndHostBlackBackground();
        }));
        // ★ 不重复启动: 新窗口第一次打开时 PlayerService 已经先调过 PlayVideo(→LoadVideoAsync),
        //   Loaded 事件随后才到。没有这个闸门就会跑两遍详情/取流/测速, 甚至
        //   "先远端起播、下完再切本地"二次重启(2026-10-30 起播慢的隐藏原因之一)。
        if (!_loadRunning && !string.IsNullOrEmpty(_currentBvid)) await LoadVideoAsync();
    }

    // ------------------------------------------------------------ 加载流程

    /// <summary>
    /// 拉详情 → 取流 → 播放。
    ///
    /// ★ <paramref name="cid"/>: 指定要播的**分P**。0 = 走默认(pages[0])。
    ///   分P 是同一个 bvid 切成几段, 换段只换 cid(见 SwitchToPageAsync),
    ///   所以这里用参数传, 不去改 _currentBvid —— 改了会把弹幕/历史/三连的归属整个搞错。
    /// </summary>
    private async Task LoadVideoAsync(long cid = 0)
    {
        if (string.IsNullOrEmpty(_currentBvid)) return;

        // ★ 每次进入都取一个新代次: 上一次在飞的加载(点了另一条视频/另一个分P)立刻作废。
        //   没有这一道时, 连点两张卡片会跑两条完整的加载链, 后回来的那条会把画面/标题盖掉。
        var gen = ++_loadGen;
        _loadRunning = true;
        var sw = Stopwatch.StartNew();
        long tDetail = 0, tPlayUrl = 0, tRemux = 0, tSponsor = 0;

        ShowLoading("正在获取视频信息…");
        FailOverlay.Visibility = Visibility.Collapsed;
        try
        {
            var (detailOk, detailErr, detail) = await Svc.Api.GetVideoAsync(_currentBvid);
            tDetail = sw.ElapsedMilliseconds;
            // 加载期间用户可能已经关掉窗口/换了片, 此时不能再碰任何 UI/原生对象
            if (_closing || gen != _loadGen) return;
            if (detail == null)
            {
                // 现在 err 会带真实原因(接口码/异常), 不用再写死一句猜测的话
                ShowFail(detailErr ?? "获取视频信息失败, 可能视频已删除或不可用");
                return;
            }
            // ★ cid 优先级: 调用方指定的分P > pages[0]。
            //   合集换片那条路不传 cid(它是"换 bvid", 落回 pages[0] 与原行为完全一致),
            //   分P 换段那条路传具体 cid。
            _currentCid = cid > 0 ? cid : detail.Cid;
            _aid = detail.Aid;
            _ownerMid = detail.OwnerMid;
            _ownerName = detail.Owner;
            UpdateUiFromDetail(detail);
            // 合集数据在详情里就已经解析好了(见 ApiClient.GetVideoAsync), 这里零额外请求。
            // 放在 UpdateUiFromDetail 之后: 它要用刚设好的 _currentCid/_aid 来定位"当前是第几集"。
            ApplySeason(detail.Season);
            // 分P 同理, 零额外请求。放在 ApplySeason 之后: 两者抢的是同一块信息栏, 后写的赢。
            ApplyPages(detail.Pages);

            if (_currentCid <= 0)
            {
                ShowFail("无法获取视频 cid, 播放中止");
                return;
            }

            // ★ 评论区 Tab 是**窗口级状态**: 换片不会自动回到「简介」。
            //   它已经处于选中态时 Checked 事件不会再触发, 新视频的评论就永远不加载 ——
            //   用户看到的就是"停在评论区、一片空白, 必须先点简介再点回来"(2026-10-30)。
            //   这里补一次(与取流并行, 不占起播时间)。
            WriteLibVLCDebug($"\n[Load] 详情就绪 aid={_aid} cid={_currentCid} tabComments={TabComments?.IsChecked}\n");
            if (TabComments?.IsChecked == true) _ = LoadCommentsAsync();

            // ★ 片段查询**立刻发起, 与下面"取播放地址 / 合流下载"并行** ——
            // 起播**不等它**(服务端首包实测 2.6s, 等了也拿不到); 回来得早才做开场空降,
            // 否则由 500ms 心跳补跳(见 BeforePlayApplySponsorAsync)。
            var sponsorTask = FetchSponsorSegmentsAsync();

            SetLoadingText("正在获取播放地址…");
            // 默认请求 1080P(qn=80), B 站会按登录态/大会员返回实际可用的清晰度
            var (playOk, playErr, main, backups, durMs, qualities, actualQn, audioUrl, remux,
                 lastPlayMs, lastPlayCid, audioIds, actualAudioId) =
                await Svc.Api.GetPlayUrlAsync(_currentBvid, _currentCid, _currentQn,
                    Svc.Settings.PreferredAudioTier);
            tPlayUrl = sw.ElapsedMilliseconds;
            if (_closing || gen != _loadGen) return;
            // 以前这里不判成败, 直接拿 main 往下走 —— 取不到地址时 main 是 null, 一路走到
            // BuildMedia(null) 才炸, 用户看到的是一句语焉不详的"打开失败"。
            // 现在把 API 层给的原因直接透出去(见 ApiClient.GetPlayUrlAsync 的说明)。
            if (!playOk && string.IsNullOrEmpty(main))
            {
                ShowFail(playErr ?? "获取播放地址失败, 请稍后重试");
                return;
            }
            _totalMs = durMs;
            UpdateTotalDurationText();
            _qualities.Clear();
            _qualities.AddRange(qualities);
            FillQualityMenu(actualQn);
            // 音质: 记录本次可选音轨与最终选中的档(弹层与按钮文案都靠它)
            _audioIds.Clear();
            _audioIds.AddRange(audioIds);
            _currentAudioId = actualAudioId;
            FillAudioMenu(actualAudioId);

            // ---- 续播: 本机记录优先, 没有本机记录时用 playurl 返回的 B 站云端进度 ----
            // ★ 进度选择放在"取流成功"之后、起播之前: 这时 durMs 已知道, 可以过滤掉
            //   "太靠前"和"已经播到片尾"的无效进度。
            var resumePlan = Svc.PlaybackProgress.ChooseResume(
                _currentBvid, _currentCid, durMs, lastPlayMs, lastPlayCid, Svc.Settings.AutoResume);
            var resumeMs = resumePlan?.PosMs ?? 0;

            // ---- 极短片(≤60s / ≤8MB): 先合流成本地文件再播; 其余一律远端双流直接起播 ----
            // 顺序: 已缓存 -> 直接播本地; 没缓存 -> 2.5 秒内下完就播本地; 下不动 -> 回落远端 DASH。
            // ★ 2026-10-30: 阈值从"3 分钟 / 150MB"收到"1 分钟 / 8MB"。旧阈值下大多数
            //   短视频都要先整片下载, 起播被拖成 10~45 秒(用户报"加载慢/卡住")。
            //   实测 15.3MB 就要 3.6 秒, 所以 8MB 才是"等得起"的体量上限。
            //
            // ★ 只在播**第一个 P** 时才碰本地缓存(2026-10-11 加分P时补的):
            //   ShortVideoCache 的键只有 bvid, 不含 cid。多P 的短视频(每段都小于体积上限,
            //   比如一个视频切成几段 vlog)切到 P2 时会命中 P1 落盘的文件 —— 表现为
            //   "换了集还是上一段的内容"。长视频本来就超预算走不到这里,
            //   所以这个坑平时碰不到, 但短视频多P 一做分P 就会踩。
            //   与其给缓存 API 加 cid 参数(要动文件命名/清理/落盘三处), 不如认准"只缓存首 P"。
            var isFirstPage = cid <= 0 || cid == detail.Cid;
            var local = isFirstPage ? ShortVideoCache.TryGet(_currentBvid, actualQn) : null;
            if (local == null && isFirstPage && ShortVideoCache.WorthCaching(remux))
                local = await PrepareShortVideoAsync(remux!, actualQn);
            tRemux = sw.ElapsedMilliseconds;
            if (_closing || gen != _loadGen) return;

            string? url;
            string? playAudio;
            if (local != null)
            {
                url = local;
                playAudio = null;      // 本地合流是单文件源, 带 input-slave 等于给同一文件再挂一条音轨
            }
            else
            {
                url = main;
                if (string.IsNullOrEmpty(url) && backups.Count > 0) url = backups[0];
                playAudio = audioUrl;
            }
            if (string.IsNullOrEmpty(url))
            {
                ShowFail("无法获取播放地址(可能为大会员/付费视频), 请登录后重试");
                return;
            }

            SetLoadingText("正在缓冲视频…");

            // 记录要播放的 URL(诊断 + 重播时重建 Media 用)
            _lastPlayUrl = url;
            _lastAudioUrl = playAudio;
            // ★★★ 「听视频」存的必须是 **DASH 音轨地址(audioUrl)**, 不能存 playAudio。
            //   两者的区别正是"三分钟以下视频点听视频说没有音轨"的根因:
            //     短视频走**本地合流文件**那条路时 playAudio 是 null(单文件源不能挂 input-slave),
            //     但接口给的 DASH 音轨地址依然是好的、依然能单独播。
            //     以前这里写成 `= playAudio` ⇒ 短视频的 _listenAudioUrl 恒为 null ⇒
            //     点「听视频」直接报"这条视频没有可用的音轨"(2026-10-06 用户实测)。
            //   存 audioUrl 之后: 聆听态播纯音轨(省流量), 退出时用 _lastPlayUrl(本地文件)
            //   原样拼回 —— 两条路各用各的地址, 互不干扰。
            _listenAudioUrl = audioUrl;
            WriteLibVLCDebug(
                $"\n[LoadVideo] bvid={_currentBvid} cid={_currentCid} qn={actualQn} url={url} audio={playAudio}\n");

            // 片段数据: 只落地"已经回来"的结果 —— 绝不为它推迟起播(见 BeforePlayApplySponsorAsync)
            await BeforePlayApplySponsorAsync(sponsorTask);
            tSponsor = sw.ElapsedMilliseconds;
            if (_closing || gen != _loadGen) return;

            // 起播耗时诊断: 每段都是**本段耗时**(不是累计), 与 [Playing] 的 first-frame 对齐看
            WriteLibVLCDebug(
                $"\n[Timing] bvid={_currentBvid} detail={tDetail}ms " +
                $"playurl={tPlayUrl - tDetail}ms remux={tRemux - tPlayUrl}ms " +
                $"sponsor={tSponsor - tRemux}ms toPlay={sw.ElapsedMilliseconds}ms " +
                $"source={(local != null ? "local" : "remote")} qn={actualQn} " +
                $"cdnWait=0\n");

            // B站 CDN 对 Referer 和 User-Agent 有校验, 必须通过 Media 选项告诉 LibVLC;
            // DASH 流音视频分离, 用 input-slave 把音轨挂到视频流上一起播放。
            // ★ 「听视频」开着时(user 开着聆听态点了下一条视频)要直接起播**音轨单流** ——
            //   否则会先起视频流再被 EnterListenMode 切一次, 白拉一段视频数据、还多闪一次画面。
            //
            // ★★ 续播**不用** `:start-time`: 那个选项只作用于主视频流, 用 input-slave 挂进来的
            //   音轨仍然从 0 开始 ⇒ 音画不同步。改走项目里已经验证过的
            //   `_resumeAfterSwitchMs`(Playing 事件里统一 seek + 看门狗), 远端/本地两条路都适用。
            if (resumeMs > 0) _resumeAfterSwitchMs = resumeMs;
            var started = false;
            if (_isListenMode && !string.IsNullOrEmpty(_listenAudioUrl))
            {
                _listenResumeMs = 0;
                ShowLoading("正在切换到听视频…");
                started = PlayMedia(BuildMedia(_listenAudioUrl!, null));
            }
            else
            {
                // ★ 聆听态开着但这一片**取不到独立音轨**(本地合流是单文件源): 退回整路播放,
                //   并把聆听态收掉 —— 否则遮罩会说"正在聆听"而实际在播视频, 自相矛盾。
                if (_isListenMode)
                {
                    _isListenMode = false;
                    ApplyListenMode(false);
                }
                started = PlayMedia(BuildMedia(url, playAudio));
            }
            if (started && resumePlan != null)
            {
                var prefix = resumePlan.Source == ResumeSource.Cloud ? "已从 B 站进度 " : "已从上次进度 ";
                Svc.Toast.Show(prefix + FormatClock(TimeSpan.FromMilliseconds(resumeMs)) + " 继续播放");
            }
            UpdateListenButtonState();
            // 加载弹幕 + 查询点赞/关注/收藏状态(用于按钮状态切换)
            _ = LoadDanmakuAsync(_currentCid);
            _ = RefreshLikeStateAsync();
            _ = RefreshFollowStateAsync();
            _ = RefreshFavStateAsync();
            _ = RefreshWatchLaterStateAsync();
        }
        catch (Exception ex)
        {
            if (!_closing && gen == _loadGen) ShowFail("加载失败: " + ex.Message);
        }
        finally
        {
            // ★ 只有"还是当前这一代"才有资格清闸门: 被新加载顶掉的旧任务清掉的话,
            //   闸门会在新任务还在飞的时候谎报空闲, OnLoaded 又会叠一条进来。
            if (gen == _loadGen) _loadRunning = false;
        }
    }

    /// <summary>
    /// 播一个 Media, 并**立刻释放我们自己那一份引用**。
    ///
    /// ★ 为什么必须释放: `MediaPlayer.Play(media)` 内部会 `set_media` —— 播放器**自己**已经
    ///   retain 了一份, 所以 Play 之后我们这份就可以放掉, 播放不受任何影响
    ///   (LibVLCSharp 官方示例就是 `using (var media = ...) mp.Play(media)`)。
    ///
    /// 反过来, 如果我们不释放, 这个 `libvlc_media_t` 就**永远**留着 —— 它握着解复用解析出来的
    /// 东西(本地 MP4 的样本表、网络流的 track / metadata), 而且全是**原生内存**,
    /// .NET 的 GC 一点都管不到。表现就是: 每看一个视频涨一块、关掉也不回落
    /// (2026-09-27 用户报的内存问题里就有这一份)。
    ///
    /// 之前 6 个播放点全都是 `_mp.Play(new Media(...))` 直接把 Media 丢掉, 一个都没释放。
    /// </summary>
    private bool PlayMedia(Media media)
    {
        try
        {
            // 首帧计时只用于诊断日志: 每次起播(含切清晰度/重播/直播)都会重置。
            _playWatch = Stopwatch.StartNew();
            var ok = _mp.Play(media);
            if (!ok) _playWatch = null;   // 没播起来就别留一个永远等不到 Playing 的计时器
            return ok;
        }
        finally
        {
            try { media.Dispose(); } catch { /* 播放器持有它自己那份引用, 这里失败不影响播放 */ }
        }
    }

    /// <summary>
    /// 喂给 LibVLC 的 UA。B 站 CDN 会校验, 用浏览器 UA 最稳;
    /// 点播/直播/重播都必须带上同一份, 所以提出来共用。
    /// </summary>
    private const string PlayerUserAgent = HttpDefaults.UserAgent;

    /// <summary>
    /// 构造带 CDN 头的 Media。url 既可能是在线地址(DASH 视频流 / durl 单流), 也可能是
    /// 本地文件(离线缓存、或短视频的本地合流缓存)。
    /// DASH 流音视频分离, 用 input-slave 挂音轨; 单文件源(durl 合流 / 本地文件)传 null。
    ///
    /// ★★ 这里顺带决定两件事(2026-10-17 播放器手术):
    ///   ① demuxer —— **走 BuildMedia 的源统一用 avformat**(见下方 2026-11-05 的尾部修复);
    ///   ② <see cref="_isReliableSeekSource"/> —— 有没有挂 input-slave 就是判据。
    /// 两条都只在这里设置一次, 别处不要再改(漏一处就会让看门狗按错的类型兜底)。
    ///
    /// 本地路径必须走 FromPath: `new Uri("C:\...\x.mp4")` 会把盘符当成协议名, 抛 UriFormatException。
    /// </summary>
    private Media BuildMedia(string url, string? audioUrl, long startMs = 0)
    {
        // B站 CDN 校验 Referer 和 User-Agent
        var options = new List<string>
        {
            ":http-referrer=https://www.bilibili.com/",
            ":http-user-agent=" + PlayerUserAgent
        };

        // ★★ 2026-11-05: 重建 Media 追 seek 目标时, 用 **:start-time** 让解复用器
        //   在 open 阶段直接定位。相比"起播后再设一次 Time", 它不会撞上 "input 还没稳定
        //   就二次 seek" 的窗口 —— 实测(探针 bd-probe-seekmode)起播即落在目标附近且继续推进。
        //   只对网络流加; 本地文件的重建走原来的 Playing 后设 Time。
        if (startMs > 0 && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            options.Add(":start-time=" +
                        (startMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture));

        // ★★★ 2026-10-17 播放器手术第 3 步: 统一 demuxer(2026-11-05 起所有源都用 avformat)。
        //
        //   【无音轨 ⇒ 单文件源】: 加 :demux=avformat。
        //     实测(探针 bd-probe-muxed2): 合流单流配 avformat, 来回 7 跳全部准确
        //     (偏差 0.9~2.2s)且画面持续推进; 不加则落点准但**跳完冻住**(推进 0ms)。
        //
        //   【DASH 双流 / 聆听音轨】: ★★★ 2026-11-05 起**也加** `:demux=avformat`。
        //     这是"播到片尾最后几秒没声音"的真正修复:
        //       · libvlc 自带的 mp4 demux 对 B 站这种 fMP4 有 bug —— **最后一整个分片
        //         (sidx 的最后一段, 约 1.5~2.4s)根本读不出来**, 到分片边界就 EndReached。
        //         实测 42s 的短片在 39.6s 结束、22 分钟的长片在 1315.5s 结束, 结尾
        //         恰好是最后一段的长度; 而且 seek 进最后一段也一样结束(不是网络问题)。
        //       · 换 avformat 后同一片源播到 1317.2s/1317.4s(只差 0.2s 的解码尾),
        //         音轨与画面对齐, 片尾不再静音、进度条也能正常走满。
        //     历史注释里"带 input-slave 加 avformat 会 seek 后 Ended"是 3.0.20 + 同进程
        //     连开多个 LibVLC 的污染结论(bd-probe-muxed5 已平反)。当前 3.0.24 实测:
        //     双流 + avformat 连续 6 次远跳(90/10/55/5/80/30%)全部存活、落点 1~2s,
        //     43s/22min 的 H.264 与 HEVC 都验过。故不再区分, 统一走 avformat。
        //
        //   ★ 判据 `isListenAudio` 仍保留: 它只用于决定"这条 url 是音轨单流",
        //     不参与 demuxer 选择; 保留它可读性更好, 也方便以后单独调聆听态。
        var isListenAudio = _isListenMode &&
                            string.Equals(url, _listenAudioUrl, StringComparison.Ordinal);
        options.Add(":demux=avformat");
        if (string.IsNullOrEmpty(audioUrl) && !isListenAudio)
        {
            // 单文件源: 没有 slave, seek 一发即准
            _isReliableSeekSource = true;
        }
        else
        {
            // DASH 双流 / 聆听音轨单流: 远端分片 fMP4, 仍按"不可靠"处理
            // (保留看门狗 + 重建兜底; avformat 只是把落点从"掉回开头"改善到 1~2s)
            _isReliableSeekSource = false;
            if (!string.IsNullOrEmpty(audioUrl))
                options.Add(":input-slave=" + audioUrl);
        }

        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var local = new Media(_libVLC, url, FromType.FromPath);
            foreach (var o in options) local.AddOption(o);
            return local;
        }

        // 起播缓冲(2026-10-30 新增, 设置页「起播缓冲」): 只加给网络流。
        // ★ 用**媒体级**选项而不是重建 LibVLC: 全局实例是进程单例, 改它等于要重启内核;
        //   媒体级选项下一条视频就生效, 且能各走各的(直播也一样)。
        // ★ 2026-11-01: 值不再直接取用户设置 —— 统一走 BufferPolicy(自动/手动两档),
        //   直播/自动档的规则见 Services/BufferPolicy.cs。
        var isShortVideo = _totalMs > 0 && _totalMs <= 60_000;
        var kind = string.IsNullOrEmpty(audioUrl) ? BufferMediaKind.VodSingle : BufferMediaKind.VodDash;
        var cachingMs = BufferPolicy.Resolve(kind, url, isShortVideo,
            Svc.Settings.NetworkCachingMs, Svc.Settings.AutoBufferPolicy);
        WriteLibVLCDebug($"[BufferPolicy] kind={kind} short={isShortVideo} nc={cachingMs}ms " +
                         $"auto={Svc.Settings.AutoBufferPolicy} measured={CdnService.Instance.LastMeasuredBytesPerSecond}B/s\n");
        options.Add(":network-caching=" + cachingMs);
        return new Media(_libVLC, new Uri(url), options.ToArray());
    }

    // ------------------------------------------------------------ 短视频本地合流(边播边下)

    /// <summary>后台合流任务。换片/切清晰度/关窗都要取消它 —— 否则会白下几十兆, 还可能把旧片写进新键</summary>
    private CancellationTokenSource? _remuxCts;

    /// <summary>
    /// 本会话里"下载超时过"的 bvid —— 不再为它尝试本地合流。
    ///
    /// ★ 为什么需要: 2.5 秒预算到点后回落到远端起播, 但**下一次再点开同一个视频**又会
    ///   从头等 2.5 秒。记下来之后, 本会话内直接流式起播, 用户不会再看到第二次转圈。
    ///   不持久化: 换网络/重启后值得重新试一次。
    /// </summary>
    private readonly HashSet<string> _remuxSlow = new(StringComparer.Ordinal);

    /// <summary>
    /// 合流任务的"代次"。换片/切清晰度都要作废上一次的在飞任务 ——
    /// 直接比字段(比如"cid 还一样吗")容易漏(切清晰度时 cid 是不变的), 用单调递增的代次最省心。
    /// </summary>
    private int _remuxGen;

    /// <summary>
    /// 加载代次。换片 / 换分P 只要再次进入 <see cref="LoadVideoAsync"/>, 代次就 +1 ——
    /// 上一次还在飞的加载回来时必须立刻作废, 否则会把旧视频的详情/地址/本地文件灌进新视频
    /// (表现是"换片后标题对了、画面还是上一条")。
    /// ★ 与 _remuxGen 同款, 但管的是**整条加载链**, 不只是合流任务。
    /// </summary>
    private int _loadGen;

    /// <summary>
    /// 是否有一次 LoadVideoAsync 正在飞。
    /// ★ 用来挡住 OnLoaded 的重复启动: 新窗口第一次打开时 PlayerService 先调 PlayVideo,
    ///   Loaded 事件随后才到 —— 没有这个闸门就会跑两遍详情/取流/测速, 甚至
    ///   "先远端起播、下完再切本地"二次重启(2026-10-30 起播慢的隐藏原因之一)。
    /// </summary>
    private bool _loadRunning;

    /// <summary>
    /// 本次 <see cref="PlayMedia"/> 到首帧(Playing)的计时器, 只用于 [Timing] 诊断日志。
    /// 打完即置空, 避免 seek 后重复的 Playing 事件把首帧耗时刷成无意义的数。
    /// </summary>
    private Stopwatch? _playWatch;

    /// <summary>
    /// 短视频: 下载两路并合流成本地文件, 期间在加载遮罩上显示进度。
    /// 返回合流好的本地路径; 失败 / 超时 / 被取消返回 null(调用方回落到远端 DASH)。
    ///
    /// 为什么是"下完再播"(用户 2026-09-25 选定):
    ///   前一版是"边播边下、就绪后换源"。换源那一下真机上暴露了两个问题 —— 跳转后画面花屏、
    ///   音轨不同步(根因见 DashRemuxer 里 mfhd 序列号那条)。下完再播从头到尾都是**同一个输入**,
    ///   没有换源这一步, 问题面小得多。
    ///
    /// ★ 2026-10-30 收窄: 只有"≤60 秒 且 ≤8MB"的极短片会走到这里, 且总预算 2.5 秒。
    ///   旧版对所有 ≤3 分钟的视频都先整片下载(45 秒上限), 起播被拖成 10~45 秒。
    ///
    /// 进度是"两路已下字节 / 按码率估算的总字节": 估算值来自接口的 bandwidth, 与实际有偏差,
    /// 所以到 100% 之后还可能要等一下合流(那步是纯内存拷贝, 很快)。
    /// </summary>
    private async Task<string?> PrepareShortVideoAsync(DashStreams spec, int qn)
    {
        // 本会话已经为它白等过 2.5 秒 ⇒ 直接流式起播, 不再重试。
        // ★ 必须放在创建 CTS / 清遮罩文案之前: 早退不该留下一个没人用的 CTS。
        var bvid = _currentBvid;  // 别在后台线程上读字段: 先取到本地变量
        if (_remuxSlow.Contains(bvid)) return null;

        CancelRemuxPrefetch();
        var cts = new CancellationTokenSource();
        _remuxCts = cts;
        var gen = _remuxGen;      // CancelRemuxPrefetch 里已经自增过, 这就是"本次任务"的代次

        SetLoadingText("正在准备高清播放…");
        try
        {
            // Progress<T> 在创建它的线程(UI 线程)上回调, 所以这里可以直接碰控件
            var progress = new Progress<double>(p =>
            {
                if (_closing || gen != _remuxGen) return;
                SetLoadingText(p > 0
                    ? $"正在下载高清视频 {p * 100:0}%"
                    : "正在准备高清播放…");
            });

            var result = await ShortVideoCache.PrepareAsync(bvid, qn, spec, cts.Token, progress);
            // 期间用户可能已经换片/切清晰度/关窗: 代次变了就丢掉这次结果
            if (_closing || gen != _remuxGen) return null;
            if (result.TimedOut) _remuxSlow.Add(bvid);
            if (result.Path == null) SetLoadingText("正在缓冲视频…");   // 合流没成 -> 走远端, 文案还原
            return result.Path;
        }
        finally
        {
            if (ReferenceEquals(_remuxCts, cts))
            {
                _remuxCts = null;
                cts.Dispose();
            }
        }
    }

    /// <summary>
    /// 取消正在进行的合流任务(换片 / 切清晰度 / 关窗都调它)。
    /// 用单调递增的代次作废在飞任务: 只比"cid 还是不是同一个"会漏 —— 切清晰度时 cid 不变。
    /// </summary>
    private void CancelRemuxPrefetch()
    {
        _remuxGen++;
        try { _remuxCts?.Cancel(); } catch { /* 已经结束 */ }
        _remuxCts?.Dispose();
        _remuxCts = null;
    }

    /// <summary>
    /// 当前分P 挂在标题后面的后缀, 如 <c>" - P12 S05E08 收获"</c>。
    /// 不是多P 视频、或当前 cid 在 pages 里定位不上, 都返回空串(标题一个字不动)。
    /// </summary>
    private string CurrentPageSuffix(VideoDetail detail)
    {
        if (detail.Pages.Count <= 1 || _currentCid <= 0) return "";
        foreach (var p in detail.Pages)
            if (p.Cid == _currentCid)
                return $" - P{p.Page} {p.DisplayTitle}";
        return "";
    }

    private void UpdateUiFromDetail(VideoDetail detail)
    {
        // ★ 多P 时标题后面挂上"当前这一段"(2026-10-11):
        //   40 段的纪录片每段标题都一样("克拉克son's农场 1-5季"), 不标出来用户根本分不清
        //   自己在第几段, 切窗口(Alt-Tab)回来也是一脸茫然。官方网页版同样在标题里带 part。
        //   单P 视频一个字符都不多写。
        var pageSuffix = CurrentPageSuffix(detail);
        var displayTitle = detail.Title + pageSuffix;

        TitleText.Text = displayTitle;
        Title = displayTitle + " - BiliDesk";
        // ★ _videoTitle 保持**不含分P**: 它是要往历史/上报里写的视频名, 那是稿件的标题,
        //   不是"我现在看到第几段"。混进去会让同一稿件在历史里出现几十条重名记录。
        _videoTitle = detail.Title;
        VideoTitleText.Text = displayTitle;
        ViewCountText.Text = VideoItem.FormatCount(detail.ViewCount);
        DanmakuCountText.Text = VideoItem.FormatCount(detail.DanmakuCount);
        PubDateText.Text = detail.Pubdate > 0
            ? DateTimeOffset.FromUnixTimeSeconds(detail.Pubdate).LocalDateTime.ToString("yyyy-MM-dd")
            : "";
        UpNameText.Text = string.IsNullOrEmpty(detail.Owner) ? "UP 主" : detail.Owner;
        UpDescText.Text = "UP 主 mid: " + detail.OwnerMid;
        DescText.Text = string.IsNullOrEmpty(detail.Desc) ? "这个 UP 主很懒, 什么都没写~" : detail.Desc;
        // ★ 换片 ⇒ 折叠态必须复位: 否则在 A 片点开简介, 换到 B 片时它是展开的,
        //   而 B 片简介很短 —— 展开按钮又不显示, 用户会以为"按钮坏了"。
        _descExpanded = false;
        // 宽度可能还没变(信息栏固定 400px), SizeChanged 这次不会触发, 得主动判一次
        UpdateDescFold();

        // 相关视频: 与详情一起取, 走后台, 不挡起播(见 LoadRelatedAsync 的代次闸门)
        _ = LoadRelatedAsync(detail.Bvid);

        UpdateButtonState();
        // 切换视频时重置各操作按钮状态(随后的异步查询会刷新为真实值)
        _isLiked = false;
        _isFollowed = false;
        // ★ 投币态**不能**取自详情(那个字段根本不存在, 见 GetCoinCountAsync 的说明),
        //   这里先复位, 真实值由随后的 RefreshCoinStateAsync 异步补上。
        _isCoined = false;
        // 计数直接来自详情(view 接口的 stat), 比旧的"点赞/投币/收藏"三个字信息量大
        _likeCount = detail.LikeCount;
        _coinCount = detail.CoinCount;
        _favCount = detail.FavoriteCount;
        UpdateActionCounts();
        SetFavorited(false);
        // 与点赞同款: 详情带不来"本人是否投过币", 换片后单独查一次, 否则投币键恒灰
        _ = RefreshCoinStateAsync();
        // 「稍后再看」的点亮态不在这里复位 —— 它统一在 ResetForNewMedia 里清,
        // 因为直播/本地文件那两条路根本不会走到这里。
        UpdateFollowButton();
        // UP 主头像优先用 owner.face(之前误用了视频封面)
        _ownerFace = detail.OwnerFace ?? "";
        _ = LoadUpAvatarAsync(_ownerFace);
        // 视频封面: 「听视频」聆听态要拿它当视觉锚点(一眼知道在听什么)。
        // 顺手记下来, 不必等用户点了聆听再去请求详情接口。
        _coverUrl = detail.Pic ?? "";
        if (_isListenMode)
        {
            if (ListenOverlayTitle != null) ListenOverlayTitle.Text = detail.Title;
            LoadListenCover(_coverUrl);
        }

        // 本机观看历史已移除, 这里不再写本地记录。
        // 观看记录仍然会上报到云端 —— 见下方的 Svc.HistorySync。ReportAsync。
    }

    private async Task LoadUpAvatarAsync(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        // 头像只有 40px 显示, 按小尺寸解码省内存
        var img = await CoverLoader.LoadAsync(url, 80);
        if (img == null) return;
        await Dispatcher.InvokeAsync(() =>
        {
            var brush = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            brush.Freeze();
            UpAvatarBox.Background = brush;
            UpAvatarFallback.Visibility = Visibility.Collapsed;
        });
    }

    private void ShowFail(string msg)
    {
        HideLoading();
        FailOverlay.Visibility = Visibility.Visible;
        FailText.Text = msg;
    }

    // ------------------------------------------------------------ 加载遮罩(含颜文字彩蛋)

    // 颜文字轮换的间隔。700ms 一换: 比一次典型的网络请求慢半拍, 像是"陪伴"而不是"闪烁"。
    private const int KaomojiIntervalMs = 700;
    private static readonly string[] KaomojiList =
    {
        "(´･ω･`)", "(･ω･)つ", "ヾ(≧▽≦*)o", "(๑•̀ㅂ•́)و✧", "(っ•̀ω•́)っ", "┌(・。・)┘♪"
    };
    private DispatcherTimer? _kaomojiTimer;
    private int _kaomojiIndex;

    /// <summary>
    /// 遮罩的淡入/淡出代次。
    /// 用途: 淡出动画跑完时, 如果这期间又被 ShowLoading 重新叫起(seek 卡顿反复触发就会),
    /// 不能再把遮罩藏掉 —— 用代次比对即可判断"我这一层还是不是当前的"。
    /// </summary>
    private int _loadingGen;

    /// <summary>遮罩淡入时长</summary>
    private const int LoadingFadeInMs = 130;

    /// <summary>遮罩淡出时长。比淡入稍长: 收起时慢一点, 画面过渡更自然。</summary>
    private const int LoadingFadeOutMs = 170;

    /// <summary>显示加载遮罩(顺带启动颜文字轮换)。加载遮罩统一从这里走, 不要直接碰 Visibility</summary>
    private void ShowLoading(string message)
    {
        if (_closing || LoadingOverlay == null) return;
        _loadingGen++;
        LoadingText.Text = message;

        if (LoadingOverlay.Visibility != Visibility.Visible)
        {
            // 首次显示: 基准值设 0, 再淡到 1 —— 别"啪"地糊一层黑上来
            LoadingOverlay.Opacity = 0;
            LoadingOverlay.Visibility = Visibility.Visible;
        }

        // 取"当前透明度"(挂了动画时拿到的是动画值)。已经在 1 上就不重播,
        // 否则每次换文案("获取视频信息"→"获取播放地址")都会闪一下。
        var from = LoadingOverlay.Opacity;
        if (from < 1)
        {
            LoadingOverlay.BeginAnimation(OpacityProperty,
                new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(LoadingFadeInMs))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }

        StartKaomoji();
    }

    /// <summary>遮罩已经可见、只是换个文案(比如"获取视频信息"→"获取播放地址")</summary>
    private void SetLoadingText(string message)
    {
        if (_closing) return;
        LoadingText.Text = message;
        StartKaomoji();
    }

    /// <summary>收起加载遮罩(淡出)。所有收起路径都必须走这里</summary>
    private void HideLoading()
    {
        _kaomojiTimer?.Stop();
        _kaomojiTimer = null;

        if (_closing || LoadingOverlay == null) return;
        if (LoadingOverlay.Visibility != Visibility.Visible) return;

        var gen = ++_loadingGen;
        var from = LoadingOverlay.Opacity;
        if (from <= 0.001)
        {
            FinishHideLoading();
            return;
        }

        var fade = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(LoadingFadeOutMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, _) =>
        {
            // 淡出期间又被 ShowLoading 接管(卡顿反复触发时会遇到) -> 不能藏
            if (_closing || gen != _loadingGen) return;
            FinishHideLoading();
        };
        LoadingOverlay.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>真正把遮罩藏起来(同时把基准透明度归零, 下次显示才能从 0 淡入)</summary>
    private void FinishHideLoading()
    {
        LoadingOverlay.BeginAnimation(OpacityProperty, null);
        LoadingOverlay.Opacity = 0;
        LoadingOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>颜文字轮播。定时器常驻到遮罩收起为止, 避免每次 ShowLoading 都新建</summary>
    private void StartKaomoji()
    {
        if (_kaomojiTimer != null) return;
        LoadingKaomoji.Text = KaomojiList[_kaomojiIndex % KaomojiList.Length];
        _kaomojiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(KaomojiIntervalMs) };
        _kaomojiTimer.Tick += (_, _) =>
        {
            if (_closing || LoadingOverlay.Visibility != Visibility.Visible)
            {
                HideLoading();
                return;
            }
            _kaomojiIndex++;
            LoadingKaomoji.Text = KaomojiList[_kaomojiIndex % KaomojiList.Length];
        };
        _kaomojiTimer.Start();
    }

    // ------------------------------------------------------------ 播放控制

}
