using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using BiliDesk.Models;

namespace BiliDesk.Views;

public partial class PlayerWindow : FluentWindow
{
    // ------------------------------------------------------------ 章节(2026-11-21)
    //
    // 数据: /x/player/wbi/v2 的 data.view_points[] —— 与字幕**共用同一次请求**
    //       (见 PlayerWindow.Subtitle.cs 的 LoadPlayerInfoAsync, 那边拿回 tracks + chapters)。
    // 渲染: 三处触点, 各管一件事 ——
    //   · 进度条刻度(ChapterMarks): "这个视频有分段";
    //   · 悬停/拖动气泡第二行(ProgressHintChapter): "这个点属于哪一章";
    //   · 控制栏章节胶囊(BtnChapters) + 弹层(ChapterPopup): 当前章名 + 跳转入口。
    // 原则: **无章节的视频完全无痕** —— 不显示按钮/刻度, 也不给"该视频无章节"的提示。
    //       B 站上大多数视频没有章节, 常驻一个死按钮只会添噪。
    //
    // ★ 不做的事(与用户确认过): 没有键盘快捷键, 没有跳章 OSD 提示。

    /// <summary>本片章节(已按 FromMs 升序)。空/单章一律视为"无章节", 见 ApplyChapters。</summary>
    private List<VideoChapter> _chapters = new();

    /// <summary>
    /// 控制栏是否处于"窄窗"形态: 胶囊收成一个图标按钮。
    /// 由 OnVideoAreaSizeChanged 更新, 属于**窗口级**状态(与片源无关)。
    /// </summary>
    private bool _chaptersCompact;

    /// <summary>填充章节弹层时抑制 SelectionChanged(与 _fillingQualityMenu / _fillingSubtitleMenu 同款闸)。</summary>
    private bool _fillingChapterMenu;

    /// <summary>
    /// 视频区窄于这个宽度就把章节胶囊收成图标。
    ///
    /// 比 <see cref="CompactControlBarWidth"/>(520) 高得多: 控制栏这一行在胶囊展开时约 710 DIP
    /// (播放键 30 + 时间 122 + 胶囊 ~164 + 音量 88 + 弹幕三颗 96 + 分隔/倍速/清晰度/全屏 ~170),
    /// 视频区 700 时可用内容只有 ~652, 右侧会被裁掉。820 能保证"宽窗形态一定放得下";
    /// 这是控制栏里最"软"的一项, 该在弹幕区域按钮/音量滑杆(520)之前先让位 ——
    /// 但它**只换形态不收入口**(600px 的窗口里照样要能跳章)。
    /// </summary>
    private const double ChapterCompactWidth = 820;

    /// <summary>章节刻度笔刷(建一次冻结复用: 每次重绘都要用, 别每次 new)。</summary>
    private static readonly Brush ChapterTickBrush = CreateChapterTickBrush();

    private static Brush CreateChapterTickBrush()
    {
        var b = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
        b.Freeze();
        return b;
    }

    /// <summary>够不够撑起章节功能(≥ 2 章)。</summary>
    private bool HasChapters => _chapters.Count >= 2;

    // ------------------------------------------------------------ 数据落地 / 重置

    /// <summary>
    /// 章节数据到达后落地。少于 2 章直接当"没有" —— 单章就是整片一段, 画刻度没有意义。
    ///
    /// ★ 由 <see cref="LoadPlayerInfoAsync"/> 在 gen 校验之后调用, 所以换片时旧响应
    ///   不会盖到新片上(与字幕同一条防串闸)。
    /// </summary>
    private void ApplyChapters(IReadOnlyList<VideoChapter>? chapters)
    {
        if (_closing) return;

        _chapters = chapters != null && ChapterParser.IsUsable(chapters)
            ? new List<VideoChapter>(chapters)
            : new List<VideoChapter>();

        // 数据换了 ⇒ 弹层里挂着的旧条目一律作废(重试/换片都可能走到这里)。
        // 关掉而不是就地刷新: 用户开着弹层时数据被换掉是极少数情况, 关掉最不容易出错。
        CloseChapterPopup();
        UpdateChapterUi();
        RenderChapterMarks();
    }

