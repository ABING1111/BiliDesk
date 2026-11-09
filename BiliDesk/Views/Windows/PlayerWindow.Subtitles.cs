// =============================================================================
// PlayerWindow.Subtitles.cs —— 字幕(外挂 / AI 字幕)运行逻辑
//
// ★ 从 1.6.3 的单文件 PlayerWindow.xaml.cs 迁到 1.7.1 的 partial 结构:
//   本文件只收「字幕取轨 / 选轨 / 显示 / 按钮与弹层状态」相关的字段与方法,
//   其余接入点(构造期初始化、换片复位、4Hz 心跳、取流后并行加载、退订)散落在
//   各自职责的 partial 文件里, 用 Subtitles 前缀的注释标注。
// =============================================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BiliDesk.Models;
using BiliDesk.Services;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
// ★ LibVLCSharp.Shared 里也有一个 SubtitleTrack(原生字幕轨), 与本项目的字幕轨同名。
//   不加这行别名就是 CS0104 二义引用 —— 我们要的一直是接口返回的那份 BiliDesk.Models。
using SubtitleTrack = BiliDesk.Models.SubtitleTrack;

namespace BiliDesk.Views;

public partial class PlayerWindow
{
    // ---------------------------------------------------------------- 字幕(外挂 / AI, 2026-11-04)
    //
    // 与弹幕的分工: 弹幕是"每片现拉现放"的滚动列表; 字幕是"取轨 → 选轨 → 下 cue → 按时间
    // 定位单条", 同一时刻最多显示一条, 所以两者不走同一套数据/状态。
    //
    // ★ 开关(_subtitleOn)与轨道下标都**不跨片保持**: 轨道是按 cid 取的, 换一条视频后
    //   下标可能指向不存在或语言完全不同的轨。这与弹幕开关(全局设置)刻意不同。

    /// <summary>字幕总开关。false 时显示层收起(用户点了「关闭字幕」)。</summary>
    private bool _subtitleOn;

    /// <summary>当前选中的字幕轨下标(_subtitleTracks 的下标); -1 = 未选/关闭。</summary>
    private int _subtitleSelectedIndex = -1;

    /// <summary>
    /// 字幕轨接口是否失败过(评审意见第 3/7 条)。
    ///
    /// ★ 必须与"这个视频本来就没有字幕"区分开: 前者是可重试的错误(网络抖动/风控/未登录),
    ///   后者是正常结果。原实现两种情况都只是把按钮写成"无字幕"并置灰,
    ///   网络一抖用户就再也点不出字幕了 —— 现在失败态按钮显示「字幕重试」且仍可点,
    ///   点一下即重新拉轨; 确实没有轨时才隐藏按钮。
    /// </summary>
    private bool _subtitleTracksFailed;

    /// <summary>字幕内容(cue)下载是否失败过。失败态下允许重新点同一条轨重试(见 OnSubtitleSelected)。</summary>
    private bool _subtitleCueFailed;

    /// <summary>
    /// 上一帧真正写进 <see cref="SubtitleText"/> 的文案。
    /// 判"同一条"用文案比对而不是下标: TimeChanged 约 4Hz, 一条字幕通常持续 1~3s,
    /// 不去抖就是每秒给 TextBlock 赋 4 次同值(会触发 TextWrapping 重排, 长句子上有可感卡顿)。
    /// </summary>
    private string _subtitleShownText = "";

    /// <summary>本片可用的字幕轨(外挂 + AI)。引用类型, 不进换片自检清单, 但必须换片清空。</summary>
    private readonly List<SubtitleTrack> _subtitleTracks = new();

    /// <summary>当前选中轨已下载的字幕条(按 From 升序)。</summary>
    private readonly List<SubtitleCue> _subtitleCues = new();

    /// <summary>填充字幕列表时抑制 SelectionChanged(与 _fillingQualityMenu 同款 UI 瞬时闸)。</summary>
    private bool _fillingSubtitleMenu;

