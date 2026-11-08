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
    private void OnProgressDragStart(object sender, DragStartedEventArgs e)
    {
        // ★ 记下起点值: WPF 的 IsMoveToPointEnabled 在**点击轨道**时会先改 Value
        //   (ValueChanged 已经把 seek 发出去了), 再把这次点击当成一次 Thumb 拖动。
        //   松手时若值没变, 就只是"点了一下", 不能再发第二条 seek。
        _progressDragStartValue = ProgressSlider.Value;
        _isSeeking = true;
        // ★ 气泡**先不亮**: WPF 把"点一下轨道"也当成一次拖动(DragStarted 照样来),
        //   此时亮气泡会闪一下又消失 —— 用户看到的是"莫名其妙弹了个 12:34"。
        //   真正的"拖动"由 DragDelta 判定(见 OnProgressDragDelta): 只有鼠标真的移动了才算。
        //   ★ 悬停态例外: 气泡在拖动**之前**就已经亮着(悬停亮起的), DragStart 不动它,
        //     按下去的瞬间气泡从"悬停位置"无缝切成"当前值", 不闪不跳。
        _progressDragMoved = false;
    }

    /// <summary>
    /// 拖动过程中的鼠标移动 —— 判定"用户真的在拖"而不是"点了一下轨道"。
    ///
    /// ★ 为什么需要它: WPF 的 <c>IsMoveToPointEnabled</c> 让"点击轨道"也走完整的拖动流程
    ///   (ValueChanged → DragStarted → DragCompleted)。如果气泡跟着 DragStarted 就亮,
    ///   点一下轨道会"闪出一个时间气泡再消失" —— 纯噪音。
    ///   而 <c>DragDelta</c> 只在鼠标**真的移动**时才来(轨道点击不产生它),
    ///   所以它是"真拖动"的准确信号。
    /// </summary>
    private void OnProgressDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_closing || !_isSeeking) return;
        _progressDragMoved = true;
        // 这里的 Value 可能比鼠标位置**落后一拍**(WPF 是先发 DragDelta 再回写 Slider.Value),
        // 下一条 ValueChanged 会立刻用新值纠正 —— 只是首帧可能差一点点, 无所谓。
        UpdateProgressDragHint(ProgressSlider.Value);
    }

    private void OnProgressDragEnd(object sender, DragCompletedEventArgs e)
    {
        _isSeeking = false;
        // 气泡立刻收: 松手后画面会跳到新位置, 气泡留着是多余的。
        // (若鼠标还压在轨道上, 下一条 MouseMove 会以"悬停"身份把气泡重新亮起 —— 那是对的。)
        HideProgressDragHint();
        // 点击轨道 / 点在滑块上但没拖动 ⇒ ValueChanged 已经处理过(或本来就不需要跳),
        // 这里不再重复 seek。重复 seek 会打断正在飞的解复用器, 表现为无限缓冲。
        // 阈值取 0.01(0~1000 量程): 1px 的拖动在常见轨道宽度下约等于 1, 不会被误吞;
        // 而轨道点击本身不会产生 DragDelta, 值严格不变。
        var moved = Math.Abs(ProgressSlider.Value - _progressDragStartValue) > 0.01;
        if (moved && _mp.Length > 0)
        {
            var targetMs = (long)Math.Round(ProgressSlider.Value / 1000.0 * _mp.Length);
            // ★ 走统一入口: 由它按片源类型决定"一步到位 / 看门狗重建"。
            SeekTo(targetMs);
        }
        // 拖动结束后立刻补报一次: 用户主动跳转是"我在看这个位置"的强信号,
        // 如果只等 15 秒周期上报, 此时关掉窗口会把位置记在拖动前的老地方。
        SyncProgressNow();
    }

    /// <summary>
    /// 更新"进度条上方的时间气泡"。
    ///
    /// ★ 目的: 拖动时画面还停在原地, 用户只能从轨道上大致猜位置; 给一个精确的目标时间,
    ///   才知道自己正要跳到哪(长视频里尤其重要 —— 1 小时 23 分的视频, 轨道上 1% 就是 50 秒)。
    ///   2026-11-07 扩展: **鼠标悬停**在轨道上也显示 —— 还没按下去就能看到"这个点是几分钟"。
    /// ★ 两路输入共用本方法: 拖动(显示将要跳到的时间)与悬停(显示鼠标指的位置)。
    ///   本质都是"轨道上这个点是什么时间", 没必要两套浮层。
    /// ★ 拖动期间**不发 seek**: 发 seek 会打断正在飞的解复用器(表现: 无限缓冲),
    ///   真正的跳转等松手(见 OnProgressDragEnd)。悬停本来就不改 Value, 天然不涉及。
    /// </summary>
    /// <param name="sliderValue">进度条量程上的值(0~1000): 拖动时 = 当前 Value, 悬停时 = 鼠标位置反推值</param>
    private void UpdateProgressDragHint(double sliderValue)
    {
        if (_closing || ProgressHint == null || ProgressHintLayer == null) return;
        if (_mp == null || _mp.Length <= 0) return;   // 直播/本地文件没有总时长, 没什么可提示

        // 目标时间与真正 seek 的换算**必须一致** —— 否则气泡显示的时间和实际落点对不上,
        // 用户会以为跳错了。公式见 SeekTo / OnProgressDragEnd。
        var targetMs = (long)Math.Round(sliderValue / 1000.0 * _mp.Length);
        targetMs = Math.Clamp(targetMs, 0, _mp.Length);

        ProgressHintText.Text = FormatClock(TimeSpan.FromMilliseconds(targetMs));

        // 量一次气泡的尺寸才能把它摆准(居中于滑块、且不超出轨道两端)。
        ProgressHint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = ProgressHint.DesiredSize.Width;
        var h = ProgressHint.DesiredSize.Height;

        // ★ 水平位置**用滑块的真实行程**而不是直接按控件宽度反推: Thumb 在模板里是个 11px 的圆点,
        //   Value 0 对应的中心在 x=半宽 处(见 TrackMetrics)。按控件宽度反推会让气泡与滑块差半个圆点。
        var (offset, usable) = TrackMetrics();
        var ratio = Math.Clamp(sliderValue / 1000.0, 0, 1);
        var centerX = offset + usable * ratio;

        // 夹在轨道范围内: 拖到最左/最右时气泡不能飘出视频区。
        var layerWidth = ProgressHintLayer.ActualWidth;
        var x = centerX - w / 2;
        if (layerWidth > 0) x = Math.Clamp(x, 0, Math.Max(0, layerWidth - w));
        x = Math.Max(0, x);

        // 垂直方向: 浮在轨道**上方**。Canvas.Top 取负值即可浮出这个 Canvas(它只有轨道那么高)。
        var y = -h - 6;
        if (ProgressHintLayer.ActualHeight > 0)
            y = Math.Min(y, ProgressHintLayer.ActualHeight - h);

        Canvas.SetLeft(ProgressHint, x);
        Canvas.SetTop(ProgressHint, y);

        // 不做淡入: 拖动时气泡每帧都在动, 动画只会让位置与透明度不同步, 反而晃眼。
        ProgressHint.Visibility = Visibility.Visible;
    }

    /// <summary>隐藏"进度条时间气泡"(松手 / 鼠标离开 / 换片 / 关闭时调用)。</summary>
    private void HideProgressDragHint()
    {
        if (_closing || ProgressHint == null) return;
        // ★ 拖动中不能收: 悬停(MouseLeave)与拖动(DragEnd)之外, 还有"拖动期间鼠标
        //   恰好滑出了轨道边界"这条交叉路径 —— 拖动未结束气泡必须留着。
        if (_isSeeking) return;
        ProgressHint.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------ 进度条悬停提示(2026-11-07)

    /// <summary>
    /// 鼠标悬停进度条: 气泡浮出, 显示鼠标指的位置对应的时间。
    ///
    /// ★ 事件挂在 Slider 上而不是轨道 Grid 上: Slider 的命中区比那条 3px 的白轨道大得多
    ///   (模板里 MinHeight=18 的透明 Grid), 挂 Slider 才能覆盖"压在轨道附近"的全部区域。
    /// ★ 拖动中此路**不更新气泡**(拖动有自己的更新路径, 且拖动时鼠标位置 ≠ 目标位置 ——
    ///   WPF 拖滑块时鼠标可以在轨道外任意远处), 只把悬停标志置上。
    /// ★ 与拖动的交接: DragCompleted 时鼠标几乎必然还压在轨道上, 那时会落进来一次
    ///   MouseMove —— 气泡从"拖动的时间"无缝切成"悬停位置的时间", 体验是对的。
    /// </summary>
    private void OnProgressSliderMouseMove(object sender, MouseEventArgs e)
    {
        if (_closing || ProgressHint == null) return;
        if (_isSeeking) return;   // 拖动优先: 气泡由拖动路径更新, 不被鼠标位置覆盖

        // 鼠标在 Slider 里的横坐标 → 量程值。方向键等程序化改动不走这里。
        var p = e.GetPosition(ProgressSlider);
        var (offset, usable) = TrackMetrics();
        if (usable <= 0) return;
        var ratio = Math.Clamp((p.X - offset) / usable, 0, 1);
        UpdateProgressDragHint(ratio * 1000);
    }

    /// <summary>鼠标离开进度条: 收气泡(拖动中除外 —— 那时气泡归拖动管)。</summary>
    private void OnProgressSliderMouseLeave(object sender, MouseEventArgs e)
    {
        // ★ 拖动中不收: 见 HideProgressDragHint 里的说明(拖动期间气泡只归拖动路径管)。
        if (!_isSeeking) HideProgressDragHint();
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mp == null) return;
        var v = (int)VolumeSlider.Value;
        // 滑块是用户拖动时的入口, 程序化赋值(SetVolume)也会走到这 —— 同值回写, 无副作用。
        // 必须同步权威值: 否则拖完滑块再滚轮, 基数还是旧缓存(见 _volumeCache 的说明)。
        _volumeCache = v;
        // ★ 音量继承: 拖滑块这条路也要落盘(2026-11-12)。
        //   SetVolume 里已经存过一次, 这里是**直接拖滑块**的路径 —— 那条路不经过 SetVolume。
        //   两个入口都存, 才不会出现"滚轮调的继承了、拖滑块调的没继承"。
        Svc.Settings.SetPlayerVolume(v);
        // seek 静音期间(与 SetVolume 同一套约定): 新值记为恢复目标, 声音保持静音直到画面就绪。
        // 这里不能直接写 _mp.Volume —— 否则拖一下滑块就绕过静音, 声音又跑到画面前面去了。
        if (_seekMuteSavedVolume >= 0)
        {
            _seekMuteSavedVolume = v;
            try { _mp.Volume = 0; } catch { }
            return;
        }
        try { _mp.Volume = v; } catch { }
    }

    // ------------------------------------------------------------ 音量(滚轮 / 快捷键)

    /// <summary>一次音量调节的步进。与方向键上下保持一致, 滚轮手感不至于一格跳太多。</summary>
    private const int VolumeStep = 5;

    /// <summary>音量提示浮层的停留时长</summary>
    private static readonly TimeSpan VolumeOsdHold = TimeSpan.FromMilliseconds(1100);

    private DispatcherTimer? _volumeOsdTimer;

    /// <summary>倍速提示的淡出计时器(松手后"已恢复 1.0x"亮一下就靠它收场)</summary>
    private DispatcherTimer? _speedOsdTimer;

    /// <summary>
    /// 滚轮调音量: 上滑 +5, 下滑 -5。
    ///
    /// 为什么用 PreviewMouseWheel 而不是 MouseWheel:
    /// 滚轮是**隧道**事件, Preview 那条从根往叶走, 挂了就能在底部控制栏的 Slider、
    /// 清晰度 Popup 的 ListBox 之前拿到事件; 否则鼠标压在控制栏上滚轮就没反应。
    /// 只挂视频区(和覆盖它的那层浮动窗口, 见 HookOverlayKeyboard) ——
    /// 鼠标在右侧信息面板/评论区时, 滚轮该滚那个列表。
    ///
    /// ⚠ **必须同时挂到 VideoView 的 ForegroundWindow**: 视频画面是被那层独立的顶层窗口
    /// (airspace)盖住的, 鼠标压在画面上时滚轮事件根本进不了主窗口的 WPF 树 ——
    /// 用户报的"滚轮调音量不灵敏"就是这个: 只有压在控制栏那一小条上才有效。
    /// 与键盘失灵、鼠标侧键失灵是同一个坑。
    ///
    /// 步进用**累计**而不是"每个事件至少一步": 标准鼠标一格 ±120, 而触控板/精密滚轮会给
    /// 一串 ±1~±40 的小值 —— 后者按"每事件至少一步"算, 轻轻一划就跳十几个百分点,
    /// 反过来显得"太灵/乱跳"。累计到一整格才走一步, 两种设备的手感就都对了。
    /// </summary>
    private void OnVideoAreaMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_mp == null || _closing || e.Delta == 0) return;

        // ★★ 2026-11-03 修"自定义倍速弹层里滚轮会调音量":
        //   弹层是独立 HWND, 但它的滚轮事件仍会冒到视频区这层 PreviewMouseWheel 上
        //   (与"点弹层空白处会漏一个 Up"是同一套 airspace 特性)。
        //   弹层开着时滚轮属于弹层自己 —— 倍速列表要滚动、自定义输入要正常操作,
        //   这里必须直接放行(不设 Handled, 让 ListBox 的 ScrollViewer 接住),
        //   同时清掉累计量, 免得回到视频区时突然跳一格音量。
        if (AnyPlayerPopupOpen)
        {
            _wheelAccum = 0;
            return;
        }

        // 反向滚动时先清掉上次攒的余量, 否则要先"抵消"掉才动
        if (Math.Sign(e.Delta) != Math.Sign(_wheelAccum)) _wheelAccum = 0;
        _wheelAccum += e.Delta;

        var notches = _wheelAccum / 120;
        if (notches != 0)
        {
            _wheelAccum -= notches * 120;
            // 基数用 _volumeCache 而不是 _mp.Volume: 见 _volumeCache 的说明(aout 未就绪时原生读回是 -1/旧值)
            SetVolume(_volumeCache + notches * VolumeStep);
        }
        // 不够一格也要吃掉: 否则事件继续冒泡给别的可滚动控件(视频区里其实没有, 但别留隐患)
        e.Handled = true;
    }

    /// <summary>滚轮累计的余量(±120 为一格)。见 OnVideoAreaMouseWheel 的说明</summary>
    private int _wheelAccum;

    /// <summary>
    /// 应用侧的音量权威值(0~100)。
    ///
    /// 为什么不直接读 _mp.Volume(反编译 LibVLCSharp 3.8.0 确认):
    /// getter 是纯原生调用 libvlc_audio_get_volume, **没有 aout(音频输出)时返回 -1**;
    /// setter 在无 aout 时也是静默丢弃。aout 的生命周期跟媒体流走 ——
    /// 长视频走远端 DASH 双流, 起播/缓冲/拖动/换清晰度期间 aout 可能尚未就绪或刚被重建,
    /// 这时拿 _mp.Volume 当滚轮基数, 基数就是 -1/旧值, 步进全乱(用户报的"长视频滚轮调音量不灵敏")。
    /// 短视频是本地合流文件, aout 几乎秒建, 所以从来感觉不到 —— 时长相关性由此而来。
    /// 滚轮/键盘/滑块一律以本字段为基数, 原生侧只在 SetVolume 里单向写、在 Playing(aout 就绪)时补写。
    /// </summary>
    private int _volumeCache = 80;

    /// <summary>
    /// 设置音量: 同步 LibVLC 与滑块, 并弹出音量提示。
    /// 统一入口是必要的 —— 之前 ↑↓/静音键各自改一遍 _mp.Volume 和 VolumeSlider.Value,
    /// 再加一条滚轮路径就会变成三份容易走偏的代码。
    /// </summary>
    private void SetVolume(int volume, bool showHint = true)
    {
        if (_closing || _mp == null) return;
        var v = Math.Max(0, Math.Min(100, volume));
        // 权威值先落账: 之后所有路径(滚轮/键盘)都以它为基数, 不依赖原生读回(见 _volumeCache 的说明)
        _volumeCache = v;
        // ★ 音量继承: 用户每调一次就落盘, 下一个视频/下一次打开都用它。
        //   放在这里而不是各处调用点 —— SetVolume 是滚轮/键盘/滑块的唯一入口。
        //   SetPlayerVolume 内部有"值没变就不写"的短路, 连滚到同一值不会反复写盘。
        Svc.Settings.SetPlayerVolume(v);
        // seek 静音期间用户调音量: 把新值记为"恢复目标", 声音仍保持静音直到画面就绪 ——
        // 若直接写 _mp.Volume 会立刻出声(又变成画面没到声音先到), 而且恢复时还会覆盖用户的值。
        if (_seekMuteSavedVolume >= 0)
        {
            _seekMuteSavedVolume = v;
            try { _mp.Volume = 0; } catch { }
            if (VolumeSlider != null) VolumeSlider.Value = v;
            if (showHint) ShowVolumeHint(v);
            return;
        }
        try { _mp.Volume = v; } catch { return; }
        if (VolumeSlider != null) VolumeSlider.Value = v;
        if (showHint) ShowVolumeHint(v);
    }

    /// <summary>
    /// 浮出音量提示。调音量时画面之外没有任何反馈(音量条在底部偶尔还是自动隐藏的),
    /// 所以给一个"音量 65%"的短提示; 连续调节时只重置计时器, 不重播淡入动画,
    /// 否则每滚一格都闪一下。
    /// </summary>
    /// <summary>
    /// 浮出倍速提示(长按快进开始 / 恢复 1x 时调用)。
    ///
    /// ★ 与音量提示的差别(所以没有复用 ShowVolumeHint):
    ///   音量是"调一下、闪一下、自己淡出"; 倍速提示必须在**整个长按期间一直挂着** ——
    ///   用户按住的那几秒里要始终知道"现在是 2 倍速, 松手会回去"。
    ///   松手时再调一次本方法(<c>hold=false</c>), 文案换成"已恢复 1.0x"并淡出。
    /// </summary>
    /// <param name="text">提示文案, 如 "2.0x 快进中" / "已恢复 1.0x"</param>
    /// <param name="hold">true = 常显不自动淡出(长按期间); false = 显示一下自己淡出</param>
    private void ShowSpeedHint(string text, bool hold)
    {
        if (_closing || SpeedOsd == null) return;

        SpeedOsdText.Text = text;
        _speedOsdTimer?.Stop();
        _speedOsdTimer = null;

        // 每次都从"当前透明度"补到 1(同 ShowVolumeHint 的理由:
        // 上一次的淡出可能还没跑完, 从当前值补最稳, 不会出现"提示淡到一半卡住")
        SpeedOsd.Visibility = Visibility.Visible;
        SpeedOsd.BeginAnimation(OpacityProperty, null);
        // ★★★ 淡入动画**绝不能写 FillBehavior=Stop**(2026-10-14 修"缓存中长按 2x,
        //   缓存结束后倍速提示消失"): XAML 里 SpeedOsd 的基准值是 `Opacity="0"`,
        //   而 Stop 语义是"播完动画把属性还原成**基准值**" —— 于是 120ms 淡入一结束,
        //   Opacity 立刻掉回 0, 提示虽然 Visibility=Visible 却整块透明。
        //   探针 bd-probe-speedosd 实测: 第一次显示 +180ms 后 Opacity=0.00。
        //   ★ 这解释了为什么"第二次显示是好的": 上一次淡出的 Completed 里写了
        //     `Opacity = 1`, 把基准值改成了 1, 之后所有淡入的 Stop 都还原到 1。
        //     ⇒ 只有**每个进程的第一次显示**中招, 与"缓存期间长按"完全对得上
        //       (缓存期间恰好是打开视频后的第一次长按)。
        //   ★ 与 ShowVolumeHint 保持一致: 它的淡入也没写 FillBehavior(默认 Hold)。
        SpeedOsd.BeginAnimation(OpacityProperty,
            new DoubleAnimation(Math.Min(1.0, SpeedOsd.Opacity), 1, TimeSpan.FromMilliseconds(120)));

        if (hold) return;   // 长按期间: 挂着等松手

        // 松手了: 亮一下"已恢复"再淡出
        _speedOsdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _speedOsdTimer.Tick += (_, _) =>
        {
            _speedOsdTimer?.Stop();
            _speedOsdTimer = null;
            if (_closing) return;
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(240))
            { FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop };
            fade.Completed += (_, _) =>
            {
                // 淡出期间用户可能又按住了(ShowSpeedHint 已重开): 那时不能藏
                if (_closing || _speedOsdTimer != null) return;
                // ★ 摘动画 + 归位(2026-10-13 清理动画 bug 时的同款问题):
                //   DoubleAnimation 默认 Hold 会把 Opacity 钉在 0, 下次常显就整块透明。
                SpeedOsd.BeginAnimation(OpacityProperty, null);
                SpeedOsd.Opacity = 1;
                SpeedOsd.Visibility = Visibility.Collapsed;
            };
            SpeedOsd.BeginAnimation(OpacityProperty, fade);
        };
        _speedOsdTimer.Start();
    }

    private void ShowVolumeHint(int volume)
    {
        if (_closing || VolumeOsd == null) return;

        VolumeOsdText.Text = volume + "%";
        // 0 用静音图标, 其余用喇叭: 一眼就能看出"是没声还是声音小"
        VolumeOsdIcon.Text = volume == 0 ? "\uE74F" : "\uE767";

        // 每次都从"当前透明度"补到 1。
        // 不要在"已经 Visible 就跳过"上做判断 —— 淡出动画还没跑完时(计时器已被置空)
        // 用户再滚一下, 浮层仍是 Visible 于是不重播, 结果它继续淡到 0 卡住,
        // 用户反而看不到任何音量反馈。从当前值补到 1 在这两种情况下都正确:
        // 已经在 1 上就是原地不动(连滚不闪), 淡出中途则接着淡回来。
        VolumeOsd.Visibility = Visibility.Visible;
        VolumeOsd.BeginAnimation(OpacityProperty,
            new DoubleAnimation(Math.Min(1.0, VolumeOsd.Opacity), 1, TimeSpan.FromMilliseconds(120)));

        _volumeOsdTimer?.Stop();
        _volumeOsdTimer = new DispatcherTimer { Interval = VolumeOsdHold };
        _volumeOsdTimer.Tick += (_, _) =>
        {
            _volumeOsdTimer?.Stop();
            _volumeOsdTimer = null;
            if (_closing) return;
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(240))
            { FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop };
            fade.Completed += (_, _) =>
            {
                // 淡出期间用户又调了一次 -> ShowVolumeHint 已经重开计时器并重播淡入,
                // 这时绝对不能把浮层藏掉(表现就是"滚轮调音量提示闪一下就不见了")
                if (_closing || _volumeOsdTimer != null) return;
                // ★ 摘动画 + 归位(2026-10-13 修): 原来这里是裸 Collapsed,
                //   而 DoubleAnimation 默认 FillBehavior=Hold ⇒ Opacity 被钉死在 0,
                //   下次滚轮时浮层虽 Visible 却整块透明 —— 提示像"消失"了一样。
                VolumeOsd.BeginAnimation(OpacityProperty, null);
                VolumeOsd.Opacity = 1;
                VolumeOsd.Visibility = Visibility.Collapsed;
            };
            VolumeOsd.BeginAnimation(OpacityProperty, fade);
        };
        _volumeOsdTimer.Start();
    }

    /// <summary>隐藏音量提示并停掉它的计时器(关闭流程用)</summary>
    private void StopVolumeOsd()
    {
        _volumeOsdTimer?.Stop();
        _volumeOsdTimer = null;
    }

    /// <summary>隐藏倍速提示并停掉它的计时器(关闭流程用)</summary>
    private void StopSpeedOsd()
    {
        _speedOsdTimer?.Stop();
        _speedOsdTimer = null;
    }

    // ------------------------------------------------------------ 倍速(预设 + 自定义, 2026-11-03)

    /// <summary>允许的最低/最高倍速。范围写在这里一处, 输入夹取与预设列表都引用它。</summary>
    private const float MinPlaybackRate = 0.25f;
    private const float MaxPlaybackRate = 4.0f;

    /// <summary>常用预设档位(顺序即弹层里的顺序)。</summary>
    private static readonly float[] SpeedPresets =
    {
        0.25f, 0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 1.75f, 2.0f, 2.5f, 3.0f, 4.0f
    };

    /// <summary>填充倍速列表时抑制 SelectionChanged, 避免"打开弹层就自动改一次倍速"。</summary>
    private bool _fillingSpeedMenu;

    private void OnSpeedClick(object sender, RoutedEventArgs e)
    {
        if (_mp == null) return;
        FillSpeedMenu();
        SpeedPopup.IsOpen = true;
    }

    /// <summary>
    /// 填充预设倍速列表并高亮当前倍速, 同时把当前值预填进自定义输入框。
    /// </summary>
    private void FillSpeedMenu()
    {
        if (SpeedList == null) return;
        _fillingSpeedMenu = true;
        try
        {
            SpeedList.Items.Clear();
            foreach (var rate in SpeedPresets)
                SpeedList.Items.Add(new ListBoxItem { Tag = rate, Content = FormatRate(rate) });

            var cur = _mp?.Rate ?? 1.0f;
            if (cur <= 0) cur = 1.0f;
            if (SpeedCustomText != null) SpeedCustomText.Text = FormatRateValue(cur);
            if (SpeedCustomHint != null) SpeedCustomHint.Text = "";

            // 只有与某个预设一致才高亮; 自定义值(如 1.15x)保持无选中
            var idx = Array.FindIndex(SpeedPresets, r => Math.Abs(r - cur) < 0.005f);
            SpeedList.SelectedIndex = idx;
        }
        finally
        {
            _fillingSpeedMenu = false;
        }
    }

    private void OnSpeedSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingSpeedMenu || SpeedPopup == null) return;
        if (SpeedList.SelectedItem is not ListBoxItem li || li.Tag is not float rate) return;
        ApplyPlaybackRate(rate, closePopup: true);
    }

    private void OnSpeedCustomApply(object sender, RoutedEventArgs e) => ApplyCustomSpeed();

    /// <summary>自定义输入框: Enter 应用, Esc 关闭弹层(不改变当前倍速)。</summary>
    private void OnSpeedCustomKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyCustomSpeed();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SpeedPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void ApplyCustomSpeed()
    {
        if (_mp == null) return;
        if (!TryParseSpeed(SpeedCustomText?.Text, out var rate))
        {
            ShowSpeedCustomHint($"请输入 {FormatRate(MinPlaybackRate)} – {FormatRate(MaxPlaybackRate)} 之间的数字, 例如 1.25");
            return;
        }
        // 超出范围自动夹到边界(而不是拒绝): 用户输入 10x 时给他 4x 比什么都不做更符合预期
        ApplyPlaybackRate(Math.Clamp(rate, MinPlaybackRate, MaxPlaybackRate), closePopup: true);
    }

    /// <summary>
    /// 唯一的倍速应用入口(预设 / 自定义共用)。
    /// ★ 顺序: 先让弹幕时钟按新倍速落定当前值, 再真正 SetRate ——
    ///   反过来先 SetRate 的话, SyncDanmakuClockRate 内部那句 DanmakuClock() 会
    ///   用**旧速率**外推(而媒体已经按新速率跑了), 那一下就是用户看到的"顿一下"。
    /// </summary>
    private void ApplyPlaybackRate(float rate, bool closePopup)
    {
        if (_mp == null) return;
        // 保留两位小数: 避免自定义输入留下 1.1499999 这种值, 显示与存储都不好看
        rate = Math.Clamp(MathF.Round(rate, 2), MinPlaybackRate, MaxPlaybackRate);

        SyncDanmakuClockRate(rate);
        try { _mp.SetRate(rate); }
        catch
        {
            ShowSpeedCustomHint("设置倍速失败, 播放器可能正在切换片源");
            return;
        }

        UpdateSpeedText(rate);
        if (SpeedCustomHint != null) SpeedCustomHint.Text = "";
        if (closePopup && SpeedPopup != null) SpeedPopup.IsOpen = false;
    }

    /// <summary>把控制栏上的倍速文字同步成实际速率(不传 = 读播放器当前值)。</summary>
    private void UpdateSpeedText(float? rate = null)
    {
        var r = rate ?? _mp?.Rate ?? 1.0f;
        if (r <= 0) r = 1.0f;
        if (SpeedText != null) SpeedText.Text = FormatRate(r);
    }

    /// <summary>带 x 后缀的显示文字, 如 1.0x / 1.25x / 4.0x。</summary>
    private static string FormatRate(float rate)
        => FormatRateValue(rate) + "x";

    /// <summary>纯数字显示(用于自定义输入框预填), 如 1.0 / 1.25。</summary>
    private static string FormatRateValue(float rate)
        => rate.ToString("0.0##", CultureInfo.InvariantCulture);

    /// <summary>解析用户输入的倍速: 允许 "1.25"、"1.25x"、逗号小数点。</summary>
    private static bool TryParseSpeed(string? text, out float rate)
    {
        rate = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim();
        if (s.EndsWith("x", StringComparison.OrdinalIgnoreCase)) s = s[..^1].Trim();
        s = s.Replace(',', '.');
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out rate)
               && float.IsFinite(rate);
    }

    private void ShowSpeedCustomHint(string message)
    {
        if (SpeedCustomHint != null) SpeedCustomHint.Text = message;
    }

    /// <summary>把弹层的右缘与目标按钮的右缘对齐(放在按钮正上方)。</summary>
    private void RightAlignPopup(System.Windows.Controls.Primitives.Popup popup,
        System.Windows.FrameworkElement target)
    {
        var w = popup.ActualWidth;
        if (w <= 0 && popup.Child is FrameworkElement fe) w = fe.ActualWidth;
        if (w <= 0) w = target.ActualWidth;   // 最后的兜底: 至少不会把弹层推出窗外
        popup.HorizontalOffset = target.ActualWidth - w;
    }

    // ------------------------------------------------------------ 清晰度

    /// <summary>填充清晰度菜单并高亮当前项(以 B 站实际返回的清晰度为准)</summary>
    private void FillQualityMenu(int actualQn)
    {
        if (QualityList == null) return;
        _fillingQualityMenu = true;
        try
        {
            QualityList.Items.Clear();
            foreach (var (qn, label) in _qualities)
                QualityList.Items.Add(new ListBoxItem { Tag = qn, Content = label });
            if (actualQn > 0) _currentQn = actualQn;
            // ★★★ 按钮上的文字必须按"**接口实际给的**档位"或"用户的偏好"来取,
            //   **绝不能**对 _currentQn 直接调 QnToLabel(2026-10-04 修):
            //   「默认画质 = 自动」时 _currentQn 是发给接口的请求上界(现在是 120/4K,
            //   见 QualityPreference.HighestQn), 而 QnToLabel 只认**真实流**里的档位名。
            //   于是本地文件/起播失败(actualQn == 0)
            //   这些"没有实际档位可显示"的路径上, 按钮会写成一个用户根本没有的档位。
            //   这里只显示**确实在播的那一档**: 有实际档位就说它, 没有(本地文件)就说用户的偏好,
            //   两者都不会把"请求用的内部值"当成画质名报给用户。
            // ★ 优先用 _qualities 里的标签: 直播的档位名来自 g_qn_desc(原画/蓝光/超清…),
            //   与点播的 QnToLabel 不是同一套 qn; 查不到才回落到点播的映射。
            string? actualLabel = null;
            foreach (var (q, l) in _qualities)
            {
                if (q == actualQn && l.Length > 0) { actualLabel = l; break; }
            }
            var label2 = actualQn > 0
                ? (actualLabel ?? ApiClient.QnToLabel(actualQn))
                : QualityPreference.LabelOf(Svc.Settings.PreferredQualityQn);
            if (QualityText != null) QualityText.Text = label2;
            // 高亮与实际清晰度匹配的项
            for (var i = 0; i < QualityList.Items.Count; i++)
            {
                if (QualityList.Items[i] is ListBoxItem li && li.Tag is int q && q == actualQn)
                {
                    QualityList.SelectedIndex = i;
                    break;
                }
            }
        }
        finally
        {
            _fillingQualityMenu = false;
        }
    }

    /// <summary>
    /// 清晰度 / 弹幕区域 / 倍速弹层是否**有任意一个开着**。
    ///
    /// ★ 为什么需要它(2026-10-13 用户报"点清晰度 → 点空白处收起 → 视频变 2 倍速"):
    ///   这几个 Popup 是 <c>StaysOpen="False"</c>, 而 Popup 是**独立 HWND**。
    ///   实测(探针 bd-probe-coin, 真实 Win32 鼠标点击, 时间戳):
    ///     <code>
    ///     12:14:38.955  VIDEO UP                ← 只有 Up, 没有 Down!
    ///     12:14:38.975  *** POPUP CLOSED ***    ← Closed 晚 20ms
    ///     </code>
    ///   ⇒ 三个"当前状态"信号在 Up 到达时**全部不可用**:
    ///     · <c>Popup.IsOpen</c> 已被 WPF 自己的收起逻辑置成 false;
    ///     · <c>Mouse.LeftButton</c> 已回报 Released;
    ///     · <c>Popup.Closed</c> 还没来。
    ///   ⇒ 第一版写在 Closed 里的时间窗是**死代码**(等它记上, Up 早跑完了),
    ///     第二版的 IsOpen 判据同样挡不住这一次 Up。
    ///
    ///   ★ 真正可靠的判据是**手势配对**: 关闭弹层那一次点击, 它的 Down 被弹层 HWND
    ///     吃掉, 只有 Up 漏进视频区 —— 也就是"没有配对 Down 的 Up"。
    ///     这种 Up 一律丢弃即可, 且**不依赖任何事件顺序**。
    /// </summary>
    private bool AnyPlayerPopupOpen
        => (QualityPopup != null && QualityPopup.IsOpen)
        || (AudioPopup != null && AudioPopup.IsOpen)
        || (DanmakuAreaPopup != null && DanmakuAreaPopup.IsOpen)
        || (SpeedPopup != null && SpeedPopup.IsOpen);

    /// <summary>
    /// 视频区手势是否已经由一次 **Down** 正式起步。
    ///
    /// ★ 唯一可靠的弹层余波判据(见 <see cref="AnyPlayerPopupOpen"/> 里的实测记录):
    ///   弹层开着时按下的那一下不算视频区手势(它属于浮层), 所以这里必须为 false;
    ///   否则随后漏进来的 Up 会被当成一次完整的"单击", 于是**收起清晰度列表会把视频暂停**,
    ///   更糟的是漏进来的 Down 会起长按计时器 → 500ms 后 SetRate(2.0) 无故快进。
    /// </summary>
    private bool _videoGestureActive;

    /// <summary>
    /// 视频区**右键**手势是否已由一次 Down 正式起步(右键那条线的 <see cref="_videoGestureActive"/>)。
    ///
    /// ★ 为什么右键要单独一份而不是复用上面那个: 两者是**并行的两条手势**,
    ///   用户完全可能左键按着拖、中途点右键; 共用一个标记会互相把对方的"配对"抹掉。
    /// </summary>
    private bool _rightGestureActive;

    private void OnQualityClick(object sender, RoutedEventArgs e)
    {
        QualityPopup.IsOpen = true;
    }

    private void OnQualitySelected(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingQualityMenu || QualityPopup == null) return;
        if (QualityList.SelectedItem is not ListBoxItem li || li.Tag is not int qn) return;
        QualityPopup.IsOpen = false;
        if (qn == _currentQn || _switchingQuality) return;
        // ★ 直播与点播是两条完全不同的切流路径: 直播没有 cid/进度, 也不能走 DASH 重建。
        if (_isLive)
        {
            _ = SwitchLiveQualityAsync(qn);
            return;
        }
        _ = SwitchQualityAsync(qn);
    }

    // ------------------------------------------------------------ 音质(2026-11-06)

    /// <summary>
    /// 填充音质弹层: 用户偏好档位在前, 后面是本次视频**实际可选**的音轨。
    ///
    /// ★ 为什么"可选项"要和"实际音轨"取交集: B 站把 flac/dolby 节点放在响应里,
    ///   但普通视频没有这两个节点 —— 直接把全部档位列出来, 用户点"无损"会发现没反应,
    ///   像按钮坏了。这里只列真实存在的档, 并在提示行里说明"这条视频没有无损音轨"。
    /// </summary>
    private void FillAudioMenu(int actualAudioId)
    {
        if (AudioList == null) return;
        _fillingAudioMenu = true;
        try
        {
            AudioList.Items.Clear();
            // 第一项永远是「自动」: 它是策略而不是某个具体码率, 任何视频都可用。
            AudioList.Items.Add(new ListBoxItem
            {
                Tag = (int)AudioTier.Auto,
                Content = AudioPreference.LabelOf(AudioTier.Auto)
            });
            foreach (var id in _audioIds)
            {
                var tier = AudioPreference.TierOfId(id);
                if (tier == AudioTier.Auto) continue;
                AudioList.Items.Add(new ListBoxItem
                {
                    Tag = (int)tier,
                    Content = AudioPreference.LabelOf(tier)
                });
            }

            var wanted = AudioPreference.Normalize(Svc.Settings.PreferredAudioTier);
            if (AudioText != null) AudioText.Text = AudioPreference.ShortLabelOf(wanted);

            // 高亮: 优先"用户指定档", 其次"实际在播的档"
            var highlight = (int)wanted;
            if (wanted == AudioTier.Auto && actualAudioId > 0)
            {
                // 自动档时按钮显示"自动", 但列表里把实际选中的那一档也标出来会更清楚 ——
                // 这里仍高亮「自动」, 因为用户的意图就是"让你替我挑"。
                highlight = (int)AudioTier.Auto;
            }
            for (var i = 0; i < AudioList.Items.Count; i++)
            {
                if (AudioList.Items[i] is ListBoxItem li && li.Tag is int t && t == highlight)
                {
                    AudioList.SelectedIndex = i;
                    break;
                }
            }

            if (AudioHintText != null)
            {
                if (wanted == AudioTier.Auto)
                {
                    AudioHintText.Text = actualAudioId > 0
                        ? $"当前音轨: {AudioPreference.LabelOf(AudioPreference.TierOfId(actualAudioId))}"
                        : "这条视频没有独立的音轨信息(单流或直播)";
                }
                else if (actualAudioId > 0 && AudioPreference.TierOfId(actualAudioId) == wanted)
                {
                    AudioHintText.Text = "已使用所选音质";
                }
                else if (_audioIds.Count == 0)
                {
                    AudioHintText.Text = "这条视频没有可选音轨(单流或直播), 音质由视频本身决定";
                }
                else
                {
                    AudioHintText.Text = $"这条视频没有{AudioPreference.LabelOf(wanted)}音轨, " +
                                        $"已使用 {AudioPreference.LabelOf(AudioPreference.TierOfId(actualAudioId))}";
                }
            }
        }
        finally
        {
            _fillingAudioMenu = false;
        }
    }

    private void OnAudioClick(object sender, RoutedEventArgs e)
    {
        FillAudioMenu(_currentAudioId);
        AudioPopup.IsOpen = true;
    }

    private void OnAudioSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingAudioMenu || AudioPopup == null) return;
        if (AudioList.SelectedItem is not ListBoxItem li || li.Tag is not int tierValue) return;
        AudioPopup.IsOpen = false;
        var tier = AudioPreference.Normalize((AudioTier)tierValue);
        if (tier == Svc.Settings.PreferredAudioTier) return;
        Svc.Settings.PreferredAudioTier = tier;
        Svc.Settings.Save();
        if (AudioText != null) AudioText.Text = AudioPreference.ShortLabelOf(tier);
        // 直播/本地文件没有可选音轨: 只记偏好, 不重新起播(重播也没用, 音轨不由我们选)。
        if (_isLive || _isLocalPlayback || _currentCid <= 0)
        {
            Svc.Toast.Show("已记住音质偏好, 下次打开视频生效");
            return;
        }
        _ = SwitchAudioAsync(tier);
    }

    /// <summary>
    /// 切换音质: 重新取一次 playurl(带上新的音轨偏好), 起播后恢复到当前进度。
    ///
    /// ★ 必须重取 playurl 而不是"本地换一条 input-slave": LibVLC 3 的 Media 是不可变的,
    ///   音轨地址绑死在 Media 上; 想换只能重建 Media —— 那就是重新起播, 和换清晰度同一套。
    /// ★ 这里不碰 `_qualities`: 音质和画质是两个独立维度, 换音轨不该让画质菜单跟着变。
    /// </summary>
    private async Task SwitchAudioAsync(AudioTier tier)
    {
        if (_closing || _switchingAudio || _switchingQuality || _currentCid <= 0) return;
        _switchingAudio = true;
        try
        {
            var resumeMs = _mp.Time;
            ShowLoading("正在切换音质…");
            CancelRemuxPrefetch();

            var (playOk, playErr, main, backups, _, _, actualQn, audioUrl, remux,
                 _, _, audioIds, actualAudioId) =
                await Svc.Api.GetPlayUrlAsync(_currentBvid, _currentCid, _currentQn, tier);
            if (_closing) return;
            if (!playOk && string.IsNullOrEmpty(main))
            {
                Svc.Toast.Show(playErr ?? "切换音质失败");
                HideLoading();
                return;
            }

            _audioIds.Clear();
            _audioIds.AddRange(audioIds);
            _currentAudioId = actualAudioId;
            // 按钮文案交给 FillAudioMenu 统一决定(它会区分"自动策略"与"实际在播的那一档"),
            // 这里不再覆盖 —— 两处各写一遍必然漂移。
            FillAudioMenu(actualAudioId);

            var local = ShortVideoCache.TryGet(_currentBvid, actualQn);
            if (local == null && ShortVideoCache.WorthCaching(remux))
                local = await PrepareShortVideoAsync(remux!, actualQn);
            if (_closing) return;

            var url = local ?? main;
            if (string.IsNullOrEmpty(url) && backups.Count > 0) url = backups[0];
            if (string.IsNullOrEmpty(url))
            {
                Svc.Toast.Show("切换音质失败: 没有可用播放地址");
                HideLoading();
                return;
            }

            _resumeAfterSwitchMs = resumeMs > 2000 ? resumeMs : 0;
            _resumeByStartTime = false;
            _eofResumeCount = 0;
            _lastEofResumeMs = 0;
            _lastPlayUrl = url;
            _lastAudioUrl = local != null ? null : audioUrl;
            _listenAudioUrl = audioUrl;
            try { _mp.Stop(); } catch { }
            if (_isListenMode && !string.IsNullOrEmpty(_listenAudioUrl))
                PlayMedia(BuildMedia(_listenAudioUrl!, null));
            else
                PlayMedia(BuildMedia(url!, _lastAudioUrl));
            UpdatePlayButton();

            if (actualAudioId > 0 && AudioPreference.TierOfId(actualAudioId) != tier && tier != AudioTier.Auto)
                Svc.Toast.Show($"这条视频没有{AudioPreference.LabelOf(tier)}音轨, 已使用 " +
                               AudioPreference.LabelOf(AudioPreference.TierOfId(actualAudioId)));
        }
        catch (Exception ex)
        {
            if (_closing) return;
            Svc.Toast.Show("切换音质失败: " + ex.Message);
            HideLoading();
        }
        finally
        {
            _switchingAudio = false;
        }
    }

    /// <summary>切换清晰度: 记住播放进度, 换流后自动恢复</summary>
    private async Task SwitchQualityAsync(int qn)
    {
        if (_closing || _switchingQuality || _currentCid <= 0) return;
        _switchingQuality = true;
        try
        {
            var resumeMs = _mp.Time;
            ShowLoading("正在切换清晰度…");

            // 换清晰度 = 换成另一路流: 上一档的合流立刻作废(缓存键里带 qn, 不会互相污染)
            CancelRemuxPrefetch();

            var (_, playErr, main, backups, _, qualities, actualQn, audioUrl, remux,
                 _, _, audioIds, actualAudioId) =
                await Svc.Api.GetPlayUrlAsync(_currentBvid, _currentCid, qn,
                    Svc.Settings.PreferredAudioTier);
            if (_closing) return;

            // 和首次加载同一套: 这一档已缓存就播本地, 没缓存就下完再播(单文件源: 跳转快、结尾完整)
            var local = ShortVideoCache.TryGet(_currentBvid, actualQn);
            if (local == null && ShortVideoCache.WorthCaching(remux))
                local = await PrepareShortVideoAsync(remux!, actualQn);
            if (_closing) return;

            var url = local ?? main;
            if (string.IsNullOrEmpty(url) && backups.Count > 0) url = backups[0];
            if (string.IsNullOrEmpty(url))
            {
                // 以前这里只有一句写死的"该清晰度暂时不可用" —— 而真正的原因(大会员/付费专属 /
                // 网络失败 / 该档无源)在 API 层就被丢掉了, 用户只能反复换档试。
                Svc.Toast.Show(string.IsNullOrEmpty(playErr) ? "该清晰度暂时不可用" : playErr);
                HideLoading();
                return;
            }
            _qualities.Clear();
            _qualities.AddRange(qualities);
            FillQualityMenu(actualQn > 0 ? actualQn : qn);
            // 切清晰度会重新取流, 音轨也可能跟着变(不同清晰度集合里音轨不同) —— 一起刷新。
            _audioIds.Clear();
            _audioIds.AddRange(audioIds);
            _currentAudioId = actualAudioId;
            FillAudioMenu(actualAudioId);

            // 起播后(Playing 事件)自动恢复到切换前进度
            _resumeAfterSwitchMs = resumeMs > 2000 ? resumeMs : 0;
            _resumeByStartTime = false;   // 换清晰度走"起播后设 Time", 不是 start-time
            // 新流重新计时"提前结束自救"
            _eofResumeCount = 0;
            _lastEofResumeMs = 0;
            // 记住换过去的这一路: 之后点「重播」要回到同一个源, 而不是换回上一个清晰度
            _lastPlayUrl = url;
            // 本地合流是单文件源: 不能带 input-slave, 否则等于给同一个文件再挂一条音轨
            _lastAudioUrl = local != null ? null : audioUrl;
            // 「听视频」的音轨地址跟着新档位更新 —— 不清的话切完清晰度再进出聆听态,
            // 用的还是旧档那条音轨(可能码率不同、甚至已过期)。
            // ★ 必须取 **audioUrl** 而不是 _lastAudioUrl: 后者在本地合流那条路上是 null
            //   (单文件源不挂 input-slave), 而音轨本身是好的。理由同 LoadVideoAsync 里那段。
            _listenAudioUrl = audioUrl;
            // 必须先 Stop: LibVLC 3 里已有 input 在播时 play() 会被**直接忽略**,
            // 不 Stop 的话新 Media 根本不会起播 —— 这就是"切换清晰度没反应"
            try { _mp.Stop(); } catch { }
            // ★ 聆听态下切清晰度: 仍然只播音轨(用户此刻要的是声音, 不该突然出画面再切回去)。
            //   没有可用音轨时退回整路播放, 至少不会什么都不播。
            if (_isListenMode && !string.IsNullOrEmpty(_listenAudioUrl))
                PlayMedia(BuildMedia(_listenAudioUrl!, null));
            else
                PlayMedia(BuildMedia(url!, _lastAudioUrl));
            UpdatePlayButton();
        }
        catch (Exception ex)
        {
            if (_closing) return;
            Svc.Toast.Show("切换清晰度失败: " + ex.Message);
            HideLoading();
        }
        finally
        {
            _switchingQuality = false;
        }
    }

    /// <summary>
    /// 直播切换清晰度(2026-11-13)。
    ///
    /// ★ 与点播 <see cref="SwitchQualityAsync"/> 的区别:
    ///   · 直播没有 cid/总时长/进度, 重新取流后直接从当前直播位置播(服务端给的就是实时流);
    ///   · **不能停弹幕连接** —— 右侧聊天记录要连续; 只清画面上在飞的滚动弹幕(媒体时间会归零);
    ///   · 直播只有一路流(没有 DASH 音轨/合流缓存), 不碰点播那套状态。
    /// </summary>
    private async Task SwitchLiveQualityAsync(int qn)
    {
        if (_closing || _switchingQuality || _liveRoomId <= 0 || !_isLive) return;

        _switchingQuality = true;
        try
        {
            ShowLoading("正在切换清晰度…");
            var (ok, err, url, actualQn, qualities) =
                await Svc.Api.GetLivePlayUrlAsync(_liveRoomId, qn);
            if (_closing || !_isLive) return;

            if (!ok || string.IsNullOrEmpty(url))
            {
                Svc.Toast.Show(string.IsNullOrEmpty(err) ? "该清晰度暂时不可用" : err);
                HideLoading();
                return;
            }

            _qualities.Clear();
            _qualities.AddRange(qualities);
            FillQualityMenu(actualQn > 0 ? actualQn : qn);
            if (actualQn > 0) _liveQn = actualQn; else _liveQn = qn;

            _lastPlayUrl = url;
            _lastAudioUrl = null;

            // 新 input 的媒体时间从 0 开始: 在飞的弹幕时间轴对不上, 清掉(右侧聊天列表保留)。
            ResetLiveDanmakuLayer();

            // ★ LibVLC 3 里已有 input 在播时 play() 会被忽略, 必须先 Stop(与点播切清晰度同款)。
            try { _mp.Stop(); } catch { }
            if (!PlayLiveStream(url, _liveRoomId))
            {
                ShowFail("直播流播放失败, 可能已经下播");
                return;
            }
            UpdatePlayButton();
        }
        catch (Exception ex)
        {
            if (_closing) return;
            Svc.Toast.Show("切换清晰度失败: " + ex.Message);
            HideLoading();
        }
        finally
        {
            _switchingQuality = false;
        }
    }
}