    /// <summary>
    /// 换片/换P 时把章节痕迹清干净(由 ResetForNewMedia 调用)。
    /// ★ 入口按钮必须一起收起: 新片的章节要等下一次 player/v2 响应, 这中间几百毫秒里
    ///   按钮若还挂着, 点开就是上一个视频的章节列表(与分P/合集同款的"串台"问题)。
    /// </summary>
    private void ResetChaptersForNewMedia()
    {
        _chapters.Clear();
        _fillingChapterMenu = false;
        if (ChapterMarks != null) ChapterMarks.Children.Clear();
        if (ChapterList != null)
        {
            ChapterList.ItemsSource = null;
            ChapterList.SelectedIndex = -1;
        }
        if (ChapterMetaText != null) ChapterMetaText.Text = "";
        if (BtnChapters != null) BtnChapters.Visibility = Visibility.Collapsed;
        CloseChapterPopup();
    }

    // ------------------------------------------------------------ 状态刷新

    /// <summary>当前播放位置(毫秒)。原生对象可能刚好失效 —— 所有读取都包在 try 里。</summary>
    private long CurrentPlaybackMs()
    {
        try
        {
            if (_mp != null)
            {
                var t = _mp.Time;
                if (t > 0) return t;
            }
        }
        catch
        {
            // 播放器正在重建/释放: 退回最后一次已知位置
        }
        return _lastKnownMs > 0 ? _lastKnownMs : 0;
    }

    /// <summary>
    /// 刷新"当前章节"相关的 UI: 胶囊文案 + 显隐 + 窄窗形态。
    /// 由 500ms 进度心跳驱动(与进度条/时间标签同一条心跳, 不另开定时器)。
    /// </summary>
    private void UpdateChapterUi()
    {
        if (_closing || BtnChapters == null || ChapterCapsuleText == null) return;

        if (!HasChapters)
        {
            BtnChapters.Visibility = Visibility.Collapsed;
            return;
        }

        var idx = ChapterParser.FindIndex(_chapters, CurrentPlaybackMs());
        var name = idx >= 0 ? _chapters[idx].Content : "章节";

        // 只在变化时写控件: 心跳 500ms 一次, 但文本绝大多数时候没变, 免得多余的布局失效
        if (!string.Equals(ChapterCapsuleText.Text, name, StringComparison.Ordinal))
            ChapterCapsuleText.Text = name;

        var tip = idx >= 0
            ? $"章节 {idx + 1}/{_chapters.Count}: {name}"
            : $"章节 (共 {_chapters.Count} 章)";
        if (!Equals(BtnChapters.ToolTip, tip)) BtnChapters.ToolTip = tip;

        BtnChapters.Visibility = Visibility.Visible;
        SetChapterCompact(_chaptersCompact);
    }