    /// <summary>
    /// 拖字号滑杆回灌时抑制 ValueChanged(与 _syncingDanmakuLook 同款)。
    /// ★ **初值必须是 true**: InitializeComponent() 里把 Slider 的 XAML 初值(100)赋给
    ///   Value 时会触发一次 ValueChanged, 那一刻用户设置还没读出来 —— 闸门开着就会用 100
    ///   把用户存的字号覆写掉(与弹幕外观四个滑杆踩过的是同一个坑, 见 _syncingDanmakuLook)。
    ///   构造函数读完设置、写完滑杆之后才置 false。
    /// </summary>
    private bool _syncingSubtitleFont = true;

    // ================================================================ 字幕(外挂 / AI, 2026-11-04)
    //
    // 链路: 取轨(getSubtitleTracks) → 选轨 → 下 cue(getSubtitleCues)
    // → 按播放位置定位单条 → 渲染。与弹幕的关键差异: 字幕只有"一条", 判据是时间区间
    // [From, To] 命中而不是"追上游标", 所以这里用二分而不是 _indexInLib 那套追尾逻辑。

    /// <summary>字幕基准字号(px)。设置里存百分比, 实际字号 = 基准 × 百分比 / 100。</summary>
    private const int SubtitleBaseFontSize = 20;

    // 字幕外观的"上次已写入"缓存, 供 ApplySubtitleLook 做变更比对(见其注释):
    // -1 是"从未写过"的哨兵值, 保证第一次必然写一遍(哪怕用户设置正好等于默认值)。
    private int _subtitleLookFont = -1;
    private int _subtitleLookOpacity = -1;
    private int _subtitleLookMargin = -1;

    /// <summary>
    /// 把字幕运行时状态与显示层清回"没有字幕"。
    /// 换片(<see cref="ResetForNewMedia"/>)与重新取轨(<see cref="LoadSubtitleAsync"/>)共用,
    /// 两处各写一遍必然漏清一个(典型表现: 新片第一帧闪一下上一条的字幕)。
    /// </summary>
    private void ResetSubtitleState()
    {
        _subtitleOn = false;
        _subtitleSelectedIndex = -1;
        _subtitleShownText = "";
        _subtitleTracksFailed = false;
        _subtitleCueFailed = false;
        _subtitleTracks.Clear();
        _subtitleCues.Clear();
        // 弹层也要收(评审意见第 6 条): 原来只清列表不关弹层, 换片时若弹层正开着,
        // 用户会看到一个空列表浮在画面上, 而且它挂的还是上一条视频的 PlacementTarget。
        if (SubtitlePopup != null) SubtitlePopup.IsOpen = false;
        if (SubtitleText != null) SubtitleText.Text = "";
        if (SubtitleBox != null) SubtitleBox.Visibility = Visibility.Collapsed;
        if (SubtitleList != null) SubtitleList.Items.Clear();
        // ★ 实测补充(T9, 多 P 切换): 切 P 与换片走同一条链 —— 点分 P 后由
        //   ResetForNewMedia(PlayerWindow.Loading.cs) 同步调到这里, 上一条轨的文案、轨道列表、
        //   弹层一次性全清, 不会出现"新 P 起播前画面还挂着上一 P 那句字幕"。
        //   别把这句清空挪进 LoadSubtitleAsync: 那条路要等详情/取流回来才跑到, 中间这段
        //   await 窗口里旧文案是真会留在屏幕上的。
        //   (自动化在切 P 后仍能遍历到隐藏的字幕文本节点, 那是 Collapsed 容器内的子元素、
        //   文本值为空, 属 WPF 正常行为, 不是状态没清。)
        UpdateSubtitleButton();
    }

