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
    // ------------------------------------------------------------ 视频合集

    /// <summary>
    /// 当前正在播的那一集(不在任何合集里时为 null)。
    /// 用来给面板里的条目打"就是这条"的标, 也是判断"要不要为切集重置进度"的依据。
    /// </summary>
    private SeasonEpisode? _currentSeasonEpisode;

    /// <summary>
    /// 当前视频有没有合集(有才显示「合集」入口)。
    /// 单独一个 bool 而不是判 <c>_currentSeasonEpisode != null</c>: 入口是否出现只取决于
    /// "这个视频属于合集吗", 而 _currentSeasonEpisode 还可能因为定位不上(合集里有一条
    /// bvid/cid/aid 都对不上的脏数据)而暂时为 null —— 那种情况下面板仍该能打开。
    /// </summary>
    private bool _hasSeason;

    /// <summary>当前视频的合集(没有则 null)。面板的数据源, 也是"换片后要不要重灌列表"的依据</summary>
    private SeasonInfo? _season;

    /// <summary>
    /// "正在为切集换片"的闸门。合集列表是 ListBox, 连点两集会连续触发两次 SelectionChanged ——
    /// 不挡住就是两个 LoadVideoAsync 并发, 后回来的那个覆盖前一个, 表现是"点了 A 却播了 B"。
    /// </summary>
    private bool _switchingSeasonEpisode;

    /// <summary>
    /// 合集面板是否展开。
    ///
    /// ★ 登记在 <see cref="PersistentFields"/> 里(跨片保持): 用户开着面板连看几集是常态;
    ///   每换一集都把面板收起来, 想接着点下一集还得再展开一次, 很烦。
    ///   代价是换到普通视频时面板会空着 —— 那种情况由 ClearSeasonUi 把面板**整个收起**,
    ///   不留一个空壳面板(见 LoadVideoAsync 里没有合集的分支)。
    /// </summary>
    private bool _seasonOpen;

    /// <summary>点 tab 行右侧的「合集」: 开关面板(不请求接口 —— 数据在详情加载时就已经拿到了)</summary>
    private void OnSeasonToggleClick(object sender, RoutedEventArgs e)
    {
        if (_closing || !_hasSeason || SeasonPanel == null) return;
        if (_seasonOpen) CloseSeasonPanel();
        else OpenSeasonPanel();
    }

    private void OnSeasonCloseClick(object sender, RoutedEventArgs e) => CloseSeasonPanel();

    private void OpenSeasonPanel()
    {
        if (_closing || SeasonPanel == null) return;
        _seasonOpen = true;
        SeasonPanel.Visibility = Visibility.Visible;
        UpdateSeasonButtonState();
        ScrollSeasonToCurrent();
    }

    /// <summary>
    /// 把合集列表滚到"正在播的那一集"。100 多集的合集里不滚, 用户得自己找半天。
    ///
    /// ★ 必须派发到 Loaded 优先级之后: 面板刚由 Collapsed 变 Visible 时布局还没跑完,
    ///   此时 ScrollIntoView 会因为没有可视区域高度而无效(静默不滚, 不报错)。
    /// </summary>
    private void ScrollSeasonToCurrent()
    {
        if (SeasonList?.SelectedItem == null) return;
        var target = SeasonList.SelectedItem;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_closing || !_seasonOpen) return;
            SeasonList?.ScrollIntoView(target);
        }));
    }

    private void CloseSeasonPanel()
    {
        _seasonOpen = false;
        if (SeasonPanel != null) SeasonPanel.Visibility = Visibility.Collapsed;
        UpdateSeasonButtonState();
    }

    /// <summary>
    /// 刷新「合集」按钮的可见性与高亮态。
    /// 没有合集的视频**隐藏**按钮(而不是禁用): 右侧信息栏只有 400px, 一个常年灰着的按钮
    /// 既占位又像是坏了; 官方在没有合集时也不给入口。
    ///
    /// ★ 2026-10-03 起高亮改由 `Tag="on"/"off"` 驱动(样式里的 DataTrigger), 不再直接写
    ///   `Foreground`: 与 简介/评论 统一风格后, 点亮要连带那根 2px 下划线一起 ——
    ///   只设 Foreground 的话下划线不会亮, 看着像个半吊子的选中态。
    ///   文字颜色仍由 XAML 里那个 TextBlock 绑 Button.Foreground 拿到(全局隐式 TextBlock
    ///   样式带 Foreground setter, 优先级高于继承, 光设 Button.Foreground 是改不动它的)。
    /// </summary>
    private void UpdateSeasonButtonState()
    {
        if (BtnSeason == null) return;
        BtnSeason.Visibility = _hasSeason ? Visibility.Visible : Visibility.Collapsed;
        BtnSeason.Tag = _seasonOpen ? "on" : "off";
    }

    /// <summary>详情加载完之后灌合集数据</summary>
    private void ApplySeason(SeasonInfo? season)
    {
        if (_closing) return;

        if (season == null || season.Episodes.Count == 0)
        {
            // 没有合集: 入口隐藏、面板整个收起 —— 不留一个空壳面板(上一个视频的面板可能正开着)
            _season = null;
            _currentSeasonEpisode = null;
            _hasSeason = false;
            _seasonOpen = false;
            ClearSeasonUi(collapsePanel: true);
            UpdateSeasonButtonState();
            return;
        }

        _season = season;
        _hasSeason = true;
        _currentSeasonEpisode = FindCurrentEpisode(season);

        ClearSeasonUi(collapsePanel: false);
        // 列表数据源 = 展平后的全部集。顺序即官方顺序(见 ApiClient.ParseSeason)
        if (SeasonList != null)
        {
            SeasonList.ItemsSource = season.Episodes;
            // 选中 = 高亮。★ 必须走 SelectedItem, 而不是"给每条算一个 IsPlaying 字段":
            // 那些条目是普通 POCO, 没有 INPC, 改了字段界面不会刷新。
            // 赋值会触发 OnSeasonEpisodeSelected —— 那一次的目标就是当前集, 会被自己短路掉。
            SeasonList.SelectedItem = _currentSeasonEpisode;
        }
        SeasonTitleText.Text = season.Title;
        // 副标题带上分组数(仅多分组时) —— 让用户知道面板是按分组排的
        SeasonMetaText.Text = season.Sections.Count > 1
            ? $"共 {season.Episodes.Count} 集 · {season.Sections.Count} 个分组"
            : $"共 {season.Episodes.Count} 集";
        // 定位不上当前集时给一句明确说明: 好过列表里一个高亮都没有, 用户会以为坏了
        SeasonHint.Text = _currentSeasonEpisode == null ? "没能在合集里定位到当前这一集" : "";
        SeasonLoadingRing.Visibility = Visibility.Collapsed;
        UpdateSeasonButtonState();
        // 面板本来开着(用户连看几集)就保持开着 —— 换片不该把面板收起来
        if (_seasonOpen) SeasonPanel.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 定位"当前播的是合集里的哪一集"。
    /// 优先级: cid 精确匹配 &gt; bvid 匹配 &gt; aid 匹配。
    ///
    /// 先按 cid 是因为**同一个 bvid 可能有多个分 P**, 合集里存的是"这一集的默认分 P",
    /// 用户从搜索等入口点进来的可能是它的第 2 个分 P —— 那时 bvid 相同但实际内容不同,
    /// 用 cid 才能把"第几集"标对。取不到 cid 才退回 bvid, 再退回 aid。
    /// </summary>
    private SeasonEpisode? FindCurrentEpisode(SeasonInfo season)
    {
        if (_currentCid > 0)
        {
            var byCid = season.Episodes.FirstOrDefault(e => e.Cid == _currentCid);
            if (byCid != null) return byCid;
        }
        if (!string.IsNullOrEmpty(_currentBvid))
        {
            var byBv = season.Episodes.FirstOrDefault(e => e.Bvid == _currentBvid);
            if (byBv != null) return byBv;
        }
        if (_aid > 0)
        {
            var byAid = season.Episodes.FirstOrDefault(e => e.Aid == _aid);
            if (byAid != null) return byAid;
        }
        return null;
    }

    /// <summary>
    /// 点了合集里的某一集 → **在本窗口**换片(不新开窗口)。
    ///
    /// ★ 这里刻意**不调 Svc.Player.PlayVideo**: 那条路会走 PlayerService 的"复用闸门",
    ///   而闸门在窗口不可复用的场景下会**新建一个 PlayerWindow** —— 用户点下一集却弹出一个
    ///   新窗口, 正是任务里明确禁止的。换片的动作直接落在本窗口上, 与「切换清晰度」同款:
    ///   Stop 掉当前流 → ResetForNewMedia 清掉上一片的全部残留 → 重新走 LoadVideoAsync。
    ///
    /// ★ 不在这里手工挪 SelectedItem: ResetForNewMedia 会把列表清空, 换片完成后
    ///   ApplySeason 会重新定位并设好选中项 —— 那才是唯一的高亮来源。手工补一次反而会在
    ///   "换片失败"时留下一个高亮但没在播的条目。
    /// </summary>
    private void OnSeasonEpisodeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_closing || SeasonList == null) return;
        if (SeasonList.SelectedItem is not SeasonEpisode ep) return;
        // 选中的就是当前这一集: 不做任何换片动作。
        // (ApplySeason 里给 SelectedItem 赋值也会走到这里, 必须能自己短路掉)
        if (ep.Bvid == _currentBvid) return;
        _ = SwitchToSeasonEpisodeAsync(ep);
    }

    private async Task SwitchToSeasonEpisodeAsync(SeasonEpisode ep)
    {
        if (_closing || string.IsNullOrEmpty(ep.Bvid)) return;
        // 防连点: 上一次换片还没走完就再点一集, 会把两个 LoadVideoAsync 叠起来跑,
        // 后一个的结果覆盖前一个 —— 表现是"点了 A 结果播了 B"。用换片闸门直接挡掉。
        if (_switchingSeasonEpisode) return;
        _switchingSeasonEpisode = true;
        try
        {
            try { _mp.Stop(); } catch { }
            _currentBvid = ep.Bvid;
            // ★ 关键: 合集里的集是**在线稿件**, 不是本地文件/直播 —— 从离线缓存/直播那类入口
            //   切过来时这两个标志还是 true, 不清掉新片会被当成"没有在线上下文",
            //   弹幕 / 三连 / 历史 / 评论全都不会加载。
            _isLocalPlayback = false;
            _isLive = false;
            _liveRoomId = 0;
            ResetForNewMedia();
            await LoadVideoAsync();
        }
        finally
        {
            _switchingSeasonEpisode = false;
        }
    }

    // ------------------------------------------------------------ 视频分P

    /// <summary>
    /// 当前视频的全部分P(单P 视频也会有一项)。分P 弹层的唯一数据源。
    ///
    /// ★ 与合集严格分开存: 两者能同时存在(一个分P 稿件被收进合集), 混在一份列表里
    ///   用户根本分不清"点这条是换稿件还是换段落"。
    /// </summary>
    private List<VideoPage> _pages = new();

    /// <summary>分P 够不够两条(够才值得给入口)。单P 视频的 pages 里也只有 1 项</summary>
    private bool _hasPages;

    /// <summary>
    /// "正在换 P"的闸门, 与 <see cref="_switchingSeasonEpisode"/> 同款理由:
    /// 连点两个 P 会叠起两个 LoadVideoAsync, 后回来的覆盖先回来的。
    /// </summary>
    private bool _switchingPage;

    /// <summary>
    /// 详情加载完之后灌分P 数据。
    ///
    /// ★ 入口的显隐只认"有没有多于一个 P": 单P 视频给一个「分P」按钮纯属噪音
    ///   (对照: B 站自己在单P 视频上也不给选集)。
    /// </summary>
    private void ApplyPages(List<VideoPage> pages)
    {
        if (_closing) return;

        _pages = pages ?? new List<VideoPage>();
        _hasPages = _pages.Count > 1;

        if (BtnPages == null) return;
        BtnPages.Visibility = _hasPages ? Visibility.Visible : Visibility.Collapsed;
        if (!_hasPages) ClosePagesPopup();
        else UpdatePagesTitle();
    }

    /// <summary>关掉分P 弹层(换片 / 单P 视频时调用)。没开就别动, 免得白白改一次 IsOpen</summary>
    private void ClosePagesPopup()
    {
        if (PagesPopup == null || !PagesPopup.IsOpen) return;
        PagesPopup.IsOpen = false;
    }

    /// <summary>
    /// 按钮上显示 "分P 12/40" —— 把"现在在第几段 / 一共几段"直接写在入口上。
    /// 40 段的纪录片只靠弹层里的高亮, 用户永远不知道自己在哪一段。
    /// </summary>
    private void UpdatePagesTitle()
    {
        if (BtnPages == null || BtnPagesText == null) return;
        var cur = CurrentPageIndex();
        BtnPagesText.Text = cur > 0
            ? $"分P {cur}/{_pages.Count}"
            : $"分P ({_pages.Count})";
    }

    /// <summary>当前正在播的是第几个 P(1 起; 定位不上返回 0)</summary>
    private int CurrentPageIndex()
    {
        if (_currentCid <= 0) return 0;
        foreach (var p in _pages)
            if (p.Cid == _currentCid) return p.Page;
        return 0;
    }

    /// <summary>点「分P」: 开/收弹层。不请求接口 —— pages 跟合集一样在详情里就拿到了</summary>
    private void OnPagesToggleClick(object sender, RoutedEventArgs e)
    {
        if (_closing || !_hasPages || PagesPopup == null) return;
        // ★ 不能直接取反 IsOpen(2026-11-14 修"点击一律是打开"): StaysOpen=False 的弹层
        //   在"再点按钮"那一下的 MouseDown 阶段就被 WPF 自动收起了, Click 到达时 IsOpen
        //   恒为 false, 取反永远走"展开"。统一走 TogglePlayerPopup 的收起时刻判定。
        TogglePlayerPopup(PagesPopup, onOpen: () =>
        {
            if (PagesList != null)
            {
                PagesList.ItemsSource = _pages;
                PagesList.SelectedItem = _pages.FirstOrDefault(p => p.Cid == _currentCid);
            }
            PagesMetaText.Text = $"共 {_pages.Count} 个分P";
        });
        // 高亮态跟随最终开关态(收起那条路 OnPagesPopupClosed 也会置 off, 两边一致)
        if (BtnPages != null) BtnPages.Tag = PagesPopup.IsOpen ? "on" : "off";
    }

    /// <summary>弹层外点/按 Esc 关掉弹层时, 把按钮的高亮态一并收回</summary>
    private void OnPagesPopupClosed(object sender, EventArgs e)
    {
        if (BtnPages != null) BtnPages.Tag = "off";
    }

    /// <summary>
    /// 选中某个分P → 换段播放。
    ///
    /// ★ 判据是 cid 不是 bvid: 分P 之间 **bvid 完全相同**, 用 bvid 判会永远短路成"没点"。
    ///   (这正是合集那条判据 `ep.Bvid != _currentBvid` 在分P 上不成立的原因。)
    /// </summary>
    private void OnPageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_closing || PagesList == null) return;
        if (PagesList.SelectedItem is not VideoPage p) return;
        if (p.Cid == _currentCid) return;
        PagesPopup?.SetCurrentValue(System.Windows.Controls.Primitives.Popup.IsOpenProperty, false);
        _ = SwitchToPageAsync(p);
    }

    private async Task SwitchToPageAsync(VideoPage p)
    {
        if (_closing || p.Cid <= 0) return;
        if (_switchingPage) return;
        _switchingPage = true;
        try
        {
            try { _mp.Stop(); } catch { }
            // ★ _currentBvid 一个字都不改: 分P 是同一个稿件的分段。改 bvid 会让
            //   弹幕游标 / 历史进度 / 三连状态全部挂到别的稿件上。
            //   ResetForNewMedia 照旧要调: cid 变了就是另一段内容, 进度、弹幕游标、
            //   视频总长、评论都得重来(它内部会把 _indexInLib 归零)。
            _isLocalPlayback = false;
            _isLive = false;
            _liveRoomId = 0;
            ResetForNewMedia();
            await LoadVideoAsync(p.Cid);
        }
        finally
        {
            _switchingPage = false;
        }
    }

}