    /// <summary>
    /// 控制栏章节入口的两种形态: 宽窗 = "章节名 ⌄"; 窄窗 = 一个列表图标。
    /// ★ 窄窗只收形态、不收入口 —— 视频区 600px 时仍然要能跳章。
    /// </summary>
    private void SetChapterCompact(bool compact)
    {
        _chaptersCompact = compact;
        if (ChapterCapsuleText == null || ChapterGlyph == null || ChapterCaret == null) return;

        ChapterCapsuleText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ChapterCaret.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ChapterGlyph.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>气泡里要显示的章节名(没有章节/落在第一章之前时返回空串)。</summary>
    private string ChapterAt(long timeMs)
    {
        if (!HasChapters) return "";
        var idx = ChapterParser.FindIndex(_chapters, timeMs);
        return idx >= 0 ? _chapters[idx].Content : "";
    }

    // ------------------------------------------------------------ 进度条刻度

    /// <summary>
    /// 把章节起点画成进度条上的刻度线。
    ///
    /// 画法与 <see cref="RenderSponsorMarks"/> 完全同源(同一套 TrackMetrics 换算), 区别在于:
    ///   · 刻度是 1.5×9 的**竖线**, 比 3px 轨道上下各探出一点 —— 它要同时压住"未播 3px /
    ///     已播 3px / 悬停 5px"三态还看得见; sponsor 的 5px 圆角色块压在轨道中心,
    ///     刻度画在它**上面**(XAML 里的声明顺序), 白线穿过彩块仍然清楚;
    ///   · 不画第 1 章的起点(0 处画一条线没有信息量);
    ///   · 相邻刻度距离 < 3px 就不画 —— 几十个章节堆在一起会糊成一片, 反而看不出分段。
    /// </summary>
    private void RenderChapterMarks()
    {
        if (ChapterMarks == null) return;
        ChapterMarks.Children.Clear();
        if (!HasChapters) return;

        var total = _totalMs > 0 ? _totalMs : (_mp?.Length ?? 0);
        if (total <= 0) return;

        var (offset, usable) = TrackMetrics();
        if (usable <= 1) return;

        const double markWidth = 1.5;
        const double markHeight = 9;
        var canvasHeight = ChapterMarks.ActualHeight;
        if (canvasHeight <= 0) canvasHeight = 14;
        var top = (canvasHeight - markHeight) / 2;

        var lastX = double.NegativeInfinity;
        foreach (var c in _chapters)
        {
            if (c.FromMs <= 0) continue;                  // 第 1 章的起点(0)不画
            var ratio = Math.Clamp(c.FromMs / (double)total, 0, 1);
            var x = offset + usable * ratio;
            if (x - lastX < 3) continue;                  // 挤在一起就不画, 免得糊成一片
            lastX = x;

            var rect = new Rectangle
            {
                Width = markWidth,
                Height = markHeight,
                RadiusX = markWidth / 2,
                RadiusY = markWidth / 2,
                Fill = ChapterTickBrush
            };
            Canvas.SetLeft(rect, x - markWidth / 2);
            Canvas.SetTop(rect, top);
            ChapterMarks.Children.Add(rect);
        }
    }

    // ------------------------------------------------------------ 入口与弹层

    /// <summary>点控制栏的章节胶囊: 开/收弹层(与字幕/倍速弹层同一套开合判定)。</summary>
    private void OnChaptersClick(object sender, RoutedEventArgs e)
    {
        if (_closing || !HasChapters || ChapterPopup == null) return;
        TogglePlayerPopup(ChapterPopup, onOpen: FillChapterMenu);
    }

    /// <summary>
    /// 填充章节弹层: 每项 = 缩略图 + 章名 + 起始时间, 选中当前章。
    /// ★ 用 ItemsSource + ItemTemplate(而非往列表里塞 ListBoxItem): 条目里有缩略图与多列排版,
    ///   容器自己当内容的话模板不参与, 只能手搓一整棵可视树。
    /// </summary>
    private void FillChapterMenu()
    {
        if (ChapterList == null) return;

        _fillingChapterMenu = true;
        try
        {
            var rows = new List<ChapterRow>(_chapters.Count);
            for (var i = 0; i < _chapters.Count; i++) rows.Add(new ChapterRow(_chapters[i], i));
            ChapterList.ItemsSource = rows;
            ChapterList.SelectedIndex = ChapterParser.FindIndex(_chapters, CurrentPlaybackMs());

            if (ChapterMetaText != null) ChapterMetaText.Text = $"共 {_chapters.Count} 章";
        }
        finally
        {
            _fillingChapterMenu = false;
        }
    }

    /// <summary>选中某一章 → 关弹层并跳过去。</summary>
    private void OnChapterSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingChapterMenu || _closing) return;
        if (ChapterList?.SelectedItem is not ChapterRow row) return;
        CloseChapterPopup();
        JumpToChapter(row.Index);
    }

    /// <summary>
    /// 跳到第 index 章的开头。
    /// ★ 走统一入口 <see cref="SeekTo"/>: 播完态重建 / 看门狗重发 / seek 静音全在那里面,
    ///   自己写 `_mp.Time = x` 会漏掉其中几样(与跳过赞助片段同一个理由)。
    /// </summary>
    private void JumpToChapter(int index)
    {
        if (_closing || _mp == null) return;
        if (index < 0 || index >= _chapters.Count) return;

        var target = _chapters[index].FromMs;
        // 已经在这一章开头附近: 不折腾(避免点"当前章"触发一次无意义的 seek)
        if (Math.Abs(CurrentPlaybackMs() - target) < 1000) return;
        SeekTo(target);
    }

    /// <summary>收起章节弹层(没开就不动, 免得白改一次 IsOpen)。</summary>
    private void CloseChapterPopup()
    {
        if (ChapterPopup == null || !ChapterPopup.IsOpen) return;
        ChapterPopup.IsOpen = false;
    }
}