    /// <summary>
    /// 取当前片的字幕轨, 然后自动选一条并下载它的 cue。
    /// 直播 / 本地文件没有字幕源, 直接跳过(只把按钮置灰)。
    /// </summary>
    /// <param name="quietCueFail">
    /// 自动选轨这条路径传 true(评审意见第 7 条): 用户并没有主动选某条轨, cue 下载失败
    /// 不该弹 Toast 打断播放, 只把按钮文案切成「字幕失败」; 用户主动重试时传 false。
    /// </param>
    private async Task LoadSubtitleAsync(string bvid, long cid, bool quietCueFail = true)
    {
        if (_closing) return;
        ResetSubtitleState();
        if (cid <= 0 || string.IsNullOrEmpty(bvid) || _isLive || _isLocalPlayback) return;

        // 代次快照: 只认"还是这一片"的响应 —— 换片后旧响应回来会写进新片的轨列表
        var gen = _loadGen;
        try
        {
            var (ok, err, tracks) = await Svc.Api.GetSubtitleTracksAsync(bvid, cid);
            if (_closing || gen != _loadGen) return;
            if (!ok)
            {
                // ★ 接口失败 ≠ 这个视频没有字幕(评审意见第 7 条)。
                //   原来把两者并在一起静默忽略, 后果是网络抖一下按钮就永久变"无字幕"并置灰,
                //   用户得换片才能重试。现在记下失败态: 下次切集/重新载入会再取一次。
                //   (第 3 条落地后按钮在失败态也不再出现, 所以提示里不能写"点字幕按钮可重试".)
                _subtitleTracksFailed = true;
                UpdateSubtitleButton();
                Svc.Toast.Show(string.IsNullOrWhiteSpace(err) ? "字幕加载失败" : err);
                return;
            }
            if (tracks.Count == 0)
            {
                // 没有字幕是常态(绝大多数视频本来就没有), 静默即可: 按钮随之隐藏(无轨不留入口)。
                UpdateSubtitleButton();
                return;
            }

            _subtitleTracks.Clear();
            _subtitleTracks.AddRange(tracks);

            // 默认轨: 优先"人工中文", 其次"任何人工轨", 最后才是 AI 轨 ——
            // 人工翻译的质量高于 AI 生成, 有就一定先用它(与 B 站客户端同款优先级)。
            _subtitleSelectedIndex = PickDefaultSubtitleTrack();
            // ★★ 字幕默认开关的三条口径(按 B 站 web 端的习惯写成代码证据, 设置项
            //    SettingsStore.SubtitleEnabled 全工程只有这一处消费, 判据就落在这里):
            //    1) 作者自制上传的外挂字幕(IsAi == false): 默认开启 —— 是否真的自动开由设置页
            //       「默认显示字幕」决定。SettingsStore.SubtitleEnabled 默认 true, 即默认开;
            //       ★ 产品决策(2026-11-08 确认): 保持默认 true, 与 B 站 web 端一致 ——
            //         web 端 handleSubtitleInfo 拿到 subtitles 且存在主轨(role=1)时就是
            //         自动开字幕, 没有主轨才置 isclosed=true 关掉。本节拍的就是同一口径。
            //       关掉它的用户连人工轨也不自动开, 轨列表照旧保留, 用户仍可在弹层里手动选;
            //    2) AI 字幕(IsAi == true): 默认关闭, 一律不自动开。AI 轨质量不可控, 网页端
            //       也是默认不勾选, 由用户自己在弹层里选; 轨本身必须保留, 否则用户永远选不到它;
            //    3) 人工轨与 AI 轨都没有: 控制栏字幕按钮整个隐藏(见 UpdateSubtitleButton 的
            //       可见性口径), 不留下只有「关闭字幕」一项的空入口;
            //       ★ 例外: 取轨**接口失败**时按钮不隐藏而是显示「字幕重试」(同上, 作者补充)。
            _subtitleOn = _subtitleSelectedIndex >= 0
                          && IsManualSubtitleTrack(_subtitleSelectedIndex)
                          && Svc.Settings.SubtitleEnabled;
            UpdateSubtitleButton();
            // 弹层已经开着(用户在轨列表还没回来时就点开了): 要把刚到的轨填进去,
            // 否则弹层里只剩一条「关闭字幕」, 用户会以为这视频没字幕(评审意见小问题)。
            if (SubtitlePopup != null && SubtitlePopup.IsOpen) FillSubtitleMenu();
            // ★ 自动选轨失败要静默(评审意见第 7 条残留): 这条轨是播放器自己挑的, 用户没点过,
            //   下载失败只切按钮文案「字幕失败」, 不弹 Toast 打断播放; 手动选轨/重试才提示。
            if (_subtitleOn) await LoadSubtitleCuesAsync(_subtitleSelectedIndex, quietCueFail);
        }
        catch (Exception ex) { App.ReportError(ex); }
    }

    /// <summary>挑默认字幕轨的下标; 没有可用轨返回 -1。</summary>
    private int PickDefaultSubtitleTrack()
    {
        if (_subtitleTracks.Count == 0) return -1;

        for (var i = 0; i < _subtitleTracks.Count; i++)
        {
            var t = _subtitleTracks[i];
            if (!t.IsAi && t.Lan.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return i;
        }
        for (var i = 0; i < _subtitleTracks.Count; i++)
            if (!_subtitleTracks[i].IsAi) return i;
        return 0;
    }

    /// <summary>
    /// 下标是否为"作者自制上传的外挂字幕"(人工轨); 越界或无轨返回 false。
    /// 默认开关与按钮文案都要按它区分人工字幕 / AI 字幕, 收在一处避免判定口径分叉。
    /// </summary>
    private bool IsManualSubtitleTrack(int index)
        => index >= 0 && index < _subtitleTracks.Count && !_subtitleTracks[index].IsAi;

    /// <summary>
    /// 下载指定轨的 cue 列表。切轨与首次自动选轨都走这里。
    /// </summary>
    /// <param name="silentFail">
    /// 静默失败(评审意见第 7 条): 自动选轨是播放器自己挑的轨, 失败不该用 Toast 打断播放,
    /// 只把按钮文案切成「字幕失败」并放行用户手动重选; 手动选轨与重试一律 false, 必须给提示。
    /// </param>
    private async Task LoadSubtitleCuesAsync(int index, bool silentFail = false)
    {
        if (_closing || index < 0 || index >= _subtitleTracks.Count) return;

        var gen = _loadGen;
        var url = _subtitleTracks[index].SubtitleUrl;
        _subtitleCues.Clear();
        _subtitleCueFailed = false;
        _subtitleShownText = "";
        if (SubtitleBox != null) SubtitleBox.Visibility = Visibility.Collapsed;
        if (string.IsNullOrEmpty(url))
        {
            // 轨道存在但地址为空: 这是一条"坏轨", 对用户来说和下载失败没区别
            _subtitleCueFailed = true;
            UpdateSubtitleButton();
            if (!silentFail) Svc.Toast.Show("该字幕轨没有可用地址");
            return;
        }

        try
        {
            var (ok, err, cues) = await Svc.Api.GetSubtitleCuesAsync(url);
            // 双闸: 换片了作废; 用户在下载途中换了轨也作废(否则旧轨的 cue 会写进新轨)
            if (_closing || gen != _loadGen || index != _subtitleSelectedIndex) return;
            if (!ok)
            {
                // ★ 下载失败与"这条轨真的没有内容"是两件事(评审意见第 7 条), 提示必须分开:
                //   原来统一说"该字幕轨暂无内容", 用户会以为是这条轨本身没字幕,
                //   于是换轨反复试, 其实只是网络没通。
                _subtitleCueFailed = true;
                UpdateSubtitleButton();
                if (!silentFail)
                    Svc.Toast.Show(string.IsNullOrWhiteSpace(err) ? "字幕加载失败, 重新选该轨可重试" : err);
                return;
            }
            if (cues.Count == 0)
            {
                _subtitleCueFailed = true;
                UpdateSubtitleButton();
                if (!silentFail) Svc.Toast.Show("该字幕轨暂无内容");
                return;
            }

            _subtitleCues.Clear();
            _subtitleCues.AddRange(cues.OrderBy(c => c.From));
            _subtitleCueFailed = false;
            UpdateSubtitleButton();
            // 立刻按当前位置定一次位: 用户在 10 分钟处换轨, 不该等到下一句台词才出字幕
            UpdateSubtitleCue();
        }
        catch (Exception ex) { App.ReportError(ex); }
    }

    /// <summary>
    /// 按当前播放位置刷新字幕(由 TimeChanged 驱动, 约 4Hz)。
    ///
    /// ★ 为什么每次二分而不是维护游标: 字幕条数通常几百到几千, 二分是 O(log n),
    ///   4Hz 的开销可以忽略; 而游标方案要处理三种失配(往回跳/往前跳/字幕比播放晚到),
    ///   弹幕那份代码为此专门写了 IsDanmakuCursorStale —— 字幕没有"逐条推送"的需求,
    ///   用二分反而不出错。
    /// </summary>
    private void UpdateSubtitleCue()
    {
        if (SubtitleBox == null || SubtitleText == null) return;

        // 外观(字号/底衬/底距)跟着设置页走。放在每帧入口用"值变了才写"的方式比对:
        // 设置页拖动滑杆时这个窗口可能还开着, 用户期望视频里的字幕立刻跟着变;
        // 全程有 _subtitleLookXxx 缓存, 值没变时不产生任何布局开销。
        ApplySubtitleLook();

        // 聆听态下字幕层被聆听画面盖着, 顺手收起省一次布局
        if (!_subtitleOn || _closing || _mp == null || _isListenMode || _subtitleCues.Count == 0)
        {
            if (SubtitleBox.Visibility != Visibility.Collapsed) SubtitleBox.Visibility = Visibility.Collapsed;
            if (_subtitleShownText.Length > 0) _subtitleShownText = "";
            return;
        }

        var seconds = _mp.Time / 1000.0;
        // 命中下标是纯派生值, 只在本方法内存活(评审意见小问题): 原先做成字段,
        // 换片自检清单还得给它留一格, 但它从来没有跨方法被读过。
        var cueIndex = FindSubtitleCueIndex(seconds);

        if (cueIndex < 0)
        {
            // 两句台词之间(或片头): 收起显示层, 避免留着一句过期台词
            if (SubtitleBox.Visibility != Visibility.Collapsed) SubtitleBox.Visibility = Visibility.Collapsed;
            _subtitleShownText = "";
            return;
        }

        var text = _subtitleCues[cueIndex].Content;
        if (text == _subtitleShownText)
        {
            // 同一条: 只补一次可见性(切轨/退出聆听后显示层可能还是收起的)
            if (SubtitleBox.Visibility != Visibility.Visible) SubtitleBox.Visibility = Visibility.Visible;
            return;
        }

        _subtitleShownText = text;
        SubtitleText.Text = text;
        // ★ 不再每次换句都 new 一个刷子: 前景色已由 XAML 显式写成 White
        //   (见 SubtitleText 的 Foreground), 显式局部值本来就压得住全局隐式样式,
        //   所以这里什么都不用做 —— 原先那行每换一条 cue 就新建一个 SolidColorBrush,
        //   一场电影下来是上千次无谓的托管分配。
        SubtitleBox.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 二分找出当前时刻命中的 cue 下标; 没有命中返回 -1。
    ///
    /// 判据: From &lt;= t &lt;= To。To 缺失(接口少数条目只给 From)时用**下一条的 From**
    /// 兜底 —— 否则那条字幕会一直挂到视频结束。末尾加 0.25s 宽限, 消除相邻条之间
    /// 几十毫秒的空档导致的闪烁。
    /// </summary>
    private int FindSubtitleCueIndex(double seconds)
    {
        var list = _subtitleCues;
        int lo = 0, hi = list.Count - 1, hit = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (list[mid].From <= seconds) { hit = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (hit < 0) return -1;

        var cue = list[hit];
        var end = cue.To > cue.From
            ? cue.To
            : (hit + 1 < list.Count ? list[hit + 1].From : double.PositiveInfinity);
        return seconds <= end + 0.25 ? hit : -1;
    }

    /// <summary>控制栏上的字幕入口: 点开弹层(轨列表 + 字号)。</summary>
    private void OnSubtitleClick(object sender, RoutedEventArgs e)
    {
        if (SubtitlePopup == null) return;
        if (SubtitlePopup.IsOpen) { SubtitlePopup.IsOpen = false; return; }

        // 取轨失败过 → 这一下就是重试(评审意见第 3/7 条): 按钮不再因为失败而置灰,
        // 用户不必换片就能再拉一次; 重试成功后第二次点击才是正常弹层。
        if (_subtitleTracksFailed)
        {
            _ = RetrySubtitleTracksAsync();
            return;
        }

        // 正常但确实没轨: 弹一个只有「关闭字幕」的层没有意义, 直接说明情况
        if (_subtitleTracks.Count == 0)
        {
            Svc.Toast.Show("该视频没有可用字幕");
            return;
        }

        FillSubtitleMenu();
        if (BtnSubtitle != null) SubtitlePopup.PlacementTarget = BtnSubtitle;
        // 右缘对齐交给构造期挂好的 Opened 回调(见 RightAlignPopup 用法处), 这里不必再算一遍
        SubtitlePopup.IsOpen = true;
    }

    /// <summary>
    /// 重试取字幕轨(评审意见第 7 条给失败态留的出口)。
    /// 与首次加载走同一条 LoadSubtitleAsync, 只是换了入口 —— 代次闸/自动选轨/默认开关
    /// 全都复用, 不另写一套逻辑, 免得两条路径行为分叉。
    /// </summary>
    private async Task RetrySubtitleTracksAsync()
    {
        if (_isLive || _isLocalPlayback || _currentCid <= 0 || string.IsNullOrEmpty(_currentBvid)) return;
        _subtitleTracksFailed = false;
        Svc.Toast.Show("正在重新加载字幕…");
        await LoadSubtitleAsync(_currentBvid, _currentCid);
        // 载入成功且有轨 → 直接替用户把弹层打开, 省一次点击(失败的反馈已经在 Toast 里)
        if (!_subtitleTracksFailed && _subtitleTracks.Count > 0 && SubtitlePopup != null && SubtitleBtnText != null)
        {
            FillSubtitleMenu();
            if (BtnSubtitle != null) SubtitlePopup.PlacementTarget = BtnSubtitle;
            SubtitlePopup.IsOpen = true;
        }
    }

    /// <summary>
    /// 填充字幕弹层: 第一项固定是「关闭字幕」, 其后每条轨一项。
    /// 与清晰度列表同款 —— 用 ListBoxItem + Tag 而不是数据绑定, 保持与既有代码一致。
    /// </summary>
    private void FillSubtitleMenu()
    {
        if (SubtitleList == null) return;
        _fillingSubtitleMenu = true;
        try
        {
            SubtitleList.Items.Clear();
            SubtitleList.Items.Add(MakeSubtitleItem("关闭字幕", -1));
            for (var i = 0; i < _subtitleTracks.Count; i++)
            {
                var t = _subtitleTracks[i];
                // 显示名统一走 SubtitleTrack.DisplayName:
                // 视频自带字幕显示原名, AI 字幕(含 AI 翻译)统一追加 " · AI", 便于多条 AI 轨并排时一眼识别。
                var label = t.DisplayName;
                if (string.IsNullOrWhiteSpace(label)) label = "字幕 " + (i + 1);
                SubtitleList.Items.Add(MakeSubtitleItem(label, i));
            }
            // 选中项回灌: 打开弹层时高亮当前轨(没开就高亮「关闭字幕」)
            SubtitleList.SelectedIndex = _subtitleOn && _subtitleSelectedIndex >= 0
                ? _subtitleSelectedIndex + 1
                : 0;
        }
        finally { _fillingSubtitleMenu = false; }
    }

    /// <summary>
    /// 造一个字幕轨菜单项。Content 必须是**字符串**(不能塞 TextBlock):
    /// SubtitleList 的 ItemContainerStyle 里挂了 ContentTemplate(TextBlock Text="{Binding}"),
    /// Content 一旦是控件对象, {Binding} 拿到的就是控件本身、ToString() 出来的是类型名,
    /// 弹层里会显示成 "System.Windows.Controls.TextBlock"(已实机复现)。
    /// 单行 + 字符级省略号由那个模板里的 TextBlock 负责, 这里只负责数据。
    /// </summary>
    private static ListBoxItem MakeSubtitleItem(string text, int index) => new()
    {
        Tag = index,
        Content = text
    };

    private void OnSubtitleSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingSubtitleMenu || SubtitlePopup == null) return;
        if (SubtitleList.SelectedItem is not ListBoxItem li || li.Tag is not int index) return;
        SubtitlePopup.IsOpen = false;

        // 「关闭字幕」
        if (index < 0)
        {
            _subtitleOn = false;
            _subtitleSelectedIndex = -1;
            _subtitleCues.Clear();
            _subtitleCueFailed = false;
            _subtitleShownText = "";
            if (SubtitleText != null) SubtitleText.Text = "";
            if (SubtitleBox != null) SubtitleBox.Visibility = Visibility.Collapsed;
            UpdateSubtitleButton();
            return;
        }

        if (index >= _subtitleTracks.Count) return;
        // 点的就是当前轨 → 无事可做。**但失败态除外**(评审意见第 7 条):
        // 上一次下载失败时 _subtitleOn 还是 true, 原样 return 会让这条轨再也点不动,
        // 用户唯一的出路是换轨再换回来。这里把失败态视为"允许重试"。
        if (index == _subtitleSelectedIndex && _subtitleOn && !_subtitleCueFailed) return;
        _subtitleSelectedIndex = index;
        _subtitleOn = true;
        UpdateSubtitleButton();
        _ = LoadSubtitleCuesAsync(index);
    }

    /// <summary>字幕字号滑杆: 50%~300%, 立刻作用于显示层并落盘。</summary>
    private void OnSubtitleFontChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var percent = (int)Math.Round(e.NewValue);
        if (SubtitleFontValue != null) SubtitleFontValue.Text = percent + "%";
        if (_syncingSubtitleFont) return;
        Svc.Settings.SetSubtitleFontScalePercent(percent);
        ApplySubtitleFontScale(percent);
    }

    /// <summary>把字号百分比落到字幕显示层。</summary>
    private void ApplySubtitleFontScale(int? percent = null)
    {
        if (SubtitleText == null) return;
        var pct = percent ?? Svc.Settings.SubtitleFontScalePercent;
        var size = Math.Round(SubtitleBaseFontSize * pct / 100.0);
        SubtitleText.FontSize = size;
        // 行高按 1.5 倍走: 两行同显时不会挤在一起, 单行时又不至于离得太远
        SubtitleText.LineHeight = Math.Round(size * 1.5);
        _subtitleLookFont = pct;
    }

    /// <summary>
    /// 把设置页的字幕外观(字号 / 底衬不透明度 / 距画面底部距离)落到字幕层。
    ///
    /// ★ 三项各自缓存"上次写进去的值", 只有真的变了才动控件 —— 本方法由 UpdateSubtitleCue
    ///   每帧(约 4Hz)调用, 不加缓存会让每次 TimeChanged 都触发一次 Border 布局。
    /// ★ 底衬颜色用 ARGB 直接算: 设置里存的是 0~100 的百分比, 0 时必须走 Transparent 而不是
    ///   ARGB(0,0,0,0) —— 后者在部分渲染路径上会残留一层极淡的黑边。
    /// </summary>
    private void ApplySubtitleLook()
    {
        if (SubtitleText == null) return;

        var pct = Svc.Settings.SubtitleFontScalePercent;
        if (pct != _subtitleLookFont) ApplySubtitleFontScale(pct);

        if (SubtitleBox == null) return;

        var opacity = Svc.Settings.SubtitleBackgroundOpacityPercent;
        if (opacity != _subtitleLookOpacity)
        {
            if (opacity <= 0)
            {
                SubtitleBox.Background = System.Windows.Media.Brushes.Transparent;
            }
            else
            {
                var a = (byte)Math.Round(255.0 * Math.Clamp(opacity, 0, 100) / 100.0);
                SubtitleBox.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(a, 0, 0, 0));
            }
            _subtitleLookOpacity = opacity;
        }

        var margin = Svc.Settings.SubtitleBottomMargin;
        if (margin != _subtitleLookMargin)
        {
            SubtitleBox.Margin = new Thickness(0, 0, 0, margin);
            _subtitleLookMargin = margin;
        }
    }

    /// <summary>
    /// 刷新字幕按钮的可见性、可用性与文案。
    /// ★ 可见性主口径(评审意见第 3 条): **确实存在字幕轨时按钮才出现** —— 作者自制的外挂
    ///   字幕与 AI 字幕都算"有字幕"; 没有轨就隐藏, 不保留置灰的空按钮, 也不写「无字幕」。
    /// ★ 唯一例外是**取轨接口失败**(作者原话的补充要求): 失败时轨同样是空, 若一并隐藏,
    ///   用户就再也没有手动重试的地方, 只能换片。所以失败态显示「字幕重试」并保持可点,
    ///   点击即重新拉轨(见 OnSubtitleClick 的 _subtitleTracksFailed 分支)。
    /// </summary>
    private void UpdateSubtitleButton()
    {
        if (BtnSubtitle == null || SubtitleBtnText == null) return;

        // 这个源本来就能播字幕(直播/本地文件永远不会有轨, 这里再兜一层)
        var sourceOk = !_isLive && !_isLocalPlayback;
        var hasTrack = _subtitleTracks.Count > 0;
        var usable = hasTrack && sourceOk;
        // ★ 取轨接口失败必须单独留一个可点入口(评审意见第 3 条的作者补充):
        //   失败时 _subtitleTracks 同样是空, 若跟"这个视频没有字幕"一样直接隐藏,
        //   用户永远点不到重试, 只能换片才行。所以失败态放行: 按钮写「字幕重试」,
        //   点一下直接重新拉轨(见 OnSubtitleClick 的 _subtitleTracksFailed 分支)。
        var retryable = _subtitleTracksFailed && sourceOk;

        var wantVisible = (usable || retryable) ? Visibility.Visible : Visibility.Collapsed;
        // ★ 实测补充(T12, 番剧 ep 路径): 番剧选集/连播与普通视频共用本方法, 该集返回 0 条轨时
        //   按钮同样整体 Collapsed —— 与普通无字幕视频保持同一种表现, 不留"只有关闭字幕项的
        //   空入口"(评审意见第 3 条口径)。番剧不做特例: 换集后轨是重新按 cid 取的, 另有轨的
        //   集会自己把按钮露回来(见 ResetForNewMedia -> LoadSubtitleAsync)。
        if (BtnSubtitle.Visibility != wantVisible) BtnSubtitle.Visibility = wantVisible;
        // 隐藏态下按钮不可见, 但仍把可用性/透明度写回一致值: 防止别处直接读状态时拿到旧值
        BtnSubtitle.IsEnabled = usable || retryable;
        BtnSubtitle.Opacity = (usable || retryable) ? 1.0 : 0.4;
        if (!usable && !retryable) return;

        // 按钮文案一律只写「字幕」(对齐 B 站 web 端): 开着是「字幕」, 关着也是「字幕」,
        // 按钮只表示"这里有字幕入口"。前景色固定为与弹幕/清晰度等按钮同值的 #F2FFFFFF
        // (XAML 写死), 不随开关变化 —— 同一个控制栏里只有一个按钮会变色本身就像 bug。
        // 两处破例都是"点了必须有反应"的场景:
        //   1) 取轨接口失败 → 「字幕重试」, 按钮本身就是重试入口;
        //   2) 已选轨但 cue 拉不到 → 「字幕失败」, 重新点开该轨即重试(见 OnSubtitleSelected)。
        SubtitleBtnText.Text = retryable
            ? "字幕重试"
            : (_subtitleOn && _subtitleCueFailed) ? "字幕失败" : "字幕";
    }

    /// <summary>
    /// 全局字幕外观变化(来自设置页): 让画面上正在显示的字幕立刻跟上。
    ///
    /// ★ 为什么必须挂这条事件(评审意见第 5 条): 之前 ApplySubtitleLook 只在
    ///   UpdateSubtitleCue 里被调用, 而那个方法由 TimeChanged 驱动 —— 用户暂停视频后
    ///   去设置页拖字号/底衬/底距滑杆, 画面上的字幕一动不动, 得点一下播放才生效。
    /// ★ 弹层里的三个滑杆走的是"先落设置"路线, 落库时 SettingsStore 广播这条事件,
    ///   于是"播放器改"和"设置页改"汇到同一个入口, 不会出现两套算法漂移。
    /// </summary>
    private void OnSubtitleSettingsChanged()
    {
        if (_closing) return;
        // 弹层里只有字号一个滑杆(底衬与底距在设置页), 所以这里只回灌它。
        // 闸门关着时不回灌: 用户正拖着弹层里的滑杆, 事件正是它自己触发的,
        // 再写回 Value 会把拖动打断(与构造期灌值同一套路, 见 _syncingSubtitleFont)。
        if (!_syncingSubtitleFont && SubtitlePopup != null && !SubtitlePopup.IsOpen
            && SubtitleFontSlider != null)
        {
            SubtitleFontSlider.Value = Svc.Settings.SubtitleFontScalePercent;
            if (SubtitleFontValue != null) SubtitleFontValue.Text = Svc.Settings.SubtitleFontScalePercent + "%";
        }
        ApplySubtitleLook();
    }
}
